namespace KickFlight.BootstrapApi.Otomo;

// The client's inventory payloads, built from a player's economy state.
//
// This is the whole of the adapter's economy read path: nothing is remembered between calls, so every field below
// is a value economy answered for *this* request. The shapes are the ones the patched client deserializes
// (docs/ECONOMY_DTOS.md), field for field - the adapter does not get to rename a key here, because the client is a
// binary nobody can recompile.
//
// Two rules about what is *not* owned:
//
//   * A disc the player owns is level 10. The economy models possession, not per-disc levels, and the disc gacha
//     and /disc/buildup that used to raise them are gone. Saying 10 is the honest answer to "what level is a disc
//     I own".
//   * A kicker is owned when a costume of it is. The active kicker and kicker 1's tutorial costume are always
//     included: they are the player's own selection (SessionState keeps that, and it is not a value), and a client
//     that is handed no kicker cannot render the player at all.
public sealed class EconomyPayloads
{
    // What the economy says a possessed disc is worth, in the client's level column.
    public const int OwnedDiscLevel = 10;

    // Kicker 1's first costume: the id the client hardcodes for the tutorial's placeholder player
    // (TutorialUtil.KICKER_COSTUME_ID) and the one a fresh account starts on.
    private const int TutorialKickerId = 1;
    private const int TutorialCostumeId = 2010101;

    private readonly IReadOnlyDictionary<int, int> _costumeKicker;
    private readonly Func<int, int[]> _gearSlots;
    private readonly IReadOnlyCollection<int> _knownDiscIds;
    private readonly ILogger _logger;

    public EconomyPayloads(IReadOnlyDictionary<int, int> costumeKicker, Func<int, int[]> gearSlots,
        IReadOnlyCollection<int> knownDiscIds, ILogger logger)
    {
        _costumeKicker = costumeKicker;
        _gearSlots = gearSlots;
        _knownDiscIds = knownDiscIds;
        _logger = logger;
    }

    // ResponseUserItem[]: the client's eight Item master rows, in id order, whether or not the player holds any.
    public object[] BuildUserItemList(EconomySnapshot snapshot)
    {
        var amounts = new Dictionary<int, int>();
        foreach (var balance in snapshot.Balances)
        {
            if (EconomyCatalog.TryItemIdForCurrency(balance.Currency, out var itemId))
            {
                amounts[itemId] = ToClientAmount(balance.Balance);
                continue;
            }
            _logger.LogWarning("Economy holds currency {Currency}, which the client's Item master has no row for", balance.Currency);
        }

        foreach (var item in snapshot.Items)
        {
            if (EconomyCatalog.TryItemIdForItem(item.Item, out var itemId))
            {
                amounts[itemId] = ToClientAmount(item.Qty);
                continue;
            }
            // A disc or a costume is not a counter on this screen; anything else is a name this adapter does not
            // know how to show, and it is said out loud rather than dropped in silence.
            if (!EconomyCatalog.TryDiscId(item.Item, out _) && !EconomyCatalog.TryCostumeId(item.Item, out _))
            {
                _logger.LogWarning("Economy holds item {Item}, which this adapter has no client id for", item.Item);
            }
        }

        return EconomyCatalog.ItemMasterIds
            .Select(itemId => (object)new { itemId, amount = amounts.GetValueOrDefault(itemId) })
            .ToArray();
    }

    // ResponseUserDisc[]: the discs the player owns, by Disc master row id.
    public object[] BuildUserDiscList(EconomySnapshot snapshot)
    {
        var rows = new List<object>();
        foreach (var item in snapshot.Items)
        {
            if (!EconomyCatalog.TryDiscId(item.Item, out var discId)) continue;
            if (!_knownDiscIds.Contains(discId))
            {
                // The client resolves every disc id through DiscMasterData; an id it does not have is a failed
                // lookup on the deck screen, so it is not served.
                _logger.LogWarning("Economy holds disc {DiscId}, which is not in the served Disc master", discId);
                continue;
            }
            rows.Add(new
            {
                discId,
                amount = ToClientAmount(item.Qty),
                level = OwnedDiscLevel,
                exp = 0,
                registerDatetime = Stamp(item.AcquiredAt)
            });
        }
        return rows.ToArray();
    }

    // ResponseUserKicker[]: one row per kicker the player owns a costume of, with the costumes they own.
    public object[] BuildUserKickerList(EconomySnapshot snapshot, int activeKickerId, int activeCostumeId)
    {
        var costumes = OwnedCostumes(snapshot, activeCostumeId);
        return costumes.Keys
            .OrderBy(kickerId => kickerId)
            .Select(kickerId => BuildUserKicker(kickerId, costumes[kickerId], activeKickerId, activeCostumeId))
            .Where(row => row is not null)
            .Select(row => row!)
            .ToArray();
    }

    // ResponseDropKickerCostume[]: what each draw landed on, in draw order. The kicker id is the owner of the
    // costume; 0 when the costume is not in the served master, which is what the client tolerates.
    public object[] BuildDropKickerCostumes(IReadOnlyList<EconomyDraw>? draws)
    {
        var rows = new List<object>();
        foreach (var draw in draws ?? [])
        {
            if (!EconomyCatalog.TryCostumeId(draw.Item, out var costumeId))
            {
                _logger.LogWarning("Economy drew {Item} from a cosmetic pool; the client cannot show it", draw.Item);
                continue;
            }
            rows.Add(new
            {
                kickerId = _costumeKicker.GetValueOrDefault(costumeId),
                kickerCostumeId = costumeId
            });
        }
        return rows.ToArray();
    }

    // ResponseUserItem[] for a receipt list: one row per item the call moved, with the total the player holds
    // afterwards (which is what the client's counters show).
    public object[] BuildReceivedUserItemList(IReadOnlyList<EconomyItemMove>? moves)
    {
        var rows = new List<object>();
        foreach (var move in moves ?? [])
        {
            if (!EconomyCatalog.TryItemIdForItem(move.Item, out var itemId))
            {
                _logger.LogWarning("Economy granted item {Item}, which this adapter has no client id for", move.Item);
                continue;
            }
            rows.Add(new { itemId, amount = ToClientAmount(move.Total) });
        }
        return rows.ToArray();
    }

    // The kickers a set of draws touched, deduplicated, for a draw's receivedUserKickerList.
    public object[] BuildReceivedUserKickerList(IReadOnlyList<EconomyDraw>? draws, EconomySnapshot snapshot,
        int activeKickerId, int activeCostumeId)
    {
        var costumes = OwnedCostumes(snapshot, activeCostumeId);
        var kickerIds = new List<int>();
        foreach (var draw in draws ?? [])
        {
            if (!EconomyCatalog.TryCostumeId(draw.Item, out var costumeId)) continue;
            var kickerId = _costumeKicker.GetValueOrDefault(costumeId);
            if (kickerId != 0 && !kickerIds.Contains(kickerId)) kickerIds.Add(kickerId);
        }
        return kickerIds
            .Where(kickerId => costumes.ContainsKey(kickerId))
            .OrderBy(kickerId => kickerId)
            .Select(kickerId => BuildUserKicker(kickerId, costumes[kickerId], activeKickerId, activeCostumeId))
            .Where(row => row is not null)
            .Select(row => row!)
            .ToArray();
    }

    // kickerId -> the costume row ids the player owns of it, in ascending order.
    private Dictionary<int, List<int>> OwnedCostumes(EconomySnapshot snapshot, int activeCostumeId)
    {
        var owned = new Dictionary<int, List<int>>();
        void Add(int costumeId)
        {
            if (!_costumeKicker.TryGetValue(costumeId, out var kickerId) || kickerId == 0)
            {
                _logger.LogWarning("Costume {CostumeId} is not in the served KickerCostume master; not served", costumeId);
                return;
            }
            if (!owned.TryGetValue(kickerId, out var list))
            {
                list = [];
                owned[kickerId] = list;
            }
            if (!list.Contains(costumeId)) list.Add(costumeId);
        }

        foreach (var item in snapshot.Items)
        {
            if (EconomyCatalog.TryCostumeId(item.Item, out var costumeId)) Add(costumeId);
        }
        // The player's own selection, and the tutorial costume, so a client with an empty inventory still has a
        // kicker it can render. Both are identity, not value: the selection is already in the save.
        Add(activeCostumeId);
        if (_costumeKicker.TryGetValue(TutorialCostumeId, out var tutorialOwner) && tutorialOwner == TutorialKickerId)
        {
            Add(TutorialCostumeId);
        }

        foreach (var list in owned.Values) list.Sort();
        return owned;
    }

    private object? BuildUserKicker(int kickerId, List<int> costumes, int activeKickerId, int activeCostumeId)
    {
        if (costumes.Count == 0) return null;
        var selected = kickerId == activeKickerId && costumes.Contains(activeCostumeId) ? activeCostumeId : costumes[0];
        return new
        {
            kickerId,
            kickerCostumeId = selected,
            userKickerCostumeList = costumes.Select(costumeId =>
            {
                var slots = _gearSlots(costumeId);
                return new
                {
                    kickerCostumeId = costumeId,
                    gearId1 = slots.Length > 0 ? slots[0] : 0,
                    gearId2 = slots.Length > 1 ? slots[1] : 0,
                    gearId3 = slots.Length > 2 ? slots[2] : 0
                };
            }).ToArray()
        };
    }

    // The client reads every one of these counters as an int; a balance that has outgrown int is clamped rather
    // than wrapped, so a number is never shown as a smaller one.
    private static int ToClientAmount(long amount) =>
        (int)Math.Clamp(amount, 0, int.MaxValue);

    // The client's date columns are strings it parses with DateTime.Parse; an empty one is what the payload has
    // always used for "unknown".
    public static string Stamp(DateTimeOffset? value) =>
        value is { } stamp ? stamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") : "";
}
