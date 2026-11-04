using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace KickFlight.BootstrapApi.Tests;

// A stand-in for otomo's economy service: the eight player-plane routes the adapter calls, the bodies its handlers
// answer (services/economy/internal/api/player.go) and the COM-5 error envelope, on loopback over real HTTP. Nothing
// is faked at the HttpClient level, so an adapter test exercises the adapter's own client end to end: the path, the
// bearer token, the bodies, the status codes and the retry.
//
// One instance is shared by the suite (OtomoTestEnvironment.Economy) and its state is per player, keyed by the `sub`
// of the access token the adapter presents - so two test sessions never see each other's wallet, exactly as the real
// service derives the player from the token and never from the request.
//
// Every request it answers is recorded, which is how a test asserts that a purchase really carried an offer id and an
// idempotency key rather than the adapter believing it did.
public sealed partial class FakeOtomoEconomy : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, PlayerEconomy> _players = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly object _lock = new();
    private int _failures;

    private FakeOtomoEconomy(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; private set; }

    // How many of the next requests answer 503 before the service behaves again. The adapter's retry is what makes
    // a single 503 invisible to the client; two in a row is a failure it has to report.
    public int FailNextRequests
    {
        get => Volatile.Read(ref _failures);
        set => Volatile.Write(ref _failures, value);
    }

    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    // When true, POST /internal/economy/items/advance answers 503 and moves nothing. It is how a test proves the
    // badge fact comes from the advance's answer rather than from anything the adapter decided locally.
    public bool AdvanceUnavailable { get; set; }

    // The advances that were asked for, in order, as the service received them.
    public IReadOnlyList<RecordedRequest> AdvanceRequests =>
        _requests.Where(request => request.Path == "/internal/economy/items/advance").ToArray();

    // The level a possession stands at in this fake. A seeded or granted item starts at 0, which every reader treats
    // as "the game has set nothing".
    public long LevelOf(string accountId, string item)
    {
        lock (_lock)
        {
            return _players.TryGetValue(accountId, out var player) ? player.Levels.GetValueOrDefault(item) : 0;
        }
    }

    // Gives a possession a level directly, for a test that wants to start above the rarity's first rung.
    public void SeedLevel(string accountId, string item, long level)
    {
        var player = _players.GetOrAdd(accountId, _ => new PlayerEconomy());
        lock (_lock)
        {
            player.Levels[item] = level;
        }
    }

    public static FakeOtomoEconomy Start()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var server = new FakeOtomoEconomy(app, "");
        server.Map();
        app.StartAsync().GetAwaiter().GetResult();

        var address = app.Urls.FirstOrDefault(url => !url.EndsWith(":0", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Kestrel did not report a bound address (urls: {string.Join(", ", app.Urls)})");
        server.BaseUrl = address.TrimEnd('/');
        return server;
    }

    // The account a token belongs to, or null when the token is not one this suite's auth server minted.
    public static string? AccountOf(string? authorization)
    {
        const string prefix = "Bearer ";
        if (authorization is null || !authorization.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var parts = authorization[prefix.Length..].Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("sub", out var subject) ? subject.GetString() : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    public void Seed(string accountId, IEnumerable<(string Currency, long Amount)>? balances = null,
        IEnumerable<(string Item, long Qty)>? items = null)
    {
        var player = _players.GetOrAdd(accountId, _ => new PlayerEconomy());
        lock (_lock)
        {
            foreach (var (currency, amount) in balances ?? []) player.Balances[currency] = amount;
            foreach (var (item, qty) in items ?? []) player.Items[item] = qty;
        }
    }

    public long BalanceOf(string accountId, string currency)
    {
        lock (_lock)
        {
            return _players.TryGetValue(accountId, out var player) ? player.Balances.GetValueOrDefault(currency) : 0;
        }
    }

    public long ItemsOf(string accountId, string item)
    {
        lock (_lock)
        {
            return _players.TryGetValue(accountId, out var player) ? player.Items.GetValueOrDefault(item) : 0;
        }
    }

    public void ClearRequests() => _requests.Clear();

    public sealed record Grant(string? Currency, string? Item, long Amount);

    // The catalogue a deployment publishes, as far as this suite needs it: the one goods-shop offer with something
    // behind it, the skin shop, and the cosmetic pool. The ids are the same strings the published namespace files
    // carry (otomo/namespaces/economy), which is the point - economy knows them only as strings.
    private static readonly Dictionary<string, (Grant[] Cost, Grant[] Grants)> Offers = new(StringComparer.Ordinal)
    {
        ["ticket.single"] = ([new Grant("jetcoin", null, 100)], [new Grant("kicker_ticket", null, 1)]),
        ["shard.skin.2010101"] = ([new Grant("shard", null, 60)], [new Grant(null, "skin:2010101", 1)])
    };

    public const string CosmeticPoolId = "cosmetic.standard";

    // The pools this fake rolls: what one draw costs in jetcoin, the bundle count that is sold for that single price
    // (economy's Pool.Bundle), and the one item every draw lands on, so a test can assert what a draw dropped without
    // modelling weights - the weighting is economy's job and is tested there, not here.
    private static readonly Dictionary<string, (long Price, long? Bundle, string Item, string Rarity)> Pools = new()
    {
        [CosmeticPoolId] = (300, null, "skin:2010101", "common"),
        ["disc.standard"] = (300, null, "disc:3010014", "r"),
        ["disc.eleven"] = (3000, 11, "disc:3010014", "r")
    };

    private void Map()
    {
        _app.MapGet("/api/player/economy/wallet", async (HttpContext context) =>
        {
            var (account, failure) = await IdentifyAsync(context);
            if (account is null) return failure;
            var balances = Snapshot(account).Balances;
            return Results.Json(new
            {
                player_id = account,
                balances = balances.Select(pair => new { currency = pair.Key, balance = pair.Value }).ToArray()
            });
        });

        _app.MapGet("/api/player/economy/inventory", async (HttpContext context) =>
        {
            var (account, failure) = await IdentifyAsync(context);
            if (account is null) return failure;
            var snapshot = Snapshot(account);
            return Results.Json(new
            {
                player_id = account,
                items = snapshot.Items.Select(pair => new
                {
                    item = pair.Key,
                    qty = pair.Value,
                    stackable = pair.Key is "fragment",
                    acquired_at = "2026-10-01T12:00:00Z",
                    updated_at = "2026-10-01T12:00:00Z",
                    // The possession's own state, which is where a disc's level lives: attrs.level.
                    attrs = new
                    {
                        level = snapshot.Levels.GetValueOrDefault(pair.Key),
                        counter = 0L,
                        available_at = (string?)null
                    }
                }).ToArray()
            });
        });

        _app.MapGet("/api/player/economy/shop", async (HttpContext context) =>
        {
            var (account, failure) = await IdentifyAsync(context);
            if (account is null) return failure;
            return Results.Json(new
            {
                release = "test",
                offers = Offers.Select(pair => new
                {
                    id = pair.Key,
                    cost = pair.Value.Cost.Select(grant => new { currency = grant.Currency, item = grant.Item, amount = grant.Amount }).ToArray(),
                    grants = pair.Value.Grants.Select(grant => new { currency = grant.Currency, item = grant.Item, amount = grant.Amount }).ToArray(),
                    limit = new { count = 3L, window = "168h" },
                    window = new { start = "", end = "" },
                    available = true
                }).ToArray()
            });
        });

        _app.MapGet("/api/player/economy/gacha/pools", async (HttpContext context) =>
        {
            var (account, failure) = await IdentifyAsync(context);
            if (account is null) return failure;
            return Results.Json(new
            {
                release = "test",
                pools = Pools.Select(pair => new
                {
                    id = pair.Key,
                    cost = new[] { new { currency = "jetcoin", item = (string?)null, amount = pair.Value.Price } },
                    entries = pair.Key == CosmeticPoolId
                        ? new[]
                        {
                            new { item = "skin:2010101", weight = 90L, rarity = "common" },
                            new { item = "skin:2010102", weight = 10L, rarity = "rare" }
                        }
                        : new[] { new { item = pair.Value.Item, weight = 100L, rarity = pair.Value.Rarity } },
                    total_weight = 100L,
                    pity = new { after = 0L, rarity = "" },
                    pity_misses = 0L,
                    window = new { start = "", end = "" },
                    open = true
                }).ToArray()
            });
        });

        _app.MapPost("/api/player/economy/shop/purchase", async (HttpContext context) =>
        {
            var (account, failure) = await IdentifyAsync(context);
            if (account is null) return failure;

            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            var root = body.RootElement;
            var offerId = root.TryGetProperty("offer_id", out var offerProp) ? offerProp.GetString() ?? "" : "";
            var quantity = root.TryGetProperty("quantity", out var qtyProp) && qtyProp.TryGetInt64(out var qty) ? qty : 1;
            if (!Offers.TryGetValue(offerId, out var offer))
            {
                return Error(context, StatusCodes.Status404NotFound, "unknown_offer", "the shop does not sell that offer");
            }

            lock (_lock)
            {
                var player = _players.GetOrAdd(account, _ => new PlayerEconomy());
                foreach (var cost in offer.Cost)
                {
                    if (player.Balances.GetValueOrDefault(cost.Currency!) < cost.Amount * quantity)
                    {
                        return Error(context, StatusCodes.Status409Conflict, "insufficient_funds", "insufficient funds");
                    }
                }

                foreach (var cost in offer.Cost)
                {
                    player.Balances[cost.Currency!] = player.Balances.GetValueOrDefault(cost.Currency) - cost.Amount * quantity;
                }

                var moved = new List<object>();
                foreach (var grant in offer.Grants)
                {
                    var amount = grant.Amount * quantity;
                    if (grant.Currency is not null)
                    {
                        player.Balances[grant.Currency] = player.Balances.GetValueOrDefault(grant.Currency) + amount;
                    }
                    if (grant.Item is not null)
                    {
                        player.Items[grant.Item] = player.Items.GetValueOrDefault(grant.Item) + amount;
                        moved.Add(new { item = grant.Item, qty = amount, total = player.Items[grant.Item] });
                    }
                }

                return Results.Json(new
                {
                    player_id = account,
                    offer_id = offerId,
                    quantity,
                    balances = player.Balances.Select(pair => new { currency = pair.Key, balance = pair.Value }).ToArray(),
                    items = moved,
                    replayed = false
                });
            }
        });

        _app.MapPost("/api/player/economy/gacha/roll", async (HttpContext context) =>
        {
            var (account, failure) = await IdentifyAsync(context);
            if (account is null) return failure;

            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            var root = body.RootElement;
            var poolId = root.TryGetProperty("pool_id", out var poolProp) ? poolProp.GetString() ?? "" : "";
            var count = root.TryGetProperty("count", out var countProp) && countProp.TryGetInt64(out var wanted) ? wanted : 1;
            if (poolId.StartsWith("capsule.", StringComparison.Ordinal))
            {
                var idempotencyKey = root.TryGetProperty("idempotency_key", out var keyProp) ? keyProp.GetString() ?? "" : "";
                return RollCapsulePool(context, account, poolId, count, idempotencyKey);
            }
            if (!Pools.TryGetValue(poolId, out var pool))
            {
                return Error(context, StatusCodes.Status404NotFound, "unknown_pool", "that gacha pool does not exist");
            }

            lock (_lock)
            {
                var player = _players.GetOrAdd(account, _ => new PlayerEconomy());
                var price = pool.Bundle == count ? pool.Price : pool.Price * count;
                if (player.Balances.GetValueOrDefault("jetcoin") < price)
                {
                    return Error(context, StatusCodes.Status409Conflict, "insufficient_funds", "insufficient funds");
                }
                player.Balances["jetcoin"] = player.Balances.GetValueOrDefault("jetcoin") - price;

                var draws = new List<object>();
                var moved = new List<object>();
                for (var i = 0; i < count; i++)
                {
                    var item = pool.Item;
                    player.Items[item] = player.Items.GetValueOrDefault(item) + 1;
                    draws.Add(new
                    {
                        item,
                        rarity = pool.Rarity,
                        duplicate = false,
                        paid_currency = (string?)null,
                        paid_amount = 0L,
                        from_pity = false
                    });
                    moved.Add(new { item, qty = 1L, total = player.Items[item] });
                }

                return Results.Json(new
                {
                    player_id = account,
                    pool_id = poolId,
                    release = "test",
                    count,
                    draws,
                    balances = player.Balances.Select(pair => new { currency = pair.Key, balance = pair.Value }).ToArray(),
                    items = moved,
                    pity = 0,
                    replayed = false
                });
            }
        });

        _app.MapGet("/api/player/economy/tasks", async (HttpContext context) =>
        {
            var (account, failure) = await IdentifyAsync(context);
            if (account is null) return failure;
            return Results.Json(new
            {
                player_id = account,
                tracks = new[]
                {
                    new
                    {
                        // A served mission (EconomyMissions: 102 / task 10201) with its one step reached and not
                        // claimed, so a test can see both the row and the badge it lights.
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
                    },
                    // A badge track as the service lists it: auto_claim, so its steps are paid by the report and read claimed.
                    new
                    {
                        track_id = "badge.login_days",
                        counter = "",
                        period = "all",
                        value = 30L,
                        best = 30L,
                        mode = "cumulative",
                        repeat = false,
                        steps = new[]
                        {
                            new { step = 0, target = 30L, @class = "badge.6010101", reached = true, claimed = true, claimable = false }
                        },
                        repetition = 0
                    }
                }
            });
        });

        _app.MapPost("/api/player/economy/tasks/claim", async (HttpContext context) =>
        {
            var (account, failure) = await IdentifyAsync(context);
            if (account is null) return failure;
            return Error(context, StatusCodes.Status409Conflict, "not_claimable", "track not claimable");
        });

        MapBadges();

        // POST /internal/economy/items/advance: one value of one possession moved by a positive amount, up to the
        // ceiling the caller names. Idempotent on `key`, like the other two; it never creates a possession, so an item
        // the player does not hold is refused rather than added.
        _app.MapPost("/internal/economy/items/advance", async (HttpContext context) =>
        {
            var (root, failure) = await InternalAsync(context);
            if (root is null) return failure;
            if (AdvanceUnavailable)
            {
                return Error(context, StatusCodes.Status503ServiceUnavailable, "internal_error", "internal server error");
            }

            var playerId = root.Value.GetProperty("player_id").GetString() ?? "";
            var key = root.Value.GetProperty("key").GetString() ?? "";
            var itemId = root.Value.GetProperty("item_id").GetString() ?? "";
            var advance = root.Value.GetProperty("advance");
            var field = advance.GetProperty("field").GetString() ?? "";
            var by = advance.GetProperty("by").GetInt64();
            var max = advance.GetProperty("max").GetInt64();

            lock (_lock)
            {
                if (_internalReplies.TryGetValue(key, out var first)) return Replay(first);
                var player = _players.GetOrAdd(playerId, _ => new PlayerEconomy());
                if (player.Items.GetValueOrDefault(itemId) <= 0)
                {
                    return Error(context, StatusCodes.Status409Conflict, "insufficient_items",
                        $"{itemId}: the player does not hold it");
                }
                if (field != "level")
                {
                    return Error(context, StatusCodes.Status400BadRequest, "invalid_body",
                        "this fake only moves a level");
                }

                var value = player.Levels.GetValueOrDefault(itemId) + by;
                if (max > 0 && value > max)
                {
                    return Error(context, StatusCodes.Status409Conflict, "attrs_above_max", "attrs above max");
                }
                player.Levels[itemId] = value;
                return Remember(key, new
                {
                    player_id = playerId,
                    item = itemId,
                    attrs = new { level = value, counter = 0L, available_at = (string?)null },
                    value
                });
            }
        });

        // The internal plane: the caller names the player and authenticates with the game-server key. Both routes
        // are idempotent on `key` - a replay answers the first call's body with replayed = true and moves nothing -
        // which is the property the adapter's retries and the level-up's double-tap guard depend on.
        _app.MapPost("/internal/economy/grants", async (HttpContext context) =>
        {
            var (root, failure) = await InternalAsync(context);
            if (root is null) return failure;
            var playerId = root.Value.GetProperty("player_id").GetString() ?? "";
            var key = root.Value.GetProperty("key").GetString() ?? "";
            var grantClass = root.Value.GetProperty("class").GetString() ?? "";
            if (!Classes.TryGetValue(grantClass, out var grants))
            {
                return Error(context, StatusCodes.Status404NotFound, "unknown_class", "no such grant class");
            }

            lock (_lock)
            {
                if (_internalReplies.TryGetValue(key, out var first)) return Replay(first);
                var player = _players.GetOrAdd(playerId, _ => new PlayerEconomy());
                var entries = new List<object>();
                var moved = new List<object>();
                foreach (var grant in grants)
                {
                    if (grant.Currency is not null)
                    {
                        player.Balances[grant.Currency] = player.Balances.GetValueOrDefault(grant.Currency) + grant.Amount;
                        entries.Add(new { owner_kind = "player", currency = grant.Currency, delta = grant.Amount });
                    }
                    if (grant.Item is not null)
                    {
                        player.Items[grant.Item] = player.Items.GetValueOrDefault(grant.Item) + grant.Amount;
                        moved.Add(new { item = grant.Item, qty = grant.Amount, total = player.Items[grant.Item] });
                    }
                }
                return Remember(key, new
                {
                    player_id = playerId,
                    key,
                    @class = grantClass,
                    balances = player.Balances.Select(pair => new { currency = pair.Key, balance = pair.Value }).ToArray(),
                    entries,
                    items = moved,
                    duplicates = Array.Empty<object>()
                });
            }
        });

        _app.MapPost("/internal/economy/consume", async (HttpContext context) =>
        {
            var (root, failure) = await InternalAsync(context);
            if (root is null) return failure;
            var playerId = root.Value.GetProperty("player_id").GetString() ?? "";
            var key = root.Value.GetProperty("key").GetString() ?? "";
            var items = root.Value.GetProperty("items").EnumerateArray()
                .Select(row => (Item: row.GetProperty("item").GetString()!, Qty: row.GetProperty("qty").GetInt64())).ToList();
            var currencies = root.Value.GetProperty("currencies").EnumerateArray()
                .Select(row => (Currency: row.GetProperty("currency").GetString()!, Amount: row.GetProperty("amount").GetInt64()))
                .ToList();

            lock (_lock)
            {
                if (_internalReplies.TryGetValue(key, out var first)) return Replay(first);
                var player = _players.GetOrAdd(playerId, _ => new PlayerEconomy());
                foreach (var (item, qty) in items)
                {
                    var held = player.Items.GetValueOrDefault(item);
                    if (held < qty)
                    {
                        return Error(context, StatusCodes.Status409Conflict, "insufficient_items",
                            $"{item}: holds {held}, needs {qty}");
                    }
                }
                foreach (var (currency, amount) in currencies)
                {
                    if (player.Balances.GetValueOrDefault(currency) < amount)
                    {
                        return Error(context, StatusCodes.Status409Conflict, "insufficient_funds", "insufficient funds");
                    }
                }

                var moved = new List<object>();
                foreach (var (item, qty) in items)
                {
                    var total = player.Items.GetValueOrDefault(item) - qty;
                    if (total == 0)
                    {
                        player.Items.Remove(item);
                        player.Levels.Remove(item);
                    }
                    else
                    {
                        player.Items[item] = total;
                    }
                    moved.Add(new { item, qty, total });
                }
                foreach (var (currency, amount) in currencies)
                {
                    player.Balances[currency] = player.Balances.GetValueOrDefault(currency) - amount;
                }
                return Remember(key, new
                {
                    player_id = playerId,
                    key,
                    items = moved,
                    balances = currencies
                        .Select(cost => new { currency = cost.Currency, balance = player.Balances[cost.Currency] }).ToArray(),
                    consumed = new { items = items.Count, currencies = currencies.Count }
                });
            }
        });
        // --- entitlements (services/economy/internal/api/entitlements.go) ----------------------------------------------
        // The KF tenant declares exactly one kind, capsule, with max_open 3. A create is idempotent by (player, key) and
        // answers the stored row (409 idempotency_key_reused when the key made another kind or rule) before the cap is
        // consulted; a claim pays the row's class once, keyed by the entitlement id, and is refused before available_at.
        _app.MapPost("/internal/economy/entitlements", async (HttpContext context) =>
        {
            var (root, failure) = await InternalAsync(context);
            if (root is null) return failure;
            var playerId = root.Value.GetProperty("player_id").GetString() ?? "";
            var kind = root.Value.GetProperty("kind").GetString() ?? "";
            var rule = root.Value.GetProperty("rule").GetString() ?? "";
            var source = root.Value.TryGetProperty("source", out var sourceProp) ? sourceProp.GetString() ?? "" : "";
            var key = root.Value.TryGetProperty("key", out var keyProp) ? keyProp.GetString() ?? "" : "";
            var availableAt = DateTimeOffset.UtcNow;
            if (root.Value.TryGetProperty("available_at", out var availableProp) && availableProp.ValueKind == JsonValueKind.String &&
                !DateTimeOffset.TryParse(availableProp.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out availableAt))
            {
                return Error(context, StatusCodes.Status400BadRequest, "invalid_body", "available_at must be an RFC3339 timestamp");
            }
            var meta = root.Value.TryGetProperty("meta", out var metaProp) && metaProp.ValueKind == JsonValueKind.Object
                ? JsonSerializer.SerializeToElement(metaProp) : JsonSerializer.SerializeToElement(new { });

            if (kind != EntitlementKind)
                return Error(context, StatusCodes.Status422UnprocessableEntity, "kind_unknown", "the entitlement kind is not declared by this deployment");
            if (!Classes.ContainsKey(rule))
                return Error(context, StatusCodes.Status422UnprocessableEntity, "class_unknown", "the rule names no configured reward class");

            lock (_lock)
            {
                var rows = _entitlements.GetOrAdd(playerId, _ => []);
                if (key.Length > 0 && rows.FirstOrDefault(row => row.Key == key) is { } existing)
                {
                    if (existing.Kind != kind || existing.Rule != rule)
                        return Error(context, StatusCodes.Status409Conflict, "idempotency_key_reused", "the key already made another entitlement");
                    return Results.Json(EntitlementJson(existing));
                }
                if (rows.Count(row => row.Kind == kind && row.ClaimedAt is null) >= EntitlementMaxOpen)
                    return Error(context, StatusCodes.Status409Conflict, "entitlement_cap", $"{kind} already holds {EntitlementMaxOpen} unclaimed");
                var created = new Entitlement
                {
                    Id = Guid.NewGuid().ToString(), PlayerId = playerId, Kind = kind, Rule = rule, Source = source,
                    AvailableAt = availableAt, Meta = meta, Key = key, CreatedAt = DateTimeOffset.UtcNow, Sequence = rows.Count
                };
                rows.Add(created);
                return Results.Json(EntitlementJson(created));
            }
        });

        _app.MapGet("/internal/economy/entitlements", (HttpContext context) =>
        {
            var failure = InternalGet(context);
            if (failure is not null) return failure;
            var playerId = context.Request.Query["player_id"].ToString();
            var includeClaimed = string.Equals(context.Request.Query["include_claimed"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
            lock (_lock)
            {
                var rows = _entitlements.GetValueOrDefault(playerId) ?? [];
                return Results.Json(new
                {
                    player_id = playerId,
                    entitlements = rows.Where(row => !_hiddenFromList.GetValueOrDefault(playerId) && (includeClaimed || row.ClaimedAt is null))
                        .OrderByDescending(row => row.Sequence).Select(EntitlementJson).ToArray()
                });
            }
        });

        _app.MapPost("/internal/economy/entitlements/{id}/claim", async (HttpContext context, string id) =>
        {
            var (root, failure) = await InternalAsync(context);
            if (root is null) return failure;
            var playerId = root.Value.GetProperty("player_id").GetString() ?? "";
            lock (_lock)
            {
                var row = _entitlements.GetValueOrDefault(playerId)?.FirstOrDefault(candidate => candidate.Id == id);
                if (row is null)
                    return Error(context, StatusCodes.Status404NotFound, "entitlement_not_found", "no such entitlement for this player");
                if (row.ClaimedAt is not null)
                    return Error(context, StatusCodes.Status409Conflict, "entitlement_claimed", "entitlement already claimed");
                if (DateTimeOffset.UtcNow < row.AvailableAt)
                    return Error(context, StatusCodes.Status409Conflict, "entitlement_not_ready", $"claimable from {row.AvailableAt:O}");

                var payoutKey = "entitlement:" + row.Id;
                var applied = !_internalReplies.ContainsKey(payoutKey);
                var player = _players.GetOrAdd(playerId, _ => new PlayerEconomy());
                if (applied)
                {
                    foreach (var grant in Classes[row.Rule])
                    {
                        if (grant.Currency is not null)
                            player.Balances[grant.Currency] = player.Balances.GetValueOrDefault(grant.Currency) + grant.Amount;
                        if (grant.Item is not null)
                            player.Items[grant.Item] = player.Items.GetValueOrDefault(grant.Item) + grant.Amount;
                    }
                    _internalReplies[payoutKey] = JsonSerializer.SerializeToElement(new { });
                }
                row.ClaimedAt = DateTimeOffset.UtcNow;
                return Results.Json(new
                {
                    applied,
                    balances = player.Balances.Select(pair => new { currency = pair.Key, balance = pair.Value }).ToArray(),
                    items = Array.Empty<object>()
                });
            }
        });
    }

    private readonly ConcurrentDictionary<string, bool> _hiddenFromList = new(StringComparer.Ordinal);

    /// <summary>The list route answers empty for this player: a read that predates rows another request has just made.</summary>
    public void HideEntitlementsFromList(string accountId, bool hidden) => _hiddenFromList[accountId] = hidden;

    public const string EntitlementKind = "capsule";
    public const int EntitlementMaxOpen = 3;

    private readonly ConcurrentDictionary<string, List<Entitlement>> _entitlements = new(StringComparer.Ordinal);

    private sealed class Entitlement
    {
        public string Id { get; init; } = "";
        public string PlayerId { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Rule { get; init; } = "";
        public string Source { get; init; } = "";
        public string Key { get; init; } = "";
        public DateTimeOffset AvailableAt { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? ClaimedAt { get; set; }
        public JsonElement Meta { get; init; }
        public int Sequence { get; init; }
    }

    private static object EntitlementJson(Entitlement row) => new
    {
        id = row.Id,
        player_id = row.PlayerId,
        kind = row.Kind,
        rule = row.Rule,
        source = row.Source,
        available_at = row.AvailableAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
        claimed_at = row.ClaimedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
        meta = row.Meta,
        created_at = row.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
    };

    /// <summary>The player's entitlements as economy holds them (claimed ones too), oldest first.</summary>
    public IReadOnlyList<(string Id, string Rule, string Source, DateTimeOffset AvailableAt, bool Claimed, string Meta)>
        EntitlementsOf(string accountId)
    {
        lock (_lock)
        {
            return (_entitlements.GetValueOrDefault(accountId) ?? []).OrderBy(row => row.Sequence)
                .Select(row => (row.Id, row.Rule, row.Source, row.AvailableAt, row.ClaimedAt is not null, row.Meta.GetRawText()))
                .ToList();
        }
    }

    /// <summary>Makes a player's open entitlement claimable now, as time passing would.</summary>
    public void MakeDue(string accountId)
    {
        lock (_lock)
        {
            foreach (var row in _entitlements.GetValueOrDefault(accountId) ?? [])
            {
                row.AvailableAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            }
        }
    }

    // capsule.<type>: one capsule_<type> token per draw, idempotent by key; the one item every draw lands on is a disc.
    private IResult RollCapsulePool(HttpContext context, string account, string poolId, long count, string idempotencyKey)
    {
        var token = "capsule_" + poolId["capsule.".Length..];
        lock (_lock)
        {
            var replayKey = $"roll:{account}:{idempotencyKey}";
            if (idempotencyKey.Length > 0 && _internalReplies.TryGetValue(replayKey, out var first)) return Replay(first);
            var player = _players.GetOrAdd(account, _ => new PlayerEconomy());
            if (player.Balances.GetValueOrDefault(token) < count)
                return Error(context, StatusCodes.Status409Conflict, "insufficient_funds", "insufficient funds");
            player.Balances[token] = player.Balances.GetValueOrDefault(token) - count;

            var draws = new List<object>();
            var moved = new List<object>();
            for (var i = 0; i < count; i++)
            {
                var item = "disc:3010014";
                player.Items[item] = player.Items.GetValueOrDefault(item) + 1;
                draws.Add(new { item, rarity = "r", duplicate = false, paid_currency = (string?)null, paid_amount = 0L, from_pity = false });
                moved.Add(new { item, qty = 1L, total = player.Items[item] });
            }
            var reply = new
            {
                player_id = account,
                pool_id = poolId,
                release = "test",
                count,
                draws,
                balances = player.Balances.Select(pair => new { currency = pair.Key, balance = pair.Value }).ToArray(),
                items = moved,
                pity = 0
            };
            if (idempotencyKey.Length == 0) return Results.Json(WithReplayed(JsonSerializer.SerializeToElement(reply), false));
            return Remember(replayKey, reply);
        }
    }

    // An internal-plane GET: recorded and authenticated like the POSTs, with no body to read.
    private IResult? InternalGet(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        _requests.Enqueue(new RecordedRequest(context.Request.Method, context.Request.Path.Value ?? "", authorization,
            context.Request.QueryString.Value));
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) || authorization.Length <= "Bearer ".Length)
            return Error(context, StatusCodes.Status401Unauthorized, "invalid_key", "a service key is required");
        if (Interlocked.Decrement(ref _failures) >= 0)
            return Error(context, StatusCodes.Status503ServiceUnavailable, "internal_error", "internal server error");
        return null;
    }


    // What the published classes pay, as far as this suite needs them (otomo/namespaces/economy, release 18).
    private static readonly Dictionary<string, Grant[]> Classes = new(StringComparer.Ordinal)
    {
        ["starter.disc_force"] = [new Grant("disc_force", null, StarterDiscForce)],
        // The 15 chat stamps; chat's attachment ownership check asks for stamp:<id>.
        ["starter.stamps"] = Enumerable.Range(6051001, 15).Select(id => new Grant(null, "stamp:" + id, 1)).ToArray(),
        ["tutorial.disc"] = [new Grant(null, "disc:3010013", 1)],
        ["tutorial.kicker"] = [new Grant(null, "skin:2010201", 1)],
        // A battle capsule's class pays one capsule_<type> token (the capsule.<type> pool costs one per draw).
        ["match.capsule.wooden"] = [new Grant("capsule_wooden", null, 1)],
        ["match.capsule.standard"] = [new Grant("capsule_standard", null, 1)],
        ["match.capsule.golden"] = [new Grant("capsule_golden", null, 1)]
    };

    public const long StarterDiscForce = 300;

    private readonly Dictionary<string, JsonElement> _internalReplies = new(StringComparer.Ordinal);

    private IResult Remember(string key, object reply)
    {
        var element = JsonSerializer.SerializeToElement(reply);
        _internalReplies[key] = element;
        return Results.Json(WithReplayed(element, false));
    }

    private static IResult Replay(JsonElement first) => Results.Json(WithReplayed(first, true));

    private static Dictionary<string, JsonElement> WithReplayed(JsonElement reply, bool replayed)
    {
        var body = reply.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        body["replayed"] = JsonSerializer.SerializeToElement(replayed);
        return body;
    }

    // Records an internal-plane request and parses its body. The game-server key is required but not checked
    // against a value: which key a deployment holds is configuration, not something this suite is testing.
    private async Task<(JsonElement? Root, IResult Failure)> InternalAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var authorization = context.Request.Headers.Authorization.ToString();
        _requests.Enqueue(new RecordedRequest(context.Request.Method, context.Request.Path.Value ?? "", authorization, body));
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) || authorization.Length <= "Bearer ".Length)
        {
            return (null, Error(context, StatusCodes.Status401Unauthorized, "invalid_key", "a service key is required"));
        }
        if (Interlocked.Decrement(ref _failures) >= 0)
        {
            return (null, Error(context, StatusCodes.Status503ServiceUnavailable, "internal_error", "internal server error"));
        }
        using var document = JsonDocument.Parse(body);
        return (document.RootElement.Clone(), Results.Empty);
    }

    // Identifies the caller the way the service does - from the token - and records the request. The body is read
    // through a buffering stream so the handler after this can parse it again.
    private async Task<(string? Account, IResult Failure)> IdentifyAsync(HttpContext context)
    {
        context.Request.EnableBuffering();
        string? body = null;
        if (context.Request.Body.CanRead)
        {
            // The body is read whether or not a Content-Length came with it: JsonContent sent over a chunked stream
            // has none, and a request recorded without its body could not be asserted on.
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            var text = await reader.ReadToEndAsync();
            body = string.IsNullOrEmpty(text) ? null : text;
            context.Request.Body.Position = 0;
        }
        _requests.Enqueue(new RecordedRequest(context.Request.Method, context.Request.Path.Value ?? "",
            context.Request.Headers.Authorization.ToString(), body));

        if (Interlocked.Decrement(ref _failures) >= 0)
        {
            return (null, Error(context, StatusCodes.Status503ServiceUnavailable, "internal_error", "internal server error"));
        }

        var account = AccountOf(context.Request.Headers.Authorization.ToString());
        return account is null
            ? (null, Error(context, StatusCodes.Status401Unauthorized, "invalid_token", "the token is not usable"))
            : (account, Results.Empty);
    }

    private (Dictionary<string, long> Balances, Dictionary<string, long> Items, Dictionary<string, long> Levels)
        Snapshot(string accountId)
    {
        lock (_lock)
        {
            var player = _players.GetOrAdd(accountId, _ => new PlayerEconomy());
            return (new Dictionary<string, long>(player.Balances, StringComparer.Ordinal),
                new Dictionary<string, long>(player.Items, StringComparer.Ordinal),
                new Dictionary<string, long>(player.Levels, StringComparer.Ordinal));
        }
    }

    private static IResult Error(HttpContext context, int status, string code, string message) =>
        Results.Json(new { error = new { code, message, request_id = context.TraceIdentifier } }, statusCode: status);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public sealed record RecordedRequest(string Method, string Path, string? Authorization, string? Body);

    private sealed class PlayerEconomy
    {
        public Dictionary<string, long> Balances { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> Items { get; } = new(StringComparer.Ordinal);
        // item id -> the level of that possession, which is attrs.level on the real service's row.
        public Dictionary<string, long> Levels { get; } = new(StringComparer.Ordinal);
    }
}
