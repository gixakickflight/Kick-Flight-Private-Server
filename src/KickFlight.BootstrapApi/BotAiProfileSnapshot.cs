namespace KickFlight.BootstrapApi;

using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Linq;

/// <summary>
/// Immutable snapshot of bot AI policies backed by FrozenDictionary.
/// Supports both independent per-kicker lookup (14 kickers x 3 modes = 42) and legacy profile lookups.
/// </summary>
public sealed class BotAiProfileSnapshot
{
    public int Version { get; }
    public string SourcePath { get; }
    public DateTimeOffset LoadedAt { get; }
    public FrozenDictionary<(int ModeIndex, int KickerId), AiRolePolicy> PoliciesByModeAndKicker { get; }
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

        var modeKickerDict = new Dictionary<(int ModeIndex, int KickerId), AiRolePolicy>();
        var modeProfileDict = new Dictionary<(int ModeIndex, int ProfileIndex), AiRolePolicy>();
        var modeProfileNameDict = new Dictionary<(int ModeIndex, string Profile), AiRolePolicy>();
        var paramIdDict = new Dictionary<int, AiRolePolicy>();

        foreach (var p in list)
        {
            if (p.KickerId >= 1 && p.KickerId <= 14)
            {
                modeKickerDict[(p.ModeIndex, p.KickerId)] = p;
            }
            paramIdDict[p.ParameterId] = p;
            modeProfileNameDict[(p.ModeIndex, p.Profile)] = p;

            var profIdx = ExperimentalAiRoleService.GetCanonicalProfileIndex(p.Profile);
            if (profIdx >= 0)
            {
                modeProfileDict[(p.ModeIndex, profIdx)] = p;
            }
            else if (p.ParameterId >= ExperimentalAiRoleService.MinRoleParamId && p.ParameterId <= ExperimentalAiRoleService.MaxRoleParamId)
            {
                var fallbackProfIdx = (p.ParameterId - ExperimentalAiRoleService.MinRoleParamId) % 13;
                if (!modeProfileDict.ContainsKey((p.ModeIndex, fallbackProfIdx)))
                {
                    modeProfileDict[(p.ModeIndex, fallbackProfIdx)] = p;
                }
            }
        }

        // Maintain legacy 0..12 profile index lookups for backwards compatibility
        for (int m = 0; m <= 2; m++)
        {
            for (int prof = 0; prof <= 12; prof++)
            {
                if (!modeProfileDict.ContainsKey((m, prof)))
                {
                    var expKicker = BotAiDefaults.Profiles[prof].KickerId;
                    if (modeKickerDict.TryGetValue((m, expKicker), out var pol))
                    {
                        modeProfileDict[(m, prof)] = pol;
                        var profName = BotAiDefaults.Profiles[prof].Profile;
                        if (!modeProfileNameDict.ContainsKey((m, profName)))
                        {
                            modeProfileNameDict[(m, profName)] = pol;
                        }
                    }
                }
            }
        }

        // Create legacy base alias policies (18..22, 31..35, 44..48) so cross-role queries resolve
        var legacyBaseSpecs = new (int ModeIndex, int ProfIdx, int ParamId, string ProfName, int SourceKicker)[]
        {
            (0, 3, 18, "objective_attacker", 10),
            (0, 4, 19, "healer", 2),
            (0, 5, 20, "speed_support", 7),
            (0, 6, 21, "damage_support", 14),
            (0, 7, 22, "trap_defender", 5),

            (1, 3, 31, "objective_attacker", 10),
            (1, 4, 32, "healer", 2),
            (1, 5, 33, "speed_support", 7),
            (1, 6, 34, "damage_support", 14),
            (1, 7, 35, "trap_defender", 5),

            (2, 3, 44, "objective_attacker", 10),
            (2, 4, 45, "healer", 2),
            (2, 5, 46, "speed_support", 7),
            (2, 6, 47, "damage_support", 14),
            (2, 7, 48, "trap_defender", 5)
        };

        foreach (var spec in legacyBaseSpecs)
        {
            if (modeKickerDict.TryGetValue((spec.ModeIndex, spec.SourceKicker), out var srcPolicy))
            {
                var alias = srcPolicy with { ParameterId = spec.ParamId, Profile = spec.ProfName };
                modeProfileDict[(spec.ModeIndex, spec.ProfIdx)] = alias;
                modeProfileNameDict[(spec.ModeIndex, spec.ProfName)] = alias;
                if (!paramIdDict.ContainsKey(spec.ParamId))
                {
                    paramIdDict[spec.ParamId] = alias;
                }
            }
        }

        PoliciesByModeAndKicker = modeKickerDict.ToFrozenDictionary();
        PoliciesByModeAndProfile = modeProfileDict.ToFrozenDictionary();
        PoliciesByModeAndProfileName = modeProfileNameDict.ToFrozenDictionary();
        PoliciesByParameterId = paramIdDict.ToFrozenDictionary();
    }

    public AiRolePolicy? GetPolicyByKicker(int modeIndex, int kickerId) =>
        PoliciesByModeAndKicker.TryGetValue((modeIndex, kickerId), out var policy) ? policy : null;

    public AiRolePolicy? GetPolicy(int modeIndex, int profileIndex) =>
        PoliciesByModeAndProfile.TryGetValue((modeIndex, profileIndex), out var policy) ? policy : null;

    public AiRolePolicy? GetPolicyByCanonicalName(string profile, int modeIndex = 0) =>
        PoliciesByModeAndProfileName.TryGetValue((modeIndex, profile), out var policy) ? policy : null;

    public AiRolePolicy? GetPolicy(int parameterId) =>
        PoliciesByParameterId.TryGetValue(parameterId, out var policy) ? policy : null;

    public AiRolePolicy? GetPolicy(string profile, int kickerId = 0, int modeIndex = 0)
    {
        if (kickerId >= 1 && kickerId <= 14 && (string.IsNullOrWhiteSpace(profile) || string.Equals(profile, BotAiDefaults.Kickers[kickerId - 1].Name, StringComparison.OrdinalIgnoreCase)))
        {
            var p = GetPolicyByKicker(modeIndex, kickerId);
            if (p != null) return p;
        }

        var effectiveProfile = profile;
        if (kickerId == 10 && modeIndex is 0 or 2)
        {
            effectiveProfile = ExperimentalAiRoleService.ProfileObjectiveAttacker;
        }

        if (!string.IsNullOrWhiteSpace(profile))
        {
            var profIdx = ExperimentalAiRoleService.GetProfileIndex(effectiveProfile, kickerId);
            if (profIdx >= 0 && modeIndex >= 0 && modeIndex <= 2)
            {
                if (PoliciesByModeAndProfile.TryGetValue((modeIndex, profIdx), out var p)) return p;
            }

            var byName = GetPolicyByCanonicalName(profile, modeIndex);
            if (byName != null) return byName;
        }

        if (kickerId >= 1 && kickerId <= 14)
        {
            var p = GetPolicyByKicker(modeIndex, kickerId);
            if (p != null) return p;
        }

        return null;
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

        // Check if an explicit cross-role override is requested (e.g. Kite K4 given objective_attacker, or Yuyan K10 given hunter in FlagFlight)
        if (!string.IsNullOrWhiteSpace(effectiveProfile) && kickerId >= 1 && kickerId <= 14)
        {
            var inheritedKey = BotAiDefaults.GetInheritedProfileKey(kickerId);
            var kickerName = BotAiDefaults.Kickers[kickerId - 1].Name;
            bool isNaturalProfile = string.Equals(effectiveProfile, kickerName, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(effectiveProfile, inheritedKey, StringComparison.OrdinalIgnoreCase) ||
                                    (kickerId == 10 && (string.Equals(effectiveProfile, "objective_attacker", StringComparison.OrdinalIgnoreCase) || string.Equals(effectiveProfile, "Yuyan", StringComparison.OrdinalIgnoreCase))) ||
                                    (kickerId == 5 && (string.Equals(effectiveProfile, "trap_defender", StringComparison.OrdinalIgnoreCase) || string.Equals(effectiveProfile, "Owlbert", StringComparison.OrdinalIgnoreCase))) ||
                                    (kickerId == 2 && (string.Equals(effectiveProfile, "healer", StringComparison.OrdinalIgnoreCase) || string.Equals(effectiveProfile, "Ruriha", StringComparison.OrdinalIgnoreCase))) ||
                                    (kickerId == 7 && (string.Equals(effectiveProfile, "speed_support", StringComparison.OrdinalIgnoreCase) || string.Equals(effectiveProfile, "Grenhawk", StringComparison.OrdinalIgnoreCase))) ||
                                    (kickerId == 14 && (string.Equals(effectiveProfile, "damage_support", StringComparison.OrdinalIgnoreCase) || string.Equals(effectiveProfile, "Sid", StringComparison.OrdinalIgnoreCase)));

            if (!isNaturalProfile)
            {
                var profIdx = ExperimentalAiRoleService.GetProfileIndex(effectiveProfile, kickerId);
                if (profIdx >= 0)
                {
                    return ExperimentalAiRoleService.MinRoleParamId + (13 * modeIndex) + profIdx;
                }
                return kickerId;
            }
        }

        // V2 per-kicker resolution: exact independent ID for kicker in this mode
        if (kickerId >= 1 && kickerId <= 14 && PoliciesByModeAndKicker.TryGetValue((modeIndex, kickerId), out var kickerPolicy))
        {
            var id = kickerPolicy.ParameterId;
            if (id < ExperimentalAiRoleService.MinRoleParamId || id > ExperimentalAiRoleService.MaxAllowedParamId)
            {
                throw new InvalidOperationException($"Role parameter ID {id} outside safe range [{ExperimentalAiRoleService.MinRoleParamId}..{ExperimentalAiRoleService.MaxAllowedParamId}].");
            }
            return id;
        }

        // Fallback resolution by profile name
        if (!string.IsNullOrWhiteSpace(profile))
        {
            var p = GetPolicyByCanonicalName(profile, modeIndex);
            if (p != null) return p.ParameterId;

            var profIdx = ExperimentalAiRoleService.GetProfileIndex(profile, kickerId);
            if (profIdx >= 0)
            {
                return ExperimentalAiRoleService.MinRoleParamId + (13 * modeIndex) + profIdx;
            }
        }

        return kickerId;
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
