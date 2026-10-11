using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace KickFlight.BootstrapApi.Ops;

/// <summary>
/// Server-side ops telemetry: who entered which match, from which app version / device / OS, and how each human's
/// match ended (OpsStatusRules), plus every /analysis entry enriched with the battle it belongs to.
///
/// Request handlers only call the Record* methods, which update a small in-memory model under one leaf lock and
/// enqueue row snapshots into a bounded channel; they never wait for storage. A full channel drops the snapshot
/// (counted in Stats) rather than slowing a request down. The background writer drains the channel in batches into
/// the store (PostgreSQL, or NDJSON without a connection string), and a 60 s sweep finalizes the time-based statuses
/// (silent drops, matches that never started) that no request would ever trigger.
///
/// Every server start writes a server_runs row tagged with KF_RELEASE_SHA (passed by the deploy workflow), which is
/// what the dashboard's default "current deploy" window starts from.
/// </summary>
public sealed class OpsTelemetryService : BackgroundService
{
    public const int DefaultQueueCapacity = 10_000;
    private const int BatchSize = 500;
    private const int MaxCauseChars = 64;
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    /// <summary>Finalized matches stay in memory this long, so a late /battle/result or relaunch still updates them.</summary>
    private static readonly TimeSpan Linger = TimeSpan.FromMinutes(10);
    /// <summary>On start, open matches of a previous run younger than this are reloaded (Photon outlives the API).</summary>
    private static readonly TimeSpan ReloadWindow = TimeSpan.FromHours(6);

    private readonly ILogger<OpsTelemetryService> _logger;
    private readonly TimeProvider _time;
    private readonly Channel<object> _queue;
    private readonly object _lock = new();
    // The in-memory model, guarded by _lock. It is a leaf lock: nothing is called out of it, which is what makes
    // it safe for BattleMatchmakingService to raise its room events while holding its own _matchLock.
    private readonly Dictionary<string, BattleTrack> _battles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParticipantTrack> _byEntry = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParticipantTrack> _activeByUser = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (OpsClientInfo Info, DateTime At)> _clients = new(StringComparer.Ordinal);
    private long _dropped;
    private long _written;
    private long _writeErrors;
    private string? _lastWriteError;
    private DateTime? _lastWriteAt;
    private DateTime _nextPruneAt = DateTime.MinValue;

    public OpsTelemetryService(ILogger<OpsTelemetryService> logger, IConfiguration configuration,
        IWebHostEnvironment environment, BattleMatchmakingService matchmaking, TimeProvider time)
        : this(logger, OpsStoreFactory.Create(configuration, environment.ContentRootPath), matchmaking, time,
            configuration.GetValue("Ops:Enabled", true),
            configuration["Ops:ReleaseSha"] is { Length: > 0 } configured ? configured : Environment.GetEnvironmentVariable("KF_RELEASE_SHA"),
            configuration.GetValue("Ops:QueueCapacity", DefaultQueueCapacity))
    {
    }

    public OpsTelemetryService(ILogger<OpsTelemetryService> logger, IOpsStore store, BattleMatchmakingService? matchmaking,
        TimeProvider time, bool enabled = true, string? releaseSha = null, int queueCapacity = DefaultQueueCapacity)
    {
        _logger = logger;
        _time = time;
        Store = store;
        Enabled = enabled;
        ReleaseSha = string.IsNullOrWhiteSpace(releaseSha) ? "unknown" : releaseSha.Trim();
        StartedAt = Now;
        RunId = $"run-{StartedAt:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..28];
        _queue = Channel.CreateBounded<object>(
            new BoundedChannelOptions(Math.Max(16, queueCapacity))
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true
            },
            dropped =>
            {
                if (dropped is FlushMarker marker) marker.Done.TrySetResult();
                else Interlocked.Increment(ref _dropped);
            });

        if (matchmaking is not null && enabled)
        {
            matchmaking.HumanJoinedRoom += OnHumanJoinedRoom;
            matchmaking.HumanLeftRoom += OnHumanLeftRoom;
            matchmaking.RoomFinalized += OnRoomFinalized;
            matchmaking.RoomDropped += OnRoomDropped;
            matchmaking.TicketRefused += OnTicketRefused;
        }
    }

    public IOpsStore Store { get; }
    public bool Enabled { get; }
    public string RunId { get; }
    public string ReleaseSha { get; }
    public DateTime StartedAt { get; }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public sealed record TelemetryStats(string Store, bool Enabled, int QueueDepth, long Written, long Dropped,
        long WriteErrors, string? LastWriteError, DateTime? LastWriteAt, int TrackedBattles);

    public TelemetryStats Stats
    {
        get
        {
            int tracked;
            lock (_lock) tracked = _battles.Count;
            return new TelemetryStats(Store.Kind, Enabled, _queue.Reader.Count, Interlocked.Read(ref _written),
                Interlocked.Read(ref _dropped), Interlocked.Read(ref _writeErrors), _lastWriteError, _lastWriteAt, tracked);
        }
    }

    // ------------------------------------------------------------------ recording (request side, never blocks)

    public void RecordServerStart(string masterVersion, string minClientVersion)
    {
        if (!Enabled) return;
        Enqueue(new ServerRunRow
        {
            Id = RunId,
            StartedAt = StartedAt,
            ReleaseSha = ReleaseSha,
            MasterVersion = masterVersion,
            MinClientVersion = minClientVersion
        });
        _logger.LogInformation("Ops telemetry run {RunId}: release {ReleaseSha}, store {Store}", RunId, ReleaseSha, Store.Kind);
    }

    /// <summary>Remembers the headers of the player's latest request, for server-side events that have no request.</summary>
    public void TouchClient(string userId, OpsClientInfo info)
    {
        if (!Enabled || string.IsNullOrEmpty(userId)) return;
        _clients[userId] = (info, Now);
    }

    /// <summary>/auth/index: a player who re-authenticates during its match most likely relaunched the app.</summary>
    public void RecordAuth(string userId, OpsClientInfo info) => Guard(() =>
    {
        TouchClient(userId, info);
        var now = Now;
        lock (_lock)
        {
            if (_activeByUser.TryGetValue(userId, out var track) && track.StartAt is not null)
            {
                track.RelaunchAt = now;
                Refresh(track, now);
            }
        }
    });

    /// <summary>/boot/index carries no session, so the relaunch is matched on client address + device name.</summary>
    public void RecordBoot(OpsClientInfo info) => Guard(() =>
    {
        if (info.Ip.Length == 0 || info.DeviceName.Length == 0) return;
        var now = Now;
        lock (_lock)
        {
            foreach (var track in _activeByUser.Values)
            {
                if (track.StartAt is null || track.Client.Ip != info.Ip || track.Client.DeviceName != info.DeviceName) continue;
                track.RelaunchAt = now;
                Refresh(track, now);
            }
        }
    });

    public void RecordEntry(string userId, string battleEntryId, string ticketId, int ruleId, int? matchType,
        int kickerId, int costumeId, bool isDiag, OpsClientInfo info) => Guard(() =>
    {
        if (string.IsNullOrEmpty(battleEntryId)) return;
        TouchClient(userId, info);
        var now = Now;
        lock (_lock)
        {
            var track = new ParticipantTrack
            {
                UserId = userId,
                BattleEntryId = battleEntryId,
                TicketId = ticketId,
                RuleId = ruleId,
                MatchType = matchType,
                KickerId = kickerId,
                CostumeId = costumeId,
                IsDiag = isDiag,
                Client = info,
                EntryAt = now
            };
            _byEntry[battleEntryId] = track;
            AddEvent(now, userId, null, "entry", info, new JsonObject
            {
                ["battle_entry_id"] = battleEntryId,
                ["rule_id"] = ruleId,
                ["kicker_id"] = kickerId,
                ["costume_id"] = costumeId,
                ["is_diag"] = isDiag
            });
        }
    });

    public void RecordCancel(string userId, string battleEntryId, bool accepted, OpsClientInfo info) => Guard(() =>
    {
        var now = Now;
        lock (_lock)
        {
            var track = FindByEntry(battleEntryId);
            if (accepted && track is not null && track.StartAt is null)
            {
                track.CancelledAt ??= now;
                Refresh(track, now);
            }
            AddEvent(now, userId, track?.BattleId, "cancel", info, new JsonObject
            {
                ["battle_entry_id"] = battleEntryId,
                ["accepted"] = accepted
            });
        }
    });

    /// <summary>/battle/timeout {battleEntryId}: the matching countdown reached 0 before the full roster arrived.</summary>
    public void RecordTimeout(string userId, string battleEntryId, OpsClientInfo info) => Guard(() =>
    {
        var now = Now;
        lock (_lock)
        {
            var track = FindByEntry(battleEntryId) ?? FindActive(userId);
            if (track is not null && track.StartAt is null)
            {
                track.TimeoutAt ??= now;
                Refresh(track, now);
            }
            AddEvent(now, userId, track?.BattleId, "battle_timeout", info, new JsonObject { ["battle_entry_id"] = battleEntryId });
        }
    });

    /// <summary>/battle/disconnect {battleEntryId, cause}.</summary>
    public void RecordDisconnect(string userId, string battleEntryId, string cause, OpsClientInfo info) => Guard(() =>
    {
        var now = Now;
        cause = CleanCause(cause);
        lock (_lock)
        {
            var track = FindByEntry(battleEntryId) ?? FindActive(userId);
            if (track is not null)
            {
                track.DisconnectCause = cause;
                Refresh(track, now);
            }
            AddEvent(now, userId, track?.BattleId, "battle_disconnect", info, new JsonObject
            {
                ["battle_entry_id"] = battleEntryId,
                ["cause"] = cause
            });
        }
    });

    /// <summary>/battle/leaving: the request body is not documented, so any battleEntryId / battleId in it is optional.</summary>
    public void RecordLeaving(string userId, string? battleEntryId, string? battleId, OpsClientInfo info) => Guard(() =>
    {
        var now = Now;
        lock (_lock)
        {
            var track = FindByEntry(battleEntryId) ?? FindInBattle(battleId, userId) ?? FindActive(userId);
            if (track is not null)
            {
                track.LeftAt ??= now;
                Refresh(track, now);
            }
            AddEvent(now, userId, track?.BattleId ?? NullIfEmpty(battleId), "battle_leaving", info, new JsonObject
            {
                ["battle_entry_id"] = battleEntryId ?? "",
                ["battle_id"] = battleId ?? ""
            });
        }
    });

    public void RecordBattleStart(string userId, string battleId, int ruleId, int? matchType, int fieldId,
        int battleTimeSeconds, OpsClientInfo info) => Guard(() =>
    {
        var now = Now;
        lock (_lock)
        {
            // A start body without a battleId (undecodable) belongs to the room the player is in, if any.
            if (string.IsNullOrEmpty(battleId)) battleId = FindActive(userId)?.BattleId ?? "";
            if (battleId.Length == 0 || battleId.StartsWith("unmatched-", StringComparison.Ordinal))
            {
                AddEvent(now, userId, null, "start", info, new JsonObject { ["rule_id"] = ruleId, ["field_id"] = fieldId });
                return;
            }
            var battle = GetOrCreateBattle(battleId, ruleId, matchType, fieldId, custom: false, now);
            battle.BattleTimeSeconds = battleTimeSeconds > 0 ? battleTimeSeconds : battle.BattleTimeSeconds;
            if (battle.FieldId == 0) battle.FieldId = fieldId;
            var track = battle.Participants.GetValueOrDefault(userId) ?? AttachUnknownParticipant(battle, userId, info, now);
            track.StartAt ??= now;
            track.LastSeenAt = now;
            battle.FirstStartAt ??= now;
            battle.Dirty = true;
            Refresh(track, now);
            AddEvent(now, userId, battle.BattleId, "start", info, new JsonObject
            {
                ["rule_id"] = ruleId,
                ["field_id"] = fieldId
            });
        }
    });

    /// <summary>A custom battle has no matchmaking room: each /customBattle/start opens its own one-human record.</summary>
    public void RecordCustomBattleStart(string userId, string customBattleId, int battleRuleType, int fieldId,
        OpsClientInfo info) => Guard(() =>
    {
        var now = Now;
        lock (_lock)
        {
            var battle = GetOrCreateBattle(customBattleId, battleRuleType, null, fieldId, custom: true, now);
            battle.FinalizedAt ??= now;
            var track = battle.Participants.GetValueOrDefault(userId) ?? AttachUnknownParticipant(battle, userId, info, now);
            track.Assigned = true;
            track.StartAt ??= now;
            track.LastSeenAt = now;
            battle.FirstStartAt ??= now;
            battle.Humans = Math.Max(battle.Humans, battle.Participants.Count);
            battle.Dirty = true;
            Refresh(track, now);
            AddEvent(now, userId, battle.BattleId, "start", info, new JsonObject
            {
                ["custom"] = true,
                ["rule_type"] = battleRuleType,
                ["field_id"] = fieldId
            });
        }
    });

    public void RecordBattleEnd(string userId, string? battleId, OpsClientInfo info) => Guard(() =>
    {
        var now = Now;
        lock (_lock)
        {
            var track = FindInBattle(battleId, userId) ?? FindActive(userId);
            if (track is not null)
            {
                track.EndAt ??= now;
                Refresh(track, now);
            }
            AddEvent(now, userId, track?.BattleId ?? NullIfEmpty(battleId), "end", info, new JsonObject());
        }
    });

    public void RecordBattleResult(string userId, string? battleEntryId, OpsClientInfo info) => Guard(() =>
    {
        var now = Now;
        lock (_lock)
        {
            var track = FindByEntry(battleEntryId) ?? FindActive(userId);
            if (track is not null)
            {
                track.ResultAt ??= now;
                Refresh(track, now);
            }
            AddEvent(now, userId, track?.BattleId, "result", info, new JsonObject { ["battle_entry_id"] = battleEntryId ?? "" });
        }
    });

    /// <summary>
    /// One accepted /analysis entry. Stored as a client_events row whose kind is the analysis key, except the client
    /// error reports a patched client sends as PhotonGetResionPingFailed with a cause starting "[Analysis]" (kind
    /// client_exception / client_anomaly, fingerprinted). The payload carries the parsed value, the request headers,
    /// the client address and the battle context (rule, field, kicker, roster) resolved here, server-side.
    /// </summary>
    public void RecordAnalysis(string userId, string key, string value, OpsClientInfo info) => Guard(() =>
    {
        var now = Now;
        var parsed = ParseValue(value);
        var valueObject = parsed as JsonObject;
        var cause = valueObject is not null && TryGetText(valueObject, "cause", out var rawCause) ? rawCause : null;
        var kind = key;
        string? fingerprint = null;
        string? signature = null;
        if (key == "PhotonGetResionPingFailed" && cause is not null && OpsFingerprint.IsClientReport(cause))
        {
            (kind, fingerprint, signature) = OpsFingerprint.Classify(cause);
        }

        lock (_lock)
        {
            var reportedBattleId = valueObject is not null && TryGetText(valueObject, "battleId", out var reported) ? reported : null;
            var track = FindInBattle(reportedBattleId, userId) ?? FindActive(userId);
            if (track is not null)
            {
                var changed = true;
                switch (kind)
                {
                    case "GameReconnectBegin":
                        track.ReconnectBegins++;
                        break;
                    case "GameReconnectFailed":
                        track.ReconnectFailedCause = CleanCause(cause ?? "unknown");
                        break;
                    case OpsFingerprint.ExceptionKind:
                        track.ExceptionCount++;
                        break;
                    default:
                        changed = false;
                        break;
                }
                if (track.StartAt is not null) track.LastSeenAt = now;
                if (changed) Refresh(track, now);
            }

            var payload = new JsonObject
            {
                ["key"] = key,
                ["value"] = parsed
            };
            if (signature is not null) payload["signature"] = signature;
            var battle = track?.BattleId is { Length: > 0 } id ? _battles.GetValueOrDefault(id) : null;
            if (track is not null) payload["context"] = BuildContext(track, battle);
            AddEvent(now, userId, battle?.BattleId ?? NullIfEmpty(reportedBattleId), kind, info, payload, fingerprint);
        }
    });

    // ------------------------------------------------------------------ matchmaking room events

    private void OnHumanJoinedRoom(BattleMatchmakingService.ActiveBattleRoom room, BattleMatchmakingService.BattleEntrySession player) => Guard(() =>
    {
        var now = Now;
        lock (_lock)
        {
            var battle = GetOrCreateBattle(room.BattleId, room.BattleRuleId, player.BattleMatchType, room.FieldId, custom: false, now);
            var track = _byEntry.GetValueOrDefault(player.BattleEntryId);
            if (track is not null && track.BattleId.Length > 0 && track.BattleId != battle.BattleId)
            {
                // Excluded from a finalized room with its party and re-queued: close the old record as requeued.
                track.Requeued = true;
                Refresh(track, now);
                track = null;
            }
            if (track is null)
            {
                track = new ParticipantTrack
                {
                    UserId = player.UserId,
                    BattleEntryId = player.BattleEntryId,
                    TicketId = player.TicketId,
                    RuleId = player.BattleRuleId,
                    MatchType = player.BattleMatchType,
                    KickerId = player.KickerId,
                    CostumeId = player.KickerCostumeId,
                    IsDiag = player.IsDiag,
                    Client = ClientOf(player.UserId),
                    EntryAt = now
                };
                _byEntry[player.BattleEntryId] = track;
            }
            if (track.BattleId == battle.BattleId)
            {
                // A retried stream re-attaching after a transient close: the player is waiting again, so the
                // stream_closed matching failure is no longer true.
                if (track.MatchingFailedCause is not null)
                {
                    track.MatchingFailedCause = null;
                    Refresh(track, now);
                }
                return;
            }
            track.BattleId = battle.BattleId;
            battle.Participants[track.UserId] = track;
            _activeByUser[track.UserId] = track;
            Refresh(track, now);
            AddEvent(now, track.UserId, battle.BattleId, "room_join", track.Client, new JsonObject
            {
                ["battle_entry_id"] = track.BattleEntryId
            });
        }
    });

    private void OnHumanLeftRoom(BattleMatchmakingService.ActiveBattleRoom room, BattleMatchmakingService.BattleEntrySession player,
        string reason) => Guard(() =>
    {
        var now = Now;
        lock (_lock)
        {
            if (!_battles.TryGetValue(room.BattleId, out var battle)
                || !battle.Participants.TryGetValue(player.UserId, out var track)
                || track.StartAt is not null) return;
            track.MatchingFailedCause ??= CleanCause(reason);
            Refresh(track, now);
            AddEvent(now, track.UserId, battle.BattleId, "room_leave", track.Client, new JsonObject { ["reason"] = reason });
        }
    });

    private void OnRoomFinalized(BattleMatchmakingService.ActiveBattleRoom room) => Guard(() =>
    {
        var now = Now;
        lock (_lock)
        {
            var roster = room.MatchingInfo.battlePlayerList;
            var battle = GetOrCreateBattle(room.BattleId, room.BattleRuleId, null, room.FieldId, custom: false, now);
            battle.FinalizedAt ??= now;
            battle.Bots = roster.Count(slot => slot.kickerAiParameterId > 0);
            battle.Humans = roster.Count - battle.Bots;
            battle.Roster = roster.Select(slot => new OpsRosterSlot(slot.userId, slot.kickerId, slot.kickerCostumeId,
                slot.teamType, slot.kickerAiParameterId > 0)).ToList();
            battle.Dirty = true;
            foreach (var track in battle.Participants.Values)
            {
                if (!room.AssignedTicketIds.Contains(track.TicketId)) continue;
                track.Assigned = true;
                var slot = roster.FirstOrDefault(entry => entry.userId == track.UserId && entry.kickerAiParameterId == 0);
                if (slot is not null) track.Team = slot.teamType;
                Refresh(track, now);
                AddEvent(now, track.UserId, battle.BattleId, "assigned", track.Client, new JsonObject
                {
                    ["team"] = track.Team,
                    ["humans"] = battle.Humans,
                    ["bots"] = battle.Bots
                });
            }
            RefreshBattle(battle, now);
        }
    });

    private void OnRoomDropped(BattleMatchmakingService.ActiveBattleRoom room) => Guard(() =>
    {
        var now = Now;
        lock (_lock)
        {
            if (!_battles.TryGetValue(room.BattleId, out var battle)) return;
            battle.Dropped = true;
            battle.Dirty = true;
            foreach (var track in battle.Participants.Values) Refresh(track, now);
            RefreshBattle(battle, now);
        }
    });

    private void OnTicketRefused(BattleMatchmakingService.BattleEntrySession player, string cause) => Guard(() =>
    {
        var now = Now;
        lock (_lock)
        {
            var track = FindByEntry(player.BattleEntryId);
            if (track is null || track.StartAt is not null) return;
            track.MatchingFailedCause ??= CleanCause(cause);
            Refresh(track, now);
        }
    });

    // ------------------------------------------------------------------ sweep

    /// <summary>Re-derives every open status at <paramref name="now"/>; the background loop calls it every 60 s.</summary>
    public void Sweep(DateTime? at = null)
    {
        var now = at ?? Now;
        lock (_lock)
        {
            // Entries that never reached a room become final after NoRoomTimeout (see Refresh).
            foreach (var track in _byEntry.Values.Where(t => t.BattleId.Length == 0).ToList())
            {
                Refresh(track, now, factChanged: false);
            }

            foreach (var battle in _battles.Values.ToList())
            {
                foreach (var track in battle.Participants.Values) Refresh(track, now, factChanged: false);
                RefreshBattle(battle, now);
                if (battle.Outcome is not null && battle.OutcomeAt is { } decided && now - decided >= Linger)
                {
                    _battles.Remove(battle.BattleId);
                }
            }

            foreach (var (entryId, track) in _byEntry.ToList())
            {
                var gone = track.BattleId.Length > 0 && !_battles.ContainsKey(track.BattleId);
                if (gone || now - track.EntryAt > TimeSpan.FromDays(1)) _byEntry.Remove(entryId);
            }
            foreach (var (userId, track) in _activeByUser.ToList())
            {
                if (!_battles.ContainsKey(track.BattleId)) _activeByUser.Remove(userId);
            }
        }
        foreach (var (userId, client) in _clients)
        {
            if (now - client.At > TimeSpan.FromDays(1)) _clients.TryRemove(userId, out _);
        }
    }

    // ------------------------------------------------------------------ background writer

    /// <summary>Resolves once everything enqueued before the call is stored (or the timeout passes).</summary>
    public async Task FlushAsync(TimeSpan timeout)
    {
        var marker = new FlushMarker();
        if (!_queue.Writer.TryWrite(marker)) return;
        try
        {
            await marker.Done.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            // Answer with what is stored; the writer may be stuck on an unreachable database.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        await InitializeStoreAsync(stoppingToken);
        if (stoppingToken.IsCancellationRequested) return;
        await ReloadOpenStateAsync(stoppingToken);

        var sweep = SweepLoopAsync(stoppingToken);
        try
        {
            await WriteLoopAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        // Shutdown: store what is still queued, bounded so a dead database cannot hold the process.
        using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (_queue.Reader.Count > 0 && !drain.IsCancellationRequested) await WriteNextBatchAsync(drain.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }
        await sweep;
    }

    private async Task InitializeStoreAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Store.InitializeAsync(stoppingToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Telemetry never fails the start: retry in the background while the bounded queue absorbs events.
                _logger.LogWarning("Ops telemetry store ({Store}) unavailable, retrying in {Seconds}s: {Error}",
                    Store.Kind, delay.TotalSeconds, ex.Message);
            }
            try { await Task.Delay(delay, _time, stoppingToken); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(300, delay.TotalSeconds * 2));
        }
    }

    private async Task ReloadOpenStateAsync(CancellationToken stoppingToken)
    {
        try
        {
            var since = Now - ReloadWindow;
            var battles = await Store.LoadBattlesAsync(since, stoppingToken);
            var participants = await Store.LoadParticipantsAsync(since, stoppingToken);
            var now = Now;
            lock (_lock)
            {
                var reloaded = 0;
                foreach (var row in battles.Where(b => b.Outcome is null && b.RunId != RunId))
                {
                    if (_battles.ContainsKey(row.BattleId)) continue;
                    var battle = BattleTrack.FromRow(row);
                    _battles[row.BattleId] = battle;
                    reloaded++;
                }
                foreach (var row in participants.Where(p => OpsStatusRules.IsOpen(p.Status)))
                {
                    if (!_battles.TryGetValue(row.BattleId, out var battle) || battle.Participants.ContainsKey(row.UserId)) continue;
                    var track = ParticipantTrack.FromRow(row);
                    // The previous process's matchmaking rooms are gone with it, so nobody still waiting can be matched.
                    if (track.StartAt is null && !track.Assigned) track.MatchingFailedCause = "server_restart";
                    if (track.Assigned) battle.FinalizedAt ??= battle.CreatedAt;
                    battle.Participants[row.UserId] = track;
                    if (track.BattleEntryId.Length > 0) _byEntry.TryAdd(track.BattleEntryId, track);
                    _activeByUser.TryAdd(track.UserId, track);
                }
                if (reloaded > 0) _logger.LogInformation("Ops telemetry reloaded {Count} open match(es) of a previous run", reloaded);
                foreach (var battle in _battles.Values.Where(b => b.RunId != RunId).ToList())
                {
                    foreach (var track in battle.Participants.Values) Refresh(track, now, factChanged: false);
                    RefreshBattle(battle, now);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Ops telemetry could not reload open matches: {Error}", ex.Message);
        }
    }

    private async Task SweepLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(SweepInterval, _time, stoppingToken); }
            catch (OperationCanceledException) { return; }
            Guard(() => Sweep());
        }
    }

    private async Task WriteLoopAsync(CancellationToken stoppingToken)
    {
        while (await _queue.Reader.WaitToReadAsync(stoppingToken))
        {
            var failed = !await WriteNextBatchAsync(stoppingToken);
            if (Now >= _nextPruneAt)
            {
                _nextPruneAt = Now + TimeSpan.FromHours(24);
                try
                {
                    await Store.PruneAsync(Now - Retention, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Ops telemetry prune failed: {Error}", ex.Message);
                }
            }
            // Do not hammer a failing database; the bounded queue keeps (or drops) what arrives meanwhile.
            if (failed) await Task.Delay(TimeSpan.FromSeconds(5), _time, stoppingToken);
        }
    }

    /// <summary>Writes up to BatchSize queued items; false when the store rejected the batch.</summary>
    private async Task<bool> WriteNextBatchAsync(CancellationToken cancellationToken)
    {
        var batch = new OpsWriteBatch();
        var battles = new Dictionary<string, BattleRow>(StringComparer.Ordinal);
        var participants = new Dictionary<(string, string), ParticipantRow>();
        var markers = new List<FlushMarker>();
        var taken = 0;
        while (taken < BatchSize && _queue.Reader.TryRead(out var item))
        {
            taken++;
            switch (item)
            {
                case FlushMarker marker: markers.Add(marker); break;
                case ServerRunRow run: batch.Runs.Add(run); break;
                // Snapshots of the same row in one batch collapse to the latest.
                case BattleRow battle: battles[battle.BattleId] = battle; break;
                case ParticipantRow participant: participants[(participant.BattleId, participant.UserId)] = participant; break;
                case ClientEventRow row: batch.Events.Add(row); break;
            }
        }
        batch.Battles.AddRange(battles.Values);
        batch.Participants.AddRange(participants.Values);

        var ok = true;
        if (batch.Count > 0)
        {
            try
            {
                await Store.WriteAsync(batch, cancellationToken);
                Interlocked.Add(ref _written, batch.Count);
                _lastWriteAt = Now;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ok = false;
                var errors = Interlocked.Increment(ref _writeErrors);
                _lastWriteError = ex.Message;
                if (errors == 1 || errors % 100 == 0)
                {
                    _logger.LogWarning("Ops telemetry write of {Count} row(s) failed ({Errors} failure(s) so far): {Error}",
                        batch.Count, errors, ex.Message);
                }
            }
        }
        foreach (var marker in markers) marker.Done.TrySetResult();
        return ok;
    }

    // ------------------------------------------------------------------ model helpers (caller holds _lock)

    private BattleTrack GetOrCreateBattle(string battleId, int ruleId, int? matchType, int fieldId, bool custom, DateTime now)
    {
        if (_battles.TryGetValue(battleId, out var battle))
        {
            if (battle.MatchType is null && matchType is not null)
            {
                battle.MatchType = matchType;
                battle.Dirty = true;
            }
            return battle;
        }
        battle = new BattleTrack
        {
            BattleId = battleId,
            RunId = RunId,
            CreatedAt = now,
            RuleId = ruleId,
            MatchType = matchType,
            FieldId = fieldId,
            Custom = custom,
            Dirty = true
        };
        _battles[battleId] = battle;
        return battle;
    }

    /// <summary>A /battle/start for a player this process never saw enter (restart, demo ticket).</summary>
    private ParticipantTrack AttachUnknownParticipant(BattleTrack battle, string userId, OpsClientInfo info, DateTime now)
    {
        var known = _activeByUser.GetValueOrDefault(userId);
        var track = new ParticipantTrack
        {
            UserId = userId,
            BattleId = battle.BattleId,
            BattleEntryId = known?.BattleId == battle.BattleId ? known.BattleEntryId : "",
            RuleId = battle.RuleId,
            MatchType = battle.MatchType,
            Client = info,
            EntryAt = now,
            Assigned = true
        };
        battle.Participants[userId] = track;
        _activeByUser[userId] = track;
        return track;
    }

    private ParticipantTrack? FindByEntry(string? battleEntryId) =>
        string.IsNullOrEmpty(battleEntryId) ? null : _byEntry.GetValueOrDefault(battleEntryId);

    private ParticipantTrack? FindInBattle(string? battleId, string userId) =>
        !string.IsNullOrEmpty(battleId) && _battles.TryGetValue(battleId, out var battle)
            ? battle.Participants.GetValueOrDefault(userId)
            : null;

    private ParticipantTrack? FindActive(string userId) => _activeByUser.GetValueOrDefault(userId);

    private OpsClientInfo ClientOf(string userId) =>
        _clients.TryGetValue(userId, out var client) ? client.Info : OpsClientInfo.Empty;

    /// <summary>
    /// Re-derives the participant's status and stores a snapshot when a fact or the status changed; then re-derives
    /// its match outcome. An entry that ends without ever reaching a room is filed under a synthetic
    /// "unmatched-&lt;battleEntryId&gt;" battle id, so the row still has its (battle_id, user_id) key.
    /// </summary>
    private void Refresh(ParticipantTrack track, DateTime now, bool factChanged = true)
    {
        var battle = track.BattleId.Length > 0 ? _battles.GetValueOrDefault(track.BattleId) : null;
        var status = OpsStatusRules.EvaluateParticipant(track.Facts(), battle?.Facts(), now);
        var changed = factChanged || status != track.Status;
        track.Status = status;
        if (track.BattleId.Length == 0 && !OpsStatusRules.IsOpen(status))
        {
            track.BattleId = "unmatched-" + track.BattleEntryId;
            changed = true;
        }
        if (changed && track.BattleId.Length > 0) Enqueue(track.ToRow(now));
        if (battle is not null) RefreshBattle(battle, now);
    }

    private void RefreshBattle(BattleTrack battle, DateTime now)
    {
        var outcome = OpsStatusRules.EvaluateOutcome(battle.Facts(),
            battle.Participants.Values.Select(p => (p.Status, p.Assigned, p.StartAt is not null)).ToList());
        if (outcome != battle.Outcome)
        {
            battle.Outcome = outcome;
            battle.OutcomeAt = outcome is null ? null : now;
            battle.Dirty = true;
        }
        if (!battle.Dirty) return;
        battle.Dirty = false;
        Enqueue(battle.ToRow(now));
    }

    private JsonObject BuildContext(ParticipantTrack track, BattleTrack? battle)
    {
        var context = new JsonObject
        {
            ["battle_id"] = battle?.BattleId,
            ["battle_entry_id"] = track.BattleEntryId,
            ["rule_id"] = battle?.RuleId ?? track.RuleId,
            ["match_type"] = battle?.MatchType ?? track.MatchType,
            ["field_id"] = battle?.FieldId,
            ["kicker_id"] = track.KickerId,
            ["costume_id"] = track.CostumeId,
            ["team"] = track.Team,
            ["status"] = track.Status
        };
        if (battle is { Roster.Count: > 0 })
        {
            var roster = new JsonArray();
            foreach (var slot in battle.Roster)
            {
                roster.Add(new JsonObject
                {
                    ["user_id"] = slot.UserId,
                    ["kicker_id"] = slot.KickerId,
                    ["costume_id"] = slot.CostumeId,
                    ["team"] = slot.Team,
                    ["bot"] = slot.Bot
                });
            }
            context["roster"] = roster;
        }
        return context;
    }

    private void AddEvent(DateTime now, string? userId, string? battleId, string kind, OpsClientInfo info,
        JsonObject payload, string? fingerprint = null)
    {
        // The client address and the headers that have no column of their own ride in the payload.
        if (info.Ip.Length > 0) payload["ip"] = info.Ip;
        if (info.Platform.Length > 0) payload["platform"] = info.Platform;
        if (info.UserAgent.Length > 0) payload["user_agent"] = info.UserAgent;
        Enqueue(new ClientEventRow
        {
            At = now,
            RunId = RunId,
            UserId = NullIfEmpty(userId),
            BattleId = NullIfEmpty(battleId),
            Kind = kind,
            AppVersion = info.AppVersion,
            Device = info.DeviceName,
            Os = info.OsVersion,
            Fingerprint = fingerprint,
            Payload = payload
        });
    }

    private void Enqueue(object item)
    {
        if (Enabled) _queue.Writer.TryWrite(item);
    }

    private void Guard(Action action)
    {
        if (!Enabled) return;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            // Telemetry must never surface as a request failure or break matchmaking.
            _logger.LogWarning("Ops telemetry hook failed: {Error}", ex.Message);
        }
    }

    private static JsonNode? ParseValue(string value)
    {
        var trimmed = value.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try { return JsonNode.Parse(value); }
            catch (JsonException) { }
        }
        return JsonValue.Create(value);
    }

    private static bool TryGetText(JsonObject value, string name, out string text)
    {
        text = "";
        if (!value.TryGetPropertyValue(name, out var node) || node is null) return false;
        text = node is JsonValue scalar && scalar.TryGetValue<string>(out var s) ? s : node.ToJsonString();
        return text.Length > 0;
    }

    /// <summary>Causes become part of a status string: one line, bounded.</summary>
    private static string CleanCause(string? cause)
    {
        var text = (cause ?? "").Trim();
        var newline = text.IndexOfAny(['\r', '\n']);
        if (newline >= 0) text = text[..newline];
        if (text.Length == 0) return "unknown";
        return text.Length <= MaxCauseChars ? text : text[..MaxCauseChars];
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private sealed class FlushMarker
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BattleTrack
    {
        public string BattleId { get; init; } = "";
        public string RunId { get; init; } = "";
        public DateTime CreatedAt { get; init; }
        public int RuleId { get; set; }
        public int? MatchType { get; set; }
        public int FieldId { get; set; }
        public int Humans { get; set; }
        public int Bots { get; set; }
        public bool Custom { get; init; }
        public DateTime? FinalizedAt { get; set; }
        public bool Dropped { get; set; }
        public DateTime? FirstStartAt { get; set; }
        public int BattleTimeSeconds { get; set; } = OpsStatusRules.DefaultBattleTimeSeconds;
        public string? Outcome { get; set; }
        public DateTime? OutcomeAt { get; set; }
        public List<OpsRosterSlot> Roster { get; set; } = [];
        public Dictionary<string, ParticipantTrack> Participants { get; } = new(StringComparer.Ordinal);
        public bool Dirty { get; set; }

        public BattleFacts Facts() => new()
        {
            CreatedAt = CreatedAt,
            FinalizedAt = FinalizedAt,
            Dropped = Dropped,
            FirstStartAt = FirstStartAt,
            BattleTimeSeconds = BattleTimeSeconds
        };

        public BattleRow ToRow(DateTime now) => new()
        {
            BattleId = BattleId,
            RunId = RunId,
            CreatedAt = CreatedAt,
            UpdatedAt = now,
            RuleId = RuleId,
            MatchType = MatchType,
            FieldId = FieldId,
            Humans = Humans > 0 ? Humans : Participants.Count,
            Bots = Bots,
            Custom = Custom,
            FirstStartAt = FirstStartAt,
            BattleTimeSeconds = BattleTimeSeconds,
            Outcome = Outcome
        };

        public static BattleTrack FromRow(BattleRow row) => new()
        {
            BattleId = row.BattleId,
            RunId = row.RunId,
            CreatedAt = row.CreatedAt,
            RuleId = row.RuleId,
            MatchType = row.MatchType,
            FieldId = row.FieldId,
            Humans = row.Humans,
            Bots = row.Bots,
            Custom = row.Custom,
            FirstStartAt = row.FirstStartAt,
            BattleTimeSeconds = row.BattleTimeSeconds,
            FinalizedAt = row.Custom || row.Bots > 0 ? row.CreatedAt : null
        };
    }

    private sealed class ParticipantTrack
    {
        public string UserId { get; init; } = "";
        public string BattleId { get; set; } = "";
        public string BattleEntryId { get; init; } = "";
        public string TicketId { get; init; } = "";
        public int RuleId { get; init; }
        public int? MatchType { get; init; }
        public int KickerId { get; init; }
        public int CostumeId { get; init; }
        public bool IsDiag { get; init; }
        public OpsClientInfo Client { get; init; } = OpsClientInfo.Empty;
        public DateTime EntryAt { get; init; }
        public bool Assigned { get; set; }
        public bool Requeued { get; set; }
        public int Team { get; set; } = -1;
        public DateTime? StartAt { get; set; }
        public DateTime? EndAt { get; set; }
        public DateTime? ResultAt { get; set; }
        public int ReconnectBegins { get; set; }
        public string? ReconnectFailedCause { get; set; }
        public string? DisconnectCause { get; set; }
        public DateTime? LeftAt { get; set; }
        public DateTime? CancelledAt { get; set; }
        public DateTime? TimeoutAt { get; set; }
        public string? MatchingFailedCause { get; set; }
        public DateTime? LastSeenAt { get; set; }
        public DateTime? RelaunchAt { get; set; }
        public int ExceptionCount { get; set; }
        public string Status { get; set; } = OpsStatusRules.Matching;

        public ParticipantFacts Facts() => new()
        {
            EntryAt = EntryAt,
            Assigned = Assigned,
            Requeued = Requeued,
            StartAt = StartAt,
            EndAt = EndAt,
            ResultAt = ResultAt,
            ReconnectBegins = ReconnectBegins,
            ReconnectFailedCause = ReconnectFailedCause,
            DisconnectCause = DisconnectCause,
            LeftAt = LeftAt,
            CancelledAt = CancelledAt,
            TimeoutAt = TimeoutAt,
            MatchingFailedCause = MatchingFailedCause,
            LastSeenAt = LastSeenAt,
            RelaunchAt = RelaunchAt
        };

        public ParticipantRow ToRow(DateTime now) => new()
        {
            BattleId = BattleId,
            UserId = UserId,
            BattleEntryId = BattleEntryId,
            CreatedAt = EntryAt,
            UpdatedAt = now,
            KickerId = KickerId,
            CostumeId = CostumeId,
            Team = Team,
            IsDiag = IsDiag,
            AppVersion = Client.AppVersion,
            DeviceName = Client.DeviceName,
            OsVersion = Client.OsVersion,
            Ip = Client.Ip,
            StartAt = StartAt,
            EndAt = EndAt,
            ResultAt = ResultAt,
            ReconnectBegins = ReconnectBegins,
            ReconnectFailedCause = ReconnectFailedCause,
            DisconnectCause = DisconnectCause,
            ExceptionCount = ExceptionCount,
            Status = Status
        };

        public static ParticipantTrack FromRow(ParticipantRow row) => new()
        {
            UserId = row.UserId,
            BattleId = row.BattleId,
            BattleEntryId = row.BattleEntryId,
            KickerId = row.KickerId,
            CostumeId = row.CostumeId,
            IsDiag = row.IsDiag,
            Client = new OpsClientInfo(row.AppVersion, row.DeviceName, row.OsVersion, "", "", row.Ip),
            EntryAt = row.CreatedAt,
            Assigned = row.Team >= 0 || row.StartAt is not null,
            Team = row.Team,
            StartAt = row.StartAt,
            EndAt = row.EndAt,
            ResultAt = row.ResultAt,
            ReconnectBegins = row.ReconnectBegins,
            ReconnectFailedCause = row.ReconnectFailedCause,
            DisconnectCause = row.DisconnectCause,
            ExceptionCount = row.ExceptionCount,
            LastSeenAt = row.StartAt,
            Status = row.Status
        };
    }
}
