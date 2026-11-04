namespace KickFlight.BootstrapApi.Otomo;

// The whole of the adapter's knowledge about battle points: how to read the one tier-key shape session publishes.
// Nothing here is a rule and nothing converts units. Session owns the points, the start, the floor, the ceiling, the
// outcomes and their deltas, and the tenant publishes its ladder in the client's own units, so the battle points the
// client sees are session's points as they are.
public static class RankTranslation
{
    // The N of a tier key "rank_<N>", which is the client's rank number. Null for a key of any other shape: the
    // caller decides what an unknown tier means, this class never guesses a rank.
    public static int? RankNumber(string? tier)
    {
        const string prefix = "rank_";
        if (tier is null || !tier.StartsWith(prefix, StringComparison.Ordinal)) return null;
        return int.TryParse(tier.AsSpan(prefix.Length), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var rank)
            ? rank
            : null;
    }
}

// A player's standing on one rule type as the client reads it: battle points in the client's units and its rank number.
public sealed record BattleRankStanding(int BattlePoint, int Rank);
