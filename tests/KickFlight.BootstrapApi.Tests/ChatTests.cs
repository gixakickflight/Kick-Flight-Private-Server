using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KickFlight.BootstrapApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// Chat store + Firebase-RTDB-compatible websocket (docs/re-notes/chat.md).
[Collection("Matchmaking static state")]
public sealed class ChatTests : IDisposable
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kf-chat-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class Env : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "t";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private ChatService NewService()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Chat:DataDirectory"] = _dir }).Build();
        return new ChatService(new Env(), config, TimeProvider.System, NullLogger<ChatService>.Instance);
    }

    [Fact]
    public async Task Write_then_list_returns_message_with_required_fields()
    {
        var chat = NewService();
        var m = await chat.WriteAsync(1, "hello", 0, 1000027, "Tester", 6010001);
        Assert.NotNull(m);
        var recent = chat.Recent(1);
        Assert.Single(recent);
        Assert.Equal("1000027", recent[0].UserId);
        Assert.Equal(6010001, recent[0].HonorId);
        Assert.True(DateTimeOffset.TryParse(recent[0].InsertDatetime, out var dt) && dt > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Matches(@"[+-]\d\d:\d\d$", recent[0].InsertDatetime);
    }

    [Fact]
    public async Task Keeps_only_newest_100_in_order()
    {
        var chat = NewService();
        for (var i = 0; i < 130; i++) await chat.WriteAsync(1, $"m{i}", 0, 5, "N", 6010000);
        var recent = chat.Recent(1);
        Assert.Equal(100, recent.Count);
        Assert.Equal("m30", recent[0].Comment);
        Assert.Equal("m129", recent[^1].Comment);
        Assert.Empty(chat.Recent(2));
    }

    [Fact]
    public async Task Persists_across_restart_and_keys_keep_increasing()
    {
        var first = NewService();
        await first.WriteAsync(3, "persist me", 0, 7, "N", 6010000);
        var second = NewService();
        Assert.Equal("persist me", second.Recent(3).Single().Comment);
        var next = await second.WriteAsync(3, "after", 0, 7, "N", 6010000);
        Assert.True(long.Parse(next!.Key) > long.Parse(second.Recent(3)[0].Key));
    }

    [Theory]
    [InlineData(1, "", 0)]            // empty, no stamp
    [InlineData(1, null, 0)]
    [InlineData(99, "hi", 0)]         // unknown room
    [InlineData(1, "hi", -1)]         // bad stamp
    public async Task Rejects_invalid(int room, string? comment, int stamp)
    {
        var chat = NewService();
        Assert.Null(await chat.WriteAsync(room, comment, stamp, 1, "N", 6010000));
        Assert.Empty(chat.Recent(1));
    }

    [Fact]
    public async Task Length_limit_is_140_and_stamp_only_is_allowed()
    {
        var chat = NewService();
        Assert.NotNull(await chat.WriteAsync(1, new string('a', 140), 0, 1, "N", 6010000));
        Assert.Null(await chat.WriteAsync(1, new string('a', 141), 0, 1, "N", 6010000));
        Assert.NotNull(await chat.WriteAsync(1, "", 5, 1, "N", 6010000));
    }

    [Fact]
    public async Task Zero_honor_falls_back_to_a_valid_default()
    {
        var chat = NewService();
        var m = await chat.WriteAsync(1, "x", 0, 1, "", 0);
        Assert.Equal(6010000, m!.HonorId);
        Assert.Equal("Player 1", m.Name);
    }

    [Theory]
    [InlineData("en/1", 1)]
    [InlineData("ja/4", 4)]
    [InlineData("en/x", null)]
    [InlineData("en", null)]
    public void Room_from_path(string path, int? expected) => Assert.Equal(expected, ChatService.RoomFromPath(path));

    // ---- websocket protocol ----

    private sealed class Wire(WebSocket ws)
    {
        public async Task SendAsync(string text) => await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, default);
        public async Task<JsonNode> ReceiveAsync()
        {
            var buf = new byte[32 * 1024];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var r = await ws.ReceiveAsync(buf, cts.Token);
            return JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, r.Count))!;
        }
    }

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        b.UseContentRoot(AppContext.BaseDirectory).ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Chat:DataDirectory"] = _dir })));

    private static string Listen(int r, int tag, string path) =>
        "{\"t\":\"d\",\"d\":{\"a\":\"q\",\"r\":" + r + ",\"b\":{\"p\":\"" + path
        + "\",\"q\":{\"vf\":\"r\",\"i\":\"insertDatetime\",\"l\":100},\"t\":" + tag + ",\"h\":\"\"}}}";

    [Fact]
    public async Task Websocket_handshake_ack_listen_and_broadcast()
    {
        await using var factory = Factory();
        var chat = factory.Services.GetRequiredService<ChatService>();
        await chat.WriteAsync(1, "old message", 0, 9, "Old", 6010000);

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
        var first = snapshot["d"]!["b"]!["d"]!.AsObject().Single().Value!;
        Assert.Equal("old message", (string?)first["comment"]);

        // live broadcast to a listener of the room, whatever lang prefix it listened with
        await chat.WriteAsync(1, "live", 0, 9, "Old", 6010001);
        var push = await wire.ReceiveAsync();
        Assert.Equal("en/1", ((string?)push["d"]!["b"]!["p"])![..4]);
        Assert.StartsWith("en/1/", (string?)push["d"]!["b"]!["p"]);
        Assert.Equal(1, (int?)push["d"]!["b"]!["t"]);
        Assert.Equal("live", (string?)push["d"]!["b"]!["d"]!["comment"]);

        // unlisten acked, then no further pushes for that room
        await wire.SendAsync("""{"t":"d","d":{"a":"n","r":2,"b":{"p":"en/1","t":1}}}""");
        Assert.Equal(2, (int?)(await wire.ReceiveAsync())["d"]!["r"]);
        await chat.WriteAsync(1, "silent", 0, 9, "Old", 6010001);
        await wire.SendAsync("""{"t":"d","d":{"a":"s","r":3,"b":{}}}""");
        Assert.Equal(3, (int?)(await wire.ReceiveAsync())["d"]!["r"]); // next frame is the ack, not a push

        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", default);
    }

    [Fact]
    public async Task Http_write_reaches_websocket_listener_with_server_side_identity()
    {
        await using var factory = Factory();
        var client = factory.CreateClient();
        var key = Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");
        var payload = JsonSerializer.Serialize(new { hash = Encoding.ASCII.GetString(key), uuid = Guid.NewGuid().ToString("N") });
        using var auth = new HttpRequestMessage(HttpMethod.Post, "/auth/index")
        { Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(payload), Encoding.ASCII.GetBytes(CommonCode), new byte[16])) };
        auth.Headers.Host = Host;
        using var authResponse = await client.SendAsync(auth);
        client.DefaultRequestHeaders.Add("x-app-access-token", authResponse.Headers.GetValues("x-app-access-token").Single());
        var userId = authResponse.Headers.GetValues("x-app-user-id").Single();

        var ws = await factory.Server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/.ws?ns=kfchat&v=5"), default);
        var wire = new Wire(ws);
        await wire.ReceiveAsync();
        await wire.SendAsync(Listen(1, 3, "ja/2"));
        await wire.ReceiveAsync(); await wire.ReceiveAsync();

        async Task<(string Status, string Body)> Post(object body)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/chatRoomComment/write")
            { Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body)), key, new byte[16])) };
            req.Headers.Host = Host;
            using var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            return (resp.Headers.GetValues("x-app-status-code").Single(), Encoding.UTF8.GetString(D2CCodec.Decode(await resp.Content.ReadAsByteArrayAsync(), key)));
        }

        Assert.Equal("1", (await Post(new { chatRoomId = 2, comment = new string('x', 141), stampId = 0 })).Status);
        Assert.Equal("1", (await Post(new { chatRoomId = 77, comment = "hi", stampId = 0 })).Status);
        Assert.Equal("1", (await Post(new { })).Status);
        Assert.Equal("0", (await Post(new { chatRoomId = 2, comment = "from http", stampId = 0 })).Status);

        var push = await wire.ReceiveAsync();
        var d = push["d"]!["b"]!["d"]!;
        Assert.Equal("from http", (string?)d["comment"]);
        Assert.Equal(userId, (string?)d["userId"]);
        Assert.True((int?)d["honorId"] > 0);
        Assert.False(string.IsNullOrEmpty((string?)d["name"]));
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", default);
    }
}
