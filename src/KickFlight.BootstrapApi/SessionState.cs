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
    // FCM registration token the client reported through POST /push/register, used for the "someone is searching
    // ranked" push (Push/). Null until the client registers; cleared when FCM reports it dead or another player
    // registers the same token (one device, one account). Never log it in full.
    public string? PushToken { get; set; }
    public DateTime? PushTokenUpdatedAtUtc { get; set; }
    public string? PushPlatform { get; set; }
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
    public int ActiveDeckNumber { get; set; } = 1;
    public Dictionary<int, List<int>> Decks { get; set; } = new()
    {
        [1] = [3010001, 3010002, 3010003, 3010004],
        [2] = [3010005, 3010006, 3010007, 3010008],
        [3] = [3010009, 3010010, 3010011, 3010012],
        [4] = [3010013, 3010014, 3010020, 3010022],
        [5] = [3010023, 3010024, 3010029, 3010030]
    };
    public Dictionary<int, UserDiscState> Discs { get; set; } = new();
    public int ItemJetCoins { get; set; } = 208754;
    public int ItemPaidJetCoins { get; set; } = 10000;
    public int ItemDiscForce { get; set; } = 999999;
    public int ItemKickPoints { get; set; } = 50000;
    // Item master ids 5-8: gear stamps (202), disc fragments (303) and the two gacha tickets (401/402). An
    // absent entry reads as DefaultStockAmount, so a session saved before round C starts with a full stock.
    public Dictionary<int, int> ItemAmounts { get; set; } = new();
    // costume row id -> three gear slots (0 = empty), the shape the client reads back as gearId1..3.
    public Dictionary<int, int[]> CostumeGears { get; set; } = new();
    // The gear rolled by gear/create and not yet equipped or discarded. gearId 0 = nothing pending.
    public PendingGearState PendingGear { get; set; } = new();

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

public sealed class PendingGearState
{
    public int KickerId { get; set; }
    public int GearId { get; set; }
}
