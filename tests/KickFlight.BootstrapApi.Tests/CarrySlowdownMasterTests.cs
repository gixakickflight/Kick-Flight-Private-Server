using System.Text;
using System.Text.Json;
using KickFlight.BootstrapApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// Carry slowdown masters: BattleRuleParameter / BattleRuleRoleParameter come from config files (so the /balance
// panel can override them), use the client RoleType enum 0..3 and ship with no slowdown.
public sealed class CarrySlowdownMasterTests : IClassFixture<ServerTestHostFixture>
{
    internal const string Host = "kickflight-api.grenge.jp";
    internal const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";
    private readonly WebApplicationFactory<Program> _factory;

    public CarrySlowdownMasterTests(ServerTestHostFixture fixture) => _factory = fixture.Factory;

    internal static async Task<List<JsonElement>> ReadMasterAsync(WebApplicationFactory<Program> factory, string name)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/demo-master/{name}");
        request.Headers.Host = Host;
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var plaintext = D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), Encoding.ASCII.GetBytes(CommonCode));
        using var document = JsonDocument.Parse(plaintext);
        return document.RootElement.EnumerateArray().Select(row => row.Clone()).ToList();
    }

    [Fact]
    public async Task Role_parameter_has_every_client_role_for_every_rule_with_no_slowdown()
    {
        var rows = await ReadMasterAsync(_factory, "BattleRuleRoleParameter");

        Assert.Equal(32, rows.Count);
        foreach (var ruleId in Enumerable.Range(1, 8))
        {
            var roles = rows.Where(r => r.GetProperty("battleRuleId").GetInt32() == ruleId)
                .Select(r => r.GetProperty("roleType").GetInt32()).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { 0, 1, 2, 3 }, roles);
        }
        Assert.All(rows, r => Assert.Equal(0d, r.GetProperty("crystalCountSpeedCorrection").GetDouble()));
        Assert.Equal(rows.Count, rows.Select(r => r.GetProperty("id").GetInt32()).Distinct().Count());
    }

    [Fact]
    public async Task Rule_parameter_does_not_clamp_the_item_count()
    {
        var rows = await ReadMasterAsync(_factory, "BattleRuleParameter");

        Assert.Equal(8, rows.Count);
        Assert.All(rows, r => Assert.Equal(-1, r.GetProperty("itemDecelerationMaxCount").GetInt32()));
        Assert.Contains(rows, r => r.GetProperty("battleRuleId").GetInt32() == RankedModeRotation.CrystalRuleId);
        Assert.Contains(rows, r => r.GetProperty("battleRuleId").GetInt32() == RankedModeRotation.FlagRuleId);
    }
}

public sealed class CarrySlowdownOverrideTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "kf-carry-override-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public CarrySlowdownOverrideTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "masters_battle_rule_role_parameter.json"),
            """[{"id":1,"battleRuleId":1,"roleType":3,"crystalCountSpeedCorrection":12.5}]""");
        File.WriteAllText(Path.Combine(_root, "masters_battle_rule_parameter.json"),
            """[{"id":1,"battleRuleId":1,"description":"d","subRuleName":"s","itemDecelerationMaxCount":2}]""");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseContentRoot(AppContext.BaseDirectory)
            .UseSetting(MasterOverrides.ConfigurationKey, _root));
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Override_files_replace_both_carry_masters()
    {
        var roles = await CarrySlowdownMasterTests.ReadMasterAsync(_factory, "BattleRuleRoleParameter");
        var rules = await CarrySlowdownMasterTests.ReadMasterAsync(_factory, "BattleRuleParameter");

        Assert.Single(roles);
        Assert.Equal(12.5d, roles[0].GetProperty("crystalCountSpeedCorrection").GetDouble());
        Assert.Equal(3, roles[0].GetProperty("roleType").GetInt32());
        Assert.Single(rules);
        Assert.Equal(2, rules[0].GetProperty("itemDecelerationMaxCount").GetInt32());
    }
}
