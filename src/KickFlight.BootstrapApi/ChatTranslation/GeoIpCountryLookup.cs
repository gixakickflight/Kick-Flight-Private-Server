using System.Collections.Concurrent;
using System.Net;
using MaxMind.Db;

namespace KickFlight.BootstrapApi.ChatTranslation;

public interface ICountryLookup
{
    /// <summary>ISO 3166-1 alpha-2 for a public address, or null (private/unknown/DB missing).</summary>
    string? Lookup(IPAddress address);
}

/// <summary>
/// Offline country lookup over a DB-IP "IP to Country Lite" mmdb (CC BY 4.0, https://db-ip.com), read with the
/// MaxMind.Db reader. A missing or unreadable file degrades to "unknown" (-> English) and is retried every
/// <see cref="ReloadCheckInterval"/>, so the monthly updater (deploy/geoip) is picked up without a restart.
/// Results are cached per address.
/// </summary>
public sealed class GeoIpCountryLookup : ICountryLookup, IDisposable
{
    private const int MaxCacheEntries = 20_000;
    public static readonly TimeSpan ReloadCheckInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly ILogger<GeoIpCountryLookup> _logger;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<IPAddress, (string? Country, DateTimeOffset Expires)> _cache = new();
    private Reader? _reader;
    private DateTime _loadedWriteTimeUtc;
    private DateTimeOffset _nextCheck = DateTimeOffset.MinValue;
    private bool _warnedMissing;

    public GeoIpCountryLookup(string path, TimeProvider time, ILogger<GeoIpCountryLookup> logger)
    {
        _path = path;
        _time = time;
        _logger = logger;
    }

    public bool DatabaseLoaded { get { EnsureReader(); return _reader is not null; } }

    public string? Lookup(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (!IsPublic(address)) return null;
        var now = _time.GetUtcNow();
        if (_cache.TryGetValue(address, out var hit) && hit.Expires > now) return hit.Country;

        var reader = EnsureReader();
        string? country = null;
        if (reader is not null)
        {
            try { country = ReadCountry(reader, address); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug(ex, "GeoIP lookup failed");
            }
        }
        if (_cache.Count >= MaxCacheEntries) _cache.Clear();
        // A missing DB must not pin "unknown" for hours after the file appears: cache that outcome only briefly.
        _cache[address] = (country, reader is null ? now.AddMinutes(1) : now + CacheTtl);
        return country;
    }

    private static string? ReadCountry(Reader reader, IPAddress address)
    {
        var record = reader.Find<Dictionary<string, object>>(address);
        if (record is null) return null;
        if (record.TryGetValue("country", out var c) && c is Dictionary<string, object> country
            && country.TryGetValue("iso_code", out var iso) && iso is string code && code.Length == 2)
            return code.ToUpperInvariant();
        return null;
    }

    private Reader? EnsureReader()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (now < _nextCheck) return _reader;
            _nextCheck = now + (_reader is null ? TimeSpan.FromMinutes(1) : ReloadCheckInterval);
            if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
            {
                if (!_warnedMissing)
                {
                    _warnedMissing = true;
                    _logger.LogWarning("GeoIP database not found (KF_GEOIP_DB); chat language falls back to English");
                }
                return _reader;
            }
            var written = File.GetLastWriteTimeUtc(_path);
            if (_reader is not null && written == _loadedWriteTimeUtc) return _reader;
            try
            {
                // Memory mode: the updater replaces the file by rename, so no handle is held on it.
                var fresh = new Reader(_path, FileAccessMode.Memory);
                var old = _reader;
                _reader = fresh;
                _loadedWriteTimeUtc = written;
                _warnedMissing = false;
                _cache.Clear();
                old?.Dispose();
                _logger.LogInformation("GeoIP database loaded");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "GeoIP database unreadable; keeping previous state");
            }
            return _reader;
        }
    }

    /// <summary>True for globally routable unicast addresses (not loopback, private, link-local, CGNAT, ULA).</summary>
    public static bool IsPublic(IPAddress a)
    {
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.None)) return false;
        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast) return false;
            var b = a.GetAddressBytes();
            return (b[0] & 0xFE) != 0xFC; // fc00::/7 unique local
        }
        var v = a.GetAddressBytes();
        return !(v[0] == 10 || v[0] == 127 || v[0] == 0 || v[0] >= 224
            || (v[0] == 172 && v[1] >= 16 && v[1] <= 31)
            || (v[0] == 192 && v[1] == 168)
            || (v[0] == 169 && v[1] == 254)
            || (v[0] == 100 && v[1] >= 64 && v[1] <= 127));
    }

    public void Dispose() { lock (_gate) { _reader?.Dispose(); _reader = null; } }
}

/// <summary>Combines client-IP extraction, the dev overrides and the country -> language map.</summary>
public sealed class ChatLanguageResolver(ChatTranslationOptions options, ClientIpResolver ipResolver, ICountryLookup countries)
{
    public string Resolve(HttpContext context) => ForAddress(ipResolver.Resolve(context));

    public string ForAddress(IPAddress? address)
    {
        if (ChatLanguages.IsSupported(options.ForceLanguage)) return options.ForceLanguage;
        if (!string.IsNullOrEmpty(options.ForceCountry)) return ChatLanguages.ForCountry(options.ForceCountry);
        return address is null ? ChatLanguages.English : ChatLanguages.ForCountry(countries.Lookup(address));
    }
}
