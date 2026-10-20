# AI Role Server Transport & Master Design

Status: Server-side architectural design and verified experimental role foundation. Native worker is currently validating lookup; custom IDs are NOT enabled in the live roster yet. All existing code, masters, and notes are preserved.

## 1. Integration Seams & Traced Paths

- **Roster & Parameter Assignment:**
  - `src/KickFlight.BootstrapApi/BattleMatchmakingService.cs:1292-1336` (`BuildRoster`):
    - Invokes `ScrambleBotCompositionSelector.Select` to produce `ScrambleBotAssignment` per bot, selecting an explicit `assignment.Profile` and `assignment.DeckId`.
    - Live roster currently passes `kickerAiParameterId = bot.KickerId` (lines 1320, 1363), preserving client stability while native lookup validation is underway.
    - Staging point is clearly identified at line 1320: `ExperimentalAiRoleService.ResolveParameterId(assignment.Profile, bot.KickerId)` will be activated once native lookup is confirmed.
    - Gym mode strictly isolated: `GymAiParameterBase + profile.KickerId` (`100 + kickerId`) is always assigned to gym bots regardless of experimental flags.
- **Transport Serialization:**
  - `BattleMatchmakingService.cs:914-936` (`SendAssignmentUpdateAsync`):
    - Serializes `MatchingBattleInfo` to JSON -> parsed to protobuf `Struct` -> transmitted in `GetAssignmentsResponse.Assignment.Properties["battle"]` / `["RoomBattleInfo"]`.
    - `MatchingPlayerBattleInfo.kickerAiParameterId` (int) is mapped directly into client `PlayerCharacter._kickerAiParameterId` (`this+0xC4`).
- **Master Serving:**
  - `src/KickFlight.BootstrapApi/DemoSessionApi.cs:602-609` (`InitializeMasters`):
    - Encrypts `config/masters_kicker_ai_parameter.json` into `_encryptedMasters["KickerAiParameter"]`.
    - Always appends gym rows via `AppendGymAiRows` (lines 865-895, IDs 101..114).
    - When `KF_EXPERIMENTAL_AI_ROLES=1` is enabled, calls `ExperimentalAiRoleService.AppendExperimentalRoleAiRows` to serve cloned rows for experimental IDs (15..27) with `rank = 13` and tuned motivations/intervals. When disabled (default), only standard rows and gym rows are served.

## 2. Verified Role Values, Mode Policies, & Protocol Distinctions

- **Existing Modes & Rules:**
  - `BattleCompositionMode.CrystalScramble`: Rules 1, 5, 6, 7. Objective: Collect crystals, deposit at team guardian; enemy guardian destructible.
  - `BattleCompositionMode.FlagFlight`: Rules 2, 8. Protocol distinction: `guardianAmount = 0` (no guardians exist). 2 flags total (1 per team pedestal); carriers fly through to capture, deliver to own base (`PlayerStateFlagStand`). No invented flag rules.
  - `BattleCompositionMode.RapidBall`: Rules 3, 4. Protocol distinction: 1 `NPCRevivalableGuardian` per goal (`guardianAmount = 1`, 6 HP steps, 15s revive). Goals locked until enemy guardian downed; balls (`it_003..005`) collected and scored into open goal.
- **Preserved 8 Specialized Profiles (No Six-Bucket Collapse):**
  - Roles must follow the actual selected profile (`assignment.Profile`), NOT inferred kicker alone.
  - The 8 specialized profiles from `config/scramble_bot_disc_decks.json` are fully preserved:
    1. `carrier`
    2. `tank_defender`
    3. `hunter`
    4. `objective_attacker`
    5. `healer`
    6. `speed_support`
    7. `damage_support`
    8. `trap_defender`

## 3. Behavioral Role & Mode Mappings

- **Carrier (`carrier`):**
  - Crystal Scramble: Collects crystals; deposits at friendly guardian when crystals >= deposit threshold (`scoreToDeposit`, evaluated natively by `AIScrambleScoreEvaluationEngine`); Ana (Kicker 8) executes evasive escape maneuvers.
  - Flag Flight: Flies directly to designated flag pedestal/carrier, captures, and flies evasively back to own base (`PlayerStateFlagStand`).
  - Rapid Ball: Gathers scattered balls; scores only after enemy guardian is downed and goal opens.
- **Tank Defender (`tank_defender`):**
  - Crystal Scramble: Engages approaching enemy attackers using threat-based self-defense (`AIDefensekEvaluationEngine` evaluates threat within attack range; native engine has no base/guardian anchor).
  - Flag Flight: Guards friendly flag pedestal and escorts friendly carrier.
  - Rapid Ball: Guards friendly guardian to keep own goal locked.
  - Specialization: Coco (3), Tenka (11), Buzzy Big (12).
- **Hunter (`hunter`):**
  - Crystal Scramble: Eliminates enemy carriers and collectors.
  - Flag Flight: Intercepts and eliminates enemy flag runners.
  - Rapid Ball: Eliminates enemy defenders and ball collectors.
  - Specialization: Kite (4), Kaito (6), Pit (13).
- **Objective Attacker (`objective_attacker`):**
  - Crystal Scramble: Targets enemy guardian to reduce HP, disrupt crystal deposits, and apply forward pressure.
  - Flag Flight: Pressures enemy base defenders and clears lane to flag stand.
  - Rapid Ball: Focuses primary damage on enemy guardian to down it and unlock the scoring goal.
  - **Specialization (Yuyan, Kicker 10):** User requires Yuyan `objective_attacker` targeting enemy guardian, not hunter defending guardian.
- **Healer (`healer`):**
  - All modes: Sustains self under damage threshold via `healDamageRate` (evaluated via `SkillHealRangeValidator` on caster `HpRate`; candidate pool teammate exclusion alone does not prove all support discs only affect self, as single-target caster buffs vs. potential AoE ally collision effects differ, and ally selection remains unresolved).
  - **Specialization (Ruriha, Kicker 2):** Dedicated high personal sustain to contest objectives longer.
- **Speed Support (`speed_support`):**
  - All modes: Applies ranged harassment and grants mobility buffs to allies.
  - **Specialization (Grenhawk, Kicker 7):** Ranged poke and team speed buffing.
- **Damage Support (`damage_support`):**
  - All modes: Escorts friendly carriers and boosts allied offensive damage.
  - **Specialization (Sid, Kicker 14):** Amplifies allied burst damage on objectives.
- **Trap Defender (`trap_defender`):**
  - Crystal Scramble: Deploys area denial, smog, and traps around disputed chokepoints and transit lanes.
  - Flag Flight: Places traps along flag routes and base approach paths.
  - Rapid Ball: Places traps around friendly guardian and goal perimeter.
  - **Specialization (Owlbert, Kicker 5):** Area denial lockdown.

## 4. Deterministic Bounded Parameter-ID Contract (15..99)

- **Strict Compatibility Bounds:**
  - `PlayerCharacter.UpdateAi` cave at `0x13BEAEC` enforces: `id >= 100` OR `id == -1` -> sets `AIOption = 63` (`Mannequin`).
  - Normal role IDs must stay strictly within `15 <= id <= 99`.
  - Base kickers occupy `1..14`.
  - Gym bots occupy `101..114` (`100 + kickerId`), fully isolated.
  - Candidate range `15..99` is reserved conditionally, pending native confirmation that `get_Item` supports non-kicker row indexing up to 99.
- **Deterministic Unique ID Allocations:**
  - Base Profile IDs:
    - `15`: `carrier`
    - `16`: `tank_defender`
    - `17`: `hunter`
    - `18`: `objective_attacker`
    - `19`: `healer`
    - `20`: `speed_support`
    - `21`: `damage_support`
    - `22`: `trap_defender`
  - Specialized Kicker Profile IDs:
    - `23`: Yuyan (10) as `objective_attacker`
    - `24`: Owlbert (5) as `trap_defender`
    - `25`: Ruriha (2) as `healer`
    - `26`: Grenhawk (7) as `speed_support`
    - `27`: Sid (14) as `damage_support`
  - All allocated IDs (15..27) satisfy: `15 <= id <= 99` and `id < 100`.
- **Opt-in Guard & Staging:**
  - Disabled by default via `KF_EXPERIMENTAL_AI_ROLES=0` (or absent env).
  - When disabled: live roster and `ResolveParameterId` return base `kickerId`. Master serving serves only standard 14 rows + 14 gym rows.
  - Live roster assignment in `BattleMatchmakingService.cs:1320` is explicitly held at `kickerAiParameterId = bot.KickerId` until native lookup verification is reported complete.

## 5. Verification & Tests

- `tests/KickFlight.BootstrapApi.Tests/ExperimentalAiRoleTests.cs`:
  - Flag disabled default behavior: returns base kicker IDs.
  - Full profile coverage: all 8 profiles resolve to bounded IDs (15..22).
  - Mode policy coverage: Scramble, Flag Flight, and Rapid Ball policies populated for all profiles.
  - Specialization coverage: Yuyan (objective_attacker targeting enemy guardian), Owlbert, Ruriha, Grenhawk, Sid.
  - Bounded unique IDs: all IDs in [15..99], no duplicates, strictly < 100.
  - Gym isolation: gym bots always receive 100+kickerId, unaffected by role flag.
  - Master cloning: `AppendExperimentalRoleAiRows` generates valid JSON rows with rank 13.
- `tests/KickFlight.BootstrapApi.Tests/ModeAwareCompositionTests.cs`: validates mode rules & bot selection partitions across all 3 modes.
- `tests/KickFlight.BootstrapApi.Tests/BattleMatchmakingTests.cs`: validates roster building, gym isolation, and live parameter ID transport.
