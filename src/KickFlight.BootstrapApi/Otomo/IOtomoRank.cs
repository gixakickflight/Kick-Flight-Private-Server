using System.Text.Json.Serialization;

namespace KickFlight.BootstrapApi.Otomo;

// Why a session (rank) call failed. The same three-way split as IOtomoSocial's, because the adapter answers the
// client the same way for all of them (the 503 contract of SocialUnavailable):
//
//   Disabled     Otomo:Rank is off, or its internal plane has no service key: there is nobody to ask.
//   Unavailable  session could not be reached, timed out, or answered 5xx. A standing or an award is unknown, not
//                zero, so nothing is invented and nothing is dropped.
//   Refused      session answered a 4xx (no such player, unknown rule type). StatusCode and Code say which.
public enum OtomoRankFailure
{
    Disabled,
    Unavailable,
    Refused
}

public sealed class OtomoRankException : Exception
{
    public OtomoRankException(OtomoRankFailure failure, string message, int statusCode = 0, string? code = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        StatusCode = statusCode;
        Code = code;
    }

    public OtomoRankFailure Failure { get; }
    public int StatusCode { get; }
    public string? Code { get; }
}

// --- the wire types: session's own JSON keys ---------------------------------------------------------------------

// One rule type's standing. `tier` is the published ladder's key for the points (always rank_<N>).
public sealed record SessionRankEntry(
    [property: JsonPropertyName("rule_type")] string RuleType,
    [property: JsonPropertyName("points")] int Points,
    [property: JsonPropertyName("tier")] string? Tier,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt);

// GET /api/player/session/rank. Only the rule types the caller has a standing in are guaranteed to be present.
public sealed record SessionOwnRanks(
    [property: JsonPropertyName("ranks")] IReadOnlyList<SessionRankEntry>? Ranks);

// GET /internal/session/rank/players/{player_id}: every rule type is present (0 points and the lowest tier when none).
public sealed record SessionPlayerRanks(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("ranks")] IReadOnlyList<SessionRankEntry>? Ranks);

// POST /internal/session/rank/awards. `applied` is false when the key had already been applied for this player and
// rule type: the answer then carries the current standing, not the effect of this call.
public sealed record SessionRankAward(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("rule_type")] string RuleType,
    [property: JsonPropertyName("points")] int Points,
    [property: JsonPropertyName("tier")] string? Tier,
    [property: JsonPropertyName("applied")] bool Applied);

// The adapter's only view of battle points.
public interface IOtomoRank
{
    // False when Otomo:Rank is off. Callers fail closed.
    bool Enabled { get; }

    // The caller's own standing, with the player's own access token (the subject is the token's).
    Task<IReadOnlyList<SessionRankEntry>> GetOwnAsync(string accessToken, CancellationToken cancellationToken);

    // Another player's standing, on the internal plane, by otomo player id.
    Task<IReadOnlyList<SessionRankEntry>> GetPlayerAsync(string otomoPlayerId, CancellationToken cancellationToken);

    // Awards `delta` session points to a player on one rule type. Idempotent by `key` per player and rule type, so a
    // retry of the same battle can be sent again safely. Session floors points at 0.
    Task<SessionRankAward> AwardAsync(string otomoPlayerId, string ruleType, int delta, string key,
        CancellationToken cancellationToken);
}
