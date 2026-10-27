using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace KickFlight.BootstrapApi.PlayerStore;

// Player state in PostgreSQL, which is what the deployment uses.
//
// Shape, and why it is not fully normalised: the four things a service actually queries - identity, the
// display name, the currencies and the battle rank - are real columns, and the rest of the state is one
// `state jsonb` document. Decks, owned discs, costume gears and the pending gear roll are only ever read and
// written as a whole (the API loads a player, mutates, saves), so normalising them would buy nothing and cost
// a mapping layer plus the chance of a half-applied save. See deploy/README.md.
//
// The identity column is otomo's player id, not a device and not a password: this schema holds no account of its
// own and no session at all (migration 3 dropped the sessions table). `id` is only the numeric handle the KF
// client's protocol requires.
//
// Migrations are embedded rather than run by hand so a fresh database reaches the current schema by starting
// the API once. `schema_meta` records which have been applied, so applying is idempotent and additive.
public sealed class PostgresPlayerStore : IPlayerStore
{
    private const int SchemaVersion = 3;

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
        (3, """
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
                id, otomo_player_id, legacy_device_uuid, kicker_id, kicker_costume_id, active_deck_number,
                item_jet_coins, item_paid_jet_coins, item_disc_force, item_kick_points, state)
            values (nextval('player_id_seq'), $1, $2, $3, $4, $5, $6, $7, $8, $9, '{}'::jsonb)
            on conflict do nothing
            returning id
            """,
            command =>
            {
                command.Parameters.AddWithValue(otomoPlayerId);
                command.Parameters.AddWithValue(string.IsNullOrEmpty(legacyDeviceUuid) ? DBNull.Value : legacyDeviceUuid);
                command.Parameters.AddWithValue(defaults.KickerId);
                command.Parameters.AddWithValue(defaults.KickerCostumeId);
                command.Parameters.AddWithValue(defaults.ActiveDeckNumber);
                command.Parameters.AddWithValue(defaults.ItemJetCoins);
                command.Parameters.AddWithValue(defaults.ItemPaidJetCoins);
                command.Parameters.AddWithValue(defaults.ItemDiscForce);
                command.Parameters.AddWithValue(defaults.ItemKickPoints);
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
            select display_name, kicker_id, kicker_costume_id, active_deck_number,
                   item_jet_coins, item_paid_jet_coins, item_disc_force, item_kick_points, state
            from players where id = $1
            """;
        command.Parameters.AddWithValue(playerId);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        // The document carries everything except the identity and the columns that a service queries; those
        // are authoritative in their own columns and are copied over whatever the document said.
        var state = DecodeDocument(reader.GetString(8));
        state.UserId = playerId.ToString();
        state.UserName = reader.IsDBNull(0) ? "" : reader.GetString(0);
        state.KickerId = reader.GetInt32(1);
        state.KickerCostumeId = reader.GetInt32(2);
        state.ActiveDeckNumber = reader.GetInt32(3);
        state.ItemJetCoins = reader.GetInt32(4);
        state.ItemPaidJetCoins = reader.GetInt32(5);
        state.ItemDiscForce = reader.GetInt32(6);
        state.ItemKickPoints = reader.GetInt32(7);
        return state;
    }

    private SessionState DecodeDocument(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return new SessionState();
        try
        {
            var state = JsonSerializer.Deserialize<SessionState>(json);
            if (state is null) return new SessionState();
            state.CostumeGears ??= new Dictionary<int, int[]>();
            state.PendingGear ??= new PendingGearState();
            state.ItemAmounts ??= new Dictionary<int, int>();
            state.Discs ??= new Dictionary<int, UserDiscState>();
            state.Decks ??= new Dictionary<int, List<int>>();
            return state;
        }
        catch (JsonException ex)
        {
            // A corrupt document must not take the player's whole account down, but it must be visible.
            _logger.LogError(ex, "Player state document is not readable as JSON; starting from defaults");
            return new SessionState();
        }
    }

    public void Save(SessionState state)
    {
        if (state.PlayerId == 0)
        {
            _logger.LogWarning("Refusing to save a state whose UserId {UserId} is not a player id", state.UserId);
            return;
        }

        // The document excludes nothing today, but the columns above are the ones services read, so they are
        // written from the state and the document keeps the rest. Neither identity column is written here: the
        // account a row belongs to is decided once, in ResolveGamePlayer, and a later save must not move it.
        var document = JsonSerializer.Serialize(state);

        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            update players set
                display_name        = nullif($2, ''),
                kicker_id           = $3,
                kicker_costume_id   = $4,
                active_deck_number  = $5,
                item_jet_coins      = $6,
                item_paid_jet_coins = $7,
                item_disc_force     = $8,
                item_kick_points    = $9,
                state               = $10::jsonb,
                updated_at          = now()
            where id = $1
            """;
        command.Parameters.AddWithValue(state.PlayerId);
        // The empty name is stored as NULL: it is the absence of a name that ends onboarding, and NULL says
        // that unambiguously where '' could also be read as a name someone chose.
        command.Parameters.AddWithValue(state.UserName);
        command.Parameters.AddWithValue(state.KickerId);
        command.Parameters.AddWithValue(state.KickerCostumeId);
        command.Parameters.AddWithValue(state.ActiveDeckNumber);
        command.Parameters.AddWithValue(state.ItemJetCoins);
        command.Parameters.AddWithValue(state.ItemPaidJetCoins);
        command.Parameters.AddWithValue(state.ItemDiscForce);
        command.Parameters.AddWithValue(state.ItemKickPoints);
        command.Parameters.AddWithValue(document);

        if (command.ExecuteNonQuery() == 0)
        {
            _logger.LogWarning("Save for player {PlayerId} matched no row", state.PlayerId);
        }
    }

    public RankState LoadRank(long playerId, int battleRuleType)
    {
        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "select battle_point, rank from player_ranks where player_id = $1 and battle_rule_type = $2";
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(battleRuleType);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new RankState(reader.GetInt32(0), reader.GetInt32(1)) : RankProgression.Starting;
    }

    public void SaveRank(long playerId, int battleRuleType, RankState rank)
    {
        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            insert into player_ranks (player_id, battle_rule_type, battle_point, rank)
            values ($1, $2, $3, $4)
            on conflict (player_id, battle_rule_type) do update set
                battle_point = excluded.battle_point,
                rank         = excluded.rank,
                updated_at   = now()
            """;
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(battleRuleType);
        command.Parameters.AddWithValue(rank.BattlePoint);
        command.Parameters.AddWithValue(rank.Rank);
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

    public IReadOnlyList<long> FindFollowedIds(long followerId, IReadOnlyCollection<long> candidateIds)
    {
        ArgumentNullException.ThrowIfNull(candidateIds);
        if (candidateIds.Count == 0) return Array.Empty<long>();

        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select followed_id from player_follows
            where follower_id = $1 and followed_id = any($2)
            order by followed_id
            """;
        command.Parameters.AddWithValue(followerId);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint,
            Value = candidateIds.Distinct().ToArray()
        });

        var result = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetInt64(0));
        return result;
    }

    public void AddFollow(long followerId, long followedId)
    {
        ValidateFollowIds(followerId, followedId);
        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            insert into player_follows (follower_id, followed_id)
            values ($1, $2)
            on conflict (follower_id, followed_id) do nothing
            """;
        command.Parameters.AddWithValue(followerId);
        command.Parameters.AddWithValue(followedId);
        command.ExecuteNonQuery();
    }

    public void RemoveFollow(long followerId, long followedId)
    {
        ValidateFollowIds(followerId, followedId);
        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "delete from player_follows where follower_id = $1 and followed_id = $2";
        command.Parameters.AddWithValue(followerId);
        command.Parameters.AddWithValue(followedId);
        command.ExecuteNonQuery();
    }

    public int CountFollowing(long playerId) => CountFollowEdges("follower_id", playerId);

    public int CountFollowers(long playerId) => CountFollowEdges("followed_id", playerId);

    public IReadOnlyList<FollowedProfile> ListFollowing(long playerId, int offset, int limit)
    {
        ValidatePage(offset, limit);
        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select p.id, coalesce(p.display_name, ''), p.kicker_id, p.kicker_costume_id,
                   f.created_at, false
            from player_follows f
            join players p on p.id = f.followed_id
            where f.follower_id = $1
            order by f.created_at desc, p.id asc
            offset $2 limit $3
            """;
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(offset);
        command.Parameters.AddWithValue(limit);
        return ReadFollowedProfiles(command);
    }

    public IReadOnlyList<FollowedProfile> ListFollowers(long playerId, int offset, int limit)
    {
        ValidatePage(offset, limit);
        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select p.id, coalesce(p.display_name, ''), p.kicker_id, p.kicker_costume_id,
                   f.created_at, f.read_at is null
            from player_follows f
            join players p on p.id = f.follower_id
            where f.followed_id = $1
            order by f.created_at desc, p.id asc
            offset $2 limit $3
            """;
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(offset);
        command.Parameters.AddWithValue(limit);
        return ReadFollowedProfiles(command);
    }

    public int CountNewFollowers(long playerId)
    {
        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from player_follows where followed_id = $1 and read_at is null";
        command.Parameters.AddWithValue(playerId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void MarkFollowersRead(long playerId, IReadOnlyCollection<long> followerIds)
    {
        ArgumentNullException.ThrowIfNull(followerIds);
        if (followerIds.Count == 0) return;

        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            update player_follows set read_at = coalesce(read_at, now())
            where followed_id = $1 and follower_id = any($2)
            """;
        command.Parameters.AddWithValue(playerId);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint,
            Value = followerIds.Distinct().ToArray()
        });
        command.ExecuteNonQuery();
    }

    public string GetOrCreateRealFriendToken(long playerId)
    {
        if (playerId <= 0) throw new ArgumentOutOfRangeException(nameof(playerId));
        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            insert into real_friend_tokens (player_id, token) values ($1, $2)
            on conflict (player_id) do update set player_id = excluded.player_id
            returning token
            """;
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(CreateOpaqueToken());
        return Convert.ToString(command.ExecuteScalar())!;
    }

    public long? TryGetRealFriendTokenOwner(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "select player_id from real_friend_tokens where token = $1";
        command.Parameters.AddWithValue(token);
        var owner = command.ExecuteScalar();
        return owner is null or DBNull ? null : Convert.ToInt64(owner);
    }

    public void AddRealFriend(long firstPlayerId, long secondPlayerId)
    {
        ValidateFollowIds(firstPlayerId, secondPlayerId);
        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            insert into player_real_friends (player_low_id, player_high_id)
            values ($1, $2)
            on conflict (player_low_id, player_high_id) do nothing
            """;
        command.Parameters.AddWithValue(Math.Min(firstPlayerId, secondPlayerId));
        command.Parameters.AddWithValue(Math.Max(firstPlayerId, secondPlayerId));
        command.ExecuteNonQuery();
    }

    public bool AreRealFriends(long firstPlayerId, long secondPlayerId)
    {
        ValidateFollowIds(firstPlayerId, secondPlayerId);
        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select exists (
                select 1 from player_real_friends
                where player_low_id = $1 and player_high_id = $2
            )
            """;
        command.Parameters.AddWithValue(Math.Min(firstPlayerId, secondPlayerId));
        command.Parameters.AddWithValue(Math.Max(firstPlayerId, secondPlayerId));
        return Convert.ToBoolean(command.ExecuteScalar());
    }

    public IReadOnlyList<RankedPlayer> ListRanks(
        int battleRuleType,
        IReadOnlyCollection<long>? playerIds,
        int offset,
        int limit)
    {
        ValidatePage(offset, limit);
        if (playerIds is { Count: 0 }) return Array.Empty<RankedPlayer>();

        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = playerIds is null
            ? RankingQuery(includePlayerFilter: false)
            : RankingQuery(includePlayerFilter: true);
        command.Parameters.AddWithValue(battleRuleType);
        command.Parameters.AddWithValue(RankProgression.StartBattlePoint);
        command.Parameters.AddWithValue(RankProgression.StartRank);
        if (playerIds is not null)
        {
            command.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint,
                Value = playerIds.Distinct().ToArray()
            });
        }
        command.Parameters.AddWithValue(offset);
        command.Parameters.AddWithValue(limit);

        var result = new List<RankedPlayer>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var profile = ReadProfile(reader, 0);
            result.Add(new RankedPlayer(profile, reader.GetInt32(4), reader.GetInt32(5), reader.GetInt64(6)));
        }
        return result;
    }

    private static string RankingQuery(bool includePlayerFilter)
    {
        var filter = includePlayerFilter ? "where id = any($4)" : "";
        var offsetParameter = includePlayerFilter ? "$5" : "$4";
        var limitParameter = includePlayerFilter ? "$6" : "$5";
        return $"""
            with scored as (
                select p.id, coalesce(p.display_name, '') as display_name, p.kicker_id, p.kicker_costume_id,
                       coalesce(r.battle_point, $2) as battle_point,
                       coalesce(r.rank, $3) as rank,
                       row_number() over (order by coalesce(r.battle_point, $2) desc, p.id asc) as position
                from players p
                left join player_ranks r on r.player_id = p.id and r.battle_rule_type = $1
                where nullif(p.display_name, '') is not null
            )
            select id, display_name, kicker_id, kicker_costume_id, battle_point, rank, position
            from scored
            {filter}
            order by position
            offset {offsetParameter} limit {limitParameter}
            """;
    }

    private static PlayerProfile ReadProfile(NpgsqlDataReader reader, int firstColumn) =>
        new(reader.GetInt64(firstColumn), reader.GetString(firstColumn + 1),
            reader.GetInt32(firstColumn + 2), reader.GetInt32(firstColumn + 3));

    private static IReadOnlyList<FollowedProfile> ReadFollowedProfiles(NpgsqlCommand command)
    {
        var result = new List<FollowedProfile>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var createdAt = DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc);
            result.Add(new FollowedProfile(ReadProfile(reader, 0), new DateTimeOffset(createdAt), reader.GetBoolean(5)));
        }
        return result;
    }

    private static void ValidateFollowIds(long followerId, long followedId)
    {
        if (followerId <= 0) throw new ArgumentOutOfRangeException(nameof(followerId));
        if (followedId <= 0) throw new ArgumentOutOfRangeException(nameof(followedId));
        if (followerId == followedId) throw new ArgumentException("A player cannot follow themselves.", nameof(followedId));
    }

    private static void ValidatePage(int offset, int limit)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
    }

    private int CountFollowEdges(string playerColumn, long playerId)
    {
        using var connection = _dataSource.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"select count(*) from player_follows where {playerColumn} = $1";
        command.Parameters.AddWithValue(playerId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string CreateOpaqueToken() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
