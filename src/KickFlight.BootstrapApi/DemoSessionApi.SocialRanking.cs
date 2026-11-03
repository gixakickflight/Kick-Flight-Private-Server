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
//   * A follow is written by *tag* ("Tanuki#4417"), and the only tag this adapter knows is the one otomo answered for
//     an account (IPlayerStore.FindOtomoPlayer). A player it has never been told a tag for cannot be followed by id:
//     that is a refusal the client shows, not a tag invented here.
//   * Session's follow relation has no read cursor, so a follower is never "new" - newFlag is false and
//     newFollowerCount is 0, and /follower/read answers an empty success. The unread mark lived in player_follows and
//     went with that table (migration 6).
//   * A ranking row's name comes from session's board, because a name now lives there. A row whose account has no
//     game row here is skipped: the client parses userId as a number, and there is no number to give it for someone
//     who has never played on this server.
public sealed partial class DemoSessionApi
{
    private const int SocialPageSize = 20;
    private const int RankingPageSize = 100;
    // How many board pages a follow-filtered ranking list walks before it stops. The board has no "these players only"
    // query, so a followed player outside the pages read is not found; this bound is what keeps that from turning one
    // screen into a walk of the whole ladder.
    private const int RankingFollowScanPages = 20;
    private const int DefaultKickerId = 1;
    // Kicker 1's first costume: what the client draws for a player this adapter holds no kicker for.
    private const int DefaultKickerCostumeId = 2010101;
    private static readonly TimeSpan OnlineActivityWindow = TimeSpan.FromMinutes(11);
    private readonly ConcurrentDictionary<long, DateTimeOffset> _onlineActivityByPlayer = new();

    // The caller's follow relation as session answered it: the accounts they follow, translated into the rows the
    // client speaks, and the two counts. A null relation is a failure the caller answers 503.
    private sealed record FollowRelation(IReadOnlyList<OtomoPlayer> Following, SessionFollowCounts Counts);

    private sealed record FollowRelationLoad(FollowRelation? Relation, Exception? Error);

    // One board row and the one-based position it holds on the ladder, which is what the client renders as `number`.
    private sealed record BoardRowRow(SessionBoardRow Row, int Position);

    private sealed record BoardRead(IReadOnlyList<BoardRowRow> Rows, SessionBoardStanding? You);

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
    private IReadOnlyList<OtomoPlayer> Translate(IReadOnlyList<SessionFollow> follows)
    {
        if (follows.Count == 0) return [];
        var links = _playerStore.FindOtomoPlayers(follows.Select(follow => follow.PlayerId).ToArray())
            .ToDictionary(link => link.OtomoPlayerId, StringComparer.Ordinal);
        var players = new List<OtomoPlayer>(follows.Count);
        foreach (var follow in follows)
        {
            if (!links.TryGetValue(follow.PlayerId, out var link))
            {
                _logger.LogInformation(
                    "Follow edge with otomo account {PlayerId} is not served: this adapter has no game row for it",
                    follow.PlayerId);
                continue;
            }
            // Session's name is the one a social screen shows, so it wins over the mirrored one, which is only ever
            // what session said the last time this adapter asked.
            players.Add(string.IsNullOrEmpty(follow.DisplayName)
                ? link
                : link with { DisplayName = follow.DisplayName, Discriminator = follow.Discriminator });
        }
        return players;
    }

    // A read that needs the relation and cannot get one: the game's error contract, 503, never an empty list.
    private IResult SocialUnavailable(HttpContext context, string path, Exception? failure)
    {
        var reason = failure switch
        {
            OtomoIdentityException identity => identity.Failure.ToString(),
            OtomoSocialException social => social.Failure.ToString(),
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

        var profiles = loaded.Relation.Following
            .Skip(page * SocialPageSize)
            .Take(SocialPageSize)
            .Select(link => JsonSerializer.Deserialize<JsonElement>(
                SocialProfileJson(state, ProfileOf(link), 1, false)));
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
        var profiles = translated
            .Skip(page * SocialPageSize)
            .Take(SocialPageSize)
            .Select(link => JsonSerializer.Deserialize<JsonElement>(
                SocialProfileJson(state, ProfileOf(link), FollowStatus(followedIds, state.PlayerId, link.PlayerId), false)));
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

    // /follow/add is addressed by the client with a numeric player id, and session addresses a follow by tag. The tag is
    // the one otomo answered for that account - a display name and a discriminator - which is why this adapter keeps it
    // (IPlayerStore.FindOtomoPlayer) and refuses rather than guesses when it has never been told one.
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
        if (add && (string.IsNullOrWhiteSpace(target.DisplayName) || target.Discriminator <= 0))
        {
            // Nothing is sent: a follow with an invented tag would either 404 or, worse, follow somebody else.
            _logger.LogWarning(
                "{Path} for user {UserId}: player {Target} has no otomo tag here yet (name '{Name}', discriminator {Tag}); " +
                "it is learned when that player next sets their name", path, state.UserId, targetId, target.DisplayName,
                target.Discriminator);
            return StatusError(context, key);
        }

        try
        {
            var accessToken = await PlayerTokenAsync(ticket, context.RequestAborted);
            if (add)
            {
                var followed = await _social.FollowAsync(accessToken, target.DisplayName, target.Discriminator,
                    context.RequestAborted);
                // Session's answer is the tag as it now stands, so it is what the mirror keeps.
                if (!string.IsNullOrEmpty(followed.DisplayName))
                {
                    _playerStore.SaveOtomoTag(followed.PlayerId, followed.DisplayName, followed.Discriminator);
                }
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

        var profiles = loaded.Relation.Following
            .Where(link => IsRecentlyActive(link.PlayerId))
            .Select(link => JsonSerializer.Deserialize<JsonElement>(
                SocialProfileJson(state, ProfileOf(link), 1, false)));
        return OkJson(context, key, JsonSerializer.Serialize(new { userProfileList = profiles }));
    }

    // A player's name is otomo's too. The ranking board serves session's display_name, and a follow is addressed by
    // that name plus a discriminator, so a rename is written through with PATCH /api/player/session/me and the tag the
    // answer carries is kept (IPlayerStore.SaveOtomoTag) - which is where this adapter gets the discriminator a follow
    // needs at all.
    //
    // Best effort on purpose: the name is already saved here, and a session that cannot be reached must not refuse a
    // player their own name. What that costs is a board that shows the old name and a follow that cannot be addressed
    // until the next rename, both of which are logged rather than silent.
    private async Task PublishNameAsync(KfIdentityTicket ticket, SessionState state, string path,
        CancellationToken cancellationToken)
    {
        if (!_social.Enabled || string.IsNullOrWhiteSpace(state.UserName)) return;
        try
        {
            var profile = await _social.RenameAsync(await PlayerTokenAsync(ticket, cancellationToken), state.UserName,
                cancellationToken);
            _playerStore.SaveOtomoTag(profile.PlayerId, profile.DisplayName ?? state.UserName, profile.Discriminator);
        }
        catch (Exception exception) when (exception is OtomoIdentityException or OtomoSocialException)
        {
            _logger.LogWarning(
                "{Path}: the name of user {UserId} was saved here but not in otomo ({Error}); the ranking board keeps " +
                "its own name for this player, and a follow addressed to them needs a rename first", path, state.UserId,
                exception.Message);
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
                "/ranking/follow" => await ReadBoardFollowingAsync(accessToken, ruleType, relation,
                    context.RequestAborted),
                // The local leaderboard needs a persisted region. No ladder carries one, so this deployment shows the
                // server population, which is the same rows the world tab returns.
                _ => await ReadBoardPageAsync(accessToken, ruleType, RankingPageSize, 0, context.RequestAborted)
            };
        }
        catch (Exception exception) when (exception is OtomoIdentityException or OtomoSocialException)
        {
            return SocialUnavailable(context, path, exception);
        }

        var followedIds = relation.Following.Select(link => link.PlayerId).ToHashSet();
        var resultRows = BuildRankingRows(board.Rows, state.PlayerId, followedIds);
        var self = BuildOwnRankingRow(state, board.You);
        var ownPoints = board.You?.Points ?? 0;
        var nextReward = Math.Max(0, RankProgression.TotalBattlePointByRank
            .Where(points => points > ownPoints).DefaultIfEmpty(ownPoints).Min() - ownPoints);

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
        CancellationToken cancellationToken)
    {
        var board = await _social.GetRankBoardAsync(accessToken, ruleType, limit, offset, cancellationToken);
        var rows = new List<BoardRowRow>();
        var index = 0;
        foreach (var row in board.Rows ?? [])
        {
            index++;
            rows.Add(new BoardRowRow(row, offset + index));
        }
        return new BoardRead(rows, board.You);
    }

    // The personal tab's window: the caller's own position first, because a page of rows does not say where they are,
    // and then the eleven rows around it.
    private async Task<BoardRead> ReadBoardWindowAsync(string accessToken, string ruleType,
        CancellationToken cancellationToken)
    {
        var own = await _social.GetRankBoardAsync(accessToken, ruleType, 1, 0, cancellationToken);
        var position = own.You?.Position ?? 0;
        if (position <= 0)
        {
            // Not on the board: the empty list plus the caller's own line is the answer, and it is a valid one.
            return new BoardRead([], own.You);
        }

        var offset = Math.Max(0, position - 6);
        var window = await ReadBoardPageAsync(accessToken, ruleType, 11, offset, cancellationToken);
        return new BoardRead(window.Rows, own.You ?? window.You);
    }

    // The follow tab: session's board has no "only these players" query, so the pages are walked until enough followed
    // players are found. Each row keeps the position it holds on the ladder, not the position it holds in this list.
    private async Task<BoardRead> ReadBoardFollowingAsync(string accessToken, string ruleType, FollowRelation relation,
        CancellationToken cancellationToken)
    {
        if (relation.Following.Count == 0) return new BoardRead([], null);

        var wanted = relation.Following.Select(link => link.OtomoPlayerId).ToHashSet(StringComparer.Ordinal);
        var rows = new List<BoardRowRow>();
        SessionBoardStanding? you = null;
        for (var page = 0; page < RankingFollowScanPages; page++)
        {
            var read = await ReadBoardPageAsync(accessToken, ruleType, RankingPageSize, page * RankingPageSize,
                cancellationToken);
            you ??= read.You;
            rows.AddRange(read.Rows.Where(row => wanted.Contains(row.Row.PlayerId)));
            // A short page is the end of the ladder, and a full list of followed rows is what the screen asked for.
            if (read.Rows.Count < RankingPageSize || rows.Count >= RankingPageSize) break;
        }
        return new BoardRead(rows, you);
    }

    private object[] BuildRankingRows(IReadOnlyList<BoardRowRow> rows, long callerId, HashSet<long> followedIds)
    {
        if (rows.Count == 0) return [];

        var links = _playerStore.FindOtomoPlayers(rows.Select(row => row.Row.PlayerId).ToArray())
            .ToDictionary(link => link.OtomoPlayerId, StringComparer.Ordinal);
        var profiles = _playerStore.FindProfiles(links.Values.Select(link => link.PlayerId).ToArray())
            .ToDictionary(profile => profile.PlayerId);
        var result = new List<object>(rows.Count);
        foreach (var (row, position) in rows)
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
                name = string.IsNullOrEmpty(row.DisplayName)
                    ? profile?.DisplayName ?? link.DisplayName
                    : row.DisplayName,
                honorId = EquippedHonorId(_playerStore.TryLoad(link.PlayerId)),
                kickerId = profile?.KickerId ?? DefaultKickerId,
                kickerCostumeId = profile?.KickerCostumeId ?? DefaultKickerCostumeId,
                languageCode = "",
                followStatus = FollowStatus(followedIds, callerId, link.PlayerId),
                rank = RankOfTier(row.Tier, row.Points),
                battlePoint = row.Points,
                number = position,
                percentile = 0.0
            });
        }
        return result.ToArray();
    }

    // The caller's own line. Their name, kicker and costume are this adapter's record of what they chose - the board's
    // `you` carries position, points and tier and nothing else.
    private string BuildOwnRankingRow(SessionState state, SessionBoardStanding? you) =>
        you is null
            ? NeutralBattleRanking
            : JsonSerializer.Serialize(new
            {
                userId = state.UserId,
                name = state.UserName,
                honorId = BadgeAwardRules.GetOwnedHonorIds(state).Contains(state.HonorId)
                    ? state.HonorId
                    : BadgeAwardRules.DefaultHonorId,
                kickerId = state.KickerId,
                kickerCostumeId = state.KickerCostumeId,
                languageCode = "",
                followStatus = 0,
                rank = RankOfTier(you.Tier, you.Points),
                battlePoint = you.Points,
                number = (int)Math.Clamp(you.Position, 0, int.MaxValue),
                percentile = 0.0
            });

    // The client's `rank` is a league number, and session's `tier` is the ladder's label for the same thing: the
    // published ladders name their tiers with it, so the label parses straight back. A label that is not a number is a
    // ladder this adapter does not know, and the client's own progression table is a better answer for one row than a
    // zero would be - said out loud, because it means the published document and this code disagree.
    private int RankOfTier(string? tier, int points)
    {
        if (int.TryParse(tier, out var rank) && rank > 0) return rank;
        _logger.LogDebug("Ranking tier '{Tier}' is not a rank number; drawing it from {Points} battle points", tier, points);
        return RankProgression.RankFor(points);
    }

    private PlayerProfile ProfileOf(OtomoPlayer link) =>
        _playerStore.FindProfile(link.PlayerId)
        ?? new PlayerProfile(link.PlayerId, link.DisplayName, DefaultKickerId, DefaultKickerCostumeId);

    // 1 when the caller follows this player, else 0. A "real friend" was a token this adapter minted and is gone
    // (migration 6), so nothing here can be 2 any more.
    private static int FollowStatus(HashSet<long> followedIds, long callerId, long targetId) =>
        callerId != targetId && followedIds.Contains(targetId) ? 1 : 0;

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
