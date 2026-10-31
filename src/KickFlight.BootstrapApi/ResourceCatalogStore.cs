using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace KickFlight.BootstrapApi;

public sealed class ResourceCatalogDocument
{
    public int SchemaVersion { get; init; } = 1;
    public int? Platform { get; init; }
    public int? AssetVersion { get; init; }
    public int? Revision { get; init; }
    public List<ResourceDefinition> Resources { get; init; } = [];
}

public sealed class ResourceDefinition
{
    public required string Id { get; init; }
    public bool Enabled { get; init; } = true;
    public string Host { get; init; } = "kickflight-resource-api.grenge.jp";
    public required string RequestPath { get; init; }
    public required string LogicalName { get; init; }
    public string Description { get; init; } = "";
    public required string SourcePath { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public required string Sha256 { get; init; }
    /// <summary>Original Octo database category from the IPA manifest: assetBundle (A) or resource (R).</summary>
    public string? Kind { get; init; }
    /// <summary>Original nullable Octo name. This deliberately does not use the server-only LogicalName fallback.</summary>
    public string? OctoName { get; init; }
    public long? Bytes { get; init; }
    public string? Md5 { get; init; }
    public uint? Crc32 { get; init; }
    /// <summary>Stable server-assigned Octo row ID; it is not an original native catalog identifier.</summary>
    public int? OctoId { get; init; }
}

public sealed record ResolvedResource(ResourceDefinition Definition, string SourceFile);
public sealed record ResourceCatalogSnapshot(
    IReadOnlyList<ResolvedResource> Resources,
    IReadOnlyList<string> Errors,
    int? Platform = null,
    int? AssetVersion = null,
    int? Revision = null,
    bool Present = true);

public sealed class ResourceCatalogStore
{
    public const int IosAssetPlatform = 9;
    public const int IosAssetVersion = 12346;
    // Source revision 46 appends 311 verified weapon models, preserving all 2,252 r6 rows.
    // Wire revision 47 forces the additive set for clients on r6.
    public const int IosCatalogRevision = 46;
    public const int IosWireRevision = 47;
    public const int IosExpectedResourceCount = 2563;
    public const int IosExpectedAssetBundleCount = 2358;
    public const int IosExpectedRawResourceCount = 205;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly HarnessOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<ResourceCatalogStore> _logger;
    private readonly object _gate = new();
    private ResourceCatalogSnapshot? _snapshot;
    private string? _fingerprint;
    private ResourceCatalogSnapshot? _iosSnapshot;
    private string? _iosFingerprint;

    public ResourceCatalogStore(
        IOptions<HarnessOptions> options,
        IWebHostEnvironment environment,
        ILogger<ResourceCatalogStore> logger)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    public ResolvedResource? Match(string host, string path) => GetSnapshot().Resources.FirstOrDefault(resource =>
        resource.Definition.Enabled &&
        string.Equals(FixtureStore.NormalizeHost(resource.Definition.Host), FixtureStore.NormalizeHost(host), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(FixtureStore.NormalizePath(resource.Definition.RequestPath), FixtureStore.NormalizePath(path), StringComparison.Ordinal));

    public ResolvedResource? MatchDirect(string path)
    {
        var matches = GetSnapshot().Resources.Where(resource =>
            resource.Definition.Enabled &&
            string.Equals(FixtureStore.NormalizePath(resource.Definition.RequestPath), FixtureStore.NormalizePath(path), StringComparison.Ordinal))
            .Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public ResolvedResource? MatchIosDirect(string path)
    {
        var matches = GetIosSnapshot().Resources.Where(resource =>
            resource.Definition.Enabled &&
            string.Equals(FixtureStore.NormalizePath(resource.Definition.RequestPath), FixtureStore.NormalizePath(path), StringComparison.Ordinal))
            .Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public bool IsIosCatalogReady
    {
        get
        {
            var snapshot = GetIosSnapshot();
            return snapshot.Present && snapshot.Errors.Count == 0 &&
                   snapshot.Platform == IosAssetPlatform && snapshot.AssetVersion == IosAssetVersion &&
                   snapshot.Revision == IosCatalogRevision && snapshot.Resources.Count == IosExpectedResourceCount;
        }
    }

    public ResourceCatalogSnapshot GetSnapshot()
    {
        var catalogPath = RepositoryPaths.Resolve(_options.ResourceCatalogPath, _environment.ContentRootPath);
        var fingerprint = Fingerprint(catalogPath);
        if (_snapshot is not null && fingerprint == _fingerprint) return _snapshot;

        lock (_gate)
        {
            if (_snapshot is not null && fingerprint == _fingerprint) return _snapshot;
            _snapshot = LoadSnapshot(catalogPath, optional: false, expectIosMetadata: false);
            _fingerprint = fingerprint;
            _logger.LogInformation("Loaded {ResourceCount} local resources with {ErrorCount} errors", _snapshot.Resources.Count, _snapshot.Errors.Count);
            return _snapshot;
        }
    }

    /// <summary>
    /// Loads the separately versioned iOS catalog from the existing read-only .local mount. Missing files disable
    /// iOS selection without changing Android readiness; a present but partial/malformed catalog fails closed.
    /// </summary>
    public ResourceCatalogSnapshot GetIosSnapshot()
    {
        var catalogPath = RepositoryPaths.Resolve(_options.IosResourceCatalogPath, _environment.ContentRootPath);
        var fingerprint = Fingerprint(catalogPath);
        if (_iosSnapshot is not null && fingerprint == _iosFingerprint) return _iosSnapshot;

        lock (_gate)
        {
            if (_iosSnapshot is not null && fingerprint == _iosFingerprint) return _iosSnapshot;
            _iosSnapshot = LoadIosSnapshot(catalogPath);
            _iosFingerprint = fingerprint;
            _logger.LogInformation("Loaded optional iOS catalog: present={Present}, resources={ResourceCount}, errors={ErrorCount}",
                _iosSnapshot.Present, _iosSnapshot.Resources.Count, _iosSnapshot.Errors.Count);
            return _iosSnapshot;
        }
    }

    private ResourceCatalogSnapshot LoadSnapshot(string catalogPath, bool optional, bool expectIosMetadata)
    {
        var resources = new List<ResolvedResource>();
        var errors = new List<string>();
        ResourceCatalogDocument? document = null;
        var present = File.Exists(catalogPath);
        if (!present && optional) return new ResourceCatalogSnapshot(resources, errors, Present: false);
        try
        {
            if (!present) throw new FileNotFoundException("Resource catalog was not found.", catalogPath);
            document = JsonSerializer.Deserialize<ResourceCatalogDocument>(File.ReadAllText(catalogPath), JsonOptions)
                ?? throw new InvalidDataException("The resource catalog was empty.");
            if (document.SchemaVersion != 1) throw new InvalidDataException($"Unsupported resource catalog schema {document.SchemaVersion}.");
            if (expectIosMetadata && (document.Platform != IosAssetPlatform || document.AssetVersion != IosAssetVersion ||
                                      document.Revision != IosCatalogRevision || document.Resources.Count != IosExpectedResourceCount))
                throw new InvalidDataException("iOS catalog identity or row count does not match the pinned 12346/revision-46/2563-resource profile.");
            foreach (var definition in document.Resources)
            {
                try
                {
                    if (expectIosMetadata && !definition.RequestPath.StartsWith("/ios/cdn/", StringComparison.Ordinal))
                        throw new InvalidDataException("iOS catalog requestPath must be under /ios/cdn/.");
                    resources.Add(ResolveAndValidate(definition, expectIosMetadata));
                }
                catch (Exception exception)
                {
                    errors.Add($"{definition.Id}: {exception.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            errors.Add($"{Path.GetFileName(catalogPath)}: {exception.Message}");
        }

        var duplicates = resources.Where(resource => resource.Definition.Enabled)
            .GroupBy(resource => $"{FixtureStore.NormalizeHost(resource.Definition.Host)}|{FixtureStore.NormalizePath(resource.Definition.RequestPath)}")
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        errors.AddRange(duplicates.Select(duplicate => $"Duplicate enabled resource route: {duplicate}"));
        if (expectIosMetadata)
        {
            if (resources.Count != IosExpectedResourceCount)
                errors.Add($"iOS catalog has {resources.Count} valid rows; expected {IosExpectedResourceCount}.");
            if (resources.Any(resource => !resource.Definition.Enabled))
                errors.Add("iOS catalog must enable every pinned native resource.");
            if (resources.Any(resource => resource.Definition.Kind is not ("assetBundle" or "resource")))
                errors.Add("iOS catalog contains an unsupported Octo kind.");
            if (resources.Any(resource => resource.Definition.Bytes is null || resource.Definition.Bytes < 0 ||
                                          string.IsNullOrWhiteSpace(resource.Definition.Md5) ||
                                          resource.Definition.Md5.Length != 32 ||
                                          !resource.Definition.Md5.All(Uri.IsHexDigit) ||
                                          resource.Definition.Crc32 is null))
                errors.Add("iOS catalog is missing original Octo size, MD5, or CRC32 metadata.");
            var duplicateIds = resources.GroupBy(resource => resource.Definition.Id, StringComparer.Ordinal)
                .Where(group => group.Count() > 1).Select(group => group.Key);
            errors.AddRange(duplicateIds.Select(id => $"Duplicate iOS resource id: {id}"));
            var octoNames = resources.Where(resource => resource.Definition.OctoName is not null)
                .GroupBy(resource => resource.Definition.OctoName, StringComparer.Ordinal)
                .Where(group => group.Count() > 1).Select(group => group.Key);
            errors.AddRange(octoNames.Select(name => $"Duplicate iOS Octo name: {name}"));
            if (resources.Count(resource => resource.Definition.Kind == "assetBundle") != IosExpectedAssetBundleCount ||
                resources.Count(resource => resource.Definition.Kind == "resource") != IosExpectedRawResourceCount)
                errors.Add("iOS catalog kind counts do not match the pinned 2,358 A / 205 R corpus.");
            var wireIds = resources.Select(resource => resource.Definition.OctoId).ToArray();
            if (wireIds.Any(id => id is null || id < 1 || id > IosExpectedResourceCount) ||
                wireIds.Select(id => id!.Value).Distinct().Count() != IosExpectedResourceCount)
                errors.Add($"iOS catalog wire IDs must be a unique, complete 1..{IosExpectedResourceCount} sequence.");
        }
        return new ResourceCatalogSnapshot(resources, errors, document?.Platform, document?.AssetVersion, document?.Revision, present);
    }

    private ResourceCatalogSnapshot LoadIosSnapshot(string catalogPath)
    {
        if (!File.Exists(catalogPath)) return new ResourceCatalogSnapshot([], [], Present: false);
        var errors = new List<string>();
        try
        {
            var manifestPath = RepositoryPaths.Resolve(_options.IosResourceManifestPath, _environment.ContentRootPath);
            if (!File.Exists(manifestPath)) throw new FileNotFoundException("Tracked iOS catalog manifest was not found.", manifestPath);
            var stagedHash = HashFile(catalogPath);
            var manifestHash = HashFile(manifestPath);
            if (!string.Equals(stagedHash, manifestHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Staged iOS catalog differs from the tracked 12346 manifest.");
        }
        catch (Exception exception)
        {
            return new ResourceCatalogSnapshot([], [$"{Path.GetFileName(catalogPath)}: {exception.Message}"], Present: true);
        }

        return LoadSnapshot(catalogPath, optional: false, expectIosMetadata: true);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Fingerprint(string path) => File.Exists(path)
        ? $"{path}:{File.GetLastWriteTimeUtc(path).Ticks}:{new FileInfo(path).Length}"
        : $"{path}:missing";

    private ResolvedResource ResolveAndValidate(ResourceDefinition definition, bool validateIosWireMetadata = false)
    {
        if (string.IsNullOrWhiteSpace(definition.Id)) throw new InvalidDataException("id is required.");
        if (string.IsNullOrWhiteSpace(definition.Host)) throw new InvalidDataException("host is required.");
        if (string.IsNullOrWhiteSpace(definition.RequestPath) || !definition.RequestPath.StartsWith('/'))
            throw new InvalidDataException("requestPath must start with '/'.");
        if (string.IsNullOrWhiteSpace(definition.LogicalName)) throw new InvalidDataException("logicalName is required.");
        if (string.IsNullOrWhiteSpace(definition.SourcePath)) throw new InvalidDataException("sourcePath is required.");
        if (definition.Sha256.Length != 64 || !definition.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("sha256 must contain 64 hexadecimal characters.");

        var sourceFile = RepositoryPaths.Resolve(definition.SourcePath, _environment.ContentRootPath);
        if (!File.Exists(sourceFile)) throw new FileNotFoundException("Source file was not found.", sourceFile);
        using var stream = File.OpenRead(sourceFile);
        if (validateIosWireMetadata && (definition.Bytes != stream.Length ||
                                         definition.Md5 is null || definition.Crc32 is null))
            throw new InvalidDataException("Original Octo size, MD5, and CRC32 metadata are required and must match the payload size.");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = validateIosWireMetadata ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null;
        var crc = uint.MaxValue;
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            if (validateIosWireMetadata)
            {
                md5!.AppendData(buffer, 0, read);
                for (var index = 0; index < read; index++)
                {
                    crc ^= buffer[index];
                    for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
                }
            }
        }
        var actualHash = Convert.ToHexString(sha.GetHashAndReset());
        if (!string.Equals(actualHash, definition.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SHA-256 mismatch; expected {definition.Sha256}, got {actualHash}.");
        if (validateIosWireMetadata)
        {
            var actualMd5 = Convert.ToHexString(md5!.GetHashAndReset());
            var actualCrc = ~crc;
            if (!string.Equals(actualMd5, definition.Md5, StringComparison.OrdinalIgnoreCase) || actualCrc != definition.Crc32)
                throw new InvalidDataException("Original Octo MD5 or CRC32 metadata does not match the payload.");
        }
        return new ResolvedResource(definition, sourceFile);
    }
}
