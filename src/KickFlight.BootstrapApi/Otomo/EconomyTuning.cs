using System.Globalization;
using System.Text.Json;

namespace KickFlight.BootstrapApi.Otomo;

// The adapter's own numbers about the economy: which capsule a battle hands out, how long it takes to open, how
// many discs it holds, and what level a disc fights at.
//
// None of this is a value. A capsule's *contents* are economy's (the capsule.<type> pool), and so is the token that
// pays for opening it (the match.capsule.<type> class); this file only decides which of the three a battle earns and
// when the slot may be opened, which is game presentation the economy has no opinion on. That is why it is a file
// the adapter reads rather than an economy document.
//
// The file is economy-tuning.json (Otomo:Economy:TuningFile, resolved against the repository root). The published
// values are also the defaults below, so a deployment without the file behaves exactly as the published one; a file
// that is present but unreadable is logged as an error and the defaults are used, because refusing every battle
// result over a capsule timer would be the wrong failure.
//
//   {
//     "capsules": [
//       { "type": "wooden",   "capsuleId": 5010003, "weight": 60, "unlock": "00:15:00", "discs": { "min": 1, "max": 1 } },
//       { "type": "standard", "capsuleId": 5010001, "weight": 30, "unlock": "01:00:00", "discs": { "min": 1, "max": 2 } },
//       { "type": "golden",   "capsuleId": 5010002, "weight": 10, "unlock": "04:00:00", "discs": { "min": 2, "max": 3 } }
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
    // match.capsule.<type> class and the capsule.<type> pool - and CapsuleId is the client's Capsule master row.
    public sealed record CapsuleType(string Type, int CapsuleId, int Weight, TimeSpan Unlock, int MinDiscs, int MaxDiscs)
    {
        public string GrantClass => $"match.capsule.{Type}";
        public string PoolId => $"capsule.{Type}";
    }

    public IReadOnlyList<CapsuleType> Capsules { get; }

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
            new CapsuleType("wooden", 5010003, 60, TimeSpan.FromMinutes(15), 1, 1),
            new CapsuleType("standard", 5010001, 30, TimeSpan.FromHours(1), 1, 2),
            new CapsuleType("golden", 5010002, 10, TimeSpan.FromHours(4), 2, 3)
        ],
        casualDiscLevel: 5,
        levelsRankedOnly: true,
        new Dictionary<int, int> { [0] = 1, [1] = 2, [2] = 3, [3] = 4 },
        "defaults");

    private EconomyTuning(IReadOnlyList<CapsuleType> capsules, int casualDiscLevel, bool levelsRankedOnly,
        IReadOnlyDictionary<int, int> startLevels, string source)
    {
        Capsules = capsules;
        CasualDiscLevel = casualDiscLevel;
        LevelsRankedOnly = levelsRankedOnly;
        StartLevels = startLevels;
        Source = source;
    }

    public CapsuleType? FindCapsule(string? type) =>
        Capsules.FirstOrDefault(capsule => string.Equals(capsule.Type, type, StringComparison.Ordinal));

    // The level a disc of this rarity has before the player has raised it.
    public int StartLevel(int rarityType) => StartLevels.TryGetValue(rarityType, out var level) ? level : 1;

    // A weighted draw over the capsule types, then a disc count inside that type's range. The randomness is this
    // process's, and that is fine: what is decided here is which token a battle pays, and the token is all economy
    // ever sees. The draws inside the capsule are economy's own.
    public (CapsuleType Type, int DiscCount) RollCapsule(Random random)
    {
        var total = Capsules.Sum(capsule => Math.Max(0, capsule.Weight));
        var pick = total > 0 ? random.Next(total) : 0;
        var chosen = Capsules[0];
        foreach (var capsule in Capsules)
        {
            var weight = Math.Max(0, capsule.Weight);
            if (pick < weight)
            {
                chosen = capsule;
                break;
            }
            pick -= weight;
        }
        return (chosen, random.Next(chosen.MinDiscs, chosen.MaxDiscs + 1));
    }

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
                string.Join(", ", tuning.Capsules.Select(c => $"{c.Type} {c.Weight}% {c.Unlock} {c.MinDiscs}-{c.MaxDiscs}")),
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

    // Strict on the parts that decide a payout (a capsule must name a type, a client id and a sane range) and lenient
    // on the rest, which fall back to the defaults one field at a time.
    public static EconomyTuning Parse(string json, string source)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var capsules = Defaults.Capsules;
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
                var weight = row.GetProperty("weight").GetInt32();
                var unlock = TimeSpan.Parse(row.GetProperty("unlock").GetString() ?? "", CultureInfo.InvariantCulture);
                var discs = row.GetProperty("discs");
                var min = discs.GetProperty("min").GetInt32();
                var max = discs.GetProperty("max").GetInt32();
                if (weight < 0 || unlock < TimeSpan.Zero || min < 1 || max < min)
                    throw new FormatException($"capsule '{type}' has a negative weight or timer, or an empty disc range");
                parsed.Add(new CapsuleType(type, capsuleId, weight, unlock, min, max));
            }
            if (parsed.Count == 0) throw new FormatException("capsules is empty");
            if (parsed.Sum(capsule => capsule.Weight) <= 0) throw new FormatException("capsule weights sum to zero");
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
