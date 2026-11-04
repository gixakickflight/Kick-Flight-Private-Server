using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KickFlight.BootstrapApi.Otomo;

// A player's loadout as the adapter uses it, and its translation to and from session's two stores for it. The adapter
// keeps none of it: it is read from session on each request that needs it and written back with the player's own token.
//
// Session's Loadout (the tenant's session.loadout, schema version 1) holds what the player builds:
//
//   schema_version  1
//   decks           { "<deck number>": [disc id, ...] }          the player's decks (<= 8 unique positive disc ids each)
//   costume_gears   { "<costume id>": [gear, gear, gear] }        three gear slots per costume, 0 = empty
//   active_deck     integer 1..16                                 the deck number the player fights with; a key of decks
//   revision        integer                                       session's, on every document it returns (not sent back)
//
// and the server-decided gear roll stays in ext (ext.session.player), because the player does not write it:
//
//   pending_gear    { "kicker_id": int, "gear_id": int }          the gear rolled by /gear/create and not yet equipped or
//                                                                 discarded; gear_id 0 means nothing is pending
public sealed record GearRoll(int KickerId, int GearId)
{
    public static GearRoll None { get; } = new(0, 0);
}

public sealed class Loadout
{
    public const int SchemaVersion = 1;
    public const string GearRollField = "pending_gear";

    // The fight deck of an account whose active deck names no deck at all: the first starter deck.
    public static readonly IReadOnlyList<int> InitialDeck = [3010001, 3010002, 3010003, 3010004];

    // What a new player starts with (today's defaults, decks 1-5), written once with If-Match: 0 when the first read finds
    // no loadout.
    public static IReadOnlyDictionary<int, List<int>> DefaultDecks() => new Dictionary<int, List<int>>
    {
        [1] = [3010001, 3010002, 3010003, 3010004],
        [2] = [3010005, 3010006, 3010007, 3010008],
        [3] = [3010009, 3010010, 3010011, 3010012],
        [4] = [3010013, 3010014, 3010020, 3010022],
        [5] = [3010023, 3010024, 3010029, 3010030]
    };

    public Loadout(int revision, int activeDeck, IDictionary<int, List<int>> decks, IDictionary<int, int[]> costumeGears,
        GearRoll pending)
    {
        Revision = revision;
        ActiveDeck = activeDeck;
        DeckSets = new Dictionary<int, List<int>>(decks.Select(pair => KeyValuePair.Create(pair.Key, new List<int>(pair.Value))));
        GearsByCostume = new Dictionary<int, int[]>(costumeGears.Select(pair => KeyValuePair.Create(pair.Key, (int[])pair.Value.Clone())));
        Roll = pending;
    }

    // The revision session held when this was read: what a save of it names in If-Match. 0 is "never saved".
    public int Revision { get; }
    public int ActiveDeck { get; set; }
    public Dictionary<int, List<int>> DeckSets { get; }
    public Dictionary<int, int[]> GearsByCostume { get; }
    public GearRoll Roll { get; }

    // A copy to edit: the original stays what session returned, so a retry can start again from a new read.
    public Loadout Copy() => new(Revision, ActiveDeck, DeckSets, GearsByCostume, Roll);

    // The loadout of a player who has never saved one, as the starting point of a first save (revision 0).
    public static Loadout Starter(GearRoll pending) => new(0, 1, DefaultDecks().ToDictionary(), new Dictionary<int, int[]>(), pending);

    // The loadout of another player who has saved none: no decks, so every reader falls back to the initial deck.
    public static Loadout Empty { get; } = new(0, 1, new Dictionary<int, List<int>>(), new Dictionary<int, int[]>(), GearRoll.None);

    // The disc ids of the deck the player fights with. A missing deck is the initial one, never an empty deck.
    public List<int> ActiveDeckDiscs() =>
        DeckSets.TryGetValue(ActiveDeck, out var deck) && deck.Count > 0 ? [.. deck] : [.. InitialDeck];

    // Three slots for a costume, always: an unequipped costume reads as three empty slots, the shape the client reads
    // back as gearId1..3.
    public int[] GearSlots(int costumeId)
    {
        var slots = new int[3];
        if (GearsByCostume.TryGetValue(costumeId, out var stored))
            Array.Copy(stored, slots, Math.Min(stored.Length, slots.Length));
        return slots;
    }

    // --- session's documents -----------------------------------------------------------------------------------------

    // A loadout document as session returned it, with the roll from the player's ext (None for another player's).
    public static Loadout FromSession(SessionLoadout stored, GearRoll pending)
    {
        var decks = new Dictionary<int, List<int>>();
        var gears = new Dictionary<int, int[]>();
        var active = 1;
        var root = stored.Document;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("decks", out var decksElement) && decksElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var deck in decksElement.EnumerateObject())
                {
                    if (!int.TryParse(deck.Name, out var number) || deck.Value.ValueKind != JsonValueKind.Array) continue;
                    decks[number] = deck.Value.EnumerateArray().Where(id => id.TryGetInt32(out _))
                        .Select(id => id.GetInt32()).ToList();
                }
            }
            if (root.TryGetProperty("active_deck", out var activeElement) && activeElement.TryGetInt32(out var number1))
                active = number1;
            if (root.TryGetProperty("costume_gears", out var gearsElement) && gearsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var costume in gearsElement.EnumerateObject())
                {
                    if (!int.TryParse(costume.Name, out var costumeId) || costume.Value.ValueKind != JsonValueKind.Array) continue;
                    var slots = new int[3];
                    var index = 0;
                    foreach (var gear in costume.Value.EnumerateArray())
                    {
                        if (index == slots.Length) break;
                        slots[index++] = gear.TryGetInt32(out var id) ? id : 0;
                    }
                    gears[costumeId] = slots;
                }
            }
        }
        return new Loadout(stored.Revision, active, decks, gears, pending);
    }

    // The document a save sends: the whole loadout, without `revision` (session owns it; the revision travels in If-Match).
    public JsonObject ToDocument()
    {
        var decks = new JsonObject();
        foreach (var (number, discs) in DeckSets.OrderBy(pair => pair.Key))
            decks[number.ToString(CultureInfo.InvariantCulture)] =
                new JsonArray(discs.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        var gears = new JsonObject();
        foreach (var (costumeId, slots) in GearsByCostume.OrderBy(pair => pair.Key))
        {
            var three = new int[3];
            Array.Copy(slots, three, Math.Min(slots.Length, three.Length));
            gears[costumeId.ToString(CultureInfo.InvariantCulture)] =
                new JsonArray(three.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        }
        return new JsonObject
        {
            ["schema_version"] = SchemaVersion,
            ["decks"] = decks,
            ["costume_gears"] = gears,
            ["active_deck"] = ActiveDeck
        };
    }

    // --- the gear roll, which stays in ext ----------------------------------------------------------------------------

    public static GearRoll RollFromExt(JsonElement? ext)
    {
        if (ext is { ValueKind: JsonValueKind.Object } root &&
            root.TryGetProperty(GearRollField, out var pendingElement) && pendingElement.ValueKind == JsonValueKind.Object)
        {
            return new GearRoll(
                pendingElement.TryGetProperty("kicker_id", out var kicker) && kicker.TryGetInt32(out var kickerId) ? kickerId : 0,
                pendingElement.TryGetProperty("gear_id", out var gear2) && gear2.TryGetInt32(out var gearId) ? gearId : 0);
        }
        return GearRoll.None;
    }

    public static JsonObject RollToExt(GearRoll pending) => new()
    {
        ["kicker_id"] = pending.KickerId,
        ["gear_id"] = pending.GearId
    };
}
