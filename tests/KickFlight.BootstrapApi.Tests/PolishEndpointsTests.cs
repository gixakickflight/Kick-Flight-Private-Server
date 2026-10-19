using System.Net;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using KickFlight.BootstrapApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenMatch;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The server writes one session JSON per user under <content root>/data/users. Pointing the test host at the
// (git-ignored) test output directory keeps every session this class creates out of the tracked src/ tree.
public sealed class PolishEndpointsFixture : IDisposable
{
    public PolishEndpointsFixture()
    {
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseContentRoot(AppContext.BaseDirectory));
        UsersDirectory = Path.Combine(Factory.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath, "data", "users");
    }

    public WebApplicationFactory<Program> Factory { get; }

    public string UsersDirectory { get; }

    public void Dispose() => Factory.Dispose();
}

// Covers the polish round: the gear inventory, the retail rank, the schema-complete stub endpoints and the
// webview HTML routes. Same auth -> access token -> encrypted POST flow as HarnessTests.
[Collection("Matchmaking static state")]
public sealed class PolishEndpointsTests : IClassFixture<PolishEndpointsFixture>
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _usersDirectory;

    public PolishEndpointsTests(PolishEndpointsFixture fixture)
    {
        _factory = fixture.Factory;
        _usersDirectory = fixture.UsersDirectory;
    }

    [Fact]
    public async Task Gear_create_set_and_destroy_round_trip_through_startup()
    {
        var gearIds = LoadGearIds();
        Assert.NotEmpty(gearIds);

        var session = await CreateSessionAsync();
        var startup = await PostAsync(session, "/startup/index", null);

        var kicker = startup.GetProperty("userKickerList")[0];
        var kickerId = kicker.GetProperty("kickerId").GetInt32();
        var costumeId = kicker.GetProperty("userKickerCostumeList")[0].GetProperty("kickerCostumeId").GetInt32();
        Assert.Equal(0, startup.GetProperty("userGear").GetProperty("gearId").GetInt32());

        // gear/create rolls a real row of the served Gear master
        var created = await PostAsync(session, "/gear/create", new { kickerId });
        var gearId = created.GetProperty("gearId").GetInt32();
        Assert.Contains(gearId, gearIds);

        // ... and the roll stays pending until it is equipped or discarded, in the payload and in the save file
        var pending = await PostAsync(session, "/startup/index", null);
        Assert.Equal(gearId, pending.GetProperty("userGear").GetProperty("gearId").GetInt32());
        Assert.Equal(kickerId, pending.GetProperty("userGear").GetProperty("kickerId").GetInt32());
        Assert.Equal(gearId, ReadPendingGearId(session));

        // an out-of-range slot is rejected and leaves the pending roll alone
        var rejected = await PostAsync(session, "/gear/set", new { kickerCostumeId = costumeId, gearIdNumber = 9 }, expectStatusZero: false);
        Assert.Equal(JsonValueKind.Object, rejected.ValueKind);
        Assert.Equal(gearId, (await PostAsync(session, "/startup/index", null)).GetProperty("userGear").GetProperty("gearId").GetInt32());

        await PostAsync(session, "/gear/set", new { kickerCostumeId = costumeId, gearIdNumber = 2 });

        var equipped = await PostAsync(session, "/startup/index", null);
        Assert.Equal(0, equipped.GetProperty("userGear").GetProperty("gearId").GetInt32());
        var slots = FindCostume(equipped, costumeId);
        Assert.Equal(0, slots.GetProperty("gearId1").GetInt32());
        Assert.Equal(gearId, slots.GetProperty("gearId2").GetInt32());
        Assert.Equal(0, slots.GetProperty("gearId3").GetInt32());
        Assert.Equal(gearId, ReadCostumeGearSlot(session, costumeId, 2));

        // a second roll that is discarded leaves the equipped slot untouched
        await PostAsync(session, "/gear/create", new { kickerId });
        await PostAsync(session, "/gear/destroy", null);

        var discarded = await PostAsync(session, "/startup/index", null);
        Assert.Equal(0, discarded.GetProperty("userGear").GetProperty("gearId").GetInt32());
        Assert.Equal(0, ReadPendingGearId(session));
        Assert.Equal(gearId, FindCostume(discarded, costumeId).GetProperty("gearId2").GetInt32());
    }

    [Theory]
    [InlineData(1, 0)] // regular and the app revision observed in the runtime request
    [InlineData(4, 27)] // ranked BattleRule row and a nonzero asset revision
    public async Task Team_endpoints_return_recruiting_data_and_ticket_streams_wait_for_host_entry(
        int battleRuleId, int appAssetRevision)
    {
        var host = await CreateSessionAsync();
        var guest = await CreateSessionAsync();
        host.Client.DefaultRequestHeaders.Add("x-app-asset-platform", "3");
        host.Client.DefaultRequestHeaders.Add("x-app-asset-revision", appAssetRevision.ToString(CultureInfo.InvariantCulture));
        var created = await PostAsync(host, "/battle/teamCreate", new
        {
            battleRuleId,
            code = "2563",
            userPingList = Array.Empty<object>()
        });
        var teamId = created.GetProperty("matchmakingTeamId").GetString()!;
        var hostTicket = created.GetProperty("battleEntryTicketId").GetString()!;
        Assert.False(string.IsNullOrEmpty(created.GetProperty("battleEntryId").GetString()));
        var asset = Assert.Single(created.GetProperty("assetList").EnumerateArray());
        Assert.Equal(3, asset.GetProperty("platform").GetInt32());
        Assert.Equal(appAssetRevision, asset.GetProperty("revision").GetInt32());
        Assert.Equal(JsonValueKind.Array, created.GetProperty("campaignList").ValueKind);

        var joined = await PostAsync(guest, "/battle/teamJoin", new
        {
            matchmakingTeamId = teamId,
            userPingList = Array.Empty<object>()
        });
        var guestTicket = joined.GetProperty("battleEntryTicketId").GetString()!;
        Assert.False(string.IsNullOrEmpty(joined.GetProperty("battleEntryId").GetString()));
        Assert.NotEqual(hostTicket, guestTicket);

        var recruiting = await PostAsync(host, "/battle/teamRecruiting", new
        {
            matchmakingTeamIdList = new[] { teamId }
        });
        var team = Assert.Single(recruiting.GetProperty("matchmakingTeamList").EnumerateArray());
        Assert.Equal(teamId, team.GetProperty("matchmakingTeamId").GetString());
        Assert.Equal(battleRuleId, team.GetProperty("battleRuleId").GetInt32());
        Assert.Equal("2563", team.GetProperty("code").GetString());
        Assert.Equal(2, team.GetProperty("kickerCostumeIdList").GetArrayLength());

        var matchmaking = _factory.Services.GetRequiredService<BattleMatchmakingService>();
        matchmaking.MatchWindow = TimeSpan.FromMilliseconds(150);
        matchmaking.JoinIncrementBaseSeconds = 0;
        using var hostCancellation = new CancellationTokenSource();
        using var guestCancellation = new CancellationTokenSource();
        var hostWriter = new TeamProbeWriter(hostCancellation);
        var guestWriter = new TeamProbeWriter(guestCancellation);
        var hostStream = matchmaking.StreamAssignmentsAsync(hostTicket, hostWriter, hostCancellation.Token);
        var guestStream = matchmaking.StreamAssignmentsAsync(guestTicket, guestWriter, guestCancellation.Token);

        await Task.Delay(150);
        Assert.False(hostWriter.FinalAssignment.IsCompleted);
        Assert.False(guestWriter.FinalAssignment.IsCompleted);

        var entered = await PostAsync(host, "/battle/teamEntry", new
        {
            matchmakingTeamId = teamId,
            userIdList = new[] { host.UserId, guest.UserId }
        });
        Assert.Equal(5, entered.GetProperty("matchmakingCancelWaitingTimeSecond").GetInt32());
        Assert.False(entered.GetProperty("scf").GetBoolean());

        var assignments = await Task.WhenAll(
            hostWriter.FinalAssignment.WaitAsync(TimeSpan.FromSeconds(8)),
            guestWriter.FinalAssignment.WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.NotEmpty(assignments[0].Assignment.Connection);
        Assert.Equal(assignments[0].Assignment.Connection, assignments[1].Assignment.Connection);
        var battleJson = assignments[0].Assignment.Properties.Fields["battle"].StructValue.ToString();
        using var battleDocument = JsonDocument.Parse(battleJson);
        var humanTeams = battleDocument.RootElement.GetProperty("battlePlayerList").EnumerateArray()
            .Where(player => player.GetProperty("kickerAiParameterId").GetInt32() == 0)
            .ToDictionary(player => player.GetProperty("userId").GetString()!,
                player => player.GetProperty("teamType").GetInt32(), StringComparer.Ordinal);
        Assert.Equal(humanTeams[host.UserId], humanTeams[guest.UserId]);

        // Retries of the exact teamEntry roster acknowledge the frozen start; a competing roster is rejected.
        var competingRoster = await PostAsync(host, "/battle/teamEntry", new
        {
            matchmakingTeamId = teamId,
            userIdList = new[] { host.UserId }
        }, expectStatusZero: false);
        Assert.Equal(JsonValueKind.Object, competingRoster.ValueKind);
        await PostAsync(host, "/battle/teamEntry", new
        {
            matchmakingTeamId = teamId,
            userIdList = new[] { host.UserId, guest.UserId }
        });

        hostCancellation.Cancel();
        guestCancellation.Cancel();
        await Task.WhenAll(
            Assert.ThrowsAnyAsync<OperationCanceledException>(() => hostStream),
            Assert.ThrowsAnyAsync<OperationCanceledException>(() => guestStream));
    }

    [Theory]
    [InlineData(1, new[] { 2, 2, 2 })]
    [InlineData(4, new[] { 3, 2 })]
    public async Task Encrypted_team_endpoints_preserve_party_groups_in_shared_match(int battleRuleId, int[] partySizes)
    {
        var parties = new List<(string TeamId, List<DemoSession> Members, List<string> Tickets)>();
        foreach (var size in partySizes)
        {
            var members = new List<DemoSession>();
            for (var index = 0; index < size; index++) members.Add(await CreateSessionAsync());
            var created = await PostAsync(members[0], "/battle/teamCreate", new
            {
                battleRuleId,
                code = "2563",
                userPingList = Array.Empty<object>()
            });
            var teamId = created.GetProperty("matchmakingTeamId").GetString()!;
            var tickets = new List<string> { created.GetProperty("battleEntryTicketId").GetString()! };
            foreach (var member in members.Skip(1))
            {
                var joined = await PostAsync(member, "/battle/teamJoin", new
                {
                    matchmakingTeamId = teamId,
                    userPingList = Array.Empty<object>()
                });
                tickets.Add(joined.GetProperty("battleEntryTicketId").GetString()!);
            }

            var recruiting = await PostAsync(members[0], "/battle/teamRecruiting", new
            {
                matchmakingTeamIdList = new[] { teamId }
            });
            var listed = Assert.Single(recruiting.GetProperty("matchmakingTeamList").EnumerateArray());
            Assert.Equal("2563", listed.GetProperty("code").GetString());
            Assert.Equal(size, listed.GetProperty("kickerCostumeIdList").GetArrayLength());
            parties.Add((teamId, members, tickets));
        }

        var matchmaking = _factory.Services.GetRequiredService<BattleMatchmakingService>();
        matchmaking.MatchWindow = TimeSpan.FromSeconds(2);
        matchmaking.JoinIncrementBaseSeconds = 0;
        var allMembers = parties.SelectMany(party => party.Members.Zip(party.Tickets,
            (member, ticket) => (Member: member, Ticket: ticket))).ToList();
        var cancellations = allMembers.Select(_ => new CancellationTokenSource()).ToList();
        var writers = cancellations.Select(cancellation => new TeamProbeWriter(cancellation)).ToList();
        var streams = new List<Task>();
        try
        {
            for (var index = 0; index < allMembers.Count; index++)
                streams.Add(matchmaking.StreamAssignmentsAsync(allMembers[index].Ticket, writers[index], cancellations[index].Token));

            foreach (var (teamId, members, _) in parties)
            {
                var entered = await PostAsync(members[0], "/battle/teamEntry", new
                {
                    matchmakingTeamId = teamId,
                    userIdList = members.Select(member => member.UserId).ToArray()
                });
                Assert.Equal(5, entered.GetProperty("matchmakingCancelWaitingTimeSecond").GetInt32());
                Assert.False(entered.GetProperty("scf").GetBoolean());
            }

            var finalAssignments = await Task.WhenAll(writers.Select(writer =>
                writer.FinalAssignment.WaitAsync(TimeSpan.FromSeconds(8))));
            var connection = finalAssignments[0].Assignment.Connection;
            Assert.NotEmpty(connection);
            Assert.All(finalAssignments, item => Assert.Equal(connection, item.Assignment.Connection));

            var battleJson = finalAssignments[0].Assignment.Properties.Fields["battle"].StructValue.ToString();
            using var battleDocument = JsonDocument.Parse(battleJson);
            var roster = battleDocument.RootElement.GetProperty("battlePlayerList").EnumerateArray().ToList();
            var expectedHumans = partySizes.Sum();
            Assert.Equal(8, roster.Count);
            Assert.Equal(expectedHumans, roster.Count(player => player.GetProperty("kickerAiParameterId").GetInt32() == 0));
            Assert.Equal(8 - expectedHumans, roster.Count(player => player.GetProperty("kickerAiParameterId").GetInt32() > 0));
            var humanTeams = roster.Where(player => player.GetProperty("kickerAiParameterId").GetInt32() == 0)
                .ToDictionary(player => player.GetProperty("userId").GetString()!,
                    player => player.GetProperty("teamType").GetInt32(), StringComparer.Ordinal);
            foreach (var (_, members, _) in parties)
            {
                Assert.Single(members.Select(member => humanTeams[member.UserId]).Distinct());
            }
        }
        finally
        {
            foreach (var cancellation in cancellations) cancellation.Cancel();
            try
            {
                await Task.WhenAll(streams.Select(async stream =>
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream)));
            }
            finally
            {
                foreach (var cancellation in cancellations) cancellation.Dispose();
            }
        }
    }

    [Fact]
    public async Task Startup_announces_configured_grpc_port_and_keeps_default()
    {
        var defaultSession = await CreateSessionAsync();
        var defaultStartup = await PostAsync(defaultSession, "/startup/index", null);
        Assert.Equal(18081, defaultStartup.GetProperty("matchmakingFrontend").GetProperty("port").GetInt32());

        using var configuredFactory = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["GrpcPort"] = "18083" })));
        var configuredSession = await CreateSessionAsync(configuredFactory);
        var configuredStartup = await PostAsync(configuredSession, "/startup/index", null);
        Assert.Equal(18083, configuredStartup.GetProperty("matchmakingFrontend").GetProperty("port").GetInt32());
    }

    [Fact]
    public async Task Battle_result_without_a_ranked_entry_does_not_change_the_rank()
    {
        var session = await CreateSessionAsync();

        var home = await PostAsync(session, "/home/index", null);
        var ranks = home.GetProperty("userBattleRankList").EnumerateArray().ToList();
        Assert.Equal(3, ranks.Count);
        foreach (var rank in ranks)
        {
            Assert.Equal(7, rank.GetProperty("rank").GetInt32());
            Assert.Equal(2900, rank.GetProperty("battlePoint").GetInt32());
        }

        var result = await PostAsync(session, "/battle/result", null);
        Assert.Equal(7, result.GetProperty("userBattleRank").GetProperty("rank").GetInt32());
        // No issued ranked entry means there is no authoritative ranked mode to apply.
        Assert.Equal(2900, result.GetProperty("userBattleRank").GetProperty("battlePoint").GetInt32());
        Assert.Equal(7, result.GetProperty("beforeUserBattleRank").GetProperty("rank").GetInt32());
        Assert.Equal(2900, result.GetProperty("beforeUserBattleRank").GetProperty("battlePoint").GetInt32());

        // The home profile continues to serve the unchanged persisted standing.
        var after = await PostAsync(session, "/home/index", null);
        Assert.Equal(2900, after.GetProperty("userBattleRankList")[0].GetProperty("battlePoint").GetInt32());
    }

    [Fact]
    public async Task Served_battle_rank_master_has_seventeen_retail_rows()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/demo-master/BattleRank");
        request.Headers.Host = Host;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var decrypted = D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), Encoding.ASCII.GetBytes(CommonCode));
        using var document = JsonDocument.Parse(decrypted);
        var rows = document.RootElement.EnumerateArray().ToList();
        Assert.Equal(17, rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            Assert.Equal(i, rows[i].GetProperty("rank").GetInt32());
        }
        Assert.Equal("S⁺9", rows[^1].GetProperty("name").GetString());

        // One row per league badge the client ships (ui/league/thumbnail_league_0 … _16). BattleRankMaster
        // .OnLoadComplete takes RegularEndRank as the first row with regularMatchFlag == 0 and BattleUtil
        // .GetDisplayRankPath caps rank to it with variant 1, so the flag has to flip exactly at rank 7 (S):
        // thumbnail_league_7_1 is the only variant-1 league badge in the catalog.
        var regularEnd = rows.First(row => !row.GetProperty("regularMatchFlag").GetBoolean());
        Assert.Equal(7, regularEnd.GetProperty("rank").GetInt32());
        Assert.All(rows.Where(row => row.GetProperty("rank").GetInt32() < 7),
            row => Assert.True(row.GetProperty("regularMatchFlag").GetBoolean()));
    }

    [Fact]
    public async Task Served_translation_formats_social_user_id_as_twelve_digits()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/demo-master/Translation");
        request.Headers.Host = Host;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var decrypted = D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), Encoding.ASCII.GetBytes(CommonCode));
        using var document = JsonDocument.Parse(decrypted);
        var row = Assert.Single(document.RootElement.EnumerateArray(), entry =>
            entry.GetProperty("key").GetString() == "common.userIdTitleFormat");
        var format = row.GetProperty("text").GetString();
        Assert.Equal("User ID: {0:D12}", format);
        Assert.Equal("User ID: 000001000028", string.Format(CultureInfo.InvariantCulture, format!, 1000028L));
    }

    // Every path of the stub table, each with a field its response must carry.
    [Theory]
    [InlineData("/analysis/index", null)]
    [InlineData("/follow/search", "followUserIdList")]
    [InlineData("/follower/index", "newFollowerCount")]
    [InlineData("/follower/read", null)]
    [InlineData("/user/search", "userProfile")]
    [InlineData("/user/detail", "userBattleParameter")]
    [InlineData("/user/change", null)]
    [InlineData("/user/birthday", "dataUsageAgreementConfirmFlag")]
    [InlineData("/user/displayUserId", "displayUserId")]
    [InlineData("/ranking/index", "battleRanking")]
    [InlineData("/ranking/user", "battleRankingList")]
    [InlineData("/ranking/follow", "nextRewardRemainingBattlePoint")]
    [InlineData("/ranking/region", "appSeasonMatchResult")]
    [InlineData("/sns/index", "userProfileList")]
    [InlineData("/sns/disconnection", null)]
    [InlineData("/present/index", "unlimitedUserPresentList")]
    [InlineData("/present/receipt", "unreceivedUserPresentList")]
    [InlineData("/mission/index", "userMissionProgressList")]
    [InlineData("/mission/read", "userDailyRandomMissionTaskList")]
    [InlineData("/mission/receipt", "receivedRewardList")]
    [InlineData("/mission/change", "userDailyRandomMissionTask")]
    [InlineData("/mission/tweet", "userMissionProgressList")]
    [InlineData("/battleReplay/index", "battleReplayChannelList")]
    [InlineData("/battleReplay/checkMovie", null)]
    [InlineData("/battleReplay/play", "battleReplayUrl")]
    [InlineData("/battleReplayNotification/read", null)]
    [InlineData("/interruptNotification/read", null)]
    [InlineData("/agreement/read", null)]
    [InlineData("/agreement/dataUsage", null)]
    [InlineData("/battleSummary/index", "userBattleSummaryList")]
    [InlineData("/battleSummary/season", "userBattleSummarySeasonList")]
    [InlineData("/battleSummary/festival", "festivalMatchResultList")]
    [InlineData("/chatRoomComment/write", null)]
    [InlineData("/capsule/immediateOpen", "userItemList")]
    [InlineData("/capsule/open", "capsuleEffectType")]
    public async Task Stub_endpoints_answer_status_zero_with_a_schema_complete_object(string path, string? expectedMember)
    {
        var session = await CreateSessionAsync();
        var body = await PostAsync(session, path, null);

        Assert.Equal(JsonValueKind.Object, body.ValueKind);
        if (expectedMember is not null)
        {
            Assert.True(body.TryGetProperty(expectedMember, out _), $"{path} is missing {expectedMember}");
        }
    }

    // The nested DTOs the dump declares, field for field: a missing one is a NullReferenceException while the
    // client deserializes, so the field names below are asserted verbatim.
    [Fact]
    public async Task Stub_nested_objects_carry_every_field_of_their_dto()
    {
        var session = await CreateSessionAsync();

        var ranking = await PostAsync(session, "/ranking/index", null);
        AssertFields(ranking.GetProperty("battleRanking"),
            "userId", "name", "honorId", "kickerId", "kickerCostumeId", "languageCode", "followStatus", "rank", "battlePoint", "number", "percentile");
        AssertFields(ranking.GetProperty("appSeasonMatchResult"),
            "battleRuleId", "resultDatetime", "rewardReceiptStartDatetime", "rewardReceiptEndDatetime");

        var mission = (await PostAsync(session, "/mission/change", null)).GetProperty("userDailyRandomMissionTask");
        AssertFields(mission, "missionTaskId", "number", "changeCount", "userMissionProgress");
        AssertFields(mission.GetProperty("userMissionProgress"), "missionId", "loopCount", "value", "userMissionTaskStatusList");

        var detail = await PostAsync(session, "/user/detail", null);
        AssertFields(detail.GetProperty("userBattleParameter"),
            "battleRuleId", "kickerId", "kickerCostumeId", "discId1", "discLevel1", "discId2", "discLevel2", "discId3", "discLevel3", "discId4", "discLevel4");
        AssertFields(detail.GetProperty("userProfile"),
            "userId", "displayUserId", "name", "honorId", "userFrameList", "kickerId", "kickerCostumeId", "onlineFlag", "battleFlag",
            "officialFlag", "languageCode", "followStatus", "newFlag", "userBattleRankList", "snsUserName", "snsScreenName", "snsUserImageUrl");
        AssertFields(detail.GetProperty("userProfile").GetProperty("userFrameList")[0], "battleRuleType", "frameId");
        AssertFields(detail.GetProperty("userProfile").GetProperty("userBattleRankList")[0], "battleRuleType", "battlePoint", "rank");
    }

    [Fact]
    public async Task User_search_returns_persisted_profiles_and_rejects_unknown_ids()
    {
        var session = await CreateSessionAsync();
        var ownId = long.Parse(session.UserId);
        var publicOwnId = PlayerDisplayIdCodec.ToPublic(ownId);

        var own = (await PostAsync(session, "/user/search", new { displayUserId = ownId })).GetProperty("userProfile");
        Assert.Equal(session.UserId, own.GetProperty("userId").GetString());
        Assert.Equal(publicOwnId, own.GetProperty("displayUserId").GetInt64());
        Assert.Equal("", own.GetProperty("name").GetString());
        Assert.Equal(6010000, own.GetProperty("honorId").GetInt32());
        Assert.Equal(7, own.GetProperty("userBattleRankList")[0].GetProperty("rank").GetInt32());

        var ownByPublicId = (await PostAsync(session, "/user/search", new { displayUserId = publicOwnId })).GetProperty("userProfile");
        Assert.Equal(session.UserId, ownByPublicId.GetProperty("userId").GetString());
        Assert.Equal(publicOwnId, ownByPublicId.GetProperty("displayUserId").GetInt64());
        var ownDetail = (await PostAsync(session, "/user/detail", new { searchUserId = publicOwnId.ToString(CultureInfo.InvariantCulture) }))
            .GetProperty("userProfile");
        Assert.Equal(session.UserId, ownDetail.GetProperty("userId").GetString());
        Assert.Equal(publicOwnId, ownDetail.GetProperty("displayUserId").GetInt64());
        var displayEndpoint = await PostAsync(session, "/user/displayUserId", null);
        Assert.Equal(publicOwnId, displayEndpoint.GetProperty("displayUserId").GetInt64());
        var home = await PostAsync(session, "/home/index", null);
        Assert.Equal(publicOwnId, home.GetProperty("userPlayer").GetProperty("displayUserId").GetInt64());

        var otherSession = await CreateSessionAsync();
        await PostAsync(otherSession, "/user/change", new { name = "Persisted Search Profile" });
        var otherId = long.Parse(otherSession.UserId);
        var other = (await PostAsync(session, "/user/search", new { displayUserId = otherId })).GetProperty("userProfile");
        Assert.Equal(otherSession.UserId, other.GetProperty("userId").GetString());
        Assert.Equal(PlayerDisplayIdCodec.ToPublic(otherId), other.GetProperty("displayUserId").GetInt64());
        Assert.Equal("Persisted Search Profile", other.GetProperty("name").GetString());
        await PostAsync(session, "/user/search", new { displayUserId = 9999999 }, expectStatusZero: false);
    }

    [Fact]
    public async Task User_change_renames_the_player_and_the_new_name_is_persisted()
    {
        var session = await CreateSessionAsync();

        await PostAsync(session, "/user/change", new { userName = "Tanuki", honorId = 6010000 });

        var search = (await PostAsync(session, "/user/search", new { displayUserId = long.Parse(session.UserId) })).GetProperty("userProfile");
        Assert.Equal("Tanuki", search.GetProperty("name").GetString());
        Assert.Equal("Tanuki", ReadUserState(session).GetProperty("UserName").GetString());

        var home = await PostAsync(session, "/home/index", null);
        Assert.Equal("Tanuki", home.GetProperty("userPlayer").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Capsule_open_repeats_the_startup_item_counters()
    {
        var session = await CreateSessionAsync();
        var items = (await PostAsync(session, "/startup/index", null)).GetProperty("userItemList");

        var open = await PostAsync(session, "/capsule/immediateOpen", new { slotNumber = 1 });
        Assert.Equal(4, open.GetProperty("userItemList").GetArrayLength());
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(items[i].GetProperty("itemId").GetInt32(), open.GetProperty("userItemList")[i].GetProperty("itemId").GetInt32());
            Assert.Equal(items[i].GetProperty("amount").GetInt32(), open.GetProperty("userItemList")[i].GetProperty("amount").GetInt32());
        }
    }

    [Theory]
    [InlineData("/webview/information/index", "Latest Information")]
    [InlineData("/webview/anything", "/webview/anything")]
    public async Task Webview_pages_return_html(string path, string expectedTitle)
    {
        using var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Contains(expectedTitle, await response.Content.ReadAsStringAsync());
    }

    private static void AssertFields(JsonElement element, params string[] expected)
    {
        var actual = element.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(expected.Length, actual.Count);
        foreach (var name in expected)
        {
            Assert.Contains(name, actual);
        }
    }

    private static JsonElement FindCostume(JsonElement startup, int costumeId)
    {
        foreach (var kicker in startup.GetProperty("userKickerList").EnumerateArray())
        {
            foreach (var costume in kicker.GetProperty("userKickerCostumeList").EnumerateArray())
            {
                if (costume.GetProperty("kickerCostumeId").GetInt32() == costumeId) return costume;
            }
        }
        throw new InvalidOperationException($"Costume {costumeId} is missing from the startup payload.");
    }

    private static HashSet<int> LoadGearIds()
    {
        var path = Path.Combine(RepositoryPaths.FindRoot(AppContext.BaseDirectory), "config", "masters_gear.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateArray().Select(row => row.GetProperty("id").GetInt32()).ToHashSet();
    }

    // A device that has never set a name is put on the name-entry window: TutorialUtil.IsTutorial is literally
    // "tutorialProgressStatus != 207", and GetTutorialInitialSubStep maps 206 to sub-step 16 (InputName).
    [Fact]
    public async Task Tutorial_end_sets_the_name_and_ends_tutorial_progress()
    {
        var session = await CreateSessionAsync();
        Assert.Equal(206, (await PostAsync(session, "/startup/index", null)).GetProperty("tutorialProgressStatus").GetInt32());

        var name = NewName();
        await PostAsync(session, "/tutorial/end", new { name });

        // The client re-issues /startup/index the moment the name is accepted, so that reply is what has to agree.
        Assert.Equal(207, (await PostAsync(session, "/startup/index", null)).GetProperty("tutorialProgressStatus").GetInt32());
        Assert.Equal(name, (await PostAsync(session, "/home/index", null)).GetProperty("userPlayer").GetProperty("name").GetString());
        Assert.Equal(name, ReadUserState(session).GetProperty("UserName").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345678901")] // 11 code units: one past what the client's own field allows to be typed
    public async Task Tutorial_end_rejects_a_name_the_rules_do_not_allow(string name)
    {
        var session = await CreateSessionAsync();

        using var response = await RawPostAsync(session, "/tutorial/end", new { name });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // 2005 is the one code the client maps to its name-rule popup; 2002/2003/2004 open the generic error
        // dialog instead, which would not tell the player what was wrong.
        Assert.Equal("2005", response.Headers.GetValues("x-app-status-code").Single());

        // The name stays unset, so the player is left on the name window rather than on a home with no name.
        Assert.Equal(206, (await PostAsync(session, "/startup/index", null)).GetProperty("tutorialProgressStatus").GetInt32());
        Assert.Equal("", ReadUserState(session).GetProperty("UserName").GetString());
    }

    [Fact]
    public async Task Tutorial_end_rejects_a_name_another_player_already_has()
    {
        var name = NewName();
        await PostAsync(await CreateSessionAsync(), "/tutorial/end", new { name });

        using var response = await RawPostAsync(await CreateSessionAsync(), "/tutorial/end", new { name });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("2005", response.Headers.GetValues("x-app-status-code").Single());

        // The client JsonUtility-parses error bodies as plain text (NetworkManager.GetResponseErrorFromResponse);
        // an encrypted body parsed to an empty code and sent the player to the generic network error unnamed.
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("2005", error.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_session_the_server_does_not_know_is_rejected_rather_than_borrowing_another_players()
    {
        var session = await CreateSessionAsync();

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-app-access-token", "not-a-token-this-server-issued");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/home/index")
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes("{}"), session.Key, new byte[16]))
        };
        request.Headers.Host = Host;
        using var response = await client.SendAsync(request);

        // Answering instead - which is what an unrecognised token used to do, with the most recently issued
        // session key - hands the caller another player's state and key. Rejecting is what makes the client
        // authenticate again.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("1", response.Headers.GetValues("x-app-status-code").Single());
    }

    [Fact]
    public async Task Battle_entry_returns_the_entry_id_and_cancel_always_answers_status_zero()
    {
        var session = await CreateSessionAsync();
        var entry = await PostAsync(session, "/battle/entry", new { battleRuleId = 1 });
        // NormalMatchingController.SendCancel does nothing while BattleEntryId is empty.
        var battleEntryId = entry.GetProperty("battleEntryId").GetString();
        Assert.False(string.IsNullOrEmpty(battleEntryId));
        Assert.False(string.IsNullOrEmpty(entry.GetProperty("battleEntryTicketId").GetString()));

        var cancelled = await PostAsync(session, "/battle/cancel", new { battleEntryId });
        Assert.Equal(JsonValueKind.Object, cancelled.ValueKind);
        Assert.Empty(cancelled.EnumerateObject());

        // The entry is gone, and a repeated or unreadable cancel still lets the client leave the matching screen.
        var matchmaking = _factory.Services.GetRequiredService<BattleMatchmakingService>();
        Assert.False(matchmaking.CancelEntry(battleEntryId!));
        await PostAsync(session, "/battle/cancel", new { battleEntryId });
        await PostAsync(session, "/battle/cancel", null);
    }

    // Names are unique, so a fixed one would collide with whatever a previous run left in the test output directory.
    private static string NewName() => "Kf" + Guid.NewGuid().ToString("N")[..6];

    // Reads the save file the server wrote for this session, so the assertions cover persistence and not just the
    // in-memory state the same request already returned.
    private JsonElement ReadUserState(DemoSession session)
    {
        var file = Path.Combine(_usersDirectory, $"{session.UserId}.json");
        Assert.True(File.Exists(file), $"user save file was not written: {file}");
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        return document.RootElement.Clone();
    }

    private int ReadPendingGearId(DemoSession session) =>
        ReadUserState(session).GetProperty("PendingGear").GetProperty("GearId").GetInt32();

    private int ReadCostumeGearSlot(DemoSession session, int costumeId, int slotNumber) =>
        ReadUserState(session).GetProperty("CostumeGears").GetProperty(costumeId.ToString())[slotNumber - 1].GetInt32();

    // A POST that is allowed to fail: the rejection codes (2005 for a name the client refuses to accept, 401 for a
    // session the server no longer knows) are exactly what these tests are about, so the status header is left to
    // the caller to inspect.
    private static async Task<HttpResponseMessage> RawPostAsync(DemoSession session, string path, object? body)
    {
        var json = body is null ? "{}" : JsonSerializer.Serialize(body);
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(json), session.Key, new byte[16]))
        };
        request.Headers.Host = Host;
        return await session.Client.SendAsync(request);
    }

    private async Task<DemoSession> CreateSessionAsync(WebApplicationFactory<Program>? factory = null)
    {
        var client = (factory ?? _factory).CreateClient();
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

        var decrypted = D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), session.Key);
        using var document = JsonDocument.Parse(decrypted);
        return document.RootElement.Clone();
    }

    private sealed record DemoSession(HttpClient Client, byte[] Key, string UserId);

    private sealed class TeamProbeWriter(CancellationTokenSource cancellation) : IServerStreamWriter<GetAssignmentsResponse>
    {
        private readonly TaskCompletionSource<GetAssignmentsResponse> _final =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WriteOptions? WriteOptions { get; set; }
        public Task<GetAssignmentsResponse> FinalAssignment => _final.Task;

        public Task WriteAsync(GetAssignmentsResponse message)
        {
            if (!string.IsNullOrEmpty(message.Assignment.Connection))
            {
                _final.TrySetResult(message);
                cancellation.Cancel();
            }
            return Task.CompletedTask;
        }
    }
}
