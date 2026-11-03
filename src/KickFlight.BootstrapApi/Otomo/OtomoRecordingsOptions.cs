namespace KickFlight.BootstrapApi.Otomo;

// Configuration for the adapter's patch (recordings) client: appsettings "Otomo:Recordings", or
// Otomo__Recordings__* environment variables.
//
// The adapter holds no replay: no blob, no index row, no featured rotation, no retention clock. A recording is
// uploaded to otomo's patch service with the game-server key, listed by tag and by the published curated list with
// the player's own access token, and fetched by that token too. This section is only where patch lives and what the
// client-facing parts of the translation are called.
public sealed class OtomoRecordingsOptions
{
    public const string SectionName = "Otomo:Recordings";

    // Hard switch, and off by default. With it off an upload is refused (the master client is told, and nothing is
    // kept here to retry from), and the replay index and play fail closed in the game's error contract (503): an
    // adapter with no recording service cannot say which replays exist, and an empty list would be a lie.
    public bool Enabled { get; set; }

    // Base URL of patch's player listener as the gateway publishes it, e.g. http://otomo-gateway:8080. The prefix is
    // kept (/patch/v1/recordings...), because the gateway forwards it intact. Used for the list and the blob, both on
    // the player's own token.
    public string BaseUrl { get; set; } = "";

    // Base URL of patch's internal listener (PATCH_INTERNAL_ADDR), e.g. http://otomo-patch:8081. Used for the upload,
    // PUT /internal/patch/recording/{id}, with the game-server key. No gateway routes there, so there is no sensible
    // default: empty means BaseUrl, which only works in a layout that exposes both on one address.
    public string InternalBaseUrl { get; set; } = "";

    // Bound on one list or probe call. At most two attempts are made (see OtomoRecordings).
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    // Bound on one upload or one blob download, which move up to MaxUploadBytes.
    public TimeSpan TransferTimeout { get; set; } = TimeSpan.FromSeconds(60);

    // How long to wait before the single retry of a transport failure or a 5xx. A 4xx is never retried.
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    // File holding patch_gameserver.key, read on every upload so rotating the key needs no restart. ServiceKey is the
    // inline alternative, for a test or a local run. Without one of them uploads are Disabled.
    public string ServiceKeyFile { get; set; } = "";
    public string ServiceKey { get; set; } = "";

    // The curated list the type-3 Featured row is read from (a name in the published patch.curated document). Patch
    // answers 404 for a name nobody has published, and the adapter serves an empty Featured row for it.
    public string FeaturedList { get; set; } = "featured";

    // How many recordings one kicker channel shows (patch caps a page at 200).
    public int KickerChannelSize { get; set; } = 20;

    // Input guards on what the client posts to /battle/upload. They bound this process's memory, not a stored
    // quota: patch's own cap (PATCH_RECORDING_MAX_BYTES, 64 MiB by default) is the stored one.
    public long MaxUploadBytes { get; set; } = 64L * 1024 * 1024;
    public long MaxDecompressedBytes { get; set; } = 256L * 1024 * 1024;

    // How long the URL and key /battleReplay/play hands out stay valid.
    public TimeSpan PlayLifetime { get; set; } = TimeSpan.FromMinutes(10);

    // Secret the play URL is sealed with. Empty means Otomo:TokenSecret, and with neither a key generated at startup:
    // a play URL then stops working at a restart, which costs a player one more tap.
    public string PlaySecret { get; set; } = "";
}
