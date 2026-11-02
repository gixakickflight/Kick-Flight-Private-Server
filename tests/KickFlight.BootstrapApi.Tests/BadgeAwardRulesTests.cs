using System.Text.Json;
using KickFlight.BootstrapApi;
using KickFlight.BootstrapApi.PlayerStore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

public sealed class BadgeAwardRulesTests
{
    [Fact]
    public void Battle_count_award_is_persistent_and_idempotent_and_unsupported_rules_stay_locked()
    {
        var rules = BadgeAwardRules.Parse("""
            {
              "entries": [
                { "id": 6010002, "requirements": { "kind": "battle_count", "target": 2 } },
                { "id": 6010401, "requirements": { "kind": "rank", "rule": 1, "target": 7 } },
                { "id": 6010701, "requirements": { "kind": "rank", "rule": 2, "target": 13 } },
                { "id": 6010003, "requirements": { "kind": "win_streak", "target": 1 } },
                { "id": 6010004, "requirements": { "kind": "unsupported", "target": 1 } }
              ]
            }
            """);
        var state = new SessionState { UserId = "1000042" };

        Assert.True(rules.TryApplyVerifiedBattle(state, "battle-1", out var firstAwards, rankedBattleRuleType: 1, rankAfter: 6));
        Assert.Empty(firstAwards);
        Assert.True(rules.TryApplyVerifiedBattle(state, "battle-2", out var secondAwards, rankedBattleRuleType: 1, rankAfter: 7));
        Assert.Equal(new[] { 6010002, 6010401 }, secondAwards);

        // The account document is the persistence boundary used by both JSON and PostgreSQL stores.
        var restored = JsonSerializer.Deserialize<SessionState>(JsonSerializer.Serialize(state))!;
        Assert.Equal(2, restored.VerifiedBattleCount);
        Assert.Contains(6010002, restored.UserHonorIds);
        Assert.False(rules.TryApplyVerifiedBattle(restored, "battle-2", out var retryAwards));
        Assert.Empty(retryAwards);
        Assert.Equal(2, restored.VerifiedBattleCount);
        Assert.DoesNotContain(6010003, restored.UserHonorIds);
        Assert.DoesNotContain(6010004, restored.UserHonorIds);
        Assert.DoesNotContain(6010701, restored.UserHonorIds);
    }

    [Fact]
    public void Only_owned_honors_can_be_equipped_and_legacy_state_has_default_honor()
    {
        var legacy = JsonSerializer.Deserialize<SessionState>("""{"userId":"1000043"}""")!;

        Assert.Contains(BadgeAwardRules.DefaultHonorId, BadgeAwardRules.GetOwnedHonorIds(legacy));
        Assert.False(BadgeAwardRules.EquipHonor(legacy, 6019999));
        legacy.UserHonorIds.Add(6010101);
        Assert.True(BadgeAwardRules.EquipHonor(legacy, 6010101));
        Assert.Equal(6010101, legacy.HonorId);
    }

    [Fact]
    public void Login_days_count_distinct_utc_dates_and_award_only_at_threshold()
    {
        var rules = BadgeAwardRules.Parse("""
            { "entries": [
              { "id": 6010101, "requirements": { "kind": "login_days", "target": 2 } },
              { "id": 6010102, "requirements": { "kind": "unsupported", "target": 1 } }
            ] }
            """);
        var state = new SessionState { UserId = "1000044" };
        var firstDay = DateTimeOffset.Parse("2026-11-01T10:00:00-06:00");
        var sameUtcDay = DateTimeOffset.Parse("2026-11-02T01:00:00+09:00");

        Assert.True(rules.TryApplyVerifiedLoginDay(state, firstDay, out var firstAwards));
        Assert.Empty(firstAwards);
        Assert.False(rules.TryApplyVerifiedLoginDay(state, sameUtcDay, out var duplicateAwards));
        Assert.Empty(duplicateAwards);
        Assert.Single(state.VerifiedLoginDays);

        Assert.True(rules.TryApplyVerifiedLoginDay(state, firstDay.AddDays(1), out var secondAwards));
        Assert.Equal(new[] { 6010101 }, secondAwards);
        Assert.Contains(6010101, state.UserHonorIds);
        Assert.DoesNotContain(6010102, state.UserHonorIds);
    }

    [Fact]
    public void Repository_catalog_drives_the_supported_login_and_rank_awards()
    {
        var root = RepositoryPaths.FindRoot(Directory.GetCurrentDirectory());
        var rules = BadgeAwardRules.Load(root);
        var state = new SessionState { UserId = "1000045" };

        for (var day = 0; day < 29; day++)
        {
            Assert.True(rules.TryApplyVerifiedLoginDay(
                state,
                new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddDays(day),
                out var awards));
            Assert.Empty(awards);
        }
        Assert.True(rules.TryApplyVerifiedLoginDay(state, new DateTimeOffset(2026, 1, 30, 12, 0, 0, TimeSpan.Zero), out var loginAwards));
        Assert.Contains(6010101, loginAwards);

        Assert.True(rules.TryApplyVerifiedBattle(state, "scramble-s", out var scrambleSAwards, rankedBattleRuleType: 1, rankAfter: 7));
        Assert.Contains(6010401, scrambleSAwards);
        Assert.DoesNotContain(6010402, scrambleSAwards);
        Assert.True(rules.TryApplyVerifiedBattle(state, "flag-flight-s-plus-six", out var flagAwards, rankedBattleRuleType: 2, rankAfter: 13));
        Assert.Contains(6010701, flagAwards);
        Assert.DoesNotContain(6011101, flagAwards);

        var registrationState = new SessionState { UserId = "1000047" };
        Assert.Equal(new[] { 6010001 }, rules.ApplyRegistrationComplete(registrationState));
        Assert.Empty(rules.ApplyRegistrationComplete(registrationState));

        var discState = new SessionState { UserId = "1000048" };
        for (var upgrade = 0; upgrade < 29; upgrade++)
            Assert.Empty(rules.ApplyVerifiedDiscUpgrade(discState));
        Assert.Equal(new[] { 6012801 }, rules.ApplyVerifiedDiscUpgrade(discState));
    }

    [Fact]
    public void Disc_upgrade_badges_count_only_successful_level_increase_events()
    {
        var rules = BadgeAwardRules.Parse("""
            { "entries": [
              { "id": 6012801, "requirements": { "kind": "disc_upgrades", "target": 2 } },
              { "id": 6012802, "requirements": { "kind": "unsupported", "target": 1 } }
            ] }
            """);
        var state = new SessionState { UserId = "1000046" };

        Assert.Empty(rules.ApplyVerifiedDiscUpgrade(state));
        Assert.Equal(new[] { 6012801 }, rules.ApplyVerifiedDiscUpgrade(state));
        Assert.Equal(2, state.DiscUpgradeCount);
        Assert.Contains(6012801, state.UserHonorIds);
        Assert.DoesNotContain(6012802, state.UserHonorIds);
    }

    [Fact]
    public void Concurrent_battle_awards_and_stale_saves_preserve_badge_progress()
    {
        var directory = Directory.CreateTempSubdirectory("kf-badge-store-");
        var store = new JsonPlayerStore(NullLogger<JsonPlayerStore>.Instance, new TestEnvironment(directory.FullName));
        var playerId = store.ResolvePlayerId("badge-race-device");
        var initial = new SessionState { UserId = playerId.ToString() };
        store.Save(initial);
        var rules = BadgeAwardRules.Parse("""
            { "entries": [
              { "id": 6010002, "requirements": { "kind": "battle_count", "target": 1 } }
            ] }
            """);
        var stale = store.TryLoad(playerId)!;

        Parallel.For(0, 8, index =>
            rules.RecordVerifiedBattle(store.TryLoad(playerId)!, store, $"entry-{index}"));
        stale.UserName = "A separate stale session update";
        rules.SavePlayerState(stale, store);

        var persisted = store.TryLoad(playerId)!;
        Assert.Equal(8, persisted.VerifiedBattleCount);
        Assert.Equal(8, persisted.ProcessedBadgeBattleEntryIds.Count);
        Assert.Contains(6010002, persisted.UserHonorIds);
        Assert.Equal("A separate stale session update", persisted.UserName);
        directory.Delete(recursive: true);
    }

    private sealed class TestEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "badge-tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Test";
        public string WebRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
