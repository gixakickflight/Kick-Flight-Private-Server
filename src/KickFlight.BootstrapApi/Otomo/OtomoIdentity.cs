using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace KickFlight.BootstrapApi.Otomo;

// The adapter's whole relationship with otomo's auth service.
//
// One device logs in once (POST /auth/anonymous), the access+refresh pair lives in this process keyed by otomo
// device id, an access token near its expiry is rotated with POST /auth/refresh, and a logout revokes that
// device's family with POST /auth/logout. Nothing is persisted and nothing is created here: identity is otomo's,
// and a session otomo does not know is a session this adapter must not remember either.
//
// Each device's calls pass through that device's cache entry, which is what makes "one login per device" hold
// under concurrency: two simultaneous requests from a cold device produce one login and the second caller waits
// for it. The same gate is why two refreshes cannot race - otomo rotates refresh tokens and treats the second
// use of an exchanged token as theft, revoking the whole family.
public sealed class OtomoIdentity : IOtomoIdentity, IDisposable
{
    private readonly HttpClient _http;
    private readonly OtomoOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<OtomoIdentity> _logger;
    private readonly ConcurrentDictionary<string, DeviceEntry> _sessions = new(StringComparer.Ordinal);
    private readonly bool _ownsHttpClient;

    private long _nextSweepUtcTicks;

    // The HttpClient is passed in rather than built here so a test can hand in its own handler, which is how the
    // suite runs against an in-memory otomo without a listener.
    public OtomoIdentity(HttpClient http, IOptions<OtomoOptions> options, TimeProvider time, ILogger<OtomoIdentity> logger)
    {
        _options = options.Value;
        _time = time;
        _logger = logger;
        _http = http;
        _ownsHttpClient = true;

        if (!string.IsNullOrWhiteSpace(_options.BaseUrl) &&
            Uri.TryCreate(_options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseAddress))
        {
            _http.BaseAddress = baseAddress;
        }
        _http.Timeout = _options.Timeout;

        if (_options.Enabled && _http.BaseAddress is null)
        {
            _logger.LogError("Otomo:Enabled is true but Otomo:BaseUrl is not a URL; every player request will fail closed");
        }
        else if (_options.Enabled)
        {
            _logger.LogInformation("otomo identity: enabled, auth service at {BaseUrl}, cache {Capacity} devices / {Idle} idle",
                _http.BaseAddress, _options.CacheCapacity, _options.CacheIdle);
        }
        else
        {
            _logger.LogWarning("Otomo:Enabled is false: no player can sign in and every player request fails closed");
        }
    }

    public bool Enabled => _options.Enabled;

    // Built here, not at the composition root: this is the only file in the project allowed to hold an outbound HTTP
    // client (HarnessTests.Production_code_has_no_upstream_client_api), and keeping the construction beside its use
    // is what keeps the rest of the server free of one.
    public static OtomoIdentity Create(IOptions<OtomoOptions> options, TimeProvider time, ILogger<OtomoIdentity> logger) =>
        new(new HttpClient(new SocketsHttpHandler
        {
            // A client that lives as long as the process would otherwise pin one DNS answer forever.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }), options, time, logger);

    public int CachedDeviceCount => _sessions.Count;

    public async ValueTask<OtomoSession> GetSessionAsync(string deviceUuid, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(deviceUuid)) deviceUuid = "default-user";
        var deviceId = OtomoDeviceId.FromDeviceUuid(deviceUuid);
        var entry = _sessions.GetOrAdd(deviceId, _ => new DeviceEntry());

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (entry.Session is { } cached && now + _options.RefreshSkew < cached.ExpiresAt)
            {
                entry.LastUsed = now;
                return cached;
            }

            var session = entry.Session is { } expiring
                ? await RefreshOrLoginAsync(deviceId, expiring, now, cancellationToken).ConfigureAwait(false)
                : await LoginAsync(deviceId, now, cancellationToken).ConfigureAwait(false);

            entry.Session = session;
            entry.LastUsed = now;
            return session;
        }
        finally
        {
            entry.Gate.Release();
            SweepIfDue(_time.GetUtcNow());
        }
    }

    public async ValueTask LogoutAsync(string deviceUuid, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(deviceUuid)) deviceUuid = "default-user";
        var deviceId = OtomoDeviceId.FromDeviceUuid(deviceUuid);
        if (!_sessions.TryGetValue(deviceId, out var entry)) return;

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _sessions.TryRemove(new KeyValuePair<string, DeviceEntry>(deviceId, entry));
            if (entry.Session is not { } session) return;

            using var response = await PostAsync("auth/logout",
                new RefreshBody(session.RefreshToken), "logout", deviceId, cancellationToken).ConfigureAwait(false);
            // otomo answers 204 for every body it can read, including one it has already revoked, so only a real
            // failure is worth reporting - and even then this device is signed out of this process already.
            if ((int)response.StatusCode >= 500)
            {
                _logger.LogError("otomo logout for device {DeviceId} answered {Status}; the refresh family may still be live",
                    OtomoDeviceId.Mask(deviceId), (int)response.StatusCode);
            }
            else
            {
                _logger.LogInformation("Revoked the otomo refresh family of device {DeviceId}", OtomoDeviceId.Mask(deviceId));
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    // An expired access token is refreshed inside its family, which is what keeps the device logged in without a
    // second account. A family otomo has revoked (logout elsewhere, or a replayed refresh) cannot be refreshed;
    // the device logs in again with the same device id and gets the same account in a new family, which is exactly
    // what otomo's device provider is for. Any other failure is reported as it is: no retry, no silent re-login.
    private async Task<OtomoSession> RefreshOrLoginAsync(string deviceId, OtomoSession expired, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            return await RefreshAsync(deviceId, expired, now, cancellationToken).ConfigureAwait(false);
        }
        catch (OtomoIdentityException exception) when (exception.Failure == OtomoIdentityFailure.Rejected)
        {
            _logger.LogInformation(
                "otomo refused the refresh token of device {DeviceId} ({Reason}); signing in again with the device id",
                OtomoDeviceId.Mask(deviceId), exception.Message);
            return await LoginAsync(deviceId, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<OtomoSession> LoginAsync(string deviceId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var response = await PostAsync("auth/anonymous",
            new DeviceBody(deviceId), "login", deviceId, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await RefusalAsync(response, "login", deviceId, cancellationToken).ConfigureAwait(false);

        var session = await ReadSessionAsync(response, now, deviceId, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("otomo signed in device {DeviceId} as player {PlayerId}", OtomoDeviceId.Mask(deviceId), session.PlayerId);
        return session;
    }

    private async Task<OtomoSession> RefreshAsync(string deviceId, OtomoSession expired, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var response = await PostAsync("auth/refresh",
            new RefreshBody(expired.RefreshToken), "refresh", deviceId, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await RefusalAsync(response, "refresh", deviceId, cancellationToken).ConfigureAwait(false);
        return await ReadSessionAsync(response, now, deviceId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object body, string operation, string deviceId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        try
        {
            return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            throw new OtomoIdentityException(OtomoIdentityFailure.Unavailable,
                $"otomo {operation} for device {OtomoDeviceId.Mask(deviceId)} could not reach {_http.BaseAddress}: {exception.Message}",
                exception);
        }
    }

    // A refusal is a 401/403/400: that client's credential is the problem. Anything else (5xx, 429) is this
    // adapter's or otomo's problem and is reported as unavailable, so one client's bad device id is never dressed
    // up as an outage and an outage is never dressed up as a bad credential.
    private async Task<OtomoIdentityException> RefusalAsync(HttpResponseMessage response, string operation, string deviceId, CancellationToken cancellationToken)
    {
        var code = await ReadErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);
        var rejected = response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
        var failure = rejected ? OtomoIdentityFailure.Rejected : OtomoIdentityFailure.Unavailable;
        var message = $"otomo {operation} for device {OtomoDeviceId.Mask(deviceId)} answered {(int)response.StatusCode}{(code is null ? "" : " " + code)}";
        if (rejected) _logger.LogWarning("{Message}", message);
        else _logger.LogError("{Message}", message);
        return new OtomoIdentityException(failure, message);
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorBody>(cancellationToken).ConfigureAwait(false);
            return body?.Error?.Code;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or HttpRequestException)
        {
            return null;
        }
    }

    // otomo's success body is the same for a login and a refresh: {schema_version, access_token, expires_in,
    // refresh_token}. The account id is not in it, so it is read from the token itself - this process is the
    // token's recipient, not its verifier.
    private static async Task<OtomoSession> ReadSessionAsync(HttpResponseMessage response, DateTimeOffset now, string deviceId, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadFromJsonAsync<LoginBody>(cancellationToken).ConfigureAwait(false);
        if (body is null || string.IsNullOrEmpty(body.AccessToken) || string.IsNullOrEmpty(body.RefreshToken))
            throw new OtomoIdentityException(OtomoIdentityFailure.Unavailable,
                $"otomo answered device {OtomoDeviceId.Mask(deviceId)} without a token pair");

        if (!TryReadClaims(body.AccessToken, out var claims) ||
            !claims.TryGetProperty("sub", out var subject) ||
            string.IsNullOrEmpty(subject.GetString()))
        {
            throw new OtomoIdentityException(OtomoIdentityFailure.Unavailable,
                $"otomo's access token for device {OtomoDeviceId.Mask(deviceId)} carries no sub claim");
        }

        var expiresAt = claims.TryGetProperty("exp", out var expiry) && expiry.TryGetInt64(out var unixSeconds)
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
            : now.AddSeconds(body.ExpiresIn);
        return new OtomoSession(subject.GetString()!, body.AccessToken, expiresAt, body.RefreshToken);
    }

    private static bool TryReadClaims(string accessToken, out JsonElement claims)
    {
        claims = default;
        var parts = accessToken.Split('.');
        if (parts.Length < 2) return false;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            claims = document.RootElement.Clone();
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return false;
        }
    }

    // Idle first, then least recently used, so the cache stays bounded even when every device is active.
    private void SweepIfDue(DateTimeOffset now)
    {
        var scheduled = Interlocked.Read(ref _nextSweepUtcTicks);
        if (now.UtcTicks < scheduled) return;
        if (Interlocked.CompareExchange(ref _nextSweepUtcTicks, now.AddMinutes(1).UtcTicks, scheduled) != scheduled) return;

        var capacity = Math.Max(1, _options.CacheCapacity);
        var idle = _options.CacheIdle;
        foreach (var pair in _sessions)
        {
            if (now - pair.Value.LastUsed > idle) _sessions.TryRemove(pair);
        }
        if (_sessions.Count <= capacity) return;

        foreach (var pair in _sessions.OrderBy(entry => entry.Value.LastUsed).Take(_sessions.Count - capacity))
        {
            _sessions.TryRemove(pair);
        }
    }

    private void EnsureEnabled()
    {
        if (!_options.Enabled)
        {
            throw new OtomoIdentityException(OtomoIdentityFailure.Disabled,
                "Otomo:Enabled is false: this server has no identity provider and cannot sign anyone in");
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }

    // One device's cache entry. The gate serialises that device's login and refresh; LastUsed drives eviction.
    private sealed class DeviceEntry
    {
        private long _lastUsedUtcTicks;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public OtomoSession? Session { get; set; }

        public DateTimeOffset LastUsed
        {
            get => new(Interlocked.Read(ref _lastUsedUtcTicks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref _lastUsedUtcTicks, value.UtcTicks);
        }
    }

    private sealed record DeviceBody([property: JsonPropertyName("device_id")] string DeviceId);

    private sealed record RefreshBody([property: JsonPropertyName("refresh_token")] string RefreshToken);

    private sealed record LoginBody(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] long ExpiresIn,
        [property: JsonPropertyName("refresh_token")] string RefreshToken);

    private sealed record ErrorBody([property: JsonPropertyName("error")] ErrorDetail? Error);

    private sealed record ErrorDetail(
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("message")] string? Message);
}
