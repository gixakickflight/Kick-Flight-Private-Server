using System.Text.Json;
using KickFlight.BootstrapApi;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

public sealed class GuardianBalanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kf-guardian-balance-" + Guid.NewGuid().ToString("N"));

    public GuardianBalanceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static Dictionary<int, JsonElement> Rows(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(r => r.Clone()).ToDictionary(r => r.GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData(8000, 10.0, 1, 8000)]   // C = base
    [InlineData(8000, 10.0, 2, 8800)]   // C+
    [InlineData(8000, 10.0, 7, 12800)]  // S: 6 steps above C
    [InlineData(8000, 10.0, 16, 20000)] // S+9: 15 steps
    [InlineData(8000, 10.0, 0, 8000)]   // D is below C: clamped to the C value
    [InlineData(3000, 2.5, 3, 3150)]
    [InlineData(100, 0.5, 2, 101)]      // 100.5 rounds away from zero
    [InlineData(6000, 0.0, 16, 6000)]
    public void Scale_is_linear_above_rank_C(double baseValue, double pct, int rank, int expected) =>
        Assert.Equal(expected, GuardianBalance.Scale(baseValue, pct, rank));

    // Battle rules as the client posts them: (battle rule id, battleRuleType). Rule 3/4 = Rapid Ball, 2/8 = Flag Flight.
    private static readonly (int RuleId, int RuleType)[] Rules = [(1, 1), (2, 2), (3, 3), (4, 3), (7, 1)];

    private static string Build(string balance) => GuardianBalance.BuildMasterJson(balance, Rules);

    [Fact]
    public void Defaults_reproduce_the_previous_hp_and_attack_per_rule_and_stay_flat()
    {
        var rows = Rows(Build(GuardianBalance.DefaultJson));

        Assert.Equal(Rules.Length * 17, rows.Count);
        // Crystal rules (type 1) = 8000/6000 at every rank, Rapid Ball rules (type 3) = 3000/6000.
        foreach (var rank in Enumerable.Range(0, 17))
        {
            Assert.Equal((8000, 6000), (rows[GuardianBalance.RowId(1, rank)].GetProperty("hp").GetInt32(), rows[GuardianBalance.RowId(1, rank)].GetProperty("attack").GetInt32()));
            Assert.Equal((8000, 6000), (rows[GuardianBalance.RowId(7, rank)].GetProperty("hp").GetInt32(), rows[GuardianBalance.RowId(7, rank)].GetProperty("attack").GetInt32()));
            Assert.Equal((3000, 6000), (rows[GuardianBalance.RowId(3, rank)].GetProperty("hp").GetInt32(), rows[GuardianBalance.RowId(3, rank)].GetProperty("attack").GetInt32()));
            Assert.Equal((3000, 6000), (rows[GuardianBalance.RowId(4, rank)].GetProperty("hp").GetInt32(), rows[GuardianBalance.RowId(4, rank)].GetProperty("attack").GetInt32()));
        }
    }

    [Fact]
    public void Rows_carry_the_plain_ladder_rank_and_the_rule_id_the_client_predicate_matches()
    {
        using var doc = JsonDocument.Parse(Build(GuardianBalance.DefaultJson));
        var all = doc.RootElement.EnumerateArray().ToList();

        // The rank is what the battle-start UI turns into a league icon: always 0..16, never a composite key.
        Assert.All(all, r => Assert.InRange(r.GetProperty("rank").GetInt32(), 0, 16));
        // The client predicate: row.MatchType == BattleRuleId && row.Rank == GuardianRank must hit exactly one row.
        foreach (var (ruleId, _) in Rules)
            for (var rank = 0; rank <= 16; rank++)
                Assert.Single(all, r => r.GetProperty("matchType").GetInt32() == ruleId && r.GetProperty("rank").GetInt32() == rank);
        Assert.Equal(all.Count, all.Select(r => r.GetProperty("id").GetInt32()).Distinct().Count());
    }

    [Fact]
    public void Percentages_scale_hp_and_attack_per_profile_and_rank()
    {
        var json = """[{"id":1,"matchType":1,"baseHp":8000,"hpPercentPerRank":10.0,"baseAttack":6000,"attackPercentPerRank":5.0},{"id":2,"matchType":1,"baseHp":3000,"hpPercentPerRank":2.0,"baseAttack":6000,"attackPercentPerRank":0.0}]""";
        var rows = Rows(Build(json));
        var crystalS = rows[GuardianBalance.RowId(1, 7)];
        var ballS = rows[GuardianBalance.RowId(3, 7)];

        Assert.Equal(12800, crystalS.GetProperty("hp").GetInt32());
        Assert.Equal(7800, crystalS.GetProperty("attack").GetInt32());
        Assert.Equal(7, crystalS.GetProperty("rank").GetInt32());
        Assert.Equal(3360, ballS.GetProperty("hp").GetInt32()); // 3000 * (1 + 2% * 6)
        Assert.Equal(107, GuardianBalance.RowId(1, 7));
    }

    [Fact]
    public void Override_file_roundtrip_changes_the_served_rows()
    {
        var overrides = new MasterOverrides(_root, AppContext.BaseDirectory);
        var root = RepositoryPaths.FindRoot(AppContext.BaseDirectory);

        var before = Rows(Build(overrides.Read(root, GuardianBalance.ConfigPath, GuardianBalance.DefaultJson)));
        Assert.Equal(8000, before[GuardianBalance.RowId(1, 7)].GetProperty("hp").GetInt32());

        File.WriteAllText(Path.Combine(_root, "masters_guardian_balance.json"),
            """[{"id":1,"matchType":1,"baseHp":10000,"hpPercentPerRank":20.0,"baseAttack":6000,"attackPercentPerRank":0.0},{"id":2,"matchType":1,"baseHp":3000,"hpPercentPerRank":0.0,"baseAttack":6000,"attackPercentPerRank":0.0}]""");
        var after = Rows(Build(overrides.Read(root, GuardianBalance.ConfigPath, GuardianBalance.DefaultJson)));

        Assert.Equal(10000, after[GuardianBalance.RowId(1, 1)].GetProperty("hp").GetInt32());
        Assert.Equal(12000, after[GuardianBalance.RowId(1, 2)].GetProperty("hp").GetInt32()); // C+
        Assert.Equal(22000, after[GuardianBalance.RowId(1, 7)].GetProperty("hp").GetInt32());
    }

    [Fact]
    public void Repository_config_file_matches_the_built_in_defaults()
    {
        var root = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        var file = File.ReadAllText(Path.Combine(root, GuardianBalance.ConfigPath));
        Assert.Equal(Build(GuardianBalance.DefaultJson), Build(file));
    }
}

public sealed class GuardianRoomRankTests
{
    private static readonly Dictionary<long, int> Ranks = new() { [1001] = 12, [1002] = 8, [1003] = 15 };
    private static int Load(long id) => Ranks[id];

    [Fact]
    public void Room_rank_is_the_lowest_human_rank_and_ignores_bots()
    {
        var roster = new[] { ("1001", 0), ("1002", 0), ("1003", 0), ("bot-1", 0), ("77", 5), ("78", 5) };
        Assert.Equal(8, GuardianBalance.RoomRank(roster, Load, fallback: 1));
    }

    [Fact]
    public void Every_member_gets_the_same_rank_regardless_of_who_asks()
    {
        var roster = new[] { ("1001", 0), ("1002", 0), ("1003", 0), ("900", 3) };
        var keys = new[] { 1001L, 1002L, 1003L }
            .Select(asker => GuardianBalance.RoomRank(roster, Load, Load(asker)))
            .Distinct().ToList();
        Assert.Equal([8], keys);
    }

    [Fact]
    public void Empty_or_bot_only_roster_uses_the_fallback_and_ranks_are_clamped()
    {
        Assert.Equal(12, GuardianBalance.RoomRank([("900", 3)], Load, fallback: 12));
        Assert.Equal(GuardianBalance.MaxRank, GuardianBalance.RoomRank([], Load, fallback: 99));
        Assert.Equal(GuardianBalance.MinRank, GuardianBalance.RoomRank([], Load, fallback: -4));
    }

    [Fact]
    public void Every_rank_the_server_can_send_has_a_served_row_for_every_rule()
    {
        var rules = new[] { (1, 1), (3, 3), (4, 3), (7, 1) };
        using var doc = JsonDocument.Parse(GuardianBalance.BuildMasterJson(GuardianBalance.DefaultJson, rules));
        var served = doc.RootElement.EnumerateArray()
            .Select(r => (r.GetProperty("matchType").GetInt32(), r.GetProperty("rank").GetInt32())).ToHashSet();
        foreach (var (ruleId, _) in rules)
            for (var rank = GuardianBalance.MinRank; rank <= GuardianBalance.MaxRank; rank++)
                Assert.Contains((ruleId, rank), served);
    }
}
