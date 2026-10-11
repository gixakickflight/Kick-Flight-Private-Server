using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using KickFlight.BootstrapApi;
using KickFlight.BootstrapApi.Ops;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The ops telemetry service is a background batch writer over a pluggable store. These tests drive the public
// Record*/Sweep API with a deterministic clock and an in-memory store, then FlushAsync and assert on the rows the
// writer produced: the exact path a request takes without a database or a server.
public sealed class OpsTelemetryServiceTests
{
    private static readonly DateTime T0 = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task End_to_end_lifecycle_records_the_participant_and_the_clean_outcome()
    {
        var (matchmaking, _) = NewMatchmaking();
        var store = new RecordingOpsStore();
        using var service = NewService(store, matchmaking, out _);
        await service.StartAsync(CancellationToken.None);

        var player = NewPlayer("1000001", "entry-1", "ticket-1");
        var room = new BattleMatchmakingService.ActiveBattleRoom { BattleId = "battle-1", BattleRuleId = 7, FieldId = 801 };
        room.HumanPlayers.Add(player);
        Raise(matchmaking, "HumanJoinedRoom", room, player);
        room.AssignedTicketIds.Add("ticket-1");
        room.MatchingInfo.battlePlayerList = [NewSlot("1000001", "entry-1")];
        Raise(matchmaking, "RoomFinalized", room);

        service.RecordBattleStart("1000001", "battle-1", 7, 4, 801, 180, OpsClientInfo.Empty);
        service.RecordBattleEnd("1000001", "battle-1", OpsClientInfo.Empty);
        await service.FlushAsync(TimeSpan.FromSeconds(5));

        var participant = Assert.Single(store.Participants).Value;
        Assert.Equal(OpsStatusRules.Completed, participant.Status);
        Assert.Equal(OpsStatusRules.Clean, Assert.Single(store.Battles).Value.Outcome);
        Assert.Contains(store.Events, e => e.Kind == "room_join");
        Assert.Contains(store.Events, e => e.Kind == "assigned");
        Assert.Contains(store.Events, e => e.Kind == "start");
        Assert.Contains(store.Events, e => e.Kind == "end");
    }

    [Fact]
    public async Task A_requeued_player_is_excluded_from_the_old_room_outcome()
    {
        var (matchmaking, _) = NewMatchmaking();
        var store = new RecordingOpsStore();
        using var service = NewService(store, matchmaking, out _);
        await service.StartAsync(CancellationToken.None);

        var requeued = NewPlayer("1000001", "entry-1", "ticket-1");
        var stays = NewPlayer("1000002", "entry-2", "ticket-2");
        var roomA = new BattleMatchmakingService.ActiveBattleRoom { BattleId = "battle-A", BattleRuleId = 1, FieldId = 101 };
        roomA.HumanPlayers.Add(requeued);
        roomA.HumanPlayers.Add(stays);
        Raise(matchmaking, "HumanJoinedRoom", roomA, requeued);
        Raise(matchmaking, "HumanJoinedRoom", roomA, stays);
        // The requeued party is excluded from the final roster, so only the stayer is assigned.
        roomA.AssignedTicketIds.Add("ticket-2");
        roomA.MatchingInfo.battlePlayerList =
        [
            NewSlot("1000001", "entry-1"), NewSlot("1000002", "entry-2")
        ];
        Raise(matchmaking, "RoomFinalized", roomA);

        // The stayer starts and finishes; the excluded player is pulled into a second room instead.
        service.RecordBattleStart("1000002", "battle-A", 1, null, 101, 180, OpsClientInfo.Empty);
        service.RecordBattleEnd("1000002", "battle-A", OpsClientInfo.Empty);

        var roomB = new BattleMatchmakingService.ActiveBattleRoom { BattleId = "battle-B", BattleRuleId = 1, FieldId = 101 };
        roomB.HumanPlayers.Add(requeued);
        Raise(matchmaking, "HumanJoinedRoom", roomB, requeued);
        await service.FlushAsync(TimeSpan.FromSeconds(5));

        var old = store.Battles["battle-A"];
        var requeuedRow = store.Participants[("battle-A", "1000001")];
        var stayerRow = store.Participants[("battle-A", "1000002")];

        Assert.Equal(OpsStatusRules.Requeued, requeuedRow.Status);
        Assert.Equal(OpsStatusRules.Completed, stayerRow.Status);
        // The requeued player must not count as an assigned non-completer of battle A.
        Assert.Equal(OpsStatusRules.Clean, old.Outcome);
    }

    [Fact]
    public async Task A_stream_that_reattaches_clears_the_stream_closed_matching_failure()
    {
        var (matchmaking, _) = NewMatchmaking();
        var store = new RecordingOpsStore();
        using var service = NewService(store, matchmaking, out _);
        await service.StartAsync(CancellationToken.None);

        var player = NewPlayer("1000001", "entry-1", "ticket-1");
        var room = new BattleMatchmakingService.ActiveBattleRoom { BattleId = "battle-1", BattleRuleId = 1, FieldId = 101 };
        room.HumanPlayers.Add(player);
        Raise(matchmaking, "HumanJoinedRoom", room, player);
        // The GetAssignments stream drops (transient network blip)...
        Raise(matchmaking, "HumanLeftRoom", room, player, "stream_closed");
        await service.FlushAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OpsStatusRules.MatchingFailedPrefix + "stream_closed",
            store.Participants[("battle-1", "1000001")].Status);

        // ...and the client re-issues it while the window is still open. The player is waiting again.
        Raise(matchmaking, "HumanJoinedRoom", room, player);
        await service.FlushAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OpsStatusRules.Matching, store.Participants[("battle-1", "1000001")].Status);
    }

    [Fact]
    public async Task Sweep_finalizes_silent_drops_and_entries_that_never_reached_a_room()
    {
        var store = new RecordingOpsStore();
        using var service = NewService(store, null, out var time);
        await service.StartAsync(CancellationToken.None);

        service.RecordEntry("1000001", "entry-playing", "ticket-1", 1, null, 1, 1, false, OpsClientInfo.Empty);
        service.RecordBattleStart("1000001", "battle-1", 1, null, 101, 180, OpsClientInfo.Empty);
        service.RecordEntry("1000002", "entry-waiting", "ticket-2", 1, null, 1, 1, false, OpsClientInfo.Empty);

        // Battle time 180 + 120 grace => silent drop; 5 min without a room => no_room for the waiter.
        time.Advance(TimeSpan.FromSeconds(181 + 120 + 1));
        service.Sweep(time.GetUtcNow().UtcDateTime);
        await service.FlushAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OpsStatusRules.DroppedSilent, store.Participants[("battle-1", "1000001")].Status);
        Assert.Equal(OpsStatusRules.MatchingFailedPrefix + "no_room", store.Participants[("unmatched-entry-waiting", "1000002")].Status);
    }

    [Fact]
    public async Task Client_reports_are_fingerprinted_and_counted_per_participant()
    {
        var (matchmaking, _) = NewMatchmaking();
        var store = new RecordingOpsStore();
        using var service = NewService(store, matchmaking, out _);
        await service.StartAsync(CancellationToken.None);

        var player = NewPlayer("1000001", "entry-1", "ticket-1");
        var room = new BattleMatchmakingService.ActiveBattleRoom { BattleId = "battle-1", BattleRuleId = 1, FieldId = 101 };
        room.HumanPlayers.Add(player);
        Raise(matchmaking, "HumanJoinedRoom", room, player);
        service.RecordBattleStart("1000001", "battle-1", 1, null, 101, 180, OpsClientInfo.Empty);

        const string report = "[Analysis]\nNullReferenceException: Object reference not set\n" +
                              "  at Colorful.PlayerCharacter.Initialize (System.Int32 id) [0x0001c] in <x>:0\n" +
                              "  at Colorful.GameManager.Setup () [0x00010] in <x>:0";
        service.RecordAnalysis("1000001", "PhotonGetResionPingFailed", JsonSerializerShim(report), OpsClientInfo.Empty);
        service.RecordAnalysis("1000001", "PhotonGetResionPingFailed", JsonSerializerShim(report), OpsClientInfo.Empty);
        service.RecordAnalysis("1000001", "GameReconnectBegin", "{}", OpsClientInfo.Empty);
        await service.FlushAsync(TimeSpan.FromSeconds(5));

        var exceptions = store.Events.Where(e => e.Kind == OpsFingerprint.ExceptionKind).ToList();
        Assert.Equal(2, exceptions.Count);
        Assert.NotNull(exceptions[0].Fingerprint);
        Assert.Equal(2, store.Participants[("battle-1", "1000001")].ExceptionCount);
        Assert.Equal(1, store.Participants[("battle-1", "1000001")].ReconnectBegins);
    }

    [Fact]
    public void Client_info_ignores_empty_headers_and_caps_long_ones()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var info = OpsClientInfo.FromRequest(context);
        Assert.Equal("", info.AppVersion);
        Assert.Equal("", info.Ip);

        context.Request.Headers["x-app-application-version"] = "  2.11.2  ";
        context.Request.Headers["x-app-device-name"] = new string('d', 500);
        context.Request.Headers["X-Real-IP"] = "   ";
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.7, 10.0.0.1";
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        var capped = OpsClientInfo.FromRequest(context);
        Assert.Equal("2.11.2", capped.AppVersion);
        Assert.Equal(128, capped.DeviceName.Length);
        Assert.Equal("198.51.100.7", capped.Ip);
    }

    [Fact]
    public void Store_factory_uses_ndjson_under_ops_directory_without_a_connection_string()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kf-ops-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ops:Directory"] = directory
        }).Build();

        var store = OpsStoreFactory.Create(configuration, Path.GetTempPath());
        var ndjson = Assert.IsType<NdjsonOpsStore>(store);
        Assert.Equal(Path.GetFullPath(directory), ndjson.Directory);

        var withDatabase = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PlayerStore:ConnectionString"] = "Host=127.0.0.1;Database=x;Username=x;Password=x"
        }).Build();
        Assert.IsType<PostgresOpsStore>(OpsStoreFactory.Create(withDatabase, Path.GetTempPath()));
    }

    [Fact]
    public async Task Deploy_window_starts_at_the_first_run_of_the_current_release()
    {
        var store = new RecordingOpsStore();
        var seed = new OpsWriteBatch();
        seed.Runs.Add(new ServerRunRow { Id = "run-a1", StartedAt = T0.AddHours(-3), ReleaseSha = "sha-a" });
        seed.Runs.Add(new ServerRunRow { Id = "run-a2", StartedAt = T0.AddHours(-2), ReleaseSha = "sha-a" });
        seed.Runs.Add(new ServerRunRow { Id = "run-b1", StartedAt = T0.AddHours(-1), ReleaseSha = "sha-b" });
        await store.WriteAsync(seed, CancellationToken.None);

        var service = new OpsTelemetryService(NullLogger<OpsTelemetryService>.Instance, store, null,
            new FakeTimeProvider(T0), enabled: true, releaseSha: "sha-b");
        using (service)
        {
            await service.StartAsync(CancellationToken.None);
            var deploy = await OpsReports.ResolveWindowAsync(service, null, T0, CancellationToken.None);
            Assert.Equal("deploy", deploy!.Name);
            Assert.Equal(T0.AddHours(-1), deploy.Since);

            var restart = await OpsReports.ResolveWindowAsync(service, "RESTART", T0, CancellationToken.None);
            Assert.Equal("restart", restart!.Name);
            Assert.Equal(service.StartedAt, restart.Since);

            var rolling = await OpsReports.ResolveWindowAsync(service, "24h", T0, CancellationToken.None);
            Assert.Equal(T0.AddHours(-24), rolling!.Since);
            Assert.Null(await OpsReports.ResolveWindowAsync(service, "this-release", T0, CancellationToken.None));

            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartAsync_survives_a_store_that_cannot_initialize()
    {
        var store = new RecordingOpsStore { FailInitialize = true };
        var service = new OpsTelemetryService(NullLogger<OpsTelemetryService>.Instance, store, null,
            new FakeTimeProvider(T0));
        // RecordServerStart runs before the host starts; it must never touch the database.
        service.RecordServerStart("v1", "2.11.0");
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(service.ExecuteTask!.IsFaulted);
        service.RecordEntry("1000001", "entry-1", "ticket-1", 1, null, 1, 1, false, OpsClientInfo.Empty);
        await service.StopAsync(CancellationToken.None);
        Assert.False(service.ExecuteTask!.IsFaulted);
    }

    [Fact]
    public async Task FlushAsync_drains_everything_enqueued_before_it()
    {
        var store = new RecordingOpsStore();
        using var service = NewService(store, null, out _);
        await service.StartAsync(CancellationToken.None);
        for (var i = 0; i < 250; i++)
            service.RecordEntry($"user-{i}", $"entry-{i}", $"ticket-{i}", 1, null, 1, 1, false, OpsClientInfo.Empty);

        await service.FlushAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(250, store.Events.Count(e => e.Kind == "entry"));
    }

    [Fact]
    public async Task FlushAsync_returns_when_the_bounded_queue_is_full()
    {
        var store = new RecordingOpsStore { WriteDelay = TimeSpan.FromMilliseconds(25) };
        // Capacity 16: the marker cannot be enqueued once the writer is behind, and FlushAsync must not hang.
        var service = new OpsTelemetryService(NullLogger<OpsTelemetryService>.Instance, store, null,
            new FakeTimeProvider(T0), enabled: true, queueCapacity: 16);
        await service.StartAsync(CancellationToken.None);
        for (var i = 0; i < 10_000; i++)
            service.RecordEntry($"user-{i}", $"entry-{i}", $"ticket-{i}", 1, null, 1, 1, false, OpsClientInfo.Empty);
        var started = DateTime.UtcNow;
        await service.FlushAsync(TimeSpan.FromSeconds(5));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);
        Assert.True(service.Stats.Dropped > 0);
    }

    [Fact]
    public async Task Disabled_service_records_nothing_and_does_not_start_a_writer()
    {
        var store = new RecordingOpsStore();
        var service = new OpsTelemetryService(NullLogger<OpsTelemetryService>.Instance, store, null,
            new FakeTimeProvider(T0), enabled: false);
        service.RecordServerStart("v1", "2.11.0");
        service.RecordEntry("1000001", "entry-1", "ticket-1", 1, null, 1, 1, false, OpsClientInfo.Empty);
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(20);
        Assert.Empty(store.Events);
    }

    // ------------------------------------------------------------------ helpers

    private static string JsonSerializerShim(string cause) =>
        new JsonObject { ["cause"] = cause }.ToJsonString();

    private static BattleMatchmakingService.BattleEntrySession NewPlayer(string userId, string entryId, string ticketId) => new()
    {
        UserId = userId, BattleEntryId = entryId, TicketId = ticketId
    };

    private static BattleMatchmakingService.MatchingPlayerBattleInfo NewSlot(string userId, string entryId) => new()
    {
        userId = userId, battleEntryId = entryId, kickerId = 1, kickerCostumeId = 1
    };

    private static (BattleMatchmakingService Matchmaking, MaintenanceState Maintenance) NewMatchmaking()
    {
        var maintenance = new MaintenanceState(NullLogger<MaintenanceState>.Instance,
            Path.Combine(Path.GetTempPath(), $"kf-ops-maint-{Guid.NewGuid():N}.json"));
        var photon = new PhotonServerManager(Options.Create(new PhotonServerOptions { Enabled = false }),
            NullLogger<PhotonServerManager>.Instance);
        return (new BattleMatchmakingService(NullLogger<BattleMatchmakingService>.Instance, photon, maintenance), maintenance);
    }

    private static OpsTelemetryService NewService(RecordingOpsStore store, BattleMatchmakingService? matchmaking,
        out FakeTimeProvider time)
    {
        time = new FakeTimeProvider(T0);
        return new OpsTelemetryService(NullLogger<OpsTelemetryService>.Instance, store, matchmaking, time);
    }

    // The room lifecycle hooks are raised by BattleMatchmakingService's private Notify; the event field is the
    // compiler-generated backing delegate, so invoking it calls exactly the handler the service subscribed.
    private static void Raise(object target, string eventName, params object[] args)
    {
        var field = target.GetType().GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((Delegate)field.GetValue(target)!).DynamicInvoke(args);
    }

    private sealed class FakeTimeProvider(DateTime now) : TimeProvider
    {
        private DateTimeOffset _now = new(DateTime.SpecifyKind(now, DateTimeKind.Utc));
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class RecordingOpsStore : IOpsStore
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, ServerRunRow> _runs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, BattleRow> _battles = new(StringComparer.Ordinal);
        private readonly Dictionary<(string, string), ParticipantRow> _participants = [];
        private readonly List<ClientEventRow> _events = [];

        public bool FailInitialize { get; set; }
        public TimeSpan WriteDelay { get; set; }
        public string Kind => "recording";

        public Task InitializeAsync(CancellationToken cancellationToken) =>
            FailInitialize ? Task.FromException(new InvalidOperationException("database is down")) : Task.CompletedTask;

        public async Task WriteAsync(OpsWriteBatch batch, CancellationToken cancellationToken)
        {
            if (WriteDelay > TimeSpan.Zero) await Task.Delay(WriteDelay, cancellationToken);
            lock (_lock)
            {
                foreach (var run in batch.Runs) _runs[run.Id] = run;
                foreach (var battle in batch.Battles) _battles[battle.BattleId] = battle;
                foreach (var participant in batch.Participants)
                    _participants[(participant.BattleId, participant.UserId)] = participant;
                foreach (var row in batch.Events) _events.Add(row with { Id = _events.Count + 1 });
            }
        }

        public IReadOnlyDictionary<string, ServerRunRow> Runs { get { lock (_lock) return _runs.ToDictionary(); } }
        public IReadOnlyDictionary<string, BattleRow> Battles { get { lock (_lock) return _battles.ToDictionary(); } }
        public IReadOnlyDictionary<(string, string), ParticipantRow> Participants
        {
            get { lock (_lock) return _participants.ToDictionary(); }
        }
        public IReadOnlyList<ClientEventRow> Events { get { lock (_lock) return _events.ToList(); } }

        public Task<IReadOnlyList<ServerRunRow>> LoadRunsAsync(DateTime since, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ServerRunRow>>(Runs.Values.Where(r => r.StartedAt >= since).ToList());

        public Task<IReadOnlyList<BattleRow>> LoadBattlesAsync(DateTime since, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BattleRow>>(Battles.Values.Where(b => b.CreatedAt >= since).ToList());

        public Task<IReadOnlyList<ParticipantRow>> LoadParticipantsAsync(DateTime since, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ParticipantRow>>(Participants.Values.Where(p => p.CreatedAt >= since).ToList());

        public Task<BattleRow?> LoadBattleAsync(string battleId, CancellationToken cancellationToken) =>
            Task.FromResult(Battles.GetValueOrDefault(battleId));

        public Task<IReadOnlyList<ParticipantRow>> LoadBattleParticipantsAsync(string battleId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ParticipantRow>>(Participants.Values.Where(p => p.BattleId == battleId).ToList());

        public Task<IReadOnlyList<ClientEventRow>> LoadEventsAsync(OpsEventQuery query, CancellationToken cancellationToken)
        {
            var events = Events.Where(e => e.At >= query.Since);
            if (query.BattleId is not null) events = events.Where(e => e.BattleId == query.BattleId);
            if (query.Kinds is { Count: > 0 } kinds) events = events.Where(e => kinds.Contains(e.Kind));
            return Task.FromResult<IReadOnlyList<ClientEventRow>>(
                events.OrderByDescending(e => e.At).Take(query.Limit).OrderBy(e => e.At).ToList());
        }

        public Task<IReadOnlyDictionary<string, int>> CountEventsByKindAsync(DateTime since, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(Events.Where(e => e.At >= since)
                .GroupBy(e => e.Kind, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal));

        public Task PruneAsync(DateTime before, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                foreach (var key in _events.Where(e => e.At < before).Select(e => e.Id).ToList())
                    _events.RemoveAll(e => e.Id == key);
            }
            return Task.CompletedTask;
        }
    }
}
