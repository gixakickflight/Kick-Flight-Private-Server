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
        Assert.Equal(42, provider.CurrentSnapshot.AllPolicies.Count);
        Assert.Equal(15, provider.CurrentSnapshot.AllPolicies.Min(p => p.ParameterId));
        Assert.Equal(71, provider.CurrentSnapshot.AllPolicies.Max(p => p.ParameterId));

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
        Assert.Equal(42, provider.CurrentSnapshot.AllPolicies.Count);
    }

    [Fact]
    public void Startup_ValidFile_LoadsSuccessfully()
    {
        var configPath = Path.Combine(_tempDir, "bot_ai_profiles.json");
        File.WriteAllText(configPath, _validJson);

        using var provider = new BotAiProfileProvider(configPath: configPath, enableWatcher: false);

        Assert.False(provider.IsUsingHardcodedFallback);
        Assert.Equal(42, provider.CurrentSnapshot.AllPolicies.Count);
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
                    Assert.Equal(42, snapshot.AllPolicies.Count);

                    var policy = provider.GetPolicy(0, 0);
                    Assert.NotNull(policy);
                    Assert.True(policy.ParameterId >= 15 && policy.ParameterId <= 71);

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
        Assert.Equal(Path.GetFullPath(Path.Combine(publishDir, ".local", "masters-overrides", "bot_ai_profiles.json")), provider.ResolvedConfigPath);
        Assert.Equal(Path.GetFullPath(publishConfigFile), provider.CurrentSnapshot.SourcePath);
        Assert.NotNull(provider.CurrentSnapshot);
        Assert.Equal(42, provider.CurrentSnapshot.AllPolicies.Count);
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

        // 2. ContentRoot publish resolution (durable override target)
        var expectedDurable = Path.GetFullPath(Path.Combine(publishDir, ".local", "masters-overrides", "bot_ai_profiles.json"));
        var contentRootResolved = BotAiProfileProvider.ResolveDurablePath(null, publishDir);
        Assert.Equal(expectedDurable, contentRootResolved);

        // 3. Upward walk from nested subdirectory inside publish root
        var nestedResolved = BotAiProfileProvider.ResolveDurablePath(null, nestedSubDir);
        Assert.Equal(expectedDurable, nestedResolved);

        // 4. Seed resolution across publish and nested directories
        Assert.Equal(Path.GetFullPath(configFile), BotAiProfileProvider.ResolveSeedPath(null, publishDir));
        Assert.Equal(Path.GetFullPath(configFile), BotAiProfileProvider.ResolveSeedPath(null, nestedSubDir));

        // 5. Environment variable override
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

    [Fact]
    public async Task Startup_SeedFallback_FirstSaveDurableOverride_ReloadsSuccessfully()
    {
        var repoDir = Path.Combine(_tempDir, "seed-first-save-test");
        var configDir = Path.Combine(repoDir, "config");
        Directory.CreateDirectory(configDir);

        var seedFile = Path.Combine(configDir, "bot_ai_profiles.json");
        File.WriteAllText(seedFile, _validJson);

        using var provider = new BotAiProfileProvider(configPath: null, contentRoot: repoDir, enableWatcher: true, debounceMs: 50);

        Assert.False(provider.IsUsingHardcodedFallback);
        Assert.Equal(1, provider.CurrentSnapshot.Version);
        Assert.Equal(Path.GetFullPath(seedFile), provider.CurrentSnapshot.SourcePath);
        Assert.Equal(90, provider.GetPolicy(0, 0)!.ScoreMotivation);

        var durableExpected = Path.GetFullPath(Path.Combine(repoDir, ".local", "masters-overrides", "bot_ai_profiles.json"));
        Assert.Equal(durableExpected, provider.ResolvedConfigPath);

        // First save creates durable override
        var node = JsonNode.Parse(_validJson)!;
        node["version"] = 2;
        node["profiles"]![0]!["motivations"]!["score"] = 42;

        var overridesDir = Path.Combine(repoDir, ".local", "masters-overrides");
        Directory.CreateDirectory(overridesDir);
        File.WriteAllText(durableExpected, node.ToJsonString());

        // Wait for debounce and verify hot-reload from durable target
        var reloaded = false;
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            if (provider.CurrentSnapshot.Version == 2 && provider.GetPolicy(0, 0)!.ScoreMotivation == 42)
            {
                reloaded = true;
                break;
            }
            await Task.Delay(50);
        }

        if (!reloaded)
        {
            // If watcher was delayed, explicit Reload must succeed from durable target
            provider.Reload();
        }

        Assert.Equal(2, provider.CurrentSnapshot.Version);
        Assert.Equal(42, provider.GetPolicy(0, 0)!.ScoreMotivation);
        Assert.Equal(durableExpected, provider.CurrentSnapshot.SourcePath);
        // Ensure seed was NOT rewritten
        Assert.Equal(90, JsonNode.Parse(File.ReadAllText(seedFile))!["profiles"]![0]!["motivations"]!["score"]!.GetValue<int>());
    }

    [Fact]
    public async Task Startup_MissingDirectories_ExternalHostCreation_ReloadsSuccessfully()
    {
        var repoDir = Path.Combine(_tempDir, "missing-dirs-test");
        var configDir = Path.Combine(repoDir, "config");
        Directory.CreateDirectory(configDir);

        var seedFile = Path.Combine(configDir, "bot_ai_profiles.json");
        File.WriteAllText(seedFile, _validJson);

        // Notice: .local directory does NOT exist at all
        var localDir = Path.Combine(repoDir, ".local");
        Assert.False(Directory.Exists(localDir));

        using var provider = new BotAiProfileProvider(configPath: null, contentRoot: repoDir, enableWatcher: true, debounceMs: 50);

        // Provider must not create .local itself (supports read-only mount layout)
        Assert.False(Directory.Exists(localDir));
        Assert.False(provider.IsUsingHardcodedFallback);
        Assert.Equal(1, provider.CurrentSnapshot.Version);

        // Host / Balance UI externally creates the directory and file atomically
        var overridesDir = Path.Combine(repoDir, ".local", "masters-overrides");
        Directory.CreateDirectory(overridesDir);

        var node = JsonNode.Parse(_validJson)!;
        node["version"] = 7;
        node["profiles"]![0]!["motivations"]!["attack"] = 99;

        var durableFile = Path.Combine(overridesDir, "bot_ai_profiles.json");
        var tempFile = Path.Combine(overridesDir, "bot_ai_profiles.tmp");
        File.WriteAllText(tempFile, node.ToJsonString());
        File.Move(tempFile, durableFile);

        var reloaded = false;
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            if (provider.CurrentSnapshot.Version == 7)
            {
                reloaded = true;
                break;
            }
            await Task.Delay(50);
        }

        if (!reloaded)
        {
            provider.Reload();
        }

        Assert.Equal(7, provider.CurrentSnapshot.Version);
        Assert.Equal(99, provider.GetPolicy(0, 0)!.AttackMotivation);
    }

    [Fact]
    public void Reload_InvalidRetention_And_DeletionRetention()
    {
        var repoDir = Path.Combine(_tempDir, "retention-test");
        var overridesDir = Path.Combine(repoDir, ".local", "masters-overrides");
        Directory.CreateDirectory(overridesDir);

        var node = JsonNode.Parse(_validJson)!;
        node["version"] = 10;
        node["profiles"]![0]!["motivations"]!["score"] = 55;

        var durableFile = Path.Combine(overridesDir, "bot_ai_profiles.json");
        File.WriteAllText(durableFile, node.ToJsonString());

        using var provider = new BotAiProfileProvider(configPath: null, contentRoot: repoDir, enableWatcher: false);
        Assert.Equal(10, provider.CurrentSnapshot.Version);
        Assert.Equal(55, provider.GetPolicy(0, 0)!.ScoreMotivation);

        var initial = provider.CurrentSnapshot;

        // 1. Corrupt the file
        File.WriteAllText(durableFile, "{ CORRUPT_JSON");
        Assert.False(provider.Reload());
        Assert.Same(initial, provider.CurrentSnapshot);
        Assert.Equal(10, provider.CurrentSnapshot.Version);

        // 2. Delete the file
        File.Delete(durableFile);
        Assert.False(provider.Reload());
        Assert.Same(initial, provider.CurrentSnapshot);
        Assert.Equal(10, provider.CurrentSnapshot.Version);
    }

    [Fact]
    public void Startup_MissingDurableAndSeed_DefaultsUnchanged()
    {
        var emptyDir = Path.Combine(_tempDir, "empty-defaults-test");
        Directory.CreateDirectory(emptyDir);

        using var provider = new BotAiProfileProvider(configPath: null, contentRoot: emptyDir, enableWatcher: false);

        Assert.True(provider.IsUsingHardcodedFallback);
        Assert.Equal(42, provider.CurrentSnapshot.AllPolicies.Count);
        Assert.Equal(15, provider.CurrentSnapshot.AllPolicies.Min(p => p.ParameterId));
        Assert.Equal(71, provider.CurrentSnapshot.AllPolicies.Max(p => p.ParameterId));
        Assert.Equal(1, provider.CurrentSnapshot.Version);
    }

    [Fact]
    public void DeploySimulation_RewritingSeedPreservesDurableOverride()
    {
        var repoDir = Path.Combine(_tempDir, "deploy-sim-test");
        var configDir = Path.Combine(repoDir, "config");
        var overridesDir = Path.Combine(repoDir, ".local", "masters-overrides");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(overridesDir);

        var seedFile = Path.Combine(configDir, "bot_ai_profiles.json");
        File.WriteAllText(seedFile, _validJson);

        // Durable override has custom tuned values (score = 42, version = 3)
        var overrideNode = JsonNode.Parse(_validJson)!;
        overrideNode["version"] = 3;
        overrideNode["profiles"]![0]!["motivations"]!["score"] = 42;
        var durableFile = Path.Combine(overridesDir, "bot_ai_profiles.json");
        File.WriteAllText(durableFile, overrideNode.ToJsonString());

        using var provider = new BotAiProfileProvider(configPath: null, contentRoot: repoDir, enableWatcher: false);
        Assert.Equal(3, provider.CurrentSnapshot.Version);
        Assert.Equal(42, provider.GetPolicy(0, 0)!.ScoreMotivation);

        // Simulate deploy: CI rsync overwrites config/ with fresh seed (version 1, score = 90)
        File.WriteAllText(seedFile, _validJson);

        // Reload provider: must still preserve durable override
        Assert.True(provider.Reload());
        Assert.Equal(3, provider.CurrentSnapshot.Version);
        Assert.Equal(42, provider.GetPolicy(0, 0)!.ScoreMotivation);

        // Simulate restart: new instance must also load durable override
        using var restartedProvider = new BotAiProfileProvider(configPath: null, contentRoot: repoDir, enableWatcher: false);
        Assert.Equal(3, restartedProvider.CurrentSnapshot.Version);
        Assert.Equal(42, restartedProvider.GetPolicy(0, 0)!.ScoreMotivation);
    }

    [Fact]
    public void MasterOverrides_SafelyIgnoresBotAiProfiles()
    {
        var overridesDir = Path.Combine(_tempDir, "master-overrides-check");
        Directory.CreateDirectory(overridesDir);

        // Place bot_ai_profiles.json and masters_kicker.json in the override directory
        var botAiFile = Path.Combine(overridesDir, "bot_ai_profiles.json");
        File.WriteAllText(botAiFile, _validJson);

        var kickerMasterFile = Path.Combine(overridesDir, "masters_kicker.json");
        File.WriteAllText(kickerMasterFile, "[{\"id\":1}]");

        var masterOverrides = new MasterOverrides(overridesDir, _tempDir);

        // Must safely ignore bot_ai_profiles.json
        Assert.Null(masterOverrides.Find("bot_ai_profiles.json"));
        Assert.Null(masterOverrides.Find(".local/masters-overrides/bot_ai_profiles.json"));
        Assert.DoesNotContain("bot_ai_profiles.json", masterOverrides.Applied);

        // Must recognize valid masters_*.json
        var kickerPath = masterOverrides.Find("masters_kicker.json");
        Assert.NotNull(kickerPath);
        Assert.Contains("masters_kicker.json", masterOverrides.Applied);
    }

    [Fact]
    public async Task ActualPythonAuthenticatedHttpSave_ConsumedByCSharpProvider_ViaSharedTempRepo()
    {
        var repoRoot = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        var sharedDir = Path.Combine(_tempDir, "py-csharp-shared");
        var configDir = Path.Combine(sharedDir, "config");
        var localDir = Path.Combine(sharedDir, ".local");
        var overridesDir = Path.Combine(localDir, "masters-overrides");
        var authDir = Path.Combine(localDir, "balance-auth");
        var backupsDir = Path.Combine(localDir, "balance-backups");

        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(authDir);
        Directory.CreateDirectory(backupsDir);

        // Copy seed config and masters so balance server starts cleanly
        var seedFile = Path.Combine(configDir, "bot_ai_profiles.json");
        File.WriteAllText(seedFile, _validJson);
        foreach (var m in Directory.GetFiles(Path.Combine(repoRoot, "config"), "masters_*.json"))
        {
            File.Copy(m, Path.Combine(configDir, Path.GetFileName(m)));
        }

        // Initialize auth db and jwt-secret via python script
        var port = 18895;
        var initAuthScript = $"""
import os, sqlite3, time, sys
auth_dir = '{authDir}'
os.chmod(auth_dir, 0o700)
sys.path.insert(0, '{Path.Combine(repoRoot, "tools", "balance")}')
from auth import hash_password
jwt_path = '{Path.Combine(authDir, "jwt-secret")}'
with open(jwt_path, 'wb') as f:
    f.write(os.urandom(32))
os.chmod(jwt_path, 0o600)
db_path = '{Path.Combine(authDir, "auth.sqlite3")}'
with sqlite3.connect(db_path) as conn:
    conn.execute('''CREATE TABLE IF NOT EXISTS users (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        username TEXT UNIQUE NOT NULL COLLATE NOCASE,
        password_hash TEXT NOT NULL,
        created_at INTEGER NOT NULL,
        locked_at INTEGER
    )''')
    conn.execute('INSERT INTO users(username, password_hash, created_at) VALUES (?, ?, ?)',
                 ('testoperator', hash_password('securepassword123'), int(time.time())))
""";
        var pyInitProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("python3", $"-c \"{initAuthScript}\"")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        });
        await pyInitProc!.WaitForExitAsync();
        Assert.Equal(0, pyInitProc.ExitCode);

        // 1. C# provider starts on seed before durable override exists
        using var provider = new BotAiProfileProvider(configPath: null, contentRoot: sharedDir, enableWatcher: true, debounceMs: 50);
        Assert.False(provider.IsUsingHardcodedFallback);
        Assert.Equal(1, provider.CurrentSnapshot.Version);
        Assert.Equal(90, provider.GetPolicy(0, 0)!.ScoreMotivation);
        Assert.Equal(Path.GetFullPath(seedFile), provider.CurrentSnapshot.SourcePath);
        Assert.False(Directory.Exists(overridesDir));

        // 2. Start Python balance server
        var serverScript = Path.Combine(repoRoot, "tools", "balance", "server.py");
        var psi = new System.Diagnostics.ProcessStartInfo("python3", $"{serverScript} --host 127.0.0.1 --port {port}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        psi.Environment["KF_BALANCE_BASE_DIR"] = configDir;
        psi.Environment["KF_BALANCE_OVERRIDE_DIR"] = overridesDir;
        psi.Environment["KF_BALANCE_BACKUP_DIR"] = backupsDir;
        psi.Environment["KF_BALANCE_AUTH_DB"] = Path.Combine(authDir, "auth.sqlite3");
        psi.Environment["KF_BALANCE_JWT_SECRET_FILE"] = Path.Combine(authDir, "jwt-secret");
        psi.Environment["KF_BALANCE_BASE_PATH"] = "/balance";
        psi.Environment["KF_BALANCE_ALLOW_APPLY"] = "0";

        var pyServer = System.Diagnostics.Process.Start(psi);
        Assert.NotNull(pyServer);

        try
        {
            using var http = new System.Net.Http.HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            // Wait for server ready
            var ready = false;
            for (int i = 0; i < 50; i++)
            {
                try
                {
                    var r = await http.GetAsync("/balance/login");
                    if (r.IsSuccessStatusCode) { ready = true; break; }
                }
                catch { }
                await Task.Delay(100);
            }
            Assert.True(ready, "Python balance server failed to bind in time");

            // Login
            var loginContent = new System.Net.Http.StringContent(
                "{\"username\":\"testoperator\",\"password\":\"securepassword123\"}",
                System.Text.Encoding.UTF8,
                "application/json");
            loginContent.Headers.Add("Origin", $"http://127.0.0.1:{port}");
            var loginResp = await http.PostAsync("/balance/api/login", loginContent);
            Assert.True(loginResp.IsSuccessStatusCode);
            var setCookie = loginResp.Headers.GetValues("Set-Cookie").First();
            var sessionCookie = setCookie.Split(';', 2)[0];

            // GET bot-ai profiles to retrieve seed hash
            var getReq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "/balance/api/bot-ai/profiles");
            getReq.Headers.Add("Cookie", sessionCookie);
            var getResp = await http.SendAsync(getReq);
            Assert.True(getResp.IsSuccessStatusCode);
            var getJson = JsonNode.Parse(await getResp.Content.ReadAsStringAsync())!;
            Assert.Equal("seed", getJson["source"]?.ToString());
            var seedHash = getJson["hash"]?.ToString();
            Assert.NotNull(seedHash);

            // POST save with modified motivation (attack = 88, version = 8)
            var postPayload = JsonNode.Parse(_validJson)!;
            postPayload["version"] = 8;
            postPayload["expectedHash"] = seedHash;
            postPayload["profiles"]![0]!["motivations"]!["attack"] = 88;

            var postReq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, "/balance/api/bot-ai/profiles")
            {
                Content = new System.Net.Http.StringContent(postPayload.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
            };
            postReq.Headers.Add("Cookie", sessionCookie);
            postReq.Headers.Add("Origin", $"http://127.0.0.1:{port}");
            var postResp = await http.SendAsync(postReq);
            Assert.True(postResp.IsSuccessStatusCode);
            var postRespJson = JsonNode.Parse(await postResp.Content.ReadAsStringAsync())!;
            Assert.Equal("override", postRespJson["source"]?.ToString());

            // 3. Verify C# provider hot-reloads the Python HTTP save from .local/masters-overrides/bot_ai_profiles.json
            var deadline = DateTime.UtcNow.AddSeconds(4);
            var consumed = false;
            while (DateTime.UtcNow < deadline)
            {
                if (provider.CurrentSnapshot.Version == 8 && provider.GetPolicy(0, 0)!.AttackMotivation == 88)
                {
                    consumed = true;
                    break;
                }
                await Task.Delay(50);
            }

            if (!consumed)
            {
                provider.Reload();
            }

            Assert.Equal(8, provider.CurrentSnapshot.Version);
            Assert.Equal(88, provider.GetPolicy(0, 0)!.AttackMotivation);
            Assert.Equal(Path.GetFullPath(Path.Combine(overridesDir, "bot_ai_profiles.json")), provider.CurrentSnapshot.SourcePath);
        }
        finally
        {
            try
            {
                if (pyServer != null && !pyServer.HasExited)
                {
                    pyServer.Kill(entireProcessTree: true);
                    pyServer.WaitForExit(1000);
                }
            }
            catch { }
        }
    }
}
