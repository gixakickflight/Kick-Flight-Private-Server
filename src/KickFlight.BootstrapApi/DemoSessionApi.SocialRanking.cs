using System.Collections.Concurrent;
using System.Text.Json;
using KickFlight.BootstrapApi.PlayerStore;

namespace KickFlight.BootstrapApi;

public sealed partial class DemoSessionApi
{
    private const int SocialPageSize = 20;
    private const int RankingPageSize = 100;
    private static readonly TimeSpan OnlineActivityWindow = TimeSpan.FromMinutes(11);
    private readonly ConcurrentDictionary<long, DateTimeOffset> _onlineActivityByPlayer = new();

    private async Task<IResult> HandleFollowIndexAsync(HttpContext context, SessionState state, byte[] key)
    {
        var request = await ReadRequestRootAsync(context, key);
        if (request is null) return StatusError(context, key);
        var root = request.Value;
        var page = ReadPage(root);
        var rows = _playerStore.ListFollowing(state.PlayerId, page * SocialPageSize, SocialPageSize);
        var profiles = rows.Select(row => JsonSerializer.Deserialize<JsonElement>(
            SocialProfileJson(state, row.Profile, FollowStatusFor(state.PlayerId, row.Profile.PlayerId), false)));
        return OkJson(context, key, JsonSerializer.Serialize(new
        {
            userProfileList = profiles,
            followCount = _playerStore.CountFollowing(state.PlayerId),
            newFollowerCount = _playerStore.CountNewFollowers(state.PlayerId)
        }));
    }

    private async Task<IResult> HandleFollowerIndexAsync(HttpContext context, SessionState state, byte[] key)
    {
        var request = await ReadRequestRootAsync(context, key);
        if (request is null) return StatusError(context, key);
        var root = request.Value;
        var page = ReadPage(root);
        var rows = _playerStore.ListFollowers(state.PlayerId, page * SocialPageSize, SocialPageSize);
        var profiles = rows.Select(row => JsonSerializer.Deserialize<JsonElement>(
            SocialProfileJson(state, row.Profile, FollowStatusFor(state.PlayerId, row.Profile.PlayerId), row.IsNew)));
        return OkJson(context, key, JsonSerializer.Serialize(new
        {
            userProfileList = profiles,
            followerCount = _playerStore.CountFollowers(state.PlayerId),
            newFollowerCount = _playerStore.CountNewFollowers(state.PlayerId)
        }));
    }

    private async Task<IResult> HandleFollowerReadAsync(HttpContext context, SessionState state, byte[] key)
    {
        var request = await ReadRequestRootAsync(context, key);
        if (request is null) return StatusError(context, key);
        var root = request.Value;
        var ids = ReadStringIds(root, "followerUserIdList");
        _playerStore.MarkFollowersRead(state.PlayerId, ids);
        return OkJson(context, key, "{}");
    }

    private async Task<IResult> HandleFollowSearchAsync(HttpContext context, SessionState state, byte[] key)
    {
        var request = await ReadRequestRootAsync(context, key);
        if (request is null) return StatusError(context, key);
        var root = request.Value;
        var candidates = ReadStringIds(root, "userIdList");
        var followed = _playerStore.FindFollowedIds(state.PlayerId, candidates);
        return OkJson(context, key, JsonSerializer.Serialize(new { followUserIdList = followed.Select(id => id.ToString()) }));
    }

    private async Task<IResult> HandleFollowMutationAsync(HttpContext context, SessionState state, byte[] key, bool add)
    {
        var request = await ReadRequestRootAsync(context, key);
        if (request is null ||
            !request.Value.TryGetProperty("followUserId", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String ||
            !long.TryParse(idElement.GetString(), out var targetId) || targetId <= 0 || targetId == state.PlayerId ||
            _playerStore.FindProfile(targetId) is null)
        {
            return StatusError(context, key);
        }

        if (add) _playerStore.AddFollow(state.PlayerId, targetId);
        else _playerStore.RemoveFollow(state.PlayerId, targetId);
        return OkJson(context, key, "{}");
    }

    private async Task<IResult> HandleFollowOnlineAsync(HttpContext context, SessionState state, byte[] key)
    {
        var rows = _playerStore.ListFollowing(state.PlayerId, 0, 100);
        var profiles = rows.Where(row => IsRecentlyActive(row.Profile.PlayerId))
            .Select(row => JsonSerializer.Deserialize<JsonElement>(
                SocialProfileJson(state, row.Profile, FollowStatusFor(state.PlayerId, row.Profile.PlayerId), false)));
        return OkJson(context, key, JsonSerializer.Serialize(new { userProfileList = profiles }));
    }

    private async Task<IResult> HandleRankingAsync(HttpContext context, SessionState state, byte[] key, string path)
    {
        var request = await ReadRequestRootAsync(context, key);
        if (request is null) return StatusError(context, key);
        // Crown ranking tabs show the currently rotating ranked mode. The request's battleRuleId is client
        // state and can be stale (or absent), so it must not select a different ladder than matchmaking.
        var battleRuleId = RankedModeRotation.RuleIdAt(_timeProvider.GetUtcNow());
        if (!_battleRuleTypeById.TryGetValue(battleRuleId, out var battleRuleType))
            battleRuleType = RegularBattleRuleType;

        IReadOnlyCollection<long>? filter = null;
        IReadOnlyList<RankedPlayer> ranked;
        switch (path)
        {
            case "/ranking/follow":
                filter = _playerStore.ListFollowing(state.PlayerId, 0, 100)
                    .Select(row => row.Profile.PlayerId).ToArray();
                ranked = filter.Count == 0 ? [] : _playerStore.ListRanks(battleRuleType, filter, 0, RankingPageSize);
                break;
            case "/ranking/region":
                // The local leaderboard needs a persisted region. Profiles currently carry no region field, so
                // this deployment shows the server population as the available fallback.
                ranked = _playerStore.ListRanks(battleRuleType, null, 0, RankingPageSize);
                break;
            case "/ranking/user":
                var ownStanding = _playerStore.ListRanks(battleRuleType, [state.PlayerId], 0, 1).FirstOrDefault();
                ranked = ownStanding is null
                    ? []
                    : _playerStore.ListRanks(battleRuleType, null, (int)Math.Max(0, ownStanding.Position - 6), 11);
                break;
            default:
                ranked = _playerStore.ListRanks(battleRuleType, null, 0, RankingPageSize);
                break;
        }

        var resultRows = ranked.Select(row => RankingProfile(state.PlayerId, row)).ToArray();
        var ownRank = _playerStore.LoadRank(state.PlayerId, battleRuleType);
        var ownProfile = _playerStore.FindProfile(state.PlayerId);
        var ownPosition = _playerStore.ListRanks(battleRuleType, [state.PlayerId], 0, 1).FirstOrDefault()?.Position ?? 0;
        var self = ownProfile is null
            ? NeutralBattleRanking
            : JsonSerializer.Serialize(new
            {
                userId = ownProfile.PlayerId.ToString(),
                name = ownProfile.DisplayName,
                honorId = BadgeAwardRules.GetOwnedHonorIds(state).Contains(state.HonorId)
                    ? state.HonorId
                    : BadgeAwardRules.DefaultHonorId,
                kickerId = ownProfile.KickerId,
                kickerCostumeId = ownProfile.KickerCostumeId,
                languageCode = "",
                followStatus = 0,
                rank = ownRank.Rank,
                battlePoint = ownRank.BattlePoint,
                number = (int)Math.Clamp(ownPosition, 0, int.MaxValue),
                percentile = 0.0
            });

        if (path == "/ranking/region")
        {
            return OkJson(context, key, $$"""{"battleRankingList":[{{string.Join(',', resultRows.Select(row => JsonSerializer.Serialize(row)))}}],"appSeasonMatchResult":{{NeutralSeasonMatchResult}}}""");
        }

        var nextReward = Math.Max(0, RankProgression.TotalBattlePointByRank
            .Where(points => points > ownRank.BattlePoint).DefaultIfEmpty(ownRank.BattlePoint).Min() - ownRank.BattlePoint);
        return OkJson(context, key,
            $$"""{"battleRankingList":[{{string.Join(',', resultRows.Select(row => JsonSerializer.Serialize(row)))}}],"battleRanking":{{self}},"nextRewardRemainingBattlePoint":{{nextReward}},"appSeasonMatchResult":{{NeutralSeasonMatchResult}}}""");
    }

    private object RankingProfile(long callerId, RankedPlayer row) => new
    {
        userId = row.Profile.PlayerId.ToString(),
        name = row.Profile.DisplayName,
        honorId = EquippedHonorId(_playerStore.TryLoad(row.Profile.PlayerId)),
        kickerId = row.Profile.KickerId,
        kickerCostumeId = row.Profile.KickerCostumeId,
        languageCode = "",
        followStatus = FollowStatusFor(callerId, row.Profile.PlayerId),
        rank = row.Rank,
        battlePoint = row.BattlePoint,
        number = (int)Math.Clamp(row.Position, 0, int.MaxValue),
        percentile = 0.0
    };

    private static int EquippedHonorId(SessionState? state) => state is not null
        && BadgeAwardRules.GetOwnedHonorIds(state).Contains(state.HonorId)
            ? state.HonorId
            : BadgeAwardRules.DefaultHonorId;

    private string SocialProfileJson(SessionState caller, PlayerProfile profile, int followStatus, bool isNew)
    {
        var targetState = _playerStore.TryLoad(profile.PlayerId) ?? caller;
        return JsonSerializer.Serialize(new
        {
            userId = profile.PlayerId.ToString(),
            displayUserId = PlayerDisplayIdCodec.ToPublic(profile.PlayerId),
            name = profile.DisplayName,
            honorId = EquippedHonorId(targetState),
            userFrameList = new[]
            {
                new { battleRuleType = 1, frameId = 1 },
                new { battleRuleType = 2, frameId = 1 },
                new { battleRuleType = 3, frameId = 1 }
            },
            kickerId = profile.KickerId,
            kickerCostumeId = profile.KickerCostumeId,
            onlineFlag = profile.PlayerId == caller.PlayerId || IsRecentlyActive(profile.PlayerId),
            battleFlag = false,
            officialFlag = false,
            languageCode = "",
            followStatus,
            newFlag = isNew,
            userBattleRankList = BuildBattleRankList(targetState),
            snsUserName = "",
            snsScreenName = "",
            snsUserImageUrl = ""
        });
    }

    private bool IsRecentlyActive(long playerId)
    {
        if (!_onlineActivityByPlayer.TryGetValue(playerId, out var lastActivity)) return false;
        var age = _timeProvider.GetUtcNow() - lastActivity;
        return age >= TimeSpan.Zero && age <= OnlineActivityWindow;
    }

    private int FollowStatusFor(long callerId, long targetId) =>
        callerId != targetId && _playerStore.AreRealFriends(callerId, targetId)
            ? 2
            : _playerStore.FindFollowedIds(callerId, [targetId]).Contains(targetId) ? 1 : 0;

    private async Task<IResult> HandleRealFriendApplyAsync(HttpContext context, SessionState state, byte[] key)
    {
        var request = await ReadRequestRootAsync(context, key);
        if (request is null ||
            !request.Value.TryGetProperty("token", out var tokenElement) ||
            tokenElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(tokenElement.GetString()))
        {
            return StatusError(context, key);
        }

        var owner = _playerStore.TryGetRealFriendTokenOwner(tokenElement.GetString()!);
        if (owner is null || owner == state.PlayerId) return StatusError(context, key);
        _playerStore.AddRealFriend(state.PlayerId, owner.Value);
        return OkJson(context, key, "{}");
    }

    private static int ReadPage(JsonElement root) =>
        root.TryGetProperty("page", out var pageElement) && pageElement.TryGetUInt32(out var page)
            ? (int)Math.Min(page, int.MaxValue / SocialPageSize) : 0;

    private static long[] ReadStringIds(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var ids) || ids.ValueKind != JsonValueKind.Array) return [];
        return ids.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out _))
            .Select(value => long.Parse(value.GetString()!))
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
    }

    private async Task<JsonElement?> ReadRequestRootAsync(HttpContext context, byte[] key)
    {
        var body = await ReadBodyAsync(context.Request);
        if (body.Length == 0)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
        try
        {
            using var document = JsonDocument.Parse(D2CCodec.Decode(body, key));
            return document.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode {Path} request: {Error}", context.Request.Path, ex.Message);
            return null;
        }
    }
}
