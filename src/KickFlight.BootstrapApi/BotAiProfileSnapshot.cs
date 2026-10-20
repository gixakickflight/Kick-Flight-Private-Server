namespace KickFlight.BootstrapApi;

using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Linq;

/// <summary>
/// Immutable snapshot of bot AI policies backed by FrozenDictionary.
/// </summary>
public sealed class BotAiProfileSnapshot
{
    public int Version { get; }
    public string SourcePath { get; }
    public DateTimeOffset LoadedAt { get; }
    public FrozenDictionary<(int ModeIndex, int ProfileIndex), AiRolePolicy> PoliciesByModeAndProfile { get; }
    public FrozenDictionary<(int ModeIndex, string Profile), AiRolePolicy> PoliciesByModeAndProfileName { get; }
    public FrozenDictionary<int, AiRolePolicy> PoliciesByParameterId { get; }
    public IReadOnlyList<AiRolePolicy> AllPolicies { get; }

    public BotAiProfileSnapshot(
        int version,
        string sourcePath,
        DateTimeOffset loadedAt,
        IEnumerable<AiRolePolicy> policies)
    {
        Version = version;
        SourcePath = sourcePath;
        LoadedAt = loadedAt;

        var list = policies.OrderBy(p => p.ParameterId).ToList();
        AllPolicies = list.AsReadOnly();

        var modeProfileDict = new Dictionary<(int ModeIndex, int ProfileIndex), AiRolePolicy>();
        var modeProfileNameDict = new Dictionary<(int ModeIndex, string Profile), AiRolePolicy>();
        var paramIdDict = new Dictionary<int, AiRolePolicy>();

        foreach (var p in list)
        {
            var profIdx = ExperimentalAiRoleService.GetCanonicalProfileIndex(p.Profile);
            if (profIdx < 0)
            {
                profIdx = (p.ParameterId - ExperimentalAiRoleService.MinRoleParamId) % 13;
            }
            modeProfileDict[(p.ModeIndex, profIdx)] = p;
            modeProfileNameDict[(p.ModeIndex, p.Profile)] = p;
            paramIdDict[p.ParameterId] = p;
        }

        PoliciesByModeAndProfile = modeProfileDict.ToFrozenDictionary();
        PoliciesByModeAndProfileName = modeProfileNameDict.ToFrozenDictionary();
        PoliciesByParameterId = paramIdDict.ToFrozenDictionary();
    }

    public AiRolePolicy? GetPolicy(int modeIndex, int profileIndex) =>
        PoliciesByModeAndProfile.TryGetValue((modeIndex, profileIndex), out var policy) ? policy : null;

    public AiRolePolicy? GetPolicyByCanonicalName(string profile, int modeIndex = 0) =>
        PoliciesByModeAndProfileName.TryGetValue((modeIndex, profile), out var policy) ? policy : null;

    public AiRolePolicy? GetPolicy(int parameterId) =>
        PoliciesByParameterId.TryGetValue(parameterId, out var policy) ? policy : null;

    public AiRolePolicy? GetPolicy(string profile, int kickerId = 0, int modeIndex = 0)
    {
        var effectiveProfile = profile;
        if (kickerId == 10 && modeIndex is 0 or 2)
        {
            effectiveProfile = ExperimentalAiRoleService.ProfileObjectiveAttacker;
        }
        var profIdx = ExperimentalAiRoleService.GetProfileIndex(effectiveProfile, kickerId);
        if (profIdx < 0 || modeIndex < 0 || modeIndex > 2) return null;

        return GetPolicy(modeIndex, profIdx);
    }

    public AiRolePolicy? GetPolicy(string profile, int kickerId, BattleCompositionMode? mode)
    {
        var modeIndex = mode.HasValue ? ExperimentalAiRoleService.GetModeIndex(mode.Value) : 0;
        if (modeIndex < 0) return null;
        return GetPolicy(profile, kickerId, modeIndex);
    }

    public int ResolveParameterId(string? profile, int kickerId, BattleCompositionMode? mode = null, bool isGym = false)
    {
        // Gym bots must ALWAYS receive GymAiParameterBase + kickerId (isolated from experimental roles)
        if (isGym)
        {
            return BattleMatchmakingService.GymAiParameterBase + kickerId;
        }

        // When experimental flag is off, retain standard pristine kicker ID
        if (!ExperimentalAiRoleService.IsEnabled)
        {
            return kickerId;
        }

        if (mode is null)
        {
            return kickerId;
        }

        var modeIndex = ExperimentalAiRoleService.GetModeIndex(mode.Value);
        if (modeIndex < 0)
        {
            return kickerId;
        }

        var effectiveProfile = profile;
        if (kickerId == 10 && mode.Value is BattleCompositionMode.CrystalScramble or BattleCompositionMode.RapidBall)
        {
            effectiveProfile = ExperimentalAiRoleService.ProfileObjectiveAttacker;
        }

        var profileIndex = ExperimentalAiRoleService.GetProfileIndex(effectiveProfile, kickerId);
        if (profileIndex < 0)
        {
            return kickerId;
        }

        var id = ExperimentalAiRoleService.MinRoleParamId + (13 * modeIndex) + profileIndex;
        if (id < ExperimentalAiRoleService.MinRoleParamId || id > ExperimentalAiRoleService.MaxAllowedParamId)
        {
            throw new InvalidOperationException($"Role parameter ID {id} outside safe range [{ExperimentalAiRoleService.MinRoleParamId}..{ExperimentalAiRoleService.MaxAllowedParamId}].");
        }
        return id;
    }

    public int ResolveParameterId(string? profile, int kickerId, int battleRuleId, bool isGym = false)
    {
        if (isGym)
        {
            return BattleMatchmakingService.GymAiParameterBase + kickerId;
        }

        var mode = ScrambleBotCompositionSelector.ModeForBattleRuleId(battleRuleId);
        return ResolveParameterId(profile, kickerId, mode, isGym);
    }
}
