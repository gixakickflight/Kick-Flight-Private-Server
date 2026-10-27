using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace KickFlight.BootstrapApi.Tests;

// A stand-in for otomo's auth service: the same three routes, the same bodies and the same refusals as
// services/auth (internal/api/anonymous.go, refresh.go), running on loopback in this process and reached over real
// HTTP. Nothing is faked at the HttpClient level, so an adapter test exercises the adapter's own client end to end -
// device id admission, response parsing, the sub claim, refresh rotation and family revocation.
//
// The device id it admits is the one otomo admits: 43 unpadded base64url characters.
//
// The suite uses one shared instance (OtomoTestEnvironment.Server) and every host reads its address from the
// environment, so existing tests authenticate through otomo without knowing it exists. Counts are per device rather
// than global, because test classes run in parallel against that shared server.
public sealed class FakeOtomoServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, string> _accountByDevice = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RefreshOwner> _refreshOwner = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RefreshOwner> _spentRefreshTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _revokedFamilies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _loginsByDevice = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _refreshesByDevice = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _logoutsByDevice = new(StringComparer.Ordinal);
    private int _totalLogins;

    private FakeOtomoServer(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; private set; }

    // How long an access token this server issues stays valid. A test that wants a refresh moves its own clock past
    // this; nothing here has to sleep.
    public TimeSpan AccessLifetime { get; set; } = TimeSpan.FromMinutes(15);

    // Widens the window a login occupies, so a concurrency test can be sure the second request really does arrive
    // while the first is still in flight.
    public TimeSpan LoginDelay { get; set; } = TimeSpan.Zero;

    // The clock tokens are minted against. The system one by default; a test that moves its own clock past an
    // expiry hands its clock in here too, so the tokens otomo issues afterwards are valid on that same clock.
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    public int TotalLogins => Volatile.Read(ref _totalLogins);

    public int LoginsFor(string otomoDeviceId) => _loginsByDevice.GetValueOrDefault(otomoDeviceId);

    public int RefreshesFor(string otomoDeviceId) => _refreshesByDevice.GetValueOrDefault(otomoDeviceId);

    public int LogoutsFor(string otomoDeviceId) => _logoutsByDevice.GetValueOrDefault(otomoDeviceId);

    public static FakeOtomoServer Start()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // Port 0 lets the OS pick; BaseUrl is read back from the address Kestrel actually bound.
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        // One instance, which both holds the counts and owns the routes: a second object over the same app would
        // answer requests through the first one's state and report zero for everything.
        var server = new FakeOtomoServer(app, "");
        server.Map();
        app.StartAsync().GetAwaiter().GetResult();

        var address = app.Urls.FirstOrDefault(url => !url.EndsWith(":0", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Kestrel did not report a bound address (urls: {string.Join(", ", app.Urls)})");
        server.BaseUrl = address.TrimEnd('/');
        return server;
    }

    private void Map()
    {
        _app.MapPost("/auth/anonymous", async (HttpContext context) =>
        {
            var deviceId = await ReadStringAsync(context.Request, "device_id");
            if (deviceId is null || !IsAdmissibleDeviceId(deviceId))
            {
                return Error(context, StatusCodes.Status400BadRequest, "invalid_device_id",
                    "device_id must be 43 base64url characters, optionally prefixed v1_");
            }

            Interlocked.Increment(ref _totalLogins);
            _loginsByDevice.AddOrUpdate(deviceId, 1, (_, count) => count + 1);
            if (LoginDelay > TimeSpan.Zero) await Task.Delay(LoginDelay, context.RequestAborted);

            // The same device id always resolves to the same account: that is what a device login is for.
            var accountId = _accountByDevice.GetOrAdd(deviceId, _ => Guid.NewGuid().ToString());
            var familyId = Guid.NewGuid().ToString();
            var (access, refresh) = Mint(accountId, familyId);
            _refreshOwner[refresh] = new RefreshOwner(accountId, familyId, deviceId);
            return Tokens(access, refresh);
        });

        _app.MapPost("/auth/refresh", async (HttpContext context) =>
        {
            var token = await ReadStringAsync(context.Request, "refresh_token");
            if (token is null || !_refreshOwner.TryRemove(token, out var owner))
            {
                // A token that was already exchanged and has come back is theft: otomo revokes the family. That is
                // why the adapter serialises refreshes per device instead of letting two race.
                if (token is not null && _spentRefreshTokens.TryGetValue(token, out var spent))
                {
                    _revokedFamilies[spent.FamilyId] = 1;
                }
                return Error(context, StatusCodes.Status401Unauthorized, "invalid_token", "the refresh token is not usable");
            }

            _spentRefreshTokens[token] = owner;
            if (_revokedFamilies.ContainsKey(owner.FamilyId))
            {
                return Error(context, StatusCodes.Status401Unauthorized, "invalid_token", "the refresh token is not usable");
            }

            _refreshesByDevice.AddOrUpdate(owner.DeviceId, 1, (_, count) => count + 1);
            // The successor stays in the same family, so sid does not change.
            var (access, refresh) = Mint(owner.AccountId, owner.FamilyId);
            _refreshOwner[refresh] = owner;
            return Tokens(access, refresh);
        });

        _app.MapPost("/auth/logout", async (HttpContext context) =>
        {
            var token = await ReadStringAsync(context.Request, "refresh_token");
            if (token is not null && _refreshOwner.TryRemove(token, out var owner))
            {
                _revokedFamilies[owner.FamilyId] = 1;
                _logoutsByDevice.AddOrUpdate(owner.DeviceId, 1, (_, count) => count + 1);
            }
            // otomo answers 204 whatever it was handed, including a token it has already revoked.
            return Results.StatusCode(StatusCodes.Status204NoContent);
        });
    }

    private (string Access, string Refresh) Mint(string accountId, string familyId)
    {
        var now = Clock.GetUtcNow();
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"EdDSA","kid":"otomo-test"}"""));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = "otomo-auth-test",
            sub = accountId,
            sid = familyId,
            aud = "player",
            // A nonce, so two tokens minted inside the same second are still different strings: a reload of the same
            // session must be visible in the token, not only in the count of calls.
            jti = Guid.NewGuid().ToString(),
            iat = now.ToUnixTimeSeconds(),
            exp = now.Add(AccessLifetime).ToUnixTimeSeconds()
        }));
        var refresh = Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        return ($"{header}.{payload}.{Base64Url(Encoding.UTF8.GetBytes("signature"))}", refresh);
    }

    private static IResult Tokens(string access, string refresh) =>
        Results.Json(new { schema_version = 1, access_token = access, expires_in = 900, refresh_token = refresh });

    private static IResult Error(HttpContext context, int status, string code, string message) =>
        Results.Json(new { error = new { code, message, request_id = context.TraceIdentifier } }, statusCode: status);

    private static async Task<string?> ReadStringAsync(HttpRequest request, string property)
    {
        using var document = await JsonDocument.ParseAsync(request.Body);
        return document.RootElement.ValueKind == JsonValueKind.Object &&
               document.RootElement.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool IsAdmissibleDeviceId(string deviceId)
    {
        var body = deviceId.Length == 46 && deviceId.StartsWith("v1_", StringComparison.Ordinal) ? deviceId[3..] : deviceId;
        if (body.Length != 43) return false;
        foreach (var character in body)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')) return false;
        }
        return true;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed record RefreshOwner(string AccountId, string FamilyId, string DeviceId);
}

// The server every host in this suite talks to, and the environment that points them at it.
//
// A module initializer runs before any test. The hosts read configuration through WebApplication.CreateBuilder,
// which includes environment variables, so Otomo__* here reaches every factory the suite builds - including the ones
// that build their own instead of using ServerTestHostFixture.
public static class OtomoTestEnvironment
{
    private static readonly Lazy<FakeOtomoServer> LazyServer = new(FakeOtomoServer.Start);

    public static FakeOtomoServer Server => LazyServer.Value;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("Otomo__Enabled", "true");
        Environment.SetEnvironmentVariable("Otomo__BaseUrl", Server.BaseUrl);
        Environment.SetEnvironmentVariable("Otomo__TokenSecret", "kickflight-test-signing-secret");
    }
}
