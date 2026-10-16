using System.Text.Json;
using KickFlight.Logic;

var checks = new List<(string Name, Action Run)>
{
    ("native interpolation boundary vectors floor toward negative infinity", BoundaryVectors),
    ("every DiscGrowMasterData rate matches independent numeric floor", AllMasterRates),
};

var failed = 0;
foreach (var (name, run) in checks)
{
    try
    {
        run();
        Console.WriteLine("PASS " + name);
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine("FAIL " + name + ": " + exception.Message);
    }
}

Console.WriteLine($"{checks.Count - failed}/{checks.Count} checks passed");
return failed == 0 ? 0 : 1;

static void BoundaryVectors()
{
    Equal(1, DiscParameterUtil.CalcCoefficient(1.25f, 4.75f, 0f), "rate=0");
    Equal(2, DiscParameterUtil.CalcCoefficient(1.25f, 4.75f, 25f), "rate=25");
    Equal(3, DiscParameterUtil.CalcCoefficient(1.25f, 4.75f, 50f), "rate=50");
    Equal(3, DiscParameterUtil.CalcCoefficient(1.25f, 4.75f, 75f), "rate=75");
    Equal(4, DiscParameterUtil.CalcCoefficient(1.25f, 4.75f, 100f), "rate=100");
    Equal(-1, DiscParameterUtil.CalcCoefficient(0f, 1f, -25f), "negative intermediate uses floor, not truncation");
}

static void AllMasterRates()
{
    var repoRoot = FindRepositoryRoot(AppContext.BaseDirectory);
    var path = Path.Combine(repoRoot, "config", "masters_disc_grow.json");
    using var document = JsonDocument.Parse(File.ReadAllBytes(path));
    var rows = document.RootElement.EnumerateArray().ToArray();
    if (rows.Length == 0) throw new Exception("No DiscGrowMasterData rows found.");

    const float min = 1.25f;
    const float max = 101.25f;
    foreach (var row in rows)
    {
        var rate = row.GetProperty("rate").GetSingle();
        var expected = ReferenceNativeFloat32(min, max, rate);
        var actual = DiscParameterUtil.CalcCoefficient(min, max, rate);
        if (actual != expected)
            throw new Exception($"groupId={row.GetProperty("groupId").GetInt32()} level={row.GetProperty("level").GetInt32()} rate={rate}: expected {expected}, got {actual}");
    }

    Console.WriteLine($"  audited {rows.Length} masters_disc_grow.json rate rows independently");
}

static int ReferenceNativeFloat32(float minCoefficient, float maxCoefficient, float rate)
{
    // Materialize each single-precision operation as in the AArch64 scalar instructions,
    // then independently apply mathematical floor to that exact float value.
    var delta = (float)(maxCoefficient - minCoefficient);
    var scaled = (float)(delta * rate);
    var percentage = (float)(scaled / 100f);
    var interpolated = (float)(minCoefficient + percentage);
    return (int)Math.Floor((double)interpolated);
}

static string FindRepositoryRoot(string start)
{
    var directory = new DirectoryInfo(start);
    while (directory != null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "README.md"))
            && Directory.Exists(Path.Combine(directory.FullName, "config")))
            return directory.FullName;
        directory = directory.Parent;
    }
    throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
}

static void Equal(int expected, int actual, string label)
{
    if (expected != actual) throw new Exception($"{label}: expected {expected}, got {actual}");
}
