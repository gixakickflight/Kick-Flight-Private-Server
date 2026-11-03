using System.Text.Json;
using System.Text.Json.Nodes;

namespace KickFlight.BootstrapApi.Otomo;

// A player's loadout as the adapter uses it, and its translation to and from the `ext` of the player's session card
// (ext.session.player, published by the KF tenant). The adapter keeps none of it: it is read from session on each request
// that needs it and written back through session's internal ext route.
//
// The ext fields, by name:
//
//   decks          { "<deck number>": [disc id, ...] }          the player's decks (<= 8 unique positive disc ids each)
//   active_deck    integer                                       the deck number the player fights with
//   costume_gears  { "<costume id>": [gear, gear, gear] }        three gear slots per costume, 0 = empty
//   pending_gear   { "kicker_id": int, "gear_id": int }          the gear rolled by /gear/create and not yet equipped or
//                                                                discarded; gear_id 0 means nothing is pending
//
// `active_deck` and `pending_gear` are not in the schema the tenant published first (only `decks` and `costume_gears`
// are); the additions are written out in the change's report and the code, tests and fakes assume them.
public sealed record GearRoll(int KickerId, int GearId)
{
    public static GearRoll None { get; } = new(0, 0);
}

public sealed class Loadout
{
    public const string DecksField = "decks";
    public const string ActiveDeckField = "active_deck";
    public const string GearSlotsField = "costume_gears";
    public const string GearRollField = "pending_gear";

    // The fight deck of an account whose active deck names no deck at all: the first starter deck.
    public static readonly IReadOnlyList<int> InitialDeck = [3010001, 3010002, 3010003, 3010004];

    // What a new player starts with (today's defaults, decks 1-5), written once to ext when the first read finds none.
    public static IReadOnlyDictionary<int, List<int>> DefaultDecks() => new Dictionary<int, List<int>>
    {
        [1] = [3010001, 3010002, 3010003, 3010004],
        [2] = [3010005, 3010006, 3010007, 3010008],
        [3] = [3010009, 3010010, 3010011, 3010012],
        [4] = [3010013, 3010014, 3010020, 3010022],
        [5] = [3010023, 3010024, 3010029, 3010030]
    };

    public Loadout(int activeDeck, IDictionary<int, List<int>> decks, IDictionary<int, int[]> costumeGears,
        GearRoll pending, bool hasDecks)
    {
        ActiveDeck = activeDeck;
        DeckSets = new Dictionary<int, List<int>>(decks);
        GearsByCostume = new Dictionary<int, int[]>(costumeGears);
        Roll = pending;
        HasDecks = hasDecks;
    }

    public int ActiveDeck { get; }
    public Dictionary<int, List<int>> DeckSets { get; }
    public Dictionary<int, int[]> GearsByCostume { get; }
    public GearRoll Roll { get; }

    // False when the ext carried no `decks` at all, which is a player session has never been told about.
    public bool HasDecks { get; }

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

    public static Loadout FromExt(JsonElement? ext)
    {
        var decks = new Dictionary<int, List<int>>();
        var gears = new Dictionary<int, int[]>();
        var active = 1;
        var pending = GearRoll.None;
        var hasDecks = false;

        if (ext is { ValueKind: JsonValueKind.Object } root)
        {
            if (root.TryGetProperty(DecksField, out var decksElement) && decksElement.ValueKind == JsonValueKind.Object)
            {
                hasDecks = true;
                foreach (var deck in decksElement.EnumerateObject())
                {
                    if (!int.TryParse(deck.Name, out var number) || deck.Value.ValueKind != JsonValueKind.Array) continue;
                    decks[number] = deck.Value.EnumerateArray().Where(id => id.TryGetInt32(out _))
                        .Select(id => id.GetInt32()).ToList();
                }
            }
            if (root.TryGetProperty(ActiveDeckField, out var activeElement) && activeElement.TryGetInt32(out var number1))
                active = number1;
            if (root.TryGetProperty(GearSlotsField, out var gearsElement) && gearsElement.ValueKind == JsonValueKind.Object)
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
            if (root.TryGetProperty(GearRollField, out var pendingElement) && pendingElement.ValueKind == JsonValueKind.Object)
            {
                pending = new GearRoll(
                    pendingElement.TryGetProperty("kicker_id", out var kicker) && kicker.TryGetInt32(out var kickerId) ? kickerId : 0,
                    pendingElement.TryGetProperty("gear_id", out var gear2) && gear2.TryGetInt32(out var gearId) ? gearId : 0);
            }
        }
        return new Loadout(active, decks, gears, pending, hasDecks);
    }

    // --- ext fragments, for a shallow-merge write ---------------------------------------------------------------------

    public static JsonObject DecksToExt(IEnumerable<KeyValuePair<int, List<int>>> decks)
    {
        var result = new JsonObject();
        foreach (var (number, discs) in decks.OrderBy(pair => pair.Key))
            result[number.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                new JsonArray(discs.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        return result;
    }

    public static JsonObject GearSlotsToExt(IEnumerable<KeyValuePair<int, int[]>> gears)
    {
        var result = new JsonObject();
        foreach (var (costumeId, slots) in gears.OrderBy(pair => pair.Key))
        {
            var three = new int[3];
            Array.Copy(slots, three, Math.Min(slots.Length, three.Length));
            result[costumeId.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                new JsonArray(three.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        }
        return result;
    }

    public static JsonObject RollToExt(GearRoll pending) => new()
    {
        ["kicker_id"] = pending.KickerId,
        ["gear_id"] = pending.GearId
    };
}
