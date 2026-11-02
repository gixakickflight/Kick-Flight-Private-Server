namespace KickFlight.BootstrapApi.Otomo;

// What this game calls the things otomo's economy holds.
//
// otomo is game-agnostic: an item id, a currency code, an offer id and a pool id are opaque strings there, and no
// KF vocabulary appears upstream. The translation therefore lives here, once, on the KF side - which is what the
// integration decision in docs/HANDOFF_OTOMO_2026-10-20.md §1 asks for - so a rename happens in one file and the
// client's own ids (the Item master's 1-8, the GoodsType values, the KickerCostume row ids) stay exactly what the
// client already expects.
//
// The names below are the ones otomo publishes for Kick-Flight (otomo/namespaces/economy in the game repo, PR #60),
// so the two halves agree by construction. A deployment must publish exactly these for the client's counters to
// mean anything:
//
//   currencies  jetcoin         the earned coin; the client's Item row 1 (JetCoinFree, 101)
//               kicker_ticket   the cosmetic pull's price; the client's Item row 7 (KickerGachaTicket, 401)
//               shard           cosmetic duplicates convert into it, and it buys a missing skin
//               event_coin      carried over between events
//   items       fragment        disc fragments; the client's Item row 6 (DiscFragment, 303)
//               disc:<discId>       a disc the player owns; owning it is level 10
//               skin:<costumeId>    a kicker costume, keyed by the KickerCostume master's ROW id;
//                                   owning a costume owns its kicker
//
// Five of the client's eight Item rows are served as a constant zero, because nothing pays them any more and a
// constant is the honest answer rather than a name invented for a counter that has no faucet:
//
//   row 2  JetCoinPaid       no IAP on this server: there is no paid coin
//   row 3  DiscForce         disc build-up is not ported: a disc is owned at level 10 or not at all
//   row 4  KickPoint          retired with the pre-EOS battle rewards
//   row 5  GearMaterial      the gear roll is still the adapter's own (SessionState.PendingGear)
//   row 8  DiscGachaTicket   the disc gacha is retired, so nothing earns or spends it
//
// Anything the economy holds that is not named here is not shown to the client: an unmapped item is logged and
// skipped rather than guessed at, because a wrong itemId puts a wrong number on a real screen.
public static class EconomyCatalog
{
    // Currencies, by the client's Item master row. These four are the legacy counters the startup payload has
    // always carried first.
    public static readonly (string Currency, int ItemId)[] CurrencyRows =
    [
        ("jetcoin", 1),
        // The kicker ticket is economy's currency rather than an item because a gacha pool is priced in
        // currencies; the client has always shown it as an Item row, so it is rendered as one.
        ("kicker_ticket", 7)
    ];

    // Stackable items, by the client's Item master row: the gear stamps the gear screen counts, the disc fragments,
    // and the two gacha tickets. The disc ticket is kept because the Item master still has the row (402); the disc
    // gacha that spent it is retired, so nothing spends it any more.
    public static readonly (string Item, int ItemId)[] ItemRows =
    [
        ("fragment", 6)
    ];

    // The client's Item master has exactly these eight rows, and the startup payload has always carried all eight
    // whether or not the player holds any. Keeping the row count is what keeps the counters screen stable.
    public static readonly int[] ItemMasterIds = [1, 2, 3, 4, 5, 6, 7, 8];

    // The GoodsType each Item master row is (served in the Item master itself): a shop or gacha price is expressed
    // to the client as a (goodsType, goodsId) pair, and goodsId is the Item row.
    public static readonly (int ItemId, int GoodsType)[] GoodsTypeRows =
    [
        (1, 101), (2, 102), (3, 302), (4, 701), (5, 202), (6, 303), (7, 401), (8, 402)
    ];

    // The goods shop's products (GoodsShopProductRow in DemoSessionApi.Shop.cs) and the economy offer each one buys.
    // The product id is the client's - it sends it in GoodsShopBuyRequestData.goodsShopProductId - and the offer id
    // is economy's; this table is the only place the two are related.
    // Only one of the four has an offer behind it. The other three sold what the economy design stopped selling:
    // a disc ticket (the disc gacha is retired), gear stamps and disc force (neither is ported). They stay in the
    // client's product list and answer "not for sale", which is the truth - the alternative, inventing offers so the
    // shelf looks full, would put four unpriced goods on a live screen.
    public static readonly (int ProductId, string OfferId)[] GoodsShopOffers =
    [
        (2, "ticket.single")
    ];

    // The one cosmetic pool in use, and the GachaKicker row id the client quotes back in
    // GachaKickerDrawRequestData.gachaId (through ResponseGacha.id). The disc gacha is retired, so nothing is mapped
    // on that side: its group is served as a banner with drawableCount 0.
    public const string CosmeticPoolId = "cosmetic.standard";

    public static readonly (int GachaId, string PoolId)[] KickerGachas = [(1, CosmeticPoolId)];

    public static bool TryGoodsTypeForItemId(int itemId, out int goodsType)
    {
        foreach (var row in GoodsTypeRows)
        {
            if (row.ItemId == itemId)
            {
                goodsType = row.GoodsType;
                return true;
            }
        }
        goodsType = 0;
        return false;
    }

    public static bool TryItemIdForCurrency(string? currency, out int itemId)
    {
        foreach (var row in CurrencyRows)
        {
            if (string.Equals(row.Currency, currency, StringComparison.Ordinal))
            {
                itemId = row.ItemId;
                return true;
            }
        }
        itemId = 0;
        return false;
    }

    public static bool TryItemIdForItem(string? item, out int itemId)
    {
        foreach (var row in ItemRows)
        {
            if (string.Equals(row.Item, item, StringComparison.Ordinal))
            {
                itemId = row.ItemId;
                return true;
            }
        }
        itemId = 0;
        return false;
    }

    // A disc the player owns: `disc:<discId>`, the client's Disc master row id.
    public static bool TryDiscId(string? item, out int discId)
    {
        discId = 0;
        return item is not null && item.StartsWith("disc:", StringComparison.Ordinal) &&
               int.TryParse(item.AsSpan(5), out discId) && discId > 0;
    }

    // A kicker costume: `skin:<kickerCostumeId>`, the KickerCostume master's row id.
    public static bool TryCostumeId(string? item, out int costumeId)
    {
        costumeId = 0;
        return item is not null && item.StartsWith("skin:", StringComparison.Ordinal) &&
               int.TryParse(item.AsSpan(5), out costumeId) && costumeId > 0;
    }

    public static string? OfferIdForProduct(int productId)
    {
        foreach (var row in GoodsShopOffers)
        {
            if (row.ProductId == productId) return row.OfferId;
        }
        return null;
    }

    public static int? ProductIdForOffer(string? offerId)
    {
        foreach (var row in GoodsShopOffers)
        {
            if (string.Equals(row.OfferId, offerId, StringComparison.Ordinal)) return row.ProductId;
        }
        return null;
    }

    public static string? PoolIdForGachaId(int gachaId)
    {
        foreach (var row in KickerGachas)
        {
            if (row.GachaId == gachaId) return row.PoolId;
        }
        return null;
    }

    public static int? GachaIdForPoolId(string? poolId)
    {
        foreach (var row in KickerGachas)
        {
            if (string.Equals(row.PoolId, poolId, StringComparison.Ordinal)) return row.GachaId;
        }
        return null;
    }
}
