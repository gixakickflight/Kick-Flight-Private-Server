namespace KickFlight.BootstrapApi.Otomo;

// Configuration for the adapter's economy client: appsettings "Otomo:Economy", or Otomo__Economy__* environment
// variables.
//
// The adapter holds no balance, no ticket and no disc. Every value the client is shown - the item counters, the
// discs it owns, the kickers it owns, the shop's prices and the gacha's banners - comes from otomo's economy
// service, called with the player's own access token. This section is only where that service lives.
public sealed class OtomoEconomyOptions
{
    public const string SectionName = "Otomo:Economy";

    // Hard switch, and off by default. With it off a request that needs a value (startup, home, the goods shop, a
    // draw) fails closed in the game's error contract: an adapter with no economy cannot answer "what do I own",
    // and answering zero would be a lie the client would believe.
    public bool Enabled { get; set; }

    // Base URL of economy's player listener, e.g. http://otomo-economy:8080. The prefix is kept
    // (/api/player/economy/...), because the gateway forwards it with the prefix intact. Trailing slashes ignored.
    public string BaseUrl { get; set; } = "";

    // Base URL of economy's internal listener, e.g. http://otomo-economy:8081. Empty means BaseUrl. Only the
    // battle-result progress report uses it, and that report is off unless ProgressEnabled is set.
    public string InternalBaseUrl { get; set; } = "";

    // Bound on one call. At most two attempts are made (see OtomoEconomy), so an unreachable economy costs two
    // timeouts and then a clear answer rather than a request that hangs.
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    // How long to wait before the single retry of a transport failure or a 5xx. A 4xx is never retried: it is an
    // answer, and repeating a refused purchase would only repeat the refusal.
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    // The battle-result progress report. It is the one call here that is not on the player plane: POST
    // /internal/economy/progress takes the game-server service key and names a player id, so it is a trust
    // decision, and it stays off unless this is true *and* a key is configured. Off, the adapter simply reports
    // nothing and /battle/result is unchanged.
    public bool ProgressEnabled { get; set; }

    // File holding the gameserver service key, read on every report so rotating the key needs no restart. Key is
    // the inline alternative, for a test or a local run; a deployment uses the file.
    public string ServiceKeyFile { get; set; } = "";
    public string ServiceKey { get; set; } = "";
}
