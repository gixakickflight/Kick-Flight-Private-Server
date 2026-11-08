using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using KickFlight.BootstrapApi;
using KickFlight.BootstrapApi.Otomo;
using KickFlight.BootstrapApi.PlayerStore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using OpenMatch;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

/// <summary>
/// Ranked progression follows the validated team outcome; solo/team follows PartyId at entry. The points are session's:
/// a result is an award posted to the (fake) session service, and the response is the standing read before it and the
/// answer to it. Session's points are the client's battle points as they are (the tenant's ladder is in the client's
/// units), and a result names an outcome, never a delta.
/// </summary>
[Collection("Matchmaking static state")]
public sealed class RankedOutcomeTests
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string SessionKey = "0123456789abcdef0123456789abcdef";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";

    private sealed record Session(string Token, byte[] Key, long PlayerId);

    // A new player stands at the ladder's start_points (200), at rank 1. The win and loss values are the fake's
    // published outcomes; the adapter itself knows neither.
    private const int StartBattlePoint = FakeOtomoSocial.StartPoints;
    private const int StartRank = 1;
    private static readonly int WinBattlePoint = FakeOtomoSocial.Outcomes["win"];
    private static readonly int LoseBattlePoint = -FakeOtomoSocial.Outcomes["loss"];

    private static readonly string[] SessionRuleTypes = ["crystal_scramble", "flag_flight", "rapid_ball"];

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Writer : IServerStreamWriter<GetAssignmentsResponse>
    {
        private readonly TaskCompletionSource<string> _final = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WriteOptions? WriteOptions { get; set; }
        public Task<string> Final => _final.Task;

        public Task WriteAsync(GetAssignmentsResponse message)
        {
            if (!string.IsNullOrWhiteSpace(message.Assignment.Connection)) _final.TrySetResult(message.Assignment.Connection);
            return Task.CompletedTask;
        }
    }

    private static async Task<Session> AuthAsync(HttpClient client)
    {
        var payload = JsonSerializer.Serialize(new { hash = SessionKey, uuid = Guid.NewGuid().ToString("N") });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/index")
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(payload), Encoding.ASCII.GetBytes(CommonCode), new byte[16]))
        };
        request.Headers.Host = Host;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new Session(response.Headers.GetValues("x-app-access-token").Single(), Encoding.ASCII.GetBytes(SessionKey),
            long.Parse(response.Headers.GetValues("x-app-user-id").Single(), CultureInfo.InvariantCulture));
    }

    private static async Task<JsonDocument> PostAsync(HttpClient client, Session session, string path, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(json), session.Key, new byte[16]))
        };
        request.Headers.Host = Host;
        request.Headers.Add("x-app-access-token", session.Token);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), session.Key));
    }

    private static async Task<HttpResponseMessage> PostRawAsync(HttpClient client, Session session, string path, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(json), session.Key, new byte[16]))
        };
        request.Headers.Host = Host;
        request.Headers.Add("x-app-access-token", session.Token);
        return await client.SendAsync(request);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Factory = null!;
        public HttpClient Client = null!;
        public BattleMatchmakingService Matchmaking = null!;
        public IPlayerStore Store = null!;
        public int RuleId;

        /// <summary>The otomo account behind a player's game row.</summary>
        public string AccountOf(Session player) => Store.FindOtomoPlayer(player.PlayerId)!.OtomoPlayerId;

        /// <summary>The player's battle points as session holds them (the same number the client is shown).</summary>
        public int ClientPoints(Session player, int battleRuleType) =>
            OtomoTestEnvironment.Social.PointsOf(AccountOf(player), SessionRuleTypes[battleRuleType - 1]);

        private readonly List<(Task Task, CancellationTokenSource Cancel)> _streams = [];

        public static Fixture Create()
        {
            // Entry and room use the real clock; the API clock is 5 minutes ahead so the room is old enough.
            var clock = new FixedClock(DateTimeOffset.UtcNow.AddMinutes(5));
            var fixture = new Fixture();
            fixture.Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(AppContext.BaseDirectory);
                // The internal plane is on, so the battle's badge fact reaches the economy fake.
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Otomo:Economy:ProgressEnabled"] = "true",
                    ["Otomo:Economy:InternalBaseUrl"] = OtomoTestEnvironment.Economy.BaseUrl,
                    ["Otomo:Economy:ServiceKey"] = "kf-test-gameserver-key"
                }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(clock);
                });
            });
            fixture.Client = fixture.Factory.CreateClient();
            fixture.Matchmaking = fixture.Factory.Services.GetRequiredService<BattleMatchmakingService>();
            fixture.Matchmaking.MatchWindow = TimeSpan.FromMilliseconds(1);
            fixture.Matchmaking.JoinIncrementBaseSeconds = 0;
            fixture.Store = fixture.Factory.Services.GetRequiredService<IPlayerStore>();
            fixture.RuleId = RankedModeRotation.RuleIdAt(clock.GetUtcNow());
            return fixture;
        }

        /// <summary>Enters the active ranked rule over HTTP (no party) and waits for the room to close.</summary>
        public async Task<(string EntryId, BattleMatchmakingService.BadgeBattleEntryContext Context)> EnterAsync(Session player,
            int? ruleId = null)
        {
            using var entry = await PostAsync(Client, player, "/battle/entry", JsonSerializer.Serialize(new { battleRuleId = ruleId ?? RuleId }));
            var entryId = entry.RootElement.GetProperty("battleEntryId").GetString()!;
            var ticketId = entry.RootElement.GetProperty("battleEntryTicketId").GetString()!;
            var cancel = new CancellationTokenSource();
            var writer = new Writer();
            _streams.Add((Matchmaking.StreamAssignmentsAsync(ticketId, writer, cancel.Token), cancel));
            await writer.Final.WaitAsync(TimeSpan.FromSeconds(10));
            var context = Matchmaking.GetBadgeBattleContext(entryId)!;
            Assert.NotNull(context.OwnTeamType);
            return (entryId, context);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var (task, cancel) in _streams)
            {
                cancel.Cancel();
                try { await task; } catch (OperationCanceledException) { }
            }
            Client.Dispose();
            Factory.Dispose();
        }
    }

    private static string Result(string entryId, long playerId, int ownTeam, int? own, int? other,
        bool breakaway = false, bool includeCaller = true, bool includeScores = true, int killCount = 0, bool mvp = false)
    {
        var dict = new Dictionary<string, object> { ["battleEntryId"] = entryId, ["killCount"] = killCount };
        if (includeScores)
            dict["teamScoreList"] = new[]
            {
                new { teamType = ownTeam, score = own ?? 0 },
                new { teamType = 1 - ownTeam, score = other ?? 0 }
            };
        dict["userScoreList"] = new[]
        {
            new { userId = includeCaller ? playerId : playerId + 999, score = 1, mvpFlag = mvp, aiFlag = false,
                  breakawayFlag = breakaway, badConnectionFlag = false }
        };
        return JsonSerializer.Serialize(dict);
    }

    private static (int Points, int Rank) Rank(JsonElement root, string property) =>
        (root.GetProperty(property).GetProperty("battlePoint").GetInt32(), root.GetProperty(property).GetProperty("rank").GetInt32());

    [Fact]
    public async Task Ranked_progression_follows_win_loss_and_leaves_tie_undetermined_and_flagged_unchanged()
    {
        await using var f = Fixture.Create();
        var player = await AuthAsync(f.Client);
        var start = StartBattlePoint;

        async Task<JsonDocument> Post(string entryId, string body)
        {
            var doc = await PostAsync(f.Client, player, "/battle/result", body);
            return doc;
        }

        // Win: the published win outcome.
        var (win, winCtx) = await f.EnterAsync(player);
        var team = winCtx.OwnTeamType!.Value;
        var ruleType = winCtx.BattleRuleType!.Value;
        using (var response = await Post(win, Result(win, player.PlayerId, team, 9, 1)))
        {
            Assert.Equal((start, StartRank), Rank(response.RootElement, "beforeUserBattleRank"));
            Assert.Equal((start + WinBattlePoint, StartRank), Rank(response.RootElement, "userBattleRank"));
        }
        Assert.Equal(start + WinBattlePoint, f.ClientPoints(player, ruleType));

        // Retry of the same entry (even with a body claiming a loss) replays the first snapshot and applies nothing.
        using (var retry = await Post(win, Result(win, player.PlayerId, team, 0, 50)))
        {
            Assert.Equal((start, StartRank), Rank(retry.RootElement, "beforeUserBattleRank"));
            Assert.Equal((start + WinBattlePoint, StartRank), Rank(retry.RootElement, "userBattleRank"));
        }
        Assert.Equal(start + WinBattlePoint, f.ClientPoints(player, ruleType));
        var afterWin = start + WinBattlePoint;

        // Loss: the published loss outcome, and the response reflects the real before/after.
        var (loss, lossCtx) = await f.EnterAsync(player);
        using (var response = await Post(loss, Result(loss, player.PlayerId, lossCtx.OwnTeamType!.Value, 1, 9)))
        {
            Assert.Equal((afterWin, StartRank), Rank(response.RootElement, "beforeUserBattleRank"));
            Assert.Equal((afterWin - LoseBattlePoint, StartRank), Rank(response.RootElement, "userBattleRank"));
        }
        var afterLoss = afterWin - LoseBattlePoint;
        Assert.Equal(afterLoss, f.ClientPoints(player, ruleType));

        // Tie, flagged caller, no scores, caller missing and malformed scores: unchanged.
        var (open, openCtx) = await f.EnterAsync(player);
        var openTeam = openCtx.OwnTeamType!.Value;
        var unchangedBodies = new[]
        {
            Result(open, player.PlayerId, openTeam, 5, 5),                                  // tie
            Result(open, player.PlayerId, openTeam, 9, 1, breakaway: true),                 // flagged caller
            Result(open, player.PlayerId, openTeam, 9, 1, includeScores: false),            // no teamScoreList
            Result(open, player.PlayerId, openTeam, 9, 1, includeCaller: false),            // caller not in userScoreList
            Result(open, player.PlayerId, openTeam, -3, 1),                                 // malformed (negative) score
        };
        foreach (var body in unchangedBodies)
        {
            using var response = await Post(open, body);
            Assert.Equal((afterLoss, StartRank), Rank(response.RootElement, "beforeUserBattleRank"));
            Assert.Equal((afterLoss, StartRank), Rank(response.RootElement, "userBattleRank"));
            Assert.Equal(afterLoss, f.ClientPoints(player, ruleType));
        }

        // Undecidable attempts cached nothing, so the same entry can still be decided once by a valid result.
        using (var decided = await Post(open, Result(open, player.PlayerId, openTeam, 9, 1)))
            Assert.Equal((afterLoss + WinBattlePoint, StartRank), Rank(decided.RootElement, "userBattleRank"));
        Assert.Equal(afterLoss + WinBattlePoint, f.ClientPoints(player, ruleType));
    }

    [Fact]
    public async Task A_ranked_result_is_one_award_with_a_stable_key_and_the_retry_posts_nothing_more()
    {
        await using var f = Fixture.Create();
        var player = await AuthAsync(f.Client);
        var account = f.AccountOf(player);
        var (win, winCtx) = await f.EnterAsync(player);
        var team = winCtx.OwnTeamType!.Value;
        var ruleName = SessionRuleTypes[winCtx.BattleRuleType!.Value - 1];

        using (await PostAsync(f.Client, player, "/battle/result", Result(win, player.PlayerId, team, 9, 1))) { }
        var award = Assert.Single(OtomoTestEnvironment.Social.Awards, call => call.PlayerId == account);
        Assert.Equal($"battle:{win}:{account}", award.Key);
        Assert.Equal(ruleName, award.RuleType);
        Assert.Equal("win", award.Outcome);

        // The same battle again: answered from the entry's cache, so nothing more is posted (and were it posted, the
        // key is the same, which session applies once).
        using (await PostAsync(f.Client, player, "/battle/result", Result(win, player.PlayerId, team, 9, 1))) { }
        Assert.Single(OtomoTestEnvironment.Social.Awards, call => call.PlayerId == account);

        // A loss is the outcome "loss" under its own entry's key.
        var (loss, lossCtx) = await f.EnterAsync(player);
        using (await PostAsync(f.Client, player, "/battle/result", Result(loss, player.PlayerId, lossCtx.OwnTeamType!.Value, 1, 9))) { }
        var lossAward = OtomoTestEnvironment.Social.Awards.Single(call => call.PlayerId == account && call.Key == $"battle:{loss}:{account}");
        Assert.Equal("loss", lossAward.Outcome);
    }

    [Fact]
    public async Task Battle_end_shows_the_published_win_outcome_as_battlePoint()
    {
        await using var f = Fixture.Create();
        var player = await AuthAsync(f.Client);
        using var end = await PostAsync(f.Client, player, "/battle/end", $$"""
            {"battleId":"battle-end-1","teamScoreList":[{"teamType":0,"score":5,"remainingDistance":2,"lastRemainingDistance":1}],
             "userScoreList":[{"userId":"{{player.PlayerId}}","score":5,"mvpFlag":true,"breakawayFlag":false,"badConnectionFlag":false,"aiFlag":false}]}
            """);
        // The KF ladder publishes outcomes.win = 50; the adapter holds no number of its own for it.
        Assert.Equal(FakeOtomoSocial.Outcomes["win"], end.RootElement.GetProperty("battlePoint").GetInt32());
        Assert.Equal(50, end.RootElement.GetProperty("battlePoint").GetInt32());
    }

    [Fact]
    public async Task A_win_posts_the_outcome_win_and_no_delta()
    {
        await using var f = Fixture.Create();
        var player = await AuthAsync(f.Client);
        var account = f.AccountOf(player);
        var (win, ctx) = await f.EnterAsync(player);
        using (await PostAsync(f.Client, player, "/battle/result", Result(win, player.PlayerId, ctx.OwnTeamType!.Value, 9, 1))) { }

        var award = Assert.Single(OtomoTestEnvironment.Social.Awards, call => call.PlayerId == account);
        using var sent = JsonDocument.Parse(award.RawBody);
        Assert.Equal("win", sent.RootElement.GetProperty("outcome").GetString());
        Assert.False(sent.RootElement.TryGetProperty("delta", out _));
        Assert.Null(award.Delta);
    }

    // A regular match (rule 1, match type 1) moves the same ladder through regular_win / regular_loss; the tenant's
    // session.rank outcome_limits are what stop those at S.
    [Fact]
    public async Task A_regular_match_win_posts_the_outcome_regular_win()
    {
        await using var f = Fixture.Create();
        var player = await AuthAsync(f.Client);
        var account = f.AccountOf(player);
        var (win, ctx) = await f.EnterAsync(player, ruleId: 1);
        using (await PostAsync(f.Client, player, "/battle/result", Result(win, player.PlayerId, ctx.OwnTeamType!.Value, 9, 1))) { }

        var award = Assert.Single(OtomoTestEnvironment.Social.Awards, call => call.PlayerId == account);
        using var sent = JsonDocument.Parse(award.RawBody);
        Assert.Equal("regular_win", sent.RootElement.GetProperty("outcome").GetString());
    }

    // The counters this player's battles reported to economy's internal plane, in order.
    private static string[] ReportedCounters(string account) =>
        OtomoTestEnvironment.Economy.Requests
            .Where(call => call.Path == "/internal/economy/progress" && call.Body is not null)
            .Select(call => JsonDocument.Parse(call.Body!).RootElement)
            .Where(body => body.GetProperty("player_id").GetString() == account)
            .Select(body => body.GetProperty("counter").GetString()!)
            .ToArray();

    [Fact]
    public async Task A_regular_match_win_as_mvp_counts_the_win_and_the_mvp_and_pays_win_experience()
    {
        await using var f = Fixture.Create();
        var player = await AuthAsync(f.Client);
        var account = f.AccountOf(player);
        var (win, ctx) = await f.EnterAsync(player, ruleId: 1);
        using var response = await PostAsync(f.Client, player, "/battle/result",
            Result(win, player.PlayerId, ctx.OwnTeamType!.Value, 9, 1, mvp: true));

        var counters = ReportedCounters(account);
        Assert.Contains("battle.play", counters);
        Assert.Contains("battle.win", counters);
        Assert.Contains("battle.mvp", counters);
        Assert.Equal(FakeOtomoEconomy.MatchExpWin, response.RootElement.GetProperty("userExp").GetInt64());
        Assert.Equal(FakeOtomoEconomy.MatchExpWin, OtomoTestEnvironment.Economy.BalanceOf(account, "player_exp"));
    }

    [Fact]
    public async Task A_loss_counts_a_play_but_no_win_and_pays_loss_experience_once_across_a_retry()
    {
        await using var f = Fixture.Create();
        var player = await AuthAsync(f.Client);
        var account = f.AccountOf(player);
        var (loss, ctx) = await f.EnterAsync(player, ruleId: 1);
        var body = Result(loss, player.PlayerId, ctx.OwnTeamType!.Value, 1, 9);
        using (await PostAsync(f.Client, player, "/battle/result", body)) { }
        using var retry = await PostAsync(f.Client, player, "/battle/result", body);

        var counters = ReportedCounters(account);
        Assert.Contains("battle.play", counters);
        Assert.DoesNotContain("battle.win", counters);
        Assert.DoesNotContain("battle.mvp", counters);
        Assert.Equal(FakeOtomoEconomy.MatchExpLoss, retry.RootElement.GetProperty("userExp").GetInt64());
        Assert.Equal(FakeOtomoEconomy.MatchExpLoss, OtomoTestEnvironment.Economy.BalanceOf(account, "player_exp"));
    }

    [Fact]
    public async Task A_loss_at_the_floor_of_200_stays_at_200()
    {
        await using var f = Fixture.Create();
        var player = await AuthAsync(f.Client);
        var (loss, ctx) = await f.EnterAsync(player);
        using var response = await PostAsync(f.Client, player, "/battle/result",
            Result(loss, player.PlayerId, ctx.OwnTeamType!.Value, 1, 9));
        // A new player stands at start_points, which is also the ladder's floor (200): session clamps, the adapter does not.
        Assert.Equal((StartBattlePoint, StartRank), Rank(response.RootElement, "beforeUserBattleRank"));
        Assert.Equal((StartBattlePoint, StartRank), Rank(response.RootElement, "userBattleRank"));
        Assert.Equal(StartBattlePoint, f.ClientPoints(player, ctx.BattleRuleType!.Value));
    }

    [Fact]
    public async Task A_result_session_cannot_award_is_answered_503_with_the_same_key_and_the_retry_awards_it_once()
    {
        await using var f = Fixture.Create();
        var player = await AuthAsync(f.Client);
        var account = f.AccountOf(player);
        var (entry, ctx) = await f.EnterAsync(player);
        var body = Result(entry, player.PlayerId, ctx.OwnTeamType!.Value, 9, 1);

        OtomoTestEnvironment.Social.SetAwardsUnavailable(account, true);
        using (var refused = await PostRawAsync(f.Client, player, "/battle/result", body))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.Equal("1", refused.Headers.GetValues("x-app-status-code").Single());
            using var error = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.Equal("social-unavailable", error.RootElement.GetProperty("error").GetString());
        }
        // Nothing was applied, and nothing was guessed: session still holds 0 points. The attempt was posted (twice:
        // the client retries a 5xx once) under the one key.
        Assert.Equal(StartBattlePoint, f.ClientPoints(player, ctx.BattleRuleType!.Value));
        var attempts = OtomoTestEnvironment.Social.Awards.Where(call => call.PlayerId == account).ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.Single(attempts.Select(call => call.Key).Distinct());

        // Session comes back; the client retries the same result. It is awarded exactly once, under the same key.
        OtomoTestEnvironment.Social.SetAwardsUnavailable(account, false);
        using (var retry = await PostAsync(f.Client, player, "/battle/result", body))
        {
            Assert.Equal((StartBattlePoint, StartRank), Rank(retry.RootElement, "beforeUserBattleRank"));
            Assert.Equal((StartBattlePoint + WinBattlePoint, StartRank), Rank(retry.RootElement, "userBattleRank"));
        }
        Assert.Equal(StartBattlePoint + WinBattlePoint, f.ClientPoints(player, ctx.BattleRuleType!.Value));
        var all = OtomoTestEnvironment.Social.Awards.Where(call => call.PlayerId == account).ToArray();
        Assert.Single(all.Select(call => call.Key).Distinct());
    }

    [Fact]
    public async Task A_result_is_answered_503_when_the_standing_cannot_be_read()
    {
        await using var f = Fixture.Create();
        var player = await AuthAsync(f.Client);
        var account = f.AccountOf(player);
        var (entry, ctx) = await f.EnterAsync(player);

        OtomoTestEnvironment.Social.SetRankReadsUnavailable(account, true);
        using var refused = await PostRawAsync(f.Client, player, "/battle/result",
            Result(entry, player.PlayerId, ctx.OwnTeamType!.Value, 9, 1));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        // No award was posted for a result whose before-standing could not be read.
        Assert.DoesNotContain(OtomoTestEnvironment.Social.Awards, call => call.PlayerId == account);
    }

    [Fact]
    public async Task Capped_stat_field_voids_stats_but_not_the_rank_outcome()
    {
        await using var f = Fixture.Create();
        var player = await AuthAsync(f.Client);
        var (entry, ctx) = await f.EnterAsync(player);
        using var response = await PostAsync(f.Client, player, "/battle/result",
            Result(entry, player.PlayerId, ctx.OwnTeamType!.Value, 9, 1, killCount: 100_000));
        Assert.Equal((StartBattlePoint + WinBattlePoint, StartRank), Rank(response.RootElement, "userBattleRank"));
        // The stat set is voided: the battle is still reported (it was played), as void, with no stat and the reason.
        var reported = Assert.Single(OtomoTestEnvironment.Economy.FactReports,
            fact => fact.PlayerId == f.AccountOf(player) && fact.Fact == "battle.result" && fact.Key.EndsWith(entry, StringComparison.Ordinal));
        Assert.False(reported.Fields.GetProperty("credited").GetBoolean());
        Assert.False(reported.Fields.GetProperty("won").GetBoolean());
        Assert.Equal(0, reported.Fields.GetProperty("kills").GetInt64());
        Assert.StartsWith("cap:", reported.Fields.GetProperty("credit_reason").GetString());
        // ...and the rank outcome (asserted above) was decided from the scores, which the cap does not void.
        Assert.Equal("win", reported.Fields.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Battle_result_response_keeps_its_shape()
    {
        await using var f = Fixture.Create();
        var player = await AuthAsync(f.Client);
        var (entry, ctx) = await f.EnterAsync(player);
        using var response = await PostAsync(f.Client, player, "/battle/result",
            Result(entry, player.PlayerId, ctx.OwnTeamType!.Value, 9, 1));
        var names = response.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        string[] expected =
        [
            "userExp", "userBattleRank", "beforeUserBattleRank", "playerLevelUpRewardList", "receivedUserCapsuleList",
            "receivedUserItemList", "receivedUserPresentList", "userMissionProgressList", "userDailyRandomMissionTaskList",
            "receivedUserHonorList", "receivedRewardList", "badConnectionReliefFlag", "battleCount", "battleWinCount",
            "matchmakingTeamId", "campaignList", "userFestivalTeam", "festivalPoint", "lotteryFestivalPointId"
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal).ToArray(), names);
        foreach (var rank in new[] { "userBattleRank", "beforeUserBattleRank" })
            Assert.Equal(["battlePoint", "battleRuleType", "rank"],
                response.RootElement.GetProperty(rank).EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(JsonValueKind.Number, response.RootElement.GetProperty("battleCount").ValueKind);
        Assert.Equal(JsonValueKind.Number, response.RootElement.GetProperty("battleWinCount").ValueKind);
    }

    [Fact]
    public async Task Solo_entrant_matched_with_others_counts_as_a_solo_win()
    {
        await using var f = Fixture.Create();
        var player = await AuthAsync(f.Client);
        var mate = await AuthAsync(f.Client);
        // Two solo entrants for the same rule may be put in one room and one team; neither entered with a party.
        var (entry, ctx) = await f.EnterAsync(player);
        var (_, mateCtx) = await f.EnterAsync(mate);
        Assert.False(ctx.QueuedAsParty);
        Assert.False(mateCtx.QueuedAsParty);

        using var response = await PostAsync(f.Client, player, "/battle/result",
            Result(entry, player.PlayerId, ctx.OwnTeamType!.Value, 9, 1));
        Assert.Equal(StartBattlePoint + WinBattlePoint,
            response.RootElement.GetProperty("userBattleRank").GetProperty("battlePoint").GetInt32());
        var reported = Assert.Single(OtomoTestEnvironment.Economy.FactReports,
            fact => fact.PlayerId == f.AccountOf(player) && fact.Fact == "battle.result" && fact.Key.EndsWith(entry, StringComparison.Ordinal));
        Assert.True(reported.Fields.GetProperty("won").GetBoolean());
        Assert.Equal("solo", reported.Fields.GetProperty("queue").GetString());
    }

    [Fact]
    public void Party_entrants_are_flagged_as_party_and_a_plain_entry_is_solo()
    {
        var service = new BattleMatchmakingService(NullLogger<BattleMatchmakingService>.Instance, null!);
        int[] deck = [3010001, 3010002, 3010003, 3010004];
        var host = service.CreateTeam("1000001", "Host", 1, 1, 1, deck.ToList(), "2563", matchType: 2, battleRuleType: 1);
        var guest = service.JoinTeam(host.MatchmakingTeamId, "1000002", "Guest", 2, 1, deck.ToList())!;
        var (soloId, _) = service.RegisterEntry("1000003", "Solo", 3, 1, 1, deck.ToList(), matchType: 2, battleRuleType: 1);

        Assert.True(service.GetBadgeBattleContext(host.BattleEntryId)!.QueuedAsParty);
        Assert.True(service.GetBadgeBattleContext(guest.BattleEntryId)!.QueuedAsParty);
        Assert.False(service.GetBadgeBattleContext(soloId)!.QueuedAsParty);

        // A party win is reported under "team", a solo win under "solo".
        foreach (var (queuedAsParty, queue) in new[] { (true, "team"), (false, "solo") })
        {
            var report = new BadgeBattleStatReport
            {
                CreditStats = true, Reason = "ok", BattleRuleType = 1, KickerId = 9, QueuedAsParty = queuedAsParty,
                Won = true, Outcome = BadgeBattleOutcome.Win
            };
            Assert.Equal(queue, DemoSessionApi.BattleFields(report, 1, 0, -1)["queue"]);
        }
    }

    [Fact]
    public void Rank_win_decision_covers_every_outcome()
    {
        BadgeBattleStatReport Report(BadgeBattleOutcome outcome, bool flagged = false, string reason = "") =>
            new() { Outcome = outcome, CallerFlagged = flagged, OutcomeReason = reason };

        Assert.True(DemoSessionApi.RankWinFor(Report(BadgeBattleOutcome.Win), out _));
        Assert.False(DemoSessionApi.RankWinFor(Report(BadgeBattleOutcome.Loss), out _));
        Assert.Null(DemoSessionApi.RankWinFor(Report(BadgeBattleOutcome.Tie), out var tie));
        Assert.Equal("tie", tie);
        Assert.Null(DemoSessionApi.RankWinFor(Report(BadgeBattleOutcome.Win, flagged: true), out var flagged));
        Assert.Equal("caller_flagged", flagged);
        Assert.Null(DemoSessionApi.RankWinFor(Report(BadgeBattleOutcome.Undetermined, reason: "no_team_score"), out var undetermined));
        Assert.Equal("no_team_score", undetermined);
        Assert.Null(DemoSessionApi.RankWinFor(null, out _));
    }

    // ---- roster profile and guardian rank (display only, read from session) ----

    private static BattleMatchmakingService.MatchingPlayerBattleInfo RosterRow(Fixture f, Session player, BattleMatchmakingService.BadgeBattleEntryContext ctx) =>
        Assert.Single(f.Matchmaking.GetRoomRoster(ctx.BattleId!),
            p => p.kickerAiParameterId == 0 && p.userId == player.PlayerId.ToString(CultureInfo.InvariantCulture));

    private static async Task<JsonElement> GuardianAsync(Fixture f, Session player, BattleMatchmakingService.BadgeBattleEntryContext ctx)
    {
        using var start = await PostAsync(f.Client, player, "/battle/start",
            JsonSerializer.Serialize(new { battleRuleId = f.RuleId, battleId = ctx.BattleId }));
        return start.RootElement.GetProperty("guardianParameter").Clone();
    }

    [Fact]
    public async Task Roster_rank_badge_and_guardian_rank_are_the_players_session_standing_and_display_slot()
    {
        await using var f = Fixture.Create();
        var player = await AuthAsync(f.Client);
        var account = f.AccountOf(player);
        foreach (var ruleType in SessionRuleTypes) OtomoTestEnvironment.Social.SeedRank(account, ruleType, 1400); // rank_4
        OtomoTestEnvironment.Social.SeedDisplay(account, "honor", "honor:6010003");

        var (_, ctx) = await f.EnterAsync(player);
        var me = RosterRow(f, player, ctx);
        Assert.Equal(4, me.rank);
        Assert.Equal(6010003, me.honorId);

        // The room's only human is this player, so the guardian rank is theirs, and the row id names rule and rank.
        var guardian = await GuardianAsync(f, player, ctx);
        Assert.Equal(4, guardian.GetProperty("rank").GetInt32());
        Assert.Equal(GuardianBalance.RowId(ctx.RoomBattleRuleId ?? f.RuleId, 4), guardian.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Session_rank_outage_keeps_the_old_roster_constants_and_guardian_rank_C_without_failing_the_request()
    {
        await using var f = Fixture.Create();
        var player = await AuthAsync(f.Client);
        var account = f.AccountOf(player);
        OtomoTestEnvironment.Social.SetRankReadsUnavailable(account, true);
        try
        {
            var (_, ctx) = await f.EnterAsync(player); // asserts the entry answered 200
            Assert.Equal(DemoSessionApi.RosterFallbackRank, RosterRow(f, player, ctx).rank);
            var guardian = await GuardianAsync(f, player, ctx);
            Assert.Equal(GuardianBalance.RankC, guardian.GetProperty("rank").GetInt32());
        }
        finally
        {
            OtomoTestEnvironment.Social.SetRankReadsUnavailable(account, false);
        }
    }
}
