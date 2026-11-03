using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace KickFlight.BootstrapApi.Otomo;

// The adapter as a client of otomo's session rank: a player's standing, and the award a battle result is.
//
// Two planes, two credentials, and no route takes both:
//
//   * Player plane (GET /api/player/session/rank): the player's own access token. The subject is the token's, so
//     there is no argument a caller could change to read for someone else.
//   * Internal plane (POST /internal/session/rank/awards, GET /internal/session/rank/players/{id}): the game-server
//     service key (session_matchmaker) as a bearer, sent the way OtomoEconomy sends its internal key. These name a
//     player in the path or body, so they are a trust decision, and they are off without a key.
//
// Same transport rules as OtomoSocial and OtomoEconomy: one retry for a transport failure or a 5xx, none for a 4xx.
// The award is safe to retry because session applies it once per (player, rule type, key).
public sealed class OtomoRank : IOtomoRank, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly OtomoRankOptions _options;
    private readonly ILogger<OtomoRank> _logger;
    private readonly Uri? _playerBase;
    private readonly Uri? _internalBase;

    public OtomoRank(HttpClient http, IOptions<OtomoRankOptions> options, IOptions<OtomoSocialOptions> social,
        ILogger<OtomoRank> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _http.Timeout = _options.Timeout;

        _playerBase = ParseBase(string.IsNullOrWhiteSpace(_options.BaseUrl) ? social.Value.BaseUrl : _options.BaseUrl);
        _internalBase = string.IsNullOrWhiteSpace(_options.InternalBaseUrl)
            ? _playerBase
            : ParseBase(_options.InternalBaseUrl);

        if (!Enabled)
        {
            _logger.LogWarning("Otomo:Rank is disabled: battle results, profile cards and the ranking screen will refuse to answer");
        }
        else if (_playerBase is null || _internalBase is null)
        {
            _logger.LogError("Otomo:Rank:Enabled is true but no session base URL is configured; every rank request will fail closed");
        }
        else
        {
            _logger.LogInformation("otomo session rank: enabled at {PlayerBase}, internal plane at {InternalBase} ({Key})",
                _playerBase, _internalBase, string.IsNullOrEmpty(ReadServiceKey()) ? "NO service key" : "service key set");
        }
    }

    public bool Enabled => _options.Enabled;

    // Built here, beside its only use, because a test asserts the list of files allowed to hold an outbound client
    // (HarnessTests.Production_code_has_no_upstream_client_api).
    public static OtomoRank Create(IOptions<OtomoRankOptions> options, IOptions<OtomoSocialOptions> social,
        ILogger<OtomoRank> logger) =>
        new(new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }),
            options, social, logger);

    public async Task<IReadOnlyList<SessionRankEntry>> GetOwnAsync(string accessToken,
        CancellationToken cancellationToken)
    {
        var url = Under(_playerBase, "api/player/session/rank");
        var ranks = await CallAsync<SessionOwnRanks>(url, HttpMethod.Get, null, accessToken, null, "own rank",
            cancellationToken).ConfigureAwait(false);
        return ranks.Ranks ?? [];
    }

    public async Task<IReadOnlyList<SessionRankEntry>> GetPlayerAsync(string otomoPlayerId,
        CancellationToken cancellationToken)
    {
        var key = RequireServiceKey("another player's rank");
        var url = Under(_internalBase, $"internal/session/rank/players/{Uri.EscapeDataString(otomoPlayerId)}");
        var ranks = await CallAsync<SessionPlayerRanks>(url, HttpMethod.Get, null, null, key, "player rank",
            cancellationToken).ConfigureAwait(false);
        return ranks.Ranks ?? [];
    }

    public async Task<SessionRankAward> AwardAsync(string otomoPlayerId, string ruleType, int delta, string key,
        CancellationToken cancellationToken)
    {
        var serviceKey = RequireServiceKey("a rank award");
        var url = Under(_internalBase, "internal/session/rank/awards");
        return await CallAsync<SessionRankAward>(url, HttpMethod.Post,
            new { player_id = otomoPlayerId, rule_type = ruleType, delta, key }, null, serviceKey, "rank award",
            cancellationToken).ConfigureAwait(false);
    }

    private Uri Under(Uri? baseUri, string relative) =>
        baseUri is null
            ? throw new OtomoRankException(OtomoRankFailure.Unavailable,
                "no session base URL is configured (Otomo:Rank:BaseUrl / InternalBaseUrl, or Otomo:Social:BaseUrl)")
            : new Uri(baseUri, relative);

    private string RequireServiceKey(string what)
    {
        var key = ReadServiceKey();
        if (!string.IsNullOrEmpty(key)) return key;
        throw new OtomoRankException(OtomoRankFailure.Disabled,
            $"{what} needs the internal plane: set Otomo:Rank:ServiceKeyFile (or ServiceKey)");
    }

    private async Task<T> CallAsync<T>(Uri url, HttpMethod method, object? body, string? accessToken,
        string? serviceKey, string operation, CancellationToken cancellationToken)
    {
        var element = await SendAsync(url, method, body, accessToken, serviceKey, operation, cancellationToken)
            .ConfigureAwait(false);
        var value = element.Deserialize<T>(JsonOptions);
        return value ?? throw new OtomoRankException(OtomoRankFailure.Unavailable,
            $"session answered {operation} with an empty body");
    }

    private async Task<JsonElement> SendAsync(Uri url, HttpMethod method, object? body, string? accessToken,
        string? serviceKey, string operation, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            throw new OtomoRankException(OtomoRankFailure.Disabled,
                "Otomo:Rank:Enabled is false: this server holds no battle points and cannot say what a player's standing is");
        }

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(method, url);
                if (body is not null) request.Content = JsonContent.Create(body);
                var bearer = !string.IsNullOrEmpty(serviceKey) ? serviceKey : accessToken;
                if (!string.IsNullOrEmpty(bearer)) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                                              exception is HttpRequestException or TaskCanceledException
                                                  or OperationCanceledException)
            {
                if (attempt == 0)
                {
                    _logger.LogWarning("otomo session rank: {Operation} to {Url} failed ({Error}); retrying once",
                        operation, url, exception.Message);
                    await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                throw new OtomoRankException(OtomoRankFailure.Unavailable,
                    $"otomo session rank {operation} could not reach {url}: {exception.Message}", 0, null, exception);
            }

            using (response)
            {
                if ((int)response.StatusCode >= 500 && attempt == 0)
                {
                    _logger.LogWarning("otomo session rank: {Operation} to {Url} answered {Status}; retrying once",
                        operation, url, (int)response.StatusCode);
                    await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var code = await ReadErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);
                    var failure = (int)response.StatusCode >= 500 ? OtomoRankFailure.Unavailable : OtomoRankFailure.Refused;
                    var message = $"otomo session rank {operation} answered {(int)response.StatusCode}{(code is null ? "" : " " + code)}";
                    if (failure == OtomoRankFailure.Unavailable) _logger.LogError("{Message}", message);
                    else _logger.LogWarning("{Message}", message);
                    throw new OtomoRankException(failure, message, (int)response.StatusCode, code);
                }

                try
                {
                    using var document = await JsonDocument.ParseAsync(
                        await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    return document.RootElement.Clone();
                }
                catch (JsonException exception)
                {
                    throw new OtomoRankException(OtomoRankFailure.Unavailable,
                        $"otomo session rank {operation} answered a body that is not JSON: {exception.Message}",
                        (int)response.StatusCode, null, exception);
                }
            }
        }
    }

    private Task DelayBeforeRetryAsync(CancellationToken cancellationToken) =>
        _options.RetryDelay > TimeSpan.Zero ? Task.Delay(_options.RetryDelay, cancellationToken) : Task.CompletedTask;

    // The COM-5 envelope: {"error":{"code","message","request_id"}}.
    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.Object &&
                   error.TryGetProperty("code", out var code) &&
                   code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException
                                              or NotSupportedException or TaskCanceledException)
        {
            return null;
        }
    }

    // Read on every internal call, so rotating the key needs no restart (the same rule as OtomoEconomy).
    private string ReadServiceKey()
    {
        if (!string.IsNullOrWhiteSpace(_options.ServiceKey)) return _options.ServiceKey.Trim();
        if (string.IsNullOrWhiteSpace(_options.ServiceKeyFile)) return "";
        try
        {
            return File.ReadAllText(_options.ServiceKeyFile).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("otomo session rank: cannot read the service key at {KeyFile}: {Error}",
                _options.ServiceKeyFile, exception.Message);
            return "";
        }
    }

    private static Uri? ParseBase(string? baseUrl) =>
        string.IsNullOrWhiteSpace(baseUrl)
            ? null
            : Uri.TryCreate(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ? uri : null;

    public void Dispose() => _http.Dispose();
}
