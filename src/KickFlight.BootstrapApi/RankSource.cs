using KickFlight.BootstrapApi.Otomo;
using KickFlight.BootstrapApi.PlayerStore;
using Microsoft.Extensions.Options;

namespace KickFlight.BootstrapApi;

// Where a player's standing comes from, in one place.
//
// Two sources answer the same question: the adapter's own rank store (what every deployment has always read) and
// otomo's player rank service (what rank is actually kept in now). Otomo:Rank:Enabled picks between them, once, at
// construction: with it off this class is a thin pass-through to IPlayerStore and behaves exactly as the payload
// builders used to; with it on nothing on the payload path touches the store's ranks at all.
//
// The two things worth knowing before changing this:
//
//   * otomo's rank route is the token's subject. There is no route that names another player, so `ReadAllAsync`
//     answers for the caller only and returns null for anyone else. The caller then reads as a player who has never
//     battled (RankProgression.Starting) rather than falling back to the store: falling back would be the stale,
//     second answer this change exists to remove.
//   * rank is display state, not value. A read that cannot be answered logs and serves the starting standing instead
//     of failing the whole home or ranking payload - unlike the economy, whose numbers a player spends and where a
//     made-up zero is a lie that costs them.
public interface IRankSource
{
    // True when otomo answers rank. Callers use it to skip their own store reads rather than to branch on a string.
    bool UsesOtomo { get; }

    // The caller's standing in every rule type the client shows, or null when this source cannot answer for that
    // player (otomo, for another player's card).
    ValueTask<IReadOnlyDictionary<int, RankState>?> ReadAllAsync(KfIdentityTicket ticket, long playerId,
        CancellationToken cancellationToken);

    // One rule type. Null when this source cannot answer for that player.
    ValueTask<RankState?> ReadAsync(KfIdentityTicket ticket, long playerId, int battleRuleType,
        CancellationToken cancellationToken);

    // One ranked result: the standing it leaves the player in.
    //
    // With the adapter's store this is the progression it has always applied - load, add the win, save - and the
    // caller must still hold its per-battle gate around it, because the call writes.
    //
    // With otomo it writes nothing and returns `before`: a match's points are the matchmaker's award on session's
    // internal listener, keyed by the match id, and an adapter that moved them here too would be a second rank
    // authority racing the first. The gate still runs, so a retried result reports the same numbers.
    RankState ApplyRankedResult(long playerId, int battleRuleType, RankState before, bool won);
}

public sealed class RankSource : IRankSource
{
    private readonly IPlayerStore _store;
    private readonly IOtomoRank _otomo;
    private readonly IOtomoIdentity _identity;
    private readonly OtomoRankOptions _options;
    private readonly ILogger<RankSource> _logger;

    public RankSource(IPlayerStore store, IOtomoRank otomo, IOtomoIdentity identity,
        IOptions<OtomoRankOptions> options, ILogger<RankSource> logger)
    {
        _store = store;
        _otomo = otomo;
        _identity = identity;
        _options = options.Value;
        _logger = logger;
    }

    public bool UsesOtomo => _otomo.Enabled;

    public async ValueTask<IReadOnlyDictionary<int, RankState>?> ReadAllAsync(KfIdentityTicket ticket,
        long playerId, CancellationToken cancellationToken)
    {
        if (!UsesOtomo) return FromStore(playerId);

        // otomo's rank is the token's own player. Another player's card has no otomo answer, and the store is no
        // longer a second one.
        if (!IsCaller(ticket, playerId)) return null;

        IReadOnlyList<OtomoRankEntry> entries;
        try
        {
            entries = await _otomo.ReadAllAsync(await AccessTokenAsync(ticket, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OtomoRankException or OtomoIdentityException)
        {
            _logger.LogWarning("Rank read for player {PlayerId} failed, serving the starting standing: {Error}",
                playerId, exception.Message);
            return FromStarting();
        }

        var byRuleType = new Dictionary<string, OtomoRankEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.RuleType)) byRuleType[entry.RuleType] = entry;
        }

        var ranks = new Dictionary<int, RankState>(RankProgression.BattleRuleTypes.Length);
        foreach (var battleRuleType in RankProgression.BattleRuleTypes)
        {
            // A rule type this deployment has not mapped, or one the published ladder does not name, is a player who
            // has not been awarded in it: zero points, the lowest tier. It is not a reason to read the old table.
            if (!_options.RuleTypes.TryGetValue(battleRuleType, out var ruleType) ||
                !byRuleType.TryGetValue(ruleType, out var entry))
            {
                ranks[battleRuleType] = Unranked;
                continue;
            }
            ranks[battleRuleType] = StandingOf(entry.Points);
        }

        return ranks;
    }

    public async ValueTask<RankState?> ReadAsync(KfIdentityTicket ticket, long playerId, int battleRuleType,
        CancellationToken cancellationToken)
    {
        if (!UsesOtomo) return _store.LoadRank(playerId, battleRuleType);
        if (!IsCaller(ticket, playerId)) return null;

        // The single-rule route, not the list filtered here: it is otomo's own answer for "this ladder, this player",
        // and a 404 (the ladder does not rank that battle) is a miss rather than a failure.
        if (!_options.RuleTypes.TryGetValue(battleRuleType, out var ruleType)) return Unranked;

        try
        {
            var entry = await _otomo
                .ReadOneAsync(ruleType, await AccessTokenAsync(ticket, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return entry is null ? Unranked : StandingOf(entry.Points);
        }
        catch (Exception exception) when (exception is OtomoRankException or OtomoIdentityException)
        {
            _logger.LogWarning("Rank read for player {PlayerId} rule {RuleType} failed, serving the starting standing: {Error}",
                playerId, battleRuleType, exception.Message);
            return RankProgression.Starting;
        }
    }

    public RankState ApplyRankedResult(long playerId, int battleRuleType, RankState before, bool won)
    {
        if (UsesOtomo) return before;

        var after = RankProgression.Apply(before, won);
        _store.SaveRank(playerId, battleRuleType, after);
        return after;
    }

    // The standing the client renders for an otomo entry.
    //
    // otomo counts battle points from zero and puts a player with no awards at zero in the lowest tier. The KF
    // client's ladder does not start at zero: its badge set cannot draw anything below the first renderable rank, so
    // the adapter has always served rank S at 2900 points (RankProgression.StartBattlePoint). Translating otomo's
    // total onto that ladder - 2900 plus otomo's points - is what makes a player with no awards read exactly as a new
    // player reads today, and what keeps the player's own row comparable with the leaderboard rows, which are still
    // the adapter's totals. The rank is then derived from the total with the same table the client already draws,
    // rather than from otomo's `tier` string: the client has no field for a tier name, only the integer badge.
    private static RankState StandingOf(int otomoPoints)
    {
        var battlePoint = RankProgression.StartBattlePoint + Math.Max(0, otomoPoints);
        battlePoint = Math.Min(battlePoint, RankProgression.TotalBattlePointByRank[RankProgression.MaxRank]);
        return new(battlePoint, RankProgression.RankFor(battlePoint));
    }

    // Zero points in the lowest renderable rank: otomo's "never awarded here" and, for a rule type this deployment
    // does not map, the same thing.
    private static RankState Unranked => StandingOf(0);

    private IReadOnlyDictionary<int, RankState> FromStore(long playerId)
    {
        var ranks = new Dictionary<int, RankState>(RankProgression.BattleRuleTypes.Length);
        foreach (var battleRuleType in RankProgression.BattleRuleTypes)
        {
            ranks[battleRuleType] = _store.LoadRank(playerId, battleRuleType);
        }
        return ranks;
    }

    private static IReadOnlyDictionary<int, RankState> FromStarting()
    {
        var ranks = new Dictionary<int, RankState>(RankProgression.BattleRuleTypes.Length);
        foreach (var battleRuleType in RankProgression.BattleRuleTypes)
        {
            ranks[battleRuleType] = RankProgression.Starting;
        }
        return ranks;
    }

    private static bool IsCaller(KfIdentityTicket ticket, long playerId) => ticket.GamePlayerId == playerId;

    // The player's own token, from the identity cache this process already holds: no second credential and no
    // second login, exactly as the economy client does it.
    private async ValueTask<string> AccessTokenAsync(KfIdentityTicket ticket, CancellationToken cancellationToken) =>
        (await _identity.GetSessionForDeviceIdAsync(ticket.DeviceId, cancellationToken).ConfigureAwait(false))
        .AccessToken;
}
