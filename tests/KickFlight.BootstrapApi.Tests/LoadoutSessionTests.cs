using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KickFlight.BootstrapApi.Otomo;
using KickFlight.BootstrapApi.PlayerStore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The loadout - decks, the active deck, costume gears - is session's Loadout (the tenant's session.loadout); the pending gear
// roll is the one part still in ext. These tests drive the real handlers against the session fake (FakeOtomoSocial.Loadout.cs),
// which keeps revisions, answers 409 with the current document and 422 against the tenant's own schema
// (Fixtures/kf.session.loadout.json), exactly as session does.
[Collection("Matchmaking static state")]
public sealed class LoadoutSessionTests : IClassFixture<ServerTestHostFixture>
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeOtomoSocial _session = OtomoTestEnvironment.Social;

    private readonly WebApplicationFactory<Program> _gearFactory;

    public LoadoutSessionTests(ServerTestHostFixture fixture)
    {
        _factory = fixture.Factory;
        _gearFactory = GearTestPlane.WithInternalPlane(fixture.Factory);
    }

    // A player on a host with economy's internal plane on, holding gear chips: what gear/create needs.
    private async Task<DemoSession> CreateGearSessionAsync()
    {
        var player = await SignInAsync(Guid.NewGuid().ToString("N"), _gearFactory);
        GearTestPlane.GiveChips(player.Account);
        return player;
    }

    private static int[] Discs(JsonElement deck) => deck.EnumerateArray().Select(id => id.GetInt32()).ToArray();

    private FakeOtomoSocial.LoadoutCall[] PlayerCalls(string account, string method) =>
        _session.LoadoutCalls.Where(call => call.PlayerId == account && call.Method == method && !call.Internal).ToArray();

    [Fact]
    public async Task A_new_player_is_given_the_starter_decks_once_with_If_Match_0_and_home_serves_them()
    {
        var player = await CreateSessionAsync();
        Assert.Equal((0, null), _session.LoadoutOf(player.Account));

        await PostAsync(player, "/startup/index", null);
        await PostAsync(player, "/startup/index", null);
        var home = await PostAsync(player, "/home/index", null);

        var (revision, document) = _session.LoadoutOf(player.Account);
        Assert.Equal(1, revision);
        using var loadout = JsonDocument.Parse(document!);
        Assert.Equal(1, loadout.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal(1, loadout.RootElement.GetProperty("active_deck").GetInt32());
        var decks = loadout.RootElement.GetProperty("decks");
        Assert.Equal(5, decks.EnumerateObject().Count());
        Assert.Equal(new[] { 3010001, 3010002, 3010003, 3010004 }, Discs(decks.GetProperty("1")));
        // Saved once: the later reads found a loadout and saved nothing. The one save was a first save.
        var save = Assert.Single(PlayerCalls(player.Account, "PUT"));
        Assert.Equal("\"0\"", save.IfMatch);
        Assert.Equal(200, save.Status);
        // Nothing of it is in ext any more.
        Assert.Equal("{}", _session.ExtOf(player.Account));
        Assert.Empty(_session.ExtWrites.Where(call => call.PlayerId == player.Account));

        Assert.Equal(1, home.GetProperty("userPlayer").GetProperty("discDeckNumber").GetInt32());
        Assert.Equal(5, home.GetProperty("userDiscDeckList").GetArrayLength());
    }

    // Session has no profile for an account until the account calls POST /me/init, and its own-card read answers 404
    // profile_not_found until then. A brand-new player must still get past the title screen: the sign-in makes the
    // profile, so the startup's loadout read finds it.
    [Fact]
    public async Task A_brand_new_account_gets_its_session_profile_at_sign_in_and_passes_startup()
    {
        var player = await CreateSessionAsync();
        Assert.True(_session.HasProfile(player.Account));

        using var response = await RawPostAsync(player, "/startup/index", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0", response.Headers.GetValues("x-app-status-code").Single());
        Assert.Equal(1, _session.LoadoutOf(player.Account).Revision);
    }

    // POST /me/init is idempotent, so every sign-in makes it; a second one leaves the profile (and its name) as it was.
    [Fact]
    public async Task Signing_in_again_keeps_the_session_profile_and_its_name()
    {
        var player = await CreateSessionAsync();
        _session.SeedName(player.Account, "Renamed");
        var before = _session.ProfileInits;

        var again = await SignInAsync(player.Uuid);
        Assert.Equal(player.Account, again.Account);
        Assert.Equal(before + 1, _session.ProfileInits);
        Assert.Equal("Renamed", _session.Ensure(player.Account).DisplayName);
        using var response = await RawPostAsync(again, "/startup/index", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_concurrent_first_save_is_a_409_and_the_loser_just_reads_what_won()
    {
        var player = await CreateSessionAsync();
        // Another request saves the first loadout between this read (404) and this save (If-Match: 0).
        _session.SimulateConcurrentSave(player.Account, _ => JsonNode.Parse(
            """{"schema_version":1,"decks":{"1":[3010050,3010051,3010052,3010053]},"costume_gears":{},"active_deck":1}""")!.AsObject());

        var home = await PostAsync(player, "/home/index", null);

        var puts = PlayerCalls(player.Account, "PUT");
        var losing = Assert.Single(puts);
        Assert.Equal(409, losing.Status);
        // It read again (404 before the save, 200 after) and served the winner's decks, not its own defaults.
        Assert.Equal([404, 200], PlayerCalls(player.Account, "GET").Select(call => call.Status).ToArray());
        var served = home.GetProperty("userDiscDeckList").EnumerateArray().Single(deck => deck.GetProperty("number").GetInt32() == 1);
        Assert.Equal(new[] { 3010050, 3010051, 3010052, 3010053 }, Discs(served.GetProperty("discIdList")));
        Assert.Equal(1, _session.LoadoutOf(player.Account).Revision);
    }

    [Fact]
    public async Task Concurrent_first_reads_save_the_starter_decks_once()
    {
        var player = await CreateSessionAsync();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PostAsync(player, "/startup/index", null)));

        Assert.Single(PlayerCalls(player.Account, "PUT"), call => call.Status == 200);
        Assert.All(PlayerCalls(player.Account, "PUT"), call => Assert.Equal("\"0\"", call.IfMatch));
        Assert.Equal(1, _session.LoadoutOf(player.Account).Revision);
    }

    [Fact]
    public async Task Disc_change_saves_the_decks_and_the_active_deck_naming_the_revision_it_read()
    {
        var player = await CreateSessionAsync();
        await PostAsync(player, "/startup/index", null);
        var change = new
        {
            discDeckNumber = 2,
            userDiscDeckList = new[] { new { number = 2, discIdList = new[] { 3010050, 3010051, 3010052, 3010053 } } }
        };

        await PostAsync(player, "/disc/change", change, cacheId: "dc-1");

        var (revision, document) = _session.LoadoutOf(player.Account);
        Assert.Equal(2, revision);
        using var loadout = JsonDocument.Parse(document!);
        Assert.Equal(2, loadout.RootElement.GetProperty("active_deck").GetInt32());
        var decks = loadout.RootElement.GetProperty("decks");
        Assert.Equal(new[] { 3010050, 3010051, 3010052, 3010053 }, Discs(decks.GetProperty("2")));
        // The decks the player did not edit are still there: the whole loadout is sent.
        Assert.Equal(5, decks.EnumerateObject().Count());
        Assert.Equal(["\"0\"", "\"1\""], PlayerCalls(player.Account, "PUT").Select(call => call.IfMatch).ToArray());

        var home = await PostAsync(player, "/home/index", null);
        Assert.Equal(2, home.GetProperty("userPlayer").GetProperty("discDeckNumber").GetInt32());
        Assert.Equal(3010050, home.GetProperty("userDiscDeckList").EnumerateArray()
            .Single(deck => deck.GetProperty("number").GetInt32() == 2).GetProperty("discIdList")[0].GetInt32());
        Assert.Empty(_session.ExtWrites.Where(call => call.PlayerId == player.Account));
    }

    [Fact]
    public async Task A_stale_revision_is_retried_from_the_document_session_returned_and_keeps_the_other_writers_change()
    {
        var player = await CreateSessionAsync();
        await PostAsync(player, "/startup/index", null);
        // A second device changes deck 3 after this request read the loadout (revision 1) and before it saves.
        _session.SimulateConcurrentSave(player.Account, current =>
        {
            var changed = JsonNode.Parse(current!.ToJsonString())!.AsObject();
            changed["decks"]!["3"] = new JsonArray(3010053, 3010052, 3010051, 3010050);
            return changed;
        });

        await PostAsync(player, "/disc/change", new
        {
            discDeckNumber = 2,
            userDiscDeckList = new[] { new { number = 2, discIdList = new[] { 3010050, 3010051, 3010052, 3010053 } } }
        });

        var puts = PlayerCalls(player.Account, "PUT");
        // first save, the stale save (If-Match 1, refused), the retry from revision 2.
        Assert.Equal([409, 200], puts.Skip(1).Select(call => call.Status).ToArray());
        Assert.Equal(["\"0\"", "\"1\"", "\"2\""], puts.Select(call => call.IfMatch).ToArray());
        var (revision, document) = _session.LoadoutOf(player.Account);
        Assert.Equal(3, revision);
        using var loadout = JsonDocument.Parse(document!);
        var decks = loadout.RootElement.GetProperty("decks");
        Assert.Equal(new[] { 3010050, 3010051, 3010052, 3010053 }, Discs(decks.GetProperty("2")));
        Assert.Equal(new[] { 3010053, 3010052, 3010051, 3010050 }, Discs(decks.GetProperty("3")));
        Assert.Equal(2, loadout.RootElement.GetProperty("active_deck").GetInt32());
    }

    [Fact]
    public async Task A_loadout_that_keeps_changing_is_tried_three_times_and_then_the_503_contract()
    {
        var player = await CreateSessionAsync();
        await PostAsync(player, "/startup/index", null);
        _session.SimulateConcurrentSave(player.Account, current => JsonNode.Parse(current!.ToJsonString())!.AsObject(), times: 3);
        var puts = PlayerCalls(player.Account, "PUT").Length;

        using var response = await RawPostAsync(player, "/disc/change", new { discDeckNumber = 2 });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(["\"1\"", "\"2\"", "\"3\""],
            PlayerCalls(player.Account, "PUT").Skip(puts).Select(call => call.IfMatch).ToArray());
        Assert.All(PlayerCalls(player.Account, "PUT").Skip(puts), call => Assert.Equal(409, call.Status));
        Assert.Equal(4, _session.LoadoutOf(player.Account).Revision);
    }

    [Fact]
    public async Task A_deck_the_schema_refuses_is_the_error_contract_and_stores_nothing()
    {
        var player = await CreateSessionAsync();
        await PostAsync(player, "/startup/index", null);
        var before = _session.LoadoutOf(player.Account);
        // Nine discs: the schema allows eight.
        var nine = await PostAsync(player, "/disc/change", new
        {
            discDeckNumber = 1,
            userDiscDeckList = new[] { new { number = 1, discIdList = new[] { 3010001, 3010002, 3010003, 3010004, 3010005, 3010006, 3010007, 3010008, 3010009 } } }
        }, expectStatusZero: false);
        Assert.Equal(JsonValueKind.Object, nine.ValueKind);
        // A repeated disc, and an active deck that is not one of the decks.
        await PostAsync(player, "/disc/change", new
        {
            userDiscDeckList = new[] { new { number = 1, discIdList = new[] { 3010001, 3010001 } } }
        }, expectStatusZero: false);
        await PostAsync(player, "/disc/change", new { discDeckNumber = 9 }, expectStatusZero: false);

        Assert.Equal(before, _session.LoadoutOf(player.Account));
        var refused = PlayerCalls(player.Account, "PUT").Where(call => call.Status == 422).ToArray();
        Assert.Equal(3, refused.Length);
        Assert.Contains("3010009", refused[0].Body);
        // A refusal is not retried: one save each.
        Assert.Equal(4, PlayerCalls(player.Account, "PUT").Length);
    }

    [Fact]
    public async Task Gear_create_retried_with_the_same_cache_id_shows_the_roll_session_stored()
    {
        var player = await CreateGearSessionAsync();
        var startup = await PostAsync(player, "/startup/index", null);
        var kickerId = startup.GetProperty("userKickerList")[0].GetProperty("kickerId").GetInt32();

        var first = await PostAsync(player, "/gear/create", new { kickerId }, cacheId: "gc-1");
        var retried = await PostAsync(player, "/gear/create", new { kickerId }, cacheId: "gc-1");

        Assert.Equal(first.GetProperty("gearId").GetInt32(), retried.GetProperty("gearId").GetInt32());
        using var ext = JsonDocument.Parse(_session.ExtOf(player.Account));
        Assert.Equal(first.GetProperty("gearId").GetInt32(), ext.RootElement.GetProperty("pending_gear").GetProperty("gear_id").GetInt32());
        Assert.Equal(kickerId, ext.RootElement.GetProperty("pending_gear").GetProperty("kicker_id").GetInt32());
        // The roll is the server's and stays in ext; the loadout was not touched.
        Assert.Equal(1, _session.LoadoutOf(player.Account).Revision);
    }

    [Fact]
    public async Task Gear_create_spends_one_gear_chip_and_a_retry_spends_nothing_more()
    {
        var player = await CreateGearSessionAsync();
        var startup = await PostAsync(player, "/startup/index", null);
        var kickerId = startup.GetProperty("userKickerList")[0].GetProperty("kickerId").GetInt32();

        await PostAsync(player, "/gear/create", new { kickerId }, cacheId: "chip-1");
        await PostAsync(player, "/gear/create", new { kickerId }, cacheId: "chip-1");

        Assert.Equal(9, OtomoTestEnvironment.Economy.BalanceOf(player.Account, EconomyCatalog.GearChipCurrency));
        // The client counts chips from the item KickerGearMaterial names, so startup carries the balance as that item.
        var after = await PostAsync(player, "/startup/index", null);
        var chips = after.GetProperty("userItemList").EnumerateArray()
            .Single(row => row.GetProperty("itemId").GetInt32() == EconomyCatalog.GearMaterialItemId);
        Assert.Equal(9, chips.GetProperty("amount").GetInt64());
    }

    [Fact]
    public async Task Gear_create_without_a_chip_is_refused_and_leaves_no_roll()
    {
        var player = await CreateGearSessionAsync();
        GearTestPlane.GiveChips(player.Account, 0);
        var startup = await PostAsync(player, "/startup/index", null);
        var kickerId = startup.GetProperty("userKickerList")[0].GetProperty("kickerId").GetInt32();

        await PostAsync(player, "/gear/create", new { kickerId }, expectStatusZero: false);

        var after = await PostAsync(player, "/startup/index", null);
        Assert.Equal(0, after.GetProperty("userGear").GetProperty("gearId").GetInt32());
    }

    [Fact]
    public async Task Gear_set_puts_the_gear_in_the_loadout_and_clears_the_roll_in_ext()
    {
        var player = await CreateGearSessionAsync();
        var startup = await PostAsync(player, "/startup/index", null);
        var kicker = startup.GetProperty("userKickerList")[0];
        var kickerId = kicker.GetProperty("kickerId").GetInt32();
        var costumeId = kicker.GetProperty("userKickerCostumeList")[0].GetProperty("kickerCostumeId").GetInt32();
        var rolled = await PostAsync(player, "/gear/create", new { kickerId });

        await PostAsync(player, "/gear/set", new { kickerCostumeId = costumeId, gearIdNumber = 2 });

        var (revision, document) = _session.LoadoutOf(player.Account);
        Assert.Equal(2, revision);
        using var loadout = JsonDocument.Parse(document!);
        Assert.Equal(new[] { 0, rolled.GetProperty("gearId").GetInt32(), 0 },
            Discs(loadout.RootElement.GetProperty("costume_gears").GetProperty(costumeId.ToString())));
        using var ext = JsonDocument.Parse(_session.ExtOf(player.Account));
        Assert.Equal(0, ext.RootElement.GetProperty("pending_gear").GetProperty("gear_id").GetInt32());
        Assert.False(ext.RootElement.TryGetProperty("costume_gears", out _));
    }

    [Fact]
    public async Task Nothing_the_adapter_writes_to_ext_is_outside_the_published_fields()
    {
        var player = await CreateGearSessionAsync();
        var startup = await PostAsync(player, "/startup/index", null);
        var kickerId = startup.GetProperty("userKickerList")[0].GetProperty("kickerId").GetInt32();
        var costumeId = startup.GetProperty("userKickerList")[0].GetProperty("userKickerCostumeList")[0].GetProperty("kickerCostumeId").GetInt32();
        await PostAsync(player, "/gear/create", new { kickerId });
        await PostAsync(player, "/gear/set", new { kickerCostumeId = costumeId, gearIdNumber = 1 });
        await PostAsync(player, "/gear/create", new { kickerId });
        await PostAsync(player, "/gear/destroy", null);
        await PostAsync(player, "/disc/change", new { discDeckNumber = 3 });

        var writes = _session.ExtWrites.Where(call => call.PlayerId == player.Account).ToArray();
        Assert.NotEmpty(writes);
        Assert.All(writes, call =>
        {
            Assert.Equal(200, call.Status);
            using var document = JsonDocument.Parse(call.Ext);
            Assert.All(document.RootElement.EnumerateObject(), field => Assert.Contains(field.Name, FakeOtomoSocial.ExtFields));
        });
        using var ext = JsonDocument.Parse(_session.ExtOf(player.Account));
        Assert.Equal(0, ext.RootElement.GetProperty("pending_gear").GetProperty("gear_id").GetInt32());
        using var loadout = JsonDocument.Parse(_session.LoadoutOf(player.Account).Document!);
        Assert.Equal(3, loadout.RootElement.GetProperty("active_deck").GetInt32());
    }

    [Fact]
    public async Task With_session_down_a_loadout_read_is_the_503_contract_and_no_loadout_is_invented()
    {
        var player = await CreateSessionAsync();
        _session.SetExtUnavailable(player.Account, true);
        try
        {
            foreach (var path in new[] { "/startup/index", "/home/index", "/battle/entry", "/gear/set" })
            {
                using var response = await RawPostAsync(player, path, path == "/battle/entry" ? new { battleRuleId = 1 } : null);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.Equal("1", response.Headers.GetValues("x-app-status-code").Single());
                using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal("session-unavailable", error.RootElement.GetProperty("error").GetString());
            }
            using var write = await RawPostAsync(player, "/disc/change", new { discDeckNumber = 2 });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, write.StatusCode);
        }
        finally
        {
            _session.SetExtUnavailable(player.Account, false);
        }
        Assert.Equal((0, null), _session.LoadoutOf(player.Account));

        // Session comes back: the player is served what session holds, and the starter decks are saved then.
        var startup = await PostAsync(player, "/startup/index", null);
        Assert.Equal(JsonValueKind.Object, startup.ValueKind);
        using var loadout = JsonDocument.Parse(_session.LoadoutOf(player.Account).Document!);
        Assert.Equal(5, loadout.RootElement.GetProperty("decks").EnumerateObject().Count());
    }

    [Fact]
    public async Task Another_players_card_is_session_s_name_and_session_s_decks_from_the_internal_read()
    {
        var viewer = await CreateSessionAsync();
        var target = await CreateSessionAsync();
        var store = _factory.Services.GetRequiredService<IPlayerStore>();
        await PostAsync(target, "/user/change", new { name = "LocalName" });
        // The two names diverge: session is the one the card shows.
        _session.SeedName(target.Account, "SessionName");
        _session.SeedLoadout(target.Account,
            """{"schema_version":1,"decks":{"4":[3010013,3010014,3010020,3010022]},"costume_gears":{},"active_deck":4}""");

        var detail = await PostAsync(viewer, "/user/detail", new { searchUserId = target.UserId });
        Assert.Equal("SessionName", detail.GetProperty("userProfile").GetProperty("name").GetString());
        Assert.Equal(3010013, detail.GetProperty("userBattleParameter").GetProperty("discId1").GetInt32());
        Assert.Equal(3010022, detail.GetProperty("userBattleParameter").GetProperty("discId4").GetInt32());
        // The deck came from the internal loadout read, with the service key, not from the card's ext.
        Assert.Contains(_session.LoadoutCalls, call => call.PlayerId == target.Account && call.Internal && call.Status == 200);
        Assert.Contains(_session.Requests, call => call.Path == $"/internal/session/players/{target.Account}/loadout");

        var search = await PostAsync(viewer, "/user/search", new { displayUserId = PlayerDisplayIdCodec.ToPublic(long.Parse(target.UserId)) });
        Assert.Equal("SessionName", search.GetProperty("userProfile").GetProperty("name").GetString());
        Assert.Contains(_session.Requests, call => call.Path == $"/internal/session/players/{target.Account}");

        // A player who has saved no loadout is shown the initial deck, not an error.
        var bare = await CreateSessionAsync();
        var bareDetail = await PostAsync(viewer, "/user/detail", new { searchUserId = bare.UserId });
        Assert.Equal(3010001, bareDetail.GetProperty("userBattleParameter").GetProperty("discId1").GetInt32());
        Assert.Contains(_session.LoadoutCalls, call => call.PlayerId == bare.Account && call.Internal && call.Status == 404);

        // A card session cannot give is the 503 contract, not a name read from this adapter's own copy.
        _session.SetExtUnavailable(target.Account, true);
        try
        {
            using var down = await RawPostAsync(viewer, "/user/detail", new { searchUserId = target.UserId });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        }
        finally
        {
            _session.SetExtUnavailable(target.Account, false);
        }
        Assert.NotNull(store.FindProfile(long.Parse(target.UserId)));
    }

    // --- plumbing ---------------------------------------------------------------------------------------------

    private Task<DemoSession> CreateSessionAsync() => SignInAsync(Guid.NewGuid().ToString("N"));

    private async Task<DemoSession> SignInAsync(string uuid, WebApplicationFactory<Program>? factory = null)
    {
        var client = (factory ?? _factory).CreateClient();
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
        var account = OtomoTestEnvironment.Server.AccountFor(OtomoDeviceId.FromDeviceUuid(uuid));
        Assert.False(string.IsNullOrEmpty(account));
        return new DemoSession(client, key, response.Headers.GetValues("x-app-user-id").Single(), account!, uuid);
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

    private static Task<HttpResponseMessage> RawPostAsync(DemoSession session, string path, object? body) =>
        session.Client.SendAsync(Request(session, path, body, null));

    private static async Task<JsonElement> PostAsync(DemoSession session, string path, object? body,
        bool expectStatusZero = true, string? cacheId = null)
    {
        using var response = await session.Client.SendAsync(Request(session, path, body, cacheId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectStatusZero ? "0" : "1", response.Headers.GetValues("x-app-status-code").Single());
        var decrypted = D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), session.Key);
        using var document = JsonDocument.Parse(decrypted);
        return document.RootElement.Clone();
    }

    private sealed record DemoSession(HttpClient Client, byte[] Key, string UserId, string Account, string Uuid = "");
}
