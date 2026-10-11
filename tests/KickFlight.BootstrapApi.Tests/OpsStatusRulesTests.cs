using KickFlight.BootstrapApi.Ops;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The participant status / match outcome rules are pure functions of the facts, so every rule is pinned here
// without a server: the sweep and the event handlers both go through exactly these functions.
public sealed class OpsStatusRulesTests
{
    private static readonly DateTime T0 = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly BattleFacts Started = new()
    {
        CreatedAt = T0, FinalizedAt = T0.AddSeconds(40), FirstStartAt = T0.AddSeconds(45), BattleTimeSeconds = 180
    };
    private static readonly ParticipantFacts Playing = new()
    {
        EntryAt = T0, Assigned = true, StartAt = T0.AddSeconds(45), LastSeenAt = T0.AddSeconds(45)
    };

    [Fact]
    public void End_or_result_completes_and_reconnects_are_remembered()
    {
        Assert.Equal("completed", OpsStatusRules.EvaluateParticipant(Playing with { ResultAt = T0.AddMinutes(4) }, Started, T0.AddMinutes(4)));
        Assert.Equal("completed", OpsStatusRules.EvaluateParticipant(Playing with { EndAt = T0.AddMinutes(4) }, Started, T0.AddMinutes(4)));
        Assert.Equal("reconnected_completed",
            OpsStatusRules.EvaluateParticipant(Playing with { EndAt = T0.AddMinutes(4), ReconnectBegins = 2 }, Started, T0.AddMinutes(4)));
        // A late /battle/result still wins over an earlier reconnect failure: the status is derived, not latched.
        Assert.Equal("reconnected_completed", OpsStatusRules.EvaluateParticipant(
            Playing with { ReconnectBegins = 1, ReconnectFailedCause = "Timeout", ResultAt = T0.AddMinutes(5) }, Started, T0.AddMinutes(5)));
    }

    [Fact]
    public void In_match_failures_are_reported_at_once()
    {
        Assert.Equal("reconnect_failed:DisconnectByServer",
            OpsStatusRules.EvaluateParticipant(Playing with { ReconnectFailedCause = "DisconnectByServer" }, Started, T0.AddMinutes(1)));
        Assert.Equal("disconnected:ClientTimeout",
            OpsStatusRules.EvaluateParticipant(Playing with { DisconnectCause = "ClientTimeout" }, Started, T0.AddMinutes(1)));
        Assert.Equal("left", OpsStatusRules.EvaluateParticipant(Playing with { LeftAt = T0.AddMinutes(1) }, Started, T0.AddMinutes(1)));
    }

    [Fact]
    public void Silent_drop_waits_for_battle_time_plus_120_seconds()
    {
        var deadline = Playing.StartAt!.Value.AddSeconds(180 + 120);
        Assert.Equal("playing", OpsStatusRules.EvaluateParticipant(Playing, Started, deadline.AddSeconds(-1)));
        Assert.Equal("dropped_silent", OpsStatusRules.EvaluateParticipant(Playing, Started, deadline));
        // Unknown battle (no rule time): the default battle time applies.
        Assert.Equal("playing", OpsStatusRules.EvaluateParticipant(Playing, null, Playing.StartAt.Value.AddSeconds(300 + 119)));
    }

    [Fact]
    public void A_relaunch_within_five_minutes_of_the_last_sign_of_life_flags_the_drop()
    {
        var after = Playing.StartAt!.Value.AddMinutes(10);
        var lastSeen = Playing.StartAt.Value.AddSeconds(90);
        Assert.Equal("dropped_silent+relaunched", OpsStatusRules.EvaluateParticipant(
            Playing with { LastSeenAt = lastSeen, RelaunchAt = lastSeen.AddMinutes(2) }, Started, after));
        Assert.Equal("dropped_silent", OpsStatusRules.EvaluateParticipant(
            Playing with { LastSeenAt = lastSeen, RelaunchAt = lastSeen.AddMinutes(6) }, Started, after));
        // An /auth before the match started is the normal launch, not a relaunch.
        Assert.Equal("dropped_silent", OpsStatusRules.EvaluateParticipant(
            Playing with { RelaunchAt = T0.AddSeconds(-30) }, Started, after));
    }

    [Fact]
    public void Matching_statuses_have_a_fixed_precedence()
    {
        var waiting = new ParticipantFacts { EntryAt = T0 };
        var room = new BattleFacts { CreatedAt = T0 };
        Assert.Equal("matching", OpsStatusRules.EvaluateParticipant(waiting, room, T0.AddSeconds(10)));
        Assert.Equal("matching_timeout", OpsStatusRules.EvaluateParticipant(waiting with { TimeoutAt = T0, CancelledAt = T0 }, room, T0));
        // The stream closes before the client's /battle/cancel arrives; the cancel still decides the status.
        Assert.Equal("cancelled", OpsStatusRules.EvaluateParticipant(
            waiting with { MatchingFailedCause = "stream_closed", CancelledAt = T0.AddSeconds(1) }, room, T0.AddSeconds(1)));
        Assert.Equal("matching_failed:stream_closed",
            OpsStatusRules.EvaluateParticipant(waiting with { MatchingFailedCause = "stream_closed" }, room, T0));
        Assert.Equal("matching_failed:PhotonTimeout",
            OpsStatusRules.EvaluateParticipant(waiting with { DisconnectCause = "PhotonTimeout" }, room, T0));
        Assert.Equal("requeued", OpsStatusRules.EvaluateParticipant(waiting with { Requeued = true, CancelledAt = T0 }, room, T0));
    }

    [Fact]
    public void Entries_without_a_room_or_a_start_time_out()
    {
        var waiting = new ParticipantFacts { EntryAt = T0 };
        Assert.Equal("matching", OpsStatusRules.EvaluateParticipant(waiting, null, T0.AddMinutes(4)));
        Assert.Equal("matching_failed:no_room", OpsStatusRules.EvaluateParticipant(waiting, null, T0.AddMinutes(5)));

        var assigned = waiting with { Assigned = true };
        var finalized = new BattleFacts { CreatedAt = T0, FinalizedAt = T0.AddSeconds(40) };
        Assert.Equal("assigned", OpsStatusRules.EvaluateParticipant(assigned, finalized, T0.AddMinutes(3)));
        Assert.Equal("matching_failed:never_started", OpsStatusRules.EvaluateParticipant(assigned, finalized, T0.AddSeconds(40).AddMinutes(3)));
        Assert.Equal("matching_failed:room_dropped",
            OpsStatusRules.EvaluateParticipant(waiting, new BattleFacts { CreatedAt = T0, Dropped = true }, T0.AddSeconds(5)));
    }

    [Fact]
    public void Outcome_follows_the_assigned_humans()
    {
        static (string, bool, bool) H(string status, bool started = true, bool assigned = true) => (status, assigned, started);

        Assert.Null(OpsStatusRules.EvaluateOutcome(new BattleFacts { CreatedAt = T0 }, [H("matching", false)]));
        Assert.Null(OpsStatusRules.EvaluateOutcome(Started, [H("completed"), H("playing")]));
        Assert.Equal("clean", OpsStatusRules.EvaluateOutcome(Started, [H("completed"), H("reconnected_completed")]));
        Assert.Equal("partial", OpsStatusRules.EvaluateOutcome(Started, [H("completed"), H("dropped_silent")]));
        Assert.Equal("partial", OpsStatusRules.EvaluateOutcome(Started, [H("completed"), H("matching_failed:never_started", started: false)]));
        Assert.Equal("abandoned", OpsStatusRules.EvaluateOutcome(Started, [H("reconnect_failed:x"), H("dropped_silent+relaunched")]));
        // A human excluded from the roster (requeued) does not count either way.
        Assert.Equal("clean", OpsStatusRules.EvaluateOutcome(Started, [H("completed"), H("requeued", false, false)]));
        Assert.Equal("never_started", OpsStatusRules.EvaluateOutcome(
            Started with { FirstStartAt = null }, [H("matching_failed:never_started", started: false)]));
        Assert.Equal("never_started", OpsStatusRules.EvaluateOutcome(new BattleFacts { CreatedAt = T0, Dropped = true }, [H("cancelled", false, false)]));
    }

    [Fact]
    public void Client_reports_are_fingerprinted_on_type_and_first_three_frames()
    {
        const string first = "[Analysis] NullReferenceException: Object reference not set to an instance of an object\n" +
                             "  at Colorful.PlayerCharacter.Initialize (System.Int32 id) [0x0001c] in <5d1f>:0\n" +
                             "  at Colorful.GameManager.Setup () [0x00010] in <5d1f>:0\n" +
                             "  at Colorful.GameManager.Start () [0x00002] in <5d1f>:0\n" +
                             "  at UnityEngine.SetupCoroutine.InvokeMoveNext () [0x00000] in <aa>:0";
        var second = first.Replace("[0x0001c]", "[0x00020]").Replace("InvokeMoveNext", "Other");
        var a = OpsFingerprint.Classify(first);
        var b = OpsFingerprint.Classify(second);
        Assert.Equal("client_exception", a.Kind);
        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.Equal(16, a.Fingerprint.Length);
        Assert.StartsWith("NullReferenceException\nat Colorful.PlayerCharacter.Initialize (System.Int32 id)", a.Signature);

        var other = OpsFingerprint.Classify(first.Replace("GameManager.Setup", "GameManager.Load"));
        Assert.NotEqual(a.Fingerprint, other.Fingerprint);

        var anomaly1 = OpsFingerprint.Classify("[Analysis] ping spike 812 ms on region 3");
        var anomaly2 = OpsFingerprint.Classify("[Analysis] ping spike 95 ms on region 1");
        Assert.Equal("client_anomaly", anomaly1.Kind);
        Assert.Equal(anomaly1.Fingerprint, anomaly2.Fingerprint);
        Assert.True(OpsFingerprint.IsClientReport("[Analysis] x"));
        Assert.False(OpsFingerprint.IsClientReport("NameServer ping failed"));
    }
}
