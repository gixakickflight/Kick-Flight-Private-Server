using System.Collections.Concurrent;
using System.Text.Json;

namespace KickFlight.BootstrapApi;

/// <summary>
/// Optional per-table override directory for the <c>config/masters_*.json</c> files, so the balance WebUI can
/// persist tuned values outside the checked-in config tree (which the CI deploy overwrites). A file in this
/// directory with the same name as a base master replaces that master entirely; when it is absent the base
/// file, and then the inline fallback, are used as before.
///
/// The directory comes from configuration (<c>Masters:OverrideDir</c> / environment
/// <c>Masters__OverrideDir</c>) and defaults to <c>.local/masters-overrides</c>, resolved against the
/// repository root like every other repo-relative path.
/// </summary>
public sealed class MasterOverrides
{
    public const string ConfigurationKey = "Masters:OverrideDir";
    public const string DefaultRelativeDir = ".local/masters-overrides";

    // LoadJson is called from the singleton's constructor and, for CustomBattleRuleField, once per request;
    // the dictionary keeps the "which were overridden" bookkeeping safe under concurrency.
    private readonly ConcurrentDictionary<string, byte> _applied = new(StringComparer.Ordinal);

    public MasterOverrides(string? configuredDir, string contentRoot)
    {
        Directory = RepositoryPaths.Resolve(
            string.IsNullOrWhiteSpace(configuredDir) ? DefaultRelativeDir : configuredDir, contentRoot);
    }

    /// <summary>Absolute override directory (it need not exist).</summary>
    public string Directory { get; }

    /// <summary>Names of the master files served from the override directory, sorted for a stable log.</summary>
    public IReadOnlyList<string> Applied =>
        _applied.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList();

    /// <summary>The override file that replaces a repository-relative master path, or null when there is none.</summary>
    public string? Find(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        if (!fileName.StartsWith("masters_", StringComparison.Ordinal) ||
            !fileName.EndsWith(".json", StringComparison.Ordinal))
        {
            return null;
        }

        var candidate = Path.Combine(Directory, fileName);
        if (!File.Exists(candidate)) return null;
        _applied.TryAdd(fileName, 0);
        return candidate;
    }

    /// <summary>Override file when present, else the base file, else the inline fallback.</summary>
    public string Read(string contentRoot, string relativePath, string fallback)
    {
        var overridePath = Find(relativePath);
        if (overridePath is not null) return File.ReadAllText(overridePath);

        var resolved = RepositoryPaths.Resolve(relativePath, contentRoot);
        return File.Exists(resolved) ? File.ReadAllText(resolved) : fallback;
    }

    /// <summary>
    /// How an override file differs from the repository's <c>config/</c> file of the same name, for the startup
    /// warning. Rows are matched by <c>id</c> (tables without a unique id are compared as row sets).
    /// </summary>
    public sealed record Divergence(string FileName, int Modified, int OnlyInOverride, int OnlyInConfig, string? Note)
    {
        public int DifferingRows => Modified + OnlyInOverride + OnlyInConfig;
    }

    /// <summary>
    /// Every override file whose rows differ from the checked-in config file, sorted by file name. Never throws:
    /// an unreadable or malformed file is reported through <see cref="Divergence.Note"/>.
    /// </summary>
    public IReadOnlyList<Divergence> DescribeDivergences(string contentRoot)
    {
        var result = new List<Divergence>();
        if (!System.IO.Directory.Exists(Directory)) return result;

        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "masters_*.json")
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(path);
            if (!fileName.EndsWith(".json", StringComparison.Ordinal)) continue;
            var basePath = RepositoryPaths.Resolve("config/" + fileName, contentRoot);
            if (!File.Exists(basePath))
            {
                result.Add(new Divergence(fileName, 0, 0, 0, "no config/ file to compare against"));
                continue;
            }

            try
            {
                using var overrideDoc = JsonDocument.Parse(File.ReadAllText(path));
                using var baseDoc = JsonDocument.Parse(File.ReadAllText(basePath));
                var divergence = Compare(fileName, baseDoc.RootElement, overrideDoc.RootElement);
                if (divergence.DifferingRows > 0 || divergence.Note is not null) result.Add(divergence);
            }
            catch (Exception error)
            {
                result.Add(new Divergence(fileName, 0, 0, 0, "could not compare: " + error.Message));
            }
        }

        return result;
    }

    internal static Divergence Compare(string fileName, JsonElement baseRoot, JsonElement overrideRoot)
    {
        if (baseRoot.ValueKind != JsonValueKind.Array || overrideRoot.ValueKind != JsonValueKind.Array)
        {
            return JsonEquals(baseRoot, overrideRoot)
                ? new Divergence(fileName, 0, 0, 0, null)
                : new Divergence(fileName, 1, 0, 0, "not a JSON array; compared as a whole");
        }

        var baseRows = baseRoot.EnumerateArray().ToList();
        var overrideRows = overrideRoot.EnumerateArray().ToList();
        var baseById = IndexById(baseRows);
        var overrideById = IndexById(overrideRows);
        if (baseById is null || overrideById is null)
        {
            // No unique "id": count rows that have no identical counterpart on the other side.
            var onlyOverride = overrideRows.Count(row => !baseRows.Any(other => JsonEquals(row, other)));
            var onlyBase = baseRows.Count(row => !overrideRows.Any(other => JsonEquals(row, other)));
            return new Divergence(fileName, 0, onlyOverride, onlyBase, "no unique id key; rows compared as sets");
        }

        var modified = 0;
        var onlyInOverride = 0;
        foreach (var (key, row) in overrideById)
        {
            if (!baseById.TryGetValue(key, out var baseRow)) onlyInOverride++;
            else if (!JsonEquals(baseRow, row)) modified++;
        }

        var onlyInConfig = baseById.Keys.Count(key => !overrideById.ContainsKey(key));
        return new Divergence(fileName, modified, onlyInOverride, onlyInConfig, null);
    }

    private static Dictionary<string, JsonElement>? IndexById(List<JsonElement> rows)
    {
        var index = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("id", out var id) ||
                id.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return null;
            if (!index.TryAdd(id.GetRawText(), row)) return null;
        }

        return index;
    }

    private static bool JsonEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;   // true and false are distinct kinds

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                var leftProps = left.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                var rightProps = right.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                return leftProps.Count == rightProps.Count &&
                       leftProps.All(p => rightProps.TryGetValue(p.Key, out var other) && JsonEquals(p.Value, other));
            case JsonValueKind.Array:
                var leftItems = left.EnumerateArray().ToList();
                var rightItems = right.EnumerateArray().ToList();
                return leftItems.Count == rightItems.Count &&
                       leftItems.Zip(rightItems).All(pair => JsonEquals(pair.First, pair.Second));
            case JsonValueKind.Number:
                return left.GetDouble() == right.GetDouble();
            case JsonValueKind.String:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);
            default:
                return true;   // true/false of the same kind, null
        }
    }
}
