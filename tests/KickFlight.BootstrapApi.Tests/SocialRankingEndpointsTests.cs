using System.Net;
using System.Text;
using System.Text.Json;
using KickFlight.BootstrapApi.Otomo;
using KickFlight.BootstrapApi.PlayerStore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The social screens and the ranking board against a real (fake) session service over HTTP. Every follow edge and
// every point the client is shown comes from that service; this adapter keeps neither, and migration 6 dropped the
// tables it used to keep the edges in.
//
// A follow is addressed by the target's otomo player id, which the adapter translates from the numeric id the client
// sends. No name or tag is needed (or kept) for that, and every name on a row is the one session answered for it.
public sealed class SocialRankingEndpointsTests
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";

    private readonly FakeOtomoSocial _social = OtomoTestEnvironment.Social;

    [Fact]
    public async Task Follow_flows_are_served_from_otomo_and_the_adapter_keeps_no_edge()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        using var factory = NewFactory(clock);
        var alice = await CreateSessionAsync(factory);
        var bob = await CreateSessionAsync(factory);
        var aliceId = long.Parse(alice.UserId);
        var bobId = long.Parse(bob.UserId);
        // Names are the tenant's session.rules names: at most 10 characters, as the client lets a player type.
        await PostAsync(alice, "/user/change", new { name = "SA" + alice.UserId[^4..] });
        await PostAsync(bob, "/user/change", new { name = "SB" + bob.UserId[^4..] });

        await PostAsync(alice, "/follow/add", new { followUserId = bob.UserId });
        await PostAsync(alice, "/follow/add", new { followUserId = bob.UserId });
        Assert.True(_social.IsFollowing(alice.Account, bob.Account));

        var following = await PostAsync(alice, "/follow/index", new { page = 0, teamBattleInvitationFlag = false });
        Assert.Equal(1, following.GetProperty("followCount").GetInt32());
        Assert.Equal(bob.UserId, following.GetProperty("userProfileList")[0].GetProperty("userId").GetString());
        Assert.Equal(PlayerDisplayIdCodec.ToPublic(bobId), following.GetProperty("userProfileList")[0].GetProperty("displayUserId").GetInt64());
        Assert.Equal(1, following.GetProperty("userProfileList")[0].GetProperty("followStatus").GetInt32());

        Assert.Equal(1, (await PostAsync(alice, "/follow/online", null)).GetProperty("userProfileList").GetArrayLength());
        clock.Advance(TimeSpan.FromMinutes(11) + TimeSpan.FromSeconds(1));
        Assert.Empty((await PostAsync(alice, "/follow/online", null)).GetProperty("userProfileList").EnumerateArray());
        var inactiveProfile = (await PostAsync(alice, "/user/search", new { displayUserId = bobId }))
            .GetProperty("userProfile");
        Assert.False(inactiveProfile.GetProperty("onlineFlag").GetBoolean());
        await PostAsync(bob, "/user/online", null); // The client polls this empty endpoint every ten minutes.
        Assert.Equal(1, (await PostAsync(alice, "/follow/online", null)).GetProperty("userProfileList").GetArrayLength());
        var activeProfile = (await PostAsync(alice, "/user/search", new { displayUserId = bobId }))
            .GetProperty("userProfile");
        Assert.True(activeProfile.GetProperty("onlineFlag").GetBoolean());
        clock.Advance(TimeSpan.FromMinutes(11) + TimeSpan.FromSeconds(1));
        var expiredProfile = (await PostAsync(alice, "/user/search", new { displayUserId = bobId }))
            .GetProperty("userProfile");
        Assert.False(expiredProfile.GetProperty("onlineFlag").GetBoolean());

        var inbound = await PostAsync(bob, "/follower/index", new { page = 0 });
        Assert.Equal(1, inbound.GetProperty("followerCount").GetInt32());
        // Session has no read cursor on a follow, so nothing is "new" and the count is zero rather than a number nobody
        // could have marked read.
        Assert.Equal(0, inbound.GetProperty("newFollowerCount").GetInt32());
        Assert.False(inbound.GetProperty("userProfileList")[0].GetProperty("newFlag").GetBoolean());
        var searched = await PostAsync(alice, "/follow/search", new { userIdList = new[] { bob.UserId, alice.UserId } });
        Assert.Equal(new[] { bob.UserId }, searched.GetProperty("followUserIdList").EnumerateArray().Select(x => x.GetString()).ToArray());
        var invalidPage = await PostAsync(alice, "/follow/index", new { page = -1 });
        Assert.Equal(1, invalidPage.GetProperty("userProfileList").GetArrayLength());

        await PostAsync(bob, "/follower/read", new { followerUserIdList = new[] { alice.UserId } });
        Assert.Equal(0, (await PostAsync(bob, "/follower/index", new { page = 0 })).GetProperty("newFollowerCount").GetInt32());

        await PostAsync(alice, "/follow/remove", new { followUserId = bob.UserId });
        Assert.False(_social.IsFollowing(alice.Account, bob.Account));
        Assert.Equal(0, (await PostAsync(alice, "/follow/index", new { page = 0 })).GetProperty("followCount").GetInt32());

        // The refusals are session's (a self-follow) or the adapter's own (a numeric id with no otomo account behind it).
        // The real-friend invitation has no source at all any more.
        await PostAsync(alice, "/follow/add", new { followUserId = alice.UserId }, expectStatusZero: false);
        await PostAsync(alice, "/follow/add", new { followUserId = long.MaxValue.ToString() }, expectStatusZero: false);
        await PostAsync(alice, "/follow/remove", new { followUserId = long.MaxValue.ToString() }, expectStatusZero: false);
        await PostAsync(alice, "/realFriend/token", null, expectStatusZero: false);
        await PostAsync(alice, "/realFriend/apply", new { token = "not-a-token" }, expectStatusZero: false);
        await PostAsync(alice, "/user/search", new { displayUserId = long.MaxValue }, expectStatusZero: false);
        Assert.StartsWith("SA", factory.Services.GetRequiredService<IPlayerStore>()
            .FindProfile(aliceId)?.DisplayName ?? "");
    }

    [Fact]
    public async Task Follow_needs_no_name_and_sends_the_target_player_id()
    {
        using var factory = NewFactory();
        var alice = await CreateSessionAsync(factory);
        var bob = await CreateSessionAsync(factory);
        _social.Ensure(bob.Account); // session knows both accounts; neither has set a name
        // Neither player has been named: the old code refused here ("no otomo tag yet"). Session addresses a follow by
        // player id, so the adapter's mapping is all that is needed, and the fake only accepts {"player_id"}.
        await PostAsync(alice, "/follow/add", new { followUserId = bob.UserId });
        Assert.True(_social.IsFollowing(alice.Account, bob.Account));
    }

    [Fact]
    public async Task Follow_refusals_from_session_reach_the_client_as_status_errors()
    {
        using var factory = NewFactory();
        var alice = await CreateSessionAsync(factory);
        var bob = await CreateSessionAsync(factory);
        var carol = await CreateSessionAsync(factory);
        _social.Ensure(bob.Account);
        _social.Ensure(carol.Account);

        // 403 blocked
        _social.Block(bob.Account, alice.Account);
        await PostAsync(alice, "/follow/add", new { followUserId = bob.UserId }, expectStatusZero: false);
        Assert.False(_social.IsFollowing(alice.Account, bob.Account));

        // 400 invalid_target (session's own check; the adapter's pre-check also catches this id)
        await PostAsync(alice, "/follow/add", new { followUserId = alice.UserId }, expectStatusZero: false);

        // A refusal is not an outage: the next follow still works.
        await PostAsync(alice, "/follow/add", new { followUserId = carol.UserId });
        Assert.True(_social.IsFollowing(alice.Account, carol.Account));
    }

    [Fact]
    public async Task Names_on_follow_rows_are_the_name_session_answered_for_the_row()
    {
        using var factory = NewFactory();
        var alice = await CreateSessionAsync(factory);
        var bob = await CreateSessionAsync(factory);
        await PostAsync(bob, "/user/change", new { name = "LB" + bob.UserId[^4..] });
        // Session's name now differs from the adapter's own copy: the row must show session's.
        _social.SeedName(bob.Account, "Session Bob " + bob.UserId[^4..]);
        await PostAsync(alice, "/follow/add", new { followUserId = bob.UserId });

        var following = await PostAsync(alice, "/follow/index", new { page = 0 });
        Assert.StartsWith("Session Bob ", following.GetProperty("userProfileList")[0].GetProperty("name").GetString());
        var followers = await PostAsync(bob, "/follower/index", new { page = 0 });
        Assert.Equal(1, followers.GetProperty("followerCount").GetInt32());
    }

    [Fact]
    public async Task Ranking_tabs_use_the_board_global_positions_and_the_follow_filter()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        using var factory = NewFactory(clock);
        var alice = await CreateSessionAsync(factory);
        var bob = await CreateSessionAsync(factory);
        var carol = await CreateSessionAsync(factory);
        await PostAsync(alice, "/user/change", new { name = "RA" + alice.UserId[^4..] });
        await PostAsync(bob, "/user/change", new { name = "RB" + bob.UserId[^4..] });
        await PostAsync(carol, "/user/change", new { name = "RC" + carol.UserId[^4..] });

        var store = factory.Services.GetRequiredService<IPlayerStore>();
        var aliceId = long.Parse(alice.UserId);
        var bobId = long.Parse(bob.UserId);
        // The ladder is session's, in the client's own units: battle points are session's points as they are, and the
        // rank is the N of the tier key rank_<N> (1100 -> rank_3, 3500 -> rank_8, 2500 -> rank_6). The fake is shared by the suite, so
        // the board starts empty for this test's three players.
        _social.ResetBoards();
        _social.SeedRank(alice.Account, "crystal_scramble", 1100);
        _social.SeedRank(bob.Account, "crystal_scramble", 3500);
        _social.SeedRank(carol.Account, "crystal_scramble", 2500);
        await PostAsync(alice, "/follow/add", new { followUserId = bob.UserId });

        var world = await PostAsync(alice, "/ranking/index", new { battleRuleId = 1 });
        Assert.Equal(3, world.GetProperty("battleRankingList").GetArrayLength());
        Assert.Equal(bob.UserId, world.GetProperty("battleRankingList")[0].GetProperty("userId").GetString());
        Assert.Equal(3500, world.GetProperty("battleRankingList")[0].GetProperty("battlePoint").GetInt32());
        Assert.Equal(1, world.GetProperty("battleRankingList")[0].GetProperty("number").GetInt32());
        Assert.Equal(8, world.GetProperty("battleRankingList")[0].GetProperty("rank").GetInt32());
        // The row's name is the board's, which is otomo's name for that account - written through by /user/change.
        Assert.StartsWith("RB", world.GetProperty("battleRankingList")[0].GetProperty("name").GetString());
        Assert.Equal(3, world.GetProperty("battleRanking").GetProperty("rank").GetInt32());
        Assert.Equal(1100, world.GetProperty("battleRanking").GetProperty("battlePoint").GetInt32());
        // 1100 points is rank 3; the next published tier (rank_4) starts at 1400.
        Assert.Equal(300, world.GetProperty("nextRewardRemainingBattlePoint").GetInt32());
        var ownPosition = world.GetProperty("battleRanking").GetProperty("number").GetInt32();
        Assert.Equal(3, ownPosition);

        // Tapping a crown row uses its raw userId string in /user/detail.searchUserId.
        var topUserId = world.GetProperty("battleRankingList")[0].GetProperty("userId").GetString()!;
        var topUserState = Assert.IsType<SessionState>(store.TryLoad(long.Parse(topUserId)));
        // The deck is session's: a player who never changed it holds the starter decks, written to ext on first read.
        var topUserDeck = Loadout.DefaultDecks()[1];
        var detail = await PostAsync(alice, "/user/detail", new { searchUserId = topUserId });
        var detailedProfile = detail.GetProperty("userProfile");
        var battleParameter = detail.GetProperty("userBattleParameter");
        Assert.Equal(topUserId, detailedProfile.GetProperty("userId").GetString());
        Assert.Equal(PlayerDisplayIdCodec.ToPublic(long.Parse(topUserId)), detailedProfile.GetProperty("displayUserId").GetInt64());
        Assert.Equal(1, battleParameter.GetProperty("battleRuleId").GetInt32());
        Assert.Equal(topUserState.KickerId, battleParameter.GetProperty("kickerId").GetInt32());
        Assert.Equal(topUserState.KickerCostumeId, battleParameter.GetProperty("kickerCostumeId").GetInt32());
        for (var slot = 0; slot < 4; slot++)
        {
            Assert.Equal(topUserDeck[slot], battleParameter.GetProperty($"discId{slot + 1}").GetInt32());
            Assert.True(battleParameter.GetProperty($"discLevel{slot + 1}").GetInt32() > 0);
        }

        var personal = await PostAsync(alice, "/ranking/user", new { battleRuleId = 1 });
        var ownPersonalRow = Assert.Single(personal.GetProperty("battleRankingList").EnumerateArray(),
            row => row.GetProperty("userId").GetString() == alice.UserId);
        Assert.Equal(ownPosition, ownPersonalRow.GetProperty("number").GetInt32());

        var follow = await PostAsync(alice, "/ranking/follow", new { battleRuleId = 1 });
        Assert.Equal(1, follow.GetProperty("battleRankingList").GetArrayLength());
        Assert.Equal(bob.UserId, follow.GetProperty("battleRankingList")[0].GetProperty("userId").GetString());
        Assert.Equal(1, follow.GetProperty("battleRankingList")[0].GetProperty("number").GetInt32());
        Assert.Equal(bob.UserId, store.FindProfiles([bobId]).Single().PlayerId.ToString());

        // The follow tab is ONE board call (scope=following, limit=100), and `number` is session's `position` on the
        // full ladder: carol is the caller's only follow now, second on the ladder, and her list index is 0.
        await PostAsync(alice, "/follow/remove", new { followUserId = bob.UserId });
        await PostAsync(alice, "/follow/add", new { followUserId = carol.UserId });
        _social.ClearRequests();
        var carolOnly = await PostAsync(alice, "/ranking/follow", new { battleRuleId = 1 });
        var carolRow = Assert.Single(carolOnly.GetProperty("battleRankingList").EnumerateArray());
        Assert.Equal(carol.UserId, carolRow.GetProperty("userId").GetString());
        Assert.Equal(2, carolRow.GetProperty("number").GetInt32());
        Assert.Equal(2500, carolRow.GetProperty("battlePoint").GetInt32());
        var boardCall = Assert.Single(_social.BoardRequests);
        Assert.Contains("scope=following", boardCall.FullPath, StringComparison.Ordinal);
        Assert.Contains("limit=100", boardCall.FullPath, StringComparison.Ordinal);

        // A card shows the followed player's own standing, read from session's internal plane by otomo id with the
        // service key - not from anything this adapter stored.
        _social.ClearRequests();
        var cards = await PostAsync(alice, "/follow/index", new { page = 0 });
        var carolRank = cards.GetProperty("userProfileList")[0].GetProperty("userBattleRankList")[0];
        Assert.Equal(1, carolRank.GetProperty("battleRuleType").GetInt32());
        Assert.Equal(2500, carolRank.GetProperty("battlePoint").GetInt32());
        Assert.Equal(6, carolRank.GetProperty("rank").GetInt32());
        var internalRead = Assert.Single(_social.Requests, request =>
            request.Path == $"/internal/session/rank/players/{carol.Account}");
        Assert.Equal("Bearer " + FakeOtomoSocial.ServiceKey, internalRead.Authorization);

        var local = await PostAsync(alice, "/ranking/region", new { battleRuleId = 1 });
        Assert.Equal(world.GetProperty("battleRankingList")[0].GetProperty("userId").GetString(),
            local.GetProperty("battleRankingList")[0].GetProperty("userId").GetString());
        Assert.StartsWith("RA", store.FindProfile(aliceId)?.DisplayName ?? "");
        Assert.Contains(_social.BoardRequests, request => request.FullPath.Contains("rule_type=crystal_scramble", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ranking_tabs_follow_the_active_rotation_and_ignore_stale_rule_requests()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        using var contentRoot = new IsolatedContentRoot();
        using var factory = NewFactory(clock, contentRoot.Path);
        var alice = await CreateSessionAsync(factory);
        var bob = await CreateSessionAsync(factory);
        var carol = await CreateSessionAsync(factory);
        await PostAsync(alice, "/user/change", new { name = "ZA" + alice.UserId[^4..] });
        await PostAsync(bob, "/user/change", new { name = "ZB" + bob.UserId[^4..] });
        await PostAsync(carol, "/user/change", new { name = "ZC" + carol.UserId[^4..] });

        // Give each ladder a different order and points, so a stale client battleRuleId cannot pass by coincidence.
        _social.ResetBoards();
        _social.SeedRank(alice.Account, "crystal_scramble", 100_001_100);
        _social.SeedRank(bob.Account, "crystal_scramble", 100_001_200);
        _social.SeedRank(carol.Account, "crystal_scramble", 100_001_300);
        _social.SeedRank(alice.Account, "flag_flight", 200_002_300);
        _social.SeedRank(bob.Account, "flag_flight", 200_002_100);
        _social.SeedRank(carol.Account, "flag_flight", 200_002_200);
        _social.SeedRank(alice.Account, "rapid_ball", 300_003_100);
        _social.SeedRank(bob.Account, "rapid_ball", 300_003_300);
        _social.SeedRank(carol.Account, "rapid_ball", 300_003_200);
        await PostAsync(alice, "/follow/add", new { followUserId = bob.UserId });

        var routes = new[] { "/ranking/index", "/ranking/user", "/ranking/follow", "/ranking/region" };
        var expectedModes = new[]
        {
            new { ruleId = RankedModeRotation.CrystalRuleId, ruleType = 1, topUser = carol.UserId, topPoints = 100_001_300, alicePoints = 100_001_100, bobPoints = 100_001_200, staleRule = RankedModeRotation.RapidBallRuleId },
            new { ruleId = RankedModeRotation.FlagRuleId, ruleType = 2, topUser = alice.UserId, topPoints = 200_002_300, alicePoints = 200_002_300, bobPoints = 200_002_100, staleRule = RankedModeRotation.CrystalRuleId },
            new { ruleId = RankedModeRotation.RapidBallRuleId, ruleType = 3, topUser = bob.UserId, topPoints = 300_003_300, alicePoints = 300_003_100, bobPoints = 300_003_300, staleRule = RankedModeRotation.FlagRuleId }
        };

        string[] RuleTypeNames = ["crystal_scramble", "flag_flight", "rapid_ball"];
        // Session's points are the client's battle points as they are.
        static int Client(int points) => points;
        foreach (var mode in expectedModes)
        {
            Assert.Equal(mode.ruleId, RankedModeRotation.RuleIdAt(clock.GetUtcNow()));
            var ladder = $"rule_type={RuleTypeNames[mode.ruleType - 1]}";
            _social.ClearRequests();
            foreach (var route in routes)
            {
                foreach (var requestBody in new object[] { new { battleRuleId = mode.staleRule }, new { } })
                {
                    var response = await PostAsync(alice, route, requestBody);
                    var list = response.GetProperty("battleRankingList");
                    if (route == "/ranking/follow")
                    {
                        var row = Assert.Single(list.EnumerateArray());
                        Assert.Equal(bob.UserId, row.GetProperty("userId").GetString());
                        Assert.Equal(Client(mode.bobPoints), row.GetProperty("battlePoint").GetInt32());
                        Assert.True(row.GetProperty("number").GetInt32() > 0);
                    }
                    else if (route == "/ranking/user")
                    {
                        Assert.Contains(list.EnumerateArray(), row =>
                            row.GetProperty("userId").GetString() == mode.topUser &&
                            row.GetProperty("battlePoint").GetInt32() == Client(mode.topPoints));
                    }
                    else
                    {
                        Assert.Equal(mode.topUser, list[0].GetProperty("userId").GetString());
                        Assert.Equal(Client(mode.topPoints), list[0].GetProperty("battlePoint").GetInt32());
                    }
                    // The local tab has no own-entry member: it lists the population, not the caller's standing.
                    if (route != "/ranking/region")
                    {
                        Assert.Equal(Client(mode.alicePoints), response.GetProperty("battleRanking").GetProperty("battlePoint").GetInt32());
                    }
                }
            }

            // Every board read asked for the ladder the rotation is on, whatever the client said.
            Assert.NotEmpty(_social.BoardRequests);
            Assert.All(_social.BoardRequests, request => Assert.Contains(ladder, request.FullPath, StringComparison.Ordinal));
            clock.Advance(TimeSpan.FromHours(1));
        }
    }

    [Fact]
    public async Task User_detail_returns_target_active_deck_and_valid_defaults_when_deck_is_missing()
    {
        using var factory = NewFactory();
        var viewer = await CreateSessionAsync(factory);
        var target = await CreateSessionAsync(factory);
        var store = factory.Services.GetRequiredService<IPlayerStore>();
        var targetId = long.Parse(target.UserId);
        var targetState = Assert.IsType<SessionState>(store.TryLoad(targetId));
        var targetKickerId = targetState.KickerId;
        var targetCostumeId = targetState.KickerCostumeId;
        var activeDeck = new[] { 3010005, 3010006, 3010007, 3010008 };
        var targetAccount = store.FindOtomoPlayer(targetId)!.OtomoPlayerId;
        _social.SeedExt(targetAccount, """{"decks":{"3":[3010005,3010006,3010007,3010008]},"active_deck":3}""");

        var detail = await PostAsync(viewer, "/user/detail", new
        {
            searchUserId = PlayerDisplayIdCodec.ToPublic(targetId).ToString()
        });
        var profile = detail.GetProperty("userProfile");
        var battle = detail.GetProperty("userBattleParameter");
        Assert.Equal(target.UserId, profile.GetProperty("userId").GetString());
        Assert.Equal(PlayerDisplayIdCodec.ToPublic(targetId), profile.GetProperty("displayUserId").GetInt64());
        Assert.Equal(targetKickerId, profile.GetProperty("kickerId").GetInt32());
        Assert.Equal(targetCostumeId, profile.GetProperty("kickerCostumeId").GetInt32());
        Assert.Equal(1, battle.GetProperty("battleRuleId").GetInt32());
        Assert.Equal(targetKickerId, battle.GetProperty("kickerId").GetInt32());
        Assert.Equal(targetCostumeId, battle.GetProperty("kickerCostumeId").GetInt32());
        for (var slot = 0; slot < activeDeck.Length; slot++)
        {
            Assert.Equal(activeDeck[slot], battle.GetProperty($"discId{slot + 1}").GetInt32());
            // The level is attrs.level on that player's own possession, and economy has no cross-player read: the card
            // shows the level each disc starts at. The ids are still that player's own deck, which is what the screen
            // is for - this assertion used to read the target's save, and that path is gone.
            Assert.True(battle.GetProperty($"discLevel{slot + 1}").GetInt32() > 0);
        }

        _social.SeedExt(targetAccount, """{"decks":{"3":[3010005,3010006,3010007,3010008]},"active_deck":99}""");
        var missingDeckDetail = await PostAsync(viewer, "/user/detail", new
        {
            searchUserId = PlayerDisplayIdCodec.ToPublic(targetId).ToString()
        });
        var fallback = missingDeckDetail.GetProperty("userBattleParameter");
        Assert.Equal(new[] { 3010001, 3010002, 3010003, 3010004 },
            Enumerable.Range(1, 4).Select(slot => fallback.GetProperty($"discId{slot}").GetInt32()).ToArray());
        Assert.All(Enumerable.Range(1, 4), slot => Assert.True(fallback.GetProperty($"discLevel{slot}").GetInt32() > 0));
        Assert.DoesNotContain(Enumerable.Range(1, 4), slot => fallback.GetProperty($"discId{slot}").GetInt32() == 0);

        // A deck of fewer than four discs (session's schema allows up to eight, and does not allow a 0 placeholder, which
        // is what this case used to store) is completed from the initial deck slot by slot.
        _social.SeedExt(targetAccount, """{"decks":{"98":[3010005]},"active_deck":98}""");
        var partialDeckDetail = await PostAsync(viewer, "/user/detail", new
        {
            searchUserId = PlayerDisplayIdCodec.ToPublic(targetId).ToString()
        });
        var partial = partialDeckDetail.GetProperty("userBattleParameter");
        Assert.Equal(new[] { 3010005, 3010002, 3010003, 3010004 },
            Enumerable.Range(1, 4).Select(slot => partial.GetProperty($"discId{slot}").GetInt32()).ToArray());
    }

    private static WebApplicationFactory<Program> NewFactory(TimeProvider? clock = null, string? contentRoot = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(contentRoot ?? AppContext.BaseDirectory);
            if (clock is not null)
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(clock);
                });
            }
        });

    private sealed class IsolatedContentRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kf-ranking-" + Guid.NewGuid().ToString("N"));

        public IsolatedContentRoot()
        {
            Directory.CreateDirectory(Path);
            var repositoryRoot = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
            Directory.CreateSymbolicLink(System.IO.Path.Combine(Path, "config"), System.IO.Path.Combine(repositoryRoot, "config"));
            Directory.CreateSymbolicLink(System.IO.Path.Combine(Path, "content"), System.IO.Path.Combine(repositoryRoot, "content"));
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static async Task<DemoSession> CreateSessionAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
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
        var account = OtomoTestEnvironment.Server.AccountFor(OtomoDeviceId.FromDeviceUuid(uuid))
                      ?? throw new InvalidOperationException("the fake auth server did not report an account");
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
        using var document = JsonDocument.Parse(D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), session.Key));
        return document.RootElement.Clone();
    }

    private sealed record DemoSession(HttpClient Client, byte[] Key, string UserId, string Account);

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
