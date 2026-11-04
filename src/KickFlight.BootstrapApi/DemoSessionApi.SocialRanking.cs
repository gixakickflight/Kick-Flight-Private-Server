using System.Collections.Concurrent;
using System.Text.Json;
using KickFlight.BootstrapApi.Otomo;
using KickFlight.BootstrapApi.PlayerStore;

namespace KickFlight.BootstrapApi;

// The social screens and the ranking board.
//
// None of this relation is kept here any more. otomo's session service owns it - a follow is a row there, the board
// is a read of its rank store - and this file only translates: the client speaks a numeric player id and a page
// number, session speaks an account id and a rule-type name. Every payload below keeps the field names the client
// has always deserialized.
//
// Three consequences of that move are deliberate and visible in the code:
//
//   * A follow is written by otomo player id: the numeric id the client sends is translated through the one mapping
//     this adapter keeps (IPlayerStore.FindOtomoPlayer), and session does the rest. No name or tag is stored here.
//   * Session's follow relation has no read cursor, so a follower is never "new" - newFlag is false and
//     newFollowerCount is 0, and /follower/read answers an empty success. The unread mark lived in player_follows and
//     went with that table (migration 6).
//   * A name on a social or ranking row is the display_name session answered for that row, because a name now lives
//     there. A row whose account has no
//     game row here is skipped: the client parses userId as a number, and there is no number to give it for someone
//     who has never played on this server.
public sealed partial class DemoSessionApi
{
    private const int SocialPageSize = 20;
    private const int RankingPageSize = 100;
    private const int DefaultKickerId = 1;
    // Kicker 1's first costume: what the client draws for a player this adapter holds no kicker for.
    private const int DefaultKickerCostumeId = 2010101;
    private static readonly TimeSpan OnlineActivityWindow = TimeSpan.FromMinutes(11);
    private readonly ConcurrentDictionary<long, DateTimeOffset> _onlineActivityByPlayer = new();

    // The caller's follow relation as session answered it: the accounts they follow, translated into the rows the
    // client speaks, and the two counts. A null relation is a failure the caller answers 503.
    private sealed record FollowRelation(IReadOnlyList<SocialRow> Following, SessionFollowCounts Counts);

    // One player on a social screen: the id mapping the adapter keeps, and the name session answered for this row.
    private sealed record SocialRow(long PlayerId, string OtomoPlayerId, string? DisplayName);

    private sealed record FollowRelationLoad(FollowRelation? Relation, Exception? Error);

    // The rows of one board read and the caller's own line. Each row carries session's `position`, which is what the
    // client renders as `number`.
    private sealed record BoardRead(IReadOnlyList<SessionBoardRow> Rows, SessionBoardStanding? You);

    // One round of session reads for the follow screens. The token is the one OtomoIdentity already holds for this
    // device, so a screen costs no second sign-in.
    private async Task<FollowRelationLoad> ReadFollowRelationAsync(KfIdentityTicket ticket, string path,
        CancellationToken cancellationToken)
    {
        if (!_social.Enabled)
        {
            return new FollowRelationLoad(null, new OtomoSocialException(OtomoSocialFailure.Disabled,
                "Otomo:Social:Enabled is false: this server holds no follow relation"));
        }

        try
        {
            var accessToken = await PlayerTokenAsync(ticket, cancellationToken);
            var following = await _social.GetFollowingAsync(accessToken, cancellationToken);
            var counts = await _social.GetFollowCountsAsync(accessToken, cancellationToken);
            return new FollowRelationLoad(new FollowRelation(Translate(following), counts), null);
        }
        catch (Exception exception) when (exception is OtomoIdentityException or OtomoSocialException)
        {
            _logger.LogWarning("Follow relation for {Path} could not be read: {Error}", path, exception.Message);
            return new FollowRelationLoad(null, exception);
        }
    }

    // The game rows the caller follows, for a screen whose follow marks are decoration (a profile card's button). A
    // failure here is logged and reads as "follows nobody": the card is still worth drawing, and a 503 on a search box
    // because the follow service blinked would be the wrong failure.
    private async Task<HashSet<long>> ReadFollowedIdsAsync(KfIdentityTicket ticket, string path,
        CancellationToken cancellationToken)
    {
        var loaded = await ReadFollowRelationAsync(ticket, path, cancellationToken);
        if (loaded.Relation is not null) return loaded.Relation.Following.Select(link => link.PlayerId).ToHashSet();
        _logger.LogInformation("{Path}: follow marks read as empty, the relation could not be read", path);
        return [];
    }

    // A session id is otomo's account id; the client speaks this adapter's numeric row. An account with no row here is
    // dropped rather than invented - it has never played on this server, so it has no name or kicker to draw.
    private IReadOnlyList<SocialRow> Translate(IReadOnlyList<SessionFollow> follows)
    {
        if (follows.Count == 0) return [];
        var links = _playerStore.FindOtomoPlayers(follows.Select(follow => follow.PlayerId).ToArray())
            .ToDictionary(link => link.OtomoPlayerId, StringComparer.Ordinal);
        var players = new List<SocialRow>(follows.Count);
        foreach (var follow in follows)
        {
            if (!links.TryGetValue(follow.PlayerId, out var link))
            {
                _logger.LogInformation(
                    "Follow edge with otomo account {PlayerId} is not served: this adapter has no game row for it",
                    follow.PlayerId);
                continue;
            }
            players.Add(new SocialRow(link.PlayerId, link.OtomoPlayerId, follow.DisplayName));
        }
        return players;
    }

    // A read that needs the relation (or a standing, or the ladder) and cannot get one: the game's error contract, 503,
    // never an empty list and never zero points. The battle result answers its unrankable award the same way.
    private IResult SocialUnavailable(HttpContext context, string path, Exception? failure)
    {
        var reason = failure switch
        {
            OtomoIdentityException identity => identity.Failure.ToString(),
            OtomoSocialException social => social.Failure.ToString(),
            OtomoRankException rank => rank.Failure.ToString(),
            _ => OtomoSocialFailure.Disabled.ToString()
        };
        _logger.LogError("Refusing {Path}: session {Reason} ({Detail})", path, reason,
            failure?.Message ?? "Otomo:Social:Enabled is false");

        context.Response.Headers["x-app-status-code"] = "1";
        return Results.Json(new { error = "social-unavailable", reason },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private async Task<IResult> HandleFollowIndexAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
    {
        const string path = "/follow/index";
        var request = await ReadRequestRootAsync(context, key);
        if (request is null) return StatusError(context, key);
        var page = ReadPage(request.Value);

        var loaded = await ReadFollowRelationAsync(ticket, path, context.RequestAborted);
        if (loaded.Relation is null) return SocialUnavailable(context, path, loaded.Error);

        var pageRows = loaded.Relation.Following.Skip(page * SocialPageSize).Take(SocialPageSize).ToArray();
        var (cardRanks, cardError) = await TryReadCardRanksAsync(ticket, state, pageRows.Select(row => row.PlayerId),
            context.RequestAborted);
        if (cardRanks is null) return SocialUnavailable(context, path, cardError);
        var honors = await HonorsOfAsync(ticket, state, pageRows.Select(row => row.PlayerId), context.RequestAborted);
        var profiles = pageRows
            .Select(link => JsonSerializer.Deserialize<JsonElement>(
                SocialProfileJson(state, ProfileOf(link), 1, false, cardRanks[link.PlayerId], honors)));
        return OkJson(context, key, JsonSerializer.Serialize(new
        {
            userProfileList = profiles,
            followCount = loaded.Relation.Counts.Following,
            // Session has no read cursor on a follow, so nothing here can be "new". The count is part of the payload,
            // and zero is the honest answer rather than a number nobody could have marked read.
            newFollowerCount = 0
        }));
    }

    private async Task<IResult> HandleFollowerIndexAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
    {
        const string path = "/follower/index";
        var request = await ReadRequestRootAsync(context, key);
        if (request is null) return StatusError(context, key);
        var page = ReadPage(request.Value);

        if (!_social.Enabled)
        {
            return SocialUnavailable(context, path, new OtomoSocialException(OtomoSocialFailure.Disabled,
                "Otomo:Social:Enabled is false: this server holds no follow relation"));
        }

        IReadOnlyList<SessionFollow> followers;
        SessionFollowCounts counts;
        try
        {
            var accessToken = await PlayerTokenAsync(ticket, context.RequestAborted);
            followers = await _social.GetFollowersAsync(accessToken, context.RequestAborted);
            counts = await _social.GetFollowCountsAsync(accessToken, context.RequestAborted);
        }
        catch (Exception exception) when (exception is OtomoIdentityException or OtomoSocialException)
        {
            return SocialUnavailable(context, path, exception);
        }

        var translated = Translate(followers);
        var followedIds = translated.Select(link => link.PlayerId).ToHashSet();
        var pageRows = translated.Skip(page * SocialPageSize).Take(SocialPageSize).ToArray();
        var (cardRanks, cardError) = await TryReadCardRanksAsync(ticket, state, pageRows.Select(row => row.PlayerId),
            context.RequestAborted);
        if (cardRanks is null) return SocialUnavailable(context, path, cardError);
        var honors = await HonorsOfAsync(ticket, state, pageRows.Select(row => row.PlayerId), context.RequestAborted);
        var profiles = pageRows
            .Select(link => JsonSerializer.Deserialize<JsonElement>(
                SocialProfileJson(state, ProfileOf(link), FollowStatus(followedIds, state.PlayerId, link.PlayerId), false,
                    cardRanks[link.PlayerId], honors)));
        return OkJson(context, key, JsonSerializer.Serialize(new
        {
            userProfileList = profiles,
            followerCount = counts.Followers,
            newFollowerCount = 0
        }));
    }

    // The follower tab calls this when the player opens it. Session keeps no per-edge read mark, so there is nothing to
    // record: the route stays, so the client's flow does not change, and an empty success is what it answers.
    private async Task<IResult> HandleFollowerReadAsync(HttpContext context, SessionState state, byte[] key)
    {
        var request = await ReadRequestRootAsync(context, key);
        if (request is null) return StatusError(context, key);
        _logger.LogDebug("/follower/read for user {UserId}: session has no read mark, nothing to record", state.UserId);
        return OkJson(context, key, "{}");
    }

    private async Task<IResult> HandleFollowSearchAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
    {
        const string path = "/follow/search";
        var request = await ReadRequestRootAsync(context, key);
        if (request is null) return StatusError(context, key);
        var candidates = ReadStringIds(request.Value, "userIdList");

        var loaded = await ReadFollowRelationAsync(ticket, path, context.RequestAborted);
        if (loaded.Relation is null) return SocialUnavailable(context, path, loaded.Error);

        var followed = loaded.Relation.Following.Select(link => link.PlayerId).ToHashSet();
        return OkJson(context, key, JsonSerializer.Serialize(new
        {
            followUserIdList = candidates.Where(followed.Contains).Select(id => id.ToString())
        }));
    }

    // /follow/add and /follow/remove are addressed by the client with a numeric player id; session addresses a follow by
    // otomo player id, which the adapter's one mapping (IPlayerStore.FindOtomoPlayer) translates. Session's refusals
    // (404 player_not_found, 403 blocked, 400 invalid_target / invalid_player_id) reach the client as StatusError.
    private async Task<IResult> HandleFollowMutationAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key, bool add)
    {
        const string path = "/follow";
        var request = await ReadRequestRootAsync(context, key);
        if (request is null ||
            !request.Value.TryGetProperty("followUserId", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String ||
            !long.TryParse(idElement.GetString(), out var targetId) || targetId <= 0 || targetId == state.PlayerId)
        {
            return StatusError(context, key);
        }

        var target = _playerStore.FindOtomoPlayer(targetId);
        if (target is null)
        {
            _logger.LogInformation("{Path} for user {UserId}: player {Target} has no otomo account here", path,
                state.UserId, targetId);
            return StatusError(context, key);
        }
        if (!_social.Enabled)
        {
            return SocialUnavailable(context, path, new OtomoSocialException(OtomoSocialFailure.Disabled,
                "Otomo:Social:Enabled is false: this server holds no follow relation"));
        }

        try
        {
            var accessToken = await PlayerTokenAsync(ticket, context.RequestAborted);
            if (add)
            {
                await _social.FollowAsync(accessToken, target.OtomoPlayerId, context.RequestAborted);
            }
            else
            {
                await _social.UnfollowAsync(accessToken, target.OtomoPlayerId, context.RequestAborted);
            }
        }
        catch (OtomoSocialException ex) when (ex.Failure == OtomoSocialFailure.Refused)
        {
            _logger.LogInformation("{Path} for user {UserId} on player {Target} was refused: {Status} {Code}", path,
                state.UserId, targetId, ex.StatusCode, ex.Code);
            return StatusError(context, key);
        }
        catch (Exception exception) when (exception is OtomoIdentityException or OtomoSocialException)
        {
            return SocialUnavailable(context, path, exception);
        }

        return OkJson(context, key, "{}");
    }

    private async Task<IResult> HandleFollowOnlineAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
    {
        const string path = "/follow/online";
        var loaded = await ReadFollowRelationAsync(ticket, path, context.RequestAborted);
        if (loaded.Relation is null) return SocialUnavailable(context, path, loaded.Error);

        var online = loaded.Relation.Following.Where(link => IsRecentlyActive(link.PlayerId)).ToArray();
        var (cardRanks, cardError) = await TryReadCardRanksAsync(ticket, state, online.Select(row => row.PlayerId),
            context.RequestAborted);
        if (cardRanks is null) return SocialUnavailable(context, path, cardError);
        var honors = await HonorsOfAsync(ticket, state, online.Select(row => row.PlayerId), context.RequestAborted);
        var profiles = online
            .Select(link => JsonSerializer.Deserialize<JsonElement>(
                SocialProfileJson(state, ProfileOf(link), 1, false, cardRanks[link.PlayerId], honors)));
        return OkJson(context, key, JsonSerializer.Serialize(new { userProfileList = profiles }));
    }

    // A player's name is otomo's too. The social and ranking screens show session's display_name, so a rename is
    // written through with PATCH /api/player/session/me. Nothing from the answer is kept.
    //
    // Best effort on purpose: the name is already saved here, and a session that cannot be reached must not refuse a
    // player their own name. What that costs is a board that shows the old name until the next rename, which is
    // logged rather than silent.
    // The name goes to session first, and session's `names` rules decide whether it is allowed. A refusal (400
    // invalid_display_name, 429 inside a rename cooldown, ...) is "rejected" and is all this method maps; session being
    // down or off is not a verdict on the name, so it is logged and the name is kept here until the next rename.
    private async Task<string?> RenameAtSessionAsync(KfIdentityTicket ticket, string name, string path,
        CancellationToken cancellationToken)
    {
        if (!_social.Enabled) return null;
        try
        {
            await _social.RenameAsync(await PlayerTokenAsync(ticket, cancellationToken), name, cancellationToken);
            return null;
        }
        catch (OtomoSocialException exception) when (exception.Failure == OtomoSocialFailure.Refused)
        {
            _logger.LogInformation("{Path}: session refused the name of {PlayerId}: {Status} {Code}", path,
                ticket.OtomoPlayerId, exception.StatusCode, exception.Code);
            return "rejected";
        }
        catch (Exception exception) when (exception is OtomoIdentityException or OtomoSocialException)
        {
            _logger.LogWarning(
                "{Path}: the name of {PlayerId} could not be checked or saved at session ({Error}); the ranking board keeps " +
                "showing the old name until the next rename", path, ticket.OtomoPlayerId, exception.Message);
            return null;
        }
    }

    // --- the ranking board ---------------------------------------------------------------------------------------

    // The ranking screen. Rows and the caller's own standing are session's board; a row's kicker, costume and equipped
    // honor are this adapter's own record of what that player chose here.
    private async Task<IResult> HandleRankingAsync(HttpContext context, KfIdentityTicket ticket, SessionState state,
        byte[] key, string path)
    {
        // Crown ranking tabs show the currently rotating ranked mode. The request's battleRuleId is client state and
        // can be stale (or absent), so it must not select a different ladder than matchmaking.
        var battleRuleId = RankedModeRotation.RuleIdAt(_timeProvider.GetUtcNow());
        if (!_battleRuleTypeById.TryGetValue(battleRuleId, out var battleRuleType))
            battleRuleType = RegularBattleRuleType;
        var ruleType = _socialOptions.RuleTypeName(battleRuleType);

        if (!_social.Enabled)
        {
            return SocialUnavailable(context, path, new OtomoSocialException(OtomoSocialFailure.Disabled,
                "Otomo:Social:Enabled is false: this server holds no ranking board"));
        }
        if (ruleType is null)
        {
            return SocialUnavailable(context, path, new OtomoSocialException(OtomoSocialFailure.Refused,
                $"no session rule type is configured for battle rule type {battleRuleType}"));
        }

        var ladder = CurrentLadder();
        if (ladder is null)
        {
            return SocialUnavailable(context, path, new OtomoRankException(OtomoRankFailure.Unavailable,
                $"the published {RankLadderNamespace} ladder is not available, and this server holds no ladder of its own"));
        }

        BoardRead board;
        FollowRelation relation;
        try
        {
            var loaded = await ReadFollowRelationAsync(ticket, path, context.RequestAborted);
            if (loaded.Relation is null) return SocialUnavailable(context, path, loaded.Error);
            relation = loaded.Relation;

            var accessToken = await PlayerTokenAsync(ticket, context.RequestAborted);
            board = path switch
            {
                // The personal tab shows the caller's five neighbours either side, from the one position a page of
                // rows cannot tell them.
                "/ranking/user" => await ReadBoardWindowAsync(accessToken, ruleType, context.RequestAborted),
                // One call: session restricts the rows to the players the caller follows and still reports each
                // row's place on the full ladder.
                "/ranking/follow" => await ReadBoardPageAsync(accessToken, ruleType, RankingPageSize, 0,
                    BoardScope.Following, context.RequestAborted),
                // The local leaderboard needs a persisted region. No ladder carries one, so this deployment shows the
                // server population, which is the same rows the world tab returns.
                _ => await ReadBoardPageAsync(accessToken, ruleType, RankingPageSize, 0, BoardScope.All,
                    context.RequestAborted)
            };
        }
        catch (Exception exception) when (exception is OtomoIdentityException or OtomoSocialException)
        {
            return SocialUnavailable(context, path, exception);
        }

        var followedIds = relation.Following.Select(link => link.PlayerId).ToHashSet();
        var rowPlayers = _playerStore.FindOtomoPlayers(board.Rows.Select(row => row.PlayerId).ToArray()).Select(link => link.PlayerId);
        var honors = await HonorsOfAsync(ticket, state, rowPlayers.Append(state.PlayerId), context.RequestAborted);
        var resultRows = BuildRankingRows(board.Rows, state.PlayerId, followedIds, ladder, honors);
        var self = BuildOwnRankingRow(state, board.You, ladder, honors.GetValueOrDefault(state.PlayerId, BadgeCatalog.DefaultHonorId));
        // Client points still to go to the next published tier (0 at the top, and for a caller not on the board).
        var nextReward = ladder.RemainingToNextTier(board.You?.Points ?? 0);

        if (path == "/ranking/region")
        {
            return OkJson(context, key, $$"""
                {"battleRankingList":[{{string.Join(',', resultRows.Select(row => JsonSerializer.Serialize(row)))}}],"appSeasonMatchResult":{{NeutralSeasonMatchResult}}}
                """);
        }

        return OkJson(context, key, $$"""
            {"battleRankingList":[{{string.Join(',', resultRows.Select(row => JsonSerializer.Serialize(row)))}}],"battleRanking":{{self}},"nextRewardRemainingBattlePoint":{{nextReward}},"appSeasonMatchResult":{{NeutralSeasonMatchResult}}}
            """);
    }

    private async Task<BoardRead> ReadBoardPageAsync(string accessToken, string ruleType, int limit, int offset,
        BoardScope scope, CancellationToken cancellationToken)
    {
        var board = await _social.GetRankBoardAsync(accessToken, ruleType, limit, offset, scope, cancellationToken);
        return new BoardRead(board.Rows ?? [], board.You);
    }

    // The personal tab's window: the caller's own position first, because a page of rows does not say where they are,
    // and then the eleven rows around it.
    private async Task<BoardRead> ReadBoardWindowAsync(string accessToken, string ruleType,
        CancellationToken cancellationToken)
    {
        var own = await _social.GetRankBoardAsync(accessToken, ruleType, 1, 0, BoardScope.All, cancellationToken);
        var position = own.You?.Position ?? 0;
        if (position <= 0)
        {
            // Not on the board: the empty list plus the caller's own line is the answer, and it is a valid one.
            return new BoardRead([], own.You);
        }

        var offset = Math.Max(0, position - 6);
        var window = await ReadBoardPageAsync(accessToken, ruleType, 11, offset, BoardScope.All, cancellationToken);
        return new BoardRead(window.Rows, own.You ?? window.You);
    }

    private object[] BuildRankingRows(IReadOnlyList<SessionBoardRow> rows, long callerId, HashSet<long> followedIds,
        RankLadder ladder, IReadOnlyDictionary<long, int> honors)
    {
        if (rows.Count == 0) return [];

        var links = _playerStore.FindOtomoPlayers(rows.Select(row => row.PlayerId).ToArray())
            .ToDictionary(link => link.OtomoPlayerId, StringComparer.Ordinal);
        var profiles = _playerStore.FindProfiles(links.Values.Select(link => link.PlayerId).ToArray())
            .ToDictionary(profile => profile.PlayerId);
        var result = new List<object>(rows.Count);
        foreach (var row in rows)
        {
            if (!links.TryGetValue(row.PlayerId, out var link))
            {
                // The client parses userId as a number; a row with no game row has no number, so it is not served.
                _logger.LogInformation(
                    "Ranking row for otomo account {PlayerId} is not served: this adapter has no game row for it",
                    row.PlayerId);
                continue;
            }
            profiles.TryGetValue(link.PlayerId, out var profile);
            result.Add(new
            {
                userId = link.PlayerId.ToString(),
                // The name is session's answer for this row.
                name = row.DisplayName ?? "",
                honorId = honors.GetValueOrDefault(link.PlayerId, BadgeCatalog.DefaultHonorId),
                kickerId = profile?.KickerId ?? DefaultKickerId,
                kickerCostumeId = profile?.KickerCostumeId ?? DefaultKickerCostumeId,
                languageCode = "",
                followStatus = FollowStatus(followedIds, callerId, link.PlayerId),
                rank = RankOfTier(row.Tier, row.Points, ladder),
                battlePoint = row.Points,
                number = row.Position,
                percentile = 0.0
            });
        }
        return result.ToArray();
    }

    // The caller's own line. Their name, kicker and costume are this adapter's record of what they chose - the board's
    // `you` carries position, points and tier and nothing else.
    private string BuildOwnRankingRow(SessionState state, SessionBoardStanding? you, RankLadder ladder, int equippedHonor) =>
        you is null
            ? NeutralBattleRanking
            : JsonSerializer.Serialize(new
            {
                userId = state.UserId,
                name = state.UserName,
                honorId = equippedHonor,
                kickerId = state.KickerId,
                kickerCostumeId = state.KickerCostumeId,
                languageCode = "",
                followStatus = 0,
                rank = RankOfTier(you.Tier, you.Points, ladder),
                battlePoint = you.Points,
                number = (int)Math.Clamp(you.Position, 0, int.MaxValue),
                percentile = 0.0
            });

    // The client's `rank` is a league number, and session's `tier` is the ladder's key for the same thing: always
    // rank_<N>, where N is that number. The published tier thresholds are the check on it: a key the ladder does not
    // name is a disagreement between session and the release, said out loud, and the rank is then the one the
    // thresholds give the row's points rather than a guess.
    private int RankOfTier(string? tier, int points, RankLadder ladder)
    {
        var rank = RankTranslation.RankNumber(tier);
        if (rank is { } named && ladder.HasRank(named)) return named;
        var fromPoints = ladder.RankFor(points);
        _logger.LogError("Ranking tier '{Tier}' is not a tier of the published ladder; {Points} points place it at rank {Rank}",
            tier, points, fromPoints);
        return fromPoints;
    }

    // The card for a player on a follow screen. The name is session's answer for that row, and only that: this adapter no
    // longer reads its own copy of it. The kicker and costume are the game row's (session's ext declares no kicker).
    private PlayerProfile ProfileOf(SocialRow row)
    {
        var local = _playerStore.FindProfile(row.PlayerId);
        return new PlayerProfile(row.PlayerId, row.DisplayName ?? "",
            local?.KickerId ?? DefaultKickerId, local?.KickerCostumeId ?? DefaultKickerCostumeId);
    }

    // 1 when the caller follows this player, else 0. A "real friend" was a token this adapter minted and is gone
    // (migration 6), so nothing here can be 2 any more.
    private static int FollowStatus(HashSet<long> followedIds, long callerId, long targetId) =>
        callerId != targetId && followedIds.Contains(targetId) ? 1 : 0;

    // `honors` is the badge each player on the screen shows (HonorsOfAsync): the caller's own display slot, and session's
    // `display.honor` for everyone else. A player not in it shows the default.
    private string SocialProfileJson(SessionState caller, PlayerProfile profile, int followStatus, bool isNew,
        IReadOnlyList<BattleRankLine> ranks, IReadOnlyDictionary<long, int>? honors = null)
    {
        return JsonSerializer.Serialize(new
        {
            userId = profile.PlayerId.ToString(),
            displayUserId = PlayerDisplayIdCodec.ToPublic(profile.PlayerId),
            name = profile.DisplayName,
            honorId = honors?.GetValueOrDefault(profile.PlayerId, BadgeCatalog.DefaultHonorId) ?? BadgeCatalog.DefaultHonorId,
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
            userBattleRankList = BuildBattleRankList(ranks),
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
