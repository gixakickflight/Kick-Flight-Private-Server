using System.Globalization;
using KickFlight.BootstrapApi.Otomo;

namespace KickFlight.BootstrapApi;

// Badges, as the adapter translates them. It evaluates nothing.
//
//   earned     a lifetime track in economy pays a reward class (badge.<id>) that grants the item honor:<id>. The
//              adapter reports what happened as declared facts and claims the rungs that became claimable.
//   owned      the honor:* items of the player's economy inventory (the default honor is everyone's).
//   equipped   the player's session display slot "honor"; the default honor is the empty slot.
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

    // The gauge counter behind each battle rule type's win streak (track badge.win_streak.<rule type>).
    internal static string StreakCounter(int battleRuleType) => $"badge.streak.{battleRuleType}";

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

    // One fact on the internal plane. Off (no key) is "nothing to do"; a failure is a warning and never a broken screen:
    // the report is keyed, and the next claim sweep pays whatever the tracks have reached.
    private async Task<bool> ReportBadgeFactAsync(string otomoPlayerId, string fact,
        IReadOnlyDictionary<string, object?> fields, string key, string reason, string reference,
        CancellationToken cancellationToken)
    {
        if (!_economy.ProgressEnabled)
        {
            _logger.LogDebug("Badge fact {Fact} for {Reference} not reported: Otomo:Economy:ProgressEnabled is off", fact, reference);
            return false;
        }
        try
        {
            await _economy.ReportFactAsync(new EconomyFactReport(otomoPlayerId, fact, fields, key, reason,
                ProgressKeyPart(reference)), cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is OtomoEconomyException or HttpRequestException)
        {
            _logger.LogWarning("Badge fact {Fact} ({Key}) was not reported: {Error}", fact, key, exception.Message);
            return false;
        }
    }

    // Pays every badge rung that has become claimable, on the player's own plane with their own token, and answers the
    // honors it granted (read from the claims' own item movements, not guessed). A rung nobody claimed yet - a like
    // that landed while the player was offline, a report whose claim failed - is simply claimed the next time.
    private async Task<IReadOnlyList<int>> ClaimBadgesAsync(KfIdentityTicket ticket, CancellationToken cancellationToken)
    {
        var granted = new List<int>();
        try
        {
            var token = await PlayerTokenAsync(ticket, cancellationToken);
            var tasks = await _economy.GetTasksAsync(token, cancellationToken);
            foreach (var track in (tasks.Tracks ?? []).Where(row => row.TrackId.StartsWith(BadgeTrackPrefix, StringComparison.Ordinal)))
            {
                foreach (var step in (track.Steps ?? []).Where(row => row.Claimable))
                {
                    var claim = await _economy.ClaimAsync(token, track.TrackId,
                        ProgressKeyPart($"badge-claim:{track.TrackId}:{track.Period}:{track.Repetition}:{step.Step}"),
                        cancellationToken);
                    foreach (var move in claim.Items ?? [])
                    {
                        if (TryHonorId(move.Item, out var honorId) && !granted.Contains(honorId)) granted.Add(honorId);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is OtomoEconomyException or OtomoIdentityException or HttpRequestException)
        {
            _logger.LogWarning("Badge claims for player {PlayerId} stopped: {Error}; the next sweep retries them",
                ticket.OtomoPlayerId, exception.Message);
        }
        if (granted.Count > 0)
            _logger.LogInformation("Player {PlayerId} was paid badges {Honors}", ticket.OtomoPlayerId, string.Join(",", granted));
        return granted;
    }

    // The battle's badge facts. `rankedRuleType`/`rankAfter` are the battle rule type and the rank after it for a ranked
    // battle, 0 and -1 otherwise.
    private async Task<IReadOnlyList<int>> RecordBadgeBattleAsync(KfIdentityTicket ticket, string battleEntryId,
        BadgeBattleStatReport report, int battleRuleType, int rankedRuleType, int rankAfter,
        CancellationToken cancellationToken)
    {
        if (!_economy.ProgressEnabled) return [];

        // The streak is the one thing a fact cannot say: it is where the run stands now, set rather than added to. Economy
        // holds it (a gauge counter with its peak), so it is read from there, never kept here. A flagged caller or an
        // undecidable result moves nothing, a loss or a tie resets it.
        long? streak = null;
        if (report.CreditStats && report.Won is { } won && report.BattleRuleType is >= 1 and <= 3)
        {
            try
            {
                var tasks = await _economy.GetTasksAsync(await PlayerTokenAsync(ticket, cancellationToken), cancellationToken);
                var counter = StreakCounter(report.BattleRuleType);
                var now = tasks.Tracks?.FirstOrDefault(row => row.Counter == counter)?.Value ?? 0;
                streak = won ? now + 1 : 0;
            }
            catch (Exception exception) when (exception is OtomoEconomyException or OtomoIdentityException or HttpRequestException)
            {
                _logger.LogWarning("Win streak for entry {Entry} not set: its present value could not be read ({Error})",
                    battleEntryId, exception.Message);
            }
        }

        await ReportBadgeFactAsync(ticket.OtomoPlayerId, BattleFact,
            BattleFields(report, battleRuleType, rankedRuleType, rankAfter), FactKey("badge-battle", battleEntryId),
            "battle_result", battleEntryId, cancellationToken);

        if (streak is { } value)
        {
            try
            {
                await _economy.ReportGaugeAsync(new EconomyGaugeReport(ticket.OtomoPlayerId,
                    StreakCounter(report.BattleRuleType), value, FactKey("badge-streak", battleEntryId),
                    "battle_result", ProgressKeyPart(battleEntryId)), cancellationToken);
            }
            catch (Exception exception) when (exception is OtomoEconomyException or HttpRequestException)
            {
                _logger.LogWarning("Win streak for entry {Entry} was not set: {Error}", battleEntryId, exception.Message);
            }
        }
        return await ClaimBadgesAsync(ticket, cancellationToken);
    }

    // The day is the key, so a second startup the same UTC day counts nothing and "login days" is a count of days.
    private async Task<IReadOnlyList<int>> RecordBadgeLoginDayAsync(KfIdentityTicket ticket, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var day = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await ReportBadgeFactAsync(ticket.OtomoPlayerId, LoginFact, new Dictionary<string, object?> { ["day"] = day },
            FactKey("badge-login", day), "login_day", day, cancellationToken);
        return await ClaimBadgesAsync(ticket, cancellationToken);
    }

    // A disc went up one level. The caller has already proved it from economy's advance answer.
    private async Task<IReadOnlyList<int>> RecordBadgeUpgradeAsync(KfIdentityTicket ticket, string item, long level,
        string advanceKey, CancellationToken cancellationToken)
    {
        await ReportBadgeFactAsync(ticket.OtomoPlayerId, UpgradeFact,
            new Dictionary<string, object?> { ["item"] = item, ["level"] = level },
            FactKey("badge-upgrade", advanceKey), "item_upgraded", advanceKey, cancellationToken);
        return await ClaimBadgesAsync(ticket, cancellationToken);
    }

    private async Task RecordBadgeRegistrationAsync(KfIdentityTicket ticket, CancellationToken cancellationToken)
    {
        await ReportBadgeFactAsync(ticket.OtomoPlayerId, RegistrationFact, new Dictionary<string, object?>(),
            "badge-registration", "registration_complete", "tutorial-end", cancellationToken);
        await ClaimBadgesAsync(ticket, cancellationToken);
    }

    // A like the target received. The target is somebody else, so their rung is claimed on their own next sweep.
    private Task<bool> RecordBadgeLikeAsync(string targetOtomoPlayerId, string battleId, string likerUserId,
        CancellationToken cancellationToken) =>
        ReportBadgeFactAsync(targetOtomoPlayerId, LikeFact, new Dictionary<string, object?> { ["battle"] = battleId },
            FactKey("badge-like", $"{battleId}:{likerUserId}"), "battle_like", battleId, cancellationToken);

    // The badge a player shows: their session display slot "honor", or the default honor when it is empty or session
    // cannot be asked. Other players' badges are not readable (session's internal profile read carries no display map),
    // so a card for somebody else shows the default; docs/OTOMO_BADGES.md names it.
    private async Task<int> EquippedHonorAsync(KfIdentityTicket ticket, CancellationToken cancellationToken)
    {
        if (!_profile.Enabled) return BadgeCatalog.DefaultHonorId;
        try
        {
            var map = await _profile.GetDisplayAsync(await PlayerTokenAsync(ticket, cancellationToken), cancellationToken);
            return map.TryGetValue(HonorSlot, out var item) && TryHonorId(item, out var honorId)
                ? honorId
                : BadgeCatalog.DefaultHonorId;
        }
        catch (Exception exception) when (exception is OtomoRankException or OtomoIdentityException or HttpRequestException)
        {
            _logger.LogWarning("Equipped badge of {PlayerId} not read ({Error}); showing the default", ticket.OtomoPlayerId, exception.Message);
            return BadgeCatalog.DefaultHonorId;
        }
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
