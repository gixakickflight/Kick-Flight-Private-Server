using System.Text.Json;
using KickFlight.BootstrapApi.Push;

namespace KickFlight.BootstrapApi;

public sealed partial class DemoSessionApi
{
    private const int PushRegisterMaxBodyBytes = 8192;
    private readonly PushTokenService? _pushTokens;
    private readonly PushRegisterThrottle? _pushThrottle;
    private readonly RankedSearchNotifier? _rankedNotifier;

    // POST /push/register: unauthenticated (the client's Java FCM hook has no session token). userId is the same
    // value /home returns as userId (SessionState.UserId, the numeric player id as a string).
    private async Task<IResult> HandlePushRegisterAsync(HttpContext context)
    {
        if (_pushTokens is null || _pushThrottle is null) return Results.NotFound();
        var headers = context.Request.Headers;
        var client = headers["X-Real-IP"].FirstOrDefault()
            ?? headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
            ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!_pushThrottle.TryAcquire(client))
            return Results.Json(new { ok = false, error = "rate_limited" }, statusCode: StatusCodes.Status429TooManyRequests);

        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[1024];
            int read;
            while ((read = await context.Request.Body.ReadAsync(chunk)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > PushRegisterMaxBodyBytes) return PushBadRequest();
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("userId", out var userIdElement))
                return PushBadRequest();
            long playerId;
            if (userIdElement.ValueKind == JsonValueKind.Number) { if (!userIdElement.TryGetInt64(out playerId)) return PushBadRequest(); }
            else if (userIdElement.ValueKind != JsonValueKind.String || !long.TryParse(userIdElement.GetString(), out playerId))
                return PushBadRequest();
            if (playerId <= 0) return PushBadRequest();
            var token = root.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var platform = root.TryGetProperty("platform", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

            switch (_pushTokens.Register(playerId, token, platform))
            {
                case PushRegisterResult.Ok:
                    _logger.LogInformation("Registered push token {Token} for player {PlayerId}", PushTokenService.Mask(token), playerId);
                    return Results.Json(new { ok = true });
                case PushRegisterResult.UnknownUser:
                    return Results.Json(new { ok = false, error = "unknown_user" }, statusCode: StatusCodes.Status404NotFound);
                default:
                    return PushBadRequest();
            }
        }
        catch (JsonException)
        {
            return PushBadRequest();
        }
    }

    private static IResult PushBadRequest() =>
        Results.Json(new { ok = false, error = "bad_request" }, statusCode: StatusCodes.Status400BadRequest);

    private DateTimeOffset? LastActivityOf(long playerId) =>
        _onlineActivityByPlayer.TryGetValue(playerId, out var seen) ? seen : null;
}
