using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace KickFlight.BootstrapApi.Otomo;

// The adapter as an ordinary client of otomo's session service.
//
// Every call here presents the player's own access token - the one OtomoIdentity caches per device - on session's
// player plane. Nothing in this class holds a service key, and no route takes the *caller* from anywhere but the
// token: a follow change's subject is the token's subject, so one player's request cannot act for another.
//
// Two transport rules, the same two economy's client states and for the same reason:
//
//   * One retry, and only for a failure that says nothing about the request: a transport error or a 5xx. A follow is
//     idempotent at the service, so a duplicated arrival is the same follow, not a second one.
//   * A 4xx is an answer (no such player, blocked, a self-follow) and is never repeated. It is reported as
//     Refused with session's own code carried out to the caller.
public sealed class OtomoSocial : IOtomoSocial, IDisposable
{
    // Session's wire keys are exact JSON names on the models, so no naming policy is applied in either direction.
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string PlayerPrefix = "api/player/session";

    private readonly HttpClient _http;
    private readonly OtomoSocialOptions _options;
    private readonly ILogger<OtomoSocial> _logger;
    private readonly Uri? _playerBase;
    private readonly bool _ownsHttpClient;

    // The HttpClient is passed in rather than built here so a test can hand in its own handler, which is how the
    // suite asserts the exact path, body and retry without a listener.
    public OtomoSocial(HttpClient http, IOptions<OtomoSocialOptions> options, ILogger<OtomoSocial> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _ownsHttpClient = true;
        _http.Timeout = _options.Timeout;

        _playerBase = ParseBase(_options.BaseUrl);
        if (!Enabled)
        {
            _logger.LogWarning("Otomo:Social is disabled: the follow screens and the ranking screen will refuse to answer");
        }
        else if (_playerBase is null)
        {
            _logger.LogError("Otomo:Social:Enabled is true but BaseUrl is not a URL; every social request will fail closed");
        }
        else
        {
            _logger.LogInformation("otomo session: enabled for social reads at {PlayerBase}, ladders {Ladders}",
                _playerBase,
                string.Join(", ", _options.RuleTypeNames.OrderBy(pair => pair.Key)
                    .Select(pair => $"{pair.Key}={pair.Value}")));
        }
    }

    public bool Enabled => _options.Enabled;

    // Built here, not at the composition root: this project keeps its outbound clients beside their only use, and
    // a test asserts the list of files allowed to hold one (HarnessTests.Production_code_has_no_upstream_client_api).
    public static OtomoSocial Create(IOptions<OtomoSocialOptions> options, ILogger<OtomoSocial> logger) =>
        new(new HttpClient(new SocketsHttpHandler
        {
            // A client that lives as long as the process would otherwise pin one DNS answer forever.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }), options, logger);

    public Task<SessionProfile> RenameAsync(string accessToken, string displayName,
        CancellationToken cancellationToken) =>
        CallAsync<SessionProfile>(Player("me"), HttpMethod.Patch, new { display_name = displayName }, accessToken,
            "rename", cancellationToken);

    public async Task<IReadOnlyList<SessionFollow>> GetFollowingAsync(string accessToken,
        CancellationToken cancellationToken)
    {
        var list = await CallAsync<SessionFollowing>(Player("follows/following"), HttpMethod.Get, null, accessToken,
            "following", cancellationToken).ConfigureAwait(false);
        return list.Following ?? [];
    }

    public async Task<IReadOnlyList<SessionFollow>> GetFollowersAsync(string accessToken,
        CancellationToken cancellationToken)
    {
        var list = await CallAsync<SessionFollowers>(Player("follows/followers"), HttpMethod.Get, null, accessToken,
            "followers", cancellationToken).ConfigureAwait(false);
        return list.Followers ?? [];
    }

    public Task<SessionFollowCounts> GetFollowCountsAsync(string accessToken, CancellationToken cancellationToken) =>
        CallAsync<SessionFollowCounts>(Player("follows/count"), HttpMethod.Get, null, accessToken, "follow counts",
            cancellationToken);

    public Task<SessionFollow> FollowAsync(string accessToken, string targetOtomoPlayerId,
        CancellationToken cancellationToken) =>
        CallAsync<SessionFollow>(Player("follows"), HttpMethod.Post,
            new { player_id = targetOtomoPlayerId }, accessToken, "follow", cancellationToken);

    // 204 No Content: there is nothing to deserialize, and an empty body is the success answer here rather than the
    // failure SendAsync treats it as for a read.
    public async Task UnfollowAsync(string accessToken, string playerId, CancellationToken cancellationToken)
    {
        await SendAsync(Player($"follows/{Uri.EscapeDataString(playerId)}"), HttpMethod.Delete, null, accessToken,
            "unfollow", cancellationToken, allowEmptyBody: true).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionFriend>> GetFriendsAsync(string accessToken, CancellationToken cancellationToken)
    {
        var list = await CallAsync<SessionFriends>(Player("friends"), HttpMethod.Get, null, accessToken, "friends",
            cancellationToken).ConfigureAwait(false);
        return list.Friends ?? [];
    }

    public Task<SessionFriendInvite> GetFriendInviteAsync(string accessToken, CancellationToken cancellationToken) =>
        CallAsync<SessionFriendInvite>(Player("friends/invite"), HttpMethod.Get, null, accessToken, "friend invite",
            cancellationToken);

    // A retry of a redeem is a 200 that changes nothing (session: "a retry is a 200"), so the resend SendAsync does on a
    // 5xx or a lost answer cannot make a second friendship.
    public Task<SessionFriend> RedeemFriendInviteAsync(string accessToken, string code,
        CancellationToken cancellationToken) =>
        CallAsync<SessionFriend>(Player("friends/invite/redeem"), HttpMethod.Post, new { code }, accessToken,
            "friend invite redeem", cancellationToken);

    public Task<SessionRankBoard> GetRankBoardAsync(string accessToken, string ruleType, int limit, int offset,
        BoardScope scope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ruleType))
        {
            throw new OtomoSocialException(OtomoSocialFailure.Refused,
                "no session rule type is configured for this battle rule type, so there is no ladder to read");
        }

        var query = $"rule_type={Uri.EscapeDataString(ruleType)}&limit={limit}&offset={offset}" +
                    $"&scope={(scope == BoardScope.Following ? "following" : "all")}";
        return CallAsync<SessionRankBoard>(Player($"rank/board?{query}"), HttpMethod.Get, null, accessToken,
            "rank board", cancellationToken);
    }

    private Uri Player(string relative) =>
        _playerBase is null
            ? throw new OtomoSocialException(OtomoSocialFailure.Unavailable,
                "Otomo:Social:BaseUrl is not a URL, so session cannot be reached")
            : new Uri(_playerBase, $"{PlayerPrefix}/{relative}");

    private async Task<T> CallAsync<T>(Uri url, HttpMethod method, object? body, string accessToken,
        string operation, CancellationToken cancellationToken)
    {
        var element = await SendAsync(url, method, body, accessToken, operation, cancellationToken,
            allowEmptyBody: false).ConfigureAwait(false);
        var value = element.Deserialize<T>(JsonOptions);
        return value ?? throw new OtomoSocialException(OtomoSocialFailure.Unavailable,
            $"session answered {operation} with an empty body");
    }

    // One request, at most twice. The retry rebuilds the request from the same values, which for a follow is the same
    // id: a duplicated arrival is the follow that was already made.
    private async Task<JsonElement> SendAsync(Uri url, HttpMethod method, object? body, string accessToken,
        string operation, CancellationToken cancellationToken, bool allowEmptyBody)
    {
        if (!Enabled) throw Disabled();

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(method, url);
                if (body is not null) request.Content = JsonContent.Create(body);
                if (!string.IsNullOrEmpty(accessToken))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                }
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                                              exception is HttpRequestException or TaskCanceledException
                                                  or OperationCanceledException)
            {
                if (attempt == 0)
                {
                    _logger.LogWarning("otomo session: {Operation} to {Url} failed ({Error}); retrying once",
                        operation, url, exception.Message);
                    await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                throw new OtomoSocialException(OtomoSocialFailure.Unavailable,
                    $"otomo session {operation} could not reach {url}: {exception.Message}", 0, null, exception);
            }

            using (response)
            {
                if ((int)response.StatusCode >= 500 && attempt == 0)
                {
                    _logger.LogWarning("otomo session: {Operation} to {Url} answered {Status}; retrying once",
                        operation, url, (int)response.StatusCode);
                    await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var code = await ReadErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);
                    var failure = (int)response.StatusCode >= 500
                        ? OtomoSocialFailure.Unavailable
                        : OtomoSocialFailure.Refused;
                    var message =
                        $"otomo session {operation} answered {(int)response.StatusCode}{(code is null ? "" : " " + code)}";
                    if (failure == OtomoSocialFailure.Unavailable) _logger.LogError("{Message}", message);
                    else _logger.LogInformation("{Message}", message);
                    throw new OtomoSocialException(failure, message, (int)response.StatusCode, code);
                }

                if (response.StatusCode == HttpStatusCode.NoContent && allowEmptyBody)
                {
                    using var empty = JsonDocument.Parse("{}");
                    return empty.RootElement.Clone();
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
                    throw new OtomoSocialException(OtomoSocialFailure.Unavailable,
                        $"otomo session {operation} answered a body that is not JSON: {exception.Message}",
                        (int)response.StatusCode, null, exception);
                }
            }
        }
    }

    private Task DelayBeforeRetryAsync(CancellationToken cancellationToken) =>
        _options.RetryDelay > TimeSpan.Zero ? Task.Delay(_options.RetryDelay, cancellationToken) : Task.CompletedTask;

    // The COM-5 envelope: {"error":{"code","message","request_id"}}. A body that is not one leaves the code null;
    // the status is still what the caller acts on.
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

    private static OtomoSocialException Disabled() =>
        new(OtomoSocialFailure.Disabled,
            "Otomo:Social:Enabled is false: this server holds no social relation and cannot say who a player follows");

    private static Uri? ParseBase(string? baseUrl) =>
        string.IsNullOrWhiteSpace(baseUrl)
            ? null
            : Uri.TryCreate(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ? uri : null;

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
