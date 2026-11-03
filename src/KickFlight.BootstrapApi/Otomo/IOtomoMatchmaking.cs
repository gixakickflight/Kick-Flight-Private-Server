using System.Text.Json.Serialization;

namespace KickFlight.BootstrapApi.Otomo;

// Why a matchmaking call failed. The adapter turns this into the game's error contract, so the distinction has to
// survive all the way out, exactly as it does for identity and the economy:
//
//   Disabled     Otomo:Matchmaking is off. There is no matchmaker to ask, and an in-memory room is not a substitute
//                the caller may quietly fall back to: the switch is what selects a path, and the caller checks it.
//   Unavailable  the matchmaker could not be reached, timed out, or answered 5xx (twice). The party's match state is
//                unknown, not refused.
//   Refused      the matchmaker answered a 4xx: this call was not allowed. Code is what it said, so a caller can tell
//                "this party is already in a match" (409 already_matched) from "this token is not usable"
//                (401 invalid_token) without parsing a message.
public enum OtomoMatchmakingFailure
{
    Disabled,
    Unavailable,
    Refused
}

public sealed class OtomoMatchmakingException : Exception
{
    public OtomoMatchmakingException(OtomoMatchmakingFailure failure, string message, int statusCode = 0,
        string? code = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        StatusCode = statusCode;
        Code = code;
    }

    public OtomoMatchmakingFailure Failure { get; }

    // The HTTP status otomo answered, or 0 when no answer arrived.
    public int StatusCode { get; }

    // otomo's own refusal code (`already_in_party`, `already_matched`, `match_in_progress`, `invalid_token`, ...),
    // when it sent one.
    public string? Code { get; }
}

// --- the wire types ---------------------------------------------------------------------------------------------
//
// These are otomo's own bodies for the four player matchmaking routes, not a local invention: the JSON names below
// are the keys those handlers write. They are the contract for every byte this client sends and reads, so nothing
// here renames a field for convenience.

// POST /api/player/session/party -> 201. A party of one, created for this player; the player is its leader.
public sealed record OtomoParty(
    [property: JsonPropertyName("party_id")] string? PartyId,
    [property: JsonPropertyName("leader_id")] string? LeaderId,
    [property: JsonPropertyName("revision")] long Revision);

// POST /api/player/match?party_id=<id> -> 201 (200 on a repeat, with the same ticket).
public sealed record OtomoMatchTicket(
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("ticket_id")] string? TicketId,
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("waited_seconds")] double WaitedSeconds);

// The three ports the client dials here. otomo publishes them as the descriptor's `ports` object.
public sealed record OtomoMatchPorts(
    [property: JsonPropertyName("master")] int Master,
    [property: JsonPropertyName("game")] int Game,
    [property: JsonPropertyName("name")] int Name);

// Where the match's room server is. `host` is what the client connects to, `ports` is what it dials, and `proxied`
// says whether that host is a relay in front of the room rather than the room itself.
public sealed record OtomoMatchDescriptor(
    [property: JsonPropertyName("transport")] string? Transport,
    [property: JsonPropertyName("region")] string? Region,
    [property: JsonPropertyName("host")] string? Host,
    [property: JsonPropertyName("ports")] OtomoMatchPorts? Ports,
    [property: JsonPropertyName("proxied")] bool Proxied);

// The match a party was placed in. `match_id` is the battle id: the client joins a Photon room by this name, so it
// is what Stage 3 carries.
public sealed record OtomoMatchAssignment(
    [property: JsonPropertyName("match_id")] string? MatchId,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("transport")] string? Transport,
    [property: JsonPropertyName("region")] string? Region,
    [property: JsonPropertyName("descriptor")] OtomoMatchDescriptor? Descriptor);

// GET /api/player/match?party_id=<id>. One of three states; `match` is present only for in_match.
public sealed record OtomoMatchState(
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("match")] OtomoMatchAssignment? Match)
{
    // The party is waiting for a match: the client belongs in the matching screen's searching stage.
    public bool IsQueued => string.Equals(State, "queued", StringComparison.Ordinal);

    // The party has a match: the client belongs in the battle, and the assignment is where.
    public bool IsInMatch => string.Equals(State, "in_match", StringComparison.Ordinal);

    // Nothing is queued and nothing is matched, e.g. the ticket was cancelled or the match ended.
    public bool IsIdle => string.Equals(State, "idle", StringComparison.Ordinal);
}

// The join address and ports, taken from a descriptor and validated, or null when the descriptor does not carry a
// usable one. The ports are handed on exactly as otomo sent them: the client rewrites 5055/5056/5058 to
// 27000-27002 itself, so 27000 in the descriptor is the value it expects, not a value to normalise.
public sealed record OtomoJoinEndpoint(string Host, int MasterPort, int GamePort, int NamePort, string Region, bool Proxied)
{
    public static OtomoJoinEndpoint? FromDescriptor(OtomoMatchDescriptor? descriptor)
    {
        if (descriptor is null || string.IsNullOrWhiteSpace(descriptor.Host) || descriptor.Ports is null) return null;
        if (!IsPort(descriptor.Ports.Master) || !IsPort(descriptor.Ports.Game) || !IsPort(descriptor.Ports.Name))
            return null;
        return new OtomoJoinEndpoint(descriptor.Host.Trim(), descriptor.Ports.Master, descriptor.Ports.Game,
            descriptor.Ports.Name, descriptor.Region ?? "", descriptor.Proxied);
    }

    private static bool IsPort(int port) => port is > 0 and <= 65535;
}

// The adapter's view of otomo's player matchmaking.
//
// One implementation (OtomoMatchmaking) talks to otomo's gateway over HTTP, on the player's own access token - the
// one OtomoIdentity caches per device. Nothing here takes a player id: the token is the identity, so a caller cannot
// queue or cancel on someone else's behalf.
public interface IOtomoMatchmaking
{
    // False when Otomo:Matchmaking is off. The caller then serves matchmaking the way it always has; nothing here is
    // consulted, and no call is made.
    bool Enabled { get; }

    // The player's party, created if this player has none. A party is created of one (the player is its leader), so
    // a returning player who is already in a party - otomo answers 409 already_in_party - is not a failure: the
    // party otomo already has is the answer, when its body names it.
    ValueTask<OtomoParty> EnsurePartyAsync(string accessToken, CancellationToken cancellationToken);

    // Puts the party in the queue. Repeating it is what otomo answers with 200 and the same ticket, so a client that
    // retries /battle/entry does not become two queue entries.
    ValueTask<OtomoMatchTicket> EnqueueAsync(string partyId, string accessToken, CancellationToken cancellationToken);

    // The party's state right now: idle, queued, or in_match with the match and its descriptor.
    ValueTask<OtomoMatchState> StateAsync(string partyId, string accessToken, CancellationToken cancellationToken);

    // Withdraws the party from the queue. Called when the player backs out of the matching screen. A party whose
    // match is already forming answers 409 match_in_progress: that is a refusal, and the caller says so rather than
    // pretending the cancel worked.
    ValueTask CancelAsync(string partyId, string accessToken, CancellationToken cancellationToken);

    // How often an open assignment stream should ask for the party's state. It is one of the client's own settings
    // (Otomo:Matchmaking:PollInterval), so the cadence a deployment tunes is the cadence its streams use.
    TimeSpan PollInterval { get; }
}
