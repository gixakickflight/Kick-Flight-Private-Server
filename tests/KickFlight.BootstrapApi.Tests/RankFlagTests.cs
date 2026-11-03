using System.Net;
using System.Text;
using System.Text.Json;
using KickFlight.BootstrapApi;
using KickFlight.BootstrapApi.Otomo;
using KickFlight.BootstrapApi.PlayerStore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// Which source the client-facing rank payloads read, and the proof that Otomo:Rank is what chooses it.
//
// The two hosts below differ in one configuration value and nothing else; the store and the fake otomo hold
// deliberately different numbers for the same player, so a payload that matches proves which one answered it. The
// spy store is the other half: with the flag on, the read and the write must not be called at all, and with it off
// the same spy must see them, or it would not be able to tell the difference either.
public sealed class RankFlagTests
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";

    [Fact]
    public async Task With_the_flag_off_the_payload_reads_the_store()
    {
        await using var otomo = FakeOtomoRank.Start();
        otomo.Seed("unused", "ranked", 9_999);
        using var factory = NewFactory(otomo: null);
        var session = await CreateSessionAsync(factory);

        var store = factory.Services.GetRequiredService<IPlayerStore>();
        store.SaveRank(long.Parse(session.UserId), DemoSessionApi.RegularBattleRuleType, new RankState(3456, 11));

        var rows = (await PostAsync(session, "/home/index")).GetProperty("userBattleRankList").EnumerateArray().ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal(3456, rows[0].GetProperty("battlePoint").GetInt32());
        Assert.Equal(11, rows[0].GetProperty("rank").GetInt32());
        Assert.Empty(otomo.Calls);
    }

    [Fact]
    public async Task With_the_flag_on_the_payload_reads_otomo()
    {
        await using var otomo = FakeOtomoRank.Start();
        using var factory = NewFactory(otomo);
        var session = await CreateSessionAsync(factory);
        otomo.Seed(session.Account, "ranked", 200);

        // The store says something else entirely. A payload showing 3100 is otomo's 200 on the client's ladder;
        // showing 3456 would mean the store answered.
        var store = factory.Services.GetRequiredService<IPlayerStore>();
        store.SaveRank(long.Parse(session.UserId), DemoSessionApi.RegularBattleRuleType, new RankState(3456, 11));

        var rows = (await PostAsync(session, "/home/index")).GetProperty("userBattleRankList").EnumerateArray().ToList();

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(RankProgression.StartBattlePoint + 200, row.GetProperty("battlePoint").GetInt32());
            Assert.Equal(RankProgression.RankFor(RankProgression.StartBattlePoint + 200), row.GetProperty("rank").GetInt32());
        });
        var call = Assert.Single(otomo.Calls);
        Assert.Equal("/api/player/session/rank", call.Path);
        Assert.StartsWith("Bearer ", call.Authorization);
    }

    [Fact]
    public async Task A_player_otomo_has_never_awarded_reads_the_starting_standing()
    {
        await using var otomo = FakeOtomoRank.Start();
        using var factory = NewFactory(otomo);
        var session = await CreateSessionAsync(factory);

        var rows = (await PostAsync(session, "/home/index")).GetProperty("userBattleRankList").EnumerateArray().ToList();

        // No seed: otomo answers 200 with zero points, which the client sees as a new player, not as a failure or a
        // row that is missing from the list.
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(RankProgression.StartBattlePoint, row.GetProperty("battlePoint").GetInt32());
            Assert.Equal(RankProgression.StartRank, row.GetProperty("rank").GetInt32());
        });
    }

    [Fact]
    public async Task Both_sources_send_the_same_rank_shape()
    {
        await using var otomo = FakeOtomoRank.Start();

        using var off = NewFactory(otomo: null);
        var offSession = await CreateSessionAsync(off);
        off.Services.GetRequiredService<IPlayerStore>()
            .SaveRank(long.Parse(offSession.UserId), DemoSessionApi.RegularBattleRuleType, new RankState(2900, 7));
        var offRows = (await PostAsync(offSession, "/home/index")).GetProperty("userBattleRankList");

        using var on = NewFactory(otomo);
        var onSession = await CreateSessionAsync(on);
        var onRows = (await PostAsync(onSession, "/home/index")).GetProperty("userBattleRankList");

        // The exact shape, pinned: three rows of the same three numbers the client has always parsed. The values are
        // what the source changes; this is what it must not.
        string[] expected = ["battleRuleType:Number", "battlePoint:Number", "rank:Number"];
        Assert.Equal(expected, Shape(offRows).Take(expected.Length));
        Assert.Equal(3, offRows.GetArrayLength());
        Assert.Equal(Shape(offRows), Shape(onRows));
    }

    [Fact]
    public async Task With_the_flag_on_the_payload_never_reads_or_writes_the_stores_rank()
    {
        await using var otomo = FakeOtomoRank.Start();
        using var factory = NewFactory(otomo, spy: true);
        var session = await CreateSessionAsync(factory);
        otomo.Seed(session.Account, "ranked", 50);

        var spy = Assert.IsType<SpyPlayerStore>(factory.Services.GetRequiredService<IPlayerStore>());

        // Home is the rank card, the ranking screen is the leaderboard: neither may take the player's own standing
        // from the table otomo replaced.
        await PostAsync(session, "/home/index");
        await PostAsync(session, "/ranking/index", new { battleRuleId = 1 });

        Assert.Equal(0, spy.RankReads);
        Assert.Equal(0, spy.RankWrites);
        Assert.NotEmpty(otomo.Calls);
    }

    [Fact]
    public async Task With_the_flag_off_the_same_spy_sees_the_store_read()
    {
        using var factory = NewFactory(otomo: null, spy: true);
        var session = await CreateSessionAsync(factory);
        var spy = Assert.IsType<SpyPlayerStore>(factory.Services.GetRequiredService<IPlayerStore>());

        await PostAsync(session, "/home/index");

        Assert.Equal(3, spy.RankReads);
        Assert.Equal(0, spy.RankWrites);
    }

    // The property names and JSON kinds of the three rows, in order. The values are what the flag changes; this is
    // what it must not.
    private static string[] Shape(JsonElement rows) =>
        rows.EnumerateArray()
            .SelectMany(row => row.EnumerateObject().Select(property => $"{property.Name}:{property.Value.ValueKind}"))
            .ToArray();

    private static WebApplicationFactory<Program> NewFactory(FakeOtomoRank? otomo = null, bool spy = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
            if (otomo is not null)
            {
                builder.UseSetting("Otomo:Rank:Enabled", "true");
                builder.UseSetting("Otomo:Rank:BaseUrl", otomo.BaseUrl);
            }
            if (spy)
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IPlayerStore>();
                    services.AddSingleton<IPlayerStore>(provider => new SpyPlayerStore(
                        ActivatorUtilities.CreateInstance<JsonPlayerStore>(provider)));
                });
            }
        });

    private static async Task<DemoSession> CreateSessionAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
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
        using var document = JsonDocument.Parse(D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), session.Key));
        return document.RootElement.Clone();
    }

    private sealed record DemoSession(HttpClient Client, byte[] Key, string UserId, string Account);

    // The real store behind a counter. Everything but the two rank methods is the store's own answer, so a test can
    // prove a request was served without the adapter taking a player's rank from it.
    private sealed class SpyPlayerStore(IPlayerStore inner) : IPlayerStore
    {
        public int RankReads { get; private set; }
        public int RankWrites { get; private set; }

        public RankState LoadRank(long playerId, int battleRuleType)
        {
            RankReads++;
            return inner.LoadRank(playerId, battleRuleType);
        }

        public void SaveRank(long playerId, int battleRuleType, RankState rank)
        {
            RankWrites++;
            inner.SaveRank(playerId, battleRuleType, rank);
        }

        public long ResolveGamePlayer(string otomoPlayerId, string legacyDeviceUuid) =>
            inner.ResolveGamePlayer(otomoPlayerId, legacyDeviceUuid);

        public SessionState? TryLoad(long playerId) => inner.TryLoad(playerId);
        public void Save(SessionState state) => inner.Save(state);
        public bool IsNameTaken(string name, long exceptPlayerId) => inner.IsNameTaken(name, exceptPlayerId);
        public PlayerProfile? FindProfile(long playerId) => inner.FindProfile(playerId);
        public IReadOnlyList<PlayerProfile> FindProfiles(IReadOnlyCollection<long> playerIds) => inner.FindProfiles(playerIds);
        public IReadOnlyList<long> FindFollowedIds(long followerId, IReadOnlyCollection<long> candidateIds) =>
            inner.FindFollowedIds(followerId, candidateIds);
        public void AddFollow(long followerId, long followedId) => inner.AddFollow(followerId, followedId);
        public void RemoveFollow(long followerId, long followedId) => inner.RemoveFollow(followerId, followedId);
        public int CountFollowing(long playerId) => inner.CountFollowing(playerId);
        public int CountFollowers(long playerId) => inner.CountFollowers(playerId);
        public IReadOnlyList<FollowedProfile> ListFollowing(long playerId, int offset, int limit) =>
            inner.ListFollowing(playerId, offset, limit);
        public IReadOnlyList<FollowedProfile> ListFollowers(long playerId, int offset, int limit) =>
            inner.ListFollowers(playerId, offset, limit);
        public int CountNewFollowers(long playerId) => inner.CountNewFollowers(playerId);
        public void MarkFollowersRead(long playerId, IReadOnlyCollection<long> followerIds) =>
            inner.MarkFollowersRead(playerId, followerIds);
        public string GetOrCreateRealFriendToken(long playerId) => inner.GetOrCreateRealFriendToken(playerId);
        public long? TryGetRealFriendTokenOwner(string token) => inner.TryGetRealFriendTokenOwner(token);
        public void AddRealFriend(long firstPlayerId, long secondPlayerId) => inner.AddRealFriend(firstPlayerId, secondPlayerId);
        public bool AreRealFriends(long firstPlayerId, long secondPlayerId) => inner.AreRealFriends(firstPlayerId, secondPlayerId);
        public IReadOnlyList<RankedPlayer> ListRanks(int battleRuleType, IReadOnlyCollection<long>? playerIds, int offset, int limit) =>
            inner.ListRanks(battleRuleType, playerIds, offset, limit);
    }
}
