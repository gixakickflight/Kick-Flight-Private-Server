namespace KickFlight.BootstrapApi.Ops;

/// <summary>What the server saw of one human in one match. Every field is a fact; the status is derived from them.</summary>
public sealed record ParticipantFacts
{
    public DateTime EntryAt { get; init; }
    /// <summary>Part of the room's final roster (Stage 2/3 went out with this player in it).</summary>
    public bool Assigned { get; init; }
    /// <summary>Excluded from a finalized room with its party and re-queued into another one.</summary>
    public bool Requeued { get; init; }
    public DateTime? StartAt { get; init; }
    public DateTime? EndAt { get; init; }
    public DateTime? ResultAt { get; init; }
    public int ReconnectBegins { get; init; }
    public string? ReconnectFailedCause { get; init; }
    /// <summary>The cause of a /battle/disconnect call.</summary>
    public string? DisconnectCause { get; init; }
    /// <summary>/battle/leaving.</summary>
    public DateTime? LeftAt { get; init; }
    public DateTime? CancelledAt { get; init; }
    /// <summary>/battle/timeout: the client's matching countdown reached 0 before the full roster arrived.</summary>
    public DateTime? TimeoutAt { get; init; }
    /// <summary>A server-side matchmaking failure (stream closed without a cancel, maintenance, restart...).</summary>
    public string? MatchingFailedCause { get; init; }
    /// <summary>
    /// Latest sign of life from inside the match: /battle/start and the battle's /analysis entries. Ordinary API
    /// requests do not count, since the ones a relaunched app sends would otherwise hide the relaunch.
    /// </summary>
    public DateTime? LastSeenAt { get; init; }
    /// <summary>Latest /auth/index or /boot/index from this player (by user, or by address + device for /boot).</summary>
    public DateTime? RelaunchAt { get; init; }
}

/// <summary>What the server saw of the match itself.</summary>
public sealed record BattleFacts
{
    public DateTime CreatedAt { get; init; }
    /// <summary>The matchmaking window closed and the bot-filled roster was built.</summary>
    public DateTime? FinalizedAt { get; init; }
    /// <summary>Every waiting human left before the window closed.</summary>
    public bool Dropped { get; init; }
    public DateTime? FirstStartAt { get; init; }
    public int BattleTimeSeconds { get; init; } = OpsStatusRules.DefaultBattleTimeSeconds;
}

/// <summary>
/// Participant status and match outcome. Pure functions of the facts and "now", so the 60 s sweep and every event
/// handler agree, and a later event (a /battle/result that arrives after a reconnect failure, a cancel that arrives
/// after the stream already closed) simply re-derives the status instead of depending on arrival order.
///
/// Participant statuses:
///   open:   matching (waiting for the window), assigned (roster sent, no /battle/start yet), playing
///   final:  completed / reconnected_completed (/battle/end or /battle/result arrived; reconnected = it had
///           GameReconnectBegin entries), reconnect_failed:&lt;cause&gt; (GameReconnectFailed), disconnected:&lt;cause&gt;
///           (/battle/disconnect during the match), left (/battle/leaving), dropped_silent (nothing until battle
///           time + 120 s; "+relaunched" when the player re-authenticated within 5 min of its last sign of life,
///           i.e. the app most likely crashed), matching_timeout (/battle/timeout), cancelled (/battle/cancel),
///           matching_failed:&lt;cause&gt;, requeued (not a failure: the party moved to the next room)
/// Match outcomes: clean (every assigned human completed), partial, abandoned (nobody completed), never_started
/// (no human ever reached /battle/start, or the room was dropped before its window closed).
/// </summary>
public static class OpsStatusRules
{
    public const string Matching = "matching";
    public const string Assigned = "assigned";
    public const string Playing = "playing";
    public const string Completed = "completed";
    public const string ReconnectedCompleted = "reconnected_completed";
    public const string DroppedSilent = "dropped_silent";
    public const string RelaunchedSuffix = "+relaunched";
    public const string MatchingTimeout = "matching_timeout";
    public const string Cancelled = "cancelled";
    public const string Left = "left";
    public const string Requeued = "requeued";
    public const string ReconnectFailedPrefix = "reconnect_failed:";
    public const string DisconnectedPrefix = "disconnected:";
    public const string MatchingFailedPrefix = "matching_failed:";

    public const string Clean = "clean";
    public const string Partial = "partial";
    public const string Abandoned = "abandoned";
    public const string NeverStarted = "never_started";

    public const int DefaultBattleTimeSeconds = 300;
    /// <summary>A started player who sends nothing until battle time + this is a silent drop.</summary>
    public static readonly TimeSpan SilentDropGrace = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan RelaunchWindow = TimeSpan.FromMinutes(5);
    /// <summary>An entry that never reached a room (no GetAssignments stream) is given up after this.</summary>
    public static readonly TimeSpan NoRoomTimeout = TimeSpan.FromMinutes(5);
    /// <summary>An assigned player that never calls /battle/start (Photon join failed) is given up after this.</summary>
    public static readonly TimeSpan NoStartTimeout = TimeSpan.FromMinutes(3);

    public static bool IsOpen(string status) => status is Matching or Assigned or Playing;

    public static bool IsCompleted(string status) => status is Completed or ReconnectedCompleted;

    /// <summary>Lost during the match: the "disconnected players" side of completed vs disconnected.</summary>
    public static bool IsDisconnected(string status) =>
        status.StartsWith(ReconnectFailedPrefix, StringComparison.Ordinal)
        || status.StartsWith(DisconnectedPrefix, StringComparison.Ordinal)
        || status.StartsWith(DroppedSilent, StringComparison.Ordinal);

    public static bool IsMatchingFailure(string status) =>
        status is MatchingTimeout or Cancelled || status.StartsWith(MatchingFailedPrefix, StringComparison.Ordinal);

    public static DateTime SilentDropDeadline(DateTime startAt, int battleTimeSeconds) =>
        startAt + TimeSpan.FromSeconds(Math.Max(0, battleTimeSeconds)) + SilentDropGrace;

    public static string EvaluateParticipant(ParticipantFacts p, BattleFacts? battle, DateTime now)
    {
        if (p.EndAt is not null || p.ResultAt is not null)
            return p.ReconnectBegins > 0 ? ReconnectedCompleted : Completed;

        if (p.StartAt is { } start)
        {
            if (!string.IsNullOrEmpty(p.ReconnectFailedCause)) return ReconnectFailedPrefix + p.ReconnectFailedCause;
            if (!string.IsNullOrEmpty(p.DisconnectCause)) return DisconnectedPrefix + p.DisconnectCause;
            if (p.LeftAt is not null) return Left;
            var deadline = SilentDropDeadline(start, battle?.BattleTimeSeconds ?? DefaultBattleTimeSeconds);
            if (now < deadline) return Playing;
            return Relaunched(p, start) ? DroppedSilent + RelaunchedSuffix : DroppedSilent;
        }

        // Never reached /battle/start.
        if (p.Requeued) return Requeued;
        if (p.TimeoutAt is not null) return MatchingTimeout;
        if (p.CancelledAt is not null) return Cancelled;
        if (!string.IsNullOrEmpty(p.MatchingFailedCause)) return MatchingFailedPrefix + p.MatchingFailedCause;
        if (!string.IsNullOrEmpty(p.DisconnectCause)) return MatchingFailedPrefix + p.DisconnectCause;
        if (p.LeftAt is not null) return MatchingFailedPrefix + "left";
        if (battle is null) return now - p.EntryAt >= NoRoomTimeout ? MatchingFailedPrefix + "no_room" : Matching;
        if (battle.Dropped) return MatchingFailedPrefix + "room_dropped";
        if (!p.Assigned)
        {
            if (battle.FinalizedAt is { } closed && now - closed >= NoRoomTimeout) return MatchingFailedPrefix + "not_assigned";
            return Matching;
        }
        var reference = battle.FirstStartAt ?? battle.FinalizedAt ?? p.EntryAt;
        return now - reference >= NoStartTimeout ? MatchingFailedPrefix + "never_started" : Assigned;
    }

    /// <summary>
    /// Null while the match is open: its window has not closed, or a human still has an open status. A dropped room
    /// is never_started at once.
    /// </summary>
    public static string? EvaluateOutcome(BattleFacts battle, IReadOnlyCollection<(string Status, bool Assigned, bool Started)> humans)
    {
        if (battle.Dropped) return NeverStarted;
        if (battle.FinalizedAt is null) return null;
        if (humans.Any(h => IsOpen(h.Status))) return null;
        var assigned = humans.Where(h => h.Assigned).ToList();
        if (assigned.Count == 0 || !assigned.Any(h => h.Started)) return NeverStarted;
        var completed = assigned.Count(h => IsCompleted(h.Status));
        if (completed == assigned.Count) return Clean;
        return completed == 0 ? Abandoned : Partial;
    }

    private static bool Relaunched(ParticipantFacts p, DateTime start)
    {
        if (p.RelaunchAt is not { } relaunch || relaunch <= start) return false;
        var lastSeen = p.LastSeenAt is { } seen && seen > start ? seen : start;
        return relaunch - lastSeen <= RelaunchWindow;
    }
}
