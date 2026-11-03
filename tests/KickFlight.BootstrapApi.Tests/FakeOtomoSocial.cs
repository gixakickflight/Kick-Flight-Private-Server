using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace KickFlight.BootstrapApi.Tests;

// A stand-in for otomo's session service: the follow relation, the profile rename, and the ranking board. Real loopback HTTP, so an adapter test exercises the adapter's own client end to end - the path,
// the bearer token, the bodies, the status codes and the retry - exactly as FakeOtomoEconomy does for values.
//
// Accounts are the ones the auth fake minted: every route is taken from the `sub` of the access token the adapter
// presents, never from the request, so two test sessions never see each other's follow list. A follow is addressed by
// the target's player id; an account that has never renamed keeps a provisional name, which is what the real service
// does too (POST /me/init assigns "Player####").
//
// The ladder is the published session.rank one (session units): rule types crystal_scramble / flag_flight /
// rapid_ball, and tiers rank_1@0 ... rank_16@7300. Besides the player plane this fake serves
//
//   * the internal plane (service key): POST /internal/session/rank/awards, idempotent by key per player and rule
//     type with points floored at 0, and GET /internal/session/rank/players/{id} with every rule type present;
//   * the release bundle (GET /internal/config/channels/{channel}/bundle) carrying config/session.rank.json, which is
//     how the adapter reads the tier thresholds.
//
// Account existence is lenient: any account id the auth fake minted is "a player session knows" and is created on its
// first use here. An id starting with "ghost-" is the one session has never heard of (404 player_not_found).
public sealed class FakeOtomoSocial : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HashSet<string>> _following = new(StringComparer.Ordinal);
    private readonly HashSet<(string Blocker, string Blocked)> _blocks = [];
    private readonly Dictionary<(string PlayerId, string RuleType, string Key), SessionAward> _awards = [];

    // session.rank, as published (session units).
    public static readonly string[] RuleTypes = ["crystal_scramble", "flag_flight", "rapid_ball"];
    public static readonly int[] TierMinPoints =
        [0, 300, 700, 1200, 1700, 2200, 2700, 3200, 3700, 4200, 4700, 5200, 5700, 6200, 6700, 7300];

    // "rank_<N>" for the highest tier whose threshold the points reach.
    public static string TierFor(int points)
    {
        var tier = 1;
        for (var index = 0; index < TierMinPoints.Length; index++)
            if (points >= TierMinPoints[index]) tier = index + 1;
        return "rank_" + tier;
    }
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly object _lock = new();

    // The game-server service key (session_matchmaker) the internal plane expects as a bearer.
    public const string ServiceKey = "test-session-matchmaker-key";

    // The document the bundle carries. A test can replace it to prove the adapter reads thresholds from the release.
    public string LadderDocument { get; set; } = DefaultLadderDocument();

    // Per account, so a test that breaks one player's rank service does not break the tests running beside it: the
    // award for this player answers 503 and applies nothing (the call is still logged), and the rank reads for this
    // player (player plane and internal) answer 503.
    private readonly ConcurrentDictionary<string, bool> _awardsDown = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _readsDown = new(StringComparer.Ordinal);

    public void SetAwardsUnavailable(string accountId, bool down) => _awardsDown[accountId] = down;

    public void SetRankReadsUnavailable(string accountId, bool down) => _readsDown[accountId] = down;

    public static string DefaultLadderDocument() => JsonSerializer.Serialize(new
    {
        rule_types = RuleTypes,
        tiers = TierMinPoints.Select((min, index) => new { key = "rank_" + (index + 1), min_points = min }).ToArray()
    });

    private FakeOtomoSocial(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; private set; }

    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    // The board reads this suite made, in order, so a test can assert which ladder a screen asked for.
    public IReadOnlyList<RecordedRequest> BoardRequests =>
        _requests.Where(request => request.Path.EndsWith("/rank/board", StringComparison.Ordinal)).ToArray();

    public static FakeOtomoSocial Start()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var server = new FakeOtomoSocial(app, "");
        server.Map();
        server.MapRank();
        app.StartAsync().GetAwaiter().GetResult();

        var address = app.Urls.FirstOrDefault(url => !url.EndsWith(":0", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Kestrel did not report a bound address (urls: {string.Join(", ", app.Urls)})");
        server.BaseUrl = address.TrimEnd('/');
        return server;
    }

    // The tag session knows an account by, created on first use. Deterministic, so a test can ask for the same
    // discriminator twice without reaching into the fake.
    public Account Ensure(string accountId)
    {
        var account = _accounts.GetOrAdd(accountId, id => new Account
        {
            PlayerId = id,
            DisplayName = "Player" + StableDigits(id, 10000).ToString("D4"),
            Discriminator = StableDigits(id + "#tag", 9998) + 1
        });
        lock (_lock) account.Touch();
        return account;
    }

    public void SeedName(string accountId, string displayName)
    {
        var account = Ensure(accountId);
        lock (_lock) account.DisplayName = displayName;
    }

    // One account's standing on one ladder, in session points. The tier is the published ladder's rank_<N> for them.
    public void SeedRank(string accountId, string ruleType, int points)
    {
        var account = Ensure(accountId);
        lock (_lock) account.Points[ruleType] = points;
    }

    // The points session holds for an account on a ladder (0 when it has none).
    public int PointsOf(string accountId, string ruleType)
    {
        var account = Ensure(accountId);
        lock (_lock) return account.Points.GetValueOrDefault(ruleType);
    }

    // The awards session accepted a request for (whether or not the key was new), in arrival order.
    public IReadOnlyList<AwardCall> Awards => _awardLog.ToArray();

    private readonly ConcurrentQueue<AwardCall> _awardLog = new();

    // Empties every ladder. The fake is shared by the whole suite, so a test that asserts an exact board order seeds
    // after calling this - otherwise the previous test's points are still on the ladder.
    public void ResetBoards()
    {
        lock (_lock)
        {
            foreach (var account in _accounts.Values) account.Points.Clear();
            _awards.Clear();
        }
    }

    // A follow edge made directly, for a test that is not exercising the follow routes themselves.
    public void Follow(string followerId, string followedId)
    {
        Ensure(followerId);
        Ensure(followedId);
        lock (_lock) _following.GetOrAdd(followerId, _ => new HashSet<string>(StringComparer.Ordinal)).Add(followedId);
    }

    // The follower may not follow the blocked account (session answers 403 blocked).
    public void Block(string blockerId, string blockedId)
    {
        Ensure(blockerId);
        Ensure(blockedId);
        lock (_lock) _blocks.Add((blockedId, blockerId));
    }

    public bool IsFollowing(string followerId, string followedId) =>
        _following.TryGetValue(followerId, out var set) && set.Contains(followedId);

    public void ClearRequests() => _requests.Clear();

    // A stable pseudo-number from a string: string.GetHashCode is randomised per process, which would give a test a
    // different tag on every run.
    private static int StableDigits(string value, int modulus)
    {
        var hash = 17;
        foreach (var b in Encoding.UTF8.GetBytes(value)) hash = unchecked(hash * 31 + b);
        return Math.Abs(hash % modulus);
    }

    private void Map()
    {
        // PATCH /api/player/session/me {display_name}: the rename that gives an account the name session shows on the
        // board, and the answer that carries the discriminator a follow needs.
        _app.MapMethods("/api/player/session/me", ["PATCH"], async (HttpContext context) =>
        {
            var account = Identify(context, out var failure);
            if (account is null) return failure!;

            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            var name = body.RootElement.TryGetProperty("display_name", out var nameProp)
                ? (nameProp.GetString() ?? "").Trim()
                : "";
            if (name.Length == 0)
            {
                return Error(context, StatusCodes.Status400BadRequest, "validation_failed", "display_name is required");
            }
            lock (_lock) account.DisplayName = name;
            return Results.Json(new
            {
                player_id = account.PlayerId,
                display_name = account.DisplayName,
                discriminator = account.Discriminator,
                ext = new { }
            });
        });

        _app.MapGet("/api/player/session/follows/following", (HttpContext context) =>
        {
            var account = Identify(context, out var failure);
            if (account is null) return failure!;
            lock (_lock)
            {
                var rows = _following.TryGetValue(account.PlayerId, out var set)
                    ? set.Select(FollowRow).ToArray()
                    : [];
                return Results.Json(new { following = rows });
            }
        });

        _app.MapGet("/api/player/session/follows/followers", (HttpContext context) =>
        {
            var account = Identify(context, out var failure);
            if (account is null) return failure!;
            lock (_lock)
            {
                var rows = _following.Where(pair => pair.Value.Contains(account.PlayerId))
                    .Select(pair => FollowRow(pair.Key)).ToArray();
                return Results.Json(new { followers = rows });
            }
        });

        _app.MapGet("/api/player/session/follows/count", (HttpContext context) =>
        {
            var account = Identify(context, out var failure);
            if (account is null) return failure!;
            lock (_lock)
            {
                var followers = _following.Count(pair => pair.Value.Contains(account.PlayerId));
                var following = _following.TryGetValue(account.PlayerId, out var set) ? set.Count : 0;
                return Results.Json(new { followers, following });
            }
        });

        // POST /api/player/session/follows {player_id}: idempotent; 400 invalid_player_id for a missing or empty id,
        // 404 player_not_found, 403 blocked, 400 invalid_target for a self-follow.
        _app.MapPost("/api/player/session/follows", async (HttpContext context) =>
        {
            var account = Identify(context, out var failure);
            if (account is null) return failure!;

            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            var targetId = body.RootElement.TryGetProperty("player_id", out var idProp) &&
                           idProp.ValueKind == JsonValueKind.String
                ? (idProp.GetString() ?? "").Trim()
                : "";
            if (targetId.Length == 0)
            {
                return Error(context, StatusCodes.Status400BadRequest, "invalid_player_id",
                    "player_id is required");
            }

            lock (_lock)
            {
                if (targetId == account.PlayerId)
                {
                    return Error(context, StatusCodes.Status400BadRequest, "invalid_target",
                        "you cannot follow yourself");
                }
                if (!_accounts.TryGetValue(targetId, out _))
                {
                    return Error(context, StatusCodes.Status404NotFound, "player_not_found", "no such player");
                }
                if (_blocks.Contains((account.PlayerId, targetId)))
                {
                    return Error(context, StatusCodes.Status403Forbidden, "blocked", "you cannot follow this player");
                }
                _following.GetOrAdd(account.PlayerId, _ => new HashSet<string>(StringComparer.Ordinal))
                    .Add(targetId);
                return Results.Json(FollowRow(targetId));
            }
        });

        _app.MapDelete("/api/player/session/follows/{playerId}", (HttpContext context, string playerId) =>
        {
            var account = Identify(context, out var failure);
            if (account is null) return failure!;
            lock (_lock)
            {
                if (_following.TryGetValue(account.PlayerId, out var set)) set.Remove(playerId);
            }
            return Results.StatusCode(StatusCodes.Status204NoContent);
        });

        // GET /api/player/session/rank/board?rule_type=&limit=&offset=&scope=all|following: one page of a ladder by
        // points descending. `position` is the row's 1-based place on the FULL ladder in both scopes; scope=following
        // keeps only the players the caller follows. `you` is the caller's own line whether or not it is on the page.
        _app.MapGet("/api/player/session/rank/board", (HttpContext context) =>
        {
            var account = Identify(context, out var failure);
            if (account is null) return failure!;

            var query = context.Request.Query;
            var ruleType = query["rule_type"].ToString();
            if (string.IsNullOrWhiteSpace(ruleType))
            {
                return Error(context, StatusCodes.Status400BadRequest, "validation_failed",
                    "rule_type is required: the board is one battle rule type's players");
            }
            if (!RuleTypes.Contains(ruleType))
            {
                return Error(context, StatusCodes.Status400BadRequest, "unknown_rule_type",
                    "no rank is kept for that battle rule type");
            }
            var scope = query.TryGetValue("scope", out var scopeValue) && scopeValue.ToString().Length > 0
                ? scopeValue.ToString()
                : "all";
            if (scope is not ("all" or "following"))
            {
                return Error(context, StatusCodes.Status400BadRequest, "validation_failed", "scope must be all or following");
            }
            var limit = query.TryGetValue("limit", out var limitValue) && int.TryParse(limitValue, out var wanted)
                ? Math.Clamp(wanted, 1, 200)
                : 50;
            var offset = query.TryGetValue("offset", out var offsetValue) && int.TryParse(offsetValue, out var skip)
                ? Math.Max(0, skip)
                : 0;

            lock (_lock)
            {
                var ordered = _accounts.Values
                    .Where(candidate => candidate.Points.ContainsKey(ruleType) && !string.IsNullOrEmpty(candidate.DisplayName))
                    .Select(candidate => new BoardRow(candidate.PlayerId, candidate.Points[ruleType]))
                    .OrderByDescending(row => row.Points)
                    .ThenBy(row => row.PlayerId, StringComparer.Ordinal)
                    .Select((row, index) => (Row: row, Position: index + 1))
                    .ToArray();

                var followed = _following.TryGetValue(account.PlayerId, out var set) ? set : [];
                var scoped = scope == "following"
                    ? ordered.Where(entry => followed.Contains(entry.Row.PlayerId)).ToArray()
                    : ordered;

                var page = scoped.Skip(offset).Take(limit).Select(entry =>
                {
                    var owner = Ensure(entry.Row.PlayerId);
                    return new
                    {
                        player_id = entry.Row.PlayerId,
                        points = entry.Row.Points,
                        tier = TierFor(entry.Row.Points),
                        display_name = owner.DisplayName,
                        discriminator = owner.Discriminator,
                        position = entry.Position
                    };
                }).ToArray();

                var ownPoints = account.Points.GetValueOrDefault(ruleType);
                var position = Array.FindIndex(ordered, entry => entry.Row.PlayerId == account.PlayerId) + 1;
                return Results.Json(new
                {
                    rule_type = ruleType,
                    limit,
                    offset,
                    named_only = true,
                    scope,
                    rows = page,
                    you = new { position, points = ownPoints, tier = TierFor(ownPoints) }
                });
            }
        });
    }

    // The player plane's own standing, the internal plane's award and read, and the release bundle.
    private void MapRank()
    {
        // GET /api/player/session/rank: the caller's own standing, only for the rule types they have played.
        _app.MapGet("/api/player/session/rank", (HttpContext context) =>
        {
            var account = Identify(context, out var failure);
            if (account is null) return failure!;
            if (_readsDown.GetValueOrDefault(account.PlayerId)) return Error(context, StatusCodes.Status503ServiceUnavailable, "unavailable", "rank is down");
            lock (_lock)
            {
                var ranks = RuleTypes.Where(account.Points.ContainsKey)
                    .Select(rule => new { rule_type = rule, points = account.Points[rule], tier = TierFor(account.Points[rule]),
                        updated_at = "2026-10-01T12:00:00Z" }).ToArray();
                return Results.Json(new { ranks });
            }
        });

        // GET /internal/session/rank/players/{id}: every rule type present, 0 points at the lowest tier when none.
        _app.MapGet("/internal/session/rank/players/{playerId}", (HttpContext context, string playerId) =>
        {
            if (!Service(context, out var failure)) return failure!;
            if (_readsDown.GetValueOrDefault(playerId)) return Error(context, StatusCodes.Status503ServiceUnavailable, "unavailable", "rank is down");
            if (playerId.StartsWith("ghost-", StringComparison.Ordinal))
                return Error(context, StatusCodes.Status404NotFound, "player_not_found", "no such player");
            var account = Ensure(playerId);
            lock (_lock)
            {
                var ranks = RuleTypes.Select(rule =>
                {
                    var points = account.Points.GetValueOrDefault(rule);
                    return new { rule_type = rule, points, tier = TierFor(points) };
                }).ToArray();
                return Results.Json(new { player_id = account.PlayerId, ranks });
            }
        });

        // POST /internal/session/rank/awards {player_id, rule_type, delta, key}: idempotent by key per player and rule
        // type; points floor at 0; the answer carries the standing after the (first) application.
        _app.MapPost("/internal/session/rank/awards", async (HttpContext context) =>
        {
            if (!Service(context, out var failure)) return failure!;
            using var body = await JsonDocument.ParseAsync(context.Request.Body);

            var root = body.RootElement;
            var playerId = root.TryGetProperty("player_id", out var playerProp) ? playerProp.GetString() ?? "" : "";
            var ruleType = root.TryGetProperty("rule_type", out var ruleProp) ? ruleProp.GetString() ?? "" : "";
            var key = root.TryGetProperty("key", out var keyProp) ? keyProp.GetString() ?? "" : "";
            if (playerId.Length == 0 || key.Length == 0 || !root.TryGetProperty("delta", out var deltaProp) ||
                !deltaProp.TryGetInt32(out var delta))
                return Error(context, StatusCodes.Status400BadRequest, "validation_failed", "player_id, delta and key are required");
            if (!RuleTypes.Contains(ruleType))
                return Error(context, StatusCodes.Status400BadRequest, "unknown_rule_type", "no rank is kept for that rule type");
            if (playerId.StartsWith("ghost-", StringComparison.Ordinal))
                return Error(context, StatusCodes.Status404NotFound, "player_not_found", "no such player");

            var account = Ensure(playerId);
            lock (_lock)
            {
                _awardLog.Enqueue(new AwardCall(playerId, ruleType, delta, key));
                if (_awardsDown.GetValueOrDefault(playerId))
                    return Error(context, StatusCodes.Status503ServiceUnavailable, "unavailable", "awards are down");
                if (_awards.TryGetValue((playerId, ruleType, key), out _))
                {
                    var current = account.Points.GetValueOrDefault(ruleType);
                    return Results.Json(new { player_id = playerId, rule_type = ruleType, points = current,
                        tier = TierFor(current), applied = false });
                }
                var points = Math.Max(0, account.Points.GetValueOrDefault(ruleType) + delta);
                account.Points[ruleType] = points;
                _awards[(playerId, ruleType, key)] = new SessionAward(delta, points);
                return Results.Json(new { player_id = playerId, rule_type = ruleType, points, tier = TierFor(points), applied = true });
            }
        });

        // GET /internal/config/channels/{channel}/bundle: the release as one gzipped tar, verified by a Digest header,
        // exactly the shape OtomoMasters reads. It carries session.rank and nothing else.
        _app.MapGet("/internal/config/channels/{channel}/bundle", (HttpContext context, string channel) =>
        {
            _requests.Enqueue(new RecordedRequest("GET", context.Request.Path.Value ?? "", "",
                context.Request.Headers.Authorization.ToString()));
            if (context.Request.Headers.Authorization.ToString() != "Bearer " + ServiceKey)
                return Error(context, StatusCodes.Status401Unauthorized, "invalid_key", "the service key is not usable");

            var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            using (var tar = new TarWriter(gzip, leaveOpen: true))
            {
                void Add(string name, string text) => tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                    { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
                Add("manifest.json", "{\"release\":\"test-release\"}");
                Add("config/session.rank.json", LadderDocument);
            }
            var bytes = buffer.ToArray();
            context.Response.Headers["Digest"] = "sha-256=" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            context.Response.Headers["X-Otomo-Release"] = "test-release";
            context.Response.Headers["X-Otomo-Channel"] = channel;
            return Results.Bytes(bytes, "application/gzip");
        });
    }

    private bool Service(HttpContext context, out IResult? failure)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        _requests.Enqueue(new RecordedRequest(context.Request.Method, context.Request.Path.Value ?? "",
            context.Request.QueryString.Value ?? "", authorization));
        if (authorization != "Bearer " + ServiceKey)
        {
            failure = Error(context, StatusCodes.Status401Unauthorized, "invalid_key", "the service key is not usable");
            return false;
        }
        failure = null;
        return true;
    }

    private object FollowRow(string accountId)
    {
        var account = Ensure(accountId);
        return new
        {
            player_id = account.PlayerId,
            display_name = account.DisplayName,
            discriminator = account.Discriminator,
            since = "2026-10-01T12:00:00Z"
        };
    }

    // The account the token belongs to, and the request recorded. Nothing else identifies the caller.
    private Account? Identify(HttpContext context, out IResult? failure)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        _requests.Enqueue(new RecordedRequest(context.Request.Method,
            context.Request.Path.Value ?? "", context.Request.QueryString.Value ?? "", authorization));

        var accountId = FakeOtomoEconomy.AccountOf(authorization);
        if (accountId is null)
        {
            failure = Error(context, StatusCodes.Status401Unauthorized, "invalid_token", "the token is not usable");
            return null;
        }
        failure = null;
        return Ensure(accountId);
    }

    private static IResult Error(HttpContext context, int status, string code, string message) =>
        Results.Json(new { error = new { code, message, request_id = context.TraceIdentifier } }, statusCode: status);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public sealed record RecordedRequest(string Method, string Path, string Query, string? Authorization)
    {
        public string FullPath => Path + Query;
    }

    public sealed class Account
    {
        public string PlayerId { get; init; } = "";
        public string DisplayName { get; set; } = "";
        public int Discriminator { get; init; }
        public Dictionary<string, int> Points { get; } = new(StringComparer.Ordinal);

        // Kept so a test could assert recency; the follow lists are unordered here, as the contract does not promise
        // an order.
        public DateTimeOffset LastTouched { get; private set; } = DateTimeOffset.UtcNow;

        public void Touch() => LastTouched = DateTimeOffset.UtcNow;
    }

    private sealed record BoardRow(string PlayerId, int Points);

    public sealed record AwardCall(string PlayerId, string RuleType, int Delta, string Key);

    private sealed record SessionAward(int Delta, int PointsAfter);
}
