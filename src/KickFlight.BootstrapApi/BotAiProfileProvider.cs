namespace KickFlight.BootstrapApi;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class BotAiProfileProvider : IBotAiProfileProvider
{
    public const string ConfigPathEnvVar = "KF_BOT_AI_PROFILES_PATH";
    public const int DefaultDebounceMilliseconds = 250;

    private readonly ILogger<BotAiProfileProvider> _logger;
    private readonly string _resolvedPath;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _debounceTimer;
    private readonly object _reloadLock = new();

    private BotAiProfileSnapshot _currentSnapshot;
    private bool _isUsingHardcodedFallback;
    private bool _disposed;

    public BotAiProfileSnapshot CurrentSnapshot => Volatile.Read(ref _currentSnapshot);
    public string ResolvedConfigPath => _resolvedPath;
    public bool IsUsingHardcodedFallback => _isUsingHardcodedFallback;

    public event Action<BotAiProfileSnapshot>? ProfilesReloaded;

    public BotAiProfileProvider(
        string? configPath = null,
        string? contentRoot = null,
        ILogger<BotAiProfileProvider>? logger = null,
        bool enableWatcher = true,
        int debounceMs = DefaultDebounceMilliseconds)
    {
        _logger = logger ?? NullLogger<BotAiProfileProvider>.Instance;
        _resolvedPath = ResolvePath(configPath, contentRoot);
        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

        // Initial load
        var initialLoaded = TryLoadFromDisk(_resolvedPath, isInitial: true, out var snapshot);
        if (initialLoaded && snapshot is not null)
        {
            _currentSnapshot = snapshot;
            _isUsingHardcodedFallback = false;
            _logger.LogInformation(
                "Bot AI profiles successfully loaded on startup (version {Version}, {Count} policies) from '{Path}'.",
                snapshot.Version, snapshot.AllPolicies.Count, _resolvedPath);
        }
        else
        {
            _currentSnapshot = BotAiDefaults.CreateDefaultSnapshot(_resolvedPath);
            _isUsingHardcodedFallback = true;
            _logger.LogWarning(
                "Bot AI configuration at '{Path}' was missing or invalid on startup. Initialized with hardcoded defaults.",
                _resolvedPath);
        }

        // Initialize file watcher
        if (enableWatcher)
        {
            try
            {
                var directory = Path.GetDirectoryName(_resolvedPath);
                var fileName = Path.GetFileName(_resolvedPath);

                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    _watcher = new FileSystemWatcher(directory, fileName)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                        IncludeSubdirectories = false,
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
                _logger.LogWarning(ex, "Failed to initialize FileSystemWatcher for '{Path}'. Hot reload via filesystem events may be unavailable.", _resolvedPath);
            }
        }
    }

    public static string ResolvePath(string? explicitPath, string? contentRoot)
    {
        // 1. Explicit path parameter
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        // 2. Environment variable
        var envPath = Environment.GetEnvironmentVariable(ConfigPathEnvVar);
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return Path.GetFullPath(envPath);
        }

        var startingPath = contentRoot ?? AppContext.BaseDirectory;

        // 3. Upward directory walk from startingPath (contentRoot or AppContext.BaseDirectory)
        var dir = new DirectoryInfo(startingPath);
        for (int i = 0; i < 5 && dir != null; i++)
        {
            var configInDir = Path.Combine(dir.FullName, "config", "bot_ai_profiles.json");
            if (File.Exists(configInDir))
            {
                return Path.GetFullPath(configInDir);
            }
            var rootInDir = Path.Combine(dir.FullName, "bot_ai_profiles.json");
            if (File.Exists(rootInDir))
            {
                return Path.GetFullPath(rootInDir);
            }
            dir = dir.Parent;
        }

        // 4. Try RepositoryPaths.Resolve (walks up to .sln)
        try
        {
            var repoResolved = RepositoryPaths.Resolve("config/bot_ai_profiles.json", startingPath);
            if (File.Exists(repoResolved))
            {
                return repoResolved;
            }
        }
        catch
        {
            // Outside repo or .sln not found (e.g. published directory)
        }

        // 5. Fallback candidates (AppContext or CurrentDirectory)
        var fallbacks = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "config", "bot_ai_profiles.json"),
            Path.Combine(AppContext.BaseDirectory, "bot_ai_profiles.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "config", "bot_ai_profiles.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "bot_ai_profiles.json")
        };

        foreach (var c in fallbacks)
        {
            if (File.Exists(c))
            {
                return Path.GetFullPath(c);
            }
        }

        // Default canonical path if not found yet
        return Path.GetFullPath(Path.Combine(startingPath, "config", "bot_ai_profiles.json"));
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        try
        {
            ScheduleDebounce();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error scheduling debounce for filesystem event on '{Path}'.", e.FullPath);
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            var targetFileName = Path.GetFileName(_resolvedPath);
            if (string.Equals(e.Name, targetFileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.OldName, targetFileName, StringComparison.OrdinalIgnoreCase))
            {
                ScheduleDebounce();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error scheduling debounce for rename event on '{Path}'.", e.FullPath);
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

            var loaded = TryLoadFromDisk(_resolvedPath, isInitial: false, out var snapshot);
            if (loaded && snapshot is not null)
            {
                Interlocked.Exchange(ref _currentSnapshot, snapshot);
                _isUsingHardcodedFallback = false;
                _logger.LogInformation(
                    "Bot AI profiles hot-reloaded successfully (version {Version}, {Count} policies) from '{Path}'.",
                    snapshot.Version, snapshot.AllPolicies.Count, _resolvedPath);

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

            // Reload failed: RETAIN active valid snapshot
            _logger.LogWarning(
                "Bot AI profiles reload failed; retaining last active valid snapshot (version {Version}) from '{Path}'.",
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
