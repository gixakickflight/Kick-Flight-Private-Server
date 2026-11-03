using System.Net;
using System.Text;
using KickFlight.BootstrapApi.Otomo;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The rank client on its own, with a handler that answers whatever a test scripts. This is where the wire contract is
// pinned: the exact paths, the player's own bearer token, otomo's bodies, and the three rules the adapter promises -
// one retry for a 5xx or a transport failure, never a retry for a 4xx, and a player who has never been awarded reading
// as zero points rather than as an error.
public sealed class OtomoRankTests
{
    private const string Token = "player-access-token";

    [Fact]
    public async Task Reading_every_rule_type_uses_the_player_route_and_the_players_own_token()
    {
        var (rank, handler) = Build();
        handler.Queue(HttpStatusCode.OK, """
            {"ranks":[
              {"rule_type":"ranked","points":450,"tier":"silver","updated_at":"2026-11-01T10:00:00Z"},
              {"rule_type":"retired_mode","points":20,"tier":"bronze","updated_at":"2026-10-01T10:00:00Z"}
            ]}
            """);

        var entries = await rank.ReadAllAsync(Token, CancellationToken.None);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("GET", call.Method);
        Assert.Equal("/api/player/session/rank", call.Path);
        Assert.Equal("Bearer " + Token, call.Authorization);
        Assert.Null(call.Body);
        Assert.Equal(2, entries.Count);
        Assert.Equal("ranked", entries[0].RuleType);
        Assert.Equal(450, entries[0].Points);
        Assert.Equal("silver", entries[0].Tier);
        Assert.NotNull(entries[0].UpdatedAt);
        Assert.Equal("retired_mode", entries[1].RuleType);
    }

    [Fact]
    public async Task A_player_with_no_awards_is_zero_points_in_the_lowest_tier_not_an_error()
    {
        var (rank, handler) = Build();
        handler.Queue(HttpStatusCode.OK, """
            {"ranks":[{"rule_type":"ranked","points":0,"tier":"bronze","updated_at":null}]}
            """);

        var entries = await rank.ReadAllAsync(Token, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal(0, entry.Points);
        Assert.Equal("bronze", entry.Tier);
        Assert.Null(entry.UpdatedAt);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task One_rule_type_reads_its_own_route()
    {
        var (rank, handler) = Build();
        handler.Queue(HttpStatusCode.OK, """
            {"rule_type":"ranked","points":700,"tier":"silver","updated_at":null}
            """);

        var entry = await rank.ReadOneAsync("ranked", Token, CancellationToken.None);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("/api/player/session/rank/ranked", call.Path);
        Assert.Equal(700, entry!.Points);
    }

    [Fact]
    public async Task A_rule_type_the_published_ladder_does_not_name_is_a_miss_rather_than_a_failure()
    {
        var (rank, handler) = Build();
        handler.Queue(HttpStatusCode.NotFound, """
            {"error":{"code":"unknown_rule_type","message":"no rank is kept for that battle rule type"}}
            """);

        var entry = await rank.ReadOneAsync("rapid_ball", Token, CancellationToken.None);

        Assert.Null(entry);
        // A 404 is otomo's answer, so it is not retried.
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task A_refusal_carries_otomos_code_and_is_never_retried()
    {
        var (rank, handler) = Build();
        handler.Queue(HttpStatusCode.Unauthorized, """
            {"error":{"code":"invalid_token","message":"the access token is not valid"}}
            """);

        var failure = await Assert.ThrowsAsync<OtomoRankException>(
            () => rank.ReadAllAsync(Token, CancellationToken.None).AsTask());

        Assert.Equal(OtomoRankFailure.Refused, failure.Failure);
        Assert.Equal(401, failure.StatusCode);
        Assert.Equal("invalid_token", failure.Code);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task A_5xx_is_retried_exactly_once_and_the_second_answer_is_used()
    {
        var (rank, handler) = Build();
        handler.Queue(HttpStatusCode.ServiceUnavailable, """{"error":{"code":"unavailable","message":"later"}}""");
        handler.Queue(HttpStatusCode.OK, """
            {"ranks":[{"rule_type":"ranked","points":120,"tier":"bronze","updated_at":null}]}
            """);

        var entries = await rank.ReadAllAsync(Token, CancellationToken.None);

        Assert.Equal(120, Assert.Single(entries).Points);
        Assert.Equal(2, handler.Calls.Count);
        Assert.All(handler.Calls, call => Assert.Equal("/api/player/session/rank", call.Path));
    }

    [Fact]
    public async Task Two_5xx_answers_are_unavailable_after_the_single_retry()
    {
        var (rank, handler) = Build();
        handler.Queue(HttpStatusCode.ServiceUnavailable, """{"error":{"code":"unavailable","message":"later"}}""");
        handler.Queue(HttpStatusCode.BadGateway, """{"error":{"code":"unavailable","message":"later"}}""");

        var failure = await Assert.ThrowsAsync<OtomoRankException>(
            () => rank.ReadAllAsync(Token, CancellationToken.None).AsTask());

        Assert.Equal(OtomoRankFailure.Unavailable, failure.Failure);
        Assert.Equal(502, failure.StatusCode);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task A_timeout_is_retried_once_and_then_reported_as_unavailable()
    {
        var (rank, handler) = Build(timeout: TimeSpan.FromMilliseconds(150));
        handler.Stall();

        var failure = await Assert.ThrowsAsync<OtomoRankException>(
            () => rank.ReadAllAsync(Token, CancellationToken.None).AsTask());

        Assert.Equal(OtomoRankFailure.Unavailable, failure.Failure);
        Assert.Equal(0, failure.StatusCode);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task A_success_body_without_a_ranks_array_is_unavailable_rather_than_an_empty_standing()
    {
        var (rank, handler) = Build();
        handler.Queue(HttpStatusCode.OK, """{"player_id":"p1"}""");

        var failure = await Assert.ThrowsAsync<OtomoRankException>(
            () => rank.ReadAllAsync(Token, CancellationToken.None).AsTask());

        Assert.Equal(OtomoRankFailure.Unavailable, failure.Failure);
    }

    [Fact]
    public async Task A_disabled_client_refuses_before_any_request()
    {
        var (rank, handler) = Build(enabled: false);

        var failure = await Assert.ThrowsAsync<OtomoRankException>(
            () => rank.ReadAllAsync(Token, CancellationToken.None).AsTask());

        Assert.Equal(OtomoRankFailure.Disabled, failure.Failure);
        Assert.Empty(handler.Calls);
    }

    private static (OtomoRank Rank, RecordingHandler Handler) Build(bool enabled = true,
        TimeSpan? timeout = null)
    {
        var handler = new RecordingHandler();
        var options = Options.Create(new OtomoRankOptions
        {
            Enabled = enabled,
            BaseUrl = "http://session.test/",
            Timeout = timeout ?? TimeSpan.FromSeconds(3),
            RetryDelay = TimeSpan.Zero
        });
        var identity = Options.Create(new OtomoOptions());
        return (new OtomoRank(new HttpClient(handler), options, identity, NullLogger<OtomoRank>.Instance), handler);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();

        public List<(string Method, string Path, string? Authorization, string? Body)> Calls { get; } = [];

        private bool _stall;

        public void Queue(HttpStatusCode status, string body) =>
            _responses.Enqueue(() => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });

        // Never answers, so the client's own timeout is what ends the attempt.
        public void Stall() => _stall = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.Method.Method, request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString(), body));

            if (_stall) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (_responses.Count == 0) throw new InvalidOperationException("no response was queued for this call");
            return _responses.Dequeue()();
        }
    }
}
