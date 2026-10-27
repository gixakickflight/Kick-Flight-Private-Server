using Microsoft.Extensions.Options;

namespace KickFlight.BootstrapApi.Otomo;

// Polls otomo for the release this server follows, for the life of the process.
//
// The interval is the whole hot-reload story: one conditional request answered 304 costs a map lookup on otomo's
// side and nothing here, and a publish becomes visible on the request after the next poll. Nothing else about the
// adapter needs to know a release happened — the tables are swapped under the request path and the master version
// is recomputed, so a client that polls /download/master picks the change up on its own next launch.
//
// A failed poll is a warning and nothing more. The tables already loaded keep serving, which is the behaviour a
// game server wants from a configuration service: staleness is recoverable, a crash loop is not.
public sealed class OtomoMastersRefresher : BackgroundService
{
    private readonly OtomoMasters _masters;
    private readonly OtomoMastersOptions _options;
    private readonly ILogger<OtomoMastersRefresher> _logger;

    public OtomoMastersRefresher(OtomoMasters masters, IOptions<OtomoMastersOptions> options,
        ILogger<OtomoMastersRefresher> logger)
    {
        _masters = masters;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_masters.Enabled) return;
        if (_options.Refresh <= TimeSpan.Zero)
        {
            _logger.LogInformation("otomo masters: refresh interval is zero or negative; the release is read once at startup");
            return;
        }

        using var timer = new PeriodicTimer(_options.Refresh);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                if (await _masters.RefreshAsync(stoppingToken).ConfigureAwait(false))
                {
                    _logger.LogInformation("otomo masters: channel {Channel} moved to {Source}", _masters.Channel, _masters.Source);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // RefreshAsync already logs its own failures; this is the belt for anything it did not foresee,
                // so the loop survives it and the next tick tries again.
                _logger.LogError(exception, "otomo masters: refresh failed");
            }
        }
    }
}
