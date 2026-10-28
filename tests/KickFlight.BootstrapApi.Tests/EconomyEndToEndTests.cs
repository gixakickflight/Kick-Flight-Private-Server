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

// The adapter's value-bearing routes against a real (fake) economy over HTTP: every number the client is shown comes
// from that service, every purchase and draw is one call to it, and the counters in a receipt come back from it.
//
// The player is seeded through the fake's own API, keyed by the otomo account the device resolves to - which is the
// same account the adapter's token carries, so nothing here reaches into the adapter to arrange state.
public sealed class EconomyEndToEndTests : IClassFixture<ServerTestHostFixture>
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeOtomoEconomy _economy = OtomoTestEnvironment.Economy;

    public EconomyEndToEndTests(ServerTestHostFixture fixture) => _factory = fixture.Factory;

    [Fact]
    public async Task Startup_serves_the_wallet_inventory_discs_and_kickers_from_economy()
    {
        var session = await CreateSessionAsync();
        _economy.Seed(session.Account,
            balances: [("jetcoin", 500), ("jetcoin.paid", 25), ("discforce", 1100)],
            items: [("stamp.gear", 7), ("disc:3010001", 2), ("skin:2010201", 1)]);

        var startup = await PostAsync(session, "/startup/index", null);

        var items = startup.GetProperty("userItemList").EnumerateArray().ToList();
        Assert.Equal(8, items.Count);
        Assert.Equal(500, ItemAmount(items, 1));
        Assert.Equal(25, ItemAmount(items, 2));
        Assert.Equal(1100, ItemAmount(items, 3));
        Assert.Equal(0, ItemAmount(items, 4));
        Assert.Equal(7, ItemAmount(items, 5));
        Assert.Equal(0, ItemAmount(items, 6));

        var disc = Assert.Single(startup.GetProperty("userDiscList").EnumerateArray().ToList());
        Assert.Equal(3010001, disc.GetProperty("discId").GetInt32());
        Assert.Equal(2, disc.GetProperty("amount").GetInt32());
        Assert.Equal(10, disc.GetProperty("level").GetInt32());

        var kicker = Assert.Single(startup.GetProperty("userKickerList").EnumerateArray().ToList());
        Assert.Equal(1, kicker.GetProperty("kickerId").GetInt32());
        var costumes = kicker.GetProperty("userKickerCostumeList").EnumerateArray()
            .Select(costume => costume.GetProperty("kickerCostumeId").GetInt32()).OrderBy(id => id).ToArray();
        // The costume the player bought from economy, plus the one their save says they are wearing.
        Assert.Equal(new[] { 2010101, 2010201 }, costumes);
    }

    [Fact]
    public async Task A_player_who_owns_nothing_is_served_zeroes_rather_than_a_demo_stock()
    {
        var session = await CreateSessionAsync();

        var startup = await PostAsync(session, "/startup/index", null);

        var items = startup.GetProperty("userItemList").EnumerateArray().ToList();
        Assert.All(items, item => Assert.Equal(0, item.GetProperty("amount").GetInt32()));
        Assert.Empty(startup.GetProperty("userDiscList").EnumerateArray());
        // One kicker is still served: the one the save says is selected, which is identity and not value.
        Assert.Single(startup.GetProperty("userKickerList").EnumerateArray());
    }

    [Fact]
    public async Task Home_prices_the_shop_and_the_kicker_gacha_from_economy()
    {
        var session = await CreateSessionAsync();

        var home = await PostAsync(session, "/home/index", null);

        var products = home.GetProperty("goodsShopProductList").EnumerateArray().ToList();
        Assert.Equal(4, products.Count);
        Assert.All(products, product => Assert.Equal(9999, product.GetProperty("purchasableCount").GetInt32()));
        var stamps = products.Single(product => product.GetProperty("id").GetInt32() == 3);
        Assert.Equal(101, stamps.GetProperty("costGoodsType").GetInt32());
        Assert.Equal(100, stamps.GetProperty("costAmount").GetInt32());

        var kickerGroup = Assert.Single(home.GetProperty("kickerGachaGroupList").EnumerateArray().ToList());
        Assert.Equal(2, kickerGroup.GetProperty("gachaGroupId").GetInt32());
        var gacha = Assert.Single(kickerGroup.GetProperty("gachaList").EnumerateArray().ToList());
        Assert.Equal(1, gacha.GetProperty("id").GetInt32());
        // The pool costs 300 JetCoin: the banner shows the pool's price, not the master's ticket price.
        Assert.Equal(101, gacha.GetProperty("costGoodsType").GetInt32());
        Assert.Equal(1, gacha.GetProperty("costGoodsId").GetInt32());
        Assert.Equal(300, gacha.GetProperty("costAmount").GetInt32());
        Assert.Equal(9999, gacha.GetProperty("drawableCount").GetInt32());

        var discGroup = Assert.Single(home.GetProperty("discGachaGroupList").EnumerateArray().ToList());
        Assert.True(discGroup.GetProperty("completedDisplayFlag").GetBoolean());
        var discGacha = Assert.Single(discGroup.GetProperty("gachaList").EnumerateArray().ToList());
        Assert.Equal(0, discGacha.GetProperty("drawableCount").GetInt32());
        Assert.Equal(402, discGacha.GetProperty("costGoodsType").GetInt32());
    }

    [Fact]
    public async Task Goods_shop_buy_is_one_economy_purchase_and_the_counters_are_read_back()
    {
        var session = await CreateSessionAsync();
        _economy.Seed(session.Account, balances: [("jetcoin", 500)]);

        var buy = await PostAsync(session, "/goodsShop/buy", new { goodsShopProductId = 3 });

        var items = buy.GetProperty("userItemList").EnumerateArray().ToList();
        Assert.Equal(400, ItemAmount(items, 1));
        Assert.Equal(10, ItemAmount(items, 5));
        var received = Assert.Single(buy.GetProperty("receivedUserItemList").EnumerateArray().ToList());
        Assert.Equal(5, received.GetProperty("itemId").GetInt32());
        Assert.Equal(10, received.GetProperty("amount").GetInt32());

        // The value moved in economy, not in this process.
        Assert.Equal(400, _economy.BalanceOf(session.Account, "jetcoin"));
        Assert.Equal(10, _economy.ItemsOf(session.Account, "stamp.gear"));

        var purchase = Assert.Single(CallsFor(session.Account, "/api/player/economy/shop/purchase"));
        using var body = JsonDocument.Parse(purchase.Body!);
        Assert.Equal("kf.goods.gear-stamp.10", body.RootElement.GetProperty("offer_id").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("quantity").GetInt64());
        Assert.Equal(36, body.RootElement.GetProperty("idempotency_key").GetString()!.Length);
        // The player's own token: a purchase is the player's act, on the player plane.
        Assert.StartsWith("Bearer ", purchase.Authorization);
    }

    [Fact]
    public async Task Goods_shop_buy_answers_the_game_error_when_economy_refuses()
    {
        var session = await CreateSessionAsync();

        var buy = await PostAsync(session, "/goodsShop/buy", new { goodsShopProductId = 3 }, expectStatusZero: false);

        Assert.Equal(JsonValueKind.Object, buy.ValueKind);
        Assert.Equal(0, _economy.BalanceOf(session.Account, "jetcoin"));
        Assert.Equal(0, _economy.ItemsOf(session.Account, "stamp.gear"));
        // It really asked: the refusal came from economy, not from a local guess.
        Assert.Single(CallsFor(session.Account, "/api/player/economy/shop/purchase"));
    }

    [Fact]
    public async Task Kicker_draw_is_one_roll_on_the_cosmetic_pool()
    {
        var session = await CreateSessionAsync();
        _economy.Seed(session.Account, balances: [("jetcoin", 400)]);

        var draw = await PostAsync(session, "/gacha/kickerDraw", new { gachaId = 1, count = 1 });

        var drop = Assert.Single(draw.GetProperty("dropKickerCostumeList").EnumerateArray().ToList());
        Assert.Equal(2010101, drop.GetProperty("kickerCostumeId").GetInt32());
        Assert.Equal(1, drop.GetProperty("kickerId").GetInt32());
        Assert.NotEmpty(draw.GetProperty("receivedUserKickerList").EnumerateArray());
        Assert.Equal(100, ItemAmount(draw.GetProperty("userItemList").EnumerateArray().ToList(), 1));

        Assert.Equal(100, _economy.BalanceOf(session.Account, "jetcoin"));
        Assert.Equal(1, _economy.ItemsOf(session.Account, "skin:2010101"));

        var roll = Assert.Single(CallsFor(session.Account, "/api/player/economy/gacha/roll"));
        using var body = JsonDocument.Parse(roll.Body!);
        Assert.Equal("cosmetic.standard", body.RootElement.GetProperty("pool_id").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task Kicker_draw_answers_the_game_error_when_the_player_cannot_pay()
    {
        var session = await CreateSessionAsync();

        var draw = await PostAsync(session, "/gacha/kickerDraw", new { gachaId = 1, count = 1 }, expectStatusZero: false);

        Assert.Equal(JsonValueKind.Object, draw.ValueKind);
        Assert.Equal(0, _economy.ItemsOf(session.Account, "skin:2010101"));
        Assert.Single(CallsFor(session.Account, "/api/player/economy/gacha/roll"));
    }

    [Fact]
    public async Task Disc_draw_is_retired_answers_no_drops_and_asks_economy_for_nothing()
    {
        var session = await CreateSessionAsync();
        _economy.Seed(session.Account, balances: [("jetcoin", 400)]);

        var draw = await PostAsync(session, "/gacha/discDraw", new { gachaId = 1, count = 1 });

        Assert.Empty(draw.GetProperty("dropDiscList").EnumerateArray());
        Assert.Empty(draw.GetProperty("receivedUserDiscList").EnumerateArray());
        Assert.Equal(0, draw.GetProperty("drawableCount").GetInt32());
        Assert.True(draw.GetProperty("completedDisplayFlag").GetBoolean());
        Assert.Equal(8, draw.GetProperty("userItemList").GetArrayLength());
        // Nothing was charged and nothing was drawn.
        Assert.Empty(CallsFor(session.Account, "/api/player/economy/gacha/roll"));
        Assert.Equal(400, _economy.BalanceOf(session.Account, "jetcoin"));
    }

    [Fact]
    public async Task With_economy_unreachable_a_startup_fails_closed_in_the_game_contract()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Otomo:Economy:Enabled"] = "true",
                    ["Otomo:Economy:BaseUrl"] = "http://127.0.0.1:1/",
                    ["Otomo:Economy:Timeout"] = "00:00:02"
                }));
        });

        var session = await CreateSessionAsync(factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/startup/index")
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes("{}"), session.Key, new byte[16]))
        };
        request.Headers.Host = Host;
        using var response = await session.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("1", response.Headers.GetValues("x-app-status-code").Single());
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("economy-unavailable", body.GetProperty("error").GetString());
    }

    private IReadOnlyList<FakeOtomoEconomy.RecordedRequest> CallsFor(string account, string path) =>
        _economy.Requests
            .Where(call => call.Path == path && FakeOtomoEconomy.AccountOf(call.Authorization) == account)
            .ToList();

    private static int ItemAmount(List<JsonElement> rows, int itemId) =>
        rows.Single(row => row.GetProperty("itemId").GetInt32() == itemId).GetProperty("amount").GetInt32();

    private async Task<DemoSession> CreateSessionAsync(WebApplicationFactory<Program>? factory = null)
    {
        var client = (factory ?? _factory).CreateClient();
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
        return new DemoSession(client, key, response.Headers.GetValues("x-app-user-id").Single(), account!);
    }

    private static async Task<JsonElement> PostAsync(DemoSession session, string path, object? body,
        bool expectStatusZero = true)
    {
        var json = body is null ? "{}" : JsonSerializer.Serialize(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(json), session.Key, new byte[16]))
        };
        request.Headers.Host = Host;
        using var response = await session.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectStatusZero ? "0" : "1", response.Headers.GetValues("x-app-status-code").Single());

        var decrypted = D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), session.Key);
        using var document = JsonDocument.Parse(decrypted);
        return document.RootElement.Clone();
    }

    private sealed record DemoSession(HttpClient Client, byte[] Key, string UserId, string Account);
}
