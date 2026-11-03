using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using KickFlight.BootstrapApi.PlayerStore;

namespace KickFlight.BootstrapApi.Push;

public enum PushRegisterResult { Ok, InvalidInput, UnknownUser }

// Owns reads/writes of SessionState.PushToken. DemoSessionApi caches a SessionState per session token and saves
// the whole document, which would overwrite a token written only to the store; LiveStates lets this service
// apply every change to those cached instances as well.
public sealed partial class PushTokenService
{
    public const int MaxTokenLength = 4096;
    private readonly IPlayerStore _store;
    private readonly TimeProvider _time;
    private readonly object _lock = new();

    public PushTokenService(IPlayerStore store, TimeProvider time)
    {
        _store = store;
        _time = time;
    }

    // Set by DemoSessionApi: the in-memory states of a player's live sessions.
    public Func<long, IEnumerable<SessionState>>? LiveStates { get; set; }

    [GeneratedRegex(@"^[A-Za-z0-9_\-:.=]+$")]
    private static partial Regex TokenCharset();

    public static bool IsValidToken(string? token) =>
        !string.IsNullOrEmpty(token) && token.Length <= MaxTokenLength && TokenCharset().IsMatch(token);

    public static string Mask(string? token) =>
        string.IsNullOrEmpty(token) ? "" : "..." + token[^Math.Min(6, token.Length)..];

    public PushRegisterResult Register(long playerId, string? token, string? platform)
    {
        if (!IsValidToken(token)) return PushRegisterResult.InvalidInput;
        platform = string.IsNullOrWhiteSpace(platform) ? "android" : platform.Trim().ToLowerInvariant();
        if (platform is not ("android" or "ios")) return PushRegisterResult.InvalidInput;

        lock (_lock)
        {
            if (_store.TryLoad(playerId) is null) return PushRegisterResult.UnknownUser;
            foreach (var other in _store.ListPushRegistrations())
            {
                if (other.PlayerId != playerId && other.Token == token) Mutate(other.PlayerId, s => Clear(s));
            }
            var now = _time.GetUtcNow().UtcDateTime;
            Mutate(playerId, s =>
            {
                s.PushToken = token;
                s.PushTokenUpdatedAtUtc = now;
                s.PushPlatform = platform;
            });
        }
        return PushRegisterResult.Ok;
    }

    // Clears the player's token, but only if it is still the one that failed.
    public void ClearIfToken(long playerId, string token)
    {
        lock (_lock)
        {
            if (_store.TryLoad(playerId)?.PushToken != token) return;
            Mutate(playerId, s => Clear(s));
        }
    }

    private static void Clear(SessionState s)
    {
        s.PushToken = null;
        s.PushTokenUpdatedAtUtc = null;
        s.PushPlatform = null;
    }

    private void Mutate(long playerId, Action<SessionState> change)
    {
        var state = _store.TryLoad(playerId);
        if (state is null) return;
        change(state);
        _store.Save(state);
        foreach (var live in LiveStates?.Invoke(playerId) ?? [])
        {
            if (!ReferenceEquals(live, state)) change(live);
        }
    }
}

// Per-client-IP fixed-window throttle for the unauthenticated /push/register route.
public sealed class PushRegisterThrottle
{
    private readonly TimeProvider _time;
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Start, int Count)> _hits = new();

    public PushRegisterThrottle(TimeProvider time, int limit = 10, TimeSpan? window = null)
    {
        _time = time;
        _limit = limit;
        _window = window ?? TimeSpan.FromMinutes(1);
    }

    public bool TryAcquire(string key)
    {
        var now = _time.GetUtcNow();
        if (_hits.Count > 10_000)
        {
            foreach (var (k, v) in _hits)
                if (now - v.Start >= _window) _hits.TryRemove(k, out _);
        }
        var allowed = true;
        _hits.AddOrUpdate(key, _ => (now, 1), (_, current) =>
        {
            if (now - current.Start >= _window) return (now, 1);
            if (current.Count >= _limit) { allowed = false; return current; }
            return (current.Start, current.Count + 1);
        });
        return allowed;
    }
}
