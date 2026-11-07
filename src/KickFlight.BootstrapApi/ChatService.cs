using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KickFlight.BootstrapApi.ChatTranslation;

namespace KickFlight.BootstrapApi;

/// <summary>One stored chat message; the JSON names are the Firebase RTDB child fields the client reads
/// (ChatDatabaseModel.SetData), see docs/re-notes/chat.md.</summary>
/// <param name="Translations">Optional pt/es/ja/en renderings of <paramref name="Comment"/> written once after the send
/// (docs/CHAT_TRANSLATION.md). Absent in room files written before the feature and when translation failed; readers then
/// get <paramref name="Comment"/>. Never sent to the client as such: only the selected text goes into "comment".</param>
public sealed record ChatMessage(string Key, string UserId, string Name, string Comment, int StampId, int HonorId, string InsertDatetime,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? Translations = null);

/// <summary>
/// In-process stand-in for the Firebase Realtime Database the client's chat reads from: per-room message store
/// (persisted as data/chat/room-N.json, newest <see cref="MaxMessages"/> kept) plus a Firebase-RTDB-wire-compatible
/// websocket listener registry. The client's listen path is "&lt;lang&gt;/&lt;chatRoomId&gt;" but the write call carries
/// no language, so rooms are language-independent here: every listener of a room receives the room's messages
/// whatever lang prefix it listened with.
/// </summary>
public sealed class ChatService
{
    public const int MaxMessages = 100;
    public const int MaxCommentLength = 140;

    private readonly string _directory;
    private readonly HashSet<int> _roomIds;
    private readonly object _lock = new();
    private readonly Dictionary<int, List<ChatMessage>> _rooms = new();
    private readonly ConcurrentDictionary<ChatConnection, byte> _connections = new();
    private readonly TimeProvider _time;
    private readonly ILogger<ChatService> _logger;
    private readonly IChatTranslator _translator;
    private readonly ChatTranslationOptions _translationOptions;
    // Per-room delivery chain: live pushes of a room stay in write order even when translations finish out of order
    // (the client drops children dated at or before the last one it saw).
    private readonly Dictionary<int, Task> _tails = new();
    private long _seq;

    public const string DummyCustomToken = "kfchat-dummy-token";

    public ChatService(IWebHostEnvironment environment, IConfiguration configuration, TimeProvider time, ILogger<ChatService> logger)
        : this(environment, configuration, time, logger, new DisabledChatTranslator(), new ChatTranslationOptions())
    {
    }

    public ChatService(IWebHostEnvironment environment, IConfiguration configuration, TimeProvider time, ILogger<ChatService> logger,
        IChatTranslator translator, ChatTranslationOptions translationOptions)
    {
        _translator = translator;
        _translationOptions = translationOptions;
        _time = time;
        Enabled = configuration.GetValue("Chat:Enabled", true);
        _logger = logger;
        _directory = configuration["Chat:DataDirectory"] ?? Path.Combine(environment.ContentRootPath, "data", "chat");
        Directory.CreateDirectory(_directory);
        _roomIds = LoadRoomIds(environment.ContentRootPath);
        foreach (var id in _roomIds) _rooms[id] = Load(id);
        _seq = _rooms.Values.SelectMany(r => r).Select(m => long.TryParse(m.Key, out var n) ? n : 0).DefaultIfEmpty(0).Max();
    }

    /// <summary>Kill switch (Chat:Enabled=false): startup then serves the empty connection id again.</summary>
    public bool Enabled { get; }

    /// <summary>True when GEMINI_API_KEY is set and messages are translated on send.</summary>
    public bool TranslationEnabled => _translator.Enabled;

    /// <summary>
    /// Firebase custom token served at startup. With translation on it carries the player id so the websocket's auth
    /// frame can tell which connection belongs to a message's author (they always see their own original text);
    /// unchanged ("kfchat-dummy-token") otherwise.
    /// </summary>
    public string CustomTokenFor(long playerId) =>
        TranslationEnabled && playerId > 0 ? $"{DummyCustomToken}.{playerId.ToString(CultureInfo.InvariantCulture)}" : DummyCustomToken;

    // Fallback for the own-message rule. The shipped client never sends the Firebase auth frame (it does not complete
    // its custom-token sign-in against this server), so the websocket cannot be tied to a player by token. Players are
    // instead matched by network address: every authenticated HTTP request records the player's client address (in
    // memory only, never persisted), and a websocket opened from that same address counts as that player's.
    // Players sharing one address (NAT) simply see each other's originals, which is the safe direction.
    private readonly ConcurrentDictionary<long, (string Address, long Ticks)> _playerAddresses = new();
    private static readonly TimeSpan AddressLifetime = TimeSpan.FromHours(6);
    private const int AddressMapLimit = 20000;

    /// <summary>Records the address a player's latest authenticated HTTP request came from (no-op when unknown).</summary>
    public void NoteClientAddress(long playerId, string? address)
    {
        if (playerId <= 0 || string.IsNullOrEmpty(address) || !TranslationEnabled) return;
        var now = _time.GetUtcNow().UtcTicks;
        _playerAddresses[playerId] = (address, now);
        if (_playerAddresses.Count <= AddressMapLimit) return;
        foreach (var entry in _playerAddresses)
            if (now - entry.Value.Ticks > AddressLifetime.Ticks) _playerAddresses.TryRemove(entry.Key, out _);
    }

    /// <summary>True when <paramref name="connection"/> belongs to the author of <paramref name="message"/>: by auth-frame
    /// id when the client sent one, otherwise by the address the author last used over HTTP.</summary>
    public bool IsAuthor(ChatMessage message, ChatConnection connection)
    {
        if (connection.UserId > 0)
            return message.UserId == connection.UserId.ToString(CultureInfo.InvariantCulture);
        return connection.ClientAddress is { Length: > 0 } address
            && long.TryParse(message.UserId, NumberStyles.None, CultureInfo.InvariantCulture, out var authorId)
            && _playerAddresses.TryGetValue(authorId, out var known)
            && known.Address == address
            && _time.GetUtcNow().UtcTicks - known.Ticks <= AddressLifetime.Ticks;
    }

    public static bool TryGetUserIdFromCredential(string? credential, out long userId)
    {
        userId = 0;
        if (string.IsNullOrEmpty(credential)) return false;
        var marker = DummyCustomToken + ".";
        var at = credential.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return false;
        var digits = new string(credential.Skip(at + marker.Length).TakeWhile(char.IsAsciiDigit).ToArray());
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out userId) && userId > 0;
    }

    /// <summary>Completes when every queued live delivery has been pushed (used by tests and shutdown).</summary>
    public Task FlushDeliveriesAsync()
    {
        Task[] tails;
        lock (_lock) tails = _tails.Values.ToArray();
        return Task.WhenAll(tails);
    }

    public bool RoomExists(int roomId) => _roomIds.Contains(roomId);

    public IReadOnlyList<ChatMessage> Recent(int roomId, int limit = MaxMessages)
    {
        lock (_lock)
            return _rooms.TryGetValue(roomId, out var list)
                ? list.OrderBy(m => m.InsertDatetime, StringComparer.Ordinal).TakeLast(limit).ToArray()
                : [];
    }

    /// <summary>Validates, stores and broadcasts. Returns null when invalid.</summary>
    public async Task<ChatMessage?> WriteAsync(int roomId, string? comment, int stampId, long userId, string name, int honorId)
    {
        if (!RoomExists(roomId) || userId <= 0 || stampId < 0) return null;
        if (string.IsNullOrEmpty(comment) || comment.Length > MaxCommentLength)
        {
            // Stamp-only messages carry no text; the client sends the stamp's id with an empty comment.
            if (!(stampId > 0 && string.IsNullOrEmpty(comment))) return null;
        }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        ChatMessage message;
        lock (_lock)
        {
            Task<IReadOnlyDictionary<string, string>?>? translation = null;
            var now = _time.GetUtcNow();
            // Strictly increasing: the client drops children dated at or before its last fetch, so same-second
            // writes get a sub-second component and the key is a monotonic sequence.
            message = new ChatMessage((++_seq).ToString(CultureInfo.InvariantCulture), userId.ToString(CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(name) ? $"Player {userId}" : name, comment ?? "", stampId, honorId <= 0 ? 6010000 : honorId,
                now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
            var list = _rooms[roomId];
            if (_translator.Enabled && !string.IsNullOrWhiteSpace(message.Comment))
            {
                // Previous messages of this room (oldest first), text only; the model gets them as context.
                var context = list.Where(m => !string.IsNullOrWhiteSpace(m.Comment))
                    .TakeLast(Math.Max(0, _translationOptions.ContextMessages))
                    .Select(m => new ChatContextLine(m.Name, m.Comment)).ToArray();
                translation = _translator.TranslateAsync(new ChatTranslationRequest(message.Name, message.Comment, context));
            }
            list.Add(message);
            if (list.Count > MaxMessages) list.RemoveRange(0, list.Count - MaxMessages);
            Save(roomId, list);
            if (translation is not null)
            {
                // The HTTP write returns now; the live push waits (bounded) for the translation, in order per room.
                var previous = _tails.TryGetValue(roomId, out var tail) ? tail : Task.CompletedTask;
                _tails[roomId] = DeliverAsync(previous, roomId, message, translation, started);
                return message;
            }
        }
        await BroadcastAsync(roomId, message, started);
        return message;
    }

    private async Task DeliverAsync(Task previous, int roomId, ChatMessage message, Task<IReadOnlyDictionary<string, string>?> translation, long started)
    {
        try
        {
            try { await previous; } catch { /* a failed earlier delivery must not block this room */ }
            IReadOnlyDictionary<string, string>? translations = null;
            using (var cts = new CancellationTokenSource())
            {
                // The window runs from receipt of this message, not from when its turn in the room chain starts, so a burst of
                // slow translations is held at most MaxBroadcastDelayMs in total instead of N times that.
                var remaining = _translationOptions.MaxBroadcastDelayMs
                    - System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var wait = Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, remaining)), cts.Token);
                if (await Task.WhenAny(translation, wait) == translation) translations = await translation;
                cts.Cancel();
            }
            if (translations is not null) message = AttachTranslations(roomId, message, translations);
            else if (!translation.IsCompleted)
            {
                // Too slow for the live push: readers get the original now; keep the late result for later reads.
                _ = translation.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully && t.Result is { } late) AttachTranslations(roomId, message, late);
                }, TaskScheduler.Default);
            }
            await BroadcastAsync(roomId, message, started);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Chat delivery failed"); }
    }

    private ChatMessage AttachTranslations(int roomId, ChatMessage message, IReadOnlyDictionary<string, string> translations)
    {
        lock (_lock)
        {
            if (!_rooms.TryGetValue(roomId, out var list)) return message;
            var index = list.FindIndex(m => m.Key == message.Key);
            if (index < 0) return message; // already trimmed out of the window
            var updated = list[index] with { Translations = translations };
            list[index] = updated;
            Save(roomId, list);
            return updated;
        }
    }

    /// <param name="language">pt/es/ja/en for translated reads; null serves the original text.</param>
    public ChatConnection Register(WebSocket socket, string? language = null, string? clientAddress = null)
    {
        var connection = new ChatConnection(socket) { Language = language, ClientAddress = clientAddress };
        _connections[connection] = 0;
        _logger.LogInformation("Chat websocket opened (reader language {Language})", language ?? "original");
        return connection;
    }

    public void Unregister(ChatConnection connection) => _connections.TryRemove(connection, out _);

    private async Task BroadcastAsync(int roomId, ChatMessage message, long started = 0)
    {
        var pushed = 0;
        foreach (var connection in _connections.Keys)
        {
            foreach (var (path, tag) in connection.ListensFor(roomId))
            {
                try { await connection.SendAsync(DataPush($"{path}/{message.Key}", tag, ToNode(message, connection))); pushed++; }
                catch (Exception ex) { _logger.LogDebug(ex, "Chat push failed"); }
            }
        }
        if (started != 0)
            _logger.LogInformation("Chat message {Key} room {Room}: pushed to {Listeners} listener(s) {Ms:F0} ms after receipt (translations {Translated})",
                message.Key, roomId, pushed, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, message.Translations is null ? "none" : "attached");
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

    public static JsonNode ToNode(ChatMessage m) => ToNode(m, null, false);

    public JsonNode ToNode(ChatMessage m, ChatConnection connection) =>
        ToNode(m, connection.Language, IsAuthor(m, connection));

    /// <summary>Read-side selection only (never translates): the author sees the original; others see the stored
    /// translation for their language, else the original.</summary>
    public static JsonNode ToNode(ChatMessage m, string? language, bool isAuthor) => new JsonObject
    {
        ["userId"] = m.UserId, ["name"] = m.Name, ["comment"] = SelectText(m, language, isAuthor),
        ["stampId"] = m.StampId, ["honorId"] = m.HonorId, ["insertDatetime"] = m.InsertDatetime
    };

    public static string SelectText(ChatMessage m, string? language, bool isAuthor)
    {
        if (isAuthor || language is null || m.Translations is null) return m.Comment;
        return m.Translations.TryGetValue(language, out var text) && !string.IsNullOrWhiteSpace(text) ? text : m.Comment;
    }

    public JsonNode InitialSnapshot(int roomId, ChatConnection? connection = null)
    {
        var snapshot = new JsonObject();
        foreach (var m in Recent(roomId)) snapshot[m.Key] = connection is null ? ToNode(m) : ToNode(m, connection);
        return snapshot;
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
                if (roomId is null) { await connection.SendAsync(DataPush(path, tag, new JsonObject())); break; }
                connection.Listen(path, tag, roomId.Value);
                await connection.SendAsync(DataPush(path, tag, InitialSnapshot(roomId.Value, connection)));
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
                // Firebase auth carries the custom token served at startup (CustomTokenFor); it identifies the reader
                // so their own messages stay untranslated. Never logged.
                if (action is "auth" or "gauth" && body?["cred"] is JsonValue cred && cred.TryGetValue<string>(out var credential)
                    && TryGetUserIdFromCredential(credential, out var authorId))
                    connection.UserId = authorId;
                _logger.LogDebug("Chat websocket frame action {Action}", action);
                if (action is "auth" or "gauth")
                    _logger.LogInformation("Chat websocket {Action} frame: reader id {UserId}, language {Language}", action, connection.UserId, connection.Language ?? "original");
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

    private string RoomFile(int roomId) => Path.Combine(_directory, $"room-{roomId}.json");

    private List<ChatMessage> Load(int roomId)
    {
        var file = RoomFile(roomId);
        if (!File.Exists(file)) return [];
        try { return JsonSerializer.Deserialize<List<ChatMessage>>(File.ReadAllText(file)) ?? []; }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not read {File}", file); return []; }
    }

    private void Save(int roomId, List<ChatMessage> list)
    {
        var file = RoomFile(roomId);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list));
        File.Move(tmp, file, overwrite: true);
    }
}

public sealed class ChatConnection(WebSocket socket)
{
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ConcurrentDictionary<string, (long Tag, int RoomId)> _listens = new();
    public WebSocket Socket { get; } = socket;
    /// <summary>Reader language (pt/es/ja/en) fixed when the socket opens; null = original text.</summary>
    public string? Language { get; init; }
    /// <summary>Player id learned from the auth frame, 0 when unknown.</summary>
    public long UserId { get; set; }
    /// <summary>Client address of the websocket handshake (trusted-proxy aware), used to recognise the author.</summary>
    public string? ClientAddress { get; init; }

    public void Listen(string path, long tag, int roomId) => _listens[path] = (tag, roomId);
    public void Unlisten(string path) => _listens.TryRemove(path, out _);
    public IEnumerable<(string Path, long Tag)> ListensFor(int roomId) =>
        _listens.Where(l => l.Value.RoomId == roomId).Select(l => (l.Key, l.Value.Tag)).ToArray();

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
