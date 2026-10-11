using System.Globalization;
using System.Text.Json.Nodes;

namespace KickFlight.BootstrapApi.Ops;

/// <summary>
/// The Health dashboard's numbers, computed in C# over the rows of the window rather than in SQL so the PostgreSQL
/// and NDJSON stores give identical answers (this server sees hundreds of matches a day, not millions).
/// </summary>
public static class OpsReports
{
    public static readonly string[] WindowNames = ["deploy", "restart", "24h", "7d"];
    private static readonly string[] ErrorKinds = [OpsFingerprint.ExceptionKind, OpsFingerprint.AnomalyKind];
    private const int ProblemMatchLimit = 100;
    private const int BreakdownLimit = 25;

    public sealed record Window(string Name, DateTime Since, DateTime Until, IReadOnlyList<ServerRunRow> Runs);

    /// <summary>
    /// deploy (default): since the first server run with the current KF_RELEASE_SHA, so restarts of the same release
    /// keep counting; restart: since this process started; 24h / 7d: rolling. Null for an unknown name.
    /// </summary>
    public static async Task<Window?> ResolveWindowAsync(OpsTelemetryService ops, string? name, DateTime now,
        CancellationToken cancellationToken)
    {
        name = string.IsNullOrWhiteSpace(name) ? "deploy" : name.Trim().ToLowerInvariant();
        var runs = await ops.Store.LoadRunsAsync(now - OpsTelemetryService.Retention, cancellationToken);
        DateTime since;
        switch (name)
        {
            case "deploy":
                since = ops.ReleaseSha == "unknown"
                    ? ops.StartedAt
                    : runs.Where(run => run.ReleaseSha == ops.ReleaseSha).Select(run => run.StartedAt)
                        .DefaultIfEmpty(ops.StartedAt).Min();
                if (since > ops.StartedAt) since = ops.StartedAt;
                break;
            case "restart": since = ops.StartedAt; break;
            case "24h": since = now - TimeSpan.FromHours(24); break;
            case "7d": since = now - TimeSpan.FromDays(7); break;
            default: return null;
        }
        return new Window(name, since, now, runs.Where(run => run.StartedAt >= since || run.Id == ops.RunId).ToList());
    }

    public static object BuildSummary(OpsTelemetryService ops, Window window, IReadOnlyList<BattleRow> battles,
        IReadOnlyList<ParticipantRow> participants, IReadOnlyDictionary<string, int> eventKinds)
    {
        var byBattle = participants.ToLookup(p => p.BattleId, StringComparer.Ordinal);
        int Outcome(string outcome) => battles.Count(b => b.Outcome == outcome);
        var running = battles.Count(b => b.Outcome is null && b.FirstStartAt is not null);

        var totals = new
        {
            matches = Outcome(OpsStatusRules.Clean) + Outcome(OpsStatusRules.Partial) + Outcome(OpsStatusRules.Abandoned) + running,
            running,
            matching = battles.Count(b => b.Outcome is null && b.FirstStartAt is null),
            clean = Outcome(OpsStatusRules.Clean),
            partial = Outcome(OpsStatusRules.Partial),
            abandoned = Outcome(OpsStatusRules.Abandoned),
            neverStarted = Outcome(OpsStatusRules.NeverStarted),
            custom = battles.Count(b => b.Custom),
            entries = participants.Count,
            players = participants.Count(p => p.StartAt is not null),
            completedPlayers = participants.Count(p => OpsStatusRules.IsCompleted(p.Status)),
            reconnectedCompleted = participants.Count(p => p.Status == OpsStatusRules.ReconnectedCompleted),
            disconnectedPlayers = participants.Count(p => OpsStatusRules.IsDisconnected(p.Status)),
            droppedSilent = participants.Count(p => p.Status.StartsWith(OpsStatusRules.DroppedSilent, StringComparison.Ordinal)),
            relaunched = participants.Count(p => p.Status.EndsWith(OpsStatusRules.RelaunchedSuffix, StringComparison.Ordinal)),
            reconnectFailed = participants.Count(p => p.Status.StartsWith(OpsStatusRules.ReconnectFailedPrefix, StringComparison.Ordinal)),
            left = participants.Count(p => p.Status == OpsStatusRules.Left),
            matchingFailures = participants.Count(p => OpsStatusRules.IsMatchingFailure(p.Status)),
            matchingTimeouts = participants.Count(p => p.Status == OpsStatusRules.MatchingTimeout),
            cancelled = participants.Count(p => p.Status == OpsStatusRules.Cancelled),
            exceptions = eventKinds.GetValueOrDefault(OpsFingerprint.ExceptionKind),
            anomalies = eventKinds.GetValueOrDefault(OpsFingerprint.AnomalyKind),
            reconnectBegins = participants.Sum(p => p.ReconnectBegins)
        };

        var problems = battles
            .Select(b => (Battle: b, People: byBattle[b.BattleId].ToList()))
            .Where(x => IsProblem(x.Battle, x.People))
            .OrderByDescending(x => x.Battle.CreatedAt)
            .Take(ProblemMatchLimit)
            .Select(x => new
            {
                battleId = x.Battle.BattleId,
                createdAt = x.Battle.CreatedAt,
                ruleId = x.Battle.RuleId,
                matchType = x.Battle.MatchType,
                fieldId = x.Battle.FieldId,
                humans = x.Battle.Humans,
                bots = x.Battle.Bots,
                custom = x.Battle.Custom,
                outcome = x.Battle.Outcome ?? (x.Battle.FirstStartAt is null ? "matching" : "running"),
                participants = x.People.Select(p => new
                {
                    userId = p.UserId,
                    status = p.Status,
                    kickerId = p.KickerId,
                    appVersion = p.AppVersion,
                    device = p.DeviceName,
                    os = p.OsVersion,
                    isDiag = p.IsDiag,
                    reconnectBegins = p.ReconnectBegins,
                    exceptionCount = p.ExceptionCount
                }).ToList()
            })
            .ToList();

        return new
        {
            window = WindowJson(ops, window),
            totals,
            statuses = participants.GroupBy(p => p.Status, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            breakdowns = new
            {
                appVersion = PlayerBreakdown(participants, p => p.AppVersion),
                device = PlayerBreakdown(participants, p => p.DeviceName),
                os = PlayerBreakdown(participants, p => p.OsVersion),
                rule = MatchBreakdown(battles, b => b.RuleId.ToString(CultureInfo.InvariantCulture)),
                field = MatchBreakdown(battles, b => b.FieldId.ToString(CultureInfo.InvariantCulture))
            },
            eventKinds = eventKinds.OrderByDescending(kv => kv.Value)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            problemMatches = problems,
            telemetry = ops.Stats
        };
    }

    public static object BuildExceptions(OpsTelemetryService ops, Window window, IReadOnlyList<ClientEventRow> events)
    {
        var groups = events
            .Where(e => ErrorKinds.Contains(e.Kind))
            .GroupBy(e => e.Fingerprint ?? e.Kind, StringComparer.Ordinal)
            .Select(g =>
            {
                var latest = g.OrderByDescending(e => e.At).First();
                var users = g.Where(e => e.UserId is not null).Select(e => e.UserId!).Distinct(StringComparer.Ordinal).ToList();
                return new
                {
                    fingerprint = g.Key,
                    kind = latest.Kind,
                    signature = Text(latest.Payload, "signature"),
                    count = g.Count(),
                    userCount = users.Count,
                    users = users.Take(20).ToList(),
                    devices = Top(g.Select(e => e.Device)),
                    os = Top(g.Select(e => e.Os)),
                    appVersions = Top(g.Select(e => e.AppVersion)),
                    battles = g.Where(e => e.BattleId is not null).Select(e => e.BattleId!).Distinct(StringComparer.Ordinal).Take(10).ToList(),
                    firstAt = g.Min(e => e.At),
                    lastAt = latest.At,
                    sample = Sample(latest.Payload)
                };
            })
            .OrderByDescending(g => g.count)
            .ThenByDescending(g => g.lastAt)
            .ToList();
        return new
        {
            window = WindowJson(ops, window),
            total = groups.Sum(g => g.count),
            groups
        };
    }

    public static object BuildBattle(BattleRow? battle, IReadOnlyList<ParticipantRow> participants, IReadOnlyList<ClientEventRow> events) => new
    {
        battle,
        participants,
        // One line per event, oldest first: the lifecycle events (entry, room_join, assigned, start, end, result,
        // cancel, battle_disconnect/timeout/leaving) and every /analysis entry of the match.
        timeline = events.OrderBy(e => e.At).ThenBy(e => e.Id).Select(e => new
        {
            at = e.At,
            userId = e.UserId,
            kind = e.Kind,
            appVersion = e.AppVersion,
            device = e.Device,
            fingerprint = e.Fingerprint,
            payload = e.Payload
        }).ToList()
    };

    private static bool IsProblem(BattleRow battle, IReadOnlyList<ParticipantRow> people) =>
        battle.Outcome is OpsStatusRules.Partial or OpsStatusRules.Abandoned
        || people.Any(p => p.ReconnectBegins > 0 || p.ExceptionCount > 0
                           || OpsStatusRules.IsDisconnected(p.Status) || p.Status == OpsStatusRules.Left
                           || p.Status.StartsWith(OpsStatusRules.MatchingFailedPrefix, StringComparison.Ordinal)
                           || p.Status == OpsStatusRules.MatchingTimeout);

    private static object WindowJson(OpsTelemetryService ops, Window window) => new
    {
        name = window.Name,
        since = window.Since,
        until = window.Until,
        releaseSha = ops.ReleaseSha,
        runId = ops.RunId,
        runStartedAt = ops.StartedAt,
        runs = window.Runs
    };

    private static List<object> PlayerBreakdown(IReadOnlyList<ParticipantRow> participants, Func<ParticipantRow, string> key) =>
        participants.GroupBy(p => Label(key(p)), StringComparer.Ordinal)
            .Select(g => new
            {
                key = g.Key,
                entries = g.Count(),
                players = g.Count(p => p.StartAt is not null),
                completed = g.Count(p => OpsStatusRules.IsCompleted(p.Status)),
                disconnected = g.Count(p => OpsStatusRules.IsDisconnected(p.Status)),
                matchingFailures = g.Count(p => OpsStatusRules.IsMatchingFailure(p.Status)),
                reconnectBegins = g.Sum(p => p.ReconnectBegins),
                exceptions = g.Sum(p => p.ExceptionCount)
            })
            .OrderByDescending(row => row.entries)
            .Take(BreakdownLimit)
            .Cast<object>()
            .ToList();

    private static List<object> MatchBreakdown(IReadOnlyList<BattleRow> battles, Func<BattleRow, string> key) =>
        battles.GroupBy(key, StringComparer.Ordinal)
            .Select(g => new
            {
                key = g.Key,
                matches = g.Count(b => b.Outcome != OpsStatusRules.NeverStarted && (b.Outcome is not null || b.FirstStartAt is not null)),
                clean = g.Count(b => b.Outcome == OpsStatusRules.Clean),
                partial = g.Count(b => b.Outcome == OpsStatusRules.Partial),
                abandoned = g.Count(b => b.Outcome == OpsStatusRules.Abandoned),
                neverStarted = g.Count(b => b.Outcome == OpsStatusRules.NeverStarted)
            })
            .OrderByDescending(row => row.matches)
            .Take(BreakdownLimit)
            .Cast<object>()
            .ToList();

    private static List<object> Top(IEnumerable<string> values) =>
        values.GroupBy(Label, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => (object)new { key = g.Key, count = g.Count() })
            .ToList();

    private static string Label(string value) => string.IsNullOrEmpty(value) ? "(none)" : value;

    private static string Text(JsonObject payload, string name) =>
        payload.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : "";

    private static string Sample(JsonObject payload)
    {
        var cause = payload["value"] is JsonObject value && value["cause"] is JsonValue causeNode
                    && causeNode.TryGetValue<string>(out var text)
            ? text
            : payload["value"]?.ToJsonString() ?? "";
        return cause.Length <= 2000 ? cause : cause[..2000];
    }
}
