using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace KickFlight.BootstrapApi.Tests;

// A stand-in for otomo's player matchmaking: the four routes the adapter calls, the bodies their handlers answer, and
// the COM-5 error envelope, on loopback over real HTTP. Nothing is faked at the HttpClient level, so an adapter test
// exercises the adapter's own client end to end: the path, the bearer token, the empty bodies, the status codes, the
// 409s and the retry.
//
// The queue is per party, keyed by party_id, exactly as the real service keys it: two tests never see each other's
// ticket. Every request is recorded so a test can assert what actually went over the wire.
public sealed class FakeOtomoMatchmaking : IAsyncDisposable
{
    // A token the fake treats as one it will not admit, so the refusal path is exercised without a second server.
    public const string RefusedToken = "refused-token";

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly ConcurrentDictionary<string, Party> _parties = new(StringComparer.Ordinal);
    private int _failures;
    private int _pollsBeforeMatch = 1;

    private FakeOtomoMatchmaking(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; private set; }

    // How many of the next requests answer 503 before the service behaves again.
    public int FailNextRequests
    {
        get => Volatile.Read(ref _failures);
        set => Volatile.Write(ref _failures, value);
    }

    // How many state reads answer "queued" before the party is reported matched. 0 matches on the first read.
    public int PollsBeforeMatch
    {
        get => Volatile.Read(ref _pollsBeforeMatch);
        set => Volatile.Write(ref _pollsBeforeMatch, value);
    }

    // POST /api/player/session/party answers 409 already_in_party; NamesTheParty decides whether that body carries
    // the id of the party the player is already in.
    public bool AlreadyInParty { get; set; }
    public bool AlreadyInPartyNamesTheParty { get; set; } = true;
    public string ExistingPartyId { get; set; } = "party-existing";

    // POST /api/player/match answers 409 already_matched.
    public bool AlreadyMatched { get; set; }

    // DELETE answers 409 match_in_progress.
    public bool CancelRefused { get; set; }

    // GET answers idle instead of the queue.
    public bool ReportIdle { get; set; }

    public string MatchId { get; set; } = "match-7f3c1a2b";
    public string Host { get; set; } = "10.10.20.148";
    public int MasterPort { get; set; } = 27000;
    public int GamePort { get; set; } = 27001;
    public int NamePort { get; set; } = 27002;
    public string Region { get; set; } = "ap-southeast";

    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    public void ClearRequests() => _requests.Clear();

    public IReadOnlyList<RecordedRequest> CallsTo(string path) =>
        _requests.Where(request => string.Equals(request.Path, path, StringComparison.Ordinal)).ToArray();

    public static FakeOtomoMatchmaking Start()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var server = new FakeOtomoMatchmaking(app, "");
        server.Map();
        app.StartAsync().GetAwaiter().GetResult();

        var address = app.Urls.FirstOrDefault(url => !url.EndsWith(":0", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Kestrel did not report a bound address (urls: {string.Join(", ", app.Urls)})");
        server.BaseUrl = address.TrimEnd('/');
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public sealed record RecordedRequest(string Method, string Path, string? Query, string? Authorization, string? Body);

    private sealed class Party
    {
        public string TicketId { get; init; } = "";
        public int Queued;
        public int Polls;
    }

    private void Map()
    {
        // POST /api/player/session/party with an empty body: a party of one. A player who already has one gets 409.
        _app.MapPost("/api/player/session/party", async (HttpContext context) =>
        {
            var (account, failure) = await IdentifyAsync(context);
            if (account is null) return failure;

            if (AlreadyInParty)
            {
                return AlreadyInPartyNamesTheParty
                    ? Error(context, StatusCodes.Status409Conflict, "already_in_party", "the player is already in a party",
                        ExistingPartyId)
                    : Error(context, StatusCodes.Status409Conflict, "already_in_party", "the player is already in a party");
            }

            var partyId = $"party-{Guid.NewGuid():N}";
            _parties.TryAdd(partyId, new Party { TicketId = $"ticket-{Guid.NewGuid():N}" });
            return Results.Json(new { party_id = partyId, leader_id = account, revision = 1 }, statusCode: 201);
        });

        // POST /api/player/match?party_id=<id>: queue the party. 201, or 200 with the same ticket on a repeat.
        _app.MapPost("/api/player/match", async (HttpContext context) =>
        {
            var (account, failure) = await IdentifyAsync(context);
            if (account is null) return failure;

            if (AlreadyMatched)
            {
                return Error(context, StatusCodes.Status409Conflict, "already_matched", "the party is already matched");
            }

            var partyId = context.Request.Query["party_id"].ToString();
            if (string.IsNullOrEmpty(partyId))
            {
                return Error(context, StatusCodes.Status400BadRequest, "missing_party_id", "party_id is required");
            }

            var party = _parties.GetOrAdd(partyId, _ => new Party { TicketId = $"ticket-{Guid.NewGuid():N}" });
            var repeated = Interlocked.Exchange(ref party.Queued, 1) == 1;
            return Results.Json(
                new { state = "queued", ticket_id = party.TicketId, position = 1, waited_seconds = 0.0 },
                statusCode: repeated ? 200 : 201);
        });

        // GET /api/player/match?party_id=<id>: idle, queued, or in_match with the descriptor.
        _app.MapGet("/api/player/match", async (HttpContext context) =>
        {
            var (account, failure) = await IdentifyAsync(context);
            if (account is null) return failure;

            var partyId = context.Request.Query["party_id"].ToString();
            if (string.IsNullOrEmpty(partyId))
            {
                return Error(context, StatusCodes.Status400BadRequest, "missing_party_id", "party_id is required");
            }

            if (ReportIdle) return Results.Json(new { state = "idle" });

            var party = _parties.GetOrAdd(partyId, _ => new Party { TicketId = $"ticket-{Guid.NewGuid():N}" });
            var polls = Interlocked.Increment(ref party.Polls);
            if (polls <= PollsBeforeMatch)
            {
                return Results.Json(new
                {
                    state = "queued",
                    ticket_id = party.TicketId,
                    position = 1,
                    waited_seconds = 0.4 * polls
                });
            }

            return Results.Json(new
            {
                state = "in_match",
                match = new
                {
                    match_id = MatchId,
                    status = "active",
                    transport = "photon",
                    region = Region,
                    descriptor = new
                    {
                        transport = "photon",
                        region = Region,
                        host = Host,
                        ports = new { master = MasterPort, game = GamePort, name = NamePort },
                        proxied = false
                    }
                }
            });
        });

        // DELETE /api/player/match?party_id=<id>: withdraw the party.
        _app.MapDelete("/api/player/match", async (HttpContext context) =>
        {
            var (account, failure) = await IdentifyAsync(context);
            if (account is null) return failure;

            if (CancelRefused)
            {
                return Error(context, StatusCodes.Status409Conflict, "match_in_progress", "the match is forming");
            }

            return Results.Json(new { state = "idle" });
        });
    }

    // Identifies the caller from the bearer token the way the service does, and records the request.
    private async Task<(string? Account, IResult Failure)> IdentifyAsync(HttpContext context)
    {
        context.Request.EnableBuffering();
        string? body = null;
        if (context.Request.Body.CanRead)
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var text = await reader.ReadToEndAsync();
            body = string.IsNullOrEmpty(text) ? null : text;
            context.Request.Body.Position = 0;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        _requests.Enqueue(new RecordedRequest(context.Request.Method, context.Request.Path.Value ?? "",
            context.Request.QueryString.Value, authorization, body));

        if (Interlocked.Decrement(ref _failures) >= 0)
        {
            return (null, Error(context, StatusCodes.Status503ServiceUnavailable, "internal_error", "internal server error"));
        }

        const string prefix = "Bearer ";
        var token = authorization.StartsWith(prefix, StringComparison.Ordinal)
            ? authorization[prefix.Length..]
            : null;
        if (string.IsNullOrEmpty(token) || token == RefusedToken)
        {
            return (null, Error(context, StatusCodes.Status401Unauthorized, "invalid_token", "the token is not usable"));
        }

        return (token, Results.Empty);
    }

    // The COM-5 envelope, with the party named alongside it when the case is "you already have one": the adapter looks
    // for that id in the body, so a fake that never carried it would not exercise that path.
    private static IResult Error(HttpContext context, int status, string code, string message, string? partyId = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["error"] = new { code, message, request_id = context.TraceIdentifier }
        };
        if (partyId is not null) body["party_id"] = partyId;
        return Results.Json(body, statusCode: status);
    }
}
