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
//   * A disc's level is not economy's. Economy models possession; the level is the player's own progress, kept in
//     the save (SessionState.DiscLevels) and handed in here as a function, so this class never decides it. The
//     same function says how many copies those levels spent, which the client counts back into the disc's amount
//     (EconomyDiscLevels explains why).
//   * A kicker is owned when a costume of it is. The active kicker and kicker 1's tutorial costume are always
//     included: they are the player's own selection (SessionState keeps that, and it is not a value), and a client
//     that is handed no kicker cannot render the player at all. Kicker 2's first costume is always included too:
//     it stands in for the tutorial's kicker draw, which the name-entry step assumes has happened.
// A disc as the save and the build-up table describe it: the level it is at, and how many duplicate copies its
// levels have already spent (EconomyDiscLevels.CopiesSpent).
public readonly record struct DiscStanding(int Level, int CopiesSpent);

public sealed class EconomyPayloads
{
    // Kicker 1's first costume: the id the client hardcodes for the tutorial's placeholder player
    // (TutorialUtil.KICKER_COSTUME_ID) and the one a fresh account starts on.
    private const int TutorialKickerId = 1;
    private const int TutorialCostumeId = 2010101;

    // Kicker 2's first costume: the kicker the tutorial's kicker gacha would have handed out. An account without a
    // name is sent tutorialProgressStatus 206 (KickerChange, "the drawn kicker is selected"), and at that step
    // KickerDisplayPresenter.UpdateView (0x15303EC) preselects the first unlocked kicker whose id is not 1 and
    // dereferences it without a null check. With kicker 1 alone the home dies there with a NullReferenceException
    // before the name window opens. Kicker 2 is the one the client picked when every kicker was served.
    private const int TutorialDrawKickerId = 2;
    private const int TutorialDrawCostumeId = 2020101;

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
            if (EconomyCatalog.IsCapsuleToken(balance.Currency)) continue;
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

        // Row 6 is the DiscFragment counter, and the Discs tab draws its fragment-conversion block from it. Nothing
        // here converts fragments, so a zero row is only an empty banner; it is left out so the block has no count
        // to render against.
        return EconomyCatalog.ItemMasterIds
            .Where(itemId => itemId != 6 || amounts.GetValueOrDefault(6) != 0)
            .Select(itemId => (object)new { itemId, amount = amounts.GetValueOrDefault(itemId) })
            .ToArray();
    }

    // ResponseUserDisc[]: the discs the player owns, by Disc master row id, as standingOf says they stand. The
    // amount is the client's cumulative count: what economy holds plus what the disc's levels spent.
    public object[] BuildUserDiscList(EconomySnapshot snapshot, Func<int, DiscStanding> standingOf)
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
            rows.Add(BuildUserDisc(discId, item.Qty, standingOf(discId), item.AcquiredAt));
        }
        return rows.ToArray();
    }

    // ResponseUserDisc[] for a receipt: the discs a call moved into the inventory, one row each.
    public object[] BuildReceivedUserDiscList(IReadOnlyList<EconomyItemMove>? moves, Func<int, DiscStanding> standingOf)
    {
        var rows = new List<object>();
        foreach (var move in moves ?? [])
        {
            if (!EconomyCatalog.TryDiscId(move.Item, out var discId) || !_knownDiscIds.Contains(discId)) continue;
            rows.Add(BuildUserDisc(discId, move.Total, standingOf(discId), DateTimeOffset.UtcNow));
        }
        return rows.ToArray();
    }

    // One ResponseUserDisc. held is what economy says the player has now; the copies the disc's levels spent are
    // added back because the client's amount counts every copy the disc ever collected.
    public static object BuildUserDisc(int discId, long held, DiscStanding standing, DateTimeOffset? acquiredAt) =>
        new
        {
            discId,
            amount = ToClientAmount(held + standing.CopiesSpent),
            level = standing.Level,
            exp = 0,
            registerDatetime = Stamp(acquiredAt)
        };

    // ResponseDropDisc[]: what each draw landed on, in draw order. A draw on a disc the player already had is a
    // duplicate; when the pool converts it to Disc Force the amount is shown in discForceAmount, which is the
    // client's own column for exactly that.
    public object[] BuildDropDiscList(IReadOnlyList<EconomyDraw>? draws)
    {
        var rows = new List<object>();
        foreach (var draw in draws ?? [])
        {
            if (!EconomyCatalog.TryDiscId(draw.Item, out var discId))
            {
                _logger.LogWarning("Economy drew {Item} from a disc pool; the client cannot show it", draw.Item);
                continue;
            }
            var discForce = draw.Duplicate &&
                            EconomyCatalog.TryItemIdForCurrency(draw.PaidCurrency, out var itemId) && itemId == 3
                ? ToClientAmount(draw.PaidAmount)
                : 0;
            rows.Add(new { discId, amount = 1, discForceAmount = discForce });
        }
        return rows.ToArray();
    }

    // ResponseUserItem[] for a receipt: the counters a call changed, as the totals the player holds afterwards. The
    // currencies come from the call's balances, filtered to the ones its journal actually moved, and the stackable
    // items from its item moves.
    public object[] BuildReceivedCounters(IReadOnlyList<EconomyBalance>? balances, IReadOnlyList<EconomyEntry>? entries,
        IReadOnlyList<EconomyItemMove>? moves)
    {
        var moved = PlayerDeltas(entries).Keys.ToHashSet(StringComparer.Ordinal);
        var rows = new List<object>();
        foreach (var balance in balances ?? [])
        {
            if (moved.Count > 0 && !moved.Contains(balance.Currency)) continue;
            if (EconomyCatalog.TryItemIdForCurrency(balance.Currency, out var itemId))
                rows.Add(new { itemId, amount = ToClientAmount(balance.Balance) });
        }
        foreach (var row in BuildReceivedUserItemList(moves)) rows.Add(row);
        return rows.ToArray();
    }

    // ResponseReceivedReward[]: what a call paid, as (goodsType, goodsId, amount) deltas - the popup that lists a
    // mission's or a capsule's reward. originalReceivedReward is the same row: nothing here is converted.
    public object[] BuildReceivedRewards(IReadOnlyList<EconomyEntry>? entries, IReadOnlyList<EconomyItemMove>? moves)
    {
        var rows = new List<object>();
        void Add(int goodsType, int goodsId, long amount)
        {
            // The receipt popup draws each reward icon from "ui/goods/thumbnail/<goodsId>", and the asset list
            // holds those as "<goodsType>0001.unity3d": a raw Item row (1 for Jet Coins, 7 for a ticket) matches
            // no asset and renders an empty frame. Discs and costumes keep their own ids - they are drawn from
            // their own thumbnails, not from a goods sheet.
            var displayId = goodsType is 101 or 102 or 302 or 303 or 401 or 402 ? goodsType * 10000 + 1 : goodsId;
            var original = new { goodsType, goodsId = displayId, amount = ToClientAmount(amount) };
            rows.Add(new { goodsType, goodsId = displayId, amount = ToClientAmount(amount), originalReceivedReward = original });
        }

        foreach (var (currency, delta) in PlayerDeltas(entries))
        {
            if (delta <= 0 || !EconomyCatalog.TryItemIdForCurrency(currency, out var itemId)) continue;
            if (EconomyCatalog.TryGoodsTypeForItemId(itemId, out var goodsType)) Add(goodsType, itemId, delta);
        }
        foreach (var move in moves ?? [])
        {
            if (move.Qty <= 0) continue;
            if (EconomyCatalog.TryDiscId(move.Item, out var discId)) Add(301, discId, move.Qty);
            else if (EconomyCatalog.TryCostumeId(move.Item, out var costumeId)) Add(201, costumeId, move.Qty);
            else if (EconomyCatalog.TryItemIdForItem(move.Item, out var itemId) &&
                     EconomyCatalog.TryGoodsTypeForItemId(itemId, out var goodsType)) Add(goodsType, itemId, move.Qty);
        }
        return rows.ToArray();
    }

    // currency -> what the player's side of a journal moved by. The system side of each pair is the faucet or the
    // sink, and counting it too would show every payout as zero.
    private static Dictionary<string, long> PlayerDeltas(IReadOnlyList<EconomyEntry>? entries)
    {
        var deltas = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in entries ?? [])
        {
            if (!string.Equals(entry.OwnerKind, "player", StringComparison.Ordinal)) continue;
            deltas[entry.Currency] = deltas.GetValueOrDefault(entry.Currency) + entry.Delta;
        }
        return deltas;
    }

    // ResponseUserMissionProgress[]: one row per track the mission masters know, with one task row per step.
    //
    // A step's status is economy's answer read three ways: claimed is ReceivedReward (3), reached is Completion (2)
    // - which is what lights the receive button - and anything else is InProgress (1). Which reached step a claim
    // actually pays is still economy's decision (the next one), so a client tapping a later rung cannot skip ahead.
    //
    // Only for a client that has the mission masters loaded: the caller decides that (DemoSessionApi.MissionRows).
    public object[] BuildMissionProgressList(IReadOnlyList<EconomyTrack>? tracks)
    {
        var rows = new List<object>();
        foreach (var track in tracks ?? [])
        {
            var mission = EconomyMissions.ByTrack(track.TrackId);
            if (mission is null)
            {
                _logger.LogWarning("Economy reports track {Track}, which the mission masters have no row for; not shown",
                    track.TrackId);
                continue;
            }

            var expires = EconomyMissions.PeriodEnd(track.Period);
            var statuses = new List<object>();
            foreach (var step in track.Steps ?? [])
            {
                var known = mission.Steps.FirstOrDefault(row => row.Index == step.Step);
                if (known is null)
                {
                    _logger.LogWarning("Economy reports step {Step} of {Track}, which the mission masters have no row for",
                        step.Step, track.TrackId);
                    continue;
                }
                if (known.Target != step.Target)
                {
                    _logger.LogWarning("Track {Track} step {Step}: economy's target is {Live}, the mission master says {Served}",
                        track.TrackId, step.Step, step.Target, known.Target);
                }
                statuses.Add(new
                {
                    missionTaskId = mission.TaskId(step.Step),
                    loopCount = track.Repetition,
                    missionTaskStatus = step.Claimed ? 3 : step.Reached || step.Claimable ? 2 : 1,
                    receiptExpirationDatetime = expires,
                    expirationDatetime = expires,
                    newFlag = false
                });
            }

            rows.Add(new
            {
                missionId = mission.MissionId,
                loopCount = track.Repetition,
                value = ToClientAmount(track.Value),
                userMissionTaskStatusList = statuses.ToArray()
            });
        }
        return rows.ToArray();
    }

    // The home's mission badge: how many steps are reached and not yet claimed.
    public static int CountClaimable(IReadOnlyList<EconomyTrack>? tracks) =>
        (tracks ?? []).Where(track => EconomyMissions.ByTrack(track.TrackId) is not null)
            .Sum(track => (track.Steps ?? []).Count(step => (step.Reached || step.Claimable) && !step.Claimed));

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
                // A disc or a costume has a receipt list of its own; anything else is a name with no client id.
                if (!EconomyCatalog.TryDiscId(move.Item, out _) && !EconomyCatalog.TryCostumeId(move.Item, out _))
                    _logger.LogWarning("Economy granted item {Item}, which this adapter has no client id for", move.Item);
                continue;
            }
            rows.Add(new { itemId, amount = ToClientAmount(move.Total) });
        }
        return rows.ToArray();
    }

    // The kickers a set of draws touched, deduplicated, for a draw's receivedUserKickerList.
    //
    // showDrawn makes each row's kickerCostumeId the costume that was drawn rather than the player's selection. The
    // tutorial's draw has no dropKickerCostumeList: GachaKickerDrawInfo..ctor(TutorialGachaKickerDrawResponseData)
    // (0x156C4E0) builds the drop it shows from receivedUserKickerList[i].{kickerId, kickerCostumeId}, so a row that
    // carries the selection shows the standard colour of a kicker the player already had instead of the skin.
    public object[] BuildReceivedUserKickerList(IReadOnlyList<EconomyDraw>? draws, EconomySnapshot snapshot,
        int activeKickerId, int activeCostumeId, bool showDrawn = false)
    {
        var costumes = OwnedCostumes(snapshot, activeCostumeId);
        var drawn = new Dictionary<int, int>();
        foreach (var draw in draws ?? [])
        {
            if (!EconomyCatalog.TryCostumeId(draw.Item, out var costumeId)) continue;
            var kickerId = _costumeKicker.GetValueOrDefault(costumeId);
            if (kickerId != 0) drawn.TryAdd(kickerId, costumeId);
        }
        return drawn.Keys
            .Where(kickerId => costumes.ContainsKey(kickerId))
            .OrderBy(kickerId => kickerId)
            .Select(kickerId => showDrawn
                ? BuildUserKicker(kickerId, costumes[kickerId], kickerId, drawn[kickerId])
                : BuildUserKicker(kickerId, costumes[kickerId], activeKickerId, activeCostumeId))
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
        if (_costumeKicker.TryGetValue(TutorialDrawCostumeId, out var drawOwner) && drawOwner == TutorialDrawKickerId)
        {
            Add(TutorialDrawCostumeId);
        }

        // Every kicker is unlocked for every account, and a kicker is only playable through a costume, so the
        // standard colour of all fourteen is served unconditionally: identity, not value, exactly like the active
        // selection above. Costume ids read 2_KK_CC_01, so "costume number 1" is ((id / 100) % 100) == 1.
        foreach (var costumeId in _costumeKicker.Keys)
        {
            if (costumeId / 100 % 100 == 1) Add(costumeId);
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

    // An instant the client compares with its own clock (a capsule's unlock time), with the offset written out. The
    // client reads these with DateTimeOffset.TryParse, which takes an offset-less string in the device's zone.
    public static string Instant(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
