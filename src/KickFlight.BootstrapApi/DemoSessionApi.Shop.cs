using System.Text.Json;
using KickFlight.BootstrapApi.Otomo;

namespace KickFlight.BootstrapApi;

// The goods shop: the four products the client's shop screen offers, priced and granted by otomo's economy.
//
// Nothing here holds a stock. Whether a product is on the shelf and what it costs come from the shop document
// economy publishes; the purchase itself is POST /api/player/economy/shop/purchase with the player's own token, and
// the counters the answer repeats are read back from economy afterwards. The adapter's whole job is translation:
// the client's goodsShopProductId to an offer id (EconomyCatalog), and economy's item names back to the client's
// Item master ids.
public sealed partial class DemoSessionApi
{
    // ResponseGoodsShopProduct.purchasableCount is a display field - the client shows it, and the real limit is the
    // offer's own, which economy enforces on purchase. A product economy sells keeps the number the client has
    // always been served, so no screen changes; one economy does not sell says 0, which is what leaves its button
    // dead without the player finding out through a failed purchase.
    private const int GoodsShopAvailableCount = 9999;

    // Economy's MaxPurchaseQuantity: one purchase moves at most this many.
    private const int MaxPurchaseQuantity = 100;

    private sealed record GoodsShopProductRow(int Id, int GoodsShopType, string Name, int CostGoodsType, int CostGoodsId,
        int CostAmount, int GoodsType, int GoodsId, int GoodsAmount);

    // goodsShopType follows GoodsShopType in the dump (1 = DiscForce, 2 = Exchange). The Disc Force product is the
    // DiscForce tab; the three JetCoin exchanges live in the Exchange tab. The cost and grant here are the client's
    // display values and the mapping is atmosphere; what a purchase charges is the offer's price, from economy.
    private static readonly GoodsShopProductRow[] GoodsShopProducts =
    [
        new(1, 2, "Disc Gacha Ticket", 101, 1, 100, 402, 8, 1),
        new(2, 2, "Kicker Gacha Ticket", 101, 1, 300, 401, 7, 1),
        new(3, 2, "Gear Stamps x10", 101, 1, 100, 202, 5, 10),
        new(4, 1, "Disc Force x1000", 101, 1, 100, 302, 3, 1000),
    ];

    // ResponseGoodsShopProduct, field for field (goodsShopProductGoodsList carries exactly one grant here).
    private object[] BuildGoodsShopProducts(EconomySnapshot economy) =>
        GoodsShopProducts.Select(product =>
        {
            var offer = OfferFor(economy, product.Id);
            var cost = offer?.Cost?.FirstOrDefault(grant =>
                grant.Currency is not null && grant.Amount > 0 &&
                EconomyCatalog.TryItemIdForCurrency(grant.Currency, out var itemId) && itemId == 1);

            // The price the client shows is the offer's, so the shop cannot advertise one price and charge another;
            // the static row's price stands when economy does not sell the product at all.
            var costAmount = cost is { Amount: > 0 } priced
                ? (int)Math.Clamp(priced.Amount, 0, int.MaxValue)
                : product.CostAmount;

            return (object)new
            {
                id = product.Id,
                goodsShopType = product.GoodsShopType,
                name = product.Name,
                costGoodsType = product.CostGoodsType,
                costGoodsId = product.CostGoodsId,
                costAmount,
                resetType = 0,
                startDatetime = GachaStartDatetime,
                endDatetime = GachaEndDatetime,
                resetDatetime = "",
                limitedCount = offer is null ? 0 : (int)Math.Clamp(offer.Limit?.Count ?? 0, 0, int.MaxValue),
                purchasableCount = offer is { Available: true } ? GoodsShopAvailableCount : 0,
                newFlag = false,
                goodsShopProductGoodsList = new[]
                {
                    new { goodsType = product.GoodsType, goodsId = product.GoodsId, amount = product.GoodsAmount }
                }
            };
        }).ToArray();

    private static EconomyOffer? OfferFor(EconomySnapshot economy, int productId)
    {
        var offerId = EconomyCatalog.OfferIdForProduct(productId);
        return offerId is null ? null : economy.Offers.FirstOrDefault(offer => offer.Id == offerId);
    }

    private async Task<IResult?> TryHandleShopAsync(string path, HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
    {
        switch (path)
        {
            // GoodsShopReadResponseData / ShopOptionResponseData / ShopBirthdayResponseData declare no fields.
            case "/goodsShop/read":
                context.Response.Headers["x-app-status-code"] = "0";
                context.Response.Headers["x-kickflight-fixture"] = "dynamic-goods-shop-read";
                return BinaryJson("{}", key);

            case "/goodsShop/buy":
                return await HandleGoodsShopBuyAsync(context, ticket, state, key);

            case "/goodsShop/reset":
            {
                // The reset screen shows the counters and the shelf, so both are read for this request.
                var economy = await ReadPlayerEconomyAsync(ticket, EconomyRead.Balances | EconomyRead.Inventory | EconomyRead.Shop,
                    path, context.RequestAborted);
                if (economy.Snapshot is null) return EconomyUnavailable(context, path, economy.Error);
                var snapshot = economy.Snapshot!;

                context.Response.Headers["x-app-status-code"] = "0";
                context.Response.Headers["x-kickflight-fixture"] = "dynamic-goods-shop-reset";
                return BinaryJson(JsonSerializer.Serialize(new
                {
                    userItemList = Payloads(state).BuildUserItemList(snapshot),
                    goodsShopProductList = BuildGoodsShopProducts(snapshot)
                }), key);
            }

            case "/shop/option":
            case "/shop/birthday":
                context.Response.Headers["x-app-status-code"] = "0";
                context.Response.Headers["x-kickflight-fixture"] = "dynamic-shop-neutral";
                return BinaryJson("{}", key);

            // Real-money IAP. Nothing is purchasable, so every reply is the neutral shape of its DTO: the shop
            // screen opens and the purchase buttons stay dead.
            case "/shop/prepare":
                context.Response.Headers["x-app-status-code"] = "0";
                context.Response.Headers["x-kickflight-fixture"] = "dynamic-shop-neutral";
                return BinaryJson("""{"monthlyPurchaseLimitFlag":false,"monthlyPurchaseAlertFlag":false,"monthlyPurchaseAlertStorePrice":0.0}""", key);

            case "/shop/verify":
                context.Response.Headers["x-app-status-code"] = "0";
                context.Response.Headers["x-kickflight-fixture"] = "dynamic-shop-neutral";
                return BinaryJson("""
                    {"receivedUserCapsuleList":[],"receivedUserDiscList":[],"receivedUserHonorList":[],"receivedUserItemList":[],
                     "receivedUserStampList":[],"userPresentList":[],
                     "userPremium":{"premiumId":0,"startDatetime":"","endDatetime":""},
                     "userSeasonPass":{"seasonPassId":0,"startDatetime":"","endDatetime":""},
                     "receivedRewardList":[],"shopTypePurchaseCountList":[],"userMissionProgressList":[]}
                    """, key);

            default:
                return null;
        }
    }

    // A purchase is economy's: the offer is looked up from the product id, the price is the shop document's, and
    // the items granted are what economy says moved. The adapter never decides that a product costs anything.
    private async Task<IResult?> HandleGoodsShopBuyAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
    {
        var body = await ReadBodyAsync(context.Request);
        var productId = 0;
        // GoodsShopBuyRequestData declares only goodsShopProductId; a "count" is honoured when one is sent anyway.
        var count = 1;
        try
        {
            var plaintext = D2CCodec.Decode(body, key);
            using var document = JsonDocument.Parse(plaintext);
            productId = IntOf(document.RootElement, "goodsShopProductId");
            count = IntOf(document.RootElement, "count", 1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode goods shop buy request: {Error}", ex.Message);
            return StatusError(context, key);
        }

        var product = GoodsShopProducts.FirstOrDefault(row => row.Id == productId);
        var offerId = product is null ? null : EconomyCatalog.OfferIdForProduct(product.Id);
        if (product is null || offerId is null || count < 1)
        {
            _logger.LogWarning("Rejected goodsShop/buy for unknown product {ProductId} x{Count}", productId, count);
            return StatusError(context, key);
        }
        count = Math.Clamp(count, 1, MaxPurchaseQuantity);

        EconomyPurchase purchase;
        try
        {
            var accessToken = (await _identity.GetSessionForDeviceIdAsync(ticket.DeviceId, context.RequestAborted)).AccessToken;
            // A fresh key per accepted request: economy answers a replay for a key it has seen, and this decision has
            // not been made before. What the key protects is the adapter's own resend of *this* request, which
            // OtomoEconomy does with the same key (see its retry).
            purchase = await _economy.PurchaseAsync(accessToken, offerId, count, Guid.NewGuid().ToString(),
                context.RequestAborted);
        }
        catch (OtomoIdentityException ex)
        {
            return EconomyUnavailable(context, "/goodsShop/buy", ex);
        }
        catch (OtomoEconomyException ex) when (ex.Failure == OtomoEconomyFailure.Refused)
        {
            // economy said no: not enough of the cost, the offer's limit is reached, its window closed, or the
            // adapter asked for an offer the shop does not have. The client's shape for that is a non-zero
            // x-app-status-code; the reason goes to the log for the operator.
            _logger.LogWarning("goodsShop/buy of product {ProductId} (offer {Offer}) x{Count} was refused by economy: {Status} {Code}",
                productId, offerId, count, ex.StatusCode, ex.Code);
            return StatusError(context, key);
        }
        catch (OtomoEconomyException ex)
        {
            return EconomyUnavailable(context, "/goodsShop/buy", ex);
        }

        var economy = await ReadPlayerEconomyAsync(ticket, EconomyRead.Balances | EconomyRead.Inventory,
            "/goodsShop/buy", context.RequestAborted);
        // The purchase has already moved the value, so its answer is served either way: a counter read that failed
        // after a successful purchase must not turn into an error the player would answer by buying again.
        var snapshot = economy.Snapshot ?? SnapshotFromPurchase(purchase);
        if (economy.Snapshot is null)
        {
            _logger.LogError("goodsShop/buy for player {PlayerId} succeeded but the counters could not be re-read; " +
                "answering from the purchase's own result", state.UserId);
        }
        var payloads = Payloads(state);

        var response = new
        {
            dropKickerCostumeList = Array.Empty<object>(),
            receivedUserKickerList = Array.Empty<object>(),
            receivedUserCapsuleList = Array.Empty<object>(),
            receivedUserDiscList = Array.Empty<object>(),
            receivedUserHonorList = Array.Empty<object>(),
            receivedUserItemList = payloads.BuildReceivedUserItemList(purchase.Items),
            receivedUserStampList = Array.Empty<object>(),
            userItemList = payloads.BuildUserItemList(snapshot),
            userPresentList = Array.Empty<object>(),
            receivedRewardList = Array.Empty<object>()
        };

        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-goods-shop-buy";
        _logger.LogInformation(
            "goodsShop/buy product {ProductId} (offer {Offer}) x{Count} for user {UserId} via economy: granted {Items}{Replayed}",
            productId, offerId, count, state.UserId,
            string.Join(",", (purchase.Items ?? []).Select(move => $"{move.Item}+{move.Qty}")),
            purchase.Replayed ? " (replay)" : "");
        return BinaryJson(JsonSerializer.Serialize(response), key);
    }
}
