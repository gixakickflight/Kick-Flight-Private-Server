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
/// The client picks its row with a predicate on GuardianParameter.rank. To keep (profile, rank) unique under a
/// rank-only comparison, row.rank is a key: (profile - 1) * 100 + ladderRank (profile 1 keeps the plain ladder
/// rank). /battle/start sends the same key as guardianParameter.rank. Row ids stay stable: rank C of a profile
/// keeps the profile id (1, 2, 3), every other rank is profile * 1000 + rank.
/// </summary>
public static class GuardianBalance
{
    public const int MinRank = 0;
    public const int MaxRank = RankProgression.MaxRank;
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

    public static int RowRankKey(int profile, int rank) =>
        (profile - 1) * 100 + Math.Clamp(rank, MinRank, MaxRank);

    public static int RowId(int profile, int rank) =>
        rank == RankC ? profile : profile * 1000 + rank;

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

    /// <summary>Expands the balance rows into the GuardianParameter master JSON (rank C of profile 1 first).</summary>
    public static string BuildMasterJson(string balanceJson)
    {
        var rows = new List<Dictionary<string, object>>();
        using var document = JsonDocument.Parse(balanceJson);
        foreach (var profile in document.RootElement.EnumerateArray().OrderBy(p => p.GetProperty("id").GetInt32()))
        {
            var id = profile.GetProperty("id").GetInt32();
            var matchType = profile.TryGetProperty("matchType", out var mt) ? mt.GetInt32() : 1;
            var baseHp = profile.GetProperty("baseHp").GetDouble();
            var hpPct = profile.GetProperty("hpPercentPerRank").GetDouble();
            var baseAtk = profile.GetProperty("baseAttack").GetDouble();
            var atkPct = profile.GetProperty("attackPercentPerRank").GetDouble();
            var ranks = new[] { RankC }.Concat(Enumerable.Range(MinRank, MaxRank - MinRank + 1).Where(r => r != RankC));
            foreach (var rank in ranks)
            {
                rows.Add(new Dictionary<string, object>
                {
                    ["id"] = RowId(id, rank),
                    ["matchType"] = matchType,
                    ["rank"] = RowRankKey(id, rank),
                    ["hp"] = Scale(baseHp, hpPct, rank),
                    ["attack"] = Scale(baseAtk, atkPct, rank)
                });
            }
        }
        return JsonSerializer.Serialize(rows);
    }
}
