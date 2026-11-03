using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace KickFlight.BootstrapApi.Otomo;

// Failures are OtomoRankException (Disabled / Unavailable / Refused): profile and ext calls go to the same session
// listener with the same game-server key as the rank calls, through the same client (OtomoRank), so there is one
// failure model and one transport for "session".
//
//   Disabled     Otomo:Rank is off, or its internal plane has no service key: there is nobody to ask.
//   Unavailable  session could not be reached, timed out, or answered 5xx. A loadout is unknown, not empty, so none is
//                invented (DemoSessionApi.LoadoutUnavailable answers 503).
//   Refused      session answered a 4xx (no such player, a value the published ext.session.player schema refuses).
//                StatusCode and Code say which; a 400 invalid_ext names the field in its message.

// --- the wire types: session's own JSON keys ---------------------------------------------------------------------

// GET /api/player/session/me: the caller's own card. `ext` is the caller's own instance-declared fields
// (ext.session.player); this adapter reads decks, costume_gears, active_deck and pending_gear from it.
public sealed record SessionMe(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("discriminator")] int Discriminator,
    [property: JsonPropertyName("ext")] JsonElement? Ext);

// GET /internal/session/players/{player_id}: another player's card. `named` is false while the player still carries the
// provisional name session assigned at first sign-in.
public sealed record SessionPlayerProfile(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("discriminator")] int Discriminator,
    [property: JsonPropertyName("named")] bool Named,
    [property: JsonPropertyName("ext")] JsonElement? Ext);

// POST /internal/session/players/ext: the value after the write, and whether this call changed it. `applied` is false
// for a replay of a key already applied, which is a success.
public sealed record SessionExtWrite(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("ext")] JsonElement? Ext,
    [property: JsonPropertyName("applied")] bool Applied);

// The adapter's only view of a player's session card and the ext fields on it.
public interface IOtomoProfile
{
    // False when Otomo:Rank is off (this interface is served by the rank client, so it shares the rank section: it is the same session listener and the same
    // game-server key). Callers fail closed.
    bool Enabled { get; }

    // The caller's own card, with the player's own access token (the subject is the token's).
    Task<SessionMe> GetOwnProfileAsync(string accessToken, CancellationToken cancellationToken);

    // Another player's card, on the internal plane, by otomo player id. Refused 404 player_not_found for a stranger.
    Task<SessionPlayerProfile> GetPlayerProfileAsync(string otomoPlayerId, CancellationToken cancellationToken);

    // Writes ext fields for one player, on the internal plane. `merge` makes `ext` a shallow merge into what the player
    // has (a top-level field named here replaces that field whole); false replaces the whole value. Idempotent by `key`
    // per player: the same key writes once and the answer says whether this call changed anything.
    Task<SessionExtWrite> WriteExtAsync(string otomoPlayerId, JsonObject ext, string key, bool merge,
        CancellationToken cancellationToken);
}
