using KickFlight.BootstrapApi.PlayerStore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using Xunit.Sdk;

namespace KickFlight.BootstrapApi.Tests;

public sealed class PostgresSocialMigrationTests
{
    [PostgresIntegrationFact]
    public void V1_schema_migrates_to_v2_and_social_operations_are_idempotent()
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

        store.AddFollow(alice, bob);
        store.AddFollow(alice, bob);
        Assert.Equal(1, store.CountFollowing(alice));
        Assert.Equal(1, store.CountFollowers(bob));
        Assert.Equal(1, store.CountNewFollowers(bob));
        Assert.Equal("bob", Assert.Single(store.ListFollowing(alice, 0, 10)).Profile.DisplayName);
        store.MarkFollowersRead(bob, new[] { alice });
        Assert.Equal(0, store.CountNewFollowers(bob));

        var token = store.GetOrCreateRealFriendToken(alice);
        Assert.Equal(token, store.GetOrCreateRealFriendToken(alice));
        Assert.Equal(alice, store.TryGetRealFriendTokenOwner(token));
        store.AddRealFriend(alice, bob);
        store.AddRealFriend(bob, alice);
        store.AddRealFriend(alice, carol);
        Assert.True(store.AreRealFriends(bob, alice));
        Assert.True(store.AreRealFriends(alice, carol));
        Assert.False(store.AreRealFriends(bob, carol));

        store.SaveRank(alice, 4, new RankState(3000, 7));
        store.SaveRank(bob, 4, new RankState(3100, 7));
        store.SaveRank(carol, 4, new RankState(3000, 7));
        Assert.Equal(new[] { bob, alice, carol }, store.ListRanks(4, null, 0, 10).Select(row => row.Profile.PlayerId));
        Assert.Equal(3, Assert.Single(store.ListRanks(4, new[] { carol }, 0, 10)).Position);

        var restarted = NewStore(connectionString);
        Assert.Equal(3, ReadSchemaVersion(connectionString));
        Assert.Equal(token, restarted.GetOrCreateRealFriendToken(alice));
        Assert.True(restarted.AreRealFriends(alice, carol));
        Assert.Equal(1, restarted.CountFollowing(alice));

        // Migration 3 is the identity move: the sessions table is gone and a player row carries otomo's account id.
        using (var connection = new NpgsqlConnection(connectionString))
        {
            connection.Open();
            using var sessionsTable = new NpgsqlCommand("select to_regclass('public.sessions') is null", connection);
            Assert.True(Convert.ToBoolean(sessionsTable.ExecuteScalar()));
            using var identityColumn = new NpgsqlCommand(
                "select count(*) from information_schema.columns where table_name = 'players' and column_name = 'otomo_player_id'",
                connection);
            Assert.Equal(1, Convert.ToInt32(identityColumn.ExecuteScalar()));
        }

        using (var connection = new NpgsqlConnection(connectionString))
        {
            connection.Open();
            using var insertFutureVersion = new NpgsqlCommand("insert into schema_meta(version) values (4)", connection);
            insertFutureVersion.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => NewStore(connectionString));
        using (var connection = new NpgsqlConnection(connectionString))
        {
            connection.Open();
            using var removeFutureVersion = new NpgsqlCommand("delete from schema_meta where version = 4", connection);
            removeFutureVersion.ExecuteNonQuery();
        }
    }

    private static PostgresPlayerStore NewStore(string connectionString) =>
        new(NullLogger<PostgresPlayerStore>.Instance, connectionString);

    private static long CreatePlayer(PostgresPlayerStore store, string name)
    {
        var id = store.ResolveGamePlayer($"social-player-{Guid.NewGuid()}", $"social-test-{Guid.NewGuid():N}");
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
