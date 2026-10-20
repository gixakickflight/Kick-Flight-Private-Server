namespace KickFlight.BootstrapApi;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;

public record AiRolePolicy(
    string Profile,
    int ModeIndex,
    int ParameterId,
    int KickerId,
    int KickerCostumeId,
    int AttackMotivation,
    int DefenseMotivation,
    int ScoreMotivation,
    int ScoreToDeposit,
    double AttackDashInterval,
    double DefenseDashInterval,
    double ScoreDashInterval,
    double AttackSkillInterval,
    double HealSkillInterval,
    double BuffSkillInterval,
    double TrapSkillInterval,
    double WarpSkillInterval,
    double HealDamageRate,
    bool CanLockon,
    string ScramblePolicy,
    string FlagFlightPolicy,
    string RapidBallPolicy,
    double LowHpDepositThreshold = 0.35);

public static class ExperimentalAiRoleService
{
    public const string EnvFlagName = "KF_EXPERIMENTAL_AI_ROLES";
    public const int MinRoleParamId = 15;
    public const int MaxRoleParamId = 53; // Deterministic 39-ID contract: 15..53
    public const int MaxAllowedParamId = 99; // Native cave >= 100 reserved for Gym mannequin

    public static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnvFlagName), "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Environment.GetEnvironmentVariable(EnvFlagName), "true", StringComparison.OrdinalIgnoreCase);

    public const string ProfileCarrier = "carrier";
    public const string ProfileTankDefender = "tank_defender";
    public const string ProfileHunter = "hunter";
    public const string ProfileObjectiveAttacker = "objective_attacker";
    public const string ProfileHealer = "healer";
    public const string ProfileSpeedSupport = "speed_support";
    public const string ProfileDamageSupport = "damage_support";
    public const string ProfileTrapDefender = "trap_defender";

    public const string ProfileYuyan = "Yuyan";
    public const string ProfileOwlbert = "Owlbert";
    public const string ProfileRuriha = "Ruriha";
    public const string ProfileGrenhawk = "Grenhawk";
    public const string ProfileSid = "Sid";

    public static readonly string[] BaseProfiles =
    [
        ProfileCarrier,
        ProfileTankDefender,
        ProfileHunter,
        ProfileObjectiveAttacker,
        ProfileHealer,
        ProfileSpeedSupport,
        ProfileDamageSupport,
        ProfileTrapDefender
    ];

    public static readonly string[] AllProfileKeys =
    [
        ProfileCarrier,
        ProfileTankDefender,
        ProfileHunter,
        ProfileObjectiveAttacker,
        ProfileHealer,
        ProfileSpeedSupport,
        ProfileDamageSupport,
        ProfileTrapDefender,
        ProfileYuyan,
        ProfileOwlbert,
        ProfileRuriha,
        ProfileGrenhawk,
        ProfileSid
    ];

    // Alias for backward compatibility
    public static readonly string[] AllProfiles = BaseProfiles;

    public static int GetModeIndex(BattleCompositionMode mode) => mode switch
    {
        BattleCompositionMode.CrystalScramble => 0,
        BattleCompositionMode.FlagFlight => 1,
        BattleCompositionMode.RapidBall => 2,
        _ => -1
    };

    public static int GetModeIndex(int battleRuleId) => battleRuleId switch
    {
        1 or 5 or 6 or 7 => 0, // Crystal Scramble
        2 or 8 => 1,          // Flag Flight
        3 or 4 => 2,          // Rapid Ball
        _ => -1
    };

    public static int GetProfileIndex(string? profile, int kickerId = 0)
    {
        if (string.IsNullOrWhiteSpace(profile)) return -1;

        // Specializations take precedence when explicit kicker matches or named profile matches
        if (string.Equals(profile, ProfileYuyan, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(profile, ProfileObjectiveAttacker, StringComparison.OrdinalIgnoreCase) && kickerId == 10))
        {
            return 8;
        }
        if (string.Equals(profile, ProfileOwlbert, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(profile, ProfileTrapDefender, StringComparison.OrdinalIgnoreCase) && kickerId == 5))
        {
            return 9;
        }
        if (string.Equals(profile, ProfileRuriha, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(profile, ProfileHealer, StringComparison.OrdinalIgnoreCase) && kickerId == 2))
        {
            return 10;
        }
        if (string.Equals(profile, ProfileGrenhawk, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(profile, ProfileSpeedSupport, StringComparison.OrdinalIgnoreCase) && kickerId == 7))
        {
            return 11;
        }
        if (string.Equals(profile, ProfileSid, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(profile, ProfileDamageSupport, StringComparison.OrdinalIgnoreCase) && kickerId == 14))
        {
            return 12;
        }

        if (string.Equals(profile, ProfileCarrier, StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.Equals(profile, ProfileTankDefender, StringComparison.OrdinalIgnoreCase)) return 1;
        if (string.Equals(profile, ProfileHunter, StringComparison.OrdinalIgnoreCase)) return 2;
        if (string.Equals(profile, ProfileObjectiveAttacker, StringComparison.OrdinalIgnoreCase)) return 3;
        if (string.Equals(profile, ProfileHealer, StringComparison.OrdinalIgnoreCase)) return 4;
        if (string.Equals(profile, ProfileSpeedSupport, StringComparison.OrdinalIgnoreCase)) return 5;
        if (string.Equals(profile, ProfileDamageSupport, StringComparison.OrdinalIgnoreCase)) return 6;
        if (string.Equals(profile, ProfileTrapDefender, StringComparison.OrdinalIgnoreCase)) return 7;

        return -1;
    }

    /// <summary>
    /// Resolves the canonical profile index (0..12) purely based on profile name,
    /// independent of kicker specialization. Used for 1:1 indexing of the 39-profile contract.
    /// </summary>
    public static int GetCanonicalProfileIndex(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile)) return -1;
        if (string.Equals(profile, ProfileCarrier, StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.Equals(profile, ProfileTankDefender, StringComparison.OrdinalIgnoreCase)) return 1;
        if (string.Equals(profile, ProfileHunter, StringComparison.OrdinalIgnoreCase)) return 2;
        if (string.Equals(profile, ProfileObjectiveAttacker, StringComparison.OrdinalIgnoreCase)) return 3;
        if (string.Equals(profile, ProfileHealer, StringComparison.OrdinalIgnoreCase)) return 4;
        if (string.Equals(profile, ProfileSpeedSupport, StringComparison.OrdinalIgnoreCase)) return 5;
        if (string.Equals(profile, ProfileDamageSupport, StringComparison.OrdinalIgnoreCase)) return 6;
        if (string.Equals(profile, ProfileTrapDefender, StringComparison.OrdinalIgnoreCase)) return 7;
        if (string.Equals(profile, ProfileYuyan, StringComparison.OrdinalIgnoreCase)) return 8;
        if (string.Equals(profile, ProfileOwlbert, StringComparison.OrdinalIgnoreCase)) return 9;
        if (string.Equals(profile, ProfileRuriha, StringComparison.OrdinalIgnoreCase)) return 10;
        if (string.Equals(profile, ProfileGrenhawk, StringComparison.OrdinalIgnoreCase)) return 11;
        if (string.Equals(profile, ProfileSid, StringComparison.OrdinalIgnoreCase)) return 12;
        return -1;
    }

    private static IBotAiProfileProvider _currentProvider = new BotAiProfileProvider(enableWatcher: false);

    public static IBotAiProfileProvider CurrentProvider
    {
        get => Volatile.Read(ref _currentProvider);
        set => Volatile.Write(ref _currentProvider, value ?? throw new ArgumentNullException(nameof(value)));
    }

    public static AiRolePolicy BuildPolicy(int mode, int prof, int paramId) =>
        BotAiDefaults.BuildDefaultPolicy(mode, prof, paramId);

    public static IReadOnlyList<AiRolePolicy> GetAllPolicies()
    {
        return CurrentProvider.GetAllPolicies();
    }

    public static AiRolePolicy? GetPolicy(string profile, int kickerId = 0, int modeIndex = 0)
    {
        return CurrentProvider.GetPolicy(profile, kickerId, modeIndex);
    }

    public static AiRolePolicy? GetPolicy(string profile, int kickerId, BattleCompositionMode? mode)
    {
        return CurrentProvider.GetPolicy(profile, kickerId, mode);
    }

    public static int ResolveParameterId(string? profile, int kickerId, BattleCompositionMode? mode = null, bool isGym = false)
    {
        return CurrentProvider.ResolveParameterId(profile, kickerId, mode, isGym);
    }

    public static int ResolveParameterId(string? profile, int kickerId, int battleRuleId, bool isGym = false)
    {
        return CurrentProvider.ResolveParameterId(profile, kickerId, battleRuleId, isGym);
    }

    public static string AppendExperimentalRoleAiRows(string aiParamJson, BotAiProfileSnapshot? snapshot = null)
    {
        try
        {
            var rows = JsonNode.Parse(aiParamJson) as JsonArray;
            if (rows is null) return aiParamJson;

            // Map existing base rows by kickerId (ignoring gym rows >= 100)
            var baseRowsByKicker = new Dictionary<int, JsonObject>();
            foreach (var r in rows)
            {
                if (r is JsonObject obj)
                {
                    var id = obj["id"]?.GetValue<int>() ?? 0;
                    var kickerId = obj["kickerId"]?.GetValue<int>() ?? 0;
                    if (id < BattleMatchmakingService.GymAiParameterBase && kickerId > 0 && !baseRowsByKicker.ContainsKey(kickerId))
                    {
                        baseRowsByKicker[kickerId] = obj;
                    }
                }
            }

            var extra = new List<JsonNode>();
            var existingIds = new HashSet<int>(rows.Select(r => r?["id"]?.GetValue<int>() ?? 0));

            // Capture ONE snapshot per master generation
            var effectiveSnapshot = snapshot ?? CurrentProvider.CurrentSnapshot;
            var policies = effectiveSnapshot.AllPolicies;

            foreach (var policy in policies)
            {
                if (existingIds.Contains(policy.ParameterId)) continue;

                // Pick template row: match policy.KickerId or first available base row
                if (!baseRowsByKicker.TryGetValue(policy.KickerId, out var template) && baseRowsByKicker.Count > 0)
                {
                    template = baseRowsByKicker.Values.First();
                }
                if (template is null) continue;

                var copy = (JsonObject)template.DeepClone();
                copy["id"] = policy.ParameterId;
                copy["kickerId"] = policy.KickerId;
                copy["kickerCostumeId"] = policy.KickerCostumeId;
                copy["rank"] = 13; // Must match bot rank for lookup
                copy["attackMotivation"] = policy.AttackMotivation;
                copy["defenseMotivation"] = policy.DefenseMotivation;
                copy["scoreMotivation"] = policy.ScoreMotivation;
                copy["scoreToDeposit"] = policy.ScoreToDeposit;
                copy["canLockon"] = policy.CanLockon;
                copy["healDamageRate"] = policy.HealDamageRate;
                copy["attackDashInterval"] = policy.AttackDashInterval;
                copy["defenseDashInterval"] = policy.DefenseDashInterval;
                copy["scoreDashInterval"] = policy.ScoreDashInterval;
                copy["attackSkillInterval"] = policy.AttackSkillInterval;
                copy["healSkillInterval"] = policy.HealSkillInterval;
                copy["buffSkillInterval"] = policy.BuffSkillInterval;
                copy["trapSkillInterval"] = policy.TrapSkillInterval;
                copy["warpSkillInterval"] = policy.WarpSkillInterval;

                extra.Add(copy);
                existingIds.Add(policy.ParameterId);
            }

            foreach (var e in extra)
            {
                rows.Add(e);
            }

            return rows.ToJsonString();
        }
        catch (Exception)
        {
            return aiParamJson;
        }
    }
}
