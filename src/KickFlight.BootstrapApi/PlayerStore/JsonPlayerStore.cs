using System.Collections.Concurrent;
using System.Text.Json;

namespace KickFlight.BootstrapApi.PlayerStore;

// The original on-disk store: one <player id>.json per player under <content root>/data/users, plus an index so
// an otomo account keeps the same local row id across restarts.
//
// This is the fallback, selected when no connection string is configured, so that local runs and the test
// suite work with no database. The deployment uses PostgresPlayerStore.
//
// Identity is not this store's job any more: otomo owns the account, and what is remembered here is only which
// numeric row in this directory belongs to which otomo player (ResolveGamePlayer). A client whose token is lost
// re-authenticates through otomo on next launch and, because the account id is stable, gets its own row back.
public sealed class JsonPlayerStore : IPlayerStore
{
    // The index file names are prefixed so they cannot collide with a save file, whose name is always a
    // bare player id. They are also excluded from the glob the deploy script uses to report user counts.
    //
    // _identities.json is the pre-otomo index (device uuid -> player id). It is read, never written: it is what
    // lets an existing player's row be adopted by the otomo account that presents that device.
    private const string LegacyIdentityIndexFile = "_identities.json";
    private const string OtomoIndexFile = "_otomo-players.json";
    private const string SocialStateFile = "_social.json";
    private const string FirstPlayerId = "1000001";

    // The highest id this directory has ever been seen to contain, process-wide. Two stores over one directory -
    // which is what IClassFixture hands the test suite, one server per test class - would otherwise each read the
    // directory before either writes and issue the same id to two different devices, leaving two sessions to
    // overwrite each other's save file. One server in production never needs this, but it costs nothing there.
    private static readonly object IdLock = new();
    private static readonly ConcurrentDictionary<string, object> IdentityLocksByDirectory = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, object> SocialLocksByDirectory = new(StringComparer.Ordinal);
    private static long _idHighWaterMark;

    private readonly string _usersDirectory;
    private readonly ILogger<JsonPlayerStore> _logger;
    private readonly object _identityLock;
    private readonly object _socialLock;
    private readonly Dictionary<string, long> _playerIdByOtomoPlayer = new(StringComparer.Ordinal);
    // The pre-otomo index, loaded only so an existing row can be adopted by the account that presents its device.
    private readonly Dictionary<string, long> _legacyPlayerIdByDevice = new(StringComparer.Ordinal);

    public JsonPlayerStore(ILogger<JsonPlayerStore> logger, IWebHostEnvironment environment)
    {
        _logger = logger;
        _usersDirectory = Path.Combine(environment.ContentRootPath, "data", "users");
        _identityLock = IdentityLocksByDirectory.GetOrAdd(Path.GetFullPath(_usersDirectory), _ => new object());
        _socialLock = SocialLocksByDirectory.GetOrAdd(Path.GetFullPath(_usersDirectory), _ => new object());
        Directory.CreateDirectory(_usersDirectory);
        lock (_identityLock) LoadPlayerIndex();
    }

    private void LoadPlayerIndex()
    {
        ReadIndex(OtomoIndexFile, _playerIdByOtomoPlayer);
        ReadIndex(LegacyIdentityIndexFile, _legacyPlayerIdByDevice);
    }

    private void ReadIndex(string fileName, Dictionary<string, long> target)
    {
        var path = Path.Combine(_usersDirectory, fileName);
        if (!File.Exists(path)) return;
        try
        {
            var index = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path));
            if (index is null) return;
            foreach (var (key, playerId) in index) target[key] = playerId;
            _logger.LogInformation("Loaded {Count} entries from {Path}", target.Count, path);
        }
        catch (Exception ex)
        {
            // Losing the index is recoverable (a returning player gets a new row) but never silent: the whole
            // point of it is to not do that.
            _logger.LogError(ex, "Could not read the player index at {Path}; returning players will be re-created", path);
        }
    }

    private void PersistPlayerIndex()
    {
        try
        {
            var path = Path.Combine(_usersDirectory, OtomoIndexFile);
            var json = JsonSerializer.Serialize(_playerIdByOtomoPlayer, new JsonSerializerOptions { WriteIndented = true });
            // Write-then-rename: a crash mid-write must not leave a truncated index, which would silently
            // re-issue rows to every player that then connects.
            // Stores over this directory share the identity lock, while the unique suffix also avoids
            // collisions with another process writing the same index.
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not write the player index; this mapping will be lost on restart");
        }
    }

    public long ResolveGamePlayer(string otomoPlayerId, string legacyDeviceUuid)
    {
        if (string.IsNullOrWhiteSpace(otomoPlayerId)) throw new ArgumentException("An otomo player id is required.", nameof(otomoPlayerId));
        lock (_identityLock)
        {
            // Each instance has its own dictionary. Refresh under the directory-wide lock so a second
            // store cannot persist an older snapshot and erase entries just added by the first.
            LoadPlayerIndex();
            if (_playerIdByOtomoPlayer.TryGetValue(otomoPlayerId, out var existing)) return existing;

            // The device this adapter used to key on: if a row was created for it, that row becomes this
            // account's row, so the player keeps their name, rank, inventory and friends.
            long playerId;
            if (!string.IsNullOrEmpty(legacyDeviceUuid) &&
                _legacyPlayerIdByDevice.TryGetValue(legacyDeviceUuid, out var adopted))
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
            PersistPlayerIndex();
            return playerId;
        }
    }

    // One past the highest id in use, so deleting a save file cannot make the next device inherit its id.
    private long NextPlayerId()
    {
        lock (IdLock)
        {
            long highest = Math.Max(_idHighWaterMark, long.Parse(FirstPlayerId));
            foreach (var candidate in _playerIdByOtomoPlayer.Values.Concat(_legacyPlayerIdByDevice.Values))
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
            loaded.Decks ??= new Dictionary<int, List<int>>();
            return loaded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error loading user state for {PlayerId}: {Error}", playerId, ex.Message);
            return null;
        }
    }

    public void Save(SessionState state)
    {
        if (state.PlayerId == 0)
        {
            _logger.LogWarning("Refusing to save a state whose UserId {UserId} is not a player id", state.UserId);
            return;
        }
        try
        {
            var file = Path.Combine(_usersDirectory, $"{state.PlayerId}.json");
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(file, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error saving user state for {PlayerId}: {Error}", state.PlayerId, ex.Message);
        }
    }

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

    public IReadOnlyList<long> FindFollowedIds(long followerId, IReadOnlyCollection<long> candidateIds)
    {
        ArgumentNullException.ThrowIfNull(candidateIds);
        if (candidateIds.Count == 0) return Array.Empty<long>();
        var candidates = candidateIds.ToHashSet();
        lock (_socialLock)
        {
            var document = LoadSocialStateLocked();
            return document.Follows
                .Where(edge => edge.FollowerId == followerId && candidates.Contains(edge.FollowedId))
                .Select(edge => edge.FollowedId)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
        }
    }

    public void AddFollow(long followerId, long followedId)
    {
        ValidateFollowIds(followerId, followedId);
        lock (_socialLock)
        {
            EnsureProfileExists(followerId);
            EnsureProfileExists(followedId);
            var document = LoadSocialStateLocked();
            if (document.Follows.Any(edge => edge.FollowerId == followerId && edge.FollowedId == followedId)) return;
            document.Follows.Add(new FollowEdge(followerId, followedId, DateTimeOffset.UtcNow, false));
            PersistSocialStateLocked(document);
        }
    }

    public void RemoveFollow(long followerId, long followedId)
    {
        ValidateFollowIds(followerId, followedId);
        lock (_socialLock)
        {
            var document = LoadSocialStateLocked();
            var removed = document.Follows.RemoveAll(edge => edge.FollowerId == followerId && edge.FollowedId == followedId);
            if (removed > 0) PersistSocialStateLocked(document);
        }
    }

    public int CountFollowing(long playerId)
    {
        lock (_socialLock)
            return LoadSocialStateLocked().Follows.Count(edge => edge.FollowerId == playerId);
    }

    public int CountFollowers(long playerId)
    {
        lock (_socialLock)
            return LoadSocialStateLocked().Follows.Count(edge => edge.FollowedId == playerId);
    }

    public IReadOnlyList<FollowedProfile> ListFollowing(long playerId, int offset, int limit)
    {
        ValidatePage(offset, limit);
        lock (_socialLock)
        {
            var rows = LoadSocialStateLocked().Follows
                .Where(edge => edge.FollowerId == playerId)
                .OrderByDescending(edge => edge.CreatedAt)
                .ThenBy(edge => edge.FollowedId)
                .Select(edge => ToFollowedProfile(edge, edge.FollowedId, isNew: false))
                .Where(row => row is not null)
                .Select(row => row!)
                .Skip(offset)
                .Take(limit)
                .ToArray();
            return rows;
        }
    }

    public IReadOnlyList<FollowedProfile> ListFollowers(long playerId, int offset, int limit)
    {
        ValidatePage(offset, limit);
        lock (_socialLock)
        {
            var rows = LoadSocialStateLocked().Follows
                .Where(edge => edge.FollowedId == playerId)
                .OrderByDescending(edge => edge.CreatedAt)
                .ThenBy(edge => edge.FollowerId)
                .Select(edge => ToFollowedProfile(edge, edge.FollowerId, isNew: !edge.IsRead))
                .Where(row => row is not null)
                .Select(row => row!)
                .Skip(offset)
                .Take(limit)
                .ToArray();
            return rows;
        }
    }

    public int CountNewFollowers(long playerId)
    {
        lock (_socialLock)
            return LoadSocialStateLocked().Follows.Count(edge => edge.FollowedId == playerId && !edge.IsRead);
    }

    public void MarkFollowersRead(long playerId, IReadOnlyCollection<long> followerIds)
    {
        ArgumentNullException.ThrowIfNull(followerIds);
        if (followerIds.Count == 0) return;
        var followers = followerIds.ToHashSet();
        lock (_socialLock)
        {
            var document = LoadSocialStateLocked();
            var changed = false;
            foreach (var edge in document.Follows.Where(edge => edge.FollowedId == playerId && followers.Contains(edge.FollowerId)))
            {
                if (edge.IsRead) continue;
                edge.IsRead = true;
                changed = true;
            }
            if (changed) PersistSocialStateLocked(document);
        }
    }

    public string GetOrCreateRealFriendToken(long playerId)
    {
        if (playerId <= 0) throw new ArgumentOutOfRangeException(nameof(playerId));
        lock (_socialLock)
        {
            EnsureProfileExists(playerId);
            var document = LoadSocialStateLocked();
            var existing = document.Tokens.FirstOrDefault(row => row.PlayerId == playerId);
            if (existing is not null) return existing.Token;

            string token;
            do token = CreateOpaqueToken();
            while (document.Tokens.Any(row => string.Equals(row.Token, token, StringComparison.Ordinal)));
            document.Tokens.Add(new RealFriendToken(playerId, token));
            PersistSocialStateLocked(document);
            return token;
        }
    }

    public long? TryGetRealFriendTokenOwner(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        lock (_socialLock)
        {
            return LoadSocialStateLocked().Tokens
                .FirstOrDefault(row => string.Equals(row.Token, token, StringComparison.Ordinal))?.PlayerId;
        }
    }

    public void AddRealFriend(long firstPlayerId, long secondPlayerId)
    {
        ValidateFollowIds(firstPlayerId, secondPlayerId);
        var low = Math.Min(firstPlayerId, secondPlayerId);
        var high = Math.Max(firstPlayerId, secondPlayerId);
        lock (_socialLock)
        {
            EnsureProfileExists(low);
            EnsureProfileExists(high);
            var document = LoadSocialStateLocked();
            if (document.RealFriends.Any(pair => pair.PlayerLowId == low && pair.PlayerHighId == high)) return;
            document.RealFriends.Add(new RealFriendPair(low, high));
            PersistSocialStateLocked(document);
        }
    }

    public bool AreRealFriends(long firstPlayerId, long secondPlayerId)
    {
        ValidateFollowIds(firstPlayerId, secondPlayerId);
        var low = Math.Min(firstPlayerId, secondPlayerId);
        var high = Math.Max(firstPlayerId, secondPlayerId);
        lock (_socialLock)
        {
            return LoadSocialStateLocked().RealFriends
                .Any(pair => pair.PlayerLowId == low && pair.PlayerHighId == high);
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

    private FollowedProfile? ToFollowedProfile(FollowEdge edge, long profileId, bool isNew)
    {
        var profile = FindProfile(profileId);
        return profile is null ? null : new FollowedProfile(profile, edge.CreatedAt, isNew);
    }

    private void EnsureProfileExists(long playerId)
    {
        if (FindProfile(playerId) is null)
            throw new InvalidOperationException($"Player {playerId} has no persisted profile.");
    }

    private static PlayerProfile ProfileFromState(long playerId, SessionState state) =>
        new(playerId, state.UserName, state.KickerId, state.KickerCostumeId);

    private SocialDocument LoadSocialStateLocked()
    {
        var path = Path.Combine(_usersDirectory, SocialStateFile);
        if (!File.Exists(path)) return new SocialDocument();
        try
        {
            return JsonSerializer.Deserialize<SocialDocument>(File.ReadAllText(path)) ?? new SocialDocument();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Social state file {Path} is not valid JSON; refusing to overwrite it", path);
            throw new InvalidDataException("Social state is unreadable; refusing to reset it.", ex);
        }
    }

    private void PersistSocialStateLocked(SocialDocument document)
    {
        var path = Path.Combine(_usersDirectory, SocialStateFile);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
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

    private static string CreateOpaqueToken() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class SocialDocument
    {
        public List<FollowEdge> Follows { get; set; } = new();
        public List<RealFriendToken> Tokens { get; set; } = new();
        public List<RealFriendPair> RealFriends { get; set; } = new();
    }

    private sealed class FollowEdge(long followerId, long followedId, DateTimeOffset createdAt, bool isRead)
    {
        public long FollowerId { get; set; } = followerId;
        public long FollowedId { get; set; } = followedId;
        public DateTimeOffset CreatedAt { get; set; } = createdAt;
        public bool IsRead { get; set; } = isRead;
    }

    private sealed record RealFriendToken(long PlayerId, string Token);
    private sealed record RealFriendPair(long PlayerLowId, long PlayerHighId);

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
