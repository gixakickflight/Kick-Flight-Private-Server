using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KickFlight.Transport
{
    /// <summary>
    /// Minimal HTTP boundary for the private server. It intentionally does not generate auth material or
    /// interpret game DTOs; callers supply the established D2C key and parse the returned UTF-8 JSON.
    /// </summary>
    public sealed class KickFlightApiClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly Uri _baseUri;
        private readonly KickFlightTransportLimits _limits;

        public KickFlightApiClient(Uri baseUri, bool allowInsecureHttpForDevelopment = false,
            KickFlightTransportLimits? limits = null)
            : this(CreateNoRedirectHandler(), baseUri, allowInsecureHttpForDevelopment, limits)
        {
        }

        internal KickFlightApiClient(HttpMessageHandler handler, Uri baseUri,
            bool allowInsecureHttpForDevelopment = false, KickFlightTransportLimits? limits = null)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (handler is HttpClientHandler httpHandler) httpHandler.AllowAutoRedirect = false;
            _http = new HttpClient(handler);
            if (baseUri == null) throw new ArgumentNullException(nameof(baseUri));
            if (!baseUri.IsAbsoluteUri || (baseUri.Scheme != Uri.UriSchemeHttps
                && !(allowInsecureHttpForDevelopment && baseUri.Scheme == Uri.UriSchemeHttp)))
                throw new ArgumentException("API base URI must be absolute HTTPS (HTTP is opt-in for local development).", nameof(baseUri));
            if (!string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
                throw new ArgumentException("API base URI cannot include a query or fragment.", nameof(baseUri));
            if (baseUri.AbsolutePath != "/")
                throw new ArgumentException("API base URI must identify the origin root.", nameof(baseUri));

            _baseUri = EnsureTrailingSlash(baseUri);
            _http.BaseAddress = _baseUri;
            _limits = limits ?? KickFlightTransportLimits.Default;
            _limits.Validate();
        }

        /// <summary>POST UTF-8 JSON through the D2C envelope; returns decrypted UTF-8 JSON on app status 0.</summary>
        public async Task<string> PostD2cJsonAsync(
            string path,
            string json,
            byte[] key,
            string? accessToken = null,
            IDictionary<string, string>? additionalHeaders = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            var plaintext = Encoding.UTF8.GetBytes(json);
            var body = D2cCodec.EncodeWithRandomIv(plaintext, key);
            var response = await PostD2cAsync(path, body, key, accessToken, additionalHeaders, cancellationToken)
                .ConfigureAwait(false);
            return Encoding.UTF8.GetString(response.JsonBytes);
        }

        /// <summary>
        /// POST JSON where the bootstrap protocol uses different request and response keys, as /auth/index does.
        /// </summary>
        public async Task<DecryptedApiResponse> PostD2cJsonWithKeysAsync(
            string path,
            string json,
            byte[] requestKey,
            byte[] responseKey,
            string? accessToken = null,
            IDictionary<string, string>? additionalHeaders = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            var encodedRequest = D2cCodec.EncodeWithRandomIv(Encoding.UTF8.GetBytes(json), requestKey);
            return await PostD2cAsync(path, encodedRequest, responseKey, accessToken, additionalHeaders, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>POST an already encoded D2C request, decrypting a successful D2C JSON response.</summary>
        public async Task<DecryptedApiResponse> PostD2cAsync(
            string path,
            byte[] encodedRequest,
            byte[] responseKey,
            string? accessToken = null,
            IDictionary<string, string>? additionalHeaders = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ValidateRelativePath(path);
            if (encodedRequest == null) throw new ArgumentNullException(nameof(encodedRequest));
            if (responseKey == null) throw new ArgumentNullException(nameof(responseKey));

            using (var request = new HttpRequestMessage(HttpMethod.Post, path))
            {
                var expectedUri = new Uri(_baseUri, path);
                request.Content = new ByteArrayContent(encodedRequest);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                if (!string.IsNullOrEmpty(accessToken)) request.Headers.TryAddWithoutValidation("x-app-access-token", accessToken);
                if (additionalHeaders != null)
                {
                    foreach (var header in additionalHeaders)
                    {
                        if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException("Set request content type through the D2C transport.", nameof(additionalHeaders));
                        if (string.Equals(header.Key, "x-app-access-token", StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException("Pass the access token through the dedicated parameter.", nameof(additionalHeaders));
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

                using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false))
                {
                    EnsureNoRedirect(path, expectedUri, response);
                    var wireBody = await ReadBoundedAsync(path, response, _limits.MaxApiResponseBytes, cancellationToken)
                        .ConfigureAwait(false);
                    var applicationStatus = ReadSingleHeader(response, "x-app-status-code");
                    if (!response.IsSuccessStatusCode || applicationStatus != "0")
                        throw new KickFlightApiException(path, response.StatusCode, applicationStatus,
                            ReadSingleHeader(response, "x-app-master-hash"), wireBody.Length);

                    byte[] plaintext;
                    try
                    {
                        plaintext = D2cCodec.Decode(wireBody, responseKey);
                    }
                    catch (Exception exception) when (exception is CryptographicException || exception is FormatException)
                    {
                        throw new KickFlightProtocolException(path, "Successful response was not a valid D2C envelope.", exception);
                    }

                    return new DecryptedApiResponse(path, plaintext, applicationStatus,
                        ReadSingleHeader(response, "x-app-access-token"),
                        ReadSingleHeader(response, "x-app-user-id"),
                        ReadSingleHeader(response, "x-app-datetime"),
                        ReadSingleHeader(response, "x-app-master-hash"));
                }
            }
        }

        /// <summary>GET a raw CDN/master object; does not decrypt or parse its format.</summary>
        public async Task<byte[]> GetBytesAsync(string path, IDictionary<string, string>? headers = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return await GetBytesBoundedAsync(path, headers, _limits.MaxDownloadResponseBytes, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<byte[]> GetBytesBoundedAsync(string path, IDictionary<string, string>? headers, int maxBytes,
            CancellationToken cancellationToken)
        {
            ValidateRelativePath(path);
            using (var request = new HttpRequestMessage(HttpMethod.Get, path))
            {
                var expectedUri = new Uri(_baseUri, path);
                if (headers != null)
                    foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false))
                {
                    EnsureNoRedirect(path, expectedUri, response);
                    var bytes = await ReadBoundedAsync(path, response, maxBytes, cancellationToken)
                        .ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        throw new KickFlightApiException(path, response.StatusCode,
                            ReadSingleHeader(response, "x-app-status-code"),
                            ReadSingleHeader(response, "x-app-master-hash"),
                            bytes.Length);
                    return bytes;
                }
            }
        }

        /// <summary>Fetch a manifest URL only when it is on the configured HTTPS API origin.</summary>
        public Task<byte[]> GetSameOriginBytesAsync(Uri absoluteUrl, IDictionary<string, string>? headers = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (absoluteUrl == null) throw new ArgumentNullException(nameof(absoluteUrl));
            if (!absoluteUrl.IsAbsoluteUri || !SameOrigin(_baseUri, absoluteUrl)
                || !string.IsNullOrEmpty(absoluteUrl.UserInfo))
                throw new ArgumentException("Download URL must use the configured API origin.", nameof(absoluteUrl));
            var path = absoluteUrl.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped).TrimStart('/');
            return GetBytesBoundedAsync(path, headers, _limits.MaxMasterResponseBytes, cancellationToken);
        }

        public void Dispose() => _http.Dispose();

        private static HttpMessageHandler CreateNoRedirectHandler() => new HttpClientHandler
        {
            AllowAutoRedirect = false
        };

        private static bool SameOrigin(Uri expected, Uri actual) =>
            string.Equals(expected.Scheme, actual.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(expected.Host, actual.Host, StringComparison.OrdinalIgnoreCase)
            && expected.Port == actual.Port;

        private static void EnsureNoRedirect(string path, Uri expectedUri, HttpResponseMessage response)
        {
            var finalUri = response.RequestMessage?.RequestUri;
            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400
                || (finalUri != null && finalUri != expectedUri))
                throw new KickFlightProtocolException(path, "Redirect responses are not accepted.",
                    new InvalidOperationException("HTTP redirects are disabled for this client."));
        }

        private static async Task<byte[]> ReadBoundedAsync(string path, HttpResponseMessage response, int maxBytes,
            CancellationToken cancellationToken)
        {
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value > maxBytes)
                throw new KickFlightProtocolException(path, "Response exceeded the configured byte limit.",
                    new InvalidDataException("Content-Length exceeded " + maxBytes + " bytes."));

            using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var output = new System.IO.MemoryStream(Math.Min(contentLength.HasValue ? (int)contentLength.Value : 81920, 81920)))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    if (output.Length + read > maxBytes)
                        throw new KickFlightProtocolException(path, "Response exceeded the configured byte limit.",
                            new InvalidDataException("Streaming response exceeded " + maxBytes + " bytes."));
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }

        private static Uri EnsureTrailingSlash(Uri uri)
        {
            var text = uri.AbsoluteUri;
            return text.EndsWith("/", StringComparison.Ordinal) ? uri : new Uri(text + "/", UriKind.Absolute);
        }

        private static void ValidateRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Request path is required.", nameof(path));
            if (path.StartsWith("/", StringComparison.Ordinal) || Uri.IsWellFormedUriString(path, UriKind.Absolute))
                throw new ArgumentException("Request path must be relative to the configured API origin.", nameof(path));
        }

        private static string? ReadSingleHeader(HttpResponseMessage response, string name)
        {
            IEnumerable<string> values;
            if (response.Headers.TryGetValues(name, out values))
            {
                using (var iterator = values.GetEnumerator())
                    return iterator.MoveNext() ? iterator.Current : null;
            }
            if (response.Content.Headers.TryGetValues(name, out values))
            {
                using (var iterator = values.GetEnumerator())
                    return iterator.MoveNext() ? iterator.Current : null;
            }
            return null;
        }
    }

    public sealed class KickFlightTransportLimits
    {
        public static KickFlightTransportLimits Default { get; } = new KickFlightTransportLimits(
            maxApiResponseBytes: 4 * 1024 * 1024,
            maxDownloadResponseBytes: 256 * 1024 * 1024,
            maxMasterResponseBytes: 8 * 1024 * 1024);
        public int MaxApiResponseBytes { get; }
        public int MaxDownloadResponseBytes { get; }
        public int MaxMasterResponseBytes { get; }

        public KickFlightTransportLimits(int maxApiResponseBytes, int maxDownloadResponseBytes,
            int maxMasterResponseBytes = 8 * 1024 * 1024)
        {
            MaxApiResponseBytes = maxApiResponseBytes;
            MaxDownloadResponseBytes = maxDownloadResponseBytes;
            MaxMasterResponseBytes = maxMasterResponseBytes;
        }

        internal void Validate()
        {
            if (MaxApiResponseBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxApiResponseBytes));
            if (MaxDownloadResponseBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxDownloadResponseBytes));
            if (MaxMasterResponseBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxMasterResponseBytes));
        }
    }

    public sealed class DecryptedApiResponse
    {
        public string Path { get; }
        public byte[] JsonBytes { get; }
        public string? ApplicationStatus { get; }
        public string? AccessToken { get; }
        public string? UserId { get; }
        public string? ServerDateTime { get; }
        public string? MasterHash { get; }

        internal DecryptedApiResponse(string path, byte[] jsonBytes, string? applicationStatus,
            string? accessToken, string? userId, string? serverDateTime, string? masterHash)
        {
            Path = path;
            JsonBytes = jsonBytes;
            ApplicationStatus = applicationStatus;
            AccessToken = accessToken;
            UserId = userId;
            ServerDateTime = serverDateTime;
            MasterHash = masterHash;
        }

        public string GetJsonText() => Encoding.UTF8.GetString(JsonBytes);
    }

    /// <summary>Contains response metadata for startup diagnostics without including payload bytes or request keys.</summary>
    public sealed class KickFlightApiException : Exception
    {
        public string Path { get; }
        public HttpStatusCode HttpStatus { get; }
        public string? ApplicationStatus { get; }
        public string? MasterHash { get; }
        public int ResponseBodyLength { get; }

        internal KickFlightApiException(string path, HttpStatusCode httpStatus, string? applicationStatus,
            string? masterHash, int responseBodyLength)
            : base("Kick Flight API rejected " + path + " (HTTP " + (int)httpStatus
                + ", app status " + (applicationStatus ?? "missing") + ").")
        {
            Path = path;
            HttpStatus = httpStatus;
            ApplicationStatus = applicationStatus;
            MasterHash = masterHash;
            ResponseBodyLength = responseBodyLength;
        }
    }

    public sealed class KickFlightProtocolException : Exception
    {
        public string Path { get; }
        internal KickFlightProtocolException(string path, string message, Exception inner)
            : base("Kick Flight protocol error at " + path + ": " + message, inner) => Path = path;
    }
}
