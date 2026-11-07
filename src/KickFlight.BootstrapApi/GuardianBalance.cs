using System.Text.Json;

namespace KickFlight.BootstrapApi;

/// <summary>
/// Rank-scaled GuardianParameter master. The balance WebUI edits one row per guardian profile in
/// <c>masters_guardian_balance.json</c> (override file wins over config/): the HP and attack at rank C plus a
/// linear percentage added per rank step above C. The API expands those rows at startup into a GuardianParameter
/// row for every (profile, rank 0..16), so a restart applies an edit.
///
/// Formula: value = round(base * (1 + pct / 100 * max(0, rank - 1))), half away from zero. Rank 1 is C
/// (BattleRank id 2); rank 0 (D) is below C and is clamped to the C value; rank 16 is S+9.
///
/// Profiles: 1 = Crystal Scramble turret, 2 = Rapid Ball goal guardian, 3 = Gym mannequin.
///
/// The client picks its row with a patched predicate: row.MatchType == the controller's BattleRuleId AND
/// row.Rank == GuardianRank. So rows are generated per (battle rule id, ladder rank): row.matchType holds the rule id
/// (the profile follows the rule's battleRuleType: 3 = Rapid Ball goal guardian, otherwise Crystal turret) and
/// row.rank is the plain ladder rank, because /battle/start sends that same plain rank (0..16) and the client also
/// renders it as the "Guardian Rank" league icon at battle start. Row id = ruleId * 100 + rank.
/// The Gym profile (3) is no longer selectable per battle (the client cannot tell it apart); its row stays editable
/// but unused.
/// </summary>
public static class GuardianBalance
{
    public const int MinRank = 0;
    public const int MaxRank = 16; // S+9, the last BattleRank row the client has a badge for (DemoSessionApi.Rank.cs)
    public const int RankC = 1;
    public const int CrystalProfile = 1;
    public const int BallProfile = 2;
    public const int GymProfile = 3;
    public const string ConfigPath = "config/masters_guardian_balance.json";

    public const string DefaultJson = """
        [{"id":1,"name":"Crystal Scramble guardian","matchType":1,"baseHp":8000,"hpPercentPerRank":0.0,"baseAttack":6000,"attackPercentPerRank":0.0},
         {"id":2,"name":"Rapid Ball goal guardian","matchType":1,"baseHp":3000,"hpPercentPerRank":0.0,"baseAttack":6000,"attackPercentPerRank":0.0},
         {"id":3,"name":"Gym guardian","matchType":1,"baseHp":8000,"hpPercentPerRank":0.0,"baseAttack":0,"attackPercentPerRank":0.0}]
        """;

    public static int ProfileFor(bool gym, int battleRuleType) =>
        gym ? GymProfile : battleRuleType == 3 ? BallProfile : CrystalProfile;

    public static int Scale(double baseValue, double percentPerRank, int rank)
    {
        var steps = Math.Max(0, rank - RankC);
        // decimal keeps x.5 cases exact (100 * 1.005 must round to 101, not 100.49999 -> 100).
        var value = (decimal)baseValue * (1m + (decimal)percentPerRank / 100m * steps);
        return (int)Math.Max(0m, Math.Round(value, MidpointRounding.AwayFromZero));
    }

    public static int RowId(int battleRuleId, int rank) =>
        battleRuleId * 100 + Math.Clamp(rank, MinRank, MaxRank);

    /// <summary>
    /// The one ladder rank every client of a room uses: the LOWEST stored rank among its human players. Bots are
    /// excluded (their roster rank is a hardcoded 13, not a stored standing). Falls back to <paramref name="fallback"/>
    /// when the roster has no human with a numeric user id. The result depends only on the roster, never on who asks.
    /// </summary>
    public static int RoomRank(IEnumerable<(string UserId, int KickerAiParameterId)> roster, Func<long, int> loadRank, int fallback)
    {
        var ranks = new List<int>();
        foreach (var (userId, aiParameterId) in roster)
        {
            if (aiParameterId != 0 || !long.TryParse(userId, out var playerId)) continue;
            ranks.Add(loadRank(playerId));
        }
        return Math.Clamp(ranks.Count == 0 ? fallback : ranks.Min(), MinRank, MaxRank);
    }

    /// <summary>(battle rule id, battleRuleType) of every BattleRule row that names both.</summary>
    public static IReadOnlyList<(int RuleId, int RuleType)> RuleTypes(string battleRuleJson)
    {
        var rules = new List<(int, int)>();
        using var document = JsonDocument.Parse(battleRuleJson);
        foreach (var row in document.RootElement.EnumerateArray())
            if (row.TryGetProperty("id", out var id) && row.TryGetProperty("battleRuleType", out var type))
                rules.Add((id.GetInt32(), type.GetInt32()));
        return rules;
    }

    /// <summary>
    /// Expands the balance rows into the GuardianParameter master JSON: one row per (battle rule id, rank 0..16),
    /// matchType = rule id, rank = ladder rank, hp/attack from the profile of the rule's battleRuleType.
    /// </summary>
    public static string BuildMasterJson(string balanceJson, IEnumerable<(int RuleId, int RuleType)> rules)
    {
        var profiles = new Dictionary<int, (double Hp, double HpPct, double Atk, double AtkPct)>();
        using var document = JsonDocument.Parse(balanceJson);
        foreach (var profile in document.RootElement.EnumerateArray())
        {
            profiles[profile.GetProperty("id").GetInt32()] = (
                profile.GetProperty("baseHp").GetDouble(), profile.GetProperty("hpPercentPerRank").GetDouble(),
                profile.GetProperty("baseAttack").GetDouble(), profile.GetProperty("attackPercentPerRank").GetDouble());
        }

        var rows = new List<Dictionary<string, object>>();
        foreach (var (ruleId, ruleType) in rules.OrderBy(r => r.RuleId))
        {
            if (!profiles.TryGetValue(ProfileFor(false, ruleType), out var p)) continue;
            for (var rank = MinRank; rank <= MaxRank; rank++)
            {
                rows.Add(new Dictionary<string, object>
                {
                    ["id"] = RowId(ruleId, rank),
                    ["matchType"] = ruleId,
                    ["rank"] = rank,
                    ["hp"] = Scale(p.Hp, p.HpPct, rank),
                    ["attack"] = Scale(p.Atk, p.AtkPct, rank)
                });
            }
        }
        return JsonSerializer.Serialize(rows);
    }
}
