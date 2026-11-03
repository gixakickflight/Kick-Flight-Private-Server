namespace KickFlight.BootstrapApi.Otomo;

// Configuration for the adapter's rank client: appsettings "Otomo:Rank", or Otomo__Rank__* environment variables.
//
// Rank is otomo's now (session's /api/player/session/rank), but the adapter's own table is what every deployment
// has been reading, so the otomo path sits behind its own switch: a deployment turns it on one environment variable
// at a time, and with it off not one line of this path runs and player_ranks answers as it always has.
public sealed class OtomoRankOptions
{
    public const string SectionName = "Otomo:Rank";

    // Hard switch, and off by default. Off, every rank payload reads the adapter's store. On, this player's own
    // standings come from otomo and the adapter stops writing its table. Nothing here is half-on: the switch is read
    // once per request.
    public bool Enabled { get; set; }

    // Base URL of the otomo gateway, e.g. http://otomo-gateway:8080. Empty means Otomo:BaseUrl - the same gateway
    // identity already talks to - so enabling this path needs only Otomo__Rank__Enabled. Trailing slashes are
    // ignored. The prefix is kept (/api/player/session/rank), because the gateway forwards it intact.
    public string BaseUrl { get; set; } = "";

    // Bound on one call. At most two attempts are made (see OtomoRank), so an unreachable rank service costs two
    // timeouts and then a clear answer rather than a home screen that hangs.
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

    // How long to wait before the single retry of a transport failure or a 5xx. A 4xx is never retried: it is an
    // answer, and repeating a refused read would only repeat the refusal.
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    // Which otomo rule type each of the adapter's battle rule types reads.
    //
    // otomo names a closed set of rule types and "ranked" is the only one a deployment has until it publishes
    // session.rank with more; the KF client speaks integers 1-3 (Crystal Scramble, Flag Flight, Rapid Ball). The
    // default maps all three onto otomo's default slug, so all three rows show one standing until a deployment
    // publishes a ladder that separates them - which is what the game would do anyway, since a player has one rank
    // in otomo until it says otherwise.
    //
    // A rule type with no mapping, or one the published ladder does not name, is served at zero points in the
    // lowest tier, never from the store: one source answers, and it is otomo's.
    public Dictionary<int, string> RuleTypes { get; set; } = new()
    {
        [1] = "ranked",
        [2] = "ranked",
        [3] = "ranked"
    };
}
