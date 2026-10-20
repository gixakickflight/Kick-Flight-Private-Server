namespace KickFlight.BootstrapApi;

using System;
using System.Collections.Generic;

/// <summary>
/// Hardcoded default AI policies matching the independent 42-policy contract (14 kickers x 3 modes).
/// Used for initial fallback when config file is missing or invalid.
/// </summary>
public static class BotAiDefaults
{
    public static readonly string[] Modes = ["CrystalScramble", "FlagFlight", "RapidBall"];

    public static readonly (int KickerId, string Name, int CostumeId)[] Kickers =
    [
        (1, "Tsubame", 2010101),
        (2, "Ruriha", 2020101),
        (3, "Coco", 2030101),
        (4, "Kite", 2040101),
        (5, "Owlbert", 2050101),
        (6, "Pitophy", 2060101),
        (7, "Grenhawk", 2070101),
        (8, "Anna", 2080101),
        (9, "Jay", 2090101),
        (10, "Yuyan", 2100101),
        (11, "Diatrius", 2110101),
        (12, "Buzzy Big", 2120101),
        (13, "Hitagi", 2130101),
        (14, "Sid", 2140101)
    ];

    // Legacy 13-profile contract maintained for backwards compatibility
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

    public static int GetInheritedProfileIndex(int kickerId) => kickerId switch
    {
        1 => 0,   // Tsubame -> carrier
        2 => 10,  // Ruriha -> Ruriha
        3 => 1,   // Coco -> tank_defender
        4 => 2,   // Kite -> hunter
        5 => 9,   // Owlbert -> Owlbert
        6 => 2,   // Pitophy -> hunter
        7 => 11,  // Grenhawk -> Grenhawk
        8 => 0,   // Anna -> carrier
        9 => 0,   // Jay -> carrier
        10 => 8,  // Yuyan -> Yuyan
        11 => 1,  // Diatrius -> tank_defender
        12 => 1,  // Buzzy Big -> tank_defender
        13 => 2,  // Hitagi -> hunter
        14 => 12, // Sid -> Sid
        _ => throw new ArgumentOutOfRangeException(nameof(kickerId), $"Unknown kicker ID {kickerId}")
    };

    public static string GetInheritedProfileKey(int kickerId) => kickerId switch
    {
        1 => ExperimentalAiRoleService.ProfileCarrier,
        2 => ExperimentalAiRoleService.ProfileRuriha,
        3 => ExperimentalAiRoleService.ProfileTankDefender,
        4 => ExperimentalAiRoleService.ProfileHunter,
        5 => ExperimentalAiRoleService.ProfileOwlbert,
        6 => ExperimentalAiRoleService.ProfileHunter,
        7 => ExperimentalAiRoleService.ProfileGrenhawk,
        8 => ExperimentalAiRoleService.ProfileCarrier,
        9 => ExperimentalAiRoleService.ProfileCarrier,
        10 => ExperimentalAiRoleService.ProfileYuyan,
        11 => ExperimentalAiRoleService.ProfileTankDefender,
        12 => ExperimentalAiRoleService.ProfileTankDefender,
        13 => ExperimentalAiRoleService.ProfileHunter,
        14 => ExperimentalAiRoleService.ProfileSid,
        _ => throw new ArgumentOutOfRangeException(nameof(kickerId), $"Unknown kicker ID {kickerId}")
    };

    public static int GetStableParameterId(int modeIndex, int kickerId) => (modeIndex, kickerId) switch
    {
        // 0: CrystalScramble
        (0, 1) => 54, (0, 2) => 25, (0, 3) => 16, (0, 4) => 17,
        (0, 5) => 24, (0, 6) => 55, (0, 7) => 26, (0, 8) => 15,
        (0, 9) => 56, (0, 10) => 23, (0, 11) => 57, (0, 12) => 58,
        (0, 13) => 59, (0, 14) => 27,

        // 1: FlagFlight
        (1, 1) => 60, (1, 2) => 38, (1, 3) => 29, (1, 4) => 30,
        (1, 5) => 37, (1, 6) => 61, (1, 7) => 39, (1, 8) => 28,
        (1, 9) => 62, (1, 10) => 36, (1, 11) => 63, (1, 12) => 64,
        (1, 13) => 65, (1, 14) => 40,

        // 2: RapidBall
        (2, 1) => 66, (2, 2) => 51, (2, 3) => 42, (2, 4) => 43,
        (2, 5) => 50, (2, 6) => 67, (2, 7) => 52, (2, 8) => 41,
        (2, 9) => 68, (2, 10) => 49, (2, 11) => 69, (2, 12) => 70,
        (2, 13) => 71, (2, 14) => 53,

        _ => throw new ArgumentOutOfRangeException($"Invalid modeIndex {modeIndex} or kickerId {kickerId}")
    };

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

    public static (string Scramble, string FlagFlight, string RapidBall) GetKickerDescriptions(int kickerId) => kickerId switch
    {
        1 => ("Tsubame aerial mobility: swift crystal collection and agile evasion while carrying score.",
              "Tsubame flag runner: dash between aerial lanes to retrieve and deposit flag swiftly.",
              "Tsubame ball runner: intercept loose balls and maneuver toward open goal."),
        2 => ("Ruriha prioritized healing: maintain high personal sustain to contest crystals longer.",
              "Ruriha dedicated runner support: heal flag carrier on transit.",
              "Ruriha dedicated objective sustain: keep guardian attackers or defenders alive."),
        3 => ("Engage approaching enemy attackers in combat zone using threat-based self-defense.",
              "Guard friendly flag pedestal and escort allied flag carrier.",
              "Guard friendly guardian to prevent goal from opening."),
        4 => ("Hunt and eliminate enemy crystal carriers and collectors.",
              "Intercept and eliminate opposing runners carrying the flag.",
              "Eliminate enemy defenders and ball collectors."),
        5 => ("Owlbert area denial: maintain continuous trap and smog coverage along key routes.",
              "Owlbert trap lockdown along flag path and pedestal.",
              "Owlbert perimeter lockdown defending friendly revivalable guardian."),
        6 => ("Pitophy aggressive hunt: pursue enemy crystal collectors with ranged burst.",
              "Pitophy pursuit: intercept enemy flag carriers in open flight.",
              "Pitophy strike: eliminate enemy ball runners and guardian defenders."),
        7 => ("Grenhawk ranged pressure and allied speed buffing.",
              "Grenhawk escort: accelerate allied flag runners and apply ranged cover.",
              "Grenhawk mobility support: accelerate ball transport and harass defenders."),
        8 => ("Collect crystals; deposit at friendly guardian when crystals >= scoreToDeposit; Ana escapes evasively.",
              "Retrieve designated flag from pedestal or carrier; fly evasively to own team base stand.",
              "Gather scattered balls; deposit into open goal once enemy guardian is downed."),
        9 => ("Jay high-speed collection: rapid crystal collection and trap-assisted evasion.",
              "Jay evasive runner: fast flag retrieval with defensive burst.",
              "Jay mobile transport: gather balls at speed and deliver into enemy goal."),
        10 => ("Target enemy guardian aggressively with pull-in and high offensive pressure; user requires Yuyan objective_attacker targeting enemy guardian.",
               "Aggressively assault defenders at enemy flag stand.",
               "Prioritize damaging enemy guardian with high burst to down it and open goal."),
        11 => ("Diatrius bastion: hold disputed crystal zones with sustained defense.",
               "Diatrius flag guard: anchor friendly pedestal and block hostile runners.",
               "Diatrius goal anchor: shield friendly guardian against hostile assault."),
        12 => ("Buzzy Big barrier: deploy front shields to deny enemy crystal access and protect allies.",
               "Buzzy Big escort: front barrier cover for allied flag carriers.",
               "Buzzy Big defender: absorb fire around friendly guardian and goal mouth."),
        13 => ("Hitagi combat assassin: eliminate high-value carriers with sudden warp and melee burst.",
               "Hitagi interceptor: ambushes enemy flag runners from unexpected angles.",
               "Hitagi defense breaker: strike enemy defenders around the goal perimeter."),
        14 => ("Sid offensive escort: maximize allied burst damage on crystal disputes and guardian pressure.",
               "Sid flag escort: escort friendly runner and eliminate interceptors.",
               "Sid guardian assault escort: amplify damage dealt to enemy guardian."),
        _ => ("", "", "")
    };

    public static AiRolePolicy BuildKickerPolicy(int mode, int kickerId, int? paramId = null)
    {
        if (kickerId < 1 || kickerId > 14)
        {
            throw new ArgumentOutOfRangeException(nameof(kickerId), $"Unknown kicker ID {kickerId}");
        }

        var (kid, name, costumeId) = Kickers[kickerId - 1];
        var pid = paramId ?? GetStableParameterId(mode, kickerId);
        var (scramble, flag, rapid) = GetKickerDescriptions(kickerId);

        var prof = GetInheritedProfileIndex(kickerId);
        var basePolicy = BuildDefaultPolicy(mode, prof, pid);

        return new AiRolePolicy(
            Profile: name,
            ModeIndex: mode,
            ParameterId: pid,
            KickerId: kid,
            KickerCostumeId: costumeId,
            AttackMotivation: basePolicy.AttackMotivation,
            DefenseMotivation: basePolicy.DefenseMotivation,
            ScoreMotivation: basePolicy.ScoreMotivation,
            ScoreToDeposit: basePolicy.ScoreToDeposit,
            AttackDashInterval: basePolicy.AttackDashInterval,
            DefenseDashInterval: basePolicy.DefenseDashInterval,
            ScoreDashInterval: basePolicy.ScoreDashInterval,
            AttackSkillInterval: basePolicy.AttackSkillInterval,
            HealSkillInterval: basePolicy.HealSkillInterval,
            BuffSkillInterval: basePolicy.BuffSkillInterval,
            TrapSkillInterval: basePolicy.TrapSkillInterval,
            WarpSkillInterval: basePolicy.WarpSkillInterval,
            HealDamageRate: basePolicy.HealDamageRate,
            CanLockon: basePolicy.CanLockon,
            ScramblePolicy: scramble,
            FlagFlightPolicy: flag,
            RapidBallPolicy: rapid,
            LowHpDepositThreshold: 0.35);
    }

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
        var policies = new List<AiRolePolicy>(42);
        for (int mode = 0; mode <= 2; mode++)
        {
            for (int kid = 1; kid <= 14; kid++)
            {
                policies.Add(BuildKickerPolicy(mode, kid));
            }
        }
        return policies;
    }

    public static BotAiProfileSnapshot CreateDefaultSnapshot(string source = "hardcoded-defaults") =>
        new(version: 1, sourcePath: source, loadedAt: DateTimeOffset.UtcNow, policies: CreateDefaultPolicies());
}
