using System.Text.Json.Serialization;

namespace KickFlight.BootstrapApi.Otomo;

// Why a rank call failed. Like identity and the economy, the distinction survives all the way out to the caller:
//
//   Disabled     Otomo:Rank is off. There is no rank service to ask, and the adapter's own table is the answer;
//                the switch selects a path, so nothing here is consulted.
//   Unavailable  otomo could not be reached, timed out, or answered 5xx (twice). The player's standing is unknown,
//                not absent.
//   Refused      otomo answered a 4xx: this call was not allowed. Code is what it said (`invalid_token`, ...), so a
//                caller can tell a bad credential from a service that is down without parsing a message.
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

    // The HTTP status otomo answered, or 0 when no answer arrived.
    public int StatusCode { get; }

    // otomo's own refusal code (`unknown_rule_type`, `invalid_token`, ...), when it sent one.
    public string? Code { get; }
}

// --- the wire types ---------------------------------------------------------------------------------------------
//
// These are otomo's own bodies (services/session/internal/api/rank.go on otomo's staging), not a local invention:
// the JSON names below are the keys those handlers write, so nothing here renames a field for convenience.

// One rule type as a player reads it. updated_at is null for a rule type the player has never been awarded in,
// which is how "no points yet" is told apart from "awarded zero points once".
public sealed record OtomoRankEntry(
    [property: JsonPropertyName("rule_type")] string? RuleType,
    [property: JsonPropertyName("points")] int Points,
    [property: JsonPropertyName("tier")] string? Tier,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt);

// GET /api/player/session/rank: one entry per battle rule type the published session.rank ladder names, plus any
// rule type the player still has a row for that the policy has since dropped.
public sealed record OtomoRankList(
    [property: JsonPropertyName("ranks")] IReadOnlyList<OtomoRankEntry>? Ranks);

// The adapter's view of otomo's rank service.
//
// One implementation (OtomoRank) talks to otomo's gateway over HTTP, on the player's own access token - the one
// OtomoIdentity already caches per device. Nothing here takes a player id: otomo's rank routes are the token's
// subject by construction, so a caller cannot read anyone else's points, and this client cannot be asked to.
//
// Every read is a read. Rank is awarded by the matchmaker on session's internal listener
// (POST /internal/session/rank/awards), not from here: an adapter that could write points would be a second, and
// unaudited, rank authority.
public interface IOtomoRank
{
    // False when Otomo:Rank is off. Every caller then reads the adapter's own store, exactly as before, and nothing
    // in this client is called.
    bool Enabled { get; }

    // Bound on one call, as configured. Exposed so a caller can reason about the worst case it adds to a request.
    TimeSpan Timeout { get; }

    // GET /api/player/session/rank: this player's points and tier in every rule type the ladder names. A player who
    // has never been awarded anything is 200 with zero points in the lowest tier, not a miss.
    ValueTask<IReadOnlyList<OtomoRankEntry>> ReadAllAsync(string accessToken, CancellationToken cancellationToken);

    // GET /api/player/session/rank/{rule_type}: one rule type. Null when otomo answers 404 unknown_rule_type - the
    // policy does not rank that battle, which is a "no rank" answer and not a reason to fail the request.
    ValueTask<OtomoRankEntry?> ReadOneAsync(string ruleType, string accessToken, CancellationToken cancellationToken);
}
