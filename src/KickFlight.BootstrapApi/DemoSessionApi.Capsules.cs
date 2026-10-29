using System.Globalization;
using System.Text.Json;
using KickFlight.BootstrapApi.Otomo;

namespace KickFlight.BootstrapApi;

// Capsules: what a battle hands out, and the disc draw that opening one is.
//
// The value is economy's at both ends. A battle result pays the match.capsule.<type> class - one capsule_<type>
// token per disc the capsule holds - on the internal plane, keyed by the battle entry, so a retried result pays
// once. Opening the capsule is one roll of the capsule.<type> pool on the player's own token, with count equal to
// the capsule's disc amount; the pool costs one token per draw, which is exactly what the battle paid, and the
// draws are economy's. The adapter decides only presentation, from EconomyTuning: which type a battle earns
// (60/30/10), how many discs it holds, and when its slot may be opened.
//
// What the adapter keeps is the slot (SessionState.Capsules): which capsule sits in which of the client's three
// slots and its unlock time. That is in the save, not in memory, so a restart keeps a player's capsules.
//
// Order of operations, chosen so that no failure loses a capsule or pays one twice:
//
//   battle result  slot saved first, then the tokens granted (keys capsule:<entry>:<n>). A grant that fails is
//                  logged, and the slot is kept: the open grants again under the same keys before it rolls.
//   open           tokens re-granted (replays when the battle already paid), one roll keyed capsule-open:<entry>,
//                  then the slot cleared. A retry after a crash replays the roll and shows the same discs.
//
// A full set of slots means the battle hands out nothing and pays nothing: a token with no slot to open it from
// would be a balance the player can never use.
public sealed partial class DemoSessionApi
{
    // The tutorial's capsule: TutorialUtil.CAPSULE_ID, opened after OPEN_CAPSULE_DURATION (8) seconds.
    private const int TutorialCapsuleId = 5010000;

    // How far ahead of this server's clock a device may be and still open a capsule its own countdown has finished.
    private static readonly TimeSpan CapsuleClockGrace = TimeSpan.FromMinutes(1);

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
        foreach (var capsule in _tuning.Capsules)
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

    // ResponseUserCapsule[]: the home's capsule slots.
    private object[] BuildUserCapsuleList(SessionState state) =>
        state.Capsules.OrderBy(capsule => capsule.SlotNumber).Select(UserCapsuleRow).ToArray();

    // completeDatetime carries its offset. UserCapsuleInfo..ctor (0x18F1398) reads it with TimeUtil.GetDatetimeFromString
    // (0x161E520), which is a bare DateTimeOffset.TryParse: a string without an offset is taken in the device's own
    // zone. The slot compares it with TimeUtil.GetCurrentDateTime, which is an instant (FromUnixTimeSeconds), so an
    // offset-less UTC stamp opened the slot early by the device's UTC offset - 8 hours on a UTC+8 phone - and the
    // open was refused here as not yet unlocked.
    private static object UserCapsuleRow(PendingCapsule capsule) => new
    {
        slotNumber = capsule.SlotNumber,
        capsuleId = capsule.CapsuleId,
        completeDatetime = EconomyPayloads.Instant(capsule.UnlockAt),
        openTimeSecond = (int)Math.Max(0, (capsule.UnlockAt - capsule.GrantedAt).TotalSeconds)
    };

    // The capsule a battle result hands out, or null when it hands out none (no issued entry, the internal plane
    // is off, every slot is taken). A retried result answers the capsule the first one handed out.
    private async Task<PendingCapsule?> AwardBattleCapsuleAsync(KfIdentityTicket ticket, SessionState state,
        string battleEntryId, CancellationToken cancellationToken)
    {
        var source = ProgressKeyPart(battleEntryId);
        if (source.Length == 0 || !_economy.ProgressEnabled) return null;

        PendingCapsule? capsule;
        lock (state)
        {
            capsule = state.Capsules.FirstOrDefault(row => row.Source == source);
            if (capsule is not null) return capsule;

            var slot = Enumerable.Range(1, EconomyTuning.CapsuleSlots)
                .FirstOrDefault(number => state.Capsules.All(row => row.SlotNumber != number));
            if (slot == 0)
            {
                _logger.LogInformation("Battle {Entry} hands user {UserId} no capsule: all {Slots} slots are full",
                    battleEntryId, state.UserId, EconomyTuning.CapsuleSlots);
                return null;
            }

            var (type, discCount) = _tuning.RollCapsule(Random.Shared);
            var now = _timeProvider.GetUtcNow();
            capsule = new PendingCapsule
            {
                SlotNumber = slot,
                CapsuleId = type.CapsuleId,
                Type = type.Type,
                DiscCount = discCount,
                GrantedAt = now,
                UnlockAt = now + type.Unlock,
                Source = source
            };
            state.Capsules.Add(capsule);
            SaveUserState(state);
        }

        try
        {
            await GrantCapsuleTokensAsync(ticket, capsule, cancellationToken);
        }
        catch (Exception exception) when (exception is OtomoEconomyException or OtomoIdentityException)
        {
            // The slot stays: opening it grants the same keys again, so the tokens arrive late rather than never.
            _logger.LogWarning("Capsule tokens for battle {Entry} (user {UserId}) not paid yet: {Error}",
                battleEntryId, state.UserId, exception.Message);
        }

        _logger.LogInformation("Battle {Entry} handed user {UserId} a {Type} capsule ({Discs} discs) in slot {Slot}, opens at {UnlockAt:u}",
            battleEntryId, state.UserId, capsule.Type, capsule.DiscCount, capsule.SlotNumber, capsule.UnlockAt);
        return capsule;
    }

    // One token per disc, each under its own key: a replay pays nothing, so this is safe to run again before every
    // open. Throws OtomoEconomyException(Disabled) when the internal plane is off.
    private async Task GrantCapsuleTokensAsync(KfIdentityTicket ticket, PendingCapsule capsule,
        CancellationToken cancellationToken)
    {
        var type = _tuning.FindCapsule(capsule.Type)
                   ?? throw new OtomoEconomyException(OtomoEconomyFailure.Refused,
                       $"capsule type '{capsule.Type}' is not in the tuning");
        for (var index = 0; index < capsule.DiscCount; index++)
        {
            var paid = await GrantClassAsync(ticket, type.GrantClass, $"capsule:{capsule.Source}:{index}",
                "battle_capsule", capsule.Source, cancellationToken);
            if (paid is null)
                throw new OtomoEconomyException(OtomoEconomyFailure.Disabled, "the internal plane is off");
        }
    }

    // /capsule/open and /capsule/immediateOpen {slotNumber}. The two differ only in the timer: open refuses before
    // the unlock time (less CapsuleClockGrace, for a device clock slightly ahead of this one), and immediateOpen
    // skips it. Either way it is one roll, paid by the tokens the battle granted, and the slot empties.
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

        PendingCapsule? capsule;
        lock (state) capsule = state.Capsules.FirstOrDefault(row => row.SlotNumber == slotNumber);
        var type = capsule is null ? null : _tuning.FindCapsule(capsule.Type);
        if (capsule is null || type is null)
        {
            _logger.LogWarning("{Path} for user {UserId}: slot {Slot} holds no capsule", path, state.UserId, slotNumber);
            return StatusError(context, key);
        }
        if (!immediate && _timeProvider.GetUtcNow() + CapsuleClockGrace < capsule.UnlockAt)
        {
            _logger.LogInformation("{Path} for user {UserId}: slot {Slot} opens at {UnlockAt:u}", path, state.UserId,
                slotNumber, capsule.UnlockAt);
            return StatusError(context, key);
        }

        EconomyRoll roll;
        try
        {
            await GrantCapsuleTokensAsync(ticket, capsule, context.RequestAborted);
            roll = await _economy.RollAsync(await PlayerTokenAsync(ticket, context.RequestAborted), type.PoolId,
                capsule.DiscCount, ProgressKeyPart($"capsule-open:{capsule.Source}"), context.RequestAborted);
        }
        catch (OtomoIdentityException ex)
        {
            return EconomyUnavailable(context, path, ex);
        }
        catch (OtomoEconomyException ex) when (ex.Failure == OtomoEconomyFailure.Refused)
        {
            _logger.LogWarning("{Path} pool {Pool} x{Count} for user {UserId} was refused by economy: {Status} {Code}",
                path, type.PoolId, capsule.DiscCount, state.UserId, ex.StatusCode, ex.Code);
            return StatusError(context, key);
        }
        catch (OtomoEconomyException ex)
        {
            return EconomyUnavailable(context, path, ex);
        }

        lock (state)
        {
            state.Capsules.RemoveAll(row => row.Source == capsule.Source);
            SaveUserState(state);
        }

        var payloads = Payloads(state);
        var dropDiscList = payloads.BuildDropDiscList(roll.Draws);
        var receivedUserDiscList = payloads.BuildReceivedUserDiscList(roll.Items, discId => DiscStandingOf(state, discId));
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
