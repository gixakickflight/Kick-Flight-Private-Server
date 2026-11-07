using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KickFlight.BootstrapApi.ChatTranslation;
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
/// <item>which text a reader gets (docs/CHAT_TRANSLATION.md): chat translates and answers <c>translations</c> per
/// language; a reader gets the one for their language (from their IP, <see cref="ChatLanguageResolver"/>) when there is
/// one, else the original, and the author's own connection always gets the original. While chat says a message's
/// translation is pending the relay holds it (and everything after it, to keep order) back for at most
/// <see cref="ChatTranslationOptions.MaxBroadcastDelayMs"/> from its sent_at.</item>
/// </list>
/// Every client room id maps to chat's one global channel (the pre-move store was language-independent too, but kept the
/// four rooms apart; chat has no room dimension, so the four tabs now show the same stream).
/// </summary>
public sealed class ChatService : IDisposable
{
    /// <summary>The window the client asks for (<c>l:100</c> in its listen query) and chat's default page.</summary>
    public const int HistoryLimit = 100;
    private const int PollLimit = 200; // chat's MaxLimit (internal/chat/service.go: MaxLimit)

    public const string DummyCustomToken = "kfchat-dummy-token";

    private readonly IOtomoChat _chat;
    private readonly IChatSenders _senders;
    private readonly OtomoChatOptions _options;
    private readonly ChatTranslationOptions _translation;
    private readonly TimeProvider _time;
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
        IWebHostEnvironment environment, IConfiguration configuration, ILogger<ChatService> logger,
        ChatTranslationOptions? translation = null, TimeProvider? time = null)
    {
        _chat = chat;
        _senders = senders;
        _options = options.Value;
        _translation = translation ?? ChatTranslationOptions.FromConfiguration(configuration);
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _switch = configuration.GetValue("Chat:Enabled", true);
        _roomIds = LoadRoomIds(environment.ContentRootPath);
    }

    /// <summary>Kill switch (Chat:Enabled=false) or no Otomo:Chat: startup then serves the empty connection id again.</summary>
    public bool Enabled => _switch && _chat.Enabled;

    /// <summary>Read-side translation selection and the pending hold-back (Chat:Translation:Enabled, on by default).
    /// With chat's translation off nothing changes either way: messages then carry no translations and no state.</summary>
    public bool TranslationEnabled => Enabled && _translation.Enabled;

    public bool RoomExists(int roomId) => _roomIds.Contains(roomId);

    // ---- the reader's identity (own messages stay in the original) ----

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
    // memory only, never persisted, bounded and expiring), and a websocket opened from that same address counts as that
    // player's. Players sharing one address (NAT) simply see each other's originals, which is the safe direction.
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
    /// <param name="Translations">chat's renderings of <paramref name="Comment"/> by language, null when it has none. Never
    /// sent to the client as such: only the selected text goes into "comment" (<see cref="SelectText"/>).</param>
    public sealed record ChatMessage(string Key, string UserId, string Name, string Comment, int StampId, int HonorId, string InsertDatetime,
        IReadOnlyDictionary<string, string>? Translations = null);

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
                DateTimeOffset.FromUnixTimeMilliseconds(millis).ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
                view.Translations is { Count: > 0 } translations ? translations : null));
        }
        return result;
    }

    private static int StampOf(string? attachment) =>
        attachment is not null && attachment.StartsWith("stamp:", StringComparison.Ordinal) &&
        int.TryParse(attachment.AsSpan("stamp:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;

    // ---- connections ----

    /// <param name="language">pt/es/ja/en for translated reads; null serves the original text.</param>
    /// <param name="clientAddress">the handshake's client address, to recognise the author (<see cref="IsAuthor"/>).</param>
    public ChatConnection Register(WebSocket socket, string? language = null, string? clientAddress = null)
    {
        var connection = new ChatConnection(socket) { Language = language, ClientAddress = clientAddress };
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

    /// <summary>
    /// The messages of <paramref name="views"/> (ascending seq) the relay may push now: everything before the first one
    /// whose translation chat still reports as pending and whose hold-back window (sent_at + MaxBroadcastDelayMs) is
    /// still open. That one and everything after it wait for a later read, so the order the client sees is chat's.
    /// </summary>
    public static int Deliverable(IReadOnlyList<ChatMessageView> views, DateTimeOffset now, int maxDelayMs, bool holdBack)
    {
        if (!holdBack) return views.Count;
        for (var i = 0; i < views.Count; i++)
            if (views[i].TranslationIsPending && now < views[i].SentAt.AddMilliseconds(maxDelayMs)) return i;
        return views.Count;
    }

    private async Task PollOnceAsync((ChatConnection Connection, ChatListen Listen)[] listens, CancellationToken token)
    {
        var channel = await ChannelIdAsync(_options.ReaderPlayerId, token);
        var after = listens.Min(l => l.Listen.LastSeq);
        var read = await _chat.ReadAsync(channel, _options.ReaderPlayerId, after, PollLimit, token);
        // A held message is not pushed and no cursor moves past it: the next poll reads it again, with its translations.
        var count = Deliverable(read, _time.GetUtcNow(), _translation.MaxBroadcastDelayMs, TranslationEnabled);
        if (count == 0) return;
        var views = count == read.Count ? read : read.Take(count).ToArray();
        var messages = await ToMessagesAsync(views, live: true, token);
        for (var i = 0; i < views.Count; i++)
        {
            foreach (var (connection, listen) in listens)
            {
                if (views[i].Seq <= listen.LastSeq || !connection.IsListening(listen)) continue;
                listen.LastSeq = views[i].Seq;
                try { await connection.SendAsync(DataPush($"{listen.Path}/{messages[i].Key}", listen.Tag, ToNode(messages[i], connection))); }
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

    public static JsonNode ToNode(ChatMessage m) => ToNode(m, null, false);

    public JsonNode ToNode(ChatMessage m, ChatConnection connection) =>
        TranslationEnabled ? ToNode(m, connection.Language, IsAuthor(m, connection)) : ToNode(m);

    /// <summary>Read-side selection only (never translates): the author sees the original; others see chat's
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

    /// <summary>The room's history as chat answers it (the newest page, ascending), and the highest seq in it. A history
    /// read is never held back: pending messages are in it with whatever translations they have.</summary>
    private async Task<(JsonNode Snapshot, long LastSeq)> InitialSnapshotAsync(ChatConnection connection, CancellationToken cancellationToken)
    {
        var snapshot = new JsonObject();
        try
        {
            var channel = await ChannelIdAsync(_options.ReaderPlayerId, cancellationToken);
            var views = await _chat.ReadAsync(channel, _options.ReaderPlayerId, 0, HistoryLimit, cancellationToken);
            var messages = await ToMessagesAsync(views, live: false, cancellationToken);
            foreach (var m in messages) snapshot[m.Key] = ToNode(m, connection);
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
                var (snapshot, lastSeq) = await InitialSnapshotAsync(connection, CancellationToken.None);
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
                // Firebase auth carries the custom token served at startup (CustomTokenFor); it identifies the reader
                // so their own messages stay untranslated. Never logged.
                if (action is "auth" or "gauth" && body?["cred"] is JsonValue cred && cred.TryGetValue<string>(out var credential)
                    && TryGetUserIdFromCredential(credential, out var readerId))
                    connection.UserId = readerId;
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
    /// <summary>Reader language (pt/es/ja/en) fixed when the socket opens; null = original text.</summary>
    public string? Language { get; init; }
    /// <summary>Player id learned from the auth frame, 0 when unknown.</summary>
    public long UserId { get; set; }
    /// <summary>Client address of the websocket handshake (trusted-proxy aware), used to recognise the author.</summary>
    public string? ClientAddress { get; init; }

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
