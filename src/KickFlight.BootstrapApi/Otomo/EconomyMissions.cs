using System.Globalization;
using System.Text.Json;

namespace KickFlight.BootstrapApi.Otomo;

// The mission screen, as the client sees economy's tracks.
//
// Economy calls them tracks (`mission.daily.play`, one counter, one or more steps, a term); the client calls them
// missions and tasks, and resolves every id it is sent through five masters - Mission, MissionTask, MissionReward,
// MissionTerm and MissionCondition. An id the masters do not have is a failed lookup on a live screen, so the
// masters are served from this table and the progress rows (userMissionProgressList) only ever name ids that are in
// it. A track economy reports that is not in this table is logged and not shown; a client cannot render it.
//
//   missionId     = one per track (1xx daily, 2xx weekly, 3xx lifetime)
//   missionTaskId = missionId * 100 + step + 1, one per step of that track
//
// The targets and the rewards below are what release 17 publishes. They are display only: what a claim actually
// pays is the step's class in economy, and the receipt is built from economy's answer, so a release that changes
// a class shows a stale reward icon until this table follows - never a wrong balance. A live track whose target
// disagrees with this table is logged (EconomyPayloads.BuildMissionProgressList) so the drift is visible.
public static class EconomyMissions
{
    public sealed record Reward(int GoodsType, int GoodsId, int Amount);

    public sealed record Step(int Index, long Target, IReadOnlyList<Reward> Rewards);

    public sealed record Mission(string TrackId, int MissionId, int MissionType, int TermType, string Title,
        IReadOnlyList<Step> Steps)
    {
        public int TaskId(int step) => MissionId * 100 + step + 1;
    }

    // MissionType (dump.cs): BattlePlay 4, BattleWin 5, BattleMvp 6, BattleKill 17.
    // MissionTermType: None 1, Daily 2. There is no weekly term in the client, so the weekly tracks are None with
    // their period named in the title; the expiry each row carries is still the real end of the week.
    private const int BattlePlay = 4, BattleWin = 5, BattleMvp = 6, BattleKill = 17;
    private const int TermNone = 1, TermDaily = 2;

    private static Reward Coins(int amount) => new(101, 1, amount);
    private static Reward Tickets(int amount) => new(401, 7, amount);

    public static readonly IReadOnlyList<Mission> All =
    [
        new("mission.daily.play", 101, BattlePlay, TermDaily, "Play 3 battles", [new(0, 3, [Coins(150)])]),
        new("mission.daily.win", 102, BattleWin, TermDaily, "Win a battle", [new(0, 1, [Coins(200), Tickets(1)])]),
        new("mission.daily.mvp", 103, BattleMvp, TermDaily, "Be the MVP of a battle", [new(0, 1, [Coins(100)])]),
        new("mission.daily.kill", 104, BattleKill, TermDaily, "Knock out 10 kickers", [new(0, 10, [Coins(100)])]),
        new("mission.weekly.play", 201, BattlePlay, TermNone, "Weekly: play 20 battles", [new(0, 20, [Coins(800)])]),
        new("mission.weekly.win", 202, BattleWin, TermNone, "Weekly: win 10 battles",
            [new(0, 10, [Coins(1200), Tickets(2)])]),
        new("mission.lifetime.play", 301, BattlePlay, TermNone, "Play 100 battles", [new(0, 100, [Coins(3000)])]),
        new("mission.lifetime.win", 302, BattleWin, TermNone, "Win battles",
            [new(0, 10, [Coins(1500)]), new(1, 50, [Coins(4000)]), new(2, 100, [Coins(8000)])])
    ];

    public static Mission? ByTrack(string? trackId) =>
        All.FirstOrDefault(mission => string.Equals(mission.TrackId, trackId, StringComparison.Ordinal));

    // The track and step a client missionTaskId names, or null when it names nothing this server serves.
    public static (Mission Mission, Step Step)? ByTaskId(int missionTaskId)
    {
        foreach (var mission in All)
        {
            foreach (var step in mission.Steps)
            {
                if (mission.TaskId(step.Index) == missionTaskId) return (mission, step);
            }
        }
        return null;
    }

    // When a period ends, from economy's period label: "dHH:yyyy-MM-dd" is the day that started at HH:00 UTC,
    // "wHH:yyyy-MM-dd" the week that started on that Monday at HH:00 UTC, and "all" never ends. The client shows the
    // countdown from this, so an unreadable label is "" (no countdown) rather than a guessed date.
    public static string PeriodEnd(string? period)
    {
        if (string.IsNullOrEmpty(period) || period.Length < 4 || period[3] != ':') return "";
        if (!int.TryParse(period.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hour)) return "";
        if (!DateTime.TryParseExact(period[4..], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var start)) return "";
        var length = period[0] switch
        {
            'd' => TimeSpan.FromDays(1),
            'w' => TimeSpan.FromDays(7),
            _ => TimeSpan.Zero
        };
        return length == TimeSpan.Zero ? "" : (start.AddHours(hour) + length).ToString("yyyy-MM-dd HH:mm:ss");
    }

    // The five masters, as the client's MasterData classes read them (field names from dump.cs: MissionMasterData,
    // MissionTaskMasterData, MissionRewardMasterData, MissionTermMasterData, MissionConditionMasterData).
    //
    // One term and one condition serve every row. The term is open from 2019 to 2030, the same window every other
    // always-on master here uses. The condition filters nothing: each enum is -1 (Invalid), because 0 is a real
    // value for some of them (RoleType.Speed) and would restrict the mission to it. That reading of -1 is inferred
    // from the enums, not from the client's condition check.
    public static IReadOnlyDictionary<string, string> BuildMasters()
    {
        var missions = All.Select(mission => new
        {
            id = mission.MissionId,
            missionType = mission.MissionType,
            missionTermType = mission.TermType,
            missionTargetType = 1, // User
            randomGroupId = 0,
            title = mission.Title,
            sortOrder = mission.MissionId,
            missionTermId = 1,
            missionConditionId = 1,
            displayFlag = true,
            presentFlag = false
        });
        var tasks = All.SelectMany(mission => mission.Steps.Select(step => new
        {
            id = mission.TaskId(step.Index),
            missionId = mission.MissionId,
            value = step.Target,
            displayFlag = true
        }));
        var rewardId = 0;
        var rewards = All.SelectMany(mission => mission.Steps.SelectMany(step => step.Rewards.Select(reward => new
        {
            id = ++rewardId,
            missionTaskId = mission.TaskId(step.Index),
            goodsType = reward.GoodsType,
            goodsId = reward.GoodsId,
            amount = reward.Amount
        }))).ToList();
        var terms = new[]
        {
            new
            {
                id = 1,
                startDatetime = "2019-01-01 00:00:00",
                endDatetime = "2030-01-01 23:59:59",
                entryStartDatetime = "2019-01-01 00:00:00",
                entryEndDatetime = "2030-01-01 23:59:59",
                availableTermDay = 0,
                receiptExpirationDatetime = "2030-01-01 23:59:59",
                displayFlag = true
            }
        };
        var conditions = new[]
        {
            new
            {
                id = 1,
                battleRuleId = 0,
                battleRuleType = -1,
                battleEntryType = -1,
                roleType = -1,
                kickerId = 0,
                premiumId = 0,
                seasonPassId = 0,
                missionTargetGroupId = 0,
                tweetText = ""
            }
        };

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Mission"] = JsonSerializer.Serialize(missions),
            ["MissionTask"] = JsonSerializer.Serialize(tasks),
            ["MissionReward"] = JsonSerializer.Serialize(rewards),
            ["MissionTerm"] = JsonSerializer.Serialize(terms),
            ["MissionCondition"] = JsonSerializer.Serialize(conditions)
        };
    }
}
