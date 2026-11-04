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

    [Fact]
    public void Defaults_reproduce_the_previous_rank_C_rows_and_stay_flat()
    {
        var rows = Rows(GuardianBalance.BuildMasterJson(GuardianBalance.DefaultJson));

        Assert.Equal(3 * 17, rows.Count);
        Assert.Equal((8000, 6000, 1), (rows[1].GetProperty("hp").GetInt32(), rows[1].GetProperty("attack").GetInt32(), rows[1].GetProperty("rank").GetInt32()));
        Assert.Equal((3000, 6000), (rows[2].GetProperty("hp").GetInt32(), rows[2].GetProperty("attack").GetInt32()));
        Assert.Equal((8000, 0), (rows[3].GetProperty("hp").GetInt32(), rows[3].GetProperty("attack").GetInt32()));
        Assert.All(rows.Values.Where(r => r.GetProperty("id").GetInt32() / 1000 == 1), r => Assert.Equal(8000, r.GetProperty("hp").GetInt32()));
    }

    [Fact]
    public void The_first_row_is_still_Crystal_rank_C_and_ids_and_rank_keys_are_unique()
    {
        using var doc = JsonDocument.Parse(GuardianBalance.BuildMasterJson(GuardianBalance.DefaultJson));
        var all = doc.RootElement.EnumerateArray().ToList();

        Assert.Equal(1, all[0].GetProperty("id").GetInt32());
        Assert.Equal(all.Count, all.Select(r => r.GetProperty("id").GetInt32()).Distinct().Count());
        Assert.Equal(all.Count, all.Select(r => r.GetProperty("rank").GetInt32()).Distinct().Count());
    }

    [Fact]
    public void Percentages_scale_hp_and_attack_per_profile_and_rank_key()
    {
        var json = """[{"id":1,"matchType":1,"baseHp":8000,"hpPercentPerRank":10.0,"baseAttack":6000,"attackPercentPerRank":5.0}]""";
        var rows = Rows(GuardianBalance.BuildMasterJson(json));
        var s = rows[GuardianBalance.RowId(1, 7)];

        Assert.Equal(12800, s.GetProperty("hp").GetInt32());
        Assert.Equal(7800, s.GetProperty("attack").GetInt32());
        Assert.Equal(7, s.GetProperty("rank").GetInt32());
        Assert.Equal(107, GuardianBalance.RowRankKey(2, 7));
        Assert.Equal(2, GuardianBalance.RowId(2, 1));
    }

    [Fact]
    public void Override_file_roundtrip_changes_the_served_rows()
    {
        var overrides = new MasterOverrides(_root, AppContext.BaseDirectory);
        var root = RepositoryPaths.FindRoot(AppContext.BaseDirectory);

        var before = Rows(GuardianBalance.BuildMasterJson(overrides.Read(root, GuardianBalance.ConfigPath, GuardianBalance.DefaultJson)));
        Assert.Equal(8000, before[GuardianBalance.RowId(1, 7)].GetProperty("hp").GetInt32());

        File.WriteAllText(Path.Combine(_root, "masters_guardian_balance.json"),
            """[{"id":1,"matchType":1,"baseHp":10000,"hpPercentPerRank":20.0,"baseAttack":6000,"attackPercentPerRank":0.0}]""");
        var after = Rows(GuardianBalance.BuildMasterJson(overrides.Read(root, GuardianBalance.ConfigPath, GuardianBalance.DefaultJson)));

        Assert.Equal(10000, after[1].GetProperty("hp").GetInt32());
        Assert.Equal(12000, after[GuardianBalance.RowId(1, 2)].GetProperty("hp").GetInt32()); // C+
        Assert.Equal(22000, after[GuardianBalance.RowId(1, 7)].GetProperty("hp").GetInt32());
    }

    [Fact]
    public void Repository_config_file_matches_the_built_in_defaults()
    {
        var root = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        var file = File.ReadAllText(Path.Combine(root, GuardianBalance.ConfigPath));
        Assert.Equal(GuardianBalance.BuildMasterJson(GuardianBalance.DefaultJson), GuardianBalance.BuildMasterJson(file));
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
    public void Every_member_gets_the_same_key_regardless_of_who_asks()
    {
        var roster = new[] { ("1001", 0), ("1002", 0), ("1003", 0), ("900", 3) };
        var keys = new[] { 1001L, 1002L, 1003L }
            .Select(asker => GuardianBalance.RowRankKey(GuardianBalance.BallProfile, GuardianBalance.RoomRank(roster, Load, Load(asker))))
            .Distinct().ToList();
        Assert.Equal([108], keys);
    }

    [Fact]
    public void Empty_or_bot_only_roster_uses_the_fallback_and_ranks_are_clamped()
    {
        Assert.Equal(12, GuardianBalance.RoomRank([("900", 3)], Load, fallback: 12));
        Assert.Equal(GuardianBalance.MaxRank, GuardianBalance.RoomRank([], Load, fallback: 99));
        Assert.Equal(GuardianBalance.MinRank, GuardianBalance.RoomRank([], Load, fallback: -4));
    }

    [Fact]
    public void Every_key_the_server_can_send_has_a_served_row()
    {
        using var doc = JsonDocument.Parse(GuardianBalance.BuildMasterJson(GuardianBalance.DefaultJson));
        var served = doc.RootElement.EnumerateArray().Select(r => r.GetProperty("rank").GetInt32()).ToHashSet();
        foreach (var profile in new[] { 1, 2, 3 })
            for (var rank = -5; rank <= 25; rank++)
                Assert.Contains(GuardianBalance.RowRankKey(profile, rank), served);
    }
}
