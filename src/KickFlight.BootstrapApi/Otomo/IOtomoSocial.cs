using System.Text.Json.Serialization;

namespace KickFlight.BootstrapApi.Otomo;

// Why a session (social) call failed. Same three-way split as economy's, because the adapter answers the client
// the same way for all of them:
//
//   Disabled     Otomo:Social is off: there is nobody to ask, and no follow list may be invented.
//   Unavailable  the service could not be reached, timed out, or answered 5xx. The relation is unknown, not empty,
//                so the caller answers a 503 rather than a screen full of "you follow nobody".
//   Refused      the service answered a 4xx: this call was not allowed. StatusCode and Code are what it said, so a
//                caller can tell "no such player" (404 player_not_found) from "you are blocked" (403 blocked).
public enum OtomoSocialFailure
{
    Disabled,
    Unavailable,
    Refused
}

public sealed class OtomoSocialException : Exception
{
    public OtomoSocialException(OtomoSocialFailure failure, string message, int statusCode = 0,
        string? code = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        StatusCode = statusCode;
        Code = code;
    }

    public OtomoSocialFailure Failure { get; }

    // The HTTP status session answered, or 0 when no answer arrived.
    public int StatusCode { get; }

    // The COM-5 error code session answered (`player_not_found`, `blocked`, `invalid_target`, ...), when it sent one.
    public string? Code { get; }
}

// --- the wire types ---------------------------------------------------------------------------------------------
//
// These are session's own bodies (services/session/internal/api/{profiles,follows,rank_board}.go), not a local
// invention: the names below are the JSON keys those handlers write. They are the contract for every byte this
// client sends and reads, so nothing here renames a field for convenience.
//
// The player id in every one of them is otomo's account id, not this adapter's numeric row: session has never heard
// of the numeric id the KF client parses.

// PATCH /api/player/session/me: the answer to a rename. The adapter does not keep it; it is read for the log and
// dropped, because session is where the name lives.
public sealed record SessionProfile(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("discriminator")] int Discriminator);

// One entry of a follow list, and the answer to a follow. `since` is absent on a row the store has no timestamp
// for, which is why it is nullable rather than defaulted.
public sealed record SessionFollow(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("discriminator")] int Discriminator,
    [property: JsonPropertyName("since")] DateTimeOffset? Since);

// The two list envelopes are separate types rather than one with optional members: session always answers the
// member its route is about, an empty list included.
public sealed record SessionFollowing(
    [property: JsonPropertyName("following")] IReadOnlyList<SessionFollow>? Following);

public sealed record SessionFollowers(
    [property: JsonPropertyName("followers")] IReadOnlyList<SessionFollow>? Followers);

// GET /api/player/session/follows/count: both directions in one answer.
public sealed record SessionFollowCounts(
    [property: JsonPropertyName("followers")] int Followers,
    [property: JsonPropertyName("following")] int Following);

// One line of GET /api/player/session/rank/board. `tier` is the ladder's own key for the row's points (rank_<N>),
// `display_name`/`discriminator` are the tag the social screen shows beside it, and `position` is the row's 1-based
// place on the FULL ladder - the same number whether the board was asked for scope=all or scope=following.
public sealed record SessionBoardRow(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("points")] int Points,
    [property: JsonPropertyName("tier")] string? Tier,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("discriminator")] int Discriminator,
    [property: JsonPropertyName("position")] int Position);

// The caller's own line. Position is 1-based, and 0 means the caller is not on the board at all - a state the
// screen renders (no highlight) rather than an error.
public sealed record SessionBoardStanding(
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("points")] int Points,
    [property: JsonPropertyName("tier")] string? Tier);

// GET /api/player/session/rank/board?rule_type=&limit=&offset=&scope=. `scope` echoes the request. `you` is the caller's own line whatever page was
// asked for, which is what lets the ranking screen show a position the caller cannot see in the rows.
public sealed record SessionRankBoard(
    [property: JsonPropertyName("rule_type")] string? RuleType,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("named_only")] bool NamedOnly,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("rows")] IReadOnlyList<SessionBoardRow>? Rows,
    [property: JsonPropertyName("you")] SessionBoardStanding? You);

// The adapter's only view of the social relation, and of the ranking board built on it.
//
// One implementation (OtomoSocial) talks to otomo's session service over HTTP with the player's *own* access
// token - the one OtomoIdentity caches per device. No route here takes a player id for the subject: on the player
// plane the token is the subject, so there is no argument a caller could change to follow or unfollow for someone
// else. The one id any of them carries is the *target* of an unfollow, which is the id the follow lists hand back.
//
// A follow is written by the target's otomo player id ({"player_id"}) and removed by the same id, so the adapter only
// ever needs the numeric-id <-> account-id mapping it already keeps; it holds no tag.
public enum BoardScope
{
    All,
    Following
}

public interface IOtomoSocial
{
    // False when Otomo:Social is off. Callers fail closed rather than serve an empty friend list.
    bool Enabled { get; }

    // Writes the player's name through to session, which is where it lives.
    Task<SessionProfile> RenameAsync(string accessToken, string displayName, CancellationToken cancellationToken);

    Task<IReadOnlyList<SessionFollow>> GetFollowingAsync(string accessToken, CancellationToken cancellationToken);

    Task<IReadOnlyList<SessionFollow>> GetFollowersAsync(string accessToken, CancellationToken cancellationToken);

    Task<SessionFollowCounts> GetFollowCountsAsync(string accessToken, CancellationToken cancellationToken);

    // Idempotent: following a player the caller already follows answers the same row. 404 player_not_found when no
    // account has that id, 403 blocked, 400 invalid_target (a self-follow) or invalid_player_id.
    Task<SessionFollow> FollowAsync(string accessToken, string targetOtomoPlayerId,
        CancellationToken cancellationToken);

    // Removes the caller's own direction only, and is a no-op when it was not there.
    Task UnfollowAsync(string accessToken, string playerId, CancellationToken cancellationToken);

    // One page of the ranking board, plus the caller's own line. `ruleType` is session's rule-type *name*, which is
    // configured because a session instance publishes the names it keeps. `scope` All is every player; Following is
    // only the players the caller follows, with each row's position still taken from the full ladder.
    Task<SessionRankBoard> GetRankBoardAsync(string accessToken, string ruleType, int limit, int offset,
        BoardScope scope, CancellationToken cancellationToken);
}
