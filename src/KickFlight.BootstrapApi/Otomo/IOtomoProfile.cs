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
// (ext.session.player); this adapter reads pending_gear from it. The decks, costume gears and active deck are the
// player's Loadout (SessionLoadout), not ext.
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
    [property: JsonPropertyName("ext")] JsonElement? Ext,
    // The item the player shows in each display slot, {slot: item_id}; `{}` when nothing is equipped.
    [property: JsonPropertyName("display")] Dictionary<string, string>? Display = null);

// A player's loadout as session serves it (GET /api/player/session/loadout, GET /internal/session/players/{id}/loadout, and
// the `current` of a 409): the whole document, which session stamps with its own `revision` (also the ETag). The
// revision is what the next PUT names in If-Match.
public sealed record SessionLoadout(int Revision, JsonElement Document);

// One thing wrong with a submitted loadout, as session's 422 names it: an RFC 6901 JSON pointer and a message.
public sealed record SessionLoadoutIssue(
    [property: JsonPropertyName("pointer")] string Pointer,
    [property: JsonPropertyName("message")] string Message);

// The outcome of PUT /api/player/session/loadout. Exactly one member is set: Saved (200, with the new revision),
// Current (409 revision_conflict: the loadout as it is now, to start again from) or Issues (422 invalid_loadout).
// Any other refusal or failure is an OtomoRankException.
public sealed record SessionLoadoutWrite(
    SessionLoadout? Saved, SessionLoadout? Current, IReadOnlyList<SessionLoadoutIssue>? Issues);

// POST /internal/session/players/ext: the value after the write, and whether this call changed it. `applied` is false
// for a replay of a key already applied, which is a success.
public sealed record SessionExtWrite(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("ext")] JsonElement? Ext,
    [property: JsonPropertyName("applied")] bool Applied);

// GET/PUT/DELETE /api/player/session/display[/{slot}]: every slot the caller has set, as {slot: item_id}. The same map
// is `display` on GET /me. Session checks, when a slot is set, that the slot is one the tenant's session.rules declares,
// that the item is displayable in it (economy's display_slot) and that the caller holds it.
// 400 slot_unknown, 404 item_unknown, 422 item_not_displayable, 403 item_not_owned, 503 display_unavailable.

// The adapter's only view of a player's session card and the ext fields on it.
public interface IOtomoProfile
{
    // False when Otomo:Rank is off (this interface is served by the rank client, so it shares the rank section: it is the same session listener and the same
    // game-server key). Callers fail closed.
    bool Enabled { get; }

    // The caller's own card, with the player's own access token (the subject is the token's).
    Task<SessionMe> GetOwnProfileAsync(string accessToken, CancellationToken cancellationToken);

    // The caller's own display map ({slot: item_id}, empty when nothing is set), with the player's own token.
    Task<IReadOnlyDictionary<string, string>> GetDisplayAsync(string accessToken, CancellationToken cancellationToken);

    // Points one slot at an item the caller owns; the answer is the whole map afterwards. Refused with the codes above.
    Task<IReadOnlyDictionary<string, string>> SetDisplayAsync(string accessToken, string slot, string itemId,
        CancellationToken cancellationToken);

    // Clears one slot (clearing an empty one is a success); the answer is the whole map afterwards.
    Task<IReadOnlyDictionary<string, string>> ClearDisplayAsync(string accessToken, string slot,
        CancellationToken cancellationToken);

    // The caller's own loadout, with the player's own token. Null for 404 loadout_not_found (never saved).
    Task<SessionLoadout?> GetOwnLoadoutAsync(string accessToken, CancellationToken cancellationToken);

    // Replaces the caller's loadout with `document` (schema_version included, revision left out), naming the revision
    // it was built from in If-Match (0 for a first save), with the player's own token.
    Task<SessionLoadoutWrite> PutOwnLoadoutAsync(string accessToken, JsonObject document, int expectedRevision,
        CancellationToken cancellationToken);

    // Another player's loadout, on the internal plane (the same service key as the card). Null for 404
    // loadout_not_found; Refused 404 player_not_found for a stranger.
    Task<SessionLoadout?> GetPlayerLoadoutAsync(string otomoPlayerId, CancellationToken cancellationToken);

    // Another player's card, on the internal plane, by otomo player id. Refused 404 player_not_found for a stranger.
    Task<SessionPlayerProfile> GetPlayerProfileAsync(string otomoPlayerId, CancellationToken cancellationToken);

    // Writes ext fields for one player, on the internal plane. `merge` makes `ext` a shallow merge into what the player
    // has (a top-level field named here replaces that field whole); false replaces the whole value. Idempotent by `key`
    // per player: the same key writes once and the answer says whether this call changed anything.
    Task<SessionExtWrite> WriteExtAsync(string otomoPlayerId, JsonObject ext, string key, bool merge,
        CancellationToken cancellationToken);
}
