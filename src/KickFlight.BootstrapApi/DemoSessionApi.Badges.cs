using System.Globalization;
using KickFlight.BootstrapApi.Otomo;

namespace KickFlight.BootstrapApi;

// Badges, as the adapter translates them. It evaluates nothing.
//
//   earned     a lifetime track in economy pays a reward class (badge.<id>) that grants the item honor:<id>. The
//              adapter reports what happened as declared facts and claims the rungs that became claimable.
//   owned      the honor:* items of the player's economy inventory (the default honor is everyone's).
//   equipped   the player's session display slot "honor"; the default honor is the empty slot. Another player's is
//              `display.honor` of session's internal profile read.
//
// The tenant's documents (kf-otomo: economy.tasks, economy.rules, session.rules) are the rules; docs/OTOMO_BADGES.md
// is the mapping. Nothing here counts, compares a threshold or remembers a badge, so there is no local state to lose,
// to drift from economy's, or to merge from a stale save.
public sealed partial class DemoSessionApi
{
    // The names the tenant's documents use (kf/tools/gen_badges.py). A fact's fields are closed both ways, so the field
    // names below are exactly the declared ones.
    internal const string BattleFact = "battle.result";
    internal const string LoginFact = "login.day";
    internal const string UpgradeFact = "item.upgraded";
    internal const string LikeFact = "like.received";
    internal const string RegistrationFact = "registration.completed";

    internal const string BadgeTrackPrefix = "badge.";
    internal const string HonorItemPrefix = "honor:";
    internal const string HonorSlot = "honor";

    internal static string HonorItem(int honorId) => HonorItemPrefix + honorId.ToString(CultureInfo.InvariantCulture);

    internal static bool TryHonorId(string? item, out int honorId)
    {
        honorId = 0;
        return item is not null && item.StartsWith(HonorItemPrefix, StringComparison.Ordinal)
            && int.TryParse(item.AsSpan(HonorItemPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out honorId)
            && honorId > 0;
    }

    // The honors a player owns: the default, and every honor:<id> item in their inventory.
    internal static IReadOnlyList<int> OwnedHonorIds(EconomySnapshot economy) =>
        economy.Items.Where(item => item.Qty > 0 && TryHonorId(item.Item, out _))
            .Select(item => { TryHonorId(item.Item, out var id); return id; })
            .Append(BadgeCatalog.DefaultHonorId).Distinct().Order().ToArray();

    // A fact key names the thing that happened, so a retry of the same report counts once: 1-128 of [A-Za-z0-9._:-].
    internal static string FactKey(string kind, string part)
    {
        var prefix = kind + ":";
        var rest = ProgressKeyPart(part);
        return prefix + (rest.Length > 128 - prefix.Length ? rest[..(128 - prefix.Length)] : rest);
    }

    // battle.result, one per verified battle and player. Every declared field is present: a battle the adapter could not
    // credit is still a battle, reported with credited false, zero stats and the reason (the tenant's tracks condition on
    // `credited`, and no stat is read from a void battle).
    internal static Dictionary<string, object?> BattleFields(BadgeBattleStatReport report, int battleRuleType,
        int rankedRuleType, int rankAfter)
    {
        var credited = report.CreditStats;
        long Stat(string name) => credited && report.Stats.TryGetValue(name, out var value) ? value : 0;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["rule_type"] = report.BattleRuleType is >= 1 and <= 3 ? report.BattleRuleType : battleRuleType,
            ["kicker_id"] = report.KickerId,
            ["queue"] = credited ? (report.QueuedAsParty ? "team" : "solo") : "",
            ["credited"] = credited,
            ["credit_reason"] = report.Reason,
            ["outcome"] = report.Outcome.ToString().ToLowerInvariant(),
            ["won"] = credited && report.Won == true,
            ["flagged"] = report.CallerFlagged,
            ["mvp"] = Stat(BadgeStatNames.Mvp) > 0,
            ["rank_rule"] = rankedRuleType,
            ["rank_after"] = rankAfter,
            ["kills"] = Stat(BadgeStatNames.Kills),
            ["deaths"] = Stat(BadgeStatNames.Deaths),
            ["crystals_collected"] = Stat(BadgeStatNames.CrystalsCollected),
            ["guardian_released_crystals"] = Stat(BadgeStatNames.GuardianReleasedCrystals),
            ["crystals_released"] = Stat(BadgeStatNames.CrystalsReleased),
            ["flag_goals"] = Stat(BadgeStatNames.FlagGoals),
            ["flag_drops"] = Stat(BadgeStatNames.FlagDrops),
            ["ball_goals"] = Stat(BadgeStatNames.BallGoals),
            ["ball_drops"] = Stat(BadgeStatNames.BallDrops),
            ["moving_distance"] = Stat(BadgeStatNames.MovingDistance)
        };
    }

    // One fact on the internal plane, and the badges the report itself paid. The tenant's badge tracks are `auto_claim`: the
    // step is paid in the same transaction as the report, once, and the answer lists it, so there is nothing to claim and
    // nothing to sweep. Off (no key) is "nothing to do"; a failure is a warning and never a broken screen. A step economy
    // refused (a ceiling, the catalogue) stays claimable by the player and is logged here, never retried by the adapter.
    // `countReplays` is false for a report whose answer a replay would repeat as news (a second startup the same day).
    private async Task<IReadOnlyList<int>> ReportBadgeFactAsync(string otomoPlayerId, string fact,
        IReadOnlyDictionary<string, object?> fields, string key, string reason, string reference, bool countReplays,
        CancellationToken cancellationToken) =>
        HonorsOf(await SendFactAsync(otomoPlayerId, fact, fields, key, reason, reference, cancellationToken),
            otomoPlayerId, countReplays);

    // The report itself, and the whole answer: auto-claimed badge steps (`auto_claims`) and the entitlements the tenant's
    // triggers minted (`triggers`) are read from the same answer by whoever asked. Null when the report is off or failed.
    private async Task<EconomyFact?> SendFactAsync(string otomoPlayerId, string fact,
        IReadOnlyDictionary<string, object?> fields, string key, string reason, string reference,
        CancellationToken cancellationToken)
    {
        if (!_economy.ProgressEnabled)
        {
            _logger.LogDebug("Badge fact {Fact} for {Reference} not reported: Otomo:Economy:ProgressEnabled is off", fact, reference);
            return null;
        }
        try
        {
            return await _economy.ReportFactAsync(new EconomyFactReport(otomoPlayerId, fact, fields, key, reason,
                ProgressKeyPart(reference)), cancellationToken);
        }
        catch (Exception exception) when (exception is OtomoEconomyException or HttpRequestException)
        {
            _logger.LogWarning("Badge fact {Fact} ({Key}) was not reported: {Error}", fact, key, exception.Message);
            return null;
        }
    }

    // The honors an answer's auto-claimed steps granted.
    private IReadOnlyList<int> HonorsOf(EconomyFact? answer, string otomoPlayerId, bool countReplays)
    {
        if (answer is null) return [];
        var granted = new List<int>();
        foreach (var claim in answer.AutoClaims ?? [])
        {
            if (claim.Result != "claimed")
            {
                _logger.LogWarning("Badge step {Track}#{Step} ({Class}) for {PlayerId} was refused at payment: {Code}",
                    claim.TrackId, claim.Step, claim.Class, otomoPlayerId, claim.Code);
                continue;
            }
            foreach (var move in claim.Items ?? [])
            {
                if (TryHonorId(move.Item, out var honorId) && !granted.Contains(honorId)) granted.Add(honorId);
            }
        }
        if (answer.Replayed && !countReplays) return [];
        if (granted.Count > 0)
            _logger.LogInformation("Player {PlayerId} was paid badges {Honors}", otomoPlayerId, string.Join(",", granted));
        return granted;
    }

    // The battle's fact, reported ONCE per battle entry (key badge-battle:<entry>) with every field the tenant declares. Its
    // one answer serves two readers: the badges its auto_claim steps paid, and the capsule its entitlement trigger minted
    // (DemoSessionApi.Capsules.cs). `rankedRuleType`/`rankAfter` are the battle rule type and the rank after it for a ranked
    // battle, 0 and -1 otherwise. The win streak is the tenant's `streak` measure over this same fact: nothing is read back
    // or set here.
    private async Task<(IReadOnlyList<int> Honors, EconomyFact? Fact)> RecordBadgeBattleAsync(KfIdentityTicket ticket,
        string battleEntryId, BadgeBattleStatReport report, int battleRuleType, int rankedRuleType, int rankAfter,
        CancellationToken cancellationToken)
    {
        var answer = await SendFactAsync(ticket.OtomoPlayerId, BattleFact,
            BattleFields(report, battleRuleType, rankedRuleType, rankAfter), FactKey("badge-battle", battleEntryId),
            "battle_result", battleEntryId, cancellationToken);
        return (HonorsOf(answer, ticket.OtomoPlayerId, countReplays: true), answer);
    }

    // The day is the key, so a second startup the same UTC day counts nothing and "login days" is a count of days.
    private Task<IReadOnlyList<int>> RecordBadgeLoginDayAsync(KfIdentityTicket ticket, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var day = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return ReportBadgeFactAsync(ticket.OtomoPlayerId, LoginFact, new Dictionary<string, object?> { ["day"] = day },
            FactKey("badge-login", day), "login_day", day, countReplays: false, cancellationToken);
    }

    // A disc went up one level. The caller has already proved it from economy's advance answer.
    private Task<IReadOnlyList<int>> RecordBadgeUpgradeAsync(KfIdentityTicket ticket, string item, long level,
        string advanceKey, CancellationToken cancellationToken) =>
        ReportBadgeFactAsync(ticket.OtomoPlayerId, UpgradeFact,
            new Dictionary<string, object?> { ["item"] = item, ["level"] = level },
            FactKey("badge-upgrade", advanceKey), "item_upgraded", advanceKey, countReplays: true, cancellationToken);

    private Task<IReadOnlyList<int>> RecordBadgeRegistrationAsync(KfIdentityTicket ticket, CancellationToken cancellationToken) =>
        ReportBadgeFactAsync(ticket.OtomoPlayerId, RegistrationFact, new Dictionary<string, object?>(),
            "badge-registration", "registration_complete", "tutorial-end", countReplays: false, cancellationToken);

    // A like the target received. The target is somebody else: their badge is paid by the report, and they see it the
    // next time they load their honors.
    private Task<IReadOnlyList<int>> RecordBadgeLikeAsync(string targetOtomoPlayerId, string battleId, string likerUserId,
        CancellationToken cancellationToken) =>
        ReportBadgeFactAsync(targetOtomoPlayerId, LikeFact, new Dictionary<string, object?> { ["battle"] = battleId },
            FactKey("badge-like", $"{battleId}:{likerUserId}"), "battle_like", battleId, countReplays: false, cancellationToken);

    // The badge a player shows: their session display slot "honor", or the default honor when it is empty or session
    // cannot be asked.
    private async Task<int> EquippedHonorAsync(KfIdentityTicket ticket, CancellationToken cancellationToken)
    {
        if (!_profile.Enabled) return BadgeCatalog.DefaultHonorId;
        try
        {
            var map = await _profile.GetDisplayAsync(await PlayerTokenAsync(ticket, cancellationToken), cancellationToken);
            return HonorOfDisplay(map);
        }
        catch (Exception exception) when (exception is OtomoRankException or OtomoIdentityException or HttpRequestException)
        {
            _logger.LogWarning("Equipped badge of {PlayerId} not read ({Error}); showing the default", ticket.OtomoPlayerId, exception.Message);
            return BadgeCatalog.DefaultHonorId;
        }
    }

    internal static int HonorOfDisplay(IReadOnlyDictionary<string, string>? display) =>
        display is not null && display.TryGetValue(HonorSlot, out var item) && TryHonorId(item, out var honorId)
            ? honorId
            : BadgeCatalog.DefaultHonorId;

    // The badges on the cards of a set of players, by game player id: the caller's own from their display slot, everyone
    // else's from `display.honor` of session's internal profile read, kept for a few seconds so a screen of fifty rows
    // is not fifty reads the next time it is drawn. A player session cannot answer for shows the default.
    private static readonly TimeSpan HonorTtl = TimeSpan.FromSeconds(30);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Honor, DateTimeOffset Expires)> _honorsByAccount =
        new(StringComparer.Ordinal);

    private async Task<IReadOnlyDictionary<long, int>> HonorsOfAsync(KfIdentityTicket ticket, SessionState caller,
        IEnumerable<long> playerIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, int>();
        var others = new List<(long PlayerId, string Account)>();
        var callerWanted = false;
        foreach (var playerId in playerIds.Distinct())
        {
            if (playerId == caller.PlayerId) callerWanted = true;
            else if (_playerStore.FindOtomoPlayer(playerId) is { } link) others.Add((playerId, link.OtomoPlayerId));
        }
        if (callerWanted) result[caller.PlayerId] = await EquippedHonorAsync(ticket, cancellationToken);
        if (others.Count == 0 || !_profile.Enabled) return result;

        var now = _timeProvider.GetUtcNow();
        using var gate = new SemaphoreSlim(8);
        var reads = others.Select(async other =>
        {
            if (_honorsByAccount.TryGetValue(other.Account, out var hit) && hit.Expires > now) return (other.PlayerId, hit.Honor);
            await gate.WaitAsync(cancellationToken);
            try
            {
                var card = await _profile.GetPlayerProfileAsync(other.Account, cancellationToken);
                var honor = HonorOfDisplay(card.Display);
                _honorsByAccount[other.Account] = (honor, now + HonorTtl);
                return (other.PlayerId, honor);
            }
            catch (Exception exception) when (exception is OtomoRankException or HttpRequestException)
            {
                _logger.LogDebug("No equipped badge for {Account} from session ({Error}); showing the default", other.Account, exception.Message);
                return (other.PlayerId, BadgeCatalog.DefaultHonorId);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();
        foreach (var (playerId, honor) in await Task.WhenAll(reads)) result[playerId] = honor;
        return result;
    }

    // The client's equip. The default honor is the empty slot; anything else must be a honor:<id> the player owns, which
    // session checks against economy (404 item_unknown, 422 item_not_displayable, 403 item_not_owned). False when the
    // equip was refused, so the client is told what it always was: the request failed.
    private async Task<bool> EquipHonorAsync(KfIdentityTicket ticket, int honorId, CancellationToken cancellationToken)
    {
        if (honorId <= 0 || !_profile.Enabled) return false;
        try
        {
            var token = await PlayerTokenAsync(ticket, cancellationToken);
            if (honorId == BadgeCatalog.DefaultHonorId)
                await _profile.ClearDisplayAsync(token, HonorSlot, cancellationToken);
            else
                await _profile.SetDisplayAsync(token, HonorSlot, HonorItem(honorId), cancellationToken);
            return true;
        }
        catch (OtomoRankException exception)
        {
            _logger.LogInformation("Equip of honor {HonorId} for {PlayerId} refused by session: {Status} {Code}",
                honorId, ticket.OtomoPlayerId, exception.StatusCode, exception.Code);
            return false;
        }
        catch (Exception exception) when (exception is OtomoIdentityException or HttpRequestException)
        {
            _logger.LogWarning("Equip of honor {HonorId} for {PlayerId} failed: {Error}", honorId, ticket.OtomoPlayerId, exception.Message);
            return false;
        }
    }
}
