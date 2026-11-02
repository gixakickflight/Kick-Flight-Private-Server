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
    // costume row id -> three gear slots (0 = empty), the shape the client reads back as gearId1..3.
    public Dictionary<int, int[]> CostumeGears { get; set; } = new();
    // The gear rolled by gear/create and not yet equipped or discarded. gearId 0 = nothing pending.
    public PendingGearState PendingGear { get; set; } = new();

    // True once the player has chosen a name, which is also what ends onboarding.
    public bool HasName => !string.IsNullOrEmpty(UserName);

    // The primary key as a number. 0 means UserId was not numeric, which no store should ever see.
    public long PlayerId => long.TryParse(UserId, out var id) ? id : 0;
}

public sealed class PendingGearState
{
    public int KickerId { get; set; }
    public int GearId { get; set; }
}
