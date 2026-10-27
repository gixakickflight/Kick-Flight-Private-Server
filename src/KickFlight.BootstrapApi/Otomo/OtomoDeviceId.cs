using System.Security.Cryptography;
using System.Text;

namespace KickFlight.BootstrapApi.Otomo;

// The otomo device id this adapter derives from the device id the KF client sends in its /auth/index body.
//
// otomo admits one shape (services/auth internal/identity/deviceid.go): 43 unpadded base64url characters, i.e. 32
// random bytes, with a degeneracy floor that a hash of anything passes. The KF client's own id is an arbitrary
// string, so the adapter's job is to turn it into an admissible id deterministically: the same client device must
// map to the same otomo account on every launch, every restart and every future login. A hash of the client id
// does that, and it is one-way, so the device id otomo stores is not the client's secret in the clear.
internal static class OtomoDeviceId
{
    private const string DerivationDomain = "kickflight-device:";

    public static string FromDeviceUuid(string deviceUuid)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(DerivationDomain + deviceUuid));
        return Base64Url(digest);
    }

    // A device id is a credential; it goes into a log line only in a form that cannot be replayed.
    public static string Mask(string deviceId) =>
        deviceId.Length <= 8 ? "***" : deviceId[..8] + "...";

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
