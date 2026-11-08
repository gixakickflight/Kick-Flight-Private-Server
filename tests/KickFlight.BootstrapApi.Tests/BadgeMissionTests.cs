using System.Text.Json;
using KickFlight.BootstrapApi.Otomo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The mission screen's Badge tab: one permanent mission per badge rule, its progress read from economy's badge track.
// Uses the repository's own catalog, the one every test host loads, so the shared table is the same either way.
public sealed class BadgeMissionTests
{
    private const int TsubameThreeWins = 6011501; // kicker_wins, kickerId 1, target 3

    private static void UseRealCatalog()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, BadgeCatalog.CatalogPath))) dir = dir.Parent;
        Assert.NotNull(dir);
        EconomyMissions.UseBadgeCatalog(File.ReadAllText(Path.Combine(dir!.FullName, BadgeCatalog.CatalogPath)));
    }

    private static JsonElement Master(string name)
    {
        using var doc = JsonDocument.Parse(EconomyMissions.BuildMasters()[name]);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Every_translatable_badge_is_a_permanent_mission_that_rewards_the_badge()
    {
        UseRealCatalog();

        var mission = Master("Mission").EnumerateArray().Single(row => row.GetProperty("id").GetInt32() == TsubameThreeWins);
        Assert.Equal(1, mission.GetProperty("missionTermType").GetInt32()); // None: the Badge list
        Assert.Equal(5, mission.GetProperty("missionType").GetInt32());     // BattleWin
        Assert.Equal("Win 3 times with Tsubame.", mission.GetProperty("title").GetString());

        var task = Master("MissionTask").EnumerateArray().Single(row => row.GetProperty("missionId").GetInt32() == TsubameThreeWins);
        Assert.Equal(TsubameThreeWins * 100 + 1, task.GetProperty("id").GetInt32());
        Assert.Equal(3, task.GetProperty("value").GetInt64());

        var reward = Master("MissionReward").EnumerateArray()
            .Single(row => row.GetProperty("missionTaskId").GetInt32() == TsubameThreeWins * 100 + 1);
        Assert.Equal(601, reward.GetProperty("goodsType").GetInt32());
        Assert.Equal(TsubameThreeWins, reward.GetProperty("goodsId").GetInt32());

        // Unsupported catalog entries (no track can pay them) are not listed.
        Assert.True(EconomyMissions.Badges.Count is > 50 and < 211);
        var ids = Master("Mission").EnumerateArray().Select(row => row.GetProperty("id").GetInt32()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void A_badge_track_shows_each_badge_step_at_the_track_progress()
    {
        UseRealCatalog();
        var payloads = new EconomyPayloads(new Dictionary<int, int>(), _ => new[] { 0, 0, 0 }, [], NullLogger.Instance);
        var track = new EconomyTrack("badge.kicker_wins.1", "badge.kicker_wins.1", "all", 4, false,
        [
            new EconomyStep(0, 3, "badge.6011501", true, true, false),
            new EconomyStep(1, 10, "badge.6011502", false, false, false)
        ], 0);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payloads.BuildMissionProgressList([track])));
        var rows = doc.RootElement.EnumerateArray().ToDictionary(row => row.GetProperty("missionId").GetInt32());

        Assert.Equal(3, rows[6011501].GetProperty("value").GetInt64()); // capped at its target
        Assert.Equal(3, rows[6011501].GetProperty("userMissionTaskStatusList")[0].GetProperty("missionTaskStatus").GetInt32());
        Assert.Equal(4, rows[6011502].GetProperty("value").GetInt64());
        Assert.Equal(1, rows[6011502].GetProperty("userMissionTaskStatusList")[0].GetProperty("missionTaskStatus").GetInt32());
    }
}
