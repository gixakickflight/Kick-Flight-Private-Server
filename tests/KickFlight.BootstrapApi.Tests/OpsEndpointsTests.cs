using System.Net;
using System.Text;
using System.Text.Json;
using KickFlight.BootstrapApi.Ops;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// One temp Ops:Directory per test class: the NDJSON fallback store is the one the /admin/ops endpoints read, so a
// shared <test output>/data/ops would leak rows between tests.
public sealed class OpsEndpointsFixture : IDisposable
{
    public OpsEndpointsFixture()
    {
        Directory = Path.Combine(Path.GetTempPath(), "kf-ops-endpoints-" + Guid.NewGuid().ToString("N"));
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ops:Directory"] = Directory,
                ["Ops:ReleaseSha"] = "test-release-sha"
            }));
        });
    }

    public WebApplicationFactory<Program> Factory { get; }
    public string Directory { get; }

    public void Dispose()
    {
        Factory.Dispose();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch { /* best effort */ }
    }
}

public sealed class OpsEndpointsTests : IClassFixture<OpsEndpointsFixture>
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";

    private readonly OpsEndpointsFixture _fixture;

    public OpsEndpointsTests(OpsEndpointsFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Every_ops_endpoint_is_loopback_only_like_the_other_admin_routes()
    {
        // Seed a real battle so the battle route cannot answer 404 for "unknown battle" instead of the gate.
        var ops = _fixture.Factory.Services.GetRequiredService<OpsTelemetryService>();
        ops.RecordBattleStart("gate-user", "battle-gate", 1, null, 101, 180, OpsClientInfo.Empty);
        await ops.FlushAsync(TimeSpan.FromSeconds(5));

        foreach (var path in new[] { "/admin/ops/summary", "/admin/ops/exceptions", "/admin/ops/battle/battle-gate" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await AdminAsync("GET", path, remote: null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await AdminAsync("GET", path, remote: IPAddress.Parse("203.0.113.5"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await AdminAsync("GET", path, remote: IPAddress.Loopback, proxyHeader: "X-Forwarded-For")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await AdminAsync("GET", path, remote: IPAddress.Loopback, proxyHeader: "X-Real-IP")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await AdminAsync("GET", path, remote: IPAddress.IPv6Loopback)).StatusCode);
        }
    }

    [Fact]
    public async Task Windows_are_case_insensitive_and_unknown_ones_are_a_400_with_the_list()
    {
        foreach (var window in new[] { "deploy", "restart", "24h", "7d", "24H", "  7D  " })
        {
            var response = await AdminAsync("GET", $"/admin/ops/summary?window={Uri.EscapeDataString(window)}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(window.Trim().ToLowerInvariant(), response.Json.GetProperty("window").GetProperty("name").GetString());
        }

        var bad = await AdminAsync("GET", "/admin/ops/summary?window=this-release");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(new[] { "deploy", "restart", "24h", "7d" },
            bad.Json.GetProperty("windows").EnumerateArray().Select(e => e.GetString()!).ToArray());

        var missing = await AdminAsync("GET", "/admin/ops/summary");
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
        Assert.Equal("deploy", missing.Json.GetProperty("window").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Summary_exposes_outcome_counts_breakdowns_and_problem_matches()
    {
        var ops = _fixture.Factory.Services.GetRequiredService<OpsTelemetryService>();
        var now = DateTime.UtcNow;
        ops.RecordEntry("1000001", "entry-1", "ticket-1", 7, 4, 3010001, 64, true, new OpsClientInfo("2.11.2", "Pixel", "14", "android", "ua", "1.2.3.4"));
        ops.RecordBattleStart("1000001", "battle-1", 7, 4, 801, 180, new OpsClientInfo("2.11.2", "Pixel", "14", "android", "ua", "1.2.3.4"));
        ops.RecordAnalysis("1000001", "PhotonGetResionPingFailed",
            new System.Text.Json.Nodes.JsonObject { ["cause"] = "[Analysis]\nNullReferenceException: boom\n  at Foo () [0x1] in <x>:0" }.ToJsonString(),
            new OpsClientInfo("2.11.2", "Pixel", "14", "android", "ua", "1.2.3.4"));
        ops.RecordBattleEnd("1000001", "battle-1", new OpsClientInfo("2.11.2", "Pixel", "14", "android", "ua", "1.2.3.4"));

        var summary = await AdminAsync("GET", "/admin/ops/summary?window=24h");
        var totals = summary.Json.GetProperty("totals");
        Assert.Equal(1, totals.GetProperty("completedPlayers").GetInt32());
        Assert.Equal(1, totals.GetProperty("exceptions").GetInt32());
        Assert.True(summary.Json.GetProperty("eventKinds").TryGetProperty("client_exception", out _));
        var breakdown = summary.Json.GetProperty("breakdowns");
        Assert.True(breakdown.GetProperty("appVersion").GetArrayLength() >= 1);
        Assert.True(breakdown.GetProperty("rule").GetArrayLength() >= 1);

        var exceptions = await AdminAsync("GET", "/admin/ops/exceptions?window=24h");
        Assert.Equal(1, exceptions.Json.GetProperty("total").GetInt32());
        var group = Assert.Single(exceptions.Json.GetProperty("groups").EnumerateArray());
        Assert.Equal("client_exception", group.GetProperty("kind").GetString());
        Assert.Equal(1, group.GetProperty("count").GetInt32());
        Assert.Contains("NullReferenceException", group.GetProperty("signature").GetString());
    }

    [Fact]
    public async Task Battle_drill_down_returns_the_timeline_and_unknown_ids_are_404()
    {
        var ops = _fixture.Factory.Services.GetRequiredService<OpsTelemetryService>();
        ops.RecordBattleStart("1000009", "battle-drill", 7, null, 801, 180, OpsClientInfo.Empty);
        ops.RecordAnalysis("1000009", "photon/ping", "42", OpsClientInfo.Empty);

        var battle = await AdminAsync("GET", "/admin/ops/battle/battle-drill");
        Assert.Equal(HttpStatusCode.OK, battle.StatusCode);
        Assert.Equal("battle-drill", battle.Json.GetProperty("battle").GetProperty("battleId").GetString());
        var kinds = battle.Json.GetProperty("timeline").EnumerateArray().Select(e => e.GetProperty("kind").GetString()).ToList();
        Assert.Contains("start", kinds);
        Assert.Contains("photon/ping", kinds);

        Assert.Equal(HttpStatusCode.NotFound, (await AdminAsync("GET", "/admin/ops/battle/does-not-exist")).StatusCode);
    }

    [Fact]
    public async Task Disconnect_timeout_and_leaving_handlers_answer_and_record_their_events()
    {
        var expected = new (string Path, string Kind, object Body)[]
        {
            ("/battle/disconnect", "battle_disconnect", new { battleEntryId = "entry-x", cause = "ClientTimeout" }),
            ("/battle/timeout", "battle_timeout", new { battleEntryId = "entry-y" }),
            ("/battle/leaving", "battle_leaving", new { battleEntryId = "entry-z", battleId = "battle-z" })
        };
        var before = await EventKindCountsAsync();

        foreach (var (path, kind, body) in expected)
        {
            var session = await CreateSessionAsync();
            using var response = await RawPostAsync(session, path, body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("0", response.Headers.GetValues("x-app-status-code").Single());
            Assert.Equal("{}", Encoding.UTF8.GetString(D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), session.Key)));

            var after = await EventKindCountsAsync();
            Assert.True(after.GetValueOrDefault(kind) > before.GetValueOrDefault(kind), $"{kind} was not recorded");
            before = after;
        }
    }

    [Fact]
    public async Task A_body_that_cannot_be_decoded_still_answers_success_and_records_the_signal()
    {
        var before = (await EventKindCountsAsync()).GetValueOrDefault("battle_disconnect");
        var session = await CreateSessionAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/battle/disconnect")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("not d2c"))
        };
        request.Headers.Host = Host;
        using var response = await session.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0", response.Headers.GetValues("x-app-status-code").Single());
        Assert.True((await EventKindCountsAsync()).GetValueOrDefault("battle_disconnect") > before);
    }

    private async Task<Dictionary<string, int>> EventKindCountsAsync()
    {
        var summary = await AdminAsync("GET", "/admin/ops/summary?window=24h");
        return summary.Json.GetProperty("eventKinds").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.Ordinal);
    }

    private sealed record AdminResponse(HttpStatusCode StatusCode, JsonElement Json);

    private Task<AdminResponse> AdminAsync(string method, string path) =>
        AdminAsync(method, path, IPAddress.Loopback, null);

    // TestServer has no socket: the remote address is set on the context directly, exactly like MaintenanceTests.
    private async Task<AdminResponse> AdminAsync(string method, string path, IPAddress? remote, string? proxyHeader = null)
    {
        var query = "";
        var question = path.IndexOf('?');
        if (question >= 0)
        {
            query = path[question..];
            path = path[..question];
        }
        var context = await _fixture.Factory.Server.SendAsync(http =>
        {
            http.Request.Method = method;
            http.Request.Path = path;
            http.Request.QueryString = new QueryString(query);
            http.Request.Host = new HostString("127.0.0.1", 8080);
            http.Connection.RemoteIpAddress = remote;
            if (proxyHeader is not null) http.Request.Headers[proxyHeader] = "198.51.100.1";
        });
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        var json = context.Response.StatusCode is 200 or 400 or 404 && text.Length > 0
            ? JsonDocument.Parse(text).RootElement.Clone()
            : default;
        return new AdminResponse((HttpStatusCode)context.Response.StatusCode, json);
    }

    private async Task<DemoSession> CreateSessionAsync()
    {
        var client = _fixture.Factory.CreateClient();
        var key = Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");
        var payload = JsonSerializer.Serialize(new { hash = Encoding.ASCII.GetString(key), uuid = Guid.NewGuid().ToString("N") });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/index")
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(payload), Encoding.ASCII.GetBytes(CommonCode), new byte[16]))
        };
        request.Headers.Host = Host;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.DefaultRequestHeaders.Add("x-app-access-token", response.Headers.GetValues("x-app-access-token").Single());
        return new DemoSession(client, key, response.Headers.GetValues("x-app-user-id").Single());
    }

    private static async Task<HttpResponseMessage> RawPostAsync(DemoSession session, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body)), session.Key, new byte[16]))
        };
        request.Headers.Host = Host;
        return await session.Client.SendAsync(request);
    }

    private sealed record DemoSession(HttpClient Client, byte[] Key, string UserId);
}
