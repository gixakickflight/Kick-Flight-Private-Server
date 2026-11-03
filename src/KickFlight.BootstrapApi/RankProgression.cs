using KickFlight.BootstrapApi.PlayerStore;

namespace KickFlight.BootstrapApi;

// Battle rank as a real, persisted quantity.
//
// Rank is not free-form: the client looks its league badge up by rank and the badge set it ships is not
// complete. For the regular rules (battleRuleType 1) BattleUtil.GetDisplayRankPath caps the rank to
// BattleRankMaster.RegularEndRank and switches to badge variant 1, and in retail that variant exists only
// for rank 7. Any rank below 7 therefore asks for a badge that is not in the bundle and the UI breaks, which
// is why the floor here is rank 7 rather than 0 - the same reason the served BattleRank table ends the
// regular flags at rank 7. Above 7 the badge is the S+ ladder, which is complete through rank 16.
//
// Thresholds mirror the BattleRank master served in DemoSessionApi (row id = rank + 1, totalBattlePoint per
// rank): 0, 200, 500, 900, 1400, 1900, 2400, [2900 = rank 7], 3400, 3900, 4400, 4900, 5400, 5900, 6400,
// 6900, 7500. Keep the two in step: the client interpolates the progress bar between them, so a rank that
// disagrees with its own threshold renders a bar past its end.
public static class RankProgression
{
    public const int MaxRank = 16;

    // The lowest rank whose badge the client can actually render. See the note above.
    public const int MinRank = 7;

    // A new player starts here: rank S. It is the same rank the server hardcoded before ranks were persisted,
    // chosen because it is the one rank that renders on every UI path.
    public const int StartRank = 7;
    public const int StartBattlePoint = 2900;

    // Points awarded and deducted per battle. The served table's win/lose coefficients are all 1.0 (retail
    // values were never recoverable from the client), so the delta is a decision, not a derivation: 50 is
    // enough that a rank-up is reachable in a short test session - ten wins from the start - while staying
    // inside the 500-point gaps between ranks.
    public const int WinBattlePoint = 50;
    public const int LoseBattlePoint = 20;

    // The battle rule types that keep a standing, in the order the client's payloads list them: Crystal Scramble,
    // Flag Flight, Rapid Ball. The client's userBattleRankList and the store's rows are keyed by these integers, and
    // one place has to name them or the two drift.
    public static readonly int[] BattleRuleTypes = [1, 2, 3];

    public static readonly int[] TotalBattlePointByRank =
    [
        0, 200, 500, 900, 1400, 1900, 2400, 2900, 3400, 3900, 4400, 4900, 5400, 5900, 6400, 6900, 7500
    ];

    // The rank a battle point total places the player in, clamped to the renderable range.
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
    // cannot fall out of the renderable ranks and cannot climb past the last one.
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
