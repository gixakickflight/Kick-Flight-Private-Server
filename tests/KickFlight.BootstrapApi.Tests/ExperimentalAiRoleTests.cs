namespace KickFlight.BootstrapApi.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using KickFlight.BootstrapApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

[Collection("Matchmaking static state")]
public class ExperimentalAiRoleTests
{
    [Fact]
    public void DisabledByDefault_PreservesBaseKickerId()
    {
        Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, null);
        Assert.False(ExperimentalAiRoleService.IsEnabled);

        foreach (var profile in ExperimentalAiRoleService.AllProfileKeys)
        {
            for (int kickerId = 1; kickerId <= 14; kickerId++)
            {
                var paramIdNoMode = ExperimentalAiRoleService.ResolveParameterId(profile, kickerId, isGym: false);
                Assert.Equal(kickerId, paramIdNoMode);

                var paramIdScramble = ExperimentalAiRoleService.ResolveParameterId(profile, kickerId, BattleCompositionMode.CrystalScramble, isGym: false);
                Assert.Equal(kickerId, paramIdScramble);

                var paramIdFlag = ExperimentalAiRoleService.ResolveParameterId(profile, kickerId, BattleCompositionMode.FlagFlight, isGym: false);
                Assert.Equal(kickerId, paramIdFlag);

                var paramIdRapid = ExperimentalAiRoleService.ResolveParameterId(profile, kickerId, BattleCompositionMode.RapidBall, isGym: false);
                Assert.Equal(kickerId, paramIdRapid);
            }
        }
    }

    [Fact]
    public void GymIsolation_AlwaysReturnsGymBasePlusKickerId_RegardlessOfFlagOrProfile()
    {
        try
        {
            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, "1");
            Assert.True(ExperimentalAiRoleService.IsEnabled);

            foreach (var profile in ExperimentalAiRoleService.AllProfileKeys)
            {
                for (int kickerId = 1; kickerId <= 14; kickerId++)
                {
                    var paramId = ExperimentalAiRoleService.ResolveParameterId(profile, kickerId, BattleCompositionMode.CrystalScramble, isGym: true);
                    Assert.Equal(BattleMatchmakingService.GymAiParameterBase + kickerId, paramId);
                    Assert.True(paramId >= 100, "Gym parameter ID must be >= 100 for mannequin behavior");

                    var paramIdRule = ExperimentalAiRoleService.ResolveParameterId(profile, kickerId, battleRuleId: 1, isGym: true);
                    Assert.Equal(BattleMatchmakingService.GymAiParameterBase + kickerId, paramIdRule);
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, null);
        }
    }

    [Fact]
    public void DeterministicSharedContract_ThirtyNineBoundedUniqueIds()
    {
        var policies = ExperimentalAiRoleService.GetAllPolicies();
        Assert.Equal(42, policies.Count);

        var seenIds = new HashSet<int>();

        for (int modeIndex = 0; modeIndex <= 2; modeIndex++)
        {
            for (int kid = 1; kid <= 14; kid++)
            {
                var expectedId = BotAiDefaults.GetStableParameterId(modeIndex, kid);
                Assert.True(expectedId >= 15 && expectedId <= 71, $"ID {expectedId} out of range [15..71]");
                Assert.True(expectedId < 100, "ID must be < 100 to avoid mannequin cave hook");
                Assert.False(expectedId >= 1 && expectedId <= 14, "ID must not overlap with pristine kicker IDs [1..14]");

                var policy = policies.FirstOrDefault(p => p.ParameterId == expectedId);
                Assert.NotNull(policy);
                Assert.Equal(modeIndex, policy.ModeIndex);
                Assert.Equal(kid, policy.KickerId);
                Assert.True(seenIds.Add(expectedId), $"Duplicate ID {expectedId}");
            }
        }

        Assert.Equal(42, seenIds.Count);
        Assert.Equal(15, seenIds.Min());
        Assert.Equal(71, seenIds.Max());
    }

    [Fact]
    public void SpecializationPrecedence_SelectsExplicitSpecialization()
    {
        try
        {
            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, "1");

            // 1. Yuyan: objective_attacker with kicker 10 -> profileIndex 8
            Assert.Equal(23, ExperimentalAiRoleService.ResolveParameterId(ExperimentalAiRoleService.ProfileObjectiveAttacker, 10, BattleCompositionMode.CrystalScramble));
            Assert.Equal(36, ExperimentalAiRoleService.ResolveParameterId(ExperimentalAiRoleService.ProfileObjectiveAttacker, 10, BattleCompositionMode.FlagFlight));
            Assert.Equal(49, ExperimentalAiRoleService.ResolveParameterId(ExperimentalAiRoleService.ProfileObjectiveAttacker, 10, BattleCompositionMode.RapidBall));

            // Named "Yuyan" direct resolution
            Assert.Equal(23, ExperimentalAiRoleService.ResolveParameterId("Yuyan", 10, BattleCompositionMode.CrystalScramble));

            // Generic kicker with objective_attacker -> profileIndex 3
            Assert.Equal(18, ExperimentalAiRoleService.ResolveParameterId(ExperimentalAiRoleService.ProfileObjectiveAttacker, 4, BattleCompositionMode.CrystalScramble));
            Assert.Equal(31, ExperimentalAiRoleService.ResolveParameterId(ExperimentalAiRoleService.ProfileObjectiveAttacker, 4, BattleCompositionMode.FlagFlight));
            Assert.Equal(44, ExperimentalAiRoleService.ResolveParameterId(ExperimentalAiRoleService.ProfileObjectiveAttacker, 4, BattleCompositionMode.RapidBall));

            // 2. Owlbert: trap_defender with kicker 5 -> profileIndex 9
            Assert.Equal(24, ExperimentalAiRoleService.ResolveParameterId(ExperimentalAiRoleService.ProfileTrapDefender, 5, BattleCompositionMode.CrystalScramble));
            Assert.Equal(37, ExperimentalAiRoleService.ResolveParameterId(ExperimentalAiRoleService.ProfileTrapDefender, 5, BattleCompositionMode.FlagFlight));
            Assert.Equal(50, ExperimentalAiRoleService.ResolveParameterId(ExperimentalAiRoleService.ProfileTrapDefender, 5, BattleCompositionMode.RapidBall));

            // 3. Ruriha: healer with kicker 2 -> profileIndex 10
            Assert.Equal(25, ExperimentalAiRoleService.ResolveParameterId(ExperimentalAiRoleService.ProfileHealer, 2, BattleCompositionMode.CrystalScramble));
            Assert.Equal(38, ExperimentalAiRoleService.ResolveParameterId(ExperimentalAiRoleService.ProfileHealer, 2, BattleCompositionMode.FlagFlight));
            Assert.Equal(51, ExperimentalAiRoleService.ResolveParameterId(ExperimentalAiRoleService.ProfileHealer, 2, BattleCompositionMode.RapidBall));

            // 4. Grenhawk: speed_support with kicker 7 -> profileIndex 11
            Assert.Equal(26, ExperimentalAiRoleService.ResolveParameterId(ExperimentalAiRoleService.ProfileSpeedSupport, 7, BattleCompositionMode.CrystalScramble));
            Assert.Equal(39, ExperimentalAiRoleService.ResolveParameterId(ExperimentalAiRoleService.ProfileSpeedSupport, 7, BattleCompositionMode.FlagFlight));
            Assert.Equal(52, ExperimentalAiRoleService.ResolveParameterId(ExperimentalAiRoleService.ProfileSpeedSupport, 7, BattleCompositionMode.RapidBall));

            // 5. Sid: damage_support with kicker 14 -> profileIndex 12
            Assert.Equal(27, ExperimentalAiRoleService.ResolveParameterId(ExperimentalAiRoleService.ProfileDamageSupport, 14, BattleCompositionMode.CrystalScramble));
            Assert.Equal(40, ExperimentalAiRoleService.ResolveParameterId(ExperimentalAiRoleService.ProfileDamageSupport, 14, BattleCompositionMode.FlagFlight));
            Assert.Equal(53, ExperimentalAiRoleService.ResolveParameterId(ExperimentalAiRoleService.ProfileDamageSupport, 14, BattleCompositionMode.RapidBall));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, null);
        }
    }

    [Fact]
    public void FlagFlightMode_NoGuardianBehavior_And_YuyanObjectiveTarget()
    {
        var policies = ExperimentalAiRoleService.GetAllPolicies();

        // 1. Verify Flag Flight (modeIndex 1) has no guardian behavior mentioned
        var flagPolicies = policies.Where(p => p.ModeIndex == 1).ToList();
        Assert.Equal(14, flagPolicies.Count);
        foreach (var policy in flagPolicies)
        {
            Assert.DoesNotContain("guardian", policy.FlagFlightPolicy, StringComparison.OrdinalIgnoreCase);
        }

        // 2. Verify Yuyan Scramble policy targets enemy guardian
        var yuyanScramble = ExperimentalAiRoleService.GetPolicy("Yuyan", 10, BattleCompositionMode.CrystalScramble);
        Assert.NotNull(yuyanScramble);
        Assert.Contains("enemy guardian", yuyanScramble.ScramblePolicy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("defending friendly guardian", yuyanScramble.ScramblePolicy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnsupportedRulesOrProfiles_ExplicitSafeFallback()
    {
        try
        {
            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, "1");

            // Unsupported battle rule -> returns kickerId
            var fallbackRule = ExperimentalAiRoleService.ResolveParameterId("carrier", 8, battleRuleId: 999);
            Assert.Equal(8, fallbackRule);

            // Null mode -> returns kickerId
            var fallbackNullMode = ExperimentalAiRoleService.ResolveParameterId("carrier", 8, mode: null);
            Assert.Equal(8, fallbackNullMode);

            // Unsupported profile -> returns kickerId
            var fallbackProfile = ExperimentalAiRoleService.ResolveParameterId("unknown_profile", 8, BattleCompositionMode.CrystalScramble);
            Assert.Equal(8, fallbackProfile);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, null);
        }
    }

    [Fact]
    public void MasterGeneration_AppendsThirtyNineValidClonedRowsWithRank13()
    {
        var sampleBaseJson = @"[
            {""id"":1,""kickerId"":1,""kickerCostumeId"":2010101,""attackMotivation"":60,""defenseMotivation"":40,""scoreMotivation"":70,""negativeCorrection"":0,""positiveCorrection"":0,""minMeasureAccuracy"":0.9,""maxMeasureAccuracy"":1.0,""changeAttributeInterval"":4.0,""rank"":13},
            {""id"":2,""kickerId"":2,""kickerCostumeId"":2020101,""attackMotivation"":60,""defenseMotivation"":40,""scoreMotivation"":70,""negativeCorrection"":0,""positiveCorrection"":0,""minMeasureAccuracy"":0.9,""maxMeasureAccuracy"":1.0,""changeAttributeInterval"":4.0,""rank"":13},
            {""id"":3,""kickerId"":3,""kickerCostumeId"":2030101,""attackMotivation"":60,""defenseMotivation"":40,""scoreMotivation"":70,""negativeCorrection"":0,""positiveCorrection"":0,""minMeasureAccuracy"":0.9,""maxMeasureAccuracy"":1.0,""changeAttributeInterval"":4.0,""rank"":13},
            {""id"":4,""kickerId"":4,""kickerCostumeId"":2040101,""attackMotivation"":60,""defenseMotivation"":40,""scoreMotivation"":70,""negativeCorrection"":0,""positiveCorrection"":0,""minMeasureAccuracy"":0.9,""maxMeasureAccuracy"":1.0,""changeAttributeInterval"":4.0,""rank"":13},
            {""id"":5,""kickerId"":5,""kickerCostumeId"":2050101,""attackMotivation"":60,""defenseMotivation"":40,""scoreMotivation"":70,""negativeCorrection"":0,""positiveCorrection"":0,""minMeasureAccuracy"":0.9,""maxMeasureAccuracy"":1.0,""changeAttributeInterval"":4.0,""rank"":13},
            {""id"":7,""kickerId"":7,""kickerCostumeId"":2070101,""attackMotivation"":60,""defenseMotivation"":40,""scoreMotivation"":70,""negativeCorrection"":0,""positiveCorrection"":0,""minMeasureAccuracy"":0.9,""maxMeasureAccuracy"":1.0,""changeAttributeInterval"":4.0,""rank"":13},
            {""id"":8,""kickerId"":8,""kickerCostumeId"":2080101,""attackMotivation"":60,""defenseMotivation"":40,""scoreMotivation"":70,""negativeCorrection"":0,""positiveCorrection"":0,""minMeasureAccuracy"":0.9,""maxMeasureAccuracy"":1.0,""changeAttributeInterval"":4.0,""rank"":13},
            {""id"":10,""kickerId"":10,""kickerCostumeId"":2100101,""attackMotivation"":60,""defenseMotivation"":40,""scoreMotivation"":70,""negativeCorrection"":0,""positiveCorrection"":0,""minMeasureAccuracy"":0.9,""maxMeasureAccuracy"":1.0,""changeAttributeInterval"":4.0,""rank"":13},
            {""id"":14,""kickerId"":14,""kickerCostumeId"":2140101,""attackMotivation"":60,""defenseMotivation"":40,""scoreMotivation"":70,""negativeCorrection"":0,""positiveCorrection"":0,""minMeasureAccuracy"":0.9,""maxMeasureAccuracy"":1.0,""changeAttributeInterval"":4.0,""rank"":13}
        ]";

        var resultJson = ExperimentalAiRoleService.AppendExperimentalRoleAiRows(sampleBaseJson);
        var rows = JsonNode.Parse(resultJson) as JsonArray;
        Assert.NotNull(rows);

        var policies = ExperimentalAiRoleService.GetAllPolicies();
        Assert.Equal(42, policies.Count);

        foreach (var policy in policies)
        {
            var match = rows.FirstOrDefault(r => (r?["id"]?.GetValue<int>() ?? 0) == policy.ParameterId) as JsonObject;
            Assert.NotNull(match);
            Assert.Equal(13, match["rank"]?.GetValue<int>());
            Assert.Equal(policy.AttackMotivation, match["attackMotivation"]?.GetValue<int>());
            Assert.Equal(policy.DefenseMotivation, match["defenseMotivation"]?.GetValue<int>());
            Assert.Equal(policy.ScoreMotivation, match["scoreMotivation"]?.GetValue<int>());
            Assert.Equal(policy.ScoreToDeposit, match["scoreToDeposit"]?.GetValue<int>());
            Assert.NotNull(match["minMeasureAccuracy"]);
            Assert.NotNull(match["maxMeasureAccuracy"]);
            Assert.NotNull(match["changeAttributeInterval"]);
        }
    }

    [Theory]
    [InlineData(1, 15, 27, 54, 59)] // Crystal Scramble
    [InlineData(2, 28, 40, 60, 65)] // Flag Flight
    [InlineData(3, 41, 53, 66, 71)] // Rapid Ball
    public async Task SerializedRoster_WhenEnabled_WiresActualProfileAndModeId_And_OffUnchanged(int ruleId, int minId, int maxId, int extraMin, int extraMax)
    {
        var root = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        var photon = new PhotonServerManager(
            Options.Create(new PhotonServerOptions { Enabled = false }),
            NullLogger<PhotonServerManager>.Instance);
        var environment = new TestWebHostEnvironment(root);

        // 1. When Disabled: kickerAiParameterId equals kickerId
        try
        {
            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, null);
            var serviceOff = new BattleMatchmakingService(NullLogger<BattleMatchmakingService>.Instance, photon, environment: environment)
            {
                MatchWindow = TimeSpan.FromMilliseconds(20),
                JoinIncrementBaseSeconds = 0
            };
            var (_, ticketOff) = serviceOff.RegisterEntry("1000001", "Human Off", 1, 2010101, ruleId, [3010001, 3010002, 3010003, 3010004]);
            var rosterOff = await GetFinalRoster(serviceOff, ticketOff);
            var botsOff = rosterOff.Where(r => r.GetProperty("kickerAiParameterId").GetInt32() > 0).ToList();
            Assert.True(botsOff.Count > 0);
            foreach (var bot in botsOff)
            {
                var kickerId = bot.GetProperty("kickerId").GetInt32();
                Assert.Equal(kickerId, bot.GetProperty("kickerAiParameterId").GetInt32());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, null);
        }

        // 2. When Enabled: kickerAiParameterId uses mode-aware IDs within expected bounds
        try
        {
            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, "1");
            var serviceOn = new BattleMatchmakingService(NullLogger<BattleMatchmakingService>.Instance, photon, environment: environment)
            {
                MatchWindow = TimeSpan.FromMilliseconds(20),
                JoinIncrementBaseSeconds = 0
            };
            var (_, ticketOn) = serviceOn.RegisterEntry("1000001", "Human On", 1, 2010101, ruleId, [3010001, 3010002, 3010003, 3010004]);
            var rosterOn = await GetFinalRoster(serviceOn, ticketOn);
            var botsOn = rosterOn.Where(r => r.GetProperty("kickerAiParameterId").GetInt32() > 0).ToList();
            Assert.True(botsOn.Count > 0);
            foreach (var bot in botsOn)
            {
                var paramId = bot.GetProperty("kickerAiParameterId").GetInt32();
                Assert.True((paramId >= minId && paramId <= maxId) || (paramId >= extraMin && paramId <= extraMax),
                    $"Param ID {paramId} outside expected ranges [{minId}..{maxId}] and [{extraMin}..{extraMax}]");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, null);
        }
    }

    [Fact]
    public void EncryptedMasters_ContainsMatchingIds_WhenEnabled()
    {
        var root = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        var aiParamPath = Path.Combine(root, "config/masters_kicker_ai_parameter.json");
        var baseJson = File.ReadAllText(aiParamPath);

        // When Disabled: Does not contain role IDs 15..53
        Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, null);
        var disabledRows = baseJson;
        if (ExperimentalAiRoleService.IsEnabled)
        {
            disabledRows = ExperimentalAiRoleService.AppendExperimentalRoleAiRows(disabledRows);
        }
        var disabledArray = JsonNode.Parse(disabledRows) as JsonArray;
        Assert.NotNull(disabledArray);
        var disabledIds = disabledArray.Select(r => r?["id"]?.GetValue<int>() ?? 0).ToHashSet();
        for (int id = 15; id <= 53; id++)
        {
            Assert.DoesNotContain(id, disabledIds);
        }

        // When Enabled: Encrypted & Decrypted master contains all 39 IDs 15..53
        try
        {
            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, "1");
            var enabledRows = ExperimentalAiRoleService.AppendExperimentalRoleAiRows(baseJson);

            // Encode using master encryption key
            var commonKey = Encoding.ASCII.GetBytes("1a837b9ee2ae11a07a0f529a4cd4b61c");
            var encrypted = D2CCodec.Encode(Encoding.UTF8.GetBytes(enabledRows), commonKey, new byte[16]);

            // Decode and parse
            var decrypted = D2CCodec.Decode(encrypted, commonKey);
            var decryptedJson = Encoding.UTF8.GetString(decrypted);
            var decryptedArray = JsonNode.Parse(decryptedJson) as JsonArray;
            Assert.NotNull(decryptedArray);

            var decryptedIds = decryptedArray.Select(r => r?["id"]?.GetValue<int>() ?? 0).ToHashSet();
            for (int id = 15; id <= 53; id++)
            {
                Assert.Contains(id, decryptedIds);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, null);
        }
    }

    [Fact]
    public void ActualRoster_YuyanBot_ReceivesObjectiveAttackerSpecialization_InScrambleAndRapid_AndPreservesFlagRules()
    {
        var root = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        var photon = new PhotonServerManager(
            Options.Create(new PhotonServerOptions { Enabled = false }),
            NullLogger<PhotonServerManager>.Instance);
        var environment = new TestWebHostEnvironment(root);
        var service = new BattleMatchmakingService(NullLogger<BattleMatchmakingService>.Instance, photon, environment: environment);

        try
        {
            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, "1");

            // 1. Crystal Scramble (Rule 1): modeIndex 0 -> formula 15 + (13 * 0) + 8 = 23 (objective_attacker specialization)
            // Real BuildRoster with Human Owlbert (Kicker 5) generates Yuyan bot in composition
            BattleMatchmakingService.MatchingPlayerBattleInfo? yuyanScrambleBot = null;
            for (int b = 0; b < 200; b++)
            {
                var session = new BattleMatchmakingService.BattleEntrySession
                {
                    UserId = "h1",
                    UserName = "Human Owlbert",
                    KickerId = 5,
                    KickerCostumeId = 2050101,
                    BattleRuleId = 1,
                    DeckDiscs = [3010001, 3010002, 3010003, 3010004]
                };
                var room = new BattleMatchmakingService.ActiveBattleRoom
                {
                    BattleId = $"battle-scramble-{b}",
                    BattleRuleId = 1,
                    HumanPlayers = [session]
                };
                var roster = service.BuildRoster(room, new Dictionary<BattleMatchmakingService.BattleEntrySession, int> { [session] = 0 });
                var match = roster.battlePlayerList.FirstOrDefault(p => p.kickerId == 10 && p.kickerAiParameterId > 0);
                if (match is not null)
                {
                    yuyanScrambleBot = match;
                    break;
                }
            }
            Assert.NotNull(yuyanScrambleBot);
            Assert.Equal(23, yuyanScrambleBot.kickerAiParameterId);

            // 2. Rapid Ball (Rule 3): modeIndex 2 -> formula 15 + (13 * 2) + 8 = 49 (objective_attacker specialization)
            // Real BuildRoster with Human Tsubame (Kicker 1) generates Yuyan bot in composition
            BattleMatchmakingService.MatchingPlayerBattleInfo? yuyanRapidBot = null;
            for (int b = 0; b < 200; b++)
            {
                var session = new BattleMatchmakingService.BattleEntrySession
                {
                    UserId = "h2",
                    UserName = "Human Tsubame",
                    KickerId = 1,
                    KickerCostumeId = 2010101,
                    BattleRuleId = 3,
                    DeckDiscs = [3010001, 3010002, 3010003, 3010004]
                };
                var room = new BattleMatchmakingService.ActiveBattleRoom
                {
                    BattleId = $"battle-rapid-{b}",
                    BattleRuleId = 3,
                    HumanPlayers = [session]
                };
                var roster = service.BuildRoster(room, new Dictionary<BattleMatchmakingService.BattleEntrySession, int> { [session] = 0 });
                var match = roster.battlePlayerList.FirstOrDefault(p => p.kickerId == 10 && p.kickerAiParameterId > 0);
                if (match is not null)
                {
                    yuyanRapidBot = match;
                    break;
                }
            }
            Assert.NotNull(yuyanRapidBot);
            Assert.Equal(49, yuyanRapidBot.kickerAiParameterId);

            // 3. Flag Flight (Rule 2): modeIndex 1 -> Flag rules preserved, hunter policy (formula 15 + 13*1 + 2 = 30)
            // Even when Yuyan is assigned hunter profile in Flag Flight, it preserves hunter policy 30 (not forced to objective_attacker 36)
            var paramIdFlag = ExperimentalAiRoleService.ResolveParameterId("hunter", 10, BattleCompositionMode.FlagFlight);
            Assert.Equal(30, paramIdFlag);

            // Also verify explicit ruleId resolution
            Assert.Equal(23, ExperimentalAiRoleService.ResolveParameterId("hunter", 10, battleRuleId: 1));
            Assert.Equal(49, ExperimentalAiRoleService.ResolveParameterId("hunter", 10, battleRuleId: 3));
            Assert.Equal(30, ExperimentalAiRoleService.ResolveParameterId("hunter", 10, battleRuleId: 2));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, null);
        }
    }

    [Fact]
    public void NestedAppContentRoot_LoadsCatalogFromRepoRoot_BuildRosterAssignsNonLegacyRoleIds15To53()
    {
        var repoRoot = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        var nestedContentRoot = Path.Combine(repoRoot, "src", "KickFlight.BootstrapApi");
        Assert.True(Directory.Exists(nestedContentRoot), "nestedContentRoot src/KickFlight.BootstrapApi must exist");
        Assert.False(File.Exists(Path.Combine(nestedContentRoot, "config", "scramble_bot_disc_decks.json")),
            "Nested app contentRoot must NOT have local config/scramble_bot_disc_decks.json directly");

        var resolved = ScrambleBotDeckCatalog.ResolveCatalogPath(nestedContentRoot);
        Assert.True(File.Exists(resolved), $"Resolved catalog path must exist: {resolved}");
        Assert.Equal(Path.GetFullPath(Path.Combine(repoRoot, "config", "scramble_bot_disc_decks.json")), resolved);

        var decks = ScrambleBotDeckCatalog.Load(nestedContentRoot, NullLogger.Instance);
        Assert.Equal(448, decks.Count);
        Assert.True(decks.Count >= 52);

        var photon = new PhotonServerManager(
            Options.Create(new PhotonServerOptions { Enabled = false }),
            NullLogger<PhotonServerManager>.Instance);
        var environment = new TestWebHostEnvironment(nestedContentRoot);
        var service = new BattleMatchmakingService(NullLogger<BattleMatchmakingService>.Instance, photon, environment: environment);

        try
        {
            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, "1");
            Assert.True(ExperimentalAiRoleService.IsEnabled);

            var session = new BattleMatchmakingService.BattleEntrySession
            {
                UserId = "user-1000027",
                UserName = "Human Player",
                KickerId = 1,
                KickerCostumeId = 2010101,
                BattleRuleId = 1,
                DeckDiscs = [3010001, 3010002, 3010003, 3010004]
            };
            var room = new BattleMatchmakingService.ActiveBattleRoom
            {
                BattleId = "battle-nested-root-1",
                BattleRuleId = 1,
                HumanPlayers = [session]
            };

            var roster = service.BuildRoster(room, new Dictionary<BattleMatchmakingService.BattleEntrySession, int> { [session] = 0 });
            Assert.Equal(8, roster.battlePlayerList.Count);

            var bots = roster.battlePlayerList.Where(p => p.userId.StartsWith("bot-")).ToList();
            Assert.Equal(7, bots.Count);

            foreach (var bot in bots)
            {
                Assert.InRange(bot.kickerAiParameterId, 15, 71);
                Assert.NotEqual(bot.kickerId, bot.kickerAiParameterId);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, null);
        }
    }

    [Fact]
    public void CatalogPathResolution_ExplicitRequestedPath_RetainsPrecedence()
    {
        var repoRoot = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        var explicitPath = Path.Combine(repoRoot, "config", "scramble_bot_disc_decks.json");
        Assert.True(File.Exists(explicitPath));

        var resolved = ScrambleBotDeckCatalog.ResolveCatalogPath(explicitPath);
        Assert.Equal(Path.GetFullPath(explicitPath), resolved);

        var decks = ScrambleBotDeckCatalog.Load(explicitPath, NullLogger.Instance);
        Assert.Equal(448, decks.Count);
    }

    [Fact]
    public void CatalogPathResolution_TrulyAbsent_FallsBackToEmptyAndLegacyRoster()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "kf_absent_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var resolved = ScrambleBotDeckCatalog.ResolveCatalogPath(tempDir);
            Assert.Equal(Path.GetFullPath(Path.Combine(tempDir, "config", "scramble_bot_disc_decks.json")), resolved);
            Assert.False(File.Exists(resolved));

            var decks = ScrambleBotDeckCatalog.Load(tempDir, NullLogger.Instance);
            Assert.Empty(decks);

            var photon = new PhotonServerManager(
                Options.Create(new PhotonServerOptions { Enabled = false }),
                NullLogger<PhotonServerManager>.Instance);
            var environment = new TestWebHostEnvironment(tempDir);
            var service = new BattleMatchmakingService(NullLogger<BattleMatchmakingService>.Instance, photon, environment: environment);

            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, "1");

            var session = new BattleMatchmakingService.BattleEntrySession
            {
                UserId = "user-human",
                UserName = "Human Player",
                KickerId = 1,
                KickerCostumeId = 2010101,
                BattleRuleId = 1,
                DeckDiscs = [3010001, 3010002, 3010003, 3010004]
            };
            var room = new BattleMatchmakingService.ActiveBattleRoom
            {
                BattleId = "battle-absent-catalog",
                BattleRuleId = 1,
                HumanPlayers = [session]
            };

            var roster = service.BuildRoster(room, new Dictionary<BattleMatchmakingService.BattleEntrySession, int> { [session] = 0 });
            var bots = roster.battlePlayerList.Where(p => p.userId.StartsWith("bot-")).ToList();
            Assert.Equal(7, bots.Count);

            // With empty catalog, must fall back to legacy kicker base IDs (no experimental roles active)
            foreach (var bot in bots)
            {
                Assert.Equal(bot.kickerId, bot.kickerAiParameterId);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, null);
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void BuildRoster_PreservesDefaultOffAndGymBehavior()
    {
        var repoRoot = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        var nestedContentRoot = Path.Combine(repoRoot, "src", "KickFlight.BootstrapApi");
        var photon = new PhotonServerManager(
            Options.Create(new PhotonServerOptions { Enabled = false }),
            NullLogger<PhotonServerManager>.Instance);
        var environment = new TestWebHostEnvironment(nestedContentRoot);
        var service = new BattleMatchmakingService(NullLogger<BattleMatchmakingService>.Instance, photon, environment: environment);

        // Case 1: Default off (flag not set)
        Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, null);
        Assert.False(ExperimentalAiRoleService.IsEnabled);

        var session = new BattleMatchmakingService.BattleEntrySession
        {
            UserId = "user-1",
            UserName = "Human",
            KickerId = 1,
            KickerCostumeId = 2010101,
            BattleRuleId = 1,
            DeckDiscs = [3010001, 3010002, 3010003, 3010004]
        };
        var roomOff = new BattleMatchmakingService.ActiveBattleRoom
        {
            BattleId = "battle-flag-off",
            BattleRuleId = 1,
            HumanPlayers = [session]
        };
        var rosterOff = service.BuildRoster(roomOff, new Dictionary<BattleMatchmakingService.BattleEntrySession, int> { [session] = 0 });
        var botsOff = rosterOff.battlePlayerList.Where(p => p.userId.StartsWith("bot-")).ToList();
        Assert.Equal(7, botsOff.Count);
        foreach (var bot in botsOff)
        {
            // Default off preserves base kicker ID
            Assert.Equal(bot.kickerId, bot.kickerAiParameterId);
        }

        // Case 2: Gym enabled (flag on or off)
        try
        {
            BattleMatchmakingService.GymEnabled = true;
            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, "1");

            var roomGym = new BattleMatchmakingService.ActiveBattleRoom
            {
                BattleId = "battle-gym",
                BattleRuleId = 1,
                HumanPlayers = [session]
            };
            var rosterGym = service.BuildRoster(roomGym, new Dictionary<BattleMatchmakingService.BattleEntrySession, int> { [session] = 0 });
            var botsGym = rosterGym.battlePlayerList.Where(p => p.userId.StartsWith("bot-")).ToList();
            Assert.True(botsGym.Count > 0);
            foreach (var bot in botsGym)
            {
                Assert.Equal(BattleMatchmakingService.GymAiParameterBase + bot.kickerId, bot.kickerAiParameterId);
                Assert.True(bot.kickerAiParameterId >= 100);
            }
        }
        finally
        {
            BattleMatchmakingService.GymEnabled = false;
            Environment.SetEnvironmentVariable(ExperimentalAiRoleService.EnvFlagName, null);
        }
    }

    private static async Task<List<JsonElement>> GetFinalRoster(BattleMatchmakingService service, string ticket)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var writer = new CollectingWriter(expectedCount: 3, cancellation);
        var stream = service.StreamAssignmentsAsync(ticket, writer, cancellation.Token);
        await writer.WaitForExpectedWritesAsync();
        try
        {
            await stream;
        }
        catch (OperationCanceledException)
        {
        }
        return Roster(writer.Responses[^1].Assignment);
    }

    private static List<JsonElement> Roster(OpenMatch.Assignment assignment)
    {
        var battleJson = assignment.Properties.Fields["battle"].StructValue.ToString();
        using var document = JsonDocument.Parse(battleJson);
        return document.RootElement.GetProperty("battlePlayerList")
            .EnumerateArray()
            .Select(element => element.Clone())
            .ToList();
    }

    private sealed class CollectingWriter(int expectedCount, CancellationTokenSource cancellation) : Grpc.Core.IServerStreamWriter<OpenMatch.GetAssignmentsResponse>
    {
        public List<OpenMatch.GetAssignmentsResponse> Responses { get; } = [];
        private readonly TaskCompletionSource _expectedWrites = new();

        public Grpc.Core.WriteOptions? WriteOptions { get; set; }

        public async Task WaitForExpectedWritesAsync()
        {
            try
            {
                await _expectedWrites.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"Only {Responses.Count} of {expectedCount} expected writes arrived within 5s");
            }
        }

        public Task WriteAsync(OpenMatch.GetAssignmentsResponse message)
        {
            Responses.Add(message);
            if (Responses.Count >= expectedCount)
            {
                _expectedWrites.TrySetResult();
                cancellation.Cancel();
            }
            return Task.CompletedTask;
        }
    }

    private sealed class TestWebHostEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "KickFlight.BootstrapApi.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRoot;
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string EnvironmentName { get; set; } = Environments.Development;
    }
}
