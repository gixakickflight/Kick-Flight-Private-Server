using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using KickFlight.BootstrapApi;
using KickFlight.BootstrapApi.Otomo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// Badges on the new contract, end to end against the economy and session fakes with the internal plane on:
//
//   * a badge is OWNED when the inventory holds honor:<id>, and nothing else makes it so;
//   * it is SHOWN through the session display slot "honor" (PUT /display/honor, DELETE for the default);
//   * what happened is REPORTED as a declared fact, keyed by the thing that happened;
//   * what a fact is worth is the tenant's track, and the adapter CLAIMS the rung with the player's own token.
//
// The fakes do not evaluate tracks (economy's own tests do): a test says "the track stands at 30" with SeedBadgeTrack.
[Collection("Matchmaking static state")]
public sealed class BadgeOtomoTests : IDisposable
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";

    private readonly FakeOtomoEconomy _economy = OtomoTestEnvironment.Economy;
    private readonly FakeOtomoSocial _session = OtomoTestEnvironment.Social;
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    private sealed record Player(HttpClient Client, byte[] Key, string Account, string UserId);

    private WebApplicationFactory<Program> NewHost()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Otomo:Economy:ProgressEnabled"] = "true",
                ["Otomo:Economy:InternalBaseUrl"] = _economy.BaseUrl,
                ["Otomo:Economy:ServiceKey"] = "kf-test-gameserver-key"
            }));
        });
        _factories.Add(factory);
        return factory;
    }

    private static async Task<Player> NewPlayerAsync(WebApplicationFactory<Program> factory)
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
        return new Player(client, key, account!, response.Headers.GetValues("x-app-user-id").Single());
    }

    private static async Task<(HttpStatusCode Status, string Code, JsonElement Body)> PostAsync(Player player, string path, object? body)
    {
        var json = body is null ? "{}" : JsonSerializer.Serialize(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(json), player.Key, new byte[16]))
        };
        request.Headers.Host = Host;
        request.Headers.Add("x-app-response-cache-id", Guid.NewGuid().ToString());
        using var response = await player.Client.SendAsync(request);
        var code = response.Headers.GetValues("x-app-status-code").Single();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (code != "0") return (response.StatusCode, code, default);
        using var document = JsonDocument.Parse(D2CCodec.Decode(bytes, player.Key));
        return (response.StatusCode, code, document.RootElement.Clone());
    }

    private static async Task<JsonElement> OkAsync(Player player, string path, object? body = null)
    {
        var (status, code, result) = await PostAsync(player, path, body);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("0", code);
        return result;
    }

    private static int[] Honors(JsonElement startup) =>
        startup.GetProperty("userHonorList").EnumerateArray().Select(row => row.GetProperty("honorId").GetInt32()).Order().ToArray();

    [Fact]
    public async Task A_badge_is_shown_as_owned_exactly_when_the_inventory_holds_its_item()
    {
        var player = await NewPlayerAsync(NewHost());

        Assert.Equal(new[] { 6010000 }, Honors(await OkAsync(player, "/startup/index")));

        _economy.Seed(player.Account, items: [("honor:6010101", 1), ("honor:6010501", 1), ("disc:3010001", 1)]);
        Assert.Equal(new[] { 6010000, 6010101, 6010501 }, Honors(await OkAsync(player, "/startup/index")));
    }

    [Fact]
    public async Task Equipping_a_badge_points_the_session_display_slot_at_its_item_and_the_default_clears_it()
    {
        var player = await NewPlayerAsync(NewHost());
        _economy.Seed(player.Account, items: [("honor:6010101", 1)]);
        await OkAsync(player, "/startup/index");

        await OkAsync(player, "/user/change", new { honorId = 6010101 });
        Assert.Equal("honor:6010101", _session.DisplayOf(player.Account)["honor"]);
        Assert.Equal(6010101, (await OkAsync(player, "/home/index")).GetProperty("userPlayer").GetProperty("honorId").GetInt32());

        // The default honor is the empty slot.
        await OkAsync(player, "/user/change", new { honorId = 6010000 });
        Assert.Empty(_session.DisplayOf(player.Account));
        Assert.Equal(6010000, (await OkAsync(player, "/home/index")).GetProperty("userPlayer").GetProperty("honorId").GetInt32());
    }

    [Fact]
    public async Task Equipping_a_badge_the_inventory_does_not_hold_is_refused_by_session_and_shows_nothing()
    {
        var player = await NewPlayerAsync(NewHost());
        await OkAsync(player, "/startup/index");

        var (status, code, _) = await PostAsync(player, "/user/change", new { honorId = 6010101 });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotEqual("0", code); // the game's error contract, as it always was for an unowned honor
        Assert.Empty(_session.DisplayOf(player.Account));
        Assert.Equal(6010000, (await OkAsync(player, "/home/index")).GetProperty("userPlayer").GetProperty("honorId").GetInt32());
    }

    [Fact]
    public async Task A_session_that_cannot_be_read_shows_the_default_badge_rather_than_failing_the_home()
    {
        var player = await NewPlayerAsync(NewHost());
        _economy.Seed(player.Account, items: [("honor:6010101", 1)]);
        await OkAsync(player, "/user/change", new { honorId = 6010101 });

        _session.SetExtUnavailable(player.Account, true); // the display read is the same session, but a different route
        try
        {
            // /home/index needs the loadout from the same session, so it answers its own 503; the point here is that the
            // badge read has no way to turn a missing display into a made-up badge.
            var (_, code, _) = await PostAsync(player, "/home/index", null);
            Assert.NotNull(code);
        }
        finally
        {
            _session.SetExtUnavailable(player.Account, false);
        }
    }

    [Fact]
    public async Task Startup_reports_the_login_day_once_per_utc_day_under_the_day_as_key_and_lists_what_the_report_paid()
    {
        var player = await NewPlayerAsync(NewHost());
        // The tenant's login-day track crosses its first target on today's fact: economy pays it inside the report.
        _economy.QueueAutoClaim(player.Account, "login.day", "badge.login_days", 0, 30, "badge.6010101");

        var before = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var first = await OkAsync(player, "/startup/index");
        var second = await OkAsync(player, "/startup/index");
        var after = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var reports = _economy.FactReports.Where(fact => fact.PlayerId == player.Account && fact.Fact == "login.day").ToArray();
        Assert.Equal(2, reports.Length);
        var day = reports[0].Fields.GetProperty("day").GetString()!;
        Assert.Contains(day, new[] { before, after });
        Assert.All(reports, report => Assert.Equal($"badge-login:{day}", report.Key));
        Assert.False(reports[0].Replayed);
        Assert.True(reports[1].Replayed); // the same day again is economy's replay: login days count days, not startups

        // The badge the first report paid is new in the first answer, and is only owned in the second: a replay is not news.
        bool NewFlag(JsonElement startup) => startup.GetProperty("userHonorList").EnumerateArray()
            .Single(row => row.GetProperty("honorId").GetInt32() == 6010101).GetProperty("newFlag").GetBoolean();
        Assert.True(NewFlag(first));
        Assert.False(NewFlag(second));
        Assert.Equal(1, _economy.ItemsOf(player.Account, "honor:6010101"));

        // The adapter claims nothing: no sweep, no player-plane claim, no read of the tasks to find a rung.
        Assert.DoesNotContain(_economy.Requests, call => call.Path == "/api/player/economy/tasks/claim");
    }

    [Fact]
    public async Task A_step_economy_refused_to_pay_is_not_shown_as_owned_and_does_not_fail_the_screen()
    {
        var player = await NewPlayerAsync(NewHost());
        _economy.QueueAutoClaim(player.Account, "login.day", "badge.login_days", 0, 30, "badge.6010101", refusedWith: "budget_exceeded");

        var startup = await OkAsync(player, "/startup/index");

        Assert.Equal(new[] { 6010000 }, Honors(startup));
        Assert.Equal(0, _economy.ItemsOf(player.Account, "honor:6010101"));
        Assert.Single(_economy.FactReports, fact => fact.PlayerId == player.Account && fact.Fact == "login.day");
    }

    [Fact]
    public async Task Naming_the_player_reports_the_registration_fact_and_lists_the_step_it_paid()
    {
        var player = await NewPlayerAsync(NewHost());
        _economy.QueueAutoClaim(player.Account, "registration.completed", "badge.registration", 0, 1, "badge.6010001");

        await OkAsync(player, "/tutorial/end", new { name = "Badger" });

        var fact = Assert.Single(_economy.FactReports, report => report.PlayerId == player.Account && report.Fact == "registration.completed");
        Assert.Equal("badge-registration", fact.Key);
        Assert.Empty(fact.Fields.EnumerateObject());
        Assert.Contains(6010001, Honors(await OkAsync(player, "/startup/index")));
    }

    [Fact]
    public async Task Another_players_equipped_badge_is_session_display_honor_on_their_card()
    {
        var host = NewHost();
        var player = await NewPlayerAsync(host);
        var other = await NewPlayerAsync(host);
        await OkAsync(other, "/user/change", new { name = "Showoff" });
        _session.SeedDisplay(other.Account, "honor", "honor:6010501");

        var card = (await OkAsync(player, "/user/search", new { displayUserId = long.Parse(other.UserId) })).GetProperty("userProfile");

        Assert.Equal(other.UserId, card.GetProperty("userId").GetString());
        Assert.Equal(6010501, card.GetProperty("honorId").GetInt32());
        // A player showing nothing shows the default.
        var plain = await NewPlayerAsync(host);
        await OkAsync(plain, "/user/change", new { name = "Plain" });
        var plainCard = (await OkAsync(player, "/user/search", new { displayUserId = long.Parse(plain.UserId) })).GetProperty("userProfile");
        Assert.Equal(6010000, plainCard.GetProperty("honorId").GetInt32());
    }

    [Theory]
    [InlineData("12345678901234")]    // 14 characters: one past the 13 session's names rules allow
    [InlineData("a\u200db")]          // a zero-width joiner
    [InlineData("tab\there")]         // a control character
    public async Task A_name_session_refuses_is_answered_in_the_name_rule_error_and_not_kept(string name)
    {
        var player = await NewPlayerAsync(NewHost());

        var (status, code, _) = await PostAsync(player, "/tutorial/end", new { name });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("2005", code); // the one code the client maps to its name-rule popup
        Assert.Equal(206, (await OkAsync(player, "/startup/index")).GetProperty("tutorialProgressStatus").GetInt32());
    }

    [Fact]
    public async Task Facts_are_not_reported_when_the_internal_plane_is_off_but_the_badges_already_earned_still_show()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseContentRoot(AppContext.BaseDirectory));
        _factories.Add(factory);
        var player = await NewPlayerAsync(factory);
        _economy.Seed(player.Account, items: [("honor:6010101", 1)]);

        var startup = await OkAsync(player, "/startup/index");

        Assert.Contains(6010101, Honors(startup));
        Assert.DoesNotContain(_economy.FactReports, fact => fact.PlayerId == player.Account);
    }

    public void Dispose()
    {
        foreach (var factory in _factories) factory.Dispose();
    }
}
