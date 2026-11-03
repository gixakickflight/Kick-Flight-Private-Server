using KickFlight.BootstrapApi.PlayerStore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The migration that ends this store's ownership of the social relation: version 6 drops player_follows,
// real_friend_tokens and player_real_friends - every write to them having been removed first. Nothing replaces them:
// no live data migrates, and the adapter keeps no column for a name or a tag.
//
// It starts from a database at version 1, which is what the previous migration test also started from, so it covers the
// whole ladder rather than only the last rung.
public sealed class PostgresSocialMigrationTests
{
    [PostgresIntegrationFact]
    public void V1_schema_migrates_to_v6_dropping_the_social_tables_and_keeping_the_accounts()
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

        Assert.Equal(6, ReadSchemaVersion(connectionString));

        // The relation is otomo's; what this store keeps of a player is who the account behind the row is.
        Assert.Equal(new OtomoPlayer(alice, "otomo-alice"), store.FindOtomoPlayer(alice));
        Assert.Equal(new[] { alice, bob },
            store.FindOtomoPlayers(["otomo-bob", "otomo-alice", "otomo-unknown"]).Select(player => player.PlayerId));

        store.SaveRank(alice, 4, new RankState(3000, 7));
        store.SaveRank(bob, 4, new RankState(3100, 7));
        store.SaveRank(carol, 4, new RankState(3000, 7));
        Assert.Equal(new[] { bob, alice, carol }, store.ListRanks(4, null, 0, 10).Select(row => row.Profile.PlayerId));
        Assert.Equal(3, Assert.Single(store.ListRanks(4, new[] { carol }, 0, 10)).Position);

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
            using var insertFutureVersion = new NpgsqlCommand("insert into schema_meta(version) values (7)", connection);
            insertFutureVersion.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => NewStore(connectionString));
        using (var connection = new NpgsqlConnection(connectionString))
        {
            connection.Open();
            using var removeFutureVersion = new NpgsqlCommand("delete from schema_meta where version = 7", connection);
            removeFutureVersion.ExecuteNonQuery();
        }
    }

    private static PostgresPlayerStore NewStore(string connectionString) =>
        new(NullLogger<PostgresPlayerStore>.Instance, connectionString);

    // The account id is derived from the name the test gave, so a row created here can be found by it afterwards. The
    // name is saved because the rank projection (until the rank commit removes it) only lists named players.
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
