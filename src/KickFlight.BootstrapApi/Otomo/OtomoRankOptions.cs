namespace KickFlight.BootstrapApi.Otomo;

// Configuration for the adapter's session (rank) client: appsettings "Otomo:Rank", or Otomo__Rank__* environment
// variables.
//
// The adapter holds no battle points, no tier and no ladder. A player's standing is read from session, a battle's
// result is an award session applies, and the tier thresholds come from the published session.rank document
// (Otomo:Masters:Documents). This section is only where session lives and how this server proves who it is.
public sealed class OtomoRankOptions
{
    public const string SectionName = "Otomo:Rank";

    // Hard switch, and off by default. With it off every screen and result that needs a standing fails closed in the
    // game's error contract (503): an adapter with no rank service cannot say what a player's points are, and a
    // battle result it cannot award must not be answered as if it had been.
    public bool Enabled { get; set; }

    // Base URL of session's player listener, for the caller's own standing (GET /api/player/session/rank), which is
    // read with the player's own access token. Empty means Otomo:Social:BaseUrl: it is the same listener.
    public string BaseUrl { get; set; } = "";

    // Base URL of session's internal listener, e.g. http://otomo-session:8081, for the award and for another player's
    // standing. Empty means the player-plane base. Both take the game-server service key (session_matchmaker).
    public string InternalBaseUrl { get; set; } = "";

    // Bound on one call. At most two attempts are made (see OtomoRank), so an unreachable session costs two timeouts
    // and then a clear answer rather than a request that hangs.
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    // How long to wait before the single retry of a transport failure or a 5xx. A 4xx is never retried.
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    // File holding the service key, read on every internal call so rotating the key needs no restart. ServiceKey is
    // the inline alternative, for a test or a local run; a deployment uses the file. Without one of them the internal
    // plane is off, which means no award can be made and no other player's standing can be read.
    public string ServiceKeyFile { get; set; } = "";
    public string ServiceKey { get; set; } = "";
}
