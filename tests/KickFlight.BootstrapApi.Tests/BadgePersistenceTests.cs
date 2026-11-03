using KickFlight.BootstrapApi.PlayerStore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using Xunit.Sdk;

namespace KickFlight.BootstrapApi.Tests;

// The same scenarios run against the JSON store (always) and PostgreSQL (when KF_TEST_POSTGRES_ADMIN_CONNECTION_STRING
// names a server where the test user may create and drop databases; every test uses its own throwaway database).
// Two store instances stand in for two API processes: separate connection pools / separate objects.
public sealed class BadgePersistenceTests
{
    private const string RulesJson = """
        { "entries": [
          { "id": 6010002, "requirements": { "kind": "battle_count", "target": 3 } },
          { "id": 6010101, "requirements": { "kind": "login_days", "target": 2 } },
          { "id": 6012801, "requirements": { "kind": "disc_upgrades", "target": 5 } },
          { "id": 6010501, "trust": "client_reported_bounded", "requirements": { "kind": "wins_total", "target": 5 } },
          { "id": 6010502, "trust": "client_reported_bounded", "requirements": { "kind": "stat_total", "stat": "likes_received", "target": 4 } },
          { "id": 6010503, "trust": "client_reported_bounded", "requirements": { "kind": "win_streak", "battleRuleType": 1, "target": 2 } }
        ] }
        """;

    private static BadgeAwardRules Rules() => BadgeAwardRules.Parse(RulesJson);

    private static BadgeBattleStatReport Win() => new()
    {
        CreditStats = true, Reason = "ok", BattleRuleType = 1, KickerId = 1, Won = true,
        Stats = { [BadgeStatNames.Kills] = 2 }
    };

    private sealed record Harness(Func<IPlayerStore> NewStore, long PlayerId, long OtherPlayerId);

    private static void RunJson(Action<Harness> scenario)
    {
        var directory = Directory.CreateTempSubdirectory("kf-badge-persist-");
        try
        {
            IPlayerStore NewStore() => new JsonPlayerStore(NullLogger<JsonPlayerStore>.Instance, new TestEnvironment(directory.FullName));
            var setup = NewStore();
            var player = setup.ResolvePlayerId("persist-a");
            var other = setup.ResolvePlayerId("persist-b");
            setup.Save(new SessionState { UserId = player.ToString() });
            setup.Save(new SessionState { UserId = other.ToString() });
            scenario(new Harness(NewStore, player, other));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void RunPostgres(Action<Harness, string> scenario)
    {
        using var database = TempDatabase.Create();
        IPlayerStore NewStore() => new PostgresPlayerStore(NullLogger<PostgresPlayerStore>.Instance, database.ConnectionString);
        var setup = NewStore();
        var player = setup.ResolvePlayerId("persist-a");
        var other = setup.ResolvePlayerId("persist-b");
        setup.Save(new SessionState { UserId = player.ToString() });
        setup.Save(new SessionState { UserId = other.ToString() });
        scenario(new Harness(NewStore, player, other), database.ConnectionString);
    }

    [Fact] public void Json_battle_retry_is_idempotent_and_round_trips() => RunJson(RetryIsIdempotent);
    [Fact] public void Json_parallel_distinct_entries_are_all_counted_once() => RunJson(ParallelDistinct);
    [Fact] public void Json_parallel_same_entry_is_counted_once() => RunJson(ParallelSameEntry);
    [Fact] public void Json_likes_credit_the_target_once_per_liker() => RunJson(Likes);
    [Fact] public void Json_disc_upgrades_and_login_days_are_atomic() => RunJson(UpgradesAndLoginDays);

    [PostgresBadgeFact] public void Pg_battle_retry_is_idempotent_and_round_trips() => RunPostgres((h, _) => RetryIsIdempotent(h));
    [PostgresBadgeFact] public void Pg_parallel_distinct_entries_are_all_counted_once() => RunPostgres((h, _) => ParallelDistinct(h));
    [PostgresBadgeFact] public void Pg_parallel_same_entry_is_counted_once() => RunPostgres((h, _) => ParallelSameEntry(h));
    [PostgresBadgeFact] public void Pg_likes_credit_the_target_once_per_liker() => RunPostgres((h, _) => Likes(h));
    [PostgresBadgeFact] public void Pg_disc_upgrades_and_login_days_are_atomic() => RunPostgres((h, _) => UpgradesAndLoginDays(h));

    [PostgresBadgeFact]
    public void Pg_stale_whole_document_save_never_lowers_counters_or_removes_honors()
    {
        RunPostgres((h, connectionString) =>
        {
            var store = h.NewStore();
            var rules = Rules();
            var stale = store.TryLoad(h.PlayerId)!;
            for (var i = 0; i < 5; i++)
                rules.RecordVerifiedBattle(new SessionState { UserId = h.PlayerId.ToString() }, h.NewStore(), $"e{i}", stats: Win());

            stale.UserName = "stale writer";
            store.Save(stale); // plain whole-document save of a document that predates the five battles

            var loaded = store.TryLoad(h.PlayerId)!;
            Assert.Equal("stale writer", loaded.UserName);
            Assert.Equal(5, loaded.VerifiedBattleCount);
            Assert.Equal(5, loaded.BadgeStats.WinsTotal);
            Assert.Equal(5, loaded.ProcessedBadgeBattleEntryIds.Count);
            Assert.Contains(6010002, loaded.UserHonorIds);
            Assert.Contains(6010501, loaded.UserHonorIds);
            Assert.Equal(5, loaded.BadgeStats.WinStreaks[1].Current);
            Assert.Equal(5, Scalar<long>(connectionString, $"select value from player_badge_counters where player_id = {h.PlayerId} and counter_key = 'verified_battles'"));

            // A stale document that also drops honors must not remove them, and a retry still finds its entry.
            stale.UserHonorIds = [BadgeAwardRules.DefaultHonorId];
            stale.VerifiedBattleCount = 0;
            store.Save(stale);
            loaded = store.TryLoad(h.PlayerId)!;
            Assert.Equal(5, loaded.VerifiedBattleCount);
            Assert.Contains(6010501, loaded.UserHonorIds);
            Assert.Equal(new[] { 6010002 }, rules.RecordVerifiedBattle(
                new SessionState { UserId = h.PlayerId.ToString() }, h.NewStore(), "e2", stats: Win()).Take(1));
        });
    }

    [PostgresBadgeFact]
    public void Pg_migration_v2_to_v3_keeps_legacy_badge_data_and_unrelated_tables_and_is_idempotent()
    {
        using var database = TempDatabase.Create();
        var cs = database.ConnectionString;
        var first = new PostgresPlayerStore(NullLogger<PostgresPlayerStore>.Instance, cs);
        var players = Enumerable.Range(0, 3).Select(i => first.ResolvePlayerId($"legacy-{i}")).ToArray();

        const string legacyDocument = """
            {"UserHonorIds":[6010000,6010002],"VerifiedBattleCount":7,"DiscUpgradeCount":3,
             "VerifiedLoginDays":["2026-01-01"],"ProcessedBadgeBattleEntryIds":["old-a","old-b"],
             "ProcessedBadgeBattleAwards":{"old-b":[6010002]},
             "BadgeStats":{"WinsTotal":4,"Totals":{"kills":9},"ModeWins":{"1":4},"KickerWins":{"1":4},"QueueWins":{"solo":4},
                           "WinStreaks":{"1":{"Battles":5,"Current":2,"Best":3}},"CreditedBattles":{"old-b":1790000000},"LikeLedger":{"b1|9":1790000001}}}
            """;
        foreach (var id in players.Take(2)) Exec(cs, $"update players set state = '{legacyDocument}'::jsonb where id = {id}");
        // Stand-in for any pre-existing unrelated history table: the migration must not touch it.
        Exec(cs, "create table badge_audit_log (id serial primary key, badge_id int, note text)");
        Exec(cs, "insert into badge_audit_log (badge_id, note) values (6010002, 'a'), (6010003, 'b')");

        // Make it a v2 database: no badge tables, no v3 row.
        Exec(cs, "drop table player_badge_ledger, player_badge_counters, player_badge_streaks, player_honors; delete from schema_meta where version = 3");
        Assert.Equal(2, Scalar<int>(cs, "select max(version) from schema_meta"));
        var documentsBefore = Rows(cs, "select id || ':' || state::text from players order by id");

        var migrated = new PostgresPlayerStore(NullLogger<PostgresPlayerStore>.Instance, cs);
        Assert.Equal(3, Scalar<int>(cs, "select max(version) from schema_meta"));
        Assert.Equal(documentsBefore, Rows(cs, "select id || ':' || state::text from players order by id"));
        Assert.Equal(new[] { "6010002:a", "6010003:b" }, Rows(cs, "select badge_id || ':' || note from badge_audit_log order by id"));

        var legacy = migrated.TryLoad(players[0])!;
        Assert.Equal(7, legacy.VerifiedBattleCount);
        Assert.Equal(3, legacy.DiscUpgradeCount);
        Assert.Equal(4, legacy.BadgeStats.WinsTotal);
        Assert.Equal(3, legacy.BadgeStats.WinStreaks[1].Best);
        Assert.Contains(6010002, legacy.UserHonorIds);
        Assert.Contains("old-a", legacy.ProcessedBadgeBattleEntryIds);

        // First write seeds the tables from the legacy document; the old entry is a retry, a new one counts.
        var rules = Rules();
        Assert.Equal(new[] { 6010002 }, rules.RecordVerifiedBattle(new SessionState { UserId = players[0].ToString() }, migrated, "old-b"));
        rules.RecordVerifiedBattle(new SessionState { UserId = players[0].ToString() }, migrated, "new-c", stats: Win());
        Assert.Equal(8, Scalar<int>(cs, $"select value from player_badge_counters where player_id = {players[0]} and counter_key = 'verified_battles'"));
        Assert.Equal(5, Scalar<int>(cs, $"select value from player_badge_counters where player_id = {players[0]} and counter_key = 'wins_total'"));
        Assert.Equal(3, Scalar<int>(cs, $"select count(*) from player_badge_ledger where player_id = {players[0]} and ledger_kind = 'battle'"));
        Assert.Equal(1, Scalar<int>(cs, $"select count(*) from player_honors where player_id = {players[0]} and honor_id = 6010002 and source = 'legacy'"));
        Assert.Contains("6010501", Rows(cs, $"select honor_id::text from player_honors where player_id = {players[0]}"));
        Assert.Equal(8, ((System.Text.Json.JsonElement)System.Text.Json.JsonDocument.Parse(
            Rows(cs, $"select state::text from players where id = {players[0]}")[0]).RootElement.GetProperty("VerifiedBattleCount")).GetInt32());

        // Re-running the migration (new store; and even with the version row missing) is a no-op for data.
        var appliedAt = Rows(cs, "select version || '@' || applied_at::text from schema_meta order by version");
        _ = new PostgresPlayerStore(NullLogger<PostgresPlayerStore>.Instance, cs);
        Assert.Equal(appliedAt, Rows(cs, "select version || '@' || applied_at::text from schema_meta order by version"));
        var tablesBefore = Rows(cs, "select 'c:' || player_id || counter_key || value from player_badge_counters union all select 'h:' || player_id || honor_id from player_honors union all select 'l:' || player_id || ledger_kind || entry_key from player_badge_ledger order by 1");
        Exec(cs, "delete from schema_meta where version = 3");
        _ = new PostgresPlayerStore(NullLogger<PostgresPlayerStore>.Instance, cs);
        Assert.Equal(tablesBefore, Rows(cs, "select 'c:' || player_id || counter_key || value from player_badge_counters union all select 'h:' || player_id || honor_id from player_honors union all select 'l:' || player_id || ledger_kind || entry_key from player_badge_ledger order by 1"));
        Assert.Equal(new[] { "6010002:a", "6010003:b" }, Rows(cs, "select badge_id || ':' || note from badge_audit_log order by id"));
        Assert.Equal(8, migrated.TryLoad(players[0])!.VerifiedBattleCount);
        // The untouched legacy player (never written since the migration) still reads its document.
        Assert.Equal(7, migrated.TryLoad(players[1])!.VerifiedBattleCount);
        Assert.Equal(0, migrated.TryLoad(players[2])!.VerifiedBattleCount);
    }

    // ---- shared scenarios ----

    private static void RetryIsIdempotent(Harness h)
    {
        var rules = Rules();
        var a = h.NewStore();
        var b = h.NewStore();
        var state = new SessionState { UserId = h.PlayerId.ToString() };
        Assert.Empty(rules.RecordVerifiedBattle(state, a, "r1", stats: Win()));
        Assert.Equal(new[] { 6010503 }, rules.RecordVerifiedBattle(state, b, "r2", stats: Win()));
        var third = rules.RecordVerifiedBattle(state, a, "r3", stats: Win());
        Assert.Equal(new[] { 6010002 }, third);
        // Retries (same or other instance) return the same awards and change nothing.
        Assert.Equal(third, rules.RecordVerifiedBattle(new SessionState { UserId = h.PlayerId.ToString() }, b, "r3", stats: Win()));
        Assert.Equal(third, rules.RecordVerifiedBattle(state, a, "r3", stats: Win()));

        var loaded = h.NewStore().TryLoad(h.PlayerId)!;
        Assert.Equal(3, loaded.VerifiedBattleCount);
        Assert.Equal(3, loaded.BadgeStats.WinsTotal);
        Assert.Equal(6, loaded.BadgeStats.Total(BadgeStatNames.Kills));
        Assert.Equal(3, loaded.BadgeStats.ModeWins[1]);
        Assert.Equal(3, loaded.BadgeStats.KickerWins[1]);
        Assert.Equal(3, loaded.BadgeStats.QueueWins["solo"]);
        Assert.Equal(new WinStreakState { Battles = 3, Current = 3, Best = 3 }.Best, loaded.BadgeStats.WinStreaks[1].Best);
        Assert.Equal(3, loaded.BadgeStats.WinStreaks[1].Battles);
        Assert.Contains(6010002, loaded.UserHonorIds);
        Assert.Contains(BadgeAwardRules.DefaultHonorId, loaded.UserHonorIds);
        Assert.Equal(3, loaded.ProcessedBadgeBattleEntryIds.Count);
        Assert.Equal(third, loaded.ProcessedBadgeBattleAwards["r3"]);
        Assert.Equal(3, state.VerifiedBattleCount); // the caller's copy was refreshed from the store
    }

    private static void ParallelDistinct(Harness h)
    {
        var rules = Rules();
        var stores = new[] { h.NewStore(), h.NewStore() };
        var awards = new System.Collections.Concurrent.ConcurrentBag<int>();
        Parallel.For(0, 20, new ParallelOptions { MaxDegreeOfParallelism = 8 }, index =>
        {
            foreach (var honor in rules.RecordVerifiedBattle(
                         new SessionState { UserId = h.PlayerId.ToString() }, stores[index % 2], $"entry-{index}", stats: Win()))
                awards.Add(honor);
        });

        var loaded = h.NewStore().TryLoad(h.PlayerId)!;
        Assert.Equal(20, loaded.VerifiedBattleCount);
        Assert.Equal(20, loaded.BadgeStats.WinsTotal);
        Assert.Equal(40, loaded.BadgeStats.Total(BadgeStatNames.Kills));
        Assert.Equal(20, loaded.BadgeStats.WinStreaks[1].Battles);
        Assert.Equal(20, loaded.BadgeStats.WinStreaks[1].Best);
        Assert.Equal(20, loaded.ProcessedBadgeBattleEntryIds.Count);
        // Each threshold honor was handed out exactly once, no matter which call crossed it.
        Assert.Equal(new[] { 6010002, 6010501, 6010503 }, awards.Order().ToArray());
    }

    private static void ParallelSameEntry(Harness h)
    {
        var rules = Rules();
        var stores = new[] { h.NewStore(), h.NewStore() };
        var results = new System.Collections.Concurrent.ConcurrentBag<int[]>();
        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 8 }, index =>
            results.Add(rules.RecordVerifiedBattle(
                new SessionState { UserId = h.PlayerId.ToString() }, stores[index % 2], "same-entry", stats: Win()).ToArray()));

        var loaded = h.NewStore().TryLoad(h.PlayerId)!;
        Assert.Equal(1, loaded.VerifiedBattleCount);
        Assert.Equal(1, loaded.BadgeStats.WinsTotal);
        Assert.Equal(2, loaded.BadgeStats.Total(BadgeStatNames.Kills));
        Assert.Equal(1, loaded.BadgeStats.WinStreaks[1].Battles);
        Assert.Equal(16, results.Count);
        Assert.Single(results.Select(result => string.Join(",", result)).Distinct());
    }

    private static void Likes(Harness h)
    {
        var rules = Rules();
        var stores = new[] { h.NewStore(), h.NewStore() };
        var granted = new System.Collections.Concurrent.ConcurrentBag<int>();
        var now = DateTimeOffset.UtcNow;
        // 6 likers, each submitting twice, concurrently, through two "processes".
        Parallel.For(0, 12, new ParallelOptions { MaxDegreeOfParallelism = 8 }, index =>
        {
            foreach (var honor in rules.RecordVerifiedLike(h.OtherPlayerId, (9000 + index % 6).ToString(), "battle-1", now, stores[index % 2]))
                granted.Add(honor);
        });

        var target = h.NewStore().TryLoad(h.OtherPlayerId)!;
        Assert.Equal(6, target.BadgeStats.Total(BadgeStatNames.LikesReceived));
        Assert.Equal(6, target.BadgeStats.LikeLedger.Count);
        Assert.Contains(6010502, target.UserHonorIds);
        Assert.Equal(new[] { 6010502 }, granted.ToArray());
        // The liker's own row is untouched.
        Assert.Equal(0, h.NewStore().TryLoad(h.PlayerId)!.BadgeStats.Total(BadgeStatNames.LikesReceived));
        // A like for a player that does not exist is ignored.
        Assert.Empty(rules.RecordVerifiedLike(987654321, "9000", "battle-1", now, stores[0]));
    }

    private static void UpgradesAndLoginDays(Harness h)
    {
        var rules = Rules();
        var stores = new[] { h.NewStore(), h.NewStore() };
        var awards = new System.Collections.Concurrent.ConcurrentBag<int>();
        Parallel.For(0, 10, new ParallelOptions { MaxDegreeOfParallelism = 8 }, index =>
        {
            foreach (var honor in rules.RecordVerifiedDiscUpgrade(new SessionState { UserId = h.PlayerId.ToString() }, stores[index % 2]))
                awards.Add(honor);
        });
        var day1 = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        var logins = new System.Collections.Concurrent.ConcurrentBag<int>();
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, index =>
        {
            foreach (var honor in rules.RecordVerifiedLoginDay(new SessionState { UserId = h.PlayerId.ToString() }, stores[index % 2], day1))
                logins.Add(honor);
        });
        Assert.Empty(logins);
        Assert.Equal(new[] { 6010101 }, rules.RecordVerifiedLoginDay(
            new SessionState { UserId = h.PlayerId.ToString() }, stores[1], day1.AddDays(1)));

        var loaded = h.NewStore().TryLoad(h.PlayerId)!;
        Assert.Equal(10, loaded.DiscUpgradeCount);
        Assert.Equal(new[] { 6012801 }, awards.ToArray());
        Assert.Equal(new[] { "2026-03-01", "2026-03-02" }, loaded.VerifiedLoginDays.Order().ToArray());
    }

    // ---- helpers ----

    private static void Exec(string connectionString, string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(string connectionString, string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    private static string[] Rows(string connectionString, string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows.ToArray();
    }

    private sealed class TempDatabase : IDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _name;
        public string ConnectionString { get; }

        private TempDatabase(string admin, string name, string connectionString)
        {
            _adminConnectionString = admin;
            _name = name;
            ConnectionString = connectionString;
        }

        public static TempDatabase Create()
        {
            var admin = Environment.GetEnvironmentVariable("KF_TEST_POSTGRES_ADMIN_CONNECTION_STRING")!;
            var name = "kf_badge_test_" + Guid.NewGuid().ToString("N")[..12];
            Exec(admin, $"create database {name}");
            var builder = new NpgsqlConnectionStringBuilder(admin) { Database = name, MaxPoolSize = 40 };
            return new TempDatabase(admin, name, builder.ConnectionString);
        }

        public void Dispose()
        {
            NpgsqlConnection.ClearAllPools();
            Exec(_adminConnectionString, $"drop database if exists {_name} with (force)");
        }
    }

    private sealed class TestEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "badge-persistence-tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Test";
        public string WebRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}

public sealed class PostgresBadgeFactAttribute : FactAttribute
{
    public PostgresBadgeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KF_TEST_POSTGRES_ADMIN_CONNECTION_STRING")))
            Skip = "Set KF_TEST_POSTGRES_ADMIN_CONNECTION_STRING to a THROWAWAY server where the user may create/drop kf_badge_test_* databases.";
    }
}
