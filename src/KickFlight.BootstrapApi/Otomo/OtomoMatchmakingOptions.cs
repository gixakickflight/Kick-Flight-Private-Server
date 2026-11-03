namespace KickFlight.BootstrapApi.Otomo;

// Configuration for the adapter's matchmaking client: appsettings "Otomo:Matchmaking", or Otomo__Matchmaking__*
// environment variables.
//
// Matchmaking is the one part of the player's path that is still this adapter's own room, so the otomo path sits
// behind its own switch, separate from identity and the economy: a deployment turns it on one environment variable
// at a time, and with it off nothing in this directory is called and the in-memory room behaves exactly as before.
public sealed class OtomoMatchmakingOptions
{
    public const string SectionName = "Otomo:Matchmaking";

    // Hard switch, and off by default. Off, /battle/entry issues a local ticket the way it always has and
    // GetAssignments fills an in-memory room with bots; on, the party is queued at otomo and the assignment comes
    // from the matchmaker's answer instead. Nothing here is half-on: the switch is read once per request/stream.
    public bool Enabled { get; set; }

    // Base URL of the otomo gateway, e.g. http://otomo-gateway:8080. Empty means Otomo:BaseUrl - the same gateway
    // identity already talks to - so enabling this path needs only Otomo__Matchmaking__Enabled. Trailing slashes are
    // ignored. The prefix is kept (/api/player/...), because the gateway forwards it with the prefix intact.
    public string BaseUrl { get; set; } = "";

    // Bound on one call. At most two attempts are made (see OtomoMatchmaking), so an unreachable matchmaker costs
    // two timeouts and then a clear answer rather than a request that hangs.
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    // How long to wait before the single retry of a transport failure or a 5xx. A 4xx is never retried: it is an
    // answer, and repeating a cancelled queue entry would only repeat the answer.
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    // How often an open GetAssignments stream asks for the party's state while it waits. A few hundred milliseconds:
    // fast enough that a match made during the client's countdown is picked up before it runs out, slow enough that a
    // waiting client is nothing like a poll loop against the gateway.
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(400);
}
