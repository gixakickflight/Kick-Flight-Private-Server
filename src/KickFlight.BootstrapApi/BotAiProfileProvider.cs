namespace KickFlight.BootstrapApi;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class BotAiProfileProvider : IBotAiProfileProvider
{
    public const string ConfigPathEnvVar = "KF_BOT_AI_PROFILES_PATH";
    public const string SeedPathEnvVar = "KF_BOT_AI_SEED_PATH";
    public const string DefaultRelativeDurableDir = ".local/masters-overrides";
    public const string DefaultRelativeSeedDir = "config";
    public const string ConfigFileName = "bot_ai_profiles.json";
    public const int DefaultDebounceMilliseconds = 250;

    private readonly ILogger<BotAiProfileProvider> _logger;
    private readonly string _resolvedDurablePath;
    private readonly string? _resolvedSeedPath;
    private readonly string _targetDirectory;
    private readonly string _targetFileName;
    private readonly bool _targetDirectoryExistedAtStart;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _debounceTimer;
    private readonly object _reloadLock = new();

    private BotAiProfileSnapshot _currentSnapshot;
    private bool _isUsingHardcodedFallback;
    private bool _disposed;

    public BotAiProfileSnapshot CurrentSnapshot => Volatile.Read(ref _currentSnapshot);
    public string ResolvedConfigPath => _resolvedDurablePath;
    public string? ResolvedSeedPath => _resolvedSeedPath;
    public bool IsUsingHardcodedFallback => _isUsingHardcodedFallback;

    public event Action<BotAiProfileSnapshot>? ProfilesReloaded;

    public BotAiProfileProvider(
        IWebHostEnvironment? environment = null,
        ILogger<BotAiProfileProvider>? logger = null,
        string? configPath = null,
        string? contentRoot = null,
        bool enableWatcher = true,
        int debounceMs = DefaultDebounceMilliseconds,
        string? seedPath = null)
    {
        _logger = logger ?? NullLogger<BotAiProfileProvider>.Instance;
        var effectiveContentRoot = contentRoot ?? environment?.ContentRootPath;

        _resolvedDurablePath = ResolveDurablePath(configPath, effectiveContentRoot);

        // When configPath is passed explicitly via constructor parameter (e.g. in targeted tests),
        // only use an explicit seedPath. In server mode (configPath is null), resolve seed automatically.
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            _resolvedSeedPath = !string.IsNullOrWhiteSpace(seedPath)
                ? (Path.IsPathRooted(seedPath) ? Path.GetFullPath(seedPath) : Path.GetFullPath(Path.Combine(ResolveBaseRoot(effectiveContentRoot), seedPath)))
                : null;
        }
        else
        {
            _resolvedSeedPath = ResolveSeedPath(seedPath, effectiveContentRoot);
        }

        _targetDirectory = Path.GetFullPath(Path.GetDirectoryName(_resolvedDurablePath) ?? AppContext.BaseDirectory);
        _targetFileName = Path.GetFileName(_resolvedDurablePath);
        _targetDirectoryExistedAtStart = Directory.Exists(_targetDirectory);

        _currentSnapshot = null!;
        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

        // Initial load hierarchy:
        // 1. Valid durable override (.local/masters-overrides/bot_ai_profiles.json)
        // 2. Valid seed fallback (config/bot_ai_profiles.json)
        // 3. Builtin hardcoded defaults (BotAiDefaults)
        bool loaded = false;
        if (File.Exists(_resolvedDurablePath))
        {
            loaded = TryLoadFromDisk(_resolvedDurablePath, isInitial: true, out var overrideSnapshot);
            if (loaded && overrideSnapshot is not null)
            {
                _currentSnapshot = overrideSnapshot;
                _isUsingHardcodedFallback = false;
                _logger.LogInformation(
                    "Bot AI profiles successfully loaded on startup from durable override (version {Version}, {Count} policies) at '{Path}'.",
                    overrideSnapshot.Version, overrideSnapshot.AllPolicies.Count, _resolvedDurablePath);
            }
        }

        if (!loaded && _resolvedSeedPath is not null && File.Exists(_resolvedSeedPath))
        {
            loaded = TryLoadFromDisk(_resolvedSeedPath, isInitial: true, out var seedSnapshot);
            if (loaded && seedSnapshot is not null)
            {
                _currentSnapshot = seedSnapshot;
                _isUsingHardcodedFallback = false;
                _logger.LogInformation(
                    "Bot AI profiles successfully loaded on startup from seed fallback (version {Version}, {Count} policies) at '{Path}'. Target durable path is '{DurablePath}'.",
                    seedSnapshot.Version, seedSnapshot.AllPolicies.Count, _resolvedSeedPath, _resolvedDurablePath);
            }
        }

        if (!loaded)
        {
            _currentSnapshot = BotAiDefaults.CreateDefaultSnapshot(_resolvedDurablePath);
            _isUsingHardcodedFallback = true;
            _logger.LogWarning(
                "Bot AI configuration at durable '{DurablePath}' and seed '{SeedPath}' was missing or invalid on startup. Initialized with hardcoded defaults.",
                _resolvedDurablePath, _resolvedSeedPath ?? "(none)");
        }

        // Initialize file watcher targeting durable path (even if absent).
        // Never requires write access to create directories if missing (supports read-only mounts).
        if (enableWatcher)
        {
            try
            {
                string watchRoot;
                bool includeSubdirs;
                string filter;

                if (_targetDirectoryExistedAtStart)
                {
                    watchRoot = _targetDirectory;
                    includeSubdirs = false;
                    filter = _targetFileName;
                }
                else
                {
                    // Find nearest existing ancestor directory
                    var cur = new DirectoryInfo(_targetDirectory);
                    while (cur != null && !cur.Exists)
                    {
                        cur = cur.Parent;
                    }

                    if (cur != null && cur.Exists)
                    {
                        watchRoot = cur.FullName;
                        includeSubdirs = true;
                        filter = "*";
                    }
                    else
                    {
                        watchRoot = effectiveContentRoot ?? AppContext.BaseDirectory;
                        includeSubdirs = true;
                        filter = "*";
                    }
                }

                if (Directory.Exists(watchRoot))
                {
                    _watcher = new FileSystemWatcher(watchRoot)
                    {
                        Filter = filter,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.CreationTime,
                        IncludeSubdirectories = includeSubdirs,
                        EnableRaisingEvents = true
                    };

                    _watcher.Changed += OnFileSystemEvent;
                    _watcher.Created += OnFileSystemEvent;
                    _watcher.Deleted += OnFileSystemEvent;
                    _watcher.Renamed += OnFileRenamed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize FileSystemWatcher for '{Path}'. Hot reload via filesystem events may be unavailable.", _resolvedDurablePath);
            }
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

    public static string ResolveDurablePath(string? explicitPath, string? contentRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.IsPathRooted(explicitPath)
                ? Path.GetFullPath(explicitPath)
                : Path.GetFullPath(Path.Combine(ResolveBaseRoot(contentRoot), explicitPath));
        }

        var envPath = Environment.GetEnvironmentVariable(ConfigPathEnvVar);
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return Path.IsPathRooted(envPath)
                ? Path.GetFullPath(envPath)
                : Path.GetFullPath(Path.Combine(ResolveBaseRoot(contentRoot), envPath));
        }

        var root = ResolveBaseRoot(contentRoot);
        return Path.GetFullPath(Path.Combine(root, ".local", "masters-overrides", ConfigFileName));
    }

    public static string? ResolveSeedPath(string? explicitSeedPath, string? contentRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitSeedPath))
        {
            return Path.IsPathRooted(explicitSeedPath)
                ? Path.GetFullPath(explicitSeedPath)
                : Path.GetFullPath(Path.Combine(ResolveBaseRoot(contentRoot), explicitSeedPath));
        }

        var envSeed = Environment.GetEnvironmentVariable(SeedPathEnvVar);
        if (!string.IsNullOrWhiteSpace(envSeed))
        {
            return Path.IsPathRooted(envSeed)
                ? Path.GetFullPath(envSeed)
                : Path.GetFullPath(Path.Combine(ResolveBaseRoot(contentRoot), envSeed));
        }

        var root = ResolveBaseRoot(contentRoot);
        var inConfig = Path.Combine(root, "config", ConfigFileName);
        if (File.Exists(inConfig)) return Path.GetFullPath(inConfig);

        var inRoot = Path.Combine(root, ConfigFileName);
        if (File.Exists(inRoot)) return Path.GetFullPath(inRoot);

        return Path.GetFullPath(inConfig);
    }

    public static string ResolvePath(string? explicitPath, string? contentRoot) =>
        ResolveDurablePath(explicitPath, contentRoot);

    private bool IsRelevantEvent(string? fullPath, string? name)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return false;

        try
        {
            var fullNorm = Path.GetFullPath(fullPath);
            if (string.Equals(fullNorm, _resolvedDurablePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var fileName = Path.GetFileName(fullPath);
            if (string.Equals(fileName, _targetFileName, StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(fullNorm);
                if (string.Equals(dir, _targetDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (!_targetDirectoryExistedAtStart && File.Exists(_resolvedDurablePath))
            {
                if (fullNorm.StartsWith(_targetDirectory, StringComparison.OrdinalIgnoreCase) ||
                    _targetDirectory.StartsWith(fullNorm, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore path evaluation errors
        }

        return false;
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (IsRelevantEvent(e.FullPath, e.Name))
            {
                ScheduleDebounce();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking filesystem event on '{Path}'.", e.FullPath);
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            if (IsRelevantEvent(e.FullPath, e.Name) || IsRelevantEvent(e.OldFullPath, e.OldName))
            {
                ScheduleDebounce();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking rename event on '{Path}'.", e.FullPath);
        }
    }

    private void ScheduleDebounce()
    {
        if (_disposed) return;
        try
        {
            _debounceTimer.Change(DefaultDebounceMilliseconds, Timeout.Infinite);
        }
        catch (ObjectDisposedException) { }
    }

    private void OnDebounceElapsed(object? state)
    {
        if (_disposed) return;
        Reload();
    }

    public bool Reload()
    {
        lock (_reloadLock)
        {
            if (_disposed) return false;

            var loaded = TryLoadFromDisk(_resolvedDurablePath, isInitial: false, out var snapshot);
            if (loaded && snapshot is not null)
            {
                Interlocked.Exchange(ref _currentSnapshot, snapshot);
                _isUsingHardcodedFallback = false;
                _logger.LogInformation(
                    "Bot AI profiles hot-reloaded successfully from durable override (version {Version}, {Count} policies) from '{Path}'.",
                    snapshot.Version, snapshot.AllPolicies.Count, _resolvedDurablePath);

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

            // Reload failed, file deleted, or invalid: RETAIN active valid snapshot
            _logger.LogWarning(
                "Bot AI profiles reload failed or file deleted/invalid; retaining last active valid snapshot (version {Version}) from '{Path}'.",
                CurrentSnapshot.Version, CurrentSnapshot.SourcePath);
            return false;
        }
    }

    private bool TryLoadFromDisk(string path, bool isInitial, out BotAiProfileSnapshot? snapshot)
    {
        snapshot = null;

        if (!File.Exists(path))
        {
            if (!isInitial)
            {
                _logger.LogWarning("Bot AI configuration file '{Path}' does not exist on reload; retaining last valid snapshot.", path);
            }
            return false;
        }

        string? content = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                content = reader.ReadToEnd();
                break;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(20);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read configuration file at '{Path}' (attempt {Attempt}).", path, attempt + 1);
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.LogWarning("Configuration file at '{Path}' was empty.", path);
            return false;
        }

        var validationResult = BotAiConfigurationValidator.Validate(content);
        if (!validationResult.IsValid)
        {
            _logger.LogError(
                "Bot AI configuration validation failed for '{Path}'. Errors: {Errors}",
                path, string.Join("; ", validationResult.Errors));
            return false;
        }

        snapshot = validationResult.ToSnapshot(path);
        return true;
    }

    public AiRolePolicy? GetPolicy(int modeIndex, int profileIndex) => CurrentSnapshot.GetPolicy(modeIndex, profileIndex);
    public AiRolePolicy? GetPolicy(string profile, int kickerId = 0, int modeIndex = 0) => CurrentSnapshot.GetPolicy(profile, kickerId, modeIndex);
    public AiRolePolicy? GetPolicy(string profile, int kickerId, BattleCompositionMode? mode) => CurrentSnapshot.GetPolicy(profile, kickerId, mode);
    public int ResolveParameterId(string? profile, int kickerId, BattleCompositionMode? mode = null, bool isGym = false) =>
        CurrentSnapshot.ResolveParameterId(profile, kickerId, mode, isGym);
    public int ResolveParameterId(string? profile, int kickerId, int battleRuleId, bool isGym = false) =>
        CurrentSnapshot.ResolveParameterId(profile, kickerId, battleRuleId, isGym);
    public IReadOnlyList<AiRolePolicy> GetAllPolicies() => CurrentSnapshot.AllPolicies;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_watcher is not null)
        {
            try
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnFileSystemEvent;
                _watcher.Created -= OnFileSystemEvent;
                _watcher.Deleted -= OnFileSystemEvent;
                _watcher.Renamed -= OnFileRenamed;
                _watcher.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing FileSystemWatcher.");
            }
        }

        try
        {
            _debounceTimer.Dispose();
        }
        catch { }
    }
}
