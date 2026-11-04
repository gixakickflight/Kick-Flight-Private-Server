namespace KickFlight.BootstrapApi;

using System;
using System.Collections.Generic;

/// <summary>
/// Service provider for hot-reloadable bot AI profiles.
/// </summary>
public interface IBotAiProfileProvider : IDisposable
{
    /// <summary>
    /// Current immutable snapshot of bot AI policies.
    /// </summary>
    BotAiProfileSnapshot CurrentSnapshot { get; }

    /// <summary>
    /// Where the current profiles came from: the otomo release ("otomo:kf.bot_ai_profiles@..."), the local file when
    /// Otomo:Masters is disabled, or "hardcoded-defaults".
    /// </summary>
    string Source { get; }

    /// <summary>
    /// True if the provider is currently serving hardcoded defaults because the config file
    /// was missing or invalid on startup.
    /// </summary>
    bool IsUsingHardcodedFallback { get; }

    AiRolePolicy? GetPolicy(int modeIndex, int profileIndex);
    AiRolePolicy? GetPolicyByKicker(int modeIndex, int kickerId);
    AiRolePolicy? GetPolicy(string profile, int kickerId = 0, int modeIndex = 0);
    AiRolePolicy? GetPolicy(string profile, int kickerId, BattleCompositionMode? mode);
    int ResolveParameterId(string? profile, int kickerId, BattleCompositionMode? mode = null, bool isGym = false);
    int ResolveParameterId(string? profile, int kickerId, int battleRuleId, bool isGym = false);
    IReadOnlyList<AiRolePolicy> GetAllPolicies();

    /// <summary>
    /// Re-reads the current document (the release's, or the local file when Otomo:Masters is disabled) now.
    /// </summary>
    bool Reload();

    /// <summary>
    /// Fired when configuration has been successfully reloaded and the snapshot atomically swapped.
    /// </summary>
    event Action<BotAiProfileSnapshot>? ProfilesReloaded;
}
