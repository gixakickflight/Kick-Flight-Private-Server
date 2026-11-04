using System.Text.Json;

namespace KickFlight.BootstrapApi.Otomo;

// The adapter's own numbers about the economy: the client's id for each capsule type, and what level a disc fights at.
//
// None of this is a value or a rule. When a capsule is awarded, which type and how many discs, and how long it waits are
// the tenant's documents (economy.rules: the capsule kind and its weighted entitlement trigger), and its contents are
// economy's capsule.<type> pool. What stays here is a translation: type -> the client's Capsule master id.
//
// The file is economy-tuning.json (Otomo:Economy:TuningFile, resolved against the repository root). The published
// values are also the defaults below, so a deployment without the file behaves exactly as the published one; a file
// that is present but unreadable is logged as an error and the defaults are used. A file written for an earlier shape
// may still carry "weight", "unlock" and "discs" per capsule: they are ignored, the tenant's documents own them now.
//
//   {
//     "capsules": [
//       { "type": "wooden",   "capsuleId": 5010003 },
//       { "type": "standard", "capsuleId": 5010001 },
//       { "type": "golden",   "capsuleId": 5010002 }
//     ],
//     "casualDiscLevel": 5,
//     "levelsRankedOnly": true,
//     "startLevels": { "n": 1, "r": 2, "sr": 3, "ur": 4 }
//   }
public sealed class EconomyTuning
{
    public const string DefaultPath = "config/economy-tuning.json";

    // The client's own limit: CapsuleSlotUtil.MAX_SLOT_COUNT_NON_PREMIUM_USER. Premium (6) is not sold here.
    public const int CapsuleSlots = 3;

    // One kind of capsule. Type is the name economy uses for it in three places - the capsule_<type> currency, the
    // match.capsule.<type>[.<discs>] class and the capsule.<type> pool - and CapsuleId is the client's Capsule master row.
    public sealed record CapsuleType(string Type, int CapsuleId)
    {
        public string PoolId => $"capsule.{Type}";
        public string TokenCurrency => $"capsule_{Type}";
    }

    public IReadOnlyList<CapsuleType> CapsuleTypes { get; }

    // Every disc in a casual battle fights at this level, whatever the player has raised it to.
    public int CasualDiscLevel { get; }

    // True: a stored level only counts in ranked. It is what "levels ranked-only" in the tuning means, and the only
    // mode implemented; the flag is read so a file that says otherwise is noticed rather than half obeyed.
    public bool LevelsRankedOnly { get; }

    // RarityType (0 N, 1 R, 2 SR, 3 UR) -> the level a disc of that rarity starts at.
    public IReadOnlyDictionary<int, int> StartLevels { get; }

    public string Source { get; }

    public static EconomyTuning Defaults { get; } = new(
        [
            new CapsuleType("wooden", 5010003),
            new CapsuleType("standard", 5010001),
            new CapsuleType("golden", 5010002)
        ],
        casualDiscLevel: 5,
        levelsRankedOnly: true,
        new Dictionary<int, int> { [0] = 1, [1] = 2, [2] = 3, [3] = 4 },
        "defaults");

    private EconomyTuning(IReadOnlyList<CapsuleType> capsules, int casualDiscLevel, bool levelsRankedOnly,
        IReadOnlyDictionary<int, int> startLevels, string source)
    {
        CapsuleTypes = capsules;
        CasualDiscLevel = casualDiscLevel;
        LevelsRankedOnly = levelsRankedOnly;
        StartLevels = startLevels;
        Source = source;
    }

    public CapsuleType? FindCapsule(string? type) =>
        CapsuleTypes.FirstOrDefault(capsule => string.Equals(capsule.Type, type, StringComparison.Ordinal));

    // The level a disc of this rarity has before the player has raised it.
    public int StartLevel(int rarityType) => StartLevels.TryGetValue(rarityType, out var level) ? level : 1;

    public static EconomyTuning Load(string? configuredPath, string contentRoot, ILogger logger)
    {
        string path;
        try
        {
            path = RepositoryPaths.Resolve(string.IsNullOrWhiteSpace(configuredPath) ? DefaultPath : configuredPath,
                contentRoot);
        }
        catch (DirectoryNotFoundException exception)
        {
            logger.LogWarning("Economy tuning: {Error}; using the published defaults", exception.Message);
            return Defaults;
        }

        if (!File.Exists(path))
        {
            logger.LogInformation("Economy tuning: {Path} is not present; using the published defaults", path);
            return Defaults;
        }

        try
        {
            var tuning = Parse(File.ReadAllText(path), path);
            logger.LogInformation(
                "Economy tuning from {Path}: capsules {Capsules}, casual disc level {Casual}",
                path,
                string.Join(", ", tuning.CapsuleTypes.Select(c => $"{c.Type}={c.CapsuleId}")),
                tuning.CasualDiscLevel);
            return tuning;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException
                                              or KeyNotFoundException or IOException)
        {
            logger.LogError("Economy tuning: {Path} is not readable ({Error}); using the published defaults",
                path, exception.Message);
            return Defaults;
        }
    }

    // Strict on the parts that decide a payout (a capsule must name a type and a client id) and lenient
    // on the rest, which fall back to the defaults one field at a time.
    public static EconomyTuning Parse(string json, string source)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var capsules = Defaults.CapsuleTypes;
        if (root.TryGetProperty("capsules", out var capsuleArray) && capsuleArray.ValueKind == JsonValueKind.Array)
        {
            var parsed = new List<CapsuleType>();
            foreach (var row in capsuleArray.EnumerateArray())
            {
                var type = row.GetProperty("type").GetString() ?? "";
                if (type.Length == 0 || !type.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)))
                    throw new FormatException($"capsule type '{type}' is not a lower-case economy name");
                var fallback = Defaults.FindCapsule(type);
                var capsuleId = row.TryGetProperty("capsuleId", out var id) ? id.GetInt32()
                    : fallback?.CapsuleId ?? throw new FormatException($"capsule '{type}' has no capsuleId");
                parsed.Add(new CapsuleType(type, capsuleId));
            }
            if (parsed.Count == 0) throw new FormatException("capsules is empty");
            capsules = parsed;
        }

        var casual = root.TryGetProperty("casualDiscLevel", out var casualLevel)
            ? casualLevel.GetInt32()
            : Defaults.CasualDiscLevel;
        var rankedOnly = !root.TryGetProperty("levelsRankedOnly", out var rankedOnlyFlag) || rankedOnlyFlag.GetBoolean();

        var startLevels = new Dictionary<int, int>(Defaults.StartLevels);
        if (root.TryGetProperty("startLevels", out var levels) && levels.ValueKind == JsonValueKind.Object)
        {
            foreach (var (rarity, rarityType) in new[] { ("n", 0), ("r", 1), ("sr", 2), ("ur", 3) })
            {
                if (levels.TryGetProperty(rarity, out var level)) startLevels[rarityType] = level.GetInt32();
            }
        }

        return new EconomyTuning(capsules, casual, rankedOnly, startLevels, source);
    }
}
