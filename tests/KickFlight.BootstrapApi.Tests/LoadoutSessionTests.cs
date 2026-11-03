using System.Net;
using System.Text;
using System.Text.Json;
using KickFlight.BootstrapApi.Otomo;
using KickFlight.BootstrapApi.PlayerStore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The loadout - decks, the active deck, costume gears, a pending gear roll - is the ext of the player's session card.
// These tests drive the real handlers against the session fake (FakeOtomoSocial.Ext.cs), which validates every write
// against the published ext.session.player schema plus this change's two fields, exactly as session does.
[Collection("Matchmaking static state")]
public sealed class LoadoutSessionTests : IClassFixture<ServerTestHostFixture>
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeOtomoSocial _session = OtomoTestEnvironment.Social;

    public LoadoutSessionTests(ServerTestHostFixture fixture) => _factory = fixture.Factory;

    [Fact]
    public async Task A_new_player_is_given_the_starter_decks_in_ext_once_and_home_serves_them()
    {
        var player = await CreateSessionAsync();
        Assert.Equal("{}", _session.ExtOf(player.Account));

        await PostAsync(player, "/startup/index", null);
        await PostAsync(player, "/startup/index", null);
        var home = await PostAsync(player, "/home/index", null);

        using var ext = JsonDocument.Parse(_session.ExtOf(player.Account));
        Assert.Equal(1, ext.RootElement.GetProperty("active_deck").GetInt32());
        var decks = ext.RootElement.GetProperty("decks");
        Assert.Equal(5, decks.EnumerateObject().Count());
        Assert.Equal(new[] { 3010001, 3010002, 3010003, 3010004 },
            decks.GetProperty("1").EnumerateArray().Select(id => id.GetInt32()).ToArray());
        // Written once: the later reads found decks and wrote nothing, and the write is keyed per player.
        var writes = _session.ExtWrites.Where(call => call.PlayerId == player.Account).ToArray();
        var write = Assert.Single(writes);
        Assert.Equal($"loadout-default:{player.Account}", write.Key);
        Assert.True(write.Merge);

        Assert.Equal(1, home.GetProperty("userPlayer").GetProperty("discDeckNumber").GetInt32());
        Assert.Equal(5, home.GetProperty("userDiscDeckList").GetArrayLength());
    }

    [Fact]
    public async Task Concurrent_first_reads_write_the_starter_decks_once()
    {
        var player = await CreateSessionAsync();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PostAsync(player, "/startup/index", null)));

        var writes = _session.ExtWrites.Where(call => call.PlayerId == player.Account).ToArray();
        Assert.All(writes, call => Assert.Equal($"loadout-default:{player.Account}", call.Key));
        Assert.Single(writes, call => call.Applied);
        using var ext = JsonDocument.Parse(_session.ExtOf(player.Account));
        Assert.Equal(5, ext.RootElement.GetProperty("decks").EnumerateObject().Count());
    }

    [Fact]
    public async Task Disc_change_writes_the_decks_and_the_active_deck_and_a_retry_writes_once()
    {
        var player = await CreateSessionAsync();
        await PostAsync(player, "/startup/index", null);
        var change = new
        {
            discDeckNumber = 2,
            userDiscDeckList = new[] { new { number = 2, discIdList = new[] { 3010050, 3010051, 3010052, 3010053 } } }
        };

        await PostAsync(player, "/disc/change", change, cacheId: "dc-1");
        await PostAsync(player, "/disc/change", change, cacheId: "dc-1");

        using var ext = JsonDocument.Parse(_session.ExtOf(player.Account));
        Assert.Equal(2, ext.RootElement.GetProperty("active_deck").GetInt32());
        var decks = ext.RootElement.GetProperty("decks");
        Assert.Equal(new[] { 3010050, 3010051, 3010052, 3010053 },
            decks.GetProperty("2").EnumerateArray().Select(id => id.GetInt32()).ToArray());
        // The decks the player did not edit are still there: a merge replaces a field whole, so all of them are sent.
        Assert.Equal(5, decks.EnumerateObject().Count());

        var changes = _session.ExtWrites.Where(call => call.Key == "loadout:disc-change:dc-1" && call.PlayerId == player.Account).ToArray();
        Assert.Equal(2, changes.Length);
        Assert.Equal([true, false], changes.Select(call => call.Applied).ToArray());

        var home = await PostAsync(player, "/home/index", null);
        Assert.Equal(2, home.GetProperty("userPlayer").GetProperty("discDeckNumber").GetInt32());
        Assert.Equal(3010050, home.GetProperty("userDiscDeckList").EnumerateArray()
            .Single(deck => deck.GetProperty("number").GetInt32() == 2).GetProperty("discIdList")[0].GetInt32());
    }

    [Fact]
    public async Task A_deck_session_refuses_is_the_error_contract_and_stores_nothing()
    {
        var player = await CreateSessionAsync();
        await PostAsync(player, "/startup/index", null);
        var before = _session.ExtOf(player.Account);
        // Nine discs: session's schema allows eight.
        var refused = await PostAsync(player, "/disc/change", new
        {
            discDeckNumber = 1,
            userDiscDeckList = new[] { new { number = 1, discIdList = new[] { 3010001, 3010002, 3010003, 3010004, 3010005, 3010006, 3010007, 3010008, 3010009 } } }
        }, expectStatusZero: false);

        Assert.Equal(JsonValueKind.Object, refused.ValueKind);
        Assert.Equal(before, _session.ExtOf(player.Account));
        var attempt = Assert.Single(_session.ExtWrites, call => call.PlayerId == player.Account && call.Status == 400);
        Assert.Contains("3010009", attempt.Ext);
    }

    [Fact]
    public async Task Gear_create_retried_with_the_same_cache_id_shows_the_roll_session_stored()
    {
        var player = await CreateSessionAsync();
        var startup = await PostAsync(player, "/startup/index", null);
        var kickerId = startup.GetProperty("userKickerList")[0].GetProperty("kickerId").GetInt32();

        var first = await PostAsync(player, "/gear/create", new { kickerId }, cacheId: "gc-1");
        var retried = await PostAsync(player, "/gear/create", new { kickerId }, cacheId: "gc-1");

        Assert.Equal(first.GetProperty("gearId").GetInt32(), retried.GetProperty("gearId").GetInt32());
        using var ext = JsonDocument.Parse(_session.ExtOf(player.Account));
        Assert.Equal(first.GetProperty("gearId").GetInt32(), ext.RootElement.GetProperty("pending_gear").GetProperty("gear_id").GetInt32());
        Assert.Equal(kickerId, ext.RootElement.GetProperty("pending_gear").GetProperty("kicker_id").GetInt32());
    }

    [Fact]
    public async Task Nothing_the_adapter_writes_is_outside_the_published_fields()
    {
        var player = await CreateSessionAsync();
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
        Assert.Equal(3, ext.RootElement.GetProperty("active_deck").GetInt32());
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
        Assert.Equal("{}", _session.ExtOf(player.Account));

        // Session comes back: the player is served what session holds, and the starter decks are written then.
        var startup = await PostAsync(player, "/startup/index", null);
        Assert.Equal(JsonValueKind.Object, startup.ValueKind);
        using var ext = JsonDocument.Parse(_session.ExtOf(player.Account));
        Assert.Equal(5, ext.RootElement.GetProperty("decks").EnumerateObject().Count());
    }

    [Fact]
    public async Task Another_players_card_is_session_s_name_and_session_s_decks()
    {
        var viewer = await CreateSessionAsync();
        var target = await CreateSessionAsync();
        var store = _factory.Services.GetRequiredService<IPlayerStore>();
        await PostAsync(target, "/user/change", new { name = "LocalName" });
        // The two names diverge: session is the one the card shows.
        _session.SeedName(target.Account, "SessionName");
        _session.SeedExt(target.Account, """{"decks":{"4":[3010013,3010014,3010020,3010022]},"active_deck":4}""");

        var detail = await PostAsync(viewer, "/user/detail", new { searchUserId = target.UserId });
        Assert.Equal("SessionName", detail.GetProperty("userProfile").GetProperty("name").GetString());
        Assert.Equal(3010013, detail.GetProperty("userBattleParameter").GetProperty("discId1").GetInt32());
        Assert.Equal(3010022, detail.GetProperty("userBattleParameter").GetProperty("discId4").GetInt32());

        var search = await PostAsync(viewer, "/user/search", new { displayUserId = PlayerDisplayIdCodec.ToPublic(long.Parse(target.UserId)) });
        Assert.Equal("SessionName", search.GetProperty("userProfile").GetProperty("name").GetString());
        Assert.Contains(_session.Requests, call => call.Path == $"/internal/session/players/{target.Account}");

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

    private async Task<DemoSession> CreateSessionAsync()
    {
        var client = _factory.CreateClient();
        var key = Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");
        var uuid = Guid.NewGuid().ToString("N");
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
        return new DemoSession(client, key, response.Headers.GetValues("x-app-user-id").Single(), account!);
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

    private sealed record DemoSession(HttpClient Client, byte[] Key, string UserId, string Account);
}
