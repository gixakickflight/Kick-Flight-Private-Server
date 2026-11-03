using KickFlight.BootstrapApi.PlayerStore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The player store's identity surface, now that the social relation is otomo's: what is left of the old social tests
// is the link between a game row and the account behind it - the numeric-id <-> otomo-account-id mapping and nothing
// more - and the rank projection the adapter still owns until the rank commit.
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
    public void An_accounts_row_is_found_by_account_or_by_numeric_id_and_survives_a_restart()
    {
        var store = NewStore();
        var alice = CreatePlayer(store, "map-alice-device", "Alice");
        var bob = CreatePlayer(store, "map-bob-device", "Bob");

        var link = Assert.IsType<OtomoPlayer>(store.FindOtomoPlayer(alice));
        Assert.Equal(new OtomoPlayer(alice, "otomo-map-alice-device"), link);
        Assert.Null(store.FindOtomoPlayer(long.MaxValue));

        // Both in one read, in row order, and an account this store has never seen is absent rather than blank.
        var both = store.FindOtomoPlayers(["otomo-map-bob-device", "otomo-unknown", "otomo-map-alice-device"]);
        Assert.Equal(new[] { alice, bob }, both.Select(player => player.PlayerId));
        Assert.Equal(new[] { "otomo-map-alice-device", "otomo-map-bob-device" }, both.Select(player => player.OtomoPlayerId));

        var restarted = NewStore();
        Assert.Equal(link, restarted.FindOtomoPlayer(alice));
        Assert.Equal(bob, restarted.FindOtomoPlayers(["otomo-map-bob-device"]).Single().PlayerId);
        Assert.Equal(17, restarted.FindProfile(alice)?.KickerId);
    }

    [Fact]
    public void The_mapping_is_the_only_thing_kept_about_an_account()
    {
        // OtomoPlayer is (PlayerId, OtomoPlayerId): no name, no discriminator, and no index file for either.
        Assert.Equal(["PlayerId", "OtomoPlayerId"], typeof(OtomoPlayer).GetProperties()
            .Where(property => property.Name is not "EqualityContract").Select(property => property.Name));
        var store = NewStore();
        CreatePlayer(store, "files-alice-device", "Alice");
        Assert.False(File.Exists(Path.Combine(_directory, "data", "users", "_otomo-tags.json")));
        Assert.DoesNotContain(typeof(IPlayerStore).GetMethods(), method => method.Name == "SaveOtomoTag");
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
    public void Concurrent_stores_keep_one_row_per_account()
    {
        var stores = Enumerable.Range(0, 12).Select(_ => NewStore()).ToArray();

        var ids = new long[stores.Length];
        Parallel.For(0, stores.Length, index =>
            ids[index] = stores[index].ResolveGamePlayer("otomo-parallel-alice-device", "parallel-alice-device"));

        Assert.Single(ids.Distinct());
        Assert.Equal(ids[0], NewStore().FindOtomoPlayer(ids[0])?.PlayerId);
        Assert.All(stores, instance => Assert.Equal("otomo-parallel-alice-device", instance.FindOtomoPlayer(ids[0])?.OtomoPlayerId));
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
