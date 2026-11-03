using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace KickFlight.BootstrapApi.PlayerStore;

// Player state in PostgreSQL, which is what the deployment uses.
//
// Shape, and why it is not fully normalised: the two things a service actually queries - identity and the
// display name - are real columns, and the rest of the state is one `state jsonb` document. Currencies moved to
// otomo's economy (migration 5) and battle points to otomo's session (migration 7). Owned discs and the badge facts are only ever read and
// written as a whole (the API loads a player, mutates, saves), so normalising them would buy nothing and cost
// a mapping layer plus the chance of a half-applied save. See deploy/README.md.
//
// The identity column is otomo's player id, not a device and not a password: this schema holds no account of its
// own and no session at all (migration 4 dropped the sessions table). `id` is only the numeric handle the KF
// client's protocol requires.
//
// Migrations are embedded rather than run by hand so a fresh database reaches the current schema by starting
// the API once. `schema_meta` records which have been applied, so applying is idempotent and additive.
public sealed class PostgresPlayerStore : IPlayerStore
{
    private const int SchemaVersion = 8;

    private static readonly (int Version, string Sql)[] Migrations =
    [
        (1, """
            create table players (
                id                  bigint primary key,
                uuid                text not null unique,
                display_name        text,
                kicker_id           integer not null,
                kicker_costume_id   integer not null,
                active_deck_number  integer not null,
                item_jet_coins      integer not null,
                item_paid_jet_coins integer not null,
                item_disc_force     integer not null,
                item_kick_points    integer not null,
                state               jsonb   not null,
                created_at          timestamptz not null default now(),
                updated_at          timestamptz not null default now()
            );

            -- Player ids stay numeric and dense from 1000001: the client parses the id with long.TryParse and
            -- renders "Player NNNN" from the low digits, and the previous in-memory allocator started at
            -- 1000002 for the second device on the strength of that.
            create sequence player_id_seq as bigint start with 1000001;

            create table sessions (
                access_token text primary key,
                player_id    bigint not null references players(id) on delete cascade,
                session_key  bytea  not null,
                created_at   timestamptz not null default now()
            );

            create index sessions_player_id_idx on sessions (player_id);

            create table player_ranks (
                player_id       bigint  not null references players(id) on delete cascade,
                battle_rule_type integer not null,
                battle_point    integer not null,
                rank            integer not null,
                updated_at      timestamptz not null default now(),
                primary key (player_id, battle_rule_type)
            );
            """),
        (2, """
            create table player_follows (
                follower_id bigint not null references players(id) on delete cascade,
                followed_id bigint not null references players(id) on delete cascade,
                created_at  timestamptz not null default now(),
                read_at     timestamptz,
                primary key (follower_id, followed_id),
                check (follower_id <> followed_id)
            );

            create index player_follows_followed_created_idx
                on player_follows (followed_id, created_at desc, follower_id);
            create index player_follows_follower_created_idx
                on player_follows (follower_id, created_at desc, followed_id);

            create table real_friend_tokens (
                player_id bigint primary key references players(id) on delete cascade,
                token     text not null unique,
                created_at timestamptz not null default now()
            );

            create table player_real_friends (
                player_low_id  bigint not null references players(id) on delete cascade,
                player_high_id bigint not null references players(id) on delete cascade,
                created_at     timestamptz not null default now(),
                primary key (player_low_id, player_high_id),
                check (player_low_id < player_high_id)
            );

            create index player_real_friends_high_idx on player_real_friends (player_high_id, player_low_id);
            """),
        // Badge facts leave the whole-document jsonb (last writer wins) for rows that are updated with
        // `value = value + delta` / insert-on-conflict inside one transaction per player. Strictly additive: no
        // existing table or column is touched, and every statement is idempotent. `players.state` keeps a mirror
        // of the badge fields for readers that only know the document, and is the legacy source that is folded
        // into these tables (greatest / union) the first time a player is written by this code.
        (3, """
            create table if not exists player_badge_ledger (
                player_id   bigint not null references players(id) on delete cascade,
                ledger_kind text   not null,
                entry_key   text   not null,
                created_at  timestamptz not null default now(),
                payload     jsonb  not null default '{}'::jsonb,
                primary key (player_id, ledger_kind, entry_key)
            );
            create index if not exists player_badge_ledger_recent_idx
                on player_badge_ledger (player_id, ledger_kind, created_at desc);

            create table if not exists player_badge_counters (
                player_id   bigint not null references players(id) on delete cascade,
                counter_key text   not null,
                value       bigint not null check (value >= 0),
                updated_at  timestamptz not null default now(),
                primary key (player_id, counter_key)
            );

            create table if not exists player_badge_streaks (
                player_id      bigint not null references players(id) on delete cascade,
                streak_key     text   not null,
                current_streak integer not null check (current_streak >= 0),
                best_streak    integer not null check (best_streak >= 0),
                battles        bigint  not null check (battles >= 0),
                updated_at     timestamptz not null default now(),
                primary key (player_id, streak_key)
            );

            create table if not exists player_honors (
                player_id  bigint  not null references players(id) on delete cascade,
                honor_id   integer not null,
                source     text    not null default 'rule',
                awarded_at timestamptz not null default now(),
                primary key (player_id, honor_id)
            );
            """),
        (4, """
            -- Identity moves to otomo: the adapter stops creating accounts and stops storing sessions, and a
            -- player becomes an otomo player id plus game-side state.
            --
            -- uuid was the device this adapter keyed players by. It is kept, renamed for what it now is, because
            -- it is the only thing an existing row can be matched on: otomo's account id is a uuid that otomo
            -- assigns and indexes by an HMAC of the device id under a pepper this process never sees, so the
            -- account id of an existing player cannot be computed here. Those rows are adopted instead, in
            -- ResolveGamePlayer: the first otomo account that presents the device takes the row over, keeping its
            -- name, rank, inventory and friends. A player who never connects again stays unmapped - reported by
            -- the query in docs/OTOMO_IDENTITY.md, never guessed.
            alter table players rename column uuid to legacy_device_uuid;
            alter table players add column otomo_player_id text unique;

            -- The sessions table is dropped rather than left behind: the access token handed to the client is
            -- signed, and the device's refresh family lives in otomo. A table here would only look authoritative.
            drop table sessions;
            """),
        (5, """
            -- The economy moved to otomo: a balance, a ticket and a disc are no longer this table's. The four
            -- counters are dropped rather than left to drift, because a column nothing reads is a column that
            -- will be read again by mistake. A save document written before this version still carries the old
            -- keys; they are ignored on read and gone on the next write.
            alter table players
                drop column if exists item_jet_coins,
                drop column if exists item_paid_jet_coins,
                drop column if exists item_disc_force,
                drop column if exists item_kick_points;
            """),
        (6, """
            -- The social relation moved to otomo's session service: who follows whom, and the friend invitation that
            -- used to be a token here. The three tables are dropped rather than left to drift, because every write to
            -- them is gone and a table nothing writes is a table something reads by mistake.
            --
            -- Nothing is copied anywhere: no live player data migrates, every player starts fresh on otomo, so there
            -- is no backfill and no compatibility path for an old follow or an old friend token.
            drop table if exists player_follows;
            drop table if exists real_friend_tokens;
            drop table if exists player_real_friends;
            """),
        (7, """
            -- Battle points moved to otomo's session service: a player's points, tier and the ladder they sit on are
            -- session's, and the adapter translates units and keeps nothing. The table is dropped rather than left to
            -- drift, because every read and write of it is gone.
            --
            -- Nothing is copied: no live player data migrates, every player starts fresh on otomo, so there is no
            -- backfill of points and no compatibility path for an old save's rank.
            drop table if exists player_ranks;
            """),
        (8, """
            -- The loadout moved to otomo's session service: a player's decks, active deck, costume gears and pending gear
            -- roll are the ext of their session card (ext.session.player), and capsules are economy entitlements. The
            -- active deck column is dropped rather than left to drift, because every read and write of it is gone; the
            -- `state` document keys for decks, gears and capsules simply stop being written, and an old document that still
            -- carries them is ignored on read.
            --
            -- Nothing is copied: no live player data migrates, every player starts fresh on otomo, so there is no
            -- backfill of decks or gears and no compatibility path for an old save's loadout.
            alter table players drop column if exists active_deck_number;
            """)
    ];

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresPlayerStore> _logger;

    public PostgresPlayerStore(ILogger<PostgresPlayerStore> logger, string connectionString)
    {
        _logger = logger;
        _dataSource = NpgsqlDataSource.Create(connectionString);
        Migrate();
    }

    // Serialised across instances because two API containers starting at once would race here; the advisory
    // lock is held for the length of the migration and released with the connection.
    private void Migrate()
    {
        using var connection = _dataSource.OpenConnection();
        using (var createMeta = connection.CreateCommand())
        {
            createMeta.CommandText = """
                create table if not exists schema_meta (
                    version    integer primary key,
                    applied_at timestamptz not null default now()
                )
                """;
            createMeta.ExecuteNonQuery();
        }

        using var advisory = connection.CreateCommand();
        advisory.CommandText = "select pg_advisory_lock(48095837)";
        advisory.ExecuteNonQuery();
        try
        {
            foreach (var (version, sql) in Migrations)
            {
                using var check = connection.CreateCommand();
                check.CommandText = "select 1 from schema_meta where version = $1";
                check.Parameters.AddWithValue(version);
                if (check.ExecuteScalar() is not null) continue;

                _logger.LogInformation("Applying player store migration {Version}", version);
                // DDL and the version row in one transaction: a crash cannot leave the schema half-applied
                // with the version recorded, which would skip the rest forever.
                using var transaction = connection.BeginTransaction();
                using (var apply = connection.CreateCommand())
                {
                    apply.Transaction = transaction;
                    apply.CommandText = sql;
                    apply.ExecuteNonQuery();
                }
                using (var record = connection.CreateCommand())
                {
                    record.Transaction = transaction;
                    record.CommandText = "insert into schema_meta (version) values ($1)";
                    record.Parameters.AddWithValue(version);
                    record.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }
        finally
        {
            using var unlock = connection.CreateCommand();
            unlock.CommandText = "select pg_advisory_unlock(48095837)";
            unlock.ExecuteNonQuery();
        }

        // Fail fast and loudly if the database is ahead of the code: a missing column would otherwise surface
        // as an unrelated exception on the first request.
        using var verify = connection.CreateCommand();
        verify.CommandText = "select coalesce(max(version), 0) from schema_meta";
        var applied = Convert.ToInt32(verify.ExecuteScalar());
        if (applied != SchemaVersion)
        {
            throw new InvalidOperationException(
                $"Player store schema version must be {SchemaVersion} (at {applied}).");
        }
    }

    public long ResolveGamePlayer(string otomoPlayerId, string legacyDeviceUuid)
    {
        if (string.IsNullOrWhiteSpace(otomoPlayerId))
            throw new ArgumentException("An otomo player id is required.", nameof(otomoPlayerId));

        using var connection = _dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();

        // 1. The account already has a row: that is the answer, whichever device it arrives from.
        if (FindPlayerId(connection, transaction, "select id from players where otomo_player_id = $1",
                command => command.Parameters.AddWithValue(otomoPlayerId)) is { } known)
        {
            transaction.Commit();
            return known;
        }

        // 2. A pre-otomo row for this device is adopted by the account presenting it, so an existing player keeps
        //    their name, rank, inventory and friends. Only a row that has no account yet can be adopted, which is
        //    what keeps two accounts from taking over the same row.
        if (!string.IsNullOrEmpty(legacyDeviceUuid) &&
            FindPlayerId(connection, transaction,
                "update players set otomo_player_id = $1 where legacy_device_uuid = $2 and otomo_player_id is null returning id",
                command =>
                {
                    command.Parameters.AddWithValue(otomoPlayerId);
                    command.Parameters.AddWithValue(legacyDeviceUuid);
                }) is { } adopted)
        {
            _logger.LogInformation("otomo player {OtomoPlayerId} adopted the existing row {PlayerId}", otomoPlayerId, adopted);
            transaction.Commit();
            return adopted;
        }

        // 3. A new account: one new row. `on conflict do nothing` covers the two writers that can race here - the
        //    same account on two requests, and a device that two accounts somehow present at once.
        var defaults = new SessionState();
        var inserted = FindPlayerId(connection, transaction, """
            insert into players (
                id, otomo_player_id, legacy_device_uuid, kicker_id, kicker_costume_id, state)
            values (nextval('player_id_seq'), $1, $2, $3, $4, '{}'::jsonb)
            on conflict do nothing
            returning id
            """,
            command =>
            {
                command.Parameters.AddWithValue(otomoPlayerId);
                command.Parameters.AddWithValue(string.IsNullOrEmpty(legacyDeviceUuid) ? DBNull.Value : legacyDeviceUuid);
                command.Parameters.AddWithValue(defaults.KickerId);
                command.Parameters.AddWithValue(defaults.KickerCostumeId);
            });
        if (inserted is { } created)
        {
            _logger.LogInformation("otomo player {OtomoPlayerId} assigned game row {PlayerId}", otomoPlayerId, created);
            transaction.Commit();
            return created;
        }

        // 4. Another request for this account won the insert: read the row it made.
        if (FindPlayerId(connection, transaction, "select id from players where otomo_player_id = $1",
                command => command.Parameters.AddWithValue(otomoPlayerId)) is { } raced)
        {
            transaction.Commit();
            return raced;
        }

        transaction.Rollback();
        throw new InvalidOperationException(
            $"No game row for otomo player {otomoPlayerId}, and one could not be created: its legacy device is already " +
            "attached to another account.");
    }

    // The device's player, created on first contact: main's identity path, kept for a server whose identity
    // provider is not configured. A row it creates carries no otomo_player_id, so an account that later presents
    // the same device adopts it in ResolveGamePlayer and keeps its name, rank and inventory.
    public long ResolvePlayerId(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
            throw new ArgumentException("A device uuid is required.", nameof(uuid));

        using var connection = _dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();

        if (FindPlayerId(connection, transaction, "select id from players where legacy_device_uuid = $1",
                command => command.Parameters.AddWithValue(uuid)) is { } existing)
        {
            transaction.Commit();
            return existing;
        }

        var defaults = new SessionState();
        var created = FindPlayerId(connection, transaction, """
            insert into players (id, legacy_device_uuid, kicker_id, kicker_costume_id, state)
            values (nextval('player_id_seq'), $1, $2, $3, '{}'::jsonb)
            returning id
            """,
            command =>
            {
                command.Parameters.AddWithValue(uuid);
                command.Parameters.AddWithValue(defaults.KickerId);
                command.Parameters.AddWithValue(defaults.KickerCostumeId);
            });
        transaction.Commit();
        return created ?? throw new InvalidOperationException($"No player row for device {uuid}, and one could not be created.");
    }

    // One statement, one row id: the shared shape of the lookups and the two writes above.
    private static long? FindPlayerId(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        Action<NpgsqlCommand> bind)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        bind(command);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    public SessionState? TryLoad(long playerId)
    {
        using var connection = _dataSource.OpenConnection();
        // One snapshot for the player row and the badge tables, so a concurrent award is seen whole or not at all.
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.RepeatableRead);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            select display_name, kicker_id, kicker_costume_id, state
            from players where id = $1
            """;
        command.Parameters.AddWithValue(playerId);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        // The document carries everything except the identity and the columns that a service queries; those
        // are authoritative in their own columns and are copied over whatever the document said.
        var state = DecodeDocument(reader.GetString(3));
        state.UserId = playerId.ToString();
        state.UserName = reader.IsDBNull(0) ? "" : reader.GetString(0);
        state.KickerId = reader.GetInt32(1);
        state.KickerCostumeId = reader.GetInt32(2);
        reader.Close();

        // The tables are authoritative for badge facts; the document may be older (or newer, if a previous
        // binary wrote it), so the two are folded together without ever lowering a value.
        BadgeTableMapping.ApplyTo(state, ReadBadgeTables(connection, transaction, playerId), replaceLedgers: false);
        return state;
    }

    private SessionState DecodeDocument(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return new SessionState();
        try
        {
            var state = JsonSerializer.Deserialize<SessionState>(json);
            if (state is null) return new SessionState();
            return state;
        }
        catch (JsonException ex)
        {
            // A corrupt document must not take the player's whole account down, but it must be visible.
            _logger.LogError(ex, "Player state document is not readable as JSON; starting from defaults");
            return new SessionState();
        }
    }

    // `uuid` is main's device-first creation hint. Identity here is decided once, in ResolveGamePlayer (the
    // account is otomo's), so a save never moves a row and the argument is ignored.
    public void Save(SessionState state, string? uuid = null)
    {
        if (state.PlayerId == 0)
        {
            _logger.LogWarning("Refusing to save a state whose UserId {UserId} is not a player id", state.UserId);
            return;
        }

        // The document excludes nothing today, but the columns above are the ones services read, so they are
        // written from the state and the document keeps the rest. Neither identity column is written here: the
        // account a row belongs to is decided once, in ResolveGamePlayer, and a later save must not move it.
        using var connection = _dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();

        // Badge facts: this whole-document save may come from a stale session, so it can only ever add to the
        // badge tables (greatest / union), and the document written back is rebuilt from the tables.
        var stored = LockAndReadDocument(connection, transaction, state.PlayerId);
        if (stored is null)
        {
            _logger.LogWarning("Save for player {PlayerId} matched no row", state.PlayerId);
            return;
        }
        MergeIntoTables(connection, transaction, state.PlayerId, BadgeTableMapping.Flatten(stored), "legacy");
        MergeIntoTables(connection, transaction, state.PlayerId, BadgeTableMapping.Flatten(state), "save");
        BadgeTableMapping.ApplyTo(state, ReadBadgeTables(connection, transaction, state.PlayerId), replaceLedgers: true);
        var document = JsonSerializer.Serialize(state);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            update players set
                display_name        = nullif($2, ''),
                kicker_id           = $3,
                kicker_costume_id   = $4,
                state               = $5::jsonb,
                updated_at          = now()
            where id = $1
            """;
        command.Parameters.AddWithValue(state.PlayerId);
        // The empty name is stored as NULL: it is the absence of a name that ends onboarding, and NULL says
        // that unambiguously where '' could also be read as a name someone chose.
        command.Parameters.AddWithValue(state.UserName);
        command.Parameters.AddWithValue(state.KickerId);
        command.Parameters.AddWithValue(state.KickerCostumeId);
        command.Parameters.AddWithValue(document);

        if (command.ExecuteNonQuery() == 0)
        {
            _logger.LogWarning("Save for player {PlayerId} matched no row", state.PlayerId);
        }
        transaction.Commit();
    }

    // The atomic badge path. Everything below runs in ONE transaction that holds the player's row lock
    // (select ... for update), so any two writers of the same player - in this process or another API
    // container - are serialised, and a loser always starts from the winner's committed state.
    public BadgeMutationResult MutateBadges(
        long playerId, SessionState? fallback, BadgeLedgerKey? ledger, Func<SessionState, IReadOnlyList<int>?> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        if (playerId <= 0) return new BadgeMutationResult(false, [], null);

        using var connection = _dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var current = LockAndReadDocument(connection, transaction, playerId);
        if (current is null) return new BadgeMutationResult(false, [], null);
        current.UserId = playerId.ToString();

        // Fold whatever the document holds (legacy data, or writes of a binary that predates the tables) into
        // the tables, then work from the tables.
        MergeIntoTables(connection, transaction, playerId, BadgeTableMapping.Flatten(current), "legacy");
        BadgeTableMapping.ApplyTo(current, ReadBadgeTables(connection, transaction, playerId), replaceLedgers: true);
        var before = BadgeTableMapping.Flatten(current);

        if (ledger is not null)
        {
            // Claim the key. Losing the insert means this exact mutation was already applied: hand back what it
            // awarded and change nothing (no second count).
            using var claim = connection.CreateCommand();
            claim.Transaction = transaction;
            claim.CommandText = """
                insert into player_badge_ledger (player_id, ledger_kind, entry_key, created_at, payload)
                values ($1, $2, $3, to_timestamp($4), '{"honors":[]}'::jsonb)
                on conflict (player_id, ledger_kind, entry_key) do nothing
                """;
            claim.Parameters.AddWithValue(playerId);
            claim.Parameters.AddWithValue(ledger.Kind);
            claim.Parameters.AddWithValue(ledger.Key);
            claim.Parameters.AddWithValue((double)ledger.NowUnix);
            if (claim.ExecuteNonQuery() == 0)
            {
                using var read = connection.CreateCommand();
                read.Transaction = transaction;
                read.CommandText = "select payload::text from player_badge_ledger where player_id = $1 and ledger_kind = $2 and entry_key = $3";
                read.Parameters.AddWithValue(playerId);
                read.Parameters.AddWithValue(ledger.Kind);
                read.Parameters.AddWithValue(ledger.Key);
                var previous = BadgeTableMapping.DecodePayload(read.ExecuteScalar() as string);
                transaction.Commit();
                return new BadgeMutationResult(false, previous, current);
            }
        }

        var awarded = mutate(current);
        if (awarded is null)
        {
            transaction.Rollback(); // also releases the ledger claim
            return new BadgeMutationResult(false, [], current);
        }
        if (ledger is not null) BadgeTableMapping.RecordLedgerAwards(current, ledger, awarded);
        BadgeTableMapping.ApplyTo(current, new BadgeTableSnapshot(), replaceLedgers: false); // normalise

        var after = BadgeTableMapping.Flatten(current);
        var delta = new BadgeTableSnapshot();
        foreach (var (key, value) in after.Counters)
        {
            var increase = value - before.Counters.GetValueOrDefault(key);
            if (increase > 0) delta.Counters[key] = increase;
        }
        foreach (var (mode, streak) in after.Streaks)
        {
            if (!before.Streaks.TryGetValue(mode, out var old)
                || old.Battles != streak.Battles || old.Current != streak.Current || old.Best != streak.Best)
                delta.Streaks[mode] = streak;
        }
        foreach (var honor in after.Honors) if (!before.Honors.Contains(honor)) delta.Honors.Add(honor);
        foreach (var (key, entry) in after.Ledger) if (!before.Ledger.ContainsKey(key)) delta.Ledger[key] = entry;

        UpsertCounters(connection, transaction, playerId, delta.Counters, add: true);
        UpsertStreaks(connection, transaction, playerId, delta.Streaks);
        InsertHonors(connection, transaction, playerId, delta.Honors, "rule");
        InsertLedger(connection, transaction, playerId, delta.Ledger);
        if (ledger is not null)
        {
            using var settle = connection.CreateCommand();
            settle.Transaction = transaction;
            settle.CommandText = "update player_badge_ledger set payload = $4::jsonb where player_id = $1 and ledger_kind = $2 and entry_key = $3";
            settle.Parameters.AddWithValue(playerId);
            settle.Parameters.AddWithValue(ledger.Kind);
            settle.Parameters.AddWithValue(ledger.Key);
            settle.Parameters.AddWithValue(BadgeTableMapping.EncodePayload(awarded));
            settle.ExecuteNonQuery();
        }

        // Keep the document mirror current, from the tables (never from the possibly bounded in-memory view).
        BadgeTableMapping.ApplyTo(current, ReadBadgeTables(connection, transaction, playerId), replaceLedgers: true);
        WriteMirror(connection, transaction, playerId, current);
        transaction.Commit();
        return new BadgeMutationResult(true, awarded, current);
    }

    private SessionState? LockAndReadDocument(NpgsqlConnection connection, NpgsqlTransaction transaction, long playerId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select state::text from players where id = $1 for update";
        command.Parameters.AddWithValue(playerId);
        return command.ExecuteScalar() is string json ? DecodeDocument(json) : null;
    }

    private static void WriteMirror(NpgsqlConnection connection, NpgsqlTransaction transaction, long playerId, SessionState state)
    {
        using var full = JsonSerializer.SerializeToDocument(state);
        var patch = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var name in BadgeTableMapping.MirroredProperties)
            if (full.RootElement.TryGetProperty(name, out var value)) patch[name] = value;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "update players set state = state || $2::jsonb, updated_at = now() where id = $1";
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(patch));
        command.ExecuteNonQuery();
    }

    private static BadgeTableSnapshot ReadBadgeTables(NpgsqlConnection connection, NpgsqlTransaction transaction, long playerId)
    {
        var snapshot = new BadgeTableSnapshot();
        using var batch = new NpgsqlBatch(connection, transaction);
        void Add(string sql)
        {
            var command = new NpgsqlBatchCommand(sql);
            command.Parameters.Add(new NpgsqlParameter { Value = playerId });
            batch.BatchCommands.Add(command);
        }
        Add("select counter_key, value from player_badge_counters where player_id = $1");
        Add("select streak_key, current_streak, best_streak, battles from player_badge_streaks where player_id = $1");
        Add("select honor_id from player_honors where player_id = $1");
        Add($"""
            (select ledger_kind, entry_key, extract(epoch from created_at)::bigint, payload::text from player_badge_ledger
              where player_id = $1 and ledger_kind = '{BadgeLedgerKinds.Battle}' order by created_at desc, entry_key limit {BadgeTableMapping.MirroredLedgerRows})
            union all
            (select ledger_kind, entry_key, extract(epoch from created_at)::bigint, payload::text from player_badge_ledger
              where player_id = $1 and ledger_kind = '{BadgeLedgerKinds.Like}' order by created_at desc, entry_key limit {BadgeTableMapping.MirroredLedgerRows})
            union all
            (select ledger_kind, entry_key, extract(epoch from created_at)::bigint, payload::text from player_badge_ledger
              where player_id = $1 and ledger_kind = '{BadgeLedgerKinds.LoginDay}')
            """);

        using var reader = batch.ExecuteReader();
        while (reader.Read()) snapshot.Counters[reader.GetString(0)] = reader.GetInt64(1);
        reader.NextResult();
        while (reader.Read())
        {
            if (int.TryParse(reader.GetString(0), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var mode))
                snapshot.Streaks[mode] = new WinStreakState
                    { Current = reader.GetInt32(1), Best = reader.GetInt32(2), Battles = reader.GetInt64(3) };
        }
        reader.NextResult();
        while (reader.Read()) snapshot.Honors.Add(reader.GetInt32(0));
        reader.NextResult();
        while (reader.Read())
            snapshot.Ledger[(reader.GetString(0), reader.GetString(1))] =
                (reader.GetInt64(2), BadgeTableMapping.DecodePayload(reader.IsDBNull(3) ? null : reader.GetString(3)));
        return snapshot;
    }

    // Whole-snapshot merge: counters by greatest, streaks by the monotonic battle sequence, honors and ledger
    // rows by insert-if-absent. Idempotent, and incapable of lowering or removing anything.
    private static void MergeIntoTables(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long playerId, BadgeTableSnapshot snapshot, string source)
    {
        UpsertCounters(connection, transaction, playerId, snapshot.Counters, add: false);
        UpsertStreaks(connection, transaction, playerId, snapshot.Streaks);
        InsertHonors(connection, transaction, playerId, snapshot.Honors, source);
        InsertLedger(connection, transaction, playerId, snapshot.Ledger);
    }

    private static void UpsertCounters(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long playerId, Dictionary<string, long> counters, bool add)
    {
        if (counters.Count == 0) return;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            insert into player_badge_counters (player_id, counter_key, value)
            select $1, k, v from unnest($2::text[], $3::bigint[]) as t(k, v)
            on conflict (player_id, counter_key) do update set
                value = {(add ? "player_badge_counters.value + excluded.value" : "excluded.value")},
                updated_at = now()
            {(add ? "" : "where excluded.value > player_badge_counters.value")}
            """;
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(counters.Keys.ToArray());
        command.Parameters.AddWithValue(counters.Values.ToArray());
        command.ExecuteNonQuery();
    }

    private static void UpsertStreaks(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long playerId, Dictionary<int, WinStreakState> streaks)
    {
        if (streaks.Count == 0) return;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // The copy that has seen more battles of the mode holds the live streak; the best is a pure maximum.
        command.CommandText = """
            insert into player_badge_streaks (player_id, streak_key, current_streak, best_streak, battles)
            select $1, k, c, b, n from unnest($2::text[], $3::integer[], $4::integer[], $5::bigint[]) as t(k, c, b, n)
            on conflict (player_id, streak_key) do update set
                current_streak = case when excluded.battles > player_badge_streaks.battles
                                      then excluded.current_streak else player_badge_streaks.current_streak end,
                best_streak = greatest(player_badge_streaks.best_streak, excluded.best_streak),
                battles = greatest(player_badge_streaks.battles, excluded.battles),
                updated_at = now()
            where excluded.battles > player_badge_streaks.battles or excluded.best_streak > player_badge_streaks.best_streak
            """;
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(streaks.Keys.Select(mode => mode.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray());
        command.Parameters.AddWithValue(streaks.Values.Select(streak => streak.Current).ToArray());
        command.Parameters.AddWithValue(streaks.Values.Select(streak => streak.Best).ToArray());
        command.Parameters.AddWithValue(streaks.Values.Select(streak => streak.Battles).ToArray());
        command.ExecuteNonQuery();
    }

    private static void InsertHonors(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long playerId, HashSet<int> honors, string source)
    {
        if (honors.Count == 0) return;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into player_honors (player_id, honor_id, source)
            select $1, h, $3 from unnest($2::integer[]) as t(h)
            on conflict (player_id, honor_id) do nothing
            """;
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(honors.ToArray());
        command.Parameters.AddWithValue(source);
        command.ExecuteNonQuery();
    }

    private static void InsertLedger(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long playerId,
        Dictionary<(string Kind, string Key), (long Unix, List<int> Honors)> ledger)
    {
        if (ledger.Count == 0) return;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into player_badge_ledger (player_id, ledger_kind, entry_key, created_at, payload)
            select $1, kind, entry, case when unix > 0 then to_timestamp(unix) else now() end, body::jsonb
            from unnest($2::text[], $3::text[], $4::bigint[], $5::text[]) as t(kind, entry, unix, body)
            on conflict (player_id, ledger_kind, entry_key) do nothing
            """;
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(ledger.Keys.Select(key => key.Kind).ToArray());
        command.Parameters.AddWithValue(ledger.Keys.Select(key => key.Key).ToArray());
        command.Parameters.AddWithValue(ledger.Values.Select(value => value.Unix).ToArray());
        command.Parameters.AddWithValue(ledger.Values.Select(value => BadgeTableMapping.EncodePayload(value.Honors)).ToArray());
        command.ExecuteNonQuery();
    }

    public bool IsNameTaken(string name, long exceptPlayerId)
    {
        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "select exists (select 1 from players where lower(display_name) = lower($1) and id <> $2)";
        command.Parameters.AddWithValue(name);
        command.Parameters.AddWithValue(exceptPlayerId);
        return Convert.ToBoolean(command.ExecuteScalar());
    }

    public PlayerProfile? FindProfile(long playerId)
    {
        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, coalesce(display_name, ''), kicker_id, kicker_costume_id
            from players where id = $1
            """;
        command.Parameters.AddWithValue(playerId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProfile(reader, 0) : null;
    }

    public IReadOnlyList<PlayerProfile> FindProfiles(IReadOnlyCollection<long> playerIds)
    {
        ArgumentNullException.ThrowIfNull(playerIds);
        if (playerIds.Count == 0) return Array.Empty<PlayerProfile>();

        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, coalesce(display_name, ''), kicker_id, kicker_costume_id
            from players where id = any($1) order by id
            """;
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint,
            Value = playerIds.Distinct().ToArray()
        });

        var profiles = new List<PlayerProfile>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) profiles.Add(ReadProfile(reader, 0));
        return profiles;
    }

    // The otomo account behind each of these accounts: the id mapping and nothing else.
    public IReadOnlyList<OtomoPlayer> FindOtomoPlayers(IReadOnlyCollection<string> otomoPlayerIds)
    {
        ArgumentNullException.ThrowIfNull(otomoPlayerIds);
        if (otomoPlayerIds.Count == 0) return Array.Empty<OtomoPlayer>();

        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = OtomoPlayerQuery + " where otomo_player_id = any($1) order by id";
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
            Value = otomoPlayerIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray()
        });

        var players = new List<OtomoPlayer>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) players.Add(ReadOtomoPlayer(reader));
        return players;
    }

    public OtomoPlayer? FindOtomoPlayer(long playerId)
    {
        if (playerId <= 0) return null;
        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = OtomoPlayerQuery + " where id = $1";
        command.Parameters.AddWithValue(playerId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadOtomoPlayer(reader) : null;
    }

    private static PlayerProfile ReadProfile(NpgsqlDataReader reader, int firstColumn) =>
        new(reader.GetInt64(firstColumn), reader.GetString(firstColumn + 1),
            reader.GetInt32(firstColumn + 2), reader.GetInt32(firstColumn + 3));

    private const string OtomoPlayerQuery = """
        select id, otomo_player_id
        from players
        """;

    private static OtomoPlayer ReadOtomoPlayer(NpgsqlDataReader reader) =>
        new(reader.GetInt64(0), reader.GetString(1));
}
