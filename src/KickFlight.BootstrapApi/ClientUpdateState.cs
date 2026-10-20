using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KickFlight.BootstrapApi;

public sealed record ClientUpdateSnapshot(string MinimumVersion, DateTime ChangedAtUtc);

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
    private ClientUpdateSnapshot _current;

    public ClientUpdateState(ILogger<ClientUpdateState> logger, IWebHostEnvironment environment, IConfiguration configuration)
        : this(logger, configuration["ClientUpdate:FilePath"] is { Length: > 0 } configured
            ? configured
            : Path.Combine(environment.ContentRootPath, "data", "client-update.json"),
            configuration["ClientUpdate:MinimumVersion"])
    {
    }

    public ClientUpdateState(ILogger<ClientUpdateState> logger, string filePath, string? initialMinimumVersion = null)
    {
        _logger = logger;
        FilePath = Path.GetFullPath(filePath);
        _current = new ClientUpdateSnapshot(Normalize(initialMinimumVersion), DateTime.UtcNow);
        Load();
    }

    public string FilePath { get; }

    /// <summary>Immutable snapshot; read it once per request so the comparison uses a consistent minimum.</summary>
    public ClientUpdateSnapshot Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>Sets (or with an empty value clears) the minimum version and persists it.</summary>
    public ClientUpdateSnapshot Set(string? minimumVersion)
    {
        ClientUpdateSnapshot next;
        lock (_lock)
        {
            next = new ClientUpdateSnapshot(Normalize(minimumVersion), DateTime.UtcNow);
            _current = next;
            Save(next);
        }

        _logger.LogInformation("Client minimum version {MinimumVersion}", next.MinimumVersion.Length == 0 ? "cleared" : $"set to {next.MinimumVersion}");
        return next;
    }

    /// <summary>
    /// True when a minimum is configured and the client is older. A missing or unparsable header counts as old, which
    /// is what catches clients too ancient to send it at all.
    /// </summary>
    public bool IsOutdated(string? clientVersion)
    {
        var minimum = Current.MinimumVersion;
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
        var match = Regex.Match(value.Trim(), @"^\d+(\.\d+)*");
        if (!match.Success || !Version.TryParse(match.Value, out var parsed)) return false;
        version = parsed;
        return true;
    }

    private void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<ClientUpdateSnapshot>(File.ReadAllText(FilePath), FileJson);
            if (loaded is null) return;
            _current = loaded with { MinimumVersion = loaded.MinimumVersion ?? "" };
            _logger.LogInformation("Loaded client minimum version {MinimumVersion} from {Path}",
                _current.MinimumVersion.Length == 0 ? "(off)" : _current.MinimumVersion, FilePath);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not read client update state {Path}, starting with the configured value: {Error}", FilePath, ex.Message);
        }
    }

    private void Save(ClientUpdateSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Write then rename, so a crash mid-write cannot leave a truncated file that loads as off.
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, FileJson));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The switch still applies to this process; it only would not survive a restart.
            _logger.LogWarning("Could not persist client update state to {Path}: {Error}", FilePath, ex.Message);
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
        MaintenanceAdmin.IsLocalAdmin(context) ? Status(clientUpdate.Current) : Results.NotFound();

    public static async Task<IResult> PostAsync(HttpContext context, ClientUpdateState clientUpdate)
    {
        if (!MaintenanceAdmin.IsLocalAdmin(context)) return Results.NotFound();

        string? minimumVersion = null;
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("minimumVersion", out var property))
            {
                return Results.BadRequest(new { error = "body must be JSON {minimumVersion}; an empty string clears it" });
            }

            if (property.ValueKind == JsonValueKind.Null) minimumVersion = "";
            else if (property.ValueKind == JsonValueKind.String) minimumVersion = property.GetString();
            else return Results.BadRequest(new { error = "minimumVersion must be a string such as 2.12.0" });
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = "body must be JSON {minimumVersion}", detail = ex.Message });
        }

        if (!ClientUpdateState.IsValidMinimumVersion(minimumVersion))
        {
            return Results.BadRequest(new { error = "minimumVersion must be a dotted version such as 2.12.0" });
        }

        return Status(clientUpdate.Set(minimumVersion));
    }

    private static IResult Status(ClientUpdateSnapshot current) => Results.Json(new
    {
        minimumVersion = current.MinimumVersion,
        changedAtUtc = current.ChangedAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
    });
}
