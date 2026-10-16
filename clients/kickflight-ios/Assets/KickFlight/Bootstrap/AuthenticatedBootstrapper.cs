using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KickFlight.Transport;

namespace KickFlight.Bootstrap
{
    /// <summary>
    /// JSON implementation boundary so the transport and bootstrap code stays independent of Unity JSON packages.
    /// The caller's implementation must preserve response JSON verbatim when returning it to the game layer.
    /// </summary>
    public interface IKickFlightBootstrapJson
    {
        string CreateAuthIndexRequest(string sessionKeyAscii32, string deviceUuid);
        IReadOnlyList<MasterDownloadEntry> ParseMasterManifest(string json);
    }

    public sealed class MasterDownloadEntry
    {
        public string Name { get; }
        public string Sha256 { get; }
        public Uri Url { get; }
        public long Size { get; }

        public MasterDownloadEntry(string name, string sha256, Uri url, long size)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Sha256 = sha256 ?? throw new ArgumentNullException(nameof(sha256));
            Url = url ?? throw new ArgumentNullException(nameof(url));
            Size = size;
        }
    }

    /// <summary>Caller-supplied bootstrap material; no value is generated, persisted, or embedded by this library.</summary>
    public sealed class AuthenticatedBootstrapInput
    {
        private readonly byte[] _commonD2cKey;
        private readonly Dictionary<string, string> _requestHeaders;
        public string SessionKeyAscii32 { get; }
        public string DeviceUuid { get; }
        public IReadOnlyDictionary<string, string> RequestHeaders => _requestHeaders;

        public AuthenticatedBootstrapInput(byte[] commonD2cKey, string sessionKeyAscii32, string deviceUuid,
            IDictionary<string, string>? requestHeaders = null)
        {
            if (commonD2cKey == null || commonD2cKey.Length != D2cCodec.KeySizeBytes)
                throw new ArgumentException("The supplied common D2C key must contain exactly 32 bytes.", nameof(commonD2cKey));
            _ = D2cCodec.KeyFromAscii32(sessionKeyAscii32);
            if (deviceUuid == null) throw new ArgumentNullException(nameof(deviceUuid));
            _commonD2cKey = (byte[])commonD2cKey.Clone();
            SessionKeyAscii32 = sessionKeyAscii32;
            DeviceUuid = deviceUuid;
            _requestHeaders = requestHeaders == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(requestHeaders, StringComparer.OrdinalIgnoreCase);
        }

        internal byte[] GetCommonD2cKey() => (byte[])_commonD2cKey.Clone();
        public byte[] GetSessionKeyBytes() => D2cCodec.KeyFromAscii32(SessionKeyAscii32);
        internal IDictionary<string, string> GetRequestHeaders() =>
            new Dictionary<string, string>(_requestHeaders, StringComparer.OrdinalIgnoreCase);
    }

    public sealed class AuthenticatedBootstrapResult
    {
        public string UserId { get; }
        public string AccessToken { get; }
        public string AuthResponseJson { get; }
        public string? MasterHash { get; }
        public IReadOnlyDictionary<string, string> MasterJsonByName { get; }
        public string StartupResponseJson { get; }

        internal AuthenticatedBootstrapResult(string userId, string accessToken, string authResponseJson,
            string? masterHash, IReadOnlyDictionary<string, string> masterJsonByName, string startupResponseJson)
        {
            UserId = userId;
            AccessToken = accessToken;
            AuthResponseJson = authResponseJson;
            MasterHash = masterHash;
            MasterJsonByName = masterJsonByName;
            StartupResponseJson = startupResponseJson;
        }
    }

    /// <summary>
    /// Replays only the source-confirmed post-title-start HTTP subset: auth/index, download/master,
    /// GET each listed master, then startup/index. Boot and Octo are a separate earlier user-action phase.
    /// </summary>
    public sealed class AuthenticatedBootstrapper
    {
        private readonly KickFlightApiClient _api;
        private readonly IKickFlightBootstrapJson _json;

        public AuthenticatedBootstrapper(KickFlightApiClient api, IKickFlightBootstrapJson json)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _json = json ?? throw new ArgumentNullException(nameof(json));
        }

        public async Task<AuthenticatedBootstrapResult> RunAsync(AuthenticatedBootstrapInput input,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var commonKey = input.GetCommonD2cKey();
            var sessionKey = input.GetSessionKeyBytes();
            var requestHeaders = input.GetRequestHeaders();

            // Source contract: request D2C uses the common code; its response uses hash/session key.
            var authJson = _json.CreateAuthIndexRequest(input.SessionKeyAscii32, input.DeviceUuid);
            var auth = await _api.PostD2cJsonWithKeysAsync("auth/index", authJson,
                commonKey, sessionKey, additionalHeaders: requestHeaders,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(auth.AccessToken) || string.IsNullOrEmpty(auth.UserId))
                throw new BootstrapProtocolException("auth/index succeeded without x-app-access-token or x-app-user-id.");

            var manifestRequest = D2cCodec.EncodeWithRandomIv(Encoding.UTF8.GetBytes("{}"), sessionKey);
            var manifestResponse = await _api.PostD2cAsync("download/master", manifestRequest, sessionKey,
                auth.AccessToken, requestHeaders, cancellationToken).ConfigureAwait(false);
            var entries = _json.ParseMasterManifest(manifestResponse.GetJsonText());
            ValidateManifest(entries);

            var masters = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var encryptedBytes = await _api.GetSameOriginBytesAsync(entry.Url,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (encryptedBytes.LongLength != entry.Size)
                    throw new BootstrapIntegrityException(entry.Name,
                        "Downloaded encrypted master length did not match manifest size.");

                var actualSha256 = Sha256Hex(encryptedBytes);
                if (!string.Equals(actualSha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new BootstrapIntegrityException(entry.Name,
                        "Downloaded encrypted master SHA-256 did not match manifest hash.");
                if (!HasZeroMasterIv(encryptedBytes))
                    throw new BootstrapProtocolException("Master '" + entry.Name + "' did not use the server's zero-IV master encoding.");

                byte[] plaintext;
                try
                {
                    plaintext = D2cCodec.Decode(encryptedBytes, commonKey);
                }
                catch (Exception exception) when (exception is CryptographicException || exception is FormatException)
                {
                    throw new BootstrapProtocolException("Master '" + entry.Name + "' was not a valid common-key D2C envelope.", exception);
                }
                masters.Add(entry.Name, Encoding.UTF8.GetString(plaintext));
            }

            var startup = await _api.PostD2cJsonAsync("startup/index", "{}", sessionKey,
                auth.AccessToken, requestHeaders, cancellationToken).ConfigureAwait(false);

            return new AuthenticatedBootstrapResult(auth.UserId, auth.AccessToken, auth.GetJsonText(),
                manifestResponse.MasterHash, masters, startup);
        }

        private static void ValidateManifest(IReadOnlyList<MasterDownloadEntry> entries)
        {
            if (entries == null) throw new BootstrapProtocolException("download/master omitted masterDownloadList.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Name))
                    throw new BootstrapProtocolException("Master manifest has an empty name.");
                if (!names.Add(entry.Name)) throw new BootstrapProtocolException("Master manifest repeats a name.");
                if (entry.Sha256.Length != 64 || !IsHex(entry.Sha256))
                    throw new BootstrapProtocolException("Master '" + entry.Name + "' has an invalid SHA-256 value.");
                if (!entry.Url.IsAbsoluteUri || entry.Url.Scheme != Uri.UriSchemeHttps)
                    throw new BootstrapProtocolException("Master '" + entry.Name + "' URL must be absolute HTTPS.");
                if (!string.IsNullOrEmpty(entry.Url.UserInfo) || !string.IsNullOrEmpty(entry.Url.Query)
                    || !string.IsNullOrEmpty(entry.Url.Fragment)
                    || !entry.Url.AbsolutePath.StartsWith("/demo-master/", StringComparison.Ordinal))
                    throw new BootstrapProtocolException("Master '" + entry.Name + "' URL is outside the server master route shape.");
                if (entry.Size < 0) throw new BootstrapProtocolException("Master '" + entry.Name + "' has a negative size.");
            }
        }

        private static bool IsHex(string value)
        {
            foreach (var character in value)
                if (!Uri.IsHexDigit(character)) return false;
            return true;
        }

        // The server's EncryptMaster calls D2CCodec.Encode(json, commonCode, new byte[16]); normal
        // API responses use a random IV. Keep this validation specific to downloaded master tables.
        private static bool HasZeroMasterIv(byte[] envelope)
        {
            if (envelope.Length < D2cCodec.IvSizeBytes + D2cCodec.BlockSizeBytes) return false;
            for (var index = 0; index < D2cCodec.IvSizeBytes; index++)
                if (envelope[index] != 0) return false;
            return true;
        }

        private static string Sha256Hex(byte[] value)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(value);
                var text = new StringBuilder(hash.Length * 2);
                foreach (var octet in hash) text.Append(octet.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }
    }

    public sealed class BootstrapProtocolException : Exception
    {
        public BootstrapProtocolException(string message) : base(message) { }
        public BootstrapProtocolException(string message, Exception inner) : base(message, inner) { }
    }

    public sealed class BootstrapIntegrityException : Exception
    {
        public string MasterName { get; }
        public BootstrapIntegrityException(string masterName, string message) : base(message) => MasterName = masterName;
    }
}
