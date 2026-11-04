using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using KickFlight.BootstrapApi.Ops;
using KickFlight.BootstrapApi.Otomo;
using KickFlight.BootstrapApi.PlayerStore;
using KickFlight.BootstrapApi.Push;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace KickFlight.BootstrapApi;

public sealed partial class DemoSessionApi : IDisposable
{
    private sealed record ApplicationAssetRevision(int platform, int revision);
    private sealed record MasterCatalogState(FrozenDictionary<string, byte[]> Masters, string MasterVersion);

    private const string CommonCode = "1a837b9ee2ae11a07a0f529a4cd4b61c";
    // The client keeps its downloaded masters until this header changes, so it must follow the content:
    // a SHA-256 over every served table (see PublishMasterState). Editing any config/masters_*.json and
    // restarting the server therefore makes the client re-download on its next launch - and so does a published
    // otomo release or a bot AI profile reload, without a restart, because both republish the catalogue in place.
    //
    // The catalogue is one immutable snapshot swapped under the lock: a publish can replace a table at any
    // moment, so a reader must see one release's version, never half of two.
    public string MasterVersion => Volatile.Read(ref _masterState).MasterVersion;
    private MasterCatalogState _masterState = new(FrozenDictionary<string, byte[]>.Empty, "demo-master-initial");
    private readonly object _masterLock = new();
    private string? _baseAiParamRows;
    private readonly IBotAiProfileProvider _botAiProfiles;
    private const string AccessToken = "demo-access-token-0000000000000000000000000000";

    // Rank is session's (DemoSessionApi.Rank.cs): points, tiers, outcomes and the ladder are read from otomo, and the adapter only
    // parses the tier key (Otomo/RankTranslation.cs).

    // Onboarding status: TutorialUtil.IsTutorial is literally "tutorialProgressStatus != 207".
    //
    // With the ladder on (below), a fresh account (no name, no progress) is sent Start (1), which runs the client's
    // own tutorial from the beginning: /tutorial/start, the scripted battle (battleProgress 101-112, battleEnd 113), then capsuleOpen
    // (202), discBuildup (203), gachaDiscDraw (204), gachaKickerDraw (205), kickerChange (206) and the name (end,
    // 207). Each step the client reports advances SessionState.TutorialProgress, so a player who quits half way
    // resumes where they stopped (GetTutorialInitialSubStep maps the status to the step). The disc and kicker steps
    // pay their rewards through economy (DemoSessionApi.Tutorial.cs).
    //
    // A name always ends it at 207, and an account that has reached 206 without one is on the name window
    // (GetTutorialInitialSubStep maps 206 to sub-step 16, InputName), which is the guarantee the short onboarding
    // gave and still holds. 206 is TutorialProgressStatus.KickerChange: the name step preselects the first owned
    // kicker other than kicker 1 and throws if there is none, which is why EconomyPayloads always serves kicker 2.
    //
    // The ladder is off unless Tutorial:Ladder = true. Off, a nameless account goes straight to 206, the short
    // onboarding that is known to work on devices; the long tutorial has not been walked to the end on one.
    //
    // On, it is still only offered to an account that can finish it. The build-up lesson (203) greys its button out
    // unless the player's Disc Force (Item row 3, from the startup) covers the lesson disc's EnhanceCost
    // (DiscDisplayPresenter.<Initialize>b__5_0 0x162C268: force >= cost), and the tutorial's pointer has nothing
    // else to press, so a player short of it is stuck after the capsule with no way forward. The lesson disc is the
    // client's own (TutorialUtil.DISC_ID_LIST[0], 3010003) at its default level, priced by the served DiscBuildup
    // master (_tutorialLessonCost). An account below that, which has not passed the lesson yet, is put on the name
    // window instead.
    //
    // Verified by decompiling the client; no client modification is involved.
    public const int TutorialStatusStart = 1;
    public const int TutorialStatusDiscBuildup = 203;
    public const int TutorialStatusInputName = 206;
    public const int TutorialStatusComplete = 207;

    // TutorialUtil.DISC_ID_LIST (global-metadata 0x8323D5): the four placeholder discs the client builds the
    // tutorial's deck and disc list from. The first is the one the build-up lesson raises.
    public static readonly int[] TutorialDeckDiscIds = [3010003, 3010024, 3010070, 3010013];

    private int TutorialStatus(SessionState state, EconomySnapshot economy)
    {
        if (state.HasName) return TutorialStatusComplete;
        if (!_tutorialLadder) return TutorialStatusInputName;
        if (state.TutorialProgress < TutorialStatusDiscBuildup && DiscForceOf(economy) < _tutorialLessonCost)
        {
            _logger.LogInformation("Tutorial: user {UserId} holds {Force} disc_force, below the build-up lesson's " +
                "{Cost}; serving the name window instead of the ladder", state.UserId, DiscForceOf(economy),
                _tutorialLessonCost);
            return TutorialStatusInputName;
        }
        // Without a name the furthest the ladder goes is the name window itself.
        return Math.Clamp(state.TutorialProgress, TutorialStatusStart, TutorialStatusInputName);
    }

    private static long DiscForceOf(EconomySnapshot economy) =>
        economy.Balances.FirstOrDefault(balance => balance.Currency == EconomyCatalog.DiscForceCurrency)?.Balance ?? 0;

    // The three rule types have separate ranked standings. Casual results continue using the existing regular ladder.
    public const int RegularBattleRuleType = 1;
    private static readonly int[] RankedBattleRuleTypes = [1, 2, 3];

    private readonly string _contentRoot;
    private readonly int _grpcPort;
    private readonly MasterOverrides _mastersOverrides;
    // The state of a player this process has already loaded, keyed by the game row id. It is only a cache: the row
    // itself is the store's, and an evicted entry is reloaded from there on the next request.
    private readonly ConcurrentDictionary<long, SessionState> _statesByPlayer = new();
    private readonly IOtomoIdentity _identity;
    // The only source of value: this process holds no balance, no ticket and no disc (see DemoSessionApi.Economy.cs).
    private readonly IOtomoEconomy _economy;
    // The only source of the social relation and the ranking board (see DemoSessionApi.SocialRanking.cs).
    private readonly IOtomoSocial _social;
    private readonly OtomoSocialOptions _socialOptions;
    // The only source of battle points (see DemoSessionApi.Rank.cs).
    private readonly IOtomoRank _rank;
    // The only source of a player's loadout - decks, gears, the active deck (session's Loadout) and the pending gear roll
    // (ext) - and of another player's card and loadout (see DemoSessionApi.Loadout.cs).
    private readonly IOtomoProfile _profile;
    private readonly KfAccessTokenCodec _accessTokens;
    private readonly IPlayerStore _playerStore;
    private readonly OtomoMasters _otomoMasters;
    // The served tables as loaded, which is what PublishMasterState hashes and ApplyOtomoTables replaces entries
    // in. Mutated only under _masterLock; readers take the immutable snapshot in _masterState instead.
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
    // Capsule odds and timers, disc start levels and the casual level (Otomo/EconomyTuning.cs). Read once at boot.
    private readonly EconomyTuning _tuning;
    private readonly bool _tutorialLadder;
    // discId -> RarityType, from the served Disc master: what a disc's start level is read from.
    private readonly Dictionary<int, int> _discRarity = [];
    // The served DiscBuildup master, read the way the client prices its build-up button (Otomo/EconomyDiscLevels.cs).
    private EconomyDiscLevels _discLevels = EconomyDiscLevels.Empty;
    // The Disc Force the tutorial's build-up lesson asks for (TutorialStatus). int.MaxValue when the masters do
    // not price it, which keeps every account off the ladder rather than guessing.
    private long _tutorialLessonCost = int.MaxValue;

    private readonly ChatService _chat;

    public DemoSessionApi(ILogger<DemoSessionApi> logger, IWebHostEnvironment environment,
        BattleMatchmakingService matchmaking, BattleReplayService replays, IPlayerStore playerStore,
        IOtomoIdentity identity, IOtomoEconomy economy, IOtomoSocial social, IOtomoRank rank, IOtomoProfile profile, KfAccessTokenCodec accessTokens,
        IConfiguration configuration, MaintenanceState maintenance, ClientUpdateState clientUpdate,
        TimeProvider timeProvider, OpsTelemetryService ops, IOptions<HarnessOptions> harnessOptions,
        IOptions<OtomoSocialOptions> socialOptions, OtomoMasters otomoMasters,
        ChatService chat, IBotAiProfileProvider? botAiProfiles = null,
        PushTokenService? pushTokens = null, RankedSearchNotifier? rankedNotifier = null)
    {
        _chat = chat;
        _pushTokens = pushTokens;
        _rankedNotifier = rankedNotifier;
        if (pushTokens is not null)
        {
            _pushThrottle = new PushRegisterThrottle(timeProvider);
            // The in-memory state of a signed-in player, so a token write is not overwritten by that player's
            // next whole-document save.
            pushTokens.LiveStates = id => _statesByPlayer.TryGetValue(id, out var live) ? [live] : [];
        }
        _logger = logger;
        _ops = ops;
        _matchmaking = matchmaking;
        _replays = replays;
        _maintenance = maintenance;
        _clientUpdate = clientUpdate;
        _timeProvider = timeProvider;
        _playerStore = playerStore;
        _identity = identity;
        _economy = economy;
        _social = social;
        _rank = rank;
        _profile = profile;
        _socialOptions = socialOptions.Value;
        _accessTokens = accessTokens;
        _otomoMasters = otomoMasters;
        _harnessOptions = harnessOptions.Value;
        _botAiProfiles = botAiProfiles ?? ExperimentalAiRoleService.CurrentProvider;
        _contentRoot = environment.ContentRootPath;
        _grpcPort = configuration.GetValue("GrpcPort", 18081);
        _tuning = EconomyTuning.Load(configuration[$"{OtomoEconomyOptions.SectionName}:TuningFile"],
            environment.ContentRootPath, logger);
        BattleMatchmakingService.UnchosenDeckDiscLevel = _tuning.CasualDiscLevel;
        _tutorialLadder = configuration.GetValue("Tutorial:Ladder", false);
        _mastersOverrides = new MasterOverrides(configuration[MasterOverrides.ConfigurationKey], environment.ContentRootPath);
        InitializeMasters(environment.ContentRootPath);
        // A publish replaces a table under the running process: re-encrypt it and recompute the version the client
        // compares, so the next /download/master advertises the new content and /demo-master serves it.
        _otomoMasters.Updated += ApplyOtomoTables;
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
        var kickerJson = MasterJson("Kicker", contentRoot, "config/masters_kicker.json", """[{"id":1,"name":"Tsubame","shortName":"Tsubame","nameSpelling":"Tsubame","voiceActorName":"CV: Yuma Uchida"}]""");
        var costumeJson = MasterJson("KickerCostume", contentRoot, "config/masters_kicker_costume.json", """[{"id":1,"kickerId":1,"costumeId":1,"costumeName":"Tsubame - Standard Color","sortOrder":1,"battleResultPositionSortOrder":1,"battleResultModelScale":1.0,"exclusiveFlag":false,"releaseDatetime":"2019-01-01 00:00:00"}]""");
        var detailJson = MasterJson("KickerDetail", contentRoot, "config/masters_kicker_detail.json", """[{"id":1,"kickerId":1,"kickerIntroductionText":"Fast as the wind","kickerSkillName":"Sonic Rush","kickerSkillShortText":"Quick attack","kickerSkillLongText":"Dashes forward","specialSkillName":"Burst Glide","specialSkillShortText":"Massive tornado","specialSkillLongText":"Creates a powerful tornado","kickerAbilityName":"Accel Charge","kickerAbilityShortText":"Speed","kickerAbilityLongText":"Faster with crystals","kickerDiscDistinctionText":"Aerial combat","kickerGraphHpRate":0.8,"kickerGraphAttackRate":0.9,"kickerGraphSpeedRate":1.0,"age":18,"birthday":"1/1","height":"165cm","profileText":"Fast as the wind"}]""");
        var parameterJson = MasterJson("KickerParameter", contentRoot, "config/masters_kicker_parameter.json", "[]");
        var abilityJson = MasterJson("KickerAbility", contentRoot, "config/masters_kicker_ability.json", "[]");
        var abilityConditionJson = MasterJson("KickerAbilityCondition", contentRoot, "config/masters_kicker_ability_condition.json", "[]");
        var translationJson = MasterJson("Translation", contentRoot, "config/masters_translation.json", "[]");

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
        var badgeCatalogJson = LoadJson(contentRoot, BadgeCatalog.CatalogPath, "");
        _encryptedMasters["Honor"] = EncryptMaster(BadgeCatalog.BuildHonorMasterJson(badgeCatalogJson));
        BadgeCatalog.LogCoverage(_logger, badgeCatalogJson);

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
        var guardianParameterJson = MasterJson("GuardianParameter", contentRoot, "config/masters_guardian_parameter.json", """[{"id":1,"matchType":1,"rank":1,"hp":8000,"attack":6000}]""");
        _encryptedMasters["GuardianParameter"] = EncryptMaster(guardianParameterJson);
        // guardianAmount is per team: GameManager.CreateGuardian spawns it for both teams and indexes
        // FieldManager.GetGuardianInitialPosition, and FLD00101_1 (the crystal-rule variant) has exactly two
        // guardian points; the ball variants (rules 3/4) also spawn one guard per goal, which must be destroyed
        // before the team can score. The file is the source; the literal is only the fallback.
        var battleRuleJson = MasterJson("BattleRule", contentRoot, "config/masters_battle_rule.json", BattleRuleFallbackJson);
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
        var customBattleRuleFieldJson = MasterJson("CustomBattleRuleField", contentRoot, "config/masters_custom_battle_rule_field.json", """
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
        var customBattleKickerAiJson = MasterJson("CustomBattleKickerAi", contentRoot, "config/masters_custom_battle_kicker_ai.json", """
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
        var regularMatchScheduleJson = MasterJson("RegularMatchBattleSchedule", contentRoot, "config/masters_regular_match_battle_schedule.json", """
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
        // Its totalBattlePoint column is the published ladder's (min_points + 200); the letter names are presentation
        // kept in DemoSessionApi.Rank.cs. With no ladder the master is not served, and nothing stands in for it.
        ApplyBattleRankMaster();
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
        // One row per tuned capsule type, so the timer the client renders is the one /capsule/open enforces, plus
        // 5010000: the capsule TutorialUtil.CAPSULE_ID hard-codes into slot 1 for the tutorial's capsule step
        // (OPEN_CAPSULE_DURATION 8 seconds), which the client looks up in this master like any other.
        _encryptedMasters["Capsule"] = EncryptMaster(BuildCapsuleMasterJson());
        // The mission screen resolves every missionId and missionTaskId it is sent through these five tables, so
        // they are served from the same table the progress rows are built from (Otomo/EconomyMissions.cs).
        foreach (var (name, json) in EconomyMissions.BuildMasters()) _encryptedMasters[name] = EncryptMaster(json);

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
        // The Item master is deliberately NOT served. Ours carries only id/goodsType/name/maxAmount, and the
        // client draws a goods icon by looking a sprite up by the row it holds: serving our invented names
        // ("JetCoin", "KickerGachaTicket") shadows the baked table that has the real sprite keys, and every
        // item icon renders as a white quad. The counts the client shows come from userItemList, not from here.
        _ = itemJson;

        var discJson = MasterJson("Disc", contentRoot, "config/masters_disc.json", "[]");
        var skillJson = MasterJson("Skill", contentRoot, "config/masters_skill.json", "[]");
        var discGrowJson = MasterJson("DiscGrow", contentRoot, "config/masters_disc_grow.json", "[]");
        var discBuildupJson = MasterJson("DiscBuildup", contentRoot, "config/masters_disc_buildup.json", "[]");
        var initialDiscDeckJson = MasterJson("InitialDiscDeck", contentRoot, "config/masters_initial_disc_deck.json", "[]");
        var gearJson = MasterJson("Gear", contentRoot, "config/masters_gear.json", "[]");
        var gearSkillJson = MasterJson("GearSkill", contentRoot, "config/masters_gear_skill.json", "[]");
        var gearSameColorBonusJson = MasterJson("GearSameColorBonus", contentRoot, "config/masters_gear_same_color_bonus.json", "[]");

        try
        {
            using var dDoc = JsonDocument.Parse(discJson);
            foreach (var el in dDoc.RootElement.EnumerateArray())
            {
                var id = el.GetProperty("id").GetInt32();
                _discIdList.Add(id);
                if (el.TryGetProperty("rarityType", out var rarity) && rarity.TryGetInt32(out var rarityType))
                    _discRarity[id] = rarityType;
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

        // Parsed from the same bytes the client is sent below, so the price /disc/buildup charges is the one the
        // client's button shows.
        _discLevels = EconomyDiscLevels.Parse(discBuildupJson, _logger);
        _logger.LogInformation("DiscBuildup master: {Rows} level-up rows", _discLevels.Count);

        // The lesson raises the tutorial's first disc from the level the client builds it at.
        var lessonDisc = TutorialDeckDiscIds[0];
        var lessonStep = _discRarity.TryGetValue(lessonDisc, out var lessonRarity)
            ? _discLevels.Next(lessonRarity, EconomyDiscLevels.ClientDefaultLevel(lessonRarity))
            : null;
        _tutorialLessonCost = lessonStep?.DiscForce ?? int.MaxValue;
        _logger.LogInformation("Tutorial build-up lesson: disc {DiscId} to level {Level} costs {Cost} disc_force",
            lessonDisc, lessonStep?.TargetLevel, lessonStep is null ? "(unpriced)" : lessonStep.DiscForce);

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

        var aiParamJson = MasterJson("KickerAiParameter", contentRoot, "config/masters_kicker_ai_parameter.json", "[]");
        var aiDiscJson = MasterJson("KickerAiDisc", contentRoot, "config/masters_kicker_ai_disc.json", "[]");
        var aiDeckJson = MasterJson("KickerAiDiscDeck", contentRoot, "config/masters_kicker_ai_disc_deck.json", "[]");
        var pingThresholdJson = MasterJson("MatchmakingPingThreshold", contentRoot, "config/masters_matchmaking_ping_threshold.json", "[]");

        _encryptedMasters["KickerAiParameter"] = EncryptMaster(BuildKickerAiParameterRows(aiParamJson));
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
            // Chat tabs (ChatView.Initialize iterates MasterManager.ChatRoomMaster); experimental, see docs/re-notes/chat.md.
            ("ChatRoom", "masters_chat_room.json"),
        })
        {
            var json = MasterJson(masterName, contentRoot, "config/" + fileName, "[]");
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
        _encryptedMasters["BattleRuleRapidBallScore"] = EncryptMaster(MasterJson("BattleRuleRapidBallScore", contentRoot, "config/masters_battle_rule_rapid_ball_score.json", "[]"));
        _encryptedMasters["BattleRuleFlagFlightScore"] = EncryptMaster(MasterJson("BattleRuleFlagFlightScore", contentRoot, "config/masters_battle_rule_flag_flight_score.json", BattleRuleFlagFlightScoreFallbackJson));

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
        var battleReplayChannelJson = MasterJson("BattleReplayChannel", contentRoot, "config/masters_battle_replay_channel.json",
            BuildBattleReplayChannelFallbackJson());
        var movieCategoryJson = MasterJson("MovieCategory", contentRoot, "config/masters_movie_category.json", "[]");
        _encryptedMasters["BattleReplayChannel"] = EncryptMaster(battleReplayChannelJson);
        _encryptedMasters["MovieCategory"] = EncryptMaster(movieCategoryJson);

        PublishMasterState();
        _logger.LogInformation("Master version {MasterVersion} ({Count} tables, {Otomo} from otomo)",
            MasterVersion, _encryptedMasters.Count,
            _otomoMasters.Enabled ? _otomoMasters.Source : "none");
    }

    // The KickerAiParameter master as served: the table, the gym rows and, with the experimental roles on, the rows the
    // current bot AI profiles define. Remembers the table with the gym rows so a profiles reload can redo the last step.
    private string BuildKickerAiParameterRows(string tableJson)
    {
        var rows = AppendGymAiRows(tableJson);
        _baseAiParamRows = rows;
        return ExperimentalAiRoleService.IsEnabled
            ? ExperimentalAiRoleService.AppendExperimentalRoleAiRows(rows, _botAiProfiles.CurrentSnapshot)
            : rows;
    }

    // One master table's JSON, from wherever it now lives.
    //
    // A table named in Otomo:Masters:Tables comes from the release this server follows and from nowhere else: if
    // the release did not carry it, Program.cs has already refused to start (OtomoMasters.LoadInitialAsync), so
    // reaching here without it is a bug rather than a reason to quietly read the file the port replaced. Every
    // other table is still the file, which is what makes moving one to otomo a single entry in that setting.
    private string MasterJson(string masterName, string contentRoot, string relativePath, string fallback)
    {
        if (!_otomoMasters.Owns(masterName)) return LoadJson(contentRoot, relativePath, fallback);
        if (_otomoMasters.TryGetTable(masterName, out var json)) return json;
        throw new InvalidOperationException(
            $"master {masterName} is configured to come from otomo ({_otomoMasters.Source}) but the release carried no table for it");
    }

    // A published release that changes a table this server serves. Runs on the refresher's thread, so it takes the
    // master lock: the entries and the version the client compares are replaced together, and no request sees a
    // catalogue half from the old release and half from the new one.
    private void ApplyOtomoTables()
    {
        lock (_masterLock)
        {
            var applied = new List<string>();
            foreach (var masterName in _encryptedMasters.Keys.ToList())
            {
                if (!_otomoMasters.Owns(masterName) || !_otomoMasters.TryGetTable(masterName, out var json)) continue;
                // KickerAiParameter is the table the bot AI profiles (kf.bot_ai_profiles, the same release) are added to.
                _encryptedMasters[masterName] = EncryptMaster(masterName == "KickerAiParameter"
                    ? BuildKickerAiParameterRows(json)
                    : ApplyObscuredOffsets(masterName, json));
                applied.Add(masterName);
            }
            // The ladder is a document of the same release, not a table, so a publish that moves a tier moves the
            // BattleRank master with it.
            ApplyBattleRankMaster();
            PublishMasterState();
            _logger.LogInformation(
                "Master version {MasterVersion}: {Count} table(s) reloaded from {Source} ({Tables})",
                MasterVersion, applied.Count, _otomoMasters.Source, string.Join(", ", applied.OrderBy(n => n, StringComparer.Ordinal)));
        }
    }

    // The client's cache key: a SHA-256 over every served table, name and bytes, published as one immutable
    // catalogue. Called with the master lock held.
    private void PublishMasterState()
    {
        using var sha = SHA256.Create();
        foreach (var kvp in _encryptedMasters.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var nameBytes = Encoding.UTF8.GetBytes(kvp.Key);
            sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
            sha.TransformBlock(kvp.Value, 0, kvp.Value.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var masterVersion = "demo-master-" + Convert.ToHexString(sha.Hash!)[..16].ToLowerInvariant();
        Volatile.Write(ref _masterState,
            new MasterCatalogState(_encryptedMasters.ToFrozenDictionary(StringComparer.Ordinal), masterVersion));
        _logger.LogInformation("Master version {MasterVersion} ({Count} tables)", masterVersion, _masterState.Masters.Count);
    }

    public void OnBotAiProfilesReloaded(BotAiProfileSnapshot snapshot)
    {
        if (!ExperimentalAiRoleService.IsEnabled) return;

        lock (_masterLock)
        {
            if (string.IsNullOrEmpty(_baseAiParamRows)) return;

            var updatedAiRows = ExperimentalAiRoleService.AppendExperimentalRoleAiRows(_baseAiParamRows, snapshot);

            // Through _encryptedMasters, the source of every published state: a release that also replaces a table
            // publishes again from it, and must not publish the profiles that were current before this one.
            _encryptedMasters["KickerAiParameter"] = EncryptMaster(updatedAiRows);
            PublishMasterState();
            _logger.LogInformation("Master version {MasterVersion} updated atomically after bot AI profile reload (version {ProfileVersion})",
                MasterVersion, snapshot.Version);
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

    // Loads a player, creating them empty if the id is unknown. (The repair of a deck naming a disc the masters no longer
    // carry moved with the decks: DemoSessionApi.Loadout.cs.)
    private SessionState GetOrCreateUserState(long playerId)
    {
        var loaded = _playerStore.TryLoad(playerId);
        if (loaded is not null)
        {
            return loaded;
        }

        var state = NewUserState(playerId);
        SaveUserState(state);
        return state;
    }

    // A new account's save holds no value at all: what it owns is whatever economy says, and a fresh otomo account
    // owns nothing until a grant has run.
    private static SessionState NewUserState(long playerId) => new() { UserId = playerId.ToString() };

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

        // The replay blob /battleReplay/play handed out (DemoSessionApi.Replay.cs). UnityWebRequest.Get sends no session
        // headers, so this is authenticated by the sealed key id in the URL only.
        if (HttpMethods.IsGet(context.Request.Method) && path.StartsWith("/battleReplay/blob/", StringComparison.Ordinal))
        {
            return await HandleBattleReplayBlobAsync(context, path["/battleReplay/blob/".Length..]);
        }

        if (!HttpMethods.IsPost(context.Request.Method)) return null;

        // /boot/index is the first call of every app launch and carries no session: the ops telemetry matches it
        // to a running match by client address + device, to flag a silent drop as a probable crash + relaunch.
        if (path == "/boot/index") _ops.RecordBoot(OpsClientInfo.FromRequest(context));

        // Unauthenticated and exempt from the update/maintenance gates: the client's FCM hook has no session.
        if (path == "/push/register") return await HandlePushRegisterAsync(context);

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

        // Fail closed when there is no identity provider: the stack still boots without otomo (masters, fixtures,
        // health, the CDN), but no player request is served under an identity this server cannot vouch for.
        if (!_identity.Enabled) return IdentityUnavailable(context, path);

        // The token decides whose state this is, and it is verified rather than looked up: it is signed by this
        // server, and its payload names the otomo account the device signed in as. There is deliberately no
        // fallback - a token this server did not sign is a rejection, never a licence to read another player's
        // state, which is what "hand an unknown token the most recent key" used to do.
        if (!_accessTokens.TryRead(accessToken, out var ticket))
        {
            _logger.LogWarning("Rejecting an access token this server did not sign ({Path})", path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers["x-app-status-code"] = "1";
            return Results.Empty;
        }

        var key = ticket.Key;
        var state = _statesByPlayer.GetOrAdd(ticket.GamePlayerId, GetOrCreateUserState);
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
            return await HandleDiscChangeAsync(context, ticket, state, key);
        }

        if (path == "/disc/buildup")
        {
            return await HandleDiscBuildupAsync(context, ticket, state, key);
        }

        if (path == "/gear/create")
        {
            return await HandleGearCreateAsync(context, ticket, state, key);
        }

        if (path == "/gear/set")
        {
            return await HandleGearSetAsync(context, ticket, state, key);
        }

        if (path == "/gear/destroy")
        {
            return await HandleGearDestroyAsync(context, ticket, state, key);
        }

        // Round C: the gacha screens, the goods shop, and the real-money IAP endpoints the shop view also calls.
        if (path.StartsWith("/gacha/", StringComparison.Ordinal))
        {
            var gacha = await TryHandleGachaAsync(path, context, ticket, state, key);
            if (gacha is not null) return gacha;
        }

        if (path.StartsWith("/goodsShop/", StringComparison.Ordinal) || path.StartsWith("/shop/", StringComparison.Ordinal))
        {
            var shop = await TryHandleShopAsync(path, context, ticket, state, key);
            if (shop is not null) return shop;
        }

        // The name-entry window's submit. This is the only place a name is set on a new account, so it must
        // persist before answering: the client re-issues /startup/index the moment this returns, and that reply
        // has to carry both the name and tutorialProgressStatus = 207 or the home is left inconsistent.
        if (path == "/tutorial/end")
        {
            return await HandleTutorialEndAsync(context, ticket, state, key);
        }

        // The rest of the tutorial ladder (DemoSessionApi.Tutorial.cs): every step advances the stored progress,
        // and the disc and kicker steps pay their economy rewards. A step this server does not know answers {}.
        if (path.StartsWith("/tutorial/", StringComparison.Ordinal))
        {
            return await HandleTutorialStepAsync(path, context, ticket, state, key);
        }

        // Missions are economy's tracks (DemoSessionApi.Missions.cs) and capsules are economy's pools opened with
        // tokens a battle paid (DemoSessionApi.Capsules.cs). Both run before the neutral stubs, which still answer
        // /mission/change.
        if (path.StartsWith("/mission/", StringComparison.Ordinal))
        {
            var mission = await TryHandleMissionAsync(path, context, ticket, state, key);
            if (mission is not null) return mission;
        }

        if (path is "/capsule/open" or "/capsule/immediateOpen")
        {
            return await HandleCapsuleOpenAsync(context, ticket, state, key, immediate: path == "/capsule/immediateOpen");
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

        // Every value-bearing payload is assembled from economy, per request. The adapter holds no balance, no
        // ticket and no disc, so a startup or a home that cannot read the player's economy state fails closed
        // rather than answering zeroes the client would believe.
        if (path == "/startup/index")
        {
            // Paid before the read, so the counters this startup sends already hold it: the client prices the
            // tutorial's build-up button from them, and nothing later in onboarding repeats the item list.
            await PayStarterDiscForceAsync(ticket, state, path, context.RequestAborted);
            // Badges first, so a login honor is part of the list this answer carries.
            var newLoginHonors = await RecordBadgeLoginDayAsync(ticket, _timeProvider.GetUtcNow(), context.RequestAborted);
            var economy = await ReadPlayerEconomyAsync(ticket,
                EconomyRead.Balances | EconomyRead.Inventory | EconomyRead.Tasks, path, context.RequestAborted);
            if (economy.Snapshot is null) return EconomyUnavailable(context, path, economy.Error);
            var snapshot = economy.Snapshot!;
            var (own, loadoutFailure) = await TryReadOwnLoadoutAsync(context, ticket, path);
            if (own is null) return loadoutFailure!;

            context.Response.Headers["x-app-status-code"] = "0";
            context.Response.Headers["x-kickflight-fixture"] = "dynamic-demo-session";
            var json = BuildStartupJson(state, snapshot, own, context.Request, newLoginHonors);
            _logger.LogInformation("Serving dynamic startup index ({ItemRows} item rows, {DiscCount} discs owned, {KickerCount} kickers owned)",
                EconomyCatalog.ItemMasterIds.Length,
                snapshot.Items.Count(item => EconomyCatalog.TryDiscId(item.Item, out _)),
                snapshot.Items.Count(item => EconomyCatalog.TryCostumeId(item.Item, out _)));
            return BinaryJson(json, key);
        }

        if (path == "/home/index")
        {
            var economy = await ReadPlayerEconomyAsync(ticket, EconomyRead.Shop | EconomyRead.Pools | EconomyRead.Tasks,
                path, context.RequestAborted);
            if (economy.Snapshot is null) return EconomyUnavailable(context, path, economy.Error);
            var snapshot = economy.Snapshot!;

            context.Response.Headers["x-app-status-code"] = "0";
            context.Response.Headers["x-kickflight-fixture"] = "dynamic-demo-session";
            IReadOnlyList<BattleRankLine> ranks;
            try
            {
                ranks = await ReadOwnBattleRanksAsync(ticket, context.RequestAborted);
            }
            catch (Exception exception) when (exception is OtomoRankException or OtomoIdentityException)
            {
                return SocialUnavailable(context, path, exception);
            }
            var (own, loadoutFailure) = await TryReadOwnLoadoutAsync(context, ticket, path);
            if (own is null) return loadoutFailure!;
            var (capsules, capsuleError) = await TryBuildUserCapsuleListAsync(ticket, context.RequestAborted);
            if (capsules is null) return EconomyUnavailable(context, path, capsuleError);
            var equippedHonor = await EquippedHonorAsync(ticket, context.RequestAborted);
            var json = BuildHomeJson(state, snapshot, ranks, own, capsules, equippedHonor);
            _logger.LogInformation("Serving dynamic home index (active kickerId={KickerId}, costumeId={CostumeId}, activeDeck={Deck}, offers={Offers}, pools={Pools})",
                state.KickerId, state.KickerCostumeId, own.Loadout.ActiveDeck, snapshot.Offers.Count, snapshot.Pools.Count);
            return BinaryJson(json, key);
        }

        if (path == "/battle/teamCreate")
        {
            return await HandleTeamCreateAsync(context, ticket, state, key);
        }

        if (path == "/battle/teamJoin")
        {
            return await HandleTeamJoinAsync(context, ticket, state, key);
        }

        if (path == "/battle/teamRejoin")
        {
            return await HandleTeamRejoinAsync(context, state, key);
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
            return await HandleBattleEntryAsync(context, ticket, state, key);
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
            return await HandleBattleResultAsync(context, ticket, state, key);
        }

        // Replays (DemoSessionApi.Replay.cs): /battle/upload hands the master client's gzip archive to otomo's patch,
        // /battleReplay/index lists the Kicker and Featured channels from it, /battleReplay/play hands out a
        // per-download key and blob URL.
        if (path == "/battle/upload")
        {
            return await HandleBattleUploadAsync(context, ticket, state, key);
        }

        if (path == "/battleReplay/index")
        {
            return await HandleBattleReplayIndexAsync(context, ticket, state, key);
        }

        if (path == "/battleReplay/play")
        {
            return await HandleBattleReplayPlayAsync(context, ticket, state, key);
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
            return await HandleBattleGoodAsync(context, state, key);
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
            return await HandleFollowIndexAsync(context, ticket, state, key);
        }

        if (path == "/follow/online")
        {
            return await HandleFollowOnlineAsync(context, ticket, state, key);
        }

        if (path == "/user/online")
        {
            context.Response.Headers["x-app-status-code"] = "0";
            return BinaryJson("{}", key);
        }

        // Screens the client opens but this server has no data for (rankings, presents, missions, replays...).
        // They answer with a schema-complete neutral payload so the UI renders instead of showing "Network Error".
        var stub = await TryHandleStubAsync(path, context, ticket, state, key);
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

    // /disc/change {discDeckNumber, userDiscDeckList[{number, discIdList}]}: the active deck and any decks the player edited,
    // written to the ext of their session card. Session replaces a field whole on a merge, so the decks are written as the
    // full set: the player's current decks with the edited ones put over them.
    private async Task<IResult?> HandleDiscChangeAsync(HttpContext context, KfIdentityTicket ticket, SessionState state,
        byte[] key)
    {
        const string path = "/disc/change";
        var body = await ReadBodyAsync(context.Request);
        int? activeDeck = null;
        var edited = new Dictionary<int, List<int>>();
        try
        {
            var plaintext = D2CCodec.Decode(body, key);
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            if (root.TryGetProperty("discDeckNumber", out var deckProp))
            {
                activeDeck = deckProp.GetInt32();
            }
            if (root.TryGetProperty("userDiscDeckList", out var deckListProp) && deckListProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var deckEl in deckListProp.EnumerateArray())
                {
                    if (deckEl.TryGetProperty("number", out var numProp) &&
                        deckEl.TryGetProperty("discIdList", out var discListProp) &&
                        discListProp.ValueKind == JsonValueKind.Array)
                    {
                        edited[numProp.GetInt32()] = discListProp.EnumerateArray().Select(x => x.GetInt32()).ToList();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode disc change request: {Error}", ex.Message);
            return null;
        }

        if (activeDeck is not null || edited.Count > 0)
        {
            var (own, readFailure) = await TryReadOwnLoadoutAsync(context, ticket, path);
            if (own is null) return readFailure!;
            var (saved, failure) = await TryEditLoadoutAsync(context, ticket, key, path, own, loadout =>
            {
                if (activeDeck is { } number) loadout.ActiveDeck = number;
                foreach (var (deckNumber, discs) in edited) loadout.DeckSets[deckNumber] = [.. discs];
            });
            if (saved is null) return failure;
        }
        _logger.LogInformation("Disc deck updated: activeDeck={ActiveDeck}, edited decks {Edited}", activeDeck,
            string.Join(",", edited.Keys));
        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-disc-change";
        return BinaryJson("{}", key);
    }

    // /disc/buildup {discId}: one level, paid for in one economy transaction.
    //
    // The price is the served DiscBuildup master's, read the way the client reads it (Otomo/EconomyDiscLevels.cs):
    // going from L to L + 1 costs row L + 1's Disc Force and total(L + 1) - total(L) duplicate copies of this same
    // disc. Both leave through POST /internal/economy/consume in a single call - copies and Disc Force together or
    // neither - keyed by the disc and the level it reaches (level-up:<player>:<disc>:<level>). Two requests that
    // both read level L, such as the adapter's own resend or a retry racing its original, name the same fact, and
    // economy answers the second with the first's result instead of spending again.
    //
    // A retry that arrives after the new level is stored reads L + 1, so the key alone would price it as the next
    // level-up. The client marks a retry itself: it resends a request whose answer it lost with the same
    // x-app-response-cache-id (NetworkManager.SendRequestAsync threads cacheId into AddRequestHeaderResponseCache),
    // and the save keeps the id of the request that raised each disc last. A match is answered with the disc as it
    // now stands and spends nothing. A new tap carries a new id and is a new level-up at its own price.
    //
    // Two economy calls, one fact. The consume is the charge; the advance (POST /internal/economy/items/advance) is
    // the level it bought, because the level is attrs.level on the disc's own possession and economy is where that
    // lives now. They carry different keys on purpose: economy keys idempotency per player across every operation, so
    // a key shared with the consume would be answered as that consume's replay instead of moving anything.
    //
    // Economy checks that the player holds enough; the one thing it cannot check is that the disc itself survives
    // (it deletes an item at zero), so the base copy is checked here against a fresh read before anything is sent.
    // Every refusal is the game's error dialog, and only an unreachable economy is a 503.
    private async Task<IResult?> HandleDiscBuildupAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
    {
        const string path = "/disc/buildup";
        var body = await ReadBodyAsync(context.Request);
        int discId;
        int targetLevel;
        try
        {
            var plaintext = D2CCodec.Decode(body, key);
            using var document = JsonDocument.Parse(plaintext);
            discId = document.RootElement.GetProperty("discId").GetInt32();
            // The level the client expects back, when it names one. 0 = it named none.
            targetLevel = document.RootElement.TryGetProperty("level", out var levelProp) &&
                          levelProp.ValueKind == JsonValueKind.Number
                ? levelProp.GetInt32()
                : 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode disc buildup request: {Error}", ex.Message);
            return BuildupRefused(context, "This disc cannot be levelled up.");
        }

        if (!_discRarity.TryGetValue(discId, out var rarity))
        {
            _logger.LogWarning("{Path} for user {UserId}: disc {DiscId} is not in the served Disc master", path,
                state.UserId, discId);
            return BuildupRefused(context, "This disc cannot be levelled up.");
        }

        var economy = await ReadPlayerEconomyAsync(ticket,
            EconomyRead.Balances | EconomyRead.Inventory | EconomyRead.Tasks, path, context.RequestAborted);
        if (economy.Snapshot is null) return EconomyUnavailable(context, path, economy.Error);
        var snapshot = economy.Snapshot;
        var discItem = $"disc:{discId}";
        var owned = snapshot.Items.FirstOrDefault(item => item.Item == discItem);
        var held = owned?.Qty ?? 0;

        // DiscBuildupResponseData. The stats are not in it: the client recomputes attack, HP and cool time from
        // the DiscGrow master at the level it is handed (DiscParameterUtil.CalcGrowParameter).
        IResult Answer(long heldNow, long discForceNow, int levelNow, IReadOnlyList<int> honors) => OkJson(context, key, JsonSerializer.Serialize(new
        {
            userDisc = EconomyPayloads.BuildUserDisc(discId, heldNow, StandingAt(rarity, levelNow), owned?.AcquiredAt),
            userItem = new
            {
                itemId = EconomyCatalog.DiscForceItemId, amount = (int)Math.Clamp(discForceNow, 0, int.MaxValue)
            },
            userMissionProgressList = MissionRows(state, snapshot.Tracks),
            receivedUserHonorList = honors.Select(honorId => new { honorId, newFlag = true }).ToArray()
        }));

        // The level the disc stands at now, from its possession's attrs: economy's answer, not a saved one.
        //
        // `stored` is the raw attrs value, where 0 means economy holds no level for this disc - a disc nobody has ever
        // raised, which reads as its rarity's start level. That is why the advance below moves by the distance to the
        // target rather than by one: at 0, economy has to be told the level the disc reaches (3, say), not the one step
        // the client thinks it took, or the two would disagree for the rest of the disc's life.
        var stored = (int)Math.Clamp(owned?.Attrs?.Level ?? 0, 0, int.MaxValue);
        var level = stored > 0 ? stored : _tuning.StartLevel(rarity);

        var cacheId = context.Request.Headers["x-app-response-cache-id"].ToString();
        if (cacheId.Length > 0 && state.DiscBuildupRequests.TryGetValue(discId, out var lastCacheId) &&
            lastCacheId == cacheId)
        {
            _logger.LogInformation("{Path}: user {UserId} resent the level-up of disc {DiscId} (cache id {CacheId}); " +
                "answering level {Level} without a second spend", path, state.UserId, discId, cacheId, level);
            return Answer(held, DiscForceOf(snapshot), level, []);
        }

        // A level the disc already has is a retry of an answer the client lost: answered from the current
        // standing, with nothing spent and nothing counted. A level beyond the next step is a client that
        // disagrees with the DiscBuildup master; it is left where it is rather than granted a jump for the price
        // of one step.
        if (targetLevel > 0 && targetLevel <= level)
        {
            _logger.LogInformation("{Path}: user {UserId} asked for disc {DiscId} at level {Target}, which it " +
                "already has (cache id {CacheId}); answering without a second spend", path, state.UserId, discId,
                targetLevel, cacheId);
            return Answer(held, DiscForceOf(snapshot), level, []);
        }
        var step = _discLevels.Next(rarity, level);
        if (step is null)
        {
            _logger.LogInformation("{Path} for user {UserId}: disc {DiscId} (rarity {Rarity}) at level {Level} has no " +
                "next level in the DiscBuildup master (cap {Cap})", path, state.UserId, discId, rarity, level,
                _discLevels.MaxLevel(rarity));
            return BuildupRefused(context, "This disc is already at its highest level.");
        }
        if (held < step.Copies + 1)
        {
            _logger.LogInformation("{Path} for user {UserId}: disc {DiscId} level {Level} -> {Target} needs {Copies} " +
                "duplicate(s) and the disc itself; economy holds {Held}", path, state.UserId, discId, level,
                step.TargetLevel, step.Copies, held);
            return BuildupRefused(context, held < 1
                ? "You do not own this disc."
                : "You do not have enough copies of this disc to level it up.");
        }

        // A level well past the next step is a client that disagrees with the DiscBuildup master. It is told the
        // disc's current standing rather than granted the jump for the price of one step.
        if (targetLevel > level + 1)
        {
            _logger.LogWarning("{Path} for user {UserId}: disc {DiscId} is at level {Level} and the request " +
                "names {Target}, more than one step; leaving it where it is", path, state.UserId, discId, level,
                targetLevel);
            return Answer(held, DiscForceOf(snapshot), level, []);
        }

        var consumeKey = EconomyDiscLevels.Key(ticket.OtomoPlayerId, discId, step.TargetLevel);
        EconomyConsumeResult spent;
        try
        {
            spent = await _economy.ConsumeAsync(new EconomyConsumeRequest(
                    ticket.OtomoPlayerId, consumeKey, "disc level-up", ProgressKeyPart($"disc-buildup:{discId}"),
                    step.Copies > 0 ? [new EconomyItemCost(discItem, step.Copies)] : [],
                    [new EconomyCurrencyCost(EconomyCatalog.DiscForceCurrency, step.DiscForce)]),
                context.RequestAborted);
        }
        catch (OtomoEconomyException ex) when (ex.Failure == OtomoEconomyFailure.Refused)
        {
            // insufficient_funds or insufficient_items: nothing has moved, and the level stays where it was.
            _logger.LogInformation("{Path} for user {UserId}: economy refused disc {DiscId} level {Level} -> {Target} " +
                "({Copies} copies, {Force} disc_force): {Status} {Code}", path, state.UserId, discId, level,
                step.TargetLevel, step.Copies, step.DiscForce, ex.StatusCode, ex.Code);
            return BuildupRefused(context, ex.Code == "insufficient_items"
                ? "You do not have enough copies of this disc to level it up."
                : "You do not have enough Disc Force to level up this disc.");
        }
        catch (OtomoEconomyException ex)
        {
            return EconomyUnavailable(context, path, ex);
        }

        // The charge has happened; this is the level it bought. A failure here is reported as an outage rather than
        // swallowed: the consume above already spent, and a retry of it is a replay that spends nothing, so the
        // client's next attempt reaches this call again and converges on the same level.
        EconomyAdvanceResult advanced;
        try
        {
            advanced = await _economy.AdvanceAsync(new EconomyAdvanceRequest(
                    ticket.OtomoPlayerId, discItem, EconomyDiscLevels.AdvanceKey(ticket.OtomoPlayerId, discId, step.TargetLevel),
                    "disc level-up", ProgressKeyPart($"disc-buildup:{discId}"), EconomyDiscLevels.LevelField,
                    Math.Max(1, step.TargetLevel - stored), _discLevels.MaxLevel(rarity)),
                context.RequestAborted);
        }
        catch (OtomoEconomyException ex)
        {
            _logger.LogError("{Path} for user {UserId}: disc {DiscId} was charged for level {Target} but the advance " +
                "was refused or unreachable ({Failure} {Status} {Code}); a retry spends nothing and moves it",
                path, state.UserId, discId, step.TargetLevel, ex.Failure, ex.StatusCode, ex.Code);
            return EconomyUnavailable(context, path, ex);
        }

        // The retry ledger only: what the disc's level is, economy has just been told.
        lock (state)
        {
            if (cacheId.Length > 0) state.DiscBuildupRequests[discId] = cacheId;
            SaveUserState(state);
        }

        // The badge fact comes from the advance's own answer, which is the only thing that knows the level moved:
        // a single verified step on a disc the masters carry is what a badge records, and a jump of more than one
        // level is not, which is the same rule main applies to its own level mutation.
        var countsAsUpgrade = _discIdList.Contains(discId) && level < 50 && advanced.Value == level + 1;
        var receivedHonors = countsAsUpgrade
            ? (await RecordBadgeUpgradeAsync(ticket, discItem, advanced.Value,
                EconomyDiscLevels.AdvanceKey(ticket.OtomoPlayerId, discId, step.TargetLevel), context.RequestAborted)).ToArray()
            : Array.Empty<int>();

        // The answer's own totals; a consume that answered without them is what was read before, less the price.
        var heldAfter = spent.Items?.FirstOrDefault(move => move.Item == discItem)?.Total ?? held - step.Copies;
        var discForce = spent.Balances?.FirstOrDefault(balance => balance.Currency == EconomyCatalog.DiscForceCurrency)?.Balance
                        ?? Math.Max(0, DiscForceOf(snapshot) - step.DiscForce);
        _logger.LogInformation("{Path}: user {UserId} raised disc {DiscId} from level {Level} to {Target}, spending " +
            "{Copies} copies and {Force} disc_force under {Key}{Replayed}; holds {Held} copies and {DiscForce} disc_force",
            path, state.UserId, discId, level, step.TargetLevel, step.Copies, step.DiscForce, consumeKey,
            spent.Replayed ? " (replayed)" : "", heldAfter, discForce);
        return Answer(heldAfter, discForce, (int)advanced.Value, receivedHonors);
    }

    // The client has no ErrorType for "cannot afford": its build-up button is greyed out by its own check
    // (CalcBuildupParameter against the counters it was sent), so this answer is for a client whose counters were
    // stale. It is the game's error contract - HTTP 200, a non-zero x-app-status-code and a PLAINTEXT
    // {"error":{code,title,message}} body, because NetworkManager.GetResponseErrorFromResponse (0x31B2AE0) reads it
    // without D2C-decoding. 2502 (GoodsUnreceivable) is not one of the codes NetworkManager.OpenErrorWindowOnFailed
    // (0x31B7F98) special-cases, so the dialog prints this title and message. 2002-2004 would not do: that function
    // replaces their text with the localized name-rule messages.
    private static IResult BuildupRefused(HttpContext context, string message) =>
        InsufficientFundsRefused(context, "Disc level-up", message);

    // The same answer for any spend the wallet cannot cover (an early capsule open is one): the title names the screen.
    private static IResult InsufficientFundsRefused(HttpContext context, string title, string message)
    {
        const string code = "2502";
        context.Response.Headers["x-app-status-code"] = code;
        var body = JsonSerializer.Serialize(new { error = new { code, title, message } });
        return Results.Text(body, "application/json", Encoding.UTF8, StatusCodes.Status200OK);
    }

    // A disc as the client is to be shown it: the level economy holds for it, and the copies that level has already
    // spent.
    private DiscStanding DiscStandingOf(EconomySnapshot snapshot, int discId) =>
        StandingAt(_discRarity.GetValueOrDefault(discId), DiscLevelOf(snapshot, discId));

    private DiscStanding StandingAt(int rarity, int level) =>
        new(level, _discLevels.CopiesSpent(rarity, level, _tuning.StartLevel(rarity)));

    // The level of a disc, from the attrs on its own possession (economy's closed schema: level, counter,
    // available_at). 0 is "economy has been told nothing", which is a disc nobody has raised, and it reads as the
    // rarity's start level - the same answer the save gave for a disc it had no entry for.
    private int DiscLevelOf(EconomySnapshot snapshot, int discId)
    {
        var item = $"disc:{discId}";
        var level = (int)Math.Clamp(
            snapshot.Items.FirstOrDefault(row => row.Item == item)?.Attrs?.Level ?? 0, 0, int.MaxValue);
        return level > 0 ? level : _tuning.StartLevel(_discRarity.GetValueOrDefault(discId));
    }

    // The disc levels an entry is issued with, read from economy's inventory. A level is a possession's attr now, so
    // a ranked entry needs that read; with levels ranked-only off nothing reads them, and no call is made.
    private async Task<EconomyLoad> ReadDeckLevelEconomyAsync(KfIdentityTicket ticket, string path, HttpContext context) =>
        _tuning.LevelsRankedOnly
            ? await ReadPlayerEconomyAsync(ticket, EconomyRead.Inventory, path, context.RequestAborted)
            : new EconomyLoad(EconomySnapshot.None, null);

    // What each disc of a deck fights at in a battle of this match type. Casual (anything but matchType 2) fights
    // at the tuning's flat level so a fresh account and a veteran meet on equal discs; ranked fights at the levels
    // the player has actually raised. The levels are fixed when the entry is issued, like the rest of the entry.
    private List<int> DeckLevelsFor(EconomySnapshot snapshot, IReadOnlyList<int> deck, int? matchType) =>
        deck.Select(discId => matchType == 2 || !_tuning.LevelsRankedOnly
            ? DiscLevelOf(snapshot, discId)
            : _tuning.CasualDiscLevel).ToList();

    // The one error shape this server uses: any non-zero x-app-status-code aborts the client flow and it shows the
    // generic error toast for that screen. The body stays schema-shaped so nothing is left half-deserialized.
    private static LocalFixtureResult StatusError(HttpContext context, byte[] key)
    {
        context.Response.Headers["x-app-status-code"] = "1";
        return BinaryJson("{}", key);
    }

    // gear/create: retail rolls a random gear for the requested kicker and hands the id back; the player then either
    // equips it (gear/set) or discards it (gear/destroy), both of which clear the pending roll. The roll waits in the ext
    // of the player's session card (pending_gear), so a restart or a second server still has it.
    private async Task<IResult?> HandleGearCreateAsync(HttpContext context, KfIdentityTicket ticket, SessionState state,
        byte[] key)
    {
        const string path = "/gear/create";
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
        var (written, failure) = await TryWriteGearRollAsync(context, ticket, key, path, "gear-create",
            new GearRoll(kickerId, gearId));
        if (written is null) return failure;
        if (!written.Applied)
        {
            // A retry of a roll session already stored: the gear the player was shown is the one stored, not this roll.
            var stored = Loadout.RollFromExt(written.Ext);
            if (stored.GearId != 0) gearId = stored.GearId;
        }

        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-gear-create";
        _logger.LogInformation("Rolled gear {GearId} for kicker {KickerId} (user {UserId})", gearId, kickerId, state.UserId);
        return BinaryJson($"{{\"gearId\":{gearId}}}", key);
    }

    private async Task<IResult?> HandleGearSetAsync(HttpContext context, KfIdentityTicket ticket, SessionState state,
        byte[] key)
    {
        const string path = "/gear/set";
        var body = await ReadBodyAsync(context.Request);
        int costumeId, slotNumber;
        try
        {
            var plaintext = D2CCodec.Decode(body, key);
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            costumeId = root.TryGetProperty("kickerCostumeId", out var costumeProp) ? costumeProp.GetInt32() : 0;
            slotNumber = root.TryGetProperty("gearIdNumber", out var slotProp) ? slotProp.GetInt32() : 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not decode gear set request: {Error}", ex.Message);
            return StatusError(context, key);
        }

        var (own, failure) = await TryReadOwnLoadoutAsync(context, ticket, path);
        if (own is null) return failure!;
        var gearId = own.Loadout.Roll.GearId;
        if (slotNumber is < 1 or > 3 || !_costumeKicker.ContainsKey(costumeId) || !_gearIdList.Contains(gearId))
        {
            _logger.LogWarning("Rejected gear/set: costume={CostumeId} slot={Slot} pendingGear={GearId}", costumeId, slotNumber, gearId);
            return StatusError(context, key);
        }

        // The gear goes into the loadout first and the roll is cleared after: a failure between the two leaves the gear
        // equipped and the roll still offered (equipping it again is the same write), never a roll lost without its gear.
        var (saved, saveFailure) = await TryEditLoadoutAsync(context, ticket, key, path, own, loadout =>
        {
            var slots = loadout.GearSlots(costumeId);
            slots[slotNumber - 1] = gearId;
            loadout.GearsByCostume[costumeId] = slots;
        });
        if (saved is null) return saveFailure;
        var (written, writeFailure) = await TryWriteGearRollAsync(context, ticket, key, path, "gear-set", GearRoll.None);
        if (written is null) return writeFailure;

        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-gear-set";
        _logger.LogInformation("Equipped gear {GearId} in slot {Slot} of costume {CostumeId} (user {UserId})", gearId, slotNumber, costumeId, state.UserId);
        return BinaryJson("{}", key);
    }

    private async Task<IResult?> HandleGearDestroyAsync(HttpContext context, KfIdentityTicket ticket, SessionState state,
        byte[] key)
    {
        var (written, failure) = await TryWriteGearRollAsync(context, ticket, key, "/gear/destroy", "gear-destroy",
            GearRoll.None);
        if (written is null) return failure;

        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-gear-destroy";
        _logger.LogInformation("Discarded the pending gear of user {UserId}", state.UserId);
        return BinaryJson("{}", key);
    }

    private string BuildStartupJson(SessionState state, EconomySnapshot economy, OwnLoadout own, HttpRequest request,
        IReadOnlyCollection<int>? newHonorIds = null)
    {
        NormalizeCostume(state);
        // The status decides which masters the client downloads next, and with them whether it can take mission rows.
        var tutorialStatus = TutorialStatus(state, economy);
        RecordStartupStatus(state, tutorialStatus);
        var pendingGear = own.Loadout.Roll;
        var payloads = Payloads(state, own.Loadout);
        // The kickers and costumes the player owns, from the inventory's skin:<costumeId> items: owning a costume
        // owns its kicker. The active costume is always included, so a save whose inventory says nothing still
        // renders the player it says is selected.
        var userKickerList = payloads.BuildUserKickerList(economy, state.KickerId, state.KickerCostumeId);

        var kickerTrainingList = _kickerList.Select(k => new
        {
            kickerId = k.Id,
            clearTrainingMissionTaskCount = 0
        }).ToArray();

        // The discs the player owns, from the inventory's disc:<discId> items, at the level economy holds on each.
        var userDiscList = payloads.BuildUserDiscList(economy, discId => DiscStandingOf(economy, discId));

        var data = new
        {
            userKickerList,
            userDiscList,
            userHonorList = OwnedHonorIds(economy)
                .Select(honorId => new { honorId, registerDatetime = "2026-09-01 00:00:00", newFlag = newHonorIds?.Contains(honorId) == true }).ToArray(),
            // All 8 Item master rows: 1-4 the legacy counters, 5-8 the round C stocks (gear stamps, disc fragments
            // and the two gacha tickets) that the gear screen and the shop read their counters from.
            userItemList = payloads.BuildUserItemList(economy),
            // The client reads userGear.gearId != 0 as "there is a rolled gear waiting for equip/discard", and
            // userGear.kickerId as whose it is; with nothing pending it keeps pointing at the active kicker.
            userGear = new
            {
                kickerId = pendingGear.GearId != 0 ? pendingGear.KickerId : state.KickerId,
                gearId = pendingGear.GearId
            },
            userStampList = Array.Empty<object>(),
            // Economy's tracks, as the mission masters name them (Otomo/EconomyMissions.cs), for a client that loads
            // those masters with this answer (DemoSessionApi.Missions.cs).
            userMissionProgressList = MissionRows(state, economy.Tracks),
            userDailyRandomMissionTaskList = Array.Empty<object>(),
            kickerTrainingList,
            userPremium = new { premiumId = 0, startDatetime = "", endDatetime = "" },
            userSeasonPass = new { seasonPassId = 1, startDatetime = "2019-01-01 00:00:00", endDatetime = "2030-01-01 23:59:59" },
            userFestivalTeam = new { battleRuleId = 5, festivalTeamId = 1, festivalPoint = 0 },
            tutorialProgressStatus = tutorialStatus,
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
            chatRoomConnectionCustomToken = _chat.Enabled ? "kfchat-dummy-token" : "",
            chatRoomConnectionId = _chat.Enabled ? "kfchat" : "",
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

    // KF_CHAT_EXP=1: serve a non-empty chat connection id so the client attempts Firebase chat setup (experiment only).

    private string BuildHomeJson(SessionState state, EconomySnapshot economy, IReadOnlyList<BattleRankLine> ranks,
        OwnLoadout own, object[] userCapsuleList, int equippedHonor)
    {
        NormalizeCostume(state);

        var userPlayer = new
        {
            userId = state.UserId,
            displayUserId = PlayerDisplayIdCodec.ToPublic(int.TryParse(state.UserId, out var uid) ? uid : 1000001),
            name = OwnName(state, own),
            exp = 38500,
            honorId = equippedHonor,
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
            discDeckNumber = own.Loadout.ActiveDeck,
            officialFlag = false
        };

        var userDiscDeckList = own.Loadout.DeckSets.OrderBy(d => d.Key).Select(d => new
        {
            number = d.Key,
            discIdList = d.Value.ToArray()
        }).ToArray();

        var data = new
        {
            userPlayer,
            userDiscDeckList,
            // The capsules battles have handed out and the player has not opened (DemoSessionApi.Capsules.cs).
            userCapsuleList = userCapsuleList,
            // The shop's disc/kicker scrollers (Colorful.ShopDiscScroller._data : List<GachaGroupInfo>) are filled
            // from these two lists through GachaGroupListInfo(ResponseGachaGroup[]); serving them empty left the
            // scroller with a data list shorter than the cell range EnhancedScroller._Resize walks and GetCellHeight
            // threw ArgumentOutOfRangeException (List<T>.get_Item).
            discGachaGroupList = BuildHomeDiscGachaGroupList(economy),
            kickerGachaGroupList = BuildHomeKickerGachaGroupList(economy),
            userBattleRankList = BuildBattleRankList(ranks),
            userMissionProgressList = MissionRows(state, economy.Tracks),
            // shopProductList is the real-money IAP list, which this server does not sell; the goods shop is the
            // JetCoin one and is served in full.
            shopProductList = Array.Empty<object>(),
            goodsShopProductList = BuildGoodsShopProducts(economy),
            // GoodsShopProductListInfo carries these two counts beside the product list, and the Discs tab draws its
            // fragment-conversion section from them: without the fields the client falls back to its own baked
            // numbers and the section renders with a blank banner. Zero products cost fragments, so both are zero.
            costDiscFragmentProductCount = 0,
            costDiscFragmentProductMinCost = 0,
            // The exchange tab's own scroller (ShopExchangeScroller) stays empty: no disc fragments are converted.
            discFragmentConversionDiscIdList = Array.Empty<int>(),
            // The badge on the mission button: steps reached and not yet claimed, over the rows MissionRows serves.
            userMissionTaskStatusNotification = new
            {
                newFlagCount = 0,
                completionCount = MissionBadge(state, economy.Tracks)
            },
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

            // otomo decides who this device is: one login per device, cached in the adapter, and nothing is
            // created here. The numeric row id is only where this player's game state is filed.
            OtomoSession session;
            long playerId;
            try
            {
                session = await _identity.GetSessionAsync(uuid, context.RequestAborted);
                playerId = _playerStore.ResolveGamePlayer(session.PlayerId, uuid);
            }
            catch (OtomoIdentityException exception)
            {
                return IdentityUnavailable(context, "/auth/index", exception, uuid);
            }

            var state = _statesByPlayer.GetOrAdd(playerId, GetOrCreateUserState);

            // No name is invented here. An empty name is what puts the client on the name-entry window; a
            // name is only ever set by the player, through POST /tutorial/end.

            var key = Encoding.ASCII.GetBytes(hash);
            // This is the token the client keeps and echoes as x-app-access-token. It is signed rather than
            // stored, and it carries the otomo device id and account, so every later request is attributed
            // without asking otomo again and a logout can revoke exactly this device's family.
            var accessToken = _accessTokens.Issue(
                new KfIdentityTicket(OtomoDeviceId.FromDeviceUuid(uuid), session.PlayerId, playerId, key));

            context.Response.Headers["x-app-status-code"] = "0";
            context.Response.Headers["x-app-user-id"] = playerId.ToString();
            context.Response.Headers["x-app-access-token"] = accessToken;
            context.Response.Headers["x-kickflight-fixture"] = "dynamic-demo-auth";
            context.Response.Headers["x-app-datetime"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            _logger.LogInformation("Signed in otomo player {OtomoPlayerId} as game row {PlayerId} (name {UserName:l})",
                session.PlayerId, playerId, state.HasName ? state.UserName : "<unset>");
            _ops.RecordAuth(state.UserId, OpsClientInfo.FromRequest(context));
            return BinaryJson("{}", key);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or ArgumentException)
        {
            _logger.LogWarning("Could not decode local demo authentication request: {Error}", exception.Message);
            return null;
        }
    }

    // The identity client's failures, answered in the game's contract rather than as a 500.
    //
    // The body is PLAINTEXT JSON with a non-zero x-app-status-code, for the same reason the name-rejection answer
    // is: NetworkManager.GetResponseErrorFromResponse JsonUtility-parses downloadHandler.text without D2C-decoding
    // it, so an encrypted body reads as an empty code and the client shows its generic network error with nothing
    // an operator can act on.
    //
    // A refusal - otomo will not admit this device, or it revoked the session - is this client's 401. Everything
    // else is a 503: the caller's identity is unknown rather than refused, and the same request works once otomo
    // answers again. Both are logged once, with the device masked: a device id is a credential.
    private IResult IdentityUnavailable(HttpContext context, string path,
        OtomoIdentityException? failure = null, string? deviceUuid = null)
    {
        var reason = failure?.Failure ?? OtomoIdentityFailure.Disabled;
        var detail = failure?.Message ?? "Otomo:Enabled is false";
        if (deviceUuid is null)
        {
            _logger.LogError("Refusing {Path}: identity {Reason} ({Detail})", path, reason, detail);
        }
        else
        {
            _logger.LogError("Refusing {Path} for device {Device}: identity {Reason} ({Detail})",
                path, OtomoDeviceId.Mask(OtomoDeviceId.FromDeviceUuid(deviceUuid)), reason, detail);
        }

        context.Response.Headers["x-app-status-code"] = "1";
        var rejected = reason == OtomoIdentityFailure.Rejected;
        return Results.Json(
            new { error = rejected ? "identity-rejected" : "identity-unavailable", reason = reason.ToString() },
            statusCode: rejected ? StatusCodes.Status401Unauthorized : StatusCodes.Status503ServiceUnavailable);
    }

    // The name-entry window's submit, and the only place a new account gets a name. It persists before
    // answering because the client re-issues /startup/index the moment this returns, and that reply has to carry
    // both the name and tutorialProgressStatus = 207 or the home is left inconsistent.
    //
    // TutorialEndRequestData carries the name in a field spelled "name"; "userName" is accepted too, for the same
    // reason /user/change accepts both.
    private async Task<IResult?> HandleTutorialEndAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
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

        // What a name may be - its length, its characters, whether it is unique, how often it changes - is session's
        // `names` rules, published by the tenant. The adapter asks session and maps a refusal to the client's error.
        if (await RenameAtSessionAsync(context, key, ticket, state, name, "/tutorial/end") is { } refused) return refused;

        state.UserName = name;
        state.TutorialProgress = TutorialStatusComplete;
        SaveUserState(state);
        await RecordBadgeRegistrationAsync(ticket, context.RequestAborted);
        _logger.LogInformation("Player {PlayerId} chose the name {UserName:l}", state.PlayerId, name);
        return OkJson(context, key, "{}");
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
                    "taken" => "That name is already used by another player.",
                    _ => "That name cannot be used."
                }
            }
        });
        return Results.Text(body, "application/json", Encoding.UTF8, StatusCodes.Status200OK);
    }

    private async Task<IResult?> HandleTeamCreateAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
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
        var (own, loadoutFailure) = await TryReadOwnLoadoutAsync(context, ticket, "/battle/teamCreate");
        if (own is null) return loadoutFailure!;
        var deck = own.Loadout.ActiveDeckDiscs();
        var ruleMetadata = BattleRuleMetadata(battleRuleId);
        var levels = await ReadDeckLevelEconomyAsync(ticket, "/battle/teamCreate", context);
        if (levels.Snapshot is null) return EconomyUnavailable(context, "/battle/teamCreate", levels.Error);
        var entry = _matchmaking.CreateTeam(
            state.UserId, EntryName(state, own), state.KickerId, state.KickerCostumeId, battleRuleId, deck, code,
            isDiag: IsDiagClient(context), matchType: ruleMetadata.MatchType,
            battleRuleType: ruleMetadata.BattleRuleType,
            deckLevels: DeckLevelsFor(levels.Snapshot, deck, ruleMetadata.MatchType));
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

    private async Task<IResult?> HandleTeamJoinAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
    {
        using var request = await ReadTeamRequestAsync(context, state, key);
        if (request is null
            || !TryReadString(request.RootElement, "matchmakingTeamId", out var teamId)
            || string.IsNullOrWhiteSpace(teamId))
        {
            return StatusError(context, key);
        }

        NormalizeCostume(state);
        var (own, loadoutFailure) = await TryReadOwnLoadoutAsync(context, ticket, "/battle/teamJoin");
        if (own is null) return loadoutFailure!;
        var deck = own.Loadout.ActiveDeckDiscs();
        // The joiner does not choose the rule - the team does - so the levels are chosen for whatever the team
        // turns out to be playing. The read is taken before the join because the lambda below is evaluated inside it.
        var levels = await ReadDeckLevelEconomyAsync(ticket, "/battle/teamJoin", context);
        if (levels.Snapshot is null) return EconomyUnavailable(context, "/battle/teamJoin", levels.Error);
        var entry = _matchmaking.JoinTeam(
            teamId, state.UserId, EntryName(state, own), state.KickerId, state.KickerCostumeId, deck,
            isDiag: IsDiagClient(context), deckLevels: matchType => DeckLevelsFor(levels.Snapshot, deck, matchType));
        if (entry is null) return StatusError(context, key);

        var response = new
        {
            battleEntryId = entry.BattleEntryId,
            matchmakingTeamId = entry.MatchmakingTeamId,
            battleEntryTicketId = entry.TicketId,
            // BattleTeamJoinResponseData has no assetList; the client's DirectTeamMatchingInfo.AssetList stays null
            // for joins, and RejoinTeamDirect later feeds it to a List ctor. Emitting it here is harmless and kept
            // in step with teamCreate.
            assetList = BuildApplicationAssetList(context.Request),
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

    /// <summary>
    /// /battle/teamRejoin: request {battleEntryId, userPingList}; response BattleTeamRejoinResponseData
    /// {battleEntryId, matchmakingTeamId, battleEntryTicketId, assetList, campaignList}. Echoes the caller's existing
    /// team entry; an unknown entry/team is a status error (client CallbackTeamRejoinFailed) instead of the old
    /// catch-all {} that left assetList null and crashed RejoinTeamDirect.
    /// </summary>
    private async Task<IResult?> HandleTeamRejoinAsync(HttpContext context, SessionState state, byte[] key)
    {
        using var request = await ReadTeamRequestAsync(context, state, key);
        if (request is null
            || !TryReadString(request.RootElement, "battleEntryId", out var battleEntryId)
            || string.IsNullOrWhiteSpace(battleEntryId))
        {
            return StatusError(context, key);
        }

        var entry = _matchmaking.RejoinTeam(battleEntryId, state.UserId);
        if (entry is null) return StatusError(context, key);

        var response = new
        {
            battleEntryId = entry.BattleEntryId,
            matchmakingTeamId = entry.MatchmakingTeamId,
            battleEntryTicketId = entry.TicketId,
            assetList = BuildApplicationAssetList(context.Request),
            campaignList = Array.Empty<object>()
        };

        context.Response.Headers["x-app-status-code"] = "0";
        context.Response.Headers["x-kickflight-fixture"] = "dynamic-battle-team-rejoin";
        _logger.LogInformation("User {UserId} rejoined team {TeamId}", state.UserId, entry.MatchmakingTeamId);
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

    private async Task<IResult?> HandleBattleEntryAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
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

        var (own, loadoutFailure) = await TryReadOwnLoadoutAsync(context, ticket, "/battle/entry");
        if (own is null) return loadoutFailure!;
        var deck = own.Loadout.ActiveDeckDiscs();
        NormalizeCostume(state);
        var ruleMetadata = BattleRuleMetadata(battleRuleId);
        var levels = await ReadDeckLevelEconomyAsync(ticket, "/battle/entry", context);
        if (levels.Snapshot is null) return EconomyUnavailable(context, "/battle/entry", levels.Error);
        var (battleEntryId, ticketId) = _matchmaking.RegisterEntry(
            state.UserId, EntryName(state, own), state.KickerId, state.KickerCostumeId, battleRuleId, deck,
            isDiag: IsDiagClient(context), matchType: ruleMetadata.MatchType,
            battleRuleType: ruleMetadata.BattleRuleType,
            deckLevels: DeckLevelsFor(levels.Snapshot, deck, ruleMetadata.MatchType));

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
        if (RankedModeRotation.IsRankedRule(battleRuleId))
        {
            // Fire-and-forget: never delays or fails the entry response.
            _rankedNotifier?.NotifyInBackground(state.PlayerId, id => _matchmaking.IsUserQueued(id.ToString()), LastActivityOf);
        }
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
            using var customFields = JsonDocument.Parse(MasterJson("CustomBattleRuleField",
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
    /// <summary>
    /// True/false when the validated report decides a rank win/loss; null (with a reason) when rank must not move:
    /// no report, undetermined outcome, tie, or a breakaway/badConnection caller.
    /// </summary>
    internal static bool? RankWinFor(BadgeBattleStatReport? report, out string reason)
    {
        reason = "";
        if (report is null) { reason = "no_report"; return null; }
        if (report.CallerFlagged) { reason = "caller_flagged"; return null; }
        switch (report.Outcome)
        {
            case BadgeBattleOutcome.Win: return true;
            case BadgeBattleOutcome.Loss: return false;
            case BadgeBattleOutcome.Tie: reason = "tie"; return null;
            default:
                reason = string.IsNullOrEmpty(report.OutcomeReason) ? report.Reason : report.OutcomeReason;
                return null;
        }
    }

    private async Task<IResult?> HandleBattleResultAsync(HttpContext context, KfIdentityTicket ticket,
        SessionState state, byte[] key)
    {
        var battleEntryId = "";
        string? resultBodyJson = null;
        // The two counters the client reports that economy keeps: kills and the MVP flag. Both are the client's
        // word, which is the same trust level as the ranked result this handler already applies.
        var killCount = 0;
        var mvp = false;
        try
        {
            var body = await ReadBodyAsync(context.Request);
            if (body.Length > 16)
            {
                resultBodyJson = Encoding.UTF8.GetString(D2CCodec.Decode(body, key));
                using var document = JsonDocument.Parse(resultBodyJson);
                if (document.RootElement.TryGetProperty("battleEntryId", out var entryProp)
                    && entryProp.ValueKind == JsonValueKind.String)
                    battleEntryId = entryProp.GetString() ?? "";
                if (document.RootElement.TryGetProperty("killCount", out var killProp) &&
                    killProp.ValueKind == JsonValueKind.Number && killProp.TryGetInt32(out var reportedKills))
                {
                    killCount = reportedKills;
                }
                mvp = TryGetBool(document.RootElement, "mvpFlag", out var reportedMvp) && reportedMvp;
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

        // The client's own stats are credited only after BadgeBattleStatEvaluator bound them against what this
        // server holds for the entry (owner, room roster/team, mode, timing). A rejected set still counts the
        // battle as played; custom battles never reach this handler (see /customBattle/result).
        BadgeBattleStatReport? statReport = null;
        if (ownedEntry is not null)
        {
            statReport = BadgeBattleStatEvaluator.Evaluate(
                resultBodyJson, _matchmaking.GetBadgeBattleContext(battleEntryId), _timeProvider.GetUtcNow());
            if (!statReport.CreditStats)
                _logger.LogInformation("Battle result stats for {UserId} entry {BattleEntryId} not credited: {Reason}",
                    state.UserId, battleEntryId, statReport.Reason);
        }

        // Non-sensitive numeric summary of the client's report, kept permanently to validate field units live
        // (movingDistance raw value + JSON type). The user is identified by a short hash only.
        _logger.LogInformation("Battle result parsed: user={UserHash} {Summary} outcome={Outcome} credited={Credited} capReason={CapReason}",
            BattleResultLogSummary.ShortHash(state.UserId), BattleResultLogSummary.Describe(resultBodyJson, battleRuleType),
            statReport?.Outcome.ToString() ?? "n/a", statReport?.CreditStats ?? false,
            statReport is { CreditStats: false } ? statReport.Reason : "none");

        // Rank follows the validated team outcome (own team from the server-held room roster, own score > other).
        // win -> win points, loss -> loss points; tie, undetermined and breakaway/badConnection callers leave the
        // rank untouched (no relief rule exists in the masters, so a flagged caller is not punished either).
        // The persistence callback runs at most once per entry; a null return applies and caches nothing.
        BattleMatchmakingService.BattleResultProcessing? processedRankedResult = null;
        BattleRankStanding before;
        BattleRankStanding after;
        try
        {
            if (applyProgression)
            {
                processedRankedResult = await _matchmaking.ProcessRankedResultOnceAsync(battleEntryId, state.UserId,
                    ruleType => AwardRankedResultAsync(ticket, state, battleEntryId, ruleType, statReport,
                        context.RequestAborted));
            }
            var rankSnapshot = processedRankedResult?.RankedResult;
            before = rankSnapshot?.Before ?? await ReadOwnStandingAsync(ticket, battleRuleType, context.RequestAborted);
            after = rankSnapshot?.After ?? before;
        }
        catch (Exception exception) when (exception is OtomoRankException or OtomoIdentityException)
        {
            // The award (or the standing the screen must show) could not be had from session. It is not dropped and not
            // guessed: the result is answered 503, nothing was cached, and the client's retry carries the same award
            // key, which session applies once.
            _logger.LogError(exception,
                "Battle result for {UserId} entry {BattleEntryId} could not be ranked by session; answering 503",
                state.UserId, battleEntryId);
            return SocialUnavailable(context, "/battle/result", exception);
        }
        // The battle goes to economy as a declared fact, once per verified battle (keyed by the entry); the tenant's
        // tracks decide what it earns, and the rungs it reached are claimed here so the result can say so.
        var battleHonors = ownedEntry is null || statReport is null
            ? (Honors: (IReadOnlyList<int>)[], Fact: (EconomyFact?)null)
            : await RecordBadgeBattleAsync(ticket, battleEntryId, statReport, battleRuleType,
                applyProgression ? battleRuleType : 0, applyProgression ? after.Rank : -1, context.RequestAborted);
        var receivedHonors = battleHonors.Honors.ToArray();

        // The battle's counters go to economy as facts, before the client is told the battle is over: what they
        // are worth is a track's step, claimed later on the player's own plane.
        await ReportBattleProgressAsync(ticket, battleEntryId, won: applyProgression, killCount, mvp,
            context.RequestAborted);

        // A capsule, for a battle this server issued to this player (DemoSessionApi.Capsules.cs): the battle fact's
        // entitlement trigger mints it. Keyed by the entry, so a retried result shows the same capsule.
        var capsule = await ShownBattleCapsuleAsync(ticket, state, battleEntryId, battleHonors.Fact, context.RequestAborted);
        var missionProgress = await ReadMissionProgressAfterBattleAsync(ticket, state, context.RequestAborted);

        var rank = new { battleRuleType, battlePoint = after.BattlePoint, rank = after.Rank };
        var resp = new
        {
            userExp = 38500 + 800, // the user's total exp (see the static exp served at startup) plus this battle's reward
            userBattleRank = rank,
            beforeUserBattleRank = new { battleRuleType, battlePoint = before.BattlePoint, rank = before.Rank },
            playerLevelUpRewardList = Array.Empty<object>(),
            receivedUserCapsuleList = capsule is null ? Array.Empty<object>() : [UserCapsuleRow(capsule)],
            receivedUserItemList = Array.Empty<object>(),
            receivedUserPresentList = Array.Empty<object>(),
            userMissionProgressList = missionProgress,
            userDailyRandomMissionTaskList = Array.Empty<object>(),
            receivedUserHonorList = receivedHonors.Select(honorId => new { honorId, newFlag = true }).ToArray(),
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

    private int _missingWinOutcomeLogged;

    // The `win` outcome of the published ladder; 0 (logged once) when the document is missing or declares none.
    private int PublishedWinBattlePoint()
    {
        if (CurrentLadder()?.WinOutcome is { } win) return win;
        if (Interlocked.Exchange(ref _missingWinOutcomeLogged, 1) == 0)
        {
            _logger.LogWarning(
                "The published {Namespace} ladder declares no 'win' outcome, so /battle/end shows battlePoint 0",
                RankLadderNamespace);
        }
        return 0;
    }

    private async Task<IResult?> HandleBattleEndAsync(HttpContext context, SessionState state, byte[] key)
    {
        // The battle pays nothing here. What a finished battle is worth is economy's - a counter reported by
        // /battle/result and a track the player claims - and an adapter that minted coins on its own would be a
        // second, unaudited economy.
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
            // What the published ladder says a win is worth, where this used to be a constant.
            battlePoint = PublishedWinBattlePoint(),
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
