using System.Collections.Concurrent;
using System.Text.Json;

namespace KickFlight.BootstrapApi.PlayerStore;

// The original on-disk store: one <player id>.json per player under <content root>/data/users, plus three
// indexes so identity survives a restart - _identities.json (device uuid -> player id, the pre-otomo key),
// _otomo-players.json (otomo account -> player id, the identity now) and _otomo-tags.json (the display name and
// discriminator otomo answers for an account, which is what a follow is addressed by).
//
// This is the fallback, selected when no connection string is configured, so that local runs and the test
// suite work with no database. The deployment uses PostgresPlayerStore.
//
// Identity is otomo's: what is remembered here is which numeric row belongs to which account, and which row a
// device used before otomo owned identity - that row is adopted by the first account that presents the device,
// so an existing player keeps their name, rank and inventory. Sessions are held in memory, and no caller issues
// them any more (the access token is signed by the API); the map is kept because local runs may still restart
// mid-session.
//
// The social relation is not here at all any more: follows, friend tokens and real friends moved to otomo's
// session service, and the three tables migration 6 drops have this file's _social.json behind them. What is left
// is identity, which is what this store was always for.
public sealed class JsonPlayerStore : IPlayerStore
{
    // The index file names are prefixed so they cannot collide with a save file, whose name is always a
    // bare player id. They are also excluded from the glob the deploy script uses to report user counts.
    private const string IdentityIndexFile = "_identities.json";
    private const string OtomoIndexFile = "_otomo-players.json";
    private const string OtomoTagFile = "_otomo-tags.json";
    private const string FirstPlayerId = "1000001";

    // The highest id this directory has ever been seen to contain, process-wide. Two stores over one directory -
    // which is what IClassFixture hands the test suite, one server per test class - would otherwise each read the
    // directory before either writes and issue the same id to two different devices, leaving two sessions to
    // overwrite each other's save file. One server in production never needs this, but it costs nothing there.
    private static readonly object IdLock = new();
    private static readonly ConcurrentDictionary<string, object> IdentityLocksByDirectory = new(StringComparer.Ordinal);
    private static long _idHighWaterMark;

    private readonly string _usersDirectory;
    private readonly ILogger<JsonPlayerStore> _logger;
    private readonly object _identityLock;
    private readonly Dictionary<string, long> _playerIdByUuid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _playerIdByOtomoPlayer = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OtomoTag> _tagsByOtomoPlayer = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _keysByAccessToken = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _playerIdByToken = new(StringComparer.Ordinal);

    public JsonPlayerStore(ILogger<JsonPlayerStore> logger, IWebHostEnvironment environment)
    {
        _logger = logger;
        _usersDirectory = Path.Combine(environment.ContentRootPath, "data", "users");
        _identityLock = IdentityLocksByDirectory.GetOrAdd(Path.GetFullPath(_usersDirectory), _ => new object());
        Directory.CreateDirectory(_usersDirectory);
        lock (_identityLock)
        {
            LoadIdentityIndex();
            LoadOtomoIndex();
            LoadOtomoTags();
        }
    }

    private void LoadIdentityIndex()
    {
        var path = Path.Combine(_usersDirectory, IdentityIndexFile);
        if (!File.Exists(path)) return;
        try
        {
            var index = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path));
            if (index is null) return;
            foreach (var (uuid, playerId) in index) _playerIdByUuid[uuid] = playerId;
            _logger.LogInformation("Loaded {Count} device identities from {Path}", _playerIdByUuid.Count, path);
        }
        catch (Exception ex)
        {
            // Losing the index is recoverable (a returning device gets a new id) but never silent: the whole
            // point of the store is to not do that.
            _logger.LogError(ex, "Could not read the identity index at {Path}; returning devices will be re-created", path);
        }
    }

    private void PersistIdentityIndex()
    {
        try
        {
            var path = Path.Combine(_usersDirectory, IdentityIndexFile);
            var json = JsonSerializer.Serialize(_playerIdByUuid, new JsonSerializerOptions { WriteIndented = true });
            // Write-then-rename: a crash mid-write must not leave a truncated index, which would silently
            // re-issue identities to every device that then connects.
            // Stores over this directory share the identity lock, while the unique suffix also avoids
            // collisions with another process writing the same index.
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not write the identity index; this mapping will be lost on restart");
        }
    }

    private void LoadOtomoIndex()
    {
        var path = Path.Combine(_usersDirectory, OtomoIndexFile);
        if (!File.Exists(path)) return;
        try
        {
            var index = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path));
            if (index is null) return;
            foreach (var (account, playerId) in index) _playerIdByOtomoPlayer[account] = playerId;
            _logger.LogInformation("Loaded {Count} otomo player rows from {Path}", _playerIdByOtomoPlayer.Count, path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the otomo player index at {Path}; returning accounts will be re-created", path);
        }
    }

    private void PersistOtomoIndex()
    {
        try
        {
            var path = Path.Combine(_usersDirectory, OtomoIndexFile);
            var json = JsonSerializer.Serialize(_playerIdByOtomoPlayer, new JsonSerializerOptions { WriteIndented = true });
            // Write-then-rename, for the same reason as the identity index: a truncated index would re-issue
            // rows to every account that then connects.
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not write the otomo player index; this mapping will be lost on restart");
        }
    }

    private void LoadOtomoTags()
    {
        var path = Path.Combine(_usersDirectory, OtomoTagFile);
        if (!File.Exists(path)) return;
        try
        {
            var index = JsonSerializer.Deserialize<Dictionary<string, OtomoTag>>(File.ReadAllText(path));
            if (index is null) return;
            foreach (var (account, tag) in index) _tagsByOtomoPlayer[account] = tag;
            _logger.LogInformation("Loaded {Count} otomo tags from {Path}", _tagsByOtomoPlayer.Count, path);
        }
        catch (Exception ex)
        {
            // A lost tag is recoverable - the account is asked for its tag again the next time it renames - but it is
            // never silent, because until then a follow cannot be addressed by a client that names that player.
            _logger.LogError(ex, "Could not read the otomo tag index at {Path}; follows by name will need a rename", path);
        }
    }

    private void PersistOtomoTags()
    {
        try
        {
            var path = Path.Combine(_usersDirectory, OtomoTagFile);
            var json = JsonSerializer.Serialize(_tagsByOtomoPlayer, new JsonSerializerOptions { WriteIndented = true });
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not write the otomo tag index; follows by name will need a rename");
        }
    }

    public long ResolvePlayerId(string uuid)
    {
        lock (_identityLock)
        {
            // Each instance has its own dictionary. Refresh under the directory-wide lock so a second
            // store cannot persist an older snapshot and erase identities just added by the first.
            LoadIdentityIndex();
            if (_playerIdByUuid.TryGetValue(uuid, out var existing)) return existing;

            var playerId = NextPlayerId();
            _playerIdByUuid[uuid] = playerId;
            PersistIdentityIndex();
            _logger.LogInformation("New device {Uuid} assigned player {PlayerId}", uuid, playerId);
            return playerId;
        }
    }

    // The account's row: stable across restarts, and the row a device already had is adopted rather than
    // abandoned, so an existing player keeps their name, rank, inventory and friends.
    public long ResolveGamePlayer(string otomoPlayerId, string legacyDeviceUuid)
    {
        if (string.IsNullOrWhiteSpace(otomoPlayerId))
            throw new ArgumentException("An otomo player id is required.", nameof(otomoPlayerId));

        lock (_identityLock)
        {
            // Each instance has its own dictionaries. Refresh under the directory-wide lock so a second store
            // cannot persist an older snapshot and erase entries just added by the first.
            LoadIdentityIndex();
            LoadOtomoIndex();
            if (_playerIdByOtomoPlayer.TryGetValue(otomoPlayerId, out var existing)) return existing;

            long playerId;
            if (!string.IsNullOrEmpty(legacyDeviceUuid) && _playerIdByUuid.TryGetValue(legacyDeviceUuid, out var adopted))
            {
                playerId = adopted;
                _logger.LogInformation("otomo player {OtomoPlayerId} adopted the existing row {PlayerId} of device {Device}",
                    otomoPlayerId, playerId, legacyDeviceUuid);
            }
            else
            {
                playerId = NextPlayerId();
                _logger.LogInformation("otomo player {OtomoPlayerId} assigned game row {PlayerId}", otomoPlayerId, playerId);
            }

            _playerIdByOtomoPlayer[otomoPlayerId] = playerId;
            PersistOtomoIndex();
            if (!string.IsNullOrEmpty(legacyDeviceUuid) && !_playerIdByUuid.ContainsKey(legacyDeviceUuid))
            {
                _playerIdByUuid[legacyDeviceUuid] = playerId;
                PersistIdentityIndex();
            }
            return playerId;
        }
    }

    // One past the highest id in use, so deleting a save file cannot make the next device inherit its id.
    private long NextPlayerId()
    {
        lock (IdLock)
        {
            long highest = Math.Max(_idHighWaterMark, long.Parse(FirstPlayerId));
            foreach (var candidate in _playerIdByUuid.Values.Concat(_playerIdByOtomoPlayer.Values))
            {
                if (candidate > highest) highest = candidate;
            }
            foreach (var file in Directory.EnumerateFiles(_usersDirectory, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (long.TryParse(name, out var id) && id > highest) highest = id;
            }
            _idHighWaterMark = highest + 1;
            return _idHighWaterMark;
        }
    }

    public SessionState? TryLoad(long playerId)
    {
        var file = Path.Combine(_usersDirectory, $"{playerId}.json");
        if (!File.Exists(file)) return null;
        try
        {
            var loaded = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(file));
            if (loaded is null) return null;
            loaded.UserId = playerId.ToString();
            // Save files written before gear support (or hand-edited with an explicit null) must load as
            // "no gear", never as a null dictionary the gear handlers would then dereference.
            loaded.CostumeGears ??= new Dictionary<int, int[]>();
            loaded.PendingGear ??= new PendingGearState();
            loaded.ItemAmounts ??= new Dictionary<int, int>();
            loaded.Discs ??= new Dictionary<int, UserDiscState>();
            loaded.Decks ??= new Dictionary<int, List<int>>();
            return loaded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error loading user state for {PlayerId}: {Error}", playerId, ex.Message);
            return null;
        }
    }

    public void Save(SessionState state, string? uuid = null)
    {
        if (state.PlayerId == 0)
        {
            _logger.LogWarning("Refusing to save a state whose UserId {UserId} is not a player id", state.UserId);
            return;
        }
        try
        {
            if (uuid is not null)
            {
                lock (_identityLock)
                {
                    LoadIdentityIndex();
                    if (_playerIdByUuid.TryGetValue(uuid, out var known) && known != state.PlayerId)
                    {
                        _logger.LogWarning("Device {Uuid} maps to {Known} but is saving {Actual}; keeping the mapping",
                            uuid, known, state.PlayerId);
                    }
                    else if (!_playerIdByUuid.ContainsKey(uuid))
                    {
                        _playerIdByUuid[uuid] = state.PlayerId;
                        PersistIdentityIndex();
                    }
                }
            }

            var file = Path.Combine(_usersDirectory, $"{state.PlayerId}.json");
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            // Write-then-rename so a crash or a concurrent reader never sees a truncated save file.
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error saving user state for {PlayerId}: {Error}", state.PlayerId, ex.Message);
        }
    }

    private static readonly ConcurrentDictionary<string, object> BadgeLocks = new(StringComparer.Ordinal);

    public BadgeMutationResult MutateBadges(
        long playerId, SessionState? fallback, BadgeLedgerKey? ledger, Func<SessionState, IReadOnlyList<int>?> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        if (playerId <= 0) return new BadgeMutationResult(false, [], null);

        // One lock per player and directory: every badge writer of this process goes through here.
        lock (BadgeLocks.GetOrAdd(Path.GetFullPath(_usersDirectory) + "|" + playerId, static _ => new object()))
        {
            var current = TryLoad(playerId) ?? fallback;
            if (current is null) return new BadgeMutationResult(false, [], null);
            if (ledger is not null && BadgeTableMapping.TryGetLedger(current, ledger, out var previous))
                return new BadgeMutationResult(false, previous, current);

            var awarded = mutate(current);
            if (awarded is null) return new BadgeMutationResult(false, [], current);
            if (ledger is not null) BadgeTableMapping.RecordLedgerAwards(current, ledger, awarded);
            Save(current);
            return new BadgeMutationResult(true, awarded, current);
        }
    }

    public void SaveSession(string accessToken, long playerId, byte[] key)
    {
        _keysByAccessToken[accessToken] = key;
        _playerIdByToken[accessToken] = playerId;
    }

    public SessionRecord? FindSession(string accessToken) =>
        _keysByAccessToken.TryGetValue(accessToken, out var key) && _playerIdByToken.TryGetValue(accessToken, out var playerId)
            ? new SessionRecord(playerId, key)
            : null;

    // Rank is not persisted in this store: it belongs to the deployment, and the fallback keeps it in memory
    // so local runs and tests see a stable value instead of the starting rank on every read.
    private readonly ConcurrentDictionary<(long PlayerId, int BattleRuleType), RankState> _ranks = new();

    public RankState LoadRank(long playerId, int battleRuleType) =>
        _ranks.TryGetValue((playerId, battleRuleType), out var rank) ? rank : RankProgression.Starting;

    public void SaveRank(long playerId, int battleRuleType, RankState rank) =>
        _ranks[(playerId, battleRuleType)] = rank;

    public PlayerProfile? FindProfile(long playerId)
    {
        if (playerId <= 0) return null;
        var state = TryLoad(playerId);
        return state is null ? null : ProfileFromState(playerId, state);
    }

    public IReadOnlyList<PlayerProfile> FindProfiles(IReadOnlyCollection<long> playerIds)
    {
        ArgumentNullException.ThrowIfNull(playerIds);
        return playerIds.Distinct().OrderBy(id => id)
            .Select(FindProfile)
            .Where(profile => profile is not null)
            .Select(profile => profile!)
            .ToArray();
    }

    public IReadOnlyList<OtomoPlayer> FindOtomoPlayers(IReadOnlyCollection<string> otomoPlayerIds)
    {
        ArgumentNullException.ThrowIfNull(otomoPlayerIds);
        if (otomoPlayerIds.Count == 0) return Array.Empty<OtomoPlayer>();
        lock (_identityLock)
        {
            LoadOtomoIndex();
            LoadOtomoTags();
            var players = new List<OtomoPlayer>();
            foreach (var account in otomoPlayerIds.Distinct(StringComparer.Ordinal))
            {
                if (!_playerIdByOtomoPlayer.TryGetValue(account, out var playerId)) continue;
                var tag = _tagsByOtomoPlayer.GetValueOrDefault(account);
                players.Add(new OtomoPlayer(playerId, account, tag?.DisplayName ?? "", tag?.Discriminator ?? 0));
            }
            return players.OrderBy(player => player.PlayerId).ToArray();
        }
    }

    public OtomoPlayer? FindOtomoPlayer(long playerId)
    {
        if (playerId <= 0) return null;
        lock (_identityLock)
        {
            LoadOtomoIndex();
            LoadOtomoTags();
            foreach (var (account, owner) in _playerIdByOtomoPlayer)
            {
                if (owner != playerId) continue;
                var tag = _tagsByOtomoPlayer.GetValueOrDefault(account);
                return new OtomoPlayer(playerId, account, tag?.DisplayName ?? "", tag?.Discriminator ?? 0);
            }
            return null;
        }
    }

    public void SaveOtomoTag(string otomoPlayerId, string displayName, int discriminator)
    {
        if (string.IsNullOrWhiteSpace(otomoPlayerId)) return;
        lock (_identityLock)
        {
            LoadOtomoIndex();
            LoadOtomoTags();
            if (!_playerIdByOtomoPlayer.ContainsKey(otomoPlayerId)) return;
            var tag = new OtomoTag(displayName, discriminator);
            if (_tagsByOtomoPlayer.TryGetValue(otomoPlayerId, out var current) && current == tag) return;
            _tagsByOtomoPlayer[otomoPlayerId] = tag;
            PersistOtomoTags();
        }
    }

    public IReadOnlyList<RankedPlayer> ListRanks(
        int battleRuleType,
        IReadOnlyCollection<long>? playerIds,
        int offset,
        int limit)
    {
        ValidatePage(offset, limit);
        if (playerIds is { Count: 0 }) return Array.Empty<RankedPlayer>();

        var selectedIds = playerIds?.ToHashSet();
        var all = Directory.EnumerateFiles(_usersDirectory, "*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(name => long.TryParse(name, out _))
            .Select(name => long.Parse(name!))
            .Select(id => FindProfile(id))
            .Where(profile => profile is not null && !string.IsNullOrEmpty(profile.DisplayName))
            .Select(profile => profile!)
            .Select(profile =>
            {
                var standing = LoadRank(profile.PlayerId, battleRuleType);
                return (Profile: profile, Standing: standing);
            })
            .OrderByDescending(row => row.Standing.BattlePoint)
            .ThenBy(row => row.Profile.PlayerId)
            .Select((row, index) => new RankedPlayer(row.Profile, row.Standing.BattlePoint, row.Standing.Rank, index + 1L));

        if (selectedIds is not null) all = all.Where(row => selectedIds.Contains(row.Profile.PlayerId));
        return all.Skip(offset).Take(limit).ToArray();
    }

    private static PlayerProfile ProfileFromState(long playerId, SessionState state) =>
        new(playerId, state.UserName, state.KickerId, state.KickerCostumeId);

    private static void ValidatePage(int offset, int limit)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
    }

    private sealed record OtomoTag(string DisplayName, int Discriminator);

    // A directory scan, which is the honest way to answer this against files: the deployment, where the answer
    // has to be fast and exact, uses the unique index the Postgres store queries instead.
    public bool IsNameTaken(string name, long exceptPlayerId)
    {
        foreach (var file in Directory.EnumerateFiles(_usersDirectory, "*.json"))
        {
            if (!long.TryParse(Path.GetFileNameWithoutExtension(file), out var playerId)) continue; // skips the identity index
            if (playerId == exceptPlayerId) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                if (document.RootElement.TryGetProperty(nameof(SessionState.UserName), out var prop)
                    && prop.ValueKind == JsonValueKind.String
                    && string.Equals(prop.GetString(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (JsonException ex)
            {
                // An unreadable save must not block someone else's onboarding.
                _logger.LogWarning("Skipping unreadable save {File} while checking names: {Error}", file, ex.Message);
            }
        }
        return false;
    }
}
