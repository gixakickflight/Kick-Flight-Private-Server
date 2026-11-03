using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace KickFlight.BootstrapApi.Otomo;

// The adapter as an ordinary client of otomo's player rank service.
//
// Every call presents the player's own access token - the one OtomoIdentity caches per device - on otomo's player
// plane. Nothing in this class takes a player id, and nothing holds a service key: otomo's rank routes answer for the
// token's subject and no one else, which is why there is no route by which this client could read another player's
// points.
//
// Three rules shape the transport, the same three the economy and matchmaking clients follow:
//
//   * One retry, and only for a failure that says nothing about the request: a transport error or a 5xx. A 4xx is an
//     answer (`unknown_rule_type`, `invalid_token`) and is never repeated.
//   * A 4xx is Refused, with otomo's own code carried out to the caller; only an unreachable or 5xx service is
//     Unavailable. One player's refusal must never be dressed up as an outage, nor the reverse.
//   * The bodies are read as they are. A field this client does not know is ignored, but a body that is missing the
//     field the adapter needs (the ranks array) is not papered over: it fails as Unavailable rather than letting a
//     caller render a standing otomo did not send.
public sealed class OtomoRank : IOtomoRank, IDisposable
{
    // otomo's wire keys are exact JSON names on the models, so no naming policy is applied in either direction.
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string RankPath = "api/player/session/rank";

    private readonly HttpClient _http;
    private readonly OtomoRankOptions _options;
    private readonly ILogger<OtomoRank> _logger;
    private readonly Uri? _base;

    // The HttpClient is passed in rather than built here so a test can hand in its own handler; Create builds the one
    // a deployment uses.
    public OtomoRank(HttpClient http, IOptions<OtomoRankOptions> options, IOptions<OtomoOptions> identity,
        ILogger<OtomoRank> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        Validate(_options, logger);
        _http.Timeout = _options.Timeout;

        // BaseUrl is optional: the gateway identity already uses is where these routes live too, so enabling the
        // path needs one variable, not two that must agree.
        var configured = string.IsNullOrWhiteSpace(_options.BaseUrl) ? identity.Value.BaseUrl : _options.BaseUrl;
        _base = ParseBase(configured);

        if (!Enabled)
        {
            _logger.LogInformation("Otomo:Rank is disabled: rank is read from the adapter's own store");
        }
        else if (_base is null)
        {
            _logger.LogError("Otomo:Rank:Enabled is true but no base URL is configured (Otomo:Rank:BaseUrl or Otomo:BaseUrl); every read will serve the starting standing");
        }
        else
        {
            _logger.LogInformation("otomo rank: enabled, player rank routes at {Base}, timeout {Timeout}", _base,
                _options.Timeout);
        }
    }

    // A configuration that cannot work is corrected once, loudly, at construction - not silently per request. A zero
    // timeout makes HttpClient refuse every request and a negative retry delay makes Task.Delay throw, so both are
    // reset to the defaults they were supposed to have.
    private static void Validate(OtomoRankOptions options, ILogger logger)
    {
        if (options.Timeout <= TimeSpan.Zero)
        {
            logger.LogWarning("Otomo:Rank:Timeout must be positive; using {Seconds}s", 3);
            options.Timeout = TimeSpan.FromSeconds(3);
        }
        if (options.RetryDelay < TimeSpan.Zero) options.RetryDelay = TimeSpan.Zero;
    }

    public bool Enabled => _options.Enabled;

    public TimeSpan Timeout => _options.Timeout;

    // Built here, not at the composition root: this project keeps its outbound clients beside their only use, and a
    // test asserts the list of files allowed to hold one (HarnessTests.Production_code_has_no_upstream_client_api).
    public static OtomoRank Create(IOptions<OtomoRankOptions> options, IOptions<OtomoOptions> identity,
        ILogger<OtomoRank> logger) =>
        new(new HttpClient(new SocketsHttpHandler
        {
            // A client that lives as long as the process would otherwise pin one DNS answer forever.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }), options, identity, logger);

    // GET /api/player/session/rank. One entry per rule type the ladder names; a player with no awards reads zero
    // points in the lowest tier, which is an answer and not a miss.
    public async ValueTask<IReadOnlyList<OtomoRankEntry>> ReadAllAsync(string accessToken,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(Url(RankPath), accessToken, "rank read", cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess) throw Refusal(response, "rank read", "the player's rank could not be read");

        var list = response.Deserialize<OtomoRankList>();
        if (list?.Ranks is null)
        {
            throw new OtomoRankException(OtomoRankFailure.Unavailable,
                $"otomo answered the rank read {response.Status} without a ranks array");
        }

        _logger.LogDebug("otomo rank: {Count} rule types read", list.Ranks.Count);
        return list.Ranks;
    }

    // GET /api/player/session/rank/{rule_type}. A 404 unknown_rule_type means the published ladder does not rank that
    // battle: null, so the caller serves "no rank yet" rather than failing a payload over it.
    public async ValueTask<OtomoRankEntry?> ReadOneAsync(string ruleType, string accessToken,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(Url($"{RankPath}/{Uri.EscapeDataString(ruleType)}"), accessToken,
            "rank read", cancellationToken).ConfigureAwait(false);

        if (response.Status == (int)HttpStatusCode.NotFound) return null;
        if (!response.IsSuccess) throw Refusal(response, "rank read", "the player's rank could not be read");

        var entry = response.Deserialize<OtomoRankEntry>();
        if (entry is null || string.IsNullOrWhiteSpace(entry.RuleType))
        {
            throw new OtomoRankException(OtomoRankFailure.Unavailable,
                $"otomo answered the rank read {response.Status} without a rule_type");
        }

        return entry;
    }

    // --- transport -----------------------------------------------------------------------------------------------

    private readonly record struct OtomoResponse(HttpStatusCode StatusCode, JsonElement? Element, string? ErrorCode)
    {
        public int Status => (int)StatusCode;
        public bool IsSuccess => Status is >= 200 and < 300;

        public T? Deserialize<T>() where T : class =>
            Element is { ValueKind: JsonValueKind.Object } element
                ? element.Deserialize<T>(JsonOptions)
                : null;
    }

    // One request, at most twice. A GET is safe to repeat, which is the whole reason the retry is unconditional on
    // the failure class rather than tracked per call.
    private async Task<OtomoResponse> SendAsync(Uri url, string accessToken, string operation,
        CancellationToken cancellationToken)
    {
        if (!Enabled) throw Disabled();

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            string text;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(accessToken))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                }
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                                              exception is HttpRequestException or TaskCanceledException
                                                  or OperationCanceledException)
            {
                if (attempt == 0)
                {
                    _logger.LogWarning("otomo rank: {Operation} to {Url} failed ({Error}); retrying once",
                        operation, url, exception.Message);
                    await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                throw new OtomoRankException(OtomoRankFailure.Unavailable,
                    $"otomo rank {operation} could not reach {url}: {exception.Message}", 0, null, exception);
            }

            using (response)
            {
                if ((int)response.StatusCode >= 500 && attempt == 0)
                {
                    _logger.LogWarning("otomo rank: {Operation} to {Url} answered {Status}; retrying once",
                        operation, url, (int)response.StatusCode);
                    await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if ((int)response.StatusCode >= 500)
                {
                    var message = $"otomo rank {operation} answered {(int)response.StatusCode}";
                    _logger.LogError("{Message}", message);
                    throw new OtomoRankException(OtomoRankFailure.Unavailable, message, (int)response.StatusCode);
                }

                var element = ParseBody(text);
                if (response.IsSuccessStatusCode && element is null && !string.IsNullOrWhiteSpace(text))
                {
                    throw new OtomoRankException(OtomoRankFailure.Unavailable,
                        $"otomo rank {operation} answered a body that is not JSON", (int)response.StatusCode);
                }

                return new OtomoResponse(response.StatusCode, element, ReadErrorCode(element));
            }
        }
    }

    // A body this client cannot parse is only an error when the status said the call worked; a 4xx with a body that
    // is not JSON still has a status and therefore an answer. Empty (204, or a bare 200) is null.
    private static JsonElement? ParseBody(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The COM-5 envelope: {"error":{"code","message","request_id"}}. A body that is not one leaves the code null; the
    // status is still what the caller acts on.
    private static string? ReadErrorCode(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } root) return null;
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.String)
            {
                return code.GetString();
            }
            if (error.ValueKind == JsonValueKind.String) return error.GetString();
        }
        return root.TryGetProperty("code", out var topLevel) && topLevel.ValueKind == JsonValueKind.String
            ? topLevel.GetString()
            : null;
    }

    private OtomoRankException Refusal(OtomoResponse response, string operation, string what)
    {
        var code = response.ErrorCode;
        var message = $"otomo rank {operation} answered {response.Status}{(code is null ? "" : " " + code)}: {what}";
        // A refusal is about this player's credential or this deployment's mapping, not about the service's health.
        _logger.LogInformation("{Message}", message);
        return new OtomoRankException(OtomoRankFailure.Refused, message, response.Status, code);
    }

    private Task DelayBeforeRetryAsync(CancellationToken cancellationToken) =>
        _options.RetryDelay > TimeSpan.Zero ? Task.Delay(_options.RetryDelay, cancellationToken) : Task.CompletedTask;

    private static OtomoRankException Disabled() =>
        new(OtomoRankFailure.Disabled,
            "Otomo:Rank:Enabled is false: rank is the adapter's own store, not otomo's");

    private Uri Url(string relative) =>
        _base is null
            ? throw new OtomoRankException(OtomoRankFailure.Unavailable,
                "no otomo base URL is configured, so the rank service cannot be reached")
            : new Uri(_base, relative);

    private static Uri? ParseBase(string value) =>
        string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            ? null
            : uri;

    public void Dispose() => _http.Dispose();
}
