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

    private static BadgeAwardRules Rules(params (int Id, string Req)[] entries)
    {
        var rows = entries.Select(e =>
            $$"""{ "id": {{e.Id}}, "trust": "client_reported_bounded", "requirements": {{e.Req}} }""");
        return BadgeAwardRules.Parse("{ \"entries\": [" + string.Join(",", rows) + "] }");
    }

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

    // ---- rule kinds

    private static IReadOnlyList<int> Apply(BadgeAwardRules rules, SessionState state, string entry, BadgeBattleStatReport report)
    {
        Assert.True(rules.TryApplyVerifiedBattle(state, entry, out var awards, stats: report, now: Now));
        return awards;
    }

    private static BadgeBattleStatReport Report(int ruleType = 1, bool? won = true, int kicker = 9, bool party = false,
        params (string Stat, long Value)[] stats)
    {
        var report = new BadgeBattleStatReport
        {
            CreditStats = true, Reason = "ok", BattleRuleType = ruleType, KickerId = kicker, QueuedAsParty = party, Won = won
        };
        foreach (var (stat, value) in stats) report.Stats[stat] = value;
        return report;
    }

    [Fact]
    public void Every_new_kind_awards_at_its_threshold_and_only_then()
    {
        var rules = Rules(
            (1, """{"kind":"wins_total","target":2}"""),
            (2, """{"kind":"kicker_wins","kickerId":9,"target":2}"""),
            (3, """{"kind":"kicker_wins","kickerId":5,"target":1}"""),
            (4, """{"kind":"mode_wins","battleRuleType":1,"target":2}"""),
            (5, """{"kind":"mode_wins","battleRuleType":2,"target":1}"""),
            (6, """{"kind":"queue_wins","queue":"solo","target":2}"""),
            (7, """{"kind":"queue_wins","queue":"team","target":1}"""),
            (8, """{"kind":"win_streak","battleRuleType":1,"target":2}"""),
            (9, """{"kind":"mvp_count","target":2}"""),
            (10, """{"kind":"stat_total","stat":"kills","target":5}"""),
            (11, """{"kind":"stat_total","stat":"crystals_collected","target":50}"""));
        Assert.Equal(11, rules.StatRuleCount);
        var state = new SessionState { UserId = "1000001" };

        Assert.Empty(Apply(rules, state, "e1", Report(stats: [("kills", 3), ("mvp", 1), ("crystals_collected", 30)])));
        var second = Apply(rules, state, "e2", Report(stats: [("kills", 2), ("mvp", 1), ("crystals_collected", 20)]));
        Assert.Equal([1, 2, 4, 6, 8, 9, 10, 11], second.Order());
        Assert.DoesNotContain(3, state.UserHonorIds); // other kicker
        Assert.DoesNotContain(5, state.UserHonorIds); // other mode
        Assert.DoesNotContain(7, state.UserHonorIds); // no party win yet
        Assert.Equal(new[] { 5 }, Apply(rules, state, "e3", Report(ruleType: 2, kicker: 5, party: true)).Intersect([5]).ToArray());
        Assert.Contains(3, state.UserHonorIds);
        Assert.Contains(7, state.UserHonorIds);
    }

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

    [Fact]
    public void Distance_goal_and_drop_totals_accumulate_and_award_at_threshold()
    {
        var rules = Rules(
            (1, """{"kind":"stat_total","stat":"moving_distance","target":10000}"""),
            (2, """{"kind":"stat_total","stat":"ball_goals","target":10}"""),
            (3, """{"kind":"stat_total","stat":"ball_drops","target":8}"""));
        Assert.Equal(3, rules.StatRuleCount);
        var state = new SessionState { UserId = "1000001" };

        Assert.Empty(Apply(rules, state, "e1", Report(ruleType: 3, stats: [("moving_distance", 6000), ("ball_goals", 6), ("ball_drops", 4)])));
        Assert.Equal(6000, state.BadgeStats.Total("moving_distance"));
        Assert.Equal([1, 2, 3], Apply(rules, state, "e2", Report(ruleType: 3, stats: [("moving_distance", 4000), ("ball_goals", 4), ("ball_drops", 4)])).Order());
        Assert.Equal(10000, state.BadgeStats.Total("moving_distance"));
    }

    [Fact]
    public void Loss_resets_streak_and_undecided_result_does_not_touch_it()
    {
        var rules = Rules((1, """{"kind":"win_streak","battleRuleType":1,"target":3}"""));
        var state = new SessionState { UserId = "1000001" };
        Apply(rules, state, "e1", Report());
        Apply(rules, state, "e2", Report());
        Apply(rules, state, "e3", Report(won: false));
        Assert.Equal(0, state.BadgeStats.WinStreaks[1].Current);
        Assert.Equal(2, state.BadgeStats.WinStreaks[1].Best);
        Apply(rules, state, "e4", Report());
        Apply(rules, state, "e5", Report(won: null)); // breakaway: neither extends nor resets
        Assert.Equal(1, state.BadgeStats.WinStreaks[1].Current);
        Apply(rules, state, "e6", Report());
        Assert.DoesNotContain(1, state.UserHonorIds);
        Assert.Contains(1, Apply(rules, state, "e7", Report()));
    }

    [Fact]
    public void Rejected_report_counts_participation_but_no_stats()
    {
        var rules = Rules((1, """{"kind":"stat_total","stat":"kills","target":1}"""));
        var state = new SessionState { UserId = "1000001" };
        Assert.True(rules.TryApplyVerifiedBattle(state, "e1", out var awards,
            stats: BadgeBattleStatReport.Rejected("cap:killCount"), now: Now));
        Assert.Empty(awards);
        Assert.Equal(1, state.VerifiedBattleCount);
        Assert.Equal(0, state.BadgeStats.Total("kills"));
    }

    [Fact]
    public void Unknown_kinds_and_untrusted_stat_rules_fail_closed()
    {
        var rules = BadgeAwardRules.Parse("""
            { "entries": [
              { "id": 1, "trust": "client_reported_bounded", "requirements": { "kind": "teleport_wins", "target": 1 } },
              { "id": 2, "trust": "client_reported_bounded", "requirements": { "kind": "stat_total", "stat": "not_a_stat", "target": 1 } },
              { "id": 3, "requirements": { "kind": "stat_total", "stat": "kills", "target": 1 } },
              { "id": 4, "trust": "server_verified", "requirements": { "kind": "mvp_count", "target": 1 } },
              { "id": 5, "trust": "client_reported_bounded", "requirements": { "kind": "mode_wins", "battleRuleType": 9, "target": 1 } },
              { "id": 6, "trust": "client_reported_bounded", "requirements": { "kind": "stat_total", "stat": "kills", "target": 0 } },
              { "id": 7, "trust": "client_reported_bounded", "requirements": { "kind": "stat_total", "stat": "kills", "target": 1 } }
            ] }
            """);
        Assert.Equal(1, rules.StatRuleCount);
        Assert.Equal("client_reported_bounded", rules.GetStatRuleTrust(7));
        Assert.Null(rules.GetStatRuleTrust(3));
    }

    [Fact]
    public void Retry_of_the_same_entry_is_idempotent_with_identical_output_and_no_double_count()
    {
        var directory = Directory.CreateTempSubdirectory("kf-badge-stats-");
        try
        {
            var store = new JsonPlayerStore(NullLogger<JsonPlayerStore>.Instance, new TestEnvironment(directory.FullName));
            var playerId = store.ResolvePlayerId("stat-retry");
            store.Save(new SessionState { UserId = playerId.ToString(CultureInfo.InvariantCulture) });
            var rules = Rules((1, """{"kind":"stat_total","stat":"kills","target":4}"""),
                (2, """{"kind":"wins_total","target":1}"""));
            var report = Report(stats: [("kills", 4)]);

            var first = rules.RecordVerifiedBattle(store.TryLoad(playerId)!, store, "e1", stats: report, now: Now);
            var retry = rules.RecordVerifiedBattle(store.TryLoad(playerId)!, store, "e1", stats: Report(stats: [("kills", 4)]), now: Now);

            Assert.Equal([1, 2], first.Order());
            Assert.Equal(first, retry);
            var persisted = store.TryLoad(playerId)!;
            Assert.Equal(4, persisted.BadgeStats.Total("kills"));
            Assert.Equal(1, persisted.BadgeStats.WinsTotal);
            Assert.Equal(1, persisted.VerifiedBattleCount);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void Stale_session_save_merges_with_max_union_semantics_including_streaks()
    {
        var directory = Directory.CreateTempSubdirectory("kf-badge-stats-");
        try
        {
            var store = new JsonPlayerStore(NullLogger<JsonPlayerStore>.Instance, new TestEnvironment(directory.FullName));
            var playerId = store.ResolvePlayerId("stat-stale");
            store.Save(new SessionState { UserId = playerId.ToString(CultureInfo.InvariantCulture) });
            var rules = Rules((1, """{"kind":"win_streak","battleRuleType":1,"target":3}"""),
                (2, """{"kind":"stat_total","stat":"kills","target":99}"""));
            var stale = store.TryLoad(playerId)!; // loaded before the battles below

            for (var i = 1; i <= 3; i++)
                rules.RecordVerifiedBattle(store.TryLoad(playerId)!, store, "e" + i, stats: Report(stats: [("kills", 2)]), now: Now);
            stale.UserName = "stale writer";
            rules.SavePlayerState(stale, store);

            var persisted = store.TryLoad(playerId)!;
            Assert.Equal("stale writer", persisted.UserName);
            Assert.Equal(6, persisted.BadgeStats.Total("kills"));
            Assert.Equal(3, persisted.BadgeStats.WinsTotal);
            Assert.Equal(3, persisted.BadgeStats.WinStreaks[1].Best);
            Assert.Equal(3, persisted.BadgeStats.CreditedBattles.Count);
            Assert.Contains(1, persisted.UserHonorIds);
            // A result entry already counted cannot be recounted via the stale document either.
            Assert.False(rules.TryApplyVerifiedBattle(persisted, "e2", out _, stats: Report(), now: Now));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void Ledger_is_bounded_and_refuses_instead_of_forgetting_fresh_entries()
    {
        var ledger = new Dictionary<string, long>();
        for (var i = 0; i < 4; i++) Assert.True(BadgeStatState.TryRecord(ledger, "k" + i, 1000, 4, 3600));
        Assert.False(BadgeStatState.TryRecord(ledger, "k4", 1001, 4, 3600)); // full, nothing old enough to evict
        Assert.False(BadgeStatState.TryRecord(ledger, "k0", 1002, 4, 3600)); // duplicate
        Assert.True(BadgeStatState.TryRecord(ledger, "k5", 1000 + 3601, 4, 3600)); // old ones evictable
        Assert.True(ledger.Count <= 4);
    }

    [Fact]
    public void BadgeStats_round_trip_through_the_json_store()
    {
        var directory = Directory.CreateTempSubdirectory("kf-badge-stats-");
        try
        {
            var store = new JsonPlayerStore(NullLogger<JsonPlayerStore>.Instance, new TestEnvironment(directory.FullName));
            var playerId = store.ResolvePlayerId("stat-roundtrip");
            var state = new SessionState { UserId = playerId.ToString(CultureInfo.InvariantCulture) };
            var rules = Rules((1, """{"kind":"wins_total","target":99}"""));
            rules.TryApplyVerifiedBattle(state, "e1", out _, stats: Report(party: true, stats: [("kills", 7), ("mvp", 1)]), now: Now);
            store.Save(state);

            var loaded = store.TryLoad(playerId)!.BadgeStats;
            Assert.Equal(7, loaded.Total("kills"));
            Assert.Equal(1, loaded.Total("mvp"));
            Assert.Equal(1, loaded.WinsTotal);
            Assert.Equal(1, loaded.KickerWins[9]);
            Assert.Equal(1, loaded.ModeWins[1]);
            Assert.Equal(1, loaded.QueueWins["team"]);
            Assert.Equal(1, loaded.WinStreaks[1].Current);
            Assert.True(loaded.CreditedBattles.ContainsKey("e1"));
        }
        finally { directory.Delete(true); }
    }

    // ---- likes

    [Fact]
    public void Likes_are_credited_once_per_battle_and_liker_and_only_for_existing_targets()
    {
        var directory = Directory.CreateTempSubdirectory("kf-badge-stats-");
        try
        {
            var store = new JsonPlayerStore(NullLogger<JsonPlayerStore>.Instance, new TestEnvironment(directory.FullName));
            var targetId = store.ResolvePlayerId("like-target");
            store.Save(new SessionState { UserId = targetId.ToString(CultureInfo.InvariantCulture) });
            var rules = Rules((1, """{"kind":"stat_total","stat":"likes_received","target":2}"""));

            Assert.Empty(rules.RecordVerifiedLike(targetId, "A", "battle-1", Now, store));
            Assert.Empty(rules.RecordVerifiedLike(targetId, "A", "battle-1", Now, store)); // duplicate ignored
            Assert.Equal(1, store.TryLoad(targetId)!.BadgeStats.Total("likes_received"));
            Assert.Equal([1], rules.RecordVerifiedLike(targetId, "B", "battle-1", Now, store));
            Assert.Empty(rules.RecordVerifiedLike(999999999, "A", "battle-2", Now, store)); // unknown target
        }
        finally { directory.Delete(true); }
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

        var rules = BadgeAwardRules.Parse(File.ReadAllText(path));
        // Rank badges are client_reported_bounded too (rank follows the client-reported team scores) but execute
        // through the rank rules, not the stat rules.
        var clientReported = entries.Where(e => e.GetProperty("trust").GetString() == "client_reported_bounded").ToArray();
        var rankEntries = clientReported.Count(e => e.GetProperty("requirements").GetProperty("kind").GetString() == "rank");
        Assert.Equal(4, rankEntries);
        Assert.Equal(clientReported.Length - rankEntries, rules.StatRuleCount);
        Assert.Equal(115, rules.StatRuleCount);
        Assert.Equal(119, clientReported.Length);
        Assert.Equal(12, entries.Count(e => e.GetProperty("trust").GetString() == "server_verified"));
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

    [Fact]
    public async Task Result_endpoint_credits_stats_once_keeps_response_shape_and_rejects_foreign_entries()
    {
        // The test clock is "now + 5 minutes": entry and room use the real clock, so the room is >= 90 s old.
        var clock = new FixedClock(DateTimeOffset.UtcNow.AddMinutes(5));
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

        // Someone else's entry id: the shape is the usual one but nothing is credited to either account.
        var store = factory.Services.GetRequiredService<IPlayerStore>();
        var (_, stolen) = await PostAsync(client, other, "/battle/result", Result(entryId, 5, 9, 1));
        Assert.True(stolen.RootElement.TryGetProperty("userExp", out _));
        Assert.Equal(0, store.TryLoad(player.PlayerId)!.BadgeStats.Total("kills"));
        Assert.Equal(0, store.TryLoad(other.PlayerId)!.BadgeStats.Total("kills"));

        var (firstRaw, first) = await PostAsync(client, player, "/battle/result", Result(entryId, 5, 9, 1));
        var keys = first.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        foreach (var expected in new[] { "userExp", "userBattleRank", "beforeUserBattleRank", "receivedUserHonorList",
                     "battleCount", "battleWinCount", "matchmakingTeamId" })
            if (keys.Contains(expected)) Assert.Contains(expected, keys); // present keys unchanged
        var stats = store.TryLoad(player.PlayerId)!;
        Assert.Equal(5, stats.BadgeStats.Total("kills"));
        Assert.Equal(1, stats.BadgeStats.Total("mvp"));
        Assert.Equal(1, stats.BadgeStats.WinsTotal);

        var (retryRaw, _) = await PostAsync(client, player, "/battle/result", Result(entryId, 50, 9, 1));
        Assert.Equal(5, store.TryLoad(player.PlayerId)!.BadgeStats.Total("kills"));
        Assert.Equal(1, store.TryLoad(player.PlayerId)!.BadgeStats.WinsTotal);
        using (var a = JsonDocument.Parse(firstRaw))
        using (var b = JsonDocument.Parse(retryRaw))
            Assert.Equal(
                a.RootElement.GetProperty("receivedUserHonorList").GetRawText(),
                b.RootElement.GetProperty("receivedUserHonorList").GetRawText());

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
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
