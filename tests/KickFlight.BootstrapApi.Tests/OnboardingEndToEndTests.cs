using System.Net;
using System.Text;
using System.Text.Json;
using KickFlight.BootstrapApi;
using KickFlight.BootstrapApi.Otomo;
using KickFlight.BootstrapApi.PlayerStore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// A fresh account from /auth/index to /home/index, and the disc level-up, against the fake economy with the internal
// plane on (grants and consumes keyed and idempotent, as the real service is). Each test builds its own host because
// Tutorial:Ladder is read once at boot.
public sealed class OnboardingEndToEndTests : IDisposable
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";

    private readonly FakeOtomoEconomy _economy = OtomoTestEnvironment.Economy;
    private readonly string _keyFile = Path.Combine(Path.GetTempPath(), $"kf-gameserver-{Guid.NewGuid():N}.key");
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public OnboardingEndToEndTests() => File.WriteAllText(_keyFile, "test-gameserver-key");

    [Fact]
    public async Task A_fresh_account_is_paid_the_starter_disc_force_once()
    {
        var host = Host_(ladder: false);
        var session = await CreateSessionAsync(host);

        var first = await PostAsync(session, "/startup/index", null);
        var second = await PostAsync(session, "/startup/index", null);

        Assert.Equal(FakeOtomoEconomy.StarterDiscForce, ItemAmount(first, EconomyCatalog.DiscForceItemId));
        Assert.Equal(FakeOtomoEconomy.StarterDiscForce, ItemAmount(second, EconomyCatalog.DiscForceItemId));
        Assert.Equal(FakeOtomoEconomy.StarterDiscForce, _economy.BalanceOf(session.Account, "disc_force"));
        var grants = StarterGrantsFor(session.Account);
        // Paid once, and the save remembers it, so the second startup does not even ask.
        var grant = Assert.Single(grants);
        Assert.Contains($"\"key\":\"starter.disc_force:{session.Account}\"", grant.Body);
    }

    [Fact]
    public async Task An_account_that_already_has_a_name_is_not_paid_the_starter()
    {
        var host = Host_(ladder: false);
        var session = await CreateSessionAsync(host);
        await PostAsync(session, "/tutorial/end", new { name = "Veteran" });

        var startup = await PostAsync(session, "/startup/index", null);

        Assert.Equal(0, ItemAmount(startup, EconomyCatalog.DiscForceItemId));
        Assert.Empty(StarterGrantsFor(session.Account));
    }

    [Fact]
    public async Task By_default_a_fresh_account_takes_the_short_onboarding_to_home()
    {
        var host = Host_(ladder: null);
        var session = await CreateSessionAsync(host);

        var startup = await PostAsync(session, "/startup/index", null);
        Assert.Equal(DemoSessionApi.TutorialStatusInputName, startup.GetProperty("tutorialProgressStatus").GetInt32());
        // The name window preselects the first owned kicker other than kicker 1.
        Assert.Contains(startup.GetProperty("userKickerList").EnumerateArray(),
            kicker => kicker.GetProperty("kickerId").GetInt32() != 1);

        await PostAsync(session, "/tutorial/end", new { name = "Shorty" });
        var named = await PostAsync(session, "/startup/index", null);
        Assert.Equal(DemoSessionApi.TutorialStatusComplete, named.GetProperty("tutorialProgressStatus").GetInt32());
        var home = await PostAsync(session, "/home/index", null);
        Assert.Equal("Shorty", home.GetProperty("userPlayer").GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_ladder_is_not_offered_to_an_account_that_cannot_pay_the_build_up_lesson()
    {
        // The starter pays 300 and the lesson disc (3010003, rarity 1, level 2 -> 3) costs 400: the client would
        // grey the button out and the tutorial could not continue, so the account gets the name window.
        var host = Host_(ladder: true);
        var session = await CreateSessionAsync(host);

        var startup = await PostAsync(session, "/startup/index", null);

        Assert.Equal(FakeOtomoEconomy.StarterDiscForce, ItemAmount(startup, EconomyCatalog.DiscForceItemId));
        Assert.Equal(DemoSessionApi.TutorialStatusInputName, startup.GetProperty("tutorialProgressStatus").GetInt32());
    }

    [Fact]
    public async Task With_the_ladder_on_a_fresh_account_walks_every_tutorial_step_to_home()
    {
        var host = Host_(ladder: true);
        var session = await CreateSessionAsync(host);
        // Enough for the lesson on top of the starter.
        _economy.Seed(session.Account, balances: [("disc_force", 100)]);

        var startup = await PostAsync(session, "/startup/index", null);
        Assert.Equal(400, ItemAmount(startup, EconomyCatalog.DiscForceItemId));
        Assert.Equal(DemoSessionApi.TutorialStatusStart, startup.GetProperty("tutorialProgressStatus").GetInt32());

        // TutorialStartResponseData: every kicker the kicker-change step can show, each with a costume.
        var start = await PostAsync(session, "/tutorial/start", null);
        var kickers = start.GetProperty("userKickerList").EnumerateArray().ToList();
        Assert.Contains(kickers, kicker => kicker.GetProperty("kickerId").GetInt32() != 1);
        Assert.All(kickers, kicker => Assert.NotEmpty(kicker.GetProperty("userKickerCostumeList").EnumerateArray()));

        for (var status = 101; status <= 112; status++)
        {
            await PostAsync(session, "/tutorial/battleProgress", new { tutorialProgressStatus = status });
        }
        await PostAsync(session, "/tutorial/battleEnd", null);
        Assert.Equal(113, (await PostAsync(session, "/startup/index", null)).GetProperty("tutorialProgressStatus").GetInt32());

        // TutorialCapsuleOpenResponseData: the disc the capsule drops is in the received list, with every column.
        var capsule = await PostAsync(session, "/tutorial/capsuleOpen", null);
        AssertMembers(capsule, "dropDiscList", "receivedUserDiscList", "receivedUserItemList", "userPresentList",
            "receivedRewardList");
        var dropped = Assert.Single(capsule.GetProperty("dropDiscList").EnumerateArray().ToList());
        var received = Assert.Single(capsule.GetProperty("receivedUserDiscList").EnumerateArray().ToList());
        Assert.Equal(dropped.GetProperty("discId").GetInt32(), received.GetProperty("discId").GetInt32());
        AssertUserDisc(received);

        // TutorialDiscBuildupResponseData: the client's own placeholder disc, one level up, nothing spent.
        var lesson = await PostAsync(session, "/tutorial/discBuildup", new { discId = DemoSessionApi.TutorialDeckDiscIds[0] });
        var lessonDisc = lesson.GetProperty("userDisc");
        AssertUserDisc(lessonDisc);
        Assert.Equal(3010003, lessonDisc.GetProperty("discId").GetInt32());
        Assert.Equal(3, lessonDisc.GetProperty("level").GetInt32());
        Assert.Equal(1, lessonDisc.GetProperty("amount").GetInt32());
        Assert.Equal(EconomyCatalog.DiscForceItemId, lesson.GetProperty("userItem").GetProperty("itemId").GetInt32());
        Assert.Equal(400, lesson.GetProperty("userItem").GetProperty("amount").GetInt32());
        Assert.Equal(400, _economy.BalanceOf(session.Account, "disc_force"));
        Assert.Equal(0, _economy.ItemsOf(session.Account, "disc:3010003"));

        var draw = await PostAsync(session, "/tutorial/gachaDiscDraw", null);
        AssertMembers(draw, "dropDiscList", "receivedUserDiscList", "receivedUserItemList", "userItemList", "modelId",
            "capsuleEffectType", "userPresentList", "receivedRewardList");
        Assert.Equal(8, draw.GetProperty("userItemList").GetArrayLength());

        var kickerDraw = await PostAsync(session, "/tutorial/gachaKickerDraw", null);
        AssertMembers(kickerDraw, "receivedUserKickerList", "userItemList", "userPresentList", "receivedRewardList");
        // The client shows receivedUserKickerList[i].kickerCostumeId as the drop: it must be the granted skin
        // (tutorial.kicker = skin:2010201), not kicker 1's standard colour that every account already has.
        var shown = Assert.Single(kickerDraw.GetProperty("receivedUserKickerList").EnumerateArray().ToList());
        Assert.Equal(1, shown.GetProperty("kickerId").GetInt32());
        Assert.Equal(2010201, shown.GetProperty("kickerCostumeId").GetInt32());
        Assert.Contains(kickerDraw.GetProperty("receivedRewardList").EnumerateArray(), reward =>
            reward.GetProperty("goodsType").GetInt32() == 201 && reward.GetProperty("goodsId").GetInt32() == 2010201);

        await PostAsync(session, "/tutorial/kickerChange", new { kickerId = 2, kickerCostumeId = 2020101 });
        var beforeName = await PostAsync(session, "/startup/index", null);
        Assert.Equal(DemoSessionApi.TutorialStatusInputName, beforeName.GetProperty("tutorialProgressStatus").GetInt32());
        // A client in the tutorial downloads no Mission masters, so it gets no mission rows and no badge, even with
        // a step economy says is claimable.
        Assert.Empty(beforeName.GetProperty("userMissionProgressList").EnumerateArray());
        var tutorialHome = await PostAsync(session, "/home/index", null);
        Assert.Empty(tutorialHome.GetProperty("userMissionProgressList").EnumerateArray());
        Assert.Equal(0, tutorialHome.GetProperty("userMissionTaskStatusNotification").GetProperty("completionCount").GetInt32());

        await PostAsync(session, "/tutorial/end", new { name = "Laddered" });
        var named = await PostAsync(session, "/startup/index", null);
        Assert.Equal(DemoSessionApi.TutorialStatusComplete, named.GetProperty("tutorialProgressStatus").GetInt32());
        // The tutorial's rewards are the player's: the disc and the skin, each paid once.
        Assert.Equal(1, _economy.ItemsOf(session.Account, "disc:3010013"));
        Assert.Equal(1, _economy.ItemsOf(session.Account, "skin:2010201"));
        Assert.Equal(400, ItemAmount(named, EconomyCatalog.DiscForceItemId));
        var home = await PostAsync(session, "/home/index", null);
        Assert.Equal("Laddered", home.GetProperty("userPlayer").GetProperty("name").GetString());

        // Started past the tutorial, the client has every master: the rows are served and the badge counts them.
        var mission = Assert.Single(named.GetProperty("userMissionProgressList").EnumerateArray().ToList());
        Assert.Equal(102, mission.GetProperty("missionId").GetInt32());
        Assert.Equal(1, home.GetProperty("userMissionTaskStatusNotification").GetProperty("completionCount").GetInt32());
        var missions = await PostAsync(session, "/mission/index", null);
        var task = Assert.Single(Assert.Single(missions.GetProperty("userMissionProgressList").EnumerateArray().ToList())
            .GetProperty("userMissionTaskStatusList").EnumerateArray().ToList());
        Assert.Equal(10201, task.GetProperty("missionTaskId").GetInt32());
        Assert.Equal(2, task.GetProperty("missionTaskStatus").GetInt32());
        Assert.EndsWith("Z", task.GetProperty("expirationDatetime").GetString());
    }

    [Fact]
    public async Task Disc_buildup_raises_a_level_spends_once_and_shows_the_new_counters()
    {
        var host = Host_(ladder: false);
        var session = await CreateSessionAsync(host);
        // Bombster (rarity 1) starts at level 2: 2 -> 3 costs 400 Disc Force and no duplicate.
        _economy.Seed(session.Account, balances: [("disc_force", 1700)], items: [("disc:3010003", 5)]);
        var startup = await PostAsync(session, "/startup/index", null);
        Assert.Equal(2000, ItemAmount(startup, EconomyCatalog.DiscForceItemId));
        Assert.Equal(2, DiscRow(startup, 3010003).GetProperty("level").GetInt32());

        var raised = await PostAsync(session, "/disc/buildup", new { discId = 3010003 }, cacheId: "first-tap");
        Assert.Equal(3, raised.GetProperty("userDisc").GetProperty("level").GetInt32());
        Assert.Equal(5, raised.GetProperty("userDisc").GetProperty("amount").GetInt32());
        Assert.Equal(1600, raised.GetProperty("userItem").GetProperty("amount").GetInt32());
        AssertMembers(raised, "userDisc", "userItem", "userMissionProgressList");

        // The client's retry of that same request carries the same cache id: answered, not spent again.
        var retried = await PostAsync(session, "/disc/buildup", new { discId = 3010003 }, cacheId: "first-tap");
        Assert.Equal(3, retried.GetProperty("userDisc").GetProperty("level").GetInt32());
        Assert.Equal(1600, retried.GetProperty("userItem").GetProperty("amount").GetInt32());
        Assert.Equal(1600, _economy.BalanceOf(session.Account, "disc_force"));
        Assert.Single(ConsumesFor(session.Account));

        // A new tap is the next level: 3 -> 4 costs 1200 and four duplicates, and the disc itself stays.
        var next = await PostAsync(session, "/disc/buildup", new { discId = 3010003 }, cacheId: "second-tap");
        Assert.Equal(4, next.GetProperty("userDisc").GetProperty("level").GetInt32());
        Assert.Equal(400, next.GetProperty("userItem").GetProperty("amount").GetInt32());
        Assert.Equal(1, _economy.ItemsOf(session.Account, "disc:3010003"));
        // The client's amount is cumulative: the one copy held plus the four the levels spent.
        Assert.Equal(5, next.GetProperty("userDisc").GetProperty("amount").GetInt32());

        var after = await PostAsync(session, "/startup/index", null);
        Assert.Equal(400, ItemAmount(after, EconomyCatalog.DiscForceItemId));
        Assert.Equal(4, DiscRow(after, 3010003).GetProperty("level").GetInt32());
        Assert.Equal(5, DiscRow(after, 3010003).GetProperty("amount").GetInt32());
    }

    // The disc-upgrade badge fact is the economy advance's answer, and nothing else.
    //
    // BadgeAwardRules counts a verified disc upgrade. It used to count the adapter's own write to the save's discLevels
    // key; that key is gone, the level is attrs.level on the disc's possession in economy's inventory, and the fact is
    // now POST /internal/economy/items/advance's answer. Both halves are asserted here, on the same request and the same
    // spend: with the advance answering, the counter moves; with the advance unavailable, it does not - so the badge can
    // only be riding on economy's answer, not on anything this adapter decided locally.
    [Fact]
    public async Task A_disc_upgrade_counts_for_the_badges_only_when_the_economy_advance_answers_it()
    {
        var host = Host_(ladder: false);
        var store = host.Services.GetRequiredService<IPlayerStore>();

        var session = await CreateSessionAsync(host);
        _economy.Seed(session.Account, balances: [("disc_force", 2000)], items: [("disc:3010003", 5)]);
        await PostAsync(session, "/startup/index", null);

        var raised = await PostAsync(session, "/disc/buildup", new { discId = 3010003 }, cacheId: "badge-tap");
        Assert.Equal(3, raised.GetProperty("userDisc").GetProperty("level").GetInt32());

        // The advance names the possession, moves the levels the consume bought, and carries its own key: economy keys idempotency
        // per player across every operation, so a key shared with the consume would replay the consume instead.
        var advance = _economy.Requests.Last(call => call.Path == "/internal/economy/items/advance");
        Assert.Contains($"\"player_id\":\"{session.Account}\"", advance.Body);
        Assert.Contains("\"item_id\":\"disc:3010003\"", advance.Body);
        Assert.Contains("\"field\":\"level\"", advance.Body);
        Assert.Contains("\"by\":3", advance.Body); // the seeded disc has no attrs.level, so 0 -> the tapped level 3
        Assert.Contains("level-up-advance:", advance.Body);
        Assert.DoesNotContain("\"key\":\"level-up:", advance.Body);

        var counted = Assert.IsType<SessionState>(store.TryLoad(LinkOf(store, session.Account).PlayerId));
        Assert.Equal(1, counted.DiscUpgradeCount);

        // The same level-up with the advance refused: the charge still happens, and no badge is counted, because the
        // only thing that can tell the adapter the level moved is the answer that never arrived.
        var refused = await CreateSessionAsync(host);
        _economy.Seed(refused.Account, balances: [("disc_force", 2000)], items: [("disc:3010003", 5)]);
        await PostAsync(refused, "/startup/index", null);
        _economy.AdvanceUnavailable = true;
        try
        {
            using var request = Request(refused, "/disc/buildup", new { discId = 3010003 }, "refused-tap");
            using var response = await refused.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            _economy.AdvanceUnavailable = false;
        }

        var uncounted = Assert.IsType<SessionState>(store.TryLoad(LinkOf(store, refused.Account).PlayerId));
        Assert.Equal(0, uncounted.DiscUpgradeCount);
        // And the disc is where it was: a level the adapter could not be told about is not a level it writes itself.
        Assert.Equal(0, _economy.LevelOf(refused.Account, "disc:3010003"));
    }

    private static OtomoPlayer LinkOf(IPlayerStore store, string account) =>
        Assert.Single(store.FindOtomoPlayers([account]));

    [Fact]
    public async Task Disc_buildup_that_cannot_be_paid_is_refused_in_the_error_contract_and_changes_nothing()
    {
        var host = Host_(ladder: false);
        var session = await CreateSessionAsync(host);
        _economy.Seed(session.Account, items: [("disc:3010003", 1)]);
        await PostAsync(session, "/startup/index", null); // 300 starter Disc Force, below the 400 the level costs

        var (status, code, error) = await PostRefusedAsync(session, "/disc/buildup", new { discId = 3010003 });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("2502", code);
        Assert.Equal("2502", error.GetProperty("error").GetProperty("code").GetString());

        // Not owned at all.
        var (_, unownedCode, _) = await PostRefusedAsync(session, "/disc/buildup", new { discId = 3010001 });
        Assert.Equal("2502", unownedCode);

        var after = await PostAsync(session, "/startup/index", null);
        Assert.Equal(2, DiscRow(after, 3010003).GetProperty("level").GetInt32());
        Assert.Equal(300, ItemAmount(after, EconomyCatalog.DiscForceItemId));
        Assert.Equal(1, _economy.ItemsOf(session.Account, "disc:3010003"));
    }

    // ResponseUserDisc as the client deserializes it: discId, amount, level and registerDatetime.
    private static void AssertUserDisc(JsonElement disc) =>
        AssertMembers(disc, "discId", "amount", "level", "registerDatetime");

    private static void AssertMembers(JsonElement body, params string[] members)
    {
        foreach (var member in members)
        {
            Assert.True(body.TryGetProperty(member, out var value), $"missing {member}");
            Assert.NotEqual(JsonValueKind.Null, value.ValueKind);
        }
    }

    private static JsonElement DiscRow(JsonElement startup, int discId) =>
        startup.GetProperty("userDiscList").EnumerateArray().Single(disc => disc.GetProperty("discId").GetInt32() == discId);

    private static int ItemAmount(JsonElement payload, int itemId) =>
        payload.GetProperty("userItemList").EnumerateArray()
            .Single(row => row.GetProperty("itemId").GetInt32() == itemId).GetProperty("amount").GetInt32();

    private List<FakeOtomoEconomy.RecordedRequest> StarterGrantsFor(string account) =>
        _economy.Requests.Where(call => call.Path == "/internal/economy/grants" && call.Body is not null &&
                                        call.Body.Contains("\"class\":\"starter.disc_force\"") &&
                                        call.Body.Contains($"\"player_id\":\"{account}\"")).ToList();

    private List<FakeOtomoEconomy.RecordedRequest> ConsumesFor(string account) =>
        _economy.Requests.Where(call => call.Path == "/internal/economy/consume" && call.Body is not null &&
                                        call.Body.Contains($"\"player_id\":\"{account}\"")).ToList();

    // ladder: null leaves Tutorial:Ladder unset, which is the shipped default.
    private WebApplicationFactory<Program> Host_(bool? ladder)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Otomo:Economy:ProgressEnabled"] = "true",
            ["Otomo:Economy:InternalBaseUrl"] = _economy.BaseUrl,
            ["Otomo:Economy:ServiceKeyFile"] = _keyFile
        };
        if (ladder is not null) settings["Tutorial:Ladder"] = ladder.Value ? "true" : "false";
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

    private static HttpRequestMessage Request(DemoSession session, string path, object? body, string? cacheId)
    {
        var json = body is null ? "{}" : JsonSerializer.Serialize(body);
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(json), session.Key, new byte[16]))
        };
        request.Headers.Host = Host;
        if (cacheId is not null) request.Headers.Add("x-app-response-cache-id", cacheId);
        return request;
    }

    private static async Task<JsonElement> PostAsync(DemoSession session, string path, object? body,
        string? cacheId = null)
    {
        using var request = Request(session, path, body, cacheId);
        using var response = await session.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0", response.Headers.GetValues("x-app-status-code").Single());
        var decrypted = D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), session.Key);
        using var document = JsonDocument.Parse(decrypted);
        return document.RootElement.Clone();
    }

    // A refusal in the game's error contract: a plaintext {"error":{code,title,message}} body.
    private static async Task<(HttpStatusCode Status, string Code, JsonElement Body)> PostRefusedAsync(
        DemoSession session, string path, object? body)
    {
        using var request = Request(session, path, body, Guid.NewGuid().ToString());
        using var response = await session.Client.SendAsync(request);
        var code = response.Headers.GetValues("x-app-status-code").Single();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, code, document.RootElement.Clone());
    }

    public void Dispose()
    {
        foreach (var factory in _factories) factory.Dispose();
        try { File.Delete(_keyFile); } catch (IOException) { /* best effort */ }
    }

    private sealed record DemoSession(HttpClient Client, byte[] Key, string Account);
}
