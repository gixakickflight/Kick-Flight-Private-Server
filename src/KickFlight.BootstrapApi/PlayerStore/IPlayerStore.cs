namespace KickFlight.BootstrapApi.PlayerStore;

// A resolved session token: which player it belongs to, and the key their bodies are encrypted with.
public sealed record SessionRecord(long PlayerId, byte[] Key);

// Public data used by profile cards and social/ranking lists. An unset display name stays empty; callers must
// not invent a player identity for an ID that has no persisted profile.
public sealed record PlayerProfile(long PlayerId, string DisplayName, int KickerId, int KickerCostumeId);

// One game row and the otomo account behind it: the numeric id the client speaks and the account id otomo speaks.
// That mapping is the only thing this adapter keeps about the account. Names, tags and discriminators are session's,
// and are read from session's answer each time they are shown.
public sealed record OtomoPlayer(long PlayerId, string OtomoPlayerId);

// Where player state lives.
//
// Two implementations, chosen by whether a connection string is configured (see PlayerStoreFactory):
// PostgresPlayerStore for the deployment, JsonPlayerStore for local runs and the test suite, which must not
// need a database to be reachable.
//
// Everything here is deliberately synchronous. The callers are the existing synchronous request handlers,
// reached from ~13 call sites, and Npgsql has first-class sync methods; making this async would convert a
// large part of DemoSessionApi for no benefit at this scale.
//
// What is *not* here any more: the social relation. Who follows whom moved to otomo's session service, and
// migration 6 dropped player_follows, real_friend_tokens and player_real_friends along with the members that read
// them. Owning half of it - a follow list here, a board there - is what this store must not do.
public interface IPlayerStore
{
    // The device uuid's player, created on first contact. This is the identity guarantee the store exists
    // for: the same uuid must resolve to the same player id forever, across restarts and redeploys.
    //
    // Main's identity path. With otomo configured, ResolveGamePlayer is what the API calls instead: the account,
    // not the device, is the identity, and a row this method created earlier is adopted by it.
    long ResolvePlayerId(string uuid);

    // The game row of an otomo account, created on first contact with the account. A device that already owns a
    // row - one ResolvePlayerId made, or one from before the move to otomo - is adopted by the first account that
    // presents it, keeping its name, rank, inventory and decks. The same account always gets the same row.
    long ResolveGamePlayer(string otomoPlayerId, string legacyDeviceUuid);

    // The player's state, or null if that id has never been seen.
    SessionState? TryLoad(long playerId);

    // Upsert. Also records the uuid -> player id mapping when the state carries one (see SessionState).
    void Save(SessionState state, string? uuid = null);

    // Session tokens are not part of this contract. Main persisted them here so a restart would not re-issue a
    // client a different identity; on this side the access token is signed by the API (KfAccessTokenCodec) and the
    // device's refresh family lives in otomo, so there is nothing to persist and migration 4 drops the sessions
    // table. JsonPlayerStore still has its file-backed map for local runs, but no caller reaches it.

    // Whether another player already goes by this name. Case-insensitive, because a name is how a player is
    // identified on the ranking and profile screens, where "Tanuki" and "tanuki" reading as two people is a bug
    // rather than a feature. `exceptPlayerId` is the caller, so re-submitting one's own name is not a clash.
    bool IsNameTaken(string name, long exceptPlayerId);

    // Profiles: the name, kicker and costume a card or a ranking row shows for a row the adapter already has. This is
    // not the social relation - it is the presentation a player chose here - and it is looked up by the numeric id
    // the client speaks. A row with no persisted profile answers null rather than a fabricated identity.
    PlayerProfile? FindProfile(long playerId);
    IReadOnlyList<PlayerProfile> FindProfiles(IReadOnlyCollection<long> playerIds);

    // The game row behind each of these otomo account ids. Rows this store has
    // never seen are absent from the answer rather than present with empty fields, because "not a player here" and
    // "a player with no name" are different answers.
    IReadOnlyList<OtomoPlayer> FindOtomoPlayers(IReadOnlyCollection<string> otomoPlayerIds);

    // The same for one game row, or null when that row has no otomo account behind it.
    OtomoPlayer? FindOtomoPlayer(long playerId);
}
