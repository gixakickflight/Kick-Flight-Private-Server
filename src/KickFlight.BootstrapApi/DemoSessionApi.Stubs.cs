using System.Text.Json;
using KickFlight.BootstrapApi.Otomo;
using KickFlight.BootstrapApi.PlayerStore;

namespace KickFlight.BootstrapApi;

public sealed partial class DemoSessionApi
{
    // ResponseBattleRanking: a row of any ranking list. No ranking data is served, so this only exists to give
    // RankingResponseData.battleRanking (the caller's own entry) a non-null, fully populated object.
    private const string NeutralBattleRanking =
        """{"userId":"","name":"","honorId":0,"kickerId":0,"kickerCostumeId":0,"languageCode":"","followStatus":0,"rank":0,"battlePoint":0,"number":0,"percentile":0.0}""";

    // ResponseAppSeasonMatchResult: the season result banner. Neutral dates, no rule attached.
    private const string NeutralSeasonMatchResult =
        """{"battleRuleId":0,"resultDatetime":"","rewardReceiptStartDatetime":"","rewardReceiptEndDatetime":""}""";

    // ResponseUserDailyRandomMissionTask: mission/change hands one back, so it cannot be an empty list.
    private const string NeutralDailyRandomMissionTask =
        """{"missionTaskId":0,"number":0,"changeCount":0,"userMissionProgress":{"missionId":0,"loopCount":0,"value":0,"userMissionTaskStatusList":[]}}""";

    private static readonly string NeutralRankingList =
        $$"""{"battleRankingList":[],"battleRanking":{{NeutralBattleRanking}},"nextRewardRemainingBattlePoint":0,"appSeasonMatchResult":{{NeutralSeasonMatchResult}}}""";

    // Every field of every ResponseData class has to be present: the client deserializes the whole object
    // unconditionally and NullReferenceExceptions on an array that is missing, not merely empty. So the payloads
    // below are the DTO's full field list (../Kick-Flight-Assets/server_revival_analysis/il2cpp/dump.cs) filled with
    // neutral values - nothing here is real data.
    private async Task<IResult?> TryHandleStubAsync(string path, HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
    {
        switch (path)
        {
            // Client telemetry; AnalysisResponseData carries no fields. Recorded by DemoSessionApi.Analysis.cs,
            // which always returns the same empty success object and never lets a failure reach the client.
            case "/analysis/index":
                return await HandleAnalysisIndexAsync(context, state, key);

            case "/follow/index":
                return await HandleFollowIndexAsync(context, state, key);
            case "/follow/online":
                return await HandleFollowOnlineAsync(context, state, key);
            case "/follow/search":
                return await HandleFollowSearchAsync(context, state, key);
            case "/follow/add":
                return await HandleFollowMutationAsync(context, state, key, add: true);
            case "/follow/remove":
                return await HandleFollowMutationAsync(context, state, key, add: false);
            case "/follower/index":
                return await HandleFollowerIndexAsync(context, state, key);
            case "/follower/read":
                return await HandleFollowerReadAsync(context, state, key);
            case "/realFriend/token":
                return OkJson(context, key, JsonSerializer.Serialize(new { token = _playerStore.GetOrCreateRealFriendToken(state.PlayerId) }));
            case "/realFriend/apply":
                return await HandleRealFriendApplyAsync(context, state, key);

            case "/user/search":
                return await HandleUserSearchStubAsync(context, state, key);
            case "/user/detail":
                return await HandleUserDetailStubAsync(context, state, key);
            case "/user/change":
                return await HandleUserChangeStubAsync(context, state, key);
            case "/user/birthday":
                return OkJson(context, key, """{"dataUsageAgreementConfirmFlag":false}""");
            case "/user/displayUserId":
                return OkJson(context, key, $$"""{"displayUserId":{{CurrentDisplayUserId(state)}}}""");

            case "/ranking/index":
            case "/ranking/user":
            case "/ranking/follow":
            case "/ranking/region":
                return await HandleRankingAsync(context, state, key, path);

            case "/sns/index":
                return OkJson(context, key, """{"userProfileList":[]}""");
            case "/sns/disconnection":
                return OkJson(context, key, "{}");

            case "/present/index":
                return OkJson(context, key, """{"unlimitedUserPresentList":[],"limitedUserPresentList":[],"userPresentHistoryList":[]}""");
            case "/present/receipt":
                return OkJson(context, key,
                    """{"receivedUserCapsuleList":[],"receivedUserDiscList":[],"receivedUserHonorList":[],"receivedUserItemList":[],"unreceivedUserPresentList":[],"userPresentHistoryList":[],"userMissionProgressList":[]}""");

            case "/mission/index":
            case "/mission/read":
            case "/mission/tweet":
                return OkJson(context, key, """{"userMissionProgressList":[],"userDailyRandomMissionTaskList":[]}""");
            case "/mission/receipt":
                return OkJson(context, key,
                    """{"receivedUserCapsuleList":[],"receivedUserDiscList":[],"receivedUserHonorList":[],"receivedUserItemList":[],"receivedUserStampList":[],"userPresentList":[],"userMissionProgressList":[],"receivedRewardList":[]}""");
            case "/mission/change":
                return OkJson(context, key, $$"""{"userDailyRandomMissionTask":{{NeutralDailyRandomMissionTask}}}""");

            case "/battleReplay/checkMovie":
                return OkJson(context, key, "{}");

            case "/battleReplayNotification/read":
            case "/interruptNotification/read":
            case "/agreement/read":
            case "/agreement/dataUsage":
                return OkJson(context, key, "{}");

            case "/battleSummary/index":
                return OkJson(context, key, """{"userBattleSummaryList":[]}""");
            case "/battleSummary/season":
                return OkJson(context, key, """{"userBattleSummarySeasonList":[]}""");
            case "/battleSummary/festival":
                return OkJson(context, key, """{"festivalMatchResultList":[]}""");

            case "/chatRoomComment/write":
                return OkJson(context, key, "{}");

            case "/capsule/immediateOpen":
            {
                // Only this one repeats userItemList: the client refreshes the item counters from it without
                // re-fetching the startup payload. The counters are economy's, so this is one more read of them.
                var itemListJson = await BuildUserItemListJsonAsync(ticket, state, context);
                return OkJson(context, key,
                    $$"""{"dropDiscList":[],"receivedUserDiscList":[],"receivedUserItemList":[],"userItemList":{{itemListJson}},"capsuleEffectType":0,"userPresentList":[],"userMissionProgressList":[],"userDailyRandomMissionTaskList":[],"receivedRewardList":[],"premiumFlag":false}""");
            }
            case "/capsule/open":
                return OkJson(context, key,
                    """{"dropDiscList":[],"receivedUserDiscList":[],"receivedUserItemList":[],"capsuleEffectType":0,"userPresentList":[],"userMissionProgressList":[],"userDailyRandomMissionTaskList":[],"receivedRewardList":[],"premiumFlag":false}""");

            default:
                return null;
        }
    }

    // user/search is a display-id lookup. Only return accounts with persisted profiles; unknown ids are a normal
    // lookup miss instead of fabricated Player NNNN identities.
    private async Task<IResult?> HandleUserSearchStubAsync(HttpContext context, SessionState state, byte[] key)
    {
        var displayUserId = CurrentDisplayUserId(state);
        var body = await ReadBodyAsync(context.Request);
        try
        {
            if (body.Length > 0)
            {
                var plaintext = D2CCodec.Decode(body, key);
                using var document = JsonDocument.Parse(plaintext);
                if (document.RootElement.TryGetProperty("displayUserId", out var idProp))
                {
                    displayUserId = idProp.GetInt64();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode user search request: {Error}", ex.Message);
            return StatusError(context, key);
        }

        var record = FindProfileByDisplayOrStoredId(displayUserId);
        if (record is null) return StatusError(context, key);
        var profile = SocialProfileJson(state, record, FollowStatusFor(state.PlayerId, record.PlayerId), false);

        return OkJson(context, key, $$"""{"userProfile":{{profile}}}""");
    }

    private async Task<IResult?> HandleUserDetailStubAsync(HttpContext context, SessionState state, byte[] key)
    {
        var searchUserId = state.PlayerId;
        var body = await ReadBodyAsync(context.Request);
        try
        {
            if (body.Length > 0)
            {
                using var document = JsonDocument.Parse(D2CCodec.Decode(body, key));
                if (document.RootElement.TryGetProperty("searchUserId", out var idProp) &&
                    idProp.ValueKind == JsonValueKind.String && long.TryParse(idProp.GetString(), out var parsedId))
                    searchUserId = parsedId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode user detail request: {Error}", ex.Message);
            return StatusError(context, key);
        }

        var record = FindProfileByDisplayOrStoredId(searchUserId);
        if (record is null) return StatusError(context, key);
        var targetState = _playerStore.TryLoad(record.PlayerId);
        if (targetState is null) return StatusError(context, key);

        NormalizeCostume(targetState);
        if (!TryBuildUserBattleParameterJson(targetState, out var battleParameter))
            return StatusError(context, key);

        var targetProfile = record with
        {
            KickerId = targetState.KickerId,
            KickerCostumeId = targetState.KickerCostumeId
        };
        var profile = SocialProfileJson(state, targetProfile, FollowStatusFor(state.PlayerId, record.PlayerId), false);
        return OkJson(context, key,
            $$"""{"userProfile":{{profile}},"userBattleParameter":{{battleParameter}},"snsScreenName":""}""");
    }

    private async Task<IResult?> HandleUserChangeStubAsync(HttpContext context, SessionState state, byte[] key)
    {
        var body = await ReadBodyAsync(context.Request);
        try
        {
            var plaintext = D2CCodec.Decode(body, key);
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            // The DTO names the field "name"; older captures renamed it, so both spellings are accepted.
            if (TryGetString(root, "userName", out var newName) || TryGetString(root, "name", out newName))
            {
                state.UserName = newName;
                SaveUserState(state);
                _logger.LogInformation("Renamed user {UserId} to {UserName}", state.UserId, newName);
            }
            return OkJson(context, key, "{}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode user change request: {Error}", ex.Message);
            return StatusError(context, key);
        }
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        value = "";
        if (!root.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String) return false;
        var text = prop.GetString();
        if (string.IsNullOrWhiteSpace(text)) return false;
        value = text;
        return true;
    }

    // A name is only ever set by the player, through /tutorial/end. Until then the profile screens still have to
    // render something, so they get "Player" plus the low four digits of the id. It is display only: the stored
    // name stays empty, which is what keeps tutorialProgressStatus at 206 and the player on the name window.
    private static string CurrentUserName(SessionState state) =>
        state.HasName ? state.UserName : $"Player {DefaultNameSuffix(state.UserId)}";

    private static string DefaultNameSuffix(string userId) =>
        userId.Length <= 4 ? userId : userId[^4..];

    private static long CurrentDisplayUserId(SessionState state) =>
        PlayerDisplayIdCodec.ToPublic(long.TryParse(state.UserId, out var parsed) ? parsed : 1000001);

    private PlayerProfile? FindProfileByDisplayOrStoredId(long id) =>
        PlayerDisplayIdCodec.Find(id, _playerStore.FindProfile);

    // ResponseUserProfile, field for field. Frames and battle ranks mirror what BuildHomeJson already serves for the
    // same user (an empty userFrameList leaves the profile card without a frame to draw).
    private string BuildUserProfileJson(SessionState state, string userId, long displayUserId, string name) =>
        JsonSerializer.Serialize(new
        {
            userId,
            displayUserId = PlayerDisplayIdCodec.ToPublic(displayUserId),
            name,
            honorId = 6010000,
            userFrameList = new[]
            {
                new { battleRuleType = 1, frameId = 1 },
                new { battleRuleType = 2, frameId = 1 },
                new { battleRuleType = 3, frameId = 1 }
            },
            kickerId = state.KickerId,
            kickerCostumeId = state.KickerCostumeId,
            onlineFlag = true,
            battleFlag = false,
            officialFlag = false,
            languageCode = "es",
            followStatus = 0,
            newFlag = false,
            userBattleRankList = BuildBattleRankList(state),
            snsUserName = "",
            snsScreenName = "",
            snsUserImageUrl = ""
        });

    // The client resolves every disc id through DiscMasterData while building a friend card. Keep the detail
    // payload tied to the target's persisted active deck, with the same initial deck as SessionState when a slot
    // is absent or invalid. Rule 1 is the valid regular rule; SessionState does not persist a selected rule.
    private bool TryBuildUserBattleParameterJson(SessionState state, out string json)
    {
        json = "";
        if (_discIdList.Count < 4) return false;

        int[] initialDeck = [3010001, 3010002, 3010003, 3010004];
        var activeDeck = state.Decks.GetValueOrDefault(state.ActiveDeckNumber) ?? [];
        int DiscAt(int slot)
        {
            var current = slot < activeDeck.Count ? activeDeck[slot] : 0;
            if (_discIdList.Contains(current)) return current;

            var fallback = initialDeck[slot];
            if (_discIdList.Contains(fallback)) return fallback;
            return _discIdList[slot];
        }

        var discIds = Enumerable.Range(0, 4).Select(DiscAt).ToArray();
        // A disc's level is economy's, and this route describes *another* player, whose inventory this process has
        // no token for: a disc either player owns is level 10 (EconomyPayloads.OwnedDiscLevel).
        static int LevelOf(int discId) => EconomyPayloads.OwnedDiscLevel;

        json = JsonSerializer.Serialize(new
        {
            battleRuleId = 1,
            kickerId = state.KickerId,
            kickerCostumeId = state.KickerCostumeId,
            discId1 = discIds[0], discLevel1 = LevelOf(discIds[0]),
            discId2 = discIds[1], discLevel2 = LevelOf(discIds[1]),
            discId3 = discIds[2], discLevel3 = LevelOf(discIds[2]),
            discId4 = discIds[3], discLevel4 = LevelOf(discIds[3])
        });
        return true;
    }

    private static IResult OkJson(HttpContext context, byte[] key, string json)
    {
        context.Response.Headers["x-app-status-code"] = "0";
        return BinaryJson(json, key);
    }
}
