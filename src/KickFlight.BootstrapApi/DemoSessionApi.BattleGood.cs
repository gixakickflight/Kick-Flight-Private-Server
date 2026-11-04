using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace KickFlight.BootstrapApi;

public sealed partial class DemoSessionApi
{
    // BattleGoodRequestData { battleId, userIdList }: the authenticated caller likes the listed players of a battle.
    // The reply stays `{}` / status 0 whatever happens (the client has nothing to show on failure). A like is credited
    // to a target only when the server's own room roster proves that the caller and the target were both human
    // players of the same finalized battle, long enough ago for it to have been played. Anything else is ignored.
    private async Task<IResult?> HandleBattleGoodAsync(HttpContext context, SessionState state, byte[] key)
    {
        context.Response.Headers["x-app-status-code"] = "0";
        try
        {
            var body = await ReadBodyAsync(context.Request);
            if (body.Length > D2CCodec.VectorSizeBytes)
            {
                using var document = JsonDocument.Parse(D2CCodec.Decode(body, key));
                await CreditBattleLikesAsync(document.RootElement, state, context.RequestAborted);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidOperationException
                                       or FormatException or ArgumentException)
        {
            _logger.LogWarning("Could not process /battle/good for {UserId}: {Error}", state.UserId, ex.Message);
        }
        return BinaryJson("{}", key);
    }

    private async Task CreditBattleLikesAsync(JsonElement root, SessionState state, CancellationToken cancellationToken)
    {
        if (root.ValueKind != JsonValueKind.Object || !TryGetString(root, "battleId", out var battleId)
            || !root.TryGetProperty("userIdList", out var list) || list.ValueKind != JsonValueKind.Array)
            return;

        var room = _matchmaking.GetBadgeRoomSnapshot(battleId);
        if (room is null) return;
        var now = _timeProvider.GetUtcNow();
        var sinceRoomClosed = now - new DateTimeOffset(DateTime.SpecifyKind(room.FinalizedAtUtc, DateTimeKind.Utc));
        if (sinceRoomClosed < TimeSpan.FromSeconds(BadgeStatLimits.MinBattleSeconds)
            || sinceRoomClosed > TimeSpan.FromSeconds(BadgeStatLimits.MaxEntryAgeSeconds))
            return;
        // The liker must be a human of that very room.
        if (!room.Players.Any(player => !player.IsAi && player.UserId == state.UserId)) return;

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in list.EnumerateArray())
        {
            if (targets.Count >= BadgeStatLimits.MaxLikeTargetsPerRequest) break;
            var targetId = element.ValueKind == JsonValueKind.String ? element.GetString()
                : element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number)
                    ? number.ToString(CultureInfo.InvariantCulture)
                    : null;
            if (string.IsNullOrWhiteSpace(targetId) || targetId == state.UserId) continue;
            if (!room.Players.Any(player => !player.IsAi && player.UserId == targetId)) continue;
            targets.Add(targetId);
        }

        foreach (var targetId in targets)
        {
            if (!long.TryParse(targetId, NumberStyles.None, CultureInfo.InvariantCulture, out var targetPlayerId)) continue;
            // The like is a fact about the target, reported under their otomo account; the (battle, liker) pair is the
            // key, so a repeated tap counts once. Their rung is claimed on their own next sweep.
            if (_playerStore.FindOtomoPlayer(targetPlayerId) is not { } target) continue;
            await RecordBadgeLikeAsync(target.OtomoPlayerId, battleId, state.UserId, cancellationToken);
        }
    }
}
