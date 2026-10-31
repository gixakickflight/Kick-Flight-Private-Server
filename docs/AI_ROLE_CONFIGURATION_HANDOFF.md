# Task Handoff & Actionable Prompt: Server-Managed Bot Behavior Configuration

## Objective
Implement a **server-managed, hot-reloadable JSON configuration system** for bot AI parameters per kicker and game mode in `KickFlight.BootstrapApi`. This system will expose native AI parameters (motivation weights, deposit thresholds, low-HP triggers, dash/skill intervals, lock-on) directly via config files instead of requiring binary APK rebuilds or hard-coded C# policy dictionaries. 

> [!IMPORTANT]
> **Preservation & Scope Rules:**
> - **DO NOT implement a new bot framework or client physics simulator in this iteration.**
> - **DO NOT touch** APK build scripts, news files (`docs/updates-page.html`, `deploy/site/updates-2.11.4.html`), or Docker Compose files (`docker-compose*.yml`), as another worker is actively finalizing APK builds.
> - Full server-authoritative movement/skills/targeting requires in-depth Photon/authority investigation and a dedicated client executor; do not assume the server can directly manipulate bot physics transforms over standard Photon UDP.
> - All work must be PR-only; preserve existing worktrees and dirty files.

---

## 1. Verified Architecture & Evidence Base (`docs/re-notes/`)

### A. The 39-Profile ID Contract (`ai-role-id-contract.md`)
- **Native Contract:** `PlayerCharacter.GetKickerAIParameterMaster` (`0x13C3260`) indexes rows via `TMasterBase<object>$$get_Item` (`0x1A30A6C`) without upper clipping for standard IDs. IDs `15..99` execute standard bot AI logic.
- **Gym Isolation:** IDs `>= 100` (`GymAiParameterBase + kickerId`) set `AIOption = 0x3F` (Mannequin). **This boundary MUST be strictly preserved.**
- **Formula:**
  $$\text{ParameterId} = 15 + (13 \times \text{modeIndex}) + \text{profileIndex}$$
  - `modeIndex` $\in [0..2]$:
    - `0`: Crystal Scramble (`battleRuleId` 1, 5, 6, 7)
    - `1`: Flag Flight (`battleRuleId` 2, 8) — *No guardians exist in this mode (`guardianAmount = 0`).*
    - `2`: Rapid Ball (`battleRuleId` 3, 4) — *1 `NPCRevivalableGuardian` per goal.*
  - `profileIndex` $\in [0..12]$:
    - `0`: `carrier` (Base Kicker 8 / Ana)
    - `1`: `tank_defender` (Base Kicker 3 / Coco)
    - `2`: `hunter` (Base Kicker 4 / Jay)
    - `3`: `objective_attacker` (Base Kicker 10 / Yuyan)
    - `4`: `healer` (Base Kicker 2 / Ruriha)
    - `5`: `speed_support` (Base Kicker 7 / Grenhawk)
    - `6`: `damage_support` (Base Kicker 14 / Sid)
    - `7`: `trap_defender` (Base Kicker 5 / Owlbert)
    - `8`: `Yuyan` specialization (Kicker 10 as `objective_attacker`)
    - `9`: `Owlbert` specialization (Kicker 5 as `trap_defender`)
    - `10`: `Ruriha` specialization (Kicker 2 as `healer`)
    - `11`: `Grenhawk` specialization (Kicker 7 as `speed_support`)
    - `12`: `Sid` specialization (Kicker 14 as `damage_support`)
  - **ID Range:** Strictly `15..53` ($15 \le \text{ID} \le 53 < 100$).

### B. Master Row Schema & Transmission (`ai-role-server-design.md`, `ai-role-master-controls.md`)
- `masters_kicker_ai_parameter.json` fields served via `DemoSessionApi`:
  - `id` (int, 15..53)
  - `kickerId` (int, 1..14)
  - `kickerCostumeId` (int, e.g. 2080101)
  - `rank` (int, exactly `13`; `GetKickerAIParameterMaster` picks rows where `row.rank <= player.rank`)
  - `attackMotivation` (int, 0..100)
  - `defenseMotivation` (int, 0..100)
  - `scoreMotivation` (int, 0..100)
  - `scoreToDeposit` (int, crystals needed to trigger guardian deposit)
  - Intervals: `attackDashInterval`, `defenseDashInterval`, `scoreDashInterval`, `attackSkillInterval`, `healSkillInterval`, `buffSkillInterval`, `trapSkillInterval`, `warpSkillInterval`
  - Heuristics: `canLockon`, `healDamageRate`, `negativeCorrection`, `positiveCorrection`, `minMeasureAccuracy`, `maxMeasureAccuracy`, `changeAttributeInterval`
- **Roster Binding:** Transmitted in `MatchingPlayerBattleInfo.kickerAiParameterId` inside `MatchingBattleInfo` protobuf struct. Client copies this directly to `PlayerCharacter._kickerAiParameterId` (`this + 0xC4`).

### C. Native Evaluation & Cave Hooks (`ai-role-targeting-implementation.md`, `ai-role-low-hp-deposit.md`)
- **Target Selection:** Evaluated in `AIPlayerEngine.UpdateTarget` (`0x180714C`).
  - Evaluator check: `0x74414941` (`"AIAt"`, `AIAttackEvaluationEngine`).
  - Target checks: `0x79616C50` (`"Play"`, `PlayerCharacter`), `0x4743504E` (`"NPCG"`, `NPCGuardian`).
  - Hook at `0x18072D0` routes to targeting cave (`0x159D6E0..0x159D854`), weighting Hunter (17) 16:1 toward players and Objective Attacker (18, 23) 16:1 toward guardians.
- **Deposit Threshold & Low HP:** Evaluated in `AIScrambleScoreEvaluationEngine.CalcScore` (`0x1808A70`).
  - `ISearchTarget.get_TargetScore()` called at `0x1808D3C` (`s0`).
  - Integer threshold `_scoreToDeposit` loaded at `0x1808D40` (`s1`).
  - Hook at `0x1808D40` routes to deposit cave (`0x159D854..0x159D9F0`), querying `PlayerCharacter.get_TargetDamageRate` (`0x13BE18C`). If `damageRate >= 0.65` (HP $\le 35\%$) and `carriedScore > 0`, forces threshold to 1.
  - Native code strictly rejects enemy guardians (`evaluator.Team != target.Team -> 0.0f`).
- **Cave Coexistence Safety:** `KF_AI_ROLES=1` (telemetry), `KF_AI_ROLE_TARGETING=1` (weighting), and `KF_AI_ROLE_DEPOSIT=1` (low-HP deposit) reside in `HomeSummonModelController.SetModel` dead body (`0x159D410..0x159DA44`, 1,588 B free). They are strictly mutually exclusive with `KF_DIAG=1`.

---

## 2. Confirmed vs. Unknown Boundaries

| Capability / Behavior | Status | Evidence & Constraints |
| :--- | :---: | :--- |
| **Server Master Row Generation (15..53)** | **CONFIRMED** | Proven in `ExperimentalAiRoleService.cs` & `DemoSessionApi.cs`; verified by 16 unit tests. |
| **Roster ID Transport to Client** | **CONFIRMED** | `BattleMatchmakingService.BuildRoster` writes `kickerAiParameterId`; client receives via protobuf struct. |
| **Gym Isolation (`>= 100`)** | **CONFIRMED** | `0x13BEAEC` enforces `AIOption = 63` (`Mannequin`) for any ID $\ge 100$. |
| **Native Scramble Hunter/Objective Weighting** | **CONFIRMED** | Cave at `0x159D6E0` scales `s0` candidate float score by 4.0 / 0.25 based on ID 17, 18, 23. |
| **Native Scramble Low-HP Base Deposit** | **CONFIRMED** | Cave at `0x159D854` clamps deposit threshold to 1 when `damageRate >= 0.65`. |
| **Flag Flight No-Guardian Constraint** | **CONFIRMED** | Protocol has `guardianAmount = 0`; no deposit/guardian defense logic permitted. |
| **Native Consumer of Intervals over Network** | **UNCONFIRMED** | Master fields exist, but exact tick/rate consumer in Photon multiplayer is unverified. |
| **Server-Authoritative Movement / Aim / Skills** | **UNKNOWN / BLOCKED** | Client executes Unity character controller locally. Server does not run physics or send direct position updates. |
| **Native Hooks for Flag Flight / Rapid Ball** | **UNCONFIRMED** | `AIFlagBattleGameHelper` and `AIBallShootGameHelper` entry points not yet hooked by caves. |
| **Ally Escort & Support Targeting** | **UNCONFIRMED** | Stock `CalcScore` returns `0.0` for teammates; ally buff/heal targeting requires custom evaluator bypass. |

---

## 3. Server-Managed Configuration Architecture

### A. Configuration File Location & JSON Schema
Create `config/bot_ai_profiles.json` (with standalone deployment link in `.csproj`):

```json
{
  "$schema": "./bot_ai_profiles.schema.json",
  "version": 1,
  "profiles": [
    {
      "profile": "carrier",
      "mode": "CrystalScramble",
      "parameterId": 15,
      "kickerId": 8,
      "kickerCostumeId": 2080101,
      "rank": 13,
      "motivations": {
        "attack": 30,
        "defense": 30,
        "score": 90
      },
      "scoreToDeposit": 5,
      "lowHpDepositThreshold": 0.35,
      "dashIntervals": {
        "attack": 1.0,
        "defense": 0.8,
        "score": 0.8
      },
      "skillIntervals": {
        "attack": 4.0,
        "heal": 3.0,
        "buff": 4.0,
        "trap": 5.0,
        "warp": 4.0
      },
      "healDamageRate": 0.5,
      "canLockon": true
    }
  ]
}
```

### B. Validation Rules
1. `version`: Positive integer.
2. `profiles`: Array of exactly 39 profiles covering modes `[CrystalScramble, FlagFlight, RapidBall]` and 13 profile keys.
3. `parameterId`: Must be unique and strictly in range `[15..53]`. Any ID $\ge 100$ must be rejected (reserved for gym).
4. `motivations`: Integers clamped between `[0..100]`.
5. `scoreToDeposit`: Integer $\ge 1$.
6. `rank`: Must equal `13`.
7. `lowHpDepositThreshold`: Float in `[0.0..1.0]`.

### C. Hot-Reload & Thread Safety
- Implement an `IOptionsMonitor` or `FileSystemWatcher` pattern with debouncing (`250ms`).
- Atomic reference swap: On reload, validate the complete JSON file into an immutable `FrozenDictionary<(int ModeIndex, int ProfileIndex), AiRolePolicy>`.
- If validation fails (schema mismatch, missing profile, ID out of range): log an error and **retain the active valid configuration** (zero downtime / no crash).

### D. Version Negotiation & Fallback
- If `config/bot_ai_profiles.json` is missing or invalid on startup: Fall back to hard-coded policies in `ExperimentalAiRoleService.cs`.
- If `KF_EXPERIMENTAL_AI_ROLES` is OFF: Matchmaking assigns pristine `bot.KickerId` (IDs 1..14), ensuring backward compatibility with older clients or unpatched builds.

---

## 4. Implementation Milestones

### Milestone 1: Schema Definition & Deserializer (`config/`, `src/KickFlight.BootstrapApi/`)
- Define C# records: `BotAiConfigurationRoot`, `BotAiProfileConfig`, `BotAiMotivations`, `BotAiDashIntervals`, `BotAiSkillIntervals`.
- Create `BotAiConfigurationValidator` with strict bounds checking.
- Add unit tests verifying schema adherence, duplicate ID rejection, and range checks.

### Milestone 2: Hot-Reload Service (`BotAiProfileProvider`)
- Implement `IBotAiProfileProvider` with file watcher and atomic update.
- Ensure resolution walk (`RepositoryPaths.Resolve`) handles execution from root, `src/KickFlight.BootstrapApi`, or publish directory.
- Add tests verifying hot reload on file touch/change.

### Milestone 3: Service Integration (`ExperimentalAiRoleService.cs`, `DemoSessionApi.cs`)
- Replace static `PoliciesByModeAndProfile` dictionary with dynamic queries to `IBotAiProfileProvider`.
- Update `AppendExperimentalRoleAiRows` in `DemoSessionApi` to serve hot-reloaded parameters in master JSON.
- Ensure `BattleMatchmakingService.BuildRoster` assigns parameters dynamically.

### Milestone 4: Verification & Runtime Acceptance
- Run the 31 C# role/composition tests and 28 Python tests:
  ```bash
  dotnet test tests/KickFlight.BootstrapApi.Tests/KickFlight.BootstrapApi.Tests.csproj --filter "FullyQualifiedName~ExperimentalAiRoleTests | FullyQualifiedName~ScrambleBotCompositionTests"
  .venv/bin/python -m unittest discover -s tests -p "test_ai_role*.py"
  ```
- Verify hot-reload during active HTTP master download (`/v1/master/demo-master-...`).

---

## 5. Photon / Server Authority Investigation Notes
- **Authority Model:** In the current Kick-Flight engine, Photon runs peer-to-peer or client-authoritative simulation where `AIPlayerEngine` runs locally on each client (or on the room master client).
- **Packet Injection:** If true server-authoritative movement/skills are desired in the future, the server (via `luxonserver` or a dedicated bot runner process) must act as a full Photon Peer participating in UDP lockstep/state synchronization.
- **Next Step for Authority:** Do not attempt to emulate physics server-side without a headless client executor (`UnityPlayer` / dedicated bot peer). Continue leveraging client-side parameter tuning via `masters_kicker_ai_parameter.json` and `MatchingPlayerBattleInfo`.
