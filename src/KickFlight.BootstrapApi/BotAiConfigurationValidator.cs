namespace KickFlight.BootstrapApi;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

public sealed record BotAiValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    BotAiConfigurationRoot? Configuration)
{
    public static BotAiValidationResult Success(BotAiConfigurationRoot config) =>
        new(true, Array.Empty<string>(), config);

    public static BotAiValidationResult Failure(IEnumerable<string> errors) =>
        new(false, errors.ToList().AsReadOnly(), null);

    public BotAiProfileSnapshot ToSnapshot(string sourcePath)
    {
        if (!IsValid || Configuration is null)
        {
            throw new InvalidOperationException("Cannot create snapshot from invalid configuration.");
        }

        var policies = new List<AiRolePolicy>(42);

        if (Configuration.Profiles.Count == 42)
        {
            foreach (var p in Configuration.Profiles)
            {
                var modeIndex = p.Mode switch
                {
                    "CrystalScramble" => 0,
                    "FlagFlight" => 1,
                    "RapidBall" => 2,
                    _ => throw new InvalidOperationException($"Invalid mode '{p.Mode}'")
                };

                var (scramble, flag, rapid) = BotAiDefaults.GetKickerDescriptions(p.KickerId);

                var clampedAttack = Math.Clamp(p.Motivations.Attack, 0, 100);
                var clampedDefense = Math.Clamp(p.Motivations.Defense, 0, 100);
                var clampedScore = Math.Clamp(p.Motivations.Score, 0, 100);

                policies.Add(new AiRolePolicy(
                    Profile: p.Profile,
                    ModeIndex: modeIndex,
                    ParameterId: p.ParameterId,
                    KickerId: p.KickerId,
                    KickerCostumeId: p.KickerCostumeId,
                    AttackMotivation: clampedAttack,
                    DefenseMotivation: clampedDefense,
                    ScoreMotivation: clampedScore,
                    ScoreToDeposit: p.ScoreToDeposit,
                    AttackDashInterval: p.DashIntervals.Attack,
                    DefenseDashInterval: p.DashIntervals.Defense,
                    ScoreDashInterval: p.DashIntervals.Score,
                    AttackSkillInterval: p.SkillIntervals.Attack,
                    HealSkillInterval: p.SkillIntervals.Heal,
                    BuffSkillInterval: p.SkillIntervals.Buff,
                    TrapSkillInterval: p.SkillIntervals.Trap,
                    WarpSkillInterval: p.SkillIntervals.Warp,
                    HealDamageRate: p.HealDamageRate,
                    CanLockon: p.CanLockon,
                    ScramblePolicy: scramble,
                    FlagFlightPolicy: flag,
                    RapidBallPolicy: rapid,
                    LowHpDepositThreshold: p.LowHpDepositThreshold));
            }
        }
        else if (Configuration.Profiles.Count == 39)
        {
            // Transparent migration of legacy 39 V1 profiles into 42 independent V2 policies
            var v1Dict = new Dictionary<(string Mode, string Profile), BotAiProfileConfig>();
            foreach (var p in Configuration.Profiles)
            {
                v1Dict[(p.Mode, p.Profile)] = p;
            }

            foreach (var mode in BotAiDefaults.Modes)
            {
                var modeIndex = mode switch
                {
                    "CrystalScramble" => 0,
                    "FlagFlight" => 1,
                    "RapidBall" => 2,
                    _ => 0
                };

                for (int kid = 1; kid <= 14; kid++)
                {
                    var inheritedKey = BotAiDefaults.GetInheritedProfileKey(kid);
                    if (!v1Dict.TryGetValue((mode, inheritedKey), out var p))
                    {
                        var profSpec = BotAiDefaults.Profiles[BotAiDefaults.GetInheritedProfileIndex(kid)];
                        v1Dict.TryGetValue((mode, profSpec.Profile), out p);
                    }

                    if (p is not null)
                    {
                        var (scramble, flag, rapid) = BotAiDefaults.GetKickerDescriptions(kid);
                        var paramId = BotAiDefaults.GetStableParameterId(modeIndex, kid);
                        var costumeId = BotAiDefaults.Kickers[kid - 1].CostumeId;
                        var kickerName = BotAiDefaults.Kickers[kid - 1].Name;

                        var clampedAttack = Math.Clamp(p.Motivations.Attack, 0, 100);
                        var clampedDefense = Math.Clamp(p.Motivations.Defense, 0, 100);
                        var clampedScore = Math.Clamp(p.Motivations.Score, 0, 100);

                        policies.Add(new AiRolePolicy(
                            Profile: kickerName,
                            ModeIndex: modeIndex,
                            ParameterId: paramId,
                            KickerId: kid,
                            KickerCostumeId: costumeId,
                            AttackMotivation: clampedAttack,
                            DefenseMotivation: clampedDefense,
                            ScoreMotivation: clampedScore,
                            ScoreToDeposit: p.ScoreToDeposit,
                            AttackDashInterval: p.DashIntervals.Attack,
                            DefenseDashInterval: p.DashIntervals.Defense,
                            ScoreDashInterval: p.DashIntervals.Score,
                            AttackSkillInterval: p.SkillIntervals.Attack,
                            HealSkillInterval: p.SkillIntervals.Heal,
                            BuffSkillInterval: p.SkillIntervals.Buff,
                            TrapSkillInterval: p.SkillIntervals.Trap,
                            WarpSkillInterval: p.SkillIntervals.Warp,
                            HealDamageRate: p.HealDamageRate,
                            CanLockon: p.CanLockon,
                            ScramblePolicy: scramble,
                            FlagFlightPolicy: flag,
                            RapidBallPolicy: rapid,
                            LowHpDepositThreshold: p.LowHpDepositThreshold));
                    }
                }
            }
        }

        return new BotAiProfileSnapshot(Configuration.Version, sourcePath, DateTimeOffset.UtcNow, policies);
    }
}

public static class BotAiConfigurationValidator
{
    private static readonly HashSet<string> ValidRootKeys = new(StringComparer.Ordinal)
    {
        "$schema", "version", "profiles"
    };

    private static readonly HashSet<string> ValidProfileKeys = new(StringComparer.Ordinal)
    {
        "profile", "mode", "parameterId", "kickerId", "kickerCostumeId", "rank",
        "motivations", "scoreToDeposit", "lowHpDepositThreshold", "dashIntervals",
        "skillIntervals", "healDamageRate", "canLockon"
    };

    private static readonly HashSet<string> ValidMotivationKeys = new(StringComparer.Ordinal)
    {
        "attack", "defense", "score"
    };

    private static readonly HashSet<string> ValidDashIntervalKeys = new(StringComparer.Ordinal)
    {
        "attack", "defense", "score"
    };

    private static readonly HashSet<string> ValidSkillIntervalKeys = new(StringComparer.Ordinal)
    {
        "attack", "heal", "buff", "trap", "warp"
    };

    private static readonly Dictionary<string, int> ExpectedModeIndices = new(StringComparer.Ordinal)
    {
        ["CrystalScramble"] = 0,
        ["FlagFlight"] = 1,
        ["RapidBall"] = 2
    };

    private static readonly Dictionary<string, (int ProfIndex, int KickerId)> ExpectedLegacyProfiles = new(StringComparer.Ordinal)
    {
        ["carrier"] = (0, 8),
        ["tank_defender"] = (1, 3),
        ["hunter"] = (2, 4),
        ["objective_attacker"] = (3, 10),
        ["healer"] = (4, 2),
        ["speed_support"] = (5, 7),
        ["damage_support"] = (6, 14),
        ["trap_defender"] = (7, 5),
        ["Yuyan"] = (8, 10),
        ["Owlbert"] = (9, 5),
        ["Ruriha"] = (10, 2),
        ["Grenhawk"] = (11, 7),
        ["Sid"] = (12, 14)
    };

    public static BotAiValidationResult Validate(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return BotAiValidationResult.Failure(["JSON content is empty or whitespace."]);
        }

        var errors = new List<string>();

        // 1. Strict schema & unknown fields check via JsonDocument
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return BotAiValidationResult.Failure([$"Malformed JSON: {ex.Message}"]);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return BotAiValidationResult.Failure(["JSON root must be an object."]);
            }

            CheckUnknownFields(doc.RootElement, ValidRootKeys, "root", errors);

            if (doc.RootElement.TryGetProperty("profiles", out var profilesElement))
            {
                if (profilesElement.ValueKind != JsonValueKind.Array)
                {
                    errors.Add("'profiles' property must be an array.");
                }
                else
                {
                    int idx = 0;
                    foreach (var p in profilesElement.EnumerateArray())
                    {
                        var prefix = $"profiles[{idx}]";
                        if (p.ValueKind != JsonValueKind.Object)
                        {
                            errors.Add($"{prefix} must be an object.");
                        }
                        else
                        {
                            CheckUnknownFields(p, ValidProfileKeys, prefix, errors);
                            if (p.TryGetProperty("motivations", out var mot) && mot.ValueKind == JsonValueKind.Object)
                            {
                                CheckUnknownFields(mot, ValidMotivationKeys, $"{prefix}.motivations", errors);
                            }
                            if (p.TryGetProperty("dashIntervals", out var dash) && dash.ValueKind == JsonValueKind.Object)
                            {
                                CheckUnknownFields(dash, ValidDashIntervalKeys, $"{prefix}.dashIntervals", errors);
                            }
                            if (p.TryGetProperty("skillIntervals", out var skill) && skill.ValueKind == JsonValueKind.Object)
                            {
                                CheckUnknownFields(skill, ValidSkillIntervalKeys, $"{prefix}.skillIntervals", errors);
                            }
                        }
                        idx++;
                    }
                }
            }
            else
            {
                errors.Add("Missing required property 'profiles'.");
            }
        }

        // 2. Deserialize into strongly-typed records
        BotAiConfigurationRoot? root;
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = false,
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
            };
            root = JsonSerializer.Deserialize<BotAiConfigurationRoot>(json, options);
        }
        catch (Exception ex)
        {
            errors.Add($"Deserialization failed: {ex.Message}");
            return BotAiValidationResult.Failure(errors);
        }

        if (root is null)
        {
            errors.Add("Deserialized root configuration was null.");
            return BotAiValidationResult.Failure(errors);
        }

        // 3. Version validation
        if (root.Version < 1)
        {
            errors.Add($"Invalid version {root.Version}; version must be >= 1.");
        }

        // 4. Profiles collection validation
        if (root.Profiles is null)
        {
            errors.Add("Profiles collection is null.");
            return BotAiValidationResult.Failure(errors);
        }

        bool isV2 = root.Profiles.Count == 42;
        bool isV1 = root.Profiles.Count == 39;

        if (!isV2 && !isV1)
        {
            errors.Add($"Expected exactly 42 profiles (3 modes x 14 kickers) or legacy 39 profiles, but found {root.Profiles.Count}.");
            return BotAiValidationResult.Failure(errors);
        }

        var coveredKickerKeys = new HashSet<(string Mode, int KickerId)>();
        var coveredLegacyKeys = new HashSet<(string Mode, string Profile)>();
        var seenParamIds = new HashSet<int>();

        for (int i = 0; i < root.Profiles.Count; i++)
        {
            var p = root.Profiles[i];
            var prefix = $"Profile[{i}]";

            if (p is null)
            {
                errors.Add($"{prefix} is null.");
                continue;
            }

            // Mode check
            if (string.IsNullOrWhiteSpace(p.Mode) || !ExpectedModeIndices.TryGetValue(p.Mode, out var modeIndex))
            {
                errors.Add($"{prefix}: invalid or missing mode '{p.Mode}'. Expected one of [CrystalScramble, FlagFlight, RapidBall].");
                modeIndex = -1;
            }

            if (isV2)
            {
                // V2: Per-kicker validation
                if (p.KickerId < 1 || p.KickerId > 14)
                {
                    errors.Add($"{prefix}: kickerId {p.KickerId} is outside valid range [1..14].");
                }
                else if (modeIndex >= 0)
                {
                    if (!coveredKickerKeys.Add((p.Mode, p.KickerId)))
                    {
                        errors.Add($"{prefix}: duplicate coverage for mode '{p.Mode}' and kickerId {p.KickerId}.");
                    }

                    var canonicalParamId = BotAiDefaults.GetStableParameterId(modeIndex, p.KickerId);
                    if (p.ParameterId != canonicalParamId)
                    {
                        errors.Add($"{prefix} ({p.Mode}/kicker {p.KickerId}): parameterId {p.ParameterId} does not match canonical formula {canonicalParamId}.");
                    }
                }
            }
            else
            {
                // V1: Legacy 39 profile validation
                if (string.IsNullOrWhiteSpace(p.Profile) || !ExpectedLegacyProfiles.TryGetValue(p.Profile, out var profSpec))
                {
                    errors.Add($"{prefix}: invalid or missing profile '{p.Profile}'.");
                    profSpec = (-1, -1);
                }

                var (profIndex, expectedKickerId) = profSpec;

                if (modeIndex >= 0 && profIndex >= 0)
                {
                    if (!coveredLegacyKeys.Add((p.Mode, p.Profile)))
                    {
                        errors.Add($"{prefix}: duplicate coverage for mode '{p.Mode}' and profile '{p.Profile}'.");
                    }

                    var canonicalParamId = ExperimentalAiRoleService.MinRoleParamId + (13 * modeIndex) + profIndex;
                    if (p.ParameterId != canonicalParamId)
                    {
                        errors.Add($"{prefix} ({p.Mode}/{p.Profile}): parameterId {p.ParameterId} does not match canonical formula {canonicalParamId} (15 + 13*{modeIndex} + {profIndex}).");
                    }
                }

                if (expectedKickerId > 0 && p.KickerId != expectedKickerId)
                {
                    errors.Add($"{prefix} ({p.Profile}): kickerId {p.KickerId} does not match expected kickerId {expectedKickerId}.");
                }
            }

            // ParameterId range and uniqueness
            if (p.ParameterId < ExperimentalAiRoleService.MinRoleParamId || p.ParameterId > ExperimentalAiRoleService.MaxAllowedParamId)
            {
                errors.Add($"{prefix}: parameterId {p.ParameterId} outside allowed range [{ExperimentalAiRoleService.MinRoleParamId}..{ExperimentalAiRoleService.MaxAllowedParamId}].");
            }

            if (p.ParameterId >= 1 && p.ParameterId <= 14)
            {
                errors.Add($"{prefix}: parameterId {p.ParameterId} overlaps with pristine kicker ID [1..14] (reserved for feature OFF).");
            }

            if (p.ParameterId >= BattleMatchmakingService.GymAiParameterBase)
            {
                errors.Add($"{prefix}: parameterId {p.ParameterId} violates gym isolation boundary (>={BattleMatchmakingService.GymAiParameterBase}).");
            }

            if (!seenParamIds.Add(p.ParameterId))
            {
                errors.Add($"{prefix}: duplicate parameterId {p.ParameterId}.");
            }

            // Rank == 13
            if (p.Rank != 13)
            {
                errors.Add($"{prefix}: rank {p.Rank} is invalid. Rank must be exactly 13.");
            }

            // ScoreToDeposit >= 1
            if (p.ScoreToDeposit < 1)
            {
                errors.Add($"{prefix}: scoreToDeposit {p.ScoreToDeposit} must be >= 1.");
            }

            // Motivations check
            if (p.Motivations is null)
            {
                errors.Add($"{prefix}: motivations object is missing.");
            }

            // lowHpDepositThreshold: finite float in [0.0..1.0]
            if (!double.IsFinite(p.LowHpDepositThreshold) || p.LowHpDepositThreshold < 0.0 || p.LowHpDepositThreshold > 1.0)
            {
                errors.Add($"{prefix}: lowHpDepositThreshold {p.LowHpDepositThreshold} must be a finite float in [0.0..1.0].");
            }

            // HealDamageRate: finite float in [0.0..1.0]
            if (!double.IsFinite(p.HealDamageRate) || p.HealDamageRate < 0.0 || p.HealDamageRate > 1.0)
            {
                errors.Add($"{prefix}: healDamageRate {p.HealDamageRate} must be a finite float in [0.0..1.0].");
            }

            // DashIntervals: positive and finite
            if (p.DashIntervals is null)
            {
                errors.Add($"{prefix}: dashIntervals object is missing.");
            }
            else
            {
                if (!double.IsFinite(p.DashIntervals.Attack) || p.DashIntervals.Attack <= 0.0)
                    errors.Add($"{prefix}: dashIntervals.attack {p.DashIntervals.Attack} must be positive finite float.");
                if (!double.IsFinite(p.DashIntervals.Defense) || p.DashIntervals.Defense <= 0.0)
                    errors.Add($"{prefix}: dashIntervals.defense {p.DashIntervals.Defense} must be positive finite float.");
                if (!double.IsFinite(p.DashIntervals.Score) || p.DashIntervals.Score <= 0.0)
                    errors.Add($"{prefix}: dashIntervals.score {p.DashIntervals.Score} must be positive finite float.");
            }

            // SkillIntervals: positive and finite
            if (p.SkillIntervals is null)
            {
                errors.Add($"{prefix}: skillIntervals object is missing.");
            }
            else
            {
                if (!double.IsFinite(p.SkillIntervals.Attack) || p.SkillIntervals.Attack <= 0.0)
                    errors.Add($"{prefix}: skillIntervals.attack {p.SkillIntervals.Attack} must be positive finite float.");
                if (!double.IsFinite(p.SkillIntervals.Heal) || p.SkillIntervals.Heal <= 0.0)
                    errors.Add($"{prefix}: skillIntervals.heal {p.SkillIntervals.Heal} must be positive finite float.");
                if (!double.IsFinite(p.SkillIntervals.Buff) || p.SkillIntervals.Buff <= 0.0)
                    errors.Add($"{prefix}: skillIntervals.buff {p.SkillIntervals.Buff} must be positive finite float.");
                if (!double.IsFinite(p.SkillIntervals.Trap) || p.SkillIntervals.Trap <= 0.0)
                    errors.Add($"{prefix}: skillIntervals.trap {p.SkillIntervals.Trap} must be positive finite float.");
                if (!double.IsFinite(p.SkillIntervals.Warp) || p.SkillIntervals.Warp <= 0.0)
                    errors.Add($"{prefix}: skillIntervals.warp {p.SkillIntervals.Warp} must be positive finite float.");
            }
        }

        // Completeness checks
        if (isV2)
        {
            foreach (var mode in ExpectedModeIndices.Keys)
            {
                for (int kid = 1; kid <= 14; kid++)
                {
                    if (!coveredKickerKeys.Contains((mode, kid)))
                    {
                        errors.Add($"Missing required profile entry for mode '{mode}' and kickerId {kid}.");
                    }
                }
            }
        }
        else
        {
            foreach (var mode in ExpectedModeIndices.Keys)
            {
                foreach (var prof in ExpectedLegacyProfiles.Keys)
                {
                    if (!coveredLegacyKeys.Contains((mode, prof)))
                    {
                        errors.Add($"Missing required profile entry for mode '{mode}' and profile '{prof}'.");
                    }
                }
            }
        }

        return errors.Count == 0
            ? BotAiValidationResult.Success(root)
            : BotAiValidationResult.Failure(errors);
    }

    private static void CheckUnknownFields(JsonElement element, HashSet<string> allowedKeys, string path, List<string> errors)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowedKeys.Contains(property.Name))
            {
                errors.Add($"Unknown or unmapped property '{property.Name}' at {path}.");
            }
        }
    }
}
