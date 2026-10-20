namespace KickFlight.BootstrapApi;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

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
    string RapidBallPolicy);

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

    private static readonly Dictionary<(int ModeIndex, int ProfileIndex), AiRolePolicy> PoliciesByModeAndProfile;

    static ExperimentalAiRoleService()
    {
        PoliciesByModeAndProfile = new Dictionary<(int ModeIndex, int ProfileIndex), AiRolePolicy>();

        for (int mode = 0; mode <= 2; mode++)
        {
            for (int prof = 0; prof <= 12; prof++)
            {
                var id = 15 + (13 * mode) + prof;
                var policy = BuildPolicy(mode, prof, id);
                PoliciesByModeAndProfile[(mode, prof)] = policy;
            }
        }
    }

    private static AiRolePolicy BuildPolicy(int mode, int prof, int paramId)
    {
        return prof switch
        {
            // 0: carrier (Kicker 8, Ana)
            0 => new AiRolePolicy(
                Profile: ProfileCarrier,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: 8,
                KickerCostumeId: 2080101,
                AttackMotivation: mode switch { 0 => 30, 1 => 25, _ => 35 },
                DefenseMotivation: mode switch { 0 => 30, 1 => 20, _ => 25 },
                ScoreMotivation: mode switch { 0 => 90, 1 => 95, _ => 90 },
                ScoreToDeposit: mode switch { 0 => 5, 1 => 1, _ => 3 },
                AttackDashInterval: 1.0,
                DefenseDashInterval: 0.8,
                ScoreDashInterval: mode == 1 ? 0.7 : 0.8,
                AttackSkillInterval: 4.0,
                HealSkillInterval: 3.0,
                BuffSkillInterval: 4.0,
                TrapSkillInterval: 5.0,
                WarpSkillInterval: 4.0,
                HealDamageRate: 0.5,
                CanLockon: true,
                ScramblePolicy: "Collect crystals; deposit at friendly guardian when crystals >= scoreToDeposit; Ana escapes evasively.",
                FlagFlightPolicy: "Retrieve designated flag from pedestal or carrier; fly evasively to own team base stand.",
                RapidBallPolicy: "Gather scattered balls; deposit into open goal once enemy guardian is downed."),

            // 1: tank_defender (Kicker 3, Coco)
            1 => new AiRolePolicy(
                Profile: ProfileTankDefender,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: 3,
                KickerCostumeId: 2030101,
                AttackMotivation: mode switch { 0 => 50, 1 => 60, _ => 55 },
                DefenseMotivation: mode switch { 0 => 90, 1 => 85, _ => 90 },
                ScoreMotivation: mode switch { 0 => 20, 1 => 20, _ => 25 },
                ScoreToDeposit: mode switch { 0 => 15, 1 => 1, _ => 10 },
                AttackDashInterval: 1.2,
                DefenseDashInterval: 0.8,
                ScoreDashInterval: 1.5,
                AttackSkillInterval: 3.0,
                HealSkillInterval: 4.0,
                BuffSkillInterval: 4.0,
                TrapSkillInterval: 4.0,
                WarpSkillInterval: 5.0,
                HealDamageRate: 0.4,
                CanLockon: true,
                ScramblePolicy: "Engage approaching enemy attackers in combat zone using threat-based self-defense.",
                FlagFlightPolicy: "Guard friendly flag pedestal and escort allied flag carrier.",
                RapidBallPolicy: "Guard friendly guardian to prevent goal from opening."),

            // 2: hunter (Kicker 4, Kite archetype)
            2 => new AiRolePolicy(
                Profile: ProfileHunter,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: 4,
                KickerCostumeId: 2040101,
                AttackMotivation: 90,
                DefenseMotivation: mode switch { 0 => 20, 1 => 30, _ => 25 },
                ScoreMotivation: 40,
                ScoreToDeposit: mode switch { 0 => 10, 1 => 1, _ => 5 },
                AttackDashInterval: 0.8,
                DefenseDashInterval: 1.2,
                ScoreDashInterval: 1.2,
                AttackSkillInterval: 2.5,
                HealSkillInterval: 5.0,
                BuffSkillInterval: 5.0,
                TrapSkillInterval: 4.0,
                WarpSkillInterval: 4.0,
                HealDamageRate: 0.5,
                CanLockon: true,
                ScramblePolicy: "Hunt and eliminate enemy crystal carriers and collectors.",
                FlagFlightPolicy: "Intercept and eliminate opposing runners carrying the flag.",
                RapidBallPolicy: "Eliminate enemy defenders and ball collectors."),

            // 3: objective_attacker (Kicker 10, Yuyan base)
            3 => new AiRolePolicy(
                Profile: ProfileObjectiveAttacker,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: 10,
                KickerCostumeId: 2100101,
                AttackMotivation: mode switch { 0 => 85, 1 => 90, _ => 90 },
                DefenseMotivation: 20,
                ScoreMotivation: mode switch { 0 => 75, 1 => 70, _ => 80 },
                ScoreToDeposit: mode switch { 0 => 10, 1 => 1, _ => 5 },
                AttackDashInterval: 0.9,
                DefenseDashInterval: 1.2,
                ScoreDashInterval: 0.9,
                AttackSkillInterval: 2.5,
                HealSkillInterval: 5.0,
                BuffSkillInterval: 4.0,
                TrapSkillInterval: 5.0,
                WarpSkillInterval: 4.0,
                HealDamageRate: 0.5,
                CanLockon: true,
                ScramblePolicy: "Target enemy guardian to reduce HP and disrupt deposits; user requires Yuyan objective_attacker targeting enemy guardian.",
                FlagFlightPolicy: "Pressure enemy base defenders and clear path to flag stand.",
                RapidBallPolicy: "Focus damage on enemy guardian to defeat it and unlock the scoring goal."),

            // 4: healer (Kicker 2, Ruriha base)
            4 => new AiRolePolicy(
                Profile: ProfileHealer,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: 2,
                KickerCostumeId: 2020101,
                AttackMotivation: 30,
                DefenseMotivation: 60,
                ScoreMotivation: 50,
                ScoreToDeposit: mode switch { 0 => 10, 1 => 1, _ => 5 },
                AttackDashInterval: 1.2,
                DefenseDashInterval: 0.9,
                ScoreDashInterval: 1.0,
                AttackSkillInterval: 4.0,
                HealSkillInterval: 2.0,
                BuffSkillInterval: 3.5,
                TrapSkillInterval: 5.0,
                WarpSkillInterval: 4.0,
                HealDamageRate: 0.7,
                CanLockon: true,
                ScramblePolicy: "Sustain self with healing discs under damage threshold to maintain presence during crystal disputes.",
                FlagFlightPolicy: "Escort team and heal friendly flag carriers.",
                RapidBallPolicy: "Keep allied defenders and collectors alive around objective areas."),

            // 5: speed_support (Kicker 7, Grenhawk base)
            5 => new AiRolePolicy(
                Profile: ProfileSpeedSupport,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: 7,
                KickerCostumeId: 2070101,
                AttackMotivation: 60,
                DefenseMotivation: 40,
                ScoreMotivation: 60,
                ScoreToDeposit: mode switch { 0 => 10, 1 => 1, _ => 5 },
                AttackDashInterval: 0.8,
                DefenseDashInterval: 0.8,
                ScoreDashInterval: 0.8,
                AttackSkillInterval: 3.0,
                HealSkillInterval: 4.0,
                BuffSkillInterval: 3.0,
                TrapSkillInterval: 5.0,
                WarpSkillInterval: 4.0,
                HealDamageRate: 0.5,
                CanLockon: true,
                ScramblePolicy: "Apply ranged pressure and provide mobility buffs to allies.",
                FlagFlightPolicy: "Contest neutral airspace and buff allied runner mobility.",
                RapidBallPolicy: "Provide speed buffs to ball gatherers and harass enemy guardian defenders."),

            // 6: damage_support (Kicker 14, Sid base)
            6 => new AiRolePolicy(
                Profile: ProfileDamageSupport,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: 14,
                KickerCostumeId: 2140101,
                AttackMotivation: 75,
                DefenseMotivation: 35,
                ScoreMotivation: 45,
                ScoreToDeposit: mode switch { 0 => 10, 1 => 1, _ => 5 },
                AttackDashInterval: 0.9,
                DefenseDashInterval: 1.0,
                ScoreDashInterval: 1.0,
                AttackSkillInterval: 2.8,
                HealSkillInterval: 4.5,
                BuffSkillInterval: 3.0,
                TrapSkillInterval: 5.0,
                WarpSkillInterval: 4.0,
                HealDamageRate: 0.5,
                CanLockon: true,
                ScramblePolicy: "Escort friendly carriers and boost allied offensive damage output.",
                FlagFlightPolicy: "Escort flag runner and buff allied combat engagement.",
                RapidBallPolicy: "Boost damage against enemy guardian to accelerate goal unlock."),

            // 7: trap_defender (Kicker 5, Owlbert base)
            7 => new AiRolePolicy(
                Profile: ProfileTrapDefender,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: 5,
                KickerCostumeId: 2050101,
                AttackMotivation: 40,
                DefenseMotivation: 85,
                ScoreMotivation: 30,
                ScoreToDeposit: mode switch { 0 => 15, 1 => 1, _ => 10 },
                AttackDashInterval: 1.1,
                DefenseDashInterval: 0.8,
                ScoreDashInterval: 1.2,
                AttackSkillInterval: 3.5,
                HealSkillInterval: 4.5,
                BuffSkillInterval: 4.0,
                TrapSkillInterval: 2.5,
                WarpSkillInterval: 4.0,
                HealDamageRate: 0.5,
                CanLockon: true,
                ScramblePolicy: "Deploy area denial, smog, and traps around disputed chokepoints and transit lanes.",
                FlagFlightPolicy: "Place traps along flag return routes and pedestal approach angles.",
                RapidBallPolicy: "Deploy traps around friendly guardian and goal perimeter."),

            // 8: Yuyan (Specialization: objective_attacker for Kicker 10)
            8 => new AiRolePolicy(
                Profile: ProfileObjectiveAttacker,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: 10,
                KickerCostumeId: 2100101,
                AttackMotivation: mode switch { 0 => 90, 1 => 92, _ => 95 },
                DefenseMotivation: 15,
                ScoreMotivation: mode switch { 0 => 80, 1 => 75, _ => 85 },
                ScoreToDeposit: mode switch { 0 => 10, 1 => 1, _ => 5 },
                AttackDashInterval: 0.8,
                DefenseDashInterval: 1.2,
                ScoreDashInterval: 0.9,
                AttackSkillInterval: 2.2,
                HealSkillInterval: 5.0,
                BuffSkillInterval: 4.0,
                TrapSkillInterval: 5.0,
                WarpSkillInterval: 4.0,
                HealDamageRate: 0.5,
                CanLockon: true,
                ScramblePolicy: "Target enemy guardian aggressively with pull-in and high offensive pressure; user requires Yuyan objective_attacker targeting enemy guardian.",
                FlagFlightPolicy: "Aggressively assault defenders at enemy flag stand.",
                RapidBallPolicy: "Prioritize damaging enemy guardian with high burst to down it and open goal."),

            // 9: Owlbert (Specialization: trap_defender for Kicker 5)
            9 => new AiRolePolicy(
                Profile: ProfileTrapDefender,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: 5,
                KickerCostumeId: 2050101,
                AttackMotivation: 35,
                DefenseMotivation: 90,
                ScoreMotivation: 25,
                ScoreToDeposit: mode switch { 0 => 15, 1 => 1, _ => 10 },
                AttackDashInterval: 1.1,
                DefenseDashInterval: 0.8,
                ScoreDashInterval: 1.2,
                AttackSkillInterval: 3.5,
                HealSkillInterval: 4.5,
                BuffSkillInterval: 4.0,
                TrapSkillInterval: 2.0,
                WarpSkillInterval: 4.0,
                HealDamageRate: 0.5,
                CanLockon: true,
                ScramblePolicy: "Owlbert area denial: maintain continuous trap and smog coverage along key routes.",
                FlagFlightPolicy: "Owlbert trap lockdown along flag path and pedestal.",
                RapidBallPolicy: "Owlbert perimeter lockdown defending friendly revivalable guardian."),

            // 10: Ruriha (Specialization: healer for Kicker 2)
            10 => new AiRolePolicy(
                Profile: ProfileHealer,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: 2,
                KickerCostumeId: 2020101,
                AttackMotivation: 25,
                DefenseMotivation: 65,
                ScoreMotivation: 50,
                ScoreToDeposit: mode switch { 0 => 10, 1 => 1, _ => 5 },
                AttackDashInterval: 1.2,
                DefenseDashInterval: 0.8,
                ScoreDashInterval: 1.0,
                AttackSkillInterval: 4.0,
                HealSkillInterval: 1.8,
                BuffSkillInterval: 3.0,
                TrapSkillInterval: 5.0,
                WarpSkillInterval: 4.0,
                HealDamageRate: 0.75,
                CanLockon: true,
                ScramblePolicy: "Ruriha prioritized healing: maintain high personal sustain to contest crystals longer.",
                FlagFlightPolicy: "Ruriha dedicated runner support: heal flag carrier on transit.",
                RapidBallPolicy: "Ruriha dedicated objective sustain: keep guardian attackers or defenders alive."),

            // 11: Grenhawk (Specialization: speed_support for Kicker 7)
            11 => new AiRolePolicy(
                Profile: ProfileSpeedSupport,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: 7,
                KickerCostumeId: 2070101,
                AttackMotivation: 65,
                DefenseMotivation: 35,
                ScoreMotivation: 60,
                ScoreToDeposit: mode switch { 0 => 10, 1 => 1, _ => 5 },
                AttackDashInterval: 0.7,
                DefenseDashInterval: 0.8,
                ScoreDashInterval: 0.7,
                AttackSkillInterval: 2.8,
                HealSkillInterval: 4.0,
                BuffSkillInterval: 2.5,
                TrapSkillInterval: 5.0,
                WarpSkillInterval: 4.0,
                HealDamageRate: 0.5,
                CanLockon: true,
                ScramblePolicy: "Grenhawk ranged pressure and allied speed buffing.",
                FlagFlightPolicy: "Grenhawk escort: accelerate allied flag runners and apply ranged cover.",
                RapidBallPolicy: "Grenhawk mobility support: accelerate ball transport and harass defenders."),

            // 12: Sid (Specialization: damage_support for Kicker 14)
            12 => new AiRolePolicy(
                Profile: ProfileDamageSupport,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: 14,
                KickerCostumeId: 2140101,
                AttackMotivation: 80,
                DefenseMotivation: 30,
                ScoreMotivation: 40,
                ScoreToDeposit: mode switch { 0 => 10, 1 => 1, _ => 5 },
                AttackDashInterval: 0.8,
                DefenseDashInterval: 1.0,
                ScoreDashInterval: 1.0,
                AttackSkillInterval: 2.5,
                HealSkillInterval: 4.5,
                BuffSkillInterval: 2.5,
                TrapSkillInterval: 5.0,
                WarpSkillInterval: 4.0,
                HealDamageRate: 0.5,
                CanLockon: true,
                ScramblePolicy: "Sid offensive escort: maximize allied burst damage on crystal disputes and guardian pressure.",
                FlagFlightPolicy: "Sid flag escort: escort friendly runner and eliminate interceptors.",
                RapidBallPolicy: "Sid guardian assault escort: amplify damage dealt to enemy guardian."),

            _ => throw new ArgumentOutOfRangeException(nameof(prof), $"Unknown profile index {prof}")
        };
    }

    public static IReadOnlyList<AiRolePolicy> GetAllPolicies()
    {
        return PoliciesByModeAndProfile.Values.OrderBy(p => p.ParameterId).ToList();
    }

    public static AiRolePolicy? GetPolicy(string profile, int kickerId = 0, int modeIndex = 0)
    {
        var effectiveProfile = profile;
        if (kickerId == 10 && modeIndex is 0 or 2)
        {
            effectiveProfile = ProfileObjectiveAttacker;
        }
        var profIdx = GetProfileIndex(effectiveProfile, kickerId);
        if (profIdx < 0 || modeIndex < 0 || modeIndex > 2) return null;

        if (PoliciesByModeAndProfile.TryGetValue((modeIndex, profIdx), out var policy))
        {
            return policy;
        }

        return null;
    }

    public static AiRolePolicy? GetPolicy(string profile, int kickerId, BattleCompositionMode? mode)
    {
        var modeIndex = mode.HasValue ? GetModeIndex(mode.Value) : 0;
        if (modeIndex < 0) return null;
        return GetPolicy(profile, kickerId, modeIndex);
    }

    public static int ResolveParameterId(string? profile, int kickerId, BattleCompositionMode? mode = null, bool isGym = false)
    {
        // Gym bots must ALWAYS receive GymAiParameterBase + kickerId (isolated from experimental roles)
        if (isGym)
        {
            return BattleMatchmakingService.GymAiParameterBase + kickerId;
        }

        // When experimental flag is off, retain standard pristine kicker ID
        if (!IsEnabled)
        {
            return kickerId;
        }

        if (mode is null)
        {
            return kickerId;
        }

        var modeIndex = GetModeIndex(mode.Value);
        if (modeIndex < 0)
        {
            return kickerId;
        }

        var effectiveProfile = profile;
        if (kickerId == 10 && mode.Value is BattleCompositionMode.CrystalScramble or BattleCompositionMode.RapidBall)
        {
            effectiveProfile = ProfileObjectiveAttacker;
        }

        var profileIndex = GetProfileIndex(effectiveProfile, kickerId);
        if (profileIndex < 0)
        {
            return kickerId;
        }

        var id = MinRoleParamId + (13 * modeIndex) + profileIndex;
        if (id < MinRoleParamId || id > MaxAllowedParamId)
        {
            throw new InvalidOperationException($"Role parameter ID {id} outside safe range [{MinRoleParamId}..{MaxAllowedParamId}].");
        }
        return id;
    }

    public static int ResolveParameterId(string? profile, int kickerId, int battleRuleId, bool isGym = false)
    {
        if (isGym)
        {
            return BattleMatchmakingService.GymAiParameterBase + kickerId;
        }

        var mode = ScrambleBotCompositionSelector.ModeForBattleRuleId(battleRuleId);
        return ResolveParameterId(profile, kickerId, mode, isGym);
    }

    public static string AppendExperimentalRoleAiRows(string aiParamJson)
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

            foreach (var policy in GetAllPolicies())
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
