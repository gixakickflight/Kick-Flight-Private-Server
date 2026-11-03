using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace KickFlight.BootstrapApi.Otomo;

// The adapter as an ordinary client of otomo's chat service (services/chat, internal listener).
//
// Every call presents chat_adapter.key as the bearer token and names the player in X-Otomo-Player
// (internal/api/errors.go:242, HeaderPlayer). The bodies are exactly the ones chat's handlers decode - and its decoder
// refuses an unknown field (errors.go:326-336) - so nothing here sends a field that is not in sendBody/channelBody.
//
// Two transport rules, the same as economy's and session's:
//
//   * One retry, only for a failure that says nothing about the request (a transport error or a 5xx). A send is
//     idempotent on its key at chat, so a duplicated arrival is a replay (200, replayed = true), not a second message.
//   * A 4xx is an answer (rate limited, NG word, ...) and is never repeated.
public sealed class OtomoChat : IOtomoChat, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Prefix = "internal/chat";
    private const string PlayerHeader = "X-Otomo-Player";

    private readonly HttpClient _http;
    private readonly OtomoChatOptions _options;
    private readonly ILogger<OtomoChat> _logger;
    private readonly Uri? _base;

    public OtomoChat(HttpClient http, IOptions<OtomoChatOptions> options, ILogger<OtomoChat> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _http.Timeout = _options.Timeout;
        _base = string.IsNullOrWhiteSpace(_options.BaseUrl) ||
                !Uri.TryCreate(_options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ? null : uri;
        if (!Enabled) _logger.LogWarning("Otomo:Chat is disabled: the client is told there is no chat");
        else if (_base is null) _logger.LogError("Otomo:Chat:Enabled is true but BaseUrl is not a URL; chat fails closed");
        else _logger.LogInformation("otomo chat: enabled at {Base}", _base);
    }

    public bool Enabled => _options.Enabled;

    // Built here, beside its only use: HarnessTests.Production_code_has_no_upstream_client_api lists the files that may
    // hold an HttpClient.
    public static OtomoChat Create(IOptions<OtomoChatOptions> options, ILogger<OtomoChat> logger) =>
        new(new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }), options, logger);

    public async Task<ChatChannelView> ResolveGlobalAsync(string playerId, CancellationToken cancellationToken)
    {
        var element = await SendAsync(HttpMethod.Post, $"{Prefix}/channels", new { kind = "global" }, playerId,
            "resolve", cancellationToken).ConfigureAwait(false);
        // {"channel": channelView}
        return Take<ChatChannelView>(element, "channel", "resolve");
    }

    public async Task<IReadOnlyList<ChatMessageView>> ReadAsync(string channelId, string playerId, long after,
        int limit, CancellationToken cancellationToken)
    {
        var path = $"{Prefix}/channels/{Uri.EscapeDataString(channelId)}/messages?after={after}" +
                   (limit > 0 ? $"&limit={limit}" : "");
        var element = await SendAsync(HttpMethod.Get, path, null, playerId, "read", cancellationToken)
            .ConfigureAwait(false);
        // {"channel_id": ..., "messages": [messageView]}
        return element.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array
            ? messages.Deserialize<List<ChatMessageView>>(JsonOptions) ?? []
            : throw new OtomoChatException(OtomoChatFailure.Unavailable, "otomo chat answered a read without messages");
    }

    public async Task<ChatSendResult> SendAsync(string channelId, string playerId, string body, string attachment,
        string key, CancellationToken cancellationToken)
    {
        var path = $"{Prefix}/channels/{Uri.EscapeDataString(channelId)}/messages";
        var element = await SendAsync(HttpMethod.Post, path, new { body, attachment, key }, playerId, "send",
            cancellationToken).ConfigureAwait(false);
        // {"message": messageView, "replayed": bool}
        var message = Take<ChatMessageView>(element, "message", "send");
        var replayed = element.TryGetProperty("replayed", out var flag) && flag.ValueKind == JsonValueKind.True;
        return new ChatSendResult(message, replayed);
    }

    private static T Take<T>(JsonElement element, string property, string operation) =>
        (element.TryGetProperty(property, out var inner) ? inner.Deserialize<T>(JsonOptions) : default)
        ?? throw new OtomoChatException(OtomoChatFailure.Unavailable,
            $"otomo chat answered {operation} without {property}");

    private async Task<JsonElement> SendAsync(HttpMethod method, string relative, object? body, string playerId,
        string operation, CancellationToken cancellationToken)
    {
        if (!Enabled) throw Disabled();
        var key = ReadServiceKey();
        if (_base is null || string.IsNullOrEmpty(key))
        {
            throw new OtomoChatException(OtomoChatFailure.Disabled,
                "Otomo:Chat has no BaseUrl or no chat_adapter key (ServiceKeyFile / ServiceKey)");
        }

        var url = new Uri(_base, relative);
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(method, url);
                if (body is not null) request.Content = JsonContent.Create(body);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                request.Headers.TryAddWithoutValidation(PlayerHeader, playerId);
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                                              exception is HttpRequestException or TaskCanceledException
                                                  or OperationCanceledException)
            {
                if (attempt == 0)
                {
                    _logger.LogWarning("otomo chat: {Operation} failed ({Error}); retrying once", operation,
                        exception.Message);
                    await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                throw new OtomoChatException(OtomoChatFailure.Unavailable,
                    $"otomo chat {operation} could not reach {url}: {exception.Message}", 0, null, 0, exception);
            }

            using (response)
            {
                if ((int)response.StatusCode >= 500 && attempt == 0)
                {
                    _logger.LogWarning("otomo chat: {Operation} answered {Status}; retrying once", operation,
                        (int)response.StatusCode);
                    await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var code = await ReadErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);
                    var failure = (int)response.StatusCode >= 500 ? OtomoChatFailure.Unavailable : OtomoChatFailure.Refused;
                    var retryAfter = response.Headers.RetryAfter?.Delta is { } delta ? (int)Math.Ceiling(delta.TotalSeconds) : 0;
                    var message = $"otomo chat {operation} answered {(int)response.StatusCode}{(code is null ? "" : " " + code)}";
                    if (failure == OtomoChatFailure.Unavailable) _logger.LogError("{Message}", message);
                    else _logger.LogInformation("{Message}", message);
                    throw new OtomoChatException(failure, message, (int)response.StatusCode, code, retryAfter);
                }

                try
                {
                    using var document = await JsonDocument.ParseAsync(
                        await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    return document.RootElement.Clone();
                }
                catch (JsonException exception)
                {
                    throw new OtomoChatException(OtomoChatFailure.Unavailable,
                        $"otomo chat {operation} answered a body that is not JSON: {exception.Message}",
                        (int)response.StatusCode, null, 0, exception);
                }
            }
        }
    }

    private Task DelayBeforeRetryAsync(CancellationToken cancellationToken) =>
        _options.RetryDelay > TimeSpan.Zero ? Task.Delay(_options.RetryDelay, cancellationToken) : Task.CompletedTask;

    // The COM-5 envelope {"error":{"code","message","request_id"}} (errors.go:289-298).
    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.Object &&
                   error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException
                                              or NotSupportedException or TaskCanceledException)
        {
            return null;
        }
    }

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
            _logger.LogError("otomo chat: cannot read the chat_adapter key at {KeyFile}: {Error}",
                _options.ServiceKeyFile, exception.Message);
            return "";
        }
    }

    private static OtomoChatException Disabled() =>
        new(OtomoChatFailure.Disabled, "Otomo:Chat:Enabled is false: this server keeps no chat");

    public void Dispose() => _http.Dispose();
}
