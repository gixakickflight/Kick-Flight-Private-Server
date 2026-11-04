using System.Collections.Concurrent;
using System.Text.Json;

namespace KickFlight.BootstrapApi.PlayerStore;

// The original on-disk store: one <player id>.json per player under <content root>/data/users, plus two
// indexes so identity survives a restart - _identities.json (device uuid -> player id, the pre-otomo key) and
// _otomo-players.json (otomo account -> player id, the identity now).
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
            loaded.ItemAmounts ??= new Dictionary<int, int>();
            loaded.Discs ??= new Dictionary<int, UserDiscState>();
            return loaded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error loading user state for {PlayerId}: {Error}", playerId, ex.Message);
            return null;
        }
    }

    public IReadOnlyList<PushRegistration> ListPushRegistrations()
    {
        var result = new List<PushRegistration>();
        foreach (var file in Directory.EnumerateFiles(_usersDirectory, "*.json"))
        {
            if (!long.TryParse(Path.GetFileNameWithoutExtension(file), out var id)) continue;
            var state = TryLoad(id);
            if (!string.IsNullOrEmpty(state?.PushToken)) result.Add(new PushRegistration(id, state.PushToken));
        }
        return result;
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

    public void SaveSession(string accessToken, long playerId, byte[] key)
    {
        _keysByAccessToken[accessToken] = key;
        _playerIdByToken[accessToken] = playerId;
    }

    public SessionRecord? FindSession(string accessToken) =>
        _keysByAccessToken.TryGetValue(accessToken, out var key) && _playerIdByToken.TryGetValue(accessToken, out var playerId)
            ? new SessionRecord(playerId, key)
            : null;

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
            var players = new List<OtomoPlayer>();
            foreach (var account in otomoPlayerIds.Distinct(StringComparer.Ordinal))
            {
                if (!_playerIdByOtomoPlayer.TryGetValue(account, out var playerId)) continue;
                players.Add(new OtomoPlayer(playerId, account));
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
            foreach (var (account, owner) in _playerIdByOtomoPlayer)
            {
                if (owner != playerId) continue;
                return new OtomoPlayer(playerId, account);
            }
            return null;
        }
    }

    private static PlayerProfile ProfileFromState(long playerId, SessionState state) =>
        new(playerId, state.UserName, state.KickerId, state.KickerCostumeId);
}
