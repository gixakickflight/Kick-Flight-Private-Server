using System.Text.Json;

namespace KickFlight.BootstrapApi.Otomo;

// The ladder session publishes as the `session.rank` document, in the client's own units. This adapter does not hardcode it:
// the thresholds below are read from the release bundle through OtomoMasters (Otomo:Masters:Documents), and when the
// document is not there nothing is served in its place.
//
// The document, as this parser reads it:
//
//   { "rule_types": ["crystal_scramble", "flag_flight", "rapid_ball"],
//     "start_points": 200, "floor": 200, "ceiling": 7500, "outcomes": {"win": 50, "loss": -20},
//     "tiers": [ {"key": "rank_1", "min_points": 200}, {"key": "rank_2", "min_points": 500}, ... ] }
//
// A tier key is always rank_<N>, where N is the client's rank number, and min_points is where the tier begins in
// battle points. The policy fields (start_points, floor, ceiling, rules) are session's and are ignored here, since
// session applies them. `outcomes` is read for one thing only: /battle/end shows the win outcome's value. Anything that does not fit - a key of another shape, a repeated rank, thresholds that do not rise -
// makes the whole document unusable (Parse throws), because a ladder partly understood is a ladder invented.
public sealed class RankLadder
{
    private readonly SortedDictionary<int, int> _minPointsByRank;

    private RankLadder(IReadOnlyList<string> ruleTypes, IReadOnlyDictionary<string, int> outcomes, SortedDictionary<int, int> minPointsByRank)
    {
        RuleTypes = ruleTypes;
        Outcomes = outcomes;
        _minPointsByRank = minPointsByRank;
    }

    public IReadOnlyList<string> RuleTypes { get; }

    // The named outcomes the document declares and what each is worth. Informational: session applies them.
    public IReadOnlyDictionary<string, int> Outcomes { get; }

    // What a win is worth as published, or null when the document declares no `win` outcome.
    public int? WinOutcome => Outcomes.TryGetValue("win", out var win) ? win : null;

    // The rank numbers the ladder names, ascending.
    public IEnumerable<int> Ranks => _minPointsByRank.Keys;

    public bool HasRank(int rank) => _minPointsByRank.ContainsKey(rank);

    // Where a tier begins. This is the BattleRank master's totalBattlePoint column, as published.
    public int MinPoints(int rank) => _minPointsByRank[rank];

    // The rank a point total reaches: the highest tier whose threshold it has passed, or the lowest tier.
    public int RankFor(int points)
    {
        var rank = _minPointsByRank.Keys.First();
        foreach (var (candidate, minPoints) in _minPointsByRank)
        {
            if (minPoints <= points) rank = candidate;
            else break;
        }
        return rank;
    }

    // Battle points still needed to reach the next tier, or 0 at the top of the ladder.
    public int RemainingToNextTier(int battlePoints)
    {
        var next = _minPointsByRank.Values
            .Where(total => total > battlePoints)
            .DefaultIfEmpty(battlePoints)
            .Min();
        return Math.Max(0, next - battlePoints);
    }

    public static RankLadder Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("session.rank is not a JSON object");

        var ruleTypes = new List<string>();
        if (root.TryGetProperty("rule_types", out var rules) && rules.ValueKind == JsonValueKind.Array)
        {
            foreach (var rule in rules.EnumerateArray())
            {
                if (rule.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(rule.GetString()))
                    ruleTypes.Add(rule.GetString()!);
            }
        }

        var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
        if (root.TryGetProperty("outcomes", out var outcomeElement) && outcomeElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var outcome in outcomeElement.EnumerateObject())
            {
                if (outcome.Value.TryGetInt32(out var delta)) outcomes[outcome.Name] = delta;
            }
        }

        if (!root.TryGetProperty("tiers", out var tiers) || tiers.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("session.rank has no tiers array");

        var minPoints = new SortedDictionary<int, int>();
        foreach (var tier in tiers.EnumerateArray())
        {
            if (tier.ValueKind != JsonValueKind.Object ||
                !tier.TryGetProperty("key", out var keyElement) || keyElement.ValueKind != JsonValueKind.String ||
                !tier.TryGetProperty("min_points", out var minElement) || !minElement.TryGetInt32(out var min))
            {
                throw new InvalidDataException("session.rank has a tier without a string key and an integer min_points");
            }

            var key = keyElement.GetString();
            var rank = RankTranslation.RankNumber(key)
                       ?? throw new InvalidDataException($"session.rank tier key '{key}' is not rank_<N>");
            if (min < 0) throw new InvalidDataException($"session.rank tier {key} starts below 0 points");
            if (!minPoints.TryAdd(rank, min)) throw new InvalidDataException($"session.rank names {key} twice");
        }

        if (minPoints.Count == 0) throw new InvalidDataException("session.rank has no tiers");
        var previous = -1;
        foreach (var (rank, min) in minPoints)
        {
            if (min <= previous)
                throw new InvalidDataException($"session.rank thresholds do not rise at rank_{rank} ({min} points)");
            previous = min;
        }

        return new RankLadder(ruleTypes, outcomes, minPoints);
    }
}
