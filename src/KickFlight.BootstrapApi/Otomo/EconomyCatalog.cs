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
//               disc_force      what a disc level-up costs; the client's Item row 3 (DiscForce, 302)
//               capsule_<type>  one token per disc a battle's capsule holds; not a counter (the slot shows it)
//               shard           cosmetic duplicates convert into it, and it buys a missing skin
//               event_coin      carried over between events
//   items       fragment        disc fragments; the client's Item row 6 (DiscFragment, 303)
//               disc:<discId>       a disc the player owns; its level is attrs.level on that possession
//               skin:<costumeId>    a kicker costume, keyed by the KickerCostume master's ROW id;
//                                   owning a costume owns its kicker
//
// Three of the client's eight Item rows are served as a constant zero, because nothing pays them any more and a
// constant is the honest answer rather than a name invented for a counter that has no faucet:
//
//   row 2  JetCoinPaid       no IAP on this server: there is no paid coin
//   row 4  KickPoint          retired with the pre-EOS battle rewards
//   row 8  DiscGachaTicket   the disc gacha is priced in Jet Coin, so nothing earns or spends it
//
// The gear chip (GearMaterial, 202) is economy's gear_chip currency: gear/create spends one per roll.
//
// Anything the economy holds that is not named here is not shown to the client: an unmapped item is logged and
// skipped rather than guessed at, because a wrong itemId puts a wrong number on a real screen.
public static class EconomyCatalog
{
    // Disc Force by name, because two routes spend or show it on their own: /disc/buildup pays a level with it and
    // answers its remaining balance as a single userItem.
    public const string DiscForceCurrency = "disc_force";

    // The client's own Item ids: goodsType * 10000 + 1, the scheme its art is keyed by (ui/goods/thumbnail_<id>) and the
    // one ItemMaster.Find(goodsType).Id answers when the client resolves a counter, a price or an "Owned" figure.
    // Rows 1-8 were this server's invention; with them a price quoted as 4010001 (for its icon) was counted against
    // row 7, and every "Owned" read 0.
    public const int JetCoinItemId = 1010001;       // GoodsType 101 JetCoinFree
    public const int PaidJetCoinItemId = 1020001;   // 102 JetCoinPaid
    public const int DiscForceItemId = 3020001;     // 302 DiscForce
    public const int KickPointItemId = 7010001;     // 701 KickPoint
    public const int GearMaterialItemId = 2020001;  // 202 GearMaterial
    public const int DiscFragmentItemId = 3030001;  // 303 DiscFragment
    public const int KickerTicketItemId = 4010001;  // 401 KickerGachaTicket
    public const int DiscTicketItemId = 4020001;    // 402 DiscGachaTicket

    // Currencies, by the client's Item master row. These four are the legacy counters the startup payload has
    // always carried first.
    public static readonly (string Currency, int ItemId)[] CurrencyRows =
    [
        ("jetcoin", JetCoinItemId),
        (DiscForceCurrency, DiscForceItemId),
        // The kicker ticket is economy's currency rather than an item because a gacha pool is priced in
        // currencies; the client has always shown it as an Item row, so it is rendered as one.
        ("kicker_ticket", KickerTicketItemId),
        // The shared gear chip: KickerGearMaterial's kickerId -1 row names this item, and gear/create spends one.
        (GearChipCurrency, GearMaterialItemId)
    ];

    public const string GearChipCurrency = "gear_chip";

    // The economy currency a client Item id is, when it is one (the reverse of CurrencyRows).
    public static string? CurrencyForItemId(int itemId) =>
        CurrencyRows.FirstOrDefault(row => row.ItemId == itemId).Currency;

    // Currencies the economy holds that are deliberately not an Item row. A capsule token is spent the moment its
    // capsule is opened and the capsule slot is what the client shows for it, so a counter would only show a
    // number nobody can use. Named here so a balance of one is skipped quietly instead of logged as unknown.
    public static bool IsCapsuleToken(string? currency) =>
        currency is not null && currency.StartsWith("capsule_", StringComparison.Ordinal);

    // Stackable items, by the client's Item master row: the gear stamps the gear screen counts, the disc fragments,
    // and the two gacha tickets. The disc ticket is kept because the Item master still has the row (402); the disc
    // gacha is priced in Jet Coin now, so nothing spends it any more.
    public static readonly (string Item, int ItemId)[] ItemRows =
    [
        ("fragment", DiscFragmentItemId)
    ];

    // The client's Item master has exactly these eight rows, and the startup payload has always carried all eight
    // whether or not the player holds any. Keeping the row count is what keeps the counters screen stable.
    public static readonly int[] ItemMasterIds =
    [
        JetCoinItemId, PaidJetCoinItemId, DiscForceItemId, KickPointItemId, GearMaterialItemId, DiscFragmentItemId,
        KickerTicketItemId, DiscTicketItemId
    ];

    // The GoodsType each Item master row is (served in the Item master itself): a shop or gacha price is expressed
    // to the client as a (goodsType, goodsId) pair, and goodsId is the Item row.
    public static readonly (int ItemId, int GoodsType)[] GoodsTypeRows =
    [
        (JetCoinItemId, 101), (PaidJetCoinItemId, 102), (DiscForceItemId, 302), (KickPointItemId, 701),
        (GearMaterialItemId, 202), (DiscFragmentItemId, 303), (KickerTicketItemId, 401), (DiscTicketItemId, 402)
    ];

    // The goods shop's products (GoodsShopProductRow in DemoSessionApi.Shop.cs) and the economy offer each one buys.
    // The product id is the client's - it sends it in GoodsShopBuyRequestData.goodsShopProductId - and the offer id
    // is economy's; this table is the only place the two are related.
    // Two of the three have an offer behind it: kicker tickets and gear chips (gear_chip.10). The disc ticket is what
    // the economy design stopped selling (the disc gacha is priced in Jet Coin); it stays in the client's product
    // list and answers "not for sale", which is the truth - the alternative, inventing an offer so the shelf looks
    // full, would put unpriced goods on a live screen.
    public static readonly (int ProductId, string OfferId)[] GoodsShopOffers =
    [
        (2, "ticket.single"),
        (3, "gear_chip.10")
    ];

    // The one cosmetic pool in use, and the GachaKicker row id the client quotes back in
    // GachaKickerDrawRequestData.gachaId (through ResponseGacha.id).
    public const string CosmeticPoolId = "cosmetic.standard";

    public static readonly (int GachaId, string PoolId)[] KickerGachas = [(1, CosmeticPoolId)];

    // The disc tab's two buttons: DiscGacha row id (GachaDiscDrawRequestData.gachaId, which is all a draw request
    // carries), the economy pool, and how many draws one press rolls. disc.eleven is a bundle pool: a roll of exactly
    // its bundle count pays its cost once, so the count here must be that bundle count.
    public static readonly (int GachaId, string PoolId, int Count)[] DiscGachas =
    [
        (1, "disc.standard", 1),
        (2, "disc.eleven", 11)
    ];

    public static (string PoolId, int Count)? DiscPoolForGachaId(int gachaId)
    {
        foreach (var row in DiscGachas)
        {
            if (row.GachaId == gachaId) return (row.PoolId, row.Count);
        }
        return null;
    }

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
