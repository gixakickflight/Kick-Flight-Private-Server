using System.Net;
using System.Text;
using System.Text.Json;
using KickFlight.BootstrapApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// A host of its own with its own state file: the minimum is persisted, and a value left behind in the shared
// <test output>/data/client-update.json would make every other test class's host answer 1400.
public sealed class ClientUpdateFixture : IDisposable
{
    public ClientUpdateFixture()
    {
        StateFile = Path.Combine(Path.GetTempPath(), $"kf-client-update-{Guid.NewGuid():N}.json");
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseContentRoot(AppContext.BaseDirectory)
            .UseSetting("ClientUpdate:FilePath", StateFile));
    }

    public WebApplicationFactory<Program> Factory { get; }

    public string StateFile { get; }

    public void Dispose()
    {
        Factory.Dispose();
        File.Delete(StateFile);
    }
}

public sealed class ClientUpdateTests : IClassFixture<ClientUpdateFixture>, IDisposable
{
    private const string Host = "kickflight-api.grenge.jp";

    private readonly WebApplicationFactory<Program> _factory;

    public ClientUpdateTests(ClientUpdateFixture fixture)
    {
        _factory = fixture.Factory;
    }

    // Every test starts and ends with the switch off.
    public void Dispose() => _factory.Services.GetRequiredService<ClientUpdateState>().Set("");

    [Fact]
    public async Task Off_by_default_passes_posts_with_any_version()
    {
        Assert.Equal("", _factory.Services.GetRequiredService<ClientUpdateState>().Current.MinimumVersion);

        foreach (var version in new string?[] { null, "2.11.0", "0.0.1" })
        {
            using var response = await PostAsync(_factory.CreateClient(), "/boot/index", version);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("0", response.Headers.GetValues("x-app-status-code").Single());
        }
    }

    [Fact]
    public async Task Old_version_gets_1400_header_and_plaintext_body_on_boot_and_other_posts()
    {
        await SetAsync("2.12.0");

        // /boot/index is served from a fixture and the rest are real handlers; the gate is before auth, so an
        // already-logged-in old build is refused on every POST too.
        foreach (var path in new[] { "/boot/index", "/home/index", "/startup/index" })
        {
            using var response = await PostAsync(_factory.CreateClient(), path, "2.11.0");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("1", response.Headers.GetValues("x-app-status-code").Single());
            // Plaintext JSON, not D2C: the client parses it before any decryption.
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var error = body.RootElement.GetProperty("error");
            Assert.Equal("1400", error.GetProperty("code").GetString());
            Assert.Equal(ClientUpdateState.DefaultTitle, error.GetProperty("title").GetString());
            Assert.Equal(ClientUpdateState.DefaultMessage, error.GetProperty("message").GetString());
        }
    }

    [Fact]
    public async Task Equal_or_newer_version_passes()
    {
        await SetAsync("2.12.0");

        foreach (var version in new[] { "2.12.0", "2.12.1", "2.13", "3.0.0" })
        {
            using var response = await PostAsync(_factory.CreateClient(), "/boot/index", version);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("0", response.Headers.GetValues("x-app-status-code").Single());
        }
    }

    [Fact]
    public async Task Diag_suffix_is_compared_as_its_numeric_version()
    {
        // "2.11.1-diag" is 2.11.1: with that minimum it reaches the normal handler, not the 1400 window.
        await SetAsync("2.11.1");
        using (var same = await PostAsync(_factory.CreateClient(), "/boot/index", "2.11.1-diag"))
        {
            Assert.Equal(HttpStatusCode.OK, same.StatusCode);
            Assert.Equal("0", same.Headers.GetValues("x-app-status-code").Single());
        }

        // A genuinely older DIAG build is still refused.
        await SetAsync("2.11.2");
        using (var older = await PostAsync(_factory.CreateClient(), "/boot/index", "2.11.1-diag"))
        {
            Assert.Equal(HttpStatusCode.OK, older.StatusCode);
            Assert.Equal("1", older.Headers.GetValues("x-app-status-code").Single());
            using var body = JsonDocument.Parse(await older.Content.ReadAsStringAsync());
            Assert.Equal("1400", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Missing_header_treated_as_old_when_enabled()
    {
        await SetAsync("2.12.0");

        using var response = await PostAsync(_factory.CreateClient(), "/boot/index", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1", response.Headers.GetValues("x-app-status-code").Single());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("1400", body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Admin_endpoint_only_answers_unforwarded_loopback_callers()
    {
        // No remote address at all, a remote one, and loopback carrying either proxy header (= came through nginx).
        Assert.Equal(HttpStatusCode.NotFound, (await AdminAsync("GET", null, remote: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AdminAsync("GET", null, IPAddress.Parse("203.0.113.5"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AdminAsync("GET", null, IPAddress.Loopback, "X-Forwarded-For")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AdminAsync("GET", null, IPAddress.Loopback, "X-Real-IP")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await AdminAsync("POST", JsonSerializer.Serialize(new { minimumVersion = "9.9.9" }), IPAddress.Loopback, "X-Forwarded-For")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await AdminAsync("POST", JsonSerializer.Serialize(new { minimumVersion = "9.9.9" }), IPAddress.Parse("10.0.0.7"))).StatusCode);
        Assert.Equal("", _factory.Services.GetRequiredService<ClientUpdateState>().Current.MinimumVersion);

        var status = await AdminAsync("GET", null, IPAddress.IPv6Loopback);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("", status.Json.GetProperty("minimumVersion").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminAsync("POST", """{"minimumVersion":"not-a-version"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AdminAsync("GET", null, IPAddress.Loopback, path: "/admin/other")).StatusCode);
    }

    [Fact]
    public void State_round_trips_through_the_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kf-client-update-{Guid.NewGuid():N}", "client-update.json");
        try
        {
            var first = new ClientUpdateState(NullLogger<ClientUpdateState>.Instance, path);
            Assert.Equal("", first.Current.MinimumVersion);
            Assert.False(first.IsOutdated("2.11.0"));

            first.Set("2.12.0");
            var reloaded = new ClientUpdateState(NullLogger<ClientUpdateState>.Instance, path);
            Assert.Equal("2.12.0", reloaded.Current.MinimumVersion);
            Assert.True(reloaded.IsOutdated("2.11.0"));
            Assert.True(reloaded.IsOutdated("2.11"));
            Assert.True(reloaded.IsOutdated(null));
            Assert.True(reloaded.IsOutdated("not-a-version"));
            // A build suffix does not defeat the numeric comparison.
            Assert.True(reloaded.IsOutdated("2.11.0-release"));
            Assert.False(reloaded.IsOutdated("2.12.0"));
            Assert.False(reloaded.IsOutdated("2.12.0+7f3a"));
            // A DIAG build is exactly its numeric version.
            Assert.False(reloaded.IsOutdated("2.12.0-diag"));
            Assert.True(reloaded.IsOutdated("2.11.1-diag"));

            reloaded.Set("");
            Assert.Equal("", new ClientUpdateState(NullLogger<ClientUpdateState>.Instance, path).Current.MinimumVersion);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Config_seeds_the_minimum_until_a_state_file_exists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kf-client-update-{Guid.NewGuid():N}", "client-update.json");
        try
        {
            var seeded = new ClientUpdateState(NullLogger<ClientUpdateState>.Instance, path, "2.12.0");
            Assert.Equal("2.12.0", seeded.Current.MinimumVersion);

            // A persisted value - even "off" - wins over the config seed on the next start.
            seeded.Set("");
            Assert.Equal("", new ClientUpdateState(NullLogger<ClientUpdateState>.Instance, path, "2.12.0").Current.MinimumVersion);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public async Task Android_2_11_3_refused_with_1400_when_2_11_4_required()
    {
        await SetAsync("2.11.4");

        using var response = await PostAsync(_factory.CreateClient(), "/boot/index", "2.11.3");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1", response.Headers.GetValues("x-app-status-code").Single());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = body.RootElement.GetProperty("error");
        Assert.Equal("1400", error.GetProperty("code").GetString());
        Assert.Equal(ClientUpdateState.DefaultTitle, error.GetProperty("title").GetString());
        Assert.Equal(ClientUpdateState.DefaultMessage, error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Android_2_11_4_and_newer_accepted()
    {
        await SetAsync("2.11.4");

        foreach (var version in new[] { "2.11.4", "2.11.5", "2.12.0", "3.0.0" })
        {
            using var response = await PostAsync(_factory.CreateClient(), "/boot/index", version);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("0", response.Headers.GetValues("x-app-status-code").Single());
        }
    }

    [Fact]
    public async Task Platform_isolation_preserves_ios_2_11_3_while_android_2_11_3_is_refused()
    {
        await SetAsync("2.11.4", "2.11.3");

        // Android on 2.11.3 is refused
        using (var androidResponse = await PostAsync(_factory.CreateClient(), "/boot/index", "2.11.3", assetPlatform: "3", platform: "Android"))
        {
            Assert.Equal(HttpStatusCode.OK, androidResponse.StatusCode);
            Assert.Equal("1", androidResponse.Headers.GetValues("x-app-status-code").Single());
            using var body = JsonDocument.Parse(await androidResponse.Content.ReadAsStringAsync());
            Assert.Equal("1400", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        // iOS on 2.11.3 is accepted via x-app-asset-platform 2
        using (var iosAssetResponse = await PostAsync(_factory.CreateClient(), "/boot/index", "2.11.3", assetPlatform: "2"))
        {
            Assert.Equal(HttpStatusCode.OK, iosAssetResponse.StatusCode);
            // It reaches the fixture or handler, not the 1400 error window
            var statusCode = iosAssetResponse.Headers.GetValues("x-app-status-code").Single();
            Assert.Equal("0", statusCode);
        }

        // iOS on 2.11.3 is accepted via x-app-platform iOS
        using (var iosPlatformResponse = await PostAsync(_factory.CreateClient(), "/boot/index", "2.11.3", platform: "iOS"))
        {
            Assert.Equal(HttpStatusCode.OK, iosPlatformResponse.StatusCode);
            var statusCode = iosPlatformResponse.Headers.GetValues("x-app-status-code").Single();
            Assert.Equal("0", statusCode);
        }

        // iOS on older 2.11.0 is still refused
        using (var iosOldResponse = await PostAsync(_factory.CreateClient(), "/boot/index", "2.11.0", assetPlatform: "2"))
        {
            Assert.Equal(HttpStatusCode.OK, iosOldResponse.StatusCode);
            Assert.Equal("1", iosOldResponse.Headers.GetValues("x-app-status-code").Single());
            using var body = JsonDocument.Parse(await iosOldResponse.Content.ReadAsStringAsync());
            Assert.Equal("1400", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Initial_request_auth_prepare_without_headers_bypasses_gate()
    {
        await SetAsync("2.11.4");

        // /auth/prepare sends no x-app-application-version; it must not be stopped with 1400
        using var prepareResponse = await PostAsync(_factory.CreateClient(), "/auth/prepare", null);
        Assert.Equal(HttpStatusCode.OK, prepareResponse.StatusCode);
        Assert.Equal("0", prepareResponse.Headers.GetValues("x-app-status-code").Single());
        Assert.Equal("demo-auth-prepare", prepareResponse.Headers.GetValues("x-kickflight-fixture").Single());

        // But /auth/index immediately after it enforces the gate for older versions
        using var authOldResponse = await PostAsync(_factory.CreateClient(), "/auth/index", "2.11.3");
        Assert.Equal(HttpStatusCode.OK, authOldResponse.StatusCode);
        Assert.Equal("1", authOldResponse.Headers.GetValues("x-app-status-code").Single());
        using var body = JsonDocument.Parse(await authOldResponse.Content.ReadAsStringAsync());
        Assert.Equal("1400", body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void State_file_upgrades_when_config_seed_is_newer()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kf-client-update-{Guid.NewGuid():N}", "client-update.json");
        try
        {
            // Persist older version 2.11.3 into the state file
            var oldState = new ClientUpdateState(NullLogger<ClientUpdateState>.Instance, path);
            oldState.Set("2.11.3");
            Assert.Equal("2.11.3", oldState.Current.MinimumVersion);

            // Next deployment starts with seed 2.11.4; it must upgrade the stale on-disk minimum
            var upgradedState = new ClientUpdateState(NullLogger<ClientUpdateState>.Instance, path, "2.11.4", "2.11.3");
            Assert.Equal("2.11.4", upgradedState.Current.MinimumVersion);
            Assert.Equal("2.11.3", upgradedState.Current.IosMinimumVersion);

            // Re-reading from disk confirms the persisted file was also updated
            var reloaded = new ClientUpdateState(NullLogger<ClientUpdateState>.Instance, path);
            Assert.Equal("2.11.4", reloaded.Current.MinimumVersion);
            Assert.Equal("2.11.3", reloaded.Current.IosMinimumVersion);
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private async Task SetAsync(string minimumVersion, string? iosMinimumVersion = null)
    {
        var payload = iosMinimumVersion is not null
            ? JsonSerializer.Serialize(new { minimumVersion, iosMinimumVersion })
            : JsonSerializer.Serialize(new { minimumVersion });
        var response = await AdminAsync("POST", payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(minimumVersion, response.Json.GetProperty("minimumVersion").GetString());
        if (iosMinimumVersion is not null)
        {
            Assert.Equal(iosMinimumVersion, response.Json.GetProperty("iosMinimumVersion").GetString());
        }
    }

    private sealed record AdminResponse(HttpStatusCode StatusCode, JsonElement Json);

    // TestServer has no socket, so the remote address is set on the context directly (null unless a test sets it).
    private async Task<AdminResponse> AdminAsync(string method, string? body, IPAddress? remote = null,
        string? proxyHeader = null, string path = "/admin/client-update")
    {
        var context = await _factory.Server.SendAsync(http =>
        {
            http.Request.Method = method;
            http.Request.Path = path;
            http.Request.Host = new HostString("127.0.0.1", 8080);
            http.Connection.RemoteIpAddress = remote;
            if (proxyHeader is not null) http.Request.Headers[proxyHeader] = "198.51.100.1";
            if (body is not null) http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        });
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        var json = context.Response.StatusCode == 200 ? JsonDocument.Parse(text).RootElement.Clone() : default;
        return new AdminResponse((HttpStatusCode)context.Response.StatusCode, json);
    }

    private Task<AdminResponse> AdminAsync(string method, string? body) => AdminAsync(method, body, IPAddress.Loopback);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string? version,
        string? assetPlatform = null, string? platform = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}"))
        };
        request.Headers.Host = Host;
        if (version is not null) request.Headers.TryAddWithoutValidation("x-app-application-version", version);
        if (assetPlatform is not null) request.Headers.TryAddWithoutValidation("x-app-asset-platform", assetPlatform);
        if (platform is not null) request.Headers.TryAddWithoutValidation("x-app-platform", platform);
        return await client.SendAsync(request);
    }
}
