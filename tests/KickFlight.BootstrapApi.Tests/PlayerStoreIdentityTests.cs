using KickFlight.BootstrapApi.PlayerStore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The JSON store allocates a row id as one past the highest it can see in its directory, and it only sees a save
// file once someone has written one. Two stores over the same directory would therefore both read the same directory
// before either writes and hand the same id to two different players, so two players would share one save file -
// each overwriting the other's inventory, name and rank.
//
// That is not hypothetical: IClassFixture gives the suite one host per test class, so ShopGachaTests and HarnessTests
// run two stores side by side over one data/users directory, which is what made ShopGachaTests.Disc_draw_grants_a_disc
// fail roughly one run in four with a KeyNotFoundException while reading a save file that belonged to another test.
//
// Identity itself is otomo's now: what these tests pin is that an otomo account keeps one row id, and that a row
// created before otomo owned identity is adopted rather than abandoned.
public sealed class PlayerStoreIdentityTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "kf-store-" + Guid.NewGuid().ToString("N")[..12]);

    private JsonPlayerStore NewStore() =>
        new(NullLogger<JsonPlayerStore>.Instance, new TestEnvironment(_directory));

    private static string NewOtomoPlayer() => Guid.NewGuid().ToString();

    [Fact]
    public void Two_stores_over_one_directory_never_issue_the_same_id()
    {
        // Both are built before either resolves anything, which is the state that makes the collision possible.
        var first = NewStore();
        var second = NewStore();

        var a = first.ResolveGamePlayer(NewOtomoPlayer(), "device-a");
        var b = second.ResolveGamePlayer(NewOtomoPlayer(), "device-b");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Many_stores_over_one_directory_still_hand_out_distinct_ids()
    {
        var stores = Enumerable.Range(0, 8).Select(_ => NewStore()).ToList();

        var ids = stores.Select((store, index) => store.ResolveGamePlayer(NewOtomoPlayer(), $"device-{index}")).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Concurrent_stores_preserve_every_account_after_reload()
    {
        const int accountCount = 64;
        var stores = Enumerable.Range(0, accountCount).Select(_ => NewStore()).ToArray();
        var accounts = Enumerable.Range(0, accountCount).Select(_ => NewOtomoPlayer()).ToArray();
        var playerIds = new long[accountCount];

        Parallel.For(0, accountCount, index =>
            playerIds[index] = stores[index].ResolveGamePlayer(accounts[index], $"parallel-device-{index}"));

        Assert.Equal(accountCount, playerIds.Distinct().Count());

        var reloaded = NewStore();
        for (var index = 0; index < accountCount; index++)
        {
            Assert.Equal(playerIds[index], reloaded.ResolveGamePlayer(accounts[index], $"parallel-device-{index}"));
        }
    }

    [Fact]
    public void The_same_account_keeps_its_row_across_stores_and_restarts()
    {
        var account = NewOtomoPlayer();
        var id = NewStore().ResolveGamePlayer(account, "device-stable");

        // A second store is what a server restart looks like: a fresh instance over the same directory, with only the
        // index to remember what it decided.
        Assert.Equal(id, NewStore().ResolveGamePlayer(account, "device-stable"));
    }

    [Fact]
    public void An_account_keeps_its_row_whatever_device_it_signs_in_from()
    {
        var account = NewOtomoPlayer();
        var store = NewStore();

        var first = store.ResolveGamePlayer(account, "phone");
        var second = store.ResolveGamePlayer(account, "tablet");

        // A second install is a second otomo session, not a second player.
        Assert.Equal(first, second);
    }

    [Fact]
    public void An_account_adopts_the_row_its_device_had_before_otomo_owned_identity()
    {
        var account = NewOtomoPlayer();
        var users = Path.Combine(_directory, "data", "users");
        Directory.CreateDirectory(users);
        File.WriteAllText(Path.Combine(users, "_identities.json"), """{"legacy-device":1000007}""");
        File.WriteAllText(Path.Combine(users, "1000007.json"), """{"UserId":"1000007","UserName":"Tanuki"}""");

        var store = NewStore();
        var id = store.ResolveGamePlayer(account, "legacy-device");

        // The player keeps their name, their rank and their friends: the row moved to the account, not the other way
        // round.
        Assert.Equal(1000007, id);
        Assert.Equal("Tanuki", store.TryLoad(id)!.UserName);

        // And the adoption is durable: the same account on a fresh store finds the same row.
        Assert.Equal(1000007, NewStore().ResolveGamePlayer(account, "legacy-device"));
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
