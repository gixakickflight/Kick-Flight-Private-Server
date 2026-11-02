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

public sealed class SocialRankingEndpointsTests
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";

    [Fact]
    public async Task Follow_and_real_friend_flows_are_separate_and_persistent_in_responses()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        using var factory = NewFactory(clock);
        var alice = await CreateSessionAsync(factory);
        var bob = await CreateSessionAsync(factory);
        var aliceId = long.Parse(alice.UserId);
        var bobId = long.Parse(bob.UserId);
        await PostAsync(alice, "/user/change", new { name = "Social Alice " + alice.UserId[^4..] });
        await PostAsync(bob, "/user/change", new { name = "Social Bob " + bob.UserId[^4..] });

        await PostAsync(alice, "/follow/add", new { followUserId = bob.UserId });
        await PostAsync(alice, "/follow/add", new { followUserId = bob.UserId });
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
        Assert.Equal(1, inbound.GetProperty("newFollowerCount").GetInt32());
        Assert.True(inbound.GetProperty("userProfileList")[0].GetProperty("newFlag").GetBoolean());
        var searched = await PostAsync(alice, "/follow/search", new { userIdList = new[] { bob.UserId, alice.UserId } });
        Assert.Equal(new[] { bob.UserId }, searched.GetProperty("followUserIdList").EnumerateArray().Select(x => x.GetString()).ToArray());
        var invalidPage = await PostAsync(alice, "/follow/index", new { page = -1 });
        Assert.Equal(1, invalidPage.GetProperty("userProfileList").GetArrayLength());

        await PostAsync(bob, "/follower/read", new { followerUserIdList = new[] { alice.UserId } });
        Assert.Equal(0, (await PostAsync(bob, "/follower/index", new { page = 0 })).GetProperty("newFollowerCount").GetInt32());

        var token = (await PostAsync(alice, "/realFriend/token", null)).GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.Equal(token, (await PostAsync(alice, "/realFriend/token", null)).GetProperty("token").GetString());
        await PostAsync(bob, "/realFriend/apply", new { token });
        await PostAsync(bob, "/realFriend/apply", new { token });
        var realFriendProfile = (await PostAsync(bob, "/user/search", new { displayUserId = aliceId })).GetProperty("userProfile");
        Assert.Equal(2, realFriendProfile.GetProperty("followStatus").GetInt32());
        Assert.Equal(0, (await PostAsync(bob, "/follow/index", new { page = 0 })).GetProperty("followCount").GetInt32());
        await PostAsync(alice, "/follow/remove", new { followUserId = bob.UserId });
        Assert.Equal(0, (await PostAsync(alice, "/follow/index", new { page = 0 })).GetProperty("followCount").GetInt32());

        await PostAsync(alice, "/realFriend/apply", new { token }, expectStatusZero: false);
        await PostAsync(alice, "/follow/add", new { followUserId = alice.UserId }, expectStatusZero: false);
        await PostAsync(alice, "/follow/add", new { followUserId = long.MaxValue.ToString() }, expectStatusZero: false);
        await PostAsync(alice, "/realFriend/apply", new { token = "not-a-token" }, expectStatusZero: false);
        await PostAsync(alice, "/user/search", new { displayUserId = long.MaxValue }, expectStatusZero: false);
    }

    [Fact]
    public async Task Ranking_tabs_use_real_points_global_positions_and_follow_filter()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UnixEpoch);
        using var factory = NewFactory(clock);
        var alice = await CreateSessionAsync(factory);
        var bob = await CreateSessionAsync(factory);
        var carol = await CreateSessionAsync(factory);
        await PostAsync(alice, "/user/change", new { name = "Rank Alice " + alice.UserId[^4..] });
        await PostAsync(bob, "/user/change", new { name = "Rank Bob " + bob.UserId[^4..] });
        await PostAsync(carol, "/user/change", new { name = "Rank Carol " + carol.UserId[^4..] });

        var store = factory.Services.GetRequiredService<IPlayerStore>();
        var aliceId = long.Parse(alice.UserId);
        var bobId = long.Parse(bob.UserId);
        var carolId = long.Parse(carol.UserId);
        store.SaveRank(aliceId, DemoSessionApi.RegularBattleRuleType, new RankState(1200, 8));
        store.SaveRank(bobId, DemoSessionApi.RegularBattleRuleType, new RankState(3400, 11));
        store.SaveRank(carolId, DemoSessionApi.RegularBattleRuleType, new RankState(2200, 9));
        await PostAsync(alice, "/follow/add", new { followUserId = bob.UserId });

        var world = await PostAsync(alice, "/ranking/index", new { battleRuleId = 1 });
        Assert.Equal(bob.UserId, world.GetProperty("battleRankingList")[0].GetProperty("userId").GetString());
        Assert.Equal(3400, world.GetProperty("battleRankingList")[0].GetProperty("battlePoint").GetInt32());
        Assert.Equal(1, world.GetProperty("battleRankingList")[0].GetProperty("number").GetInt32());
        Assert.Equal(8, world.GetProperty("battleRanking").GetProperty("rank").GetInt32());
        var ownPosition = world.GetProperty("battleRanking").GetProperty("number").GetInt32();
        Assert.True(ownPosition > 0);

        // Tapping a crown row uses its raw userId string in /user/detail.searchUserId.
        var topUserId = world.GetProperty("battleRankingList")[0].GetProperty("userId").GetString()!;
        var topUserState = Assert.IsType<SessionState>(store.TryLoad(long.Parse(topUserId)));
        var topUserDeck = topUserState.Decks[topUserState.ActiveDeckNumber];
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

        var local = await PostAsync(alice, "/ranking/region", new { battleRuleId = 1 });
        Assert.Equal(world.GetProperty("battleRankingList")[0].GetProperty("userId").GetString(),
            local.GetProperty("battleRankingList")[0].GetProperty("userId").GetString());
        Assert.StartsWith("Rank Alice ", store.FindProfile(aliceId)?.DisplayName ?? "");
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
        await PostAsync(alice, "/user/change", new { name = "Rotation Alice " + alice.UserId[^4..] });
        await PostAsync(bob, "/user/change", new { name = "Rotation Bob " + bob.UserId[^4..] });
        await PostAsync(carol, "/user/change", new { name = "Rotation Carol " + carol.UserId[^4..] });
        var store = factory.Services.GetRequiredService<IPlayerStore>();
        var aliceId = long.Parse(alice.UserId);
        var bobId = long.Parse(bob.UserId);
        var carolId = long.Parse(carol.UserId);

        // Give each ladder a different order and points so a stale client battleRuleId cannot pass by coincidence.
        store.SaveRank(aliceId, 1, new RankState(100_001_100, 11));
        store.SaveRank(bobId, 1, new RankState(100_001_200, 12));
        store.SaveRank(carolId, 1, new RankState(100_001_300, 13));
        store.SaveRank(aliceId, 2, new RankState(200_002_300, 23));
        store.SaveRank(bobId, 2, new RankState(200_002_100, 21));
        store.SaveRank(carolId, 2, new RankState(200_002_200, 22));
        store.SaveRank(aliceId, 3, new RankState(300_003_100, 31));
        store.SaveRank(bobId, 3, new RankState(300_003_300, 33));
        store.SaveRank(carolId, 3, new RankState(300_003_200, 32));
        await PostAsync(alice, "/follow/add", new { followUserId = bob.UserId });

        var routes = new[] { "/ranking/index", "/ranking/user", "/ranking/follow", "/ranking/region" };
        var expectedModes = new[]
        {
            new { ruleId = RankedModeRotation.CrystalRuleId, ruleType = 1, topUser = carol.UserId, topPoints = 100_001_300, alicePoints = 100_001_100, bobPoints = 100_001_200, staleRule = RankedModeRotation.RapidBallRuleId },
            new { ruleId = RankedModeRotation.FlagRuleId, ruleType = 2, topUser = alice.UserId, topPoints = 200_002_300, alicePoints = 200_002_300, bobPoints = 200_002_100, staleRule = RankedModeRotation.CrystalRuleId },
            new { ruleId = RankedModeRotation.RapidBallRuleId, ruleType = 3, topUser = bob.UserId, topPoints = 300_003_300, alicePoints = 300_003_100, bobPoints = 300_003_300, staleRule = RankedModeRotation.FlagRuleId }
        };

        foreach (var mode in expectedModes)
        {
            Assert.Equal(mode.ruleId, RankedModeRotation.RuleIdAt(clock.GetUtcNow()));
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
                    Assert.Equal(mode.bobPoints, row.GetProperty("battlePoint").GetInt32());
                    Assert.True(row.GetProperty("number").GetInt32() > 0);
                }
                else if (route == "/ranking/user")
                {
                    Assert.Contains(list.EnumerateArray(), row =>
                        row.GetProperty("userId").GetString() == mode.topUser &&
                        row.GetProperty("battlePoint").GetInt32() == mode.topPoints);
                }
                else
                {
                    Assert.Equal(mode.topUser, list[0].GetProperty("userId").GetString());
                    Assert.Equal(mode.topPoints, list[0].GetProperty("battlePoint").GetInt32());
                }

                if (route != "/ranking/region")
                {
                    Assert.Equal(mode.alicePoints, response.GetProperty("battleRanking").GetProperty("battlePoint").GetInt32());
                    if (route != "/ranking/follow")
                        Assert.Equal(mode.alicePoints, Assert.Single(list.EnumerateArray(), row =>
                            row.GetProperty("userId").GetString() == alice.UserId).GetProperty("battlePoint").GetInt32());
                }
                }
            }

            // Ranking queries are projections and must not alter any of the three stored ladders.
            Assert.Equal(new[] { new RankState(100_001_100, 11), new RankState(200_002_300, 23), new RankState(300_003_100, 31) },
                Enumerable.Range(1, 3).Select(type => store.LoadRank(aliceId, type)).ToArray());
            Assert.Equal(new[] { new RankState(100_001_200, 12), new RankState(200_002_100, 21), new RankState(300_003_300, 33) },
                Enumerable.Range(1, 3).Select(type => store.LoadRank(bobId, type)).ToArray());
            Assert.Equal(new[] { new RankState(100_001_300, 13), new RankState(200_002_200, 22), new RankState(300_003_200, 32) },
                Enumerable.Range(1, 3).Select(type => store.LoadRank(carolId, type)).ToArray());

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
        targetState.ActiveDeckNumber = 3;
        targetState.Decks[3] = activeDeck.ToList();
        store.Save(targetState);

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
            // A disc's level is economy's, and this route describes another player, whose inventory this process has
            // no token for: a disc either player owns is level 10 (EconomyPayloads.OwnedDiscLevel).
            Assert.Equal(EconomyPayloads.OwnedDiscLevel, battle.GetProperty($"discLevel{slot + 1}").GetInt32());
        }

        targetState.ActiveDeckNumber = 99;
        store.Save(targetState);
        var missingDeckDetail = await PostAsync(viewer, "/user/detail", new
        {
            searchUserId = PlayerDisplayIdCodec.ToPublic(targetId).ToString()
        });
        var fallback = missingDeckDetail.GetProperty("userBattleParameter");
        Assert.Equal(new[] { 3010001, 3010002, 3010003, 3010004 },
            Enumerable.Range(1, 4).Select(slot => fallback.GetProperty($"discId{slot}").GetInt32()).ToArray());
        Assert.All(Enumerable.Range(1, 4), slot => Assert.True(fallback.GetProperty($"discLevel{slot}").GetInt32() > 0));
        Assert.DoesNotContain(Enumerable.Range(1, 4), slot => fallback.GetProperty($"discId{slot}").GetInt32() == 0);

        targetState.ActiveDeckNumber = 98;
        targetState.Decks[98] = [3010005, 0, 3010007];
        store.Save(targetState);
        var partialDeckDetail = await PostAsync(viewer, "/user/detail", new
        {
            searchUserId = PlayerDisplayIdCodec.ToPublic(targetId).ToString()
        });
        var partial = partialDeckDetail.GetProperty("userBattleParameter");
        Assert.Equal(new[] { 3010005, 3010002, 3010007, 3010004 },
            Enumerable.Range(1, 4).Select(slot => partial.GetProperty($"discId{slot}").GetInt32()).ToArray());
        Assert.All(Enumerable.Range(1, 4), slot => Assert.True(partial.GetProperty($"discLevel{slot}").GetInt32() > 0));
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
        var payload = JsonSerializer.Serialize(new { hash = Encoding.ASCII.GetString(key), uuid = Guid.NewGuid().ToString("N") });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/index")
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(payload), Encoding.ASCII.GetBytes(CommonCode), new byte[16]))
        };
        request.Headers.Host = Host;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.DefaultRequestHeaders.Add("x-app-access-token", response.Headers.GetValues("x-app-access-token").Single());
        return new DemoSession(client, key, response.Headers.GetValues("x-app-user-id").Single());
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

    private sealed record DemoSession(HttpClient Client, byte[] Key, string UserId);

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
