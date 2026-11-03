namespace KickFlight.BootstrapApi.Otomo;

// The whole of the adapter's knowledge about battle points: how the client's units map onto session's. Nothing here
// is a rule. Session owns the points, the floor and the tiers; this class only converts between the two scales and
// parses the one tier-key shape session publishes.
//
//   client battle points = session points + ClientPointsOffset
//
// The offset is KF's old starting score: the client's BattleRank master puts rank 1 (C) at 200, session's ladder puts
// rank_1 at 0. Session floors points at 0, so KF's old floor of 200 is preserved without any code here.
//
// KF used to cap a player at 7500 (rank 16's threshold). Session does not enforce a ceiling, so a player can now
// exceed it; that is accepted, and the client simply keeps drawing rank 16.
public static class RankTranslation
{
    public const int ClientPointsOffset = 200;

    public static int ClientPoints(int sessionPoints) => sessionPoints + ClientPointsOffset;

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
