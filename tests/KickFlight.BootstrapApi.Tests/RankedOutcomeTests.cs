using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using KickFlight.BootstrapApi;
using KickFlight.BootstrapApi.PlayerStore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using OpenMatch;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

/// <summary>Ranked progression follows the validated team outcome; solo/team follows PartyId at entry.</summary>
[Collection("Matchmaking static state")]
public sealed class RankedOutcomeTests
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string SessionKey = "0123456789abcdef0123456789abcdef";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";

    private sealed record Session(string Token, byte[] Key, long PlayerId);

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

    private sealed class Fixture : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Factory = null!;
        public HttpClient Client = null!;
        public BattleMatchmakingService Matchmaking = null!;
        public IPlayerStore Store = null!;
        public int RuleId;
        private readonly List<(Task Task, CancellationTokenSource Cancel)> _streams = [];

        public static Fixture Create()
        {
            // Entry and room use the real clock; the API clock is 5 minutes ahead so the room is old enough.
            var clock = new FixedClock(DateTimeOffset.UtcNow.AddMinutes(5));
            var fixture = new Fixture();
            fixture.Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(AppContext.BaseDirectory);
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
        public async Task<(string EntryId, BattleMatchmakingService.BadgeBattleEntryContext Context)> EnterAsync(Session player)
        {
            using var entry = await PostAsync(Client, player, "/battle/entry", JsonSerializer.Serialize(new { battleRuleId = RuleId }));
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
        bool breakaway = false, bool includeCaller = true, bool includeScores = true, int killCount = 0)
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
            new { userId = includeCaller ? playerId : playerId + 999, score = 1, mvpFlag = false, aiFlag = false,
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
        var start = RankProgression.StartBattlePoint;

        async Task<JsonDocument> Post(string entryId, string body)
        {
            var doc = await PostAsync(f.Client, player, "/battle/result", body);
            return doc;
        }

        // Win: +50.
        var (win, winCtx) = await f.EnterAsync(player);
        var team = winCtx.OwnTeamType!.Value;
        var ruleType = winCtx.BattleRuleType!.Value;
        using (var response = await Post(win, Result(win, player.PlayerId, team, 9, 1)))
        {
            Assert.Equal((start, 7), Rank(response.RootElement, "beforeUserBattleRank"));
            Assert.Equal((start + RankProgression.WinBattlePoint, 7), Rank(response.RootElement, "userBattleRank"));
        }
        Assert.Equal(start + RankProgression.WinBattlePoint, f.Store.LoadRank(player.PlayerId, ruleType).BattlePoint);

        // Retry of the same entry (even with a body claiming a loss) replays the first snapshot and applies nothing.
        using (var retry = await Post(win, Result(win, player.PlayerId, team, 0, 50)))
        {
            Assert.Equal((start, 7), Rank(retry.RootElement, "beforeUserBattleRank"));
            Assert.Equal((start + RankProgression.WinBattlePoint, 7), Rank(retry.RootElement, "userBattleRank"));
        }
        Assert.Equal(start + RankProgression.WinBattlePoint, f.Store.LoadRank(player.PlayerId, ruleType).BattlePoint);
        var afterWin = start + RankProgression.WinBattlePoint;

        // Loss: -20, and the response reflects the real before/after.
        var (loss, lossCtx) = await f.EnterAsync(player);
        using (var response = await Post(loss, Result(loss, player.PlayerId, lossCtx.OwnTeamType!.Value, 1, 9)))
        {
            Assert.Equal((afterWin, 7), Rank(response.RootElement, "beforeUserBattleRank"));
            Assert.Equal((afterWin - RankProgression.LoseBattlePoint, 7), Rank(response.RootElement, "userBattleRank"));
        }
        var afterLoss = afterWin - RankProgression.LoseBattlePoint;
        Assert.Equal(afterLoss, f.Store.LoadRank(player.PlayerId, ruleType).BattlePoint);

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
            Assert.Equal((afterLoss, 7), Rank(response.RootElement, "beforeUserBattleRank"));
            Assert.Equal((afterLoss, 7), Rank(response.RootElement, "userBattleRank"));
            Assert.Equal(afterLoss, f.Store.LoadRank(player.PlayerId, ruleType).BattlePoint);
        }

        // Undecidable attempts cached nothing, so the same entry can still be decided once by a valid result.
        using (var decided = await Post(open, Result(open, player.PlayerId, openTeam, 9, 1)))
            Assert.Equal((afterLoss + RankProgression.WinBattlePoint, 7), Rank(decided.RootElement, "userBattleRank"));
        Assert.Equal(afterLoss + RankProgression.WinBattlePoint, f.Store.LoadRank(player.PlayerId, ruleType).BattlePoint);
    }

    [Fact]
    public async Task Capped_stat_field_voids_stats_but_not_the_rank_outcome()
    {
        await using var f = Fixture.Create();
        var player = await AuthAsync(f.Client);
        var (entry, ctx) = await f.EnterAsync(player);
        using var response = await PostAsync(f.Client, player, "/battle/result",
            Result(entry, player.PlayerId, ctx.OwnTeamType!.Value, 9, 1, killCount: 100_000));
        Assert.Equal((RankProgression.StartBattlePoint + RankProgression.WinBattlePoint, 7), Rank(response.RootElement, "userBattleRank"));
        Assert.Equal(0, f.Store.TryLoad(player.PlayerId)!.BadgeStats.Total("kills")); // stat set voided
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
        Assert.Equal(RankProgression.StartBattlePoint + RankProgression.WinBattlePoint,
            response.RootElement.GetProperty("userBattleRank").GetProperty("battlePoint").GetInt32());
        var stats = f.Store.TryLoad(player.PlayerId)!.BadgeStats;
        Assert.Equal(1, stats.QueueWins.GetValueOrDefault("solo"));
        Assert.Equal(0, stats.QueueWins.GetValueOrDefault("team"));
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

        // A party win is credited under "team", a solo win under "solo".
        var rules = BadgeAwardRules.Parse("{\"entries\":[]}");
        foreach (var (queuedAsParty, key) in new[] { (true, "team"), (false, "solo") })
        {
            var state = new SessionState { UserId = "1000001" };
            var report = new BadgeBattleStatReport
            {
                CreditStats = true, Reason = "ok", BattleRuleType = 1, KickerId = 9, QueuedAsParty = queuedAsParty,
                Won = true, Outcome = BadgeBattleOutcome.Win
            };
            Assert.True(rules.TryApplyVerifiedBattle(state, "be-" + key, out _, stats: report, now: DateTimeOffset.UtcNow));
            Assert.Equal(1, state.BadgeStats.QueueWins.GetValueOrDefault(key));
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
}
