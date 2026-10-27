using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace KickFlight.BootstrapApi.Otomo;

// The master tables this server loads from otomo instead of from config/masters_*.json.
//
// Otomo publishes a release as one deterministic gzipped tar: manifest.json first, then config/<namespace>.json
// per namespace. This class polls the release channel's bundle, verifies it against the digest otomo states,
// unpacks it in memory and hands over the tables named in Otomo:Masters:Tables. It holds the last good tables in
// a snapshot that the request path reads, so a publish takes effect on the next refresh with no restart.
//
// Three rules, each of them a failure seen in practice or the obvious next one:
//
//   * The digest is checked before anything is unpacked. A truncated download that got as far as a valid gzip
//     header would otherwise become a table with half its rows.
//   * The last good bundle is kept on disk. A restart while otomo is unreachable must serve what it last served,
//     not refuse to start: a game server on yesterday's balance is much better than one that is down.
//   * A binding that the release does not carry is an error. Serving the file "just in case" is how the two
//     copies start to differ, and the whole point of deleting the file path is that there is one copy.
public sealed class OtomoMasters : IDisposable
{
    private readonly HttpClient _http;
    private readonly OtomoMastersOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<OtomoMasters> _logger;
    private readonly IReadOnlyList<MasterBinding> _bindings;
    private readonly object _applyLock = new();

    private IReadOnlyDictionary<string, string> _tables = new Dictionary<string, string>(StringComparer.Ordinal);
    private string? _etag;

    // Where the tables in _tables came from, for the log line and for /health/ready: "channel/release" from a
    // fetch, or "cache/channel/release" when the boot fetch failed and the on-disk copy was used.
    public string Source { get; private set; } = "none";

    // The signed-in tables. Replaced whole, never mutated, so a reader never sees a half-applied release.
    public IReadOnlyDictionary<string, string> Tables => _tables;

    public OtomoMasters(HttpClient http, IOptions<OtomoMastersOptions> options, TimeProvider time,
        ILogger<OtomoMasters> logger)
    {
        _http = http;
        _options = options.Value;
        _time = time;
        _logger = logger;
        _bindings = _options.Bindings();

        if (!string.IsNullOrWhiteSpace(_options.BaseUrl) &&
            Uri.TryCreate(_options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseAddress))
        {
            _http.BaseAddress = baseAddress;
        }
        _http.Timeout = _options.Timeout;

        if (!Enabled)
        {
            _logger.LogWarning("Otomo:Masters is disabled: every master table is read from config/masters_*.json");
        }
        else if (_http.BaseAddress is null)
        {
            _logger.LogError("Otomo:Masters:Enabled is true but BaseUrl is not a URL; no table can be fetched");
        }
        else
        {
            _logger.LogInformation(
                "otomo masters: following {Channel} at {BaseUrl}, {Count} table(s) from otomo ({Tables}), refresh every {Refresh}",
                _options.Channel, _http.BaseAddress, _bindings.Count,
                _bindings.Count == 0 ? "none" : string.Join(", ", _bindings.Select(b => b.Table)),
                _options.Refresh);
        }
    }

    public bool Enabled => _options.Enabled;

    public string Channel => _options.Channel;

    // Built here for the same reason OtomoIdentity builds its own: the invariant that this project has exactly
    // two outbound HTTP clients is asserted by a test, and keeping construction beside use is what keeps it true.
    public static OtomoMasters Create(IOptions<OtomoMastersOptions> options, TimeProvider time,
        ILogger<OtomoMasters> logger) =>
        new(new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }), options, time, logger);

    // Whether this table comes from otomo. The table's file read is skipped when this is true, which is what
    // makes the port a replacement rather than a second source.
    public bool Owns(string table) => _bindings.Any(binding => string.Equals(binding.Table, table, StringComparison.Ordinal));

    public bool TryGetTable(string table, out string json) => _tables.TryGetValue(table, out json!);

    // Raised after a refresh changed at least one table. The subscriber re-encrypts and recomputes the master
    // version, so the client's next /download/master sees the new hashes.
    public event Action? Updated;

    // The boot path: fetch once and apply, or fall back to the bundle on disk. Throws when the adapter is
    // configured to take a table from otomo and neither otomo nor the cache can supply it — a server that cannot
    // serve the masters it was told to serve must not answer /download/master with something else.
    public async Task LoadInitialAsync(CancellationToken cancellationToken)
    {
        if (!Enabled) return;

        var fetched = await TryFetchAsync(cancellationToken).ConfigureAwait(false);
        if (fetched is not null)
        {
            Apply(fetched, "otomo");
            return;
        }

        var cached = TryLoadCache();
        if (cached is not null)
        {
            Apply(cached, "cache");
            _logger.LogWarning(
                "otomo masters: {BaseUrl} did not answer; serving the last good bundle from {CacheDir} ({Release})",
                _http.BaseAddress, CacheDirectory, Source);
            return;
        }

        throw new InvalidOperationException(
            $"masters {string.Join(", ", _bindings.Select(b => b.Table))} come from otomo " +
            $"({_http.BaseAddress} channel {_options.Channel}) but the fetch failed and {CacheDirectory} holds no " +
            "bundle to fall back on");
    }

    // One poll. Returns true when the tables changed, which is what the refresh loop logs. A 304, an unreachable
    // otomo or a malformed bundle all leave the current tables serving: a bad fetch must never make the server
    // worse than it was.
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        if (!Enabled) return false;

        var fetched = await TryFetchAsync(cancellationToken).ConfigureAwait(false);
        if (fetched is null) return false;
        Apply(fetched, "otomo");
        return true;
    }

    private void Apply(FetchedBundle bundle, string source)
    {
        lock (_applyLock)
        {
            _tables = bundle.Tables;
            _etag = bundle.ETag;
            Source = $"{source}/{bundle.Channel}/{bundle.Release}";
        }
        _logger.LogInformation(
            "otomo masters: {Source} release {Release} applied, {Count} table(s) from otomo",
            source, bundle.Release, bundle.Tables.Count);
        Updated?.Invoke();
    }

    private async Task<FetchedBundle?> TryFetchAsync(CancellationToken cancellationToken)
    {
        // The path is otomo Config's, not the channel's name: BaseUrl is the listener (host:port) and this is the
        // route a game server fetches its configuration from.
        var path = $"internal/config/channels/{Uri.EscapeDataString(_options.Channel)}/bundle";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_http.BaseAddress!, path));
        var key = ReadKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            _logger.LogError("otomo masters: no service key in {KeyFile}; the fetch will be refused", _options.KeyFile);
            return null;
        }
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        if (_etag is { Length: > 0 } etag)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("otomo masters: fetch {Url} failed: {Error}", request.RequestUri, exception.Message);
            return null;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("otomo masters: fetch {Url} answered HTTP {Status}", request.RequestUri, (int)response.StatusCode);
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var digest = response.Headers.TryGetValues("Digest", out var values) ? values.FirstOrDefault() : null;
            if (!DigestMatches(digest, bytes))
            {
                _logger.LogError("otomo masters: bundle digest {Digest} does not match the {Bytes} bytes received",
                    digest ?? "(missing)", bytes.Length);
                return null;
            }

            var release = response.Headers.TryGetValues("X-Otomo-Release", out var releaseValues)
                ? releaseValues.FirstOrDefault() ?? "?"
                : "?";
            var channel = response.Headers.TryGetValues("X-Otomo-Channel", out var channelValues)
                ? channelValues.FirstOrDefault() ?? _options.Channel
                : _options.Channel;
            var responseETag = response.Headers.ETag?.Tag;

            FetchedBundle bundle;
            try
            {
                bundle = Unpack(bytes, channel, release, responseETag);
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or KeyNotFoundException
                                              or IOException or ObjectDisposedException)
            {
                _logger.LogError("otomo masters: bundle for release {Release} is not usable: {Error}", release, exception.Message);
                return null;
            }

            WriteCache(bytes, channel, release, responseETag);
            return bundle;
        }
    }

    private FetchedBundle Unpack(byte[] bytes, string channel, string release, string? etag)
    {
        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress))
        using (var tar = new TarReader(gzip))
        {
            while (tar.GetNextEntry() is { } entry)
            {
                if (!entry.Name.StartsWith("config/", StringComparison.Ordinal) ||
                    !entry.Name.EndsWith(".json", StringComparison.Ordinal))
                {
                    continue;
                }
                if (entry.DataStream is null) continue;
                // leaveOpen: the entry's stream belongs to the TarReader, which still has to advance past this
                // member. Disposing it here makes the next GetNextEntry throw ObjectDisposedException.
                using var reader = new StreamReader(entry.DataStream, Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var name = Path.GetFileNameWithoutExtension(entry.Name);
                documents[name] = reader.ReadToEnd();
            }
        }

        var tables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var binding in _bindings)
        {
            if (!documents.TryGetValue(binding.Namespace, out var document))
            {
                throw new InvalidDataException(
                    $"release {release} carries no document for namespace {binding.Namespace} " +
                    $"(it carries: {(documents.Count == 0 ? "none" : string.Join(", ", documents.Keys.OrderBy(n => n)))}), " +
                    $"so master {binding.Table} cannot be served from otomo");
            }
            using var parsed = JsonDocument.Parse(document);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object ||
                !parsed.RootElement.TryGetProperty("rows", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    $"namespace {binding.Namespace} is not the {{\"rows\": [...]}} document its schema requires");
            }
            tables[binding.Table] = rows.GetRawText();
        }

        return new FetchedBundle(tables, channel, release, etag);
    }

    private static bool DigestMatches(string? header, byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(header)) return false;
        var expected = header.StartsWith("sha-256=", StringComparison.OrdinalIgnoreCase) ? header[8..] : header;
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return string.Equals(expected.Trim(), actual, StringComparison.OrdinalIgnoreCase);
    }

    private string ReadKey()
    {
        if (!string.IsNullOrWhiteSpace(_options.Key)) return _options.Key.Trim();
        if (string.IsNullOrWhiteSpace(_options.KeyFile)) return "";
        try
        {
            return File.ReadAllText(_options.KeyFile).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("otomo masters: cannot read the service key at {KeyFile}: {Error}", _options.KeyFile, exception.Message);
            return "";
        }
    }

    private string CacheDirectory => RepositoryPaths.Resolve(_options.CacheDir, Directory.GetCurrentDirectory());

    private string CacheBundlePath => Path.Combine(CacheDirectory, "bundle.tar.gz");
    private string CacheStampPath => Path.Combine(CacheDirectory, "release.txt");

    // The bundle bytes and a one-line stamp naming what they are. Written after a verified fetch only, so the
    // cache can never hold something the digest did not vouch for.
    private void WriteCache(byte[] bytes, string channel, string release, string? etag)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var temporary = CacheBundlePath + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, CacheBundlePath, overwrite: true);
            File.WriteAllText(CacheStampPath, $"{channel}\n{release}\n{etag}\n");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written costs a cold start, not correctness: say so once and carry on.
            _logger.LogWarning("otomo masters: cannot write the bundle cache at {CacheDir}: {Error}",
                CacheDirectory, exception.Message);
        }
    }

    private FetchedBundle? TryLoadCache()
    {
        try
        {
            if (!File.Exists(CacheBundlePath) || !File.Exists(CacheStampPath)) return null;
            var stamp = File.ReadAllLines(CacheStampPath);
            var channel = stamp.Length > 0 ? stamp[0] : _options.Channel;
            var release = stamp.Length > 1 ? stamp[1] : "?";
            var etag = stamp.Length > 2 ? stamp[2] : null;
            var bundle = Unpack(File.ReadAllBytes(CacheBundlePath), channel, release, string.IsNullOrWhiteSpace(etag) ? null : etag);
            _etag = bundle.ETag;
            return bundle;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or KeyNotFoundException)
        {
            _logger.LogWarning("otomo masters: the bundle cache at {CacheDir} is not usable: {Error}",
                CacheDirectory, exception.Message);
            return null;
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed record FetchedBundle(IReadOnlyDictionary<string, string> Tables, string Channel, string Release, string? ETag);
}
