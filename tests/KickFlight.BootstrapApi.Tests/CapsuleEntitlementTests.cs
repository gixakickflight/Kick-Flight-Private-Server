using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using KickFlight.BootstrapApi.Otomo;
using KickFlight.BootstrapApi.PlayerStore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenMatch;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// A battle capsule is an economy entitlement (DemoSessionApi.Capsules.cs), and the tenant's documents own its policy: an
// entitlement trigger on the battle.result fact mints it. These drive the real battle result, home and open handlers
// against the economy fake, which states the tenant's capsule kind and trigger (FakeOtomoEconomy.Triggers.cs), enforces
// max_open 3, idempotency by key and "not before available_at", as services/economy does.
//
// The fake's trigger draws from the tenant's weighted picks like otomo does; most tests pin the draw
// (FakeOtomoEconomy.CapsuleClassOverride) so one result earns one known capsule.
[Collection("Matchmaking static state")]
public sealed class CapsuleEntitlementTests : IDisposable
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string SessionKey = "0123456789abcdef0123456789abcdef";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";
    private const string Golden = "match.capsule.golden.2";

    private readonly FakeOtomoEconomy _economy = OtomoTestEnvironment.Economy;
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "kf-capsules-" + Guid.NewGuid().ToString("N"));
    private readonly List<Fixture> _fixtures = [];

    public CapsuleEntitlementTests() => Directory.CreateDirectory(_tempDirectory);

    [Fact]
    public async Task A_battle_result_reports_the_fact_and_the_tenants_trigger_mints_the_capsule_and_pays_nothing()
    {
        var f = NewFixture();
        var player = await f.AuthAsync();
        var account = f.AccountOf(player);
        var (entry, ctx) = await f.EnterAsync(player);

        using var result = await f.PostAsync(player, "/battle/result", Result(entry, player.PlayerId, ctx.OwnTeamType!.Value));

        // The adapter reported the battle, with the type it drew, and created nothing itself.
        var fact = Assert.Single(_economy.FactReports, report => report.PlayerId == account && report.Fact == "battle.result");
        // Nothing in the report is for capsules: the trigger draws the capsule.
        Assert.False(fact.Fields.TryGetProperty("capsule_type", out _));
        Assert.Empty(CreatesFor(account));
        // The trigger minted it: the class, the source and the due time are the tenant's (golden waits 4 hours).
        var row = Assert.Single(_economy.EntitlementsOf(account));
        Assert.Equal(Golden, row.Rule);
        Assert.False(row.Claimed);
        Assert.Equal("trigger:battle.result", row.Source);
        Assert.InRange((row.AvailableAt - DateTimeOffset.UtcNow).TotalMinutes, 4 * 60 - 1, 4 * 60 + 1);
        // An entitlement is a promise, not a payout.
        Assert.Equal(0, _economy.BalanceOf(account, "capsule_golden"));

        var slot = Assert.Single(result.RootElement.GetProperty("receivedUserCapsuleList").EnumerateArray());
        Assert.Equal(1, slot.GetProperty("slotNumber").GetInt32());
        Assert.Equal(5010002, slot.GetProperty("capsuleId").GetInt32());
        Assert.Equal(4 * 3600, slot.GetProperty("openTimeSecond").GetInt32());
        Assert.EndsWith("Z", slot.GetProperty("completeDatetime").GetString());
    }

    [Theory]
    [InlineData("match.capsule.wooden", 5010003, 15 * 60, "capsule_wooden", 1)]
    [InlineData("match.capsule.standard.1", 5010001, 3600, "capsule_standard", 1)]
    [InlineData("match.capsule.standard.2", 5010001, 3600, "capsule_standard", 2)]
    [InlineData("match.capsule.golden.3", 5010002, 4 * 3600, "capsule_golden", 3)]
    public async Task The_drawn_class_names_the_type_the_timer_and_the_discs_in_the_tenants_trigger(string rule, int capsuleId,
        int seconds, string token, int discs)
    {
        var f = NewFixture(rule);
        var player = await f.AuthAsync();
        var account = f.AccountOf(player);
        var (entry, ctx) = await f.EnterAsync(player);

        using var result = await f.PostAsync(player, "/battle/result", Result(entry, player.PlayerId, ctx.OwnTeamType!.Value));

        Assert.Equal(rule, Assert.Single(_economy.EntitlementsOf(account)).Rule);
        var slot = Assert.Single(result.RootElement.GetProperty("receivedUserCapsuleList").EnumerateArray());
        Assert.Equal(capsuleId, slot.GetProperty("capsuleId").GetInt32());
        Assert.Equal(seconds, slot.GetProperty("openTimeSecond").GetInt32());

        // Opening rolls as many draws as the class paid tokens.
        _economy.MakeDue(account);
        using var open = await f.PostAsync(player, "/capsule/open", JsonSerializer.Serialize(new { slotNumber = 1 }));
        Assert.Equal(discs, open.RootElement.GetProperty("dropDiscList").GetArrayLength());
        Assert.Equal(0, _economy.BalanceOf(account, token));
    }

    [Fact]
    public void The_fakes_draw_follows_the_published_weights()
    {
        var counts = new Dictionary<string, int>();
        for (var i = 0; i < 20000; i++)
        {
            var pick = FakeOtomoEconomy.DrawCapsule($"badge-battle:be-{i}");
            counts[pick.Class] = counts.GetValueOrDefault(pick.Class) + 1;
        }
        // 60 / 15 / 15 / 5 / 5 percent, which is the old 60/30/10 over types and the uniform disc count within each.
        Assert.InRange(counts["match.capsule.wooden"], 11400, 12600);
        Assert.InRange(counts["match.capsule.standard.1"] + counts["match.capsule.standard.2"], 5400, 6600);
        Assert.InRange(counts["match.capsule.golden.2"] + counts["match.capsule.golden.3"], 1500, 2500);
    }

    [Fact]
    public async Task A_retried_result_creates_nothing_more_and_shows_the_same_capsule()
    {
        var f = NewFixture();
        var player = await f.AuthAsync();
        var (entry, ctx) = await f.EnterAsync(player);
        var body = Result(entry, player.PlayerId, ctx.OwnTeamType!.Value);

        using var first = await f.PostAsync(player, "/battle/result", body);
        using var retry = await f.PostAsync(player, "/battle/result", body);

        Assert.Single(_economy.EntitlementsOf(f.AccountOf(player)));
        Assert.Equal(1, Assert.Single(retry.RootElement.GetProperty("receivedUserCapsuleList").EnumerateArray())
            .GetProperty("slotNumber").GetInt32());
        Assert.Equal(first.RootElement.GetProperty("receivedUserCapsuleList").GetRawText(),
            retry.RootElement.GetProperty("receivedUserCapsuleList").GetRawText());
    }

    [Fact]
    public async Task A_player_holding_three_open_capsules_is_handed_none_because_the_trigger_is_refused_by_the_kind()
    {
        var f = NewFixture();
        var player = await f.AuthAsync();
        var account = f.AccountOf(player);
        for (var slot = 1; slot <= 3; slot++) await CreateDirectAsync(account, $"seed-{slot}");
        var (entry, ctx) = await f.EnterAsync(player);

        using var result = await f.PostAsync(player, "/battle/result", Result(entry, player.PlayerId, ctx.OwnTeamType!.Value));

        Assert.Empty(result.RootElement.GetProperty("receivedUserCapsuleList").EnumerateArray());
        Assert.Equal(3, _economy.EntitlementsOf(account).Count);
        Assert.Equal(0, _economy.BalanceOf(account, "capsule_golden"));
        // The battle was still reported: a refused mint never fails the fact.
        Assert.Single(_economy.FactReports, report => report.PlayerId == account && report.Fact == "battle.result");
    }

    [Fact]
    public async Task Slots_are_stable_a_freed_slot_is_reused_and_an_older_open_does_not_move_the_others()
    {
        var f = NewFixture();
        var player = await f.AuthAsync();
        var account = f.AccountOf(player);
        for (var n = 1; n <= 3; n++) await CreateDirectAsync(account, $"seed-{n}");
        _economy.MakeDue(account);
        using var openedFirst = await f.PostAsync(player, "/capsule/open", JsonSerializer.Serialize(new { slotNumber = 1 }));

        // Slots 2 and 3 are still 2 and 3, though 1 is gone.
        using var home = await f.PostAsync(player, "/home/index", "{}");
        Assert.Equal([2, 3], home.RootElement.GetProperty("userCapsuleList").EnumerateArray()
            .Select(slot => slot.GetProperty("slotNumber").GetInt32()).Order());

        // A new battle takes the freed one.
        var (entry, ctx) = await f.EnterAsync(player);
        using var result = await f.PostAsync(player, "/battle/result", Result(entry, player.PlayerId, ctx.OwnTeamType!.Value));
        Assert.Equal(1, Assert.Single(result.RootElement.GetProperty("receivedUserCapsuleList").EnumerateArray())
            .GetProperty("slotNumber").GetInt32());
    }

    [Fact]
    public async Task Home_shows_the_open_entitlements_as_slots_and_an_opened_one_is_gone()
    {
        var f = NewFixture();
        var player = await f.AuthAsync();
        var (entry, ctx) = await f.EnterAsync(player);
        using var result = await f.PostAsync(player, "/battle/result", Result(entry, player.PlayerId, ctx.OwnTeamType!.Value));

        using var before = await f.PostAsync(player, "/home/index", "{}");
        var slot = Assert.Single(before.RootElement.GetProperty("userCapsuleList").EnumerateArray());
        Assert.Equal(1, slot.GetProperty("slotNumber").GetInt32());
        Assert.Equal(5010002, slot.GetProperty("capsuleId").GetInt32());

        _economy.MakeDue(f.AccountOf(player));
        using var opened = await f.PostAsync(player, "/capsule/open", JsonSerializer.Serialize(new { slotNumber = 1 }));
        Assert.Equal(2, opened.RootElement.GetProperty("dropDiscList").GetArrayLength());

        using var after = await f.PostAsync(player, "/home/index", "{}");
        Assert.Empty(after.RootElement.GetProperty("userCapsuleList").EnumerateArray());
    }

    [Fact]
    public async Task Capsule_open_pays_exactly_once()
    {
        // A golden capsule holds two discs: its class pays two tokens in the claim and the pool costs one per draw. Two
        // tokens in, two out, and a repeat of the open - a retry after a lost answer - moves nothing.
        var f = NewFixture();
        var player = await f.AuthAsync();
        var account = f.AccountOf(player);
        var (entry, ctx) = await f.EnterAsync(player);
        using var result = await f.PostAsync(player, "/battle/result", Result(entry, player.PlayerId, ctx.OwnTeamType!.Value));
        Assert.Equal(0, _economy.BalanceOf(account, "capsule_golden"));
        _economy.MakeDue(account);
        var discsBefore = _economy.ItemsOf(account, "disc:3010014");

        using var open = await f.PostAsync(player, "/capsule/open", JsonSerializer.Serialize(new { slotNumber = 1 }));

        Assert.Equal(2, open.RootElement.GetProperty("dropDiscList").GetArrayLength());
        Assert.Equal(discsBefore + 2, _economy.ItemsOf(account, "disc:3010014"));
        Assert.True(Assert.Single(_economy.EntitlementsOf(account)).Claimed);
        // One claim (economy's payout, keyed by the entitlement id), no further grants from the adapter, one roll.
        var claims = _economy.Requests.Where(call => call.Path.EndsWith("/claim") && call.Body!.Contains(account)).ToArray();
        Assert.Single(claims);
        Assert.DoesNotContain("early", claims[0].Body);
        Assert.Empty(GrantsFor(account, Golden));
        Assert.Equal(0, _economy.BalanceOf(account, "capsule_golden"));
        Assert.Single(RollsFor(account));
        Assert.Contains("\"count\":2", RollsFor(account)[0].Body);

        // The same open again (the client's retry): the claim is already paid, the roll replays.
        using var again = await f.PostAsync(player, "/capsule/open", JsonSerializer.Serialize(new { slotNumber = 1 }));
        Assert.Equal(open.RootElement.GetProperty("dropDiscList").GetRawText(), again.RootElement.GetProperty("dropDiscList").GetRawText());
        Assert.Equal(discsBefore + 2, _economy.ItemsOf(account, "disc:3010014"));
        Assert.Equal(0, _economy.BalanceOf(account, "capsule_golden"));
    }

    [Fact]
    public async Task A_capsule_claimed_but_not_rolled_is_opened_by_the_retry_without_paying_again()
    {
        var f = NewFixture();
        var player = await f.AuthAsync();
        var account = f.AccountOf(player);
        var (entry, ctx) = await f.EnterAsync(player);
        using var result = await f.PostAsync(player, "/battle/result", Result(entry, player.PlayerId, ctx.OwnTeamType!.Value));
        _economy.MakeDue(account);
        // What a crash between the claim and the roll leaves: the row claimed (the tokens paid) and no roll.
        var row = Assert.Single(_economy.EntitlementsOf(account));
        await ClaimDirectAsync(account, row.Id);
        Assert.Equal(2, _economy.BalanceOf(account, "capsule_golden"));

        using var open = await f.PostAsync(player, "/capsule/open", JsonSerializer.Serialize(new { slotNumber = 1 }));

        Assert.Equal(2, open.RootElement.GetProperty("dropDiscList").GetArrayLength());
        Assert.Equal(0, _economy.BalanceOf(account, "capsule_golden"));
    }

    [Theory]
    [InlineData("/capsule/open")]
    [InlineData("/capsule/immediateOpen")]
    public async Task A_capsule_is_not_opened_before_it_is_due_when_the_kind_sells_no_early_claim(string path)
    {
        var f = NewFixture();
        var player = await f.AuthAsync();
        var account = f.AccountOf(player);
        var (entry, ctx) = await f.EnterAsync(player);
        using var result = await f.PostAsync(player, "/battle/result", Result(entry, player.PlayerId, ctx.OwnTeamType!.Value));

        using var refused = await f.PostRawAsync(player, path, JsonSerializer.Serialize(new { slotNumber = 1 }));

        Assert.Equal("1", refused.Headers.GetValues("x-app-status-code").Single());
        var row = Assert.Single(_economy.EntitlementsOf(account));
        Assert.False(row.Claimed);
        Assert.Equal(0, _economy.BalanceOf(account, "capsule_golden"));
        // The refusal is economy's (409 entitlement_not_ready on the claim), not a guess made here.
        Assert.Single(_economy.Requests, call => call.Path.EndsWith("/claim") && call.Body!.Contains(account));
        // immediateOpen asked for the price first and was told the kind offers none.
        Assert.Equal(path == "/capsule/immediateOpen" ? 1 : 0,
            _economy.Requests.Count(call => call.Path.EndsWith("/early_claim") && call.Body!.Contains(account)));
        Assert.Empty(RollsFor(account));
    }

    [Fact]
    public async Task Immediate_open_of_a_due_capsule_repeats_the_item_counters_like_open_does_not()
    {
        var f = NewFixture();
        var player = await f.AuthAsync();
        var (entry, ctx) = await f.EnterAsync(player);
        using var result = await f.PostAsync(player, "/battle/result", Result(entry, player.PlayerId, ctx.OwnTeamType!.Value));
        _economy.MakeDue(f.AccountOf(player));

        using var immediate = await f.PostAsync(player, "/capsule/immediateOpen", JsonSerializer.Serialize(new { slotNumber = 1 }));

        Assert.True(immediate.RootElement.TryGetProperty("userItemList", out _));
        Assert.Equal(2, immediate.RootElement.GetProperty("dropDiscList").GetArrayLength());
    }

    [Fact]
    public async Task Immediate_open_pays_the_kinds_early_claim_price_when_the_tenant_declares_one()
    {
        var f = NewFixture();
        var player = await f.AuthAsync();
        var account = f.AccountOf(player);
        var (entry, ctx) = await f.EnterAsync(player);
        using var result = await f.PostAsync(player, "/battle/result", Result(entry, player.PlayerId, ctx.OwnTeamType!.Value));
        // A price the test states (the tenant publishes none yet): 50 jetcoin per started hour, 4 hours to go.
        _economy.EarlyClaim = new FakeOtomoEconomy.EarlyClaimPrice("jetcoin", 50, TimeSpan.FromHours(1));
        try
        {
            _economy.Seed(account, balances: [("jetcoin", 1000)]);

            using var immediate = await f.PostAsync(player, "/capsule/immediateOpen", JsonSerializer.Serialize(new { slotNumber = 1 }));

            Assert.Equal(2, immediate.RootElement.GetProperty("dropDiscList").GetArrayLength());
            Assert.Equal(1000 - 4 * 50, _economy.BalanceOf(account, "jetcoin"));
            Assert.True(Assert.Single(_economy.EntitlementsOf(account)).Claimed);
            var claim = Assert.Single(_economy.Requests, call => call.Path.EndsWith("/claim") && call.Body!.Contains(account));
            Assert.Contains("\"early\":true", claim.Body);
            Assert.Single(_economy.Requests, call => call.Path.EndsWith("/early_claim") && call.Body!.Contains(account));
            // Plain open of a capsule not due is still refused: the price is for immediateOpen.
        }
        finally
        {
            _economy.EarlyClaim = null;
        }
    }

    [Fact]
    public async Task Immediate_open_without_the_money_is_the_not_enough_answer_and_moves_nothing()
    {
        var f = NewFixture();
        var player = await f.AuthAsync();
        var account = f.AccountOf(player);
        var (entry, ctx) = await f.EnterAsync(player);
        using var result = await f.PostAsync(player, "/battle/result", Result(entry, player.PlayerId, ctx.OwnTeamType!.Value));
        _economy.EarlyClaim = new FakeOtomoEconomy.EarlyClaimPrice("jetcoin", 50, TimeSpan.FromHours(1));
        try
        {
            _economy.Seed(account, balances: [("jetcoin", 199)]);

            using var refused = await f.PostRawAsync(player, "/capsule/immediateOpen", JsonSerializer.Serialize(new { slotNumber = 1 }));

            // The same answer a build-up the wallet cannot cover gets: 200, code 2502 and a plaintext reason.
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            Assert.Equal("2502", refused.Headers.GetValues("x-app-status-code").Single());
            Assert.Contains("enough", await refused.Content.ReadAsStringAsync());
            Assert.Equal(199, _economy.BalanceOf(account, "jetcoin"));
            Assert.False(Assert.Single(_economy.EntitlementsOf(account)).Claimed);
            Assert.Empty(RollsFor(account));
        }
        finally
        {
            _economy.EarlyClaim = null;
        }
    }

    [Fact]
    public async Task Home_with_no_capsules_serves_an_empty_list_and_creates_nothing()
    {
        var f = NewFixture();
        var player = await f.AuthAsync();
        var account = f.AccountOf(player);
        using var home = await f.PostAsync(player, "/home/index", "{}");
        Assert.Empty(home.RootElement.GetProperty("userCapsuleList").EnumerateArray());
        Assert.Empty(CreatesFor(account));
    }

    // --- the fake's own contract, which the tests above lean on ---------------------------------------------------

    [Fact]
    public async Task The_economy_fake_enforces_the_kind_the_cap_the_key_and_the_not_before_check()
    {
        var player = "fake-" + Guid.NewGuid().ToString("N");
        using var http = new HttpClient { BaseAddress = new Uri(_economy.BaseUrl) };
        http.DefaultRequestHeaders.Add("Authorization", "Bearer test-gameserver-key");
        async Task<HttpResponseMessage> Create(string kind, string rule, string key, DateTimeOffset? at = null) =>
            await http.PostAsJsonAsync("/internal/economy/entitlements", new
            {
                player_id = player, kind, rule, key, source = "s",
                available_at = (at ?? DateTimeOffset.UtcNow.AddHours(1)).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                meta = new { slot = 1 }
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Create("mail", Golden, "k0")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Create("capsule", "no.such.class", "k0")).StatusCode);
        var first = await Create("capsule", Golden, "k1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        // The same key answers the same row; the same key with another rule is refused.
        Assert.Equal(firstId, (await (await Create("capsule", Golden, "k1")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Create("capsule", "match.capsule.wooden", "k1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Create("capsule", Golden, "k2")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Create("capsule", Golden, "k3")).StatusCode);
        var capped = await Create("capsule", Golden, "k4");
        Assert.Equal(HttpStatusCode.Conflict, capped.StatusCode);
        Assert.Contains("entitlement_cap", await capped.Content.ReadAsStringAsync());
        // A replay of a key that made a row is not a fourth row, even when the cap is full.
        Assert.Equal(HttpStatusCode.OK, (await Create("capsule", Golden, "k2")).StatusCode);

        // Not before available_at; and nobody else's row.
        var notReady = await http.PostAsJsonAsync($"/internal/economy/entitlements/{firstId}/claim", new { player_id = player, key = "c1" });
        Assert.Equal(HttpStatusCode.Conflict, notReady.StatusCode);
        Assert.Contains("entitlement_not_ready", await notReady.Content.ReadAsStringAsync());
        var stranger = await http.PostAsJsonAsync($"/internal/economy/entitlements/{firstId}/claim", new { player_id = "someone-else", key = "c1" });
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);

        _economy.MakeDue(player);
        var claimed = await http.PostAsJsonAsync($"/internal/economy/entitlements/{firstId}/claim", new { player_id = player, key = "c1" });
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        Assert.True((await claimed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("applied").GetBoolean());
        Assert.Equal(2, _economy.BalanceOf(player, "capsule_golden"));
        // A second claim is 409 entitlement_claimed and pays nothing; and the claimed row no longer counts against the cap.
        var twice = await http.PostAsJsonAsync($"/internal/economy/entitlements/{firstId}/claim", new { player_id = player, key = "c2" });
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal(2, _economy.BalanceOf(player, "capsule_golden"));
        Assert.Equal(HttpStatusCode.OK, (await Create("capsule", Golden, "k4")).StatusCode);

        var open = await (await http.GetAsync($"/internal/economy/entitlements?player_id={player}&include_claimed=false")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, open.GetProperty("entitlements").GetArrayLength());
        var all = await (await http.GetAsync($"/internal/economy/entitlements?player_id={player}&include_claimed=true")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, all.GetProperty("entitlements").GetArrayLength());
    }

    // --- plumbing ---------------------------------------------------------------------------------------------

    private List<FakeOtomoEconomy.RecordedRequest> CreatesFor(string account) =>
        _economy.Requests.Where(call => call.Path == "/internal/economy/entitlements" && call.Method == "POST" &&
                                        call.Body is not null && call.Body.Contains($"\"player_id\":\"{account}\"")).ToList();

    // The rolls this player's own token made (a roll names no player in its body: the token is the identity).
    private FakeOtomoEconomy.RecordedRequest[] RollsFor(string account) =>
        _economy.Requests.Where(call => call.Path.EndsWith("/gacha/roll") &&
                                        FakeOtomoEconomy.AccountOf(call.Authorization) == account).ToArray();

    private FakeOtomoEconomy.RecordedRequest[] GrantsFor(string account, string grantClass) =>
        _economy.Requests.Where(call => call.Path == "/internal/economy/grants" && call.Body is not null &&
                                        call.Body.Contains($"\"player_id\":\"{account}\"") &&
                                        call.Body.Contains($"\"class\":\"{grantClass}\"")).ToArray();

    private async Task CreateDirectAsync(string account, string key)
    {
        using var http = new HttpClient { BaseAddress = new Uri(_economy.BaseUrl) };
        http.DefaultRequestHeaders.Add("Authorization", "Bearer test-gameserver-key");
        var response = await http.PostAsJsonAsync("/internal/economy/entitlements", new
        {
            player_id = account, kind = "capsule", rule = Golden, key, source = key,
            available_at = DateTimeOffset.UtcNow.AddHours(5).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            meta = new { }
        });
        response.EnsureSuccessStatusCode();
    }

    private async Task ClaimDirectAsync(string account, string id)
    {
        using var http = new HttpClient { BaseAddress = new Uri(_economy.BaseUrl) };
        http.DefaultRequestHeaders.Add("Authorization", "Bearer test-gameserver-key");
        (await http.PostAsJsonAsync($"/internal/economy/entitlements/{id}/claim", new { player_id = account, key = "outside" }))
            .EnsureSuccessStatusCode();
    }

    private static string Result(string entryId, long playerId, int ownTeam) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["battleEntryId"] = entryId,
        ["killCount"] = 0,
        ["teamScoreList"] = new[] { new { teamType = ownTeam, score = 9 }, new { teamType = 1 - ownTeam, score = 1 } },
        ["userScoreList"] = new[]
        {
            new { userId = playerId, score = 1, mvpFlag = false, aiFlag = false, breakawayFlag = false, badConnectionFlag = false }
        }
    });

    // Every battle draws `rule` (golden, two discs, unless a test says otherwise).
    private Fixture NewFixture(string rule = Golden)
    {
        _economy.CapsuleClassOverride = rule;
        var keyFile = Path.Combine(_tempDirectory, "gameserver.key");
        File.WriteAllText(keyFile, "test-gameserver-key");
        var fixture = new Fixture(new AdjustableClock(DateTimeOffset.UtcNow.AddMinutes(5)), new Dictionary<string, string?>
        {
            ["Otomo:Economy:ProgressEnabled"] = "true",
            ["Otomo:Economy:InternalBaseUrl"] = _economy.BaseUrl,
            ["Otomo:Economy:ServiceKeyFile"] = keyFile
        });
        _fixtures.Add(fixture);
        return fixture;
    }

    public void Dispose()
    {
        _economy.CapsuleClassOverride = null;
        foreach (var fixture in _fixtures) fixture.Dispose();
        try { Directory.Delete(_tempDirectory, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private sealed class AdjustableClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Player(string Token, byte[] Key, long PlayerId);

    private sealed class Writer : IServerStreamWriter<GetAssignmentsResponse>
    {
        private readonly TaskCompletionSource<string> _final = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WriteOptions? WriteOptions { get; set; }
        public Task<string> Final => _final.Task;

        public Task WriteAsync(GetAssignmentsResponse message)
        {
            if (!string.IsNullOrWhiteSpace(message.Assignment.Connection)) _final.TrySetResult(message.Assignment.Connection);
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly WebApplicationFactory<Program> _factory;
        private readonly HttpClient _client;
        private readonly BattleMatchmakingService _matchmaking;
        private readonly IPlayerStore _store;
        private readonly int _ruleId;
        private readonly List<(Task Task, CancellationTokenSource Cancel)> _streams = [];

        public Fixture(TimeProvider clock, Dictionary<string, string?> settings)
        {
            Clock = clock;
            // Entry and room use the real clock; the API clock is 5 minutes ahead so the room is old enough.
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(AppContext.BaseDirectory);
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton(clock);
                });
            });
            _client = _factory.CreateClient();
            _matchmaking = _factory.Services.GetRequiredService<BattleMatchmakingService>();
            _matchmaking.MatchWindow = TimeSpan.FromMilliseconds(1);
            _matchmaking.JoinIncrementBaseSeconds = 0;
            _store = _factory.Services.GetRequiredService<IPlayerStore>();
            _ruleId = RankedModeRotation.RuleIdAt(clock.GetUtcNow());
        }

        public TimeProvider Clock { get; }

        public string AccountOf(Player player) => _store.FindOtomoPlayer(player.PlayerId)!.OtomoPlayerId;

        public async Task<Player> AuthAsync()
        {
            var payload = JsonSerializer.Serialize(new { hash = SessionKey, uuid = Guid.NewGuid().ToString("N") });
            using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/index")
            {
                Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(payload), Encoding.ASCII.GetBytes(CommonCode), new byte[16]))
            };
            request.Headers.Host = Host;
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return new Player(response.Headers.GetValues("x-app-access-token").Single(), Encoding.ASCII.GetBytes(SessionKey),
                long.Parse(response.Headers.GetValues("x-app-user-id").Single(), CultureInfo.InvariantCulture));
        }

        public async Task<HttpResponseMessage> PostRawAsync(Player player, string path, string json)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(json), player.Key, new byte[16]))
            };
            request.Headers.Host = Host;
            request.Headers.Add("x-app-access-token", player.Token);
            return await _client.SendAsync(request);
        }

        public async Task<JsonDocument> PostAsync(Player player, string path, string json)
        {
            using var response = await PostRawAsync(player, path, json);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("0", response.Headers.GetValues("x-app-status-code").Single());
            return JsonDocument.Parse(D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), player.Key));
        }

        public async Task<(string EntryId, BattleMatchmakingService.BadgeBattleEntryContext Context)> EnterAsync(Player player)
        {
            using var entry = await PostAsync(player, "/battle/entry", JsonSerializer.Serialize(new { battleRuleId = _ruleId }));
            var entryId = entry.RootElement.GetProperty("battleEntryId").GetString()!;
            var ticketId = entry.RootElement.GetProperty("battleEntryTicketId").GetString()!;
            var cancel = new CancellationTokenSource();
            var writer = new Writer();
            _streams.Add((_matchmaking.StreamAssignmentsAsync(ticketId, writer, cancel.Token), cancel));
            await writer.Final.WaitAsync(TimeSpan.FromSeconds(10));
            var context = _matchmaking.GetBadgeBattleContext(entryId)!;
            Assert.NotNull(context.OwnTeamType);
            return (entryId, context);
        }

        public void Dispose()
        {
            foreach (var (task, cancel) in _streams)
            {
                cancel.Cancel();
                try { task.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            }
            _client.Dispose();
            _factory.Dispose();
        }
    }
}
