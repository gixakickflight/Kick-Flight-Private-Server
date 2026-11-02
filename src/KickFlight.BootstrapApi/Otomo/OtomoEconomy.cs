using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace KickFlight.BootstrapApi.Otomo;

// The adapter as an ordinary client of otomo's economy service.
//
// Every call here presents the player's own access token - the one OtomoIdentity caches per device - on economy's
// player plane. Nothing in this class holds a service key for a player route, and nothing takes a player id: the
// token is the identity, which is why a caller cannot ask for someone else's wallet.
//
// Two rules shape the transport:
//
//   * One retry, and only for a failure that says nothing about the request: a transport error or a 5xx. The
//     retry resends the same body, so the same idempotency key - economy answers a replay rather than charging
//     twice. A 4xx is an answer (no funds, unknown offer, expired window) and is never repeated.
//   * A 4xx is Refused, with economy's own code carried out to the caller; only an unreachable or 5xx service is
//     Unavailable. One player's refusal must never be dressed up as an outage, nor the reverse.
public sealed class OtomoEconomy : IOtomoEconomy, IDisposable
{
    // Economy's wire keys are exact JSON names on the models, so no naming policy is applied in either direction.
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string PlayerPrefix = "api/player/economy";

    private readonly HttpClient _http;
    private readonly OtomoEconomyOptions _options;
    private readonly ILogger<OtomoEconomy> _logger;
    private readonly Uri? _playerBase;
    private readonly Uri? _internalBase;
    private readonly bool _ownsHttpClient;

    // The HttpClient is passed in rather than built here so a test can hand in its own handler, which is how the
    // suite asserts the exact path, body and retry without a listener.
    public OtomoEconomy(HttpClient http, IOptions<OtomoEconomyOptions> options, ILogger<OtomoEconomy> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _ownsHttpClient = true;
        _http.Timeout = _options.Timeout;

        _playerBase = ParseBase(_options.BaseUrl);
        _internalBase = string.IsNullOrWhiteSpace(_options.InternalBaseUrl)
            ? _playerBase
            : ParseBase(_options.InternalBaseUrl);

        if (!Enabled)
        {
            _logger.LogWarning("Otomo:Economy is disabled: the shop, the gacha and every item counter will refuse to answer");
        }
        else if (_playerBase is null)
        {
            _logger.LogError("Otomo:Economy:Enabled is true but BaseUrl is not a URL; every request that needs a value will fail closed");
        }
        else
        {
            _logger.LogInformation(
                "otomo economy: enabled, player plane at {PlayerBase}, progress report {Progress}",
                _playerBase,
                ProgressEnabled ? $"to {_internalBase} with the game-server key" : "off");
        }
    }

    public bool Enabled => _options.Enabled;

    // A report needs both the switch and a readable key. Reading the key here rather than caching it is what lets
    // an operator rotate it without a restart; the read is a file of a few dozen bytes, once per battle.
    public bool ProgressEnabled => _options.Enabled && _options.ProgressEnabled && !string.IsNullOrEmpty(ReadServiceKey());

    // Built here, not at the composition root: this project keeps its outbound clients beside their only use, and
    // a test asserts the list of files allowed to hold one (HarnessTests.Production_code_has_no_upstream_client_api).
    public static OtomoEconomy Create(IOptions<OtomoEconomyOptions> options, ILogger<OtomoEconomy> logger) =>
        new(new HttpClient(new SocketsHttpHandler
        {
            // A client that lives as long as the process would otherwise pin one DNS answer forever.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }), options, logger);

    public Task<EconomyWallet> GetWalletAsync(string accessToken, CancellationToken cancellationToken) =>
        CallAsync<EconomyWallet>(Player("wallet"), HttpMethod.Get, null, accessToken, "wallet", cancellationToken);

    public Task<EconomyInventory> GetInventoryAsync(string accessToken, CancellationToken cancellationToken) =>
        CallAsync<EconomyInventory>(Player("inventory"), HttpMethod.Get, null, accessToken, "inventory", cancellationToken);

    public Task<EconomyShop> GetShopAsync(string accessToken, CancellationToken cancellationToken) =>
        CallAsync<EconomyShop>(Player("shop"), HttpMethod.Get, null, accessToken, "shop", cancellationToken);

    public Task<EconomyPools> GetPoolsAsync(string accessToken, CancellationToken cancellationToken) =>
        CallAsync<EconomyPools>(Player("gacha/pools"), HttpMethod.Get, null, accessToken, "gacha pools", cancellationToken);

    public Task<EconomyPurchase> PurchaseAsync(string accessToken, string offerId, long quantity,
        string idempotencyKey, CancellationToken cancellationToken) =>
        CallAsync<EconomyPurchase>(Player("shop/purchase"), HttpMethod.Post,
            new { offer_id = offerId, quantity, idempotency_key = idempotencyKey },
            accessToken, "purchase", cancellationToken);

    public Task<EconomyRoll> RollAsync(string accessToken, string poolId, long count, string idempotencyKey,
        CancellationToken cancellationToken) =>
        CallAsync<EconomyRoll>(Player("gacha/roll"), HttpMethod.Post,
            new { pool_id = poolId, count, idempotency_key = idempotencyKey },
            accessToken, "roll", cancellationToken);

    public Task<EconomyTasks> GetTasksAsync(string accessToken, CancellationToken cancellationToken) =>
        CallAsync<EconomyTasks>(Player("tasks"), HttpMethod.Get, null, accessToken, "tasks", cancellationToken);

    public Task<EconomyClaim> ClaimAsync(string accessToken, string trackId, string idempotencyKey,
        CancellationToken cancellationToken) =>
        CallAsync<EconomyClaim>(Player("tasks/claim"), HttpMethod.Post,
            new { track_id = trackId, idempotency_key = idempotencyKey },
            accessToken, "claim", cancellationToken);

    public async Task<EconomyProgress> ReportProgressAsync(EconomyProgressReport report,
        CancellationToken cancellationToken)
    {
        if (!Enabled) throw Disabled();
        var key = ReadServiceKey();
        if (!_options.ProgressEnabled || string.IsNullOrEmpty(key) || _internalBase is null)
        {
            throw new OtomoEconomyException(OtomoEconomyFailure.Disabled,
                "the battle progress report is off: set Otomo:Economy:ProgressEnabled and a gameserver service key");
        }

        var url = new Uri(_internalBase, "internal/economy/progress");
        // The body is a dictionary rather than an anonymous type because `ref` is a C# keyword: the field name on
        // the wire is exactly `ref`, and economy refuses an unknown field rather than ignoring it.
        var payload = new Dictionary<string, object?>
        {
            ["player_id"] = report.PlayerId,
            ["counter"] = report.Counter,
            ["delta"] = report.Delta,
            ["key"] = report.Key,
            ["reason"] = report.Reason,
            ["ref"] = report.Reference
        };

        var element = await SendAsync(url, HttpMethod.Post, payload, bearer: null, serviceKey: key,
            operation: "progress", cancellationToken).ConfigureAwait(false);
        return element.Deserialize<EconomyProgress>(JsonOptions)
               ?? throw new OtomoEconomyException(OtomoEconomyFailure.Unavailable,
                   "economy answered the progress report with an empty body");
    }

    private Uri Player(string relative) =>
        _playerBase is null
            ? throw new OtomoEconomyException(OtomoEconomyFailure.Unavailable,
                "Otomo:Economy:BaseUrl is not a URL, so the economy cannot be reached")
            : new Uri(_playerBase, $"{PlayerPrefix}/{relative}");

    private async Task<T> CallAsync<T>(Uri url, HttpMethod method, object? body, string accessToken,
        string operation, CancellationToken cancellationToken)
    {
        var element = await SendAsync(url, method, body, accessToken, serviceKey: null, operation, cancellationToken)
            .ConfigureAwait(false);
        var value = element.Deserialize<T>(JsonOptions);
        return value ?? throw new OtomoEconomyException(OtomoEconomyFailure.Unavailable,
            $"economy answered {operation} with an empty body");
    }

    // One request, at most twice. The retry rebuilds the request from the same values - body, and therefore the
    // caller's idempotency key - so a duplicate arrival is a replay on economy's side rather than a second charge.
    private async Task<JsonElement> SendAsync(Uri url, HttpMethod method, object? body, string? bearer,
        string? serviceKey, string operation, CancellationToken cancellationToken)
    {
        if (!Enabled) throw Disabled();

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(method, url);
                if (body is not null) request.Content = JsonContent.Create(body);
                if (!string.IsNullOrEmpty(bearer))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
                }
                else if (!string.IsNullOrEmpty(serviceKey))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + serviceKey);
                }
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                                              exception is HttpRequestException or TaskCanceledException
                                                  or OperationCanceledException)
            {
                if (attempt == 0)
                {
                    _logger.LogWarning("otomo economy: {Operation} to {Url} failed ({Error}); retrying once",
                        operation, url, exception.Message);
                    await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                throw new OtomoEconomyException(OtomoEconomyFailure.Unavailable,
                    $"otomo economy {operation} could not reach {url}: {exception.Message}", 0, null, exception);
            }

            using (response)
            {
                if ((int)response.StatusCode >= 500 && attempt == 0)
                {
                    _logger.LogWarning("otomo economy: {Operation} to {Url} answered {Status}; retrying once",
                        operation, url, (int)response.StatusCode);
                    await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var code = await ReadErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);
                    var failure = (int)response.StatusCode >= 500
                        ? OtomoEconomyFailure.Unavailable
                        : OtomoEconomyFailure.Refused;
                    var message =
                        $"otomo economy {operation} answered {(int)response.StatusCode}{(code is null ? "" : " " + code)}";
                    if (failure == OtomoEconomyFailure.Unavailable) _logger.LogError("{Message}", message);
                    else _logger.LogInformation("{Message}", message);
                    throw new OtomoEconomyException(failure, message, (int)response.StatusCode, code);
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
                    throw new OtomoEconomyException(OtomoEconomyFailure.Unavailable,
                        $"otomo economy {operation} answered a body that is not JSON: {exception.Message}",
                        (int)response.StatusCode, null, exception);
                }
            }
        }
    }

    private Task DelayBeforeRetryAsync(CancellationToken cancellationToken) =>
        _options.RetryDelay > TimeSpan.Zero ? Task.Delay(_options.RetryDelay, cancellationToken) : Task.CompletedTask;

    // The COM-5 envelope: {"error":{"code","message","request_id"}}. A body that is not one leaves the code null;
    // the status is still what the caller acts on.
    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
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
            _logger.LogError("otomo economy: cannot read the game-server service key at {KeyFile}: {Error}",
                _options.ServiceKeyFile, exception.Message);
            return "";
        }
    }

    private static OtomoEconomyException Disabled() =>
        new(OtomoEconomyFailure.Disabled,
            "Otomo:Economy:Enabled is false: this server holds no value and cannot answer what a player owns");

    private static Uri? ParseBase(string value) =>
        string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            ? null
            : uri;

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
