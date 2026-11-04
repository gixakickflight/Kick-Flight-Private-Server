using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace KickFlight.BootstrapApi.Tests;

// The facts half of the economy fake: what the Kick-Flight tenant declares for badges, as services/economy serves it.
//
//   POST /internal/economy/facts   a declared fact with its typed fields. The schema is closed both ways (an undeclared
//                                  field is 400 unknown_fact_field, an omitted one 400 missing_fact_field, a wrongly typed
//                                  one 400 invalid_fact_field, an undeclared fact 422 unknown_fact) and a key makes a retry
//                                  count once: a replay answers the first report's stored result.
//
// The tenant's badge tracks are `auto_claim`: a report that crosses a step pays its class inside the report and lists it in
// the answer's `auto_claims` ({track_id, period, step, target, class, result: claimed|refused, code?, items?}). This fake does
// NOT evaluate tracks from facts - deciding which report counts, what it adds and when a streak continues is the economy's
// job and is tested there (services/economy/internal/rules). A test says "the next battle.result crosses this step" with
// QueueAutoClaim, and the fake then answers exactly as the service does (and with the capsule trigger's `triggers`): pays the class (a class named badge.<id> grants the
// item honor:<id>, as the tenant's generated classes do) or reports a refusal that leaves the step unpaid.
//
// The fact schemas below are the ones kf-otomo's economy.tasks declares (kf/tools/gen_badges.py), so an adapter that sends a
// field the tenant does not declare fails here as it would on the real service.
public sealed partial class FakeOtomoEconomy
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> FactSchemas =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["battle.result"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rule_type"] = "integer", ["kicker_id"] = "integer", ["queue"] = "text", ["credited"] = "boolean",
                ["credit_reason"] = "text", ["outcome"] = "text", ["won"] = "boolean", ["flagged"] = "boolean",
                ["mvp"] = "boolean", ["rank_rule"] = "integer", ["rank_after"] = "integer", ["kills"] = "integer",
                ["deaths"] = "integer", ["crystals_collected"] = "integer", ["guardian_released_crystals"] = "integer",
                ["crystals_released"] = "integer", ["flag_goals"] = "integer", ["flag_drops"] = "integer",
                ["ball_goals"] = "integer", ["ball_drops"] = "integer", ["moving_distance"] = "integer"
            },
            ["login.day"] = new Dictionary<string, string> { ["day"] = "text" },
            ["item.upgraded"] = new Dictionary<string, string> { ["item"] = "text", ["level"] = "integer" },
            ["like.received"] = new Dictionary<string, string> { ["battle"] = "text" },
            ["registration.completed"] = new Dictionary<string, string>()
        };

    public sealed record FactReport(string PlayerId, string Fact, string Key, JsonElement Fields, bool Replayed);

    private sealed record PendingClaim(string Fact, string TrackId, int Step, long Target, string Class, string? RefusedWith);

    private readonly List<FactReport> _factReports = [];
    private readonly Dictionary<string, List<PendingClaim>> _pendingClaims = new(StringComparer.Ordinal);

    // Every fact the adapter reported, in arrival order, whether or not its key was new.
    public IReadOnlyList<FactReport> FactReports { get { lock (_lock) return _factReports.ToArray(); } }

    // The next report of `fact` for this player crosses a step of an auto_claim track. With `refusedWith` (a claim-route
    // code such as budget_exceeded) the payment is refused: the step is not paid and stays claimable by the player.
    public void QueueAutoClaim(string accountId, string fact, string trackId, int step, long target, string payClass,
        string? refusedWith = null)
    {
        lock (_lock)
        {
            if (!_pendingClaims.TryGetValue(accountId, out var list)) _pendingClaims[accountId] = list = [];
            list.Add(new PendingClaim(fact, trackId, step, target, payClass, refusedWith));
        }
    }

    private void MapBadges()
    {
        _app.MapPost("/internal/economy/facts", async (HttpContext context) =>
        {
            var (root, failure) = await InternalAsync(context);
            if (root is null) return failure;
            var playerId = root.Value.GetProperty("player_id").GetString() ?? "";
            var fact = root.Value.GetProperty("fact").GetString() ?? "";
            var key = root.Value.GetProperty("key").GetString() ?? "";
            var fields = root.Value.GetProperty("fields");
            if (!FactSchemas.TryGetValue(fact, out var schema))
                return Error(context, StatusCodes.Status422UnprocessableEntity, "unknown_fact", $"no fact {fact}");
            foreach (var field in fields.EnumerateObject())
            {
                if (!schema.ContainsKey(field.Name))
                    return Error(context, StatusCodes.Status400BadRequest, "unknown_fact_field", $"{fact}.{field.Name}");
            }
            foreach (var (name, type) in schema)
            {
                if (!fields.TryGetProperty(name, out var value))
                    return Error(context, StatusCodes.Status400BadRequest, "missing_fact_field", $"{fact}.{name}");
                var fits = type switch
                {
                    "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "text" => value.ValueKind == JsonValueKind.String,
                    "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                    _ => false
                };
                if (!fits)
                    return Error(context, StatusCodes.Status400BadRequest, "invalid_fact_field", $"{fact}.{name} is not {type}");
            }

            lock (_lock)
            {
                var replayKey = "fact:" + playerId + ":" + key;
                if (_internalReplies.TryGetValue(replayKey, out var first))
                {
                    _factReports.Add(new FactReport(playerId, fact, key, fields.Clone(), true));
                    return Replay(first);
                }
                _factReports.Add(new FactReport(playerId, fact, key, fields.Clone(), false));

                var player = _players.GetOrAdd(playerId, _ => new PlayerEconomy());
                var autoClaims = new List<object>();
                if (_pendingClaims.TryGetValue(playerId, out var pending))
                {
                    foreach (var claim in pending.Where(row => row.Fact == fact).ToArray())
                    {
                        pending.Remove(claim);
                        if (claim.RefusedWith is not null)
                        {
                            autoClaims.Add(new
                            {
                                track_id = claim.TrackId, period = "all", step = claim.Step, target = claim.Target,
                                @class = claim.Class, result = "refused", code = claim.RefusedWith
                            });
                            continue;
                        }
                        var moved = new List<object>();
                        if (claim.Class.StartsWith("badge.", StringComparison.Ordinal) && int.TryParse(claim.Class[6..], out var honorId))
                        {
                            var item = "honor:" + honorId;
                            player.Items[item] = player.Items.GetValueOrDefault(item) + 1;
                            moved.Add(new { item, qty = 1L, total = player.Items[item] });
                        }
                        autoClaims.Add(new
                        {
                            track_id = claim.TrackId, period = "all", step = claim.Step, target = claim.Target,
                            @class = claim.Class, result = "claimed", items = moved
                        });
                    }
                }

                // The tenant's capsule trigger listens to battle.result; its outcome rides in the same answer as the
                // auto-claimed steps (FakeOtomoEconomy.Triggers.cs). Both members are absent when empty, as the service answers.
                var triggers = fact == "battle.result" ? RunCapsuleTrigger(playerId, fact, key) : [];
                var answer = new Dictionary<string, object?>
                {
                    ["player_id"] = playerId, ["fact"] = fact, ["tracks"] = Array.Empty<object>(), ["key"] = key
                };
                if (autoClaims.Count > 0) answer["auto_claims"] = autoClaims;
                if (triggers.Length > 0) answer["triggers"] = triggers;
                return Remember(replayKey, answer);
            }
        });
    }
}
