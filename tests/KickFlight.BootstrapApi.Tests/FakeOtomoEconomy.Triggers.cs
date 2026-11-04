using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace KickFlight.BootstrapApi.Tests;

// The two things the tenant's capsule policy adds to the economy fake: facts that fire entitlement triggers, and an
// entitlement kind's early claim. They follow services/economy/README.md (Entitlements): a trigger mints through the same
// guards as the create route, a refusal is an outcome in the fact's answer, the mint key is trg:<index>:<fact key>, and a
// replayed fact key answers the stored result. The documents are the tenant's (kf-otomo economy.rules and economy.tasks)
// as of this change; the fake does not read them, it states them, so a change to the tenant's policy is a change here.
public sealed partial class FakeOtomoEconomy
{
    // economy.rules `entitlements.capsule`: the classes it may pay, and its delay window.
    private static readonly HashSet<string> CapsuleClasses =
        new(["match.capsule.wooden", "match.capsule.standard.1", "match.capsule.standard.2", "match.capsule.golden.2",
            "match.capsule.golden.3"], StringComparer.Ordinal);

    private static readonly TimeSpan DelayMin = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DelayMax = TimeSpan.FromHours(6);

    // economy.rules `entitlement_triggers[0]`: on battle.result, weighted picks, each with its delay. The draw is otomo's:
    // the first 8 bytes of SHA-256 over the trigger index (8 bytes, big-endian) and the fact key, modulo the total weight.
    private static readonly (string Class, TimeSpan Delay, int Weight)[] CapsulePicks =
    [
        ("match.capsule.wooden", TimeSpan.FromMinutes(15), 60),
        ("match.capsule.standard.1", TimeSpan.FromHours(1), 15),
        ("match.capsule.standard.2", TimeSpan.FromHours(1), 15),
        ("match.capsule.golden.2", TimeSpan.FromHours(4), 5),
        ("match.capsule.golden.3", TimeSpan.FromHours(4), 5)
    ];

    /// <summary>When set, every battle fact draws this class: a test that needs one known capsule. Null is the real draw.</summary>
    public string? CapsuleClassOverride { get; set; }

    internal static (string Class, TimeSpan Delay, int Weight) DrawCapsule(string factKey, string? forced = null)
    {
        if (forced is not null) return CapsulePicks.First(pick => pick.Class == forced);
        var input = new byte[8 + Encoding.UTF8.GetByteCount(factKey)];
        Encoding.UTF8.GetBytes(factKey, input.AsSpan(8));
        var hash = System.Security.Cryptography.SHA256.HashData(input);
        var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(hash) % (ulong)CapsulePicks.Sum(pick => pick.Weight);
        foreach (var pick in CapsulePicks)
        {
            if (value < (ulong)pick.Weight) return pick;
            value -= (ulong)pick.Weight;
        }
        return CapsulePicks[^1];
    }

    public sealed record EarlyClaimPrice(string Currency, long Amount, TimeSpan? Per);

    /// <summary>The capsule kind's <c>early_claim</c>; null (the tenant's published state) is a kind that offers none.</summary>
    public EarlyClaimPrice? EarlyClaim { get; set; }

    private static (string Currency, long Amount) EarlyCost(EarlyClaimPrice price, DateTimeOffset availableAt)
    {
        var remaining = availableAt - DateTimeOffset.UtcNow;
        var units = price.Per is { } per && per > TimeSpan.Zero ? (long)Math.Ceiling(remaining / per) : 1;
        return (price.Currency, price.Amount * Math.Max(1, units));
    }

    private static string? GuardRefusal(DateTimeOffset availableAt)
    {
        var wait = availableAt - DateTimeOffset.UtcNow;
        // A second of slack: the adapter's and the fake's clocks are read a moment apart.
        return wait < DelayMin - TimeSpan.FromSeconds(1) || wait > DelayMax + TimeSpan.FromSeconds(1)
            ? "available_at_out_of_range"
            : null;
    }

    // A stamp that is later than every one before it, to the tick: the adapter orders rows by created_at and compares
    // it with claimed_at, as it does against a database that stamps in microseconds.
    private long _lastTicks;

    private DateTimeOffset NextStamp()
    {
        var ticks = Math.Max(DateTimeOffset.UtcNow.UtcTicks, _lastTicks + 1);
        _lastTicks = ticks;
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    // What the capsule trigger does for one battle.result report, as the answer's `triggers` lists it. Called under _lock,
    // by the facts route in FakeOtomoEconomy.Badges.cs, so one answer carries this and the badges' `auto_claims`.
    private object[] RunCapsuleTrigger(string playerId, string fact, string key)
    {
        var pick = DrawCapsule(key, CapsuleClassOverride);
        var now = NextStamp();
        var rows = _entitlements.GetOrAdd(playerId, _ => []);
        if (rows.Count(row => row.Kind == EntitlementKind && row.ClaimedAt is null) >= EntitlementMaxOpen)
            return [new { index = 0, kind = EntitlementKind, result = "refused", @class = pick.Class, code = "entitlement_cap" }];
        var created = new Entitlement
        {
            Id = Guid.NewGuid().ToString(), PlayerId = playerId, Kind = EntitlementKind, Rule = pick.Class,
            Source = "trigger:" + fact, AvailableAt = now + pick.Delay,
            Meta = JsonSerializer.SerializeToElement(new { fact, trigger = 0 }), Key = $"trg:0:{key}", CreatedAt = now,
            Sequence = rows.Count
        };
        rows.Add(created);
        return
        [
            new
            {
                index = 0, kind = EntitlementKind, result = "minted", entitlement_id = created.Id, @class = pick.Class,
                available_at = created.AvailableAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'")
            }
        ];
    }

    private void MapEarlyClaim()
    {
        _app.MapGet("/internal/economy/entitlements/{id}/early_claim", (HttpContext context, string id) =>
        {
            var failure = InternalGet(context);
            if (failure is not null) return failure;
            var playerId = context.Request.Query["player_id"].ToString();
            lock (_lock)
            {
                var row = _entitlements.GetValueOrDefault(playerId)?.FirstOrDefault(candidate => candidate.Id == id);
                if (row is null)
                    return Error(context, StatusCodes.Status404NotFound, "entitlement_not_found", "no such entitlement for this player");
                if (EarlyClaim is not { } price)
                    return Error(context, StatusCodes.Status404NotFound, "early_claim_not_offered", "the kind declares no early_claim");
                var due = DateTimeOffset.UtcNow >= row.AvailableAt;
                var cost = EarlyCost(price, row.AvailableAt);
                return Results.Json(new
                {
                    cost = due ? Array.Empty<object>() : [new { currency = cost.Currency, amount = cost.Amount }],
                    available_at = row.AvailableAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
                });
            }
        });
    }
}
