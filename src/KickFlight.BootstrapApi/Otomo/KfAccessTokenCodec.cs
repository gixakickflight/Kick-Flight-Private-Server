using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KickFlight.BootstrapApi.Otomo;

// What a KF request needs to know about its caller, once the access token has been verified.
//
// DeviceId is the otomo device id (so a logout can revoke the right family), OtomoPlayerId is otomo's account,
// GamePlayerId is the numeric row id the KF protocol requires, and Key is the D2C key the client chose for its
// own bodies in /auth/index.
public sealed record KfIdentityTicket(string DeviceId, string OtomoPlayerId, long GamePlayerId, byte[] Key);

// The token the client echoes as x-app-access-token.
//
// It is signed rather than stored. Identity is otomo's, so this adapter has no session table to look a token up
// in - and a token that only lives in a table is a token every client loses when the process restarts. The
// signature is what makes it unforgeable; the payload carries only what a request needs, and the key it carries
// is the client's own (it sent that key to this server in the first place).
public sealed class KfAccessTokenCodec
{
    private const string Prefix = "kf1";
    private readonly byte[] _signingKey;
    private readonly ILogger<KfAccessTokenCodec> _logger;

    public KfAccessTokenCodec(string? configuredSecret, ILogger<KfAccessTokenCodec> logger)
    {
        _logger = logger;
        if (string.IsNullOrWhiteSpace(configuredSecret))
        {
            _signingKey = RandomNumberGenerator.GetBytes(32);
            // Not fatal - every token this process issues still verifies - but a deployment that restarts drops
            // every client that was mid-session, so it is worth saying out loud.
            _logger.LogWarning(
                "Otomo:TokenSecret is not set: access tokens are signed with a key generated at startup and will not " +
                "survive a restart");
            return;
        }

        _signingKey = SHA256.HashData(Encoding.UTF8.GetBytes(configuredSecret));
    }

    public string Issue(KfIdentityTicket ticket)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(
            ticket.DeviceId,
            ticket.OtomoPlayerId,
            ticket.GamePlayerId,
            Convert.ToBase64String(ticket.Key),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var encoded = Base64Url(payload);
        return $"{Prefix}.{encoded}.{Base64Url(Sign(encoded))}";
    }

    // A token this process did not sign, or one whose payload no longer parses, is a rejection - never a licence
    // to fall back to another player.
    public bool TryRead(string token, out KfIdentityTicket ticket)
    {
        ticket = null!;
        if (string.IsNullOrEmpty(token)) return false;

        var parts = token.Split('.');
        if (parts.Length != 3 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal)) return false;
        if (!CryptographicOperations.FixedTimeEquals(Sign(parts[1]), UnBase64Url(parts[2]))) return false;

        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(UnBase64Url(parts[1]));
            if (payload is null || string.IsNullOrEmpty(payload.DeviceId) || string.IsNullOrEmpty(payload.OtomoPlayerId) ||
                payload.GamePlayerId <= 0)
            {
                return false;
            }
            var key = Convert.FromBase64String(payload.Key);
            if (key.Length != D2CCodec.KeySizeBytes) return false;

            ticket = new KfIdentityTicket(payload.DeviceId, payload.OtomoPlayerId, payload.GamePlayerId, key);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return false;
        }
    }

    private byte[] Sign(string encodedPayload) =>
        HMACSHA256.HashData(_signingKey, Encoding.ASCII.GetBytes($"{Prefix}.{encodedPayload}"));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] UnBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    private sealed record Payload(
        [property: JsonPropertyName("d")] string DeviceId,
        [property: JsonPropertyName("o")] string OtomoPlayerId,
        [property: JsonPropertyName("p")] long GamePlayerId,
        [property: JsonPropertyName("k")] string Key,
        [property: JsonPropertyName("t")] long IssuedAtUnixSeconds);
}
