namespace KickFlight.BootstrapApi.Otomo;

// Configuration for the adapter's otomo client: appsettings "Otomo" section, or Otomo__* environment variables.
//
// Identity belongs to otomo. This adapter holds no accounts, no passwords and no session table; the only state it
// keeps is a per-device token cache in memory (see OtomoIdentity).
public sealed class OtomoOptions
{
    public const string SectionName = "Otomo";

    // Hard switch, and off by default. With it off the stack still boots - masters, fixtures, health, the CDN and
    // the boot telemetry need no identity - but a player request fails closed with the game's error envelope
    // instead of being served under an identity this server cannot vouch for.
    public bool Enabled { get; set; }

    // Base URL of otomo's auth service, e.g. http://otomo-auth:8080. Trailing slashes are ignored.
    public string BaseUrl { get; set; } = "";

    // Bound on one call to otomo, login and refresh alike. There is no retry: an otomo that is down costs one
    // request one timeout and a clear log line, not a retry storm against a service that is already struggling.
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    // An access token this close to its expiry is refreshed before use, so a request never travels with a token
    // that dies in flight.
    public TimeSpan RefreshSkew { get; set; } = TimeSpan.FromSeconds(30);

    // Device sessions held in memory. The bound is what keeps a busy server from growing without limit; idle
    // entries are dropped first, and only then the least recently used.
    public int CacheCapacity { get; set; } = 4096;

    // A device session unused for this long is dropped from the cache. The device logs in again on its next
    // request, which otomo answers with the same account and a fresh token family.
    public TimeSpan CacheIdle { get; set; } = TimeSpan.FromMinutes(30);

    // HMAC key for the access token this adapter hands the KF client (KfAccessTokenCodec). Empty means a random
    // key per process: correct, but every restart then invalidates the tokens clients still hold. Set it in a
    // deployment that restarts.
    public string TokenSecret { get; set; } = "";
}
