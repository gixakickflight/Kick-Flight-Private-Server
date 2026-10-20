using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KickFlight.BootstrapApi;

public sealed record ClientUpdateSnapshot(
    string MinimumVersion,
    DateTime ChangedAtUtc,
    string IosMinimumVersion = "",
    bool Enabled = true,
    bool IsManualOverride = false);

/// <summary>
/// Server-side forced client update switch, driven only by what the API answers (no client change): when a minimum
/// version is set, every POST whose x-app-application-version is older (or missing/unparsable) gets the client's
/// forced-update window as a plaintext {"error":{"code":"1400",...}} body with header x-app-status-code: 1. The
/// client compares nothing itself; it just renders its hard-coded Play Store button. Set through the loopback-only
/// /admin/client-update endpoint and persisted to &lt;content root&gt;/data/client-update.json (next to the JSON player
/// store's data/users) so an API restart keeps it. ClientUpdate:FilePath overrides the location, and
/// ClientUpdate:MinimumVersion seeds the initial value (empty = off) when no state file exists yet.
/// </summary>
public sealed class ClientUpdateState
{
    public const string DefaultTitle = "Update required";
    public const string DefaultMessage =
        "A new version of Kick Flight is available. Download it from kick-flight-fenix.us.ci and install it to keep playing.";

    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = true };

    private readonly ILogger<ClientUpdateState> _logger;
    private readonly object _lock = new();
    private readonly string _initialMinimumVersion;
    private readonly string _initialIosMinimumVersion;
    private ClientUpdateSnapshot _current;
    private bool _invalidPersistedState;

    public ClientUpdateState(ILogger<ClientUpdateState> logger, IWebHostEnvironment environment, IConfiguration configuration)
        : this(logger, configuration["ClientUpdate:FilePath"] is { Length: > 0 } configured
            ? configured
            : Path.Combine(environment.ContentRootPath, "data", "client-update.json"),
            configuration["ClientUpdate:MinimumVersion"] ?? configuration["ClientUpdate:AndroidMinimumVersion"],
            configuration["ClientUpdate:IosMinimumVersion"])
    {
    }

    public ClientUpdateState(ILogger<ClientUpdateState> logger, string filePath, string? initialMinimumVersion = null, string? initialIosMinimumVersion = null)
    {
        _logger = logger;
        FilePath = Path.GetFullPath(filePath);
        _initialMinimumVersion = Normalize(initialMinimumVersion);
        _initialIosMinimumVersion = Normalize(initialIosMinimumVersion);
        var initialEnabled = _initialMinimumVersion.Length > 0 || _initialIosMinimumVersion.Length > 0;
        _current = new ClientUpdateSnapshot(_initialMinimumVersion, DateTime.UtcNow, _initialIosMinimumVersion, initialEnabled, IsManualOverride: false);
        Load();
    }

    public string FilePath { get; }
    public string ConfiguredMinimumVersion => _initialMinimumVersion;
    public string ConfiguredIosMinimumVersion => _initialIosMinimumVersion;
    public bool IsPersistedStateValid { get { lock (_lock) return !_invalidPersistedState; } }

    /// <summary>Immutable snapshot; read it once per request so the comparison uses a consistent minimum.</summary>
    public ClientUpdateSnapshot Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>Sets (or with an empty value clears) the minimum version and persists it.</summary>
    public ClientUpdateSnapshot Set(string? minimumVersion, string? iosMinimumVersion = null, bool? enabled = null, bool isManualOverride = false)
    {
        ClientUpdateSnapshot next;
        lock (_lock)
        {
            var nextMin = minimumVersion is not null ? Normalize(minimumVersion) : _current.MinimumVersion;
            var nextIos = iosMinimumVersion is not null ? Normalize(iosMinimumVersion) : _current.IosMinimumVersion;
            var nextEnabled = enabled ?? (nextMin.Length > 0 || nextIos.Length > 0);
            next = new ClientUpdateSnapshot(nextMin, DateTime.UtcNow, nextIos, nextEnabled, isManualOverride);
            if (!Save(next)) throw new IOException($"Could not persist client update state to {FilePath}");
            _current = next;
            _invalidPersistedState = false;
        }

        _logger.LogInformation("Client minimum version {MinimumVersion} (iOS: {IosMinimumVersion}, Enabled: {Enabled}, ManualOverride: {ManualOverride})",
            next.MinimumVersion.Length == 0 ? "cleared" : $"set to {next.MinimumVersion}",
            next.IosMinimumVersion.Length == 0 ? "default" : next.IosMinimumVersion,
            next.Enabled,
            next.IsManualOverride);
        return next;
    }

    /// <summary>Resets the client update settings to deployment configuration defaults.</summary>
    public ClientUpdateSnapshot ResetToConfig()
    {
        ClientUpdateSnapshot next;
        lock (_lock)
        {
            var enabled = _initialMinimumVersion.Length > 0 || _initialIosMinimumVersion.Length > 0;
            next = new ClientUpdateSnapshot(_initialMinimumVersion, DateTime.UtcNow, _initialIosMinimumVersion, enabled, IsManualOverride: false);
            if (!Save(next)) throw new IOException($"Could not persist client update state to {FilePath}");
            _current = next;
            _invalidPersistedState = false;
        }

        _logger.LogInformation("Reset client update state to deployment config (Android: {MinimumVersion}, iOS: {IosMinimumVersion}, Enabled: {Enabled})",
            next.MinimumVersion.Length == 0 ? "(off)" : next.MinimumVersion,
            next.IosMinimumVersion.Length == 0 ? "(default)" : next.IosMinimumVersion,
            next.Enabled);
        return next;
    }

    /// <summary>
    /// True when a minimum is configured, the gate is enabled, and the client is older. A missing or unparsable header
    /// counts as old, which is what catches clients too ancient to send it at all.
    /// </summary>
    public bool IsOutdated(string? clientVersion, bool isIos = false)
    {
        var current = Current;
        // Unknown persisted settings must never silently disable an update gate. An operator can recover by saving
        // valid settings or explicitly resetting to deployment defaults through the loopback admin endpoint.
        if (!IsPersistedStateValid) return true;
        if (!current.Enabled) return false;
        var minimum = isIos
            ? (current.IosMinimumVersion.Length > 0 ? current.IosMinimumVersion : current.MinimumVersion)
            : current.MinimumVersion;
        if (minimum.Length == 0) return false;
        if (!TryParseVersion(minimum, out var minimumVersion)) return false;
        return !TryParseVersion(clientVersion, out var parsed) || parsed < minimumVersion;
    }

    /// <summary>Empty means off; anything else must be a dotted numeric version.</summary>
    public static bool IsValidMinimumVersion(string? value) =>
        string.IsNullOrWhiteSpace(value) || TryParseVersion(value, out _);

    /// <summary>Plaintext (not D2C-encrypted) body the client turns into its forced-update window.</summary>
    public string BuildForcedUpdateJson() =>
        // "1400" opens the forced-update window; the window's own texts come from the Translation master.
        JsonSerializer.Serialize(new { error = new { code = "1400", title = DefaultTitle, message = DefaultMessage } });

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim();

    /// <summary>
    /// The client's versionName is normally "2.11.0", but a build can append a "-suffix" / "+build" tail; keep the
    /// leading dotted numeric part and compare it with System.Version.
    /// </summary>
    internal static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value)) return false;
        var match = Regex.Match(value.Trim(), @"^(\d+(?:\.\d+)*)(?:[-+][A-Za-z0-9_.-]+)?$");
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var parsed)) return false;
        version = parsed;
        return true;
    }

    private void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<ClientUpdateSnapshot>(File.ReadAllText(FilePath), FileJson);
            if (loaded is null || !IsValidMinimumVersion(loaded.MinimumVersion) || !IsValidMinimumVersion(loaded.IosMinimumVersion))
            {
                _invalidPersistedState = true;
                _logger.LogWarning("Invalid client update state {Path}; forcing update responses until an operator saves valid settings", FilePath);
                return;
            }
            var minVer = loaded.MinimumVersion ?? "";
            var iosMinVer = loaded.IosMinimumVersion ?? "";
            var isManualOverride = loaded.IsManualOverride;
            var isEnabled = loaded.Enabled && (minVer.Length > 0 || iosMinVer.Length > 0 || isManualOverride);

            var upgraded = false;
            // Only auto-upgrade if NOT an explicit operator manual override!
            // Deliberate admin edits (disabling or lowering minimum) MUST be respected across restarts.
            if (!isManualOverride)
            {
                if (TryParseVersion(_initialMinimumVersion, out var configVer) &&
                    TryParseVersion(minVer, out var currentVer) &&
                    configVer > currentVer)
                {
                    _logger.LogInformation("Upgrading client minimum version from {Current} to {Configured} per deployment configuration",
                        minVer, _initialMinimumVersion);
                    minVer = _initialMinimumVersion;
                    isEnabled = true;
                    upgraded = true;
                }
                if (TryParseVersion(_initialIosMinimumVersion, out var configIosVer) &&
                    (iosMinVer.Length == 0 || (TryParseVersion(iosMinVer, out var currentIosVer) && configIosVer > currentIosVer)))
                {
                    _logger.LogInformation("Upgrading iOS client minimum version from {Current} to {Configured} per deployment configuration",
                        iosMinVer.Length == 0 ? "(empty)" : iosMinVer, _initialIosMinimumVersion);
                    iosMinVer = _initialIosMinimumVersion;
                    isEnabled = true;
                    upgraded = true;
                }
            }
            else
            {
                _logger.LogInformation("Preserving operator manual client update configuration across restart (Enabled: {Enabled}, Android: {Android}, iOS: {iOS})",
                    loaded.Enabled, minVer, iosMinVer);
                isEnabled = loaded.Enabled;
            }

            _current = new ClientUpdateSnapshot(minVer, loaded.ChangedAtUtc, iosMinVer, isEnabled, isManualOverride);
            if (upgraded)
            {
                Save(_current);
            }

            _logger.LogInformation("Loaded client minimum version {MinimumVersion} (iOS: {IosMinimumVersion}, Enabled: {Enabled}, ManualOverride: {ManualOverride}) from {Path}",
                _current.MinimumVersion.Length == 0 ? "(off)" : _current.MinimumVersion,
                _current.IosMinimumVersion.Length == 0 ? "(default)" : _current.IosMinimumVersion,
                _current.Enabled,
                _current.IsManualOverride,
                FilePath);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _invalidPersistedState = true;
            _logger.LogWarning("Could not read client update state {Path}; forcing update responses until an operator saves valid settings: {Error}", FilePath, ex.Message);
        }
    }

    private bool Save(ClientUpdateSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Write then rename, so a crash mid-write cannot leave a truncated file that loads as off.
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, FileJson));
            File.Move(temporary, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep the previous in-memory snapshot when persistence fails; callers must not report an unsaved change.
            _logger.LogWarning("Could not persist client update state to {Path}: {Error}", FilePath, ex.Message);
            return false;
        }
    }
}

/// <summary>
/// GET/POST /admin/client-update. Only for a caller on the API's own loopback with no proxy headers: nginx (the cdn
/// sidecar) adds X-Real-IP / X-Forwarded-For and connects from another container, so nothing from outside gets in.
/// Use it from inside the container:
///   docker exec deploy-api-1 curl -s -X POST http://127.0.0.1:8080/admin/client-update -d '{"minimumVersion":"2.12.0"}'
///   docker exec deploy-api-1 curl -s -X POST http://127.0.0.1:8080/admin/client-update -d '{"minimumVersion":""}'
/// Anyone else - and every other /admin path - gets a plain 404.
/// </summary>
public static class ClientUpdateAdmin
{
    public static IResult Get(HttpContext context, ClientUpdateState clientUpdate) =>
        MaintenanceAdmin.IsLocalAdmin(context) ? Status(clientUpdate) : Results.NotFound();

    public static async Task<IResult> PostAsync(HttpContext context, ClientUpdateState clientUpdate)
    {
        if (!MaintenanceAdmin.IsLocalAdmin(context)) return Results.NotFound();

        string? minimumVersion = null;
        string? iosMinimumVersion = null;
        bool? enabled = null;
        bool resetToDefault = false;

        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Results.BadRequest(new { error = "body must be a JSON object" });
            }

            if (root.TryGetProperty("resetToDefault", out var resetProp) &&
                (resetProp.ValueKind == JsonValueKind.True || resetProp.ValueKind == JsonValueKind.False))
            {
                resetToDefault = resetProp.GetBoolean();
            }

            if (resetToDefault)
            {
                return Status(clientUpdate.ResetToConfig(), clientUpdate);
            }

            if (root.TryGetProperty("enabled", out var enabledProp))
            {
                if (enabledProp.ValueKind == JsonValueKind.True || enabledProp.ValueKind == JsonValueKind.False)
                {
                    enabled = enabledProp.GetBoolean();
                }
                else
                {
                    return Results.BadRequest(new { error = "enabled must be a boolean" });
                }
            }

            if (root.TryGetProperty("minimumVersion", out var property))
            {
                if (property.ValueKind == JsonValueKind.Null) minimumVersion = "";
                else if (property.ValueKind == JsonValueKind.String) minimumVersion = property.GetString();
                else return Results.BadRequest(new { error = "minimumVersion must be a string such as 2.12.0" });
            }

            if (root.TryGetProperty("iosMinimumVersion", out var iosProperty))
            {
                if (iosProperty.ValueKind == JsonValueKind.Null) iosMinimumVersion = "";
                else if (iosProperty.ValueKind == JsonValueKind.String) iosMinimumVersion = iosProperty.GetString();
                else return Results.BadRequest(new { error = "iosMinimumVersion must be a string such as 2.11.3" });
            }

            if (minimumVersion is null && iosMinimumVersion is null && enabled is null)
            {
                return Results.BadRequest(new { error = "body must be JSON {minimumVersion} or contain enabled/iosMinimumVersion/resetToDefault" });
            }
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = "body must be JSON {minimumVersion}", detail = ex.Message });
        }

        if (minimumVersion is not null && !ClientUpdateState.IsValidMinimumVersion(minimumVersion))
        {
            return Results.BadRequest(new { error = "minimumVersion must be a dotted version such as 2.12.0" });
        }
        if (iosMinimumVersion is not null && !ClientUpdateState.IsValidMinimumVersion(iosMinimumVersion))
        {
            return Results.BadRequest(new { error = "iosMinimumVersion must be a dotted version such as 2.11.3" });
        }

        return Status(clientUpdate.Set(minimumVersion, iosMinimumVersion, enabled, isManualOverride: true), clientUpdate);
    }

    private static IResult Status(ClientUpdateState state) => Status(state.Current, state);

    private static IResult Status(ClientUpdateSnapshot current, ClientUpdateState state) => Results.Json(new
    {
        enabled = current.Enabled,
        minimumVersion = current.MinimumVersion,
        iosMinimumVersion = current.IosMinimumVersion,
        isManualOverride = current.IsManualOverride,
        stateValid = state.IsPersistedStateValid,
        defaultMinimumVersion = state.ConfiguredMinimumVersion,
        defaultIosMinimumVersion = state.ConfiguredIosMinimumVersion,
        changedAtUtc = current.ChangedAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
    });
}
