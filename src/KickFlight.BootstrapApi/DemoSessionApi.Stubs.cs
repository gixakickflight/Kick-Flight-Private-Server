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
                return await HandleFollowIndexAsync(context, ticket, state, key);
            case "/follow/online":
                return await HandleFollowOnlineAsync(context, ticket, state, key);
            case "/follow/search":
                return await HandleFollowSearchAsync(context, ticket, state, key);
            case "/follow/add":
                return await HandleFollowMutationAsync(context, ticket, state, key, add: true);
            case "/follow/remove":
                return await HandleFollowMutationAsync(context, ticket, state, key, add: false);
            case "/follower/index":
                return await HandleFollowerIndexAsync(context, ticket, state, key);
            case "/follower/read":
                return await HandleFollowerReadAsync(context, state, key);
            // A real friend is a session friendship, made by a friend invitation: the token is the player's own invite code
            // and applying someone's token redeems it (DemoSessionApi.SocialRanking.cs).
            case "/realFriend/token":
                return await HandleRealFriendTokenAsync(context, ticket, state, key);
            case "/realFriend/apply":
                return await HandleRealFriendApplyAsync(context, ticket, state, key);

            case "/user/search":
                return await HandleUserSearchStubAsync(context, ticket, state, key);
            case "/user/detail":
                return await HandleUserDetailStubAsync(context, ticket, state, key);
            case "/user/change":
                return await HandleUserChangeStubAsync(context, ticket, state, key);
            case "/user/birthday":
                return OkJson(context, key, """{"dataUsageAgreementConfirmFlag":false}""");
            case "/user/displayUserId":
                return OkJson(context, key, $$"""{"displayUserId":{{CurrentDisplayUserId(state)}}}""");

            case "/ranking/index":
            case "/ranking/user":
            case "/ranking/follow":
            case "/ranking/region":
                return await HandleRankingAsync(context, ticket, state, key, path);

            case "/sns/index":
                return OkJson(context, key, """{"userProfileList":[]}""");
            case "/sns/disconnection":
                return OkJson(context, key, "{}");

            case "/present/index":
                return OkJson(context, key, """{"unlimitedUserPresentList":[],"limitedUserPresentList":[],"userPresentHistoryList":[]}""");
            case "/present/receipt":
                return OkJson(context, key,
                    """{"receivedUserCapsuleList":[],"receivedUserDiscList":[],"receivedUserHonorList":[],"receivedUserItemList":[],"unreceivedUserPresentList":[],"userPresentHistoryList":[],"userMissionProgressList":[]}""");

            // /mission/index, read, tweet and receipt are economy's tracks (DemoSessionApi.Missions.cs). The daily
            // random missions are not ported, so change hands back the neutral task the DTO requires.
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
                return await HandleChatWriteAsync(context, ticket, state, key);

            default:
                return null;
        }
    }

    // user/search is a display-id lookup. Only return accounts with persisted profiles; unknown ids are a normal
    // lookup miss instead of fabricated Player NNNN identities.
    private async Task<IResult?> HandleUserSearchStubAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
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

        var local = FindProfileByDisplayOrStoredId(displayUserId);
        if (local is null) return StatusError(context, key);
        var (record, _, cardFailure) = await ReadTargetCardAsync(context, key, "/user/search", local);
        if (record is null) return cardFailure;
        var marks = await ReadFollowMarksAsync(ticket, "/user/search", context.RequestAborted);
        var (cardRanks, cardError) = await TryReadCardRanksAsync(ticket, state, [record.PlayerId], context.RequestAborted);
        if (cardRanks is null) return SocialUnavailable(context, "/user/search", cardError);
        var profile = SocialProfileJson(state, record, marks.StatusOf(state.PlayerId, record.PlayerId), false,
            cardRanks[record.PlayerId], await HonorsOfAsync(ticket, state, [record.PlayerId], context.RequestAborted));

        return OkJson(context, key, $$"""{"userProfile":{{profile}}}""");
    }

    private async Task<IResult?> HandleUserDetailStubAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
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

        var local = FindProfileByDisplayOrStoredId(searchUserId);
        if (local is null) return StatusError(context, key);
        var targetState = _playerStore.TryLoad(local.PlayerId);
        if (targetState is null) return StatusError(context, key);
        // The name and the decks are session's answer for that player (its card and its loadout); the kicker, costume and honor are the game rows'.
        var (record, _, cardFailure) = await ReadTargetCardAsync(context, key, "/user/detail", local);
        if (record is null) return cardFailure;
        var (targetLoadout, loadoutError) = await TryReadPlayerLoadoutAsync(
            _playerStore.FindOtomoPlayer(local.PlayerId)!.OtomoPlayerId, context.RequestAborted);
        if (targetLoadout is null)
        {
            if (loadoutError is OtomoRankException { Failure: OtomoRankFailure.Refused, Code: "player_not_found" })
                return StatusError(context, key);
            return LoadoutUnavailable(context, "/user/detail", loadoutError);
        }

        NormalizeCostume(targetState);
        if (!TryBuildUserBattleParameterJson(targetState, targetLoadout, out var battleParameter))
            return StatusError(context, key);

        var targetProfile = record with
        {
            KickerId = targetState.KickerId,
            KickerCostumeId = targetState.KickerCostumeId
        };
        var marks = await ReadFollowMarksAsync(ticket, "/user/detail", context.RequestAborted);
        var (cardRanks, cardError) = await TryReadCardRanksAsync(ticket, state, [record.PlayerId], context.RequestAborted);
        if (cardRanks is null) return SocialUnavailable(context, "/user/detail", cardError);
        var profile = SocialProfileJson(state, targetProfile, marks.StatusOf(state.PlayerId, record.PlayerId), false,
            cardRanks[record.PlayerId], await HonorsOfAsync(ticket, state, [record.PlayerId], context.RequestAborted));
        return OkJson(context, key,
            $$"""{"userProfile":{{profile}},"userBattleParameter":{{battleParameter}},"snsScreenName":""}""");
    }

    private async Task<IResult?> HandleUserChangeStubAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
    {
        var body = await ReadBodyAsync(context.Request);
        try
        {
            var plaintext = D2CCodec.Decode(body, key);
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            if (root.TryGetProperty("honorId", out var honorProp))
            {
                if (!honorProp.TryGetInt32(out var honorId)
                    || !await EquipHonorAsync(ticket, honorId, context.RequestAborted))
                    return StatusError(context, key);
            }
            // The DTO names the field "name"; older captures renamed it, so both spellings are accepted.
            if (TryGetString(root, "userName", out var newName) || TryGetString(root, "name", out newName))
            {
                if (await RenameAtSessionAsync(context, key, ticket, state, newName, "/user/change") is { } refused)
                    return refused;
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
    // name stays empty, which is what keeps tutorialProgressStatus below 207 and the player in the tutorial.
    private static string CurrentUserName(SessionState state) =>
        state.HasName ? state.UserName : $"Player {DefaultNameSuffix(state.UserId)}";

    private static string DefaultNameSuffix(string userId) =>
        userId.Length <= 4 ? userId : userId[^4..];

    private static long CurrentDisplayUserId(SessionState state) =>
        PlayerDisplayIdCodec.ToPublic(long.TryParse(state.UserId, out var parsed) ? parsed : 1000001);

    // The game row a numeric id (a search box, a profile tap) names. It supplies the id mapping and the kicker and costume
    // the adapter keeps; the name on the card is session's (ReadTargetCardAsync), never this row's DisplayName.
    private PlayerProfile? FindProfileByDisplayOrStoredId(long id) =>
        PlayerDisplayIdCodec.Find(id, _playerStore.FindProfile);

    // Another player's card from session: the game row with the name session shows for that player, and the card itself. A row with no otomo account was never signed in through otomo and has no card, so it is
    // a lookup miss; a player session does not know is one too; session not answering is the 503 contract.
    private async Task<(PlayerProfile? Profile, SessionPlayerProfile? Card, IResult? Failure)> ReadTargetCardAsync(
        HttpContext context, byte[] key, string path, PlayerProfile local)
    {
        var link = _playerStore.FindOtomoPlayer(local.PlayerId);
        if (link is null)
        {
            _logger.LogInformation("{Path}: game row {PlayerId} has no otomo account, so there is no card to show", path,
                local.PlayerId);
            return (null, null, StatusError(context, key));
        }

        var (card, error) = await TryReadCardAsync(link.OtomoPlayerId, context.RequestAborted);
        if (card is null)
        {
            if (error is OtomoRankException { Failure: OtomoRankFailure.Refused, Code: "player_not_found" })
            {
                _logger.LogInformation("{Path}: session has no card for otomo player {OtomoPlayerId}", path, link.OtomoPlayerId);
                return (null, null, StatusError(context, key));
            }
            return (null, null, LoadoutUnavailable(context, path, error));
        }
        return (local with { DisplayName = card.DisplayName ?? "" }, card, null);
    }

    // The client resolves every disc id through DiscMasterData while building a friend card. Keep the detail
    // payload tied to the target's active deck as session holds it, with the initial deck where a slot is absent or
    // invalid. Rule 1 is the valid regular rule; no selected rule is stored.
    private bool TryBuildUserBattleParameterJson(SessionState state, Loadout loadout, out string json)
    {
        json = "";
        if (_discIdList.Count < 4) return false;

        int[] initialDeck = [3010001, 3010002, 3010003, 3010004];
        var activeDeck = loadout.DeckSets.GetValueOrDefault(loadout.ActiveDeck) ?? [];
        int DiscAt(int slot)
        {
            var current = slot < activeDeck.Count ? activeDeck[slot] : 0;
            if (_discIdList.Contains(current)) return current;

            var fallback = initialDeck[slot];
            if (_discIdList.Contains(fallback)) return fallback;
            return _discIdList[slot];
        }

        var discIds = Enumerable.Range(0, 4).Select(DiscAt).ToArray();
        // A disc's level is attrs.level on that player's own possession, and economy's player plane is read on the
        // caller's token: there is no cross-player inventory read, and the staff route needs a credential this adapter
        // does not hold. So a friend card shows every disc at the level its rarity starts at, which is what a disc
        // nobody has raised fights at. It is a deliberate loss, recorded in docs/OTOMO_SOCIAL_DISCS.md; the card's
        // kicker, costume and deck are still that player's own.
        int LevelOf(int discId) => _tuning.StartLevel(_discRarity.GetValueOrDefault(discId));

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
