using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KickFlight.BootstrapApi;

/// <summary>One stored chat message; the JSON names are the Firebase RTDB child fields the client reads
/// (ChatDatabaseModel.SetData), see docs/re-notes/chat.md.</summary>
public sealed record ChatMessage(string Key, string UserId, string Name, string Comment, int StampId, int HonorId, string InsertDatetime);

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
    private long _seq;

    public ChatService(IWebHostEnvironment environment, IConfiguration configuration, TimeProvider time, ILogger<ChatService> logger)
    {
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
        ChatMessage message;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            // Strictly increasing: the client drops children dated at or before its last fetch, so same-second
            // writes get a sub-second component and the key is a monotonic sequence.
            message = new ChatMessage((++_seq).ToString(CultureInfo.InvariantCulture), userId.ToString(CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(name) ? $"Player {userId}" : name, comment ?? "", stampId, honorId <= 0 ? 6010000 : honorId,
                now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
            var list = _rooms[roomId];
            list.Add(message);
            if (list.Count > MaxMessages) list.RemoveRange(0, list.Count - MaxMessages);
            Save(roomId, list);
        }
        await BroadcastAsync(roomId, message);
        return message;
    }

    public ChatConnection Register(WebSocket socket)
    {
        var connection = new ChatConnection(socket);
        _connections[connection] = 0;
        return connection;
    }

    public void Unregister(ChatConnection connection) => _connections.TryRemove(connection, out _);

    private async Task BroadcastAsync(int roomId, ChatMessage message)
    {
        foreach (var connection in _connections.Keys)
        {
            foreach (var (path, tag) in connection.ListensFor(roomId))
            {
                try { await connection.SendAsync(DataPush($"{path}/{message.Key}", tag, ToNode(message))); }
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

    public JsonNode InitialSnapshot(int roomId)
    {
        var snapshot = new JsonObject();
        foreach (var m in Recent(roomId)) snapshot[m.Key] = ToNode(m);
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
                await connection.SendAsync(DataPush(path, tag, InitialSnapshot(roomId.Value)));
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
