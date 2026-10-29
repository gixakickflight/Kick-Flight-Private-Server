using KickFlight.BootstrapApi.Otomo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The level-up price, read from the DiscBuildup master the client is actually served. What is pinned is the
// client's reading of the table (DiscParameterUtil.CalcBuildupParameter): row L + 1 prices the step from L, and
// totalDiscAmount is a cumulative copy count.
public sealed class EconomyDiscLevelsTests
{
    private static EconomyDiscLevels Served() =>
        EconomyDiscLevels.Parse(File.ReadAllText(Path.Combine(
            RepositoryPaths.FindRoot(AppContext.BaseDirectory), "config/masters_disc_buildup.json")),
            NullLogger.Instance);

    [Theory]
    // rarity, level -> target, Disc Force, duplicate copies spent
    [InlineData(0, 1, 2, 70, 2)]      // N: 1 -> 2 is row 2 (70 DF, total 3) minus the base copy
    [InlineData(1, 2, 3, 400, 0)]     // R: 2 -> 3 costs Disc Force alone (total 1 -> 1)
    [InlineData(1, 3, 4, 1200, 4)]    // R: 3 -> 4, total 1 -> 5
    [InlineData(2, 3, 4, 1880, 0)]    // SR: 3 -> 4
    [InlineData(3, 9, 10, 28000, 1)]  // UR: 9 -> 10, total 4 -> 5
    public void A_step_is_priced_by_the_next_row_and_spends_the_difference_in_copies(int rarity, int level,
        int target, int discForce, int copies)
    {
        var step = Served().Next(rarity, level);

        Assert.Equal(new EconomyDiscLevels.Step(target, discForce, copies), step);
    }

    [Fact]
    public void The_cap_is_the_highest_row_and_has_no_next_step()
    {
        var levels = Served();

        Assert.Equal(10, levels.MaxLevel(0));
        Assert.Null(levels.Next(0, 10));
        // A level with no row of its own is refused rather than priced from a guess.
        Assert.Null(levels.Next(3, 2));
    }

    [Fact]
    public void The_copies_a_disc_has_spent_are_its_cumulative_total_less_its_start_row()
    {
        var levels = Served();

        Assert.Equal(0, levels.CopiesSpent(1, 2, startLevel: 2));
        Assert.Equal(0, levels.CopiesSpent(1, 3, startLevel: 2));
        Assert.Equal(4, levels.CopiesSpent(1, 4, startLevel: 2));
        Assert.Equal(599, levels.CopiesSpent(0, 10, startLevel: 1));
    }

    [Fact]
    public void A_row_missing_a_column_is_not_a_free_level()
    {
        var levels = EconomyDiscLevels.Parse(
            """[{"rarityType":0,"level":1,"necessaryDiscForceAmount":50,"totalDiscAmount":1},{"rarityType":0,"level":2,"totalDiscAmount":3}]""",
            NullLogger.Instance);

        Assert.Null(levels.Next(0, 1));
    }

    [Fact]
    public void The_key_names_the_disc_and_the_level_it_reaches()
    {
        Assert.Equal("level-up:6f1d:3010003:4", EconomyDiscLevels.Key("6f1d", 3010003, 4));
    }

    [Theory]
    // CommonUtil.GetDefaultDiscLevel: rarity + 1 for 1..3, else 1 - which is where every rarity's rows start.
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    [InlineData(4, 1)]
    public void The_client_default_level_is_the_first_row_of_its_rarity(int rarity, int level)
    {
        Assert.Equal(level, EconomyDiscLevels.ClientDefaultLevel(rarity));
        if (rarity <= 3) Assert.NotNull(Served().TotalDiscs(rarity, level));
    }

    [Fact]
    public void The_tutorial_lesson_costs_the_rare_rows_second_level()
    {
        // TutorialUtil.DISC_ID_LIST[0] is Bombster (rarity 1): the lesson is 2 -> 3, Disc Force alone.
        var step = Served().Next(1, EconomyDiscLevels.ClientDefaultLevel(1));

        Assert.Equal(new EconomyDiscLevels.Step(3, 400, 0), step);
        Assert.Equal(1, Served().TotalDiscs(1, 3));
    }
}
