using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KickFlight.BootstrapApi;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

public sealed class HarnessTests : IClassFixture<ServerTestHostFixture>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HarnessTests(ServerTestHostFixture fixture) => _factory = fixture.Factory;

    [Fact]
    public async Task Health_live_reports_that_the_server_is_running()
    {
        using var response = await _factory.CreateClient().GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("live", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Health_ready_reports_missing_resource_prerequisites()
    {
        using var response = await _factory.CreateClient().GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not-ready", body.GetProperty("status").GetString());
        Assert.Contains(
            body.GetProperty("errors").EnumerateArray().Select(error => error.GetString()),
            error => error is not null && error.Contains("apk-remote-kickflightsg: Source file was not found.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("kickflight-api.grenge.jp", "POST", "application/json")]
    [InlineData("colorful-api-octo-sb.grenge.jp", "GET", "application/x-protobuf")]
    [InlineData("kickflight-resource-api.grenge.jp", "GET", "application/octet-stream")]
    public async Task Fixtures_route_by_host_method_and_path(string host, string method, string contentType)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "/__fixture-probe");
        request.Headers.Host = host;
        if (method == "POST") request.Content = JsonContent.Create(new { probe = true });
        using var response = await _factory.CreateClient().SendAsync(request);
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("harness-", response.Headers.GetValues("x-kickflight-fixture").Single());
    }

    [Fact]
    public async Task Unknown_route_is_strict_and_never_forwards()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/definitely-unknown");
        request.Headers.Host = "kickflight-api.grenge.jp";
        using var response = await _factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("disabled", json.GetProperty("upstream").GetString());
    }

    [Fact]
    public async Task Boot_route_returns_the_recovered_binary_contract()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/boot/index")
        {
            Content = new ByteArrayContent(new byte[32])
        };
        request.Content.Headers.ContentType = new("application/octet-stream");
        request.Headers.Host = "kickflight-api.grenge.jp";
        using var response = await _factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0", response.Headers.GetValues("x-app-status-code").Single());
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(96, body.Length);
        Assert.Equal("63A85ADB36726F4612A52A07B9BBE93387D683B48B424DA50FE2718B9BE31686",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)));
    }

    [Fact]
    public async Task Observed_octo_list_route_returns_the_reconstructed_title_database()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/list/12345/0");
        request.Headers.Host = "kickflight-resource-api.grenge.jp";
        using var response = await _factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.True(body.Length > 300);
        var title = ReadTitleResourceDefinition();
        AssertOctoRevision(body, title.Revision);
        var protobufText = Encoding.UTF8.GetString(body);
        Assert.Contains("ui/localize/en/title/title_logo.unity3d", protobufText);
        // title_logo is served by its own generated en clone (the zh capture row is removed like every other
        // ui/localize/zh route), so its Octo objectName is the generator's, not the capture's 7pXtSo.
        Assert.Contains("l1X500", protobufText);
        Assert.Contains("Og1RF0", protobufText);
        Assert.Contains("ujPPwv", protobufText);
        Assert.Contains("Ei4139", protobufText);
        Assert.Contains("originalshader.unity3d", protobufText);
        Assert.Contains("shader/preloadgameshadervariants.unity3d", protobufText);
        Assert.Contains("/cdn/{o}", protobufText);

        var offset = 2; // Database.revision = 19 (08 13)
        Assert.Equal(0x12UL, ReadVarint(body, ref offset)); // first Database.asset
        var assetLength = checked((int)ReadVarint(body, ref offset));
        var firstAsset = body[offset..(offset + assetLength)];
        Assert.Equal(
            new[] { (1, 0), (2, 2), (3, 2), (4, 0), (5, 0), (7, 0), (9, 0), (10, 2), (11, 2), (12, 0), (13, 0) },
            ReadProtobufLayout(firstAsset));
    }

    [Fact]
    public async Task Octo_supported_old_revision_can_update_to_the_reconstructed_title_database()
    {
        var title = ReadTitleResourceDefinition();
        var fromRevision = title.FromRevisions.First(revision => revision < title.Revision);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/v1/list/{title.AssetVersion}/{fromRevision}");
        request.Headers.Host = "kickflight-resource-api.grenge.jp";
        using var response = await _factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        AssertOctoRevision(body, title.Revision);
    }

    [Fact]
    public async Task Octo_current_revision_preserves_the_url_format()
    {
        var title = ReadTitleResourceDefinition();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/v1/list/{title.AssetVersion}/{title.Revision}");
        request.Headers.Host = "kickflight-resource-api.grenge.jp";
        using var response = await _factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        AssertOctoRevision(body, title.Revision);
        Assert.Contains("/cdn/{o}", Encoding.UTF8.GetString(body));
    }

    [Fact]
    public async Task Public_domain_uses_default_https_cdn_origin_even_when_forwarded_proto_is_http()
    {
        Assert.Equal("https://kick-flight-fenix.us.ci/cdn/{o}",
            new HarnessOptions().OctoCdnUrlFormatsByHost["kick-flight-fenix.us.ci"]);
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Harness:DirectClientHosts:0"] = "kick-flight-fenix.us.ci"
            });
        }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/list/12345/0");
        request.Headers.Host = "kick-flight-fenix.us.ci";
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "http");
        using var response = await factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("https://kick-flight-fenix.us.ci/cdn/{o}", body);
        Assert.DoesNotContain("http://kick-flight-fenix.us.ci/cdn/{o}", body);
    }

    [Fact]
    public async Task Explicit_cdn_format_overrides_host_specific_format()
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Harness:DirectClientHosts:0"] = "kick-flight-fenix.us.ci",
                ["Harness:OctoCdnUrlFormat"] = "https://assets.example.test/cdn/{o}"
            })));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/list/12345/0");
        request.Headers.Host = "kick-flight-fenix.us.ci";
        using var response = await factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("https://assets.example.test/cdn/{o}", body);
        Assert.DoesNotContain("https://kick-flight-fenix.us.ci/cdn/{o}", body);
    }

    [Fact]
    public async Task Public_ip_keeps_the_request_scheme_cdn_fallback()
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Harness:DirectClientHosts:0"] = "159.54.134.159"
            })));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/list/12345/0");
        request.Headers.Host = "159.54.134.159:18080";
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        using var response = await factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("http://159.54.134.159:18080/cdn/{o}", body);
        Assert.DoesNotContain("https://kick-flight-fenix.us.ci/cdn/{o}", body);
    }

    [Fact]
    public async Task Octo_supported_old_revision_receives_current_database_and_future_revision_is_not_fallback()
    {
        var title = ReadTitleResourceDefinition();
        var fromRevision = title.FromRevisions.First(revision => revision < title.Revision);
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/v1/list/{title.AssetVersion}/{fromRevision}");
        request.Headers.Host = "kickflight-resource-api.grenge.jp";
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        AssertOctoRevision(body, title.Revision);

        using var futureRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/v1/list/{title.AssetVersion}/{title.Revision + 1}");
        futureRequest.Headers.Host = "kickflight-resource-api.grenge.jp";
        using var futureResponse = await client.SendAsync(futureRequest);
        Assert.Equal(HttpStatusCode.NotFound, futureResponse.StatusCode);
    }

    [Fact]
    public async Task Configured_direct_client_host_routes_by_method_and_path()
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Harness:DirectClientHosts:0"] = "192.168.1.34"
            })));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/boot/index")
        {
            Content = new ByteArrayContent(new byte[32])
        };
        request.Content.Headers.ContentType = new("application/octet-stream");
        request.Headers.Host = "192.168.1.34:18080";
        using var response = await factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("accepted-boot-index-d2c", response.Headers.GetValues("x-kickflight-fixture").Single());
    }

    [Fact]
    public async Task Cataloged_resource_streams_with_integrity_and_range_support()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kickflight-resources", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var resourcePath = Path.Combine(directory, "sample.bundle");
        var catalogPath = Path.Combine(directory, "catalog.json");
        var bytes = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        await File.WriteAllBytesAsync(resourcePath, bytes);
        await File.WriteAllTextAsync(catalogPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            resources = new[]
            {
                new
                {
                    id = "sample-bundle",
                    host = "kickflight-resource-api.grenge.jp",
                    requestPath = "/cdn/sample.bundle",
                    logicalName = "Test bundle",
                    sourcePath = resourcePath,
                    contentType = "application/octet-stream",
                    sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
                }
            }
        }));

        try
        {
            using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Harness:ResourceCatalogPath"] = catalogPath
                })));
            using var request = new HttpRequestMessage(HttpMethod.Get, "/cdn/sample.bundle");
            request.Headers.Host = "kickflight-resource-api.grenge.jp";
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(4, 7);
            using var response = await factory.CreateClient().SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(bytes[4..8], await response.Content.ReadAsByteArrayAsync());
            Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal($"\"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()}\"", response.Headers.ETag?.Tag);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cataloged_resource_routes_through_direct_client_host()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kickflight-resources", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var resourcePath = Path.Combine(directory, "sample.bin");
        var catalogPath = Path.Combine(directory, "catalog.json");
        var bytes = "direct-resource"u8.ToArray();
        await File.WriteAllBytesAsync(resourcePath, bytes);
        await File.WriteAllTextAsync(catalogPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            resources = new[]
            {
                new
                {
                    id = "direct-resource",
                    host = "kickflight-resource-api.grenge.jp",
                    requestPath = "/cdn/direct.bin",
                    logicalName = "Direct resource",
                    sourcePath = resourcePath,
                    sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
                }
            }
        }));

        try
        {
            using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Harness:ResourceCatalogPath"] = catalogPath,
                    ["Harness:DirectClientHosts:0"] = "192.168.1.12"
                })));
            using var request = new HttpRequestMessage(HttpMethod.Get, "/cdn/direct.bin");
            request.Headers.Host = "192.168.1.12:18080";
            using var response = await factory.CreateClient().SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Unconfigured_direct_client_host_remains_rejected()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/boot/index");
        request.Headers.Host = "192.168.1.99:18080";
        using var response = await _factory.CreateClient().SendAsync(request);
        Assert.Equal((HttpStatusCode)421, response.StatusCode);
    }

    [Fact]
    public void D2c_codec_matches_known_aes256_cbc_pkcs7_vector()
    {
        var key = Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");
        var vector = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var encoded = D2CCodec.Encode("{}"u8, key, vector);
        Assert.Equal(
            "000102030405060708090A0B0C0D0E0F4D0F4FA5133FB38F26F5A933DFB9695C",
            Convert.ToHexString(encoded));
        Assert.Equal("{}", Encoding.UTF8.GetString(D2CCodec.Decode(encoded, key)));
    }

    [Fact]
    public async Task Unknown_host_is_rejected()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/anything");
        request.Headers.Host = "example.invalid";
        using var response = await _factory.CreateClient().SendAsync(request);
        Assert.Equal((HttpStatusCode)421, response.StatusCode);
    }

    [Fact]
    public void Every_known_sensitive_header_is_redacted_deterministically()
    {
        var inspector = new SafeRequestInspector();
        var headers = new HeaderDictionary();
        foreach (var name in SafeRequestInspector.SensitiveHeaders) headers[name] = "sensitive-value";
        var result = inspector.InspectHeaders(headers);
        foreach (var name in SafeRequestInspector.SensitiveHeaders)
        {
            var serialized = JsonSerializer.Serialize(result[name]);
            Assert.DoesNotContain("sensitive-value", serialized);
            Assert.Contains("sha256_12", serialized);
            using var fingerprint = JsonDocument.Parse(serialized);
            Assert.Equal(15, fingerprint.RootElement.GetProperty("length").GetInt32());
        }
    }

    [Fact]
    public void Json_body_redacts_device_and_auth_values()
    {
        var inspector = new SafeRequestInspector();
        var result = inspector.InspectBody(Encoding.UTF8.GetBytes("{\"uuid\":\"abc\",\"accessToken\":\"def\",\"safe\":42}"), "application/json", false);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("abc", json);
        Assert.DoesNotContain("def", json);
        Assert.Contains("safe", json);
    }

    [Fact]
    public async Task Capture_limit_truncates_preview_but_hashes_full_body()
    {
        var captureDirectory = Path.Combine(Path.GetTempPath(), "kickflight-tests", Guid.NewGuid().ToString("n"));
        try
        {
            using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Harness:PersistCaptures"] = "true",
                    ["Harness:CaptureDirectory"] = captureDirectory,
                    ["Harness:MaxCapturedBodyBytes"] = "4"
                })));
            using var request = new HttpRequestMessage(HttpMethod.Post, "/__fixture-probe")
            {
                Content = new StringContent("1234567890", Encoding.UTF8, "text/plain")
            };
            request.Headers.Host = "kickflight-api.grenge.jp";
            // A sensitive header, and one a real /boot/index actually carries (docs/evidence/boot-index-redacted.md):
            // this route is served by the fixture router, which never reads a session token, so the request must
            // stay a 200 whether or not the client has authenticated yet.
            request.Headers.Add("x-app-adid", "must-not-appear-in-capture");
            using var response = await factory.CreateClient().SendAsync(request);
            Assert.True(response.IsSuccessStatusCode);
            var file = Directory.GetFiles(captureDirectory, "request-*.json").Single();
            using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            var body = capture.RootElement.GetProperty("body");
            Assert.Equal(10, body.GetProperty("totalBytes").GetInt64());
            Assert.Equal(4, body.GetProperty("capturedBytes").GetInt32());
            Assert.True(body.GetProperty("truncated").GetBoolean());
            Assert.DoesNotContain("must-not-appear-in-capture", await File.ReadAllTextAsync(file));
        }
        finally
        {
            if (Directory.Exists(captureDirectory)) Directory.Delete(captureDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Fixture_changes_reload_without_recompiling()
    {
        var fixtureDirectory = Path.Combine(Path.GetTempPath(), "kickflight-fixtures", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(fixtureDirectory);
        var fixturePath = Path.Combine(fixtureDirectory, "reload.json");
        try
        {
            WriteReloadFixture(fixturePath, "first");
            using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Harness:FixtureDirectory"] = fixtureDirectory
                })));
            var client = factory.CreateClient();
            using var first = new HttpRequestMessage(HttpMethod.Get, "/reload");
            first.Headers.Host = "kickflight-api.grenge.jp";
            Assert.Equal("first", await (await client.SendAsync(first)).Content.ReadAsStringAsync());

            WriteReloadFixture(fixturePath, "second-longer");
            using var second = new HttpRequestMessage(HttpMethod.Get, "/reload");
            second.Headers.Host = "kickflight-api.grenge.jp";
            Assert.Equal("second-longer", await (await client.SendAsync(second)).Content.ReadAsStringAsync());
        }
        finally
        {
            if (Directory.Exists(fixtureDirectory)) Directory.Delete(fixtureDirectory, recursive: true);
        }
    }

    [Fact]
    public void Production_code_has_no_upstream_client_api()
    {
        var root = FindRepositoryRoot();
        var sourceFiles = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories);
        var forbidden = new[] { "HttpClient", "SocketsHttpHandler", "WebRequest.Create", "TcpClient" };
        // The policy this pins is that production code never proxies to the defunct upstream, which is why it is
        // strict about the symbol rather than about where a request would go. The single exception is the otomo
        // identity client: otomo is a service this project runs (docs/OTOMO_IDENTITY.md), and identity cannot be
        // delegated without an outbound call. Every other file stays forbidden, and this list stays this short.
        var exempt = new[]
        {
            Path.Combine("src", "KickFlight.BootstrapApi", "Otomo", "OtomoIdentity.cs"),
            Path.Combine("src", "KickFlight.BootstrapApi", "Otomo", "OtomoMasters.cs"),
            Path.Combine("src", "KickFlight.BootstrapApi", "Otomo", "OtomoEconomy.cs")
        };
        foreach (var file in sourceFiles)
        {
            if (exempt.Any(relative => file.EndsWith(relative, StringComparison.OrdinalIgnoreCase))) continue;
            var source = File.ReadAllText(file);
            foreach (var symbol in forbidden) Assert.DoesNotContain(symbol, source);
        }
    }

    [Fact]
    public async Task Download_master_returns_kicker_costume_and_matches_served_master()
    {
        var client = _factory.CreateClient();
        var host = "kickflight-api.grenge.jp";
        var sessionKey = "0123456789abcdef0123456789abcdef";
        var sessionKeyBytes = Encoding.ASCII.GetBytes(sessionKey);
        var commonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";
        var commonCodeBytes = Encoding.ASCII.GetBytes(commonCode);

        // 1. Authenticate to establish demo session
        var testUuid = Guid.NewGuid().ToString("N");
        var authPayload = JsonSerializer.Serialize(new { hash = sessionKey, uuid = testUuid });
        var authVector = new byte[16];
        var encodedAuth = D2CCodec.Encode(Encoding.UTF8.GetBytes(authPayload), commonCodeBytes, authVector);
        using var authRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/index")
        {
            Content = new ByteArrayContent(encodedAuth)
        };
        authRequest.Headers.Host = host;
        using var authResponse = await client.SendAsync(authRequest);
        Assert.Equal(HttpStatusCode.OK, authResponse.StatusCode);
        var accessToken = authResponse.Headers.GetValues("x-app-access-token").Single();

        // 2. Request /download/master
        using var masterRequest = new HttpRequestMessage(HttpMethod.Post, "/download/master")
        {
            Content = new ByteArrayContent(Array.Empty<byte>())
        };
        masterRequest.Headers.Host = host;
        masterRequest.Headers.Add("x-app-access-token", accessToken);
        using var masterResponse = await client.SendAsync(masterRequest);
        Assert.StartsWith("demo-master-", masterResponse.Headers.GetValues("x-app-master-hash").Single());

        var masterEncrypted = await masterResponse.Content.ReadAsByteArrayAsync();
        var masterDecrypted = D2CCodec.Decode(masterEncrypted, sessionKeyBytes);
        using var masterDoc = JsonDocument.Parse(masterDecrypted);
        var downloadList = masterDoc.RootElement.GetProperty("masterDownloadList");
        Assert.True(downloadList.GetArrayLength() >= 1);

        var kickerCostumeEntry = downloadList.EnumerateArray().First(e => e.GetProperty("name").GetString() == "KickerCostume");
        Assert.Equal("KickerCostume", kickerCostumeEntry.GetProperty("name").GetString());
        var masterUrl = kickerCostumeEntry.GetProperty("url").GetString();
        Assert.NotNull(masterUrl);
        Assert.StartsWith($"http://{host}/demo-master/KickerCostume", masterUrl);
        var expectedHash = kickerCostumeEntry.GetProperty("hash").GetString();
        var expectedSize = kickerCostumeEntry.GetProperty("size").GetInt32();

        // 3. Request GET /demo-master/KickerCostume
        using var getMasterRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(masterUrl).PathAndQuery);
        getMasterRequest.Headers.Host = host;
        using var getMasterResponse = await client.SendAsync(getMasterRequest);
        Assert.Equal(HttpStatusCode.OK, getMasterResponse.StatusCode);
        Assert.Equal("application/octet-stream", getMasterResponse.Content.Headers.ContentType?.MediaType);

        var masterBytes = await getMasterResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(expectedSize, masterBytes.Length);
        Assert.Equal(expectedHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(masterBytes)).ToLowerInvariant());

        var decryptedMaster = D2CCodec.Decode(masterBytes, Encoding.ASCII.GetBytes("1a837b9ee2ae11a07a0f529a4cd4b61c"));
        using var costumeDoc = JsonDocument.Parse(decryptedMaster);
        var list = costumeDoc.RootElement;
        Assert.Equal(118, list.GetArrayLength());
        // The customer-facing fields of the first row (Tsubame's default colour); the row id is the composite
        // 2|kicker|costume|variant, whose kicker-1 / costume-1 value the client itself hardcodes as 2010101.
        Assert.Equal(2010101, list[0].GetProperty("id").GetInt32());
        Assert.Equal(1, list[0].GetProperty("kickerId").GetInt32());
        Assert.Equal(1, list[0].GetProperty("costumeId").GetInt32());

        // 4. Request GET /demo-master/Kicker (verify 14 kickers)
        using var getKickerRequest = new HttpRequestMessage(HttpMethod.Get, "/demo-master/Kicker");
        getKickerRequest.Headers.Host = host;
        using var getKickerResponse = await client.SendAsync(getKickerRequest);
        Assert.Equal(HttpStatusCode.OK, getKickerResponse.StatusCode);
        var kickerBytes = await getKickerResponse.Content.ReadAsByteArrayAsync();
        var decryptedKicker = D2CCodec.Decode(kickerBytes, Encoding.ASCII.GetBytes("1a837b9ee2ae11a07a0f529a4cd4b61c"));
        using var kickerDoc = JsonDocument.Parse(decryptedKicker);
        Assert.Equal(14, kickerDoc.RootElement.GetArrayLength());
        Assert.Equal("Tsubame", kickerDoc.RootElement[0].GetProperty("name").GetString());
        Assert.Equal("Sid", kickerDoc.RootElement[13].GetProperty("name").GetString());

        // 5. Request GET /demo-master/BattleRule (verify Spanish localization)
        using var getRuleRequest = new HttpRequestMessage(HttpMethod.Get, "/demo-master/BattleRule");
        getRuleRequest.Headers.Host = host;
        using var getRuleResponse = await client.SendAsync(getRuleRequest);
        Assert.Equal(HttpStatusCode.OK, getRuleResponse.StatusCode);
        var ruleBytes = await getRuleResponse.Content.ReadAsByteArrayAsync();
        var decryptedRule = D2CCodec.Decode(ruleBytes, Encoding.ASCII.GetBytes("1a837b9ee2ae11a07a0f529a4cd4b61c"));
        using var ruleDoc = JsonDocument.Parse(decryptedRule);
        Assert.Equal("Crystal Scramble", ruleDoc.RootElement[0].GetProperty("name").GetString());
        Assert.Equal("Rapid Ball", ruleDoc.RootElement[2].GetProperty("name").GetString());

        // 6. Request POST /startup/index (verify 14 kickers unlocked)
        using var startupRequest = new HttpRequestMessage(HttpMethod.Post, "/startup/index")
        {
            Content = new ByteArrayContent(Array.Empty<byte>())
        };
        startupRequest.Headers.Host = host;
        startupRequest.Headers.Add("x-app-access-token", accessToken);
        using var startupResponse = await client.SendAsync(startupRequest);
        Assert.Equal(HttpStatusCode.OK, startupResponse.StatusCode);
        var startupBytes = await startupResponse.Content.ReadAsByteArrayAsync();
        var startupDecrypted = D2CCodec.Decode(startupBytes, sessionKeyBytes);
        using var startupDoc = JsonDocument.Parse(startupDecrypted);
        // Ownership is economy's, and a fresh otomo account owns nothing: the startup payload carries the one kicker
        // the save says is selected (identity, not value) and no discs at all. That the masters still carry every
        // kicker, costume and disc is asserted through /download/master above and below, which is the catalogue's
        // own contract - a player owning rows of it is a different question, and economy answers it.
        var userKickerList = startupDoc.RootElement.GetProperty("userKickerList");
        Assert.Equal(1, userKickerList.GetArrayLength());
        Assert.Equal(1, userKickerList[0].GetProperty("kickerId").GetInt32());
        var userDiscList = startupDoc.RootElement.GetProperty("userDiscList");
        Assert.Empty(userDiscList.EnumerateArray());

        // 7. Request POST /home/index (initial kickerId = 1, 5 decks)
        using var homeRequest = new HttpRequestMessage(HttpMethod.Post, "/home/index")
        {
            Content = new ByteArrayContent(Array.Empty<byte>())
        };
        homeRequest.Headers.Host = host;
        homeRequest.Headers.Add("x-app-access-token", accessToken);
        using var homeResponse = await client.SendAsync(homeRequest);
        Assert.Equal(HttpStatusCode.OK, homeResponse.StatusCode);
        var homeBytes = await homeResponse.Content.ReadAsByteArrayAsync();
        var homeDecrypted = D2CCodec.Decode(homeBytes, sessionKeyBytes);
        using var homeDoc = JsonDocument.Parse(homeDecrypted);
        Assert.Equal(1, homeDoc.RootElement.GetProperty("userPlayer").GetProperty("kickerId").GetInt32());
        var userDiscDeckList = homeDoc.RootElement.GetProperty("userDiscDeckList");
        Assert.Equal(5, userDiscDeckList.GetArrayLength());

        // 8. Select kicker 8 (Anna) and ask for a costume that is not hers: 2010101 is kicker 1's first
        //    costume, so the server has to swap it for one of Anna's (see the assertion in step 10).
        var changePayload = JsonSerializer.Serialize(new { kickerId = 8, kickerCostumeId = 2010101 });
        var encodedChange = D2CCodec.Encode(Encoding.UTF8.GetBytes(changePayload), sessionKeyBytes, new byte[16]);
        using var changeRequest = new HttpRequestMessage(HttpMethod.Post, "/kicker/change")
        {
            Content = new ByteArrayContent(encodedChange)
        };
        changeRequest.Headers.Host = host;
        changeRequest.Headers.Add("x-app-access-token", accessToken);
        using var changeResponse = await client.SendAsync(changeRequest);
        Assert.Equal(HttpStatusCode.OK, changeResponse.StatusCode);
        Assert.Equal("0", changeResponse.Headers.GetValues("x-app-status-code").Single());

        // 9. Update Deck 2 and switch active deck via POST /disc/change
        var discChangePayload = JsonSerializer.Serialize(new
        {
            discDeckNumber = 2,
            userDiscDeckList = new[]
            {
                new { number = 2, discIdList = new[] { 3010050, 3010051, 3010052, 3010053 } }
            }
        });
        var encodedDiscChange = D2CCodec.Encode(Encoding.UTF8.GetBytes(discChangePayload), sessionKeyBytes, new byte[16]);
        using var discChangeRequest = new HttpRequestMessage(HttpMethod.Post, "/disc/change")
        {
            Content = new ByteArrayContent(encodedDiscChange)
        };
        discChangeRequest.Headers.Host = host;
        discChangeRequest.Headers.Add("x-app-access-token", accessToken);
        using var discChangeResponse = await client.SendAsync(discChangeRequest);
        Assert.Equal(HttpStatusCode.OK, discChangeResponse.StatusCode);
        Assert.Equal("0", discChangeResponse.Headers.GetValues("x-app-status-code").Single());

        // 10. Request POST /home/index again (persisted kickerId = 8, activeDeck = 2; kickerCostumeId is a KickerCostume row id,
        //     and that one belongs to kicker 1, so NormalizeCostume swaps it for Anna's first costume row 2080101)
        using var homeRequest2 = new HttpRequestMessage(HttpMethod.Post, "/home/index")
        {
            Content = new ByteArrayContent(Array.Empty<byte>())
        };
        homeRequest2.Headers.Host = host;
        homeRequest2.Headers.Add("x-app-access-token", accessToken);
        using var homeResponse2 = await client.SendAsync(homeRequest2);
        Assert.Equal(HttpStatusCode.OK, homeResponse2.StatusCode);
        var homeBytes2 = await homeResponse2.Content.ReadAsByteArrayAsync();
        var homeDecrypted2 = D2CCodec.Decode(homeBytes2, sessionKeyBytes);
        using var homeDoc2 = JsonDocument.Parse(homeDecrypted2);
        Assert.Equal(8, homeDoc2.RootElement.GetProperty("userPlayer").GetProperty("kickerId").GetInt32());
        Assert.Equal(2080101, homeDoc2.RootElement.GetProperty("userPlayer").GetProperty("kickerCostumeId").GetInt32());
        Assert.Equal(2, homeDoc2.RootElement.GetProperty("userPlayer").GetProperty("discDeckNumber").GetInt32());
        var updatedDeck2 = homeDoc2.RootElement.GetProperty("userDiscDeckList").EnumerateArray().First(d => d.GetProperty("number").GetInt32() == 2);
        Assert.Equal(3010050, updatedDeck2.GetProperty("discIdList")[0].GetInt32());
    }

    [Fact]
    public async Task Download_master_uses_canonical_https_origin_but_keeps_android_ip_http_origin()
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Harness:DirectClientHosts:0"] = "kick-flight-fenix.us.ci",
                ["Harness:DirectClientHosts:1"] = "159.54.134.159"
            })));
        var client = factory.CreateClient();
        const string sessionKey = "0123456789abcdef0123456789abcdef";
        const string commonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";
        var commonCodeBytes = Encoding.ASCII.GetBytes(commonCode);
        var uuid = Guid.NewGuid().ToString("N");
        var authPayload = JsonSerializer.Serialize(new { hash = sessionKey, uuid });
        var encodedAuth = D2CCodec.Encode(Encoding.UTF8.GetBytes(authPayload), commonCodeBytes, new byte[16]);
        using var authRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/index")
        {
            Content = new ByteArrayContent(encodedAuth)
        };
        authRequest.Headers.Host = "kick-flight-fenix.us.ci";
        using var authResponse = await client.SendAsync(authRequest);
        Assert.Equal(HttpStatusCode.OK, authResponse.StatusCode);
        var accessToken = authResponse.Headers.GetValues("x-app-access-token").Single();

        async Task<JsonDocument> GetMasterList(string host)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/download/master")
            {
                Content = new ByteArrayContent(Array.Empty<byte>())
            };
            request.Headers.Host = host;
            request.Headers.Add("x-app-access-token", accessToken);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(),
                Encoding.ASCII.GetBytes(sessionKey)));
        }

        using var publicHost = await GetMasterList("kick-flight-fenix.us.ci");
        using var androidIp = await GetMasterList("159.54.134.159:18080");
        var publicRows = publicHost.RootElement.GetProperty("masterDownloadList").EnumerateArray().ToArray();
        var androidRows = androidIp.RootElement.GetProperty("masterDownloadList").EnumerateArray().ToArray();
        Assert.NotEmpty(publicRows);
        Assert.Equal(publicRows.Length, androidRows.Length);
        for (var index = 0; index < publicRows.Length; index++)
        {
            var publicRow = publicRows[index];
            var androidRow = androidRows[index];
            var name = publicRow.GetProperty("name").GetString();
            Assert.Equal(name, androidRow.GetProperty("name").GetString());
            Assert.Equal(publicRow.GetProperty("hash").GetString(), androidRow.GetProperty("hash").GetString());
            Assert.Equal(publicRow.GetProperty("size").GetInt32(), androidRow.GetProperty("size").GetInt32());
            Assert.Equal($"https://kick-flight-fenix.us.ci/demo-master/{name}", publicRow.GetProperty("url").GetString());
            Assert.Equal($"http://159.54.134.159:18080/demo-master/{name}", androidRow.GetProperty("url").GetString());
        }
    }

    [Fact]
    public async Task Rapid_ball_masters_are_served_with_goal_guardians()
    {
        var client = _factory.CreateClient();
        var host = "kickflight-api.grenge.jp";
        var sessionKey = "0123456789abcdef0123456789abcdef";
        var sessionKeyBytes = Encoding.ASCII.GetBytes(sessionKey);
        var commonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";
        var commonCodeBytes = Encoding.ASCII.GetBytes(commonCode);

        // 1. Authenticate to establish demo session
        var testUuid = Guid.NewGuid().ToString("N");
        var authPayload = JsonSerializer.Serialize(new { hash = sessionKey, uuid = testUuid });
        var encodedAuth = D2CCodec.Encode(Encoding.UTF8.GetBytes(authPayload), commonCodeBytes, new byte[16]);
        using var authRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/index")
        {
            Content = new ByteArrayContent(encodedAuth)
        };
        authRequest.Headers.Host = host;
        using var authResponse = await client.SendAsync(authRequest);
        Assert.Equal(HttpStatusCode.OK, authResponse.StatusCode);
        var accessToken = authResponse.Headers.GetValues("x-app-access-token").Single();

        // 2. The rule controllers read both score tables every frame and at match end; an unlisted table makes the
        //    client NRE in GetScore and loop on the loading screen instead of sending /battle/end.
        using var masterRequest = new HttpRequestMessage(HttpMethod.Post, "/download/master")
        {
            Content = new ByteArrayContent(Array.Empty<byte>())
        };
        masterRequest.Headers.Host = host;
        masterRequest.Headers.Add("x-app-access-token", accessToken);
        using var masterResponse = await client.SendAsync(masterRequest);
        var masterDecrypted = D2CCodec.Decode(await masterResponse.Content.ReadAsByteArrayAsync(), sessionKeyBytes);
        using var masterDoc = JsonDocument.Parse(masterDecrypted);
        var masterNames = masterDoc.RootElement.GetProperty("masterDownloadList").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Contains("BattleRuleRapidBallScore", masterNames);
        Assert.Contains("BattleRuleFlagFlightScore", masterNames);

        // 3. Ball goals hold a guardian (guardianAmount 1 for the ball rules 3/4), and the crystal rule is untouched.
        using var getRuleRequest = new HttpRequestMessage(HttpMethod.Get, "/demo-master/BattleRule");
        getRuleRequest.Headers.Host = host;
        using var getRuleResponse = await client.SendAsync(getRuleRequest);
        Assert.Equal(HttpStatusCode.OK, getRuleResponse.StatusCode);
        var ruleBytes = await getRuleResponse.Content.ReadAsByteArrayAsync();
        using var ruleDoc = JsonDocument.Parse(D2CCodec.Decode(ruleBytes, commonCodeBytes));
        var rules = ruleDoc.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("id").GetInt32());
        Assert.Equal(1, rules[3].GetProperty("guardianAmount").GetInt32());
        Assert.Equal(1, rules[4].GetProperty("guardianAmount").GetInt32());
        Assert.Equal(1, rules[1].GetProperty("guardianAmount").GetInt32());
        Assert.Equal(50, rules[1].GetProperty("crystalAmount").GetInt32());
        Assert.Equal((2, 1), (rules[7].GetProperty("matchType").GetInt32(), rules[7].GetProperty("battleRuleType").GetInt32()));
        Assert.Equal((2, 2), (rules[8].GetProperty("matchType").GetInt32(), rules[8].GetProperty("battleRuleType").GetInt32()));
        Assert.Equal("Rapid Ball", rules[4].GetProperty("name").GetString());

        using var scheduleRequest = new HttpRequestMessage(HttpMethod.Get, "/demo-master/RankerMatchBattleSchedule");
        scheduleRequest.Headers.Host = host;
        using var scheduleResponse = await client.SendAsync(scheduleRequest);
        Assert.Equal(HttpStatusCode.OK, scheduleResponse.StatusCode);
        using var scheduleDoc = JsonDocument.Parse(D2CCodec.Decode(await scheduleResponse.Content.ReadAsByteArrayAsync(), commonCodeBytes));
        Assert.Equal(48, scheduleDoc.RootElement.GetArrayLength());
        Assert.Contains(scheduleDoc.RootElement.EnumerateArray(), row =>
            row.GetProperty("seasonMatchBattleRuleType").GetInt32() == 1
            && row.GetProperty("battleRuleId").GetInt32() == RankedModeRotation.CrystalRuleId);
        Assert.Contains(scheduleDoc.RootElement.EnumerateArray(), row =>
            row.GetProperty("seasonMatchBattleRuleType").GetInt32() == 2
            && row.GetProperty("battleRuleId").GetInt32() == RankedModeRotation.FlagRuleId);
        Assert.Contains(scheduleDoc.RootElement.EnumerateArray(), row =>
            row.GetProperty("seasonMatchBattleRuleType").GetInt32() == 3
            && row.GetProperty("battleRuleId").GetInt32() == RankedModeRotation.RapidBallRuleId);
        Assert.Contains(scheduleDoc.RootElement.EnumerateArray(), row =>
            row.GetProperty("seasonMatchBattleRuleType").GetInt32() == -1
            && row.GetProperty("battleRuleId").GetInt32() == RankedModeRotation.CrystalRuleId);

        using var ruleFieldsRequest = new HttpRequestMessage(HttpMethod.Get, "/demo-master/BattleRuleField");
        ruleFieldsRequest.Headers.Host = host;
        using var ruleFieldsResponse = await client.SendAsync(ruleFieldsRequest);
        Assert.Equal(HttpStatusCode.OK, ruleFieldsResponse.StatusCode);
        using var ruleFieldsDoc = JsonDocument.Parse(D2CCodec.Decode(await ruleFieldsResponse.Content.ReadAsByteArrayAsync(), commonCodeBytes));
        var ruleFields = ruleFieldsDoc.RootElement.EnumerateArray().ToArray();
        foreach (var rankedRuleId in new[] { RankedModeRotation.CrystalRuleId, RankedModeRotation.FlagRuleId })
        {
            Assert.Equal(
                ruleFields.Where(row => row.GetProperty("battleRuleId").GetInt32() == 1).Select(row => row.GetProperty("fieldId").GetInt32()).Order().ToArray(),
                ruleFields.Where(row => row.GetProperty("battleRuleId").GetInt32() == rankedRuleId).Select(row => row.GetProperty("fieldId").GetInt32()).Order().ToArray());
        }

        foreach (var (masterName, expectedRuleId) in new[]
                 {
                     ("BattleRuleScrambleScore", RankedModeRotation.CrystalRuleId),
                     ("BattleRuleFlagFlightScore", RankedModeRotation.FlagRuleId)
                 })
        {
            using var scoreRequest = new HttpRequestMessage(HttpMethod.Get, $"/demo-master/{masterName}");
            scoreRequest.Headers.Host = host;
            using var scoreResponse = await client.SendAsync(scoreRequest);
            Assert.Equal(HttpStatusCode.OK, scoreResponse.StatusCode);
            using var modeScoreDoc = JsonDocument.Parse(D2CCodec.Decode(await scoreResponse.Content.ReadAsByteArrayAsync(), commonCodeBytes));
            Assert.Contains(modeScoreDoc.RootElement.EnumerateArray(), row =>
                row.GetProperty("id").GetInt32() == expectedRuleId
                && row.GetProperty("battleRuleId").GetInt32() == expectedRuleId);
        }

        // 4. Row 2 is the weaker goal guardian /battle/start returns for the ball rules.
        using var getGuardianRequest = new HttpRequestMessage(HttpMethod.Get, "/demo-master/GuardianParameter");
        getGuardianRequest.Headers.Host = host;
        using var getGuardianResponse = await client.SendAsync(getGuardianRequest);
        Assert.Equal(HttpStatusCode.OK, getGuardianResponse.StatusCode);
        var guardianBytes = await getGuardianResponse.Content.ReadAsByteArrayAsync();
        using var guardianDoc = JsonDocument.Parse(D2CCodec.Decode(guardianBytes, commonCodeBytes));
        var guardians = guardianDoc.RootElement.EnumerateArray().ToDictionary(g => g.GetProperty("id").GetInt32());
        Assert.Equal(3000, guardians[2].GetProperty("hp").GetInt32());

        // 5. Both ball rules (3 and 4) need a score row keyed by battleRuleId.
        using var getScoreRequest = new HttpRequestMessage(HttpMethod.Get, "/demo-master/BattleRuleRapidBallScore");
        getScoreRequest.Headers.Host = host;
        using var getScoreResponse = await client.SendAsync(getScoreRequest);
        Assert.Equal(HttpStatusCode.OK, getScoreResponse.StatusCode);
        var scoreBytes = await getScoreResponse.Content.ReadAsByteArrayAsync();
        using var scoreDoc = JsonDocument.Parse(D2CCodec.Decode(scoreBytes, commonCodeBytes));
        Assert.Contains(scoreDoc.RootElement.EnumerateArray(), r => r.GetProperty("battleRuleId").GetInt32() == 3);

        // 6. /battle/start hands the ball rule (battleRuleType 3) the goal-guardian row 2, everything else row 1.
        using var startRequest = new HttpRequestMessage(HttpMethod.Post, "/battle/start")
        {
            Content = new ByteArrayContent(D2CCodec.Encode(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { battleId = "battle-1", battleRuleId = 3 })),
                sessionKeyBytes, new byte[16]))
        };
        startRequest.Headers.Host = host;
        startRequest.Headers.Add("x-app-access-token", accessToken);
        using var startResponse = await client.SendAsync(startRequest);
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);
        using var startDoc = JsonDocument.Parse(D2CCodec.Decode(await startResponse.Content.ReadAsByteArrayAsync(), sessionKeyBytes));
        Assert.Equal(2, startDoc.RootElement.GetProperty("guardianParameter").GetProperty("id").GetInt32());
        // The arena is drawn at random from BattleMatchmakingService.FieldPool (KF_FIELDS) for every battle.
        Assert.Contains(startDoc.RootElement.GetProperty("fieldId").GetInt32(), BattleMatchmakingService.FieldPool);
    }

    [Fact]
    public async Task Second_battle_start_succeeds_before_first_battle_ends()
    {
        var client = _factory.CreateClient();
        const string host = "kickflight-api.grenge.jp";
        var sessionKeyBytes = Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");
        var commonCodeBytes = Encoding.ASCII.GetBytes("1a837b9ee2ae11a07a0f529a4cd4b61c");

        var authPayload = JsonSerializer.Serialize(new { hash = Encoding.ASCII.GetString(sessionKeyBytes), uuid = Guid.NewGuid().ToString("N") });
        using var authRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/index")
        {
            Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(authPayload), commonCodeBytes, new byte[16]))
        };
        authRequest.Headers.Host = host;
        using var authResponse = await client.SendAsync(authRequest);
        Assert.Equal(HttpStatusCode.OK, authResponse.StatusCode);
        var accessToken = authResponse.Headers.GetValues("x-app-access-token").Single();

        async Task<JsonElement> StartBattle(string battleId, int ruleId)
        {
            var payload = JsonSerializer.Serialize(new { battleId, battleRuleId = ruleId });
            using var request = new HttpRequestMessage(HttpMethod.Post, "/battle/start")
            {
                Content = new ByteArrayContent(D2CCodec.Encode(Encoding.UTF8.GetBytes(payload), sessionKeyBytes, new byte[16]))
            };
            request.Headers.Host = host;
            request.Headers.Add("x-app-access-token", accessToken);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("0", response.Headers.GetValues("x-app-status-code").Single());
            return JsonDocument.Parse(D2CCodec.Decode(await response.Content.ReadAsByteArrayAsync(), sessionKeyBytes)).RootElement.Clone();
        }

        // Battle A remains active while the server starts battle B under a separate id and rule.
        var battleA = await StartBattle($"battle-a-{Guid.NewGuid():N}", ruleId: 3);
        var battleB = await StartBattle($"battle-b-{Guid.NewGuid():N}", ruleId: 1);
        Assert.Equal(2, battleA.GetProperty("guardianParameter").GetProperty("id").GetInt32());
        Assert.Equal(1, battleB.GetProperty("guardianParameter").GetProperty("id").GetInt32());
        Assert.Contains(battleA.GetProperty("fieldId").GetInt32(), BattleMatchmakingService.FieldPool);
        Assert.Contains(battleB.GetProperty("fieldId").GetInt32(), BattleMatchmakingService.FieldPool);

        using var endRequest = new HttpRequestMessage(HttpMethod.Post, "/battle/end")
        {
            Content = new ByteArrayContent(Array.Empty<byte>())
        };
        endRequest.Headers.Host = host;
        endRequest.Headers.Add("x-app-access-token", accessToken);
        using var endResponse = await client.SendAsync(endRequest);
        Assert.Equal(HttpStatusCode.OK, endResponse.StatusCode);
        Assert.Equal("0", endResponse.Headers.GetValues("x-app-status-code").Single());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KickFlight.PrivateServer.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static (int AssetVersion, int Revision, int[] FromRevisions) ReadTitleResourceDefinition()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(FindRepositoryRoot(), "config/resources/title-minimum.json")));
        var title = document.RootElement;
        return (
            title.GetProperty("assetVersion").GetInt32(),
            title.GetProperty("revision").GetInt32(),
            title.GetProperty("fromRevisions").EnumerateArray().Select(revision => revision.GetInt32()).ToArray());
    }

    private static void AssertOctoRevision(ReadOnlySpan<byte> body, int expectedRevision)
    {
        var offset = 0;
        Assert.Equal(8UL, ReadVarint(body, ref offset)); // Database.revision tag
        Assert.Equal((ulong)expectedRevision, ReadVarint(body, ref offset));
    }

    private static (int Field, int WireType)[] ReadProtobufLayout(ReadOnlySpan<byte> payload)
    {
        var result = new List<(int, int)>();
        var offset = 0;
        while (offset < payload.Length)
        {
            var tag = ReadVarint(payload, ref offset);
            var wireType = checked((int)(tag & 7));
            result.Add((checked((int)(tag >> 3)), wireType));
            switch (wireType)
            {
                case 0:
                    ReadVarint(payload, ref offset);
                    break;
                case 2:
                    var length = checked((int)ReadVarint(payload, ref offset));
                    offset += length;
                    break;
                default:
                    throw new InvalidDataException(
                        $"Unsupported protobuf wire type {wireType} at offset {offset - 1}; " +
                        $"payload={Convert.ToHexString(payload)}");
            }
        }
        return result.ToArray();
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> payload, ref int offset)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64 && offset < payload.Length; shift += 7)
        {
            var value = payload[offset++];
            result |= (ulong)(value & 0x7F) << shift;
            if ((value & 0x80) == 0) return result;
        }
        throw new InvalidDataException("Invalid protobuf varint.");
    }

    private static void WriteReloadFixture(string path, string body)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            id = "reload-test",
            enabled = true,
            host = "kickflight-api.grenge.jp",
            method = "GET",
            path = "/reload",
            statusCode = 200,
            contentType = "text/plain",
            body
        }));
    }
}
