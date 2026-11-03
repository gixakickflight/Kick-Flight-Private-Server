using KickFlight.BootstrapApi.PlayerStore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The player store's identity surface, now that the social relation is otomo's: what is left of the old social tests
// is the link between a game row and the account behind it (and the tag a follow is addressed by), and the rank
// projection the adapter still owns.
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
    public void An_accounts_tag_is_remembered_by_account_and_survives_a_restart()
    {
        var store = NewStore();
        var alice = CreatePlayer(store, "tag-alice-device", "Alice");
        var bob = CreatePlayer(store, "tag-bob-device", "Bob");

        // Nothing has been asked of otomo yet: the rows have accounts, and no tags.
        var before = Assert.Single(store.FindOtomoPlayers(["otomo-tag-alice-device"]));
        Assert.Equal(alice, before.PlayerId);
        Assert.Equal("", before.DisplayName);
        Assert.Equal(0, before.Discriminator);
        Assert.Null(store.FindOtomoPlayer(long.MaxValue));

        store.SaveOtomoTag("otomo-tag-alice-device", "Alice", 4417);
        store.SaveOtomoTag("otomo-tag-bob-device", "Bob", 812);

        var tag = Assert.IsType<OtomoPlayer>(store.FindOtomoPlayer(alice));
        Assert.Equal("otomo-tag-alice-device", tag.OtomoPlayerId);
        Assert.Equal("Alice", tag.DisplayName);
        Assert.Equal(4417, tag.Discriminator);

        // Both in one read, in row order, and an account this store has never seen is absent rather than blank.
        var both = store.FindOtomoPlayers(["otomo-tag-bob-device", "otomo-unknown", "otomo-tag-alice-device"]);
        Assert.Equal(new[] { alice, bob }, both.Select(player => player.PlayerId));
        Assert.Equal(new[] { 4417, 812 }, both.Select(player => player.Discriminator));

        var restarted = NewStore();
        Assert.Equal("Alice", restarted.FindOtomoPlayer(alice)?.DisplayName);
        Assert.Equal(812, restarted.FindOtomoPlayer(bob)?.Discriminator);
        Assert.Equal(17, restarted.FindProfile(alice)?.KickerId);
    }

    [Fact]
    public void A_tag_for_an_account_with_no_row_is_not_stored()
    {
        var store = NewStore();
        var alice = CreatePlayer(store, "orphan-alice-device", "Alice");

        // This store does not create identities for the social service, so there is nowhere to put this one.
        store.SaveOtomoTag("otomo-not-a-player", "Nobody", 5);

        Assert.Equal(0, store.FindOtomoPlayer(alice)?.Discriminator ?? 0);
        Assert.Empty(store.FindOtomoPlayers(["otomo-not-a-player"]));
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
    public void Concurrent_stores_keep_one_tag_per_account()
    {
        var store = NewStore();
        var alice = CreatePlayer(store, "parallel-alice-device", "Alice");
        var stores = Enumerable.Range(0, 12).Select(_ => NewStore()).ToArray();

        Parallel.ForEach(stores, instance => instance.SaveOtomoTag("otomo-parallel-alice-device", "Alice", 4417));

        Assert.Equal(4417, NewStore().FindOtomoPlayer(alice)?.Discriminator);
        Assert.All(stores, instance => Assert.Equal(4417, instance.FindOtomoPlayer(alice)?.Discriminator));
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
