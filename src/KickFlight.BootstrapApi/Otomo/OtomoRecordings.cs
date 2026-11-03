using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace KickFlight.BootstrapApi.Otomo;

// The adapter as a client of otomo's patch recordings: the upload of a replay, the two lists a replay screen renders
// (a tag's newest N, and a published curated list), and the bytes.
//
// Two planes, two credentials, and no route takes both (services/patch/README.md "Recorded content"):
//
//   * Internal plane (PUT, and GET .../{id}/blob, /internal/patch/recording/{id}): the game-server key (patch_gameserver.key) as a bearer,
//     sent the way OtomoRank sends its internal key. It names the players who were in the recording, so it is a trust
//     decision, and it is off without a key.
//   * Player plane (GET /patch/v1/recordings): the player's own access token. Patch verifies it itself. A listing by
//     tag or curated list is not scoped to the caller. The bytes are NOT read on this plane (the player route is
//     participants-only); they come from the internal blob route with the game-server key.
//
// Same transport rules as OtomoSocial and OtomoRank: one retry for a transport failure or a 5xx, none for a 4xx.
// The upload is safe to retry because patch accepts an identical repeat (200) and refuses a different one (409).
public sealed class OtomoRecordings : IOtomoRecordings, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly OtomoRecordingsOptions _options;
    private readonly ILogger<OtomoRecordings> _logger;
    private readonly Uri? _playerBase;
    private readonly Uri? _internalBase;

    public OtomoRecordings(HttpClient http, IOptions<OtomoRecordingsOptions> options, IOptions<OtomoSocialOptions> social,
        ILogger<OtomoRecordings> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        // Each call carries its own bound (Timeout or TransferTimeout), so the client's own is switched off.
        _http.Timeout = Timeout.InfiniteTimeSpan;

        _playerBase = ParseBase(string.IsNullOrWhiteSpace(_options.BaseUrl) ? social.Value.BaseUrl : _options.BaseUrl);
        _internalBase = string.IsNullOrWhiteSpace(_options.InternalBaseUrl)
            ? _playerBase
            : ParseBase(_options.InternalBaseUrl);

        if (!Enabled)
        {
            _logger.LogWarning("Otomo:Recordings is disabled: replays cannot be uploaded, listed or played");
        }
        else if (_playerBase is null)
        {
            _logger.LogError("Otomo:Recordings:Enabled is true but no patch base URL is configured; every recording request will fail closed");
        }
        else
        {
            _logger.LogInformation("otomo patch recordings: enabled at {PlayerBase}, internal plane at {InternalBase} ({Key})",
                _playerBase, _internalBase, string.IsNullOrEmpty(ReadServiceKey()) ? "NO service key" : "service key set");
        }
    }

    public bool Enabled => _options.Enabled;

    // Built here, beside its only use, because a test asserts the list of files allowed to hold an outbound client
    // (HarnessTests.Production_code_has_no_upstream_client_api).
    public static OtomoRecordings Create(IOptions<OtomoRecordingsOptions> options, IOptions<OtomoSocialOptions> social,
        ILogger<OtomoRecordings> logger) =>
        new(new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }),
            options, social, logger);

    public async Task<bool> PutAsync(string recordingId, RecordingUpload upload, CancellationToken cancellationToken)
    {
        var key = RequireServiceKey("an upload");
        var url = Under(_internalBase, $"internal/patch/recording/{Uri.EscapeDataString(recordingId)}");
        var response = await SendAsync(url, HttpMethod.Put, () => BuildUpload(upload), key, "upload",
            _options.TransferTimeout, cancellationToken).ConfigureAwait(false);
        using (response)
        {
            return response.StatusCode == HttpStatusCode.Created;
        }
    }

    public async Task<IReadOnlyList<PatchRecording>> ListByTagAsync(string accessToken, string tag, int limit,
        CancellationToken cancellationToken)
    {
        var url = Under(_playerBase,
            $"patch/v1/recordings?tag={Uri.EscapeDataString(tag)}&limit={Math.Clamp(limit, 1, 200)}");
        return await ReadListAsync(url, accessToken, "tag list", cancellationToken).ConfigureAwait(false) ?? [];
    }

    public async Task<IReadOnlyList<PatchRecording>?> ListCuratedAsync(string accessToken, string list,
        CancellationToken cancellationToken)
    {
        var url = Under(_playerBase, $"patch/v1/recordings?list={Uri.EscapeDataString(list)}");
        try
        {
            return await ReadListAsync(url, accessToken, "curated list", cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (OtomoRecordingsException exception) when (exception is
            { Failure: OtomoRecordingsFailure.Refused, StatusCode: 404 })
        {
            // A name nobody has published is not an outage; the caller decides what an absent list looks like.
            return null;
        }
    }

    public async Task<bool> CanFetchAsync(string recordingId, CancellationToken cancellationToken)
    {
        var key = RequireServiceKey("a recording fetch");
        var url = Under(_internalBase, $"internal/patch/recording/{Uri.EscapeDataString(recordingId)}/blob");
        try
        {
            using var response = await SendAsync(url, HttpMethod.Get, null, key, "blob probe", _options.Timeout,
                cancellationToken, request => request.Headers.Range = new RangeHeaderValue(0, 0)).ConfigureAwait(false);
            return true;
        }
        catch (OtomoRecordingsException exception) when (exception is
            { Failure: OtomoRecordingsFailure.Refused, StatusCode: 404 })
        {
            return false;
        }
    }

    public async Task<byte[]?> GetBlobAsync(string recordingId, CancellationToken cancellationToken)
    {
        var key = RequireServiceKey("a recording fetch");
        var url = Under(_internalBase, $"internal/patch/recording/{Uri.EscapeDataString(recordingId)}/blob");
        try
        {
            using var response = await SendAsync(url, HttpMethod.Get, null, key, "blob", _options.TransferTimeout,
                cancellationToken).ConfigureAwait(false);
            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OtomoRecordingsException exception) when (exception is
            { Failure: OtomoRecordingsFailure.Refused, StatusCode: 404 })
        {
            return null;
        }
    }

    private string RequireServiceKey(string what)
    {
        var key = ReadServiceKey();
        if (!string.IsNullOrEmpty(key)) return key;
        throw new OtomoRecordingsException(OtomoRecordingsFailure.Disabled,
            $"{what} needs the internal plane: set Otomo:Recordings:ServiceKeyFile (or ServiceKey)");
    }

    // The multipart body, fields first and the bytes in the final `file` part, which patch requires
    // (services/patch/internal/api/recording.go:229-297). Rebuilt for the retry: a content object is single use.
    private static HttpContent BuildUpload(RecordingUpload upload)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(upload.Kind), "kind");
        form.Add(new StringContent(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(upload.Bytes)).ToLowerInvariant()), "sha256");
        form.Add(new StringContent(upload.Bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)), "size");
        foreach (var player in upload.Players) form.Add(new StringContent(player), "players");
        foreach (var tag in upload.Tags) form.Add(new StringContent(tag), "tags");
        form.Add(new StringContent(upload.MetaJson), "meta");
        var file = new ByteArrayContent(upload.Bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "recording.bin");
        return form;
    }

    private async Task<IReadOnlyList<PatchRecording>?> ReadListAsync(Uri url, string accessToken, string operation,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(url, HttpMethod.Get, null, accessToken, operation, _options.Timeout,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var body = await response.Content.ReadFromJsonAsync<PatchRecordingList>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return body?.Recordings ?? [];
        }
        catch (JsonException exception)
        {
            throw new OtomoRecordingsException(OtomoRecordingsFailure.Unavailable,
                $"otomo patch {operation} answered a body that is not JSON: {exception.Message}",
                (int)response.StatusCode, null, exception);
        }
    }

    // Also where a switched-off client is noticed first, so the answer is Disabled whether or not a base URL is set.
    private Uri Under(Uri? baseUri, string relative)
    {
        if (!Enabled)
        {
            throw new OtomoRecordingsException(OtomoRecordingsFailure.Disabled,
                "Otomo:Recordings:Enabled is false: this server keeps no replay and cannot say which ones exist");
        }
        return baseUri is null
            ? throw new OtomoRecordingsException(OtomoRecordingsFailure.Unavailable,
                "no patch base URL is configured (Otomo:Recordings:BaseUrl, or Otomo:Social:BaseUrl)")
            : new Uri(baseUri, relative);
    }

    // Sends with the bound and the retry rule. Returns the successful response (caller disposes); a non-success is an
    // exception, so every status is classified in one place.
    private async Task<HttpResponseMessage> SendAsync(Uri url, HttpMethod method, Func<HttpContent>? body,
        string bearer, string operation, TimeSpan bound, CancellationToken cancellationToken,
        Action<HttpRequestMessage>? shape = null)
    {
        if (!Enabled)
        {
            throw new OtomoRecordingsException(OtomoRecordingsFailure.Disabled,
                "Otomo:Recordings:Enabled is false: this server keeps no replay and cannot say which ones exist");
        }

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bounded.CancelAfter(bound);
                using var request = new HttpRequestMessage(method, url);
                if (body is not null) request.Content = body();
                if (!string.IsNullOrEmpty(bearer)) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
                shape?.Invoke(request);
                // The body is read inside the bound too: a response that stalls half way is a timeout, not a hang.
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, bounded.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                                              exception is HttpRequestException or TaskCanceledException
                                                  or OperationCanceledException)
            {
                if (attempt == 0)
                {
                    _logger.LogWarning("otomo patch recordings: {Operation} to {Url} failed ({Error}); retrying once",
                        operation, url, exception.Message);
                    await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                throw new OtomoRecordingsException(OtomoRecordingsFailure.Unavailable,
                    $"otomo patch recordings {operation} could not reach {url}: {exception.Message}", 0, null, exception);
            }

            if ((int)response.StatusCode >= 500 && attempt == 0)
            {
                _logger.LogWarning("otomo patch recordings: {Operation} to {Url} answered {Status}; retrying once",
                    operation, url, (int)response.StatusCode);
                response.Dispose();
                await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (response.IsSuccessStatusCode) return response;

            using (response)
            {
                var code = await ReadErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);
                var failure = (int)response.StatusCode >= 500 ? OtomoRecordingsFailure.Unavailable : OtomoRecordingsFailure.Refused;
                var message = $"otomo patch recordings {operation} answered {(int)response.StatusCode}{(code is null ? "" : " " + code)}";
                // A 404 is an answer (no such recording, not in it); only an outage is an error worth a line.
                if (failure == OtomoRecordingsFailure.Unavailable) _logger.LogError("{Message}", message);
                else _logger.LogWarning("{Message}", message);
                throw new OtomoRecordingsException(failure, message, (int)response.StatusCode, code);
            }
        }
    }

    private Task DelayBeforeRetryAsync(CancellationToken cancellationToken) =>
        _options.RetryDelay > TimeSpan.Zero ? Task.Delay(_options.RetryDelay, cancellationToken) : Task.CompletedTask;

    // The COM-5 envelope: {"error":{"code","message","request_id"}} (services/patch/internal/api/errors.go).
    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.Object &&
                   error.TryGetProperty("code", out var code) &&
                   code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException
                                              or NotSupportedException or TaskCanceledException)
        {
            return null;
        }
    }

    // Read on every upload, so rotating the key needs no restart (the same rule as OtomoRank).
    private string ReadServiceKey()
    {
        if (!string.IsNullOrWhiteSpace(_options.ServiceKey)) return _options.ServiceKey.Trim();
        if (string.IsNullOrWhiteSpace(_options.ServiceKeyFile)) return "";
        try
        {
            return File.ReadAllText(_options.ServiceKeyFile).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("otomo patch recordings: cannot read the service key at {KeyFile}: {Error}",
                _options.ServiceKeyFile, exception.Message);
            return "";
        }
    }

    private static Uri? ParseBase(string? baseUrl) =>
        string.IsNullOrWhiteSpace(baseUrl)
            ? null
            : Uri.TryCreate(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ? uri : null;

    public void Dispose() => _http.Dispose();
}
