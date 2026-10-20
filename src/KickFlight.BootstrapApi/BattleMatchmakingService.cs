using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using OpenMatch;
using KickFlight.BootstrapApi.PlayerStore;

namespace KickFlight.BootstrapApi;

public sealed class BattleMatchmakingService
{
    private readonly ILogger<BattleMatchmakingService> _logger;
    private readonly IPhotonServerManager _photonManager;
    private readonly MaintenanceState? _maintenance;
    private readonly IReadOnlyList<ScrambleBotDeck> _scrambleBotDecks;
    private readonly ConcurrentDictionary<string, BattleEntrySession> _entriesByTicket = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BattleEntrySession> _entriesByBattleEntryId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveBattleRoom> _roomsByBattleId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TeamLobby> _teamsById = new(StringComparer.Ordinal);
    private readonly object _teamLock = new();
    // Luxon can outlive this API process. Seed the compact numeric suffix from
    // the current UTC second so an API restart cannot accidentally reuse a
    // still-cached Photon room from the previous process.
    private int _battleCounter = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 1_000_000_000);

    public BattleMatchmakingService(ILogger<BattleMatchmakingService> logger, IPhotonServerManager photonManager,
        MaintenanceState? maintenance = null, IWebHostEnvironment? environment = null)
    {
        _logger = logger;
        _photonManager = photonManager;
        _maintenance = maintenance;
        _scrambleBotDecks = ScrambleBotDeckCatalog.Load(
            environment?.ContentRootPath ?? Directory.GetCurrentDirectory(), logger);
        if (ExperimentalAiRoleService.IsEnabled && _scrambleBotDecks.Count == 0)
        {
            _logger.LogWarning(
                "Experimental AI roles enabled ({Flag}=1) but Scramble bot deck catalog is unavailable (count=0); falling back to legacy bot AI parameters (experimental roles inactive)",
                ExperimentalAiRoleService.EnvFlagName);
        }
    }

    // Room lifecycle hooks for the ops telemetry (Ops/OpsTelemetryService). Some are raised while _matchLock is
    // held, so a handler must be quick, must not call back into this service, and only takes leaf locks; Notify
    // swallows whatever it throws, so telemetry can never break matchmaking.
    /// <summary>A human's stream created or joined the room (raised again, harmlessly, when a retried stream re-attaches).</summary>
    public event Action<ActiveBattleRoom, BattleEntrySession>? HumanJoinedRoom;
    /// <summary>A waiting human's stream ended before the window closed without a /battle/cancel (reason "stream_closed").</summary>
    public event Action<ActiveBattleRoom, BattleEntrySession, string>? HumanLeftRoom;
    /// <summary>The window closed: MatchingInfo holds the final roster and AssignedTicketIds who is in it.</summary>
    public event Action<ActiveBattleRoom>? RoomFinalized;
    /// <summary>Every waiting human left before the window closed.</summary>
    public event Action<ActiveBattleRoom>? RoomDropped;
    /// <summary>A ticket was not matched at all (cause "maintenance").</summary>
    public event Action<BattleEntrySession, string>? TicketRefused;

    private void Notify(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Matchmaking telemetry hook failed: {Error}", ex.Message);
        }
    }

    public sealed class BattleEntrySession
    {
        public string TicketId { get; set; } = "";
        public string BattleEntryId { get; set; } = "";
        public string UserId { get; set; } = "";
        public string UserName { get; set; } = "";
        public int KickerId { get; set; } = 1;
        public int KickerCostumeId { get; set; } = 2010101;
        public int BattleRuleId { get; set; } = 1;
        // Snapshotted from BattleRule master when this server issues the entry, and used for result classification.
        public int? BattleMatchType { get; set; }
        public int? BattleRuleType { get; set; }
        public List<int> DeckDiscs { get; set; } = [];
        /// <summary>Stable matchmaking party identity. Null means the player queues alone.</summary>
        public string? PartyId { get; set; }
        /// <summary>
        /// True when the registering /battle/entry (or team) request carried the DIAG version suffix. Hard maintenance
        /// matches these tickets instead of refusing them (see StreamAssignmentsCoreAsync).
        /// </summary>
        public bool IsDiag { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        internal object BattleResultLock { get; } = new();
        internal RankedResultSnapshot? AppliedRankedResult { get; set; }
    }

    public sealed class TeamLobby
    {
        public string MatchmakingTeamId { get; init; } = "";
        public string Code { get; init; } = "";
        public string HostUserId { get; init; } = "";
        public int BattleRuleId { get; init; }
        public Dictionary<string, BattleEntrySession> MembersByUserId { get; } = new(StringComparer.Ordinal);
        public HashSet<string> SelectedUserIds { get; } = new(StringComparer.Ordinal);
        public bool IsStarted { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int? BattleMatchType { get; set; }
        public int? BattleRuleType { get; set; }
    }

    public sealed record TeamMemberEntry(string BattleEntryId, string TicketId, string MatchmakingTeamId);
    public sealed record TeamLobbySnapshot(
        string MatchmakingTeamId, string Code, int BattleRuleId, IReadOnlyList<int> KickerCostumeIdList);
    public sealed record BattleResultEntry(string UserId, int BattleRuleId, int? MatchType, int? BattleRuleType);
    public sealed record RankedResultSnapshot(int BattleRuleType, RankState Before, RankState After);
    public sealed record BattleResultProcessing(BattleResultEntry Entry, RankedResultSnapshot? RankedResult, bool IsReplay);

    public TeamMemberEntry CreateTeam(
        string userId, string userName, int kickerId, int costumeId, int battleRuleId, List<int> deck, string code,
        bool isDiag = false, int? matchType = null, int? battleRuleType = null)
    {
        lock (_teamLock)
        {
            var teamId = $"team-{Guid.NewGuid():N}"[..21];
            var member = RegisterTeamMember(userId, userName, kickerId, costumeId, battleRuleId, deck, teamId,
                isDiag, matchType, battleRuleType);
            var team = new TeamLobby
            {
                MatchmakingTeamId = teamId,
                Code = code.Trim(),
                HostUserId = userId,
                BattleRuleId = battleRuleId,
                BattleMatchType = matchType,
                BattleRuleType = battleRuleType
            };
            team.MembersByUserId.Add(userId, member);
            _teamsById[teamId] = team;
            return new TeamMemberEntry(member.BattleEntryId, member.TicketId, teamId);
        }
    }

    public TeamMemberEntry? JoinTeam(
        string teamId, string userId, string userName, int kickerId, int costumeId, List<int> deck, bool isDiag = false)
    {
        lock (_teamLock)
        {
            if (!_teamsById.TryGetValue(teamId, out var team) || team.IsStarted) return null;
            if (team.MembersByUserId.TryGetValue(userId, out var existing))
                return new TeamMemberEntry(existing.BattleEntryId, existing.TicketId, team.MatchmakingTeamId);
            if (team.MembersByUserId.Count >= MaxPerTeam) return null;

            var member = RegisterTeamMember(userId, userName, kickerId, costumeId, team.BattleRuleId, deck, teamId,
                isDiag, team.BattleMatchType, team.BattleRuleType);
            team.MembersByUserId.Add(userId, member);
            return new TeamMemberEntry(member.BattleEntryId, member.TicketId, team.MatchmakingTeamId);
        }
    }

    public bool StartTeam(string teamId, string hostUserId, IReadOnlyCollection<string> userIds)
    {
        lock (_teamLock)
        {
            if (!_teamsById.TryGetValue(teamId, out var team) || team.HostUserId != hostUserId)
                return false;

            // Photon supplies the players actually present in the host's room. Keep exactly that selected roster
            // as the party so stale invitations and users who already left cannot be assigned to this match.
            var selectedIds = new HashSet<string>(userIds, StringComparer.Ordinal) { hostUserId };
            var selectedMembers = team.MembersByUserId.Keys.Where(selectedIds.Contains).ToHashSet(StringComparer.Ordinal);
            if (team.IsStarted) return selectedMembers.SetEquals(team.SelectedUserIds);

            foreach (var member in team.MembersByUserId.Values)
            {
                // A member who left before the battle can still have an open assignment stream. Make that ticket
                // a solo entry so the remaining members keep their party intact and the leaver can be re-queued.
                if (!selectedMembers.Contains(member.UserId)) member.PartyId = null;
            }
            if (selectedMembers.Count == 0 || selectedMembers.Count > MaxPerTeam) return false;
            team.SelectedUserIds.UnionWith(selectedMembers);
            team.IsStarted = true;
            team.Started.TrySetResult();
            return true;
        }
    }

    public IReadOnlyList<TeamLobbySnapshot> GetTeams(IReadOnlyCollection<string> teamIds)
    {
        lock (_teamLock)
        {
            return teamIds.Distinct(StringComparer.Ordinal)
                .Where(_teamsById.ContainsKey)
                .Select(teamId =>
                {
                    var team = _teamsById[teamId];
                    var costumes = team.MembersByUserId.Values
                        .OrderBy(member => member.CreatedAt)
                        .Select(member => member.KickerCostumeId)
                        .ToArray();
                    return new TeamLobbySnapshot(team.MatchmakingTeamId, team.Code, team.BattleRuleId, costumes);
                })
                .ToList();
        }
    }

    private Task? GetTeamStartTask(string? teamId)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return null;
        lock (_teamLock)
        {
            return _teamsById.TryGetValue(teamId, out var team) ? team.Started.Task : null;
        }
    }

    public sealed class ActiveBattleRoom
    {
        public string BattleId { get; set; } = "";
        public int BattleRuleId { get; set; } = 1;
        public int FieldId { get; set; } = 101;
        public List<BattleEntrySession> HumanPlayers { get; set; } = [];
        public MatchingBattleInfo MatchingInfo { get; set; } = new();

        // --- matchmaking window (2026-09-21) ---
        // Humans keep joining until WindowDeadline; only then do bots take the empty slots. MatchingInfo stays
        // empty while the window is open (interim rosters are built on the fly), which is what keeps the
        // "room already started" replay check honest.
        public DateTime WindowDeadline { get; set; }

        /// <summary>The window closed and the final bot-filled roster was built; no further human can join.</summary>
        public bool IsFinalized { get; set; }

        /// <summary>Completes with the final roster; every waiting GetAssignments stream is parked on it.</summary>
        public TaskCompletionSource<MatchingBattleInfo> Finalized { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Tickets included in this battle; excluded parties are re-queued by their active streams.</summary>
        public HashSet<string> AssignedTicketIds { get; } = new(StringComparer.Ordinal);

        /// <summary>One entry per waiting stream, so a join can refresh the other clients' slots.</summary>
        public List<AssignmentSubscriber> Subscribers { get; } = [];
    }

    /// <summary>
    /// How long after the room's deadline the client's countdown reaches 0. The client sends /battle/timeout once
    /// its countdown hits 0 before the full roster arrives, and the window task only fires 50 ms after the
    /// deadline, so this covers that plus clock drift (the client's "now" is extrapolated from x-app-datetime).
    /// </summary>
    private static readonly TimeSpan MatchmakingExpiryMargin = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The matching countdown is clamp(ceil(expiry - server now), 0, 120) on the client
    /// (NormalMatchingController.GetMatchingTime), and it keeps only the latest expiry it has seen. So send the real
    /// deadline, never a far-future placeholder: a 2030 expiry pinned the timer at 120 and could not be lowered.
    /// The client parses it with DateTimeOffset.TryParse, which reads a bare wall-clock string in the phone's own
    /// zone (a UTC "now + X" was hours in the past on a UTC+8 phone and timed out, 2026-09-14), so the string
    /// carries an explicit UTC designator.
    /// </summary>
    private static string MatchmakingExpiry(ActiveBattleRoom room) =>
        (DateTime.SpecifyKind(room.WindowDeadline, DateTimeKind.Utc) + MatchmakingExpiryMargin)
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// 4v4: eight slots, so four humans per team is also the join cap. Shared by the join check and the bot fill;
    /// `tests/test_teamtype.py` guards it against drifting from the team rules.
    /// </summary>
    public const int MaxPerTeam = 4;

    public sealed class MatchingBattleInfo
    {
        public string matchmakingExpirationDatetime { get; set; } = "2030-01-01 00:00:00";
        public int photonCloudRegionId { get; set; } = 1;
        public List<MatchingPlayerBattleInfo> battlePlayerList { get; set; } = [];
    }

    public sealed class MatchingPlayerBattleInfo
    {
        public string userId { get; set; } = "";
        public string matchmakingTeamId { get; set; } = "";
        public string battleEntryId { get; set; } = "";
        public string name { get; set; } = "";
        public int rank { get; set; } = 13;
        public int kickerId { get; set; } = 1;
        public int kickerCostumeId { get; set; } = 1;
        public int honorId { get; set; } = 6010000;
        public int teamType { get; set; } = 0; // 0 = Blue, 1 = Red (Colorful.TeamType)
        public int kickerAiParameterId { get; set; } = 0; // 0 for human, > 0 for bot
        public int kickerAiDiscDeckId { get; set; } = 0; // 0 for human, > 0 for bot
        public string languageCode { get; set; } = "es";
        public int frameId { get; set; } = 1;
        public int discId1 { get; set; } = 3010001;
        public int discLevel1 { get; set; } = 10;
        public int discId2 { get; set; } = 3010002;
        public int discLevel2 { get; set; } = 10;
        public int discId3 { get; set; } = 3010003;
        public int discLevel3 { get; set; } = 10;
        public int discId4 { get; set; } = 3010004;
        public int discLevel4 { get; set; } = 10;
        public int gearId1 { get; set; } = 0;
        public int gearId2 { get; set; } = 0;
        public int gearId3 { get; set; } = 0;
    }

    public (string battleEntryId, string ticketId) RegisterEntry(
        string userId, string userName, int kickerId, int costumeId, int battleRuleId, List<int> deck,
        string? partyId = null, bool isDiag = false, int? matchType = null, int? battleRuleType = null)
    {
        var session = RegisterTeamMember(userId, userName, kickerId, costumeId, battleRuleId, deck, partyId,
            isDiag, matchType, battleRuleType);
        return (session.BattleEntryId, session.TicketId);
    }

    private BattleEntrySession RegisterTeamMember(
        string userId, string userName, int kickerId, int costumeId, int battleRuleId, List<int> deck, string? partyId,
        bool isDiag = false, int? matchType = null, int? battleRuleType = null)
    {
        var entryId = $"be-{Guid.NewGuid():N}"[..12];
        var ticketId = $"ticket-{Guid.NewGuid():N}"[..16];

        var session = new BattleEntrySession
        {
            TicketId = ticketId,
            BattleEntryId = entryId,
            UserId = userId,
            UserName = userName,
            KickerId = kickerId,
            KickerCostumeId = costumeId,
            BattleRuleId = battleRuleId,
            BattleMatchType = matchType,
            BattleRuleType = battleRuleType,
            DeckDiscs = deck,
            PartyId = string.IsNullOrWhiteSpace(partyId) ? null : partyId.Trim(),
            IsDiag = isDiag
        };

        _entriesByTicket[ticketId] = session;
        _entriesByBattleEntryId[entryId] = session;
        _logger.LogInformation("Registered battle entry: user={UserId} ({UserName}), ticket={TicketId}, kicker={KickerId}, rule={RuleId}",
            userId, userName, ticketId, kickerId, battleRuleId);

        return session;
    }

    // Arenas that ship complete in the capture: field/fld{id:05}, fielddata/fld{id:05}_{1,2,3} (crystal / flag / ball
    // variants), itemdata/ite{id:05}_{1,2,3} and minimap/mim{id:05}_0. 102/302/402/602/702/902 have a model but no
    // fielddata, 11/0 are tutorial, 801 is the Trial arena, 9000x are the home stages. Every room draws one at random;
    // /battle/start answers it (the client only reads the field from that response). Set KF_FIELDS=101 to pin one.
    public static readonly int[] FieldPool = ParseFieldPool(Environment.GetEnvironmentVariable("KF_FIELDS"));
    private static readonly Random _fieldRandom = new();

    private static int[] ParseFieldPool(string? value)
    {
        var ids = (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => int.TryParse(v, out var id) ? id : 0).Where(id => id > 0).ToArray();
        return ids.Length > 0 ? ids : [101, 301, 401, 601, 701, 901];
    }

    public static int PickRandomField()
    {
        lock (_fieldRandom) return FieldPool[_fieldRandom.Next(FieldPool.Length)];
    }

    /// <summary>Field of an existing room (both humans of a room must load the same arena), else null.</summary>
    public int? GetRoomFieldId(string battleId)
    {
        return _roomsByBattleId.TryGetValue(battleId, out var room) ? room.FieldId : null;
    }

    /// <summary>The mode selected when this room was created; it remains authoritative across schedule changes.</summary>
    public int? GetRoomBattleRuleId(string battleId) =>
        _roomsByBattleId.TryGetValue(battleId, out var room) ? room.BattleRuleId : null;

    /// <summary>
    /// The final roster of a room still in memory, or an empty list. Used by /battle/end to snapshot the players
    /// (with their kickerCostumeId row ids) into the replay index; a restarted process has no rooms, which is why
    /// the upload's own battle/result JSON is the primary roster source.
    /// </summary>
    public IReadOnlyList<MatchingPlayerBattleInfo> GetRoomRoster(string battleId) =>
        _roomsByBattleId.TryGetValue(battleId, out var room) ? room.MatchingInfo.battlePlayerList : [];

    /// <summary>The rule selected on entry, retained for result reporting after the room has started.</summary>
    public int? GetBattleRuleIdForEntry(string battleEntryId) =>
        _entriesByBattleEntryId.TryGetValue(battleEntryId, out var entry) ? entry.BattleRuleId : null;

    public BattleResultEntry? GetBattleResultEntry(string battleEntryId) =>
        _entriesByBattleEntryId.TryGetValue(battleEntryId, out var entry)
            ? new BattleResultEntry(entry.UserId, entry.BattleRuleId, entry.BattleMatchType, entry.BattleRuleType)
            : null;

    /// <summary>
    /// Applies and caches one ranked result for an owned, server-issued entry. The per-entry gate keeps concurrent
    /// retries from incrementing twice; the cache is assigned only after the caller's persistence callback succeeds.
    /// </summary>
    public BattleResultProcessing? ProcessRankedResultOnce(
        string battleEntryId, string userId, Func<int, RankedResultSnapshot> persistRankedResult)
    {
        if (!_entriesByBattleEntryId.TryGetValue(battleEntryId, out var entry) || entry.UserId != userId)
            return null;

        lock (entry.BattleResultLock)
        {
            var resultEntry = new BattleResultEntry(entry.UserId, entry.BattleRuleId,
                entry.BattleMatchType, entry.BattleRuleType);
            if (entry.BattleMatchType != 2 || entry.BattleRuleType is not (>= 1 and <= 3))
                return new BattleResultProcessing(resultEntry, null, false);

            if (entry.AppliedRankedResult is { } cached)
                return new BattleResultProcessing(resultEntry, cached, true);

            var applied = persistRankedResult(entry.BattleRuleType.Value);
            entry.AppliedRankedResult = applied;
            return new BattleResultProcessing(resultEntry, applied, false);
        }
    }

    private readonly object _matchLock = new();
    private ActiveBattleRoom? _pendingRoom;

    /// <summary>Tickets withdrawn with /battle/cancel; a stream still open for one never joins or re-queues. Guarded by _matchLock.</summary>
    private readonly HashSet<string> _cancelledTickets = new(StringComparer.Ordinal);

    /// <summary>One token source per open GetAssignments stream, so CancelEntry can end it. Guarded by _matchLock.</summary>
    private readonly Dictionary<string, List<CancellationTokenSource>> _streamsByTicket = new(StringComparer.Ordinal);

    // How long a room stays open for more humans. The first entry opens this base window and every human that
    // joins extends the deadline (JoinIncrementSeconds); bots fill the empty slots after it expires.
    // KF_MATCH_WINDOW_SECONDS=0 disables the window (start as soon as a second human appears).
    public TimeSpan MatchWindow { get; set; } =
        TimeSpan.FromSeconds(ParseMatchConfiguration("KF_MATCH_WINDOW_SECONDS", 40.0));

    /// <summary>
    /// Seconds the second human adds to the window; each later join adds a tenth of this less: 5, 4.5, 4, ...
    /// Tune with KF_MATCH_JOIN_INCREMENT_SECONDS; 0 means joins do not extend the window.
    /// </summary>
    public double JoinIncrementBaseSeconds { get; set; } =
        ParseMatchConfiguration("KF_MATCH_JOIN_INCREMENT_SECONDS", 5.0);

    /// <summary>
    /// How much a join extends the window, counting the joiner: 5 s for the second human, then half a second
    /// less each time (4.5, 4, ... 2 s for the eighth), so eight humans total 40 + 5 + 4.5 + ... + 2 = 64.5 s.
    /// </summary>
    public double JoinIncrementSeconds(int humansInRoom)
    {
        if (humansInRoom < 2) return 0;
        var step = JoinIncrementBaseSeconds / 10.0;
        return Math.Max(0, JoinIncrementBaseSeconds - step * (humansInRoom - 2));
    }

    /// <summary>
    /// Seconds the Photon room master waits in the matching scene for the other humans before it starts the battle
    /// (home/index `battleStartWaitTime`). The client's NormalMatchingJoinBattleRoomState starts the countdown when the
    /// master enters the room, restarts it on every CallbackPlayerEnteredRoom, and sends /battle/start the moment the
    /// room holds every human of the assignment (NormalMatchingController.IsBattleStart: MaxPlayerCount - AICount ==
    /// room player count) or the countdown hits 0. Serving 0 made the master start instantly, so a human whose
    /// Name -> Master -> GameServer hop took longer than the master's scene load (RoomIsCreatePlayer at +2.6 s) joined a
    /// room that had already instantiated its players and never got in (5 of 5 failed rooms, 2026-10-10).
    /// The wait ends early once everyone is present, so it only costs time when a matched human never arrives.
    /// KF_BATTLE_START_WAIT_SECONDS overrides it; 0 restores the old behaviour.
    /// </summary>
    public static readonly double BattleStartWaitSeconds = ParseMatchConfiguration("KF_BATTLE_START_WAIT_SECONDS", 10.0);

    private static double ParseMatchConfiguration(string variable, double fallback)
    {
        // Invariant culture: with a comma-decimal culture "5.0" parses as 50.
        return double.TryParse(Environment.GetEnvironmentVariable(variable), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
            ? seconds
            : fallback;
    }

    /// <summary>A room takes more humans while its window is open and it still has a free slot.</summary>
    private static bool IsJoinable(ActiveBattleRoom? room, BattleEntrySession player)
    {
        return room is not null
            && !room.IsFinalized
            && room.BattleRuleId == player.BattleRuleId
            && room.HumanPlayers.Count < MaxPerTeam * 2;
    }

    /// <summary>
    /// Closes the room's window: builds the final roster (humans, plus bots for the empty slots) and wakes every
    /// waiting stream. Idempotent — both the window task and a room that filled up with humans call it.
    /// </summary>
    private void FinalizeRoom(ActiveBattleRoom room)
    {
        MatchingBattleInfo roster;
        lock (_matchLock)
        {
            // Nothing to finalize once every human has left, and a room that reached 8 humans was already
            // finalized by the join that filled it.
            if (room.IsFinalized || room.HumanPlayers.Count == 0) return;

            var allocation = SelectBattleParties(room.HumanPlayers, room.BattleId);
            if (allocation.Assignments.Count == 0) return;
            room.IsFinalized = true;
            foreach (var player in allocation.Assignments.Keys) room.AssignedTicketIds.Add(player.TicketId);
            roster = BuildRoster(room, allocation.Assignments);
            room.MatchingInfo = roster;
            if (ReferenceEquals(_pendingRoom, room)) _pendingRoom = null;
        }

        _logger.LogInformation(
            "Matchmaking window of room {BattleId} closed with {Humans} human(s); filling empty slots with bots",
            room.BattleId, room.AssignedTicketIds.Count);

        Notify(() => RoomFinalized?.Invoke(room));
        room.Finalized.TrySetResult(roster);
    }

    /// <summary>
    /// Waits out the room's window. Task.Delay cannot be extended, so the deadline is re-read after each delay:
    /// that is what lets a join push the end of the window further away.
    /// </summary>
    private async Task RunMatchWindowAsync(ActiveBattleRoom room)
    {
        try
        {
            while (true)
            {
                TimeSpan remaining;
                lock (_matchLock)
                {
                    if (room.IsFinalized || room.HumanPlayers.Count == 0) return;
                    remaining = room.WindowDeadline - DateTime.UtcNow;
                }

                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining + TimeSpan.FromMilliseconds(50));
            }
        }
        catch (Exception ex)
        {
            // Fire and forget: a fault here would leave a room that can never start.
            _logger.LogError(ex, "Matchmaking window of room {BattleId} failed", room.BattleId);
        }

        FinalizeRoom(room);
    }

    /// <summary>
    /// /battle/cancel, the matching screen's back button: withdraws a waiting human from its room right away instead of
    /// when its GetAssignments stream ends, and drops the room with the last one. False (and nothing changes) for an
    /// unknown entry or one already assigned to a room whose window closed: a battle that has its final roster, and
    /// may already have sent Stage 3, is never taken apart from here.
    /// </summary>
    public bool CancelEntry(string battleEntryId)
    {
        if (string.IsNullOrWhiteSpace(battleEntryId)) return false;

        List<CancellationTokenSource>? streams;
        lock (_matchLock)
        {
            if (!_entriesByBattleEntryId.TryGetValue(battleEntryId, out var session)) return false;
            var ticketId = session.TicketId;
            if (_roomsByBattleId.Values.Any(room => room.IsFinalized && room.AssignedTicketIds.Contains(ticketId)))
                return false;

            // Null when the stream has not attached yet (or an excluded party member is between rooms).
            var openRoom = _roomsByBattleId.Values.FirstOrDefault(room =>
                !room.IsFinalized && room.HumanPlayers.Any(player => player.TicketId == ticketId));

            _entriesByBattleEntryId.TryRemove(battleEntryId, out _);
            _entriesByTicket.TryRemove(ticketId, out _);
            _cancelledTickets.Add(ticketId);

            var humansLeft = 0;
            if (openRoom is not null)
            {
                openRoom.HumanPlayers.RemoveAll(player => player.TicketId == ticketId);
                humansLeft = openRoom.HumanPlayers.Count;
                if (humansLeft == 0) DropRoom(openRoom);
            }

            _logger.LogInformation(
                "Cancelled battle entry {BattleEntryId} for user {UserId} (room {BattleId}, {Humans} human(s) left)",
                battleEntryId, session.UserId, openRoom?.BattleId ?? "none", humansLeft);
            _streamsByTicket.Remove(ticketId, out streams);
        }

        // End the ticket's open streams too. After the cancel succeeds the client keeps waiting for its GetAssignments
        // call to finish before it leaves the matching screen; a stream left parked on the dropped room's window froze
        // it there (2026-10-05). Cancelled outside the lock: the streams' own cleanup takes it.
        foreach (var stream in streams ?? [])
        {
            try
            {
                stream.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // That stream already ended on its own.
            }
        }
        return true;
    }

    /// <summary>Forgets a room every waiting human left. Caller holds _matchLock.</summary>
    private void DropRoom(ActiveBattleRoom room)
    {
        _roomsByBattleId.TryRemove(room.BattleId, out _);
        if (ReferenceEquals(_pendingRoom, room)) _pendingRoom = null;
        _logger.LogInformation("Room {BattleId} dropped: every waiting human left", room.BattleId);
        Notify(() => RoomDropped?.Invoke(room));
    }

    // ResolveAssignment/ResolveAssignmentAsync used to live here: the pre-stream matching path, unreachable since
    // GetAssignments took over (nothing in src/ or tests/ called it) and unable to compile without _pendingRoomTcs,
    // which the per-room window now owns.

    /// <summary>Fallback session for a ticket this process does not know (demo tickets).</summary>
    private static BattleEntrySession DemoEntrySession(string ticketId) => new()
    {
        TicketId = ticketId,
        BattleEntryId = "be-demo-001",
        UserId = "1000001",
        UserName = "Gixarde3",
        KickerId = 1,
        KickerCostumeId = 2010101,
        BattleRuleId = 1,
        DeckDiscs = [3010001, 3010002, 3010003, 3010004]
    };

    public async Task StreamAssignmentsAsync(
        string ticketId,
        IServerStreamWriter<GetAssignmentsResponse> responseStream,
        CancellationToken cancellationToken,
        bool isDiag = false)
    {
        // A cancelled ticket's stream, whether it attached before /battle/cancel or after, completes normally (gRPC
        // status OK, same as the excluded-party path) instead of waiting for a room it is no longer in.
        using var withdrawal = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_matchLock)
        {
            if (_cancelledTickets.Contains(ticketId))
            {
                _logger.LogInformation("Ticket {TicketId} was cancelled; ending its GetAssignments stream", ticketId);
                return;
            }
            if (!_streamsByTicket.TryGetValue(ticketId, out var open)) _streamsByTicket[ticketId] = open = [];
            open.Add(withdrawal);
        }

        try
        {
            await StreamAssignmentsCoreAsync(ticketId, responseStream, withdrawal.Token, isDiag);
        }
        catch (OperationCanceledException) when (withdrawal.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Ended GetAssignments stream of cancelled ticket {TicketId}", ticketId);
        }
        finally
        {
            lock (_matchLock)
            {
                if (_streamsByTicket.TryGetValue(ticketId, out var open) && open.Remove(withdrawal) && open.Count == 0)
                    _streamsByTicket.Remove(ticketId);
            }
        }
    }

    private async Task StreamAssignmentsCoreAsync(
        string ticketId,
        IServerStreamWriter<GetAssignmentsResponse> responseStream,
        CancellationToken cancellationToken,
        bool isDiag = false)
    {
        var playerSession = _entriesByTicket.TryGetValue(ticketId, out var registered)
            ? registered
            : DemoEntrySession(ticketId);

        // Team tickets can be returned while members are still recruiting. Do not let an early client poll
        // create or join a battle room until the host's /battle/teamEntry commits the selected roster.
        var teamStart = GetTeamStartTask(playerSession.PartyId);
        if (teamStart is not null)
        {
            await teamStart.WaitAsync(cancellationToken);
        }

        // A reconnect/retry for a ticket whose room already started must replay the same final
        // assignment. Creating a second room here leaves the client and Photon
        // with different room identities and makes recovery impossible.
        // Only a finalized room counts: while the window is open the roster is deliberately partial, so replaying
        // on a non-empty roster would hand out a Connection before the match exists.
        ActiveBattleRoom? completedRoom;
        lock (_matchLock)
        {
            completedRoom = _roomsByBattleId.Values.FirstOrDefault(room =>
                room.IsFinalized &&
                room.AssignedTicketIds.Contains(ticketId));
        }
        if (completedRoom is not null)
        {
            _logger.LogInformation(
                "Replaying completed Stage 3 assignment {BattleId} for ticket {TicketId}",
                completedRoom.BattleId,
                ticketId);
            await SendAssignmentUpdateAsync(responseStream, completedRoom.BattleId, completedRoom.MatchingInfo);
            await HoldFinalAssignmentAsync(playerSession.UserId, cancellationToken);
            return;
        }

        var subscriber = new AssignmentSubscriber(responseStream);
        ActiveBattleRoom room;
        bool joinedExistingRoom;
        bool roomIsFull;
        MatchingBattleInfo interimRoster;

        lock (_matchLock)
        {
            if (_cancelledTickets.Contains(ticketId))
            {
                _logger.LogInformation("Ticket {TicketId} was cancelled; not joining a room", ticketId);
                return;
            }

            // gRPC does not pass through DemoSessionApi's 503: a ticket issued just before hard maintenance must not
            // open or join a room either. Replays of rooms that already started (above) still go out. A DIAG ticket
            // (the registering /battle/entry carried the suffix, or the GetAssignments metadata did) keeps matching.
            if (_maintenance?.Current.Mode == MaintenanceMode.Hard)
            {
                if (!playerSession.IsDiag && !isDiag)
                {
                    _logger.LogInformation("Maintenance: ticket {TicketId} not matched", ticketId);
                    Notify(() => TicketRefused?.Invoke(playerSession, "maintenance"));
                    return;
                }
                _logger.LogDebug("Maintenance: DIAG ticket {TicketId} allowed to match", ticketId);
            }

            var openRoom = IsJoinable(_pendingRoom, playerSession) ? _pendingRoom : null;

            if (openRoom is not null)
            {
                // The second, third, ... human lands in the first player's room while its window is open.
                room = openRoom;
                joinedExistingRoom = true;
                var humansBefore = room.HumanPlayers.Count;

                // A retried stream, or the same user entering again with a fresh ticket, replaces its own stale
                // session instead of taking a second slot.
                room.HumanPlayers.RemoveAll(player =>
                    player.UserId == playerSession.UserId || player.TicketId == ticketId);
                room.HumanPlayers.Add(playerSession);
                CanonicalizeHumanOrder(room);

                if (room.HumanPlayers.Count > humansBefore)
                {
                    // Each human that really joined (not a re-attach) pushes the deadline out.
                    for (var count = humansBefore + 1; count <= room.HumanPlayers.Count; count++)
                    {
                        room.WindowDeadline += TimeSpan.FromSeconds(JoinIncrementSeconds(count));
                    }

                    _logger.LogInformation(
                        "Human {UserId} joined room {BattleId} ({Humans} human(s) waiting); deadline now {Deadline:O}",
                        playerSession.UserId, room.BattleId, room.HumanPlayers.Count, room.WindowDeadline);
                }
                else
                {
                    _logger.LogInformation("Ticket {TicketId} re-attached to open room {BattleId}",
                        ticketId, room.BattleId);
                }
            }
            else
            {
                room = new ActiveBattleRoom
                {
                    BattleId = $"battle-{Interlocked.Increment(ref _battleCounter)}",
                    BattleRuleId = playerSession.BattleRuleId,
                    FieldId = PickRandomField(),
                    HumanPlayers = [playerSession],
                    WindowDeadline = DateTime.UtcNow + MatchWindow
                };
                _roomsByBattleId[room.BattleId] = room;
                _pendingRoom = room;
                joinedExistingRoom = false;
                _logger.LogInformation(
                    "Created battle room {BattleId} for user {UserId}; window open for {Seconds:0.#}s",
                    room.BattleId, playerSession.UserId, MatchWindow.TotalSeconds);
            }

            room.Subscribers.Add(subscriber);
            interimRoster = BuildHumanRoster(room);
            roomIsFull = room.HumanPlayers.Count >= MaxPerTeam * 2;
        }

        Notify(() => HumanJoinedRoom?.Invoke(room, playerSession));

        if (!joinedExistingRoom)
        {
            // Only the stream that opened the room starts the window task; it follows deadline moves.
            _ = RunMatchWindowAsync(room);
        }

        try
        {
            // --- STAGE 1: the humans waiting in this room, so a late joiner sees everyone at once and the clients
            // already waiting watch each other arrive. No bots yet: they only take the empty slots once the window
            // closes.
            await subscriber.SendStageAsync("", interimRoster);
            _logger.LogInformation(
                "Streamed Stage 1 (room preparation / searching; {Humans} human(s)) for user {UserId}",
                interimRoster.battlePlayerList.Count, playerSession.UserId);

            if (joinedExistingRoom)
            {
                await BroadcastInterimRosterAsync(room, subscriber, interimRoster);
            }

            if (roomIsFull)
            {
                // No slot left for anyone else: no reason to keep the window open.
                FinalizeRoom(room);
            }

            // --- STAGE 2: every waiting stream has been parked here for the whole window, which closes when the
            // deadline runs out or the room filled up with humans; the empty slots are bots by now.
            var fullRoster = await room.Finalized.Task.WaitAsync(cancellationToken);

            if (!room.AssignedTicketIds.Contains(ticketId))
            {
                // Keep an indivisible party together for the next match when it does not fit this roster. A human
                // who cancelled while waiting lands here too (no longer in the roster) and simply ends its stream.
                bool cancelled;
                lock (_matchLock)
                {
                    room.Subscribers.Remove(subscriber);
                    cancelled = _cancelledTickets.Contains(ticketId);
                }
                if (cancelled) return;
                _logger.LogInformation(
                    "Re-queueing excluded party member {UserId} from room {BattleId}",
                    playerSession.UserId, room.BattleId);
                await StreamAssignmentsCoreAsync(ticketId, responseStream, cancellationToken, isDiag);
                return;
            }

            await subscriber.SendStageAsync("", fullRoster);
            _logger.LogInformation("Streamed Stage 2 (full roster / Iniciar combate) for user {UserId}", playerSession.UserId);

            // Keep this close to the successful client trace: the runner primes the
            // start control before Stage 1, and Stage 3 must arrive while that local
            // ready state is still active. A longer (12 s) hold was consumed and
            // acknowledged by both clients but neither opened Photon afterwards.
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

            // --- STAGE 3: Final assignment with non-empty Connection -> triggers Success (Result 1) and enters battle! ---
            await subscriber.SendFinalStageAsync(room.BattleId, fullRoster);
            _logger.LogInformation("Streamed Stage 3 (battle start assignment: {BattleId}) for user {UserId}", room.BattleId, playerSession.UserId);

            // Keep the server-streaming call alive until the client consumes Stage 3 and
            // cancels GetAssignments while changing state.  Completing the RPC here can
            // enqueue the terminal callback beside the Stage 3 callback on Unity's
            // SynchronizationContext; on slower emulators that race leaves the client in
            // MatchingScene even though the final assignment was delivered successfully.
            // The upper bound only protects abandoned clients; healthy clients cancel
            // this delay as soon as they enter JoinBattleRoom.
            await HoldFinalAssignmentAsync(playerSession.UserId, cancellationToken);
        }
        finally
        {
            // Leaving the matching screen destroys the GetAssignments stream: a stream that ends before its room
            // starts means the waiting human withdrew. Drop them, and drop the room with the last one. After a
            // /battle/cancel (CancelEntry) they are already gone, so only remove/drop when this stream still had a slot.
            lock (_matchLock)
            {
                room.Subscribers.Remove(subscriber);

                if (!room.IsFinalized && room.HumanPlayers.RemoveAll(player => player.TicketId == ticketId) > 0)
                {
                    Notify(() => HumanLeftRoom?.Invoke(room, playerSession, "stream_closed"));
                    if (room.HumanPlayers.Count == 0) DropRoom(room);
                }
            }
        }
    }

    private async Task HoldFinalAssignmentAsync(string userId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Holding GetAssignments open for Stage 3 acknowledgement from user {UserId}", userId);
        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
    }

    /// <summary>
    /// Refreshes the other waiting clients' slots with the room's humans (the joiner already got the same roster as
    /// its Stage 1). A client that is gone only fails its own stream, which cleans itself up.
    /// </summary>
    private async Task BroadcastInterimRosterAsync(
        ActiveBattleRoom room,
        AssignmentSubscriber sender,
        MatchingBattleInfo roster)
    {
        AssignmentSubscriber[] subscribers;
        lock (_matchLock) subscribers = [.. room.Subscribers];

        foreach (var subscriber in subscribers)
        {
            if (ReferenceEquals(subscriber, sender)) continue;

            try
            {
                await subscriber.BroadcastAsync(roster);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Interim roster update to a waiting stream of room {BattleId} failed", room.BattleId);
            }
        }
    }

    private static void CanonicalizeHumanOrder(ActiveBattleRoom room)
    {
        // The original client derives team/room ownership from roster position.
        // Arrival order varies with emulator load, so never let it alter the
        // authoritative player ordering sent to either client.
        room.HumanPlayers.Sort((left, right) =>
        {
            var userComparison = string.CompareOrdinal(left.UserId, right.UserId);
            return userComparison != 0
                ? userComparison
                : string.CompareOrdinal(left.BattleEntryId, right.BattleEntryId);
        });
    }

    private static async Task SendAssignmentUpdateAsync(
        IServerStreamWriter<GetAssignmentsResponse> responseStream,
        string connection,
        MatchingBattleInfo roster)
    {
        var json = JsonSerializer.Serialize(roster);
        var battleStruct = Struct.Parser.ParseJson(json);
        var propertiesStruct = new Struct();
        propertiesStruct.Fields["battle"] = Value.ForStruct(battleStruct);
        propertiesStruct.Fields["RoomBattleInfo"] = Value.ForStruct(battleStruct);
        propertiesStruct.Fields["matchStatus"] = Value.ForStruct(battleStruct);

        var response = new GetAssignmentsResponse
        {
            Assignment = new Assignment
            {
                Connection = connection,
                Properties = propertiesStruct
            }
        };

        await responseStream.WriteAsync(response);
    }

    /// <summary>
    /// One waiting client. Its stream may be written by two things at once — its own stage sequence and the
    /// interim roster broadcast that a join triggers — and they must not interleave: gRPC allows a single
    /// in-flight write per stream, and a broadcast landing after the final assignment would drag the client back
    /// to the matching screen.
    /// </summary>
    public sealed class AssignmentSubscriber
    {
        private readonly IServerStreamWriter<GetAssignmentsResponse> _responseStream;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _stagingComplete;

        public AssignmentSubscriber(IServerStreamWriter<GetAssignmentsResponse> responseStream)
        {
            _responseStream = responseStream;
        }

        /// <summary>Shows the waiting client the room's current humans; a no-op once its final assignment went out.</summary>
        public async Task BroadcastAsync(MatchingBattleInfo roster)
        {
            await _gate.WaitAsync();
            try
            {
                if (_stagingComplete) return;
                await SendAssignmentUpdateAsync(_responseStream, "", roster);
            }
            finally
            {
                _gate.Release();
            }
        }

        public Task SendStageAsync(string connection, MatchingBattleInfo roster) =>
            SendAsync(connection, roster, stagingComplete: false);

        /// <summary>Stage 3: with the Connection set the client leaves the matching screen, so stop broadcasting.</summary>
        public Task SendFinalStageAsync(string connection, MatchingBattleInfo roster) =>
            SendAsync(connection, roster, stagingComplete: true);

        private async Task SendAsync(string connection, MatchingBattleInfo roster, bool stagingComplete)
        {
            await _gate.WaitAsync();
            try
            {
                if (stagingComplete) _stagingComplete = true;
                await SendAssignmentUpdateAsync(_responseStream, connection, roster);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public sealed record BotProfile(int KickerId, string Name, int CostumeRowId, int RoleType, string Archetype);

    // Gym mode (toggled at runtime with GET /gym/on | /gym/off | /gym): the next match is the human(s) on Blue against
    // exactly GymBotCount mannequin bots on Red, and /battle/start hands out the harmless guardian row. Mannequin =
    // kickerAiParameterId 100 + kickerId: the client patch (scripts/re/bot_special_skill_cave.py) sets
    // PlayerCharacter.AIOption = Mannequin (63) for any id >= 100, and DemoSessionApi serves those ids as copies of
    // the real AI rows with zero motivation so an unpatched client gets a mostly idle bot too.
    public static volatile bool GymEnabled;
    public const int GymAiParameterBase = 100;
    public const int GymBotCount = 3;

    // Local combat-balance test roster. Opt in only on a local API process with
    // KF_TEST_BOT_DISCS=1; production/default matches keep the ordinary starter deck.
    private static readonly bool TestBotDiscDeckEnabled =
        string.Equals(Environment.GetEnvironmentVariable("KF_TEST_BOT_DISCS"), "1", StringComparison.Ordinal);
    private static readonly int[] TestBotDiscIds = [3010020, 3010022, 3010134, 3010082];

    // Names from config/masters_kicker.json. Bots are taken in this order (skipping the human's kicker), so the
    // kickers whose weapons/skills were reworked on 2026-09-19 (Owlbert drone + smog, Buzzy Big shields + front
    // barrier, Yuyan nunchaku + panda, Sid wrist lasers) come first and show up in every solo match for testing.
    private static readonly BotProfile[] BotProfiles =
    [
        new(5, "Owlbert Bot", 2050101, 1, "owlbert_support"),
        new(12, "Buzzy Big Bot", 2120101, 3, "anchor"),
        new(10, "Yuyan Bot", 2100101, 2, "hunter"),
        new(14, "Sid Bot", 2140101, 1, "hunter"),
        new(1, "Tsubame Bot", 2010101, 0, "carrier"),
        new(2, "Ruriha Bot", 2020101, 1, "support"),
        new(3, "Coco Bot", 2030101, 3, "anchor"),
        new(4, "Kite Bot", 2040101, 2, "damage"),
        new(6, "Pitophy Bot", 2060101, 2, "damage"),
        new(7, "Grenhawk Bot", 2070101, 1, "mobile_pressure"),
        new(8, "Anna Bot", 2080101, 0, "carrier"),
        new(9, "Jay Bot", 2090101, 0, "carrier"),
        new(11, "Diatrius Bot", 2110101, 3, "anchor"),
        new(13, "Hitagi Bot", 2130101, 2, "damage")
    ];

    /// <summary>
    /// What a waiting client sees while the room's window is still open: the humans already in it and no bots.
    /// </summary>
    private static MatchingBattleInfo BuildHumanRoster(ActiveBattleRoom room)
    {
        return new MatchingBattleInfo
        {
            // Drives the client's countdown. Every join re-sends this roster to all waiting clients
            // (BroadcastInterimRosterAsync), so they pick up the extended deadline.
            matchmakingExpirationDatetime = MatchmakingExpiry(room),
            photonCloudRegionId = 1,
            battlePlayerList = BuildHumanEntries(room.HumanPlayers, AssignHumanTeamsForInterim(room.HumanPlayers))
        };
    }

    private static List<MatchingPlayerBattleInfo> BuildHumanEntries(
        IReadOnlyList<BattleEntrySession> players,
        IReadOnlyDictionary<BattleEntrySession, int>? fixedTeams)
    {
        var entries = new List<MatchingPlayerBattleInfo>();
        var teams = fixedTeams ?? AssignHumanTeamsForInterim(players);
        if (teams.Count != players.Count) teams = AssignHumanTeamsForInterim(players);

        for (int i = 0; i < players.Count; i++)
        {
            var p = players[i];
            int team = teams[p];

            var playerName = p.UserName;
            if (i > 0 && playerName == players[0].UserName)
            {
                playerName = $"{p.UserName} (P{i + 1})";
            }

            var discs = p.DeckDiscs.Count >= 4 ? p.DeckDiscs : [3010001, 3010002, 3010003, 3010004];
            entries.Add(new MatchingPlayerBattleInfo
            {
                userId = p.UserId,
                matchmakingTeamId = $"team-{(team == 0 ? 1 : 2)}",
                battleEntryId = p.BattleEntryId,
                name = playerName,
                rank = 13,
                kickerId = p.KickerId,
                kickerCostumeId = p.KickerCostumeId,
                honorId = 6010000,
                teamType = team,
                kickerAiParameterId = 0, // Human
                kickerAiDiscDeckId = 0,  // Human
                languageCode = "es",
                frameId = 1,
                discId1 = discs[0], discLevel1 = 10,
                discId2 = discs[1], discLevel2 = 10,
                discId3 = discs[2], discLevel3 = 10,
                discId4 = discs[3], discLevel4 = 10
            });
        }

        return entries;
    }

    /// <summary>
    private static Dictionary<BattleEntrySession, int> AssignHumanTeamsForInterim(
        IReadOnlyList<BattleEntrySession> players)
    {
        var parties = players
            .GroupBy(player => player.PartyId is { Length: > 0 } partyId
                ? $"party:{partyId}"
                : $"solo:{player.TicketId}", StringComparer.Ordinal)
            .Select(group => group.ToList())
            .ToList();

        // Recruiting may temporarily contain a party combination that cannot fit. Keep groups intact in the
        // waiting display; final allocation below selects the largest compatible set for the battle.
        var interimTeams = new Dictionary<BattleEntrySession, int>();
        var counts = new int[2];
        foreach (var party in parties)
        {
            var team = counts[0] <= counts[1] ? 0 : 1;
            foreach (var player in party) interimTeams[player] = team;
            counts[team] += party.Count;
        }
        return interimTeams;
    }

    private sealed record PartyAllocation(Dictionary<BattleEntrySession, int> Assignments);

    /// <summary>Select the largest whole-party subset that fits two four-player sides.</summary>
    private PartyAllocation SelectBattleParties(IReadOnlyList<BattleEntrySession> players, string battleId)
    {
        var parties = players
            .GroupBy(player => player.PartyId is { Length: > 0 } partyId
                ? $"party:{partyId}"
                : $"solo:{player.TicketId}", StringComparer.Ordinal)
            .Select(group => group.OrderBy(player => player.CreatedAt)
                .ThenBy(player => player.UserId, StringComparer.Ordinal).ToList())
            .OrderBy(group => group[0].CreatedAt)
            .ThenBy(group => group[0].UserId, StringComparer.Ordinal)
            .ToList();

        var useModeAwareHumanComposition = players.Count > 0
            && !GymEnabled && !TestBotDiscDeckEnabled && _scrambleBotDecks.Count > 0
            && ScrambleBotCompositionSelector.ModeForBattleRuleId(players[0].BattleRuleId) is not null;
        if (useModeAwareHumanComposition)
            return SelectModeAwareBattleParties(parties, battleId, players[0].BattleRuleId);

        // Each party has three states: queued for this match's Blue side, queued for Red, or left for the
        // next match. A room has at most eight entries, so this checks at most 3^8 combinations.
        var optionCount = (int)Math.Pow(3, parties.Count);
        var bestOption = -1;
        var bestPlayers = 0;
        var bestPriority = -1L;
        var bestDifference = int.MaxValue;
        var bestBlueCount = -1;
        for (var option = 0; option < optionCount; option++)
        {
            var state = option;
            var selectedPlayers = 0;
            var priority = 0L;
            var blueCount = 0;
            var redCount = 0;
            for (var i = 0; i < parties.Count; i++)
            {
                var side = state % 3;
                state /= 3;
                if (side == 0) continue;
                selectedPlayers += parties[i].Count;
                priority += 1L << (parties.Count - i - 1);
                if (side == 1) blueCount += parties[i].Count;
                else redCount += parties[i].Count;
                if (blueCount > MaxPerTeam || redCount > MaxPerTeam) break;
            }

            if (blueCount > MaxPerTeam || redCount > MaxPerTeam) continue;
            var difference = Math.Abs(blueCount - redCount);
            if (selectedPlayers > bestPlayers
                || selectedPlayers == bestPlayers && priority > bestPriority
                || selectedPlayers == bestPlayers && priority == bestPriority && difference < bestDifference
                || selectedPlayers == bestPlayers && priority == bestPriority && difference == bestDifference && blueCount > bestBlueCount)
            {
                bestOption = option;
                bestPlayers = selectedPlayers;
                bestPriority = priority;
                bestDifference = difference;
                bestBlueCount = blueCount;
            }
        }

        var assignments = new Dictionary<BattleEntrySession, int>();
        if (bestOption < 0) return new PartyAllocation(assignments);
        var bestState = bestOption;
        for (var i = 0; i < parties.Count; i++)
        {
            var side = bestState % 3;
            bestState /= 3;
            if (side == 0) continue;
            var team = side == 1 ? 0 : 1;
            foreach (var player in parties[i]) assignments[player] = team;
        }
        return new PartyAllocation(assignments);
    }

    private PartyAllocation SelectModeAwareBattleParties(
        IReadOnlyList<List<BattleEntrySession>> parties,
        string battleId,
        int battleRuleId)
    {
        // Preserve the existing admission policy: maximize humans, then retain the oldest whole-party subset.
        // Team color and synergy are chosen only after that subset is fixed.
        var optionCount = (int)Math.Pow(3, parties.Count);
        var bestPlayers = -1;
        var bestPriority = -1L;
        var selectedMask = 0;
        for (var option = 0; option < optionCount; option++)
        {
            var state = option;
            var selectedPlayers = 0;
            var priority = 0L;
            var blueCount = 0;
            var redCount = 0;
            var subset = 0;
            for (var index = 0; index < parties.Count; index++)
            {
                var side = state % 3;
                state /= 3;
                if (side == 0) continue;
                subset |= 1 << index;
                selectedPlayers += parties[index].Count;
                priority += 1L << (parties.Count - index - 1);
                if (side == 1) blueCount += parties[index].Count;
                else redCount += parties[index].Count;
                if (blueCount > MaxPerTeam || redCount > MaxPerTeam) break;
            }
            if (blueCount > MaxPerTeam || redCount > MaxPerTeam) continue;
            if (selectedPlayers > bestPlayers || selectedPlayers == bestPlayers && priority > bestPriority)
            {
                bestPlayers = selectedPlayers;
                bestPriority = priority;
                selectedMask = subset;
            }
        }

        var selectedParties = parties.Where((_, index) => (selectedMask & (1 << index)) != 0).ToArray();
        var scrambleParties = selectedParties
            .Select(party => new ScrambleHumanParty(
                PartyKey(party[0]),
                party.Select(player => new ScrambleHuman(0, player.KickerId)).ToArray()))
            .ToArray();
        var candidates = BotProfiles.Select(profile =>
            new ScrambleBotCandidate(profile.KickerId, profile.RoleType, profile.Archetype)).ToArray();
        var seed = ScrambleBotCompositionSelector.SeedFor(battleId);
        var mode = ScrambleBotCompositionSelector.ModeForBattleRuleId(battleRuleId)!.Value;
        var selection = ScrambleBotCompositionSelector.SelectHumanTeams(scrambleParties, candidates, seed, mode);

        var assignments = new Dictionary<BattleEntrySession, int>();
        foreach (var party in selectedParties)
        {
            if (!selection.PartyTeams.TryGetValue(PartyKey(party[0]), out var team)) continue;
            foreach (var player in party) assignments[player] = team;
        }
        if (assignments.Count == selectedParties.Sum(party => party.Count))
            return new PartyAllocation(assignments);

        // Unknown/invalid source data still produces a legal whole-party allocation.
        var fallbackCounts = new int[2];
        foreach (var party in selectedParties.OrderBy(party => party[0].UserId, StringComparer.Ordinal))
        {
            var team = fallbackCounts[0] <= fallbackCounts[1] ? 0 : 1;
            foreach (var player in party) assignments[player] = team;
            fallbackCounts[team] += party.Count;
        }
        return new PartyAllocation(assignments);
    }

    private static string PartyKey(BattleEntrySession player) =>
        player.PartyId is { Length: > 0 } partyId ? $"party:{partyId}" : $"solo:{player.UserId}";

    /// <summary>Final roster, built only when the room's window closes: the humans plus the bots for the free slots.</summary>
    internal MatchingBattleInfo BuildRoster(
        ActiveBattleRoom room,
        IReadOnlyDictionary<BattleEntrySession, int> humanTeams)
    {
        var selectedPlayers = room.HumanPlayers.Where(humanTeams.ContainsKey).ToList();
        var humanEntries = BuildHumanEntries(selectedPlayers, humanTeams);
        var info = new MatchingBattleInfo
        {
            matchmakingExpirationDatetime = MatchmakingExpiry(room),
            photonCloudRegionId = 1,
            battlePlayerList = [.. humanEntries]
        };

        var usedKickers = new HashSet<int>(selectedPlayers.Select(player => player.KickerId));
        int team0Count = humanEntries.Count(entry => entry.teamType == 0);
        int team1Count = humanEntries.Count - team0Count;

        // Supported non-gym modes use a deterministic, catalog-backed near-best team composition. Gym matches,
        // unknown battle rules and the explicit local test-deck override retain the established roster path below.
        var availableBots = BotProfiles.Where(b => !usedKickers.Contains(b.KickerId)).ToList();
        var botIdx = 0;

        var gym = GymEnabled;
        var totalPlayers = gym ? selectedPlayers.Count + GymBotCount : MaxPerTeam * 2;

        var useModeAwareComposition = !gym && !TestBotDiscDeckEnabled && _scrambleBotDecks.Count > 0
            && ScrambleBotCompositionSelector.ModeForBattleRuleId(room.BattleRuleId) is not null;
        if (!useModeAwareComposition && ExperimentalAiRoleService.IsEnabled && !gym && !TestBotDiscDeckEnabled
            && ScrambleBotCompositionSelector.ModeForBattleRuleId(room.BattleRuleId) is not null && _scrambleBotDecks.Count == 0)
        {
            _logger.LogWarning(
                "Battle {BattleId}: Experimental AI roles enabled ({Flag}=1) but Scramble bot deck catalog is unavailable (count=0); falling back to legacy bot parameter IDs (experimental roles inactive)",
                room.BattleId, ExperimentalAiRoleService.EnvFlagName);
        }
        if (useModeAwareComposition)
        {
            var mode = ScrambleBotCompositionSelector.ModeForBattleRuleId(room.BattleRuleId)!.Value;
            var humans = humanEntries.Select(entry => new ScrambleHuman(entry.teamType, entry.kickerId)).ToArray();
            var candidates = BotProfiles.Select(profile =>
                new ScrambleBotCandidate(profile.KickerId, profile.RoleType, profile.Archetype)).ToArray();
            var assignments = ScrambleBotCompositionSelector.Select(humans, candidates, _scrambleBotDecks,
                MaxPerTeam - team0Count, MaxPerTeam - team1Count,
                ScrambleBotCompositionSelector.SeedFor(room.BattleId), mode);

            if (assignments.Count == (MaxPerTeam - team0Count) + (MaxPerTeam - team1Count))
            {
                foreach (var assignment in assignments)
                {
                    var bot = BotProfiles.First(profile => profile.KickerId == assignment.KickerId);
                    info.battlePlayerList.Add(new MatchingPlayerBattleInfo
                    {
                        userId = $"bot-{1000 + info.battlePlayerList.Count}",
                        matchmakingTeamId = $"team-{(assignment.TeamType == 0 ? 1 : 2)}",
                        battleEntryId = $"be-bot-{info.battlePlayerList.Count}",
                        name = bot.Name,
                        rank = 13,
                        kickerId = bot.KickerId,
                        kickerCostumeId = bot.CostumeRowId,
                        honorId = 6010000,
                        teamType = assignment.TeamType,
                        kickerAiParameterId = ExperimentalAiRoleService.ResolveParameterId(
                            assignment.Profile,
                            bot.KickerId,
                            mode,
                            isGym: false),
                        kickerAiDiscDeckId = assignment.DeckId,
                        languageCode = "es",
                        frameId = 1,
                        discId1 = assignment.DiscIds[0], discLevel1 = 10,
                        discId2 = assignment.DiscIds[1], discLevel2 = 10,
                        discId3 = assignment.DiscIds[2], discLevel3 = 10,
                        discId4 = assignment.DiscIds[3], discLevel4 = 10
                    });
                }
                _logger.LogInformation(
                    "{Mode} composition selected for {BattleId}: {Bots} bots; team0=[{Blue}] team1=[{Red}]",
                    mode, room.BattleId, assignments.Count,
                    string.Join(',', assignments.Where(a => a.TeamType == 0).Select(a => $"{a.KickerId}:{a.Profile}:{a.DeckId}")),
                    string.Join(',', assignments.Where(a => a.TeamType == 1).Select(a => $"{a.KickerId}:{a.Profile}:{a.DeckId}")));
            }
        }

        while (info.battlePlayerList.Count < totalPlayers)
        {
            int team = gym ? 1 : (team0Count < MaxPerTeam) ? 0 : 1;
            if (team == 0) team0Count++; else team1Count++;

            var profile = (botIdx < availableBots.Count) ? availableBots[botIdx++] : new BotProfile(botIdx + 1, $"Bot {botIdx + 1}", 2010101, -1, "unknown");
            var botDiscs = TestBotDiscDeckEnabled
                ? TestBotDiscIds
                : [3010001, 3010002, 3010003, 3010004];

            info.battlePlayerList.Add(new MatchingPlayerBattleInfo
            {
                userId = $"bot-{1000 + info.battlePlayerList.Count}",
                matchmakingTeamId = $"team-{(team == 0 ? 1 : 2)}",
                battleEntryId = $"be-bot-{info.battlePlayerList.Count}",
                name = gym ? $"{profile.Name} (gym)" : profile.Name,
                // Must be >= the `rank` of the kicker's rows in masters_kicker_ai_parameter.json (all 13):
                // PlayerCharacter.GetKickerAIParameterMaster picks the closest row with row.rank <= player rank,
                // so a lower rank leaves the bot without AI parameters and it never acts.
                rank = 13,
                kickerId = profile.KickerId,
                kickerCostumeId = profile.CostumeRowId,
                honorId = 6010000,
                teamType = team,
                // KickerAiParameterMaster row *id* (PlayerCharacter.GetKickerAIParameterMaster = get_Item(_kickerAiParameterId))
                kickerAiParameterId = gym ? GymAiParameterBase + profile.KickerId : profile.KickerId,
                kickerAiDiscDeckId = TestBotDiscDeckEnabled ? 3 : 1, // Test deck 3 exists only for local balance runs.
                languageCode = "es",
                frameId = 1,
                discId1 = botDiscs[0], discLevel1 = 10,
                discId2 = botDiscs[1], discLevel2 = 10,
                discId3 = botDiscs[2], discLevel3 = 10,
                discId4 = botDiscs[3], discLevel4 = 10
            });
        }

        if (TestBotDiscDeckEnabled)
        {
            for (var index = 0; index < info.battlePlayerList.Count; index++)
            {
                var player = info.battlePlayerList[index];
                _logger.LogInformation(
                    "KF_TEST_BOT_ROSTER battle={BattleId} rosterIndex={RosterIndex} user={UserId} isBot={IsBot} team={Team} kicker={KickerId} costumeRow={CostumeRowId} aiParameter={AiParameterId} aiDeck={AiDeckId} discIds=[{Disc1},{Disc2},{Disc3},{Disc4}] levels=[{Level1},{Level2},{Level3},{Level4}]",
                    room.BattleId, index, player.userId, player.kickerAiParameterId > 0, player.teamType, player.kickerId,
                    player.kickerCostumeId, player.kickerAiParameterId, player.kickerAiDiscDeckId,
                    player.discId1, player.discId2, player.discId3, player.discId4,
                    player.discLevel1, player.discLevel2, player.discLevel3, player.discLevel4);
            }
        }

        return info;
    }
}

public sealed class OpenMatchFrontendService : Frontend.FrontendBase
{
    private readonly BattleMatchmakingService _matchmaking;
    private readonly MaintenanceState? _maintenance;
    private readonly ILogger<OpenMatchFrontendService> _logger;

    public OpenMatchFrontendService(BattleMatchmakingService matchmaking, ILogger<OpenMatchFrontendService> logger,
        MaintenanceState? maintenance = null)
    {
        _matchmaking = matchmaking;
        _logger = logger;
        _maintenance = maintenance;
    }

    public override async Task GetAssignments(
        GetAssignmentsRequest request,
        IServerStreamWriter<GetAssignmentsResponse> responseStream,
        ServerCallContext context)
    {
        _logger.LogInformation("gRPC GetAssignments invoked for ticket {TicketId}", request.TicketId);

        // The ticket's registering /battle/entry request is the authoritative DIAG signal, but the client may also
        // repeat x-app-application-version as gRPC metadata; honour it when the ticket is one this process does not
        // know (a demo ticket).
        var version = context.RequestHeaders
            .FirstOrDefault(entry => string.Equals(entry.Key, "x-app-application-version", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        var isDiag = _maintenance?.IsBypassClient(version) == true;

        try
        {
            await _matchmaking.StreamAssignmentsAsync(request.TicketId, responseStream, context.CancellationToken, isDiag);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("GetAssignments stream cancelled for ticket {TicketId}", request.TicketId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetAssignments for ticket {TicketId}", request.TicketId);
        }
    }
}
