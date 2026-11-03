using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using KickFlight.BootstrapApi.Otomo;

namespace KickFlight.BootstrapApi;

// Capsules: what a battle hands out, and the disc draw that opening one is.
//
// A capsule is an economy entitlement (POST /internal/economy/entitlements), and the adapter keeps nothing about it:
//
//   kind        capsule - the one kind the KF tenant declares, with max_open 3: economy refuses a fourth open row, which is
//               the client's three slots
//   rule        match.capsule.<type> - the reward class the claim pays (wooden, standard or golden)
//   available_at  the moment the slot may be opened: the grant time plus the type's unlock timer
//   source      the battle entry that earned it
//   meta        what the client draws the slot from: {capsuleId, discCount, slot, openTimeSecond}
//   key         capsule:<player>:<battle entry>, so a retried battle result creates nothing more
//
// The adapter decides only presentation, from EconomyTuning: which type a battle earns (60/30/10), how many discs it
// holds, and when its slot may be opened. The draw inside the capsule is economy's capsule.<type> pool.
//
//   battle result  one create. A player who already holds three open capsules is refused by economy (409
//                  entitlement_cap) and the battle hands out nothing - exactly what a full set of slots always meant.
//   home           the open entitlements are the slots (GET /internal/economy/entitlements).
//   open           claim the entitlement (economy pays the class once, refuses before available_at), then one roll of the
//                  capsule.<type> pool on the player's own token, count = the capsule's disc amount, keyed
//                  capsule-open:<entry>. The pool costs one capsule_<type> token per draw.
//
// Payment, exactly once: nothing is paid when the battle hands the capsule out - an entitlement is a promise, not a
// payout. The claim pays match.capsule.<type> once, keyed by the entitlement id on economy's side (so a retried or
// concurrent claim pays once; the second is 409 entitlement_claimed). That class is one token, and a capsule holding more
// than one disc needs one token per draw, so the remaining discCount - 1 tokens are the same class granted under
// capsule:<entry>:<n> keys, which replay on a retry. The sum is discCount tokens, each paid once, and the roll spends
// them. A claim that succeeds and a roll that then fails leaves the row claimed: the open is retried within
// ClaimedRecoveryWindow, finds the claimed row, and replays the claim and the roll's key.
public sealed partial class DemoSessionApi
{
    // The tutorial's capsule: TutorialUtil.CAPSULE_ID, opened after OPEN_CAPSULE_DURATION (8) seconds.
    private const int TutorialCapsuleId = 5010000;

    private string BuildCapsuleMasterJson()
    {
        var rows = new List<object>
        {
            new
            {
                id = TutorialCapsuleId, lotteryId = 1, name = "Tutorial Capsule", openTime = "00:00:08",
                lotteryDiscRarityGroupId = 1, lotteryDiscDropAmountGroupId = 1
            }
        };
        foreach (var capsule in _tuning.CapsuleTypes)
        {
            // The lottery groups are the ones the earlier inline rows carried; they only drive the client's odds
            // screen, since the draw itself is the capsule.<type> pool.
            var golden = capsule.Type == "golden";
            rows.Add(new
            {
                id = capsule.CapsuleId,
                lotteryId = golden ? 2 : capsule.Type == "standard" ? 1 : 3,
                name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(capsule.Type) + " Capsule",
                openTime = ClockOf(capsule.Unlock),
                lotteryDiscRarityGroupId = golden ? 2 : 1,
                lotteryDiscDropAmountGroupId = golden ? 2 : 1
            });
        }
        return JsonSerializer.Serialize(rows);
    }

    // "HH:mm:ss" with the hours unbounded, which is how the Capsule master writes a timer.
    private static string ClockOf(TimeSpan span) =>
        $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";

    // The entitlement kind the KF tenant declares for capsules, and how long after a claim an open may still find the row
    // (a claim that paid and a roll that failed leave a claimed row nobody lists any more).
    private const string CapsuleKind = "capsule";
    private const string CapsuleRulePrefix = "match.capsule.";
    private static readonly TimeSpan ClaimedRecoveryWindow = TimeSpan.FromMinutes(10);

    // One entitlement as the client's slot: which slot, which Capsule master row, how many discs, when it opens.
    private sealed record CapsuleSlot(int SlotNumber, EconomyEntitlement Row, string Type, int CapsuleId, int DiscCount,
        int OpenTimeSecond)
    {
        public string EntitlementId => Row.Id;
        public DateTimeOffset UnlockAt => Row.AvailableAt;
        public string Source => Row.Source ?? "";
    }

    // The capsule rows among a player's entitlements, oldest first. Other kinds are not ours to show.
    private async Task<List<EconomyEntitlement>> ListCapsuleRowsAsync(KfIdentityTicket ticket, bool includeClaimed,
        CancellationToken cancellationToken) =>
        (await _economy.ListEntitlementsAsync(ticket.OtomoPlayerId, includeClaimed, cancellationToken))
            .Where(row => row.Kind == CapsuleKind && row.Rule.StartsWith(CapsuleRulePrefix, StringComparison.Ordinal))
            .OrderBy(row => row.CreatedAt ?? row.AvailableAt).ThenBy(row => row.Id, StringComparer.Ordinal)
            .ToList();

    // The slots the open rows occupy: a row keeps the slot its meta names, and one whose slot is taken (two battles that
    // raced) or absent takes the first free one. A row beyond the client's three slots is not shown.
    private List<CapsuleSlot> SlotsOf(IEnumerable<EconomyEntitlement> openRows)
    {
        var slots = new List<CapsuleSlot>();
        var taken = new HashSet<int>();
        var pending = new List<(EconomyEntitlement Row, int Wanted)>();
        foreach (var row in openRows.Where(row => row.ClaimedAt is null))
            pending.Add((row, MetaInt(row, "slot")));
        // Rows that name a free, valid slot first, so a late arrival cannot displace a row that holds its own.
        foreach (var (row, wanted) in pending)
        {
            if (wanted is < 1 or > EconomyTuning.CapsuleSlots || !taken.Add(wanted)) continue;
            if (SlotFor(row, wanted) is { } slot) slots.Add(slot);
        }
        foreach (var (row, wanted) in pending)
        {
            if (slots.Any(slot => slot.Row.Id == row.Id)) continue;
            var free = Enumerable.Range(1, EconomyTuning.CapsuleSlots).FirstOrDefault(number => !taken.Contains(number));
            if (free == 0) break;
            taken.Add(free);
            if (SlotFor(row, free) is { } slot) slots.Add(slot);
        }
        return slots.OrderBy(slot => slot.SlotNumber).ToList();
    }

    private CapsuleSlot? SlotFor(EconomyEntitlement row, int slotNumber)
    {
        var type = row.Rule[CapsuleRulePrefix.Length..];
        var tuned = _tuning.FindCapsule(type);
        if (tuned is null)
        {
            _logger.LogWarning("Entitlement {Id} names capsule type '{Type}', which the tuning does not have; not shown", row.Id, type);
            return null;
        }
        var capsuleId = MetaInt(row, "capsuleId");
        var discCount = MetaInt(row, "discCount");
        var openTime = MetaInt(row, "openTimeSecond");
        return new CapsuleSlot(slotNumber, row, type, capsuleId > 0 ? capsuleId : tuned.CapsuleId,
            discCount > 0 ? discCount : tuned.MinDiscs,
            openTime > 0 ? openTime : (int)Math.Max(0, (row.AvailableAt - (row.CreatedAt ?? row.AvailableAt)).TotalSeconds));
    }

    private static int MetaInt(EconomyEntitlement row, string name) =>
        row.Meta is { ValueKind: JsonValueKind.Object } meta && meta.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var number)
            ? number
            : 0;

    // ResponseUserCapsule[]: the home's capsule slots, from the open entitlements. With the internal plane off no
    // capsule can have been handed out, so the list is empty; with it on, economy not answering is a 503 and never an
    // empty list the client would render as "no capsules".
    private async Task<(object[]? List, Exception? Error)> TryBuildUserCapsuleListAsync(KfIdentityTicket ticket,
        CancellationToken cancellationToken)
    {
        if (!_economy.ProgressEnabled) return ([], null);
        try
        {
            var rows = await ListCapsuleRowsAsync(ticket, includeClaimed: false, cancellationToken);
            return (SlotsOf(rows).Select(UserCapsuleRow).ToArray(), null);
        }
        catch (Exception exception) when (exception is OtomoEconomyException or OtomoIdentityException)
        {
            return (null, exception);
        }
    }

    // completeDatetime carries its offset. UserCapsuleInfo..ctor (0x18F1398) reads it with TimeUtil.GetDatetimeFromString
    // (0x161E520), which is a bare DateTimeOffset.TryParse: a string without an offset is taken in the device's own
    // zone. The slot compares it with TimeUtil.GetCurrentDateTime, which is an instant (FromUnixTimeSeconds), so an
    // offset-less UTC stamp opened the slot early by the device's UTC offset - 8 hours on a UTC+8 phone - and the
    // open was refused here as not yet unlocked.
    private static object UserCapsuleRow(CapsuleSlot capsule) => new
    {
        slotNumber = capsule.SlotNumber,
        capsuleId = capsule.CapsuleId,
        completeDatetime = EconomyPayloads.Instant(capsule.UnlockAt),
        openTimeSecond = capsule.OpenTimeSecond
    };

    // The capsule a battle result hands out, or null when it hands out none (no issued entry, the internal plane is off,
    // all three slots are taken, economy cannot be reached). A retried result answers the capsule the first one handed
    // out, and a capsule already opened is not handed out again.
    private async Task<CapsuleSlot?> AwardBattleCapsuleAsync(KfIdentityTicket ticket, SessionState state,
        string battleEntryId, CancellationToken cancellationToken)
    {
        var source = ProgressKeyPart(battleEntryId);
        if (source.Length == 0 || !_economy.ProgressEnabled) return null;

        try
        {
            var rows = await ListCapsuleRowsAsync(ticket, includeClaimed: true, cancellationToken);
            var open = rows.Where(row => row.ClaimedAt is null).ToList();
            var existing = rows.FirstOrDefault(row => row.Source == source);
            if (existing is not null) return ShownCapsule(existing, open);
            if (open.Count >= EconomyTuning.CapsuleSlots)
            {
                _logger.LogInformation("Battle {Entry} hands user {UserId} no capsule: all {Slots} slots are full",
                    battleEntryId, state.UserId, EconomyTuning.CapsuleSlots);
                return null;
            }

            var occupied = SlotsOf(open).Select(slot => slot.SlotNumber).ToHashSet();
            var slotNumber = Enumerable.Range(1, EconomyTuning.CapsuleSlots).First(number => !occupied.Contains(number));
            var (type, discCount) = _tuning.RollCapsule(Random.Shared);
            var now = _timeProvider.GetUtcNow();
            var row = await _economy.CreateEntitlementAsync(new EconomyEntitlementRequest(
                ticket.OtomoPlayerId, CapsuleKind, type.GrantClass, source, now + type.Unlock,
                new JsonObject
                {
                    ["capsuleId"] = type.CapsuleId,
                    ["discCount"] = discCount,
                    ["slot"] = slotNumber,
                    ["openTimeSecond"] = (int)type.Unlock.TotalSeconds
                },
                ProgressKeyPart($"capsule:{ticket.OtomoPlayerId}:{source}")), cancellationToken);

            _logger.LogInformation("Battle {Entry} handed user {UserId} a {Type} capsule ({Discs} discs) in slot {Slot}, opens at {UnlockAt:u}",
                battleEntryId, state.UserId, type.Type, discCount, slotNumber, row.AvailableAt);
            return ShownCapsule(row, open);
        }
        catch (OtomoEconomyException exception) when (exception.Failure == OtomoEconomyFailure.Refused)
        {
            if (exception.Code == "entitlement_cap")
            {
                _logger.LogInformation("Battle {Entry} hands user {UserId} no capsule: economy holds {Slots} open already",
                    battleEntryId, state.UserId, EconomyTuning.CapsuleSlots);
            }
            else if (exception.Code == "idempotency_key_reused")
            {
                // A concurrent result for the same entry made the row first, with another roll: show that one.
                try
                {
                    var rows = await ListCapsuleRowsAsync(ticket, includeClaimed: true, cancellationToken);
                    var made = rows.FirstOrDefault(row => row.Source == source);
                    if (made is not null) return ShownCapsule(made, rows.Where(row => row.ClaimedAt is null).ToList());
                }
                catch (Exception inner) when (inner is OtomoEconomyException or OtomoIdentityException)
                {
                    _logger.LogWarning("Capsule for battle {Entry} could not be re-read: {Error}", battleEntryId, inner.Message);
                }
            }
            else
            {
                _logger.LogError("Economy refused the capsule for battle {Entry} (user {UserId}): {Status} {Code}",
                    battleEntryId, state.UserId, exception.StatusCode, exception.Code);
            }
            return null;
        }
        catch (Exception exception) when (exception is OtomoEconomyException or OtomoIdentityException)
        {
            // The battle result is answered without a capsule; the result is retried by the client, and the same entry
            // key creates it then.
            _logger.LogWarning("Capsule for battle {Entry} (user {UserId}) not handed out: {Error}", battleEntryId,
                state.UserId, exception.Message);
            return null;
        }
    }

    // The slot a row is shown in, or null when it is already opened (a retried result after the player opened it).
    private CapsuleSlot? ShownCapsule(EconomyEntitlement row, List<EconomyEntitlement> open)
    {
        if (row.ClaimedAt is not null) return null;
        var rows = open.Any(candidate => candidate.Id == row.Id) ? open : [.. open, row];
        return SlotsOf(rows).FirstOrDefault(slot => slot.Row.Id == row.Id);
    }

    // A capsule the player claimed moments ago and whose roll did not complete: the row is claimed, so no list shows it, but
    // the open that failed is retried by the client and must find it. Only the newest claimed row of that slot, and only
    // inside ClaimedRecoveryWindow; the roll's own key replays when the roll had in fact happened.
    private CapsuleSlot? ClaimedAwaitingRoll(IEnumerable<EconomyEntitlement> rows, int slotNumber)
    {
        var cutoff = _timeProvider.GetUtcNow() - ClaimedRecoveryWindow;
        var row = rows.Where(candidate => candidate.ClaimedAt is { } claimedAt && claimedAt >= cutoff &&
                                          MetaInt(candidate, "slot") == slotNumber)
            .OrderByDescending(candidate => candidate.ClaimedAt).FirstOrDefault();
        return row is null ? null : SlotFor(row, slotNumber);
    }

    // The remaining tokens of a capsule that holds more than one disc: the claim paid the first, and the class is
    // granted again for each further draw under its own key, which a retry replays. Throws OtomoEconomyException
    // (Disabled) when the internal plane is off.
    private async Task GrantRemainingCapsuleTokensAsync(KfIdentityTicket ticket, CapsuleSlot capsule,
        CancellationToken cancellationToken)
    {
        var type = _tuning.FindCapsule(capsule.Type)
                   ?? throw new OtomoEconomyException(OtomoEconomyFailure.Refused,
                       $"capsule type '{capsule.Type}' is not in the tuning");
        for (var index = 1; index < capsule.DiscCount; index++)
        {
            var paid = await GrantClassAsync(ticket, type.GrantClass, $"capsule:{capsule.Source}:{index}",
                "battle_capsule", capsule.Source, cancellationToken);
            if (paid is null)
                throw new OtomoEconomyException(OtomoEconomyFailure.Disabled, "the internal plane is off");
        }
    }

    // /capsule/open and /capsule/immediateOpen {slotNumber}. Both claim the slot's entitlement, which economy refuses
    // before its available_at, and then roll the pool once on the tokens the claim paid. immediateOpen used to skip the
    // timer for free; economy's claim cannot be asked to ignore available_at, so it now opens a capsule only once it is
    // due, like open (see the report).
    private async Task<IResult?> HandleCapsuleOpenAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key, bool immediate)
    {
        var path = immediate ? "/capsule/immediateOpen" : "/capsule/open";
        int slotNumber;
        try
        {
            using var document = JsonDocument.Parse(D2CCodec.Decode(await ReadBodyAsync(context.Request), key));
            slotNumber = document.RootElement.GetProperty("slotNumber").GetInt32();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode {Path} request: {Error}", path, ex.Message);
            return StatusError(context, key);
        }

        if (!_economy.ProgressEnabled)
        {
            _logger.LogWarning("{Path} for user {UserId}: slot {Slot} holds no capsule (the internal plane is off)", path,
                state.UserId, slotNumber);
            return StatusError(context, key);
        }

        CapsuleSlot? capsule;
        try
        {
            var rows = await ListCapsuleRowsAsync(ticket, includeClaimed: true, context.RequestAborted);
            capsule = SlotsOf(rows).FirstOrDefault(slot => slot.SlotNumber == slotNumber)
                      ?? ClaimedAwaitingRoll(rows, slotNumber);
        }
        catch (Exception ex) when (ex is OtomoEconomyException or OtomoIdentityException)
        {
            return EconomyUnavailable(context, path, ex);
        }

        var type = capsule is null ? null : _tuning.FindCapsule(capsule.Type);
        if (capsule is null || type is null)
        {
            _logger.LogWarning("{Path} for user {UserId}: slot {Slot} holds no capsule", path, state.UserId, slotNumber);
            return StatusError(context, key);
        }

        EconomyRoll roll;
        try
        {
            var playerToken = await PlayerTokenAsync(ticket, context.RequestAborted);
            try
            {
                var claim = await _economy.ClaimEntitlementAsync(capsule.EntitlementId, ticket.OtomoPlayerId,
                    ProgressKeyPart($"capsule-claim:{capsule.EntitlementId}"), context.RequestAborted);
                _logger.LogInformation("{Path}: user {UserId} claimed capsule {Id} ({Type}){Replayed}", path, state.UserId,
                    capsule.EntitlementId, capsule.Type, claim.Applied ? "" : " (payout already made)");
            }
            catch (OtomoEconomyException ex) when (ex.Failure == OtomoEconomyFailure.Refused &&
                                                  ex.Code == "entitlement_claimed")
            {
                // The claim of an earlier attempt that paid and then lost the roll: the class is paid, go on to the roll.
                _logger.LogInformation("{Path}: capsule {Id} was claimed by an earlier attempt; rolling", path,
                    capsule.EntitlementId);
            }
            await GrantRemainingCapsuleTokensAsync(ticket, capsule, context.RequestAborted);
            roll = await _economy.RollAsync(playerToken, type.PoolId, capsule.DiscCount,
                ProgressKeyPart($"capsule-open:{capsule.Source}"), context.RequestAborted);
        }
        catch (OtomoIdentityException ex)
        {
            return EconomyUnavailable(context, path, ex);
        }
        catch (OtomoEconomyException ex) when (ex.Failure == OtomoEconomyFailure.Refused)
        {
            _logger.LogWarning("{Path} capsule {Id} ({Type}, slot {Slot}) for user {UserId} was refused by economy: {Status} {Code}",
                path, capsule.EntitlementId, capsule.Type, slotNumber, state.UserId, ex.StatusCode, ex.Code);
            return StatusError(context, key);
        }
        catch (OtomoEconomyException ex)
        {
            return EconomyUnavailable(context, path, ex);
        }

        // A receipt shows each disc at the level it stands at, which is attrs.level on the disc's own possession, so
        // the inventory is read again after the roll. A read that fails falls back to the roll's own items, which
        // carry no attrs: every disc a capsule hands over is one the player just received, so the start level is the
        // right answer for a new one and the only one available for a duplicate.
        var inventory = await ReadPlayerEconomyAsync(ticket, EconomyRead.Inventory, path, context.RequestAborted);
        var snapshot = inventory.Snapshot ?? SnapshotFromRoll(roll);
        var payloads = Payloads(state);
        var dropDiscList = payloads.BuildDropDiscList(roll.Draws);
        var receivedUserDiscList = payloads.BuildReceivedUserDiscList(roll.Items, discId => DiscStandingOf(snapshot, discId));
        var receivedUserItemList = payloads.BuildReceivedCounters(roll.Balances, roll.Entries, roll.Items);
        var receivedRewardList = payloads.BuildReceivedRewards(roll.Entries, roll.Items);
        var effect = CapsuleEffectOf(roll.Draws);

        _logger.LogInformation("{Path}: user {UserId} opened the {Type} capsule in slot {Slot}: {Items}{Replayed}",
            path, state.UserId, capsule.Type, slotNumber,
            string.Join(",", (roll.Draws ?? []).Select(draw => draw.Item)), roll.Replayed ? " (replayed)" : "");

        if (!immediate)
        {
            return OkJson(context, key, JsonSerializer.Serialize(new
            {
                dropDiscList,
                receivedUserDiscList,
                receivedUserItemList,
                capsuleEffectType = effect,
                userPresentList = Array.Empty<object>(),
                userMissionProgressList = Array.Empty<object>(),
                userDailyRandomMissionTaskList = Array.Empty<object>(),
                receivedRewardList,
                premiumFlag = false
            }));
        }

        // Only immediateOpen repeats userItemList: the client refreshes the item counters from it. The roll has
        // already happened, so a counter read that fails falls back to the roll's own balances, never to zeroes.
        var economy = await ReadPlayerEconomyAsync(ticket, EconomyRead.Balances | EconomyRead.Inventory, path,
            context.RequestAborted);
        return OkJson(context, key, JsonSerializer.Serialize(new
        {
            dropDiscList,
            receivedUserDiscList,
            receivedUserItemList,
            userItemList = payloads.BuildUserItemList(economy.Snapshot ?? SnapshotFromRoll(roll)),
            capsuleEffectType = effect,
            userPresentList = Array.Empty<object>(),
            userMissionProgressList = Array.Empty<object>(),
            userDailyRandomMissionTaskList = Array.Empty<object>(),
            receivedRewardList,
            premiumFlag = false
        }));
    }

    // CapsuleEffectType: Normal 1, Orange 2, Rainbow 3 - the colour the capsule bursts in. It follows the best
    // rarity drawn (SR orange, UR rainbow), which is the reading the three colours suggest; the client's own rule
    // was not recovered.
    private static int CapsuleEffectOf(IReadOnlyList<EconomyDraw>? draws)
    {
        var rarities = (draws ?? []).Select(draw => draw.Rarity).ToList();
        if (rarities.Contains("ur")) return 3;
        if (rarities.Contains("sr")) return 2;
        return 1;
    }
}
