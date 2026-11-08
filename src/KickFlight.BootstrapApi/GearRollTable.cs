using System.Text.Json;

namespace KickFlight.BootstrapApi;

// What gear/create rolls from: a rarity first, weighted by the LotteryGearRarity master's dropRatio, then one Gear row
// of that rarity, uniformly (every colour x skill of the rarity is equally likely). Retail rolled server-side too:
// GearCreateResponseData carries only the Gear row id, and the client derives colour, skill and the same-colour bonus
// from the Gear, GearSkill and GearSameColorBonus masters itself (PlayerGearParameter..ctor, 0x1743FAC).
//
// Both inputs are served masters, so a liveops edit of a drop ratio or a Gear row is a published release, and the
// table is rebuilt from that release (DemoSessionApi.ApplyOtomoTables). Immutable: a roll reads one whole table.
public sealed class GearRollTable
{
    private readonly IReadOnlyList<(int Rarity, double Weight, IReadOnlyList<int> GearIds)> _buckets;
    private readonly double _totalWeight;

    public IReadOnlySet<int> GearIds { get; }

    private GearRollTable(IReadOnlyList<(int, double, IReadOnlyList<int>)> buckets, IReadOnlySet<int> gearIds)
    {
        _buckets = buckets;
        _totalWeight = buckets.Sum(bucket => bucket.Item2);
        GearIds = gearIds;
    }

    public static readonly GearRollTable Empty = new([], new HashSet<int>());

    public bool IsEmpty => _buckets.Count == 0;

    // A rarity with no dropRatio row, or a ratio of 0, is never rolled. A rarity table with no usable row at all falls
    // back to a uniform roll over every Gear row, which is what this server did before the ratios were read.
    public static GearRollTable Parse(string gearJson, string rarityJson, out string? problem)
    {
        problem = null;
        var byRarity = new Dictionary<int, List<int>>();
        var all = new HashSet<int>();
        using (var gears = JsonDocument.Parse(gearJson))
        {
            foreach (var row in gears.RootElement.EnumerateArray())
            {
                var id = row.GetProperty("id").GetInt32();
                var rarity = row.TryGetProperty("rarityType", out var r) && r.TryGetInt32(out var value) ? value : 0;
                if (!byRarity.TryGetValue(rarity, out var list)) byRarity[rarity] = list = [];
                list.Add(id);
                all.Add(id);
            }
        }

        var weights = new Dictionary<int, double>();
        using (var ratios = JsonDocument.Parse(rarityJson))
        {
            foreach (var row in ratios.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("rarityType", out var r) || !r.TryGetInt32(out var rarity)) continue;
                var ratio = row.TryGetProperty("dropRatio", out var d) && d.TryGetDouble(out var w) ? w : 0;
                if (ratio > 0) weights[rarity] = weights.GetValueOrDefault(rarity) + ratio;
            }
        }

        var buckets = byRarity
            .Where(pair => weights.ContainsKey(pair.Key))
            .OrderBy(pair => pair.Key)
            .Select(pair => (pair.Key, weights[pair.Key], (IReadOnlyList<int>)pair.Value))
            .ToList();
        if (buckets.Count == 0 && all.Count > 0)
        {
            problem = "LotteryGearRarity has no positive dropRatio for any Gear rarity: rolling uniformly over all gears";
            buckets = [(0, 1.0, all.OrderBy(id => id).ToList())];
        }
        return new GearRollTable(buckets, all);
    }

    public int Roll(Random random)
    {
        var pick = random.NextDouble() * _totalWeight;
        foreach (var (_, weight, ids) in _buckets)
        {
            if (pick < weight) return ids[random.Next(ids.Count)];
            pick -= weight;
        }
        var last = _buckets[^1].GearIds;
        return last[random.Next(last.Count)];
    }

    // The chance of each rarity, for the log line a release prints.
    public string Describe() => _totalWeight <= 0
        ? "empty"
        : string.Join(", ", _buckets.Select(b => $"rarity {b.Rarity}: {b.Weight / _totalWeight:P1} of {b.GearIds.Count} gears"));
}
