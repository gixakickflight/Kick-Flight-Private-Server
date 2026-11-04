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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using OpenMatch;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

[Collection("Matchmaking static state")]
public sealed class BadgeBattleStatsTests
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string SessionKey = "0123456789abcdef0123456789abcdef";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";
    private static readonly DateTime Created = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset Now = new(Created.AddMinutes(5), TimeSpan.Zero);

    private static BattleMatchmakingService.BadgeBattleEntryContext Context(
        int ruleType = 1, bool party = false, int kicker = 9, int team = 0, string user = "1000001",
        DateTime? finalized = null, DateTime? created = null, int? matchType = 1) =>
        new(user, "be-1", kicker, 7, matchType, ruleType, party, created ?? Created, "battle-1", 7,
            finalized ?? Created.AddMinutes(1), team);

    private static string Body(object? extra = null, int callerTeamScore = 10, int otherTeamScore = 5, bool mvp = false,
        bool ai = false, bool breakaway = false, string entry = "be-1", string user = "1000001", int team = 0)
    {
        var dict = new Dictionary<string, object?>
        {
            ["battleEntryId"] = entry,
            ["teamScoreList"] = new[]
            {
                new { teamType = team, score = callerTeamScore },
                new { teamType = 1 - team, score = otherTeamScore }
            },
            ["userScoreList"] = new object[]
            {
                new { userId = long.Parse(user), score = 1, mvpFlag = mvp, aiFlag = ai, breakawayFlag = breakaway, badConnectionFlag = false },
                new { userId = 2000002L, score = 1, mvpFlag = false, aiFlag = false, breakawayFlag = false, badConnectionFlag = false }
            }
        };
        if (extra is IDictionary<string, object> map)
            foreach (var (key, value) in map) dict[key] = value;
        else if (extra is not null)
            foreach (var property in extra.GetType().GetProperties())
                dict[property.Name] = property.GetValue(extra);
        return JsonSerializer.Serialize(dict);
    }

    private static BadgeBattleStatReport Eval(string body, BattleMatchmakingService.BadgeBattleEntryContext? ctx = null) =>
        BadgeBattleStatEvaluator.Evaluate(body, ctx ?? Context(), Now);

    // ---- evaluator

    [Fact]
    public void Valid_report_is_credited_with_mode_gating_and_win_decision()
    {
        var crystal = Eval(Body(new { killCount = 3, killedCount = 1, acquiredCrystalCount = 40, flagGoalCount = 2 }, mvp: true));
        Assert.True(crystal.CreditStats);
        Assert.True(crystal.Won);
        Assert.Equal(3, crystal.Stats["kills"]);
        Assert.Equal(40, crystal.Stats["crystals_collected"]);
        Assert.Equal(1, crystal.Stats["mvp"]);
        Assert.DoesNotContain("flag_goals", crystal.Stats.Keys); // mode gating: goals only count in Flag Flight

        var flag = Eval(Body(new { acquiredCrystalCount = 40, flagGoalCount = 2, enemyFlagDropCount = 4 }), Context(ruleType: 2));
        Assert.Equal(2, flag.Stats["flag_goals"]);
        Assert.Equal(4, flag.Stats["flag_drops"]);
        Assert.DoesNotContain("crystals_collected", flag.Stats.Keys);
    }

    [Fact]
    public void Loss_and_tie_are_not_wins_and_own_team_is_taken_from_the_server_roster()
    {
        Assert.False(Eval(Body(callerTeamScore: 3, otherTeamScore: 9)).Won);
        Assert.False(Eval(Body(callerTeamScore: 5, otherTeamScore: 5)).Won);
        // Server says the caller is on team 1; the body's score for team 1 decides.
        Assert.True(Eval(Body(callerTeamScore: 9, otherTeamScore: 3, team: 1), Context(team: 1)).Won);
        Assert.False(Eval(Body(callerTeamScore: 9, otherTeamScore: 3, team: 0), Context(team: 1)).Won);
    }

    [Theory]
    [InlineData("killCount", 61, "cap:killCount")]
    [InlineData("killedCount", 61, "cap:killedCount")]
    [InlineData("acquiredCrystalCount", 501, "cap:acquiredCrystalCount")]
    [InlineData("releasedCrystalCount", 501, "cap:releasedCrystalCount")]
    [InlineData("guardianReleasedCrystalCount", 501, "cap:guardianReleasedCrystalCount")]
    [InlineData("flagGoalCount", 31, "cap:flagGoalCount")]
    [InlineData("enemyFlagDropCount", 101, "cap:enemyFlagDropCount")]
    [InlineData("killCount", -1, "negative:killCount")]
    public void A_field_above_its_cap_or_negative_voids_the_whole_stat_set(string field, int value, string reason)
    {
        var raw = Body(new Dictionary<string, object> { [field] = value, ["killCount"] = field == "killCount" ? value : 2 });
        var report = Eval(raw);
        Assert.False(report.CreditStats);
        Assert.Equal(reason, report.Reason);
        Assert.Empty(report.Stats);
    }

    [Fact]
    public void Non_integer_and_non_numeric_counters_are_rejected()
    {
        Assert.Equal("malformed:killCount", Eval(Body(new { killCount = 1.5 })).Reason);
        Assert.Equal("malformed:killCount", Eval(Body(new { killCount = "3" })).Reason);
    }

    [Fact]
    public void Structural_rules_reject_multiple_mvp_missing_or_ai_caller_and_foreign_entry()
    {
        var twoMvp = Body().Replace("\"mvpFlag\":false", "\"mvpFlag\":true");
        Assert.Equal("multiple_mvp", Eval(twoMvp).Reason);
        Assert.Equal("caller_missing", Eval(Body(user: "3000003")).Reason);
        Assert.Equal("caller_ai", Eval(Body(ai: true)).Reason);
        Assert.Equal("entry_mismatch", Eval(Body(entry: "be-other")).Reason);
        Assert.Equal("malformed_body", Eval("{not json").Reason);
        Assert.Equal("empty_body", Eval("").Reason);
        Assert.Equal("body_not_object", Eval("[1]").Reason);
        Assert.Equal("no_entry", BadgeBattleStatEvaluator.Evaluate(Body(), null, Now).Reason);
    }

    [Fact]
    public void Mode_room_and_timing_gates_apply()
    {
        Assert.Equal("mode_not_counted", Eval(Body(), Context(ruleType: 100)).Reason);
        Assert.Equal("no_room", Eval(Body(), Context() with { BattleId = null }).Reason);
        Assert.Equal("room_rule_mismatch", Eval(Body(), Context() with { RoomBattleRuleId = 8 }).Reason);
        Assert.Equal("not_in_roster", Eval(Body(), Context() with { OwnTeamType = null }).Reason);
        Assert.Equal("too_early", Eval(Body(), Context(finalized: Now.UtcDateTime.AddSeconds(-30))).Reason);
        Assert.Equal("entry_too_old", Eval(Body(), Context(created: Now.UtcDateTime.AddHours(-4))).Reason);
    }

    [Fact]
    public void Breakaway_caller_gets_stats_but_no_win_credit()
    {
        var report = Eval(Body(new { killCount = 2 }, breakaway: true));
        Assert.True(report.CreditStats);
        Assert.Null(report.Won);
        Assert.Equal(2, report.Stats["kills"]);
    }

    // ---- evaluator, continued

    [Fact]
    public void Rapid_ball_goals_and_drops_are_mode_gated_to_rule_type_3()
    {
        var rapid = Eval(Body(new { ballShootPoint = 7, ballDropCount = 4, killCount = 1 }), Context(ruleType: 3));
        Assert.True(rapid.CreditStats);
        Assert.Equal(7, rapid.Stats["ball_goals"]);
        Assert.Equal(4, rapid.Stats["ball_drops"]);

        foreach (var mode in new[] { 1, 2 })
        {
            var other = Eval(Body(new { ballShootPoint = 7, ballDropCount = 4, killCount = 1 }), Context(ruleType: mode));
            Assert.True(other.CreditStats);
            Assert.DoesNotContain("ball_goals", other.Stats.Keys);
            Assert.DoesNotContain("ball_drops", other.Stats.Keys);
            Assert.Equal(1, other.Stats["kills"]);
        }

        // Outside Rapid Ball the fields are not even read, so an absurd value cannot void the other stats.
        var ignored = Eval(Body(new { ballShootPoint = 9999, ballDropCount = 9999, killCount = 2 }), Context(ruleType: 1));
        Assert.True(ignored.CreditStats);
        Assert.Equal(2, ignored.Stats["kills"]);
    }

    [Theory]
    [InlineData("ballShootPoint", 101, "cap:ballShootPoint")]
    [InlineData("ballDropCount", 201, "cap:ballDropCount")]
    [InlineData("ballShootPoint", -1, "negative:ballShootPoint")]
    [InlineData("movingDistance", 30001, "cap:movingDistance")]
    [InlineData("movingDistance", -5, "negative:movingDistance")]
    public void Rapid_ball_and_distance_fields_above_cap_or_negative_void_the_stat_set(string field, int value, string reason)
    {
        var report = Eval(Body(new Dictionary<string, object> { [field] = value, ["killCount"] = 2 }), Context(ruleType: 3));
        Assert.False(report.CreditStats);
        Assert.Equal(reason, report.Reason);
        Assert.Empty(report.Stats);
        Assert.True(Eval(Body(new Dictionary<string, object> { ["ballShootPoint"] = 100, ["ballDropCount"] = 200, ["movingDistance"] = 30000 }),
            Context(ruleType: 3)).CreditStats); // exactly at the caps is accepted
    }

    [Fact]
    public void Moving_distance_is_metres_accepts_float_floors_and_counts_in_every_mode()
    {
        foreach (var mode in new[] { 1, 2, 3 })
            Assert.Equal(1234, Eval(Body(new { movingDistance = 1234 }), Context(ruleType: mode)).Stats["moving_distance"]);
        var fractional = Eval("{" + Body().TrimStart('{').TrimEnd('}') + ",\"movingDistance\":1234.9}");
        Assert.True(fractional.CreditStats);
        Assert.Equal(1234, fractional.Stats["moving_distance"]);
        Assert.DoesNotContain("moving_distance", Eval(Body(new { movingDistance = 0 })).Stats.Keys);
        Assert.Equal("malformed:movingDistance", Eval(Body(new { movingDistance = "5" })).Reason);
    }

    // ---- catalog integrity

    [Fact]
    public void Shipped_catalog_has_211_unique_entries_and_every_new_rule_is_labelled_and_consistent()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config", "badge_rule_catalog.json");
        if (!File.Exists(path)) return; // catalog is optional in checkouts that do not ship it
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var entries = document.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(211, entries.Select(e => e.GetProperty("id").GetInt32()).Distinct().Count());
        Assert.Equal(211, entries.Select(e => e.GetProperty("name").GetString()).Distinct().Count());
        foreach (var entry in entries)
        {
            foreach (var field in new[] { "name", "description", "ruleText", "displayName", "trust" })
                Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty(field).GetString()), field);
            var kind = entry.GetProperty("requirements").GetProperty("kind").GetString();
            var trust = entry.GetProperty("trust").GetString();
            if (kind == "unsupported") Assert.Equal("none", trust);
            if (trust == "client_reported_bounded") Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("executedRuleText").GetString()));
        }

        // Rank badges are client_reported_bounded too (rank follows the client-reported team scores).
        var clientReported = entries.Where(e => e.GetProperty("trust").GetString() == "client_reported_bounded").ToArray();
        Assert.Equal(4, clientReported.Count(e => e.GetProperty("requirements").GetProperty("kind").GetString() == "rank"));
        Assert.Equal(119, clientReported.Length);
        Assert.Equal(12, entries.Count(e => e.GetProperty("trust").GetString() == "server_verified"));

        // The tenant's documents express every executable kind: 130 rules, none unported, and the other 81 entries (the
        // default honor and 80 whose award trigger nobody could name) were never rules.
        var coverage = BadgeCatalog.Measure(File.ReadAllText(path));
        Assert.Equal(130, coverage.Translated);
        Assert.Empty(coverage.Unported);
        Assert.Equal(81, coverage.NotRules);
    }

    // ---- endpoint flow

    private sealed record Session(string Token, byte[] Key, long PlayerId);

    private static async Task<Session> AuthAsync(HttpClient client)
    {
        var commonCode = Encoding.ASCII.GetBytes(CommonCode);
        var payload = JsonSerializer.Serialize(new { hash = SessionKey, uuid = Guid.NewGuid().ToString("N") });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/index")
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(payload), commonCode, new byte[16]))
        };
        request.Headers.Host = Host;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new Session(response.Headers.GetValues("x-app-access-token").Single(), Encoding.ASCII.GetBytes(SessionKey),
            long.Parse(response.Headers.GetValues("x-app-user-id").Single(), CultureInfo.InvariantCulture));
    }

    private static async Task<(string Raw, JsonDocument Doc)> PostAsync(HttpClient client, Session session, string path, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(json), session.Key, new byte[16]))
        };
        request.Headers.Host = Host;
        request.Headers.Add("x-app-access-token", session.Token);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = Encoding.UTF8.GetString(D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), session.Key));
        return (text, JsonDocument.Parse(text));
    }

    // The battle is one declared fact per verified entry and player, keyed by the entry. The adapter counts nothing: what the
    // fact is worth is the tenant's tracks, and a retry of the same result (with different numbers, as a forged retry would
    // be) is economy's replay of the first report.
    [Fact]
    public async Task Result_endpoint_reports_one_battle_fact_per_entry_keeps_response_shape_and_ignores_foreign_entries()
    {
        await ResultEndpointReportsOneFactAsync();
    }

    private static async Task ResultEndpointReportsOneFactAsync()
    {
        // The test clock is "now + 5 minutes": entry and room use the real clock, so the room is >= 90 s old.
        var clock = new FixedClock(DateTimeOffset.UtcNow.AddMinutes(5));
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
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
        var client = factory.CreateClient();
        var player = await AuthAsync(client);
        var other = await AuthAsync(client);
        var ruleId = RankedModeRotation.RuleIdAt(clock.GetUtcNow());

        var (_, entryDoc) = await PostAsync(client, player, "/battle/entry", JsonSerializer.Serialize(new { battleRuleId = ruleId }));
        var entryId = entryDoc.RootElement.GetProperty("battleEntryId").GetString()!;
        var ticketId = entryDoc.RootElement.GetProperty("battleEntryTicketId").GetString()!;
        var matchmaking = factory.Services.GetRequiredService<BattleMatchmakingService>();
        matchmaking.MatchWindow = TimeSpan.FromMilliseconds(1);
        matchmaking.JoinIncrementBaseSeconds = 0;
        using var cancel = new CancellationTokenSource();
        var writer = new Writer();
        var task = matchmaking.StreamAssignmentsAsync(ticketId, writer, cancel.Token);
        var battleId = await writer.Final.WaitAsync(TimeSpan.FromSeconds(5));
        var context = matchmaking.GetBadgeBattleContext(entryId)!;
        Assert.NotNull(context.OwnTeamType);
        Assert.Equal(battleId, context.BattleId);

        string Result(string entry, int kills, int ownScore, int otherScore) => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["battleEntryId"] = entry,
            ["killCount"] = kills,
            ["teamScoreList"] = new[]
            {
                new { teamType = context.OwnTeamType!.Value, score = ownScore },
                new { teamType = 1 - context.OwnTeamType!.Value, score = otherScore }
            },
            ["userScoreList"] = new[]
            {
                new { userId = player.PlayerId, score = 1, mvpFlag = true, aiFlag = false, breakawayFlag = false, badConnectionFlag = false }
            }
        });

        var store = factory.Services.GetRequiredService<IPlayerStore>();
        var playerAccount = store.FindOtomoPlayer(player.PlayerId)!.OtomoPlayerId;
        var otherAccount = store.FindOtomoPlayer(other.PlayerId)!.OtomoPlayerId;
        var economy = OtomoTestEnvironment.Economy;
        var key = $"badge-battle:{entryId}";

        // Someone else's entry id: the shape is the usual one but nothing is reported for either account.
        var (_, stolen) = await PostAsync(client, other, "/battle/result", Result(entryId, 5, 9, 1));
        Assert.True(stolen.RootElement.TryGetProperty("userExp", out _));
        Assert.DoesNotContain(economy.FactReports, fact => fact.Key == key);

        // This battle crosses a step of an auto_claim badge track: economy pays it inside the report and says so.
        economy.QueueAutoClaim(playerAccount, "battle.result", "badge.kicker_wins.1", 0, 3, "badge.6011501");
        var (firstRaw, first) = await PostAsync(client, player, "/battle/result", Result(entryId, 5, 9, 1));
        var keys = first.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        foreach (var expected in new[] { "userExp", "userBattleRank", "beforeUserBattleRank", "receivedUserHonorList",
                     "battleCount", "battleWinCount", "matchmakingTeamId" })
            Assert.Contains(expected, keys);

        var received = Assert.Single(first.RootElement.GetProperty("receivedUserHonorList").EnumerateArray());
        Assert.Equal(6011501, received.GetProperty("honorId").GetInt32());
        Assert.True(received.GetProperty("newFlag").GetBoolean());
        Assert.Equal(1, economy.ItemsOf(playerAccount, "honor:6011501"));

        // One fact for the verified battle, under the entry, with the fields the tenant declares and the numbers the
        // evaluator bounded.
        var reported = Assert.Single(economy.FactReports, fact => fact.Key == key);
        Assert.Equal(playerAccount, reported.PlayerId);
        Assert.Equal("battle.result", reported.Fact);
        Assert.False(reported.Replayed);
        var fields = reported.Fields;
        Assert.Equal(FakeOtomoEconomy.FactSchemas["battle.result"].Keys.Order().ToArray(),
            fields.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.True(fields.GetProperty("credited").GetBoolean());
        Assert.True(fields.GetProperty("won").GetBoolean());
        Assert.False(fields.GetProperty("flagged").GetBoolean());
        Assert.Equal("win", fields.GetProperty("outcome").GetString());
        Assert.Equal("solo", fields.GetProperty("queue").GetString());
        Assert.Equal(5, fields.GetProperty("kills").GetInt64());
        Assert.True(fields.GetProperty("mvp").GetBoolean());
        Assert.Equal(context.BattleRuleType, fields.GetProperty("rule_type").GetInt32());
        Assert.Equal(context.KickerId, fields.GetProperty("kicker_id").GetInt32());
        // A ranked battle carries the rank after it, which is what the rank badges are held to.
        Assert.Equal(context.BattleRuleType, fields.GetProperty("rank_rule").GetInt32());
        Assert.True(fields.GetProperty("rank_after").GetInt32() >= 0);
        Assert.DoesNotContain(economy.FactReports, fact => fact.PlayerId == otherAccount && fact.Key == key);

        // The win streak is the tenant's `streak` measure over this same fact: the adapter reads nothing back, sets nothing
        // and claims nothing.
        Assert.DoesNotContain(economy.Requests, call => call.Path.EndsWith("/progress/gauge", StringComparison.Ordinal)
            || call.Path.EndsWith("/tasks/claim", StringComparison.Ordinal));
        Assert.DoesNotContain(economy.Requests, call => call.Path == "/api/player/economy/tasks" && call.Method == "GET");

        // The client retries with different numbers: economy answers its first report, nothing is counted twice.
        var (retryRaw, _) = await PostAsync(client, player, "/battle/result", Result(entryId, 50, 9, 1));
        Assert.Equal(5, Assert.Single(economy.FactReports, fact => fact.Key == key && !fact.Replayed)
            .Fields.GetProperty("kills").GetInt64());
        Assert.Contains(economy.FactReports, fact => fact.Key == key && fact.Replayed);
        using (var a = JsonDocument.Parse(firstRaw))
        using (var b = JsonDocument.Parse(retryRaw))
            Assert.Equal(
                a.RootElement.GetProperty("receivedUserHonorList").GetRawText(),
                b.RootElement.GetProperty("receivedUserHonorList").GetRawText());

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    // A like is a fact about the player who received it, reported under their account and keyed by the (battle, liker)
    // pair, so a second tap is economy's replay. Only players of the same finalized room can like one another.
    [Fact]
    public async Task A_like_is_reported_as_a_fact_for_the_target_once_per_liker_and_only_inside_the_room()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow.AddMinutes(5));
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
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
        var client = factory.CreateClient();
        var liker = await AuthAsync(client);
        var target = await AuthAsync(client);
        var outsider = await AuthAsync(client);
        var ruleId = RankedModeRotation.RuleIdAt(clock.GetUtcNow());
        var matchmaking = factory.Services.GetRequiredService<BattleMatchmakingService>();
        matchmaking.MatchWindow = TimeSpan.FromMilliseconds(600);
        matchmaking.JoinIncrementBaseSeconds = 0;

        using var cancel = new CancellationTokenSource();
        var streams = new List<Task>();
        var writers = new List<Writer>();
        foreach (var player in new[] { liker, target })
        {
            var (_, entry) = await PostAsync(client, player, "/battle/entry", JsonSerializer.Serialize(new { battleRuleId = ruleId }));
            var writer = new Writer();
            writers.Add(writer);
            streams.Add(matchmaking.StreamAssignmentsAsync(entry.RootElement.GetProperty("battleEntryTicketId").GetString()!, writer, cancel.Token));
        }
        var battleIds = await Task.WhenAll(writers.Select(writer => writer.Final.WaitAsync(TimeSpan.FromSeconds(10))));
        Assert.Equal(battleIds[0], battleIds[1]); // one room, two humans
        var battleId = battleIds[0];

        var store = factory.Services.GetRequiredService<IPlayerStore>();
        var targetAccount = store.FindOtomoPlayer(target.PlayerId)!.OtomoPlayerId;
        var economy = OtomoTestEnvironment.Economy;
        var key = $"badge-like:{battleId}:{liker.PlayerId}";

        string Like(params long[] ids) => JsonSerializer.Serialize(new { battleId, userIdList = ids });
        await PostAsync(client, liker, "/battle/good", Like(target.PlayerId, outsider.PlayerId, liker.PlayerId));
        await PostAsync(client, liker, "/battle/good", Like(target.PlayerId));

        var reports = economy.FactReports.Where(fact => fact.Fact == "like.received" && fact.Key == key).ToArray();
        Assert.Equal(2, reports.Length);
        Assert.All(reports, report => Assert.Equal(targetAccount, report.PlayerId));
        Assert.Equal(battleId, reports[0].Fields.GetProperty("battle").GetString());
        Assert.False(reports[0].Replayed);
        Assert.True(reports[1].Replayed);
        // Not the outsider (not in the room) and not the liker themselves.
        Assert.DoesNotContain(economy.FactReports, fact => fact.Fact == "like.received" && fact.PlayerId != targetAccount
            && fact.Key.StartsWith($"badge-like:{battleId}", StringComparison.Ordinal));

        cancel.Cancel();
        foreach (var stream in streams) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream);
    }

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

    private sealed class TestEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "badge-stat-tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Test";
        public string WebRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}

public class BattleResultLogSummaryTests
{
    [Fact]
    public void Describe_ReportsRawValueAndJsonTypeWithoutOtherFields()
    {
        var s = BattleResultLogSummary.Describe("{\"movingDistance\":1234.5,\"ballShootPoint\":2,\"killCount\":\"x\",\"token\":\"secret\"}", 2);
        Assert.Contains("movingDistance=1234.5(Number)", s);
        Assert.Contains("ballShootPoint=2(Number)", s);
        Assert.Contains("ballDropCount=absent", s);
        Assert.Contains("killCount=<non-numeric>", s);
        Assert.DoesNotContain("secret", s);
        Assert.Contains("body=unparseable", BattleResultLogSummary.Describe("not json", 1));
        Assert.Equal(8, BattleResultLogSummary.ShortHash("u1").Length);
    }
}
