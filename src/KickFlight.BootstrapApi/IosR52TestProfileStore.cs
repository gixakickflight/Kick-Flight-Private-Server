using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace KickFlight.BootstrapApi;

public sealed record IosR52ListDatabase(
    string Id,
    byte[] Body,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// Loads the offline, explicitly test-only R52 profile from an isolated read-only staging directory.
/// The normal native iOS catalog remains handled by <see cref="ResourceCatalogStore"/>.
/// </summary>
public sealed class IosR52TestProfileStore
{
    public const int Revision = 52;
    public const int PreviousRevision = 51;
    public const int AssetVersion = 12346;
    public const int ExpectedResourceRoutes = 3389;
    public const int ExpectedUpdateRows = 3719;
    public const string CanonicalHost = "kick-flight-fenix.us.ci";
    public const string CdnPrefix = "/ios/cdn/";
    public const string CdnUrlFormat = "https://kick-flight-fenix.us.ci/ios/cdn/{o}";
    private const string ReportSchema = "r56.bootstrapapi.offline-profile.r52-direct-review.v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly HarnessOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ResourceCatalogStore _resourceCatalogStore;
    private readonly ILogger<IosR52TestProfileStore> _logger;
    private readonly object _gate = new();
    private IosR52ProfileState? _state;
    private string? _fingerprint;

    public IosR52TestProfileStore(
        IOptions<HarnessOptions> options,
        IWebHostEnvironment environment,
        ResourceCatalogStore resourceCatalogStore,
        ILogger<IosR52TestProfileStore> logger)
    {
        _options = options.Value;
        _environment = environment;
        _resourceCatalogStore = resourceCatalogStore;
        _logger = logger;
    }

    public bool Enabled => _options.EnableIosR52TestProfile;

    public bool IsReady
    {
        get
        {
            if (!Enabled) return false;
            var snapshot = GetState();
            return snapshot.Catalog.Present && snapshot.Catalog.Errors.Count == 0 &&
                   snapshot.Catalog.Resources.Count == ExpectedResourceRoutes &&
                   snapshot.ListDatabases.Count == Revision + 1;
        }
    }

    public ResourceCatalogSnapshot GetSnapshot() => Enabled
        ? GetState().Catalog
        : new ResourceCatalogSnapshot([], [], Present: false);

    public IosR52ListDatabase? GetListDatabase(int fromRevision)
    {
        if (!Enabled || fromRevision is < 0 or > Revision) return null;
        return GetState().ListDatabases.GetValueOrDefault(fromRevision);
    }

    public ResolvedResource? MatchDirect(string path)
    {
        if (!Enabled) return null;
        var normalizedPath = FixtureStore.NormalizePath(path);
        var matches = GetState().Catalog.Resources.Where(resource =>
                resource.Definition.Enabled &&
                string.Equals(FixtureStore.NormalizePath(resource.Definition.RequestPath), normalizedPath, StringComparison.Ordinal))
            .Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private IosR52ProfileState GetState()
    {
        var root = GetProfileRoot();
        var fingerprint = Fingerprint(root);
        if (_state is not null && fingerprint == _fingerprint) return _state;

        lock (_gate)
        {
            if (_state is not null && fingerprint == _fingerprint) return _state;
            _state = Load(root);
            _fingerprint = fingerprint;
            _logger.LogInformation("Loaded optional R52 iOS test profile: ready={Ready}, resources={ResourceCount}, errors={ErrorCount}",
                _state.Catalog.Present && _state.Catalog.Errors.Count == 0 && _state.ListDatabases.Count == Revision + 1,
                _state.Catalog.Resources.Count,
                _state.Catalog.Errors.Count);
            return _state;
        }
    }

    private string GetProfileRoot() => Path.GetFullPath(
        RepositoryPaths.Resolve(_options.IosR52TestProfilePath, _environment.ContentRootPath));

    private IosR52ProfileState Load(string root)
    {
        if (!Directory.Exists(root))
            return Failed(present: false, "R52 test profile directory is missing.");
        try
        {
            var reportPath = ResolveProfileFile(root, "profile-report.json");
            var catalogPath = ResolveProfileFile(root, "catalog.json");
            var titlePath = ResolveProfileFile(root, "title-minimum.json");
            var fixtureDirectory = ResolveProfileDirectory(root, "fixtures");

            var report = ReadJson<OfflineProfileReport>(reportPath, "profile report");
            ValidateReport(report);
            ValidateFileHash(catalogPath, report.PackageCatalogSha256, "catalog");
            ValidateFileHash(titlePath, report.TitleMinimumSha256, "title minimum");
            ValidateTitleMinimum(titlePath);

            var document = ReadJson<ResourceCatalogDocument>(catalogPath, "resource catalog");
            if (document.SchemaVersion != 1 || document.Resources.Count != ExpectedResourceRoutes)
                throw new InvalidDataException("R52 catalog schema or route count is invalid.");

            var resources = LoadResources(root, document.Resources, report.PayloadBytes);
            var lists = LoadListDatabases(fixtureDirectory, report);
            var snapshot = new ResourceCatalogSnapshot(resources, [], Platform: 9, AssetVersion, Revision, Present: true);
            return new IosR52ProfileState(snapshot, lists);
        }
        catch (Exception exception)
        {
            var error = $"R52 test profile validation failed ({exception.GetType().Name}).";
            return Failed(present: true, error);
        }
    }

    private List<ResolvedResource> LoadResources(
        string root,
        IReadOnlyList<ResourceDefinition> definitions,
        long expectedPayloadBytes)
    {
        var errors = new List<string>();
        var resources = new List<ResolvedResource>(definitions.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var routes = new HashSet<string>(StringComparer.Ordinal);
        var sourcePaths = new HashSet<string>(StringComparer.Ordinal);
        long payloadBytes = 0;

        foreach (var definition in definitions)
        {
            try
            {
                if (!definition.Enabled ||
                    !string.Equals(FixtureStore.NormalizeHost(definition.Host), CanonicalHost, StringComparison.Ordinal) ||
                    !IsSafeCdnPath(definition.RequestPath) ||
                    !definition.SourcePath.StartsWith("payload-r7/", StringComparison.Ordinal) ||
                    !ids.Add(definition.Id) || !routes.Add(definition.RequestPath) || !sourcePaths.Add(definition.SourcePath))
                    throw new InvalidDataException("R52 catalog row is not unique and constrained to the iOS CDN profile.");

                var resource = _resourceCatalogStore.ResolveProfileResource(definition, root);
                payloadBytes = checked(payloadBytes + new FileInfo(resource.SourceFile).Length);
                resources.Add(resource);
            }
            catch (Exception exception)
            {
                errors.Add($"R52 resource validation failed ({exception.GetType().Name}).");
            }
        }

        if (resources.Count != ExpectedResourceRoutes || payloadBytes != expectedPayloadBytes)
            errors.Add("R52 payload count or total size does not match the profile report.");
        if (errors.Count != 0)
            throw new InvalidDataException("R52 resource catalog validation failed.");
        return resources;
    }

    private static Dictionary<int, IosR52ListDatabase> LoadListDatabases(string directory, OfflineProfileReport report)
    {
        var files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        var expectedNames = Enumerable.Range(0, Revision)
            .Select(from => $"resource-list-{AssetVersion}-from-{from}.json")
            .Append($"resource-list-{AssetVersion}.json")
            .ToHashSet(StringComparer.Ordinal);
        if (!files.SetEquals(expectedNames) || report.FixtureSha256 is null || !report.FixtureSha256.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expectedNames))
            throw new InvalidDataException("R52 fixture set is incomplete or contains unexpected files.");

        var result = new Dictionary<int, IosR52ListDatabase>(Revision + 1);
        for (var from = 0; from <= Revision; from++)
        {
            var fileName = from == Revision
                ? $"resource-list-{AssetVersion}.json"
                : $"resource-list-{AssetVersion}-from-{from}.json";
            var path = ResolveProfileFile(directory, fileName);
            ValidateFileHash(path, report.FixtureSha256[fileName], fileName);
            var fixture = ReadJson<FixtureDefinition>(path, "R52 list fixture");
            var expectedPath = $"/v1/list/{AssetVersion}/{from}";
            if (!fixture.Enabled || !string.Equals(fixture.Host, CanonicalHost, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(fixture.Method, "GET", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(fixture.Path, expectedPath, StringComparison.Ordinal) ||
                fixture.StatusCode != 200 || !string.Equals(fixture.ContentType, "application/x-protobuf", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("R52 list fixture route or response metadata is invalid.");

            var body = fixture.GetBodyBytes();
            var expectedRows = from == Revision ? 0 : report.ServedRows;
            ValidateOctoDatabase(body, expectedRows, report.UrlFormatInFixtures);
            result.Add(from, new IosR52ListDatabase(fixture.Id, body, fixture.Headers));
        }
        return result;
    }

    private static void ValidateReport(OfflineProfileReport report)
    {
        if (!string.Equals(report.Schema, ReportSchema, StringComparison.Ordinal) ||
            report.AssetVersion != AssetVersion || report.Revision != Revision || report.PreviousRevision != PreviousRevision ||
            report.CurrentFixtureRevision != Revision || report.FixtureCount != Revision + 1 ||
            report.ResourceRoutes != ExpectedResourceRoutes || report.ServedRows != ExpectedUpdateRows ||
            report.PayloadCount != ExpectedResourceRoutes || report.UnverifiedReconstructedRowIds != 1156 ||
            !string.Equals(report.CanonicalHost, CanonicalHost, StringComparison.Ordinal) ||
            !string.Equals(report.CdnRequestPrefix, CdnPrefix, StringComparison.Ordinal) ||
            !string.Equals(report.UrlFormatInFixtures, CdnUrlFormat, StringComparison.Ordinal) ||
            !report.TestOnly || report.Complete || report.OriginalWireMapVerified || !report.ExperimentalField901 ||
            !report.UpstreamMustRemainDisabled || !report.NoVpsWrite || !report.NoNetworkRequests || !report.NoServerStarted ||
            report.OriginalshaderPresent != true || report.MobileDiffuseShaderPresent != false ||
            report.ParticleStandardUnlitShaderPresent != false || report.DownloadsShaderCandidatesAdded != false)
            throw new InvalidDataException("R52 profile report is not the expected test-only, incomplete profile.");

        var expectedRevisions = Enumerable.Range(0, Revision).ToArray();
        if (report.FromRevisions is null || !report.FromRevisions.SequenceEqual(expectedRevisions) ||
            report.FullUpdateFixtureRevisions is null || !report.FullUpdateFixtureRevisions.SequenceEqual(expectedRevisions))
            throw new InvalidDataException("R52 profile revision window is invalid.");
        RequireSha256(report.PackageCatalogSha256, "package catalog");
        RequireSha256(report.TitleMinimumSha256, "title minimum");
        RequireSha256(report.FixtureSetSha256, "fixture set");
        RequireSha256(report.PayloadSetSha256, "payload set");
        if (report.FixtureSha256 is null || report.FixtureSha256.Count != Revision + 1 ||
            report.FixtureSha256.Values.Any(value => !IsSha256(value)))
            throw new InvalidDataException("R52 fixture hash manifest is invalid.");
    }

    private static void ValidateTitleMinimum(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        if (root.GetProperty("assetVersion").GetInt32() != AssetVersion ||
            root.GetProperty("revision").GetInt32() != Revision ||
            root.GetProperty("previousRevision").GetInt32() != PreviousRevision ||
            !string.Equals(root.GetProperty("urlPathFormat").GetString(), CdnPrefix + "{o}", StringComparison.Ordinal))
            throw new InvalidDataException("R52 title minimum identity or CDN prefix is invalid.");
        var revisions = root.GetProperty("fromRevisions").EnumerateArray().Select(item => item.GetInt32()).ToArray();
        if (!revisions.SequenceEqual(Enumerable.Range(0, Revision)))
            throw new InvalidDataException("R52 title minimum revision window is invalid.");
    }

    private static void ValidateOctoDatabase(byte[] body, int expectedRows, string? expectedUrlFormat)
    {
        ulong? version = null;
        string? urlFormat = null;
        var rows = 0;
        var fieldOneCount = 0;
        var fieldFiveCount = 0;
        var index = 0;
        while (index < body.Length)
        {
            var key = ReadVarint(body, ref index);
            var field = checked((int)(key >> 3));
            var wire = (int)(key & 7);
            switch (wire)
            {
                case 0:
                    var value = ReadVarint(body, ref index);
                    if (field == 1)
                    {
                        fieldOneCount++;
                        version = value;
                    }
                    break;
                case 1:
                    EnsureAvailable(body, index, 8);
                    index += 8;
                    break;
                case 2:
                    var length = checked((int)ReadVarint(body, ref index));
                    EnsureAvailable(body, index, length);
                    if (field is 2 or 4) rows++;
                    if (field == 5)
                    {
                        fieldFiveCount++;
                        urlFormat = StrictUtf8.GetString(body, index, length);
                    }
                    index += length;
                    break;
                case 5:
                    EnsureAvailable(body, index, 4);
                    index += 4;
                    break;
                default:
                    throw new InvalidDataException("R52 list fixture has an unsupported protobuf wire type.");
            }
        }

        if (fieldOneCount != 1 || version != Revision || fieldFiveCount != 1 ||
            !string.Equals(urlFormat, expectedUrlFormat, StringComparison.Ordinal) || rows != expectedRows)
            throw new InvalidDataException("R52 list fixture wire version, route, or row count is invalid.");
    }

    private static ulong ReadVarint(byte[] data, ref int index)
    {
        ulong value = 0;
        var shift = 0;
        while (index < data.Length && shift < 64)
        {
            var next = data[index++];
            value |= (ulong)(next & 0x7f) << shift;
            if ((next & 0x80) == 0) return value;
            shift += 7;
        }
        throw new InvalidDataException("R52 list fixture contains an invalid protobuf varint.");
    }

    private static void EnsureAvailable(byte[] data, int index, int length)
    {
        if (length < 0 || index < 0 || index > data.Length - length)
            throw new InvalidDataException("R52 list fixture contains a truncated protobuf field.");
    }

    private static bool IsSafeCdnPath(string path)
    {
        if (!path.StartsWith(CdnPrefix, StringComparison.Ordinal)) return false;
        var name = path[CdnPrefix.Length..];
        return name.Length > 0 && name.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private static void ValidateFileHash(string path, string? expectedHash, string name)
    {
        RequireSha256(expectedHash, name);
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"R52 {name} hash does not match its profile report.");
    }

    private static T ReadJson<T>(string path, string name) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"R52 {name} is empty.");

    private static string ResolveProfileFile(string root, string fileName)
    {
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName is "." or "..")
            throw new InvalidDataException("Invalid R52 profile file name.");
        return ResolveProfilePath(root, fileName, expectDirectory: false);
    }

    private static string ResolveProfileDirectory(string root, string directoryName) =>
        ResolveProfilePath(root, directoryName, expectDirectory: true);

    private static string ResolveProfilePath(string root, string relativePath, bool expectDirectory = false)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains('\\'))
            throw new InvalidDataException("R52 profile path must be relative.");
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(fullRoot) || (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("R52 profile root is missing or is a symbolic link.");
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("R52 profile path escapes its root.");
        if (!(expectDirectory ? Directory.Exists(fullPath) : File.Exists(fullPath)))
            throw new FileNotFoundException("R52 profile file is missing.");
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("R52 profile path is a symbolic link.");
        return fullPath;
    }

    private static string Fingerprint(string root)
    {
        if (!Directory.Exists(root)) return $"{root}:missing";
        var names = new[] { "profile-report.json", "catalog.json", "title-minimum.json" }
            .Select(name => Path.Combine(root, name))
            .Append(Path.Combine(root, "fixtures"));
        var parts = new List<string>();
        foreach (var path in names)
        {
            if (Directory.Exists(path))
            {
                var directory = new DirectoryInfo(path);
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return $"{root}:invalid-link";
                var files = Directory.GetFiles(path, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal);
                parts.AddRange(files.Select(FileFingerprint));
            }
            else if (File.Exists(path))
            {
                parts.Add(FileFingerprint(path));
            }
            else
            {
                parts.Add($"{path}:missing");
            }
        }
        return string.Join('|', parts);
    }

    private static string FileFingerprint(string path) =>
        $"{path}:{File.GetLastWriteTimeUtc(path).Ticks}:{new FileInfo(path).Length}";

    private static IosR52ProfileState Failed(bool present, string error) => new(
        new ResourceCatalogSnapshot([], [error], Platform: 9, AssetVersion, Revision, Present: present),
        new Dictionary<int, IosR52ListDatabase>());

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void RequireSha256(string? value, string name)
    {
        if (!IsSha256(value)) throw new InvalidDataException($"R52 {name} hash is invalid.");
    }

    private sealed record IosR52ProfileState(
        ResourceCatalogSnapshot Catalog,
        IReadOnlyDictionary<int, IosR52ListDatabase> ListDatabases);

    private sealed class OfflineProfileReport
    {
        public string? Schema { get; init; }
        public int AssetVersion { get; init; }
        public int Revision { get; init; }
        public int PreviousRevision { get; init; }
        public int CurrentFixtureRevision { get; init; }
        public int FixtureCount { get; init; }
        public int ServedRows { get; init; }
        public int ResourceRoutes { get; init; }
        public int PayloadCount { get; init; }
        public long PayloadBytes { get; init; }
        public int UnverifiedReconstructedRowIds { get; init; }
        public string? CanonicalHost { get; init; }
        public string? CdnRequestPrefix { get; init; }
        public string? UrlFormatInFixtures { get; init; }
        public bool TestOnly { get; init; }
        public bool Complete { get; init; }
        public bool OriginalWireMapVerified { get; init; }
        public bool ExperimentalField901 { get; init; }
        public bool UpstreamMustRemainDisabled { get; init; }
        public bool NoVpsWrite { get; init; }
        public bool NoNetworkRequests { get; init; }
        public bool NoServerStarted { get; init; }
        public bool? OriginalshaderPresent { get; init; }
        public bool? MobileDiffuseShaderPresent { get; init; }
        public bool? ParticleStandardUnlitShaderPresent { get; init; }
        public bool? DownloadsShaderCandidatesAdded { get; init; }
        public int[]? FromRevisions { get; init; }
        public int[]? FullUpdateFixtureRevisions { get; init; }
        public string? PackageCatalogSha256 { get; init; }
        public string? TitleMinimumSha256 { get; init; }
        public string? FixtureSetSha256 { get; init; }
        public string? PayloadSetSha256 { get; init; }
        public Dictionary<string, string>? FixtureSha256 { get; init; }
    }
}
