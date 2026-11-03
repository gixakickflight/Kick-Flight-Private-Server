using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace KickFlight.BootstrapApi.Tests;

// The ext half of the session fake: a player's loadout, as services/session serves it.
//
//   GET  /api/player/session/me                  the caller's own card, ext included (player token)
//   GET  /internal/session/players/{player_id}   another player's card: name, discriminator, named, ext (service key);
//                                                404 player_not_found for an id starting "ghost-"
//   POST /internal/session/players/ext           {player_id, ext, key, merge}: validated against the published
//                                                ext.session.player schema, idempotent by key per player, 400 invalid_ext
//                                                naming the field, 404 player_not_found
//
// The schema below is what the KF tenant publishes TODAY (decks, costume_gears) plus the two fields this change needs
// (active_deck, pending_gear), whose JSON Schema is written out in the change's report. additionalProperties is false,
// so a field the adapter invents is a 400 here exactly as it is on the real service.
public sealed partial class FakeOtomoSocial
{
    // The fields the schema declares, for a test that wants to assert nothing outside it is ever written.
    public static readonly string[] ExtFields = ["decks", "costume_gears", "active_deck", "pending_gear"];

    private readonly Dictionary<(string PlayerId, string Key), JsonObject> _extWrites = [];
    private readonly ConcurrentQueue<ExtWriteCall> _extLog = new();
    private readonly ConcurrentDictionary<string, bool> _extDown = new(StringComparer.Ordinal);

    // Every ext write that reached the fake (whether or not the key was new), in arrival order.
    public IReadOnlyList<ExtWriteCall> ExtWrites => _extLog.ToArray();

    public sealed record ExtWriteCall(string PlayerId, string Key, bool Merge, string Ext, bool Applied, int Status);

    // The ext session holds for an account, as JSON text ("{}" when it holds none).
    public string ExtOf(string accountId)
    {
        var account = Ensure(accountId);
        lock (_lock) return account.Ext.ToJsonString();
    }

    // Puts an ext on an account directly (not through the validator), for a test that starts from a known loadout.
    public void SeedExt(string accountId, string extJson)
    {
        var account = Ensure(accountId);
        lock (_lock) account.Ext = JsonNode.Parse(extJson)!.AsObject();
    }

    // The ext routes (read and write) of this account answer 503 and change nothing.
    public void SetExtUnavailable(string accountId, bool down) => _extDown[accountId] = down;

    private void MapExt()
    {
        _app.MapGet("/api/player/session/me", (HttpContext context) =>
        {
            var account = Identify(context, out var failure);
            if (account is null) return failure!;
            if (_extDown.GetValueOrDefault(account.PlayerId))
                return Error(context, StatusCodes.Status503ServiceUnavailable, "unavailable", "session is down");
            lock (_lock)
            {
                return Results.Json(new
                {
                    player_id = account.PlayerId,
                    display_name = account.DisplayName,
                    discriminator = account.Discriminator,
                    ext = JsonNode.Parse(account.Ext.ToJsonString())
                });
            }
        });

        _app.MapGet("/internal/session/players/{playerId}", (HttpContext context, string playerId) =>
        {
            if (!Service(context, out var failure)) return failure!;
            if (_extDown.GetValueOrDefault(playerId))
                return Error(context, StatusCodes.Status503ServiceUnavailable, "unavailable", "session is down");
            if (playerId.StartsWith("ghost-", StringComparison.Ordinal))
                return Error(context, StatusCodes.Status404NotFound, "player_not_found", "no such player");
            var account = Ensure(playerId);
            lock (_lock)
            {
                return Results.Json(new
                {
                    player_id = account.PlayerId,
                    display_name = account.DisplayName,
                    discriminator = account.Discriminator,
                    // Provisional until the player renames, which is what the fake's PATCH /me does.
                    named = !System.Text.RegularExpressions.Regex.IsMatch(account.DisplayName, @"^Player\d{4}$"),
                    ext = JsonNode.Parse(account.Ext.ToJsonString())
                });
            }
        });

        _app.MapPost("/internal/session/players/ext", async (HttpContext context) =>
        {
            if (!Service(context, out var failure)) return failure!;
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            var root = body.RootElement;
            var playerId = root.TryGetProperty("player_id", out var playerProp) ? playerProp.GetString() ?? "" : "";
            var key = root.TryGetProperty("key", out var keyProp) ? keyProp.GetString() ?? "" : "";
            var merge = root.TryGetProperty("merge", out var mergeProp) && mergeProp.ValueKind == JsonValueKind.True;
            var hasExt = root.TryGetProperty("ext", out var extProp) && extProp.ValueKind == JsonValueKind.Object;
            var extText = hasExt ? extProp.GetRawText() : "";

            IResult Done(int status, string code, string message, bool applied = false)
            {
                _extLog.Enqueue(new ExtWriteCall(playerId, key, merge, extText, applied, status));
                return Error(context, status, code, message);
            }

            if (playerId.Length == 0) return Done(400, "invalid_player_id", "player_id must be a UUID");
            if (string.IsNullOrWhiteSpace(key)) return Done(400, "missing_key", "key is required");
            if (!hasExt) return Done(400, "missing_ext", "ext is required");
            if (_extDown.GetValueOrDefault(playerId)) return Done(503, "unavailable", "session is down");
            if (ValidateExt(extProp) is { } problem) return Done(400, "invalid_ext", problem);
            if (playerId.StartsWith("ghost-", StringComparison.Ordinal)) return Done(404, "player_not_found", "no such player");

            var account = Ensure(playerId);
            lock (_lock)
            {
                bool applied;
                if (_extWrites.ContainsKey((playerId, key)))
                {
                    applied = false;
                }
                else
                {
                    var incoming = JsonNode.Parse(extText)!.AsObject();
                    if (merge)
                    {
                        foreach (var (name, value) in incoming)
                            account.Ext[name] = value?.DeepClone();
                    }
                    else
                    {
                        account.Ext = incoming;
                    }
                    _extWrites[(playerId, key)] = incoming;
                    applied = true;
                }
                _extLog.Enqueue(new ExtWriteCall(playerId, key, merge, extText, applied, 200));
                return Results.Json(new
                {
                    player_id = playerId,
                    ext = JsonNode.Parse(account.Ext.ToJsonString()),
                    applied
                });
            }
        });
    }

    // ext.session.player as the tenant publishes it plus this change's two fields. Returns the message session's 400
    // names the field with, or null when the value follows the schema.
    public static string? ValidateExt(JsonElement ext)
    {
        foreach (var property in ext.EnumerateObject())
        {
            var path = "/" + property.Name;
            switch (property.Name)
            {
                case "decks":
                    if (property.Value.ValueKind != JsonValueKind.Object) return $"ext: {path} must be an object";
                    foreach (var deck in property.Value.EnumerateObject())
                    {
                        if (!int.TryParse(deck.Name, out var number) || number is < 1 or > 999)
                            return $"ext: {path}/{deck.Name} is not a deck number from 1 to 999";
                        if (deck.Value.ValueKind != JsonValueKind.Array)
                            return $"ext: {path}/{deck.Name} must be an array";
                        var discs = deck.Value.EnumerateArray().ToList();
                        if (discs.Count > 8) return $"ext: {path}/{deck.Name} has more than 8 discs";
                        if (discs.Any(disc => !disc.TryGetInt64(out var id) || id < 1))
                            return $"ext: {path}/{deck.Name} holds a disc id that is not a positive integer";
                        if (discs.Select(disc => disc.GetInt64()).Distinct().Count() != discs.Count)
                            return $"ext: {path}/{deck.Name} repeats a disc";
                    }
                    break;
                case "costume_gears":
                    if (property.Value.ValueKind != JsonValueKind.Object) return $"ext: {path} must be an object";
                    foreach (var costume in property.Value.EnumerateObject())
                    {
                        if (!long.TryParse(costume.Name, out var costumeId) || costumeId < 1)
                            return $"ext: {path}/{costume.Name} is not a costume id";
                        if (costume.Value.ValueKind != JsonValueKind.Array || costume.Value.GetArrayLength() != 3 ||
                            costume.Value.EnumerateArray().Any(gear => !gear.TryGetInt64(out var id) || id < 0))
                            return $"ext: {path}/{costume.Name} must be exactly 3 integers >= 0";
                    }
                    break;
                case "active_deck":
                    if (!property.Value.TryGetInt64(out var active) || active is < 1 or > 999)
                        return $"ext: {path} must be an integer from 1 to 999";
                    break;
                case "pending_gear":
                    if (property.Value.ValueKind != JsonValueKind.Object) return $"ext: {path} must be an object";
                    foreach (var field in property.Value.EnumerateObject())
                    {
                        if (field.Name is not ("kicker_id" or "gear_id"))
                            return $"ext: {path}/{field.Name} is not a declared extension field";
                        if (!field.Value.TryGetInt64(out var value) || value < 0)
                            return $"ext: {path}/{field.Name} must be an integer >= 0";
                    }
                    if (!property.Value.TryGetProperty("kicker_id", out _) || !property.Value.TryGetProperty("gear_id", out _))
                        return $"ext: {path} requires kicker_id and gear_id";
                    break;
                default:
                    return $"ext: {path} is not a declared extension field";
            }
        }
        return null;
    }
}
