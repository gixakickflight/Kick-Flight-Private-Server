namespace KickFlight.BootstrapApi.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using KickFlight.BootstrapApi;
using KickFlight.BootstrapApi.Otomo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

// Bot AI tuning is the published config namespace kf.bot_ai_profiles. These tests run the provider against a fake
// release bundle (FakeOtomoSocial serves it exactly as otomo does) through a real OtomoMasters client.
[Collection("Matchmaking static state")]
public class BotAiHotReloadTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _validJson;
    private readonly FakeOtomoSocial _otomo;
    private readonly List<OtomoMasters> _clients = new();

    public BotAiHotReloadTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bot-ai-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        // The tenant fork's document (kf/tenant/documents/kf.bot_ai_profiles.json), copied in as a fixture.
        _validJson = FakeOtomoSocial.DefaultBotAiDocument();
        _otomo = FakeOtomoSocial.Start();
    }

    public void Dispose()
    {
        foreach (var client in _clients) client.Dispose();
        _otomo.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    private OtomoMasters Masters(bool enabled = true)
    {
        var client = OtomoMasters.Create(Options.Create(new OtomoMastersOptions
        {
            Enabled = enabled,
            BaseUrl = _otomo.BaseUrl,
            Key = FakeOtomoSocial.ServiceKey,
            Refresh = TimeSpan.Zero,
            Documents = new[] { "session.rank", BotAiProfileProvider.Namespace },
            CacheDir = Path.Combine(_tempDir, "cache-" + _clients.Count),
        }), TimeProvider.System, NullLogger<OtomoMasters>.Instance);
        _clients.Add(client);
        return client;
    }

    private async Task<(OtomoMasters Masters, BotAiProfileProvider Provider, CapturingLogger Log)> FollowAsync()
    {
        var masters = Masters();
        await masters.LoadInitialAsync(CancellationToken.None);
        var log = new CapturingLogger();
        return (masters, new BotAiProfileProvider(masters, logger: log), log);
    }

    private string Edited(int version, int anaScore)
    {
        var node = JsonNode.Parse(_validJson)!;
        node["version"] = version;
        node["profiles"]![0]!["motivations"]!["score"] = anaScore;
        return node.ToJsonString();
    }

    private void Publish(string release, string? document)
    {
        _otomo.Release = release;
        _otomo.BotAiDocument = document;
    }

    // --- the fork's document -----------------------------------------------------------------------------------------

    [Fact]
    public void ForkDocument_Validates_AndBuildsTheFullSnapshot()
    {
        var result = BotAiConfigurationValidator.Validate(_validJson);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        var snapshot = result.ToSnapshot("fixture");
        Assert.Equal(42, snapshot.AllPolicies.Count);
        Assert.Equal(15, snapshot.AllPolicies.Min(p => p.ParameterId));
        Assert.Equal(71, snapshot.AllPolicies.Max(p => p.ParameterId));
    }

    [Fact]
    public async Task ForkDocument_ComesThroughTheRelease_AndLoads()
    {
        var (_, provider, _) = await FollowAsync();
        using var _disposeProvider = provider;

        Assert.False(provider.IsUsingHardcodedFallback);
        Assert.StartsWith("otomo:kf.bot_ai_profiles@", provider.Source);
        Assert.Equal(42, provider.CurrentSnapshot.AllPolicies.Count);
        var ana = provider.GetPolicy(0, 0);
        Assert.NotNull(ana);
        Assert.Equal(15, ana.ParameterId);
        Assert.Equal(8, ana.KickerId);
        Assert.Equal(90, ana.ScoreMotivation);
    }

    // --- profiles come from the release; a new release reloads them -------------------------------------------------

    [Fact]
    public async Task NewRelease_ReloadsProfilesAndFiresEvent()
    {
        var (masters, provider, _) = await FollowAsync();
        using var _disposeProvider = provider;
        var fired = new List<BotAiProfileSnapshot>();
        provider.ProfilesReloaded += fired.Add;

        Publish("test-release-2", Edited(version: 2, anaScore: 45));
        Assert.True(await masters.RefreshAsync(CancellationToken.None));

        Assert.Equal(2, provider.CurrentSnapshot.Version);
        Assert.Equal(45, provider.GetPolicy(0, 0)!.ScoreMotivation);
        Assert.Single(fired);
        Assert.Same(provider.CurrentSnapshot, fired[0]);
        Assert.Contains("test-release-2", provider.Source);
    }

    [Fact]
    public async Task NewRelease_ThatLeavesTheDocumentAlone_DoesNotReload()
    {
        var (masters, provider, _) = await FollowAsync();
        using var _disposeProvider = provider;
        var before = provider.CurrentSnapshot;
        var fired = 0;
        provider.ProfilesReloaded += _ => fired++;

        Publish("test-release-3", _validJson);
        await masters.RefreshAsync(CancellationToken.None);

        Assert.Equal(0, fired);
        Assert.Same(before, provider.CurrentSnapshot);
    }

    // --- a bad document keeps the last good one -----------------------------------------------------------------------

    [Fact]
    public async Task BadDocument_KeepsLastGoodProfiles_LogsError_AndRecoversOnTheNextGoodRelease()
    {
        var (masters, provider, log) = await FollowAsync();
        using var _disposeProvider = provider;
        Publish("r2", Edited(version: 2, anaScore: 45));
        await masters.RefreshAsync(CancellationToken.None);
        var good = provider.CurrentSnapshot;
        var fired = 0;
        provider.ProfilesReloaded += _ => fired++;

        // Valid JSON, but rank must be 13: OtomoMasters passes it on and the provider's validation refuses it.
        var node = JsonNode.Parse(Edited(version: 3, anaScore: 10))!;
        node["profiles"]![0]!["rank"] = 12;
        Publish("r3", node.ToJsonString());
        await masters.RefreshAsync(CancellationToken.None);

        Assert.Same(good, provider.CurrentSnapshot);
        Assert.Equal(45, provider.GetPolicy(0, 0)!.ScoreMotivation);
        Assert.Equal(0, fired);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("failed validation"));

        // Not JSON at all: OtomoMasters refuses the whole bundle, so nothing changes either.
        Publish("r4", "{ this is not json");
        await masters.RefreshAsync(CancellationToken.None);
        Assert.Same(good, provider.CurrentSnapshot);

        Publish("r5", Edited(version: 5, anaScore: 33));
        await masters.RefreshAsync(CancellationToken.None);
        Assert.Equal(5, provider.CurrentSnapshot.Version);
        Assert.Equal(33, provider.GetPolicy(0, 0)!.ScoreMotivation);
        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task BadDocumentAtStartup_ServesHardcodedDefaults_AndLogsError()
    {
        var node = JsonNode.Parse(_validJson)!;
        node["profiles"]![0]!["rank"] = 12;
        Publish("bad-start", node.ToJsonString());

        var (masters, provider, log) = await FollowAsync();
        using var _disposeProvider = provider;

        Assert.True(provider.IsUsingHardcodedFallback);
        Assert.Equal(42, provider.CurrentSnapshot.AllPolicies.Count);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error);

        Publish("fixed", _validJson);
        await masters.RefreshAsync(CancellationToken.None);
        Assert.False(provider.IsUsingHardcodedFallback);
    }

    [Fact]
    public async Task ReleaseWithoutTheDocument_KeepsLastGoodProfiles()
    {
        var (masters, provider, _) = await FollowAsync();
        using var _disposeProvider = provider;
        var good = provider.CurrentSnapshot;

        // OtomoMasters refuses a release that lacks a namespace it was told to follow, so the provider never sees it.
        Publish("no-bot-ai", null);
        Assert.False(await masters.RefreshAsync(CancellationToken.None));

        Assert.Same(good, provider.CurrentSnapshot);
    }

    // --- masters disabled: the file, for local runs only ------------------------------------------------------------

    [Fact]
    public void MastersDisabled_ReadsTheFile_AndIgnoresTheRelease()
    {
        var path = Path.Combine(_tempDir, "bot_ai_profiles.json");
        File.WriteAllText(path, Edited(version: 7, anaScore: 12));
        Publish("ignored", Edited(version: 99, anaScore: 99));

        using var provider = new BotAiProfileProvider(Masters(enabled: false), configPath: path);

        Assert.False(provider.IsUsingHardcodedFallback);
        Assert.Equal(path, provider.Source);
        Assert.Equal(7, provider.CurrentSnapshot.Version);
        Assert.Equal(12, provider.GetPolicy(0, 0)!.ScoreMotivation);
    }

    [Fact]
    public void MastersDisabled_NoMastersAtAll_ReadsTheFileToo()
    {
        var path = Path.Combine(_tempDir, "bot_ai_profiles.json");
        File.WriteAllText(path, _validJson);

        using var provider = new BotAiProfileProvider(configPath: path);

        Assert.False(provider.IsUsingHardcodedFallback);
        Assert.Equal(42, provider.CurrentSnapshot.AllPolicies.Count);
    }

    [Fact]
    public void MastersDisabled_MissingOrInvalidFile_FallsBackToDefaults_AndInvalidReloadKeepsLastGood()
    {
        using (var missing = new BotAiProfileProvider(configPath: Path.Combine(_tempDir, "missing.json")))
        {
            Assert.True(missing.IsUsingHardcodedFallback);
            Assert.Equal(42, missing.CurrentSnapshot.AllPolicies.Count);
            Assert.Equal(90, missing.GetPolicy(0, 0)!.ScoreMotivation);
        }

        var path = Path.Combine(_tempDir, "bot_ai_profiles.json");
        File.WriteAllText(path, "{ not json");
        using var invalid = new BotAiProfileProvider(configPath: path);
        Assert.True(invalid.IsUsingHardcodedFallback);

        File.WriteAllText(path, Edited(version: 2, anaScore: 44));
        Assert.True(invalid.Reload());
        var good = invalid.CurrentSnapshot;

        File.WriteAllText(path, "{ not json");
        Assert.False(invalid.Reload());
        File.Delete(path);
        Assert.False(invalid.Reload());
        Assert.Same(good, invalid.CurrentSnapshot);
    }

    // --- behaviour that does not depend on where the document comes from ---------------------------------------------

    [Fact]
    public void Reload_EventListenerException_DoesNotBlockOtherListenersOrReloadSuccess()
    {
        var path = Path.Combine(_tempDir, "bot_ai_profiles.json");
        File.WriteAllText(path, _validJson);
        using var provider = new BotAiProfileProvider(configPath: path);

        var secondListenerExecuted = false;
        provider.ProfilesReloaded += _ => throw new InvalidOperationException("Simulated listener exception");
        provider.ProfilesReloaded += _ => secondListenerExecuted = true;

        File.WriteAllText(path, Edited(version: 4, anaScore: 50));

        Assert.True(provider.Reload());
        Assert.True(secondListenerExecuted);
        Assert.Equal(4, provider.CurrentSnapshot.Version);
    }

    [Fact]
    public void FeatureFlag_DefaultOff_PreservesPristineKickerId()
    {
        Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, null);
        var path = Path.Combine(_tempDir, "bot_ai_profiles.json");
        File.WriteAllText(path, _validJson);

        using var provider = new BotAiProfileProvider(configPath: path);

        Assert.Equal(8, provider.ResolveParameterId("carrier", 8, BattleCompositionMode.CrystalScramble, isGym: false));
        var gymParamId = provider.ResolveParameterId("carrier", 8, BattleCompositionMode.CrystalScramble, isGym: true);
        Assert.Equal(BattleMatchmakingService.GymAiParameterBase + 8, gymParamId);
        Assert.True(gymParamId >= 100);
    }

    [Fact]
    public async Task Concurrency_ConcurrentReadersAndReleases_ThreadSafeAndCoherent()
    {
        Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, "1");
        try
        {
            var (masters, provider, _) = await FollowAsync();
            using var _disposeProvider = provider;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            var readers = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    Assert.Equal(42, provider.CurrentSnapshot.AllPolicies.Count);
                    Assert.Equal(15, provider.GetPolicy(0, 0)!.ParameterId);
                    Assert.Equal(15, provider.ResolveParameterId("carrier", 8, BattleCompositionMode.CrystalScramble));
                }
            })).ToArray();

            var publisher = Task.Run(async () =>
            {
                for (var version = 2; !cts.Token.IsCancellationRequested; version++)
                {
                    Publish("r" + version, Edited(version, 30 + version % 40));
                    await masters.RefreshAsync(CancellationToken.None);
                }
            });

            await Task.WhenAll(readers.Append(publisher));
            Assert.True(provider.CurrentSnapshot.Version >= 2);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, null);
        }
    }

    [Fact]
    public void MasterOverrides_SafelyIgnoresBotAiProfiles()
    {
        var overridesDir = Path.Combine(_tempDir, "master-overrides-check");
        Directory.CreateDirectory(overridesDir);
        File.WriteAllText(Path.Combine(overridesDir, "bot_ai_profiles.json"), _validJson);
        File.WriteAllText(Path.Combine(overridesDir, "masters_kicker.json"), "[{\"id\":1}]");

        var masterOverrides = new MasterOverrides(overridesDir, _tempDir);

        Assert.Null(masterOverrides.Find("bot_ai_profiles.json"));
        Assert.DoesNotContain("bot_ai_profiles.json", masterOverrides.Applied);
        Assert.NotNull(masterOverrides.Find("masters_kicker.json"));
        Assert.Contains("masters_kicker.json", masterOverrides.Applied);
    }

    private sealed class CapturingLogger : ILogger<BotAiProfileProvider>
    {
        public record Entry(LogLevel Level, string Message);

        private readonly List<Entry> _entries = new();
        public IReadOnlyList<Entry> Entries { get { lock (_entries) return _entries.ToArray(); } }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add(new Entry(logLevel, formatter(state, exception)));
        }
    }
}
