using KickFlight.BootstrapApi.PlayerStore;
using Microsoft.Extensions.Options;

namespace KickFlight.BootstrapApi.Push;

// Sends the "someone is searching ranked" push. Called fire-and-forget; never throws.
public sealed class RankedSearchNotifier
{
    private readonly IPlayerStore _store;
    private readonly IFcmSender _sender;
    private readonly PushTokenService _tokens;
    private readonly IOptions<PushOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RankedSearchNotifier> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<long, DateTimeOffset> _lastBySearcher = new();
    private DateTimeOffset? _lastGlobal;
    private int _disabledLogged;

    public RankedSearchNotifier(IPlayerStore store, IFcmSender sender, PushTokenService tokens,
        IOptions<PushOptions> options, TimeProvider time, ILogger<RankedSearchNotifier> logger)
    {
        _store = store;
        _sender = sender;
        _tokens = tokens;
        _options = options;
        _time = time;
        _logger = logger;
    }

    // Runs the notification on the thread pool so the caller's response is never delayed.
    public void NotifyInBackground(long searcherId, Func<long, bool> isQueued, Func<long, DateTimeOffset?> lastActivity) =>
        _ = Task.Run(() => NotifyAsync(searcherId, isQueued, lastActivity));

    // Returns the number of pushes attempted.
    public async Task<int> NotifyAsync(long searcherId, Func<long, bool> isQueued, Func<long, DateTimeOffset?> lastActivity,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var options = _options.Value;
            if (!options.Enabled || !_sender.IsConfigured)
            {
                if (Interlocked.Exchange(ref _disabledLogged, 1) == 0)
                    _logger.LogInformation("Ranked push disabled (Push:Enabled={Enabled}, credentials present={Present})",
                        options.Enabled, _sender.IsConfigured);
                return 0;
            }

            if (!CooldownsAllow(searcherId, options, claim: false)) return 0;

            var now = _time.GetUtcNow();
            var skipWindow = TimeSpan.FromSeconds(options.SkipRecentlyActiveSeconds);
            var recipients = _store.ListPushRegistrations()
                .Where(r => r.PlayerId != searcherId)
                .Where(r => !isQueued(r.PlayerId))
                .Where(r => !(lastActivity(r.PlayerId) is { } seen && now - seen < skipWindow))
                .ToList();
            if (recipients.Count == 0) return 0;

            if (!CooldownsAllow(searcherId, options, claim: true)) return 0;

            using var gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentSends));
            var sent = 0;
            await Task.WhenAll(recipients.Select(async recipient =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var outcome = await _sender.SendAsync(recipient.Token, cancellationToken);
                    if (outcome == FcmSendOutcome.Sent) Interlocked.Increment(ref sent);
                    else if (outcome == FcmSendOutcome.InvalidToken) _tokens.ClearIfToken(recipient.PlayerId, recipient.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Ranked push to player {PlayerId} ({Token}) failed: {Error}",
                        recipient.PlayerId, PushTokenService.Mask(recipient.Token), ex.GetType().Name);
                }
                finally
                {
                    gate.Release();
                }
            }));
            _logger.LogInformation("Ranked search push: {Sent}/{Total} delivered to FCM", sent, recipients.Count);
            return recipients.Count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Ranked search push failed: {Error}", ex.GetType().Name);
            return 0;
        }
    }

    private bool CooldownsAllow(long searcherId, PushOptions options, bool claim)
    {
        var now = _time.GetUtcNow();
        var global = TimeSpan.FromSeconds(options.GlobalCooldownSeconds);
        var perPlayer = TimeSpan.FromSeconds(options.PerPlayerCooldownSeconds);
        lock (_lock)
        {
            if (_lastGlobal is { } g && now - g < global) return false;
            if (_lastBySearcher.TryGetValue(searcherId, out var p) && now - p < perPlayer) return false;
            if (!claim) return true;
            _lastGlobal = now;
            _lastBySearcher[searcherId] = now;
            foreach (var stale in _lastBySearcher.Where(kv => now - kv.Value >= perPlayer).Select(kv => kv.Key).ToList())
                _lastBySearcher.Remove(stale);
            return true;
        }
    }
}
