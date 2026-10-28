using System.Net;
using System.Text;
using System.Text.Json;
using KickFlight.BootstrapApi;
using KickFlight.BootstrapApi.Otomo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The shop screen's DTO shapes and the gacha masters - the parts of the storefront that are the client's contract
// rather than a value. What a purchase or a draw *does* is economy's, and EconomyEndToEndTests covers that; these
// tests are here so a field the client deserializes cannot go missing while the value side is rewritten.
public sealed class ShopGachaTests : IClassFixture<ServerTestHostFixture>
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";

    // The Item master rows the round adds on top of the four legacy counters.
    private const int GearMaterialItemId = 5;      // GoodsType 202, the gear stamps the gear screen counts

    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeOtomoEconomy _economy = OtomoTestEnvironment.Economy;

    public ShopGachaTests(ServerTestHostFixture fixture)
    {
        _factory = fixture.Factory;
    }

    [Fact]
    public async Task Startup_carries_the_eight_item_rows_with_no_stock_of_its_own()
    {
        // A player who owns nothing: the adapter has no demo stock to fall back on, so every counter is economy's
        // answer, and economy says zero.
        var session = await CreateSessionAsync();
        var items = (await PostAsync(session, "/startup/index", null)).GetProperty("userItemList").EnumerateArray().ToList();

        Assert.Equal(8, items.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, items.Select(item => item.GetProperty("itemId").GetInt32()).ToArray());
        Assert.All(items, item => Assert.Equal(0, item.GetProperty("amount").GetInt32()));
    }

    [Theory]
    [InlineData("/goodsShop/read")]
    [InlineData("/gacha/read")]
    [InlineData("/gacha/discInformation")]
    [InlineData("/gacha/kickerInformation")]
    [InlineData("/goodsShop/reset")]
    [InlineData("/shop/prepare")]
    [InlineData("/shop/verify")]
    [InlineData("/shop/option")]
    [InlineData("/shop/birthday")]
    public async Task Endpoints_answer_status_zero_with_an_object(string path)
    {
        var session = await CreateSessionAsync();
        var body = await PostAsync(session, path, null);
        Assert.Equal(JsonValueKind.Object, body.ValueKind);
    }

    [Fact]
    public async Task Goods_shop_reset_repeats_the_item_list_and_the_products()
    {
        var session = await CreateSessionAsync();
        var reset = await PostAsync(session, "/goodsShop/reset", null);

        Assert.Equal(8, reset.GetProperty("userItemList").GetArrayLength());
        Assert.Equal(4, reset.GetProperty("goodsShopProductList").GetArrayLength());
    }

    // The gacha DTOs, field for field: a missing field is a NullReferenceException while the client deserializes.
    [Fact]
    public async Task Draw_and_information_responses_carry_every_field_of_their_dto()
    {
        var session = await CreateSessionAsync();
        // The kicker draw is a real roll on economy, so this player needs the pool's price to draw once.
        _economy.Seed(session.Account, balances: [("jetcoin", 400)]);

        // The disc draw is retired: the DTO is complete, the drops are empty, and the tab says it is finished.
        var discDraw = await PostAsync(session, "/gacha/discDraw", new { gachaId = 1, count = 1 });
        AssertFields(discDraw,
            "dropDiscList", "receivedUserDiscList", "receivedUserItemList", "userItemList", "gachaDropCountRewardList",
            "modelId", "effectTopImageFilename", "effectBottomImageFilename", "capsuleEffectType", "drawableCount",
            "drawableDatetime", "completedDisplayFlag", "userPresentList", "userMissionProgressList",
            "userDailyRandomMissionTaskList", "receivedRewardList", "premiumFlag");
        Assert.Empty(discDraw.GetProperty("dropDiscList").EnumerateArray());
        Assert.True(discDraw.GetProperty("completedDisplayFlag").GetBoolean());

        var kickerDraw = await PostAsync(session, "/gacha/kickerDraw", new { gachaId = 1, count = 1 });
        AssertFields(kickerDraw,
            "dropKickerCostumeList", "receivedUserKickerList", "userItemList", "effectTopImageFilename",
            "effectBottomImageFilename", "userPresentList", "userMissionProgressList",
            "userDailyRandomMissionTaskList", "receivedRewardList", "gachaBoxStatus");
        AssertFields(kickerDraw.GetProperty("dropKickerCostumeList")[0], "kickerId", "kickerCostumeId");
        AssertFields(kickerDraw.GetProperty("receivedUserKickerList")[0], "kickerId", "kickerCostumeId", "userKickerCostumeList");
        AssertFields(kickerDraw.GetProperty("gachaBoxStatus"), "acquiredAmount", "totalAmount");

        var discInformation = await PostAsync(session, "/gacha/discInformation", new { gachaGroupId = 1 });
        AssertFields(discInformation,
            "informationText", "noticeText", "discGachaGroupInformationList", "dropDiscForceList", "lotteryDropDiscForceList");
        var group = discInformation.GetProperty("discGachaGroupInformationList")[0];
        AssertFields(group, "lotteryId", "name", "lotteryDiscRarityList", "lotteryDropDiscList");
        Assert.NotEmpty(group.GetProperty("lotteryDropDiscList").EnumerateArray().ToList());
        Assert.NotEmpty(group.GetProperty("lotteryDiscRarityList").EnumerateArray().ToList());
        AssertFields(group.GetProperty("lotteryDropDiscList")[0], "discId", "pickupFlag", "newFlag", "dropRate");

        var kickerInformation = await PostAsync(session, "/gacha/kickerInformation", new { gachaGroupId = 2 });
        AssertFields(kickerInformation, "informationText", "noticeText", "boxFlag", "sideBySideFlag", "pickupFlag", "lotteryDropKickerList");
        Assert.NotEmpty(kickerInformation.GetProperty("lotteryDropKickerList").EnumerateArray().ToList());
        AssertFields(kickerInformation.GetProperty("lotteryDropKickerList")[0], "kickerCostumeId", "pickupFlag", "newFlag", "dropRate");
    }

    // The home payload the shop screen is built from: HomeResponseData.discGachaGroupList / kickerGachaGroupList feed
    // ShopDiscScroller._data, so both must carry a group and a gacha row (an empty list is what made GetCellHeight
    // throw).
    [Fact]
    public async Task Home_lists_both_gacha_groups_and_the_four_goods_shop_products()
    {
        var session = await CreateSessionAsync();
        var home = await PostAsync(session, "/home/index", null);

        var discGroups = home.GetProperty("discGachaGroupList").EnumerateArray().ToList();
        var kickerGroups = home.GetProperty("kickerGachaGroupList").EnumerateArray().ToList();
        Assert.Single(discGroups);
        Assert.Single(kickerGroups);
        Assert.Equal(1, discGroups[0].GetProperty("gachaGroupId").GetInt32());
        Assert.Equal(2, kickerGroups[0].GetProperty("gachaGroupId").GetInt32());
        AssertFields(discGroups[0],
            "gachaGroupId", "name", "gachaType", "bannerImageFilename", "startDatetime", "endDatetime",
            "boxFlag", "stepFlag", "newFlag", "completedDisplayFlag", "remainingTimeDisplayFlag", "gachaList",
            "gachaDropCountRewardList");
        Assert.Single(discGroups[0].GetProperty("gachaList").EnumerateArray().ToList());
        AssertFields(discGroups[0].GetProperty("gachaList")[0],
            "id", "dropAmount", "drawableCount", "drawableDatetime", "costGoodsType", "costGoodsId", "costAmount",
            "gachaBoxStatus", "stepNumber", "effectTopImageFilename", "effectBottomImageFilename");
        // The disc gacha is retired but still priced from its own master (item 8 / GoodsType 402), and not drawable.
        Assert.Equal(402, discGroups[0].GetProperty("gachaList")[0].GetProperty("costGoodsType").GetInt32());
        Assert.Equal(8, discGroups[0].GetProperty("gachaList")[0].GetProperty("costGoodsId").GetInt32());
        Assert.Equal(0, discGroups[0].GetProperty("gachaList")[0].GetProperty("drawableCount").GetInt32());
        // The kicker gacha is economy's pool, and its banner carries the pool's own price.
        Assert.Equal(101, kickerGroups[0].GetProperty("gachaList")[0].GetProperty("costGoodsType").GetInt32());
        Assert.Equal(300, kickerGroups[0].GetProperty("gachaList")[0].GetProperty("costAmount").GetInt32());

        var products = home.GetProperty("goodsShopProductList").EnumerateArray().ToList();
        Assert.Equal(4, products.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, products.Select(p => p.GetProperty("id").GetInt32()).ToArray());
        AssertFields(products[0],
            "id", "goodsShopType", "name", "costGoodsType", "costGoodsId", "costAmount", "resetType",
            "startDatetime", "endDatetime", "resetDatetime", "limitedCount", "purchasableCount", "newFlag",
            "goodsShopProductGoodsList");
        AssertFields(products[0].GetProperty("goodsShopProductGoodsList")[0], "goodsType", "goodsId", "amount");
        Assert.Equal(JetCoinGoodsType, products[0].GetProperty("costGoodsType").GetInt32());
        Assert.Empty(home.GetProperty("shopProductList").EnumerateArray().ToList());
    }

    [Theory]
    [InlineData("GachaGroup")]
    [InlineData("DiscGacha")]
    [InlineData("KickerGacha")]
    [InlineData("LotteryCost")]
    [InlineData("LotteryDiscRarity")]
    [InlineData("LotteryDropDisc")]
    [InlineData("LotteryDropKicker")]
    [InlineData("LotteryDiscDropAmount")]
    [InlineData("LotteryDropCountReward")]
    [InlineData("LotteryGearRarity")]
    public async Task Gacha_masters_are_served_and_decrypt_to_a_non_empty_array(string masterName)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/demo-master/{masterName}");
        request.Headers.Host = Host;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var decrypted = D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), Encoding.ASCII.GetBytes(CommonCode));
        using var document = JsonDocument.Parse(decrypted);
        var rows = document.RootElement.EnumerateArray().ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.True(row.TryGetProperty("id", out _), $"{masterName} row without id"));
    }

    // LotteryDropDisc has to cover the whole disc master: the (retired) disc tab's information page still lists it.
    [Fact]
    public async Task Lottery_drop_disc_covers_every_disc_and_lottery_drop_kicker_every_costume()
    {
        var dropDisc = await ReadMasterAsync("LotteryDropDisc");
        var discIds = LoadMasterIds("masters_disc.json");
        Assert.Equal(discIds.Count, dropDisc.Count);
        Assert.Equal(discIds.OrderBy(id => id).ToArray(), dropDisc.Select(r => r.GetProperty("discId").GetInt32()).OrderBy(id => id).ToArray());
        // Uniform: every disc the same 1/N chance.
        Assert.All(dropDisc, row => Assert.Equal(1.0 / discIds.Count, row.GetProperty("dropRate").GetDouble(), 6));

        var dropKicker = await ReadMasterAsync("LotteryDropKicker");
        var costumeRowIds = LoadMasterIds("masters_kicker_costume.json");
        Assert.Equal(costumeRowIds.Count, dropKicker.Count);
        Assert.Equal(costumeRowIds.OrderBy(id => id).ToArray(),
            dropKicker.Select(r => r.GetProperty("kickerCostumeId").GetInt32()).OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task Served_item_master_has_the_eight_rows_of_the_round()
    {
        var items = await ReadMasterAsync("Item");
        Assert.Equal(8, items.Count);
        Assert.Equal([101, 102, 302, 701, 202, 303, 401, 402], items.Select(r => r.GetProperty("goodsType").GetInt32()).ToArray());
        Assert.All(items, row => Assert.True(row.GetProperty("maxAmount").GetInt32() > 0));
    }

    // GoodsType.JetCoinFree: the currency every goods shop product is priced in.
    private const int JetCoinGoodsType = 101;

    private static void AssertFields(JsonElement element, params string[] expected)
    {
        var actual = element.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(expected.Length, actual.Count);
        foreach (var name in expected)
        {
            Assert.Contains(name, actual);
        }
    }

    private async Task<List<JsonElement>> ReadMasterAsync(string masterName)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/demo-master/{masterName}");
        request.Headers.Host = Host;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var decrypted = D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), Encoding.ASCII.GetBytes(CommonCode));
        using var document = JsonDocument.Parse(decrypted);
        return document.RootElement.EnumerateArray().Select(row => row.Clone()).ToList();
    }

    private static List<int> LoadMasterIds(string fileName)
    {
        var path = Path.Combine(RepositoryPaths.FindRoot(AppContext.BaseDirectory), "config", fileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateArray().Select(row => row.GetProperty("id").GetInt32()).ToList();
    }

    private async Task<DemoSession> CreateSessionAsync()
    {
        var client = _factory.CreateClient();
        var uuid = Guid.NewGuid().ToString("N");
        var key = Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");
        var payload = JsonSerializer.Serialize(new { hash = Encoding.ASCII.GetString(key), uuid });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/index")
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(payload), Encoding.ASCII.GetBytes(CommonCode), new byte[16]))
        };
        request.Headers.Host = Host;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        client.DefaultRequestHeaders.Add("x-app-access-token", response.Headers.GetValues("x-app-access-token").Single());
        var account = OtomoTestEnvironment.Server.AccountFor(OtomoDeviceId.FromDeviceUuid(uuid)) ?? "";
        return new DemoSession(client, key, response.Headers.GetValues("x-app-user-id").Single(), account);
    }

    private static async Task<JsonElement> PostAsync(DemoSession session, string path, object? body, bool expectStatusZero = true)
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
