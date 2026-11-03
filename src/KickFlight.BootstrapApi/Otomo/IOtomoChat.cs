using System.Text.Json.Serialization;

namespace KickFlight.BootstrapApi.Otomo;

// Why a chat call failed. Same three-way split as economy's and session's:
//
//   Disabled     Otomo:Chat is off or has no key: there is nobody to ask.
//   Unavailable  chat could not be reached, timed out, or answered 5xx - which includes chat's own
//                503 not_configured / membership_unavailable / ownership_unavailable. The message may or may not exist,
//                so the caller answers the 503 contract, not "rejected".
//   Refused      chat answered a 4xx. StatusCode and Code are what it said (rate_limited, refused_words, too_long,
//                attachment_not_owned, attachment_unknown, text_required, empty_message, ...); RetryAfter is the
//                Retry-After seconds of a 429, when it sent one.
public enum OtomoChatFailure
{
    Disabled,
    Unavailable,
    Refused
}

public sealed class OtomoChatException : Exception
{
    public OtomoChatException(OtomoChatFailure failure, string message, int statusCode = 0, string? code = null,
        int retryAfterSeconds = 0, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        StatusCode = statusCode;
        Code = code;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public OtomoChatFailure Failure { get; }
    public int StatusCode { get; }
    public string? Code { get; }
    public int RetryAfterSeconds { get; }
}

// --- the wire types: chat's own JSON keys ---------------------------------------------------------------------------
// services/chat/internal/api/messages.go: channelView (:36-44), messageView (:90-97), and the send/resolve bodies.

// One message. body and attachment are absent when empty. seq is the per-channel strictly increasing sequence;
// sent_at is RFC 3339 with nanoseconds, UTC. There is no sender name: sender_id is the otomo player id.
public sealed record ChatMessageView(
    [property: JsonPropertyName("channel_id")] string ChannelId,
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("sender_id")] string SenderId,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("attachment")] string? Attachment,
    [property: JsonPropertyName("sent_at")] DateTimeOffset SentAt);

public sealed record ChatChannelView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("scope_ref")] string? ScopeRef,
    [property: JsonPropertyName("title")] string? Title);

public sealed record ChatSendResult(ChatMessageView Message, bool Replayed);

// The adapter's only view of otomo chat. Every call names the player it is for; the key identifies the adapter.
public interface IOtomoChat
{
    // False when Otomo:Chat is off. Callers fail closed.
    bool Enabled { get; }

    // POST /internal/chat/channels {"kind":"global"}: idempotent, answers the channel (id "global").
    Task<ChatChannelView> ResolveGlobalAsync(string playerId, CancellationToken cancellationToken);

    // GET /internal/chat/channels/{id}/messages?after=&limit=: ascending, `after` exclusive. When more than `limit` are
    // newer than `after` the NEWEST `limit` come back (store.go:236-246), so after=0 is "the last page". limit is
    // clamped by chat to 200 (default 100).
    Task<IReadOnlyList<ChatMessageView>> ReadAsync(string channelId, string playerId, long after, int limit,
        CancellationToken cancellationToken);

    // POST /internal/chat/channels/{id}/messages {"body","attachment","key"}: idempotent on `key` per (channel, sender).
    Task<ChatSendResult> SendAsync(string channelId, string playerId, string body, string attachment, string key,
        CancellationToken cancellationToken);
}
