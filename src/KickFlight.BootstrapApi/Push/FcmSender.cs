using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace KickFlight.BootstrapApi.Push;

public enum FcmSendOutcome { Sent, InvalidToken, Failed }

public interface IFcmSender
{
    bool IsConfigured { get; }
    Task<FcmSendOutcome> SendAsync(string token, CancellationToken cancellationToken);
}

// FCM HTTP v1 sender using a service-account JWT (RS256) -> OAuth2 access token. BCL only. The credentials file
// is read from PushOptions.CredentialsPath and kept in memory; nothing from it is ever logged.
public sealed class FcmSender : IFcmSender
{
    public const string HttpClientName = "fcm";
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private sealed record Credentials(string ProjectId, string ClientEmail, string PrivateKeyPem, string TokenUri);

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<PushOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<FcmSender> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private Credentials? _credentials;
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiry;

    public FcmSender(IHttpClientFactory httpFactory, IOptions<PushOptions> options, TimeProvider time, ILogger<FcmSender> logger)
    {
        _httpFactory = httpFactory;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.Value.CredentialsPath) && File.Exists(_options.Value.CredentialsPath);

    public async Task<FcmSendOutcome> SendAsync(string token, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var credentials = LoadCredentials();
        var client = _httpFactory.CreateClient(HttpClientName);
        var payload = JsonSerializer.Serialize(new
        {
            message = new
            {
                token,
                notification = new { title = options.Title, body = options.Body },
                android = new
                {
                    priority = "HIGH",
                    ttl = $"{options.TtlSeconds}s",
                    collapse_key = "ranked_search",
                    notification = new { tag = "ranked_search" }
                }
            }
        });
        var url = $"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(credentials.ProjectId)}/messages:send";

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var access = await GetAccessTokenAsync(credentials, client, forceRefresh: attempt > 0, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) return FcmSendOutcome.Sent;
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var (status, errorCode, message) = ParseError(body);
            _logger.LogWarning("FCM send to {Token} failed: HTTP {Status} {ErrorStatus} {ErrorCode}",
                PushTokenService.Mask(token), (int)response.StatusCode, status, errorCode);
            if (IsDeadToken(response.StatusCode, status, errorCode, message)) return FcmSendOutcome.InvalidToken;
            return FcmSendOutcome.Failed;
        }
        return FcmSendOutcome.Failed;
    }

    internal static bool IsDeadToken(HttpStatusCode code, string? status, string? errorCode, string? message)
    {
        if (code == HttpStatusCode.NotFound) return errorCode == "UNREGISTERED" || status is "NOT_FOUND" or "UNREGISTERED";
        if (code == HttpStatusCode.BadRequest && (status == "INVALID_ARGUMENT" || errorCode == "INVALID_ARGUMENT"))
            return message is not null && (message.Contains("registration token", StringComparison.OrdinalIgnoreCase)
                || message.Contains("message.token", StringComparison.OrdinalIgnoreCase));
        return false;
    }

    private static (string? Status, string? ErrorCode, string? Message) ParseError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var error)) return (null, null, null);
            var status = error.TryGetProperty("status", out var s) ? s.GetString() : null;
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
            string? errorCode = null;
            if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                foreach (var detail in details.EnumerateArray())
                {
                    if (detail.TryGetProperty("errorCode", out var c)) errorCode = c.GetString();
                    // BadRequest field violations name the offending field ("message.token").
                    if (detail.TryGetProperty("fieldViolations", out var fv) && fv.ValueKind == JsonValueKind.Array)
                        foreach (var v in fv.EnumerateArray())
                            if (v.TryGetProperty("field", out var f)) message = (message ?? "") + " " + f.GetString();
                }
            }
            return (status, errorCode, message);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private Credentials LoadCredentials()
    {
        if (_credentials is not null) return _credentials;
        using var doc = JsonDocument.Parse(File.ReadAllText(_options.Value.CredentialsPath));
        var root = doc.RootElement;
        string Required(string name) => root.TryGetProperty(name, out var p) && p.GetString() is { Length: > 0 } v
            ? v : throw new InvalidOperationException($"Push credentials file lacks '{name}'");
        _credentials = new Credentials(Required("project_id"), Required("client_email"), Required("private_key"),
            root.TryGetProperty("token_uri", out var t) && t.GetString() is { Length: > 0 } u
                ? u : "https://oauth2.googleapis.com/token");
        return _credentials;
    }

    private async Task<string> GetAccessTokenAsync(Credentials credentials, HttpClient client, bool forceRefresh, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();
            if (!forceRefresh && _accessToken is not null && now < _accessTokenExpiry - RefreshMargin) return _accessToken;

            var assertion = BuildAssertion(credentials, now);
            using var request = new HttpRequestMessage(HttpMethod.Post, credentials.TokenUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = assertion
                })
            };
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"OAuth token request failed with HTTP {(int)response.StatusCode}");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            _accessToken = doc.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("OAuth response has no access_token");
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
            _accessTokenExpiry = now.AddSeconds(expiresIn);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static string BuildAssertion(Credentials credentials, DateTimeOffset now)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = credentials.ClientEmail,
            ["scope"] = Scope,
            ["aud"] = credentials.TokenUri,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddHours(1).ToUnixTimeSeconds()
        }));
        var signingInput = header + "." + claims;
        using var rsa = RSA.Create();
        rsa.ImportFromPem(credentials.PrivateKeyPem);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public static class PushServiceCollectionExtensions
{
    // Registers options, the FCM HTTP client (IHttpClientFactory), the sender, token service and notifier.
    public static IServiceCollection AddRankedPush(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PushOptions>(configuration.GetSection(PushOptions.SectionName));
        services.AddHttpClient(FcmSender.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<IFcmSender, FcmSender>();
        services.AddSingleton<PushTokenService>();
        services.AddSingleton<RankedSearchNotifier>();
        return services;
    }
}
