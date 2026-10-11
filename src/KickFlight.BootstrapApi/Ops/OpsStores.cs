using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KickFlight.BootstrapApi.PlayerStore;
using Npgsql;
using NpgsqlTypes;

namespace KickFlight.BootstrapApi.Ops;

/// <summary>One writer flush. Battles and participants are whole-row upserts (the latest snapshot wins).</summary>
public sealed class OpsWriteBatch
{
    public List<ServerRunRow> Runs { get; } = [];
    public List<BattleRow> Battles { get; } = [];
    public List<ParticipantRow> Participants { get; } = [];
    public List<ClientEventRow> Events { get; } = [];

    public int Count => Runs.Count + Battles.Count + Participants.Count + Events.Count;
}

public sealed record OpsEventQuery(DateTime Since, string? BattleId = null, IReadOnlyCollection<string>? Kinds = null, int Limit = 5000);

/// <summary>
/// Where ops telemetry lands: PostgreSQL in the deployment (same database and connection string as the player
/// store), daily NDJSON files otherwise. Only the background writer and the admin queries call it, never a request.
/// </summary>
public interface IOpsStore
{
    string Kind { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
    Task WriteAsync(OpsWriteBatch batch, CancellationToken cancellationToken);
    Task<IReadOnlyList<ServerRunRow>> LoadRunsAsync(DateTime since, CancellationToken cancellationToken);
    Task<IReadOnlyList<BattleRow>> LoadBattlesAsync(DateTime since, CancellationToken cancellationToken);
    Task<IReadOnlyList<ParticipantRow>> LoadParticipantsAsync(DateTime since, CancellationToken cancellationToken);
    Task<BattleRow?> LoadBattleAsync(string battleId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ParticipantRow>> LoadBattleParticipantsAsync(string battleId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ClientEventRow>> LoadEventsAsync(OpsEventQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, int>> CountEventsByKindAsync(DateTime since, CancellationToken cancellationToken);
    /// <summary>Deletes everything older than the cutoff (30 days by default).</summary>
    Task PruneAsync(DateTime before, CancellationToken cancellationToken);
}

public static class OpsStoreFactory
{
    public static IOpsStore Create(IConfiguration configuration, string contentRoot)
    {
        var connectionString = PlayerStoreFactory.ResolveConnectionString(configuration);
        if (!string.IsNullOrWhiteSpace(connectionString)) return new PostgresOpsStore(connectionString);

        var directory = configuration["Ops:Directory"];
        directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(contentRoot, "data", "ops")
            : Path.IsPathRooted(directory) ? directory : Path.GetFullPath(Path.Combine(contentRoot, directory));
        return new NdjsonOpsStore(directory);
    }
}

/// <summary>
/// Local/test fallback: one ops-YYYYMMDD.ndjson file per UTC day, each line {"t": run|battle|participant|event,
/// "r": row}. Battles and participants are appended again on every change and the last line per key wins on
/// read. A row's updates always come after its creation, so reading the files from the window's first day on sees
/// every row created inside the window in its latest state.
/// </summary>
public sealed class NdjsonOpsStore : IOpsStore
{
    private readonly object _fileLock = new();
    private long _eventSequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

    public NdjsonOpsStore(string directory) => Directory = Path.GetFullPath(directory);

    public string Directory { get; }
    public string Kind => "ndjson";

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(Directory);
        return Task.CompletedTask;
    }

    public Task WriteAsync(OpsWriteBatch batch, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        foreach (var run in batch.Runs) AppendLine(text, "run", run);
        foreach (var battle in batch.Battles) AppendLine(text, "battle", battle);
        foreach (var participant in batch.Participants) AppendLine(text, "participant", participant);
        foreach (var row in batch.Events)
            AppendLine(text, "event", row with { Id = Interlocked.Increment(ref _eventSequence) });
        if (text.Length == 0) return Task.CompletedTask;

        lock (_fileLock)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.AppendAllText(Path.Combine(Directory, $"ops-{DateTime.UtcNow:yyyyMMdd}.ndjson"), text.ToString(), Encoding.UTF8);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ServerRunRow>> LoadRunsAsync(DateTime since, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ServerRunRow>>(Read(since).Runs.Values
            .Where(run => run.StartedAt >= since).OrderBy(run => run.StartedAt).ToList());

    public Task<IReadOnlyList<BattleRow>> LoadBattlesAsync(DateTime since, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BattleRow>>(Read(since).Battles.Values
            .Where(battle => battle.CreatedAt >= since).OrderBy(battle => battle.CreatedAt).ToList());

    public Task<IReadOnlyList<ParticipantRow>> LoadParticipantsAsync(DateTime since, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ParticipantRow>>(Read(since).Participants.Values
            .Where(participant => participant.CreatedAt >= since).OrderBy(participant => participant.CreatedAt).ToList());

    public Task<BattleRow?> LoadBattleAsync(string battleId, CancellationToken cancellationToken) =>
        Task.FromResult(Read(DateTime.MinValue).Battles.GetValueOrDefault(battleId));

    public Task<IReadOnlyList<ParticipantRow>> LoadBattleParticipantsAsync(string battleId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ParticipantRow>>(Read(DateTime.MinValue).Participants.Values
            .Where(participant => participant.BattleId == battleId).OrderBy(participant => participant.CreatedAt).ToList());

    public Task<IReadOnlyList<ClientEventRow>> LoadEventsAsync(OpsEventQuery query, CancellationToken cancellationToken)
    {
        var since = query.BattleId is null ? query.Since : DateTime.MinValue;
        IEnumerable<ClientEventRow> events = Read(since).Events.Where(row => row.At >= query.Since);
        if (query.BattleId is not null) events = events.Where(row => row.BattleId == query.BattleId);
        if (query.Kinds is { Count: > 0 } kinds) events = events.Where(row => kinds.Contains(row.Kind));
        return Task.FromResult<IReadOnlyList<ClientEventRow>>(events
            .OrderByDescending(row => row.At).Take(query.Limit).OrderBy(row => row.At).ToList());
    }

    public Task<IReadOnlyDictionary<string, int>> CountEventsByKindAsync(DateTime since, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, int>>(Read(since).Events
            .Where(row => row.At >= since)
            .GroupBy(row => row.Kind, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));

    public Task PruneAsync(DateTime before, CancellationToken cancellationToken)
    {
        lock (_fileLock)
        {
            foreach (var (path, day) in Files())
            {
                if (day < before.Date) File.Delete(path);
            }
        }
        return Task.CompletedTask;
    }

    private static void AppendLine<T>(StringBuilder text, string type, T row)
    {
        text.Append("{\"t\":\"").Append(type).Append("\",\"r\":")
            .Append(JsonSerializer.Serialize(row, OpsJson.Rows)).Append("}\n");
    }

    private IEnumerable<(string Path, DateTime Day)> Files()
    {
        if (!System.IO.Directory.Exists(Directory)) yield break;
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "ops-*.ndjson"))
        {
            var stem = Path.GetFileNameWithoutExtension(path)["ops-".Length..];
            if (DateTime.TryParseExact(stem, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day))
                yield return (path, day);
        }
    }

    private sealed class Contents
    {
        public Dictionary<string, ServerRunRow> Runs { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, BattleRow> Battles { get; } = new(StringComparer.Ordinal);
        public Dictionary<(string, string), ParticipantRow> Participants { get; } = [];
        public List<ClientEventRow> Events { get; } = [];
    }

    private Contents Read(DateTime since)
    {
        var contents = new Contents();
        List<string> paths;
        lock (_fileLock)
        {
            paths = Files().Where(file => file.Day >= since.Date).OrderBy(file => file.Day).Select(file => file.Path).ToList();
        }
        foreach (var path in paths)
        {
            string[] lines;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                lines = reader.ReadToEnd().Split('\n');
            }
            catch (IOException)
            {
                continue;
            }
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var row = root.GetProperty("r");
                    switch (root.GetProperty("t").GetString())
                    {
                        case "run":
                            var run = row.Deserialize<ServerRunRow>(OpsJson.Rows)!;
                            contents.Runs[run.Id] = run;
                            break;
                        case "battle":
                            var battle = row.Deserialize<BattleRow>(OpsJson.Rows)!;
                            contents.Battles[battle.BattleId] = battle;
                            break;
                        case "participant":
                            var participant = row.Deserialize<ParticipantRow>(OpsJson.Rows)!;
                            contents.Participants[(participant.BattleId, participant.UserId)] = participant;
                            break;
                        case "event":
                            contents.Events.Add(row.Deserialize<ClientEventRow>(OpsJson.Rows)!);
                            break;
                    }
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    // A torn last line (crash mid-append) or a foreign line: skip it, keep the rest.
                }
            }
        }
        return contents;
    }
}

/// <summary>
/// The deployment's store. Its schema is versioned in its own ops_schema_meta table rather than as migration 3 of
/// the player store: PostgresPlayerStore refuses to start when the database is at a version it does not know, so
/// sharing that counter would make rolling the API back past this feature impossible without a manual downgrade.
/// The same advisory-lock pattern keeps two starting containers from racing.
/// </summary>
public sealed class PostgresOpsStore : IOpsStore
{
    private const long MigrationLockKey = 48095839;

    private static readonly (int Version, string Sql)[] Migrations =
    [
        (1, """
            create table server_runs (
                id                 text primary key,
                started_at         timestamptz not null,
                release_sha        text not null,
                master_version     text not null,
                min_client_version text not null
            );
            create index server_runs_release_idx on server_runs (release_sha, started_at);

            create table battles (
                battle_id           text primary key,
                run_id              text not null,
                created_at          timestamptz not null,
                updated_at          timestamptz not null,
                rule_id             integer not null,
                match_type          integer,
                field_id            integer not null,
                humans              integer not null,
                bots                integer not null,
                custom              boolean not null,
                first_start_at      timestamptz,
                battle_time_seconds integer not null,
                outcome             text
            );
            create index battles_created_idx on battles (created_at desc);
            create index battles_outcome_created_idx on battles (outcome, created_at desc);

            create table battle_participants (
                battle_id              text not null,
                user_id                text not null,
                battle_entry_id        text not null,
                created_at             timestamptz not null,
                updated_at             timestamptz not null,
                kicker_id              integer not null,
                costume_id             integer not null,
                team                   integer not null,
                is_diag                boolean not null,
                app_version            text not null,
                device_name            text not null,
                os_version             text not null,
                ip                     text not null,
                start_at               timestamptz,
                end_at                 timestamptz,
                result_at              timestamptz,
                reconnect_begins       integer not null,
                reconnect_failed_cause text,
                disconnect_cause       text,
                exception_count        integer not null,
                status                 text not null,
                primary key (battle_id, user_id)
            );
            create index battle_participants_created_idx on battle_participants (created_at desc);
            create index battle_participants_user_idx on battle_participants (user_id, created_at desc);
            create index battle_participants_entry_idx on battle_participants (battle_entry_id);

            create table client_events (
                id          bigint generated always as identity primary key,
                at          timestamptz not null,
                run_id      text not null,
                user_id     text,
                battle_id   text,
                kind        text not null,
                app_version text not null,
                device      text not null,
                os          text not null,
                fingerprint text,
                payload     jsonb not null
            );
            create index client_events_at_idx on client_events (at desc);
            create index client_events_kind_at_idx on client_events (kind, at desc);
            create index client_events_battle_idx on client_events (battle_id, at) where battle_id is not null;
            create index client_events_fingerprint_idx on client_events (fingerprint, at desc) where fingerprint is not null;
            """)
    ];

    private readonly NpgsqlDataSource _dataSource;

    public PostgresOpsStore(string connectionString) => _dataSource = NpgsqlDataSource.Create(connectionString);

    public string Kind => "postgres";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await ExecuteAsync(connection, null, """
            create table if not exists ops_schema_meta (
                version    integer primary key,
                applied_at timestamptz not null default now()
            )
            """, cancellationToken);
        await ExecuteAsync(connection, null, $"select pg_advisory_lock({MigrationLockKey})", cancellationToken);
        try
        {
            foreach (var (version, sql) in Migrations)
            {
                await using (var check = new NpgsqlCommand("select 1 from ops_schema_meta where version = $1", connection))
                {
                    check.Parameters.AddWithValue(version);
                    if (await check.ExecuteScalarAsync(cancellationToken) is not null) continue;
                }
                // DDL and its version row commit together, as in PostgresPlayerStore.
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await ExecuteAsync(connection, transaction, sql, cancellationToken);
                await using (var record = new NpgsqlCommand("insert into ops_schema_meta (version) values ($1)", connection, transaction))
                {
                    record.Parameters.AddWithValue(version);
                    await record.ExecuteNonQueryAsync(cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            await ExecuteAsync(connection, null, $"select pg_advisory_unlock({MigrationLockKey})", CancellationToken.None);
        }
    }

    public async Task WriteAsync(OpsWriteBatch batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var commands = new NpgsqlBatch(connection, transaction);
        foreach (var run in batch.Runs)
        {
            commands.BatchCommands.Add(Command("""
                insert into server_runs (id, started_at, release_sha, master_version, min_client_version)
                values ($1, $2, $3, $4, $5) on conflict (id) do nothing
                """, run.Id, run.StartedAt, run.ReleaseSha, run.MasterVersion, run.MinClientVersion));
        }
        foreach (var b in batch.Battles)
        {
            commands.BatchCommands.Add(Command("""
                insert into battles (battle_id, run_id, created_at, updated_at, rule_id, match_type, field_id, humans,
                                     bots, custom, first_start_at, battle_time_seconds, outcome)
                values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
                on conflict (battle_id) do update set
                    updated_at = excluded.updated_at, rule_id = excluded.rule_id, match_type = excluded.match_type,
                    field_id = excluded.field_id, humans = excluded.humans, bots = excluded.bots,
                    custom = excluded.custom, first_start_at = excluded.first_start_at,
                    battle_time_seconds = excluded.battle_time_seconds, outcome = excluded.outcome
                """, b.BattleId, b.RunId, b.CreatedAt, b.UpdatedAt, b.RuleId, b.MatchType, b.FieldId, b.Humans,
                b.Bots, b.Custom, b.FirstStartAt, b.BattleTimeSeconds, b.Outcome));
        }
        foreach (var p in batch.Participants)
        {
            commands.BatchCommands.Add(Command("""
                insert into battle_participants (battle_id, user_id, battle_entry_id, created_at, updated_at, kicker_id,
                    costume_id, team, is_diag, app_version, device_name, os_version, ip, start_at, end_at, result_at,
                    reconnect_begins, reconnect_failed_cause, disconnect_cause, exception_count, status)
                values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21)
                on conflict (battle_id, user_id) do update set
                    battle_entry_id = excluded.battle_entry_id, updated_at = excluded.updated_at,
                    kicker_id = excluded.kicker_id, costume_id = excluded.costume_id, team = excluded.team,
                    is_diag = excluded.is_diag, app_version = excluded.app_version, device_name = excluded.device_name,
                    os_version = excluded.os_version, ip = excluded.ip, start_at = excluded.start_at,
                    end_at = excluded.end_at, result_at = excluded.result_at,
                    reconnect_begins = excluded.reconnect_begins, reconnect_failed_cause = excluded.reconnect_failed_cause,
                    disconnect_cause = excluded.disconnect_cause, exception_count = excluded.exception_count,
                    status = excluded.status
                """, p.BattleId, p.UserId, p.BattleEntryId, p.CreatedAt, p.UpdatedAt, p.KickerId, p.CostumeId, p.Team,
                p.IsDiag, p.AppVersion, p.DeviceName, p.OsVersion, p.Ip, p.StartAt, p.EndAt, p.ResultAt,
                p.ReconnectBegins, p.ReconnectFailedCause, p.DisconnectCause, p.ExceptionCount, p.Status));
        }
        foreach (var e in batch.Events)
        {
            var command = Command("""
                insert into client_events (at, run_id, user_id, battle_id, kind, app_version, device, os, fingerprint, payload)
                values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
                """, e.At, e.RunId, e.UserId, e.BattleId, e.Kind, e.AppVersion, e.Device, e.Os, e.Fingerprint);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = e.Payload.ToJsonString() });
            commands.BatchCommands.Add(command);
        }
        await commands.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ServerRunRow>> LoadRunsAsync(DateTime since, CancellationToken cancellationToken) =>
        await QueryAsync("select id, started_at, release_sha, master_version, min_client_version from server_runs " +
                         "where started_at >= $1 order by started_at", [since], ReadRun, cancellationToken);

    public async Task<IReadOnlyList<BattleRow>> LoadBattlesAsync(DateTime since, CancellationToken cancellationToken) =>
        await QueryAsync($"select {BattleColumns} from battles where created_at >= $1 order by created_at",
            [since], ReadBattle, cancellationToken);

    public async Task<IReadOnlyList<ParticipantRow>> LoadParticipantsAsync(DateTime since, CancellationToken cancellationToken) =>
        await QueryAsync($"select {ParticipantColumns} from battle_participants where created_at >= $1 order by created_at",
            [since], ReadParticipant, cancellationToken);

    public async Task<BattleRow?> LoadBattleAsync(string battleId, CancellationToken cancellationToken) =>
        (await QueryAsync($"select {BattleColumns} from battles where battle_id = $1", [battleId], ReadBattle, cancellationToken))
            .FirstOrDefault();

    public async Task<IReadOnlyList<ParticipantRow>> LoadBattleParticipantsAsync(string battleId, CancellationToken cancellationToken) =>
        await QueryAsync($"select {ParticipantColumns} from battle_participants where battle_id = $1 order by created_at",
            [battleId], ReadParticipant, cancellationToken);

    public async Task<IReadOnlyList<ClientEventRow>> LoadEventsAsync(OpsEventQuery query, CancellationToken cancellationToken)
    {
        var sql = new StringBuilder($"select {EventColumns} from client_events where at >= $1");
        var parameters = new List<object?> { query.Since };
        if (query.BattleId is not null)
        {
            parameters.Add(query.BattleId);
            sql.Append(" and battle_id = $").Append(parameters.Count);
        }
        if (query.Kinds is { Count: > 0 } kinds)
        {
            parameters.Add(kinds.ToArray());
            sql.Append(" and kind = any($").Append(parameters.Count).Append(')');
        }
        parameters.Add(query.Limit);
        sql.Append(" order by at desc limit $").Append(parameters.Count);
        var rows = await QueryAsync(sql.ToString(), parameters.ToArray(), ReadEvent, cancellationToken);
        return rows.OrderBy(row => row.At).ToList();
    }

    public async Task<IReadOnlyDictionary<string, int>> CountEventsByKindAsync(DateTime since, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync("select kind, count(*) from client_events where at >= $1 group by kind", [since],
            reader => (reader.GetString(0), (int)reader.GetInt64(1)), cancellationToken);
        return rows.ToDictionary(row => row.Item1, row => row.Item2, StringComparer.Ordinal);
    }

    public async Task PruneAsync(DateTime before, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        foreach (var sql in new[]
                 {
                     "delete from client_events where at < $1",
                     "delete from battle_participants where created_at < $1",
                     "delete from battles where created_at < $1",
                     "delete from server_runs where started_at < $1"
                 })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue(before);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private const string BattleColumns =
        "battle_id, run_id, created_at, updated_at, rule_id, match_type, field_id, humans, bots, custom, first_start_at, " +
        "battle_time_seconds, outcome";
    private const string ParticipantColumns =
        "battle_id, user_id, battle_entry_id, created_at, updated_at, kicker_id, costume_id, team, is_diag, app_version, " +
        "device_name, os_version, ip, start_at, end_at, result_at, reconnect_begins, reconnect_failed_cause, " +
        "disconnect_cause, exception_count, status";
    private const string EventColumns =
        "id, at, run_id, user_id, battle_id, kind, app_version, device, os, fingerprint, payload::text";

    private static ServerRunRow ReadRun(NpgsqlDataReader r) => new()
    {
        Id = r.GetString(0), StartedAt = Utc(r.GetDateTime(1)), ReleaseSha = r.GetString(2),
        MasterVersion = r.GetString(3), MinClientVersion = r.GetString(4)
    };

    private static BattleRow ReadBattle(NpgsqlDataReader r) => new()
    {
        BattleId = r.GetString(0), RunId = r.GetString(1), CreatedAt = Utc(r.GetDateTime(2)), UpdatedAt = Utc(r.GetDateTime(3)),
        RuleId = r.GetInt32(4), MatchType = r.IsDBNull(5) ? null : r.GetInt32(5), FieldId = r.GetInt32(6),
        Humans = r.GetInt32(7), Bots = r.GetInt32(8), Custom = r.GetBoolean(9), FirstStartAt = NullableUtc(r, 10),
        BattleTimeSeconds = r.GetInt32(11), Outcome = r.IsDBNull(12) ? null : r.GetString(12)
    };

    private static ParticipantRow ReadParticipant(NpgsqlDataReader r) => new()
    {
        BattleId = r.GetString(0), UserId = r.GetString(1), BattleEntryId = r.GetString(2),
        CreatedAt = Utc(r.GetDateTime(3)), UpdatedAt = Utc(r.GetDateTime(4)), KickerId = r.GetInt32(5),
        CostumeId = r.GetInt32(6), Team = r.GetInt32(7), IsDiag = r.GetBoolean(8), AppVersion = r.GetString(9),
        DeviceName = r.GetString(10), OsVersion = r.GetString(11), Ip = r.GetString(12), StartAt = NullableUtc(r, 13),
        EndAt = NullableUtc(r, 14), ResultAt = NullableUtc(r, 15), ReconnectBegins = r.GetInt32(16),
        ReconnectFailedCause = r.IsDBNull(17) ? null : r.GetString(17), DisconnectCause = r.IsDBNull(18) ? null : r.GetString(18),
        ExceptionCount = r.GetInt32(19), Status = r.GetString(20)
    };

    private static ClientEventRow ReadEvent(NpgsqlDataReader r) => new()
    {
        Id = r.GetInt64(0), At = Utc(r.GetDateTime(1)), RunId = r.GetString(2), UserId = r.IsDBNull(3) ? null : r.GetString(3),
        BattleId = r.IsDBNull(4) ? null : r.GetString(4), Kind = r.GetString(5), AppVersion = r.GetString(6),
        Device = r.GetString(7), Os = r.GetString(8), Fingerprint = r.IsDBNull(9) ? null : r.GetString(9),
        Payload = JsonNode.Parse(r.GetString(10)) as JsonObject ?? new JsonObject()
    };

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? NullableUtc(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Utc(reader.GetDateTime(ordinal));

    private static NpgsqlBatchCommand Command(string sql, params object?[] values)
    {
        var command = new NpgsqlBatchCommand(sql);
        foreach (var value in values) command.Parameters.Add(Parameter(value));
        return command;
    }

    private static NpgsqlParameter Parameter(object? value) => value switch
    {
        null => new NpgsqlParameter { Value = DBNull.Value },
        DateTime time => new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = DateTime.SpecifyKind(time, DateTimeKind.Utc) },
        _ => new NpgsqlParameter { Value = value }
    };

    private async Task<List<T>> QueryAsync<T>(string sql, object?[] parameters, Func<NpgsqlDataReader, T> read,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in parameters) command.Parameters.Add(Parameter(value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken)) rows.Add(read(reader));
        return rows;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
