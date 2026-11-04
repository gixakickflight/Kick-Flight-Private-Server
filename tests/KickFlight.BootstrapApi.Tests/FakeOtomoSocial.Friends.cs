using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace KickFlight.BootstrapApi.Tests;

// Session's friend list and friend invitations (services/session/README.md: Friends and blocks, Friend invitations; PR
// #264). Each player has ONE shareable code, made on the first read and stable until it is rotated; redeeming someone's
// code makes the two friends at once. The decision order is the contract's: no code 400 invalid_code, unknown/revoked
// code 404 invite_not_found, your own 400 invalid_target, a block in either direction 403 blocked, already friends 200
// (a retry changes nothing), otherwise the friendship. `invites.enabled: false` answers all three invite routes
// 403 invites_disabled. The code ignores case, spaces and hyphens.
public sealed partial class FakeOtomoSocial
{
    private readonly ConcurrentDictionary<string, string> _inviteByOwner = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _ownerByCode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _redemptions = new(StringComparer.Ordinal);
    private readonly HashSet<(string Low, string High)> _friendships = [];
    private int _codeSerial;

    /// <summary>session.rules <c>invites.enabled</c>.</summary>
    public bool InvitesEnabled { get; set; } = true;

    /// <summary>A friendship made directly, for a test that is not exercising the invite routes.</summary>
    public void MakeFriends(string first, string second)
    {
        Ensure(first);
        Ensure(second);
        lock (_lock) _friendships.Add(Pair(first, second));
    }

    public bool AreFriends(string first, string second)
    {
        lock (_lock) return _friendships.Contains(Pair(first, second));
    }

    /// <summary>A block in this direction (either direction refuses an invite, as for a friend request).</summary>
    public void BlockEitherWay(string blocker, string blocked)
    {
        Ensure(blocker);
        Ensure(blocked);
        lock (_lock) _blocks.Add((blocker, blocked));
    }

    private static (string Low, string High) Pair(string first, string second) =>
        string.CompareOrdinal(first, second) < 0 ? (first, second) : (second, first);

    private static string NormalCode(string code) =>
        new(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private string InviteFor(string owner)
    {
        // Under _lock. A fresh code is derived from the account and a serial, so two accounts never share one.
        if (_inviteByOwner.TryGetValue(owner, out var existing)) return existing;
        var code = ("K" + StableDigits(owner, 100000).ToString("D5") + (++_codeSerial).ToString("D4") + "X").ToUpperInvariant();
        _inviteByOwner[owner] = code;
        _ownerByCode[code] = owner;
        return code;
    }

    private object InviteBody(string owner)
    {
        lock (_lock)
        {
            return new
            {
                code = InviteFor(owner),
                created_at = "2026-11-01T12:00:00Z",
                expires_at = (string?)null,
                redemptions = _redemptions.GetValueOrDefault(owner)
            };
        }
    }

    private object FriendRow(string accountId)
    {
        var account = Ensure(accountId);
        return new
        {
            player_id = account.PlayerId,
            display_name = account.DisplayName,
            discriminator = account.Discriminator,
            since = "2026-11-01T12:00:00Z",
            state = "accepted"
        };
    }

    private void MapFriends()
    {
        _app.MapGet("/api/player/session/friends", (HttpContext context) =>
        {
            var account = Identify(context, out var failure);
            if (account is null) return failure!;
            lock (_lock)
            {
                var friends = _friendships.Where(pair => pair.Low == account.PlayerId || pair.High == account.PlayerId)
                    .Select(pair => pair.Low == account.PlayerId ? pair.High : pair.Low).Select(FriendRow).ToArray();
                return Results.Json(new { friends, incoming = Array.Empty<object>(), outgoing = Array.Empty<object>() });
            }
        });

        _app.MapGet("/api/player/session/friends/invite", (HttpContext context) =>
        {
            var account = Identify(context, out var failure);
            if (account is null) return failure!;
            return InvitesEnabled
                ? Results.Json(InviteBody(account.PlayerId))
                : Error(context, StatusCodes.Status403Forbidden, "invites_disabled", "friend invitations are off");
        });

        _app.MapPost("/api/player/session/friends/invite/rotate", (HttpContext context) =>
        {
            var account = Identify(context, out var failure);
            if (account is null) return failure!;
            if (!InvitesEnabled)
                return Error(context, StatusCodes.Status403Forbidden, "invites_disabled", "friend invitations are off");
            lock (_lock)
            {
                if (_inviteByOwner.TryRemove(account.PlayerId, out var old)) _ownerByCode.TryRemove(old, out _);
            }
            return Results.Json(InviteBody(account.PlayerId));
        });

        _app.MapPost("/api/player/session/friends/invite/redeem", async (HttpContext context) =>
        {
            var account = Identify(context, out var failure);
            if (account is null) return failure!;
            if (!InvitesEnabled)
                return Error(context, StatusCodes.Status403Forbidden, "invites_disabled", "friend invitations are off");

            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            var code = body.RootElement.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.String
                ? NormalCode(codeProp.GetString() ?? "")
                : "";
            if (code.Length == 0)
                return Error(context, StatusCodes.Status400BadRequest, "invalid_code", "code is required");

            lock (_lock)
            {
                if (!_ownerByCode.TryGetValue(code, out var owner))
                    return Error(context, StatusCodes.Status404NotFound, "invite_not_found", "no such invitation");
                if (owner == account.PlayerId)
                    return Error(context, StatusCodes.Status400BadRequest, "invalid_target", "this is your own code");
                if (_blocks.Contains((account.PlayerId, owner)) || _blocks.Contains((owner, account.PlayerId)))
                    return Error(context, StatusCodes.Status403Forbidden, "blocked", "you cannot befriend this player");
                if (_friendships.Add(Pair(account.PlayerId, owner)))
                    _redemptions[owner] = _redemptions.GetValueOrDefault(owner) + 1;
                return Results.Json(FriendRow(owner));
            }
        });
    }
}
