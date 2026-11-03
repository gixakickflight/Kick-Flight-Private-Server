using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace KickFlight.BootstrapApi.Otomo;

// The adapter as an ordinary client of otomo's player matchmaking.
//
// Every call here presents the player's own access token - the one OtomoIdentity caches per device - on otomo's
// player plane. Nothing in this class takes a player id, and nothing holds a service key: the token is the identity,
// which is why a caller cannot queue or cancel on someone else's behalf.
//
// Three rules shape the transport:
//
//   * One retry, and only for a failure that says nothing about the request: a transport error or a 5xx. A 4xx is an
//     answer (already in a party, already matched, a match already forming) and is never repeated - repeating a
//     cancel that otomo refused would only repeat the refusal.
//   * A 4xx is Refused, with otomo's own code carried out to the caller; only an unreachable or 5xx service is
//     Unavailable. One player's refusal must never be dressed up as an outage, nor the reverse.
//   * The bodies are read as they are. A field this client does not know is ignored, but a body that is missing a
//     field the adapter needs (a party id, a ticket id, a state) is not papered over: it fails as Unavailable rather
//     than letting the caller act on a guess.
public sealed class OtomoMatchmaking : IOtomoMatchmaking, IDisposable
{
    // otomo's wire keys are exact JSON names on the models, so no naming policy is applied in either direction.
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string PartyPath = "api/player/session/party";
    private const string MatchPath = "api/player/match";

    private readonly HttpClient _http;
    private readonly OtomoMatchmakingOptions _options;
    private readonly ILogger<OtomoMatchmaking> _logger;
    private readonly Uri? _base;

    // The HttpClient is passed in rather than built here so a test can hand in its own handler; Create builds the
    // one a deployment uses.
    public OtomoMatchmaking(HttpClient http, IOptions<OtomoMatchmakingOptions> options, IOptions<OtomoOptions> identity,
        ILogger<OtomoMatchmaking> logger)
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
            _logger.LogInformation("Otomo:Matchmaking is disabled: matchmaking stays the in-memory room this adapter always had");
        }
        else if (_base is null)
        {
            _logger.LogError("Otomo:Matchmaking:Enabled is true but no base URL is configured (Otomo:Matchmaking:BaseUrl or Otomo:BaseUrl); every entry will fail closed");
        }
        else
        {
            _logger.LogInformation(
                "otomo matchmaking: enabled, player match routes at {Base}, polling every {Poll}ms",
                _base, _options.PollInterval.TotalMilliseconds);
        }
    }

    // A configuration that cannot work is corrected once, loudly, at construction - not silently per request. A
    // zero timeout makes HttpClient refuse the request, and a zero poll interval would turn a waiting stream into a
    // spin, so both are reset to the defaults they were supposed to have.
    private static void Validate(OtomoMatchmakingOptions options, ILogger logger)
    {
        if (options.Timeout <= TimeSpan.Zero)
        {
            logger.LogWarning("Otomo:Matchmaking:Timeout must be positive; using {Seconds}s", 5);
            options.Timeout = TimeSpan.FromSeconds(5);
        }
        if (options.PollInterval <= TimeSpan.Zero)
        {
            logger.LogWarning("Otomo:Matchmaking:PollInterval must be positive; using {Milliseconds}ms", 400);
            options.PollInterval = TimeSpan.FromMilliseconds(400);
        }
        if (options.RetryDelay < TimeSpan.Zero) options.RetryDelay = TimeSpan.Zero;
    }

    public bool Enabled => _options.Enabled;

    public TimeSpan PollInterval => _options.PollInterval;

    // Built here, not at the composition root: this project keeps its outbound clients beside their only use, and a
    // test asserts the list of files allowed to hold one (HarnessTests.Production_code_has_no_upstream_client_api).
    public static OtomoMatchmaking Create(IOptions<OtomoMatchmakingOptions> options, IOptions<OtomoOptions> identity,
        ILogger<OtomoMatchmaking> logger) =>
        new(new HttpClient(new SocketsHttpHandler
        {
            // A client that lives as long as the process would otherwise pin one DNS answer forever.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }), options, identity, logger);

    // POST /api/player/session/party with an empty body: a party of one, led by this player. A player who is
    // already in one gets 409 already_in_party, which is not a failure of the entry - the party otomo already has is
    // the answer, and its body names it. A 409 that names nothing is a refusal: the adapter does not invent a party.
    public async ValueTask<OtomoParty> EnsurePartyAsync(string accessToken, CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Post, Url(PartyPath), EmptyBody, accessToken, "session/party",
            cancellationToken).ConfigureAwait(false);

        if (response.IsSuccess)
        {
            var party = response.Deserialize<OtomoParty>();
            if (party is null || string.IsNullOrWhiteSpace(party.PartyId))
            {
                throw new OtomoMatchmakingException(OtomoMatchmakingFailure.Unavailable,
                    $"otomo answered session/party {response.StatusCode} without a party_id");
            }
            _logger.LogInformation("otomo matchmaking: party {Party} for a player that had none (leader {Leader}, revision {Revision})",
                party.PartyId, party.LeaderId, party.Revision);
            return party;
        }

        if ((int)response.StatusCode == 409 && string.Equals(response.ErrorCode, "already_in_party", StringComparison.Ordinal))
        {
            if (TryFindPartyId(response.Element) is { } existing)
            {
                _logger.LogInformation("otomo matchmaking: player is already in party {Party}; using it", existing);
                return new OtomoParty(existing, null, 0);
            }

            // otomo's 409 for this case is documented as a bare `already_in_party`, with the party in no field this
            // client can rely on. Reporting that plainly beats queueing a party of a different id.
            throw Refusal(response, "session/party",
                "the player is already in a party and the answer did not name it");
        }

        throw Refusal(response, "session/party", "the party could not be created");
    }

    // POST /api/player/match?party_id=<id> with an empty body. 201, or 200 on a repeat with the same ticket: a client
    // that retries /battle/entry must not become two queue entries, which is why a repeat is not an error here.
    public async ValueTask<OtomoMatchTicket> EnqueueAsync(string partyId, string accessToken,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Post, MatchUrl(partyId), EmptyBody, accessToken, "match enqueue",
            cancellationToken).ConfigureAwait(false);

        if (response.IsSuccess)
        {
            var ticket = response.Deserialize<OtomoMatchTicket>();
            if (ticket is null || string.IsNullOrWhiteSpace(ticket.TicketId))
            {
                throw new OtomoMatchmakingException(OtomoMatchmakingFailure.Unavailable,
                    $"otomo answered the enqueue {response.StatusCode} without a ticket_id");
            }
            _logger.LogInformation("otomo matchmaking: party {Party} queued as ticket {Ticket} (position {Position}, waited {Waited}s)",
                partyId, ticket.TicketId, ticket.Position, ticket.WaitedSeconds);
            return ticket;
        }

        throw Refusal(response, "match enqueue", "the party was not queued");
    }

    // GET /api/player/match?party_id=<id>: idle, queued, or in_match with the match and its descriptor. Called in a
    // loop by an open assignment stream, so it logs at Debug; the transitions are logged by the caller.
    public async ValueTask<OtomoMatchState> StateAsync(string partyId, string accessToken,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get, MatchUrl(partyId), null, accessToken, "match state",
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccess) throw Refusal(response, "match state", "the party's state could not be read");

        var state = response.Deserialize<OtomoMatchState>();
        if (state is null || string.IsNullOrWhiteSpace(state.State))
        {
            throw new OtomoMatchmakingException(OtomoMatchmakingFailure.Unavailable,
                $"otomo answered the state read {response.StatusCode} without a state");
        }

        _logger.LogDebug("otomo matchmaking: party {Party} is {State}", partyId, state.State);
        return state;
    }

    // DELETE /api/player/match?party_id=<id>: withdraw the party. 409 match_in_progress means the match is already
    // forming and the queue entry is no longer the player's to withdraw - a refusal, reported as one.
    public async ValueTask CancelAsync(string partyId, string accessToken, CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Delete, MatchUrl(partyId), null, accessToken, "match cancel",
            cancellationToken).ConfigureAwait(false);

        if (response.IsSuccess)
        {
            _logger.LogInformation("otomo matchmaking: party {Party} withdrawn from the queue", partyId);
            return;
        }

        throw Refusal(response, "match cancel", "the party was not withdrawn");
    }

    // --- transport -----------------------------------------------------------------------------------------------

    // An explicitly empty body: the routes take none, and a POST with Content-Length 0 is what "empty body" means on
    // the wire. (JsonContent would send the four bytes `null`, which is not the same request.)
    private static readonly byte[] EmptyBody = [];

    private readonly record struct OtomoResponse(HttpStatusCode StatusCode, JsonElement? Element, string? ErrorCode,
        string? Body)
    {
        public int Status => (int)StatusCode;
        public bool IsSuccess => Status is >= 200 and < 300;

        public T? Deserialize<T>() where T : class =>
            Element is { ValueKind: JsonValueKind.Object } element
                ? element.Deserialize<T>(JsonOptions)
                : null;
    }

    // One request, at most twice. The retry rebuilds the request from the same values; these calls are not
    // idempotent by key, but they are idempotent by contract (enqueue repeats to the same ticket, cancel of a
    // cancelled party is answered, a second party create is a 409 the caller already handles).
    private async Task<OtomoResponse> SendAsync(HttpMethod method, Uri url, byte[]? body, string accessToken,
        string operation, CancellationToken cancellationToken)
    {
        if (!Enabled) throw Disabled();

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            string text;
            try
            {
                using var request = new HttpRequestMessage(method, url);
                if (body is not null) request.Content = new ByteArrayContent(body);
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
                    _logger.LogWarning("otomo matchmaking: {Operation} to {Url} failed ({Error}); retrying once",
                        operation, url, exception.Message);
                    await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                throw new OtomoMatchmakingException(OtomoMatchmakingFailure.Unavailable,
                    $"otomo matchmaking {operation} could not reach {url}: {exception.Message}", 0, null, exception);
            }

            using (response)
            {
                if ((int)response.StatusCode >= 500 && attempt == 0)
                {
                    _logger.LogWarning("otomo matchmaking: {Operation} to {Url} answered {Status}; retrying once",
                        operation, url, (int)response.StatusCode);
                    await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if ((int)response.StatusCode >= 500)
                {
                    var message = $"otomo matchmaking {operation} answered {(int)response.StatusCode}";
                    _logger.LogError("{Message}", message);
                    throw new OtomoMatchmakingException(OtomoMatchmakingFailure.Unavailable, message,
                        (int)response.StatusCode);
                }

                var element = ParseBody(text);
                if (response.IsSuccessStatusCode && element is null && !string.IsNullOrWhiteSpace(text))
                {
                    throw new OtomoMatchmakingException(OtomoMatchmakingFailure.Unavailable,
                        $"otomo matchmaking {operation} answered a body that is not JSON", (int)response.StatusCode);
                }

                return new OtomoResponse(response.StatusCode, element, ReadErrorCode(element), text);
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

    // The COM-5 envelope: {"error":{"code","message","request_id"}}. A body that is not one leaves the code null;
    // the status is still what the caller acts on.
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

    // The party id in a 409 body, in any of the shapes an error envelope may carry it. Absent means the caller has
    // to refuse: a party id this client made up would queue a party that is not the player's.
    private static string? TryFindPartyId(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } root) return null;

        foreach (var name in new[] { "party_id", "partyId" })
        {
            if (root.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(direct.GetString()))
            {
                return direct.GetString();
            }
        }

        if (root.TryGetProperty("party", out var party) && party.ValueKind == JsonValueKind.Object &&
            party.TryGetProperty("id", out var partyId) && partyId.ValueKind == JsonValueKind.String)
        {
            return partyId.GetString();
        }

        foreach (var container in new[] { "error", "details" })
        {
            if (root.TryGetProperty(container, out var nested) && nested.ValueKind == JsonValueKind.Object &&
                TryFindPartyId(nested) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private OtomoMatchmakingException Refusal(OtomoResponse response, string operation, string what)
    {
        var code = response.ErrorCode;
        var message = $"otomo matchmaking {operation} answered {response.Status}{(code is null ? "" : " " + code)}: {what}";
        // A refusal is about this player's request, not about the service's health.
        _logger.LogInformation("{Message}", message);
        return new OtomoMatchmakingException(OtomoMatchmakingFailure.Refused, message, response.Status, code);
    }

    private Task DelayBeforeRetryAsync(CancellationToken cancellationToken) =>
        _options.RetryDelay > TimeSpan.Zero ? Task.Delay(_options.RetryDelay, cancellationToken) : Task.CompletedTask;

    private static OtomoMatchmakingException Disabled() =>
        new(OtomoMatchmakingFailure.Disabled,
            "Otomo:Matchmaking:Enabled is false: matchmaking is the in-memory room, not otomo's queue");

    private Uri Url(string relative) =>
        _base is null
            ? throw new OtomoMatchmakingException(OtomoMatchmakingFailure.Unavailable,
                "no otomo base URL is configured, so the matchmaker cannot be reached")
            : new Uri(_base, relative);

    private Uri MatchUrl(string partyId) =>
        Url($"{MatchPath}?party_id={Uri.EscapeDataString(partyId)}");

    private static Uri? ParseBase(string value) =>
        string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            ? null
            : uri;

    public void Dispose() => _http.Dispose();
}
