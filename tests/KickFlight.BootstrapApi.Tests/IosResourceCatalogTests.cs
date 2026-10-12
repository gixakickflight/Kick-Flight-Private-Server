using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

public sealed class IosResourceCatalogTests : IClassFixture<ServerTestHostFixture>
{
    private readonly WebApplicationFactory<Program> _factory;

    public IosResourceCatalogTests(ServerTestHostFixture fixture) =>
        _factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Harness:DirectClientHosts:0"] = "kick-flight-fenix.us.ci",
                ["Harness:DirectClientHosts:1"] = "159.54.134.159"
            })));

    [Fact]
    public void Ios_boot_and_catalog_manifests_are_published_beside_the_api_binary()
    {
        var options = new HarnessOptions();
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "catalog-ios-12346.json");
        var bootPath = Path.Combine(AppContext.BaseDirectory, "boot-index-ios-platform-2.json");

        Assert.Equal(catalogPath, options.IosResourceManifestPath);
        Assert.Equal(bootPath, options.IosBootFixturePath);
        Assert.Equal(catalogPath, RepositoryPaths.Resolve(options.IosResourceManifestPath, "/srv/repo"));
        Assert.Equal(bootPath, RepositoryPaths.Resolve(options.IosBootFixturePath, "/srv/repo"));
        Assert.Equal("9852A6F6A4D52B7E1571A45896D3BE5E4A02BDC7F6DD5C61E7218C43428E2D1C",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))));
        Assert.Equal("765D997973BF3EAF8582292A3377FED0641DEBE8B15376AB96DCF10C1AEFC9C3",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bootPath))));
    }

    [Fact]
    public async Task Ios_profile_selects_native_boot_and_complete_revisioned_catalog_without_changing_android()
    {
        using var boot = new HttpRequestMessage(HttpMethod.Post, "/boot/index")
        {
            Content = new ByteArrayContent(new byte[32])
        };
        boot.Headers.Host = "kick-flight-fenix.us.ci";
        boot.Headers.TryAddWithoutValidation("x-app-asset-platform", "2");
        using var bootResponse = await _factory.CreateClient().SendAsync(boot);
        Assert.Equal(HttpStatusCode.OK, bootResponse.StatusCode);
        Assert.Equal("accepted-boot-index-ios-platform-2-d2c",
            bootResponse.Headers.GetValues("x-kickflight-fixture").Single());
        var bootBody = await bootResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(96, bootBody.Length);
        Assert.Equal("0E8E94854C099044249500A88A420ECC0176D33C4F0BC6F13BBAB679DAA2EE69",
            Convert.ToHexString(SHA256.HashData(bootBody)));

        using var androidBoot = new HttpRequestMessage(HttpMethod.Post, "/boot/index")
        {
            Content = new ByteArrayContent(new byte[32])
        };
        androidBoot.Headers.Host = "kick-flight-fenix.us.ci";
        using var androidBootResponse = await _factory.CreateClient().SendAsync(androidBoot);
        var key = Encoding.ASCII.GetBytes("1a837b9ee2ae11a07a0f529a4cd4b61c");
        var iosBootJson = JsonNode.Parse(D2CCodec.Decode(bootBody, key))!.AsObject();
        var androidBootJson = JsonNode.Parse(D2CCodec.Decode(await androidBootResponse.Content.ReadAsByteArrayAsync(), key))!.AsObject();
        Assert.Equal(12346, (int)iosBootJson["assetVersion"]!);
        Assert.Equal(12345, (int)androidBootJson["assetVersion"]!);
        iosBootJson["assetVersion"] = 12345;
        Assert.Equal(androidBootJson.ToJsonString(), iosBootJson.ToJsonString());

        var initial = await GetListAsync(0);
        Assert.Equal(45, ReadDatabaseRevision(initial));
        var initialRows = ReadDatabaseRows(initial);
        var wireNames = initialRows.Select(row => Encoding.UTF8.GetString(ReadBytesField(row.Payload, 3))).ToArray();
        Assert.Equal(2036, wireNames.Length);
        Assert.All(wireNames, name => Assert.False(string.IsNullOrWhiteSpace(name)));
        Assert.Equal(2036, wireNames.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(1831, initialRows.Count(row => row.Field == 2));
        Assert.Equal(205, initialRows.Count(row => row.Field == 4));
        Assert.All(initialRows, row => Assert.Equal(1UL, ReadVarintField(row.Payload, 9)));
        Assert.Equal("https://kick-flight-fenix.us.ci/ios/cdn/{o}", ReadDatabaseUrl(initial));

        var initialIds = initialRows.Select(row => ReadVarintField(row.Payload, 1)).ToArray();
        Assert.Equal(Enumerable.Range(1, 2036).Select(id => (ulong)id), initialIds.Order());
        var oldWireRows = initialRows.Where(row => ReadVarintField(row.Payload, 1) <= 494).ToArray();
        Assert.Equal("D19BC8852CEC1300C2A1D005C5638B6C7D720006E276030A403793C3C9522AD3",
            HashWireRows(oldWireRows));
        var addedRows = initialRows.Where(row => ReadVarintField(row.Payload, 1) >= 502).ToArray();
        Assert.Equal(1535, addedRows.Length);
        Assert.Equal(Enumerable.Range(502, 1535).Select(id => (ulong)id),
            addedRows.Select(row => ReadVarintField(row.Payload, 1)).Order());

        var update = await GetListAsync(40);
        var updateRows = ReadDatabaseRows(update);
        Assert.Equal(2036, updateRows.Count);
        Assert.All(updateRows, row => Assert.Equal(2UL, ReadVarintField(row.Payload, 9)));
        Assert.Equal("D19BC8852CEC1300C2A1D005C5638B6C7D720006E276030A403793C3C9522AD3",
            HashWireRows(updateRows.Where(row => ReadVarintField(row.Payload, 1) <= 494).ToArray()));
        var namelessNativeResource = updateRows.Single(row => row.Field == 4 &&
            Encoding.UTF8.GetString(ReadBytesField(row.Payload, 11)) == "12Cxc3");
        Assert.Equal("resource/12Cxc3", Encoding.UTF8.GetString(ReadBytesField(namelessNativeResource.Payload, 2)));
        Assert.Equal("resource/12Cxc3", Encoding.UTF8.GetString(ReadBytesField(namelessNativeResource.Payload, 3)));

        // Existing clients may have cached the old source/wire revision 43.
        var rev41 = await GetListAsync(41);
        Assert.Equal(45, ReadDatabaseRevision(rev41));
        var rev41Rows = ReadDatabaseRows(rev41);
        Assert.Equal(2036, rev41Rows.Count);
        Assert.All(rev41Rows, row => Assert.Equal(2UL, ReadVarintField(row.Payload, 9)));
        Assert.Equal("D19BC8852CEC1300C2A1D005C5638B6C7D720006E276030A403793C3C9522AD3",
            HashWireRows(rev41Rows.Where(row => ReadVarintField(row.Payload, 1) <= 494).ToArray()));
        var rev41NamelessNativeResource = rev41Rows.Single(row => row.Field == 4 &&
            Encoding.UTF8.GetString(ReadBytesField(row.Payload, 11)) == "12Cxc3");
        Assert.Equal("resource/12Cxc3", Encoding.UTF8.GetString(ReadBytesField(rev41NamelessNativeResource.Payload, 3)));

        var rev42 = await GetListAsync(42);
        Assert.Equal(2036, ReadDatabaseRows(rev42).Count);
        Assert.All(ReadDatabaseRows(rev42), row => Assert.Equal(2UL, ReadVarintField(row.Payload, 9)));

        var rev43 = await GetListAsync(43);
        Assert.Equal(2036, ReadDatabaseRows(rev43).Count);
        Assert.All(ReadDatabaseRows(rev43), row => Assert.Equal(2UL, ReadVarintField(row.Payload, 9)));

        var current = await GetListAsync(44);
        Assert.Equal(2036, ReadDatabaseRows(current).Count);
        Assert.All(ReadDatabaseRows(current), row => Assert.Equal(2UL, ReadVarintField(row.Payload, 9)));
        Assert.Equal("https://kick-flight-fenix.us.ci/ios/cdn/{o}", ReadDatabaseUrl(current));

        var latest = await GetListAsync(45);
        Assert.Empty(ReadDatabaseRows(latest));
        Assert.Equal("https://kick-flight-fenix.us.ci/ios/cdn/{o}", ReadDatabaseUrl(latest));

        using var futureRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/list/12346/46");
        futureRequest.Headers.Host = "kick-flight-fenix.us.ci";
        using var futureResponse = await _factory.CreateClient().SendAsync(futureRequest);
        Assert.Equal(HttpStatusCode.NotFound, futureResponse.StatusCode);

        // Android's existing 12345 response remains byte-for-byte the checked-in fixture body.
        using var androidRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/list/12345/0");
        androidRequest.Headers.Host = "kickflight-resource-api.grenge.jp";
        using var androidResponse = await _factory.CreateClient().SendAsync(androidRequest);
        Assert.Equal(HttpStatusCode.OK, androidResponse.StatusCode);
        var androidBody = await androidResponse.Content.ReadAsByteArrayAsync();
        var repo = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        using var androidFixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "config/fixtures/resource-list-12345.json")));
        var checkedInAndroidBody = Convert.FromBase64String(androidFixture.RootElement.GetProperty("bodyBase64").GetString()!);
        Assert.Equal(checkedInAndroidBody, androidBody);

        using var resourceRequest = new HttpRequestMessage(HttpMethod.Get, "/ios/cdn/12Cxc3");
        resourceRequest.Headers.Host = "kick-flight-fenix.us.ci";
        using var resourceResponse = await _factory.CreateClient().SendAsync(resourceRequest);
        Assert.Equal(HttpStatusCode.OK, resourceResponse.StatusCode);
        var resourceBody = await resourceResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(5920, resourceBody.Length);
        Assert.Equal("8883A4064573EC15FABD8DC07582F130F7A256369AA5935BEC9B7ECD681929B8",
            Convert.ToHexString(SHA256.HashData(resourceBody)));
    }

    [Fact]
    public async Task Missing_or_partial_ios_catalog_fails_closed_without_failing_android_routes()
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Harness:IosResourceCatalogPath"] = ".local/ios-resources/not-staged/catalog.json"
            })));

        using var iosRequest = new HttpRequestMessage(HttpMethod.Post, "/boot/index")
        {
            Content = new ByteArrayContent(new byte[32])
        };
        iosRequest.Headers.Host = "kick-flight-fenix.us.ci";
        iosRequest.Headers.TryAddWithoutValidation("x-app-asset-platform", "2");
        using var iosResponse = await factory.CreateClient().SendAsync(iosRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, iosResponse.StatusCode);
        using var error = JsonDocument.Parse(await iosResponse.Content.ReadAsStringAsync());
        Assert.Equal("ios-resource-catalog-unavailable", error.RootElement.GetProperty("error").GetString());

        using var androidRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/list/12345/40");
        androidRequest.Headers.Host = "kickflight-resource-api.grenge.jp";
        using var androidResponse = await factory.CreateClient().SendAsync(androidRequest);
        Assert.Equal(HttpStatusCode.OK, androidResponse.StatusCode);

        var partialCatalog = Path.Combine(Path.GetTempPath(), $"kf-ios-catalog-partial-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(partialCatalog, "{\"schemaVersion\":1,\"assetVersion\":12346,\"revision\":43,\"platform\":9,\"resources\":[]}");
        try
        {
            using var partialFactory = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Harness:IosResourceCatalogPath"] = partialCatalog
                })));
            using var partialRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/list/12346/0");
            partialRequest.Headers.Host = "kick-flight-fenix.us.ci";
            using var partialResponse = await partialFactory.CreateClient().SendAsync(partialRequest);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, partialResponse.StatusCode);
        }
        finally
        {
            File.Delete(partialCatalog);
        }
    }

    [Fact]
    public async Task Duplicate_ios_wire_id_fails_closed()
    {
        var sourceCatalog = Path.Combine(AppContext.BaseDirectory, "catalog-ios-12346.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(sourceCatalog))!.AsObject();
        var resources = document["resources"]!.AsArray();
        resources[1]!["octoId"] = resources[0]!["octoId"]!.GetValue<int>();
        var duplicateCatalog = Path.Combine(Path.GetTempPath(), $"kf-ios-catalog-wire-id-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(duplicateCatalog, document.ToJsonString());
        try
        {
            using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Harness:IosResourceCatalogPath"] = duplicateCatalog,
                    ["Harness:IosResourceManifestPath"] = duplicateCatalog
                })));
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/list/12346/0");
            request.Headers.Host = "kick-flight-fenix.us.ci";
            using var response = await factory.CreateClient().SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            File.Delete(duplicateCatalog);
        }
    }

    [Fact]
    public async Task Forced_update_still_precedes_the_platform_specific_boot_fixture()
    {
        var updateState = Path.Combine(Path.GetTempPath(), $"kf-ios-catalog-update-{Guid.NewGuid():N}.json");
        using var factory = _factory.WithWebHostBuilder(builder => builder
            .UseSetting("ClientUpdate:FilePath", updateState)
            .UseSetting("ClientUpdate:MinimumVersion", "2.11.3"));
        try
        {
            using var oldRequest = new HttpRequestMessage(HttpMethod.Post, "/boot/index")
            {
                Content = new ByteArrayContent(new byte[32])
            };
            oldRequest.Headers.Host = "kick-flight-fenix.us.ci";
            oldRequest.Headers.TryAddWithoutValidation("x-app-asset-platform", "2");
            oldRequest.Headers.TryAddWithoutValidation("x-app-application-version", "2.11.0");
            using var oldResponse = await factory.CreateClient().SendAsync(oldRequest);
            Assert.Equal(HttpStatusCode.OK, oldResponse.StatusCode);
            Assert.Equal("1", oldResponse.Headers.GetValues("x-app-status-code").Single());
            Assert.False(oldResponse.Headers.Contains("x-kickflight-fixture"));
            using var oldBody = JsonDocument.Parse(await oldResponse.Content.ReadAsStringAsync());
            Assert.Equal("1400", oldBody.RootElement.GetProperty("error").GetProperty("code").GetString());

            using var currentRequest = new HttpRequestMessage(HttpMethod.Post, "/boot/index")
            {
                Content = new ByteArrayContent(new byte[32])
            };
            currentRequest.Headers.Host = "kick-flight-fenix.us.ci";
            currentRequest.Headers.TryAddWithoutValidation("x-app-asset-platform", "2");
            currentRequest.Headers.TryAddWithoutValidation("x-app-application-version", "2.11.3");
            using var currentResponse = await factory.CreateClient().SendAsync(currentRequest);
            Assert.Equal(HttpStatusCode.OK, currentResponse.StatusCode);
            Assert.Equal("accepted-boot-index-ios-platform-2-d2c",
                currentResponse.Headers.GetValues("x-kickflight-fixture").Single());
        }
        finally
        {
            if (File.Exists(updateState)) File.Delete(updateState);
        }
    }

    private async Task<byte[]> GetListAsync(int fromRevision)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/list/12346/{fromRevision}");
        request.Headers.Host = "kick-flight-fenix.us.ci";
        using var response = await _factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private sealed record ProtoField(int Number, int Wire, byte[] Payload, ulong Varint);

    private static List<ProtoField> ReadFields(byte[] payload)
    {
        var fields = new List<ProtoField>();
        var offset = 0;
        while (offset < payload.Length)
        {
            var key = ReadVarint(payload, ref offset);
            var number = checked((int)(key >> 3));
            var wire = checked((int)(key & 7));
            switch (wire)
            {
                case 0:
                    fields.Add(new ProtoField(number, wire, [], ReadVarint(payload, ref offset)));
                    break;
                case 2:
                    var length = checked((int)ReadVarint(payload, ref offset));
                    if (offset + length > payload.Length) throw new InvalidDataException("Truncated protobuf field.");
                    fields.Add(new ProtoField(number, wire, payload[offset..(offset + length)], 0));
                    offset += length;
                    break;
                default:
                    throw new InvalidDataException($"Unexpected protobuf wire type {wire}.");
            }
        }
        return fields;
    }

    private static List<(int Field, byte[] Payload)> ReadDatabaseRows(byte[] payload) =>
        ReadFields(payload).Where(field => field.Number is 2 or 4)
            .Select(field => (field.Number, field.Payload)).ToList();

    private static int ReadDatabaseRevision(byte[] payload) => checked((int)ReadFields(payload).Single(field => field.Number == 1).Varint);

    private static string ReadDatabaseUrl(byte[] payload) =>
        Encoding.UTF8.GetString(ReadFields(payload).Single(field => field.Number == 5).Payload);

    private static string HashWireRows(IEnumerable<(int Field, byte[] Payload)> rows)
    {
        var canonical = new StringBuilder();
        foreach (var row in rows.OrderBy(row => ReadVarintField(row.Payload, 1)))
        {
            canonical.Append(ReadVarintField(row.Payload, 1)).Append('|')
                .Append(row.Field).Append('|')
                .Append(Encoding.UTF8.GetString(ReadBytesField(row.Payload, 2))).Append('|')
                .Append(Encoding.UTF8.GetString(ReadBytesField(row.Payload, 3))).Append('|')
                .Append(Encoding.UTF8.GetString(ReadBytesField(row.Payload, 11))).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static ulong ReadVarintField(byte[] payload, int number) =>
        ReadFields(payload).Single(field => field.Number == number).Varint;

    private static byte[] ReadBytesField(byte[] payload, int number) =>
        ReadFields(payload).Single(field => field.Number == number).Payload;

    private static ulong ReadVarint(byte[] payload, ref int offset)
    {
        ulong value = 0;
        var shift = 0;
        while (offset < payload.Length && shift < 64)
        {
            var current = payload[offset++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0) return value;
            shift += 7;
        }
        throw new InvalidDataException("Invalid protobuf varint.");
    }
}
