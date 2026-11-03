using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The master tables come from otomo's release, all of them: masters/otomo/namespaces.json lists the namespaces this
// repository owns, and appsettings.json (Otomo:Masters:Tables) binds each to the name DemoSessionApi serves the table under.
public sealed class OtomoMasterBindingTests
{
    private const string Host = "kickflight-api.grenge.jp";
    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";
    private const string SessionKey = "0123456789abcdef0123456789abcdef";

    // config/masters_disc_rarity.json is in the namespace list (the release carries it) but this adapter never serves it:
    // there is no DiscRarity master in DemoSessionApi, so a binding for it would name a master that does not exist.
    private static readonly string[] CarriedButNeverServed = ["disc_rarity"];

    private static string Pascal(string table) =>
        string.Concat(table.Split('_').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private static (string Table, string Namespace)[] Namespaces()
    {
        var root = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        using var catalogue = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "masters", "otomo", "namespaces.json")));
        return catalogue.RootElement.GetProperty("namespaces").EnumerateArray()
            .Select(entry => (entry.GetProperty("table").GetString()!, entry.GetProperty("namespace").GetString()!))
            .ToArray();
    }

    private static (string Master, string Namespace)[] Bindings()
    {
        var root = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        using var settings = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "src", "KickFlight.BootstrapApi", "appsettings.json")));
        return settings.RootElement.GetProperty("Otomo").GetProperty("Masters").GetProperty("Tables").EnumerateArray()
            .Select(entry => entry.GetString()!.Split('=', 2))
            .Select(parts => (parts[0], parts[1]))
            .ToArray();
    }

    [Fact]
    public void Every_table_in_namespaces_json_is_bound_to_its_namespace()
    {
        var bindings = Bindings();
        Assert.Equal(bindings.Length, bindings.Select(binding => binding.Master).Distinct().Count());
        Assert.Equal(bindings.Length, bindings.Select(binding => binding.Namespace).Distinct().Count());

        foreach (var (table, ns) in Namespaces().Where(entry => !CarriedButNeverServed.Contains(entry.Table)))
        {
            Assert.Contains((Pascal(table), ns), bindings);
        }
        // ... and nothing is bound that the catalogue does not list.
        var listed = Namespaces().Select(entry => entry.Namespace).ToHashSet();
        Assert.All(bindings, binding => Assert.Contains(binding.Namespace, listed));
        Assert.Equal(Namespaces().Length - CarriedButNeverServed.Length, bindings.Length);
    }

    [Fact]
    public async Task Every_binding_names_a_master_the_adapter_serves()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseContentRoot(AppContext.BaseDirectory));
        var served = await ServedMastersAsync(factory);

        Assert.All(Bindings(), binding => Assert.Contains(binding.Master, served));
        // The one table the catalogue carries and the adapter does not serve is named, not forgotten.
        Assert.DoesNotContain("DiscRarity", served);
    }

    [Fact]
    public async Task A_table_other_than_translation_is_served_from_the_release_at_boot()
    {
        await using var release = FakeOtomoSocial.Start();
        var root = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(
            File.ReadAllText(Path.Combine(root, "config", "masters_stamp.json")))!;
        var asPublished = rows.Select(row => new Dictionary<string, object?>(row.Select(pair =>
            new KeyValuePair<string, object?>(pair.Key, pair.Key == "name" && ReferenceEquals(row, rows[0]) ? "PUBLISHED-IN-OTOMO" : pair.Value)))).ToList();
        release.ReplaceMasterRows("kf.masters.stamp", JsonSerializer.Serialize(asPublished));

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Otomo:Masters:BaseUrl"] = release.BaseUrl,
                ["Otomo:Masters:CacheDir"] = Path.Combine(Path.GetTempPath(), "kf-test-masters-" + Guid.NewGuid().ToString("N"))
            }));
        });

        var stamp = await ServedTableAsync(factory, "Stamp");
        Assert.Equal("PUBLISHED-IN-OTOMO", stamp[0].GetProperty("name").GetString());
        Assert.Equal(rows.Count, stamp.Count);
        // A table the release did not change is the file's rows, served from the release's copy of them.
        var kicker = await ServedTableAsync(factory, "Kicker");
        Assert.Equal(14, kicker.Count);
    }

    [Fact]
    public async Task With_otomo_masters_off_the_bound_names_change_nothing_and_the_files_are_served()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Otomo:Masters:Enabled"] = "false"
            }));
        });

        var stamp = await ServedTableAsync(factory, "Stamp");
        Assert.Equal("STP6051001", stamp[0].GetProperty("name").GetString());
    }

    // --- plumbing ---------------------------------------------------------------------------------------------

    private static async Task<string> AuthAsync(HttpClient client)
    {
        var payload = JsonSerializer.Serialize(new { hash = SessionKey, uuid = Guid.NewGuid().ToString("N") });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/index")
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(payload), Encoding.ASCII.GetBytes(CommonCode), new byte[16]))
        };
        request.Headers.Host = Host;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.GetValues("x-app-access-token").Single();
    }

    private static async Task<HashSet<string>> ServedMastersAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var token = await AuthAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/download/master") { Content = new ByteArrayContent([]) };
        request.Headers.Host = Host;
        request.Headers.Add("x-app-access-token", token);
        using var response = await client.SendAsync(request);
        using var list = JsonDocument.Parse(D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), Encoding.ASCII.GetBytes(SessionKey)));
        return list.RootElement.GetProperty("masterDownloadList").EnumerateArray()
            .Select(entry => entry.GetProperty("name").GetString()!).ToHashSet();
    }

    private static async Task<List<JsonElement>> ServedTableAsync(WebApplicationFactory<Program> factory, string master)
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/demo-master/" + master);
        request.Headers.Host = Host;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var table = JsonDocument.Parse(D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), Encoding.ASCII.GetBytes(CommonCode)));
        return table.RootElement.EnumerateArray().Select(row => row.Clone()).ToList();
    }
}
