namespace KickFlight.BootstrapApi;

public sealed class HarnessOptions
{
    public bool StrictMode { get; set; } = true;
    public string FixtureDirectory { get; set; } = "config/fixtures";
    public string ResourceCatalogPath { get; set; } = "config/resources/catalog.json";
    /// <summary>
    /// Optional, isolated iOS Octo resources. The immutable catalog and payload tree are staged below the existing
    /// read-only .local mount; absence keeps the iOS profile disabled rather than falling back to Android assets.
    /// </summary>
    public string IosResourceCatalogPath { get; set; } = ".local/ios-resources/12346-r5/catalog.json";
    /// <summary>Tracked, versioned manifest whose bytes must match the staged immutable iOS catalog.</summary>
    public string IosResourceManifestPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "catalog-ios-12346.json");
    public string IosBootFixturePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "boot-index-ios-platform-2.json");
    public string CaptureDirectory { get; set; } = "captures";
    public bool PersistCaptures { get; set; }
    public int MaxCapturedBodyBytes { get; set; } = 65_536;
    public string[] FirstPartyHosts { get; set; } =
    [
        "kickflight-api.grenge.jp",
        "colorful-api-octo-sb.grenge.jp",
        "kickflight-resource-api.grenge.jp"
    ];
    public string[] DirectClientHosts { get; set; } = [];

    /// <summary>
    /// Optional Octo CDN url format (e.g. "https://pub-xxxx.r2.dev/{o}") handed to direct clients instead of
    /// "{scheme}://{request host}/cdn/{o}". Lets the asset bundles live on an external static host so the initial
    /// 700 MB download does not have to squeeze through this machine's uplink.
    /// </summary>
    public string? OctoCdnUrlFormat { get; set; }

    /// <summary>
    /// Optional CDN URL formats for specific direct-client hosts. This is useful when a public TLS proxy
    /// fronts the API but the app sees its internal HTTP scheme. The host-specific format is used only when
    /// OctoCdnUrlFormat is not set; unmatched hosts keep the request-scheme fallback.
    /// </summary>
    public Dictionary<string, string> OctoCdnUrlFormatsByHost { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kick-flight-fenix.us.ci"] = "https://kick-flight-fenix.us.ci/cdn/{o}"
    };

    /// <summary>
    /// Canonical origins for master downloads when the public TLS proxy forwards requests to the API over HTTP.
    /// Unmatched hosts retain the request scheme and host, which keeps direct Android/IP clients on their HTTP origin.
    /// </summary>
    public Dictionary<string, string> MasterDownloadOriginsByHost { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kick-flight-fenix.us.ci"] = "https://kick-flight-fenix.us.ci"
    };

    /// <summary>Host-specific HTTPS origin for the separate iOS asset catalog.</summary>
    public Dictionary<string, string> IosOctoCdnUrlFormatsByHost { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kick-flight-fenix.us.ci"] = "https://kick-flight-fenix.us.ci/ios/cdn/{o}"
    };
}
