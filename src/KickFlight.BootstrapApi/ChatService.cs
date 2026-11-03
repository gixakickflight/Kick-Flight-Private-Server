using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KickFlight.BootstrapApi.Otomo;
using Microsoft.Extensions.Options;

namespace KickFlight.BootstrapApi;

/// <summary>
/// The client's chat protocol, translated onto otomo chat. This class keeps no message, no room store, no retention,
/// no length rule and no file: those are chat's (services/chat). What it owns is the client's own vocabulary:
/// <list type="bullet">
/// <item>the Firebase-RTDB-compatible websocket frames the client reads chat through (docs/re-notes/chat.md),</item>
/// <item>the client's room ids (config/masters_chat_room.json) and its <c>&lt;lang&gt;/&lt;room id&gt;</c> listen path,</item>
/// <item>the message DTO (<see cref="ChatMessage"/>), built from chat's message plus the sender's name and honor
/// (<see cref="IChatSenders"/>),</item>
/// <item>the relay: chat's only push is a Valkey publish, which this adapter has no client for, so while at least one
/// socket listens it reads the global channel after its last seq every <see cref="OtomoChatOptions.PollInterval"/>, and
/// a write made through this adapter wakes the read at once.</item>
/// </list>
/// Every client room id maps to chat's one global channel (the pre-move store was language-independent too, but kept the
/// four rooms apart; chat has no room dimension, so the four tabs now show the same stream).
/// </summary>
public sealed class ChatService : IDisposable
{
    /// <summary>The window the client asks for (<c>l:100</c> in its listen query) and chat's default page.</summary>
    public const int HistoryLimit = 100;
    private const int PollLimit = 200; // chat's MaxLimit (internal/chat/service.go: MaxLimit)

    private readonly IOtomoChat _chat;
    private readonly IChatSenders _senders;
    private readonly OtomoChatOptions _options;
    private readonly HashSet<int> _roomIds;
    private readonly bool _switch;
    private readonly ConcurrentDictionary<ChatConnection, byte> _connections = new();
    private readonly ILogger<ChatService> _logger;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _relayLock = new();
    private readonly object _timeLock = new();
    private string? _channelId;
    private Task? _relay;
    private long _lastMillis;

    public ChatService(IOtomoChat chat, IChatSenders senders, IOptions<OtomoChatOptions> options,
        IWebHostEnvironment environment, IConfiguration configuration, ILogger<ChatService> logger)
    {
        _chat = chat;
        _senders = senders;
        _options = options.Value;
        _logger = logger;
        _switch = configuration.GetValue("Chat:Enabled", true);
        _roomIds = LoadRoomIds(environment.ContentRootPath);
    }

    /// <summary>Kill switch (Chat:Enabled=false) or no Otomo:Chat: startup then serves the empty connection id again.</summary>
    public bool Enabled => _switch && _chat.Enabled;

    public bool RoomExists(int roomId) => _roomIds.Contains(roomId);

    // ---- writes ----

    /// <summary>
    /// Sends one comment as the player. The text goes in <c>body</c>; a stamp is the attachment item id
    /// <c>stamp:&lt;stampId&gt;</c> (what chat.rules lists and economy owns). Policy is chat's: any refusal arrives as an
    /// <see cref="OtomoChatException"/> for the caller to map.
    /// </summary>
    public async Task<ChatSendResult> WriteAsync(string otomoPlayerId, string? comment, int stampId, string key,
        CancellationToken cancellationToken)
    {
        var channel = await ChannelIdAsync(otomoPlayerId, cancellationToken).ConfigureAwait(false);
        var result = await _chat.SendAsync(channel, otomoPlayerId, comment ?? "", AttachmentOf(stampId), key,
            cancellationToken).ConfigureAwait(false);
        Wake();
        return result;
    }

    public static string AttachmentOf(int stampId) =>
        stampId > 0 ? "stamp:" + stampId.ToString(CultureInfo.InvariantCulture) : "";

    private async Task<string> ChannelIdAsync(string playerId, CancellationToken cancellationToken)
    {
        if (_channelId is { } known) return known;
        var channel = await _chat.ResolveGlobalAsync(playerId, cancellationToken).ConfigureAwait(false);
        return _channelId = channel.Id;
    }

    // ---- the DTO ----

    /// <summary>One message as the client reads it; the JSON names are the Firebase RTDB child fields (ChatDatabaseModel.SetData).</summary>
    public sealed record ChatMessage(string Key, string UserId, string Name, string Comment, int StampId, int HonorId, string InsertDatetime);

    private async Task<List<ChatMessage>> ToMessagesAsync(IReadOnlyList<ChatMessageView> views, bool live,
        CancellationToken cancellationToken)
    {
        var senders = new Dictionary<string, ChatSender>(StringComparer.Ordinal);
        foreach (var id in views.Select(v => v.SenderId).Distinct(StringComparer.Ordinal))
            senders[id] = await _senders.ResolveAsync(id, cancellationToken).ConfigureAwait(false);

        long previous = 0;
        var result = new List<ChatMessage>(views.Count);
        foreach (var view in views)
        {
            var sender = senders[view.SenderId];
            // The client drops children dated at or before its last fetch and the format has millisecond precision, so
            // two messages inside one millisecond are separated by one: dates are strictly increasing in seq order.
            var millis = view.SentAt.ToUnixTimeMilliseconds();
            lock (_timeLock)
            {
                var floor = live ? Math.Max(previous, _lastMillis) : previous;
                if (millis <= floor) millis = floor + 1;
                previous = millis;
                if (live) _lastMillis = millis;
                else _lastMillis = Math.Max(_lastMillis, millis);
            }
            result.Add(new ChatMessage(view.Seq.ToString(CultureInfo.InvariantCulture), sender.UserId, sender.Name,
                view.Body ?? "", StampOf(view.Attachment), sender.HonorId,
                DateTimeOffset.FromUnixTimeMilliseconds(millis).ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture)));
        }
        return result;
    }

    private static int StampOf(string? attachment) =>
        attachment is not null && attachment.StartsWith("stamp:", StringComparison.Ordinal) &&
        int.TryParse(attachment.AsSpan("stamp:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;

    // ---- connections ----

    public ChatConnection Register(WebSocket socket)
    {
        var connection = new ChatConnection(socket);
        _connections[connection] = 0;
        return connection;
    }

    public void Unregister(ChatConnection connection) => _connections.TryRemove(connection, out _);

    // ---- the relay: chat -> sockets ----

    private void Wake()
    {
        if (_relay is null || _connections.IsEmpty) return;
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private void EnsureRelay()
    {
        lock (_relayLock) _relay ??= Task.Run(RelayAsync);
    }

    private async Task RelayAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var listens = _connections.Keys.SelectMany(c => c.Listens().Select(l => (Connection: c, Listen: l))).ToArray();
                if (listens.Length > 0) await PollOnceAsync(listens, token);
                // Idle: nothing listens, so wait for a listener or a write rather than polling for nobody.
                if (listens.Length == 0) await _wake.WaitAsync(token);
                else await _wake.WaitAsync(_options.PollInterval, token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // Chat down or a bad body: the pushes in this interval are missed, not lost - the next read is by seq.
                _logger.LogWarning("chat relay: {Error}", ex.Message);
                try { await Task.Delay(_options.PollInterval, token); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task PollOnceAsync((ChatConnection Connection, ChatListen Listen)[] listens, CancellationToken token)
    {
        var channel = await ChannelIdAsync(_options.ReaderPlayerId, token);
        var after = listens.Min(l => l.Listen.LastSeq);
        var views = await _chat.ReadAsync(channel, _options.ReaderPlayerId, after, PollLimit, token);
        if (views.Count == 0) return;
        var messages = await ToMessagesAsync(views, live: true, token);
        for (var i = 0; i < views.Count; i++)
        {
            foreach (var (connection, listen) in listens)
            {
                if (views[i].Seq <= listen.LastSeq || !connection.IsListening(listen)) continue;
                listen.LastSeq = views[i].Seq;
                try { await connection.SendAsync(DataPush($"{listen.Path}/{messages[i].Key}", listen.Tag, ToNode(messages[i]))); }
                catch (Exception ex) { _logger.LogDebug(ex, "Chat push failed"); }
            }
        }
    }

    // ---- Firebase RTDB wire protocol (observed subset, client SDK 19.2.1) ----

    public static string Handshake(string host, string sid) => new JsonObject
    {
        ["t"] = "c",
        ["d"] = new JsonObject { ["t"] = "h", ["d"] = new JsonObject
        {
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["v"] = "5", ["h"] = host, ["s"] = sid
        } }
    }.ToJsonString();

    public static string Ack(long requestId, string status = "ok") => new JsonObject
    {
        ["t"] = "d",
        ["d"] = new JsonObject { ["r"] = requestId, ["b"] = new JsonObject { ["s"] = status, ["d"] = new JsonObject() } }
    }.ToJsonString();

    public static string DataPush(string path, long? tag, JsonNode data)
    {
        var body = new JsonObject { ["p"] = path };
        if (tag is not null) body["t"] = tag;
        body["d"] = data;
        return new JsonObject { ["t"] = "d", ["d"] = new JsonObject { ["a"] = "d", ["b"] = body } }.ToJsonString();
    }

    public static JsonNode ToNode(ChatMessage m) => new JsonObject
    {
        ["userId"] = m.UserId, ["name"] = m.Name, ["comment"] = m.Comment,
        ["stampId"] = m.StampId, ["honorId"] = m.HonorId, ["insertDatetime"] = m.InsertDatetime
    };

    /// <summary>The room's history as chat answers it (the newest page, ascending), and the highest seq in it.</summary>
    private async Task<(JsonNode Snapshot, long LastSeq)> InitialSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = new JsonObject();
        try
        {
            var channel = await ChannelIdAsync(_options.ReaderPlayerId, cancellationToken);
            var views = await _chat.ReadAsync(channel, _options.ReaderPlayerId, 0, HistoryLimit, cancellationToken);
            var messages = await ToMessagesAsync(views, live: false, cancellationToken);
            foreach (var m in messages) snapshot[m.Key] = ToNode(m);
            return (snapshot, views.Count == 0 ? 0 : views[^1].Seq);
        }
        catch (OtomoChatException ex)
        {
            // The client gets an empty room, as it did for any room with nothing in it; the relay then fills it.
            _logger.LogWarning("chat history unavailable ({Failure} {Status} {Code})", ex.Failure, ex.StatusCode, ex.Code);
            return (snapshot, 0);
        }
    }

    /// <summary>Handles one inbound text frame. Replies are sent on the connection.</summary>
    public async Task HandleFrameAsync(ChatConnection connection, string frame)
    {
        if (frame.Length == 0 || frame == "0") return; // keepalive
        JsonNode? root;
        try { root = JsonNode.Parse(frame); } catch (JsonException) { return; }
        if (root?["t"]?.GetValue<string>() != "d") return;
        var d = root["d"];
        var action = d?["a"]?.GetValue<string>();
        var body = d?["b"];
        long? requestId = d?["r"]?.GetValue<long>();
        switch (action)
        {
            case "q":
            {
                var path = body?["p"]?.GetValue<string>() ?? "";
                var tag = body?["t"]?.GetValue<long>() ?? 0;
                if (requestId is not null) await connection.SendAsync(Ack(requestId.Value));
                var roomId = RoomFromPath(path);
                if (roomId is null || !RoomExists(roomId.Value) || !Enabled)
                {
                    await connection.SendAsync(DataPush(path, tag, new JsonObject()));
                    break;
                }
                var (snapshot, lastSeq) = await InitialSnapshotAsync(CancellationToken.None);
                // Registered after the read, from the seq it ended at: whatever arrives later is the relay's.
                connection.Listen(path, tag, roomId.Value, lastSeq);
                EnsureRelay();
                Wake();
                await connection.SendAsync(DataPush(path, tag, snapshot));
                break;
            }
            case "n":
            {
                var path = body?["p"]?.GetValue<string>() ?? "";
                connection.Unlisten(path);
                if (requestId is not null) await connection.SendAsync(Ack(requestId.Value));
                break;
            }
            default:
                // "s" (stats), "g" (get), "p"/"m" (put/merge), auth ... every request with an r gets a plain ok.
                if (requestId is not null) await connection.SendAsync(Ack(requestId.Value));
                break;
        }
    }

    public static int? RoomFromPath(string path)
    {
        var parts = path.Trim('/').Split('/');
        return parts.Length == 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    private static HashSet<int> LoadRoomIds(string contentRoot)
    {
        // Same file DemoSessionApi serves as the ChatRoom master; the config dir sits at (or above) the content root.
        var ids = new HashSet<int>();
        string? file = null;
        // KF_REPO_ROOT is where the container mounts config/ (deploy/docker-compose.vps.yml).
        var repoRoot = Environment.GetEnvironmentVariable("KF_REPO_ROOT");
        if (!string.IsNullOrEmpty(repoRoot) && File.Exists(Path.Combine(repoRoot, "config", "masters_chat_room.json")))
            file = Path.Combine(repoRoot, "config", "masters_chat_room.json");
        for (var dir = new DirectoryInfo(contentRoot); file is null && dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "config", "masters_chat_room.json");
            if (File.Exists(candidate)) { file = candidate; break; }
        }
        if (file is null) return ids;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        foreach (var row in doc.RootElement.EnumerateArray())
            if (row.TryGetProperty("id", out var id) && id.TryGetInt32(out var n)) ids.Add(n);
        return ids;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        _wake.Dispose();
    }
}

/// <summary>One path a socket listens on, and the highest chat seq already pushed (or snapshotted) to it.</summary>
public sealed class ChatListen(string path, long tag, int roomId, long lastSeq)
{
    public string Path { get; } = path;
    public long Tag { get; } = tag;
    public int RoomId { get; } = roomId;
    public long LastSeq { get; set; } = lastSeq;
}

public sealed class ChatConnection(WebSocket socket)
{
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ConcurrentDictionary<string, ChatListen> _listens = new();
    public WebSocket Socket { get; } = socket;

    public void Listen(string path, long tag, int roomId, long lastSeq) => _listens[path] = new ChatListen(path, tag, roomId, lastSeq);
    public void Unlisten(string path) => _listens.TryRemove(path, out _);
    public bool IsListening(ChatListen listen) => _listens.TryGetValue(listen.Path, out var current) && ReferenceEquals(current, listen);
    public IReadOnlyCollection<ChatListen> Listens() => _listens.Values.ToArray();

    public async Task SendAsync(string text)
    {
        await _send.WaitAsync();
        try
        {
            if (Socket.State == WebSocketState.Open)
                await Socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally { _send.Release(); }
    }
}
