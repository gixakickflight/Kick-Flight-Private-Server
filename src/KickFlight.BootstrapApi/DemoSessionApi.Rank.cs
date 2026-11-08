using System.Text;
using System.Text.Json;
using KickFlight.BootstrapApi.Otomo;
using KickFlight.BootstrapApi.PlayerStore;

namespace KickFlight.BootstrapApi;

// Battle points, as the client reads them.
//
// None of it is kept here. A player's points and tier are session's (IOtomoRank), the ladder they sit on is the
// published session.rank document (read through OtomoMasters, never typed in here), and this file only translates:
// the tenant publishes its ladder in the client's own units (start, floor, ceiling, outcomes and tiers), so the points
// session holds are the battle points the client reads, and the only wire translation left is the tier key rank_<N>
// to a rank number (Otomo/RankTranslation.cs).
//
// Three rules hold throughout, all of them "nothing is invented":
//
//   * A standing session cannot give is an outage (the 503 contract of SocialUnavailable), not zero points.
//   * A ladder the release did not publish is an outage too. There is no built-in ladder to fall back on.
//   * An award session did not accept is never dropped: the battle result is answered 503, and the client's retry
//     carries the same award key, which session applies once.
//   * The adapter names an outcome ("win" or "loss"), never a number: what an outcome is worth is the published policy.
public sealed partial class DemoSessionApi
{
    // The namespace the ladder is published under in the release bundle (Otomo:Masters:Documents).
    private const string RankLadderNamespace = "session.rank";

    // Regular matches report regular_win / regular_loss; the tenant's session.rank gives those outcomes their points
    // and the outcome_limits that stop them at S.
    private const string RegularOutcomePrefix = "regular_";

    // The BattleRank master's letter names, rank 0 .. 16. This is presentation the client needs and the ladder does not
    // carry; the thresholds beside it are the ladder's. Rank 0 (D) is the blank plate below the first tier, so its
    // total stays 0.
    private static readonly string[] BattleRankNames =
        ["D", "C", "C⁺", "B", "B⁺", "A", "A⁺", "S", "S⁺1", "S⁺2", "S⁺3", "S⁺4", "S⁺5", "S⁺6", "S⁺7", "S⁺8", "S⁺9"];

    // The first rank whose league badge is the "S" variant: the BattleRank master flips regularMatchFlag here.
    private const int FirstNonRegularRank = 7;

    // One player's standing on one of the client's three ladders, in the client's units.
    private sealed record BattleRankLine(int BattleRuleType, int BattlePoint, int Rank);

    private sealed record LadderCacheEntry(string Json, RankLadder? Ladder);

    private LadderCacheEntry? _ladderCache;

    // The published ladder, or null when the release has not carried a usable one. Parsed once per document.
    private RankLadder? CurrentLadder()
    {
        if (!_otomoMasters.TryGetDocument(RankLadderNamespace, out var json)) return null;
        var cached = Volatile.Read(ref _ladderCache);
        if (cached is not null && string.Equals(cached.Json, json, StringComparison.Ordinal)) return cached.Ladder;

        RankLadder? ladder = null;
        try
        {
            ladder = RankLadder.Parse(json);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            _logger.LogError("The published {Namespace} document is not usable, so there is no ladder: {Error}",
                RankLadderNamespace, exception.Message);
        }
        Volatile.Write(ref _ladderCache, new LadderCacheEntry(json, ladder));
        return ladder;
    }

    private RankLadder RequireLadder() =>
        CurrentLadder() ?? throw new OtomoRankException(OtomoRankFailure.Unavailable,
            $"the published {RankLadderNamespace} ladder is not available, and this server holds no ladder of its own");

    // The BattleRank master from the published tiers: totalBattlePoint is each tier's min_points, rank 0 stays 0.
    // The ladder must name exactly the ranks 1..16 the client has badges for; one that does not is refused rather than
    // reshaped, because a served master that disagrees with the badges renders a bar past its end.
    internal static string BuildBattleRankMasterJson(RankLadder ladder)
    {
        var expected = Enumerable.Range(1, BattleRankNames.Length - 1).ToArray();
        if (!ladder.Ranks.SequenceEqual(expected))
        {
            throw new InvalidDataException(
                $"session.rank names ranks [{string.Join(", ", ladder.Ranks)}], but the client's BattleRank master " +
                $"has exactly ranks 1..{BattleRankNames.Length - 1}");
        }

        var rows = new StringBuilder("[");
        for (var rank = 0; rank < BattleRankNames.Length; rank++)
        {
            var total = rank == 0 ? 0 : ladder.MinPoints(rank);
            if (rank > 0) rows.Append(',');
            rows.Append("{\"id\":").Append(rank + 1)
                .Append(",\"name\":\"").Append(BattleRankNames[rank]).Append('"')
                .Append(",\"rank\":").Append(rank)
                .Append(",\"regularMatchFlag\":").Append(rank < FirstNonRegularRank ? "true" : "false")
                .Append(",\"totalBattlePoint\":").Append(total)
                .Append(",\"winBattlePointCoefficient\":1.0,\"loseBattlePointCoefficient\":1.0,\"demontioableFlag\":false}");
        }
        return rows.Append(']').ToString();
    }

    // Puts the BattleRank master into the served set from the current ladder, or takes it out when there is none: a
    // master built from a ladder this server made up is worse than a missing one, and OtomoMasters has already refused
    // to start (or to apply a release) that lacks the document it was told to follow. Called with the master lock held,
    // or from the constructor.
    private void ApplyBattleRankMaster()
    {
        var ladder = CurrentLadder();
        if (ladder is null)
        {
            _encryptedMasters.Remove("BattleRank");
            _logger.LogError(
                "No published {Namespace} ladder: the BattleRank master is NOT served. Set Otomo:Masters:Enabled and " +
                "Documents, and publish the document", RankLadderNamespace);
            return;
        }

        try
        {
            _encryptedMasters["BattleRank"] = EncryptMaster(BuildBattleRankMasterJson(ladder));
        }
        catch (InvalidDataException exception)
        {
            _encryptedMasters.Remove("BattleRank");
            _logger.LogError("The BattleRank master is NOT served: {Error}", exception.Message);
        }
    }

    // --- reads -----------------------------------------------------------------------------------------------------

    // The standing of a player across the client's three ladders, from session's entries. A rule type with no entry
    // is session breaking its contract (it reports every rule type, start_points for an unplayed one).
    private IReadOnlyList<BattleRankLine> TranslateRanks(IReadOnlyList<SessionRankEntry> entries)
    {
        var lines = new List<BattleRankLine>(RankedBattleRuleTypes.Length);
        foreach (var battleRuleType in RankedBattleRuleTypes)
        {
            var ruleType = _socialOptions.RuleTypeName(battleRuleType) ?? throw new OtomoRankException(
                OtomoRankFailure.Refused, $"no session rule type is configured for battle rule type {battleRuleType}");
            var entry = entries.FirstOrDefault(candidate => string.Equals(candidate.RuleType, ruleType, StringComparison.Ordinal));
            lines.Add(entry is null
                ? throw NoEntry(ruleType)
                : new BattleRankLine(battleRuleType, entry.Points, TierRank(entry.Tier)));
        }
        return lines;
    }

    // Session answers one entry per rule type its policy names, start_points for a player who has not played, so a
    // missing entry is session breaking its contract: said out loud as an outage, never filled in with a number of ours.
    private static OtomoRankException NoEntry(string ruleType) =>
        new(OtomoRankFailure.Unavailable, $"session answered no rank entry for rule type '{ruleType}'");

    private static int TierRank(string? tier) =>
        RankTranslation.RankNumber(tier) ?? throw new OtomoRankException(OtomoRankFailure.Unavailable,
            $"session answered the tier '{tier}', which is not rank_<N>");

    // The caller's own standing: GET /api/player/session/rank with their own token.
    private async Task<IReadOnlyList<BattleRankLine>> ReadOwnBattleRanksAsync(KfIdentityTicket ticket,
        CancellationToken cancellationToken)
    {
        var accessToken = await PlayerTokenAsync(ticket, cancellationToken);
        return TranslateRanks(await _rank.GetOwnAsync(accessToken, cancellationToken));
    }

    // The cards of these players, for a screen that shows several: the caller's own through their token, everyone else
    // through the internal plane by otomo id. One round of requests, in parallel. A failure is returned, not thrown, so
    // the screen can answer it in the 503 contract.
    private async Task<(IReadOnlyDictionary<long, IReadOnlyList<BattleRankLine>>? Ranks, Exception? Error)>
        TryReadCardRanksAsync(KfIdentityTicket ticket, SessionState caller, IEnumerable<long> playerIds,
            CancellationToken cancellationToken)
    {
        try
        {
            var ids = playerIds.Distinct().ToArray();
            var reads = ids.Select(async id =>
            {
                if (id == caller.PlayerId) return (id, Lines: await ReadOwnBattleRanksAsync(ticket, cancellationToken));
                var link = _playerStore.FindOtomoPlayer(id);
                if (link is null) return (id, Lines: TranslateRanks([])); // no session account: no standing to show (throws)
                var entries = await _rank.GetPlayerAsync(link.OtomoPlayerId, cancellationToken);
                return (id, Lines: TranslateRanks(entries));
            }).ToArray();
            var results = await Task.WhenAll(reads);
            return (results.ToDictionary(result => result.id, result => result.Lines), null);
        }
        catch (Exception exception) when (exception is OtomoRankException or OtomoIdentityException)
        {
            return (null, exception);
        }
    }

    private static object[] BuildBattleRankList(IReadOnlyList<BattleRankLine> lines) =>
        lines.Select(line => (object)new { battleRuleType = line.BattleRuleType, battlePoint = line.BattlePoint, rank = line.Rank })
            .ToArray();

    // --- the battle result -------------------------------------------------------------------------------------------

    // One ranked result as an award: read the standing, post the award, translate both answers. Called at most once
    // per entry by ProcessRankedResultOnceAsync; session's key makes a repeat harmless as well.
    //
    //   key = "battle:" + <battle entry id> + ":" + <otomo player id>
    //
    // is the same for every retry of the same battle by the same player, so session applies it once (`applied` false on
    // the repeats). A failure is thrown to the handler, which answers 503: the award is never silently dropped.
    private async Task<BattleMatchmakingService.RankedResultSnapshot?> AwardRankedResultAsync(KfIdentityTicket ticket,
        SessionState state, string battleEntryId, int battleRuleType, BadgeBattleStatReport? statReport,
        string outcomePrefix, CancellationToken cancellationToken)
    {
        var won = RankWinFor(statReport, out var skipReason);
        if (won is null)
        {
            _logger.LogInformation("Ranked result for {UserId} entry {BattleEntryId}: rank unchanged ({Reason})",
                state.UserId, battleEntryId, skipReason);
            return null;
        }

        var ruleType = _socialOptions.RuleTypeName(battleRuleType) ?? throw new OtomoRankException(
            OtomoRankFailure.Refused, $"no session rule type is configured for battle rule type {battleRuleType}");

        var accessToken = await PlayerTokenAsync(ticket, cancellationToken);
        var current = await _rank.GetOwnAsync(accessToken, cancellationToken);
        var currentEntry = current.FirstOrDefault(entry => string.Equals(entry.RuleType, ruleType, StringComparison.Ordinal));
        var before = currentEntry is null
            ? throw NoEntry(ruleType)
            : new BattleRankStanding(currentEntry.Points, TierRank(currentEntry.Tier));

        var award = await _rank.AwardAsync(ticket.OtomoPlayerId, ruleType,
            outcomePrefix + (won.Value ? "win" : "loss"),
            $"battle:{battleEntryId}:{ticket.OtomoPlayerId}", cancellationToken);
        var after = new BattleRankStanding(award.Points, TierRank(award.Tier));

        if (!award.Applied)
        {
            // Session had this key already: the answer is the standing as it is now, which includes the award, and the
            // standing read just before it does too. There is no earlier point to show, so the screen shows no change.
            _logger.LogInformation(
                "Ranked result for {UserId} entry {BattleEntryId} was already awarded by session; showing the current standing",
                state.UserId, battleEntryId);
            before = after;
        }

        return new BattleMatchmakingService.RankedResultSnapshot(battleRuleType, before, after);
    }

    // The caller's standing on one ladder, for a battle result that awarded nothing (a tie, an undecided or an
    // unranked battle).
    private async Task<BattleRankStanding> ReadOwnStandingAsync(KfIdentityTicket ticket, int battleRuleType,
        CancellationToken cancellationToken)
    {
        var lines = await ReadOwnBattleRanksAsync(ticket, cancellationToken);
        var line = lines.FirstOrDefault(candidate => candidate.BattleRuleType == battleRuleType) ?? lines[0];
        return new BattleRankStanding(line.BattlePoint, line.Rank);
    }
}
