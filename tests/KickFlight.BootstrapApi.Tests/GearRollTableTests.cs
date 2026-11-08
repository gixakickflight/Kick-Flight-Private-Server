using Xunit;

namespace KickFlight.BootstrapApi.Tests;

public sealed class GearRollTableTests
{
    private const string Gears = """
        [{"id":1,"rarityType":0},{"id":2,"rarityType":0},{"id":3,"rarityType":1},{"id":4,"rarityType":3}]
        """;

    [Fact]
    public void Rarities_are_rolled_by_drop_ratio_and_a_zero_ratio_is_never_rolled()
    {
        var table = GearRollTable.Parse(Gears,
            """[{"rarityType":0,"dropRatio":3},{"rarityType":1,"dropRatio":1},{"rarityType":3,"dropRatio":0}]""",
            out var problem);
        Assert.Null(problem);

        var random = new Random(7);
        var counts = new Dictionary<int, int>();
        for (var i = 0; i < 8000; i++)
        {
            var id = table.Roll(random);
            counts[id] = counts.GetValueOrDefault(id) + 1;
        }

        Assert.False(counts.ContainsKey(4));
        var common = counts.GetValueOrDefault(1) + counts.GetValueOrDefault(2);
        Assert.InRange(common / 8000.0, 0.72, 0.78);
        Assert.InRange(counts.GetValueOrDefault(3) / 8000.0, 0.22, 0.28);
        // Within a rarity every gear is equally likely.
        Assert.InRange(counts[1] / (double)common, 0.45, 0.55);
    }

    [Fact]
    public void A_ratio_table_with_no_usable_row_falls_back_to_every_gear_and_says_so()
    {
        var table = GearRollTable.Parse(Gears, "[]", out var problem);
        Assert.NotNull(problem);
        var random = new Random(1);
        var seen = Enumerable.Range(0, 400).Select(_ => table.Roll(random)).ToHashSet();
        Assert.Equal(new HashSet<int> { 1, 2, 3, 4 }, seen);
    }
}
