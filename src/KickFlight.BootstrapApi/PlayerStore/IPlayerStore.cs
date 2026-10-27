namespace KickFlight.BootstrapApi.PlayerStore;

// A player's standing in one battle rule. Rank is what the client renders as a league badge.
public sealed record RankState(int BattlePoint, int Rank);

// Public data used by profile cards and social/ranking lists. An unset display name stays empty; callers must
// not invent a player identity for an ID that has no persisted profile.
public sealed record PlayerProfile(long PlayerId, string DisplayName, int KickerId, int KickerCostumeId);

// A persisted follow edge. IsNew is meaningful for followers (incoming edges) and false for following rows.
public sealed record FollowedProfile(PlayerProfile Profile, DateTimeOffset FollowedAt, bool IsNew);

// Position is one-based in the complete named-player ranking for this battle rule, including when the query is
// filtered to a set of followed players.
public sealed record RankedPlayer(PlayerProfile Profile, int BattlePoint, int Rank, long Position);

// Where game-side player state lives.
//
// Two implementations, chosen by whether a connection string is configured (see PlayerStoreFactory):
// PostgresPlayerStore for the deployment, JsonPlayerStore for local runs and the test suite, which must not
// need a database to be reachable.
//
// Identity is not here. otomo owns every account: it decides who a device is, issues the tokens, and revokes them.
// This store keeps what a player has in *this game* - the save document, ranks, follows - and the one thing the
// KF protocol forces it to keep, the numeric id under which that state is filed (see ResolveGamePlayer).
//
// Everything here is deliberately synchronous. The callers are the existing synchronous request handlers,
// reached from ~13 call sites, and Npgsql has first-class sync methods; making this async would convert a
// large part of DemoSessionApi for no benefit at this scale.
public interface IPlayerStore
{
    // The numeric row id for an otomo account, minted once and stable thereafter.
    //
    // The number is not an account: it is the handle the KF client insists on, because it parses x-app-user-id
    // with long.TryParse and renders "Player NNNN" from it. Nothing is authenticated by it - the signed access
    // token carries the otomo identity, and this is looked up from there.
    //
    // legacyDeviceUuid is the device id players were keyed by before otomo owned identity. A row with no otomo id
    // that carries that device is adopted by the account presenting it, so an existing player keeps their name,
    // rank, inventory and friends instead of starting over.
    long ResolveGamePlayer(string otomoPlayerId, string legacyDeviceUuid);

    // The player's state, or null if that id has never been seen.
    SessionState? TryLoad(long playerId);

    // Upsert of the state document and its columns.
    void Save(SessionState state);

    // Rank per battle rule type. Absent rows read as the starting rank, so a new player needs no seeding.
    RankState LoadRank(long playerId, int battleRuleType);

    void SaveRank(long playerId, int battleRuleType, RankState rank);

    // Whether another player already goes by this name. Case-insensitive, because a name is how a player is
    // identified on the ranking and profile screens, where "Tanuki" and "tanuki" reading as two people is a bug
    // rather than a feature. `exceptPlayerId` is the caller, so re-submitting one's own name is not a clash.
    bool IsNameTaken(string name, long exceptPlayerId);

    // Social graph. Edges are directed: followerId follows followedId. Repeated add/remove calls are safe.
    PlayerProfile? FindProfile(long playerId);
    IReadOnlyList<PlayerProfile> FindProfiles(IReadOnlyCollection<long> playerIds);
    IReadOnlyList<long> FindFollowedIds(long followerId, IReadOnlyCollection<long> candidateIds);
    void AddFollow(long followerId, long followedId);
    void RemoveFollow(long followerId, long followedId);
    int CountFollowing(long playerId);
    int CountFollowers(long playerId);
    IReadOnlyList<FollowedProfile> ListFollowing(long playerId, int offset, int limit);
    IReadOnlyList<FollowedProfile> ListFollowers(long playerId, int offset, int limit);
    int CountNewFollowers(long playerId);
    void MarkFollowersRead(long playerId, IReadOnlyCollection<long> followerIds);

    // Explicit real-friend invitations are a separate symmetric relation from following.
    string GetOrCreateRealFriendToken(long playerId);
    long? TryGetRealFriendTokenOwner(string token);
    void AddRealFriend(long firstPlayerId, long secondPlayerId);
    bool AreRealFriends(long firstPlayerId, long secondPlayerId);

    // A null playerIds selects the complete named-player ranking. Filtered results retain their global position.
    IReadOnlyList<RankedPlayer> ListRanks(
        int battleRuleType,
        IReadOnlyCollection<long>? playerIds,
        int offset,
        int limit);
}
