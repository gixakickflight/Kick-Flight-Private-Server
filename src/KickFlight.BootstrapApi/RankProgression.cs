using KickFlight.BootstrapApi.PlayerStore;

namespace KickFlight.BootstrapApi;

// Battle rank as a real, persisted quantity.
//
// Rank is not free-form: the client looks its league badge up by rank. BattleUtil.GetDisplayRankPath (0x15B74DC)
// asks for ui/league/thumbnail_league_<rank> (variant 0) for every rank below BattleRankMaster.RegularEndRank, and
// only for a regular match at or above RegularEndRank (7, S) does it cap to RegularEndRank with variant 1
// (thumbnail_league_7_1). Every one of thumbnail_league_0..16 and 7_1 ships, so every rank renders. Badge 0 is a
// blank plate, not a letter: the lowest lettered rank is 1, C, which is where a new player starts and the floor a
// demotion stops at.
//
// Thresholds mirror the BattleRank master served in DemoSessionApi (row id = rank + 1, totalBattlePoint per
// rank): 0, [200 = rank 1], 500, 900, 1400, 1900, 2400, 2900, 3400, 3900, 4400, 4900, 5400, 5900, 6400,
// 6900, 7500. Keep the two in step: the client interpolates the progress bar between them, so a rank that
// disagrees with its own threshold renders a bar past its end.
public static class RankProgression
{
    public const int MaxRank = 16;

    // The lowest lettered rank, C. See the note above.
    public const int MinRank = 1;

    // A new player starts at the bottom of the ladder and climbs it.
    public const int StartRank = MinRank;
    public const int StartBattlePoint = 200;

    // Points awarded and deducted per battle. The served table's win/lose coefficients are all 1.0 (retail
    // values were never recoverable from the client), so the delta is a decision, not a derivation: 50 is
    // enough that a rank-up is reachable in a short test session - six wins from the start - while staying
    // inside the 300-600-point gaps between ranks.
    public const int WinBattlePoint = 50;
    public const int LoseBattlePoint = 20;

    public static readonly int[] TotalBattlePointByRank =
    [
        0, 200, 500, 900, 1400, 1900, 2400, 2900, 3400, 3900, 4400, 4900, 5400, 5900, 6400, 6900, 7500
    ];

    // The rank a battle point total places the player in, clamped to the lettered range.
    public static int RankFor(int battlePoint)
    {
        var rank = MinRank;
        for (var candidate = MinRank; candidate <= MaxRank; candidate++)
        {
            if (TotalBattlePointByRank[candidate] <= battlePoint) rank = candidate;
            else break;
        }
        return Math.Clamp(rank, MinRank, MaxRank);
    }

    // The battle point total at the floor of a rank, which is what a demotion clamps to.
    public static int FloorBattlePoint(int rank) =>
        TotalBattlePointByRank[Math.Clamp(rank, MinRank, MaxRank)];

    // The outcome of one battle, as the new standing. Both the total and the rank are clamped: a player
    // cannot fall below C and cannot climb past the last rank.
    public static RankState Apply(RankState current, bool won)
    {
        var total = won ? current.BattlePoint + WinBattlePoint : current.BattlePoint - LoseBattlePoint;
        var floor = FloorBattlePoint(MinRank);
        var ceiling = TotalBattlePointByRank[MaxRank];
        total = Math.Clamp(total, floor, ceiling);
        return new RankState(total, RankFor(total));
    }

    // The standing a player who has never battled holds.
    public static RankState Starting => new(StartBattlePoint, StartRank);
}
