using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KickFlight.BootstrapApi.PlayerStore;
using KickFlight.BootstrapApi.Push;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

public sealed class PushTests : IClassFixture<ServerTestHostFixture>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kf-push-" + Guid.NewGuid().ToString("N")[..12]);

    public PushTests(ServerTestHostFixture fixture) => _factory = fixture.Factory;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class Env(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "push-tests";
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Test";
        public string WebRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private JsonPlayerStore NewStore() => new(NullLogger<JsonPlayerStore>.Instance, new Env(_directory));

    private static long NewPlayer(IPlayerStore store)
    {
        var id = store.ResolvePlayerId(Guid.NewGuid().ToString("N"));
        store.Save(new SessionState { UserId = id.ToString() });
        return id;
    }

    private static string Token(char c) => new string(c, 40);

    // ---- register endpoint ----

    private async Task<HttpResponseMessage> PostRegister(object body)
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/push/register") { Content = JsonContent.Create(body) };
        request.Headers.Host = "kickflight-api.grenge.jp";
        request.Headers.Add("X-Real-IP", "198.51.100." + Random.Shared.Next(1, 250));
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Register_endpoint_validates_input_and_dedupes_tokens()
    {
        var store = _factory.Services.GetRequiredService<IPlayerStore>();
        var a = NewPlayer(store);
        var b = NewPlayer(store);
        var token = "tok:" + Guid.NewGuid().ToString("N");

        using (var ok = await PostRegister(new { userId = a, token, platform = "android" }))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.True((await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
        }
        Assert.Equal(token, store.TryLoad(a)!.PushToken);
        Assert.Equal("android", store.TryLoad(a)!.PushPlatform);
        Assert.NotNull(store.TryLoad(a)!.PushTokenUpdatedAtUtc);

        // userId may also be a string; the same token moves to the new player.
        using (var moved = await PostRegister(new { userId = b.ToString(), token, platform = "android" }))
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Null(store.TryLoad(a)!.PushToken);
        Assert.Equal(token, store.TryLoad(b)!.PushToken);

        using (var empty = await PostRegister(new { userId = a, token = "", platform = "android" }))
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using (var tooLong = await PostRegister(new { userId = a, token = new string('x', 4097), platform = "android" }))
            Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        using (var badChars = await PostRegister(new { userId = a, token = "bad token<>", platform = "android" }))
            Assert.Equal(HttpStatusCode.BadRequest, badChars.StatusCode);
        using (var noUser = await PostRegister(new { token }))
            Assert.Equal(HttpStatusCode.BadRequest, noUser.StatusCode);
        using (var unknown = await PostRegister(new { userId = 999999999L, token = "abc", platform = "android" }))
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public void Throttle_allows_ten_per_minute_per_key()
    {
        var clock = new FakeClock();
        var throttle = new PushRegisterThrottle(clock);
        for (var i = 0; i < 10; i++) Assert.True(throttle.TryAcquire("1.2.3.4"));
        Assert.False(throttle.TryAcquire("1.2.3.4"));
        Assert.True(throttle.TryAcquire("5.6.7.8"));
        clock.Now = clock.Now.AddSeconds(61);
        Assert.True(throttle.TryAcquire("1.2.3.4"));
    }

    // ---- notifier ----

    private sealed class FakeSender : IFcmSender
    {
        public bool IsConfigured { get; set; } = true;
        public List<string> Sent { get; } = [];
        public Dictionary<string, FcmSendOutcome> Outcomes { get; } = new();
        public Task<FcmSendOutcome> SendAsync(string token, CancellationToken ct)
        {
            lock (Sent) Sent.Add(token);
            return Task.FromResult(Outcomes.GetValueOrDefault(token, FcmSendOutcome.Sent));
        }
    }

    private (RankedSearchNotifier Notifier, FakeSender Sender, FakeClock Clock, JsonPlayerStore Store, PushTokenService Tokens)
        NewNotifier(PushOptions? options = null)
    {
        var store = NewStore();
        var clock = new FakeClock();
        var sender = new FakeSender();
        var tokens = new PushTokenService(store, clock);
        var notifier = new RankedSearchNotifier(store, sender, tokens,
            Options.Create(options ?? new PushOptions { Enabled = true }), clock, NullLogger<RankedSearchNotifier>.Instance);
        return (notifier, sender, clock, store, tokens);
    }

    [Fact]
    public async Task Recipients_exclude_searcher_queued_and_recently_active_players()
    {
        var (notifier, sender, clock, store, tokens) = NewNotifier();
        var searcher = NewPlayer(store);
        var queued = NewPlayer(store);
        var active = NewPlayer(store);
        var idle = NewPlayer(store);
        var noToken = NewPlayer(store);
        foreach (var (id, c) in new[] { (searcher, 'a'), (queued, 'b'), (active, 'c'), (idle, 'd') })
            Assert.Equal(PushRegisterResult.Ok, tokens.Register(id, Token(c), "android"));

        var attempted = await notifier.NotifyAsync(searcher, id => id == queued,
            id => id == active ? clock.Now.AddSeconds(-30) : id == idle ? clock.Now.AddSeconds(-500) : null);

        Assert.Equal(1, attempted);
        Assert.Equal([Token('d')], sender.Sent);
        Assert.Null(store.TryLoad(noToken)!.PushToken);
    }

    [Fact]
    public async Task Global_and_per_player_cooldowns_are_enforced_with_an_injected_clock()
    {
        var (notifier, sender, clock, store, tokens) = NewNotifier();
        var s1 = NewPlayer(store);
        var s2 = NewPlayer(store);
        var r = NewPlayer(store);
        tokens.Register(r, Token('r'), "android");

        Assert.Equal(1, await notifier.NotifyAsync(s1, _ => false, _ => null));
        // Global cooldown blocks any searcher, including a different one.
        clock.Now = clock.Now.AddSeconds(100);
        Assert.Equal(0, await notifier.NotifyAsync(s2, _ => false, _ => null));
        Assert.Equal(0, await notifier.NotifyAsync(s1, _ => false, _ => null));
        // Past the global cooldown a new searcher passes, the first is still on its own cooldown only until 240s.
        clock.Now = clock.Now.AddSeconds(141);
        Assert.Equal(1, await notifier.NotifyAsync(s2, _ => false, _ => null));
        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task Per_player_cooldown_applies_when_global_cooldown_is_shorter()
    {
        var (notifier, sender, clock, store, tokens) = NewNotifier(
            new PushOptions { Enabled = true, GlobalCooldownSeconds = 10, PerPlayerCooldownSeconds = 240 });
        var s = NewPlayer(store);
        var r = NewPlayer(store);
        tokens.Register(r, Token('r'), "android");
        Assert.Equal(1, await notifier.NotifyAsync(s, _ => false, _ => null));
        clock.Now = clock.Now.AddSeconds(60);
        Assert.Equal(0, await notifier.NotifyAsync(s, _ => false, _ => null));
        clock.Now = clock.Now.AddSeconds(200);
        Assert.Equal(1, await notifier.NotifyAsync(s, _ => false, _ => null));
        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task Disabled_or_unconfigured_is_a_no_op()
    {
        var (disabled, sender, _, store, tokens) = NewNotifier(new PushOptions { Enabled = false });
        var s = NewPlayer(store);
        tokens.Register(NewPlayer(store), Token('r'), "android");
        Assert.Equal(0, await disabled.NotifyAsync(s, _ => false, _ => null));
        Assert.Empty(sender.Sent);

        var (enabled, sender2, _, store2, tokens2) = NewNotifier();
        sender2.IsConfigured = false;
        tokens2.Register(NewPlayer(store2), Token('r'), "android");
        Assert.Equal(0, await enabled.NotifyAsync(NewPlayer(store2), _ => false, _ => null));
        Assert.Empty(sender2.Sent);
    }

    [Fact]
    public async Task Invalid_token_outcome_clears_the_recipients_token()
    {
        var (notifier, sender, _, store, tokens) = NewNotifier();
        var s = NewPlayer(store);
        var r = NewPlayer(store);
        tokens.Register(r, Token('r'), "android");
        sender.Outcomes[Token('r')] = FcmSendOutcome.InvalidToken;
        await notifier.NotifyAsync(s, _ => false, _ => null);
        Assert.Null(store.TryLoad(r)!.PushToken);
    }

    // ---- FcmSender over a mocked HttpMessageHandler ----

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return respond(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private FcmSender NewFcm(Handler handler, FakeClock clock)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "sa.json");
        using var rsa = RSA.Create(2048);
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["project_id"] = "demo-project",
            ["client_email"] = "svc@demo-project.iam.gserviceaccount.com",
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["token_uri"] = "https://oauth.test/token"
        }));
        return new FcmSender(new Factory(handler),
            Options.Create(new PushOptions { Enabled = true, CredentialsPath = path }), clock, NullLogger<FcmSender>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Oauth() => Json(HttpStatusCode.OK, """{"access_token":"at-1","expires_in":3600}""");

    [Fact]
    public async Task Fcm_sends_expected_message_and_caches_access_token()
    {
        var handler = new Handler(r => r.RequestUri!.Host == "oauth.test" ? Oauth() : Json(HttpStatusCode.OK, "{}"));
        var fcm = NewFcm(handler, new FakeClock());
        Assert.Equal(FcmSendOutcome.Sent, await fcm.SendAsync("T1", default));
        Assert.Equal(FcmSendOutcome.Sent, await fcm.SendAsync("T2", default));

        Assert.Equal(1, handler.Requests.Count(r => r.RequestUri!.Host == "oauth.test"));
        var send = handler.Requests.First(r => r.RequestUri!.Host == "fcm.googleapis.com");
        Assert.Equal("https://fcm.googleapis.com/v1/projects/demo-project/messages:send", send.RequestUri!.ToString());
        Assert.Equal("Bearer", send.Headers.Authorization!.Scheme);
        Assert.Equal("at-1", send.Headers.Authorization.Parameter);
        var message = JsonDocument.Parse(handler.Bodies[handler.Requests.IndexOf(send)]).RootElement.GetProperty("message");
        Assert.Equal("T1", message.GetProperty("token").GetString());
        var android = message.GetProperty("android");
        Assert.Equal("HIGH", android.GetProperty("priority").GetString());
        Assert.Equal("60s", android.GetProperty("ttl").GetString());
        Assert.Equal("ranked_search", android.GetProperty("collapse_key").GetString());
        Assert.Equal("ranked_search", android.GetProperty("notification").GetProperty("tag").GetString());
    }

    [Theory]
    [InlineData(404, """{"error":{"code":404,"status":"NOT_FOUND","details":[{"errorCode":"UNREGISTERED"}]}}""", FcmSendOutcome.InvalidToken)]
    [InlineData(400, """{"error":{"code":400,"status":"INVALID_ARGUMENT","message":"The registration token is not a valid FCM registration token"}}""", FcmSendOutcome.InvalidToken)]
    [InlineData(400, """{"error":{"code":400,"status":"INVALID_ARGUMENT","message":"Invalid JSON payload"}}""", FcmSendOutcome.Failed)]
    [InlineData(500, """{"error":{"code":500,"status":"INTERNAL"}}""", FcmSendOutcome.Failed)]
    public async Task Fcm_error_responses_are_classified(int status, string body, FcmSendOutcome expected)
    {
        var handler = new Handler(r => r.RequestUri!.Host == "oauth.test" ? Oauth() : Json((HttpStatusCode)status, body));
        var fcm = NewFcm(handler, new FakeClock());
        Assert.Equal(expected, await fcm.SendAsync("T1", default));
    }

    [Fact]
    public async Task Fcm_unregistered_response_clears_the_stored_token_end_to_end()
    {
        var handler = new Handler(r => r.RequestUri!.Host == "oauth.test"
            ? Oauth()
            : Json(HttpStatusCode.NotFound, """{"error":{"status":"NOT_FOUND","details":[{"errorCode":"UNREGISTERED"}]}}"""));
        var clock = new FakeClock();
        var fcm = NewFcm(handler, clock);
        var store = NewStore();
        var tokens = new PushTokenService(store, clock);
        var notifier = new RankedSearchNotifier(store, fcm, tokens,
            Options.Create(new PushOptions { Enabled = true, CredentialsPath = Path.Combine(_directory, "sa.json") }),
            clock, NullLogger<RankedSearchNotifier>.Instance);
        var searcher = NewPlayer(store);
        var recipient = NewPlayer(store);
        tokens.Register(recipient, Token('z'), "android");

        await notifier.NotifyAsync(searcher, _ => false, _ => null);
        Assert.Null(store.TryLoad(recipient)!.PushToken);
    }

    [Fact]
    public void Token_mask_shows_at_most_six_characters()
    {
        Assert.Equal("...abcdef", PushTokenService.Mask("0123456789abcdef"));
        Assert.Equal("", PushTokenService.Mask(null));
    }
}
