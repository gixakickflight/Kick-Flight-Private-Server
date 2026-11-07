using System.Net;

namespace KickFlight.BootstrapApi.ChatTranslation;

/// <summary>
/// Finds the real client address behind the VPS proxy chain (Caddy -> nginx -> API). Forwarding headers are honoured
/// only when the direct TCP peer is in the trusted proxy list (same rule as ASP.NET ForwardedHeaders
/// KnownProxies/KnownNetworks, implemented locally so the rest of the app's RemoteIpAddress handling is untouched).
/// nginx sets X-Real-IP to the visitor address (deploy/nginx.vps.conf) and appends to X-Forwarded-For.
/// </summary>
public sealed class ClientIpResolver
{
    /// <summary>Loopback + RFC1918 + ULA: the API is only reachable through the compose network, so these are the
    /// proxy hops. Override with Chat:GeoIp:TrustedProxies (CIDR or single address).</summary>
    public static readonly string[] DefaultTrusted =
    [
        "127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7"
    ];

    private readonly List<IPNetwork> _trusted = [];

    public ClientIpResolver(IEnumerable<string>? trustedProxies = null)
    {
        foreach (var entry in trustedProxies ?? DefaultTrusted)
        {
            var text = entry.Trim();
            if (text.Length == 0) continue;
            if (IPNetwork.TryParse(text, out var network)) _trusted.Add(network);
            else if (IPAddress.TryParse(text, out var single))
                _trusted.Add(new IPNetwork(single, single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
        }
    }

    public bool IsTrusted(IPAddress address)
    {
        address = Normalize(address);
        return _trusted.Any(n => n.Contains(address));
    }

    public IPAddress? Resolve(HttpContext context) =>
        Resolve(context.Connection.RemoteIpAddress, context.Request.Headers["X-Real-IP"].FirstOrDefault(),
            string.Join(",", context.Request.Headers["X-Forwarded-For"].ToArray()));

    public IPAddress? Resolve(IPAddress? peer, string? xRealIp, string? xForwardedFor)
    {
        if (peer is null) return null;
        peer = Normalize(peer);
        if (!IsTrusted(peer)) return peer; // spoofable headers from an untrusted peer are ignored

        if (!string.IsNullOrWhiteSpace(xRealIp) && TryParse(xRealIp, out var real)) return real;

        if (!string.IsNullOrWhiteSpace(xForwardedFor))
        {
            // Right to left: skip our own proxy hops, the first untrusted address is the client.
            var hops = xForwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = hops.Length - 1; i >= 0; i--)
            {
                if (!TryParse(hops[i], out var hop)) return peer; // garbage in the chain: do not guess
                if (!IsTrusted(hop)) return hop;
            }
        }
        return peer;
    }

    private static bool TryParse(string text, out IPAddress address)
    {
        if (IPAddress.TryParse(text.Trim(), out var parsed)) { address = Normalize(parsed); return true; }
        address = IPAddress.None;
        return false;
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
