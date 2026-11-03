using System.Text.Json;
using KickFlight.BootstrapApi.Otomo;
using KickFlight.BootstrapApi.PlayerStore;

namespace KickFlight.BootstrapApi;

// chatRoomComment/write is a translation onto otomo chat (services/chat); this adapter stores nothing of it.
//
//   room     the client's chatRoomId must be a row of masters_chat_room.json; every row is chat's one global channel
//            (ChatService). The request carries no language, so it is not per-language either.
//   writer   the otomo player id the ticket maps to (X-Otomo-Player), never the numeric id.
//   stamp    stampId > 0 is the attachment item id "stamp:<stampId>"; chat checks it is declared in chat.rules and owned
//            in economy. The comment is the body, empty for a stamp alone.
//   key      the idempotency key is the client's x-app-response-cache-id when it sent one (a retry of one request carries
//            the same id, as for a gacha draw or a loadout write), else a fresh one per request.
//   refused  chat's 4xx - rate_limited, refused_words, too_long, attachment_not_owned, attachment_unknown, text_required,
//            empty_message - is the one error the client already handles for a rejected comment (StatusError: non-zero
//            x-app-status-code, `{}`), as every rejection was before the move. Chat down or not configured is the 503
//            contract (ChatUnavailable); the message may or may not exist and is not reported as rejected.
//   reply    no fields; the message reaches readers through the websocket relay.
public sealed partial class DemoSessionApi
{
    private async Task<IResult> HandleChatWriteAsync(HttpContext context, KfIdentityTicket ticket, SessionState state, byte[] key)
    {
        var root = await ReadRequestRootAsync(context, key);
        if (root is not { ValueKind: JsonValueKind.Object } body) return StatusError(context, key);
        if (!body.TryGetProperty("chatRoomId", out var room) || !room.TryGetInt32(out var roomId) || !_chat.RoomExists(roomId))
            return StatusError(context, key);
        var comment = body.TryGetProperty("comment", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : "";
        var stampId = body.TryGetProperty("stampId", out var s) && s.TryGetInt32(out var sid) ? sid : 0;
        if (stampId < 0) return StatusError(context, key);

        var cacheId = ProgressKeyPart(context.Request.Headers["x-app-response-cache-id"].ToString());
        var idempotencyKey = cacheId.Length > 0 ? $"chat:{cacheId}" : $"chat:{Guid.NewGuid():N}";
        try
        {
            await _chat.WriteAsync(ticket.OtomoPlayerId, comment, stampId, idempotencyKey, context.RequestAborted);
            return OkJson(context, key, "{}");
        }
        catch (OtomoChatException exception) when (exception.Failure == OtomoChatFailure.Refused)
        {
            _logger.LogInformation("chatRoomComment/write for user {UserId} refused by chat: {Status} {Code}",
                state.UserId, exception.StatusCode, exception.Code);
            return StatusError(context, key);
        }
        catch (OtomoChatException exception)
        {
            return ChatUnavailable(context, "/chatRoomComment/write", exception);
        }
    }

    // A write chat could not answer: the game's error contract, 503, and never a "rejected" the player would retype.
    private IResult ChatUnavailable(HttpContext context, string path, OtomoChatException failure)
    {
        _logger.LogError("Refusing {Path}: chat {Reason} ({Detail})", path, failure.Failure, failure.Message);
        context.Response.Headers["x-app-status-code"] = "1";
        return Results.Json(new { error = "chat-unavailable", reason = failure.Failure.ToString() },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
