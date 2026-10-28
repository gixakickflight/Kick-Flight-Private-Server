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
public sealed class FakeOtomoEconomy : IAsyncDisposable
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

    // The catalogue a deployment publishes, as far as this suite needs it: the four goods-shop offers and one
    // cosmetic pool. The ids are the adapter's own mapping table (EconomyCatalog), which is the point - economy
    // knows them only as strings.
    private static readonly Dictionary<string, (Grant[] Cost, Grant[] Grants)> Offers = new(StringComparer.Ordinal)
    {
        ["kf.goods.disc-ticket"] = ([new Grant("jetcoin", null, 100)], [new Grant(null, "ticket.disc", 1)]),
        ["kf.goods.kicker-ticket"] = ([new Grant("jetcoin", null, 300)], [new Grant(null, "ticket.kicker", 1)]),
        ["kf.goods.gear-stamp.10"] = ([new Grant("jetcoin", null, 100)], [new Grant(null, "stamp.gear", 10)]),
        ["kf.goods.disc-force.1000"] = ([new Grant("jetcoin", null, 100)], [new Grant(null, "discforce", 1000)])
    };

    public const string CosmeticPoolId = "cosmetic.standard";

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
            var items = Snapshot(account).Items;
            return Results.Json(new
            {
                player_id = account,
                items = items.Select(pair => new
                {
                    item = pair.Key,
                    qty = pair.Value,
                    stackable = pair.Key is "stamp.gear" or "fragment.disc" or "ticket.kicker" or "ticket.disc",
                    acquired_at = "2026-10-01T12:00:00Z",
                    updated_at = "2026-10-01T12:00:00Z"
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
                pools = new[]
                {
                    new
                    {
                        id = CosmeticPoolId,
                        cost = new[] { new { currency = "jetcoin", item = (string?)null, amount = 300L } },
                        entries = new[]
                        {
                            new { item = "skin:2010101", weight = 90L, rarity = "common" },
                            new { item = "skin:2010102", weight = 10L, rarity = "rare" }
                        },
                        total_weight = 100L,
                        pity = new { after = 0L, rarity = "" },
                        pity_misses = 0L,
                        window = new { start = "", end = "" },
                        open = true
                    }
                }
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
            if (poolId != CosmeticPoolId)
            {
                return Error(context, StatusCodes.Status404NotFound, "unknown_pool", "that gacha pool does not exist");
            }

            lock (_lock)
            {
                var player = _players.GetOrAdd(account, _ => new PlayerEconomy());
                if (player.Balances.GetValueOrDefault("jetcoin") < 300 * count)
                {
                    return Error(context, StatusCodes.Status409Conflict, "insufficient_funds", "insufficient funds");
                }
                player.Balances["jetcoin"] = player.Balances.GetValueOrDefault("jetcoin") - 300 * count;

                var draws = new List<object>();
                var moved = new List<object>();
                for (var i = 0; i < count; i++)
                {
                    // The same entry every time, so a test can assert what a draw dropped without modelling weights:
                    // the weighting is economy's job and is tested there, not here.
                    const string item = "skin:2010101";
                    player.Items[item] = player.Items.GetValueOrDefault(item) + 1;
                    draws.Add(new
                    {
                        item,
                        rarity = "common",
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
                        track_id = "daily.play",
                        counter = "battle.play",
                        period = "d2026-10-28",
                        value = 0L,
                        repeat = false,
                        steps = new[]
                        {
                            new { step = 1, target = 1L, @class = "mission.small", reached = false, claimed = false, claimable = false }
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

    private (Dictionary<string, long> Balances, Dictionary<string, long> Items) Snapshot(string accountId)
    {
        lock (_lock)
        {
            var player = _players.GetOrAdd(accountId, _ => new PlayerEconomy());
            return (new Dictionary<string, long>(player.Balances, StringComparer.Ordinal),
                new Dictionary<string, long>(player.Items, StringComparer.Ordinal));
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
    }
}
