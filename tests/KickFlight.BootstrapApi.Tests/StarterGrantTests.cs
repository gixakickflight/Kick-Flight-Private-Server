using System.Net;
using System.Text;
using System.Text.Json;
using KickFlight.BootstrapApi;
using KickFlight.BootstrapApi.Otomo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The starter bundle's guard, against the fake economy with the internal plane on: who is paid what on a startup,
// and that nothing is paid twice. The account in each test is the otomo player id the device resolves to, which is
// also what a grant is keyed by.
public sealed class StarterGrantTests : IDisposable
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";
    private const string GameServerKey = "test-gameserver-key";

    private readonly FakeOtomoEconomy _economy = OtomoTestEnvironment.Economy;
    private readonly string _keyFile = Path.Combine(Path.GetTempPath(), $"kf-gameserver-{Guid.NewGuid():N}.key");
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public StarterGrantTests() => File.WriteAllText(_keyFile, GameServerKey);

    [Fact]
    public async Task An_unbranded_account_is_paid_the_whole_starter_bundle_once()
    {
        var host = Host_();
        var session = await CreateSessionAsync(host);

        var first = await PostAsync(session, "/startup/index");
        await PostAsync(session, "/startup/index");

        // Three grants, in the bundle's own order, each keyed by the player: a retry replays rather than pays.
        Assert.Equal(new[] { "starter.discs", "starter.jetcoin", "starter.disc_force" }, GrantClassesFor(session.Account));
        Assert.Equal(new[]
        {
            $"starter.discs:{session.Account}", $"starter.jetcoin:{session.Account}",
            $"starter.disc_force:{session.Account}"
        }, GrantKeysFor(session.Account));

        Assert.Equal(FakeOtomoEconomy.StarterDiscForce, _economy.BalanceOf(session.Account, "disc_force"));
        Assert.Equal(FakeOtomoEconomy.StarterJetCoins, _economy.BalanceOf(session.Account, "jetcoin"));
        // And the counters this startup answers with already report it.
        Assert.Equal(FakeOtomoEconomy.StarterDiscForce, ItemAmount(first, EconomyCatalog.DiscForceItemId));
    }

    [Fact]
    public async Task A_named_account_with_no_disc_force_is_paid_only_the_disc_force()
    {
        var host = Host_();
        var session = await CreateSessionAsync(host);
        await PostAsync(session, "/tutorial/end", new { name = UniqueName() });

        var startup = await PostAsync(session, "/startup/index");

        // One grant, and it is the class its build-up lesson needs.
        Assert.Equal(new[] { "starter.disc_force" }, GrantClassesFor(session.Account));
        Assert.Equal(new[] { $"starter.disc_force:{session.Account}" }, GrantKeysFor(session.Account));
        Assert.Equal(FakeOtomoEconomy.StarterDiscForce, _economy.BalanceOf(session.Account, "disc_force"));
        // Not the discs and not the coins: those are onboarding's own screens, and onboarding is over.
        Assert.Equal(0, _economy.BalanceOf(session.Account, "jetcoin"));
        foreach (var disc in new[] { "disc:3010001", "disc:3010002", "disc:3010003", "disc:3010004" })
        {
            Assert.Equal(0, _economy.ItemsOf(session.Account, disc));
        }
        Assert.Equal(FakeOtomoEconomy.StarterDiscForce, ItemAmount(startup, EconomyCatalog.DiscForceItemId));
    }

    [Fact]
    public async Task A_named_account_that_already_holds_disc_force_is_paid_nothing()
    {
        var host = Host_();
        var session = await CreateSessionAsync(host);
        await PostAsync(session, "/tutorial/end", new { name = UniqueName() });
        _economy.Seed(session.Account, balances: [("disc_force", 500)]);

        var startup = await PostAsync(session, "/startup/index");

        Assert.Empty(GrantClassesFor(session.Account));
        Assert.Equal(500, _economy.BalanceOf(session.Account, "disc_force"));
        Assert.Equal(500, ItemAmount(startup, EconomyCatalog.DiscForceItemId));
    }

    [Fact]
    public async Task A_replayed_disc_force_grant_pays_nothing_again()
    {
        var host = Host_();
        var session = await CreateSessionAsync(host);
        await PostAsync(session, "/tutorial/end", new { name = UniqueName() });

        // Another device was paid under this account's key and the force has been spent since, so this startup reads
        // an empty wallet and asks again: economy answers the replay, and the balance does not move.
        await GrantDirectlyAsync(session.Account, $"starter.disc_force:{session.Account}");
        _economy.Seed(session.Account, balances: [("disc_force", 0)]);

        var startup = await PostAsync(session, "/startup/index");

        var grant = Assert.Single(StartupGrantsFor(session.Account));
        Assert.Contains($"\"key\":\"starter.disc_force:{session.Account}\"", grant.Body);
        Assert.Equal(0, _economy.BalanceOf(session.Account, "disc_force"));
        Assert.Equal(0, ItemAmount(startup, EconomyCatalog.DiscForceItemId));
    }

    // A grant already made under the account's key, as an earlier device would have made it. Sent to the fake's own
    // route rather than through the adapter, because it is not this process's grant.
    private async Task GrantDirectlyAsync(string account, string key)
    {
        using var client = new HttpClient { BaseAddress = new Uri(_economy.BaseUrl) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/economy/grants")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                player_id = account,
                key,
                @class = "starter.disc_force",
                reason = "starter",
                @ref = "another-device"
            }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", GameServerKey);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(FakeOtomoEconomy.StarterDiscForce, _economy.BalanceOf(account, "disc_force"));
    }

    private List<FakeOtomoEconomy.RecordedRequest> GrantsFor(string account) =>
        _economy.Requests.Where(call => call.Path == "/internal/economy/grants" && call.Body is not null &&
                                        call.Body.Contains($"\"player_id\":\"{account}\"")).ToList();

    // The grants the adapter's own /startup/index made (its reference is the request path, not another device's).
    private List<FakeOtomoEconomy.RecordedRequest> StartupGrantsFor(string account) =>
        GrantsFor(account).Where(call => call.Body!.Contains("\"ref\":\"-startup-index\"")).ToList();

    private List<string> GrantClassesFor(string account) => GrantsFor(account)
        .Select(call => JsonDocument.Parse(call.Body!).RootElement.GetProperty("class").GetString() ?? "")
        .ToList();

    private List<string> GrantKeysFor(string account) => GrantsFor(account)
        .Select(call => JsonDocument.Parse(call.Body!).RootElement.GetProperty("key").GetString() ?? "")
        .ToList();

    // Names are unique per account and unique across runs: a replayed run must not collide with a saved one.
    private static string UniqueName() => "Vet" + Guid.NewGuid().ToString("N")[..6];

    private static int ItemAmount(JsonElement payload, int itemId) =>
        payload.GetProperty("userItemList").EnumerateArray()
            .Single(row => row.GetProperty("itemId").GetInt32() == itemId).GetProperty("amount").GetInt32();

    private WebApplicationFactory<Program> Host_()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Otomo:Economy:ProgressEnabled"] = "true",
            ["Otomo:Economy:InternalBaseUrl"] = _economy.BaseUrl,
            ["Otomo:Economy:ServiceKeyFile"] = _keyFile,
            ["Tutorial:Ladder"] = "false"
        };
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
        });
        _factories.Add(factory);
        return factory;
    }

    private static async Task<DemoSession> CreateSessionAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var uuid = Guid.NewGuid().ToString("N");
        var key = Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");
        var payload = JsonSerializer.Serialize(new { hash = Encoding.ASCII.GetString(key), uuid });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/index")
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(payload),
                Encoding.ASCII.GetBytes(CommonCode), new byte[16]))
        };
        request.Headers.Host = Host;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        client.DefaultRequestHeaders.Add("x-app-access-token", response.Headers.GetValues("x-app-access-token").Single());
        var account = OtomoTestEnvironment.Server.AccountFor(OtomoDeviceId.FromDeviceUuid(uuid));
        Assert.False(string.IsNullOrEmpty(account));
        return new DemoSession(client, key, account!);
    }

    private static async Task<JsonElement> PostAsync(DemoSession session, string path, object? body = null)
    {
        var json = body is null ? "{}" : JsonSerializer.Serialize(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(json), session.Key, new byte[16]))
        };
        request.Headers.Host = Host;
        using var response = await session.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0", response.Headers.GetValues("x-app-status-code").Single());
        var decrypted = D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), session.Key);
        using var document = JsonDocument.Parse(decrypted);
        return document.RootElement.Clone();
    }

    public void Dispose()
    {
        foreach (var factory in _factories) factory.Dispose();
        try { File.Delete(_keyFile); } catch (IOException) { }
    }

    private sealed record DemoSession(HttpClient Client, byte[] Key, string Account);
}
