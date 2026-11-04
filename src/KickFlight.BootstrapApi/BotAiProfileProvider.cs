namespace KickFlight.BootstrapApi;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using KickFlight.BootstrapApi.Otomo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Bot AI tuning. It is game data an operator edits in otomo's admin UI as the published config namespace
/// <c>kf.bot_ai_profiles</c>; this provider follows the release through <see cref="OtomoMasters"/> (the namespace must be
/// listed in Otomo:Masters:Documents) and swaps in the new profiles when the release changes, the way the BattleRank
/// master follows <c>session.rank</c>. A document that fails validation is logged at Error and the last good profiles
/// keep serving.
/// </summary>
public sealed class BotAiProfileProvider : IBotAiProfileProvider
{
    public const string Namespace = "kf.bot_ai_profiles";

    // Local runs only (Otomo:Masters disabled): the tracked file, or this path.
    public const string ConfigPathEnvVar = "KF_BOT_AI_PROFILES_PATH";
    public const string ConfigFileName = "bot_ai_profiles.json";

    private readonly ILogger<BotAiProfileProvider> _logger;
    private readonly OtomoMasters? _masters;
    private readonly string _filePath;
    private readonly object _reloadLock = new();

    private BotAiProfileSnapshot _currentSnapshot;
    private string? _lastDocument;
    private bool _isUsingHardcodedFallback;
    private bool _disposed;

    public BotAiProfileSnapshot CurrentSnapshot => Volatile.Read(ref _currentSnapshot);
    public string Source => CurrentSnapshot.SourcePath;
    public bool IsUsingHardcodedFallback => _isUsingHardcodedFallback;

    public event Action<BotAiProfileSnapshot>? ProfilesReloaded;

    /// <param name="masters">The release client. When it is enabled the profiles come from the release; otherwise (a
    /// local run with no otomo) from the file.</param>
    public BotAiProfileProvider(
        OtomoMasters? masters = null,
        IWebHostEnvironment? environment = null,
        ILogger<BotAiProfileProvider>? logger = null,
        string? configPath = null,
        string? contentRoot = null)
    {
        _logger = logger ?? NullLogger<BotAiProfileProvider>.Instance;
        _masters = masters is { Enabled: true } ? masters : null;
        _filePath = ResolveConfigPath(configPath, contentRoot ?? environment?.ContentRootPath);

        _currentSnapshot = BotAiDefaults.CreateDefaultSnapshot(_masters is null ? _filePath : "hardcoded-defaults");
        _isUsingHardcodedFallback = true;

        if (_masters is not null)
        {
            _masters.Updated += OnReleaseUpdated;
        }

        if (_masters is not null && _masters.Source == "none")
        {
            // Built before the first fetch: OtomoMasters raises Updated when it has the release, and that loads the profiles.
            _logger.LogInformation("Bot AI profiles: waiting for the first {Namespace} release; the hardcoded defaults serve until then.", Namespace);
        }
        else if (!TryApply(force: true, out _))
        {
            _logger.LogWarning("Bot AI profiles could not be loaded from {Source} on startup; serving the hardcoded defaults.",
                _masters is null ? _filePath : Namespace);
        }
    }

    public static string ResolveBaseRoot(string? startingPath)
    {
        // 1. KF_REPO_ROOT environment variable (used in Docker / VPS container)
        var envRoot = Environment.GetEnvironmentVariable(RepositoryPaths.RootOverrideVariable);
        if (!string.IsNullOrWhiteSpace(envRoot) && Directory.Exists(envRoot))
        {
            return Path.GetFullPath(envRoot);
        }

        // If an explicit startingPath / contentRoot is given, strictly scope search to it and its ancestors.
        if (!string.IsNullOrWhiteSpace(startingPath))
        {
            var cur = new DirectoryInfo(Path.GetFullPath(startingPath));
            while (cur != null)
            {
                if (File.Exists(Path.Combine(cur.FullName, "KickFlight.PrivateServer.sln")) ||
                    Directory.Exists(Path.Combine(cur.FullName, ".local")) ||
                    File.Exists(Path.Combine(cur.FullName, "config", ConfigFileName)) ||
                    File.Exists(Path.Combine(cur.FullName, ConfigFileName)))
                {
                    return cur.FullName;
                }
                cur = cur.Parent;
            }
            return Path.GetFullPath(startingPath);
        }

        // Otherwise (ambient server mode without explicit contentRoot):
        // Upward directory walk for KickFlight.PrivateServer.sln from CurrentDirectory / AppContext
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var cur = new DirectoryInfo(Path.GetFullPath(start));
            while (cur != null)
            {
                if (File.Exists(Path.Combine(cur.FullName, "KickFlight.PrivateServer.sln")))
                {
                    return cur.FullName;
                }
                cur = cur.Parent;
            }
        }

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var cur = new DirectoryInfo(Path.GetFullPath(start));
            for (int i = 0; i < 5 && cur != null; i++)
            {
                if (Directory.Exists(Path.Combine(cur.FullName, ".local")) ||
                    File.Exists(Path.Combine(cur.FullName, "config", ConfigFileName)) ||
                    File.Exists(Path.Combine(cur.FullName, ConfigFileName)))
                {
                    return cur.FullName;
                }
                cur = cur.Parent;
            }
        }

        return AppContext.BaseDirectory;
    }

    public static string ResolveConfigPath(string? explicitPath, string? contentRoot)
    {
        var path = !string.IsNullOrWhiteSpace(explicitPath) ? explicitPath : Environment.GetEnvironmentVariable(ConfigPathEnvVar);
        var root = ResolveBaseRoot(contentRoot);
        return string.IsNullOrWhiteSpace(path)
            ? Path.GetFullPath(Path.Combine(root, "config", ConfigFileName))
            : Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(root, path));
    }

    // The release moved. A release that did not touch this document is the common case, and it must not rebuild the
    // KickerAiParameter master (and so change the client's master version) for nothing.
    private void OnReleaseUpdated() => TryApply(force: false, out _);

    public bool Reload() => TryApply(force: true, out _);

    // Reads the current document, validates it and swaps the snapshot. Any failure leaves the snapshot as it was.
    private bool TryApply(bool force, out BotAiProfileSnapshot? applied)
    {
        applied = null;
        lock (_reloadLock)
        {
            if (_disposed) return false;

            string source;
            string? content;
            if (_masters is not null)
            {
                source = $"otomo:{Namespace}@{_masters.Source}";
                if (!_masters.TryGetDocument(Namespace, out content))
                {
                    _logger.LogError(
                        "Bot AI profiles: the release carries no {Namespace} document. Add it to Otomo:Masters:Documents and publish it; keeping the current profiles ({Current}).",
                        Namespace, CurrentSnapshot.SourcePath);
                    return false;
                }
            }
            else
            {
                // Local runs only: with Otomo:Masters disabled there is no release, so the tracked file is read once.
                source = _filePath;
                content = ReadFile(_filePath);
                if (content is null) return false;
            }

            if (!force && string.Equals(content, _lastDocument, StringComparison.Ordinal)) return false;
            _lastDocument = content;

            var validation = BotAiConfigurationValidator.Validate(content);
            if (!validation.IsValid)
            {
                _logger.LogError(
                    "Bot AI profiles from {Source} failed validation; keeping the last good profiles ({Current}). Errors: {Errors}",
                    source, CurrentSnapshot.SourcePath, string.Join("; ", validation.Errors));
                return false;
            }

            var snapshot = validation.ToSnapshot(source);
            Interlocked.Exchange(ref _currentSnapshot, snapshot);
            _isUsingHardcodedFallback = false;
            applied = snapshot;
            _logger.LogInformation("Bot AI profiles loaded from {Source} (version {Version}, {Count} policies).",
                source, snapshot.Version, snapshot.AllPolicies.Count);

            if (ProfilesReloaded is not null)
            {
                foreach (Action<BotAiProfileSnapshot> handler in ProfilesReloaded.GetInvocationList())
                {
                    try
                    {
                        handler(snapshot);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error occurred in ProfilesReloaded handler '{HandlerMethod}'.", handler.Method.Name);
                    }
                }
            }
            return true;
        }
    }

    private string? ReadFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            if (!string.IsNullOrWhiteSpace(text)) return text;
            _logger.LogError("Bot AI profiles file '{Path}' is empty.", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Bot AI profiles file '{Path}' cannot be read: {Error}", path, ex.Message);
        }
        return null;
    }

    public AiRolePolicy? GetPolicy(int modeIndex, int profileIndex) => CurrentSnapshot.GetPolicy(modeIndex, profileIndex);
    public AiRolePolicy? GetPolicyByKicker(int modeIndex, int kickerId) => CurrentSnapshot.GetPolicyByKicker(modeIndex, kickerId);
    public AiRolePolicy? GetPolicy(string profile, int kickerId = 0, int modeIndex = 0) => CurrentSnapshot.GetPolicy(profile, kickerId, modeIndex);
    public AiRolePolicy? GetPolicy(string profile, int kickerId, BattleCompositionMode? mode) => CurrentSnapshot.GetPolicy(profile, kickerId, mode);
    public int ResolveParameterId(string? profile, int kickerId, BattleCompositionMode? mode = null, bool isGym = false) =>
        CurrentSnapshot.ResolveParameterId(profile, kickerId, mode, isGym);
    public int ResolveParameterId(string? profile, int kickerId, int battleRuleId, bool isGym = false) =>
        CurrentSnapshot.ResolveParameterId(profile, kickerId, battleRuleId, isGym);
    public IReadOnlyList<AiRolePolicy> GetAllPolicies() => CurrentSnapshot.AllPolicies;

    public void Dispose()
    {
        if (_masters is not null) _masters.Updated -= OnReleaseUpdated;
        lock (_reloadLock) _disposed = true;
    }
}
