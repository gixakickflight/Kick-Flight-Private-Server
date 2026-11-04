using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace KickFlight.BootstrapApi.Tests;

// The facts and tracks half of the economy fake: what the Kick-Flight tenant declares for badges, as services/economy
// serves it.
//
//   POST /internal/economy/facts           a declared fact with its typed fields. The schema is closed both ways (an
//                                          undeclared field is 400 unknown_fact_field, an omitted one 400
//                                          missing_fact_field, a wrongly typed one 400 invalid_fact_field, an undeclared
//                                          fact 422 unknown_fact) and a key makes a retry count once.
//   POST /internal/economy/progress/gauge  the present value of a gauge counter; the answer carries value and best.
//   GET  /api/player/economy/tasks         every track of the player: value, best, mode, steps (reached/claimed/claimable)
//   POST /api/player/economy/tasks/claim   the player claims the next reached rung; its class grants honor:<id>
//
// This fake does NOT evaluate tracks from facts. Deciding which report counts and what it adds is the economy's job and
// is tested there (services/economy/internal/rules); a test here says "the track stands at 30" with SeedBadgeTrack, and
// asserts what the adapter reported with FactReports. The fact schemas below are the ones kf-otomo's economy.tasks
// declares (kf/tools/gen_badges.py), so an adapter that sends a field the tenant does not declare fails here as it would
// on the real service.
public sealed partial class FakeOtomoEconomy
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> FactSchemas =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["battle.result"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rule_type"] = "integer", ["kicker_id"] = "integer", ["queue"] = "text", ["credited"] = "boolean",
                ["credit_reason"] = "text", ["outcome"] = "text", ["won"] = "boolean", ["flagged"] = "boolean",
                ["mvp"] = "boolean", ["rank_rule"] = "integer", ["rank_after"] = "integer", ["kills"] = "integer",
                ["deaths"] = "integer", ["crystals_collected"] = "integer", ["guardian_released_crystals"] = "integer",
                ["crystals_released"] = "integer", ["flag_goals"] = "integer", ["flag_drops"] = "integer",
                ["ball_goals"] = "integer", ["ball_drops"] = "integer", ["moving_distance"] = "integer"
            },
            ["login.day"] = new Dictionary<string, string> { ["day"] = "text" },
            ["item.upgraded"] = new Dictionary<string, string> { ["item"] = "text", ["level"] = "integer" },
            ["like.received"] = new Dictionary<string, string> { ["battle"] = "text" },
            ["registration.completed"] = new Dictionary<string, string>()
        };

    // The three win-streak ladders (track badge.win_streak.<rule type> over gauge counter badge.streak.<rule type>).
    private static readonly Dictionary<int, int[]> StreakHonors = new()
    {
        [1] = [6010501, 6010502, 6010503], [2] = [6010801, 6010802, 6010803], [3] = [6011201, 6011202, 6011203]
    };

    private static readonly long[] StreakTargets = [3, 5, 10];

    public sealed class BadgeTrack
    {
        public string Id { get; init; } = "";
        public string? Counter { get; init; }
        public string Mode { get; init; } = "cumulative";
        public long Value { get; set; }
        public long Best { get; set; }
        public (long Target, string Class)[] Steps { get; init; } = [];
        public HashSet<int> Claimed { get; } = [];
    }

    public sealed record FactReport(string PlayerId, string Fact, string Key, JsonElement Fields, bool Replayed);

    public sealed record GaugeReport(string PlayerId, string Counter, long Value, string Key, bool Replayed);

    private readonly List<FactReport> _factReports = [];
    private readonly List<GaugeReport> _gaugeReports = [];

    // Every fact the adapter reported, in arrival order, whether or not its key was new.
    public IReadOnlyList<FactReport> FactReports { get { lock (_lock) return _factReports.ToArray(); } }

    public IReadOnlyList<GaugeReport> GaugeReports { get { lock (_lock) return _gaugeReports.ToArray(); } }

    // Puts a track in a player's tasks at a value, with the steps (target, reward class) it pays. A class named
    // badge.<id> grants the item honor:<id>, which is what the tenant's generated classes do.
    public void SeedBadgeTrack(string accountId, string trackId, long value, params (long Target, string Class)[] steps)
    {
        var player = _players.GetOrAdd(accountId, _ => new PlayerEconomy());
        lock (_lock)
        {
            player.Tracks[trackId] = new BadgeTrack { Id = trackId, Value = value, Best = value, Steps = steps };
        }
    }

    // Where a gauge counter stands for a player: (value now, peak).
    public (long Value, long Best) GaugeOf(string accountId, string counter)
    {
        lock (_lock)
        {
            var track = _players.TryGetValue(accountId, out var player)
                ? player.Tracks.Values.FirstOrDefault(row => row.Counter == counter)
                : null;
            return track is null ? (0, 0) : (track.Value, track.Best);
        }
    }

    public void SeedGauge(string accountId, string counter, long value, long best)
    {
        lock (_lock)
        {
            var track = GaugeTrack(_players.GetOrAdd(accountId, _ => new PlayerEconomy()), counter)!;
            track.Value = value;
            track.Best = best;
        }
    }

    private static BadgeTrack? GaugeTrack(PlayerEconomy player, string counter)
    {
        if (!counter.StartsWith("badge.streak.", StringComparison.Ordinal)
            || !int.TryParse(counter["badge.streak.".Length..], out var ruleType) || !StreakHonors.ContainsKey(ruleType))
            return null;
        var id = $"badge.win_streak.{ruleType}";
        if (!player.Tracks.TryGetValue(id, out var track))
        {
            track = new BadgeTrack
            {
                Id = id, Counter = counter, Mode = "gauge",
                Steps = StreakTargets.Select((target, index) => (target, $"badge.{StreakHonors[ruleType][index]}")).ToArray()
            };
            player.Tracks[id] = track;
        }
        return track;
    }

    private void MapBadges()
    {
        _app.MapPost("/internal/economy/facts", async (HttpContext context) =>
        {
            var (root, failure) = await InternalAsync(context);
            if (root is null) return failure;
            var playerId = root.Value.GetProperty("player_id").GetString() ?? "";
            var fact = root.Value.GetProperty("fact").GetString() ?? "";
            var key = root.Value.GetProperty("key").GetString() ?? "";
            var fields = root.Value.GetProperty("fields");
            if (!FactSchemas.TryGetValue(fact, out var schema))
                return Error(context, StatusCodes.Status422UnprocessableEntity, "unknown_fact", $"no fact {fact}");
            foreach (var field in fields.EnumerateObject())
            {
                if (!schema.ContainsKey(field.Name))
                    return Error(context, StatusCodes.Status400BadRequest, "unknown_fact_field", $"{fact}.{field.Name}");
            }
            foreach (var (name, type) in schema)
            {
                if (!fields.TryGetProperty(name, out var value))
                    return Error(context, StatusCodes.Status400BadRequest, "missing_fact_field", $"{fact}.{name}");
                var fits = type switch
                {
                    "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "text" => value.ValueKind == JsonValueKind.String,
                    "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                    _ => false
                };
                if (!fits)
                    return Error(context, StatusCodes.Status400BadRequest, "invalid_fact_field", $"{fact}.{name} is not {type}");
            }

            lock (_lock)
            {
                var replayKey = "fact:" + playerId + ":" + key;
                if (_internalReplies.TryGetValue(replayKey, out var first))
                {
                    _factReports.Add(new FactReport(playerId, fact, key, fields.Clone(), true));
                    return Replay(first);
                }
                _factReports.Add(new FactReport(playerId, fact, key, fields.Clone(), false));
                return Remember(replayKey, new { player_id = playerId, fact, tracks = Array.Empty<object>(), key });
            }
        });

        _app.MapPost("/internal/economy/progress/gauge", async (HttpContext context) =>
        {
            var (root, failure) = await InternalAsync(context);
            if (root is null) return failure;
            var playerId = root.Value.GetProperty("player_id").GetString() ?? "";
            var counter = root.Value.GetProperty("counter").GetString() ?? "";
            var key = root.Value.GetProperty("key").GetString() ?? "";
            if (root.Value.TryGetProperty("delta", out _))
                return Error(context, StatusCodes.Status400BadRequest, "invalid_body", "a gauge is set with value, never delta");
            var value = root.Value.GetProperty("value").GetInt64();
            if (value < 0)
                return Error(context, StatusCodes.Status400BadRequest, "invalid_request", "value must be zero or more");

            lock (_lock)
            {
                var player = _players.GetOrAdd(playerId, _ => new PlayerEconomy());
                var track = GaugeTrack(player, counter);
                if (track is null)
                    return Error(context, StatusCodes.Status422UnprocessableEntity, "unknown_counter", counter);
                var replayKey = "gauge:" + playerId + ":" + key;
                if (_internalReplies.TryGetValue(replayKey, out var first))
                {
                    _gaugeReports.Add(new GaugeReport(playerId, counter, value, key, true));
                    return Replay(first);
                }
                track.Value = value;
                track.Best = Math.Max(track.Best, value);
                _gaugeReports.Add(new GaugeReport(playerId, counter, value, key, false));
                return Remember(replayKey, new
                {
                    player_id = playerId, counter, key,
                    tracks = new[] { new { track_id = track.Id, period = "all", value = track.Value, best = track.Best } }
                });
            }
        });
    }

    // Reached means the track's measured number (the peak for a gauge) is at the target.
    private static bool Reached(BadgeTrack track, long target) => (track.Mode == "gauge" ? track.Best : track.Value) >= target;

    private IResult TasksOf(string account)
    {
        lock (_lock)
        {
            var player = _players.GetOrAdd(account, _ => new PlayerEconomy());
            foreach (var ruleType in StreakHonors.Keys) GaugeTrack(player, $"badge.streak.{ruleType}");
            var badges = player.Tracks.Values.OrderBy(track => track.Id, StringComparer.Ordinal).Select(track => new
            {
                track_id = track.Id,
                counter = track.Counter ?? "",
                period = "all",
                value = track.Value,
                best = track.Best,
                mode = track.Mode,
                repeat = false,
                steps = track.Steps.Select((step, index) => new
                {
                    step = index,
                    target = step.Target,
                    @class = step.Class,
                    reached = Reached(track, step.Target),
                    claimed = track.Claimed.Contains(index),
                    claimable = Reached(track, step.Target) && !track.Claimed.Contains(index)
                }).ToArray(),
                repetition = 0
            }).ToArray();

            // A served mission (EconomyMissions: 102 / task 10201) with its one step reached and not claimed, so a test
            // can see both the row and the badge it lights.
            var mission = new
            {
                track_id = "mission.daily.win",
                counter = "battle.win",
                period = "d04:2026-10-28",
                value = 1L,
                best = 1L,
                mode = "cumulative",
                repeat = false,
                steps = new[]
                {
                    new { step = 0, target = 1L, @class = "mission.daily.win.1", reached = true, claimed = false, claimable = true }
                },
                repetition = 0
            };
            return Results.Json(new { player_id = account, tracks = new object[] { mission }.Concat(badges).ToArray() });
        }
    }

    // The server picks the rung: the lowest one reached and not yet claimed. A claim key replays the first answer.
    private IResult ClaimTrack(HttpContext context, string account, string body)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var trackId = document.RootElement.TryGetProperty("track_id", out var t) ? t.GetString() ?? "" : "";
        var key = document.RootElement.TryGetProperty("idempotency_key", out var k) ? k.GetString() ?? "" : "";
        lock (_lock)
        {
            var replayKey = "claim:" + account + ":" + key;
            if (_internalReplies.TryGetValue(replayKey, out var first)) return Replay(first);
            var player = _players.GetOrAdd(account, _ => new PlayerEconomy());
            foreach (var ruleType in StreakHonors.Keys) GaugeTrack(player, $"badge.streak.{ruleType}");
            if (!player.Tracks.TryGetValue(trackId, out var track))
            {
                return trackId == "mission.daily.win"
                    ? Error(context, StatusCodes.Status409Conflict, "not_claimable", "track not claimable")
                    : Error(context, StatusCodes.Status404NotFound, "unknown_track", trackId);
            }
            var index = Enumerable.Range(0, track.Steps.Length)
                .FirstOrDefault(i => Reached(track, track.Steps[i].Target) && !track.Claimed.Contains(i), -1);
            if (index < 0) return Error(context, StatusCodes.Status409Conflict, "not_claimable", "track not claimable");

            var step = track.Steps[index];
            track.Claimed.Add(index);
            var moved = new List<object>();
            if (step.Class.StartsWith("badge.", StringComparison.Ordinal) && int.TryParse(step.Class[6..], out var honorId))
            {
                var item = "honor:" + honorId;
                player.Items[item] = player.Items.GetValueOrDefault(item) + 1;
                moved.Add(new { item, qty = 1L, total = player.Items[item] });
            }
            return Remember(replayKey, new
            {
                player_id = account,
                track_id = trackId,
                period = "all",
                step = index,
                target = step.Target,
                @class = step.Class,
                balances = player.Balances.Select(pair => new { currency = pair.Key, balance = pair.Value }).ToArray(),
                items = moved,
                duplicates = Array.Empty<object>(),
                entries = Array.Empty<object>()
            });
        }
    }
}
