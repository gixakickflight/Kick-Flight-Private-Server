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
// The ladder is Kick-Flight's published session.rank, in the client's own units: rule types crystal_scramble /
// flag_flight / rapid_ball, start_points 200, floor 200, ceiling 7500, outcomes win +50 / loss -20, and tiers
// rank_1@200 ... rank_16@7500. Besides the player plane this fake serves
//
//   * the internal plane (service key): POST /internal/session/rank/awards, idempotent by key per player and rule
//     type, by a declared outcome (400 unknown_outcome otherwise) with points clamped to floor..ceiling, and GET /internal/session/rank/players/{id} with every rule type present;
//   * the release bundle (GET /internal/config/channels/{channel}/bundle) carrying config/session.rank.json, which is
//     how the adapter reads the tier thresholds.
//
// Account existence is lenient: any account id the auth fake minted is "a player session knows" and is created on its
// first use here. An id starting with "ghost-" is the one session has never heard of (404 player_not_found).
public sealed partial class FakeOtomoSocial : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HashSet<string>> _following = new(StringComparer.Ordinal);
    private readonly HashSet<(string Blocker, string Blocked)> _blocks = [];
    private readonly Dictionary<(string PlayerId, string RuleType, string Key), SessionAward> _awards = [];

    // session.rank, as Kick-Flight's tenant publishes it: policy and tiers in the client's own units.
    public static readonly string[] RuleTypes = ["crystal_scramble", "flag_flight", "rapid_ball"];
    public const int StartPoints = 200;
    public const int Floor = 200;
    public const int Ceiling = 7500;
    public static readonly IReadOnlyDictionary<string, int> Outcomes =
        new Dictionary<string, int> { ["win"] = 50, ["loss"] = -20 };
    public static readonly int[] TierMinPoints =
        [200, 500, 900, 1400, 1900, 2400, 2900, 3400, 3900, 4400, 4900, 5400, 5900, 6400, 6900, 7500];

    // "rank_<N>" for the highest tier whose threshold the points reach (rank_1 below the first, as session reports).
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

    // The kf.bot_ai_profiles document the release carries: the tenant fork's own document, copied in as
    // Fixtures/kf.bot_ai_profiles.json. A test replaces it to publish "a new release"; null leaves it out of the bundle.
    public string? BotAiDocument { get; set; } = DefaultBotAiDocument();

    // The release id the bundle names. A test that publishes sets a new one, as otomo would.
    public string Release { get; set; } = "test-release";

    public static string DefaultBotAiDocument() => File.ReadAllText(Path.Combine(
        KickFlight.BootstrapApi.RepositoryPaths.FindRoot(AppContext.BaseDirectory),
        "tests", "KickFlight.BootstrapApi.Tests", "Fixtures", "kf.bot_ai_profiles.json"));

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
        start_points = StartPoints,
        floor = Floor,
        ceiling = Ceiling,
        outcomes = Outcomes,
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
        server.MapExt();
        server.MapDisplay();
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

    // The points session holds for an account on a ladder (start_points when it has no row).
    public int PointsOf(string accountId, string ruleType)
    {
        var account = Ensure(accountId);
        lock (_lock) return account.Points.GetValueOrDefault(ruleType, StartPoints);
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

                var ownPoints = account.Points.GetValueOrDefault(ruleType, StartPoints);
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
        // GET /api/player/session/rank: the caller's own standing, one entry per rule type the policy names, in its
        // order; a player with no row reads start_points, the tier for it and updated_at null.
        _app.MapGet("/api/player/session/rank", (HttpContext context) =>
        {
            var account = Identify(context, out var failure);
            if (account is null) return failure!;
            if (_readsDown.GetValueOrDefault(account.PlayerId)) return Error(context, StatusCodes.Status503ServiceUnavailable, "unavailable", "rank is down");
            lock (_lock)
            {
                var ranks = RuleTypes.Select(rule =>
                {
                    var played = account.Points.TryGetValue(rule, out var held);
                    var points = played ? held : StartPoints;
                    return new { rule_type = rule, points, tier = TierFor(points),
                        updated_at = played ? "2026-10-01T12:00:00Z" : null };
                }).ToArray();
                return Results.Json(new { ranks });
            }
        });

        // GET /internal/session/rank/players/{id}: every rule type present, start_points when there is no row.
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
                    var points = account.Points.GetValueOrDefault(rule, StartPoints);
                    return new { rule_type = rule, points, tier = TierFor(points) };
                }).ToArray();
                return Results.Json(new { player_id = account.PlayerId, ranks });
            }
        });

        // POST /internal/session/rank/awards {player_id, rule_type, key, outcome | delta}: idempotent by key per player
        // and rule type. An `outcome` is a name the published policy declares (400 unknown_outcome otherwise) and is
        // worth that policy's delta; points are clamped to [floor, ceiling]. The answer echoes the outcome and carries
        // the standing after the (first) application.
        _app.MapPost("/internal/session/rank/awards", async (HttpContext context) =>
        {
            if (!Service(context, out var failure)) return failure!;
            using var reader = new StreamReader(context.Request.Body);
            var raw = await reader.ReadToEndAsync();
            using var body = JsonDocument.Parse(raw);

            var root = body.RootElement;
            var playerId = root.TryGetProperty("player_id", out var playerProp) ? playerProp.GetString() ?? "" : "";
            var ruleType = root.TryGetProperty("rule_type", out var ruleProp) ? ruleProp.GetString() ?? "" : "";
            var key = root.TryGetProperty("key", out var keyProp) ? keyProp.GetString() ?? "" : "";
            var outcome = root.TryGetProperty("outcome", out var outcomeProp) && outcomeProp.ValueKind == JsonValueKind.String
                ? outcomeProp.GetString()
                : null;
            var hasDelta = root.TryGetProperty("delta", out var deltaProp) && deltaProp.TryGetInt32(out _);
            if (playerId.Length == 0 || key.Length == 0 || (outcome is null && !hasDelta))
                return Error(context, StatusCodes.Status400BadRequest, "validation_failed", "player_id, key and an outcome (or a delta) are required");
            if (!RuleTypes.Contains(ruleType))
                return Error(context, StatusCodes.Status400BadRequest, "unknown_rule_type", "no rank is kept for that rule type");
            if (playerId.StartsWith("ghost-", StringComparison.Ordinal))
                return Error(context, StatusCodes.Status404NotFound, "player_not_found", "no such player");
            if (outcome is not null && !Outcomes.ContainsKey(outcome))
                return Error(context, StatusCodes.Status400BadRequest, "unknown_outcome", "the ladder declares no such outcome");
            var delta = outcome is not null ? Outcomes[outcome] : deltaProp.GetInt32();

            var account = Ensure(playerId);
            lock (_lock)
            {
                _awardLog.Enqueue(new AwardCall(playerId, ruleType, outcome, hasDelta ? deltaProp.GetInt32() : null, key, raw));
                if (_awardsDown.GetValueOrDefault(playerId))
                    return Error(context, StatusCodes.Status503ServiceUnavailable, "unavailable", "awards are down");
                if (_awards.TryGetValue((playerId, ruleType, key), out _))
                {
                    var current = account.Points.GetValueOrDefault(ruleType, StartPoints);
                    return Results.Json(new { player_id = playerId, rule_type = ruleType, points = current,
                        tier = TierFor(current), applied = false, outcome });
                }
                var points = Math.Clamp(account.Points.GetValueOrDefault(ruleType, StartPoints) + delta, Floor, Ceiling);
                account.Points[ruleType] = points;
                _awards[(playerId, ruleType, key)] = new SessionAward(delta, points);
                return Results.Json(new { player_id = playerId, rule_type = ruleType, points, tier = TierFor(points), applied = true, outcome });
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
                Add("manifest.json", "{\"release\":\"" + Release + "\"}");
                Add("config/session.rank.json", LadderDocument);
                if (BotAiDocument is { } botAi) Add("config/kf.bot_ai_profiles.json", botAi);
                // The tenant's staging release carries every master table as kf.masters.<table> = {"rows": [...]}. The
                // fake serves the repository's own config/masters_*.json files in that envelope, so a host that follows
                // it serves what the files hold unless a test changes a table here (ReplaceMasterRows).
                foreach (var (ns, rows) in MasterDocuments()) Add($"config/{ns}.json", rows);
            }
            var bytes = buffer.ToArray();
            context.Response.Headers["Digest"] = "sha-256=" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            context.Response.Headers["X-Otomo-Release"] = Release;
            context.Response.Headers["X-Otomo-Channel"] = channel;
            return Results.Bytes(bytes, "application/gzip");
        });
    }

    // namespace -> the {"rows": [...]} document, for every table masters/otomo/namespaces.json lists.
    private static readonly Lazy<IReadOnlyDictionary<string, string>> RepositoryMasterDocuments = new(() =>
    {
        var root = KickFlight.BootstrapApi.RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        using var catalogue = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "masters", "otomo", "namespaces.json")));
        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in catalogue.RootElement.GetProperty("namespaces").EnumerateArray())
        {
            var file = Path.Combine(root, entry.GetProperty("source").GetString()!);
            documents[entry.GetProperty("namespace").GetString()!] = "{\"rows\": " + File.ReadAllText(file) + "}";
        }
        return documents;
    });

    private readonly ConcurrentDictionary<string, string> _replacedMasters = new(StringComparer.Ordinal);

    // Makes the release carry other rows for a table (by namespace, e.g. "kf.masters.stamp") than the file holds.
    public void ReplaceMasterRows(string ns, string rowsJson) => _replacedMasters[ns] = "{\"rows\": " + rowsJson + "}";

    private IEnumerable<KeyValuePair<string, string>> MasterDocuments() =>
        RepositoryMasterDocuments.Value.Select(pair =>
            new KeyValuePair<string, string>(pair.Key, _replacedMasters.GetValueOrDefault(pair.Key, pair.Value)));

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

        // The instance-declared fields session holds for this account (ext.session.player); see FakeOtomoSocial.Ext.cs.
        public System.Text.Json.Nodes.JsonObject Ext { get; set; } = new();

        // The item shown in each display slot, {slot: item_id}; see FakeOtomoSocial.Display.cs.
        public Dictionary<string, string> Display { get; } = new(StringComparer.Ordinal);

        // Kept so a test could assert recency; the follow lists are unordered here, as the contract does not promise
        // an order.
        public DateTimeOffset LastTouched { get; private set; } = DateTimeOffset.UtcNow;

        public void Touch() => LastTouched = DateTimeOffset.UtcNow;
    }

    private sealed record BoardRow(string PlayerId, int Points);

    public sealed record AwardCall(string PlayerId, string RuleType, string? Outcome, int? Delta, string Key, string RawBody);

    private sealed record SessionAward(int Delta, int PointsAfter);
}
