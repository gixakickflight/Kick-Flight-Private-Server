using System.Net;
using System.Text;
using System.Text.Json;
using KickFlight.BootstrapApi.Otomo;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The economy client on its own, with a handler that answers whatever a test scripts. This is where the wire
// contract is pinned: the exact path, the bearer token, the body economy's handler decodes, and the two rules the
// adapter promises - one retry for a 5xx or a transport failure with the same idempotency key, and never a retry for
// a 4xx.
public sealed class OtomoEconomyTests
{
    private const string Token = "player-access-token";

    [Fact]
    public async Task Wallet_reads_the_player_route_with_the_players_own_token()
    {
        var (economy, handler) = Build();
        handler.Queue(HttpStatusCode.OK, """{"player_id":"p1","balances":[{"currency":"jetcoin","balance":250},{"currency":"jetcoin.paid","balance":0}]}""");

        var wallet = await economy.GetWalletAsync(Token, CancellationToken.None);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("GET", call.Method);
        Assert.Equal("/api/player/economy/wallet", call.Path);
        Assert.Equal("Bearer " + Token, call.Authorization);
        Assert.Null(call.Body);
        Assert.Equal(250, wallet.Balances!.Single(balance => balance.Currency == "jetcoin").Balance);
        Assert.Equal("p1", wallet.PlayerId);
    }

    [Fact]
    public async Task Inventory_shop_and_pools_read_their_own_routes()
    {
        var (economy, handler) = Build();
        handler.Queue(HttpStatusCode.OK, """{"player_id":"p1","items":[{"item":"skin:2010101","qty":1,"stackable":false,"acquired_at":"2026-10-01T00:00:00Z","updated_at":"2026-10-01T00:00:00Z"}]}""");
        handler.Queue(HttpStatusCode.OK, """{"release":"r1","offers":[{"id":"kf.goods.gear-stamp.10","cost":[{"currency":"jetcoin","amount":100}],"grants":[{"item":"stamp.gear","amount":10}],"limit":{"count":3},"window":{},"available":true}]}""");
        handler.Queue(HttpStatusCode.OK, """{"release":"r1","pools":[{"id":"cosmetic.standard","cost":[{"currency":"jetcoin","amount":300}],"entries":[{"item":"skin:2010101","weight":1,"rarity":"common"}],"total_weight":1,"pity":{"after":0},"pity_misses":0,"window":{},"open":true}]}""");

        var inventory = await economy.GetInventoryAsync(Token, CancellationToken.None);
        var shop = await economy.GetShopAsync(Token, CancellationToken.None);
        var pools = await economy.GetPoolsAsync(Token, CancellationToken.None);

        Assert.Equal(new[] { "/api/player/economy/inventory", "/api/player/economy/shop", "/api/player/economy/gacha/pools" },
            handler.Calls.Select(call => call.Path).ToArray());
        Assert.Equal("skin:2010101", inventory.Items!.Single().Item);
        Assert.Equal(100, shop.Offers!.Single().Cost!.Single().Amount);
        Assert.True(pools.Pools!.Single().Open);
    }

    [Fact]
    public async Task Purchase_sends_the_offer_the_quantity_and_the_idempotency_key()
    {
        var (economy, handler) = Build();
        handler.Queue(HttpStatusCode.OK, """
            {"player_id":"p1","offer_id":"kf.goods.gear-stamp.10","quantity":2,
             "balances":[{"currency":"jetcoin","balance":300}],"items":[{"item":"stamp.gear","qty":20,"total":20}],"replayed":false}
            """);

        var purchase = await economy.PurchaseAsync(Token, "kf.goods.gear-stamp.10", 2, "key-1", CancellationToken.None);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("POST", call.Method);
        Assert.Equal("/api/player/economy/shop/purchase", call.Path);
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal("kf.goods.gear-stamp.10", body.RootElement.GetProperty("offer_id").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("quantity").GetInt64());
        Assert.Equal("key-1", body.RootElement.GetProperty("idempotency_key").GetString());
        // The exact field set economy's decoder accepts: it refuses an unknown field and it refuses an `amount`.
        Assert.Equal(new[] { "offer_id", "quantity", "idempotency_key" },
            body.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(20, purchase.Items!.Single().Total);
    }

    [Fact]
    public async Task A_roll_sends_the_pool_and_the_count_and_nothing_else()
    {
        var (economy, handler) = Build();
        handler.Queue(HttpStatusCode.OK, """
            {"player_id":"p1","pool_id":"cosmetic.standard","release":"r1","count":1,
             "draws":[{"item":"skin:2010101","rarity":"common","duplicate":false}],
             "balances":[],"items":[],"pity":0,"replayed":false}
            """);

        var roll = await economy.RollAsync(Token, "cosmetic.standard", 1, "roll-key", CancellationToken.None);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("/api/player/economy/gacha/roll", call.Path);
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal("cosmetic.standard", body.RootElement.GetProperty("pool_id").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("count").GetInt64());
        Assert.Equal("roll-key", body.RootElement.GetProperty("idempotency_key").GetString());
        Assert.Single(roll.Draws!);
    }

    [Fact]
    public async Task Tasks_and_a_claim_read_and_post_their_routes()
    {
        var (economy, handler) = Build();
        handler.Queue(HttpStatusCode.OK, """{"player_id":"p1","tracks":[{"track_id":"daily.play","counter":"battle.play","period":"d1","value":2,"repeat":false,"steps":[],"repetition":0}]}""");
        handler.Queue(HttpStatusCode.OK, """{"player_id":"p1","track_id":"daily.play","period":"d1","step":1,"target":1,"class":"mission.small","balances":[],"items":[],"replayed":false}""");

        var tasks = await economy.GetTasksAsync(Token, CancellationToken.None);
        var claim = await economy.ClaimAsync(Token, "daily.play", "claim-key", CancellationToken.None);

        Assert.Equal("/api/player/economy/tasks", handler.Calls[0].Path);
        Assert.Equal("/api/player/economy/tasks/claim", handler.Calls[1].Path);
        using var body = JsonDocument.Parse(handler.Calls[1].Body!);
        Assert.Equal("daily.play", body.RootElement.GetProperty("track_id").GetString());
        Assert.Equal("claim-key", body.RootElement.GetProperty("idempotency_key").GetString());
        Assert.Equal("daily.play", tasks.Tracks!.Single().TrackId);
        Assert.Equal(1, claim.Step);
    }

    [Fact]
    public async Task A_5xx_is_retried_once_with_the_same_body_and_key()
    {
        var (economy, handler) = Build();
        handler.Queue(HttpStatusCode.ServiceUnavailable, """{"error":{"code":"internal_error"}}""");
        handler.Queue(HttpStatusCode.OK, """{"player_id":"p1","offer_id":"kf.goods.disc-ticket","quantity":1,"balances":[],"items":[],"replayed":false}""");

        var purchase = await economy.PurchaseAsync(Token, "kf.goods.disc-ticket", 1, "same-key", CancellationToken.None);

        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal(handler.Calls[0].Body, handler.Calls[1].Body);
        Assert.Contains("same-key", handler.Calls[1].Body!);
        Assert.Equal("kf.goods.disc-ticket", purchase.OfferId);
    }

    [Fact]
    public async Task Two_5xx_in_a_row_are_reported_as_unavailable()
    {
        var (economy, handler) = Build();
        handler.Queue(HttpStatusCode.InternalServerError, """{"error":{"code":"internal_error"}}""");
        handler.Queue(HttpStatusCode.BadGateway, "not json at all");

        var failure = await Assert.ThrowsAsync<OtomoEconomyException>(() =>
            economy.GetWalletAsync(Token, CancellationToken.None));

        Assert.Equal(OtomoEconomyFailure.Unavailable, failure.Failure);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task A_4xx_is_an_answer_is_not_retried_and_carries_its_code()
    {
        var (economy, handler) = Build();
        handler.Queue(HttpStatusCode.Conflict, """{"error":{"code":"insufficient_funds","message":"insufficient funds"}}""");

        var failure = await Assert.ThrowsAsync<OtomoEconomyException>(() =>
            economy.PurchaseAsync(Token, "kf.goods.gear-stamp.10", 1, "key", CancellationToken.None));

        Assert.Equal(OtomoEconomyFailure.Refused, failure.Failure);
        Assert.Equal(409, failure.StatusCode);
        Assert.Equal("insufficient_funds", failure.Code);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task A_transport_failure_is_retried_once_and_then_reported_as_unavailable()
    {
        var (economy, handler) = Build();
        handler.FailTransport();

        var failure = await Assert.ThrowsAsync<OtomoEconomyException>(() =>
            economy.GetInventoryAsync(Token, CancellationToken.None));

        Assert.Equal(OtomoEconomyFailure.Unavailable, failure.Failure);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task A_disabled_client_refuses_before_any_request()
    {
        var (economy, handler) = Build(enabled: false);

        var failure = await Assert.ThrowsAsync<OtomoEconomyException>(() =>
            economy.GetWalletAsync(Token, CancellationToken.None));

        Assert.Equal(OtomoEconomyFailure.Disabled, failure.Failure);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task The_progress_report_goes_to_the_internal_route_with_the_service_key_and_a_ref()
    {
        var (economy, handler) = Build(progress: true, serviceKey: "game-server-key");
        handler.Queue(HttpStatusCode.OK, """{"player_id":"6f1d","counter":"battle.play","tracks":[],"key":"entry-1:battle.play","replayed":false}""");

        await economy.ReportProgressAsync(
            new EconomyProgressReport("6f1d", "battle.play", 1, "entry-1:battle.play", "battle_result", "entry-1"),
            CancellationToken.None);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("/internal/economy/progress", call.Path);
        Assert.Equal("Bearer game-server-key", call.Authorization);
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal("6f1d", body.RootElement.GetProperty("player_id").GetString());
        Assert.Equal("battle.play", body.RootElement.GetProperty("counter").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("delta").GetInt64());
        Assert.Equal("entry-1:battle.play", body.RootElement.GetProperty("key").GetString());
        Assert.Equal("entry-1", body.RootElement.GetProperty("ref").GetString());
        Assert.True(economy.ProgressEnabled);
    }

    [Fact]
    public async Task Without_a_service_key_the_progress_report_is_disabled_and_sends_nothing()
    {
        var (economy, handler) = Build(progress: true, serviceKey: null);

        Assert.False(economy.ProgressEnabled);
        var failure = await Assert.ThrowsAsync<OtomoEconomyException>(() =>
            economy.ReportProgressAsync(new EconomyProgressReport("p", "battle.play", 1, "k", "r", "r"),
                CancellationToken.None));
        Assert.Equal(OtomoEconomyFailure.Disabled, failure.Failure);
        Assert.Empty(handler.Calls);
    }

    private static (OtomoEconomy Economy, RecordingHandler Handler) Build(bool enabled = true, bool progress = false,
        string? serviceKey = null)
    {
        var handler = new RecordingHandler();
        var options = Options.Create(new OtomoEconomyOptions
        {
            Enabled = enabled,
            BaseUrl = "http://economy.test/",
            InternalBaseUrl = "http://economy-internal.test:8081/",
            RetryDelay = TimeSpan.Zero,
            ProgressEnabled = progress,
            ServiceKey = serviceKey ?? ""
        });
        return (new OtomoEconomy(new HttpClient(handler), options, NullLogger<OtomoEconomy>.Instance), handler);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();

        public List<(string Method, string Path, string? Authorization, string? Body)> Calls { get; } = [];

        private bool _failTransport;

        public void Queue(HttpStatusCode status, string body) =>
            _responses.Enqueue(() => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });

        public void FailTransport() => _failTransport = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.Method.Method, request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString(), body));

            if (_failTransport) throw new HttpRequestException("connection refused");
            if (_responses.Count == 0) throw new InvalidOperationException("no response was queued for this call");
            return _responses.Dequeue()();
        }
    }
}
