using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

public sealed class IosProfileRoutingTests : IClassFixture<ServerTestHostFixture>
{
    private const string PublicHost = IosTestProfileStore.CanonicalHost;
    private const string CdnUrl = IosTestProfileStore.CdnUrlFormat;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ServerTestHostFixture _server;

    public IosProfileRoutingTests(ServerTestHostFixture server) => _server = server;

    /// <summary>Synthetic profile identity and size; the current pin (12346/52) and a hypothetical next profile.</summary>
    public sealed record ProfileShape(int AssetVersion, int Revision, int Routes, int Rows);

    public static TheoryData<int, int, int, int> Shapes => new()
    {
        { 12346, 52, 3389, 3719 },
        { 12347, 53, 40, 77 },
    };

    [IosProfileFact]
    public async Task Review_package_is_accepted_when_profile_path_is_set()
    {
        var profileRoot = Environment.GetEnvironmentVariable("KF_IOS_REVIEW_PROFILE");
        Assert.False(string.IsNullOrWhiteSpace(profileRoot));
        Assert.True(Directory.Exists(profileRoot));
        // The staged package is self-describing: identity and row count come from its own report.
        using var reviewReport = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(profileRoot, "profile-report.json")));
        var reviewShape = new ProfileShape(
            reviewReport.RootElement.GetProperty("assetVersion").GetInt32(),
            reviewReport.RootElement.GetProperty("revision").GetInt32(),
            reviewReport.RootElement.GetProperty("resourceRoutes").GetInt32(),
            reviewReport.RootElement.GetProperty("servedRows").GetInt32());

        using var temp = new TemporaryDirectory();
        var android = await CreateAndroidSourcesAsync(temp.Path);
        using var factory = CreateFactory(temp.Path, profileRoot, enabled: true, android.CatalogPath, android.FixtureDirectory, reviewShape);
        using var client = factory.CreateClient();

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, $"/v1/list/{reviewShape.AssetVersion}/0");
        listRequest.Headers.Host = PublicHost;
        using var listResponse = await client.SendAsync(listRequest);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listBody = await listResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(reviewShape.Rows, CountOctoRows(listBody));
        Assert.Equal(CdnUrl, ReadTopLevelString(listBody, fieldNumber: 5));

        using var currentRequest = new HttpRequestMessage(HttpMethod.Get, $"/v1/list/{reviewShape.AssetVersion}/{reviewShape.Revision}");
        currentRequest.Headers.Host = PublicHost;
        using var currentResponse = await client.SendAsync(currentRequest);
        Assert.Equal(HttpStatusCode.OK, currentResponse.StatusCode);
        var currentBody = await currentResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(0, CountOctoRows(currentBody));
        Assert.Equal(CdnUrl, ReadTopLevelString(currentBody, fieldNumber: 5));

        var catalog = JsonSerializer.Deserialize<ResourceCatalogDocument>(
            await File.ReadAllTextAsync(Path.Combine(profileRoot, "catalog.json")), JsonOptions)!;
        var firstResource = catalog.Resources[0];
        using var payloadRequest = new HttpRequestMessage(HttpMethod.Get, firstResource.RequestPath);
        payloadRequest.Headers.Host = PublicHost;
        using var payloadResponse = await client.SendAsync(payloadRequest);
        Assert.Equal(HttpStatusCode.OK, payloadResponse.StatusCode);
        Assert.Equal(firstResource.Sha256, payloadResponse.Headers.ETag?.Tag.Trim('"'));
        Assert.Equal(new FileInfo(Path.Combine(profileRoot, firstResource.SourcePath)).Length,
            payloadResponse.Content.Headers.ContentLength);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Profile_routes_prebuilt_wire_and_staged_ios_payloads_without_changing_android(
        int assetVersion, int revision, int routes, int rows)
    {
        var shape = new ProfileShape(assetVersion, revision, routes, rows);
        using var temp = new TemporaryDirectory();
        var profile = await CreateProfileAsync(Path.Combine(temp.Path, "profile"), shape);
        var android = await CreateAndroidSourcesAsync(temp.Path);
        using var factory = CreateFactory(temp.Path, profile.Root, enabled: true, android.CatalogPath, android.FixtureDirectory, shape);
        using var client = factory.CreateClient();

        using var firstListRequest = new HttpRequestMessage(HttpMethod.Get, $"/v1/list/{assetVersion}/0");
        firstListRequest.Headers.Host = PublicHost;
        using var firstListResponse = await client.SendAsync(firstListRequest);
        Assert.Equal(HttpStatusCode.OK, firstListResponse.StatusCode);
        Assert.Equal("application/x-protobuf", firstListResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"r{revision}-0", firstListResponse.Headers.GetValues("x-kickflight-fixture").Single());
        Assert.Equal(profile.UpdateWireBody, await firstListResponse.Content.ReadAsByteArrayAsync());

        using var currentRequest = new HttpRequestMessage(HttpMethod.Get, $"/v1/list/{assetVersion}/{revision}");
        currentRequest.Headers.Host = PublicHost;
        using var currentResponse = await client.SendAsync(currentRequest);
        Assert.Equal(HttpStatusCode.OK, currentResponse.StatusCode);
        Assert.Equal(profile.CurrentWireBody, await currentResponse.Content.ReadAsByteArrayAsync());

        using var healthResponse = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
        using (var health = JsonDocument.Parse(await healthResponse.Content.ReadAsByteArrayAsync()))
        {
            Assert.True(health.RootElement.GetProperty("iosCatalogReady").GetBoolean());
            Assert.Equal($"r{revision}-test", health.RootElement.GetProperty("iosProfile").GetString());
            Assert.Equal(revision, health.RootElement.GetProperty("iosProfileRevision").GetInt32());
        }

        using var futureRequest = new HttpRequestMessage(HttpMethod.Get, $"/v1/list/{assetVersion}/{revision + 1}");
        futureRequest.Headers.Host = PublicHost;
        using var futureResponse = await client.SendAsync(futureRequest);
        Assert.Equal(HttpStatusCode.NotFound, futureResponse.StatusCode);

        using var iosPayloadRequest = new HttpRequestMessage(HttpMethod.Get, "/ios/cdn/object-0001.octo");
        iosPayloadRequest.Headers.Host = PublicHost;
        using var iosPayloadResponse = await client.SendAsync(iosPayloadRequest);
        Assert.Equal(HttpStatusCode.OK, iosPayloadResponse.StatusCode);
        Assert.Equal(profile.PayloadSha256, iosPayloadResponse.Headers.ETag?.Tag.Trim('"'));
        Assert.Equal(profile.PayloadBytes, await iosPayloadResponse.Content.ReadAsByteArrayAsync());

        using var androidListRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/list/12345/0");
        androidListRequest.Headers.Host = PublicHost;
        using var androidListResponse = await client.SendAsync(androidListRequest);
        Assert.Equal(HttpStatusCode.OK, androidListResponse.StatusCode);
        Assert.Equal("https://kick-flight-fenix.us.ci/cdn/{o}",
            ReadTopLevelString(await androidListResponse.Content.ReadAsByteArrayAsync(), fieldNumber: 5));

        using var androidPayloadRequest = new HttpRequestMessage(HttpMethod.Get, "/cdn/android-test.octo");
        androidPayloadRequest.Headers.Host = PublicHost;
        using var androidPayloadResponse = await client.SendAsync(androidPayloadRequest);
        Assert.Equal(HttpStatusCode.OK, androidPayloadResponse.StatusCode);
        Assert.Equal(android.PayloadBytes, await androidPayloadResponse.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Invalid_or_disabled_profile_fails_closed_and_keeps_android_available(
        int assetVersion, int revision, int routes, int rows)
    {
        var shape = new ProfileShape(assetVersion, revision, routes, rows);
        using var temp = new TemporaryDirectory();
        var profile = await CreateProfileAsync(Path.Combine(temp.Path, "profile"), shape);
        var reportPath = Path.Combine(profile.Root, "profile-report.json");
        var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
        var reportObject = JsonNode.Parse(report.RootElement.GetRawText())!.AsObject();
        report.Dispose();
        reportObject["testOnly"] = false;
        await File.WriteAllTextAsync(reportPath, reportObject.ToJsonString(JsonOptions));

        var android = await CreateAndroidSourcesAsync(temp.Path);
        using var enabledFactory = CreateFactory(temp.Path, profile.Root, enabled: true, android.CatalogPath, android.FixtureDirectory, shape);
        using var enabledClient = enabledFactory.CreateClient();
        using var rejectedListRequest = new HttpRequestMessage(HttpMethod.Get, $"/v1/list/{assetVersion}/0");
        rejectedListRequest.Headers.Host = PublicHost;
        using var rejectedList = await enabledClient.SendAsync(rejectedListRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejectedList.StatusCode);

        using var rejectedPayloadRequest = new HttpRequestMessage(HttpMethod.Get, "/ios/cdn/object-0001.octo");
        rejectedPayloadRequest.Headers.Host = PublicHost;
        using var rejectedPayload = await enabledClient.SendAsync(rejectedPayloadRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejectedPayload.StatusCode);

        using var androidPayloadRequest = new HttpRequestMessage(HttpMethod.Get, "/cdn/android-test.octo");
        androidPayloadRequest.Headers.Host = PublicHost;
        using var androidPayload = await enabledClient.SendAsync(androidPayloadRequest);
        Assert.Equal(HttpStatusCode.OK, androidPayload.StatusCode);

        using var disabledFactory = CreateFactory(temp.Path, profile.Root, enabled: false, android.CatalogPath, android.FixtureDirectory, shape);
        using var disabledClient = disabledFactory.CreateClient();
        using var disabledRequest = new HttpRequestMessage(HttpMethod.Get, "/ios/cdn/object-0001.octo");
        disabledRequest.Headers.Host = PublicHost;
        using var disabledResponse = await disabledClient.SendAsync(disabledRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledResponse.StatusCode);
    }

    private WebApplicationFactory<Program> CreateFactory(
        string tempRoot,
        string profileRoot,
        bool enabled,
        string androidCatalogPath,
        string fixtureDirectory,
        ProfileShape shape)
    {
        var contentRoot = Path.Combine(tempRoot, "content-root");
        Directory.CreateDirectory(contentRoot);
        return _server.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Harness:DirectClientHosts:0"] = PublicHost,
                ["Harness:EnableIosTestProfile"] = enabled.ToString(),
                ["Harness:IosTestProfileAssetVersion"] = shape.AssetVersion.ToString(),
                ["Harness:IosTestProfileRevision"] = shape.Revision.ToString(),
                ["Harness:IosTestProfilePath"] = profileRoot,
                ["Harness:ResourceCatalogPath"] = androidCatalogPath,
                ["Harness:FixtureDirectory"] = fixtureDirectory,
                ["Harness:IosResourceCatalogPath"] = Path.Combine(tempRoot, "missing-ios", "catalog.json"),
                ["Harness:IosResourceManifestPath"] = Path.Combine(tempRoot, "missing-ios", "manifest.json"),
                ["Harness:IosBootFixturePath"] = Path.Combine(AppContext.BaseDirectory, "boot-index-ios-platform-2.json"),
                ["Logging:LogLevel:Default"] = "Error",
                ["PhotonServer:Enabled"] = "false"
            }));
        });
    }

    private static async Task<ProfileFixture> CreateProfileAsync(string root, ProfileShape shape)
    {
        var fixtureDirectory = Path.Combine(root, "fixtures");
        var payloadDirectory = Path.Combine(root, "payload-r7");
        Directory.CreateDirectory(fixtureDirectory);
        Directory.CreateDirectory(payloadDirectory);

        var payloadBytes = new byte[] { 0x52, 0x37, 0x21, 0x00 };
        var payloadSha = Sha256(payloadBytes);
        var resources = new List<ResourceDefinition>(shape.Routes);
        for (var id = 1; id <= shape.Routes; id++)
        {
            var objectName = $"object-{id:D4}.octo";
            var sourcePath = $"payload-r7/{objectName}";
            await File.WriteAllBytesAsync(Path.Combine(payloadDirectory, objectName), payloadBytes);
            resources.Add(new ResourceDefinition
            {
                Id = $"r{shape.Revision}-{id:D4}",
                Enabled = true,
                Host = PublicHost,
                RequestPath = $"{IosTestProfileStore.CdnPrefix}object-{id:D4}.octo",
                LogicalName = $"resource/synthetic-{id:D4}",
                SourcePath = sourcePath,
                ContentType = "application/octet-stream",
                Sha256 = payloadSha
            });
        }

        var catalogPath = Path.Combine(root, "catalog.json");
        await File.WriteAllTextAsync(catalogPath, JsonSerializer.Serialize(new ResourceCatalogDocument { Resources = resources }, JsonOptions));

        var updateWireBody = CreateWireDatabase(shape.Rows, shape.Revision);
        var currentWireBody = CreateWireDatabase(0, shape.Revision);
        var fixtureHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var from = 0; from < shape.Revision; from++)
        {
            var fixtureName = $"resource-list-{shape.AssetVersion}-from-{from}.json";
            fixtureHashes[fixtureName] = await WriteFixtureAsync(
                fixtureDirectory, fixtureName, $"r{shape.Revision}-{from}", $"/v1/list/{shape.AssetVersion}/{from}", updateWireBody);
        }
        var currentFixtureName = $"resource-list-{shape.AssetVersion}.json";
        fixtureHashes[currentFixtureName] = await WriteFixtureAsync(
            fixtureDirectory, currentFixtureName, $"r{shape.Revision}-current", $"/v1/list/{shape.AssetVersion}/{shape.Revision}", currentWireBody);

        var titleMinimumPath = Path.Combine(root, "title-minimum.json");
        await File.WriteAllTextAsync(titleMinimumPath, JsonSerializer.Serialize(new
        {
            assetVersion = shape.AssetVersion,
            revision = shape.Revision,
            previousRevision = shape.Revision - 1,
            fromRevisions = Enumerable.Range(0, shape.Revision),
            urlPathFormat = "/ios/cdn/{o}",
            entries = Array.Empty<object>()
        }, JsonOptions));

        var report = new
        {
            schema = $"r56.bootstrapapi.offline-profile.r{shape.Revision}-direct-review.v1",
            assetVersion = shape.AssetVersion,
            revision = shape.Revision,
            previousRevision = shape.Revision - 1,
            fromRevisions = Enumerable.Range(0, shape.Revision),
            fixtureCount = shape.Revision + 1,
            fullUpdateFixtureRevisions = Enumerable.Range(0, shape.Revision),
            currentFixtureRevision = shape.Revision,
            servedRows = shape.Rows,
            resourceRoutes = shape.Routes,
            payloadCount = shape.Routes,
            payloadBytes = (long)payloadBytes.Length * shape.Routes,
            canonicalHost = PublicHost,
            cdnRequestPrefix = IosTestProfileStore.CdnPrefix,
            urlFormatInFixtures = CdnUrl,
            testOnly = true,
            complete = false,
            originalWireMapVerified = false,
            experimentalField901 = true,
            unverifiedReconstructedRowIds = 1156,
            upstreamMustRemainDisabled = true,
            noVpsWrite = true,
            noNetworkRequests = true,
            noServerStarted = true,
            originalshaderPresent = true,
            mobileDiffuseShaderPresent = false,
            particleStandardUnlitShaderPresent = false,
            downloadsShaderCandidatesAdded = false,
            packageCatalogSha256 = Sha256(await File.ReadAllBytesAsync(catalogPath)),
            titleMinimumSha256 = Sha256(await File.ReadAllBytesAsync(titleMinimumPath)),
            fixtureSetSha256 = Sha256(Encoding.UTF8.GetBytes("synthetic-fixtures")),
            payloadSetSha256 = Sha256(Encoding.UTF8.GetBytes("synthetic-payloads")),
            fixtureSha256 = fixtureHashes
        };
        await File.WriteAllTextAsync(Path.Combine(root, "profile-report.json"), JsonSerializer.Serialize(report, JsonOptions));
        return new ProfileFixture(root, payloadBytes, payloadSha, updateWireBody, currentWireBody);
    }

    private static async Task<string> WriteFixtureAsync(
        string fixtureDirectory,
        string fileName,
        string id,
        string path,
        byte[] body)
    {
        var fixture = new FixtureDefinition
        {
            Id = id,
            Enabled = true,
            Host = PublicHost,
            Method = "GET",
            Path = path,
            StatusCode = 200,
            ContentType = "application/x-protobuf",
            Headers = new Dictionary<string, string> { ["x-kickflight-fixture"] = id },
            BodyBase64 = Convert.ToBase64String(body)
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(fixture, JsonOptions);
        await File.WriteAllBytesAsync(Path.Combine(fixtureDirectory, fileName), bytes);
        return Sha256(bytes);
    }

    private static async Task<AndroidSources> CreateAndroidSourcesAsync(string root)
    {
        var androidDirectory = Path.Combine(root, "android");
        var fixtureDirectory = Path.Combine(root, "android-fixtures");
        Directory.CreateDirectory(androidDirectory);
        Directory.CreateDirectory(fixtureDirectory);
        var payload = new byte[] { 0x41, 0x4e, 0x44, 0x52 };
        var payloadPath = Path.Combine(androidDirectory, "android-test.octo");
        await File.WriteAllBytesAsync(payloadPath, payload);
        var catalog = new ResourceCatalogDocument
        {
            Resources =
            [
                new ResourceDefinition
                {
                    Id = "android-test",
                    Host = PublicHost,
                    RequestPath = "/cdn/android-test.octo",
                    LogicalName = "android/test",
                    SourcePath = payloadPath,
                    ContentType = "application/octet-stream",
                    Sha256 = Sha256(payload)
                }
            ]
        };
        var catalogPath = Path.Combine(androidDirectory, "catalog.json");
        await File.WriteAllTextAsync(catalogPath, JsonSerializer.Serialize(catalog, JsonOptions));

        var androidList = CreateAndroidWireDatabase();
        await WriteFixtureAsync(fixtureDirectory, "android-list.json", "android-list", "/v1/list/12345/0", androidList);
        return new AndroidSources(catalogPath, fixtureDirectory, payload);
    }

    private static byte[] CreateWireDatabase(int rows, int revision)
    {
        using var output = new MemoryStream();
        WriteVarintField(output, 1, (ulong)revision);
        for (var row = 0; row < Math.Min(rows, 3131); row++) WriteBytesField(output, 2, []);
        if (rows != 0) WriteStringField(output, 3, "common");
        for (var row = 3131; row < rows; row++) WriteBytesField(output, 4, []);
        WriteStringField(output, 5, CdnUrl);
        return output.ToArray();
    }

    private static byte[] CreateAndroidWireDatabase()
    {
        using var output = new MemoryStream();
        WriteVarintField(output, 1, 47);
        WriteStringField(output, 5, "http://127.0.0.1:18080/cdn/{o}");
        return output.ToArray();
    }

    private static string ReadTopLevelString(byte[] data, int fieldNumber)
    {
        var index = 0;
        while (index < data.Length)
        {
            var key = ReadVarint(data, ref index);
            var number = (int)(key >> 3);
            var wire = (int)(key & 7);
            if (wire == 0) _ = ReadVarint(data, ref index);
            else if (wire == 2)
            {
                var length = checked((int)ReadVarint(data, ref index));
                if (number == fieldNumber) return Encoding.UTF8.GetString(data, index, length);
                index += length;
            }
            else throw new InvalidDataException("Unsupported test wire field.");
        }
        throw new InvalidDataException("Expected test wire field was missing.");
    }

    private static int CountOctoRows(byte[] data)
    {
        var index = 0;
        var count = 0;
        while (index < data.Length)
        {
            var key = ReadVarint(data, ref index);
            var number = (int)(key >> 3);
            var wire = (int)(key & 7);
            if (wire == 0) _ = ReadVarint(data, ref index);
            else if (wire == 2)
            {
                var length = checked((int)ReadVarint(data, ref index));
                if (index > data.Length - length) throw new InvalidDataException("Truncated Octo test message.");
                if (number is 2 or 4) count++;
                index += length;
            }
            else throw new InvalidDataException("Unsupported Octo test wire field.");
        }
        return count;
    }

    private static ulong ReadVarint(byte[] data, ref int index)
    {
        ulong value = 0;
        var shift = 0;
        while (index < data.Length && shift < 64)
        {
            var next = data[index++];
            value |= (ulong)(next & 0x7f) << shift;
            if ((next & 0x80) == 0) return value;
            shift += 7;
        }
        throw new InvalidDataException("Invalid test protobuf varint.");
    }

    private static void WriteVarintField(Stream output, int field, ulong value)
    {
        WriteVarint(output, ((ulong)field << 3) | 0);
        WriteVarint(output, value);
    }

    private static void WriteBytesField(Stream output, int field, byte[] bytes)
    {
        WriteVarint(output, ((ulong)field << 3) | 2);
        WriteVarint(output, (ulong)bytes.Length);
        output.Write(bytes);
    }

    private static void WriteStringField(Stream output, int field, string value) =>
        WriteBytesField(output, field, Encoding.UTF8.GetBytes(value));

    private static void WriteVarint(Stream output, ulong value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        output.WriteByte((byte)value);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record ProfileFixture(string Root, byte[] PayloadBytes, string PayloadSha256, byte[] UpdateWireBody, byte[] CurrentWireBody);
    private sealed record AndroidSources(string CatalogPath, string FixtureDirectory, byte[] PayloadBytes);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"kf-ios-profile-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

public sealed class IosProfileFactAttribute : FactAttribute
{
    public IosProfileFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KF_IOS_REVIEW_PROFILE")))
            Skip = "Set KF_IOS_REVIEW_PROFILE to a verified staged iOS profile package to run full-payload routing validation.";
    }
}
