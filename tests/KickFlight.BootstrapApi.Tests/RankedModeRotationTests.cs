using KickFlight.BootstrapApi.Otomo;
using System.Net;
using System.Globalization;
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

public sealed class RankedModeRotationTests
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string SessionKey = "0123456789abcdef0123456789abcdef";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";

    [Fact]
    public void Daily_schedule_covers_each_hour_once_and_matches_utc_epoch_cycle()
    {
        using var document = JsonDocument.Parse(RankedModeRotation.BuildScheduleJson());
        var rows = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(48, rows.Length);
        Assert.Equal(24, rows.Count(row => row.GetProperty("seasonMatchBattleRuleType").GetInt32() == -1));
        Assert.Equal(new[] { 8, 8, 8 }, Enumerable.Range(1, 3)
            .Select(type => rows.Count(row => row.GetProperty("seasonMatchBattleRuleType").GetInt32() == type)));

        // The 23:00 window closes at "00:00:00"; the client adds a day to an end that precedes its start
        // (RankerMatchBattleScheduleMaster.GetRankerMatchSchedule) and treats the end as exclusive.
        var negativeOne = rows.Where(row => row.GetProperty("seasonMatchBattleRuleType").GetInt32() == -1).ToArray();
        Assert.Equal("00:00:00", negativeOne.Single(row => row.GetProperty("startTime").GetString() == "23:00:00")
            .GetProperty("endTime").GetString());

        for (var utcHour = 0; utcHour < 24; utcHour++)
        {
            var instant = DateTimeOffset.UnixEpoch.AddHours(utcHour).AddMinutes(30);
            var jstTime = instant.AddHours(9).TimeOfDay;
            var active = negativeOne.Where(row => IsInScheduleWindow(row, jstTime)).ToArray();
            var row = Assert.Single(active);
            Assert.Equal(RankedModeRotation.RuleIdAt(instant), row.GetProperty("battleRuleId").GetInt32());
        }
    }

    [Fact]
    public void Negative_one_rows_resolve_to_match_type_two_rules_in_config_and_inline_masters()
    {
        using var schedule = JsonDocument.Parse(RankedModeRotation.BuildScheduleJson());
        var negativeOne = schedule.RootElement.EnumerateArray()
            .Where(row => row.GetProperty("seasonMatchBattleRuleType").GetInt32() == -1)
            .ToArray();
        Assert.Equal(24, negativeOne.Length);

        var configPath = Path.Combine(RepositoryPaths.FindRoot(AppContext.BaseDirectory), "config/masters_battle_rule.json");
        AssertNegativeOneRowsResolve(negativeOne, File.ReadAllText(configPath));
        AssertNegativeOneRowsResolve(negativeOne, DemoSessionApi.BattleRuleFallbackJson);
    }

    private static void AssertNegativeOneRowsResolve(JsonElement[] negativeOne, string battleRuleJson)
    {
        using var rules = JsonDocument.Parse(battleRuleJson);
        var matchTypeTwoRuleIds = rules.RootElement.EnumerateArray()
            .Where(row => row.GetProperty("matchType").GetInt32() == 2)
            .Select(row => row.GetProperty("id").GetInt32())
            .ToHashSet();
        Assert.Contains(RankedModeRotation.CrystalRuleId, matchTypeTwoRuleIds);
        Assert.Contains(RankedModeRotation.FlagRuleId, matchTypeTwoRuleIds);

        for (var hour = 0; hour < 24; hour++)
        {
            var jstTime = new TimeSpan(hour, 30, 0);
            var active = negativeOne.Where(row => IsInScheduleWindow(row, jstTime)).ToArray();
            var row = Assert.Single(active);
            Assert.Contains(row.GetProperty("battleRuleId").GetInt32(), matchTypeTwoRuleIds);
        }
    }

    [Theory]
    [InlineData(0, 7)]
    [InlineData(3599, 7)]
    [InlineData(3600, 8)]
    [InlineData(7200, 4)]
    [InlineData(10800, 7)]
    public void Resolver_changes_exactly_at_utc_hour_boundaries(long unixSeconds, int expectedRuleId)
    {
        Assert.Equal(expectedRuleId, RankedModeRotation.RuleIdAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds)));
    }

    [Fact]
    public async Task Battle_entry_rejects_previous_hour_rule_and_accepts_current_rule()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(2 * 3600));
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
        });
        var client = factory.CreateClient();
        var sessionKey = Encoding.ASCII.GetBytes(SessionKey);
        var commonCode = Encoding.ASCII.GetBytes(CommonCode);
        var authPayload = JsonSerializer.Serialize(new { hash = SessionKey, uuid = Guid.NewGuid().ToString("N") });
        using var authRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/index")
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(authPayload), commonCode, new byte[16]))
        };
        authRequest.Headers.Host = Host;
        using var authResponse = await client.SendAsync(authRequest);
        Assert.Equal(HttpStatusCode.OK, authResponse.StatusCode);
        var accessToken = authResponse.Headers.GetValues("x-app-access-token").Single();

        async Task<HttpResponseMessage> Enter(int ruleId)
        {
            var body = JsonSerializer.Serialize(new { battleRuleId = ruleId });
            using var request = new HttpRequestMessage(HttpMethod.Post, "/battle/entry")
            {
                Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(body), sessionKey, new byte[16]))
            };
            request.Headers.Host = Host;
            request.Headers.Add("x-app-access-token", accessToken);
            return await client.SendAsync(request);
        }

        var previousRule = RankedModeRotation.RuleIdAt(clock.GetUtcNow().AddHours(-1));
        var activeRule = RankedModeRotation.RuleIdAt(clock.GetUtcNow());
        using var rejected = await Enter(previousRule);
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal("1", rejected.Headers.GetValues("x-app-status-code").Single());
        Assert.Equal("{}", Encoding.UTF8.GetString(D2CCodec.Decode(await rejected.Content.ReadAsByteArrayAsync(), sessionKey)));

        using var accepted = await Enter(activeRule);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("0", accepted.Headers.GetValues("x-app-status-code").Single());
        using var acceptedBody = JsonDocument.Parse(D2CCodec.Decode(await accepted.Content.ReadAsByteArrayAsync(), sessionKey));
        Assert.False(string.IsNullOrWhiteSpace(acceptedBody.RootElement.GetProperty("battleEntryTicketId").GetString()));

        var battleEntryId = acceptedBody.RootElement.GetProperty("battleEntryId").GetString()!;
        var ticketId = acceptedBody.RootElement.GetProperty("battleEntryTicketId").GetString()!;
        var matchmaking = factory.Services.GetRequiredService<BattleMatchmakingService>();
        matchmaking.MatchWindow = TimeSpan.FromMilliseconds(1);
        matchmaking.JoinIncrementBaseSeconds = 0;
        using var assignmentCancellation = new CancellationTokenSource();
        var assignmentWriter = new FinalAssignmentWriter();
        var assignmentTask = matchmaking.StreamAssignmentsAsync(ticketId, assignmentWriter, assignmentCancellation.Token);
        var battleId = await assignmentWriter.FinalBattleId.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(activeRule, matchmaking.GetRoomBattleRuleId(battleId));

        // The room keeps its selected mode when the global hourly rotation advances during matchmaking.
        clock.SetUtcNow(clock.GetUtcNow().AddHours(1));
        var newRule = RankedModeRotation.RuleIdAt(clock.GetUtcNow());
        Assert.NotEqual(activeRule, newRule);
        var startPayload = JsonSerializer.Serialize(new { battleId, battleRuleId = newRule });
        using var startRequest = new HttpRequestMessage(HttpMethod.Post, "/battle/start")
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(startPayload), sessionKey, new byte[16]))
        };
        startRequest.Headers.Host = Host;
        startRequest.Headers.Add("x-app-access-token", accessToken);
        using var startResponse = await client.SendAsync(startRequest);
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);
        using var startBody = JsonDocument.Parse(D2CCodec.Decode(await startResponse.Content.ReadAsByteArrayAsync(), sessionKey));
        Assert.Equal(2, startBody.RootElement.GetProperty("guardianParameter").GetProperty("id").GetInt32());

        var resultPayload = JsonSerializer.Serialize(new { battleEntryId });
        using var resultRequest = new HttpRequestMessage(HttpMethod.Post, "/battle/result")
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(resultPayload), sessionKey, new byte[16]))
        };
        resultRequest.Headers.Host = Host;
        resultRequest.Headers.Add("x-app-access-token", accessToken);
        using var resultResponse = await client.SendAsync(resultRequest);
        Assert.Equal(HttpStatusCode.OK, resultResponse.StatusCode);
        using var resultBody = JsonDocument.Parse(D2CCodec.Decode(await resultResponse.Content.ReadAsByteArrayAsync(), sessionKey));
        var beforeRank = resultBody.RootElement.GetProperty("beforeUserBattleRank");
        var afterRank = resultBody.RootElement.GetProperty("userBattleRank");
        Assert.Equal(3, afterRank.GetProperty("battleRuleType").GetInt32());
        // The body carries no team scores, so the outcome is undetermined and rank must not move (outcome-driven
        // progression is covered in RankedOutcomeTests).
        Assert.Equal(beforeRank.GetProperty("battlePoint").GetInt32(), afterRank.GetProperty("battlePoint").GetInt32());
        assignmentCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => assignmentTask);
    }

    [Fact]
    public async Task Concurrent_retries_of_one_ranked_result_apply_and_return_the_same_snapshot()
    {
        var service = new BattleMatchmakingService(NullLogger<BattleMatchmakingService>.Instance, null!);
        var (entryId, _) = service.RegisterEntry("player-1", "player", 1, 1,
            RankedModeRotation.CrystalRuleId, [], matchType: 2, battleRuleType: 1);
        // A callback that fails (session unreachable) releases the gate and caches nothing.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProcessRankedResultOnceAsync(entryId, "player-1",
                _ => Task.FromException<BattleMatchmakingService.RankedResultSnapshot?>(
                    new InvalidOperationException("session unreachable"))));
        var appliedCount = 0;
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
            (await service.ProcessRankedResultOnceAsync(entryId, "player-1", async ruleType =>
            {
                Interlocked.Increment(ref appliedCount);
                await Task.Delay(5);
                return new BattleMatchmakingService.RankedResultSnapshot(ruleType,
                    new BattleRankStanding(2900, 7), new BattleRankStanding(2950, 7));
            }))!)));

        Assert.Equal(1, appliedCount);
        Assert.Single(responses, response => !response.IsReplay);
        Assert.Equal(11, responses.Count(response => response.IsReplay));
        Assert.All(responses, response => Assert.Equal(responses[0].RankedResult, response.RankedResult));
    }

    [Fact]
    public async Task Battle_result_progresses_only_the_owned_ranked_entry_mode()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
        });
        var client = factory.CreateClient();
        var player = await CreateResultSessionAsync(client);
        var otherPlayer = await CreateResultSessionAsync(client);

        async Task<string> Enter(ResultSession session, int ruleId)
        {
            using var body = await PostJsonAsync(client, session, "/battle/entry", new { battleRuleId = ruleId });
            var entryId = body.RootElement.GetProperty("battleEntryId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(entryId));
            return entryId!;
        }

        static void AssertUnchanged(JsonDocument result)
        {
            var before = result.RootElement.GetProperty("beforeUserBattleRank");
            var after = result.RootElement.GetProperty("userBattleRank");
            Assert.Equal(before.GetProperty("battleRuleType").GetInt32(), after.GetProperty("battleRuleType").GetInt32());
            Assert.Equal(before.GetProperty("battlePoint").GetInt32(), after.GetProperty("battlePoint").GetInt32());
            Assert.Equal(before.GetProperty("rank").GetInt32(), after.GetProperty("rank").GetInt32());
        }

        // Retail BattleResultRequestData carries teamScoreList, not a trusted won flag. Both score orderings
        // must leave every normal rule's shared ranked ladder unchanged.
        foreach (var ruleId in new[] { 1, 2, 3 })
        {
            var entryId = await Enter(player, ruleId);
            foreach (var (ourScore, theirScore) in new[] { (100, 50), (50, 100) })
            {
                using var result = await PostJsonAsync(client, player, "/battle/result", new
                {
                    battleEntryId = entryId,
                    teamScoreList = new[]
                    {
                        new { teamType = 0, score = ourScore },
                        new { teamType = 1, score = theirScore }
                    }
                });
                AssertUnchanged(result);
                Assert.Equal(ruleId, result.RootElement.GetProperty("userBattleRank").GetProperty("battleRuleType").GetInt32());
            }
        }

        // Festival rules and custom results have no ranked eligibility. Custom result requests have no normal entry.
        var festivalEntryId = await Enter(player, 5);
        using (var festival = await PostJsonAsync(client, player, "/battle/result", new { battleEntryId = festivalEntryId }))
            AssertUnchanged(festival);
        using (var missing = await PostJsonAsync(client, player, "/battle/result", new { }))
            AssertUnchanged(missing);
        var store = factory.Services.GetRequiredService<IPlayerStore>();
        var account = store.FindOtomoPlayer(player.PlayerId)!.OtomoPlayerId;
        var rankBeforeCustom = OtomoTestEnvironment.Social.PointsOf(account, "crystal_scramble");
        using (var custom = await PostJsonAsync(client, player, "/customBattle/result", new { customBattleId = "custom-test" }))
            Assert.Empty(custom.RootElement.EnumerateObject());
        Assert.Equal(rankBeforeCustom, OtomoTestEnvironment.Social.PointsOf(account, "crystal_scramble"));
        using (var unknown = await PostJsonAsync(client, player, "/battle/result", new { battleEntryId = "be-not-issued" }))
            AssertUnchanged(unknown);

        // A known entry belongs to the player who received it; another session cannot claim its ranked result.
        var rankedEntryId = await Enter(player, RankedModeRotation.CrystalRuleId);
        var rankedEntry = factory.Services.GetRequiredService<BattleMatchmakingService>()
            .GetBattleResultEntry(rankedEntryId);
        Assert.NotNull(rankedEntry);
        Assert.Equal(2, rankedEntry!.MatchType);
        Assert.Equal(1, rankedEntry.BattleRuleType);
        using (var stolen = await PostJsonAsync(client, otherPlayer, "/battle/result", new { battleEntryId = rankedEntryId }))
            AssertUnchanged(stolen);

        // Advancing the rotation after entry must not replace the entry's snapshotted rule 7/type 1 with rule 8/type 2.
        clock.SetUtcNow(DateTimeOffset.UnixEpoch.AddHours(1));
        Assert.Equal(RankedModeRotation.FlagRuleId, RankedModeRotation.RuleIdAt(clock.GetUtcNow()));
        using var ranked = await PostJsonAsync(client, player, "/battle/result", new { battleEntryId = rankedEntryId });
        var beforeRank = ranked.RootElement.GetProperty("beforeUserBattleRank");
        var afterRank = ranked.RootElement.GetProperty("userBattleRank");
        Assert.Equal(1, afterRank.GetProperty("battleRuleType").GetInt32());
        // No team scores in the body: undetermined outcome, rank unchanged (the rule/mode snapshot is still the entry's).
        Assert.Equal(beforeRank.GetProperty("battlePoint").GetInt32(), afterRank.GetProperty("battlePoint").GetInt32());
        using (var replay = await PostJsonAsync(client, player, "/battle/result", new { battleEntryId = rankedEntryId }))
        {
            Assert.Equal(ranked.RootElement.GetRawText(), replay.RootElement.GetRawText());
            Assert.Equal(afterRank.GetProperty("battlePoint").GetInt32(),
                OtomoTestEnvironment.Social.PointsOf(account, "crystal_scramble"));
        }

        var flagRankedEntryId = await Enter(player, RankedModeRotation.FlagRuleId);
        var flagRankedEntry = factory.Services.GetRequiredService<BattleMatchmakingService>()
            .GetBattleResultEntry(flagRankedEntryId);
        Assert.NotNull(flagRankedEntry);
        Assert.Equal(2, flagRankedEntry!.MatchType);
        Assert.Equal(2, flagRankedEntry.BattleRuleType);
        clock.SetUtcNow(DateTimeOffset.UnixEpoch.AddHours(2));
        Assert.Equal(RankedModeRotation.RapidBallRuleId, RankedModeRotation.RuleIdAt(clock.GetUtcNow()));
        using var flagRanked = await PostJsonAsync(client, player, "/battle/result", new { battleEntryId = flagRankedEntryId });
        var beforeFlagRank = flagRanked.RootElement.GetProperty("beforeUserBattleRank");
        var afterFlagRank = flagRanked.RootElement.GetProperty("userBattleRank");
        Assert.Equal(2, afterFlagRank.GetProperty("battleRuleType").GetInt32());
        Assert.Equal(beforeFlagRank.GetProperty("battlePoint").GetInt32(), afterFlagRank.GetProperty("battlePoint").GetInt32());
    }

    [Fact]
    public async Task Unrecognized_battle_entry_id_does_not_move_any_rank()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseContentRoot(AppContext.BaseDirectory));
        var client = factory.CreateClient();
        var sessionKey = Encoding.ASCII.GetBytes(SessionKey);
        var commonCode = Encoding.ASCII.GetBytes(CommonCode);
        var authPayload = JsonSerializer.Serialize(new { hash = SessionKey, uuid = Guid.NewGuid().ToString("N") });
        using var authRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/index")
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(authPayload), commonCode, new byte[16]))
        };
        authRequest.Headers.Host = Host;
        using var authResponse = await client.SendAsync(authRequest);
        Assert.Equal(HttpStatusCode.OK, authResponse.StatusCode);
        var accessToken = authResponse.Headers.GetValues("x-app-access-token").Single();

        var payload = JsonSerializer.Serialize(new { battleEntryId = "be-not-issued" });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/battle/result")
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(payload), sessionKey, new byte[16]))
        };
        request.Headers.Host = Host;
        request.Headers.Add("x-app-access-token", accessToken);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0", response.Headers.GetValues("x-app-status-code").Single());

        using var body = JsonDocument.Parse(D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), sessionKey));
        Assert.Equal(
            body.RootElement.GetProperty("beforeUserBattleRank").GetProperty("battlePoint").GetInt32(),
            body.RootElement.GetProperty("userBattleRank").GetProperty("battlePoint").GetInt32());
        Assert.Equal(
            body.RootElement.GetProperty("beforeUserBattleRank").GetProperty("rank").GetInt32(),
            body.RootElement.GetProperty("userBattleRank").GetProperty("rank").GetInt32());
    }

    private sealed record ResultSession(string AccessToken, byte[] SessionKey, long PlayerId);

    private static async Task<ResultSession> CreateResultSessionAsync(HttpClient client)
    {
        var sessionKey = Encoding.ASCII.GetBytes(SessionKey);
        var commonCode = Encoding.ASCII.GetBytes(CommonCode);
        var authPayload = JsonSerializer.Serialize(new { hash = SessionKey, uuid = Guid.NewGuid().ToString("N") });
        using var authRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/index")
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(authPayload), commonCode, new byte[16]))
        };
        authRequest.Headers.Host = Host;
        using var authResponse = await client.SendAsync(authRequest);
        Assert.Equal(HttpStatusCode.OK, authResponse.StatusCode);
        var playerId = long.Parse(authResponse.Headers.GetValues("x-app-user-id").Single(), CultureInfo.InvariantCulture);
        return new ResultSession(authResponse.Headers.GetValues("x-app-access-token").Single(), sessionKey, playerId);
    }

    private static async Task<JsonDocument> PostJsonAsync(
        HttpClient client, ResultSession session, string path, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(json), session.SessionKey, new byte[16]))
        };
        request.Headers.Host = Host;
        request.Headers.Add("x-app-access-token", session.AccessToken);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0", response.Headers.GetValues("x-app-status-code").Single());
        return JsonDocument.Parse(D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), session.SessionKey));
    }

    private static bool IsInScheduleWindow(JsonElement row, TimeSpan time)
    {
        var start = TimeSpan.Parse(row.GetProperty("startTime").GetString()!);
        var end = TimeSpan.Parse(row.GetProperty("endTime").GetString()!);
        return end > start ? time >= start && time < end : time >= start || time < end;
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void SetUtcNow(DateTimeOffset value) => _now = value;
    }

    private sealed class FinalAssignmentWriter : IServerStreamWriter<GetAssignmentsResponse>
    {
        private readonly TaskCompletionSource<string> _finalBattleId = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WriteOptions? WriteOptions { get; set; }
        public Task<string> FinalBattleId => _finalBattleId.Task;

        public Task WriteAsync(GetAssignmentsResponse message)
        {
            if (!string.IsNullOrWhiteSpace(message.Assignment.Connection))
                _finalBattleId.TrySetResult(message.Assignment.Connection);
            return Task.CompletedTask;
        }
    }
}
