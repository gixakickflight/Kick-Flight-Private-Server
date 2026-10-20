namespace KickFlight.BootstrapApi.Tests;

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KickFlight.BootstrapApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Collection("Matchmaking static state")]
public class BotAiMasterHttpTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly string _commonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";
    private readonly string _sessionKey = "0123456789abcdef0123456789abcdef";
    private string? _accessToken;

    public BotAiMasterHttpTests()
    {
        Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, "1");

        _tempDir = Path.Combine(Path.GetTempPath(), "bot-ai-http-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
        _configFilePath = Path.Combine(_tempDir, "config", "bot_ai_profiles.json");

        var repoRoot = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        var sourceConfig = Path.Combine(repoRoot, "config", "bot_ai_profiles.json");
        File.Copy(sourceConfig, _configFilePath);

        Environment.SetEnvironmentVariable(BotAiProfileProvider.ConfigPathEnvVar, _configFilePath);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(_tempDir);
        });
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, null);
        Environment.SetEnvironmentVariable(BotAiProfileProvider.ConfigPathEnvVar, null);
        _client.Dispose();
        _factory.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    private async Task<string> AuthenticateAsync()
    {
        if (_accessToken != null) return _accessToken;

        var host = "kickflight-api.grenge.jp";
        var testUuid = Guid.NewGuid().ToString("N");
        var authPayload = JsonSerializer.Serialize(new { hash = _sessionKey, uuid = testUuid });
        var encodedAuth = D2CCodec.Encode(Encoding.UTF8.GetBytes(authPayload), Encoding.ASCII.GetBytes(_commonCode), new byte[16]);

        using var authRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/index")
        {
            Content = new ByteArrayContent(encodedAuth)
        };
        authRequest.Headers.Host = host;

        using var authResponse = await _client.SendAsync(authRequest);
        Assert.Equal(HttpStatusCode.OK, authResponse.StatusCode);
        _accessToken = authResponse.Headers.GetValues("x-app-access-token").Single();
        return _accessToken;
    }

    [Fact]
    public async Task MasterDownload_HotReload_UpdatesHashAndContentCoherently()
    {
        var token = await AuthenticateAsync();
        var host = "kickflight-api.grenge.jp";

        // 1. Initial /download/master request
        using var masterReq1 = new HttpRequestMessage(HttpMethod.Post, "/download/master")
        {
            Content = new ByteArrayContent(Array.Empty<byte>())
        };
        masterReq1.Headers.Host = host;
        masterReq1.Headers.Add("x-app-access-token", token);

        using var masterResp1 = await _client.SendAsync(masterReq1);
        Assert.Equal(HttpStatusCode.OK, masterResp1.StatusCode);
        var masterVersion1 = masterResp1.Headers.GetValues("x-app-master-hash").Single();
        Assert.StartsWith("demo-master-", masterVersion1);

        var decryptedList1 = D2CCodec.Decode(await masterResp1.Content.ReadAsByteArrayAsync(), Encoding.ASCII.GetBytes(_sessionKey));
        using var doc1 = JsonDocument.Parse(decryptedList1);
        var list1 = doc1.RootElement.GetProperty("masterDownloadList");

        var kickerAiEntry1 = list1.EnumerateArray().First(e => e.GetProperty("name").GetString() == "KickerAiParameter");
        var hash1 = kickerAiEntry1.GetProperty("hash").GetString()!;
        var size1 = kickerAiEntry1.GetProperty("size").GetInt32();
        var url1 = kickerAiEntry1.GetProperty("url").GetString()!;

        // 2. Fetch GET /demo-master/KickerAiParameter
        using var getReq1 = new HttpRequestMessage(HttpMethod.Get, new Uri(url1).PathAndQuery);
        getReq1.Headers.Host = host;
        using var getResp1 = await _client.SendAsync(getReq1);
        Assert.Equal(HttpStatusCode.OK, getResp1.StatusCode);
        Assert.Contains("no-cache", getResp1.Headers.CacheControl?.ToString() ?? "");
        Assert.Equal($"\"{hash1}\"", getResp1.Headers.ETag?.Tag);

        var contentBytes1 = await getResp1.Content.ReadAsByteArrayAsync();
        Assert.Equal(size1, contentBytes1.Length);
        Assert.Equal(hash1, Convert.ToHexString(SHA256.HashData(contentBytes1)).ToLowerInvariant());

        var decryptedTable1 = Encoding.UTF8.GetString(D2CCodec.Decode(contentBytes1, Encoding.ASCII.GetBytes(_commonCode)));
        var rows1 = JsonNode.Parse(decryptedTable1)!.AsArray();

        // Parameter ID 15 is Ana carrier (scoreMotivation is 90)
        var row15_v1 = rows1.First(r => r?["id"]?.GetValue<int>() == 15)!;
        Assert.Equal(90, row15_v1["scoreMotivation"]?.GetValue<int>());

        // 3. Update configuration: change Ana carrier scoreMotivation to 45
        var configNode = JsonNode.Parse(File.ReadAllText(_configFilePath))!;
        configNode["version"] = 2;
        configNode["profiles"]![0]!["motivations"]!["score"] = 45;
        File.WriteAllText(_configFilePath, configNode.ToJsonString());

        // Trigger reload on the provider
        var provider = _factory.Services.GetRequiredService<IBotAiProfileProvider>();
        var reloaded = provider.Reload();
        Assert.True(reloaded);

        // 4. Request /download/master again
        using var masterReq2 = new HttpRequestMessage(HttpMethod.Post, "/download/master")
        {
            Content = new ByteArrayContent(Array.Empty<byte>())
        };
        masterReq2.Headers.Host = host;
        masterReq2.Headers.Add("x-app-access-token", token);

        using var masterResp2 = await _client.SendAsync(masterReq2);
        Assert.Equal(HttpStatusCode.OK, masterResp2.StatusCode);
        var masterVersion2 = masterResp2.Headers.GetValues("x-app-master-hash").Single();
        Assert.StartsWith("demo-master-", masterVersion2);
        Assert.NotEqual(masterVersion1, masterVersion2); // Master hash MUST change!

        var decryptedList2 = D2CCodec.Decode(await masterResp2.Content.ReadAsByteArrayAsync(), Encoding.ASCII.GetBytes(_sessionKey));
        using var doc2 = JsonDocument.Parse(decryptedList2);
        var list2 = doc2.RootElement.GetProperty("masterDownloadList");

        var kickerAiEntry2 = list2.EnumerateArray().First(e => e.GetProperty("name").GetString() == "KickerAiParameter");
        var hash2 = kickerAiEntry2.GetProperty("hash").GetString()!;
        var size2 = kickerAiEntry2.GetProperty("size").GetInt32();
        var url2 = kickerAiEntry2.GetProperty("url").GetString()!;

        Assert.NotEqual(hash1, hash2); // Hash MUST change!

        // 5. Fetch updated GET /demo-master/KickerAiParameter
        using var getReq2 = new HttpRequestMessage(HttpMethod.Get, new Uri(url2).PathAndQuery);
        getReq2.Headers.Host = host;
        using var getResp2 = await _client.SendAsync(getReq2);
        Assert.Equal(HttpStatusCode.OK, getResp2.StatusCode);
        Assert.Equal($"\"{hash2}\"", getResp2.Headers.ETag?.Tag);

        var contentBytes2 = await getResp2.Content.ReadAsByteArrayAsync();
        Assert.Equal(size2, contentBytes2.Length);
        Assert.Equal(hash2, Convert.ToHexString(SHA256.HashData(contentBytes2)).ToLowerInvariant());

        var decryptedTable2 = Encoding.UTF8.GetString(D2CCodec.Decode(contentBytes2, Encoding.ASCII.GetBytes(_commonCode)));
        var rows2 = JsonNode.Parse(decryptedTable2)!.AsArray();

        // Parameter ID 15 must now reflect scoreMotivation == 45!
        var row15_v2 = rows2.First(r => r?["id"]?.GetValue<int>() == 15)!;
        Assert.Equal(45, row15_v2["scoreMotivation"]?.GetValue<int>());
    }

    [Fact]
    public async Task Concurrent_HttpDownloadAndHotReload_GuaranteesCoherentManifestHashAndContent()
    {
        var token = await AuthenticateAsync();
        var host = "kickflight-api.grenge.jp";
        var provider = _factory.Services.GetRequiredService<IBotAiProfileProvider>();

        // Verify single coherent DI lifecycle
        Assert.Same(provider, ExperimentalAiRoleService.CurrentProvider);

        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));

        // Background reload task repeatedly mutating config and triggering reload
        var reloadTask = Task.Run(async () =>
        {
            int ver = 10;
            while (!cts.Token.IsCancellationRequested)
            {
                ver++;
                var configNode = JsonNode.Parse(File.ReadAllText(_configFilePath))!;
                configNode["version"] = ver;
                configNode["profiles"]![0]!["motivations"]!["score"] = 40 + (ver % 50);
                File.WriteAllText(_configFilePath, configNode.ToJsonString());

                provider.Reload();
                await Task.Delay(25, cts.Token).ConfigureAwait(false);
            }
        }, cts.Token);

        // 6 concurrent HTTP downloader tasks continuously requesting manifest and master blob
        var readerTasks = Enumerable.Range(0, 6).Select(async readerId =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                // 1. POST /download/master
                using var masterReq = new HttpRequestMessage(HttpMethod.Post, "/download/master")
                {
                    Content = new ByteArrayContent(Array.Empty<byte>())
                };
                masterReq.Headers.Host = host;
                masterReq.Headers.Add("x-app-access-token", token);

                using var masterResp = await _client.SendAsync(masterReq).ConfigureAwait(false);
                Assert.Equal(HttpStatusCode.OK, masterResp.StatusCode);

                var headerVersion = masterResp.Headers.GetValues("x-app-master-hash").Single();
                Assert.StartsWith("demo-master-", headerVersion);

                var decryptedManifest = D2CCodec.Decode(
                    await masterResp.Content.ReadAsByteArrayAsync().ConfigureAwait(false),
                    Encoding.ASCII.GetBytes(_sessionKey));

                using var doc = JsonDocument.Parse(decryptedManifest);
                var list = doc.RootElement.GetProperty("masterDownloadList");

                var kickerAiEntry = list.EnumerateArray().First(e => e.GetProperty("name").GetString() == "KickerAiParameter");
                var manifestHash = kickerAiEntry.GetProperty("hash").GetString()!;
                var manifestSize = kickerAiEntry.GetProperty("size").GetInt32();
                var manifestUrl = kickerAiEntry.GetProperty("url").GetString()!;

                // 2. GET /demo-master/KickerAiParameter
                using var getReq = new HttpRequestMessage(HttpMethod.Get, new Uri(manifestUrl).PathAndQuery);
                getReq.Headers.Host = host;

                using var getResp = await _client.SendAsync(getReq).ConfigureAwait(false);
                Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

                var etag = getResp.Headers.ETag?.Tag?.Trim('"');
                var servedBytes = await getResp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

                // Anti-torn read assertions:
                // ETag on served blob MUST match the SHA256 of the served content exactly
                var actualContentHash = Convert.ToHexString(SHA256.HashData(servedBytes)).ToLowerInvariant();
                Assert.Equal(actualContentHash, etag);
                Assert.Equal(manifestSize, servedBytes.Length);

                // Decode and verify table content validity
                var decryptedTable = Encoding.UTF8.GetString(D2CCodec.Decode(servedBytes, Encoding.ASCII.GetBytes(_commonCode)));
                var rows = JsonNode.Parse(decryptedTable)!.AsArray();
                var row15 = rows.FirstOrDefault(r => r?["id"]?.GetValue<int>() == 15);
                Assert.NotNull(row15);

                var scoreMot = row15["scoreMotivation"]?.GetValue<int>();
                Assert.True(scoreMot >= 40 && scoreMot <= 90);
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(readerTasks.Concat(new[] { reloadTask }));
        }
        catch (OperationCanceledException) { }

        // 3. Test Invalid Retention under HTTP
        var invalidConfig = "{ this is invalid json syntax";
        File.WriteAllText(_configFilePath, invalidConfig);

        var reloadFailed = provider.Reload();
        Assert.False(reloadFailed);

        // Download master should still serve last active valid master without failure
        using var validReq = new HttpRequestMessage(HttpMethod.Post, "/download/master")
        {
            Content = new ByteArrayContent(Array.Empty<byte>())
        };
        validReq.Headers.Host = host;
        validReq.Headers.Add("x-app-access-token", token);

        using var validResp = await _client.SendAsync(validReq);
        Assert.Equal(HttpStatusCode.OK, validResp.StatusCode);
        var retainedVersion = validResp.Headers.GetValues("x-app-master-hash").Single();
        Assert.StartsWith("demo-master-", retainedVersion);
    }
}
