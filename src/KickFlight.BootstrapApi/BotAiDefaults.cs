namespace KickFlight.BootstrapApi;

using System;
using System.Collections.Generic;

/// <summary>
/// Hardcoded default AI policies matching the canonical 39-profile contract.
/// Used for initial fallback when config file is missing or invalid.
/// </summary>
public static class BotAiDefaults
{
    public static readonly string[] Modes = ["CrystalScramble", "FlagFlight", "RapidBall"];

    public static readonly (string Profile, int KickerId, int CostumeId)[] Profiles =
    [
        (ExperimentalAiRoleService.ProfileCarrier, 8, 2080101),
        (ExperimentalAiRoleService.ProfileTankDefender, 3, 2030101),
        (ExperimentalAiRoleService.ProfileHunter, 4, 2040101),
        (ExperimentalAiRoleService.ProfileObjectiveAttacker, 10, 2100101),
        (ExperimentalAiRoleService.ProfileHealer, 2, 2020101),
        (ExperimentalAiRoleService.ProfileSpeedSupport, 7, 2070101),
        (ExperimentalAiRoleService.ProfileDamageSupport, 14, 2140101),
        (ExperimentalAiRoleService.ProfileTrapDefender, 5, 2050101),
        (ExperimentalAiRoleService.ProfileYuyan, 10, 2100101),
        (ExperimentalAiRoleService.ProfileOwlbert, 5, 2050101),
        (ExperimentalAiRoleService.ProfileRuriha, 2, 2020101),
        (ExperimentalAiRoleService.ProfileGrenhawk, 7, 2070101),
        (ExperimentalAiRoleService.ProfileSid, 14, 2140101)
    ];

    public static (string Scramble, string FlagFlight, string RapidBall) GetDescriptions(int prof) => prof switch
    {
        0 => ("Collect crystals; deposit at friendly guardian when crystals >= scoreToDeposit; Ana escapes evasively.",
              "Retrieve designated flag from pedestal or carrier; fly evasively to own team base stand.",
              "Gather scattered balls; deposit into open goal once enemy guardian is downed."),
        1 => ("Engage approaching enemy attackers in combat zone using threat-based self-defense.",
              "Guard friendly flag pedestal and escort allied flag carrier.",
              "Guard friendly guardian to prevent goal from opening."),
        2 => ("Hunt and eliminate enemy crystal carriers and collectors.",
              "Intercept and eliminate opposing runners carrying the flag.",
              "Eliminate enemy defenders and ball collectors."),
        3 => ("Target enemy guardian to reduce HP and disrupt deposits; user requires Yuyan objective_attacker targeting enemy guardian.",
              "Pressure enemy base defenders and clear path to flag stand.",
              "Focus damage on enemy guardian to defeat it and unlock the scoring goal."),
        4 => ("Sustain self with healing discs under damage threshold to maintain presence during crystal disputes.",
              "Escort team and heal friendly flag carriers.",
              "Keep allied defenders and collectors alive around objective areas."),
        5 => ("Apply ranged pressure and provide mobility buffs to allies.",
              "Contest neutral airspace and buff allied runner mobility.",
              "Provide speed buffs to ball gatherers and harass enemy guardian defenders."),
        6 => ("Escort friendly carriers and boost allied offensive damage output.",
              "Escort flag runner and buff allied combat engagement.",
              "Boost damage against enemy guardian to accelerate goal unlock."),
        7 => ("Deploy area denial, smog, and traps around disputed chokepoints and transit lanes.",
              "Place traps along flag return routes and pedestal approach angles.",
              "Deploy traps around friendly guardian and goal perimeter."),
        8 => ("Target enemy guardian aggressively with pull-in and high offensive pressure; user requires Yuyan objective_attacker targeting enemy guardian.",
              "Aggressively assault defenders at enemy flag stand.",
              "Prioritize damaging enemy guardian with high burst to down it and open goal."),
        9 => ("Owlbert area denial: maintain continuous trap and smog coverage along key routes.",
              "Owlbert trap lockdown along flag path and pedestal.",
              "Owlbert perimeter lockdown defending friendly revivalable guardian."),
        10 => ("Ruriha prioritized healing: maintain high personal sustain to contest crystals longer.",
               "Ruriha dedicated runner support: heal flag carrier on transit.",
               "Ruriha dedicated objective sustain: keep guardian attackers or defenders alive."),
        11 => ("Grenhawk ranged pressure and allied speed buffing.",
               "Grenhawk escort: accelerate allied flag runners and apply ranged cover.",
               "Grenhawk mobility support: accelerate ball transport and harass defenders."),
        12 => ("Sid offensive escort: maximize allied burst damage on crystal disputes and guardian pressure.",
               "Sid flag escort: escort friendly runner and eliminate interceptors.",
               "Sid guardian assault escort: amplify damage dealt to enemy guardian."),
        _ => ("", "", "")
    };

    public static AiRolePolicy BuildDefaultPolicy(int mode, int prof, int paramId)
    {
        var (pname, kickerId, costumeId) = Profiles[prof];
        var (scramble, flag, rapid) = GetDescriptions(prof);

        return prof switch
        {
            // 0: carrier (Kicker 8, Ana)
            0 => new AiRolePolicy(
                Profile: pname,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: kickerId,
                KickerCostumeId: costumeId,
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
                ScramblePolicy: scramble,
                FlagFlightPolicy: flag,
                RapidBallPolicy: rapid,
                LowHpDepositThreshold: 0.35),

            // 1: tank_defender (Kicker 3, Coco)
            1 => new AiRolePolicy(
                Profile: pname,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: kickerId,
                KickerCostumeId: costumeId,
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
                ScramblePolicy: scramble,
                FlagFlightPolicy: flag,
                RapidBallPolicy: rapid,
                LowHpDepositThreshold: 0.35),

            // 2: hunter (Kicker 4, Kite archetype)
            2 => new AiRolePolicy(
                Profile: pname,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: kickerId,
                KickerCostumeId: costumeId,
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
                ScramblePolicy: scramble,
                FlagFlightPolicy: flag,
                RapidBallPolicy: rapid,
                LowHpDepositThreshold: 0.35),

            // 3: objective_attacker (Kicker 10, Yuyan base)
            3 => new AiRolePolicy(
                Profile: pname,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: kickerId,
                KickerCostumeId: costumeId,
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
                ScramblePolicy: scramble,
                FlagFlightPolicy: flag,
                RapidBallPolicy: rapid,
                LowHpDepositThreshold: 0.35),

            // 4: healer (Kicker 2, Ruriha base)
            4 => new AiRolePolicy(
                Profile: pname,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: kickerId,
                KickerCostumeId: costumeId,
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
                ScramblePolicy: scramble,
                FlagFlightPolicy: flag,
                RapidBallPolicy: rapid,
                LowHpDepositThreshold: 0.35),

            // 5: speed_support (Kicker 7, Grenhawk base)
            5 => new AiRolePolicy(
                Profile: pname,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: kickerId,
                KickerCostumeId: costumeId,
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
                ScramblePolicy: scramble,
                FlagFlightPolicy: flag,
                RapidBallPolicy: rapid,
                LowHpDepositThreshold: 0.35),

            // 6: damage_support (Kicker 14, Sid base)
            6 => new AiRolePolicy(
                Profile: pname,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: kickerId,
                KickerCostumeId: costumeId,
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
                ScramblePolicy: scramble,
                FlagFlightPolicy: flag,
                RapidBallPolicy: rapid,
                LowHpDepositThreshold: 0.35),

            // 7: trap_defender (Kicker 5, Owlbert base)
            7 => new AiRolePolicy(
                Profile: pname,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: kickerId,
                KickerCostumeId: costumeId,
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
                ScramblePolicy: scramble,
                FlagFlightPolicy: flag,
                RapidBallPolicy: rapid,
                LowHpDepositThreshold: 0.35),

            // 8: Yuyan (Specialization: objective_attacker for Kicker 10)
            8 => new AiRolePolicy(
                Profile: pname,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: kickerId,
                KickerCostumeId: costumeId,
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
                ScramblePolicy: scramble,
                FlagFlightPolicy: flag,
                RapidBallPolicy: rapid,
                LowHpDepositThreshold: 0.35),

            // 9: Owlbert (Specialization: trap_defender for Kicker 5)
            9 => new AiRolePolicy(
                Profile: pname,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: kickerId,
                KickerCostumeId: costumeId,
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
                ScramblePolicy: scramble,
                FlagFlightPolicy: flag,
                RapidBallPolicy: rapid,
                LowHpDepositThreshold: 0.35),

            // 10: Ruriha (Specialization: healer for Kicker 2)
            10 => new AiRolePolicy(
                Profile: pname,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: kickerId,
                KickerCostumeId: costumeId,
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
                ScramblePolicy: scramble,
                FlagFlightPolicy: flag,
                RapidBallPolicy: rapid,
                LowHpDepositThreshold: 0.35),

            // 11: Grenhawk (Specialization: speed_support for Kicker 7)
            11 => new AiRolePolicy(
                Profile: pname,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: kickerId,
                KickerCostumeId: costumeId,
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
                ScramblePolicy: scramble,
                FlagFlightPolicy: flag,
                RapidBallPolicy: rapid,
                LowHpDepositThreshold: 0.35),

            // 12: Sid (Specialization: damage_support for Kicker 14)
            12 => new AiRolePolicy(
                Profile: pname,
                ModeIndex: mode,
                ParameterId: paramId,
                KickerId: kickerId,
                KickerCostumeId: costumeId,
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
                ScramblePolicy: scramble,
                FlagFlightPolicy: flag,
                RapidBallPolicy: rapid,
                LowHpDepositThreshold: 0.35),

            _ => throw new ArgumentOutOfRangeException(nameof(prof), $"Unknown profile index {prof}")
        };
    }

    public static List<AiRolePolicy> CreateDefaultPolicies()
    {
        var policies = new List<AiRolePolicy>(39);
        for (int mode = 0; mode <= 2; mode++)
        {
            for (int prof = 0; prof <= 12; prof++)
            {
                var id = ExperimentalAiRoleService.MinRoleParamId + (13 * mode) + prof;
                policies.Add(BuildDefaultPolicy(mode, prof, id));
            }
        }
        return policies;
    }

    public static BotAiProfileSnapshot CreateDefaultSnapshot(string source = "hardcoded-defaults") =>
        new(version: 1, sourcePath: source, loadedAt: DateTimeOffset.UtcNow, policies: CreateDefaultPolicies());
}
