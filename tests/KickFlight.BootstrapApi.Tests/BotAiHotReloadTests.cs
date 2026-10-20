namespace KickFlight.BootstrapApi.Tests;

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using KickFlight.BootstrapApi;
using Xunit;

[Collection("Matchmaking static state")]
public class BotAiHotReloadTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _validJson;

    public BotAiHotReloadTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bot-ai-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var repoRoot = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        var configPath = Path.Combine(repoRoot, "config", "bot_ai_profiles.json");
        _validJson = File.ReadAllText(configPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void Startup_MissingFile_FallsBackToHardcodedDefaults()
    {
        var missingPath = Path.Combine(_tempDir, "missing_profiles.json");
        using var provider = new BotAiProfileProvider(configPath: missingPath, enableWatcher: false);

        Assert.True(provider.IsUsingHardcodedFallback);
        Assert.NotNull(provider.CurrentSnapshot);
        Assert.Equal(39, provider.CurrentSnapshot.AllPolicies.Count);
        Assert.Equal(15, provider.CurrentSnapshot.AllPolicies.Min(p => p.ParameterId));
        Assert.Equal(53, provider.CurrentSnapshot.AllPolicies.Max(p => p.ParameterId));

        // Ana carrier (mode 0, prof 0) in defaults
        var ana = provider.GetPolicy(0, 0);
        Assert.NotNull(ana);
        Assert.Equal(15, ana.ParameterId);
        Assert.Equal(8, ana.KickerId);
        Assert.Equal(90, ana.ScoreMotivation);
    }

    [Fact]
    public void Startup_InvalidFile_FallsBackToHardcodedDefaults()
    {
        var invalidPath = Path.Combine(_tempDir, "invalid_profiles.json");
        File.WriteAllText(invalidPath, "{ not valid json syntax");

        using var provider = new BotAiProfileProvider(configPath: invalidPath, enableWatcher: false);

        Assert.True(provider.IsUsingHardcodedFallback);
        Assert.NotNull(provider.CurrentSnapshot);
        Assert.Equal(39, provider.CurrentSnapshot.AllPolicies.Count);
    }

    [Fact]
    public void Startup_ValidFile_LoadsSuccessfully()
    {
        var configPath = Path.Combine(_tempDir, "bot_ai_profiles.json");
        File.WriteAllText(configPath, _validJson);

        using var provider = new BotAiProfileProvider(configPath: configPath, enableWatcher: false);

        Assert.False(provider.IsUsingHardcodedFallback);
        Assert.Equal(39, provider.CurrentSnapshot.AllPolicies.Count);
        Assert.Equal(1, provider.CurrentSnapshot.Version);
    }

    [Fact]
    public void Reload_ValidModification_UpdatesSnapshotAndFiresEvent()
    {
        var configPath = Path.Combine(_tempDir, "bot_ai_profiles.json");
        File.WriteAllText(configPath, _validJson);

        using var provider = new BotAiProfileProvider(configPath: configPath, enableWatcher: false);
        Assert.Equal(90, provider.GetPolicy(0, 0)!.ScoreMotivation);

        // Modify scoreMotivation of carrier to 42 and version to 2
        var node = JsonNode.Parse(_validJson)!;
        node["version"] = 2;
        node["profiles"]![0]!["motivations"]!["score"] = 42;
        File.WriteAllText(configPath, node.ToJsonString());

        BotAiProfileSnapshot? reloadedSnapshot = null;
        provider.ProfilesReloaded += snapshot => reloadedSnapshot = snapshot;

        var success = provider.Reload();
        Assert.True(success);
        Assert.NotNull(reloadedSnapshot);
        Assert.Equal(2, provider.CurrentSnapshot.Version);
        Assert.Equal(42, provider.GetPolicy(0, 0)!.ScoreMotivation);
    }

    [Fact]
    public void Reload_InvalidModification_RetainsLastActiveValidSnapshot()
    {
        var configPath = Path.Combine(_tempDir, "bot_ai_profiles.json");
        File.WriteAllText(configPath, _validJson);

        using var provider = new BotAiProfileProvider(configPath: configPath, enableWatcher: false);
        var initialSnapshot = provider.CurrentSnapshot;
        Assert.Equal(90, provider.GetPolicy(0, 0)!.ScoreMotivation);

        // Corrupt file with invalid JSON
        File.WriteAllText(configPath, "{ broken json content");

        var success = provider.Reload();
        Assert.False(success);

        // MUST retain initial valid snapshot
        Assert.Same(initialSnapshot, provider.CurrentSnapshot);
        Assert.Equal(90, provider.GetPolicy(0, 0)!.ScoreMotivation);
        Assert.False(provider.IsUsingHardcodedFallback);
    }

    [Fact]
    public void Reload_DeletedFile_RetainsLastActiveValidSnapshot()
    {
        var configPath = Path.Combine(_tempDir, "bot_ai_profiles.json");
        File.WriteAllText(configPath, _validJson);

        using var provider = new BotAiProfileProvider(configPath: configPath, enableWatcher: false);
        var initialSnapshot = provider.CurrentSnapshot;

        // Delete file
        File.Delete(configPath);

        var success = provider.Reload();
        Assert.False(success);

        // MUST retain initial valid snapshot
        Assert.Same(initialSnapshot, provider.CurrentSnapshot);
        Assert.Equal(90, provider.GetPolicy(0, 0)!.ScoreMotivation);
    }

    [Fact]
    public void Reload_AtomicRename_UpdatesSnapshot()
    {
        var configPath = Path.Combine(_tempDir, "bot_ai_profiles.json");
        var tempFilePath = Path.Combine(_tempDir, "bot_ai_profiles.tmp");
        File.WriteAllText(configPath, _validJson);

        using var provider = new BotAiProfileProvider(configPath: configPath, enableWatcher: false);

        // Prepare new version in temp file
        var node = JsonNode.Parse(_validJson)!;
        node["version"] = 3;
        node["profiles"]![0]!["motivations"]!["score"] = 99;
        File.WriteAllText(tempFilePath, node.ToJsonString());

        // Atomic file move / rename
        File.Move(tempFilePath, configPath, overwrite: true);

        var success = provider.Reload();
        Assert.True(success);
        Assert.Equal(3, provider.CurrentSnapshot.Version);
        Assert.Equal(99, provider.GetPolicy(0, 0)!.ScoreMotivation);
    }

    [Fact]
    public void Reload_UnchangedTouch_RetainsValidCoherentSnapshot()
    {
        var configPath = Path.Combine(_tempDir, "bot_ai_profiles.json");
        File.WriteAllText(configPath, _validJson);

        using var provider = new BotAiProfileProvider(configPath: configPath, enableWatcher: false);

        // Touch file without changing contents
        File.SetLastWriteTimeUtc(configPath, DateTime.UtcNow.AddMinutes(1));

        var success = provider.Reload();
        Assert.True(success);
        Assert.Equal(1, provider.CurrentSnapshot.Version);
        Assert.Equal(90, provider.GetPolicy(0, 0)!.ScoreMotivation);
    }

    [Fact]
    public async Task Concurrency_ConcurrentReadersAndReloaders_ThreadSafeAndCoherent()
    {
        Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, "1");
        try
        {
            var configPath = Path.Combine(_tempDir, "bot_ai_profiles.json");
            File.WriteAllText(configPath, _validJson);

            using var provider = new BotAiProfileProvider(configPath: configPath, enableWatcher: false);
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            // 10 reader tasks constantly reading
            var readers = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var snapshot = provider.CurrentSnapshot;
                    Assert.NotNull(snapshot);
                    Assert.Equal(39, snapshot.AllPolicies.Count);

                    var policy = provider.GetPolicy(0, 0);
                    Assert.NotNull(policy);
                    Assert.True(policy.ParameterId >= 15 && policy.ParameterId <= 53);

                    var paramId = provider.ResolveParameterId("carrier", 8, BattleCompositionMode.CrystalScramble);
                    Assert.Equal(15, paramId);
                }
            }, cts.Token)).ToArray();

            // 2 writer tasks updating config and calling reload
            var writers = Enumerable.Range(0, 2).Select(writerId => Task.Run(async () =>
            {
                int ver = 1;
                while (!cts.Token.IsCancellationRequested)
                {
                    ver++;
                    var node = JsonNode.Parse(_validJson)!;
                    node["version"] = ver;
                    node["profiles"]![0]!["motivations"]!["attack"] = 30 + (ver % 40);
                    File.WriteAllText(configPath, node.ToJsonString());

                    provider.Reload();
                    await Task.Delay(20);
                }
            }, cts.Token)).ToArray();

            await Task.WhenAll(readers.Concat(writers));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, null);
        }
    }

    [Fact]
    public void Startup_FromPublishDirectory_LoadsConfigSuccessfullyWithoutSln()
    {
        // Simulate a publish directory (isolated from the repository root, no .sln)
        var publishDir = Path.Combine(_tempDir, "isolated-publish-output");
        var publishConfigDir = Path.Combine(publishDir, "config");
        Directory.CreateDirectory(publishConfigDir);

        var publishConfigFile = Path.Combine(publishConfigDir, "bot_ai_profiles.json");
        File.WriteAllText(publishConfigFile, _validJson);

        using var provider = new BotAiProfileProvider(configPath: null, contentRoot: publishDir, enableWatcher: false);

        Assert.False(provider.IsUsingHardcodedFallback);
        Assert.Equal(Path.GetFullPath(publishConfigFile), provider.ResolvedConfigPath);
        Assert.NotNull(provider.CurrentSnapshot);
        Assert.Equal(39, provider.CurrentSnapshot.AllPolicies.Count);
        Assert.Equal(1, provider.CurrentSnapshot.Version);
    }

    [Fact]
    public void ResolvePath_ResolvesCorrectlyAcrossPublishAndNestedDirectories()
    {
        var publishDir = Path.Combine(_tempDir, "publish-cwd-test");
        var configDir = Path.Combine(publishDir, "config");
        var nestedSubDir = Path.Combine(publishDir, "sub", "deep");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(nestedSubDir);

        var configFile = Path.Combine(configDir, "bot_ai_profiles.json");
        File.WriteAllText(configFile, _validJson);

        // 1. Explicit path parameter resolution
        var explicitResolved = BotAiProfileProvider.ResolvePath(configFile, null);
        Assert.Equal(Path.GetFullPath(configFile), explicitResolved);

        // 2. ContentRoot publish resolution
        var contentRootResolved = BotAiProfileProvider.ResolvePath(null, publishDir);
        Assert.Equal(Path.GetFullPath(configFile), contentRootResolved);

        // 3. Upward walk from nested subdirectory inside publish root
        var nestedResolved = BotAiProfileProvider.ResolvePath(null, nestedSubDir);
        Assert.Equal(Path.GetFullPath(configFile), nestedResolved);

        // 4. Environment variable override
        Environment.SetEnvironmentVariable(BotAiProfileProvider.ConfigPathEnvVar, configFile);
        try
        {
            var envResolved = BotAiProfileProvider.ResolvePath(null, "/nonexistent");
            Assert.Equal(Path.GetFullPath(configFile), envResolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(BotAiProfileProvider.ConfigPathEnvVar, null);
        }
    }

    [Fact]
    public void FeatureFlag_DefaultOff_PreservesPristineKickerId()
    {
        Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, null);
        var configPath = Path.Combine(_tempDir, "bot_ai_profiles.json");
        File.WriteAllText(configPath, _validJson);

        using var provider = new BotAiProfileProvider(configPath: configPath, enableWatcher: false);
        var resolvedParamId = provider.ResolveParameterId("carrier", 8, BattleCompositionMode.CrystalScramble, isGym: false);

        // Feature flag OFF returns pristine kickerId (8)
        Assert.Equal(8, resolvedParamId);

        // Gym parameter ID is ALWAYS preserved regardless of flag
        var gymParamId = provider.ResolveParameterId("carrier", 8, BattleCompositionMode.CrystalScramble, isGym: true);
        Assert.Equal(BattleMatchmakingService.GymAiParameterBase + 8, gymParamId);
        Assert.True(gymParamId >= 100);
    }

    [Fact]
    public void Reload_EventListenerException_DoesNotBlockOtherListenersOrReloadSuccess()
    {
        var configPath = Path.Combine(_tempDir, "bot_ai_profiles.json");
        File.WriteAllText(configPath, _validJson);

        using var provider = new BotAiProfileProvider(configPath: configPath, enableWatcher: false);

        bool secondListenerExecuted = false;

        // First listener intentionally throws
        provider.ProfilesReloaded += _ => throw new InvalidOperationException("Simulated listener exception");

        // Second listener should still execute safely
        provider.ProfilesReloaded += snapshot => secondListenerExecuted = true;

        var node = JsonNode.Parse(_validJson)!;
        node["version"] = 4;
        File.WriteAllText(configPath, node.ToJsonString());

        var success = provider.Reload();
        Assert.True(success);
        Assert.True(secondListenerExecuted);
        Assert.Equal(4, provider.CurrentSnapshot.Version);
    }

    [Fact]
    public void Watcher_Disposal_IsIdempotentAndThreadSafe()
    {
        var configPath = Path.Combine(_tempDir, "bot_ai_profiles.json");
        File.WriteAllText(configPath, _validJson);

        var provider = new BotAiProfileProvider(configPath: configPath, enableWatcher: true);

        // Multiple concurrent or sequential disposals must never throw
        provider.Dispose();
        provider.Dispose();
        provider.Dispose();

        Assert.False(provider.Reload());
    }

    [Fact]
    public void Reload_NonFiniteOrInvalidBounds_RetainsLastActiveValidSnapshot()
    {
        var configPath = Path.Combine(_tempDir, "bot_ai_profiles.json");
        File.WriteAllText(configPath, _validJson);

        using var provider = new BotAiProfileProvider(configPath: configPath, enableWatcher: false);
        var initialSnapshot = provider.CurrentSnapshot;

        // Try reload with negative interval
        var node = JsonNode.Parse(_validJson)!;
        node["profiles"]![0]!["dashIntervals"]!["attack"] = -1.5;
        File.WriteAllText(configPath, node.ToJsonString());

        var success = provider.Reload();
        Assert.False(success);
        Assert.Same(initialSnapshot, provider.CurrentSnapshot);
    }
}
