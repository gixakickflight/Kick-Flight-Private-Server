using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace KickFlight.BootstrapApi.Tests;

// A stand-in for otomo's patch recordings, real loopback HTTP, so an adapter test exercises the adapter's own client
// end to end (paths, bearers, multipart body, statuses, retry) exactly as FakeOtomoSocial does for session.
//
// Every rule here is one services/patch states (internal/api/recording.go, internal/recording/recording.go,
// internal/curated/curated.go at otomo staging de8325e), and none is an invention:
//
//   * PUT /internal/patch/recording/{id}: the game-server key as a bearer (401 otherwise); multipart with `kind`,
//     `sha256`, `size`, repeated `players` (1-64 distinct ids) and `tags` (at most 16 distinct), optional `meta` (a JSON
//     object up to 64 KiB) and the bytes in a FINAL `file` part; the digest and size are checked against the bytes
//     (400 digest_mismatch / size_mismatch). 201 on create, 200 for the identical upload again (players and tags are
//     unioned), 409 recording_conflict for a different one under a taken id.
//   * GET /patch/v1/recordings: a player token (the `sub`). No parameter: the recordings the caller was in. ?tag=: the
//     newest N with that tag, NOT scoped to the caller, [] for an unused tag. ?list=: a curated list in the published
//     order, entries whose recording is gone omitted, 404 for a name never published. Newest first with the id as the
//     tie-break; ?limit= 1..200 (a 400 otherwise); ?tag= with ?list= is a 400. The default page is 50; a curated list
//     is served whole unless a limit is sent.
//   * GET /patch/v1/recordings/{id}/blob: the bytes, ONLY to a player who was in the recording (404 otherwise, the same
//     answer as for no such recording), with Range support.
//
// Time is a counter, one second per upload, so "newest first" is deterministic whatever the test's wall clock does.
public sealed class FakeOtomoRecordings : IAsyncDisposable
{
    public const string ServiceKey = "test-patch-gameserver-key";

    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$");
    private static readonly Regex KindPattern = new(@"^[a-z0-9][a-z0-9_.-]{0,63}$");
    private static readonly Regex TagPattern = new(@"^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$");
    private static readonly Regex ShaPattern = new(@"^[0-9a-f]{64}$");
    private static readonly Regex ListNamePattern = new(@"^[a-z0-9][a-z0-9_.-]{0,63}$");

    private readonly WebApplication _app;
    private readonly object _lock = new();
    private readonly Dictionary<string, Stored> _recordings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _curated = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private DateTimeOffset _clock = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private volatile bool _down;

    private FakeOtomoRecordings(WebApplication app)
    {
        _app = app;
    }

    public string BaseUrl { get; private set; } = "";

    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    // Every route answers 503 and changes nothing, for the outage path.
    public void SetUnavailable(bool down) => _down = down;

    public static FakeOtomoRecordings Start()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var fake = new FakeOtomoRecordings(app);
        fake.Map();
        app.StartAsync().GetAwaiter().GetResult();
        fake.BaseUrl = (app.Urls.FirstOrDefault(url => !url.EndsWith(":0", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Kestrel did not report a bound address")).TrimEnd('/');
        return fake;
    }

    // A player access token whose `sub` is this account, in the shape the auth fake mints.
    public static string TokenFor(string accountId)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64(Encoding.UTF8.GetBytes("""{"alg":"EdDSA"}"""));
        var payload = B64(JsonSerializer.SerializeToUtf8Bytes(new { sub = accountId, aud = "player" }));
        return $"{header}.{payload}.{B64(Encoding.UTF8.GetBytes("signature"))}";
    }

    // The published patch.curated document, as one list: ids in the order to serve. A name that was never set is a 404.
    public void PublishCurated(string name, params string[] ids)
    {
        lock (_lock) _curated[name] = [.. ids];
    }

    public Stored? Get(string id)
    {
        lock (_lock) return _recordings.GetValueOrDefault(id);
    }

    public int Count
    {
        get { lock (_lock) return _recordings.Count; }
    }

    // A recording filed directly (another producer's, with whatever meta it likes), bypassing the upload route.
    public void Seed(string id, string[] tags, string[] players, string metaJson, byte[]? content = null)
    {
        lock (_lock)
        {
            _clock = _clock.AddSeconds(1);
            var bytes = content ?? Encoding.UTF8.GetBytes("seed:" + id);
            _recordings[id] = new Stored(id, "other.kind", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                JsonNode.Parse(metaJson)!, [.. players], [.. tags], _clock, bytes);
        }
    }

    // Removes a recording the way an operator's delete does, to prove a curated entry for it is omitted.
    public void Delete(string id)
    {
        lock (_lock) _recordings.Remove(id);
    }

    private void Map()
    {
        _app.MapPut("/internal/patch/recording/{id}", async (HttpContext context, string id) =>
        {
            Log(context);
            if (_down) return Error(context, 503, "unavailable", "patch is down");
            if (context.Request.Headers.Authorization.ToString() != "Bearer " + ServiceKey)
                return Error(context, 401, "invalid_key", "the service key is not usable");
            if (!IdPattern.IsMatch(id)) return Error(context, 400, "validation_failed", "bad recording id");
            if (!context.Request.HasFormContentType)
                return Error(context, 400, "validation_failed", "the body must be multipart/form-data");

            var boundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.Parse(context.Request.ContentType).Boundary).Value!;
            var reader = new MultipartReader(boundary, context.Request.Body);
            string? kind = null, sha = null, metaText = null;
            long size = 0;
            var players = new List<string>();
            var tags = new List<string>();
            byte[]? bytes = null;

            for (var section = await reader.ReadNextSectionAsync(); section is not null; section = await reader.ReadNextSectionAsync())
            {
                var disposition = ContentDispositionHeaderValue.Parse(section.ContentDisposition!);
                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                if (bytes is not null) return Error(context, 400, "validation_failed", "the file must be the final part");
                if (name == "file")
                {
                    using var memory = new MemoryStream();
                    await section.Body.CopyToAsync(memory);
                    bytes = memory.ToArray();
                    continue;
                }
                var value = await new StreamReader(section.Body, Encoding.UTF8).ReadToEndAsync();
                switch (name)
                {
                    case "kind": kind = value; break;
                    case "sha256": sha = value; break;
                    case "size": long.TryParse(value, out size); break;
                    case "players": players.Add(value); break;
                    case "tags": tags.Add(value); break;
                    case "meta": metaText = value; break;
                    default: return Error(context, 400, "validation_failed", $"unknown form field {name}");
                }
            }

            if (bytes is null) return Error(context, 400, "validation_failed", "the body must carry the bytes in a final file part");
            if (kind is null || !KindPattern.IsMatch(kind)) return Error(context, 400, "validation_failed", "bad kind");
            if (sha is null || !ShaPattern.IsMatch(sha)) return Error(context, 400, "validation_failed", "bad sha256");
            if (size <= 0) return Error(context, 400, "validation_failed", "bad size");
            if (players.Count is 0 or > 64 || players.Any(p => !IdPattern.IsMatch(p)) || players.Distinct().Count() != players.Count)
                return Error(context, 400, "validation_failed", "players must be 1-64 distinct ids");
            if (tags.Count > 16 || tags.Any(t => !TagPattern.IsMatch(t)) || tags.Distinct().Count() != tags.Count)
                return Error(context, 400, "validation_failed", "bad tags");
            JsonNode? meta;
            try
            {
                meta = JsonNode.Parse(string.IsNullOrEmpty(metaText) ? "{}" : metaText);
            }
            catch (JsonException)
            {
                return Error(context, 400, "validation_failed", "meta must be a JSON object");
            }
            if (meta is not JsonObject || (metaText?.Length ?? 0) > 64 << 10)
                return Error(context, 400, "validation_failed", "meta must be a JSON object of at most 64 KiB");
            if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != sha)
                return Error(context, 400, "digest_mismatch", "the bytes do not match the declared sha256");
            if (bytes.Length != size) return Error(context, 400, "size_mismatch", "the bytes are not the declared size");

            lock (_lock)
            {
                if (_recordings.TryGetValue(id, out var existing))
                {
                    if (existing.Sha256 != sha || existing.Kind != kind || !JsonNode.DeepEquals(existing.Meta, meta))
                        return Error(context, 409, "recording_conflict", "recording id is already taken by different content");
                    existing.Players = existing.Players.Union(players).ToList();
                    existing.Tags = existing.Tags.Union(tags).Order(StringComparer.Ordinal).ToList();
                    return Results.Json(new { recording = existing.ToWire() }, statusCode: 200);
                }
                _clock = _clock.AddSeconds(1);
                var stored = new Stored(id, kind, bytes.Length, sha, meta, [.. players], [.. tags.Order(StringComparer.Ordinal)], _clock, bytes);
                _recordings[id] = stored;
                return Results.Json(new { recording = stored.ToWire() }, statusCode: 201);
            }
        });

        _app.MapGet("/patch/v1/recordings", (HttpContext context) =>
        {
            Log(context);
            if (_down) return Error(context, 503, "unavailable", "patch is down");
            var player = PlayerOf(context);
            if (player is null) return Error(context, 401, "invalid_token", "a player token is required");

            var query = context.Request.Query;
            var limit = 0;
            var explicitLimit = query.ContainsKey("limit");
            if (explicitLimit && (!int.TryParse(query["limit"], out limit) || limit < 1 || limit > 200))
                return Error(context, 400, "validation_failed", "limit must be an integer between 1 and 200");
            string tag = query["tag"].ToString(), list = query["list"].ToString();
            if (tag != "" && list != "") return Error(context, 400, "validation_failed", "a list request takes either tag or list, not both");
            var page = explicitLimit ? limit : 50;

            lock (_lock)
            {
                IEnumerable<Stored> rows;
                if (list != "")
                {
                    if (!ListNamePattern.IsMatch(list) || !_curated.TryGetValue(list, out var ids))
                        return Error(context, 404, "not_found", "no such list");
                    if (explicitLimit && ids.Count > limit) ids = ids.Take(limit).ToList();
                    rows = ids.Where(_recordings.ContainsKey).Select(i => _recordings[i]);
                }
                else if (tag != "")
                {
                    if (!TagPattern.IsMatch(tag)) return Error(context, 400, "validation_failed", "bad tag");
                    rows = Newest(_recordings.Values.Where(r => r.Tags.Contains(tag))).Take(page);
                }
                else
                {
                    rows = Newest(_recordings.Values.Where(r => r.Players.Contains(player))).Take(page);
                }
                return Results.Json(new { recordings = rows.Select(r => r.ToWire()).ToArray() });
            }
        });

        // GET /internal/patch/recording/{id}/blob: the game-server key, no participant check, 404 recording_not_found
        // for missing or deleted, Range -> 206.
        _app.MapGet("/internal/patch/recording/{id}/blob", (HttpContext context, string id) =>
        {
            Log(context);
            if (_down) return Error(context, 503, "unavailable", "patch is down");
            if (context.Request.Headers.Authorization.ToString() != "Bearer " + ServiceKey)
                return Error(context, 401, "invalid_key", "the service key is not usable");
            Stored? stored;
            lock (_lock) stored = _recordings.GetValueOrDefault(id);
            if (stored is null) return Error(context, 404, "recording_not_found", "no such recording");
            context.Response.Headers["ETag"] = "\"" + stored.Sha256 + "\"";
            return Results.File(stored.Bytes, "application/octet-stream", enableRangeProcessing: true);
        });

        _app.MapGet("/patch/v1/recordings/{id}/blob", (HttpContext context, string id) =>
        {
            Log(context);
            if (_down) return Error(context, 503, "unavailable", "patch is down");
            var player = PlayerOf(context);
            if (player is null) return Error(context, 401, "invalid_token", "a player token is required");
            Stored? stored;
            lock (_lock) stored = _recordings.GetValueOrDefault(id);
            if (stored is null || !stored.Players.Contains(player))
                return Error(context, 404, "not_found", "no such recording");
            context.Response.Headers["ETag"] = "\"" + stored.Sha256 + "\"";
            context.Response.Headers["X-Otomo-Recording-Sha256"] = stored.Sha256;
            context.Response.Headers["X-Otomo-Recording-Kind"] = stored.Kind;
            context.Response.Headers["Cache-Control"] = "private, no-cache";
            return Results.File(stored.Bytes, "application/octet-stream", enableRangeProcessing: true);
        });
    }

    private static IEnumerable<Stored> Newest(IEnumerable<Stored> rows) =>
        rows.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id, StringComparer.Ordinal);

    private static string? PlayerOf(HttpContext context) => FakeOtomoEconomy.AccountOf(context.Request.Headers.Authorization);

    private void Log(HttpContext context) =>
        _requests.Enqueue(new RecordedRequest(context.Request.Method, context.Request.Path.Value ?? "",
            context.Request.QueryString.Value ?? "", context.Request.Headers.Authorization.ToString(),
            context.Request.Headers.Range.ToString()));

    private static IResult Error(HttpContext context, int status, string code, string message) =>
        Results.Json(new { error = new { code, message, request_id = context.TraceIdentifier } }, statusCode: status);

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    public sealed record RecordedRequest(string Method, string Path, string Query, string Authorization, string Range);

    public sealed class Stored
    {
        public Stored(string id, string kind, long size, string sha256, JsonNode meta, List<string> players,
            List<string> tags, DateTimeOffset createdAt, byte[] bytes)
        {
            Id = id; Kind = kind; Size = size; Sha256 = sha256; Meta = meta; Players = players; Tags = tags;
            CreatedAt = createdAt; Bytes = bytes;
        }

        public string Id { get; }
        public string Kind { get; }
        public long Size { get; }
        public string Sha256 { get; }
        public JsonNode Meta { get; }
        public List<string> Players { get; set; }
        public List<string> Tags { get; set; }
        public DateTimeOffset CreatedAt { get; }
        public byte[] Bytes { get; }

        // services/patch/internal/recording/recording.go:109-118: the keys, verbatim.
        public object ToWire() => new
        {
            id = Id, kind = Kind, size = Size, sha256 = Sha256, meta = JsonNode.Parse(Meta.ToJsonString()),
            players = Players.Order(StringComparer.Ordinal).ToArray(), tags = Tags.ToArray(), created_at = CreatedAt
        };
    }
}
