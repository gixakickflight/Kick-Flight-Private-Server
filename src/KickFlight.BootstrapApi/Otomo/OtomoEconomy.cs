using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    // POST /internal/economy/facts. The same plane, key and switch as the progress report. The body names a fact and
    // its fields and never a track, an amount or a reward.
    public async Task<EconomyFact> ReportFactAsync(EconomyFactReport report, CancellationToken cancellationToken)
    {
        if (!Enabled) throw Disabled();
        var key = ReadServiceKey();
        if (!_options.ProgressEnabled || string.IsNullOrEmpty(key) || _internalBase is null)
        {
            throw new OtomoEconomyException(OtomoEconomyFailure.Disabled,
                "the fact report is off: set Otomo:Economy:ProgressEnabled and a gameserver service key");
        }

        var payload = new Dictionary<string, object?>
        {
            ["player_id"] = report.PlayerId,
            ["fact"] = report.Fact,
            ["fields"] = report.Fields,
            ["key"] = report.Key,
            ["reason"] = report.Reason,
            ["ref"] = report.Reference
        };
        var element = await SendAsync(new Uri(_internalBase, "internal/economy/facts"), HttpMethod.Post, payload,
            bearer: null, serviceKey: key, operation: "fact", cancellationToken).ConfigureAwait(false);
        return element.Deserialize<EconomyFact>(JsonOptions)
               ?? throw new OtomoEconomyException(OtomoEconomyFailure.Unavailable,
                   "economy answered the fact report with an empty body");
    }

    // POST /internal/economy/progress/gauge. Same plane and key. A gauge is set, so the body carries `value` and
    // never `delta`: economy refuses the wrong route's field rather than ignoring it.
    public async Task<EconomyProgress> ReportGaugeAsync(EconomyGaugeReport report, CancellationToken cancellationToken)
    {
        if (!Enabled) throw Disabled();
        var key = ReadServiceKey();
        if (!_options.ProgressEnabled || string.IsNullOrEmpty(key) || _internalBase is null)
        {
            throw new OtomoEconomyException(OtomoEconomyFailure.Disabled,
                "the gauge report is off: set Otomo:Economy:ProgressEnabled and a gameserver service key");
        }

        var payload = new Dictionary<string, object?>
        {
            ["player_id"] = report.PlayerId,
            ["counter"] = report.Counter,
            ["value"] = report.Value,
            ["key"] = report.Key,
            ["reason"] = report.Reason,
            ["ref"] = report.Reference
        };
        var element = await SendAsync(new Uri(_internalBase, "internal/economy/progress/gauge"), HttpMethod.Post,
            payload, bearer: null, serviceKey: key, operation: "gauge", cancellationToken).ConfigureAwait(false);
        return element.Deserialize<EconomyProgress>(JsonOptions)
               ?? throw new OtomoEconomyException(OtomoEconomyFailure.Unavailable,
                   "economy answered the gauge report with an empty body");
    }

    // POST /internal/economy/grants. The same plane, key and switch as the progress report: one trust decision,
    // stated once. The body names a class and never an amount; economy refuses an `amount` field outright.
    public async Task<EconomyGrantResult> GrantAsync(EconomyGrantRequest grant, CancellationToken cancellationToken)
    {
        if (!Enabled) throw Disabled();
        var key = ReadServiceKey();
        if (!_options.ProgressEnabled || string.IsNullOrEmpty(key) || _internalBase is null)
        {
            throw new OtomoEconomyException(OtomoEconomyFailure.Disabled,
                "internal grants are off: set Otomo:Economy:ProgressEnabled and a gameserver service key");
        }

        var url = new Uri(_internalBase, "internal/economy/grants");
        var payload = new Dictionary<string, object?>
        {
            ["player_id"] = grant.PlayerId,
            ["key"] = grant.Key,
            ["reason"] = grant.Reason,
            ["ref"] = grant.Reference,
            ["class"] = grant.Class
        };

        var element = await SendAsync(url, HttpMethod.Post, payload, bearer: null, serviceKey: key,
            operation: "grant", cancellationToken).ConfigureAwait(false);
        return element.Deserialize<EconomyGrantResult>(JsonOptions)
               ?? throw new OtomoEconomyException(OtomoEconomyFailure.Unavailable,
                   "economy answered the grant with an empty body");
    }

    // POST /internal/economy/consume. The same plane, key and switch again: a sink is no less a trust decision than
    // a faucet. The retry in SendAsync resends the same key, which economy answers as a replay rather than a second
    // spend - the property the level-up depends on.
    public async Task<EconomyConsumeResult> ConsumeAsync(EconomyConsumeRequest consume,
        CancellationToken cancellationToken)
    {
        if (!Enabled) throw Disabled();
        var key = ReadServiceKey();
        if (!_options.ProgressEnabled || string.IsNullOrEmpty(key) || _internalBase is null)
        {
            throw new OtomoEconomyException(OtomoEconomyFailure.Disabled,
                "internal consumes are off: set Otomo:Economy:ProgressEnabled and a gameserver service key");
        }

        var url = new Uri(_internalBase, "internal/economy/consume");
        var payload = new Dictionary<string, object?>
        {
            ["player_id"] = consume.PlayerId,
            ["key"] = consume.Key,
            ["reason"] = consume.Reason,
            ["ref"] = consume.Reference,
            ["items"] = consume.Items.Select(item => new { item = item.Item, qty = item.Qty }).ToArray(),
            ["currencies"] = consume.Currencies
                .Select(cost => new { currency = cost.Currency, amount = cost.Amount }).ToArray()
        };

        var element = await SendAsync(url, HttpMethod.Post, payload, bearer: null, serviceKey: key,
            operation: "consume", cancellationToken).ConfigureAwait(false);
        return element.Deserialize<EconomyConsumeResult>(JsonOptions)
               ?? throw new OtomoEconomyException(OtomoEconomyFailure.Unavailable,
                   "economy answered the consume with an empty body");
    }

    // POST /internal/economy/items/advance. The same plane, key and switch as the consume: what it moves is the
    // level that consume was the price of, so it is the same trust decision stated once.
    public async Task<EconomyAdvanceResult> AdvanceAsync(EconomyAdvanceRequest advance,
        CancellationToken cancellationToken)
    {
        if (!Enabled) throw Disabled();
        var key = ReadServiceKey();
        if (!_options.ProgressEnabled || string.IsNullOrEmpty(key) || _internalBase is null)
        {
            throw new OtomoEconomyException(OtomoEconomyFailure.Disabled,
                "internal advances are off: set Otomo:Economy:ProgressEnabled and a gameserver service key");
        }

        var url = new Uri(_internalBase, "internal/economy/items/advance");
        var payload = new Dictionary<string, object?>
        {
            ["player_id"] = advance.PlayerId,
            ["key"] = advance.Key,
            ["reason"] = advance.Reason,
            ["ref"] = advance.Reference,
            ["item_id"] = advance.ItemId,
            ["advance"] = new { field = advance.Field, by = advance.By, max = advance.Max }
        };

        var element = await SendAsync(url, HttpMethod.Post, payload, bearer: null, serviceKey: key,
            operation: "advance", cancellationToken).ConfigureAwait(false);
        return element.Deserialize<EconomyAdvanceResult>(JsonOptions)
               ?? throw new OtomoEconomyException(OtomoEconomyFailure.Unavailable,
                   "economy answered the advance with an empty body");
    }

    // POST /internal/economy/entitlements. The same plane, key and switch as the grant: it names a player and records
    // value owed, so it is the same trust decision. The body names a class (`rule`) and never an amount.
    public async Task<EconomyEntitlement> CreateEntitlementAsync(EconomyEntitlementRequest request,
        CancellationToken cancellationToken)
    {
        var (baseUri, serviceKey) = InternalPlane("entitlements");
        var payload = new Dictionary<string, object?>
        {
            ["player_id"] = request.PlayerId,
            ["kind"] = request.Kind,
            ["rule"] = request.Rule,
            ["available_at"] = request.AvailableAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",
                System.Globalization.CultureInfo.InvariantCulture),
            ["source"] = request.Source,
            ["meta"] = request.Meta,
            ["key"] = request.Key
        };
        var element = await SendAsync(new Uri(baseUri, "internal/economy/entitlements"), HttpMethod.Post, payload,
            bearer: null, serviceKey, "entitlement create", cancellationToken).ConfigureAwait(false);
        return element.Deserialize<EconomyEntitlement>(JsonOptions)
               ?? throw new OtomoEconomyException(OtomoEconomyFailure.Unavailable,
                   "economy answered the entitlement create with an empty body");
    }

    // GET /internal/economy/entitlements?player_id=&include_claimed=. A bare array is accepted as well as the
    // `{entitlements: [...]}` envelope the service writes.
    public async Task<IReadOnlyList<EconomyEntitlement>> ListEntitlementsAsync(string playerId, bool includeClaimed,
        CancellationToken cancellationToken)
    {
        var (baseUri, serviceKey) = InternalPlane("entitlements");
        var url = new Uri(baseUri,
            $"internal/economy/entitlements?player_id={Uri.EscapeDataString(playerId)}&include_claimed={(includeClaimed ? "true" : "false")}");
        var element = await SendAsync(url, HttpMethod.Get, null, bearer: null, serviceKey, "entitlement list",
            cancellationToken).ConfigureAwait(false);
        if (element.ValueKind == JsonValueKind.Array)
            return element.Deserialize<List<EconomyEntitlement>>(JsonOptions) ?? [];
        return element.Deserialize<EconomyEntitlements>(JsonOptions)?.Entitlements ?? [];
    }

    // POST /internal/economy/entitlements/{id}/claim. The retry in SendAsync resends the same key; the payout is keyed by
    // the entitlement id on economy's side, so a repeated claim never pays twice (it answers 409 entitlement_claimed).
    public async Task<EconomyEntitlementClaim> ClaimEntitlementAsync(string entitlementId, string playerId, string key,
        CancellationToken cancellationToken)
    {
        var (baseUri, serviceKey) = InternalPlane("entitlement claims");
        var url = new Uri(baseUri, $"internal/economy/entitlements/{Uri.EscapeDataString(entitlementId)}/claim");
        var element = await SendAsync(url, HttpMethod.Post, new { player_id = playerId, key }, bearer: null, serviceKey,
            "entitlement claim", cancellationToken).ConfigureAwait(false);
        return element.Deserialize<EconomyEntitlementClaim>(JsonOptions)
               ?? throw new OtomoEconomyException(OtomoEconomyFailure.Unavailable,
                   "economy answered the entitlement claim with an empty body");
    }

    // The internal plane's one gate: the switch, a readable game-server key and a base URL.
    private (Uri BaseUri, string ServiceKey) InternalPlane(string what)
    {
        if (!Enabled) throw Disabled();
        var key = ReadServiceKey();
        if (!_options.ProgressEnabled || string.IsNullOrEmpty(key) || _internalBase is null)
        {
            throw new OtomoEconomyException(OtomoEconomyFailure.Disabled,
                $"internal {what} are off: set Otomo:Economy:ProgressEnabled and a gameserver service key");
        }
        return (_internalBase, key);
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
