using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace KickFlight.BootstrapApi.Tests;

// A stand-in for otomo's chat service: the three internal routes the adapter calls, with the bodies, status codes and
// error codes of services/chat (internal/api/messages.go, errors.go; policy in internal/chat/service.go), on loopback
// over real HTTP, so an adapter test exercises the adapter's own client end to end.
//
// What it honours, because the adapter's behaviour depends on it:
//   * chat_adapter.key as the bearer token and X-Otomo-Player as a UUID on every route (400 invalid_request without);
//   * strict bodies (an unknown field is 400 invalid_request, like the Go decoder), `key` required on a send;
//   * a global channel that exists only after a resolve (404 not_found before), id "global";
//   * a per-channel strictly increasing seq, retention (newest N kept), reads that return the NEWEST `limit` of those
//     after `after`, ascending (default 100, at most 200);
//   * send policy in the order chat checks it: empty_message, text_required, too_long (runes), refused_words (the word
//     is never echoed), attachment_unknown (closed list), the per-player/channel token bucket (429 + Retry-After),
//     then ownership (attachment_not_owned) and only then the row; a repeated key answers 200 replayed with the first
//     message and stores nothing.
// A refused message is never stored. There is no fan-out: chat publishes on Valkey, which the adapter does not consume,
// so the adapter polls this fake the way it polls the real service.
public sealed class FakeOtomoChat : IAsyncDisposable
{
    public const string ServiceKey = "chat-adapter-test-key-0123456789abcdef0123456789";
    public const int MaxLength = 140;
    public const int DefaultRetention = 100;

    public sealed record RecordedRequest(string Method, string Path, string Query, string? Player, string? Authorization, string? Body);
    public sealed record StoredMessage(string ChannelId, long Seq, string SenderId, string Body, string Attachment, DateTimeOffset SentAt, string Key);

    private readonly WebApplication _app;
    private readonly object _lock = new();
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly Dictionary<string, List<StoredMessage>> _channels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _nextSeq = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (double Tokens, long Ticks)> _buckets = new(StringComparer.Ordinal);
    private int _failures;

    private FakeOtomoChat(WebApplication app) => _app = app;

    public string BaseUrl { get; private set; } = "";

    // chat.rules, as the tenant publishes it (stamp:6051001 ... stamp:6051015, stamp-only allowed).
    public int Retention { get; set; } = DefaultRetention;
    public List<string> NgWords { get; } = [];
    public HashSet<string> Attachments { get; } =
        new(Enumerable.Range(6051001, 15).Select(id => "stamp:" + id), StringComparer.Ordinal);
    public bool AttachmentWithoutText { get; set; } = true;

    // Token bucket per (player, channel): `Burst` messages at once, refilled at Messages per WindowSeconds.
    public int Burst { get; set; } = 1000;
    public int RateMessages { get; set; } = 1000;
    public int RateWindowSeconds { get; set; } = 1;

    // Economy's answer for "does this player own this item".
    public Func<string, string, bool> Owns { get; set; } = (_, _) => true;

    public int FailNextRequests
    {
        get => Volatile.Read(ref _failures);
        set => Volatile.Write(ref _failures, value);
    }

    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();
    public void ClearRequests() => _requests.Clear();

    public IReadOnlyList<StoredMessage> Messages(string channelId = "global")
    {
        lock (_lock) return _channels.TryGetValue(channelId, out var list) ? list.ToArray() : [];
    }

    public static FakeOtomoChat Start()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var server = new FakeOtomoChat(app);
        server.Map();
        app.StartAsync().GetAwaiter().GetResult();
        server.BaseUrl = (app.Urls.FirstOrDefault(url => !url.EndsWith(":0", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Kestrel did not report a bound address")).TrimEnd('/');
        return server;
    }

    private void Map()
    {
        _app.MapPost("/internal/chat/channels", async (HttpContext context) =>
        {
            var (root, failure) = await GuardAsync(context);
            if (failure is not null) return failure;
            if (!Strict(root!.Value, "kind", "scope", "title") || Str(root.Value, "kind") != "global")
                return Error(context, 400, "invalid_request", "kind must be global, party or match");
            if (Str(root.Value, "scope") is { Length: > 0 })
                return Error(context, 400, "invalid_request", "a global channel has no scope");
            lock (_lock)
            {
                if (!_channels.ContainsKey("global")) { _channels["global"] = []; _nextSeq["global"] = 1; }
            }
            return Results.Json(new { channel = new { id = "global", kind = "global", created_at = DateTimeOffset.UtcNow } });
        });

        _app.MapGet("/internal/chat/channels/{channelId}/messages", async (HttpContext context, string channelId) =>
        {
            var (_, failure) = await GuardAsync(context);
            if (failure is not null) return failure;
            long after = 0;
            if (context.Request.Query["after"].ToString() is { Length: > 0 } rawAfter &&
                (!long.TryParse(rawAfter, out after) || after < 0))
                return Error(context, 400, "invalid_request", "after must be a non-negative sequence number");
            var limit = 100;
            if (context.Request.Query["limit"].ToString() is { Length: > 0 } rawLimit &&
                (!int.TryParse(rawLimit, out limit) || limit <= 0))
                return Error(context, 400, "invalid_request", "limit must be a positive number");
            limit = Math.Min(limit, 200);
            lock (_lock)
            {
                if (!_channels.TryGetValue(channelId, out var list)) return Error(context, 404, "not_found", "no such channel");
                var page = list.Where(m => m.Seq > after).OrderByDescending(m => m.Seq).Take(limit).OrderBy(m => m.Seq)
                    .Select(View).ToArray();
                return Results.Json(new { channel_id = channelId, messages = page });
            }
        });

        _app.MapPost("/internal/chat/channels/{channelId}/messages", async (HttpContext context, string channelId) =>
        {
            var (root, failure) = await GuardAsync(context);
            if (failure is not null) return failure;
            if (!Strict(root!.Value, "body", "attachment", "key"))
                return Error(context, 400, "invalid_request", "request body is not the documented JSON");
            var player = context.Request.Headers["X-Otomo-Player"].ToString();
            var key = Str(root.Value, "key");
            if (key.Length == 0) return Error(context, 400, "invalid_request", "key is required");
            var body = Str(root.Value, "body").Trim();
            var attachment = Str(root.Value, "attachment").Trim();

            lock (_lock)
            {
                if (!_channels.TryGetValue(channelId, out var list)) return Error(context, 404, "not_found", "no such channel");
                if (body.Length == 0 && attachment.Length == 0) return Error(context, 400, "empty_message", "a message needs a body or an attachment");
                if (body.Length == 0 && !AttachmentWithoutText) return Error(context, 422, "text_required", "text is required");
                if (body.EnumerateRunes().Count() > MaxLength)
                    return Error(context, 422, "too_long", "too long");
                if (NgWords.Any(word => body.Contains(word, StringComparison.OrdinalIgnoreCase)))
                    return Error(context, 422, "refused_words", "the message contains a word this instance refuses");
                if (attachment.Length > 0 && !Attachments.Contains(attachment))
                    return Error(context, 422, "attachment_unknown", "not an attachment this instance knows");

                var wait = Take(player + "\0" + channelId);
                if (wait > 0)
                {
                    context.Response.Headers["Retry-After"] = wait.ToString();
                    return Error(context, 429, "rate_limited", "over the bucket");
                }
                if (attachment.Length > 0 && !Owns(player, attachment))
                    return Error(context, 422, "attachment_not_owned", "you do not own that attachment");

                var first = list.FirstOrDefault(m => m.SenderId == player && m.Key == key);
                if (first is not null) return Results.Json(new { message = View(first), replayed = true });

                var message = new StoredMessage(channelId, _nextSeq[channelId]++, player, body, attachment, DateTimeOffset.UtcNow, key);
                list.Add(message);
                if (list.Count > Retention) list.RemoveRange(0, list.Count - Retention);
                return Results.Json(new { message = View(message), replayed = false }, statusCode: 201);
            }
        });
    }

    // Seconds to wait when the bucket is empty, else 0 (and a token is spent).
    private int Take(string bucket)
    {
        var now = Stopwatch.GetTimestamp();
        var perSecond = (double)RateMessages / RateWindowSeconds;
        var (tokens, ticks) = _buckets.TryGetValue(bucket, out var current) ? current : (Burst, now);
        tokens = Math.Min(Burst, tokens + (now - ticks) / (double)Stopwatch.Frequency * perSecond);
        if (tokens < 1)
        {
            _buckets[bucket] = (tokens, now);
            return (int)Math.Ceiling((1 - tokens) / perSecond);
        }
        _buckets[bucket] = (tokens - 1, now);
        return 0;
    }

    private static object View(StoredMessage m) => new Dictionary<string, object?>
    {
        ["channel_id"] = m.ChannelId,
        ["seq"] = m.Seq,
        ["sender_id"] = m.SenderId,
        // body and attachment are omitted when empty (messageView, messages.go:90-97).
        ["body"] = m.Body.Length > 0 ? m.Body : null,
        ["attachment"] = m.Attachment.Length > 0 ? m.Attachment : null,
        ["sent_at"] = m.SentAt.UtcDateTime.ToString("O")
    }.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value);

    // The key, the player header and (for a body route) a JSON object body.
    private async Task<(JsonElement? Root, IResult? Failure)> GuardAsync(HttpContext context)
    {
        string? text = null;
        if (context.Request.ContentLength is > 0 || context.Request.Method == "POST")
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            text = await reader.ReadToEndAsync();
        }
        var authorization = context.Request.Headers.Authorization.ToString();
        var player = context.Request.Headers["X-Otomo-Player"].ToString();
        _requests.Enqueue(new RecordedRequest(context.Request.Method, context.Request.Path.Value ?? "",
            context.Request.QueryString.Value ?? "", player.Length == 0 ? null : player, authorization, text));

        if (authorization != "Bearer " + ServiceKey)
            return (null, Error(context, 401, "unauthorized", "a service key is required"));
        if (Interlocked.Decrement(ref _failures) >= 0)
            return (null, Error(context, 503, "internal_error", "the request could not be completed"));
        if (!Guid.TryParse(player, out _))
            return (null, Error(context, 400, "invalid_request", "X-Otomo-Player must be a player UUID"));
        if (text is null) return (null, null);
        try
        {
            using var document = JsonDocument.Parse(text);
            return (document.RootElement.Clone(), null);
        }
        catch (JsonException)
        {
            return (null, Error(context, 400, "invalid_request", "request body is not the documented JSON"));
        }
    }

    private static bool Strict(JsonElement root, params string[] allowed) =>
        root.ValueKind == JsonValueKind.Object && root.EnumerateObject().All(p => allowed.Contains(p.Name));

    private static string Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static IResult Error(HttpContext context, int status, string code, string message) =>
        Results.Json(new { error = new { code, message, request_id = "fake" } }, statusCode: status);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
