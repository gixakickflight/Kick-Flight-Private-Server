namespace KickFlight.BootstrapApi.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using KickFlight.BootstrapApi;
using Xunit;

[Collection("Matchmaking static state")]
public class BotAiConfigurationValidatorTests
{
    private readonly string _validJson;

    public BotAiConfigurationValidatorTests()
    {
        var repoRoot = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        var configPath = Path.Combine(repoRoot, "config", "bot_ai_profiles.json");
        _validJson = File.ReadAllText(configPath);
    }

    [Fact]
    public void ValidConfig_PassesValidation_WithCompleteCoverage()
    {
        var result = BotAiConfigurationValidator.Validate(_validJson);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Empty(result.Errors);
        Assert.NotNull(result.Configuration);
        Assert.Equal(1, result.Configuration.Version);
        Assert.Equal(42, result.Configuration.Profiles.Count);

        var snapshot = result.ToSnapshot("test-path");
        Assert.Equal(42, snapshot.AllPolicies.Count);
        Assert.Equal(15, snapshot.AllPolicies.Min(p => p.ParameterId));
        Assert.Equal(71, snapshot.AllPolicies.Max(p => p.ParameterId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-10)]
    public void InvalidVersion_Rejected(int invalidVersion)
    {
        var node = JsonNode.Parse(_validJson)!;
        node["version"] = invalidVersion;

        var result = BotAiConfigurationValidator.Validate(node.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("version", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingVersion_Rejected()
    {
        var node = JsonNode.Parse(_validJson)!.AsObject();
        node.Remove("version");

        var result = BotAiConfigurationValidator.Validate(node.ToJsonString());
        Assert.False(result.IsValid);
    }

    [Fact]
    public void ProfileCountLessThan39_Rejected()
    {
        var node = JsonNode.Parse(_validJson)!;
        var arr = node["profiles"]!.AsArray();
        arr.RemoveAt(arr.Count - 1); // 38 items

        var result = BotAiConfigurationValidator.Validate(node.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("39 profiles", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProfileCountGreaterThan39_Rejected()
    {
        var node = JsonNode.Parse(_validJson)!;
        var arr = node["profiles"]!.AsArray();
        arr.Add(arr[0]!.DeepClone()); // 40 items

        var result = BotAiConfigurationValidator.Validate(node.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("39 profiles", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingKeyCoverage_Rejected()
    {
        var node = JsonNode.Parse(_validJson)!;
        var arr = node["profiles"]!.AsArray();
        // Replace profile at index 12 (Sid in CrystalScramble) with another copy of profile 0
        arr[12] = arr[0]!.DeepClone();

        var result = BotAiConfigurationValidator.Validate(node.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Missing required profile entry", StringComparison.OrdinalIgnoreCase)
                                         || e.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(14)] // below 15
    [InlineData(54)] // above 53
    [InlineData(99)] // below gym but outside 15..53
    [InlineData(100)] // gym mannequin boundary
    [InlineData(108)] // gym Ana
    public void ParameterIdOutOfRange_Rejected(int invalidId)
    {
        var node = JsonNode.Parse(_validJson)!;
        node["profiles"]![0]!["parameterId"] = invalidId;

        var result = BotAiConfigurationValidator.Validate(node.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("parameterId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CanonicalFormulaMismatch_Rejected()
    {
        var node = JsonNode.Parse(_validJson)!;
        // Swap parameterId between profile 0 and 1
        node["profiles"]![0]!["parameterId"] = 16;
        node["profiles"]![1]!["parameterId"] = 15;

        var result = BotAiConfigurationValidator.Validate(node.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("canonical formula", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void KickerIdentityMismatch_Rejected()
    {
        var node = JsonNode.Parse(_validJson)!;
        // carrier must be kicker 8; change to kicker 3
        node["profiles"]![0]!["kickerId"] = 3;

        var result = BotAiConfigurationValidator.Validate(node.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("kickerId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RankNot13_Rejected()
    {
        var node = JsonNode.Parse(_validJson)!;
        node["profiles"]![0]!["rank"] = 12;

        var result = BotAiConfigurationValidator.Validate(node.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("rank", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Motivations_ClampedToZeroAndHundred_PerRequest()
    {
        var node = JsonNode.Parse(_validJson)!;
        node["profiles"]![0]!["motivations"]!["attack"] = -25;
        node["profiles"]![0]!["motivations"]!["defense"] = 150;
        node["profiles"]![0]!["motivations"]!["score"] = 50;

        var result = BotAiConfigurationValidator.Validate(node.ToJsonString());
        Assert.True(result.IsValid, string.Join("; ", result.Errors));

        var snapshot = result.ToSnapshot("test-path");
        var policy = snapshot.GetPolicy(modeIndex: 0, profileIndex: 0)!;
        Assert.Equal(0, policy.AttackMotivation);
        Assert.Equal(100, policy.DefenseMotivation);
        Assert.Equal(50, policy.ScoreMotivation);
    }

    [Fact]
    public void ScoreToDeposit_ZeroOrNegative_Rejected()
    {
        var node = JsonNode.Parse(_validJson)!;
        node["profiles"]![0]!["scoreToDeposit"] = 0;

        var result = BotAiConfigurationValidator.Validate(node.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("scoreToDeposit", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.05)]
    [InlineData(2.5)]
    public void LowHpDepositThreshold_OutOfRange_Rejected(double invalidThreshold)
    {
        var node = JsonNode.Parse(_validJson)!;
        node["profiles"]![0]!["lowHpDepositThreshold"] = invalidThreshold;

        var result = BotAiConfigurationValidator.Validate(node.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("lowHpDepositThreshold", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(-0.001)]
    public void DashInterval_NonPositive_Rejected(double invalidInterval)
    {
        var node = JsonNode.Parse(_validJson)!;
        node["profiles"]![0]!["dashIntervals"]!["attack"] = invalidInterval;

        var result = BotAiConfigurationValidator.Validate(node.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("dashIntervals", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    [InlineData(-5.0)]
    public void SkillInterval_NonPositive_Rejected(double invalidInterval)
    {
        var node = JsonNode.Parse(_validJson)!;
        node["profiles"]![0]!["skillIntervals"]!["heal"] = invalidInterval;

        var result = BotAiConfigurationValidator.Validate(node.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("skillIntervals", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NonNumericOrNullInterval_Rejected()
    {
        var badJson = _validJson.Replace("\"attack\": 1.0", "\"attack\": null");
        var result = BotAiConfigurationValidator.Validate(badJson);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void UnknownRootField_RejectedByStrictSchema()
    {
        var node = JsonNode.Parse(_validJson)!.AsObject();
        node["unexpectedField"] = "bad-value";

        var result = BotAiConfigurationValidator.Validate(node.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("unexpectedField", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnknownProfileField_RejectedByStrictSchema()
    {
        var node = JsonNode.Parse(_validJson)!;
        node["profiles"]![0]!["unknownProfileField"] = 123;

        var result = BotAiConfigurationValidator.Validate(node.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("unknownProfileField", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnknownNestedField_RejectedByStrictSchema()
    {
        var node = JsonNode.Parse(_validJson)!;
        node["profiles"]![0]!["motivations"]!["stealth"] = 50;

        var result = BotAiConfigurationValidator.Validate(node.ToJsonString());
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("stealth", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MalformedJson_Rejected()
    {
        var result = BotAiConfigurationValidator.Validate("{ invalid json");
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Malformed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmptyJson_Rejected()
    {
        var result = BotAiConfigurationValidator.Validate("");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Snapshot_Has39UniqueDictionaryKeys_AndExposesAllConfiguredParameters()
    {
        var result = BotAiConfigurationValidator.Validate(_validJson);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));

        var snapshot = result.ToSnapshot("test-path");

        // V2 verification: 42 policies per-kicker, 39 legacy profile slots, 57 total parameter IDs (with legacy aliases)
        Assert.Equal(42, snapshot.PoliciesByModeAndKicker.Count);
        Assert.Equal(39, snapshot.PoliciesByModeAndProfile.Count);
        Assert.Equal(57, snapshot.PoliciesByParameterId.Count);
        Assert.Equal(42, snapshot.AllPolicies.Count);

        // Verify GetPolicy(mode, each 0..12) for all 3 modes and expose all row parameters
        for (int mode = 0; mode <= 2; mode++)
        {
            for (int prof = 0; prof <= 12; prof++)
            {
                var policy = snapshot.GetPolicy(mode, prof);
                Assert.NotNull(policy);

                var expectedParamId = 15 + (13 * mode) + prof;
                Assert.Equal(expectedParamId, policy.ParameterId);
                Assert.Equal(mode, policy.ModeIndex);

                // Verify all configured row params are exposed and bounded
                Assert.InRange(policy.AttackMotivation, 0, 100);
                Assert.InRange(policy.DefenseMotivation, 0, 100);
                Assert.InRange(policy.ScoreMotivation, 0, 100);
                Assert.True(policy.ScoreToDeposit >= 1, $"ScoreToDeposit was {policy.ScoreToDeposit}");

                Assert.True(double.IsFinite(policy.AttackDashInterval) && policy.AttackDashInterval > 0.0);
                Assert.True(double.IsFinite(policy.DefenseDashInterval) && policy.DefenseDashInterval > 0.0);
                Assert.True(double.IsFinite(policy.ScoreDashInterval) && policy.ScoreDashInterval > 0.0);

                Assert.True(double.IsFinite(policy.AttackSkillInterval) && policy.AttackSkillInterval > 0.0);
                Assert.True(double.IsFinite(policy.HealSkillInterval) && policy.HealSkillInterval > 0.0);
                Assert.True(double.IsFinite(policy.BuffSkillInterval) && policy.BuffSkillInterval > 0.0);
                Assert.True(double.IsFinite(policy.TrapSkillInterval) && policy.TrapSkillInterval > 0.0);
                Assert.True(double.IsFinite(policy.WarpSkillInterval) && policy.WarpSkillInterval > 0.0);

                Assert.True(double.IsFinite(policy.HealDamageRate) && policy.HealDamageRate >= 0.0 && policy.HealDamageRate <= 1.0);
                Assert.True(double.IsFinite(policy.LowHpDepositThreshold) && policy.LowHpDepositThreshold >= 0.0 && policy.LowHpDepositThreshold <= 1.0);
                Assert.True(policy.KickerId >= 1 && policy.KickerId <= 14);
                Assert.True(policy.KickerCostumeId > 0);

                Assert.False(string.IsNullOrWhiteSpace(policy.ScramblePolicy));
                Assert.False(string.IsNullOrWhiteSpace(policy.FlagFlightPolicy));
                Assert.False(string.IsNullOrWhiteSpace(policy.RapidBallPolicy));

                // Verify GetPolicyByCanonicalName matches GetPolicy(mode, prof)
                var byName = snapshot.GetPolicyByCanonicalName(policy.Profile, mode);
                Assert.NotNull(byName);
                Assert.Same(policy, byName);
            }
        }
    }

    [Fact]
    public void Config_ExactlyPreservesExistingDefaults_IncludingCostumeAndHeuristicsForAll39()
    {
        var result = BotAiConfigurationValidator.Validate(_validJson);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));

        var configSnapshot = result.ToSnapshot("config-path");
        var defaultSnapshot = BotAiDefaults.CreateDefaultSnapshot();

        Assert.Equal(defaultSnapshot.AllPolicies.Count, configSnapshot.AllPolicies.Count);

        for (int i = 0; i < defaultSnapshot.AllPolicies.Count; i++)
        {
            var def = defaultSnapshot.AllPolicies[i];
            var cfg = configSnapshot.AllPolicies[i];

            Assert.Equal(def.ParameterId, cfg.ParameterId);
            Assert.Equal(def.Profile, cfg.Profile);
            Assert.Equal(def.ModeIndex, cfg.ModeIndex);
            Assert.Equal(def.KickerId, cfg.KickerId);
            Assert.Equal(def.KickerCostumeId, cfg.KickerCostumeId);

            Assert.Equal(def.AttackMotivation, cfg.AttackMotivation);
            Assert.Equal(def.DefenseMotivation, cfg.DefenseMotivation);
            Assert.Equal(def.ScoreMotivation, cfg.ScoreMotivation);
            Assert.Equal(def.ScoreToDeposit, cfg.ScoreToDeposit);

            Assert.Equal(def.AttackDashInterval, cfg.AttackDashInterval);
            Assert.Equal(def.DefenseDashInterval, cfg.DefenseDashInterval);
            Assert.Equal(def.ScoreDashInterval, cfg.ScoreDashInterval);

            Assert.Equal(def.AttackSkillInterval, cfg.AttackSkillInterval);
            Assert.Equal(def.HealSkillInterval, cfg.HealSkillInterval);
            Assert.Equal(def.BuffSkillInterval, cfg.BuffSkillInterval);
            Assert.Equal(def.TrapSkillInterval, cfg.TrapSkillInterval);
            Assert.Equal(def.WarpSkillInterval, cfg.WarpSkillInterval);

            Assert.Equal(def.HealDamageRate, cfg.HealDamageRate);
            Assert.Equal(def.CanLockon, cfg.CanLockon);
            Assert.Equal(def.LowHpDepositThreshold, cfg.LowHpDepositThreshold);

            Assert.Equal(def.ScramblePolicy, cfg.ScramblePolicy);
            Assert.Equal(def.FlagFlightPolicy, cfg.FlagFlightPolicy);
            Assert.Equal(def.RapidBallPolicy, cfg.RapidBallPolicy);
        }

        // Verify heuristic preservation across master generation
        var sampleBaseJson = """
        [
          {
            "id": 8, "kickerId": 8, "kickerCostumeId": 2080101, "rank": 13,
            "changeAttributeInterval": 4.5, "maxMeasureAccuracy": 0.95, "minMeasureAccuracy": 0.85,
            "negativeCorrection": -2, "positiveCorrection": 3
          },
          {
            "id": 10, "kickerId": 10, "kickerCostumeId": 2100101, "rank": 13,
            "changeAttributeInterval": 3.8, "maxMeasureAccuracy": 0.92, "minMeasureAccuracy": 0.80,
            "negativeCorrection": 1, "positiveCorrection": 4
          }
        ]
        """;

        var enrichedJson = ExperimentalAiRoleService.AppendExperimentalRoleAiRows(sampleBaseJson, configSnapshot);
        var enrichedArray = JsonNode.Parse(enrichedJson)!.AsArray();

        // Check Ana carrier (id 15) preserves Ana's base heuristics
        var row15 = enrichedArray.First(r => r?["id"]?.GetValue<int>() == 15)!;
        Assert.Equal(4.5, row15["changeAttributeInterval"]?.GetValue<double>());
        Assert.Equal(0.95, row15["maxMeasureAccuracy"]?.GetValue<double>());
        Assert.Equal(0.85, row15["minMeasureAccuracy"]?.GetValue<double>());
        Assert.Equal(-2, row15["negativeCorrection"]?.GetValue<int>());
        Assert.Equal(3, row15["positiveCorrection"]?.GetValue<int>());

        // Check Yuyan (id 23) preserves Yuyan's base heuristics
        var row23 = enrichedArray.First(r => r?["id"]?.GetValue<int>() == 23)!;
        Assert.Equal(3.8, row23["changeAttributeInterval"]?.GetValue<double>());
        Assert.Equal(0.92, row23["maxMeasureAccuracy"]?.GetValue<double>());
        Assert.Equal(0.80, row23["minMeasureAccuracy"]?.GetValue<double>());
        Assert.Equal(1, row23["negativeCorrection"]?.GetValue<int>());
        Assert.Equal(4, row23["positiveCorrection"]?.GetValue<int>());
    }

    [Fact]
    public void RosterResolutionSemantics_PreservedForActualKickerAssignments()
    {
        Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, "1");
        try
        {
            var result = BotAiConfigurationValidator.Validate(_validJson);
            Assert.True(result.IsValid);
            var snapshot = result.ToSnapshot("test-path");

            // 1. Yuyan specialization (kicker 10) vs generic objective attacker (e.g. Coco kicker 3)
            // Mode 0: Crystal Scramble (prof 3 is id 18, prof 8 is id 23)
            var yuyanParamId = snapshot.ResolveParameterId("objective_attacker", 10, BattleCompositionMode.CrystalScramble);
            Assert.Equal(23, yuyanParamId); // 15 + 13*0 + 8
            var yuyanPolicy = snapshot.GetPolicy("objective_attacker", 10, BattleCompositionMode.CrystalScramble);
            Assert.NotNull(yuyanPolicy);
            Assert.Equal(23, yuyanPolicy.ParameterId);
            Assert.Equal("Yuyan", yuyanPolicy.Profile);

            var cocoParamId = snapshot.ResolveParameterId("objective_attacker", 3, BattleCompositionMode.CrystalScramble);
            Assert.Equal(18, cocoParamId); // 15 + 13*0 + 3
            var cocoPolicy = snapshot.GetPolicy("objective_attacker", 3, BattleCompositionMode.CrystalScramble);
            Assert.NotNull(cocoPolicy);
            Assert.Equal(18, cocoPolicy.ParameterId);
            Assert.Equal("objective_attacker", cocoPolicy.Profile);

            // 2. Owlbert specialization (kicker 5) vs generic trap defender (kicker 3)
            // Mode 1: Flag Flight (prof 7 is id 35, prof 9 is id 37)
            var owlbertParamId = snapshot.ResolveParameterId("trap_defender", 5, BattleCompositionMode.FlagFlight);
            Assert.Equal(37, owlbertParamId); // 15 + 13*1 + 9
            var owlbertPolicy = snapshot.GetPolicy("trap_defender", 5, BattleCompositionMode.FlagFlight);
            Assert.NotNull(owlbertPolicy);
            Assert.Equal(37, owlbertPolicy.ParameterId);
            Assert.Equal("Owlbert", owlbertPolicy.Profile);

            var genericTrapParamId = snapshot.ResolveParameterId("trap_defender", 3, BattleCompositionMode.FlagFlight);
            Assert.Equal(35, genericTrapParamId); // 15 + 13*1 + 7
            var genericTrapPolicy = snapshot.GetPolicy("trap_defender", 3, BattleCompositionMode.FlagFlight);
            Assert.NotNull(genericTrapPolicy);
            Assert.Equal(35, genericTrapPolicy.ParameterId);
            Assert.Equal("trap_defender", genericTrapPolicy.Profile);

            // 3. Ruriha specialization (kicker 2) vs generic healer (kicker 8)
            // Mode 2: Rapid Ball (prof 4 is id 45, prof 10 is id 51)
            var rurihaParamId = snapshot.ResolveParameterId("healer", 2, BattleCompositionMode.RapidBall);
            Assert.Equal(51, rurihaParamId); // 15 + 13*2 + 10
            var rurihaPolicy = snapshot.GetPolicy("healer", 2, BattleCompositionMode.RapidBall);
            Assert.NotNull(rurihaPolicy);
            Assert.Equal(51, rurihaPolicy.ParameterId);
            Assert.Equal("Ruriha", rurihaPolicy.Profile);

            var genericHealerParamId = snapshot.ResolveParameterId("healer", 8, BattleCompositionMode.RapidBall);
            Assert.Equal(45, genericHealerParamId); // 15 + 13*2 + 4
            var genericHealerPolicy = snapshot.GetPolicy("healer", 8, BattleCompositionMode.RapidBall);
            Assert.NotNull(genericHealerPolicy);
            Assert.Equal(45, genericHealerPolicy.ParameterId);
            Assert.Equal("healer", genericHealerPolicy.Profile);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, null);
        }
    }

    [Fact]
    public void Independence_K1Tsubame_And_K8Anna_HaveIndependentValuesAndIds()
    {
        var result = BotAiConfigurationValidator.Validate(_validJson);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        var snapshot = result.ToSnapshot("test-path");

        // Scramble: K1 (Tsubame) = 54, K8 (Anna) = 15
        var k1Scramble = snapshot.GetPolicyByKicker(0, 1);
        var k8Scramble = snapshot.GetPolicyByKicker(0, 8);
        Assert.NotNull(k1Scramble);
        Assert.NotNull(k8Scramble);
        Assert.Equal(54, k1Scramble.ParameterId);
        Assert.Equal(15, k8Scramble.ParameterId);
        Assert.NotEqual(k1Scramble.ParameterId, k8Scramble.ParameterId);

        // FlagFlight: K1 = 60, K8 = 28
        var k1Flag = snapshot.GetPolicyByKicker(1, 1);
        var k8Flag = snapshot.GetPolicyByKicker(1, 8);
        Assert.NotNull(k1Flag);
        Assert.NotNull(k8Flag);
        Assert.Equal(60, k1Flag.ParameterId);
        Assert.Equal(28, k8Flag.ParameterId);

        // RapidBall: K1 = 66, K8 = 41
        var k1Rapid = snapshot.GetPolicyByKicker(2, 1);
        var k8Rapid = snapshot.GetPolicyByKicker(2, 8);
        Assert.NotNull(k1Rapid);
        Assert.NotNull(k8Rapid);
        Assert.Equal(66, k1Rapid.ParameterId);
        Assert.Equal(41, k8Rapid.ParameterId);

        // Modify K1 in JSON node and re-validate
        var node = JsonNode.Parse(_validJson)!;
        var profiles = node["profiles"]!.AsArray();
        var k1Node = profiles.First(p => (string)p!["mode"]! == "CrystalScramble" && (int)p["kickerId"]! == 1);
        k1Node!["motivations"]!["attack"] = 99;
        k1Node["motivations"]!["score"] = 1;
        k1Node["dashIntervals"]!["attack"] = 0.42;

        var modResult = BotAiConfigurationValidator.Validate(node.ToJsonString());
        Assert.True(modResult.IsValid);
        var modSnapshot = modResult.ToSnapshot("mod-path");

        var modK1 = modSnapshot.GetPolicyByKicker(0, 1);
        var modK8 = modSnapshot.GetPolicyByKicker(0, 8);
        Assert.NotNull(modK1);
        Assert.NotNull(modK8);
        Assert.Equal(99, modK1.AttackMotivation);
        Assert.Equal(1, modK1.ScoreMotivation);
        Assert.Equal(0.42, modK1.AttackDashInterval);

        // K8 remains completely unchanged
        Assert.Equal(k8Scramble.AttackMotivation, modK8.AttackMotivation);
        Assert.Equal(k8Scramble.ScoreMotivation, modK8.ScoreMotivation);
        Assert.Equal(k8Scramble.AttackDashInterval, modK8.AttackDashInterval);
    }

    [Fact]
    public void Migration_LegacyThirtyNineProfiles_MigratesTransparentlyToFortyTwoIndependentProfiles()
    {
        // Construct legacy 39-profile V1 JSON
        var v1Profiles = new JsonArray();
        string[] modes = ["CrystalScramble", "FlagFlight", "RapidBall"];
        var expectedProfiles = new (string Profile, int ProfIdx, int KickerId)[]
        {
            ("carrier", 0, 8),
            ("tank_defender", 1, 3),
            ("hunter", 2, 4),
            ("objective_attacker", 3, 10),
            ("healer", 4, 2),
            ("speed_support", 5, 7),
            ("damage_support", 6, 14),
            ("trap_defender", 7, 5),
            ("Yuyan", 8, 10),
            ("Owlbert", 9, 5),
            ("Ruriha", 10, 2),
            ("Grenhawk", 11, 7),
            ("Sid", 12, 14)
        };

        for (int m = 0; m < modes.Length; m++)
        {
            for (int p = 0; p < expectedProfiles.Length; p++)
            {
                var spec = expectedProfiles[p];
                var paramId = 15 + (13 * m) + spec.ProfIdx;
                var obj = new JsonObject
                {
                    ["profile"] = spec.Profile,
                    ["mode"] = modes[m],
                    ["parameterId"] = paramId,
                    ["kickerId"] = spec.KickerId,
                    ["kickerCostumeId"] = 2000000 + (spec.KickerId * 10000) + 101,
                    ["rank"] = 13,
                    ["motivations"] = new JsonObject { ["attack"] = 10 + spec.ProfIdx * 5, ["defense"] = 20 + spec.ProfIdx * 5, ["score"] = 30 + spec.ProfIdx * 5 },
                    ["scoreToDeposit"] = 5 + spec.ProfIdx,
                    ["lowHpDepositThreshold"] = 0.35,
                    ["dashIntervals"] = new JsonObject { ["attack"] = 1.0, ["defense"] = 1.1, ["score"] = 1.2 },
                    ["skillIntervals"] = new JsonObject { ["attack"] = 2.0, ["heal"] = 3.0, ["buff"] = 4.0, ["trap"] = 5.0, ["warp"] = 6.0 },
                    ["healDamageRate"] = 0.5,
                    ["canLockon"] = true
                };
                v1Profiles.Add(obj);
            }
        }

        var v1Doc = new JsonObject
        {
            ["version"] = 1,
            ["profiles"] = v1Profiles
        };

        var result = BotAiConfigurationValidator.Validate(v1Doc.ToJsonString());
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.NotNull(result.Configuration);
        Assert.Equal(39, result.Configuration.Profiles.Count);

        var snapshot = result.ToSnapshot("migration-test");
        Assert.Equal(42, snapshot.AllPolicies.Count);

        // Verify all 14 kickers in Scramble have their stable IDs [15..71]
        for (int kid = 1; kid <= 14; kid++)
        {
            var policy = snapshot.GetPolicyByKicker(0, kid);
            Assert.NotNull(policy);
            var expectedId = BotAiDefaults.GetStableParameterId(0, kid);
            Assert.Equal(expectedId, policy.ParameterId);
            Assert.Equal(kid, policy.KickerId);
        }

        // Verify K1 (Tsubame), K8 (Anna), K9 (Jay) inherited from carrier (profIdx 0 -> attack 10, defense 20, score 30)
        foreach (var kid in new[] { 1, 8, 9 })
        {
            var pol = snapshot.GetPolicyByKicker(0, kid);
            Assert.NotNull(pol);
            Assert.Equal(10, pol.AttackMotivation);
            Assert.Equal(20, pol.DefenseMotivation);
            Assert.Equal(30, pol.ScoreMotivation);
        }

        // Verify K3 (Coco), K11 (Diatrius), K12 (Buzzy Big) inherited from tank_defender (profIdx 1 -> attack 15, defense 25, score 35)
        foreach (var kid in new[] { 3, 11, 12 })
        {
            var pol = snapshot.GetPolicyByKicker(0, kid);
            Assert.NotNull(pol);
            Assert.Equal(15, pol.AttackMotivation);
            Assert.Equal(25, pol.DefenseMotivation);
            Assert.Equal(35, pol.ScoreMotivation);
        }

        // Verify K4 (Kite), K6 (Pitophy), K13 (Hitagi) inherited from hunter (profIdx 2 -> attack 20, defense 30, score 40)
        foreach (var kid in new[] { 4, 6, 13 })
        {
            var pol = snapshot.GetPolicyByKicker(0, kid);
            Assert.NotNull(pol);
            Assert.Equal(20, pol.AttackMotivation);
            Assert.Equal(30, pol.DefenseMotivation);
            Assert.Equal(40, pol.ScoreMotivation);
        }
    }

    [Fact]
    public void AllFortyTwoProfiles_HaveValidCostumesAndUniqueBoundedIds()
    {
        var result = BotAiConfigurationValidator.Validate(_validJson);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        var snapshot = result.ToSnapshot("test-path");

        var seenIds = new HashSet<int>();
        foreach (var p in snapshot.AllPolicies)
        {
            Assert.True(seenIds.Add(p.ParameterId), $"Duplicate parameterId {p.ParameterId}");
            Assert.InRange(p.ParameterId, 15, 71);
            Assert.True(p.ParameterId < 100, $"ParameterId {p.ParameterId} violates Gym boundary");
            Assert.False(p.ParameterId >= 1 && p.ParameterId <= 14, $"ParameterId {p.ParameterId} overlaps with pristine kicker ID");

            // Kicker costume row ID check
            var expectedCostume = 2000000 + (p.KickerId * 10000) + 101;
            Assert.Equal(expectedCostume, p.KickerCostumeId);
        }
        Assert.Equal(42, seenIds.Count);
    }
}
