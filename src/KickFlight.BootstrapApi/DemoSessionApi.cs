using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using KickFlight.BootstrapApi.Ops;
using KickFlight.BootstrapApi.PlayerStore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace KickFlight.BootstrapApi;

public sealed partial class DemoSessionApi : IDisposable
{
    private sealed record ApplicationAssetRevision(int platform, int revision);
    private sealed record MasterCatalogState(FrozenDictionary<string, byte[]> Masters, string MasterVersion);

    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";
    // The client keeps its downloaded masters until this header changes, so it must follow the content:
    // a SHA-256 over every served table (see the end of the constructor). Editing any config/masters_*.json
    // and restarting the server therefore makes the client re-download on its next launch.
    public string MasterVersion => Volatile.Read(ref _masterState).MasterVersion;
    private MasterCatalogState _masterState = new(FrozenDictionary<string, byte[]>.Empty, "demo-master-initial");
    private readonly object _masterLock = new();
    private string? _baseAiParamRows;
    private readonly IBotAiProfileProvider _botAiProfiles;
    private const string AccessToken = "demo-access-token-0000000000000000000000000000";

    // Rank is per player and persisted now (player_ranks, via IPlayerStore); the table it indexes into, the
    // range that renders, and the per-battle delta live in RankProgression.

    // Onboarding status, which is how the player is made to choose a name.
    //
    // TutorialUtil.IsTutorial is literally "tutorialProgressStatus != 207", and GetTutorialInitialSubStep maps
    // 206 to sub-step 16 (InputName), which HomeCharacterSelectView.RefreshTutorial opens as the name-entry
    // window. So: 206 while the player has no name, 207 once they do. Never 0 - that starts the long tutorial
    // at TapSlot, whose capsuleOpen / discBuildup / gachaDraw steps would each need a real inventory.
    //
    // Verified by decompiling the client; no client modification is involved.
    public const int TutorialStatusInputName = 206;
    public const int TutorialStatusComplete = 207;

    private static int TutorialStatus(SessionState state) =>
        state.HasName ? TutorialStatusComplete : TutorialStatusInputName;

    // The three rule types have separate ranked standings. Casual results continue using the existing regular ladder.
    public const int RegularBattleRuleType = 1;
    private static readonly int[] RankedBattleRuleTypes = [1, 2, 3];

    private object[] BuildBattleRankList(SessionState state) =>
        RankedBattleRuleTypes.Select(ruleType =>
        {
            var rank = _playerStore.LoadRank(state.PlayerId, ruleType);
            return (object)new { battleRuleType = ruleType, battlePoint = rank.BattlePoint, rank = rank.Rank };
        }).ToArray();

    private readonly string _contentRoot;
    private readonly int _grpcPort;
    private readonly MasterOverrides _mastersOverrides;
    // A live session, key and state together. One dictionary rather than two parallel ones so the two can
    // never disagree about which tokens are known.
    //
    // This is a cache, not the record: the record is the store's sessions table. A token that is not here may
    // still be valid - it was issued before a restart - so a miss is resolved against the store, and only a
    // miss in both is a rejection.
    private sealed record CachedSession(byte[] Key, SessionState State);

    private readonly ConcurrentDictionary<string, CachedSession> _sessions = new(StringComparer.Ordinal);
    private readonly IPlayerStore _playerStore;
    private readonly Dictionary<string, byte[]> _encryptedMasters = new(StringComparer.Ordinal);
    private readonly Dictionary<int, int> _battleRuleTypeById = [];
    private readonly Dictionary<int, int> _battleMatchTypeById = [];
    private readonly Dictionary<int, int> _battleTimeSecondsById = [];
    private long _customBattleSequence;
    private readonly List<KickerInfo> _kickerList = [];
    // kickerId -> KickerCostume master ROW ids. The client keys everything by row id (KickerCostumeMaster is a plain
    // TMasterBase: UserKickerInfo.kickerCostumeId, /kicker/change, BattleInfo all carry the row id; it saves e.g. 110
    // for Hitagi's standard colour, 64 for Owlbert's), not by the per-kicker costumeId column (1, 2, 3...).
    private readonly Dictionary<int, List<int>> _costumesByKicker = [];
    private readonly Dictionary<int, int> _costumeKicker = [];   // row id -> kickerId
    private readonly List<int> _discIdList = [];
    private readonly List<int> _gearIdList = [];                 // Gear master row ids, the pool gear/create rolls from
    private readonly ILogger<DemoSessionApi> _logger;
    private readonly BattleMatchmakingService _matchmaking;
    private readonly BattleReplayService _replays;
    private readonly MaintenanceState _maintenance;
    private readonly ClientUpdateState _clientUpdate;
    private readonly TimeProvider _timeProvider;
    private readonly OpsTelemetryService _ops;
    private readonly HarnessOptions _harnessOptions;

    public DemoSessionApi(ILogger<DemoSessionApi> logger, IWebHostEnvironment environment,
        BattleMatchmakingService matchmaking, BattleReplayService replays, IPlayerStore playerStore,
        IConfiguration configuration, MaintenanceState maintenance, ClientUpdateState clientUpdate,
        TimeProvider timeProvider, OpsTelemetryService ops, IOptions<HarnessOptions> harnessOptions,
        IBotAiProfileProvider? botAiProfiles = null)
    {
        _logger = logger;
        _ops = ops;
        _matchmaking = matchmaking;
        _replays = replays;
        _maintenance = maintenance;
        _clientUpdate = clientUpdate;
        _timeProvider = timeProvider;
        _playerStore = playerStore;
        _harnessOptions = harnessOptions.Value;
        _botAiProfiles = botAiProfiles ?? ExperimentalAiRoleService.CurrentProvider;
        _contentRoot = environment.ContentRootPath;
        _grpcPort = configuration.GetValue("GrpcPort", 18081);
        _mastersOverrides = new MasterOverrides(configuration[MasterOverrides.ConfigurationKey], environment.ContentRootPath);
        InitializeMasters(environment.ContentRootPath);
        _botAiProfiles.ProfilesReloaded += OnBotAiProfilesReloaded;

        var overridden = _mastersOverrides.Applied;
        _logger.LogInformation("Master overrides in {OverrideDir}: {Count} ({Names})",
            _mastersOverrides.Directory, overridden.Count,
            overridden.Count == 0 ? "none" : string.Join(", ", overridden));
    }

    // Inline fallbacks for a checkout without config/ (InitializeMasters). Public so tests can validate the rules
    // the client receives on that path too. Rules 7 (Crystal Scramble) and 8 (Flag Flight) are the matchType-2
    // entries the hourly RankedModeRotation needs; they must stay byte-for-byte equivalent to
    // config/masters_battle_rule.json.
    public const string BattleRuleFallbackJson = """
        [
          {"id":1,"name":"Crystal Scramble","seasonName":"Season 1","festivalName":"","matchType":1,"battleRuleType":1,"regularMatchFlag":true,"guardianAmount":1,"crystalAmount":50,"flagAmount":0,"generalAmount":0,"minimapVisibleType":1,"battleTimeSecond":180,"startDatetime":"2019-01-01 00:00:00","endDatetime":"2030-01-01 23:59:59"},
          {"id":2,"name":"Flag Flight","seasonName":"Season 1","festivalName":"","matchType":1,"battleRuleType":2,"regularMatchFlag":true,"guardianAmount":0,"crystalAmount":0,"flagAmount":1,"generalAmount":0,"minimapVisibleType":1,"battleTimeSecond":180,"startDatetime":"2019-01-01 00:00:00","endDatetime":"2030-01-01 23:59:59"},
          {"id":3,"name":"Rapid Ball","seasonName":"Season 1","festivalName":"","matchType":1,"battleRuleType":3,"regularMatchFlag":true,"guardianAmount":1,"crystalAmount":0,"flagAmount":0,"generalAmount":1,"minimapVisibleType":1,"battleTimeSecond":180,"startDatetime":"2019-01-01 00:00:00","endDatetime":"2030-01-01 23:59:59"},
          {"id":4,"name":"Rapid Ball","seasonName":"","festivalName":"","matchType":2,"battleRuleType":3,"regularMatchFlag":false,"guardianAmount":1,"crystalAmount":0,"flagAmount":0,"generalAmount":1,"minimapVisibleType":1,"battleTimeSecond":180,"startDatetime":"2019-01-01 00:00:00","endDatetime":"2030-01-01 23:59:59"},
          {"id":5,"name":"Crystal Scramble","seasonName":"Season 1","festivalName":"","matchType":3,"battleRuleType":1,"regularMatchFlag":false,"guardianAmount":1,"crystalAmount":50,"flagAmount":0,"generalAmount":0,"minimapVisibleType":1,"battleTimeSecond":180,"startDatetime":"2019-01-01 00:00:00","endDatetime":"2020-01-01 00:00:00"},
          {"id":6,"name":"Kick-Flight Festival","seasonName":"","festivalName":"Kick-Flight Festival","matchType":4,"battleRuleType":1,"regularMatchFlag":false,"guardianAmount":1,"crystalAmount":50,"flagAmount":0,"generalAmount":0,"minimapVisibleType":1,"battleTimeSecond":180,"startDatetime":"2019-01-01 00:00:00","endDatetime":"2020-01-01 00:00:00"},
          {"id":7,"name":"Crystal Scramble","seasonName":"","festivalName":"","matchType":2,"battleRuleType":1,"regularMatchFlag":false,"guardianAmount":1,"crystalAmount":50,"flagAmount":0,"generalAmount":0,"minimapVisibleType":1,"battleTimeSecond":180,"startDatetime":"2019-01-01 00:00:00","endDatetime":"2030-01-01 23:59:59"},
          {"id":8,"name":"Flag Flight","seasonName":"","festivalName":"","matchType":2,"battleRuleType":2,"regularMatchFlag":false,"guardianAmount":0,"crystalAmount":0,"flagAmount":1,"generalAmount":0,"minimapVisibleType":1,"battleTimeSecond":180,"startDatetime":"2019-01-01 00:00:00","endDatetime":"2030-01-01 23:59:59"}
        ]
        """;

    public const string BattleRuleFlagFlightScoreFallbackJson = """
        [
          {"id": 2, "battleRuleId": 2, "baseScore": 100, "goalCountScore": 100, "flagMoveScore": 10, "dropCountScore": 5, "flagMoveBackScore": 10, "killCountScore": 50, "killAssistCountScore": 25, "assistCountScore": 25, "specialSkillCountScore": 30, "winScoreCorrection": 1.5},
          {"id": 8, "battleRuleId": 8, "baseScore": 100, "goalCountScore": 100, "flagMoveScore": 10, "dropCountScore": 5, "flagMoveBackScore": 10, "killCountScore": 50, "killAssistCountScore": 25, "assistCountScore": 25, "specialSkillCountScore": 30, "winScoreCorrection": 1.5}
        ]
        """;

    private void InitializeMasters(string contentRoot)
    {
        var kickerJson = LoadJson(contentRoot, "config/masters_kicker.json", """[{"id":1,"name":"Tsubame","shortName":"Tsubame","nameSpelling":"Tsubame","voiceActorName":"CV: Yuma Uchida"}]""");
        var costumeJson = LoadJson(contentRoot, "config/masters_kicker_costume.json", """[{"id":1,"kickerId":1,"costumeId":1,"costumeName":"Tsubame - Standard Color","sortOrder":1,"battleResultPositionSortOrder":1,"battleResultModelScale":1.0,"exclusiveFlag":false,"releaseDatetime":"2019-01-01 00:00:00"}]""");
        var detailJson = LoadJson(contentRoot, "config/masters_kicker_detail.json", """[{"id":1,"kickerId":1,"kickerIntroductionText":"Fast as the wind","kickerSkillName":"Sonic Rush","kickerSkillShortText":"Quick attack","kickerSkillLongText":"Dashes forward","specialSkillName":"Burst Glide","specialSkillShortText":"Massive tornado","specialSkillLongText":"Creates a powerful tornado","kickerAbilityName":"Accel Charge","kickerAbilityShortText":"Speed","kickerAbilityLongText":"Faster with crystals","kickerDiscDistinctionText":"Aerial combat","kickerGraphHpRate":0.8,"kickerGraphAttackRate":0.9,"kickerGraphSpeedRate":1.0,"age":18,"birthday":"1/1","height":"165cm","profileText":"Fast as the wind"}]""");
        var parameterJson = LoadJson(contentRoot, "config/masters_kicker_parameter.json", "[]");
        var abilityJson = LoadJson(contentRoot, "config/masters_kicker_ability.json", "[]");
        var abilityConditionJson = LoadJson(contentRoot, "config/masters_kicker_ability_condition.json", "[]");
        var translationJson = LoadJson(contentRoot, "config/masters_translation.json", "[]");

        try
        {
            using var kDoc = JsonDocument.Parse(kickerJson);
            foreach (var el in kDoc.RootElement.EnumerateArray())
            {
                var id = el.GetProperty("id").GetInt32();
                var name = el.GetProperty("name").GetString() ?? $"Kicker {id}";
                _kickerList.Add(new KickerInfo(id, name));
            }

            using var cDoc = JsonDocument.Parse(costumeJson);
            foreach (var el in cDoc.RootElement.EnumerateArray())
            {
                var kickerId = el.GetProperty("kickerId").GetInt32();
                var costumeRowId = el.GetProperty("id").GetInt32();
                if (!_costumesByKicker.TryGetValue(kickerId, out var cList))
                {
                    cList = [];
                    _costumesByKicker[kickerId] = cList;
                }
                cList.Add(costumeRowId);
                _costumeKicker[costumeRowId] = kickerId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error parsing master json definitions: {Error}", ex.Message);
        }

        if (_kickerList.Count == 0)
        {
            // Only reached when the masters above could not be parsed at all. The costume is KickerCostume's
            // composite row id for kicker 1 / costume 1 rather than "1", because every id the server hands
            // the client is a lookup key into that master, not an index into our own files.
            _kickerList.Add(new KickerInfo(1, "Tsubame"));
            _costumesByKicker[1] = [2010101];
            _costumeKicker[2010101] = 1;
        }

        _encryptedMasters["Kicker"] = EncryptMaster(kickerJson);
        _encryptedMasters["KickerCostume"] = EncryptMaster(costumeJson);
        _encryptedMasters["KickerDetail"] = EncryptMaster(detailJson);
        _encryptedMasters["KickerParameter"] = EncryptMaster(parameterJson);
        _encryptedMasters["KickerAbility"] = EncryptMaster(ApplyObscuredOffsets("KickerAbility", abilityJson));
        _encryptedMasters["KickerAbilityCondition"] = EncryptMaster(abilityConditionJson);
        _encryptedMasters["Translation"] = EncryptMaster(translationJson);

        // FieldManager.InitializeAsync does FieldMaster[fieldId] and silently gives up (FieldInfo stays null, every
        // PlayerCharacter.Initialize then NREs and the loading screen never ends) when the row is missing. 801 is the
        // flat Trial arena BattleUtil.CreateTrialBattleInfo hard-codes (fld00801 / fielddata/fld00801_100 / mim00801_0).
        // One row per arena of BattleMatchmakingService.FieldPool (+ the home stage 99999, the Trial arena 801 and the
        // tutorial arena 11, hard-coded by TutorialUtil.CreateTutorialBattleRuleInfo 0x147A6AC: fld00011 / fld00011_1 / mim00011_0).
        _encryptedMasters["Field"] = EncryptMaster("""[{"id":99999,"name":"FLD99999","minimapId":99999,"minimapSizeX":100,"minimapSizeY":100,"itemPostionMasterId":0},{"id":801,"name":"FLD00801","minimapId":801,"minimapSizeX":100,"minimapSizeY":100,"itemPostionMasterId":0},{"id":101,"name":"FLD00101","minimapId":101,"minimapSizeX":100,"minimapSizeY":100,"itemPostionMasterId":0},{"id":301,"name":"FLD00301","minimapId":301,"minimapSizeX":100,"minimapSizeY":100,"itemPostionMasterId":0},{"id":401,"name":"FLD00401","minimapId":401,"minimapSizeX":100,"minimapSizeY":100,"itemPostionMasterId":0},{"id":601,"name":"FLD00601","minimapId":601,"minimapSizeX":100,"minimapSizeY":100,"itemPostionMasterId":0},{"id":701,"name":"FLD00701","minimapId":701,"minimapSizeX":100,"minimapSizeY":100,"itemPostionMasterId":0},{"id":901,"name":"FLD00901","minimapId":901,"minimapSizeX":100,"minimapSizeY":100,"itemPostionMasterId":0},{"id":11,"name":"FLD00011","minimapId":11,"minimapSizeX":100,"minimapSizeY":100,"itemPostionMasterId":0}]""");
        // hp = turret HP per deposited crystal (StepHpValue; max HP = hp x crystal carry limit). Basic hits deal their
        // raw attack x coefficient to the turret and a kicker with four lv10 discs attacks for ~2000-3000, so 8000 =
        // about three hits per crystal (300 stripped ~10 crystals per hit). attack = beam damage before the receiver's
        // scaling (150 came out as ~50 on a ~40k HP kicker; 6000 = ~2000 per shot). Row 2 is the ball-rule goal
        // guardian: NPCRevivalableGuardian.SetMaxHP is MAX_HP_STEP_AMOUNT (6) x hp, so 6 x 3000 = 18000 = ~7 hits.
        // The file is the source; the literal is only the fallback for a checkout without config/.
        var guardianParameterJson = LoadJson(contentRoot, "config/masters_guardian_parameter.json", """[{"id":1,"matchType":1,"rank":1,"hp":8000,"attack":6000}]""");
        _encryptedMasters["GuardianParameter"] = EncryptMaster(guardianParameterJson);
        // guardianAmount is per team: GameManager.CreateGuardian spawns it for both teams and indexes
        // FieldManager.GetGuardianInitialPosition, and FLD00101_1 (the crystal-rule variant) has exactly two
        // guardian points; the ball variants (rules 3/4) also spawn one guard per goal, which must be destroyed
        // before the team can score. The file is the source; the literal is only the fallback.
        var battleRuleJson = LoadJson(contentRoot, "config/masters_battle_rule.json", BattleRuleFallbackJson);
        _encryptedMasters["BattleRule"] = EncryptMaster(battleRuleJson);
        // id -> mode and rule type. Entries snapshot both values at matchmaking time, so results never infer mode
        // from a result request or from a master that may have changed while the battle was running.
        try
        {
            using var brDoc = JsonDocument.Parse(battleRuleJson);
            foreach (var el in brDoc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("id", out var idProp) && el.TryGetProperty("battleRuleType", out var typeProp))
                {
                    _battleRuleTypeById[idProp.GetInt32()] = typeProp.GetInt32();
                    if (el.TryGetProperty("matchType", out var matchTypeProp))
                        _battleMatchTypeById[idProp.GetInt32()] = matchTypeProp.GetInt32();
                    // Only the ops telemetry reads it: the silent-drop deadline is battle time + 120 s.
                    if (el.TryGetProperty("battleTimeSecond", out var timeProp) && timeProp.ValueKind == JsonValueKind.Number)
                        _battleTimeSecondsById[idProp.GetInt32()] = timeProp.GetInt32();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error parsing battle rule json: {Error}", ex.Message);
        }

        // every rule can be played on every arena of the pool (equal ratio); the server, not the client, draws the field
        var battleRuleFieldRows = new List<object>();
        var battleRuleFieldId = 1;
        foreach (var battleRule in Enumerable.Range(1, 6).Append(RankedModeRotation.CrystalRuleId)
                     .Append(RankedModeRotation.FlagRuleId))
        foreach (var field in BattleMatchmakingService.FieldPool)
            battleRuleFieldRows.Add(new { id = battleRuleFieldId++, battleRuleId = battleRule, fieldId = field, ratio = 16 });
        _encryptedMasters["BattleRuleField"] = EncryptMaster(JsonSerializer.Serialize(battleRuleFieldRows));
        // Custom battle uses its own stage lookup. Only expose fields whose base scene and three rule variants
        // are present in the catalog; Trial field 801 has a single flat _100 variant and is not a custom stage.
        var customBattleRuleFieldJson = LoadJson(contentRoot, "config/masters_custom_battle_rule_field.json", """
            [
              {"id":1,"battleRuleType":1,"fieldId":101,"sortOrder":1},
              {"id":2,"battleRuleType":1,"fieldId":301,"sortOrder":2},
              {"id":3,"battleRuleType":1,"fieldId":401,"sortOrder":3},
              {"id":4,"battleRuleType":1,"fieldId":601,"sortOrder":4},
              {"id":5,"battleRuleType":1,"fieldId":701,"sortOrder":5},
              {"id":6,"battleRuleType":1,"fieldId":901,"sortOrder":6},
              {"id":7,"battleRuleType":2,"fieldId":101,"sortOrder":1},
              {"id":8,"battleRuleType":2,"fieldId":301,"sortOrder":2},
              {"id":9,"battleRuleType":2,"fieldId":401,"sortOrder":3},
              {"id":10,"battleRuleType":2,"fieldId":601,"sortOrder":4},
              {"id":11,"battleRuleType":2,"fieldId":701,"sortOrder":5},
              {"id":12,"battleRuleType":2,"fieldId":901,"sortOrder":6},
              {"id":13,"battleRuleType":3,"fieldId":101,"sortOrder":1},
              {"id":14,"battleRuleType":3,"fieldId":301,"sortOrder":2},
              {"id":15,"battleRuleType":3,"fieldId":401,"sortOrder":3},
              {"id":16,"battleRuleType":3,"fieldId":601,"sortOrder":4},
              {"id":17,"battleRuleType":3,"fieldId":701,"sortOrder":5},
              {"id":18,"battleRuleType":3,"fieldId":901,"sortOrder":6}
            ]
            """);
        _encryptedMasters["CustomBattleRuleField"] = EncryptMaster(customBattleRuleFieldJson);

        // These are the retail-supported AI kicker/AI deck pairs. The client uses this custom-only master when an
        // owner chooses Add AI; humans do not depend on it.
        var customBattleKickerAiJson = LoadJson(contentRoot, "config/masters_custom_battle_kicker_ai.json", """
            [
              {"id":1,"kickerAiParameterId":1,"kickerAiDiscDeckId":1},
              {"id":2,"kickerAiParameterId":2,"kickerAiDiscDeckId":1},
              {"id":3,"kickerAiParameterId":3,"kickerAiDiscDeckId":1},
              {"id":4,"kickerAiParameterId":4,"kickerAiDiscDeckId":1},
              {"id":5,"kickerAiParameterId":5,"kickerAiDiscDeckId":1},
              {"id":6,"kickerAiParameterId":6,"kickerAiDiscDeckId":1},
              {"id":7,"kickerAiParameterId":7,"kickerAiDiscDeckId":1},
              {"id":8,"kickerAiParameterId":8,"kickerAiDiscDeckId":1},
              {"id":9,"kickerAiParameterId":9,"kickerAiDiscDeckId":1},
              {"id":10,"kickerAiParameterId":10,"kickerAiDiscDeckId":1},
              {"id":11,"kickerAiParameterId":11,"kickerAiDiscDeckId":1},
              {"id":12,"kickerAiParameterId":12,"kickerAiDiscDeckId":1},
              {"id":13,"kickerAiParameterId":13,"kickerAiDiscDeckId":1},
              {"id":14,"kickerAiParameterId":14,"kickerAiDiscDeckId":1}
            ]
            """);
        _encryptedMasters["CustomBattleKickerAi"] = EncryptMaster(customBattleKickerAiJson);
        // The file is the source; the literal is only the fallback.
        var regularMatchScheduleJson = LoadJson(contentRoot, "config/masters_regular_match_battle_schedule.json", """
            [
              {"id":1,"seasonMatchBattleRuleType":3,"groupId":1,"startTime":"00:00:00","endTime":"23:59:59","battleRuleId":1,"sortOrder":1},
              {"id":2,"seasonMatchBattleRuleType":3,"groupId":1,"startTime":"00:00:00","endTime":"23:59:59","battleRuleId":2,"sortOrder":2},
              {"id":3,"seasonMatchBattleRuleType":3,"groupId":1,"startTime":"00:00:00","endTime":"23:59:59","battleRuleId":3,"sortOrder":3},
              {"id":4,"seasonMatchBattleRuleType":-1,"groupId":1,"startTime":"00:00:00","endTime":"23:59:59","battleRuleId":1,"sortOrder":1},
              {"id":5,"seasonMatchBattleRuleType":-1,"groupId":1,"startTime":"00:00:00","endTime":"23:59:59","battleRuleId":2,"sortOrder":2},
              {"id":6,"seasonMatchBattleRuleType":-1,"groupId":1,"startTime":"00:00:00","endTime":"23:59:59","battleRuleId":3,"sortOrder":3}
            ]
            """);
        _encryptedMasters["RegularMatchBattleSchedule"] = EncryptMaster(regularMatchScheduleJson);
        _encryptedMasters["RankerMatchBattleSchedule"] = EncryptMaster(RankedModeRotation.BuildScheduleJson());
        // Retail is rows 1..17 (rank 0..16): D, C, C⁺, B, B⁺, A, A⁺, S, S⁺1 … S⁺9 — one row per league badge the
        // client ships (ui/league/thumbnail_league_0 … _16, see config/resources/catalog.json). The rank glyphs are
        // resolved by index, so a short table shifts every name by one and the two top ranks have no sprite at all:
        // BattleRankMaster.OnLoadComplete picks RegularEndRank as the first row with regularMatchFlag == 0, and
        // BattleUtil.GetDisplayRankPath caps rank to RegularEndRank.Rank with variant 1 (asset thumbnail_league_<n>_1)
        // for battleRuleType 1. Retail ships exactly one such variant asset, thumbnail_league_7_1 (S with the up
        // arrow), so RegularEndRank must be rank 7 = S: regularMatchFlag is true for ranks 0..6 and false from rank 7.
        // With the old 12-row table RegularEndRank was rank 4 and an S-rank player asked for thumbnail_league_4_1 /
        // _11_1, neither of which exists — the Image kept a null sprite, which is the empty rectangle in the
        // mode-selection banner and the black rectangle beside the lobby portrait.
        _encryptedMasters["BattleRank"] = EncryptMaster("""
            [
              {"id":1,"name":"D","rank":0,"regularMatchFlag":true,"totalBattlePoint":0,"winBattlePointCoefficient":1.0,"loseBattlePointCoefficient":1.0,"demontioableFlag":false},
              {"id":2,"name":"C","rank":1,"regularMatchFlag":true,"totalBattlePoint":200,"winBattlePointCoefficient":1.0,"loseBattlePointCoefficient":1.0,"demontioableFlag":false},
              {"id":3,"name":"C⁺","rank":2,"regularMatchFlag":true,"totalBattlePoint":500,"winBattlePointCoefficient":1.0,"loseBattlePointCoefficient":1.0,"demontioableFlag":false},
              {"id":4,"name":"B","rank":3,"regularMatchFlag":true,"totalBattlePoint":900,"winBattlePointCoefficient":1.0,"loseBattlePointCoefficient":1.0,"demontioableFlag":false},
              {"id":5,"name":"B⁺","rank":4,"regularMatchFlag":true,"totalBattlePoint":1400,"winBattlePointCoefficient":1.0,"loseBattlePointCoefficient":1.0,"demontioableFlag":false},
              {"id":6,"name":"A","rank":5,"regularMatchFlag":true,"totalBattlePoint":1900,"winBattlePointCoefficient":1.0,"loseBattlePointCoefficient":1.0,"demontioableFlag":false},
              {"id":7,"name":"A⁺","rank":6,"regularMatchFlag":true,"totalBattlePoint":2400,"winBattlePointCoefficient":1.0,"loseBattlePointCoefficient":1.0,"demontioableFlag":false},
              {"id":8,"name":"S","rank":7,"regularMatchFlag":false,"totalBattlePoint":2900,"winBattlePointCoefficient":1.0,"loseBattlePointCoefficient":1.0,"demontioableFlag":false},
              {"id":9,"name":"S⁺1","rank":8,"regularMatchFlag":false,"totalBattlePoint":3400,"winBattlePointCoefficient":1.0,"loseBattlePointCoefficient":1.0,"demontioableFlag":false},
              {"id":10,"name":"S⁺2","rank":9,"regularMatchFlag":false,"totalBattlePoint":3900,"winBattlePointCoefficient":1.0,"loseBattlePointCoefficient":1.0,"demontioableFlag":false},
              {"id":11,"name":"S⁺3","rank":10,"regularMatchFlag":false,"totalBattlePoint":4400,"winBattlePointCoefficient":1.0,"loseBattlePointCoefficient":1.0,"demontioableFlag":false},
              {"id":12,"name":"S⁺4","rank":11,"regularMatchFlag":false,"totalBattlePoint":4900,"winBattlePointCoefficient":1.0,"loseBattlePointCoefficient":1.0,"demontioableFlag":false},
              {"id":13,"name":"S⁺5","rank":12,"regularMatchFlag":false,"totalBattlePoint":5400,"winBattlePointCoefficient":1.0,"loseBattlePointCoefficient":1.0,"demontioableFlag":false},
              {"id":14,"name":"S⁺6","rank":13,"regularMatchFlag":false,"totalBattlePoint":5900,"winBattlePointCoefficient":1.0,"loseBattlePointCoefficient":1.0,"demontioableFlag":false},
              {"id":15,"name":"S⁺7","rank":14,"regularMatchFlag":false,"totalBattlePoint":6400,"winBattlePointCoefficient":1.0,"loseBattlePointCoefficient":1.0,"demontioableFlag":false},
              {"id":16,"name":"S⁺8","rank":15,"regularMatchFlag":false,"totalBattlePoint":6900,"winBattlePointCoefficient":1.0,"loseBattlePointCoefficient":1.0,"demontioableFlag":false},
              {"id":17,"name":"S⁺9","rank":16,"regularMatchFlag":false,"totalBattlePoint":7500,"winBattlePointCoefficient":1.0,"loseBattlePointCoefficient":1.0,"demontioableFlag":false}
            ]
            """);
        _encryptedMasters["CapsuleCampaign"] = EncryptMaster("""[{"id":1,"startDatetime":"2019-01-01 00:00:00","endDatetime":"2030-01-01 23:59:59","coefficient":0.5}]""");
        _encryptedMasters["CapsuleDropCampaign"] = EncryptMaster("""[{"id":1,"startDatetime":"2019-01-01 00:00:00","endDatetime":"2030-01-01 23:59:59"}]""");
        _encryptedMasters["BattleRankingClass"] = EncryptMaster("""[{"id":1,"battleRuleId":1,"name":"Class 1","minBattlePoint":0},{"id":2,"battleRuleId":2,"name":"Class 1","minBattlePoint":0},{"id":3,"battleRuleId":3,"name":"Class 1","minBattlePoint":0},{"id":4,"battleRuleId":4,"name":"Div.1","minBattlePoint":0},{"id":5,"battleRuleId":5,"name":"Div.1","minBattlePoint":0},{"id":6,"battleRuleId":6,"name":"Class 1","minBattlePoint":0},{"id":7,"battleRuleId":7,"name":"Div.1","minBattlePoint":0},{"id":8,"battleRuleId":8,"name":"Div.1","minBattlePoint":0}]""");
        _encryptedMasters["HomeFieldSchedule"] = EncryptMaster("""[{"id":1,"fieldId":99999,"startDatetime":"2019-01-01 00:00:00","endDatetime":"2030-01-01 23:59:59"}]""");
        _encryptedMasters["Festival"] = EncryptMaster("""[{"id":1,"battleRuleId":6,"theme":"Kick-Flight Festival"}]""");
        _encryptedMasters["FestivalTeam"] = EncryptMaster("""[{"id":1,"battleRuleId":6,"name":"Red Team","color":"#FF0000"},{"id":2,"battleRuleId":6,"name":"Blue Team","color":"#0000FF"}]""");
        _encryptedMasters["BattleRuleParameter"] = EncryptMaster("""
            [
              {"id":1,"battleRuleId":1,"description":"without affecting your rank","subRuleName":"Fun Match","itemDecelerationMaxCount":0},
              {"id":2,"battleRuleId":2,"description":"without affecting your rank","subRuleName":"Fun Match","itemDecelerationMaxCount":0},
              {"id":3,"battleRuleId":3,"description":"without affecting your rank","subRuleName":"Fun Match","itemDecelerationMaxCount":0},
              {"id":4,"battleRuleId":4,"description":"Intense battles at S ranks","subRuleName":"Ranked Match","itemDecelerationMaxCount":0},
              {"id":5,"battleRuleId":5,"description":"Season 1","subRuleName":"Season Match","itemDecelerationMaxCount":0},
              {"id":6,"battleRuleId":6,"description":"Festival Match","subRuleName":"Kick-Flight Festival","itemDecelerationMaxCount":0},
              {"id":7,"battleRuleId":7,"description":"Intense battles at S ranks","subRuleName":"Ranked Match","itemDecelerationMaxCount":0},
              {"id":8,"battleRuleId":8,"description":"Intense battles at S ranks","subRuleName":"Ranked Match","itemDecelerationMaxCount":0}
            ]
            """);
        _encryptedMasters["Capsule"] = EncryptMaster("""
            [
              {"id":5010001,"lotteryId":1,"name":"Standard Capsule","openTime":"01:00:00","lotteryDiscRarityGroupId":1,"lotteryDiscDropAmountGroupId":1},
              {"id":5010002,"lotteryId":2,"name":"Golden Capsule","openTime":"12:00:00","lotteryDiscRarityGroupId":2,"lotteryDiscDropAmountGroupId":2},
              {"id":5010003,"lotteryId":3,"name":"Wooden Capsule","openTime":"03:00:00","lotteryDiscRarityGroupId":1,"lotteryDiscDropAmountGroupId":1}
            ]
            """);

        var weaponList = new List<object>();
        int weaponId = 1;
        var catalogPath = RepositoryPaths.Resolve("config/resources/catalog.json", contentRoot);
        bool loadedFromCatalog = false;

        if (File.Exists(catalogPath))
        {
            try
            {
                using var catDoc = JsonDocument.Parse(File.ReadAllText(catalogPath));
                if (catDoc.RootElement.TryGetProperty("resources", out var resArray))
                {
                    // weaponType per kicker: a Drone (4) is not held, it hovers from the body's Prop_Common bone
                    var weaponTypes = new Dictionary<int, int>();
                    try
                    {
                        using var kpDoc = JsonDocument.Parse(parameterJson);
                        foreach (var k in kpDoc.RootElement.EnumerateArray())
                            if (k.TryGetProperty("weaponType", out var wt)) weaponTypes[k.GetProperty("kickerId").GetInt32()] = wt.GetInt32();
                    }
                    catch (Exception ex) { _logger.LogWarning("Could not read weapon types: {Error}", ex.Message); }
                    var validWeapons = new SortedSet<(int kickerId, int modelId, int propId)>();
                    var weaponRegex = new System.Text.RegularExpressions.Regex(@"weapon/wp_(\d+)/wp_\d+_(\d+)_(\d+)\.unity3d");
                    foreach (var res in resArray.EnumerateArray())
                    {
                        if (res.TryGetProperty("logicalName", out var logName))
                        {
                            var match = weaponRegex.Match(logName.GetString() ?? "");
                            if (match.Success)
                            {
                                validWeapons.Add((
                                    int.Parse(match.Groups[1].Value),
                                    int.Parse(match.Groups[2].Value),
                                    int.Parse(match.Groups[3].Value)
                                ));
                            }
                        }
                    }

                    if (validWeapons.Count > 0)
                    {
                        foreach (var (k, m, p) in validWeapons)
                        {
                            // props 101/201 are alternate right-hand models, 102/202 alternate left-hand ones (JapaneseSword
                            // "OverSoul" 201/202, Drone/Laser 101...). A row with an empty bone is parented to nothing and
                            // JapaneseSwordAction.Initialize then dies in GetComponentInParent -> the whole home/select model fails.
                            // Only the base props are attach rows. 101/201/202 (Drone extras, JapaneseSword "OverSoul"
                            // forms...) are swapped in by the weapon/skill code itself; served as attach rows they leave
                            // Owlbert/Jay/Yuyan/Hitagi without a model in Home and character select.
                            weaponTypes.TryGetValue(k, out var kwt);
                            string bone;
                            var attach = 1;                                               // PropAttachType.Child
                            // Bone names come from the client: ShieldPropTypeExtensions/LaserPropTypeTypeExtensions.AttachBoneName
                            // resolve the wrist props by "Prop_R2"/"Prop_L2" (Buzzy's shields, Sid's lasers - the *2 bones sit on
                            // the forearm with the opposite orientation, which is why Prop_L put the left shield on the wrong side of
                            // the arm and the lasers pointed sideways); everything hand-held uses "Prop_R"/"Prop_L".
                            // Drone (Owlbert) and Nunchaku (Yuyan) weapons are animated by the body clips: the clips bind
                            // "Root/wp_005_001_Root/wp_005_001_Hip/..." (the drone hover, and the SS mini drones under
                            // wp_005_101_Root / wp_005_201_Root), ".../Hand_R/Prop_R/wp_010_001_Root/wp_010_001_Grip_L" (the
                            // nunchaku swing / dangling panda head) and "Root/wp_010_201_Root" (the SS panda mount), so those rows
                            // need the exact bone and the model root renamed to "wp_<kicker>_<prop>_Root" (Weapon.Initialize sets
                            // the instantiated model's name from rootName). Verified with scripts/re/anim_paths.py (CRC32 path
                            // hashes of every clip vs. the body + weapon prefab hierarchies).
                            var wristProps = kwt == 9 || kwt == 13;                       // Shield, Laser
                            if (p == 1) bone = kwt == 4 ? "Root" : wristProps ? "Prop_R2" : "Prop_R";
                            else if (p == 2) bone = wristProps ? "Prop_L2" : "Prop_L";
                            else if (p == 201 && kwt == 11) bone = "Root";               // Nunchaku panda mount (NunchakuPandaAction)
                            else if (p == 101 && kwt == 13) bone = "Root";               // Laser prop 101 = Sid's decoy statue (LaserPropType.KickerSkillStatue):
                                                                                        // StatueTrapAction..ctor does ModelManager.InstantiateWeaponModel(kicker,
                                                                                        // model, 101, parent: trap) and LoadManager only preloads the weapon
                                                                                        // bundles that have a Weapon row (LoadWeaponModelAsync ->
                                                                                        // GetWeaponAttachData), so without this row the cache misses and the
                                                                                        // kicker skill NREs in the ctor (logcat 2026-09-21). Hidden on the
                                                                                        // player like every prop >= 100; "Root" so LaserAttackAction's
                                                                                        // GetWeapon("Prop_R2") never resolves to the statue.
                            else if ((p == 201 && (kwt == 4 || kwt == 10)) || (p == 101 && kwt == 4))
                            {
                                // Drone prop 101 is the drone unit itself: NPCDrone.InitializeAsync instantiates weapon prop 101
                                // and reads owner.GetWeapon(101).ModelCtr, so Owlbert's kicker skill (silence drone trap) and
                                // special (smog drones for the team) spawn nothing without this row.
                                // HighPlayerCharacter builds its weapons from the modelId-1 rows too (ObjectUtil.
                                // GetHighWeaponAttachData(id, 1)), so the cut-in rows must exist for modelId 1, not only 101.
                                // Drone (Owlbert) and Bat (Jay) special-skill cut-ins call HighPlayerCharacter.GetWeapon(201)
                                // and dereference its ModelCtr (DroneSpecialSkillCutAction/BatSpecialSkillCutAction.Initialize):
                                // without a 201 row the NRE kills PlayerCharacter.ResetPlayer and the battle never loads.
                                // Attached as a Child like prop 1 (props >= 100 are hidden by Weapon.Initialize and shown by the
                                // skill code itself); the old breakage with 101/201 rows came from serving them as
                                // PropAttachType.Replace rows.
                                bone = kwt == 4 ? "Root" : "Prop_R";
                            }
                            else continue;
                            weaponList.Add(new { id = weaponId++, kickerId = k, modelId = m, propId = p, boneName = bone, rootName = $"wp_{k:000}_{p:000}_Root", attachType = attach });
                        }
                        loadedFromCatalog = true;
                        _logger.LogInformation("Loaded {Count} authentic weapon master entries from catalog.json", weaponList.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to parse weapon catalog from {Path}: {Error}", catalogPath, ex.Message);
            }
        }

        if (!loadedFromCatalog)
        {
            var kickerProps = new Dictionary<int, int[]>
            {
                [1] = [1],
                [2] = [1],
                [3] = [1],
                // 4 has no weapons
                [5] = [1],
                [6] = [1],
                [7] = [1],
                [8] = [1],
                [9] = [1],
                [10] = [1],
                // 11 has no weapons
                [12] = [1, 2],
                [13] = [1, 2],
                [14] = [1, 2]
            };

            foreach (var (k, props) in kickerProps)
            {
                foreach (var mId in new[] { 1, 101 })
                {
                    foreach (var pId in props)
                    {
                        var bone = pId == 1 ? "Prop_R" : pId == 2 ? "Prop_L" : "";
                        var attach = (pId == 1 || pId == 2) ? 1 : 2;
                        weaponList.Add(new { id = weaponId++, kickerId = k, modelId = mId, propId = pId, boneName = bone, rootName = "", attachType = attach });
                    }
                }
            }
        }

        // TwoGuns kickers fire from both hands: TwoGunsAttackAction resolves the weapon by bone name ("Prop_R" for
        // RightHandWeapon, "Prop_L" for LeftHandWeapon) and the only bundle that exists is prop 1, so the left gun is a
        // second attach row of the same model on Prop_L (otherwise PlayerCharacter.GetWeapon returns null -> NRE per shot).
        try
        {
            using var kpDoc = JsonDocument.Parse(parameterJson);
            var twoGunKickers = kpDoc.RootElement.EnumerateArray()
                .Where(k => k.TryGetProperty("weaponType", out var wt) && wt.GetInt32() == 1)
                .Select(k => k.GetProperty("kickerId").GetInt32()).ToHashSet();
            var leftHand = new List<object>();
            foreach (var w in weaponList)
            {
                var wj = JsonSerializer.SerializeToElement(w);
                if (twoGunKickers.Contains(wj.GetProperty("kickerId").GetInt32()) && wj.GetProperty("propId").GetInt32() == 1)
                    leftHand.Add(new { id = weaponId++, kickerId = wj.GetProperty("kickerId").GetInt32(), modelId = wj.GetProperty("modelId").GetInt32(), propId = 1, boneName = "Prop_L", rootName = "", attachType = 1 });
            }
            weaponList.AddRange(leftHand);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not add left-hand weapon rows: {Error}", ex.Message);
        }
        _encryptedMasters["Weapon"] = EncryptMaster(JsonSerializer.Serialize(weaponList));



        var levelExpList = Enumerable.Range(1, 50).Select(lvl => new
        {
            id = lvl,
            level = lvl,
            totalExp = (lvl - 1) * 1000
        });
        _encryptedMasters["PlayerLevelExp"] = EncryptMaster(JsonSerializer.Serialize(levelExpList));

        _encryptedMasters["Frame"] = EncryptMaster("""[{"id":1,"name":"Standard Frame","battleRuleId":0}]""");
        // ItemMasterData: MasterData(id) + goodsType, name, maxAmount. ids 1-4 are the legacy counters; 5-8 carry the
        // goodsTypes the gear screen (202 gear stamps), the gacha (401/402 tickets) and the goods shop trade in.
        var itemJson = """
            [
              {"id":1,"goodsType":101,"name":"JetCoin","maxAmount":9999999},
              {"id":2,"goodsType":102,"name":"PaidJetCoin","maxAmount":9999999},
              {"id":3,"goodsType":302,"name":"DiscForce","maxAmount":9999999},
              {"id":4,"goodsType":701,"name":"KickPoint","maxAmount":9999999},
              {"id":5,"goodsType":202,"name":"GearMaterial","maxAmount":9999999},
              {"id":6,"goodsType":303,"name":"DiscFragment","maxAmount":9999999},
              {"id":7,"goodsType":401,"name":"KickerGachaTicket","maxAmount":9999999},
              {"id":8,"goodsType":402,"name":"DiscGachaTicket","maxAmount":9999999}
            ]
            """;
        _encryptedMasters["Item"] = EncryptMaster(itemJson);
        ParseItemMaster(itemJson);

        var discJson = LoadJson(contentRoot, "config/masters_disc.json", "[]");
        var skillJson = LoadJson(contentRoot, "config/masters_skill.json", "[]");
        var discGrowJson = LoadJson(contentRoot, "config/masters_disc_grow.json", "[]");
        var discBuildupJson = LoadJson(contentRoot, "config/masters_disc_buildup.json", "[]");
        var initialDiscDeckJson = LoadJson(contentRoot, "config/masters_initial_disc_deck.json", "[]");
        var gearJson = LoadJson(contentRoot, "config/masters_gear.json", "[]");
        var gearSkillJson = LoadJson(contentRoot, "config/masters_gear_skill.json", "[]");
        var gearSameColorBonusJson = LoadJson(contentRoot, "config/masters_gear_same_color_bonus.json", "[]");

        try
        {
            using var dDoc = JsonDocument.Parse(discJson);
            foreach (var el in dDoc.RootElement.EnumerateArray())
            {
                var id = el.GetProperty("id").GetInt32();
                _discIdList.Add(id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error parsing disc master json: {Error}", ex.Message);
        }

        if (_discIdList.Count == 0)
        {
            for (var i = 3010001; i <= 3010135; i++) _discIdList.Add(i);
        }

        try
        {
            using var gDoc = JsonDocument.Parse(gearJson);
            foreach (var el in gDoc.RootElement.EnumerateArray())
            {
                _gearIdList.Add(el.GetProperty("id").GetInt32());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error parsing gear master json: {Error}", ex.Message);
        }

        _encryptedMasters["Disc"] = EncryptMaster(ApplyObscuredOffsets("Disc", discJson));
        _encryptedMasters["Skill"] = EncryptMaster(ApplyObscuredOffsets("Skill", skillJson));
        _encryptedMasters["DiscGrow"] = EncryptMaster(discGrowJson);
        _encryptedMasters["DiscBuildup"] = EncryptMaster(discBuildupJson);
        _encryptedMasters["DiscForceCampaign"] = EncryptMaster("[]");
        _encryptedMasters["InitialDiscDeck"] = EncryptMaster(initialDiscDeckJson);
        _encryptedMasters["Gear"] = EncryptMaster(gearJson);
        _encryptedMasters["GearSkill"] = EncryptMaster(gearSkillJson);
        _encryptedMasters["GearSameColorBonus"] = EncryptMaster(gearSameColorBonusJson);

        var aiParamJson = LoadJson(contentRoot, "config/masters_kicker_ai_parameter.json", "[]");
        var aiDiscJson = LoadJson(contentRoot, "config/masters_kicker_ai_disc.json", "[]");
        var aiDeckJson = LoadJson(contentRoot, "config/masters_kicker_ai_disc_deck.json", "[]");
        var pingThresholdJson = LoadJson(contentRoot, "config/masters_matchmaking_ping_threshold.json", "[]");

        var aiParamRows = AppendGymAiRows(aiParamJson);
        _baseAiParamRows = aiParamRows;
        if (ExperimentalAiRoleService.IsEnabled)
        {
            var snapshot = _botAiProfiles.CurrentSnapshot;
            aiParamRows = ExperimentalAiRoleService.AppendExperimentalRoleAiRows(aiParamRows, snapshot);
        }
        _encryptedMasters["KickerAiParameter"] = EncryptMaster(aiParamRows);
        _encryptedMasters["KickerAiDisc"] = EncryptMaster(aiDiscJson);
        _encryptedMasters["KickerAiDiscDeck"] = EncryptMaster(aiDeckJson);
        _encryptedMasters["MatchmakingPingThreshold"] = EncryptMaster(pingThresholdJson);

        // Battle & Combat Masters. Every table is a config/masters_*.json file (templates come from
        // scripts/generate_combat_masters.py, column meanings in docs/COMBAT_MASTERS_FILL_IN.md). The client
        // throws NullReferenceException inside animation events when the per-kicker WeaponAttackHit/Collision
        // rows or the per-skill Summon row are missing, so a missing file falls back to an empty table only.
        foreach (var (masterName, fileName) in new (string, string)[]
        {
            ("WeaponAttack", "masters_weapon_attack.json"),
            ("WeaponAttackHit", "masters_weapon_attack_hit.json"),
            ("WeaponAttackCollision", "masters_weapon_attack_collision.json"),
            ("WeaponAttackBullet", "masters_weapon_attack_bullet.json"),
            ("WeaponAttackCondition", "masters_weapon_attack_condition.json"),
            ("SkillCondition", "masters_skill_condition.json"),
            ("SkillHeal", "masters_skill_heal.json"),
            ("SkillBlowOff", "masters_skill_blow_off.json"),
            ("SkillPullIn", "masters_skill_pull_in.json"),
            ("SkillTrap", "masters_skill_trap.json"),
            ("SkillCollision", "masters_skill_collision.json"),   // guardian turret beam (NPCSkillParameter.SetCollisionInitInfos)
            ("SkillHit", "masters_skill_hit.json"),               // guardian turret beam (NPCSkillParameter.SetAttackHitInfos)
            ("Summon", "masters_summon.json"),
            ("CommonConditionHit", "masters_common_condition_hit.json"),
            ("SpecialSkill", "masters_special_skill.json"),
            ("SpecialSkillHit", "masters_special_skill_hit.json"),
            ("SpecialSkillCollision", "masters_special_skill_collision.json"),
            ("SpecialSkillBullet", "masters_special_skill_bullet.json"),
            ("SpecialSkillCondition", "masters_special_skill_condition.json"),
            ("SpecialSkillBlowOff", "masters_special_skill_blow_off.json"),
            ("SpecialSkillTrap", "masters_special_skill_trap.json"),
            // Result-screen stamps: ResultUIManager.PlayRandomStamp picks from GoodsUtil.GetStampMaster(1, {Win|Lose|Draw}),
            // i.e. StampSituation rows whose Stamp has defaultFlag; with neither table served the list was empty and
            // List.get_Item(0) threw ArgumentOutOfRangeException on every result screen.
            ("Stamp", "masters_stamp.json"),
            ("StampSituation", "masters_stamp_situation.json"),
        })
        {
            var json = LoadJson(contentRoot, "config/" + fileName, "[]");
            if (json.Trim() == "[]" && masterName is "WeaponAttack" or "WeaponAttackHit" or "WeaponAttackCollision" or "Summon" or "SpecialSkill" or "SpecialSkillHit" or "SpecialSkillCollision")
                _logger.LogWarning("Combat master {Master} is empty ({File} missing?) - attacks/skills will throw in the client", masterName, fileName);
            _encryptedMasters[masterName] = EncryptMaster(ApplyObscuredOffsets(masterName, json));
        }

        // PlayerStateDeposit.get_DepositTime = powf(depositCount, crystalDepositSpeedCoefficient) - 0.2, depositCount
        // starting at 1 and +1 per crystal of the same deposit. 1.0 made every consecutive crystal slower (0.8, 1.8,
        // 2.8 s ...); a negative exponent makes them faster: -0.5 -> 0.8, 0.51, 0.38, 0.30, 0.25 s.
        _encryptedMasters["BattleRuleScramble"] = EncryptMaster("""
            [
              {"id":1,"battleRuleId":1,"crystalDepositSpeedCoefficient":-0.1},
              {"id":2,"battleRuleId":5,"crystalDepositSpeedCoefficient":-0.1},
              {"id":3,"battleRuleId":6,"crystalDepositSpeedCoefficient":-0.1},
              {"id":4,"battleRuleId":7,"crystalDepositSpeedCoefficient":-0.1}
            ]
            """);

        var roleParams = new List<object>();
        int rId = 1;
        foreach (var rule in Enumerable.Range(1, 6).Append(RankedModeRotation.CrystalRuleId)
                     .Append(RankedModeRotation.FlagRuleId))
        {
            for (int role = 1; role <= 4; role++)
            {
                roleParams.Add(new { id = rId++, battleRuleId = rule, roleType = role, crystalCountSpeedCorrection = 1.0f });
            }
        }
        _encryptedMasters["BattleRuleRoleParameter"] = EncryptMaster(JsonSerializer.Serialize(roleParams));

        _encryptedMasters["BattleRuleScrambleScore"] = EncryptMaster("""
            [
              {
                "id": 1,
                "battleRuleId": 1,
                "baseScore": 100,
                "stackedCountScore": 10,
                "depositCount": 10,
                "crystalCountScore": 10,
                "dropCountScore": 10,
                "killCountScore": 50,
                "killAssistCountScore": 25,
                "assistCountScore": 25,
                "specialSkillCountScore": 30,
                "winScoreCorrection": 1.5
              },
              {
                "id": 7,
                "battleRuleId": 7,
                "baseScore": 100,
                "stackedCountScore": 10,
                "depositCount": 10,
                "crystalCountScore": 10,
                "dropCountScore": 10,
                "killCountScore": 50,
                "killAssistCountScore": 25,
                "assistCountScore": 25,
                "specialSkillCountScore": 30,
                "winScoreCorrection": 1.5
              }
            ]
            """);

        // BallShootRuleController.GetScore / FlagFlightRuleController.GetScore read these every frame and at match end
        // (RuleControllerBase.CalcScore); a missing row NREs and the client never leaves "Cargando". The lookup is
        // TMasterBase.get_Item(battleRuleId) - by row *id*, with a hard-coded fallback to id 3 - so each row's id must
        // equal its battleRuleId (verified 2026-09-19: ids 1/2 kept the NRE, ids 3/4 fixed it).
        _encryptedMasters["BattleRuleRapidBallScore"] = EncryptMaster(LoadJson(contentRoot, "config/masters_battle_rule_rapid_ball_score.json", "[]"));
        _encryptedMasters["BattleRuleFlagFlightScore"] = EncryptMaster(LoadJson(contentRoot, "config/masters_battle_rule_flag_flight_score.json", BattleRuleFlagFlightScoreFallbackJson));

        _encryptedMasters["Guardian"] = EncryptMaster("""
            [
              {"id":1,"modelId":1,"skillId":1,"attack":100,"defense":100,"laserLength":50.0,"targetSearchRadius":30.0,"aIInterval":1.0}
            ]
            """);

        var kickerAis = Enumerable.Range(1, 14).Select(k => new
        {
            id = k,
            kickerId = k,
            kickerAiParameterId = k
        });
        _encryptedMasters["KickerAi"] = EncryptMaster(JsonSerializer.Serialize(kickerAis));

        // The tutorial fights a scripted battle instead of loading a normal home, which is why this table cannot
        // stay empty. HomeScene.<PreBeginAsync>d__3 skips NetworkManager.HomeAsync whenever TutorialUtil.IsTutorial
        // (tutorialProgressStatus != 207), so the tutorial scene has to get everything it needs from the masters;
        // with no rows here the lookup came back null, the home never finished loading and the client hung on the
        // LOADING splash with a NullReferenceException in that same MoveNext (verified 2026-09-22: logcat
        // 17:30:52.923, and no POST /home/index in the API log at all).
        //
        // The rows mirror KickerAi, whose shape is proven against this client (bots fight with it). The extra keys
        // are deliberate: the tutorial's row class is TutorialKickerAiMasterData and we have not recovered its
        // field list, so each row carries the spellings the KickerAi family uses - the family prefixes the *class*
        // (KickerAiMasterData, KickerAiDiscDeckMasterData) but shares the *fields* (id, kickerAiParameterId,
        // kickerAiDiscId1..4) - and Unity's JsonUtility ignores the ones the class does not declare. Remove the
        // aliases once the real field list is known. kickerAiDiscDeckId stays 1 because KickerAiDiscDeck has rows
        // 1..6 and a reference to a missing deck is the same kind of null.
        // Seven rows, not one per kicker: BattleUtil.CreateTutorialBattleInfo (RVA 0x15BB6D0) sorts the rows by
        // TeamNumber and copies rows 3..N-1 (control tutorial) or 0..N-1 into a PlayerBattleInfo[] of fixed length
        // 5 / 8 (slot 0 = the player), so more than 7 rows throws IndexOutOfRangeException at 0x15BC32C and the
        // tutorial hangs on its loading screen. teamNumber = k keeps that sort stable.
        var tutorialKickerAis = Enumerable.Range(1, 7).Select(k => new
        {
            id = k,
            teamNumber = k,
            kickerId = k,
            kickerAiParameterId = k,
            kickerAiDiscDeckId = 1,
            tutorialKickerAiId = k,
            tutorialKickerAiParameterId = k
        });
        _encryptedMasters["TutorialKickerAi"] = EncryptMaster(JsonSerializer.Serialize(tutorialKickerAis));

        // Round C: the gacha and goods shop tables (config/masters_gacha_group.json and friends), plus the drop
        // tables the draw endpoints read. Registered here so the master hash covers them too.
        InitializeGachaMasters(contentRoot);

        // Replay channels: BattleReplayChannelMaster (type-2 Kicker rows + the type-3 Featured row) and
        // MovieCategoryMaster (the Pickup tab's categories; empty keeps it empty). The served key is the client's
        // master class name without the "Master" suffix - the same convention as every other table here (see
        // docs/REPLAYS.md for the disassembly evidence: MasterBase.get_ClassName, RVA 0x1798DF4).
        var battleReplayChannelJson = LoadJson(contentRoot, "config/masters_battle_replay_channel.json",
            BuildBattleReplayChannelFallbackJson());
        var movieCategoryJson = LoadJson(contentRoot, "config/masters_movie_category.json", "[]");
        _encryptedMasters["BattleReplayChannel"] = EncryptMaster(battleReplayChannelJson);
        _encryptedMasters["MovieCategory"] = EncryptMaster(movieCategoryJson);

        string masterVersion;
        using (var sha = SHA256.Create())
        {
            foreach (var kvp in _encryptedMasters.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var nameBytes = Encoding.UTF8.GetBytes(kvp.Key);
                sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
                sha.TransformBlock(kvp.Value, 0, kvp.Value.Length, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            masterVersion = "demo-master-" + Convert.ToHexString(sha.Hash!)[..16].ToLowerInvariant();
        }
        _masterState = new MasterCatalogState(_encryptedMasters.ToFrozenDictionary(StringComparer.Ordinal), masterVersion);
        _logger.LogInformation("Master version {MasterVersion} ({Count} tables)", masterVersion, _masterState.Masters.Count);
    }

    public void OnBotAiProfilesReloaded(BotAiProfileSnapshot snapshot)
    {
        if (!ExperimentalAiRoleService.IsEnabled) return;

        lock (_masterLock)
        {
            if (string.IsNullOrEmpty(_baseAiParamRows)) return;

            var updatedAiRows = ExperimentalAiRoleService.AppendExperimentalRoleAiRows(_baseAiParamRows, snapshot);
            var newEncrypted = EncryptMaster(updatedAiRows);

            var current = Volatile.Read(ref _masterState);
            var dict = new Dictionary<string, byte[]>(current.Masters, StringComparer.Ordinal)
            {
                ["KickerAiParameter"] = newEncrypted
            };

            string newMasterVersion;
            using (var sha = SHA256.Create())
            {
                foreach (var kvp in dict.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    var nameBytes = Encoding.UTF8.GetBytes(kvp.Key);
                    sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
                    sha.TransformBlock(kvp.Value, 0, kvp.Value.Length, null, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                newMasterVersion = "demo-master-" + Convert.ToHexString(sha.Hash!)[..16].ToLowerInvariant();
            }

            Volatile.Write(ref _masterState, new MasterCatalogState(dict.ToFrozenDictionary(StringComparer.Ordinal), newMasterVersion));
            _logger.LogInformation("Master version {MasterVersion} updated atomically after bot AI profile reload (version {ProfileVersion})",
                newMasterVersion, snapshot.Version);
        }
    }

    public void Dispose()
    {
        _botAiProfiles.ProfilesReloaded -= OnBotAiProfilesReloaded;
    }

    // Fallback for a checkout without config/masters_battle_replay_channel.json: the same table the config file
    // holds (Featured type-3 id 1 + one type-2 row per kicker, channel id 1000+kickerId), built from the kicker
    // master so names stay real.
    private string BuildBattleReplayChannelFallbackJson()
    {
        var rows = new List<object>
        {
            new { id = 1, battleReplayChannelType = 3, name = "Featured", kickerId = 0, minRank = 1, maxRank = 99, matchType = 1, sortOrder = 1 }
        };
        var sortOrder = 1;
        foreach (var kicker in _kickerList)
        {
            rows.Add(new
            {
                id = 1000 + kicker.Id,
                battleReplayChannelType = 2,
                name = kicker.Name,
                kickerId = kicker.Id,
                minRank = 1,
                maxRank = 99,
                matchType = 1,
                sortOrder = sortOrder++
            });
        }
        return JsonSerializer.Serialize(rows);
    }

    // Loads a player, creating them empty if the id is unknown, and repairs whatever the masters no longer
    // support. The repair is policy, which is why it stays here: it needs _discIdList, and the store is only
    // responsible for persisting the document.
    private SessionState GetOrCreateUserState(long playerId)
    {
        var loaded = _playerStore.TryLoad(playerId);
        if (loaded is not null)
        {
            foreach (var discId in _discIdList)
            {
                if (!loaded.Discs.ContainsKey(discId))
                {
                    // A disc added to the masters after this save was written: hand it over at the base level so
                    // the client's disc list matches the masters it just downloaded.
                    loaded.Discs[discId] = new UserDiscState { DiscId = discId, Level = 10, Amount = 99 };
                }
            }
            // a deck slot pointing at a disc that no longer exists in masters_disc.json makes the
            // client throw in PlayerDeckParameter..ctor and the battle never loads: swap it for a valid disc
            foreach (var (deckNumber, deck) in loaded.Decks)
            {
                for (var slot = 0; slot < deck.Count; slot++)
                {
                    if (_discIdList.Contains(deck[slot])) continue;
                    var replacement = _discIdList.FirstOrDefault(id => !deck.Contains(id), _discIdList[0]);
                    _logger.LogWarning("User {PlayerId} deck {Deck} slot {Slot}: disc {DiscId} is not in the masters, replaced with {Replacement}",
                        playerId, deckNumber, slot + 1, deck[slot], replacement);
                    deck[slot] = replacement;
                }
            }
            return loaded;
        }

        var state = NewUserState(playerId);
        SaveUserState(state);
        return state;
    }

    private SessionState NewUserState(long playerId)
    {
        var state = new SessionState { UserId = playerId.ToString() };
        foreach (var discId in _discIdList)
        {
            state.Discs[discId] = new UserDiscState { DiscId = discId, Level = 10, Amount = 99 };
        }
        return state;
    }

    private void SaveUserState(SessionState state, string? uuid = null) => _playerStore.Save(state, uuid);

    // Gym-mode bots use KickerAiParameter row id 100 + kickerId (BattleMatchmakingService.GymAiParameterBase): a copy
    // of the kicker's real row with every motivation at 0, no lock-on and very long skill/dash intervals, so even a
    // client without the Mannequin patch mostly stands still. The row must exist or the bot never gets an AI engine.
    private static string AppendGymAiRows(string aiParamJson)
    {
        try
        {
            var rows = System.Text.Json.Nodes.JsonNode.Parse(aiParamJson) as System.Text.Json.Nodes.JsonArray;
            if (rows is null) return aiParamJson;
            var extra = new List<System.Text.Json.Nodes.JsonNode>();
            foreach (var row in rows)
            {
                if (row is not System.Text.Json.Nodes.JsonObject obj) continue;
                var copy = (System.Text.Json.Nodes.JsonObject)obj.DeepClone();
                copy["id"] = BattleMatchmakingService.GymAiParameterBase + (obj["id"]?.GetValue<int>() ?? 0);
                copy["attackMotivation"] = 0;
                copy["defenseMotivation"] = 0;
                copy["scoreMotivation"] = 0;
                copy["canLockon"] = false;
                foreach (var col in new[] { "attackDashInterval", "defenseDashInterval", "scoreDashInterval", "attackSkillInterval",
                                            "healSkillInterval", "buffSkillInterval", "trapSkillInterval", "warpSkillInterval" })
                {
                    copy[col] = 9999.0;
                }
                extra.Add(copy);
            }
            foreach (var e in extra) rows.Add(e);
            return rows.ToJsonString();
        }
        catch (Exception)
        {
            return aiParamJson;
        }
    }

    // A costume row that belongs to another kicker (hand-edited user file, or a file saved before costume ids were
    // row ids) makes KickerDisplayPresenter.UpdateView work on the wrong kicker's costume; fall back to the kicker's
    // first row (its standard colour).
    private void NormalizeCostume(SessionState state)
    {
        if (_costumeKicker.TryGetValue(state.KickerCostumeId, out var owner) && owner == state.KickerId)
        {
            return;
        }
        if (_costumesByKicker.TryGetValue(state.KickerId, out var rows) && rows.Count > 0)
        {
            _logger.LogInformation("Costume {Costume} does not belong to kicker {Kicker}; using row {Row}", state.KickerCostumeId, state.KickerId, rows[0]);
            state.KickerCostumeId = rows[0];
        }
    }

    private string LoadJson(string contentRoot, string relativePath, string fallback) =>
        _mastersOverrides.Read(contentRoot, relativePath, fallback);

    private (int? MatchType, int? BattleRuleType) BattleRuleMetadata(int battleRuleId)
    {
        int? matchType = _battleMatchTypeById.TryGetValue(battleRuleId, out var foundMatchType)
            ? foundMatchType
            : null;
        int? battleRuleType = _battleRuleTypeById.TryGetValue(battleRuleId, out var foundRuleType)
            ? foundRuleType
            : null;
        return (matchType, battleRuleType);
    }

    // The client's master getters subtract a fixed anti-tamper offset from these integer columns
    // (SkillMasterData.get_CoolTime = coolTime - 230, DiscMasterData.get_MinHp = minHp - 928, ...), so the served
    // JSON must carry value + offset. Config files hold the human-readable values; the offset is added here.
    private static readonly Dictionary<string, (string field, int offset)[]> ObscuredMasterOffsets = new(StringComparer.Ordinal)
    {
        ["Skill"] = [("coolTime", 0xe6)],
        ["Disc"] = [("minHp", 0x3a0), ("maxHp", 0x2cb), ("minAttack", 0xa7), ("maxAttack", 0x2ad)],
        ["KickerAbility"] = [("overlapCount", 0xcb)],
        ["SpecialSkillHit"] = [("fixedDamage", 0x1c6)],
    };

    private static string ApplyObscuredOffsets(string masterName, string json)
    {
        if (!ObscuredMasterOffsets.TryGetValue(masterName, out var fields)) return json;
        var root = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonArray;
        if (root == null) return json;
        foreach (var row in root.OfType<System.Text.Json.Nodes.JsonObject>())
        {
            foreach (var (field, offset) in fields)
            {
                if (row[field] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<double>(out var d))
                    row[field] = (int)Math.Round(d) + offset;
            }
        }
        return root.ToJsonString();
    }

    private static byte[] EncryptMaster(string json) =>
        D2CCodec.Encode(Encoding.UTF8.GetBytes(json), Encoding.ASCII.GetBytes(CommonCode), new byte[16]);

    internal byte[]? GetEncryptedMaster(string masterName) =>
        Volatile.Read(ref _masterState).Masters.TryGetValue(masterName, out var bytes) ? bytes : null;

    public async Task<IResult?> TryHandleAsync(HttpContext context)
    {
        var masterState = Volatile.Read(ref _masterState);
        context.Response.Headers["x-app-datetime"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        context.Response.Headers["x-app-master-hash"] = masterState.MasterVersion;
        var path = context.Request.Path.Value ?? "/";

        if (HttpMethods.IsGet(context.Request.Method) && path.StartsWith("/demo-master/", StringComparison.Ordinal))
        {
            var masterName = path["/demo-master/".Length..];
            if (masterState.Masters.TryGetValue(masterName, out var masterBytes))
            {
                context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                context.Response.Headers["ETag"] = $"\"{Convert.ToHexString(SHA256.HashData(masterBytes)).ToLowerInvariant()}\"";
                return new LocalFixtureResult(masterBytes, "application/octet-stream", StatusCodes.Status200OK);
            }
            return Results.NotFound();
        }

        // The replay blob /battleReplay/play handed out. UnityWebRequest.Get sends no session headers, so this is
        // authenticated by the one-shot key id in the URL only.
        if (HttpMethods.IsGet(context.Request.Method) && path.StartsWith("/battleReplay/blob/", StringComparison.Ordinal))
        {
            var keyId = path["/battleReplay/blob/".Length..];
            return _replays.TryEncodeBlob(keyId, _timeProvider.GetUtcNow().UtcDateTime, out var encoded)
                ? Results.Bytes(encoded, "application/octet-stream")
                : Results.NotFound();
        }

        if (!HttpMethods.IsPost(context.Request.Method)) return null;

        // /boot/index is the first call of every app launch and carries no session: the ops telemetry matches it
        // to a running match by client address + device, to flag a silent drop as a probable crash + relaunch.
        if (path == "/boot/index") _ops.RecordBoot(OpsClientInfo.FromRequest(context));

        // An out-of-date client is stopped here, before auth and before any other handler: /boot/index is served
        // from a fixture, so a gate inside this method is what catches it. The admin and health routes are mapped
        // outside this catch-all, so they never reach here.
        var updateResult = TryHandleForcedUpdate(context, path);
        if (updateResult is not null) return updateResult;

        var maintenanceResult = TryHandleHardMaintenance(context, path);
        if (maintenanceResult is not null) return maintenanceResult;

        if (path == "/auth/index") return await HandleAuthAsync(context);

        var accessToken = context.Request.Headers["x-app-access-token"].ToString();
        if (string.IsNullOrEmpty(accessToken)) return null;

        // The access token decides whose state this is, and there is deliberately no fallback: the previous
        // behaviour, handing an unrecognised token the most recently issued session key, meant a stale or
        // forged token read and wrote another player's state. A token the store does not know is a rejection.
        if (!_sessions.TryGetValue(accessToken, out var cached))
        {
            var session = _playerStore.FindSession(accessToken);
            if (session is null)
            {
                _logger.LogWarning("Rejecting unknown access token {Token}", accessToken);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers["x-app-status-code"] = "1";
                return Results.Empty;
            }
            // Issued before this process started, which is why the player id comes from the session row rather
            // than from anything in memory: that is what makes a client's identity survive a redeploy.
            cached = new CachedSession(session.Key, GetOrCreateUserState(session.PlayerId));
            cached = _sessions.GetOrAdd(accessToken, cached);
        }

        var key = cached.Key;
        var state = cached.State;
        _onlineActivityByPlayer[state.PlayerId] = _timeProvider.GetUtcNow();
        // The x-app-* device headers of the player's latest request, for the telemetry of its matches.
        _ops.TouchClient(state.UserId, OpsClientInfo.FromRequest(context));

        if (path == "/training/index")
        {
            context.Response.Headers["x-app-status-code"] = "0";
            return BinaryJson("""{"trainingMissionTaskIdList":[]}""", key);
        }

        if (path == "/kicker/change")
        {
            return await HandleKickerChangeAsync(context, state, key);
        }

        if (path == "/disc/change")
        {
            return await HandleDiscChangeAsync(context, state, key);
        }

        if (path == "/disc/buildup")
        {
            return await HandleDiscBuildupAsync(context, state, key);
        }

        if (path == "/gear/create")
        {
            return await HandleGearCreateAsync(context, state, key);
        }

        if (path == "/gear/set")
        {
            return await HandleGearSetAsync(context, state, key);
        }

        if (path == "/gear/destroy")
        {
            return await HandleGearDestroyAsync(context, state, key);
        }

        // Round C: the gacha screens, the goods shop, and the real-money IAP endpoints the shop view also calls.
        if (path.StartsWith("/gacha/", StringComparison.Ordinal))
        {
            var gacha = await TryHandleGachaAsync(path, context, state, key);
            if (gacha is not null) return gacha;
        }

        if (path.StartsWith("/goodsShop/", StringComparison.Ordinal) || path.StartsWith("/shop/", StringComparison.Ordinal))
        {
            var shop = await TryHandleShopAsync(path, context, state, key);
            if (shop is not null) return shop;
        }

        // The name-entry window's submit. This is the only place a name is set on a new account, so it must
        // persist before answering: the client re-issues /startup/index the moment this returns, and that reply
        // has to carry both the name and tutorialProgressStatus = 207 or the home is left inconsistent.
        if (path == "/tutorial/end")
        {
            return await HandleTutorialEndAsync(context, state, key);
        }

        // Every other /tutorial/* step (capsuleOpen, discBuildup, gachaDraw, ...) belongs to the long tutorial
        // the player is never put on - see TutorialStatus - so they stay stubs.
        if (path.StartsWith("/tutorial/", StringComparison.Ordinal))
        {
            context.Response.Headers["x-app-status-code"] = "0";
            return BinaryJson("{}", key);
        }

        if (path == "/download/master")
        {
            context.Response.Headers["x-app-status-code"] = "0";
            context.Response.Headers["x-kickflight-fixture"] = "dynamic-demo-session";
            context.Response.Headers["x-app-master-hash"] = masterState.MasterVersion;
            var json = BuildDownloadMasterJson(context.Request, masterState);
            _logger.LogInformation("Serving dynamic master list ({Count} masters)", masterState.Masters.Count);
            return BinaryJson(json, key);
        }

        if (path == "/startup/index")
        {
            context.Response.Headers["x-app-status-code"] = "0";
            context.Response.Headers["x-kickflight-fixture"] = "dynamic-demo-session";
            var json = BuildStartupJson(state, context.Request);
            _logger.LogInformation("Serving dynamic startup index ({KickerCount} kickers, {DiscCount} discs)", _kickerList.Count, _discIdList.Count);
            return BinaryJson(json, key);
        }

        if (path == "/home/index")
        {
            context.Response.Headers["x-app-status-code"] = "0";
            context.Response.Headers["x-kickflight-fixture"] = "dynamic-demo-session";
            var json = BuildHomeJson(state);
            _logger.LogInformation("Serving dynamic home index (active kickerId={KickerId}, costumeId={CostumeId}, activeDeck={Deck})", state.KickerId, state.KickerCostumeId, state.ActiveDeckNumber);
            return BinaryJson(json, key);
        }

        if (path == "/battle/teamCreate")
        {
            return await HandleTeamCreateAsync(context, state, key);
        }

        if (path == "/battle/teamJoin")
        {
            return await HandleTeamJoinAsync(context, state, key);
        }

        if (path == "/battle/teamEntry")
        {
            return await HandleTeamEntryAsync(context, state, key);
        }

        if (path == "/battle/teamRecruiting")
        {
            return await HandleTeamRecruitingAsync(context, state, key);
        }

        if (path == "/battle/entry")
        {
            return await HandleBattleEntryAsync(context, state, key);
        }

        if (path == "/customBattle/prepare")
        {
            // The client copies this ResponseAsset list into ArchiveData.ApplicationAssetRevisionList and later
            // publishes it as a Photon lobby property. Returning [] erases its current asset revision; the next
            // create flow then treats the application assets as stale and opens the full title-data update/restart.
            // ResponseAsset.IsSameAssetRevision compares against ArchiveData.AppAssetRevision, which the client
            // sends as x-app-asset-revision. This app-asset revision is separate from the CDN title-minimum
            // revision (currently 27); echo the active client revision so the current installation is recognized.
            context.Response.Headers["x-app-status-code"] = "0";
            context.Response.Headers["x-kickflight-fixture"] = "dynamic-custom-battle-prepare";
            var assetList = BuildApplicationAssetList(context.Request);
            _logger.LogInformation("Prepared custom battle assets for {UserId}: platform={Platform} revision={Revision}",
                state.UserId, assetList[0].platform, assetList[0].revision);
            var response = new { assetList };
            return BinaryJson(JsonSerializer.Serialize(response), key);
        }

        if (path == "/customBattle/start")
        {
            return await HandleCustomBattleStartAsync(context, state, key);
        }

        if (path == "/customBattle/end")
        {
            return await HandleCustomBattleEndAsync(context, state, key);
        }

        if (path == "/customBattle/result")
        {
            // CustomBattleResultResponseData carries no fields in the retail client. Keep it explicit so the call
            // never depends on the broad route fallback and is easy to distinguish in captured API traces.
            context.Response.Headers["x-app-status-code"] = "0";
            context.Response.Headers["x-kickflight-fixture"] = "dynamic-custom-battle-result";
            return BinaryJson("{}", key);
        }

        if (path == "/battle/start" || path == "/customBattle/start")
        {
            return await HandleBattleStartAsync(context, state, key);
        }

        if (path == "/battle/end")
        {
            return await HandleBattleEndAsync(context, state, key);
        }

        if (path == "/battle/result" || path == "/customBattle/result")
        {
            return await HandleBattleResultAsync(context, state, key);
        }

        // Replays: /battle/upload receives the master client's gzip archive, /battleReplay/index lists the Kicker
        // and Featured channels, /battleReplay/play hands out a per-download key and blob URL.
        if (path == "/battle/upload")
        {
            return await HandleBattleUploadAsync(context, state, key);
        }

        if (path == "/battleReplay/index")
        {
            return HandleBattleReplayIndex(context, state, key);
        }

        if (path == "/battleReplay/play")
        {
            return await HandleBattleReplayPlayAsync(context, state, key);
        }

        if (path == "/battle/cancel")
        {
            return await HandleBattleCancelAsync(context, state, key);
        }

        if (path == "/battle/teamCancel" || path == "/matching/cancel")
        {
            context.Response.Headers["x-app-status-code"] = "0";
            return BinaryJson("{}", key);
        }

        if (path == "/battle/good")
        {
            context.Response.Headers["x-app-status-code"] = "0";
            return BinaryJson("{}", key);
        }

        // Real client calls that used to vanish into the catch-all below: still answered {} with status 0, but
        // recorded by the ops telemetry (DemoSessionApi.Ops.cs).
        if (path is "/battle/disconnect" or "/battle/timeout" or "/battle/leaving")
        {
            return await HandleBattleSignalAsync(path, context, state, key);
        }

        if (path.StartsWith("/battle/", StringComparison.Ordinal) || path.StartsWith("/customBattle/", StringComparison.Ordinal) || path.StartsWith("/matching/", StringComparison.Ordinal))
        {
            context.Response.Headers["x-app-status-code"] = "0";
            return BinaryJson("{}", key);
        }

        if (path == "/ping/index")
        {
            context.Response.Headers["x-app-status-code"] = "0";
            return BinaryJson("{}", key);
        }

        if (path == "/follow/index")
        {
            return await HandleFollowIndexAsync(context, state, key);
        }

        if (path == "/follow/online")
        {
            return await HandleFollowOnlineAsync(context, state, key);
        }

        if (path == "/user/online")
        {
            context.Response.Headers["x-app-status-code"] = "0";
            return BinaryJson("{}", key);
        }

        // Screens the client opens but this server has no data for (rankings, presents, missions, replays...).
        // They answer with a schema-complete neutral payload so the UI renders instead of showing "Network Error".
        var stub = await TryHandleStubAsync(path, context, state, key);
        if (stub is not null) return stub;

        return null;
    }

    private async Task<IResult?> HandleKickerChangeAsync(HttpContext context, SessionState state, byte[] key)
    {
        var body = await ReadBodyAsync(context.Request);
        try
        {
            var plaintext = D2CCodec.Decode(body, key);
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            if (root.TryGetProperty("kickerId", out var kickerProp))
            {
                state.KickerId = kickerProp.GetInt32();
            }
            if (root.TryGetProperty("kickerCostumeId", out var costumeProp))
            {
                state.KickerCostumeId = costumeProp.GetInt32();
            }
            NormalizeCostume(state);

            SaveUserState(state);
            _logger.LogInformation("Session updated: KickerId={KickerId}, CostumeId={CostumeId}", state.KickerId, state.KickerCostumeId);
            context.Response.Headers["x-app-status-code"] = "0";
            context.Response.Headers["x-kickflight-fixture"] = "dynamic-kicker-change";
            return BinaryJson("{}", key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode kicker change request: {Error}", ex.Message);
            return null;
        }
    }

    private async Task<IResult?> HandleDiscChangeAsync(HttpContext context, SessionState state, byte[] key)
    {
        var body = await ReadBodyAsync(context.Request);
        try
        {
            var plaintext = D2CCodec.Decode(body, key);
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            if (root.TryGetProperty("discDeckNumber", out var deckProp))
            {
                state.ActiveDeckNumber = deckProp.GetInt32();
            }
            if (root.TryGetProperty("userDiscDeckList", out var deckListProp) && deckListProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var deckEl in deckListProp.EnumerateArray())
                {
                    if (deckEl.TryGetProperty("number", out var numProp) &&
                        deckEl.TryGetProperty("discIdList", out var discListProp) &&
                        discListProp.ValueKind == JsonValueKind.Array)
                    {
                        var deckNumber = numProp.GetInt32();
                        var ids = discListProp.EnumerateArray().Select(x => x.GetInt32()).ToList();
                        state.Decks[deckNumber] = ids;
                    }
                }
            }

            SaveUserState(state);
            _logger.LogInformation("Disc deck updated: activeDeck={ActiveDeck}", state.ActiveDeckNumber);
            context.Response.Headers["x-app-status-code"] = "0";
            context.Response.Headers["x-kickflight-fixture"] = "dynamic-disc-change";
            return BinaryJson("{}", key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode disc change request: {Error}", ex.Message);
            return null;
        }
    }

    private async Task<IResult?> HandleDiscBuildupAsync(HttpContext context, SessionState state, byte[] key)
    {
        var body = await ReadBodyAsync(context.Request);
        try
        {
            var plaintext = D2CCodec.Decode(body, key);
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            var discId = root.GetProperty("discId").GetInt32();
            var targetLevel = root.TryGetProperty("level", out var lvlProp) ? lvlProp.GetInt32() : -1;

            if (!state.Discs.TryGetValue(discId, out var discState))
            {
                discState = new UserDiscState { DiscId = discId, Level = 10, Amount = 99 };
                state.Discs[discId] = discState;
            }

            if (targetLevel > 0)
            {
                discState.Level = targetLevel;
            }
            else
            {
                discState.Level = Math.Min(50, discState.Level + 1);
            }

            state.ItemDiscForce = Math.Max(0, state.ItemDiscForce - 1100);
            SaveUserState(state);

            var resp = new
            {
                userDisc = new
                {
                    discId = discState.DiscId,
                    amount = discState.Amount,
                    level = discState.Level,
                    exp = 0,
                    registerDatetime = "2026-09-01 00:00:00"
                },
                userItem = new
                {
                    itemId = 3,
                    amount = state.ItemDiscForce
                }
            };

            _logger.LogInformation("Disc buildup succeeded: discId={DiscId}, newLevel={Level}", discId, discState.Level);
            context.Response.Headers["x-app-status-code"] = "0";
            context.Response.Headers["x-kickflight-fixture"] = "dynamic-disc-buildup";
            return BinaryJson(JsonSerializer.Serialize(resp), key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode disc buildup request: {Error}", ex.Message);
            return null;
        }
    }

    // Slot array of a costume: always three entries, padded from whatever the save file holds (a file from before
    // gear support, or a short hand-edited array, must read as empty slots instead of throwing).
    private static int[] GearSlots(SessionState state, int kickerCostumeId)
    {
        var slots = new int[3];
        if (state.CostumeGears is not null && state.CostumeGears.TryGetValue(kickerCostumeId, out var stored) && stored is not null)
        {
            Array.Copy(stored, slots, Math.Min(stored.Length, slots.Length));
        }
        return slots;
    }

    // The one error shape this server uses: any non-zero x-app-status-code aborts the client flow and it shows the
    // generic error toast for that screen. The body stays schema-shaped so nothing is left half-deserialized.
    private static LocalFixtureResult StatusError(HttpContext context, byte[] key)
    {
        context.Response.Headers["x-app-status-code"] = "1";
        return BinaryJson("{}", key);
    }

    // gear/create: retail rolls a random gear for the requested kicker and hands the id back; the player then either
    // equips it (gear/set) or discards it (gear/destroy), both of which clear the pending roll.
    private async Task<IResult?> HandleGearCreateAsync(HttpContext context, SessionState state, byte[] key)
    {
        var body = await ReadBodyAsync(context.Request);
        var kickerId = state.KickerId;
        try
        {
            if (body.Length > 0)
            {
                var plaintext = D2CCodec.Decode(body, key);
                using var document = JsonDocument.Parse(plaintext);
                if (document.RootElement.TryGetProperty("kickerId", out var kickerProp))
                {
                    kickerId = kickerProp.GetInt32();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode gear create request: {Error}", ex.Message);
            return StatusError(context, key);
        }

        if (_gearIdList.Count == 0 || _kickerList.All(k => k.Id != kickerId))
        {
            _logger.LogWarning("Rejected gear/create for unknown kicker {KickerId} ({GearCount} gears in master)", kickerId, _gearIdList.Count);
            return StatusError(context, key);
        }

        var gearId = _gearIdList[Random.Shared.Next(_gearIdList.Count)];
        state.PendingGear = new PendingGearState { KickerId = kickerId, GearId = gearId };
        SaveUserState(state);

        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-gear-create";
        _logger.LogInformation("Rolled gear {GearId} for kicker {KickerId} (user {UserId})", gearId, kickerId, state.UserId);
        return BinaryJson($"{{\"gearId\":{gearId}}}", key);
    }

    private async Task<IResult?> HandleGearSetAsync(HttpContext context, SessionState state, byte[] key)
    {
        var body = await ReadBodyAsync(context.Request);
        try
        {
            var plaintext = D2CCodec.Decode(body, key);
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            var costumeId = root.TryGetProperty("kickerCostumeId", out var costumeProp) ? costumeProp.GetInt32() : 0;
            var slotNumber = root.TryGetProperty("gearIdNumber", out var slotProp) ? slotProp.GetInt32() : 0;

            var gearId = state.PendingGear?.GearId ?? 0;
            if (slotNumber is < 1 or > 3 || !_costumeKicker.ContainsKey(costumeId) || !_gearIdList.Contains(gearId))
            {
                _logger.LogWarning("Rejected gear/set: costume={CostumeId} slot={Slot} pendingGear={GearId}", costumeId, slotNumber, gearId);
                return StatusError(context, key);
            }

            if (!state.CostumeGears.TryGetValue(costumeId, out var slots))
            {
                slots = new int[3];
                state.CostumeGears[costumeId] = slots;
            }
            else if (slots.Length < 3)
            {
                Array.Resize(ref slots, 3);
                state.CostumeGears[costumeId] = slots;
            }
            slots[slotNumber - 1] = gearId;
            state.PendingGear = new PendingGearState();
            SaveUserState(state);

            context.Response.Headers["x-app-status-code"] = "0";
            context.Response.Headers["x-kickflight-fixture"] = "dynamic-gear-set";
            _logger.LogInformation("Equipped gear {GearId} in slot {Slot} of costume {CostumeId} (user {UserId})", gearId, slotNumber, costumeId, state.UserId);
            return BinaryJson("{}", key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode gear set request: {Error}", ex.Message);
            return StatusError(context, key);
        }
    }

    private Task<IResult?> HandleGearDestroyAsync(HttpContext context, SessionState state, byte[] key)
    {
        state.PendingGear = new PendingGearState();
        SaveUserState(state);

        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-gear-destroy";
        _logger.LogInformation("Discarded the pending gear of user {UserId}", state.UserId);
        return Task.FromResult<IResult?>(BinaryJson("{}", key));
    }

    private string BuildStartupJson(SessionState state, HttpRequest request)
    {
        NormalizeCostume(state);
        var pendingGear = state.PendingGear ?? new PendingGearState();
        var userKickerList = _kickerList.Select(k =>
        {
            var costumes = _costumesByKicker.GetValueOrDefault(k.Id, [1]);
            var selectedCostume = k.Id == state.KickerId ? state.KickerCostumeId : costumes[0];
            return new
            {
                kickerId = k.Id,
                kickerCostumeId = selectedCostume,
                userKickerCostumeList = costumes.Select(c =>
                {
                    var slots = GearSlots(state, c);
                    return new
                    {
                        kickerCostumeId = c,
                        gearId1 = slots[0],
                        gearId2 = slots[1],
                        gearId3 = slots[2]
                    };
                }).ToArray()
            };
        }).ToArray();

        var kickerTrainingList = _kickerList.Select(k => new
        {
            kickerId = k.Id,
            clearTrainingMissionTaskCount = 0
        }).ToArray();

        var userDiscList = _discIdList.Select(discId =>
        {
            var dState = state.Discs.GetValueOrDefault(discId) ?? new UserDiscState { DiscId = discId, Level = 10, Amount = 99 };
            return new
            {
                discId = dState.DiscId,
                amount = dState.Amount,
                level = dState.Level,
                exp = 0,
                registerDatetime = "2026-09-01 00:00:00"
            };
        }).ToArray();

        var data = new
        {
            userKickerList,
            userDiscList,
            userHonorList = new[] { new { honorId = 6010000, newFlag = false } },
            // All 8 Item master rows: 1-4 the legacy counters, 5-8 the round C stocks (gear stamps, disc fragments
            // and the two gacha tickets) that the gear screen and the shop read their counters from.
            userItemList = BuildUserItemList(state),
            // The client reads userGear.gearId != 0 as "there is a rolled gear waiting for equip/discard", and
            // userGear.kickerId as whose it is; with nothing pending it keeps pointing at the active kicker.
            userGear = new
            {
                kickerId = pendingGear.GearId != 0 ? pendingGear.KickerId : state.KickerId,
                gearId = pendingGear.GearId
            },
            userStampList = Array.Empty<object>(),
            userMissionProgressList = Array.Empty<object>(),
            userDailyRandomMissionTaskList = Array.Empty<object>(),
            kickerTrainingList,
            userPremium = new { premiumId = 0, startDatetime = "", endDatetime = "" },
            userSeasonPass = new { seasonPassId = 1, startDatetime = "2019-01-01 00:00:00", endDatetime = "2030-01-01 23:59:59" },
            userFestivalTeam = new { battleRuleId = 5, festivalTeamId = 1, festivalPoint = 0 },
            tutorialProgressStatus = TutorialStatus(state),
            birthdayConfirmFlag = true,
            dataUsageAgreementConfirmFlag = true,
            dataUsageAgreementAvailableFlag = false,
            userDataUsageAgreementList = Array.Empty<object>(),
            birthdayRegistrationFlag = true,
            purchaseAlertFlag = false,
            connectionSnsList = Array.Empty<object>(),
            connectionPlatformFlag = false,
            photonCloudRegionList = new[]
            {
                new { id = 1, code = "jp", appId = "local-demo-app" }
            },
            matchmakingFrontend = new { host = request.Host.Host, port = _grpcPort },
            battleTeamPhotonCloudRegionIdList = new[] { 1 },
            interruptNotificationList = Array.Empty<object>(),
            teamBattleInvitationCustomToken = "",
            teamBattleInvitationId = "",
            chatRoomConnectionCustomToken = "",
            chatRoomConnectionId = "",
            officialUrl = ""
        };

        return JsonSerializer.Serialize(data);
    }

    /// <summary>
    /// Forced client update: when a minimum version is configured and the client's x-app-application-version is
    /// older (or missing/unparsable), answer HTTP 200 with header x-app-status-code: 1 and a plaintext
    /// {"error":{"code":"1400",...}} body. The client has no version comparison of its own; that answer makes it
    /// show its forced-update window (button = hard-coded Play Store URL). It runs before auth, so it also catches a
    /// client that logged in on an older build. Null = handle normally.
    /// </summary>
    private IResult? TryHandleForcedUpdate(HttpContext context, string path)
    {
        // /auth/prepare carries no x-app-application-version; /auth/index right after it carries the header and enforces the gate.
        if (path == "/auth/prepare") return null;

        var isIos = IsIosClient(context.Request);
        var clientVersion = context.Request.Headers["x-app-application-version"].ToString();
        if (!_clientUpdate.IsOutdated(clientVersion, isIos)) return null;

        _logger.LogInformation("Client update: refused {Path} from {Platform} version {Version}", path,
            isIos ? "iOS" : "Android",
            string.IsNullOrWhiteSpace(clientVersion) ? "(missing)" : clientVersion);
        context.Response.Headers["x-app-status-code"] = "1";
        return Results.Text(_clientUpdate.BuildForcedUpdateJson(), "application/json", Encoding.UTF8,
            StatusCodes.Status200OK);
    }

    internal static bool IsIosClient(HttpRequest request) =>
        (int.TryParse(request.Headers["x-app-asset-platform"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var assetPlatform) && assetPlatform == 2) ||
        string.Equals(request.Headers["x-app-platform"].ToString(), "iOS", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Hard maintenance: every POST answers HTTP 503 with a plaintext {"error":{"code":"1000",...}} body, which the
    /// client shows as TitleMaintenanceWindow (our title/message on the Title scene, the Translation texts
    /// error.maintenanceTitle/Description elsewhere, then back to Title). That covers /auth/index and /battle/entry, so
    /// nobody logs in or queues. Let through: /boot/index, so the client still reaches the title screen; /battle/end
    /// and /battle/result, so matches already running finish and keep their results. /user/online (every 600 s, empty
    /// callbacks) gets 401 instead, which silently sends an idle player back to Title. A diagnostic build
    /// (x-app-application-version ending in Maintenance:BypassVersionSuffix) is served normally on every path and each
    /// bypass is logged once at Debug. Null = handle normally.
    /// </summary>
    private IResult? TryHandleHardMaintenance(HttpContext context, string path)
    {
        if (_maintenance.Current.Mode != MaintenanceMode.Hard) return null;
        // /auth/prepare carries no x-app-application-version (so a DIAG build could not be told apart there); /auth/index
        // right after it is still refused for non-DIAG clients, so nobody logs in.
        if (path is "/boot/index" or "/auth/prepare" or "/battle/end" or "/battle/result") return null;

        var clientVersion = context.Request.Headers["x-app-application-version"].ToString();
        if (_maintenance.IsBypassClient(clientVersion))
        {
            _logger.LogDebug("Maintenance: bypassed for DIAG client {Version} on {Path}", clientVersion, path);
            return null;
        }

        if (path == "/user/online")
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers["x-app-status-code"] = "1";
            return Results.Empty;
        }

        _logger.LogInformation("Maintenance: refused {Path} with 503", path);
        return Results.Text(_maintenance.BuildHardErrorJson(), "application/json", Encoding.UTF8,
            StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>True when this request identifies itself as a diagnostic build (see MaintenanceState.IsBypassClient).</summary>
    private bool IsDiagClient(HttpContext context) =>
        _maintenance.IsBypassClient(context.Request.Headers["x-app-application-version"].ToString());

    private string BuildHomeJson(SessionState state)
    {
        NormalizeCostume(state);

        var userPlayer = new
        {
            userId = state.UserId,
            displayUserId = PlayerDisplayIdCodec.ToPublic(int.TryParse(state.UserId, out var uid) ? uid : 1000001),
            name = CurrentUserName(state),
            exp = 38500,
            honorId = 6010000,
            userFrameList = new[]
            {
                new { battleRuleType = 1, frameId = 1 },
                new { battleRuleType = 2, frameId = 1 },
                new { battleRuleType = 3, frameId = 1 }
            },
            lastLoginDatetime = "2026-09-03 00:00:00",
            lastNameUpdateDatetime = "2026-09-03 00:00:00",
            kickerId = state.KickerId,
            kickerCostumeId = state.KickerCostumeId,
            discDeckNumber = state.ActiveDeckNumber,
            officialFlag = false
        };

        var userDiscDeckList = state.Decks.Select(d => new
        {
            number = d.Key,
            discIdList = d.Value.ToArray()
        }).ToArray();

        var data = new
        {
            userPlayer,
            userDiscDeckList,
            // No capsules are granted: tapping the old placeholder (5010001) softlocked the client, since
            // /capsule/open and /capsule/immediateOpen are stubs with no real drops.
            userCapsuleList = Array.Empty<object>(),
            // The shop's disc/kicker scrollers (Colorful.ShopDiscScroller._data : List<GachaGroupInfo>) are filled
            // from these two lists through GachaGroupListInfo(ResponseGachaGroup[]); serving them empty left the
            // scroller with a data list shorter than the cell range EnhancedScroller._Resize walks and GetCellHeight
            // threw ArgumentOutOfRangeException (List<T>.get_Item).
            discGachaGroupList = BuildHomeGachaGroupList(DiscGachaGroupId),
            kickerGachaGroupList = BuildHomeGachaGroupList(KickerGachaGroupId),
            userBattleRankList = BuildBattleRankList(state),
            userMissionProgressList = Array.Empty<object>(),
            // shopProductList is the real-money IAP list, which this server does not sell; the goods shop is the
            // JetCoin one and is served in full.
            shopProductList = Array.Empty<object>(),
            goodsShopProductList = BuildGoodsShopProducts(),
            // The exchange tab's own scroller (ShopExchangeScroller) stays empty: no disc fragments are converted.
            discFragmentConversionDiscIdList = Array.Empty<int>(),
            userMissionTaskStatusNotification = new { newFlagCount = 0, completionCount = 0 },
            presentNotificationCount = 0,
            appInformationNotificationFlag = false,
            newFollowerNotificationFlag = false,
            appMovieNotificationCount = 0,
            userBattleBreakaway = new { breakawayCount = 0 },
            mutualFollowBattleId = 0,
            mutualFollowBattleEndDatetime = "",
            mutualFollowBattleRewardCompleteCount = 0,
            userBattleReplayNotificationList = Array.Empty<object>(),
            seasonMatchBattleRankResetNotificationFlag = false,
            seasonMatchResultNotification = (object?)null,
            festivalMatchTeamSelectionNotificationFlag = false,
            festivalMatchResultNotification = (object?)null,
            appFestivalMatchResult = (object?)null,
            photonReconnectWaitTimeList = new[] { 0.5, 1.0, 2.0 },
            battleStartWaitTime = BattleMatchmakingService.BattleStartWaitSeconds,
            battlePlayablePingThreshold = 1000,
            battleRetryPingThreshold = 1000,
            battleRetryPingMaxCount = 0,
            battleTeamPhotonCloudRegionIdList = new[] { 1 },
            teamBattleCampaign = (object?)null,
            userTeamBattleCampaignList = Array.Empty<object>(),
            // One notice window per entry, shown each time Home is entered: the maintenance warning, when it is on.
            userNoticeList = _maintenance.BuildUserNoticeList()
        };

        return JsonSerializer.Serialize(data);
    }

    private string BuildDownloadMasterJson(HttpRequest request, MasterCatalogState state)
    {
        var configuredOrigin = _harnessOptions.MasterDownloadOriginsByHost.FirstOrDefault(pair =>
            string.Equals(pair.Key, request.Host.Host, StringComparison.OrdinalIgnoreCase)).Value;
        var origin = string.IsNullOrWhiteSpace(configuredOrigin)
            ? $"{request.Scheme}://{request.Host}"
            : configuredOrigin.TrimEnd('/');
        return JsonSerializer.Serialize(new
        {
            masterDownloadList = state.Masters.Select(kvp => new
            {
                name = kvp.Key,
                hash = Convert.ToHexString(SHA256.HashData(kvp.Value)).ToLowerInvariant(),
                url = $"{origin}/demo-master/{kvp.Key}",
                size = kvp.Value.Length
            }).ToArray()
        });
    }

    private async Task<IResult?> HandleAuthAsync(HttpContext context)
    {
        var requestBody = await ReadBodyAsync(context.Request);
        try
        {
            var plaintext = D2CCodec.Decode(requestBody, Encoding.ASCII.GetBytes(CommonCode));
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            var hash = root.GetProperty("hash").GetString();
            if (hash is null || Encoding.ASCII.GetByteCount(hash) != D2CCodec.KeySizeBytes) return null;

            var uuid = root.TryGetProperty("uuid", out var uuidProp) ? uuidProp.GetString() ?? "default-user" : "default-user";

            // The device uuid's player, created on first contact and the same one forever after. This is what
            // used to be an in-memory counter, which re-issued 1000002 to whichever device authenticated first
            // after a restart.
            var playerId = _playerStore.ResolvePlayerId(uuid);
            var state = GetOrCreateUserState(playerId);

            // No name is invented here. An empty name is what puts the client on the name-entry window; a
            // name is only ever set by the player, through POST /tutorial/end.

            var key = Encoding.ASCII.GetBytes(hash);
            var sessionToken = $"demo-token-{playerId}-{Guid.NewGuid():N}";
            _sessions[sessionToken] = new CachedSession(key, state);
            _sessions[AccessToken] = new CachedSession(key, state);
            _playerStore.SaveSession(sessionToken, playerId, key);
            _playerStore.SaveSession(AccessToken, playerId, key);

            context.Response.Headers["x-app-status-code"] = "0";
            context.Response.Headers["x-app-user-id"] = playerId.ToString();
            context.Response.Headers["x-app-access-token"] = sessionToken;
            context.Response.Headers["x-kickflight-fixture"] = "dynamic-demo-auth";
            context.Response.Headers["x-app-datetime"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            _logger.LogInformation("Created session for player {PlayerId} (name {UserName:l}) with token {Token}",
                playerId, state.HasName ? state.UserName : "<unset>", sessionToken);
            _ops.RecordAuth(state.UserId, OpsClientInfo.FromRequest(context));
            return BinaryJson("{}", key);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or ArgumentException)
        {
            _logger.LogWarning("Could not decode local demo authentication request: {Error}", exception.Message);
            return null;
        }
    }

    // The name-entry window's submit, and the only place a new account gets a name. It persists before
    // answering because the client re-issues /startup/index the moment this returns, and that reply has to carry
    // both the name and tutorialProgressStatus = 207 or the home is left inconsistent.
    //
    // TutorialEndRequestData carries the name in a field spelled "name"; "userName" is accepted too, for the same
    // reason /user/change accepts both.
    private async Task<IResult?> HandleTutorialEndAsync(HttpContext context, SessionState state, byte[] key)
    {
        var body = await ReadBodyAsync(context.Request);
        string name;
        try
        {
            var plaintext = D2CCodec.Decode(body, key);
            using var document = JsonDocument.Parse(plaintext);
            name = TryGetString(document.RootElement, "name", out var decoded) || TryGetString(document.RootElement, "userName", out decoded)
                ? decoded.Trim()
                : "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode tutorial end request: {Error}", ex.Message);
            return StatusError(context, key);
        }

        var rejection = NameRejection(state, name);
        if (rejection is not null)
        {
            _logger.LogInformation("Rejected the name submitted by player {PlayerId}: {Reason} ({Length} code units)",
                state.PlayerId, rejection, name.Length);
            return NameRejected(context, key, rejection);
        }

        state.UserName = name;
        SaveUserState(state);
        _logger.LogInformation("Player {PlayerId} chose the name {UserName:l}", state.PlayerId, name);
        return OkJson(context, key, "{}");
    }

    // The client caps typing at 10 UTF-16 code units and truncates with Substring(0, 10) on end-edit, so 10 is the
    // limit here too: the server must never reject a name the client let the player type. Surrogates and the
    // zero-width joiner would let a name render as something other than what it compares as, and a control
    // character would break the single line the name is drawn on.
    private const int NameMaxLength = 10;

    private string? NameRejection(SessionState state, string name)
    {
        if (name.Length == 0) return "empty";
        if (name.Length > NameMaxLength) return "too-long";
        if (name.Any(char.IsSurrogate)) return "surrogate";
        if (name.Contains('‍')) return "zero-width-joiner";
        if (name.Any(char.IsControl)) return "control-character";
        if (_playerStore.IsNameTaken(name, state.PlayerId)) return "taken";
        return null;
    }

    // A rejection is HTTP 200 carrying a non-zero x-app-status-code: the code, not the HTTP status, is what the
    // client reads as the failure signal. Only 2005 opens its name-rule popup - the canned text lives in the
    // prefab, so it explains the rules it knows and cannot be reworded from here - while 2002/2003/2004 fall
    // through to the generic error dialog. The body must be PLAINTEXT JSON like the forced-update and maintenance
    // answers: NetworkManager.GetResponseErrorFromResponse (0x31B2AE0) JsonUtility-parses downloadHandler.text
    // without D2C-decoding it, so an encrypted body parsed to an empty code and TutorialInputNameWindow's error
    // callback (0x146F858) took the generic path (network error, tutorial closed, player left unnamed).
    private static IResult NameRejected(HttpContext context, byte[] key, string reason)
    {
        context.Response.Headers["x-app-status-code"] = "2005";
        var body = JsonSerializer.Serialize(new
        {
            error = new
            {
                code = "2005",
                title = "Invalid name",
                message = reason switch
                {
                    "empty" => "Enter a name.",
                    "too-long" => $"Names cannot be longer than {NameMaxLength} characters.",
                    "taken" => "That name is already used by another player.",
                    _ => "That name cannot be used."
                }
            }
        });
        return Results.Text(body, "application/json", Encoding.UTF8, StatusCodes.Status200OK);
    }

    private async Task<IResult?> HandleTeamCreateAsync(HttpContext context, SessionState state, byte[] key)
    {
        using var request = await ReadTeamRequestAsync(context, state, key);
        if (request is null
            || !TryReadInt(request.RootElement, "battleRuleId", out var battleRuleId)
            || !TryReadString(request.RootElement, "code", out var code)
            || string.IsNullOrWhiteSpace(code))
        {
            return StatusError(context, key);
        }

        NormalizeCostume(state);
        var deck = state.Decks.GetValueOrDefault(state.ActiveDeckNumber) ?? [3010001, 3010002, 3010003, 3010004];
        var ruleMetadata = BattleRuleMetadata(battleRuleId);
        var entry = _matchmaking.CreateTeam(
            state.UserId, state.UserName, state.KickerId, state.KickerCostumeId, battleRuleId, deck, code,
            isDiag: IsDiagClient(context), matchType: ruleMetadata.MatchType,
            battleRuleType: ruleMetadata.BattleRuleType);
        // TeamCreate is called before Photon joins the recruiting room. Echo the app-asset revision the client
        // already has; an empty list is interpreted as missing application data and sends it to the update/restart
        // flow before it reaches Photon.
        var assetList = BuildApplicationAssetList(context.Request);
        var response = new
        {
            battleEntryId = entry.BattleEntryId,
            matchmakingTeamId = entry.MatchmakingTeamId,
            battleEntryTicketId = entry.TicketId,
            assetList,
            campaignList = Array.Empty<object>()
        };

        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-battle-team-create";
        _ops.RecordEntry(state.UserId, entry.BattleEntryId, entry.TicketId, battleRuleId, ruleMetadata.MatchType,
            state.KickerId, state.KickerCostumeId, IsDiagClient(context), OpsClientInfo.FromRequest(context));
        _logger.LogInformation("Created team {TeamId} for {UserId}, code={Code}, rule={RuleId}",
            entry.MatchmakingTeamId, state.UserId, code, battleRuleId);
        return BinaryJson(JsonSerializer.Serialize(response), key);
    }

    private async Task<IResult?> HandleTeamJoinAsync(HttpContext context, SessionState state, byte[] key)
    {
        using var request = await ReadTeamRequestAsync(context, state, key);
        if (request is null
            || !TryReadString(request.RootElement, "matchmakingTeamId", out var teamId)
            || string.IsNullOrWhiteSpace(teamId))
        {
            return StatusError(context, key);
        }

        NormalizeCostume(state);
        var deck = state.Decks.GetValueOrDefault(state.ActiveDeckNumber) ?? [3010001, 3010002, 3010003, 3010004];
        var entry = _matchmaking.JoinTeam(
            teamId, state.UserId, state.UserName, state.KickerId, state.KickerCostumeId, deck,
            isDiag: IsDiagClient(context));
        if (entry is null) return StatusError(context, key);

        var response = new
        {
            battleEntryId = entry.BattleEntryId,
            matchmakingTeamId = entry.MatchmakingTeamId,
            battleEntryTicketId = entry.TicketId,
            campaignList = Array.Empty<object>()
        };

        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-battle-team-join";
        var joinedRule = _matchmaking.GetBattleResultEntry(entry.BattleEntryId);
        _ops.RecordEntry(state.UserId, entry.BattleEntryId, entry.TicketId, joinedRule?.BattleRuleId ?? 0, joinedRule?.MatchType,
            state.KickerId, state.KickerCostumeId, IsDiagClient(context), OpsClientInfo.FromRequest(context));
        _logger.LogInformation("User {UserId} joined team {TeamId}", state.UserId, teamId);
        return BinaryJson(JsonSerializer.Serialize(response), key);
    }

    private async Task<IResult?> HandleTeamEntryAsync(HttpContext context, SessionState state, byte[] key)
    {
        using var request = await ReadTeamRequestAsync(context, state, key);
        if (request is null
            || !TryReadString(request.RootElement, "matchmakingTeamId", out var teamId)
            || string.IsNullOrWhiteSpace(teamId)
            || !TryReadStringList(request.RootElement, "userIdList", out var userIds)
            || !_matchmaking.StartTeam(teamId, state.UserId, userIds))
        {
            return StatusError(context, key);
        }

        var response = new { matchmakingCancelWaitingTimeSecond = 5, scf = false };
        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-battle-team-entry";
        _logger.LogInformation("Started team entry {TeamId} for {UserId} with {MemberCount} selected member(s)",
            teamId, state.UserId, userIds.Count);
        return BinaryJson(JsonSerializer.Serialize(response), key);
    }

    private async Task<IResult?> HandleTeamRecruitingAsync(HttpContext context, SessionState state, byte[] key)
    {
        using var request = await ReadTeamRequestAsync(context, state, key);
        if (request is null
            || !TryReadStringList(request.RootElement, "matchmakingTeamIdList", out var teamIds))
            return StatusError(context, key);

        var teams = _matchmaking.GetTeams(teamIds);
        var response = new
        {
            matchmakingTeamList = teams.Select(team => new
            {
                matchmakingTeamId = team.MatchmakingTeamId,
                battleRuleId = team.BattleRuleId,
                code = team.Code,
                kickerCostumeIdList = team.KickerCostumeIdList
            }).ToArray()
        };
        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-battle-team-recruiting";
        return BinaryJson(JsonSerializer.Serialize(response), key);
    }

    private async Task<JsonDocument?> ReadTeamRequestAsync(HttpContext context, SessionState state, byte[] key)
    {
        var body = await ReadBodyAsync(context.Request);
        try
        {
            var plaintext = D2CCodec.Decode(body, key);
            var document = JsonDocument.Parse(plaintext);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                return null;
            }
            return document;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning("Could not decode team request for {UserId}: {Error}", state.UserId, ex.Message);
            return null;
        }
    }

    private static bool TryReadString(JsonElement root, string propertyName, out string value)
    {
        value = "";
        if (!root.TryGetProperty(propertyName, out var property)) return false;
        if (property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? "";
            return true;
        }
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            value = number.ToString(CultureInfo.InvariantCulture);
            return true;
        }
        return false;
    }

    private static bool TryReadInt(JsonElement root, string propertyName, out int value)
    {
        value = 0;
        if (!root.TryGetProperty(propertyName, out var property)) return false;
        if (property.ValueKind == JsonValueKind.Number) return property.TryGetInt32(out value);
        return property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadStringList(JsonElement root, string propertyName, out List<string> values)
    {
        values = [];
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
                continue;
            }
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number))
            {
                values.Add(number.ToString(CultureInfo.InvariantCulture));
                continue;
            }
            if (item.ValueKind == JsonValueKind.Object
                && TryReadString(item, "userId", out var userId)
                && !string.IsNullOrWhiteSpace(userId))
            {
                values.Add(userId);
                continue;
            }
            return false;
        }

        values = values.Distinct(StringComparer.Ordinal).ToList();
        return true;
    }

    private static ApplicationAssetRevision[] BuildApplicationAssetList(HttpRequest request)
    {
        var platform = int.TryParse(request.Headers["x-app-asset-platform"], NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var requestedPlatform)
            ? requestedPlatform
            : 3;
        var revision = int.TryParse(request.Headers["x-app-asset-revision"], NumberStyles.Integer,
                           CultureInfo.InvariantCulture, out var requestedRevision)
                       && requestedRevision >= 0
            ? requestedRevision
            : 0;
        return [new ApplicationAssetRevision(platform, revision)];
    }

    private async Task<IResult?> HandleBattleEntryAsync(HttpContext context, SessionState state, byte[] key)
    {
        var body = await ReadBodyAsync(context.Request);
        int battleRuleId = 1;
        try
        {
            if (body.Length > 16)
            {
                var plaintext = D2CCodec.Decode(body, key);
                using var document = JsonDocument.Parse(plaintext);
                if (document.RootElement.TryGetProperty("battleRuleId", out var ruleProp))
                {
                    battleRuleId = ruleProp.GetInt32();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to parse battle entry body: {Error}, using rule 1", ex.Message);
        }

        // A ranked request is only accepted while its hour block is active. The check happens before a ticket is
        // issued: once an entry/room exists its stored rule is authoritative (battle start and result both read it),
        // so crossing the boundary during matchmaking cannot strand a battle on the wrong mode. Non-ranked rules
        // (and an undecodable body, which falls back to rule 1) are unaffected.
        var activeRankedRuleId = RankedModeRotation.RuleIdAt(_timeProvider.GetUtcNow());
        if (RankedModeRotation.IsRankedRule(battleRuleId) && battleRuleId != activeRankedRuleId)
        {
            _logger.LogInformation("Rejected stale ranked entry for {UserId}: requested rule={RuleId}, active rule={ActiveRuleId}",
                state.UserId, battleRuleId, activeRankedRuleId);
            return StatusError(context, key);
        }

        var deck = state.Decks.GetValueOrDefault(state.ActiveDeckNumber) ?? [3010001, 3010002, 3010003, 3010004];
        NormalizeCostume(state);
        var ruleMetadata = BattleRuleMetadata(battleRuleId);
        var (battleEntryId, ticketId) = _matchmaking.RegisterEntry(
            state.UserId, state.UserName, state.KickerId, state.KickerCostumeId, battleRuleId, deck,
            isDiag: IsDiagClient(context), matchType: ruleMetadata.MatchType,
            battleRuleType: ruleMetadata.BattleRuleType);

        var resp = new
        {
            // NormalMatchingController.SendCancel returns early while BattleEntryId is empty, so without this id the
            // matching screen's back button does nothing.
            battleEntryId,
            battleEntryTicketId = ticketId,
            matchmakingCancelWaitingTimeSecond = 5,
            campaignList = Array.Empty<object>(),
            scf = false
        };

        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-battle-entry";
        _ops.RecordEntry(state.UserId, battleEntryId, ticketId, battleRuleId, ruleMetadata.MatchType,
            state.KickerId, state.KickerCostumeId, IsDiagClient(context), OpsClientInfo.FromRequest(context));
        _logger.LogInformation("Handled /battle/entry for {UserId}: ticket={TicketId}", state.UserId, ticketId);
        return BinaryJson(JsonSerializer.Serialize(resp), key);
    }

    private async Task<IResult?> HandleBattleCancelAsync(HttpContext context, SessionState state, byte[] key)
    {
        // BattleCancelRequest { battleEntryId }. Answer {} with status 0 whatever happens: on success the client
        // changes scene back, and an error here would strand it on the matching screen. CancelEntry leaves a room
        // that already closed its window alone, so a late cancel cannot break a battle that is starting.
        var body = await ReadBodyAsync(context.Request);
        var battleEntryId = "";
        try
        {
            using var document = JsonDocument.Parse(D2CCodec.Decode(body, key));
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                TryReadString(document.RootElement, "battleEntryId", out battleEntryId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode /battle/cancel body for {UserId}: {Error}", state.UserId, ex.Message);
        }

        var cancelled = _matchmaking.CancelEntry(battleEntryId);
        _ops.RecordCancel(state.UserId, battleEntryId, cancelled, OpsClientInfo.FromRequest(context));
        if (!cancelled)
        {
            _logger.LogInformation("Ignored /battle/cancel for {UserId}: entry '{BattleEntryId}' is unknown or already started",
                state.UserId, battleEntryId);
        }

        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-battle-cancel";
        return BinaryJson("{}", key);
    }

    private async Task<IResult?> HandleBattleStartAsync(HttpContext context, SessionState state, byte[] key)
    {
        var body = await ReadBodyAsync(context.Request);
        var battleRuleId = 1;
        var battleRuleType = 1;
        var battleId = "";
        try
        {
            if (body.Length > 16)
            {
                var plaintext = D2CCodec.Decode(body, key);
                using var document = JsonDocument.Parse(plaintext);
                if (document.RootElement.TryGetProperty("battleRuleId", out var ruleProp))
                {
                    battleRuleId = ruleProp.GetInt32();
                }
                if (document.RootElement.TryGetProperty("battleId", out var battleProp) && battleProp.ValueKind == JsonValueKind.String)
                {
                    battleId = battleProp.GetString() ?? "";
                }
            }
            if (!_battleRuleTypeById.TryGetValue(battleRuleId, out battleRuleType)) battleRuleType = 1;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to parse battle start body: {Error}, using rule 1", ex.Message);
            battleRuleId = 1;
            battleRuleType = 1;
        }

        // If a room exists, its selected rule is authoritative. The player may cross a schedule boundary while the
        // matchmaking window is open; the already-created room must keep the original mode.
        if (_matchmaking.GetRoomBattleRuleId(battleId) is { } roomRuleId)
        {
            battleRuleId = roomRuleId;
            _battleRuleTypeById.TryGetValue(battleRuleId, out battleRuleType);
        }

        // GuardianParameterMaster is looked up by the id returned here (CallbackBattleStartSuccess), so a rule
        // whose guardian is the revivable ball-goal variant (battleRuleType 3) gets the weaker row 2.
        // Gym: row 3 = same HP, attack 0, so the guardian's eye laser cannot hurt anyone.
        var guardianId = BattleMatchmakingService.GymEnabled ? 3 : battleRuleType == 3 ? 2 : 1;
        // The arena: the room's draw (same for every human in it) or a fresh draw when the room is unknown.
        var fieldId = _matchmaking.GetRoomFieldId(battleId) ?? BattleMatchmakingService.PickRandomField();
        var resp = new
        {
            fieldId,
            guardianParameter = new
            {
                id = guardianId,
                rank = 1
            },
            lotteryFestivalPointId = 0
        };

        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-battle-start";
        _ops.RecordBattleStart(state.UserId, battleId, battleRuleId, BattleRuleMetadata(battleRuleId).MatchType, fieldId,
            _battleTimeSecondsById.GetValueOrDefault(battleRuleId), OpsClientInfo.FromRequest(context));
        _logger.LogInformation("Handled /battle/start for {UserId}: rule={RuleId} type={RuleType} guardianParameter={GuardianId} field={FieldId}",
            state.UserId, battleRuleId, battleRuleType, guardianId, fieldId);
        return BinaryJson(JsonSerializer.Serialize(resp), key);
    }

    private async Task<IResult?> HandleCustomBattleStartAsync(HttpContext context, SessionState state, byte[] key)
    {
        var body = await ReadBodyAsync(context.Request);
        int battleRuleType;
        int fieldId;
        int guardianId;
        try
        {
            var plaintext = D2CCodec.Decode(body, key);
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            battleRuleType = root.GetProperty("battleRuleType").GetInt32();
            fieldId = root.GetProperty("fieldId").GetInt32();

            // CustomBattleRuleField is the client's source of legal rule/map pairs. Enforce that same pair at
            // start so the saved map and the selected mode cannot drift after the lobby has been created.
            using var customFields = JsonDocument.Parse(LoadJson(
                _contentRoot, "config/masters_custom_battle_rule_field.json", "[]"));
            var fieldIsAllowed = customFields.RootElement.EnumerateArray().Any(row =>
                row.TryGetProperty("battleRuleType", out var type) && type.GetInt32() == battleRuleType
                && row.TryGetProperty("fieldId", out var field) && field.GetInt32() == fieldId);
            if (!fieldIsAllowed)
            {
                _logger.LogWarning("Rejected custom battle start for {UserId}: unsupported rule/map pair {RuleType}/{FieldId}",
                    state.UserId, battleRuleType, fieldId);
                return StatusError(context, key);
            }

            guardianId = battleRuleType == 3 ? 2 : 1;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidOperationException
                                   or KeyNotFoundException or FormatException)
        {
            _logger.LogWarning("Could not decode custom battle start for {UserId}: {Error}", state.UserId, ex.Message);
            return StatusError(context, key);
        }

        // This is the custom battle's recording/correlation ID used by its end/result requests. Photon already
        // created/joined the room from the lobby RoomName before this response; it is not the Photon GameId or
        // the four-digit invite code (neither is present in CustomBattleStartRequestData).
        var customBattleId = $"custom-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Interlocked.Increment(ref _customBattleSequence)}";
        var response = new
        {
            customBattleId,
            guardianParameter = new { id = guardianId, rank = 1 }
        };

        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-custom-battle-start";
        _ops.RecordCustomBattleStart(state.UserId, customBattleId, battleRuleType, fieldId, OpsClientInfo.FromRequest(context));
        _logger.LogInformation(
            "Started custom battle {CustomBattleId} for {UserId}: ruleType={RuleType} field={FieldId}",
            customBattleId, state.UserId, battleRuleType, fieldId);
        return BinaryJson(JsonSerializer.Serialize(response), key);
    }

    // Colorful.Networking.BattleResultResponseData: every list must be present (empty is fine); the client
    // constructs BattleResultInfo from it unconditionally and NREs on a missing array, which leaves the
    // ResultScene stuck on the score board.
    //
    // Rank progress applies only to ranked matches. The entry stores a server-derived snapshot of matchType and
    // battleRuleType; neither a result body nor a later master/schedule change can reclassify the match.
    private async Task<IResult?> HandleBattleResultAsync(HttpContext context, SessionState state, byte[] key)
    {
        var battleEntryId = "";
        try
        {
            var body = await ReadBodyAsync(context.Request);
            if (body.Length > 16)
            {
                using var document = JsonDocument.Parse(D2CCodec.Decode(body, key));
                if (document.RootElement.TryGetProperty("battleEntryId", out var entryProp)
                    && entryProp.ValueKind == JsonValueKind.String)
                    battleEntryId = entryProp.GetString() ?? "";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to parse battle result body: {Error}", ex.Message);
        }

        // Only an issued entry owned by this session can be ranked. Missing/forged IDs (including custom battle
        // results, which have no normal entry) fail closed. matchType 2 is the ranked mode in BattleRule master.
        var entry = string.IsNullOrEmpty(battleEntryId) ? null : _matchmaking.GetBattleResultEntry(battleEntryId);
        var ownedEntry = entry is { } candidate && candidate.UserId == state.UserId ? candidate : null;
        var battleRuleType = ownedEntry?.BattleRuleType ?? RegularBattleRuleType;
        var applyProgression = ownedEntry is { MatchType: 2, BattleRuleType: >= 1 and <= 3 };
        if (!string.IsNullOrEmpty(battleEntryId) && entry is null)
        {
            _logger.LogWarning("Battle result for {UserId}: unknown entry {BattleEntryId}, rank left unchanged",
                state.UserId, battleEntryId);
        }
        else if (entry is not null && ownedEntry is null)
        {
            _logger.LogWarning("Battle result for {UserId}: entry {BattleEntryId} belongs to another user, rank left unchanged",
                state.UserId, battleEntryId);
        }
        else if (ownedEntry is not null && ownedEntry.MatchType is null)
        {
            _logger.LogWarning("Battle result for {UserId}: entry {BattleEntryId} has no known match type, rank left unchanged",
                state.UserId, battleEntryId);
        }

        var processedRankedResult = applyProgression
            ? _matchmaking.ProcessRankedResultOnce(battleEntryId, state.UserId, ruleType =>
            {
                var beforeRank = _playerStore.LoadRank(state.PlayerId, ruleType);
                var afterRank = RankProgression.Apply(beforeRank, won: true);
                _playerStore.SaveRank(state.PlayerId, ruleType, afterRank);
                return new BattleMatchmakingService.RankedResultSnapshot(ruleType, beforeRank, afterRank);
            })
            : null;
        var rankSnapshot = processedRankedResult?.RankedResult;
        var before = rankSnapshot?.Before ?? _playerStore.LoadRank(state.PlayerId, battleRuleType);
        var after = rankSnapshot?.After ?? before;

        var rank = new { battleRuleType, battlePoint = after.BattlePoint, rank = after.Rank };
        var resp = new
        {
            userExp = 38500 + 800, // the user's total exp (see the static exp served at startup) plus this battle's reward
            userBattleRank = rank,
            beforeUserBattleRank = new { battleRuleType, battlePoint = before.BattlePoint, rank = before.Rank },
            playerLevelUpRewardList = Array.Empty<object>(),
            receivedUserCapsuleList = Array.Empty<object>(),
            receivedUserItemList = Array.Empty<object>(),
            receivedUserPresentList = Array.Empty<object>(),
            userMissionProgressList = Array.Empty<object>(),
            userDailyRandomMissionTaskList = Array.Empty<object>(),
            receivedRewardList = Array.Empty<object>(),
            badConnectionReliefFlag = false,
            battleCount = 1,
            battleWinCount = 0,
            matchmakingTeamId = "team-1",
            campaignList = Array.Empty<object>(),
            userFestivalTeam = new { battleRuleId = 1, festivalTeamId = 0, festivalPoint = 0 },
            festivalPoint = 0,
            lotteryFestivalPointId = 0
        };

        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-battle-result";
        _ops.RecordBattleResult(state.UserId, battleEntryId, OpsClientInfo.FromRequest(context));
        _logger.LogInformation("Handled /battle/result for {UserId}: entry={BattleEntryId} rule={RuleId} type={RuleType} matchType={MatchType} ranked={Applied}",
            state.UserId, battleEntryId, entry?.BattleRuleId, battleRuleType, entry?.MatchType, applyProgression);
        return BinaryJson(JsonSerializer.Serialize(resp), key);
    }

    private async Task<IResult?> HandleBattleEndAsync(HttpContext context, SessionState state, byte[] key)
    {
        state.ItemKickPoints += 200;
        state.ItemJetCoins += 1000;
        SaveUserState(state);

        // Snapshot the match while the matchmaking room is still in memory: /battle/upload arrives seconds later
        // with the replay and its own battle/result JSON, but that JSON has no kickerCostumeId field.
        var endedBattleId = await RecordMatchEndAsync(context, state, key, custom: false);
        _ops.RecordBattleEnd(state.UserId, endedBattleId, OpsClientInfo.FromRequest(context));

        var resp = new
        {
            // The master client uploads the replay because of this flag (GameManager.CallbackBattleEndSuccess ->
            // ArchiveData.set_IsUploadReplayData). Everything else is unchanged.
            replayUploadFlag = true,
            // The same delta /battle/result persisted, so the reward screen and the stored rank agree.
            battlePoint = RankProgression.WinBattlePoint,
            exp = 800,
            kickPoint = 200,
            jetCoin = 1000
        };

        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-battle-end";
        _logger.LogInformation("Handled /battle/end for {UserId}", state.UserId);
        return BinaryJson(JsonSerializer.Serialize(resp), key);
    }

    // The custom-battle end DTO has no replayUploadFlag; the production client cave (scripts/re/replay_upload_cave.py)
    // sets ArchiveData.IsUploadReplayData itself, so this only records the match and answers the empty DTO.
    private async Task<IResult?> HandleCustomBattleEndAsync(HttpContext context, SessionState state, byte[] key)
    {
        var endedBattleId = await RecordMatchEndAsync(context, state, key, custom: true);
        _ops.RecordBattleEnd(state.UserId, endedBattleId, OpsClientInfo.FromRequest(context));
        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-custom-battle-end";
        return BinaryJson("{}", key);
    }

    /// <returns>The battle (or custom battle) id of the end request, "" when it has none.</returns>
    private async Task<string> RecordMatchEndAsync(HttpContext context, SessionState state, byte[] key, bool custom)
    {
        var battleId = "";
        try
        {
            var body = await ReadBodyAsync(context.Request);
            if (body.Length <= D2CCodec.VectorSizeBytes) return battleId;
            using var document = JsonDocument.Parse(D2CCodec.Decode(body, key));
            var root = document.RootElement;
            battleId = custom
                ? (TryGetString(root, "customBattleId", out var customId) ? customId : "")
                : (TryGetString(root, "battleId", out var normalId) ? normalId : "");
            if (string.IsNullOrEmpty(battleId)) return battleId;

            IReadOnlyList<BattleMatchmakingService.MatchingPlayerBattleInfo> roster =
                custom ? [] : _matchmaking.GetRoomRoster(battleId);
            var players = BuildPendingPlayers(root, roster);
            var teams = BuildPendingTeams(root, players);
            _replays.RecordBattleEnd(new BattleReplayService.PendingMatch
            {
                BattleId = battleId,
                Custom = custom,
                BattleRuleId = custom ? 0 : _matchmaking.GetRoomBattleRuleId(battleId) ?? 0,
                FieldId = custom ? 0 : _matchmaking.GetRoomFieldId(battleId) ?? 0,
                HumanCount = CountHumans(root, roster, players),
                Teams = teams,
                Players = players
            }, _timeProvider.GetUtcNow().UtcDateTime);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException)
        {
            _logger.LogWarning("Could not record match end for {UserId}: {Error}", state.UserId, ex.Message);
        }
        return battleId;
    }

    private static List<BattleReplayService.PendingPlayer> BuildPendingPlayers(JsonElement root,
        IReadOnlyList<BattleMatchmakingService.MatchingPlayerBattleInfo> roster)
    {
        var rosterByUser = roster.ToDictionary(p => p.userId, StringComparer.Ordinal);
        var players = new List<BattleReplayService.PendingPlayer>();
        if (root.TryGetProperty("userScoreList", out var scores) && scores.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in scores.EnumerateArray())
            {
                var userId = TryGetString(element, "userId", out var parsed) ? parsed : "";
                rosterByUser.TryGetValue(userId, out var match);
                players.Add(new BattleReplayService.PendingPlayer
                {
                    UserId = userId,
                    Name = match?.name ?? "",
                    Rank = match?.rank ?? 0,
                    KickerId = match?.kickerId ?? 0,
                    KickerCostumeId = match?.kickerCostumeId ?? 0,
                    FrameId = match?.frameId ?? 0,
                    LanguageCode = match?.languageCode ?? "",
                    TeamType = match?.teamType ?? 0,
                    Ai = TryGetBool(element, "aiFlag", out var ai) ? ai : (match?.kickerAiParameterId ?? 0) > 0,
                    Mvp = TryGetBool(element, "mvpFlag", out var mvp) && mvp
                });
            }
        }

        // A roster entry missing from userScoreList (rare) still belongs to the match.
        foreach (var match in roster)
        {
            if (players.Any(p => p.UserId == match.userId)) continue;
            players.Add(new BattleReplayService.PendingPlayer
            {
                UserId = match.userId,
                Name = match.name,
                Rank = match.rank,
                KickerId = match.kickerId,
                KickerCostumeId = match.kickerCostumeId,
                FrameId = match.frameId,
                LanguageCode = match.languageCode,
                TeamType = match.teamType,
                Ai = match.kickerAiParameterId > 0
            });
        }
        return players;
    }

    private static List<BattleReplayService.PendingTeam> BuildPendingTeams(JsonElement root,
        List<BattleReplayService.PendingPlayer> players)
    {
        var teams = new Dictionary<int, BattleReplayService.PendingTeam>();
        if (root.TryGetProperty("teamScoreList", out var teamScores) && teamScores.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in teamScores.EnumerateArray())
            {
                var teamType = element.TryGetProperty("teamType", out var typeProp) && typeProp.ValueKind == JsonValueKind.Number
                    ? typeProp.GetInt32() : 0;
                teams[teamType] = new BattleReplayService.PendingTeam
                {
                    TeamType = teamType,
                    Score = ReadInt(element, "score"),
                    RemainingDistance = ReadInt(element, "remainingDistance"),
                    LastRemainingDistance = ReadInt(element, "lastRemainingDistance")
                };
            }
        }
        foreach (var player in players)
        {
            if (!teams.ContainsKey(player.TeamType))
                teams[player.TeamType] = new BattleReplayService.PendingTeam { TeamType = player.TeamType };
        }
        return teams.Values.OrderBy(team => team.TeamType).ToList();
    }

    private static int CountHumans(JsonElement root,
        IReadOnlyList<BattleMatchmakingService.MatchingPlayerBattleInfo> roster,
        List<BattleReplayService.PendingPlayer> players)
    {
        if (root.TryGetProperty("userScoreList", out var scores) && scores.ValueKind == JsonValueKind.Array)
        {
            var any = false;
            var humans = 0;
            foreach (var element in scores.EnumerateArray())
            {
                any = true;
                if (TryGetBool(element, "aiFlag", out var ai))
                {
                    if (!ai) humans++;
                    continue;
                }
                var userId = TryGetString(element, "userId", out var parsed) ? parsed : "";
                if (!userId.StartsWith("bot-", StringComparison.OrdinalIgnoreCase)) humans++;
            }
            if (any) return humans;
        }
        if (roster.Count > 0) return roster.Count(p => p.kickerAiParameterId == 0);
        return players.Count(p => !p.Ai && !p.UserId.StartsWith("bot-", StringComparison.OrdinalIgnoreCase));
    }

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var value)
            ? value : 0;

    private static bool TryGetBool(JsonElement element, string name, out bool value)
    {
        value = false;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var prop)) return false;
        if (prop.ValueKind == JsonValueKind.True) { value = true; return true; }
        if (prop.ValueKind == JsonValueKind.False) { value = false; return true; }
        return false;
    }

    // /battle/upload: base64( gzip( archive ) ) inside the D2C body, exactly what ReplayUtility.GetReplayWorkZipData
    // hands to Convert.ToBase64String (see docs/REPLAYS.md). The response DTO is empty.
    private async Task<IResult?> HandleBattleUploadAsync(HttpContext context, SessionState state, byte[] key)
    {
        var body = await ReadBodyAsync(context.Request);
        try
        {
            var plaintext = D2CCodec.Decode(body, key);
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            var battleId = TryGetString(root, "battleId", out var parsedId) ? parsedId : "";
            var uploadFile = TryGetString(root, "uploadFile", out var parsedFile) ? parsedFile : "";
            if (string.IsNullOrEmpty(uploadFile)) return StatusError(context, key);

            // Reject on the base64 length before allocating the decoded buffer (base64 is 4/3 of the raw size).
            if (uploadFile.Length > _replays.MaxUploadBytes / 3 * 4 + 8)
            {
                _logger.LogWarning("Rejected /battle/upload for {UserId}: base64 upload is over the {Cap}-byte cap",
                    state.UserId, _replays.MaxUploadBytes);
                return StatusError(context, key);
            }

            byte[] gzip;
            try
            {
                gzip = Convert.FromBase64String(uploadFile);
            }
            catch (FormatException)
            {
                _logger.LogWarning("Rejected /battle/upload for {UserId}: uploadFile is not base64", state.UserId);
                return StatusError(context, key);
            }

            var stored = _replays.StoreUpload(battleId, gzip, _timeProvider.GetUtcNow().UtcDateTime);
            context.Response.Headers["x-app-status-code"] = "0";
            context.Response.Headers["x-kickflight-fixture"] = "dynamic-battle-upload";
            _logger.LogInformation("Handled /battle/upload for {UserId}: battle={BattleId} bytes={Bytes} stored={Stored}",
                state.UserId, battleId, gzip.Length, stored);
            return BinaryJson("{}", key);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException or InvalidDataException or IOException)
        {
            _logger.LogWarning("Could not handle /battle/upload for {UserId}: {Error}", state.UserId, ex.Message);
            return StatusError(context, key);
        }
    }

    // /battleReplay/index: Featured is channel id 1 (the served type-3 master row); each kicker is 1000+kickerId
    // (the type-2 rows). The client matches entries by battleReplayChannelId against BattleReplayChannelMaster.
    private IResult HandleBattleReplayIndex(HttpContext context, SessionState state, byte[] key)
    {
        var kickerIds = _kickerList.Select(k => k.Id).OrderBy(id => id).ToArray();
        var json = _replays.BuildIndexJson(kickerIds, DefaultCostumeRowId, _timeProvider.GetUtcNow().UtcDateTime);
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-battle-replay-index";
        return OkJson(context, key, json);
    }

    private int DefaultCostumeRowId(int kickerId) =>
        _costumesByKicker.TryGetValue(kickerId, out var rows) && rows.Count > 0 ? rows[0] : 1;

    private async Task<IResult?> HandleBattleReplayPlayAsync(HttpContext context, SessionState state, byte[] key)
    {
        var replayId = "";
        try
        {
            var body = await ReadBodyAsync(context.Request);
            if (body.Length > D2CCodec.VectorSizeBytes)
            {
                using var document = JsonDocument.Parse(D2CCodec.Decode(body, key));
                if (TryGetString(document.RootElement, "battleReplayId", out var parsed)) replayId = parsed;
            }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException)
        {
            _logger.LogWarning("Could not decode /battleReplay/play request: {Error}", ex.Message);
            return StatusError(context, key);
        }

        var (keyId, encryptionKey) = _replays.CreatePlayKey(replayId, _timeProvider.GetUtcNow().UtcDateTime);
        var url = string.IsNullOrEmpty(keyId)
            ? ""
            : $"{context.Request.Scheme}://{context.Request.Host}/battleReplay/blob/{keyId}";
        _logger.LogInformation("Handled /battleReplay/play for {UserId}: replay={ReplayId} found={Found}",
            state.UserId, replayId, !string.IsNullOrEmpty(keyId));
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-battle-replay-play";
        return OkJson(context, key, JsonSerializer.Serialize(new { battleReplayUrl = url, encryptionKey }));
    }

    private static LocalFixtureResult BinaryJson(string json, byte[] key) =>
        new(D2CCodec.Encode(Encoding.UTF8.GetBytes(json), key, RandomNumberGenerator.GetBytes(D2CCodec.VectorSizeBytes)),
            "application/octet-stream", StatusCodes.Status200OK);

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request)
    {
        request.EnableBuffering();
        request.Body.Position = 0;
        using var stream = new MemoryStream();
        await request.Body.CopyToAsync(stream, request.HttpContext.RequestAborted);
        request.Body.Position = 0;
        return stream.ToArray();
    }

    private sealed record KickerInfo(int Id, string Name);
}
