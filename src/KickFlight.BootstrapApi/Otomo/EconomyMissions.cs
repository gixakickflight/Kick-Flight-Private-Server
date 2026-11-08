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
//   missionId     = one per track (1xx daily, 3xx lifetime; 2xx was weekly, retired 2026-11-08)
//   missionTaskId = missionId * 100 + step + 1, one per step of that track
//
// The targets and the rewards below are what release 17 publishes. They are display only: what a claim actually
// pays is the step's class in economy, and the receipt is built from economy's answer, so a release that changes
// a class shows a stale reward icon until this table follows - never a wrong balance. A live track whose target
// disagrees with this table is logged (EconomyPayloads.BuildMissionProgressList) so the drift is visible.
public static class EconomyMissions
{
    public sealed record Reward(int GoodsType, int GoodsId, int Amount);

    // "<goodsType>0001": the variant the asset list carries for every goods type.
    public static int GoodsThumbnailId(int goodsType) => goodsType * 10000 + 1;

    public sealed record Step(int Index, long Target, IReadOnlyList<Reward> Rewards);

    public sealed record Mission(string TrackId, int MissionId, int MissionType, int TermType, string Title,
        IReadOnlyList<Step> Steps)
    {
        public int TaskId(int step) => MissionId * 100 + step + 1;
    }

    // MissionType (dump.cs): BattlePlay 4, BattleWin 5, BattleMvp 6, BattleKill 17.
    // MissionTermType: None 1, Daily 2. The client has no weekly term; the weekly tracks are retired, and a weekly track
    // economy still reports is logged and not shown, like any track this table does not have.
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
        new("mission.lifetime.play", 301, BattlePlay, TermNone, "Play 100 battles", [new(0, 100, [Coins(3000)])]),
        new("mission.lifetime.win", 302, BattleWin, TermNone, "Win battles",
            [new(0, 10, [Coins(1500)]), new(1, 50, [Coins(4000)]), new(2, 100, [Coins(8000)])])
    ];

    // ---- The Badge tab ------------------------------------------------------------------------------------------
    //
    // The mission screen's Badge list (MissionListType.Badge 3) shows the rows whose missionTermType is None
    // (MissionListTypeExtensions.ToMissionTermType 0x1722D90, table 0x3228500: Daily 2, Limited 3, Badge 1). One
    // mission per badge rule of the catalog: its missionId is the honor id, one task (honorId * 100 + 1) at the rule's
    // target, and the badge itself as the reward (GoodsType.Honor 601, goodsId = the honor id). Progress is economy's:
    // the badge tracks (kf-otomo gen_badges.py) pay "badge.<honorId>" on the step that awards it, and that step's
    // track value is the row's value. Badge tracks are auto-claimed, so a reached step reads as received and there is
    // nothing to claim on this list.
    public sealed record BadgeMission(int HonorId, int MissionType, string Title, long Target)
    {
        public int TaskId => HonorId * 100 + 1;
    }

    private const int GoodsTypeHonor = 601;
    private const string BadgeClassPrefix = "badge.";
    private const string AchievementPrefix = "Recognizes the listed achievement: ";

    private static IReadOnlyList<BadgeMission> _badges = [];
    private static IReadOnlyDictionary<int, BadgeMission> _badgesById = new Dictionary<int, BadgeMission>();

    public static IReadOnlyList<BadgeMission> Badges => _badges;

    // The honor id a badge track's step pays, or null for a class that is not a badge this list shows.
    public static BadgeMission? BadgeForClass(string? stepClass) =>
        stepClass is not null && stepClass.StartsWith(BadgeClassPrefix, StringComparison.Ordinal)
        && int.TryParse(stepClass.AsSpan(BadgeClassPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
        && _badgesById.TryGetValue(id, out var badge)
            ? badge
            : null;

    // Read once at boot from config/badge_rule_catalog.json (the same catalog the Honor master is built from). Only
    // the kinds the tenant documents express (BadgeCatalog.TranslatedKinds) with a positive target are listed: a
    // badge no track can pay would sit at 0 forever.
    public static void UseBadgeCatalog(string? catalogJson)
    {
        var badges = new List<BadgeMission>();
        if (!string.IsNullOrWhiteSpace(catalogJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(catalogJson);
                foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
                {
                    if (!entry.TryGetProperty("requirements", out var req) || req.ValueKind != JsonValueKind.Object) continue;
                    var kind = req.TryGetProperty("kind", out var k) ? k.GetString() : null;
                    if (kind is null || !BadgeCatalog.TranslatedKinds.Contains(kind)) continue;
                    if (!req.TryGetProperty("target", out var t) || !t.TryGetInt64(out var target) || target <= 0) continue;
                    var id = entry.GetProperty("id").GetInt32();
                    var stat = req.TryGetProperty("stat", out var s) ? s.GetString() : null;
                    badges.Add(new BadgeMission(id, BadgeMissionType(kind, stat), BadgeTitle(entry), target));
                }
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException
                                                  or FormatException)
            {
                badges.Clear();
            }
        }
        _badges = badges;
        _badgesById = badges.ToDictionary(badge => badge.HonorId);
    }

    // MissionType (dump.cs) only picks the row's icon and where its "go" button leads; the closest one per rule kind.
    private static int BadgeMissionType(string kind, string? stat) => kind switch
    {
        "battle_count" or "registration_complete" or "login_days" => BattlePlay,
        "wins_total" or "kicker_wins" or "mode_wins" or "queue_wins" => BattleWin,
        "mvp_count" => BattleMvp,
        "rank" => 7,           // RankUp
        "win_streak" => 20,    // BattleStraightWin
        "disc_upgrades" => 11, // DiscBuildup
        "stat_total" => stat switch
        {
            "kills" => BattleKill,
            "deaths" => 18,                         // BattleKilled
            "moving_distance" => 13,                // BattleMovingDistance
            "crystals_collected" => 14,             // BattleAcquiredCrystal
            "guardian_released_crystals" or "crystals_released" => 22, // BattleReleasedCrystal
            "flag_goals" => 15,                     // BattleFlagGoal
            "flag_drops" => 23,                     // BattleEnemyFlagDrop
            "ball_goals" => 24,                     // BattleBallShoot
            "ball_drops" => 25,                     // BattleBallDrop
            "likes_received" => 19,                 // BattleGood
            _ => BattlePlay
        },
        _ => BattlePlay
    };

    // "Recognizes the listed achievement: Win 3 times with Tsubame." -> "Win 3 times with Tsubame."; the badge's
    // name when the catalog has no description.
    private static string BadgeTitle(JsonElement entry)
    {
        var description = entry.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        if (description.StartsWith(AchievementPrefix, StringComparison.Ordinal)) description = description[AchievementPrefix.Length..];
        description = description.Trim();
        return description.Length > 0 ? description : entry.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
    }

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
    // countdown from this, so an unreadable label is "" (no countdown) rather than a guessed date. The offset is
    // written out (EconomyPayloads.Instant): the client parses dates with DateTimeOffset.TryParse, which reads an
    // offset-less string in the device's zone and would expire a UTC+8 player's dailies eight hours early.
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
        return length == TimeSpan.Zero
            ? ""
            : EconomyPayloads.Instant(new DateTimeOffset(start.AddHours(hour) + length, TimeSpan.Zero));
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
        }).Concat(Badges.Select(badge => new
        {
            id = badge.HonorId,
            missionType = badge.MissionType,
            missionTermType = TermNone,
            missionTargetType = 1,
            randomGroupId = 0,
            title = badge.Title,
            sortOrder = badge.HonorId,
            missionTermId = 1,
            missionConditionId = 1,
            displayFlag = true,
            presentFlag = false
        }));
        var tasks = All.SelectMany(mission => mission.Steps.Select(step => new
        {
            id = mission.TaskId(step.Index),
            missionId = mission.MissionId,
            value = step.Target,
            displayFlag = true
        })).Concat(Badges.Select(badge => new
        {
            id = badge.TaskId,
            missionId = badge.HonorId,
            value = badge.Target,
            displayFlag = true
        }));
        var rewardId = 0;
        var rewards = All.SelectMany(mission => mission.Steps.SelectMany(step => step.Rewards.Select(reward => new
        {
            id = ++rewardId,
            missionTaskId = mission.TaskId(step.Index),
            goodsType = reward.GoodsType,
            // The client draws a reward icon by asking for "ui/goods/thumbnail/<goodsId>", and the assets are named
            // "<goodsType>0001.unity3d". A raw item id (1 for JetCoin, 7 for a ticket) matches no asset, which is
            // why every reward tile was a white square. The composite is what resolves.
            goodsId = GoodsThumbnailId(reward.GoodsType),
            amount = reward.Amount
        }))).ToList();
        // A badge's reward is the badge: the honor id itself, as every other honor goods row names it.
        foreach (var badge in Badges)
        {
            rewards.Add(new
            {
                id = ++rewardId,
                missionTaskId = badge.TaskId,
                goodsType = GoodsTypeHonor,
                goodsId = badge.HonorId,
                amount = 1
            });
        }
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
