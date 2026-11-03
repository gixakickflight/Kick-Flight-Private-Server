using KickFlight.BootstrapApi.PlayerStore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The migrations that end this store's ownership of the social relation and of battle points: version 6 drops
// player_follows, real_friend_tokens and player_real_friends, and version 7 drops player_ranks - every write to them
// having been removed first. Nothing replaces them: no live data migrates, and the adapter keeps no column for a name,
// a tag or a point.
//
// It starts from a database at version 1, which is what the previous migration test also started from, so it covers the
// whole ladder rather than only the last rung.
public sealed class PostgresSocialMigrationTests
{
    [PostgresIntegrationFact]
    public void V1_schema_migrates_to_v7_dropping_the_social_and_rank_tables_and_keeping_the_accounts()
    {
        var connectionString = Environment.GetEnvironmentVariable("KF_TEST_POSTGRES_CONNECTION_STRING");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        using (var connection = new NpgsqlConnection(connectionString))
        {
            connection.Open();
            using var database = new NpgsqlCommand("select current_database()", connection);
            var databaseName = Convert.ToString(database.ExecuteScalar()) ?? "";
            Assert.StartsWith("kf_social_test_", databaseName, StringComparison.Ordinal);

            using var version = new NpgsqlCommand("select coalesce(max(version), 0) from schema_meta", connection);
            Assert.Equal(1, Convert.ToInt32(version.ExecuteScalar()));
            using var v2Table = new NpgsqlCommand("select to_regclass('public.player_follows') is not null", connection);
            Assert.False(Convert.ToBoolean(v2Table.ExecuteScalar()));
        }

        var store = NewStore(connectionString);
        var alice = CreatePlayer(store, "alice");
        var bob = CreatePlayer(store, "bob");
        var carol = CreatePlayer(store, "carol");

        Assert.Equal(7, ReadSchemaVersion(connectionString));

        // The v1 database had player_ranks; it is gone once the ladder has run, and the accounts survive.
        using (var connection = new NpgsqlConnection(connectionString))
        {
            connection.Open();
            using var ranksTable = new NpgsqlCommand("select to_regclass('public.player_ranks') is null", connection);
            Assert.True(Convert.ToBoolean(ranksTable.ExecuteScalar()), "player_ranks should have been dropped");
        }

        // The relation is otomo's; what this store keeps of a player is who the account behind the row is.
        Assert.Equal(new OtomoPlayer(alice, "otomo-alice"), store.FindOtomoPlayer(alice));
        Assert.Equal(new[] { alice, bob },
            store.FindOtomoPlayers(["otomo-bob", "otomo-alice", "otomo-unknown"]).Select(player => player.PlayerId));

        var restarted = NewStore(connectionString);
        Assert.Equal(new OtomoPlayer(alice, "otomo-alice"), restarted.FindOtomoPlayer(alice));
        Assert.Equal(new OtomoPlayer(bob, "otomo-bob"), restarted.FindOtomoPlayer(bob));

        // The three tables are gone, and nothing replaced them: a player row has no name-or-tag columns.
        using (var connection = new NpgsqlConnection(connectionString))
        {
            connection.Open();
            foreach (var table in new[] { "player_follows", "real_friend_tokens", "player_real_friends" })
            {
                using var dropped = new NpgsqlCommand($"select to_regclass('public.{table}') is null", connection);
                Assert.True(Convert.ToBoolean(dropped.ExecuteScalar()), $"{table} should have been dropped");
            }
            using var columns = new NpgsqlCommand("""
                select count(*) from information_schema.columns
                where table_name = 'players' and column_name in ('otomo_display_name', 'otomo_discriminator')
                """, connection);
            Assert.Equal(0, Convert.ToInt32(columns.ExecuteScalar()));
            // Migration 4 is the identity move (3 is main's badge tables): there is still no session table here.
            using var sessionsTable = new NpgsqlCommand("select to_regclass('public.sessions') is null", connection);
            Assert.True(Convert.ToBoolean(sessionsTable.ExecuteScalar()));
        }

        // A database ahead of this code fails fast rather than serving with a column that does not exist.
        using (var connection = new NpgsqlConnection(connectionString))
        {
            connection.Open();
            using var insertFutureVersion = new NpgsqlCommand("insert into schema_meta(version) values (8)", connection);
            insertFutureVersion.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => NewStore(connectionString));
        using (var connection = new NpgsqlConnection(connectionString))
        {
            connection.Open();
            using var removeFutureVersion = new NpgsqlCommand("delete from schema_meta where version = 8", connection);
            removeFutureVersion.ExecuteNonQuery();
        }
    }

    private static PostgresPlayerStore NewStore(string connectionString) =>
        new(NullLogger<PostgresPlayerStore>.Instance, connectionString);

    // The account id is derived from the name the test gave, so a row created here can be found by it afterwards.
    private static long CreatePlayer(PostgresPlayerStore store, string name)
    {
        var id = store.ResolveGamePlayer($"otomo-{name}", $"social-test-{Guid.NewGuid():N}");
        store.Save(new SessionState { UserId = id.ToString(), UserName = name });
        return id;
    }

    private static int ReadSchemaVersion(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("select coalesce(max(version), 0) from schema_meta", connection);
        return Convert.ToInt32(command.ExecuteScalar());
    }
}

public sealed class PostgresIntegrationFactAttribute : FactAttribute
{
    public PostgresIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KF_TEST_POSTGRES_CONNECTION_STRING")))
            Skip = "Set KF_TEST_POSTGRES_CONNECTION_STRING to an isolated kf_social_test_* database at schema v1.";
    }
}
