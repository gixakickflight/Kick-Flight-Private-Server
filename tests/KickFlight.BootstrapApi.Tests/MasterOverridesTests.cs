using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KickFlight.BootstrapApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// Unit tests for the master override layer: a file under <override dir>/masters_<table>.json replaces the
// base config/masters_<table>.json entirely; without an override file the base file (or the inline fallback)
// is used unchanged.
public sealed class MasterOverridesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kf-master-overrides-" + Guid.NewGuid().ToString("N"));

    public MasterOverridesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static string RepoRoot => RepositoryPaths.FindRoot(AppContext.BaseDirectory);

    [Fact]
    public void Override_wins_over_the_base_file()
    {
        var overrides = new MasterOverrides(_root, AppContext.BaseDirectory);
        var content = """[{"id":1,"name":"overridden"}]""";
        File.WriteAllText(Path.Combine(_root, "masters_kicker.json"), content);

        var read = overrides.Read(AppContext.BaseDirectory, "config/masters_kicker.json", "inline-fallback");

        Assert.Equal(content, read);
        Assert.Contains("masters_kicker.json", overrides.Applied);
    }

    [Fact]
    public void Absent_override_falls_back_to_the_base_file()
    {
        var overrides = new MasterOverrides(_root, AppContext.BaseDirectory);   // empty override dir
        var baseFile = Path.Combine(RepoRoot, "config", "masters_kicker.json");

        var read = overrides.Read(AppContext.BaseDirectory, "config/masters_kicker.json", "inline-fallback");

        Assert.Equal(File.ReadAllText(baseFile), read);
        Assert.Empty(overrides.Applied);
    }

    [Fact]
    public void Absent_override_and_missing_base_return_the_inline_fallback()
    {
        var overrides = new MasterOverrides(_root, AppContext.BaseDirectory);

        var read = overrides.Read(AppContext.BaseDirectory, "config/masters_does_not_exist.json", "inline-fallback");

        Assert.Equal("inline-fallback", read);
        Assert.Empty(overrides.Applied);
    }

    [Fact]
    public void Divergence_report_is_empty_when_the_override_matches_config_semantically()
    {
        var overrides = new MasterOverrides(_root, AppContext.BaseDirectory);
        var baseText = File.ReadAllText(Path.Combine(RepoRoot, "config", "masters_kicker.json"));
        // Same rows, different formatting: not a divergence.
        var compact = JsonSerializer.Serialize(JsonDocument.Parse(baseText).RootElement);
        File.WriteAllText(Path.Combine(_root, "masters_kicker.json"), compact);

        Assert.Empty(overrides.DescribeDivergences(AppContext.BaseDirectory));
    }

    [Fact]
    public void Divergence_report_counts_modified_added_and_removed_rows()
    {
        var overrides = new MasterOverrides(_root, AppContext.BaseDirectory);
        var rows = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot, "config", "masters_kicker.json")))!.AsArray();
        Assert.True(rows.Count >= 3);
        rows[0]!["name"] = "Retuned by the balance team";           // modified
        rows.RemoveAt(1);                                            // only in config
        rows.Add(JsonNode.Parse("""{"id":987654321,"name":"Override only"}"""));   // only in override
        File.WriteAllText(Path.Combine(_root, "masters_kicker.json"), rows.ToJsonString());

        var report = Assert.Single(overrides.DescribeDivergences(AppContext.BaseDirectory));

        Assert.Equal("masters_kicker.json", report.FileName);
        Assert.Equal(1, report.Modified);
        Assert.Equal(1, report.OnlyInConfig);
        Assert.Equal(1, report.OnlyInOverride);
        Assert.Equal(3, report.DifferingRows);
    }

    [Fact]
    public void Divergence_report_never_throws_on_a_malformed_override()
    {
        var overrides = new MasterOverrides(_root, AppContext.BaseDirectory);
        File.WriteAllText(Path.Combine(_root, "masters_kicker.json"), "{broken");

        var report = Assert.Single(overrides.DescribeDivergences(AppContext.BaseDirectory));

        Assert.NotNull(report.Note);
    }

    [Fact]
    public void Divergence_report_flags_an_override_without_a_config_file()
    {
        var overrides = new MasterOverrides(_root, AppContext.BaseDirectory);
        File.WriteAllText(Path.Combine(_root, "masters_not_in_config.json"), "[]");

        var report = Assert.Single(overrides.DescribeDivergences(AppContext.BaseDirectory));

        Assert.Contains("no config/ file", report.Note);
    }

    [Fact]
    public void Default_directory_is_resolved_under_the_repository_root()
    {
        var overrides = new MasterOverrides(null, AppContext.BaseDirectory);

        Assert.Equal(Path.GetFullPath(Path.Combine(RepoRoot, ".local", "masters-overrides")), overrides.Directory);
    }
}

// The same override layer through the running API: with Masters:OverrideDir pointing at a directory that
// holds a masters_kicker.json, /demo-master/Kicker has to serve that file, not the checked-in config one.
public sealed class MasterOverrideHostTests : IDisposable
{
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";
    private const string Host = "kickflight-api.grenge.jp";
    private const string OverriddenKickerJson =
        """[{"id":1,"name":"Overridden Tsubame","shortName":"Overridden","nameSpelling":"Overridden","voiceActorName":"CV: Test"}]""";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "kf-master-override-host-" + Guid.NewGuid().ToString("N"));

    public MasterOverrideHostTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "masters_kicker.json"), OverriddenKickerJson);
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseContentRoot(AppContext.BaseDirectory)
            .UseSetting(MasterOverrides.ConfigurationKey, _root));
    }

    public WebApplicationFactory<Program> Factory { get; }

    public void Dispose()
    {
        Factory.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Served_kicker_master_comes_from_the_override_file()
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/demo-master/Kicker");
        request.Headers.Host = Host;
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var plaintext = D2CCodec.Decode(
            await response.Content.ReadAsByteArrayAsync(), Encoding.ASCII.GetBytes(CommonCode));
        using var document = JsonDocument.Parse(plaintext);
        var first = document.RootElement.EnumerateArray().First();

        Assert.Equal("Overridden Tsubame", first.GetProperty("name").GetString());
    }
}
