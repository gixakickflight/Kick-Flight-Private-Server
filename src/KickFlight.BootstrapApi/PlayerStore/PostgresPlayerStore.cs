using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace KickFlight.BootstrapApi.PlayerStore;

// Player state in PostgreSQL, which is what the deployment uses.
//
// Shape, and why it is not fully normalised: the two things a service actually queries - identity and the
// display name - are real columns, and the rest of the state is one `state jsonb` document. Currencies moved to
// otomo's economy (migration 5), battle points to otomo's session (migration 7) and badges to economy facts, tracks and
// items plus a session display slot (migration 9). What is left is read and written as a whole (the API loads a player,
// mutates, saves), so normalising it would buy nothing and cost a mapping layer plus the chance of a half-applied
// save. See deploy/README.md.
//
// The identity column is otomo's player id, not a device and not a password: this schema holds no account of its
// own and no session at all (migration 4 dropped the sessions table). `id` is only the numeric handle the KF
// client's protocol requires.
//
// Migrations are embedded rather than run by hand so a fresh database reaches the current schema by starting
// the API once. `schema_meta` records which have been applied, so applying is idempotent and additive.
public sealed class PostgresPlayerStore : IPlayerStore
{
    private const int SchemaVersion = 9;

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
            """),
        (9, """
            -- Badges moved to otomo. What a player has earned is the honor:* items in their economy inventory, the
            -- progress toward the next one is lifetime tracks fed by facts this adapter reports (battle.result,
            -- login.day, item.upgraded, like.received, registration.completed), and the badge a player shows is a
            -- session display slot. The four tables are dropped rather than left to drift, because every read and
            -- write of them is gone and a table nothing writes is a table something reads by mistake. The `state`
            -- document keys for honors, counters, login days and the processed-battle ledger simply stop being
            -- written, and an old document that still carries them is ignored on read.
            --
            -- Nothing is copied: no live player data migrates, every player starts fresh on otomo, so there is no
            -- backfill of counters or honors and no compatibility path for an old save's badges.
            drop table if exists player_badge_ledger;
            drop table if exists player_badge_counters;
            drop table if exists player_badge_streaks;
            drop table if exists player_honors;
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
        using var command = connection.CreateCommand();
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

    public IReadOnlyList<PushRegistration> ListPushRegistrations()
    {
        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, state->>'PushToken' from players
            where coalesce(state->>'PushToken', '') <> ''
            """;
        var result = new List<PushRegistration>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new PushRegistration(reader.GetInt64(0), reader.GetString(1)));
        return result;
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
