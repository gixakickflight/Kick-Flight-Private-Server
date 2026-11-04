using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace KickFlight.BootstrapApi.Tests;

// The loadout half of the session fake: a player's Loadout, as services/session serves it.
//
//   GET /api/player/session/loadout                          the caller's loadout, `revision` in the document and in ETag;
//                                                            404 loadout_not_found before a first save (player token)
//   PUT /api/player/session/loadout                          If-Match is the revision last read ("0" for a first save):
//                                                            428 without it, 422 invalid_loadout {issues[{pointer,message}]}
//                                                            when the document breaks the tenant's schema, 409 revision_conflict
//                                                            {current} when the revision is stale; 200 with the stored document
//   GET /internal/session/players/{player_id}/loadout        another player's loadout, the same body as their own GET
//                                                            (service key); 404 loadout_not_found / player_not_found
//
// The schema is the one the KF tenant publishes: Fixtures/kf.session.loadout.json is the tenant fork's
// namespaces/session.loadout.json copied in whole, and the active version's schema is evaluated by MiniJsonSchema (no JSON
// Schema library is referenced here). The fake also owns `revision`, as session does: it is written into every document it
// returns and ignored in one it receives.
public sealed partial class FakeOtomoSocial
{
    private readonly ConcurrentQueue<LoadoutCall> _loadoutLog = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Func<JsonObject?, JsonObject>>> _concurrentSaves =
        new(StringComparer.Ordinal);
    private readonly JsonElement _loadoutSchema = LoadSchema();

    // Every loadout request that reached the fake, in arrival order: who, which route, the If-Match it carried and the status.
    public IReadOnlyList<LoadoutCall> LoadoutCalls => _loadoutLog.ToArray();

    public sealed record LoadoutCall(string PlayerId, string Method, bool Internal, string? IfMatch, int Status, string Body);

    public static string DefaultLoadoutRules() => File.ReadAllText(Path.Combine(
        KickFlight.BootstrapApi.RepositoryPaths.FindRoot(AppContext.BaseDirectory),
        "tests", "KickFlight.BootstrapApi.Tests", "Fixtures", "kf.session.loadout.json"));

    private static JsonElement LoadSchema()
    {
        using var rules = JsonDocument.Parse(DefaultLoadoutRules());
        var active = rules.RootElement.GetProperty("active_version").GetInt32().ToString();
        return rules.RootElement.GetProperty("versions").GetProperty(active).Clone();
    }

    // The revision and the loadout session holds for an account ((0, null) when it holds none).
    public (int Revision, string? Document) LoadoutOf(string accountId)
    {
        var account = Ensure(accountId);
        lock (_lock) return (account.LoadoutRevision, account.Loadout?.ToJsonString());
    }

    // Puts a loadout on an account directly (not through the validator), at the given revision, for a test that starts from a
    // known loadout.
    public void SeedLoadout(string accountId, string loadoutJson, int revision = 1)
    {
        var account = Ensure(accountId);
        lock (_lock)
        {
            account.Loadout = JsonNode.Parse(loadoutJson)!.AsObject();
            account.LoadoutRevision = revision;
        }
    }

    // Another writer saves this account's loadout just before the next `times` PUTs from it are checked, as a second device
    // or request would: `change` gets the stored loadout (null when there is none) and returns the one that is saved, at the
    // next revision. The PUT that follows then names a stale revision and is answered 409 with the changed loadout.
    public void SimulateConcurrentSave(string accountId, Func<JsonObject?, JsonObject> change, int times = 1)
    {
        var queue = _concurrentSaves.GetOrAdd(accountId, _ => new ConcurrentQueue<Func<JsonObject?, JsonObject>>());
        for (var index = 0; index < times; index++) queue.Enqueue(change);
    }

    private void MapLoadout()
    {
        _app.MapGet("/api/player/session/loadout", (HttpContext context) =>
        {
            var account = Identify(context, out var failure);
            if (account is null) return failure!;
            if (_extDown.GetValueOrDefault(account.PlayerId))
                return Logged(account.PlayerId, "GET", false, null, context, Error(context, 503, "unavailable", "session is down"), 503);
            lock (_lock)
            {
                if (account.Loadout is null)
                    return Logged(account.PlayerId, "GET", false, null, context,
                        Error(context, 404, "loadout_not_found", "no loadout is saved yet; send one with PUT and If-Match: 0"), 404);
                return Logged(account.PlayerId, "GET", false, null, context, LoadoutBody(account), 200);
            }
        });

        _app.MapPut("/api/player/session/loadout", async (HttpContext context) =>
        {
            var account = Identify(context, out var failure);
            if (account is null) return failure!;
            var ifMatch = context.Request.Headers["If-Match"].ToString();
            using var reader = new StreamReader(context.Request.Body);
            var text = await reader.ReadToEndAsync();

            IResult Done(int status, IResult result) => Logged(account.PlayerId, "PUT", false, ifMatch, context, result, status, text);

            if (_extDown.GetValueOrDefault(account.PlayerId)) return Done(503, Error(context, 503, "unavailable", "session is down"));
            if (string.IsNullOrWhiteSpace(ifMatch))
                return Done(428, Error(context, 428, "precondition_required", "If-Match is required: send the revision you last read, or 0 for a first save"));
            if (!int.TryParse(ifMatch.Trim().Trim('"'), out var expected) || expected < 0)
                return Done(400, Error(context, 400, "invalid_if_match", "If-Match must be a revision number"));

            // The schema is checked before the revision, as session does: a refusal changes nothing.
            JsonNode? parsed;
            try { parsed = JsonNode.Parse(text); }
            catch (JsonException) { parsed = null; }
            if (parsed is not JsonObject document)
                return Done(422, InvalidLoadout(context, [("", "the document must be a JSON object")]));
            document.Remove("revision");
            using var instance = JsonDocument.Parse(document.ToJsonString());
            if (!instance.RootElement.TryGetProperty("schema_version", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var versionNumber))
                return Done(422, InvalidLoadout(context, [("/schema_version", "must be a whole number")]));
            if (versionNumber != 1)
                return Done(422, Results.Json(new
                {
                    error = new { code = "unknown_schema_version", message = "that schema version is not known; see known_versions", request_id = context.TraceIdentifier },
                    known_versions = new[] { 1 }
                }, statusCode: 422));
            var issues = new List<(string Pointer, string Message)>();
            MiniJsonSchema.Validate(_loadoutSchema, instance.RootElement, "", issues);
            if (issues.Count > 0) return Done(422, InvalidLoadout(context, issues));

            lock (_lock)
            {
                if (_concurrentSaves.TryGetValue(account.PlayerId, out var queue) && queue.TryDequeue(out var change))
                {
                    account.Loadout = change(account.Loadout);
                    account.LoadoutRevision++;
                }
                if (account.Loadout is null && expected != 0)
                    return Done(409, Error(context, 409, "loadout_not_found", "there is no saved loadout at that revision; read it again or save with If-Match: 0"));
                if (account.Loadout is not null && expected != account.LoadoutRevision)
                {
                    context.Response.Headers.ETag = $"\"{account.LoadoutRevision}\"";
                    return Done(409, Results.Json(new
                    {
                        error = new { code = "revision_conflict", message = "the loadout changed since you read it; here is the current one", request_id = context.TraceIdentifier },
                        current = JsonNode.Parse(WithRevision(account).ToJsonString())
                    }, statusCode: 409));
                }
                account.Loadout = document;
                account.LoadoutRevision++;
                return Done(200, LoadoutBody(account));
            }
        });

        _app.MapGet("/internal/session/players/{playerId}/loadout", (HttpContext context, string playerId) =>
        {
            if (!Service(context, out var failure)) return failure!;
            if (_extDown.GetValueOrDefault(playerId))
                return Logged(playerId, "GET", true, null, context, Error(context, 503, "unavailable", "session is down"), 503);
            if (playerId.StartsWith("ghost-", StringComparison.Ordinal))
                return Logged(playerId, "GET", true, null, context, Error(context, 404, "player_not_found", "no such player"), 404);
            var account = Ensure(playerId);
            lock (_lock)
            {
                if (account.Loadout is null)
                    return Logged(playerId, "GET", true, null, context, Error(context, 404, "loadout_not_found", "no loadout is saved"), 404);
                return Logged(playerId, "GET", true, null, context, LoadoutBody(account), 200);
            }
        });
    }

    private IResult Logged(string playerId, string method, bool isInternal, string? ifMatch, HttpContext context, IResult result,
        int status, string body = "")
    {
        _loadoutLog.Enqueue(new LoadoutCall(playerId, method, isInternal, ifMatch, status, body));
        return result;
    }

    // The stored loadout with session's revision in it. Called under _lock.
    private static JsonObject WithRevision(Account account)
    {
        var document = JsonNode.Parse(account.Loadout!.ToJsonString())!.AsObject();
        document["revision"] = account.LoadoutRevision;
        return document;
    }

    private static IResult LoadoutBody(Account account) => new LoadoutResult(WithRevision(account).ToJsonString(), account.LoadoutRevision);

    private sealed class LoadoutResult(string json, int revision) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.ContentType = "application/json";
            httpContext.Response.Headers.ETag = $"\"{revision}\"";
            httpContext.Response.Headers.CacheControl = "no-store";
            await httpContext.Response.WriteAsync(json);
        }
    }

    private static IResult InvalidLoadout(HttpContext context, IEnumerable<(string Pointer, string Message)> issues) =>
        Results.Json(new
        {
            error = new { code = "invalid_loadout", message = "the loadout does not follow the schema; see issues", request_id = context.TraceIdentifier },
            issues = issues.Select(issue => new { pointer = issue.Pointer, message = issue.Message }).ToArray()
        }, statusCode: 422);
}

// The part of JSON Schema (draft 2020-12) that the tenant's loadout schema uses, evaluated against a document. A keyword it
// does not know is an error, so a schema that grows a keyword cannot be silently ignored by the fake.
internal static class MiniJsonSchema
{
    private static readonly HashSet<string> Annotations = ["$schema", "title", "description"];

    public static void Validate(JsonElement schema, JsonElement instance, string pointer, List<(string Pointer, string Message)> issues)
    {
        foreach (var keyword in schema.EnumerateObject())
        {
            var value = keyword.Value;
            switch (keyword.Name)
            {
                case var annotation when Annotations.Contains(annotation):
                    break;
                case "type":
                    if (!TypeMatches(value.GetString()!, instance)) issues.Add((pointer, $"type: want {value.GetString()}"));
                    break;
                case "const":
                    if (!instance.GetRawText().Equals(value.GetRawText(), StringComparison.Ordinal))
                        issues.Add((pointer, $"const: want {value.GetRawText()}"));
                    break;
                case "required":
                    if (instance.ValueKind == JsonValueKind.Object)
                        foreach (var name in value.EnumerateArray().Select(item => item.GetString()!))
                            if (!instance.TryGetProperty(name, out _)) issues.Add((pointer, $"missing property '{name}'"));
                    break;
                case "properties":
                    if (instance.ValueKind == JsonValueKind.Object)
                        foreach (var property in value.EnumerateObject())
                            if (instance.TryGetProperty(property.Name, out var member))
                                Validate(property.Value, member, $"{pointer}/{property.Name}", issues);
                    break;
                case "additionalProperties":
                    if (instance.ValueKind != JsonValueKind.Object) break;
                    var declared = schema.TryGetProperty("properties", out var props)
                        ? props.EnumerateObject().Select(p => p.Name).ToHashSet() : [];
                    foreach (var member in instance.EnumerateObject().Where(m => !declared.Contains(m.Name)))
                    {
                        if (value.ValueKind == JsonValueKind.False)
                            issues.Add((pointer, $"additional properties '{member.Name}' not allowed"));
                        else if (value.ValueKind == JsonValueKind.Object)
                            Validate(value, member.Value, $"{pointer}/{member.Name}", issues);
                    }
                    break;
                case "maxProperties":
                    if (instance.ValueKind == JsonValueKind.Object && instance.EnumerateObject().Count() > value.GetInt32())
                        issues.Add((pointer, $"maxProperties: got {instance.EnumerateObject().Count()}, want {value.GetInt32()}"));
                    break;
                case "propertyNames":
                    if (instance.ValueKind == JsonValueKind.Object)
                        foreach (var member in instance.EnumerateObject())
                            if (value.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(member.Name, pattern.GetString()!))
                                issues.Add((pointer, $"'{member.Name}' does not match pattern '{pattern.GetString()}'"));
                    break;
                case "items":
                    if (instance.ValueKind == JsonValueKind.Array)
                    {
                        var index = 0;
                        foreach (var item in instance.EnumerateArray()) Validate(value, item, $"{pointer}/{index++}", issues);
                    }
                    break;
                case "minItems":
                    if (instance.ValueKind == JsonValueKind.Array && instance.GetArrayLength() < value.GetInt32())
                        issues.Add((pointer, $"minItems: got {instance.GetArrayLength()}, want {value.GetInt32()}"));
                    break;
                case "maxItems":
                    if (instance.ValueKind == JsonValueKind.Array && instance.GetArrayLength() > value.GetInt32())
                        issues.Add((pointer, $"maxItems: got {instance.GetArrayLength()}, want {value.GetInt32()}"));
                    break;
                case "uniqueItems":
                    if (value.ValueKind == JsonValueKind.True && instance.ValueKind == JsonValueKind.Array &&
                        instance.EnumerateArray().Select(item => item.GetRawText()).Distinct().Count() != instance.GetArrayLength())
                        issues.Add((pointer, "items are not unique"));
                    break;
                case "minimum":
                    if (instance.ValueKind == JsonValueKind.Number && instance.GetDouble() < value.GetDouble())
                        issues.Add((pointer, $"minimum: got {instance.GetRawText()}, want {value.GetRawText()}"));
                    break;
                case "maximum":
                    if (instance.ValueKind == JsonValueKind.Number && instance.GetDouble() > value.GetDouble())
                        issues.Add((pointer, $"maximum: got {instance.GetRawText()}, want {value.GetRawText()}"));
                    break;
                case "allOf":
                    foreach (var branch in value.EnumerateArray()) Validate(branch, instance, pointer, issues);
                    break;
                case "if":
                    var condition = new List<(string, string)>();
                    Validate(value, instance, pointer, condition);
                    if (condition.Count == 0 && schema.TryGetProperty("then", out var then))
                        Validate(then, instance, pointer, issues);
                    break;
                case "then":
                    break; // evaluated with its "if"
                default:
                    throw new NotSupportedException($"MiniJsonSchema does not evaluate the keyword '{keyword.Name}'");
            }
        }
    }

    private static bool TypeMatches(string type, JsonElement instance) => type switch
    {
        "object" => instance.ValueKind == JsonValueKind.Object,
        "array" => instance.ValueKind == JsonValueKind.Array,
        "string" => instance.ValueKind == JsonValueKind.String,
        "integer" => instance.ValueKind == JsonValueKind.Number && instance.TryGetInt64(out _),
        _ => throw new NotSupportedException($"MiniJsonSchema does not evaluate the type '{type}'")
    };
}
