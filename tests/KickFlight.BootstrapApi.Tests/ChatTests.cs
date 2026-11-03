using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KickFlight.BootstrapApi.Otomo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// Chat is otomo's (services/chat): messages, seq, retention, the length limit, NG words, the rate limit and stamp ownership
// are tested there and, for the contract, in FakeOtomoChat. What this adapter owns - and what is tested here - is the
// translation: the client's HTTP write and Firebase-RTDB-compatible websocket (docs/re-notes/chat.md) onto chat's
// requests, chat's refusals onto the error the client already handles, and chat's messages onto the client's DTO. Each
// test has its own FakeOtomoChat so its rules and its history are its own.
[Collection("Matchmaking static state")]
public sealed class ChatTests : IAsyncLifetime
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";
    private static readonly byte[] SessionKey = Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");

    private FakeOtomoChat _chat = null!;
    private readonly FakeOtomoEconomy _economy = OtomoTestEnvironment.Economy;

    public Task InitializeAsync()
    {
        _chat = FakeOtomoChat.Start();
        // Ownership is economy's: the same economy fake the adapter reads from.
        _chat.Owns = (player, item) => _economy.ItemsOf(player, item) > 0;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _chat.DisposeAsync();

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        b.UseContentRoot(AppContext.BaseDirectory).ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Otomo:Chat:BaseUrl"] = _chat.BaseUrl,
                ["Otomo:Chat:PollInterval"] = "00:00:00.100"
            })));

    // A client of the fake as some other player, i.e. someone else's message arriving at chat.
    private OtomoChat ChatClient() => new(new HttpClient(),
        Options.Create(new OtomoChatOptions { Enabled = true, BaseUrl = _chat.BaseUrl, ServiceKey = FakeOtomoChat.ServiceKey }),
        NullLogger<OtomoChat>.Instance);

    private async Task<string> SomeoneSaysAsync(string text, string? stamp = null)
    {
        var player = Guid.NewGuid().ToString();
        if (stamp is not null) _economy.Seed(player, items: [(stamp, 1)]);
        using var client = ChatClient();
        var channel = await client.ResolveGlobalAsync(player, default);
        await client.SendAsync(channel.Id, player, text, stamp ?? "", Guid.NewGuid().ToString("N"), default);
        return player;
    }

    // ---- the client's vocabulary ----

    [Theory]
    [InlineData("en/1", 1)]
    [InlineData("ja/4", 4)]
    [InlineData("en/x", null)]
    [InlineData("en", null)]
    public void Room_from_path(string path, int? expected) => Assert.Equal(expected, ChatService.RoomFromPath(path));

    [Theory]
    [InlineData(0, "")]
    [InlineData(-3, "")]
    [InlineData(6051001, "stamp:6051001")]
    [InlineData(6051015, "stamp:6051015")]
    public void A_stamp_is_the_attachment_item_id(int stampId, string expected) =>
        Assert.Equal(expected, ChatService.AttachmentOf(stampId));

    // ---- HTTP write -> chat ----

    private sealed record Session(HttpClient Client, string UserId, string Account);

    private static async Task<Session> LoginAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var uuid = Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.Serialize(new { hash = Encoding.ASCII.GetString(SessionKey), uuid });
        using var auth = new HttpRequestMessage(HttpMethod.Post, "/auth/index")
        { Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(payload), Encoding.ASCII.GetBytes(CommonCode), new byte[16])) };
        auth.Headers.Host = Host;
        using var authResponse = await client.SendAsync(auth);
        client.DefaultRequestHeaders.Add("x-app-access-token", authResponse.Headers.GetValues("x-app-access-token").Single());
        var account = OtomoTestEnvironment.Server.AccountFor(OtomoDeviceId.FromDeviceUuid(uuid));
        Assert.False(string.IsNullOrEmpty(account));
        return new Session(client, authResponse.Headers.GetValues("x-app-user-id").Single(), account!);
    }

    private static async Task<(HttpStatusCode Http, string Status, string Body)> PostAsync(Session session, object body,
        string? cacheId = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/chatRoomComment/write")
        { Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body)), SessionKey, new byte[16])) };
        req.Headers.Host = Host;
        if (cacheId is not null) req.Headers.Add("x-app-response-cache-id", cacheId);
        using var resp = await session.Client.SendAsync(req);
        var raw = await resp.Content.ReadAsByteArrayAsync();
        var text = resp.StatusCode == HttpStatusCode.OK ? Encoding.UTF8.GetString(D2CCodec.Decode(raw, SessionKey)) : Encoding.UTF8.GetString(raw);
        return (resp.StatusCode, resp.Headers.GetValues("x-app-status-code").Single(), text);
    }

    private IEnumerable<FakeOtomoChat.RecordedRequest> Sends() =>
        _chat.Requests.Where(r => r.Method == "POST" && r.Path.EndsWith("/messages", StringComparison.Ordinal));

    [Fact]
    public async Task A_write_is_sent_to_chat_as_the_players_otomo_account_with_exactly_chats_fields()
    {
        await using var factory = Factory();
        var session = await LoginAsync(factory);

        var (http, status, body) = await PostAsync(session, new { chatRoomId = 2, comment = "hello there", stampId = 0 }, "cache-1");
        Assert.Equal((HttpStatusCode.OK, "0", "{}"), (http, status, body));

        var send = Assert.Single(Sends());
        Assert.Equal("/internal/chat/channels/global/messages", send.Path);
        Assert.Equal(session.Account, send.Player); // the otomo account, not the numeric id
        Assert.Equal("Bearer " + FakeOtomoChat.ServiceKey, send.Authorization);
        var sent = JsonDocument.Parse(send.Body!).RootElement;
        Assert.Equal(["attachment", "body", "key"], sent.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("hello there", sent.GetProperty("body").GetString());
        Assert.Equal("", sent.GetProperty("attachment").GetString());
        Assert.Equal("chat:cache-1", sent.GetProperty("key").GetString());

        var stored = Assert.Single(_chat.Messages());
        Assert.Equal(session.Account, stored.SenderId);
    }

    [Fact]
    public async Task Every_client_room_is_chats_one_global_channel()
    {
        await using var factory = Factory();
        var session = await LoginAsync(factory);
        foreach (var room in new[] { 1, 2, 3, 4 })
            Assert.Equal("0", (await PostAsync(session, new { chatRoomId = room, comment = $"r{room}", stampId = 0 })).Status);
        Assert.Equal(4, _chat.Messages("global").Count);
        Assert.Equal(["global"], _chat.Messages("global").Select(m => m.ChannelId).Distinct());
    }

    [Fact]
    public async Task A_stamp_is_sent_as_the_stamp_item_and_a_stamp_alone_has_no_text()
    {
        await using var factory = Factory();
        var session = await LoginAsync(factory);
        _economy.Seed(session.Account, items: [("stamp:6051007", 1)]);

        Assert.Equal("0", (await PostAsync(session, new { chatRoomId = 1, comment = "", stampId = 6051007 })).Status);

        var sent = JsonDocument.Parse(Assert.Single(Sends()).Body!).RootElement;
        Assert.Equal("stamp:6051007", sent.GetProperty("attachment").GetString());
        Assert.Equal("", sent.GetProperty("body").GetString());
        Assert.Equal("stamp:6051007", Assert.Single(_chat.Messages()).Attachment);
    }

    [Fact]
    public async Task A_retry_of_one_request_reuses_its_key_and_stores_one_message_and_a_new_request_gets_a_new_key()
    {
        await using var factory = Factory();
        var session = await LoginAsync(factory);

        Assert.Equal("0", (await PostAsync(session, new { chatRoomId = 1, comment = "once", stampId = 0 }, "same-request")).Status);
        Assert.Equal("0", (await PostAsync(session, new { chatRoomId = 1, comment = "once", stampId = 0 }, "same-request")).Status);
        Assert.Single(_chat.Messages());

        Assert.Equal("0", (await PostAsync(session, new { chatRoomId = 1, comment = "twice", stampId = 0 })).Status);
        Assert.Equal("0", (await PostAsync(session, new { chatRoomId = 1, comment = "twice", stampId = 0 })).Status);
        var keys = Sends().Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("key").GetString()).ToArray();
        Assert.Equal(4, keys.Length);
        Assert.StartsWith("chat:", keys[2]);
        Assert.NotEqual(keys[2], keys[3]); // no client id: a fresh key per request
        Assert.Equal(3, _chat.Messages().Count);
    }

    [Fact]
    public async Task What_the_client_cannot_have_meant_never_reaches_chat()
    {
        await using var factory = Factory();
        var session = await LoginAsync(factory);
        Assert.Equal("1", (await PostAsync(session, new { chatRoomId = 77, comment = "hi", stampId = 0 })).Status); // no such room
        Assert.Equal("1", (await PostAsync(session, new { })).Status);                                              // no room
        Assert.Equal("1", (await PostAsync(session, new { chatRoomId = 1, comment = "hi", stampId = -1 })).Status); // malformed stamp
        Assert.Empty(Sends());
    }

    // Chat's 4xx is the one error the client already handles for a rejected comment (x-app-status-code 1, `{}`); the
    // rules themselves are chat's, so these only prove each refusal is carried to that error and nothing is stored.
    [Fact]
    public async Task Each_refusal_chat_makes_is_the_clients_rejected_comment_error_and_stores_nothing()
    {
        await using var factory = Factory();
        var session = await LoginAsync(factory);
        _chat.NgWords.Add("badword");
        _economy.Seed(session.Account, items: [("stamp:6051001", 1)]);

        var cases = new (string Name, object Body)[]
        {
            ("too_long", new { chatRoomId = 1, comment = new string('x', 141), stampId = 0 }),
            ("refused_words", new { chatRoomId = 1, comment = "a BadWord here", stampId = 0 }),
            ("attachment_not_owned", new { chatRoomId = 1, comment = "", stampId = 6051002 }),
            ("attachment_unknown", new { chatRoomId = 1, comment = "", stampId = 6059999 }),
            ("empty_message", new { chatRoomId = 1, comment = "", stampId = 0 })
        };
        foreach (var (name, body) in cases)
        {
            var (http, status, text) = await PostAsync(session, body);
            Assert.True(http == HttpStatusCode.OK && status == "1" && text == "{}", name);
        }
        Assert.Empty(_chat.Messages());

        // text_required: an instance whose rules do not allow a stamp alone.
        _chat.AttachmentWithoutText = false;
        Assert.Equal("1", (await PostAsync(session, new { chatRoomId = 1, comment = "", stampId = 6051001 })).Status);
        Assert.Empty(_chat.Messages());

        // rate_limited: the bucket holds one, so the second line is refused and the first stays.
        _chat.Burst = 1;
        _chat.RateMessages = 1;
        _chat.RateWindowSeconds = 3600;
        Assert.Equal("0", (await PostAsync(session, new { chatRoomId = 1, comment = "one", stampId = 0 })).Status);
        Assert.Equal("1", (await PostAsync(session, new { chatRoomId = 1, comment = "two", stampId = 0 })).Status);
        Assert.Equal("one", Assert.Single(_chat.Messages()).Body);
    }

    [Fact]
    public async Task Chat_that_cannot_answer_is_the_503_contract_and_not_a_rejection()
    {
        await using var factory = Factory();
        var session = await LoginAsync(factory);
        _chat.FailNextRequests = 4; // the resolve and its one retry, then the send and its one retry

        var (http, status, body) = await PostAsync(session, new { chatRoomId = 1, comment = "hi", stampId = 0 });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, http);
        Assert.Equal("1", status);
        Assert.Contains("chat-unavailable", body);
        Assert.Empty(_chat.Messages());

        // And once chat is back the same player writes normally: nothing was cached from the failure.
        _chat.FailNextRequests = 0;
        Assert.Equal("0", (await PostAsync(session, new { chatRoomId = 1, comment = "hi", stampId = 0 })).Status);
    }

    [Fact]
    public async Task One_transient_failure_is_retried_with_the_same_key_and_stores_one_message()
    {
        await using var factory = Factory();
        var session = await LoginAsync(factory);
        Assert.Equal("0", (await PostAsync(session, new { chatRoomId = 1, comment = "warm", stampId = 0 })).Status); // resolves the channel
        _chat.ClearRequests();
        _chat.FailNextRequests = 1;

        Assert.Equal("0", (await PostAsync(session, new { chatRoomId = 1, comment = "flaky", stampId = 0 })).Status);

        var keys = Sends().Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("key").GetString()).ToArray();
        Assert.Equal(2, keys.Length);
        Assert.Equal(keys[0], keys[1]);
        Assert.Equal(1, _chat.Messages().Count(m => m.Body == "flaky"));
    }

    // ---- chat -> websocket ----

    private sealed class Wire(WebSocket ws)
    {
        public async Task SendAsync(string text) => await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, default);
        public async Task<JsonNode> ReceiveAsync(int seconds = 10)
        {
            var buf = new byte[32 * 1024];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            var r = await ws.ReceiveAsync(buf, cts.Token);
            return JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, r.Count))!;
        }
    }

    private static string Listen(int r, int tag, string path) =>
        "{\"t\":\"d\",\"d\":{\"a\":\"q\",\"r\":" + r + ",\"b\":{\"p\":\"" + path
        + "\",\"q\":{\"vf\":\"r\",\"i\":\"insertDatetime\",\"l\":100},\"t\":" + tag + ",\"h\":\"\"}}}";

    private static async Task<(WebSocket Socket, Wire Wire)> ConnectAsync(WebApplicationFactory<Program> factory)
    {
        var ws = await factory.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/.ws?ns=kfchat&v=5"), default);
        var wire = new Wire(ws);
        await wire.ReceiveAsync(); // the handshake
        return (ws, wire);
    }

    [Fact]
    public async Task Websocket_handshake_ack_listen_and_broadcast()
    {
        await SomeoneSaysAsync("old message");
        await using var factory = Factory();

        var ws = await factory.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/.ws?ns=kfchat&v=5"), default);
        var wire = new Wire(ws);

        var hello = await wire.ReceiveAsync();
        Assert.Equal("c", (string?)hello["t"]);
        Assert.Equal("h", (string?)hello["d"]!["t"]);
        Assert.Equal("5", (string?)hello["d"]!["d"]!["v"]);
        Assert.False(string.IsNullOrEmpty((string?)hello["d"]!["d"]!["s"]));

        await wire.SendAsync("""{"t":"d","d":{"a":"s","r":0,"b":{"c":{"sdk.android.19-2-1":1}}}}""");
        var statsAck = await wire.ReceiveAsync();
        Assert.Equal(0, (int?)statsAck["d"]!["r"]);
        Assert.Equal("ok", (string?)statsAck["d"]!["b"]!["s"]);

        await wire.SendAsync("0"); // keepalive: no reply expected

        await wire.SendAsync(Listen(1, 1, "en/1"));
        var ack = await wire.ReceiveAsync();
        Assert.Equal(1, (int?)ack["d"]!["r"]);
        var snapshot = await wire.ReceiveAsync();
        Assert.Equal("d", (string?)snapshot["d"]!["a"]);
        Assert.Equal("en/1", (string?)snapshot["d"]!["b"]!["p"]);
        Assert.Equal(1, (int?)snapshot["d"]!["b"]!["t"]);
        Assert.Equal("old message", (string?)snapshot["d"]!["b"]!["d"]!.AsObject().Single().Value!["comment"]);

        // live: another player's message reaches a listener of any room, whatever lang prefix it listened with
        await SomeoneSaysAsync("live");
        var push = await wire.ReceiveAsync();
        Assert.StartsWith("en/1/", (string?)push["d"]!["b"]!["p"]);
        Assert.Equal(1, (int?)push["d"]!["b"]!["t"]);
        Assert.Equal("live", (string?)push["d"]!["b"]!["d"]!["comment"]);

        // unlisten acked, then no further pushes
        await wire.SendAsync("""{"t":"d","d":{"a":"n","r":2,"b":{"p":"en/1","t":1}}}""");
        Assert.Equal(2, (int?)(await wire.ReceiveAsync())["d"]!["r"]);
        await SomeoneSaysAsync("silent");
        await Task.Delay(400); // several poll intervals
        await wire.SendAsync("""{"t":"d","d":{"a":"s","r":3,"b":{}}}""");
        Assert.Equal(3, (int?)(await wire.ReceiveAsync())["d"]!["r"]); // next frame is the ack, not a push

        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", default);
    }

    [Fact]
    public async Task The_history_is_chats_newest_page_in_the_clients_dto_and_each_message_is_pushed_once()
    {
        await using var factory = Factory();
        await SomeoneSaysAsync("", stamp: "stamp:6051003");
        for (var i = 0; i < 130; i++) await SomeoneSaysAsync($"m{i}");
        var (ws, wire) = await ConnectAsync(factory);

        await wire.SendAsync(Listen(1, 7, "ja/3"));
        await wire.ReceiveAsync(); // ack
        var snapshot = (await wire.ReceiveAsync())["d"]!["b"]!["d"]!.AsObject();

        // Chat answered the newest 100 (the adapter asked for after=0, limit=100); the oldest 31 are not the client's.
        Assert.Contains(_chat.Requests, r => r.Method == "GET" && r.Query == "?after=0&limit=100");
        var rows = snapshot.Select(p => p.Value!).ToArray();
        Assert.Equal(100, rows.Length);
        Assert.Equal("m30", (string?)rows[0]["comment"]);
        Assert.Equal("m129", (string?)rows[^1]["comment"]);
        // The DTO is exactly the client's fields; the child key is the seq, which is what keeps the client's order.
        Assert.Equal(["comment", "honorId", "insertDatetime", "name", "stampId", "userId"],
            rows[0].AsObject().Select(p => p.Key).Order().ToArray());
        Assert.Equal(["32", "33"], snapshot.Select(p => p.Key).Take(2)); // seq 1 is the stamp, m<i> is seq i+2: m30 is 32
        var dates = rows.Select(r => DateTimeOffset.Parse((string)r["insertDatetime"]!)).ToArray();
        Assert.True(dates.Zip(dates.Skip(1), (a, b) => a < b).All(x => x), "insertDatetime must be strictly increasing");
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}[+-]\d\d:\d\d$", (string)rows[0]["insertDatetime"]!);

        // A second listener on another room is the same stream; a message arrives once per listener, never twice.
        await SomeoneSaysAsync("after-snapshot");
        var push = await wire.ReceiveAsync();
        Assert.Equal("after-snapshot", (string?)push["d"]!["b"]!["d"]!["comment"]);
        await wire.SendAsync("""{"t":"d","d":{"a":"s","r":9,"b":{}}}""");
        Assert.Equal(9, (int?)(await wire.ReceiveAsync())["d"]!["r"]); // no duplicate push in between
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", default);
    }

    [Fact]
    public async Task A_stamp_message_reaches_the_client_as_stampId_with_an_empty_comment()
    {
        await using var factory = Factory();
        var (ws, wire) = await ConnectAsync(factory);
        await wire.SendAsync(Listen(1, 1, "en/1"));
        await wire.ReceiveAsync(); await wire.ReceiveAsync();

        await SomeoneSaysAsync("", stamp: "stamp:6051009");
        var d = (await wire.ReceiveAsync())["d"]!["b"]!["d"]!;
        Assert.Equal(6051009, (int?)d["stampId"]);
        Assert.Equal("", (string?)d["comment"]);
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", default);
    }

    [Fact]
    public async Task Http_write_reaches_websocket_listener_with_server_side_identity()
    {
        await using var factory = Factory();
        var session = await LoginAsync(factory);
        var (ws, wire) = await ConnectAsync(factory);
        await wire.SendAsync(Listen(1, 3, "ja/2"));
        await wire.ReceiveAsync(); await wire.ReceiveAsync();

        Assert.Equal("1", (await PostAsync(session, new { chatRoomId = 2, comment = new string('x', 141), stampId = 0 })).Status);
        Assert.Equal("1", (await PostAsync(session, new { chatRoomId = 77, comment = "hi", stampId = 0 })).Status);
        Assert.Equal("1", (await PostAsync(session, new { })).Status);
        Assert.Equal("0", (await PostAsync(session, new { chatRoomId = 2, comment = "from http", stampId = 0 })).Status);

        var push = await wire.ReceiveAsync();
        var d = push["d"]!["b"]!["d"]!;
        Assert.StartsWith("ja/2/", (string?)push["d"]!["b"]!["p"]);
        Assert.Equal(3, (int?)push["d"]!["b"]!["t"]);
        Assert.Equal("from http", (string?)d["comment"]);
        Assert.Equal(session.UserId, (string?)d["userId"]); // the client's numeric id, mapped from chat's otomo sender id
        Assert.True((int?)d["honorId"] > 0);
        Assert.False(string.IsNullOrEmpty((string?)d["name"]));
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", default);
    }

    [Fact]
    public async Task Every_listening_socket_gets_the_message_and_nobody_polls_for_an_empty_house()
    {
        await using var factory = Factory();
        await Task.Delay(300);
        Assert.Empty(_chat.Requests); // no listener: no read

        var (ws1, wire1) = await ConnectAsync(factory);
        var (ws2, wire2) = await ConnectAsync(factory);
        await wire1.SendAsync(Listen(1, 1, "en/1")); await wire1.ReceiveAsync(); await wire1.ReceiveAsync();
        await wire2.SendAsync(Listen(1, 2, "en/4")); await wire2.ReceiveAsync(); await wire2.ReceiveAsync();

        await SomeoneSaysAsync("for both");
        Assert.Equal("for both", (string?)(await wire1.ReceiveAsync())["d"]!["b"]!["d"]!["comment"]);
        var second = await wire2.ReceiveAsync();
        Assert.Equal("for both", (string?)second["d"]!["b"]!["d"]!["comment"]);
        Assert.StartsWith("en/4/", (string?)second["d"]!["b"]!["p"]);
        await ws1.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", default);
        await ws2.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", default);
    }

    [Fact]
    public async Task A_listen_while_chat_is_down_gets_an_empty_room_and_the_relay_recovers()
    {
        await using var factory = Factory();
        var (ws, wire) = await ConnectAsync(factory);
        _chat.FailNextRequests = 2; // the resolve and its retry
        await wire.SendAsync(Listen(1, 1, "en/1"));
        await wire.ReceiveAsync(); // ack
        var snapshot = await wire.ReceiveAsync();
        Assert.Empty(snapshot["d"]!["b"]!["d"]!.AsObject());

        await SomeoneSaysAsync("back");
        Assert.Equal("back", (string?)(await wire.ReceiveAsync())["d"]!["b"]!["d"]!["comment"]);
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", default);
    }

    [Fact]
    public async Task A_room_that_is_not_in_the_master_is_an_empty_room_and_asks_chat_for_nothing()
    {
        await using var factory = Factory();
        var (ws, wire) = await ConnectAsync(factory);
        await wire.SendAsync(Listen(1, 1, "en/99"));
        await wire.ReceiveAsync();
        Assert.Empty((await wire.ReceiveAsync())["d"]!["b"]!["d"]!.AsObject());
        Assert.Empty(_chat.Requests);
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", default);
    }

    [Fact]
    public async Task The_adapter_keeps_no_chat_state_of_its_own()
    {
        await using var factory = Factory();
        var session = await LoginAsync(factory);
        Assert.Equal("0", (await PostAsync(session, new { chatRoomId = 1, comment = "ephemeral", stampId = 0 })).Status);
        var content = factory.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
        Assert.False(Directory.Exists(Path.Combine(content, "data", "chat")));
    }
}
