using System.Globalization;
using System.Text.Json;

namespace KickFlight.BootstrapApi;

/// <summary>Trust labels carried by badge rules and catalog entries.</summary>
public static class BadgeTrust
{
    /// <summary>Every input is held by the server (issued entry, account state, server clock).</summary>
    public const string ServerVerified = "server_verified";
    /// <summary>
    /// The numbers come from the playing client's own /battle/result body. They are credited only after the
    /// server-held context (entry owner, room roster, mode, timing) and the hard per-match caps below accepted them.
    /// </summary>
    public const string ClientReportedBounded = "client_reported_bounded";
}

public static class BadgeStatNames
{
    public const string Kills = "kills";
    public const string Deaths = "deaths";
    public const string Mvp = "mvp";
    public const string CrystalsCollected = "crystals_collected";
    public const string GuardianReleasedCrystals = "guardian_released_crystals";
    public const string CrystalsReleased = "crystals_released";
    public const string FlagGoals = "flag_goals";
    public const string FlagDrops = "flag_drops";
    public const string LikesReceived = "likes_received";
    /// <summary>Rapid Ball goals, from BattleResultRequestData.ballShootPoint (the value the result screen shows as goals).</summary>
    public const string BallGoals = "ball_goals";
    /// <summary>Rapid Ball ball drops, from ballDropCount regardless of direction.</summary>
    public const string BallDrops = "ball_drops";
    /// <summary>Metres flown, from movingDistance (1 = 1 m; result screen text "Distance covered: {0} m").</summary>
    public const string MovingDistance = "moving_distance";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Kills, Deaths, Mvp, CrystalsCollected, GuardianReleasedCrystals, CrystalsReleased, FlagGoals, FlagDrops,
        LikesReceived, BallGoals, BallDrops, MovingDistance
    };
}

/// <summary>
/// Named bounds for client-reported battle stats. A match is one 3 minute (masters_battle_rule.battleTimeSecond=180)
/// 4v4 battle on a field with 50 crystals / 1 flag / 1 ball. The caps are deliberately generous multiples of what that
/// can produce: they exist to stop absurd values, not to police skill. Any field above its cap drops the whole stat set.
/// </summary>
public static class BadgeStatLimits
{
    // The battle lasts 180 s; a result sooner than half of that after the room closed cannot be a played battle.
    public const int MinBattleSeconds = 90;
    // Longest plausible gap between /battle/entry and its /battle/result (queue 40-65 s + 180 s battle + loading + slack).
    public const int MaxEntryAgeSeconds = 3 * 60 * 60;

    public const int MaxListedPlayers = 8;            // 4v4
    public const int MaxTeams = 2;
    public const long MaxTeamScore = 10_000_000;       // sanity bound only; the score unit is client-defined

    public const long MaxKills = 60;                   // 8 players x 180 s: one kill every 3 s is already absurd
    public const long MaxDeaths = 60;
    public const long MaxCrystalsAcquired = 500;       // 50 on the field, plus drops/guardian releases/respawns
    public const long MaxCrystalsReleased = 500;
    public const long MaxGuardianReleasedCrystals = 500;
    public const long MaxFlagGoals = 30;               // one flag, carried the whole field length each time
    public const long MaxEnemyFlagDrops = 100;
    // Rapid Ball: one ball, 180 s. One goal every ~2 s for the whole match by a whole 4 man team is already absurd (90),
    // so 100 is generous. Ball drops (either direction) are bounded like flag drops.
    public const long MaxBallGoals = 100;
    public const long MaxBallDrops = 200;
    // movingDistance is metres (unit confidence: medium, see docs/re-notes/battle-result-fields.md). Cap = 150 m/s
    // (far above any kicker flight speed) x 200 s (180 s battle plus overtime/loading slack) = 30 000 m per match.
    public const long MaxMovingDistanceMetres = 30_000;

    // Ledgers are bounded because the account document is rewritten whole on every save.
    public const int CreditedBattleLedgerCapacity = 512;
    public const int LikeLedgerCapacity = 512;
    public const int MaxLikeTargetsPerRequest = 7;     // everyone in an 8 slot room except the liker
}

/// <summary>Team-score outcome of the caller's team, decided only from server-held team assignment and validated scores.</summary>
public enum BadgeBattleOutcome
{
    /// <summary>No room/team record, malformed or missing scores, or the caller is not in the validated user list.</summary>
    Undetermined = 0,
    Win,
    Loss,
    /// <summary>Both teams reported the same score: not a win, and not a loss either.</summary>
    Tie
}

public sealed class BadgeBattleStatReport
{
    /// <summary>
    /// The caller's team outcome from the validated team scores, independent of the stat caps (a capped stat set
    /// does not void the score) and independent of the caller's breakaway/badConnection flags (see <see cref="CallerFlagged"/>).
    /// </summary>
    public BadgeBattleOutcome Outcome { get; set; } = BadgeBattleOutcome.Undetermined;
    /// <summary>Why <see cref="Outcome"/> is undetermined (empty otherwise).</summary>
    public string OutcomeReason { get; set; } = "";
    /// <summary>The caller's own userScoreList row carried breakawayFlag or badConnectionFlag.</summary>
    public bool CallerFlagged { get; set; }
    /// <summary>False means: count participation only, credit no stat, win or streak movement.</summary>
    public bool CreditStats { get; set; }
    public string Reason { get; set; } = "";
    public int BattleRuleType { get; set; }
    public int KickerId { get; set; }
    public bool QueuedAsParty { get; set; }
    /// <summary>True win, false loss/tie, null when the outcome cannot be decided or the caller was flagged.</summary>
    public bool? Won { get; set; }
    /// <summary>Validated stat amounts by <see cref="BadgeStatNames"/> (mvp is 0 or 1).</summary>
    public Dictionary<string, long> Stats { get; } = new(StringComparer.Ordinal);

    public static BadgeBattleStatReport Rejected(string reason) => new() { CreditStats = false, Reason = reason };
}

/// <summary>
/// Validates a client's /battle/result body against what the server issued and assigned, producing the stat set that
/// may be credited. It never throws and never trusts a field it did not bound.
/// </summary>
public static class BadgeBattleStatEvaluator
{
    // AllowFraction: the client may send a float (metres); it is floored. SkipOutsideMode: a field that is irrelevant
    // in the match mode is not even read, so an odd value there cannot void the other stats.
    private sealed record StatField(string Json, string Stat, long Cap, int? OnlyInRuleType,
        bool AllowFraction = false, bool SkipOutsideMode = false);

    // Mode gating: a stat is credited only in the mode whose objective produces it.
    private static readonly StatField[] Fields =
    [
        new("killCount", BadgeStatNames.Kills, BadgeStatLimits.MaxKills, null),
        new("killedCount", BadgeStatNames.Deaths, BadgeStatLimits.MaxDeaths, null),
        new("acquiredCrystalCount", BadgeStatNames.CrystalsCollected, BadgeStatLimits.MaxCrystalsAcquired, 1),
        new("releasedCrystalCount", BadgeStatNames.CrystalsReleased, BadgeStatLimits.MaxCrystalsReleased, 1),
        new("guardianReleasedCrystalCount", BadgeStatNames.GuardianReleasedCrystals, BadgeStatLimits.MaxGuardianReleasedCrystals, 1),
        new("flagGoalCount", BadgeStatNames.FlagGoals, BadgeStatLimits.MaxFlagGoals, 2),
        new("enemyFlagDropCount", BadgeStatNames.FlagDrops, BadgeStatLimits.MaxEnemyFlagDrops, 2),
        new("ballShootPoint", BadgeStatNames.BallGoals, BadgeStatLimits.MaxBallGoals, 3, SkipOutsideMode: true),
        new("ballDropCount", BadgeStatNames.BallDrops, BadgeStatLimits.MaxBallDrops, 3, SkipOutsideMode: true),
        new("movingDistance", BadgeStatNames.MovingDistance, BadgeStatLimits.MaxMovingDistanceMetres, null, AllowFraction: true),
    ];

    public static BadgeBattleStatReport Evaluate(
        string? resultJson, BattleMatchmakingService.BadgeBattleEntryContext? context, DateTimeOffset now)
    {
        try
        {
            return EvaluateCore(resultJson, context, now);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException
                                       or OverflowException or ArgumentException)
        {
            return BadgeBattleStatReport.Rejected("malformed_body");
        }
    }

    private static BadgeBattleStatReport EvaluateCore(
        string? resultJson, BattleMatchmakingService.BadgeBattleEntryContext? context, DateTimeOffset now)
    {
        if (context is null) return BadgeBattleStatReport.Rejected("no_entry");
        if (context.BattleRuleType is not (>= 1 and <= 3)) return BadgeBattleStatReport.Rejected("mode_not_counted");
        if (context.BattleId is null || context.RoomFinalizedUtc is not { } finalized || context.RoomBattleRuleId is null)
            return BadgeBattleStatReport.Rejected("no_room");
        if (context.RoomBattleRuleId != context.BattleRuleId) return BadgeBattleStatReport.Rejected("room_rule_mismatch");
        if (context.OwnTeamType is not (0 or 1)) return BadgeBattleStatReport.Rejected("not_in_roster");

        var entryAge = now - new DateTimeOffset(DateTime.SpecifyKind(context.EntryCreatedUtc, DateTimeKind.Utc));
        if (entryAge > TimeSpan.FromSeconds(BadgeStatLimits.MaxEntryAgeSeconds)) return BadgeBattleStatReport.Rejected("entry_too_old");
        var sinceRoomClosed = now - new DateTimeOffset(DateTime.SpecifyKind(finalized, DateTimeKind.Utc));
        if (sinceRoomClosed < TimeSpan.FromSeconds(BadgeStatLimits.MinBattleSeconds)) return BadgeBattleStatReport.Rejected("too_early");

        if (string.IsNullOrWhiteSpace(resultJson)) return BadgeBattleStatReport.Rejected("empty_body");
        using var document = JsonDocument.Parse(resultJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return BadgeBattleStatReport.Rejected("body_not_object");

        // The entry id in the body must be the one the server resolved the context for.
        if (!root.TryGetProperty("battleEntryId", out var entryProp) || entryProp.ValueKind != JsonValueKind.String
            || entryProp.GetString() != context.BattleEntryId)
            return BadgeBattleStatReport.Rejected("entry_mismatch");

        if (!TryReadUsers(root, context.UserId, out var mvp, out var flagged, out var userProblem))
            return BadgeBattleStatReport.Rejected(userProblem);

        var report = new BadgeBattleStatReport
        {
            CreditStats = true,
            Reason = "ok",
            BattleRuleType = context.BattleRuleType.Value,
            KickerId = context.KickerId,
            QueuedAsParty = context.QueuedAsParty
        };

        // The team outcome is decided from the scores alone, so a capped stat field (which voids the stat set below)
        // does not hide a valid result from rank progression.
        var teamScoresOk = TryReadTeamScores(root, context.OwnTeamType.Value, out var outcome, out var teamProblem);
        report.Outcome = teamScoresOk ? outcome : BadgeBattleOutcome.Undetermined;
        report.OutcomeReason = !teamScoresOk ? teamProblem
            : outcome == BadgeBattleOutcome.Undetermined ? "no_team_score" : "";
        report.CallerFlagged = flagged;

        // Bound and mode-gate every stat before crediting any: one bad field voids the whole set.
        foreach (var field in Fields)
        {
            if (field.SkipOutsideMode && field.OnlyInRuleType is { } gate && gate != report.BattleRuleType) continue;
            if (!TryReadCount(root, field.Json, field.Cap, out var value, out var problem, field.AllowFraction))
            {
                var rejected = BadgeBattleStatReport.Rejected(problem);
                rejected.Outcome = report.Outcome;
                rejected.OutcomeReason = report.OutcomeReason;
                rejected.CallerFlagged = flagged;
                return rejected;
            }
            if (field.OnlyInRuleType is { } onlyIn && onlyIn != report.BattleRuleType) continue;
            if (value > 0) report.Stats[field.Stat] = value;
        }

        if (!teamScoresOk)
            return BadgeBattleStatReport.Rejected(teamProblem);
        bool? won = outcome switch
        {
            BadgeBattleOutcome.Win => true,
            BadgeBattleOutcome.Loss or BadgeBattleOutcome.Tie => false,
            _ => null
        };

        // A breakaway/bad-connection caller left or lost the link mid-match: their win is not credited and the
        // outcome neither extends nor breaks a streak. Their bounded counters still are.
        report.Won = flagged ? null : won;
        if (mvp) report.Stats[BadgeStatNames.Mvp] = 1;
        return report;
    }

    private static bool TryReadCount(JsonElement obj, string name, long cap, out long value, out string problem, bool allowFraction = false)
    {
        value = 0;
        problem = "";
        if (!obj.TryGetProperty(name, out var prop) || prop.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return true; // absent: the client simply did not report it
        if (prop.ValueKind == JsonValueKind.Number && allowFraction && !prop.TryGetInt64(out value))
        {
            // A finite, non-negative float is floored; anything else (NaN cannot occur in JSON, huge values) is malformed.
            if (prop.TryGetDouble(out var real) && double.IsFinite(real))
            {
                if (real < 0) { problem = "negative:" + name; return false; }
                if (real > cap) { problem = "cap:" + name; return false; }
                value = (long)Math.Floor(real);
                return true;
            }
        }
        if (prop.ValueKind != JsonValueKind.Number || !prop.TryGetInt64(out value))
        {
            problem = "malformed:" + name;
            return false;
        }
        if (value < 0)
        {
            problem = "negative:" + name;
            return false;
        }
        if (value > cap)
        {
            problem = "cap:" + name;
            return false;
        }
        return true;
    }

    private static bool TryReadFlag(JsonElement obj, string name, out bool value)
    {
        value = false;
        if (!obj.TryGetProperty(name, out var prop) || prop.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return true;
        if (prop.ValueKind == JsonValueKind.True) { value = true; return true; }
        return prop.ValueKind == JsonValueKind.False;
    }

    private static bool TryReadUsers(
        JsonElement root, string callerUserId, out bool callerIsMvp, out bool callerFlagged, out string problem)
    {
        callerIsMvp = false;
        callerFlagged = false;
        problem = "";
        if (!root.TryGetProperty("userScoreList", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            problem = "no_user_score";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var mvpFlags = 0;
        var callerSeen = false;
        foreach (var user in list.EnumerateArray())
        {
            if (seen.Count >= BadgeStatLimits.MaxListedPlayers) { problem = "too_many_players"; return false; }
            if (user.ValueKind != JsonValueKind.Object) { problem = "malformed:userScoreList"; return false; }
            var userId = ReadUserId(user);
            if (userId is null || !seen.Add(userId)) { problem = "malformed:userId"; return false; }
            if (!TryReadFlag(user, "mvpFlag", out var mvp) || !TryReadFlag(user, "aiFlag", out var ai)
                || !TryReadFlag(user, "breakawayFlag", out var breakaway)
                || !TryReadFlag(user, "badConnectionFlag", out var badConnection))
            {
                problem = "malformed:flags";
                return false;
            }
            if (mvp) mvpFlags++;
            if (userId != callerUserId) continue;
            callerSeen = true;
            if (ai) { problem = "caller_ai"; return false; }
            callerIsMvp = mvp;
            callerFlagged = breakaway || badConnection;
        }

        if (mvpFlags > 1) { problem = "multiple_mvp"; return false; }
        if (!callerSeen) { problem = "caller_missing"; return false; }
        return true;
    }

    private static string? ReadUserId(JsonElement user)
    {
        if (!user.TryGetProperty("userId", out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.String) return string.IsNullOrWhiteSpace(prop.GetString()) ? null : prop.GetString();
        return prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static bool TryReadTeamScores(JsonElement root, int ownTeam, out BadgeBattleOutcome outcome, out string problem)
    {
        outcome = BadgeBattleOutcome.Undetermined;
        problem = "";
        if (!root.TryGetProperty("teamScoreList", out var list) || list.ValueKind != JsonValueKind.Array) return true;

        var scores = new Dictionary<int, long>();
        foreach (var team in list.EnumerateArray())
        {
            if (scores.Count >= BadgeStatLimits.MaxTeams || team.ValueKind != JsonValueKind.Object)
            {
                problem = "malformed:teamScoreList";
                return false;
            }
            if (!team.TryGetProperty("teamType", out var typeProp) || typeProp.ValueKind != JsonValueKind.Number
                || !typeProp.TryGetInt32(out var teamType) || teamType is not (0 or 1) || scores.ContainsKey(teamType))
            {
                problem = "malformed:teamType";
                return false;
            }
            if (!TryReadCount(team, "score", BadgeStatLimits.MaxTeamScore, out var score, out problem)) return false;
            scores[teamType] = score;
        }

        // Without both sides the outcome is undecidable; a tie is not a win.
        if (scores.TryGetValue(ownTeam, out var own) && scores.TryGetValue(1 - ownTeam, out var other))
            outcome = own > other ? BadgeBattleOutcome.Win : own < other ? BadgeBattleOutcome.Loss : BadgeBattleOutcome.Tie;
        return true;
    }
}

/// <summary>Builds the non-sensitive log summary of a /battle/result body: numeric stat fields with their JSON type.</summary>
public static class BattleResultLogSummary
{
    private static readonly string[] Fields = { "movingDistance", "ballShootPoint", "ballDropCount", "killCount" };

    public static string ShortHash(object? id)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id?.ToString() ?? ""));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

    public static string Describe(string? resultBodyJson, int battleRuleType)
    {
        var parts = new List<string> { $"battleRuleType={battleRuleType}" };
        try
        {
            if (!string.IsNullOrEmpty(resultBodyJson))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(resultBodyJson);
                foreach (var field in Fields)
                {
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                        && doc.RootElement.TryGetProperty(field, out var v))
                    {
                        var raw = v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetRawText() : "<non-numeric>";
                        parts.Add($"{field}={raw}({v.ValueKind})");
                    }
                    else parts.Add($"{field}=absent");
                }
                return string.Join(' ', parts);
            }
        }
        catch (System.Text.Json.JsonException) { }
        parts.Add("body=unparseable");
        return string.Join(' ', parts);
    }
}
