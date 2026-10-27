using KickFlight.BootstrapApi.PlayerStore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

public sealed class PlayerStoreSocialTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "kf-social-" + Guid.NewGuid().ToString("N")[..12]);

    private JsonPlayerStore NewStore() =>
        new(NullLogger<JsonPlayerStore>.Instance, new TestEnvironment(_directory));

    private long CreatePlayer(JsonPlayerStore store, string uuid, string name)
    {
        // Identity is otomo's; the store only files the row. Each name gets its own account and device.
        var id = store.ResolveGamePlayer($"otomo-{uuid}", uuid);
        store.Save(new SessionState
        {
            UserId = id.ToString(),
            UserName = name,
            KickerId = 17,
            KickerCostumeId = 1701
        });
        return id;
    }

    [Fact]
    public void Follows_are_idempotent_durable_and_track_unread_followers()
    {
        var store = NewStore();
        var alice = CreatePlayer(store, "alice-device", "Alice");
        var bob = CreatePlayer(store, "bob-device", "Bob");

        store.AddFollow(alice, bob);
        store.AddFollow(alice, bob);

        Assert.Equal(1, store.CountFollowing(alice));
        Assert.Equal(1, store.CountFollowers(bob));
        Assert.Equal(1, store.CountNewFollowers(bob));
        Assert.Equal(new[] { bob }, store.FindFollowedIds(alice, new[] { bob, alice }));
        var follower = Assert.Single(store.ListFollowers(bob, 0, 10));
        Assert.Equal("Alice", follower.Profile.DisplayName);
        Assert.True(follower.IsNew);

        store.MarkFollowersRead(bob, new[] { alice, alice });
        Assert.Equal(0, store.CountNewFollowers(bob));
        Assert.False(Assert.Single(store.ListFollowers(bob, 0, 10)).IsNew);

        var restarted = NewStore();
        Assert.Equal(1, restarted.CountFollowing(alice));
        Assert.Equal(0, restarted.CountNewFollowers(bob));
        Assert.Equal("Bob", Assert.Single(restarted.ListFollowing(alice, 0, 10)).Profile.DisplayName);

        restarted.RemoveFollow(alice, bob);
        restarted.RemoveFollow(alice, bob);
        Assert.Equal(0, restarted.CountFollowing(alice));
        Assert.Equal(0, restarted.CountFollowers(bob));
    }

    [Fact]
    public void Real_friend_token_is_stable_reusable_and_separate_from_following()
    {
        var store = NewStore();
        var alice = CreatePlayer(store, "real-alice-device", "Alice");
        var bob = CreatePlayer(store, "real-bob-device", "Bob");
        var carol = CreatePlayer(store, "real-carol-device", "Carol");

        var token = store.GetOrCreateRealFriendToken(alice);
        Assert.Equal(token, NewStore().GetOrCreateRealFriendToken(alice));
        Assert.Equal(alice, store.TryGetRealFriendTokenOwner(token));
        Assert.Null(store.TryGetRealFriendTokenOwner("unknown-token"));

        store.AddRealFriend(alice, bob);
        store.AddRealFriend(bob, alice);
        store.AddRealFriend(alice, carol);
        Assert.True(store.AreRealFriends(alice, bob));
        Assert.True(store.AreRealFriends(bob, alice));
        Assert.True(store.AreRealFriends(alice, carol));
        Assert.False(store.AreRealFriends(bob, carol));
        Assert.Equal(0, store.CountFollowing(alice));
        Assert.Equal(0, store.CountFollowers(alice));

        Assert.Throws<ArgumentException>(() => store.AddRealFriend(alice, alice));
        Assert.True(NewStore().AreRealFriends(alice, carol));
    }

    [Fact]
    public void Profiles_are_real_and_rank_positions_are_deterministic_and_global_when_filtered()
    {
        var store = NewStore();
        var alice = CreatePlayer(store, "rank-alice-device", "Alice");
        var bob = CreatePlayer(store, "rank-bob-device", "Bob");
        var carol = CreatePlayer(store, "rank-carol-device", "Carol");

        store.SaveRank(alice, 4, new RankState(3000, 7));
        store.SaveRank(bob, 4, new RankState(3100, 7));
        store.SaveRank(carol, 4, new RankState(3000, 7));

        Assert.Null(store.FindProfile(long.MaxValue));
        Assert.Equal(new[] { alice, carol }, store.FindProfiles(new[] { carol, long.MaxValue, alice }).Select(p => p.PlayerId));

        var rankings = store.ListRanks(4, null, 0, 10);
        Assert.Equal(new[] { bob, alice, carol }, rankings.Select(row => row.Profile.PlayerId));
        Assert.Equal(new long[] { 1, 2, 3 }, rankings.Select(row => row.Position));
        var filtered = Assert.Single(store.ListRanks(4, new[] { carol }, 0, 10));
        Assert.Equal(3, filtered.Position);
    }

    [Fact]
    public void Concurrent_stores_keep_one_follow_edge_and_one_reusable_token()
    {
        var store = NewStore();
        var alice = CreatePlayer(store, "parallel-alice-device", "Alice");
        var bob = CreatePlayer(store, "parallel-bob-device", "Bob");
        var stores = Enumerable.Range(0, 12).Select(_ => NewStore()).ToArray();

        Parallel.ForEach(stores, instance =>
        {
            instance.AddFollow(alice, bob);
            instance.GetOrCreateRealFriendToken(alice);
        });

        Assert.Equal(1, NewStore().CountFollowing(alice));
        Assert.All(stores, instance => Assert.Equal(stores[0].GetOrCreateRealFriendToken(alice), instance.GetOrCreateRealFriendToken(alice)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class TestEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Test";
        public string WebRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
