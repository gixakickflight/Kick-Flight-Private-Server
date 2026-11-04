using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using KickFlight.BootstrapApi.Otomo;

namespace KickFlight.BootstrapApi;

// Capsules: what a battle hands out, and the disc draw that opening one is.
//
// A capsule is an economy entitlement, and the adapter holds none of its policy. The tenant's documents (kf-otomo,
// economy.rules) say when one is awarded, which, and how long it waits:
//
//   kind        capsule: max_open 3 (the client's three slots), the five match.capsule.* classes, a delay window
//   trigger     battle.result -> one capsule, drawn by economy from weighted picks (60/30/10 over the types, and the disc
//               count within a type), each pick with its own delay
//   class       match.capsule.<type>[.<discs>] pays the type's tokens (capsule_<type>), one per disc the capsule holds
//
// What the adapter does is translate:
//
//   battle result  read, from the one battle.result report (DemoSessionApi.Badges.cs: RecordBadgeBattleAsync), the trigger's answer: minted gives the
//                  capsule to show; a refusal (entitlement_cap: all slots full) or a skip gives none. It decides nothing.
//   home           the open entitlements are the slots (GET /internal/economy/entitlements).
//   open           claim the entitlement (economy pays the class once, refuses before available_at), then one roll of the
//                  capsule.<type> pool on the player's own token, as many draws as the claim paid tokens, keyed
//                  capsule-open:<entitlement>.
//   immediateOpen  the same, but a capsule not yet due is claimed early at the price the kind declares (economy's
//                  early_claim: GET .../early_claim for the price, claim with early: true). A kind that offers no early
//                  claim answers 404 early_claim_not_offered and the plain claim - and its not-yet-due refusal - follows.
//
// A trigger's meta is only the fact and trigger (and the fact fields it names, none here), so a slot is not stored anywhere: it is the lowest slot free at the moment the row was made,
// which is recomputed from the rows' created_at and claimed_at (SlotsOf) and does not move when an older one is opened.
//
// Payment, exactly once: nothing is paid when the battle hands the capsule out - an entitlement is a promise, not a
// payout. The claim pays match.capsule.<type> once, keyed by the entitlement id on economy's side (so a retried or
// concurrent claim pays once; the second is 409 entitlement_claimed), and the roll spends the tokens it paid. A claim
// that succeeds and a roll that then fails leaves the row claimed: the open is retried within ClaimedRecoveryWindow,
// finds the claimed row in its slot, and rolls whatever tokens the claim paid (the roll's own key replays when the roll
// had in fact happened).
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
                openTime = MasterOpenTime(capsule.Type),
                lotteryDiscRarityGroupId = golden ? 2 : 1,
                lotteryDiscDropAmountGroupId = golden ? 2 : 1
            });
        }
        return JsonSerializer.Serialize(rows);
    }

    // The Capsule master's openTime column, which the client prints on its capsule information screen. It is the client's
    // own copy of the timer, not the timer: what a slot waits is the tenant trigger's delay and reaches the client in the
    // slot's completeDatetime and openTimeSecond. The two are kept equal by hand (see the tenant's README); a capsule
    // type this does not know is shown as ready.
    private static string MasterOpenTime(string type) => type switch
    {
        "wooden" => "00:15:00",
        "standard" => "01:00:00",
        "golden" => "04:00:00",
        _ => "00:00:00"
    };

    // The entitlement kind the KF tenant declares for capsules, and how long after a claim an open may still find the row
    // (a claim that paid and a roll that failed leave a claimed row nobody lists any more).
    private const string CapsuleKind = "capsule";
    private const string CapsuleRulePrefix = "match.capsule.";
    private static readonly TimeSpan ClaimedRecoveryWindow = TimeSpan.FromMinutes(10);

    // One entitlement as the client's slot: which slot, which Capsule master row, when it opens.
    private sealed record CapsuleSlot(int SlotNumber, EconomyEntitlement Row, string Type, int CapsuleId, int OpenTimeSecond)
    {
        public string EntitlementId => Row.Id;
        public DateTimeOffset UnlockAt => Row.AvailableAt;
        public bool Claimed => Row.ClaimedAt is not null;
    }

    // The capsule rows among a player's entitlements, oldest first. Other kinds are not ours to show. Claimed rows are
    // read too: a slot's number depends on what was open when the row was made.
    private async Task<List<EconomyEntitlement>> ListCapsuleRowsAsync(KfIdentityTicket ticket,
        CancellationToken cancellationToken) =>
        (await _economy.ListEntitlementsAsync(ticket.OtomoPlayerId, includeClaimed: true, cancellationToken))
            .Where(row => row.Kind == CapsuleKind && row.Rule.StartsWith(CapsuleRulePrefix, StringComparison.Ordinal))
            .OrderBy(row => row.CreatedAt ?? row.AvailableAt).ThenBy(row => row.Id, StringComparer.Ordinal)
            .ToList();

    // The slot of every row: the lowest of the client's three that no earlier row still held when this one was made. A row
    // holds its slot from its creation until it is claimed, so claiming an older capsule moves nothing, and a battle
    // handed a slot a row has since freed gets that slot. That is what the stored slot number used to be, recomputed from
    // the timestamps economy keeps instead of kept in meta (a trigger writes none). Rows are oldest first.
    private List<CapsuleSlot> SlotsOf(IReadOnlyList<EconomyEntitlement> rows)
    {
        var slots = new List<CapsuleSlot>();
        var holding = new List<(int Slot, DateTimeOffset? ReleasedAt)>();
        foreach (var row in rows)
        {
            var made = row.CreatedAt ?? row.AvailableAt;
            holding.RemoveAll(held => held.ReleasedAt is { } released && released <= made);
            var free = Enumerable.Range(1, EconomyTuning.CapsuleSlots)
                .FirstOrDefault(number => holding.All(held => held.Slot != number));
            if (free == 0)
            {
                // More open rows than the client has slots: max_open is the tenant's, and it should be 3.
                _logger.LogWarning("Entitlement {Id} has no free capsule slot; not shown", row.Id);
                continue;
            }
            holding.Add((free, row.ClaimedAt));
            if (SlotFor(row, free) is { } slot) slots.Add(slot);
        }
        return slots;
    }

    private CapsuleSlot? SlotFor(EconomyEntitlement row, int slotNumber)
    {
        // The class is match.capsule.<type> or match.capsule.<type>.<discs>: the type is the first segment. The disc count is
        // not read here - the slot shows none; the open rolls as many draws as the claim paid tokens.
        var type = row.Rule[CapsuleRulePrefix.Length..].Split('.')[0];
        var tuned = _tuning.FindCapsule(type);
        if (tuned is null)
        {
            _logger.LogWarning("Entitlement {Id} names capsule type '{Type}', which the tuning does not have; not shown", row.Id, type);
            return null;
        }
        var openTime = (int)Math.Max(0, Math.Round((row.AvailableAt - (row.CreatedAt ?? row.AvailableAt)).TotalSeconds));
        return new CapsuleSlot(slotNumber, row, type, tuned.CapsuleId, openTime);
    }

    // ResponseUserCapsule[]: the home's capsule slots, from the open entitlements. With the internal plane off no
    // capsule can have been handed out, so the list is empty; with it on, economy not answering is a 503 and never an
    // empty list the client would render as "no capsules".
    private async Task<(object[]? List, Exception? Error)> TryBuildUserCapsuleListAsync(KfIdentityTicket ticket,
        CancellationToken cancellationToken)
    {
        if (!_economy.ProgressEnabled) return ([], null);
        try
        {
            var rows = await ListCapsuleRowsAsync(ticket, cancellationToken);
            return (SlotsOf(rows).Where(slot => !slot.Claimed).Select(UserCapsuleRow).ToArray(), null);
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
    // all three slots are taken, economy cannot be reached). The adapter decides nothing: whether
    // the battle earns a capsule, when it opens and whether the slots are full are the tenant's trigger and kind. A
    // retried result is economy's replay of the fact, which answers the capsule the first one minted, and a capsule
    // already opened is not shown again.
    private async Task<CapsuleSlot?> ShownBattleCapsuleAsync(KfIdentityTicket ticket, SessionState state,
        string battleEntryId, EconomyFact? fact, CancellationToken cancellationToken)
    {
        var trigger = fact?.Triggers?.FirstOrDefault(result => result.Kind == CapsuleKind);
        if (trigger is null) return null;
        if (!trigger.Minted)
        {
            _logger.LogInformation("Battle {Entry} hands user {UserId} no capsule: the trigger {Result} ({Code})",
                battleEntryId, state.UserId, trigger.Result, trigger.Code);
            return null;
        }

        try
        {
            var slot = SlotsOf(await ListCapsuleRowsAsync(ticket, cancellationToken))
                .FirstOrDefault(candidate => candidate.EntitlementId == trigger.EntitlementId && !candidate.Claimed);
            if (slot is not null)
            {
                _logger.LogInformation("Battle {Entry} handed user {UserId} a {Type} capsule in slot {Slot}, opens at {UnlockAt:u}",
                    battleEntryId, state.UserId, slot.Type, slot.SlotNumber, slot.UnlockAt);
            }
            return slot;
        }
        catch (Exception exception) when (exception is OtomoEconomyException or OtomoIdentityException)
        {
            // The battle result is answered without a capsule; the capsule exists, and the home shows it.
            _logger.LogWarning("Capsule {Id} for battle {Entry} (user {UserId}) could not be read back: {Error}",
                trigger.EntitlementId, battleEntryId, state.UserId, exception.Message);
            return null;
        }
    }

    // A capsule the player claimed moments ago and whose roll did not complete: the row is claimed, so no list shows it, but
    // the open that failed is retried by the client and must find it. Only the claimed row that held that slot, newest
    // first, and only inside ClaimedRecoveryWindow; the roll's own key replays when the roll had in fact happened.
    private CapsuleSlot? ClaimedAwaitingRoll(IEnumerable<CapsuleSlot> slots, int slotNumber)
    {
        var cutoff = _timeProvider.GetUtcNow() - ClaimedRecoveryWindow;
        return slots.Where(candidate => candidate.SlotNumber == slotNumber && candidate.Row.ClaimedAt is { } claimedAt &&
                                        claimedAt >= cutoff)
            .OrderByDescending(candidate => candidate.Row.ClaimedAt).FirstOrDefault();
    }

    // /capsule/open and /capsule/immediateOpen {slotNumber}. Both claim the slot's entitlement, which economy refuses
    // before its available_at, and then roll the pool on the tokens the claim paid. immediateOpen asks economy for the
    // kind's early claim first: a capsule not yet due is opened for the price the kind declares, charged by economy in
    // the claim's own transaction. A kind that declares no early claim leaves immediateOpen as open: refused until due.
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
            var slots = SlotsOf(await ListCapsuleRowsAsync(ticket, context.RequestAborted));
            capsule = slots.FirstOrDefault(slot => slot.SlotNumber == slotNumber && !slot.Claimed)
                      ?? ClaimedAwaitingRoll(slots, slotNumber);
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
            var early = false;
            if (immediate && !capsule.Claimed)
            {
                try
                {
                    var quote = await _economy.GetEarlyClaimAsync(capsule.EntitlementId, ticket.OtomoPlayerId,
                        context.RequestAborted);
                    early = true;
                    _logger.LogInformation("{Path}: capsule {Id} costs {Cost} to open now", path, capsule.EntitlementId,
                        string.Join(",", (quote.Cost ?? []).Select(charge => $"{charge.Amount} {charge.Currency}")));
                }
                catch (OtomoEconomyException ex) when (ex.Failure == OtomoEconomyFailure.Refused &&
                                                      ex.Code == "early_claim_not_offered")
                {
                    // The kind sells no early claim: this is a plain open, refused by economy until the capsule is due.
                    _logger.LogInformation("{Path}: the capsule kind offers no early claim; opening as {Open}", path, "/capsule/open");
                }
            }

            EconomyEntitlementClaim? claim = null;
            try
            {
                claim = await _economy.ClaimEntitlementAsync(capsule.EntitlementId, ticket.OtomoPlayerId,
                    ProgressKeyPart($"capsule-claim:{capsule.EntitlementId}"), context.RequestAborted, early);
                _logger.LogInformation("{Path}: user {UserId} claimed capsule {Id} ({Type}){Early}{Replayed}", path, state.UserId,
                    capsule.EntitlementId, capsule.Type,
                    claim.Charged is { Count: > 0 } charged
                        ? " early for " + string.Join(",", charged.Select(charge => $"{charge.Amount} {charge.Currency}"))
                        : "",
                    claim.Applied ? "" : " (payout already made)");
            }
            catch (OtomoEconomyException ex) when (ex.Failure == OtomoEconomyFailure.Refused &&
                                                  ex.Code == "entitlement_claimed")
            {
                // The claim of an earlier attempt that paid and then lost the roll: the class is paid, go on to the roll.
                _logger.LogInformation("{Path}: capsule {Id} was claimed by an earlier attempt; rolling", path,
                    capsule.EntitlementId);
            }

            // One draw per token the claim paid: the class carries the capsule's disc count. A claim that says what the
            // wallet holds is read as it is, otherwise the wallet is read; a roll that had already happened is a replay of
            // its key, whatever count is sent.
            var tokens = claim?.Balances?.FirstOrDefault(balance => balance.Currency == type.TokenCurrency)?.Balance;
            if (tokens is null)
            {
                var wallet = await ReadPlayerEconomyAsync(ticket, EconomyRead.Balances, path, context.RequestAborted);
                if (wallet.Snapshot is null) return EconomyUnavailable(context, path, wallet.Error);
                tokens = wallet.Snapshot.Balances.FirstOrDefault(balance => balance.Currency == type.TokenCurrency)?.Balance;
            }
            roll = await _economy.RollAsync(playerToken, type.PoolId, (int)Math.Max(1, tokens ?? 1),
                ProgressKeyPart($"capsule-open:{capsule.EntitlementId}"), context.RequestAborted);
        }
        catch (OtomoIdentityException ex)
        {
            return EconomyUnavailable(context, path, ex);
        }
        catch (OtomoEconomyException ex) when (ex.Failure == OtomoEconomyFailure.Refused && ex.Code == "insufficient_funds")
        {
            // The early claim's price is more than the wallet holds. Nothing moved: the charge, the payout and the
            // claimed flag are one transaction in economy.
            _logger.LogInformation("{Path} capsule {Id} ({Type}, slot {Slot}) for user {UserId}: not enough to open it early",
                path, capsule.EntitlementId, capsule.Type, slotNumber, state.UserId);
            return InsufficientFundsRefused(context, "Capsule", "You do not have enough to open this capsule now.");
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
