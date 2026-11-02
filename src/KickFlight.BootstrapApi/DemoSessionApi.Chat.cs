using System.Text.Json;

namespace KickFlight.BootstrapApi;

public sealed partial class DemoSessionApi
{
    // POST /chatRoomComment/write {chatRoomId, comment, stampId}. The reply has no fields; the message reaches
    // readers through the Firebase-compatible websocket (ChatService), not through this response.
    private async Task<IResult> HandleChatWriteAsync(HttpContext context, SessionState state, byte[] key)
    {
        var root = await ReadRequestRootAsync(context, key);
        if (root is not { ValueKind: JsonValueKind.Object } body) return StatusError(context, key);
        if (!body.TryGetProperty("chatRoomId", out var room) || !room.TryGetInt32(out var roomId)) return StatusError(context, key);
        var comment = body.TryGetProperty("comment", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : "";
        var stampId = body.TryGetProperty("stampId", out var s) && s.TryGetInt32(out var sid) ? sid : 0;
        var message = await _chat.WriteAsync(roomId, comment, stampId, state.PlayerId, state.UserName, EquippedHonorId(state));
        return message is null ? StatusError(context, key) : OkJson(context, key, "{}");
    }
}
