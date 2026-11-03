namespace KickFlight.BootstrapApi;

// One player's mutable state, as a single document.
//
// This is the unit the API loads at the start of a request and saves at the end of one that changed
// something; there is no partial update. It used to live nested inside DemoSessionApi and be persisted one
// JSON file per user under <content root>/data/users, which is still the fallback (PlayerStore.JsonPlayerStore);
// the deployment persists it in PostgreSQL instead (PlayerStore.PostgresPlayerStore).
public sealed class SessionState
{
    // The player id, as a string. It is numeric in practice - the client parses it back with
    // long.TryParse and renders "Player NNNN" from it - and it is the primary key in either store, so it is
    // assigned once and never changes. Use PlayerId for the typed form.
    public string UserId { get; set; } = "";
    // Empty until the player picks one.
    //
    // This used to default to "Gixarde3" and be overwritten at authentication with an invented
    // "Player NNNN", which is why every account looked anonymous: there was nothing durable to read a real
    // name from. An empty name is now meaningful - it is what puts the client on the name-entry window
    // (see the tutorialProgressStatus handling in BuildStartupJson) - so it must not be defaulted here.
    public string UserName { get; set; } = "";
    public int KickerId { get; set; } = 1;
    // The honor (badge) the player currently displays. The default honor is also owned by every new account.
    public int HonorId { get; set; } = BadgeAwardRules.DefaultHonorId;
    // Persisted ownership and lifetime facts used by the badge rules the server can verify.
    public List<int> UserHonorIds { get; set; } = [BadgeAwardRules.DefaultHonorId];
    public int VerifiedBattleCount { get; set; }
    // A battle result is retried by clients. Keep the opaque issued entry IDs so a retry cannot award twice.
    public HashSet<string> ProcessedBadgeBattleEntryIds { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<int>> ProcessedBadgeBattleAwards { get; set; } = new(StringComparer.Ordinal);
    // Distinct server UTC calendar dates on which this account reached authenticated startup.
    public List<string> VerifiedLoginDays { get; set; } = [];
    // Count of successful server-side disc level increases, not attempted/capped requests.
    public int DiscUpgradeCount { get; set; }
    // Client-reported-but-bounded battle counters (wins, kills, MVPs, ...) for the stat badges. See BadgeStatState.
    public BadgeStatState BadgeStats { get; set; } = new();
    // Kicker 1's first costume. Costume ids are the KickerCostume master's composite row ids (2|kicker|
    // costume|variant), not a sequential index: kicker 1 / costume 1 is 2010101, which is also the id the
    // client hardcodes for the tutorial's placeholder player (TutorialUtil.KICKER_COSTUME_ID). A fresh
    // account starts on it, and NormalizeCostume repairs a save that still carries an older numbering.
    public int KickerCostumeId { get; set; } = 2010101;
    // The decks, the active deck, the gear in each costume's slots and a pending gear roll are not in this document:
    // they are the ext of the player's session card (DemoSessionApi.Loadout.cs, Otomo/Loadout.cs).
    // The discs this player owns, as main's model holds them (discId -> level/amount). Our economy owns
    // possession, so nothing on this side reads it any more: it stays in the document because main's store and
    // gacha code load and save it, and a merge must not delete a model member its code still touches.
    public Dictionary<int, UserDiscState> Discs { get; set; } = new();
    // The four legacy item counters, likewise main's: the columns are dropped by migration 5 and economy serves
    // what the client renders, so these are only the document's mirror of a save written before that move.
    public int ItemJetCoins { get; set; } = 208754;
    public int ItemPaidJetCoins { get; set; } = 10000;
    public int ItemDiscForce { get; set; } = 999999;
    public int ItemKickPoints { get; set; } = 50000;
    // Item master ids 5-8: gear stamps (202), disc fragments (303) and the two gacha tickets (401/402). An
    // absent entry reads as DefaultStockAmount, so a session saved before round C starts with a full stock.
    public Dictionary<int, int> ItemAmounts { get; set; } = new();
    // The furthest TutorialProgressStatus the client has reported through a /tutorial/* step. 0 = none yet, which
    // a fresh account reads as Start (1); the name ends it at 207 regardless (see DemoSessionApi.TutorialStatus).
    public int TutorialProgress { get; set; }

    // The disc levels used to live here, as `discId -> level`. They do not any more: a level is attrs.level on the
    // disc's own possession in economy's inventory, raised by POST /internal/economy/items/advance, and every read of
    // it - the disc list, a build-up, a ranked deck's fight levels - goes there. A save written before that move still
    // carries the old key; it is ignored on read and gone on the next write.

    // discId -> the x-app-response-cache-id of the last /disc/buildup that raised it. The client sends the same id
    // again when it retries a request whose answer it lost, so a match is that retry and is answered without a
    // second spend (DemoSessionApi.HandleDiscBuildupAsync).
    public Dictionary<int, string> DiscBuildupRequests { get; set; } = new();

    // True once economy has paid this account's starter Disc Force (class starter.disc_force). The grant is keyed
    // per player, so this only saves the call on later startups; a lost flag pays nothing twice.
    public bool StarterDiscForcePaid { get; set; }

    // Capsules are not here either: a battle's capsule is an entitlement in economy, and the home's slots are built from the
    // open ones (DemoSessionApi.Capsules.cs).

    // True once the player has chosen a name, which is also what ends onboarding.
    public bool HasName => !string.IsNullOrEmpty(UserName);

    // The primary key as a number. 0 means UserId was not numeric, which no store should ever see.
    public long PlayerId => long.TryParse(UserId, out var id) ? id : 0;
}

public sealed class UserDiscState
{
    public int DiscId { get; set; }
    public int Level { get; set; } = 10;
    public int Amount { get; set; } = 99;
}
