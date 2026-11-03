using System.Text.Json;

namespace KickFlight.BootstrapApi.Otomo;

// What a disc level-up costs, read the way the client reads it.
//
// The table is the served DiscBuildup master (config/masters_disc_buildup.json): one row per (rarityType, level) with
// necessaryDiscForceAmount and totalDiscAmount. The client prices its own build-up button from it in
// DiscParameterUtil.CalcBuildupParameter (0x1638624), and the server has to charge exactly that, or the button and
// the answer disagree. Read off the binary, the client finds three rows of the disc's rarity - level - 1, level and
// level + 1 - and:
//
//   * the price of going from L to L + 1 is row L + 1: its necessaryDiscForceAmount is EnhanceCost, and the disc's
//     amount has to reach its totalDiscAmount. CalcAmount (0x16388EC) and CalcMaxAmount (0x1638908) draw the bar as
//     amount - total(L) out of total(L + 1) - total(L), and as amount out of total(L + 1) at the start level, where
//     there is no row below;
//   * totalDiscAmount is therefore cumulative - the copies a disc has collected over its life, not the copies one
//     step takes - and the highest level among the rarity's rows is the cap (BuildupParameter.MaxLevel).
//
// Economy holds the copies the player still has, and a level-up spends total(L + 1) - total(L) of them. Since the
// client's amount is the cumulative count, a disc is served as held + total(level) - total(start level): the copies
// it has, plus the ones its levels already used. That keeps the client's bar and its affordability check exact after
// a spend. It is derived from the save's level and this table on every read, never stored, so there is no second
// counter to drift from economy's.
//
// The base copy is never spent. Every start row has totalDiscAmount 1, which is simply owning the disc, so a step that
// needs N duplicates needs N + 1 held - otherwise the level-up would delete the disc it raised.
public sealed class EconomyDiscLevels
{
    // One level-up: the level it reaches, the Disc Force it costs and the duplicate copies it spends (0 is a real
    // answer - several steps cost Disc Force alone).
    public sealed record Step(int TargetLevel, int DiscForce, int Copies);

    public static EconomyDiscLevels Empty { get; } = new([]);

    private readonly Dictionary<(int Rarity, int Level), (int DiscForce, int TotalDiscs)> _rows;

    private EconomyDiscLevels(Dictionary<(int Rarity, int Level), (int DiscForce, int TotalDiscs)> rows) => _rows = rows;

    public int Count => _rows.Count;

    // The served JSON, as the client deserializes it. A row the client could not use either (a missing column) is
    // skipped and logged rather than read as zero, because a zero price is a free level.
    public static EconomyDiscLevels Parse(string json, ILogger logger)
    {
        var rows = new Dictionary<(int, int), (int, int)>();
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("rarityType", out var rarity) || !rarity.TryGetInt32(out var rarityType) ||
                    !row.TryGetProperty("level", out var level) || !level.TryGetInt32(out var levelValue) ||
                    !row.TryGetProperty("necessaryDiscForceAmount", out var force) || !force.TryGetInt32(out var forceValue) ||
                    !row.TryGetProperty("totalDiscAmount", out var total) || !total.TryGetInt32(out var totalValue))
                {
                    logger.LogWarning("DiscBuildup row {Row} is missing a column; it is not used to price a level-up",
                        row.GetRawText());
                    continue;
                }
                rows[(rarityType, levelValue)] = (forceValue, totalValue);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            logger.LogError("DiscBuildup master is not readable ({Error}); every level-up will be refused", exception.Message);
        }
        return new EconomyDiscLevels(rows);
    }

    // The next level-up of a disc of this rarity at this level, or null when there is none: the disc is at the cap,
    // or the table has no row for its level (a start level the tuning moved off the master), which is refused
    // rather than priced from a guess.
    public Step? Next(int rarityType, int level)
    {
        if (!_rows.TryGetValue((rarityType, level), out var current) ||
            !_rows.TryGetValue((rarityType, level + 1), out var next))
        {
            return null;
        }
        return new Step(level + 1, next.DiscForce, Math.Max(0, next.TotalDiscs - current.TotalDiscs));
    }

    // The highest level the rarity's rows reach, which is where the client stops offering the button.
    public int MaxLevel(int rarityType) =>
        _rows.Keys.Where(row => row.Rarity == rarityType).Select(row => row.Level).DefaultIfEmpty(0).Max();

    // A row's cumulative copy count, or null when the table has no such row.
    public int? TotalDiscs(int rarityType, int level) =>
        _rows.TryGetValue((rarityType, level), out var row) ? row.TotalDiscs : null;

    // The level the client itself puts a disc at when it builds one with no server row behind it - the tutorial's
    // placeholder discs (TutorialUtil.GetUserDiscListInfo). CommonUtil.GetDefaultDiscLevel (0x31EFD50) is
    // "rarity + 1 for rarities 1 to 3, else 1", which is also where every rarity's rows start in the served table.
    public static int ClientDefaultLevel(int rarityType) => rarityType is >= 1 and <= 3 ? rarityType + 1 : 1;

    // How many copies a disc's levels have spent since its start level: what the client counts back into its amount.
    public int CopiesSpent(int rarityType, int level, int startLevel)
    {
        if (level <= startLevel) return 0;
        if (!_rows.TryGetValue((rarityType, level), out var current) ||
            !_rows.TryGetValue((rarityType, startLevel), out var start))
        {
            return 0;
        }
        return Math.Max(0, current.TotalDiscs - start.TotalDiscs);
    }

    // The consume key: one per disc reaching one level, so a retry of the same level-up - the adapter's own resend,
    // or two taps that both read the old level - is economy's replay and spends once.
    public static string Key(string playerId, int discId, int targetLevel) =>
        $"level-up:{playerId}:{discId}:{targetLevel}";

    // The advance key: the same fact under a different name. Economy keys idempotency per player across every
    // operation, so a key shared with the consume would be answered as that consume's replay instead of moving the
    // level; two names keep both halves of one level-up retryable on their own.
    public static string AdvanceKey(string playerId, int discId, int targetLevel) =>
        $"level-up-advance:{playerId}:{discId}:{targetLevel}";

    // The attrs field a disc's level lives in. Economy's schema is closed - level, counter, available_at - and an
    // advance names the field it moves rather than implying it.
    public const string LevelField = "level";
}
