# AI Role Implementation Independent Review & Verification Audit

**Audit Target:** `/home/gixarde3/Proyectos/KickFlight/Kick-Flight-Private-Server/.local/worktrees/ai-role-investigation`  
**Branch / Commit:** `research/ai-role-patching` (`33d38a7`)  
**Scope:** Experimental role transport, 39-ID mapping, native ARM64 caves (`ai_role_cave.py`), and patch map integrity.  
**Date:** 2026-10-19  

---

## 1. Executive Summary
- **Overall Status:** Sound architecture, verified register preservation, and mathematically consistent ID contract across server and native layers.
- **Defect Identified (P2):** Missing mutual exclusivity guard in `scripts/patch-il2cpp-endpoints.py` between `KF_AI_ROLES=1` and `KF_DIAG=1`. Both patch sets compete for the SetModel dead body window (`0x159D410..0x159D854`), resulting in byte-guard collision if built concurrently.
- **Production Safety:** For standard production and offline builds (`KF_DIAG=0`), native caves in `0x159D410..0x159D854` (1,092 B) fit safely within the 1,588 B free window with zero branch or cave overlap.

---

## 2. Verification by Focus Area

### A. Mode-ID Mapping (39-ID Contract)
- **Status:** Verified (No Issues).
- **Evidence:**
  - Formula: $\text{ParameterId} = 15 + (13 \times \text{modeIndex}) + \text{profileIndex}$.
  - Mode mapping in `ExperimentalAiRoleService.cs`: Crystal Scramble (0) for rule IDs 1, 5, 6, 7; Flag Flight (1) for 2, 8; Rapid Ball (2) for 3, 4. Matches `ScrambleBotCompositionSelector.ModeForBattleRuleId` bit-for-bit.
  - IDs 15..53 cleanly partition into:
    - Scramble: 15..27
    - Flag Flight: 28..40
    - Rapid Ball: 41..53
  - All 16 unit tests in `ExperimentalAiRoleTests.cs` pass.

### B. Yuyan Override
- **Status:** Verified (No Issues).
- **Evidence:**
  - `ExperimentalAiRoleService.ResolveParameterId` and `GetPolicy` explicitly override `effectiveProfile = ProfileObjectiveAttacker` when `kickerId == 10` for Scramble (mode 0) and Rapid Ball (mode 2).
  - In Scramble, Yuyan maps to profile index 8 (specialization row 23).
  - Native caves (`ai_role_cave.py`) explicitly match both base row 18 (`objective_attacker`) and specialization row 23 (`Yuyan`) in `lbl_check_obj_attacker` and `check_target`.
  - In Flag Flight (mode 1), no guardian exists; Yuyan resolves to row 36 without guardian targeting weighting, correctly preserving mode semantics.

### C. Flags Default-Off
- **Status:** Verified (No Issues).
- **Evidence:**
  - Server: `ExperimentalAiRoleService.IsEnabled` checks `KF_EXPERIMENTAL_AI_ROLES == "1"|"true"`. When unset/off:
    - `DemoSessionApi` appends 0 rows to `KickerAiParameter`.
    - `ResolveParameterId` returns pristine `kickerId`.
  - Native: `scripts/patch-il2cpp-endpoints.py` defaults `AI_ROLES_PATCHES_ARM64 = []`. `KF_AI_ROLE_TARGETING=1` enforces `KF_AI_ROLES=1` as a strict prerequisite.
  - Pristine build dry-runs produce zero native diffs when flags are omitted.

### D. Gym Mannequin Isolation (`>= 100`)
- **Status:** Verified (No Issues).
- **Evidence:**
  - `BattleMatchmakingService.BuildRoster`: `gym ? GymAiParameterBase + profile.KickerId : ...` ensures gym bots always receive IDs $\ge 100$.
  - `ExperimentalAiRoleService.ResolveParameterId`: immediate short-circuit `if (isGym) return GymAiParameterBase + kickerId;`.
  - `AppendExperimentalRoleAiRows`: filters `id < BattleMatchmakingService.GymAiParameterBase` when cloning base rows.
  - Native telemetry cave: `cmp w23, #15; b.lt bypass; cmp w23, #53; b.gt bypass`. Gym IDs (101..114) branch to bypass immediately.
  - Native targeting cave: strictly restricted to IDs 17, 18, 23.

### E. Native Type-Prefix Discrimination
- **Status:** Verified (No Issues).
- **Evidence:**
  - Evaluator: `0x74414941` (`"AIAt"`) matches only `AIAttackEvaluationEngine`. Excludes `AIScrambleScoreEvaluationEngine`, `AIDefensekEvaluationEngine`, and others.
  - Target Entities:
    - `0x79616C50` (`"Play"`) matches `PlayerCharacter`.
    - `0x4743504E` (`"NPCG"`) matches `NPCGuardian`.
  - Endianness: 32-bit little-endian immediate loading (`movz/movk`) matches ARM64 memory layout bit-for-bit.
  - Verified against all 9 classes implementing `ISearchTarget` in `dump.cs`; no prefix collisions.

### F. Displaced Instructions
- **Status:** Verified (No Issues).
- **Evidence:**
  - Telemetry hook (`0x1807474`): replaces `str w0, [x19, #0x7c]`. Cave restores full state, executes `str w0, [x19, #0x7c]`, and branches to `RET_ADDR` (`0x1807478`).
  - Targeting hook (`0x18072D0`): replaces `ldr x1, [x26]`. Cave restores registers, executes `ldr x1, [x26]` (preserving generic type parameter `x1` for `List<T>.get_Count` call at `0x18072EC`), and branches to `0x18072D4` (`fcmp s0, s8`).

### G. Register Preservation (LR, NZCV, SIMD)
- **Status:** Verified (No Issues).
- **Evidence:**
  - Telemetry cave (`0x159D410`): stack frame `0x300`. Preserves general registers `x0..x30`, `nzcv` via `mrs/msr`, and all 32 vector registers `q0..q31`. Inner `log_probe` saves `x29, x30` on stack.
  - Targeting cave (`0x159D6E0`): stack frame `0x120`. Preserves general registers `x0..x18`, `x30`, `nzcv`, and SIMD `q1..q4`. Scaled candidate score `s0` is purposefully modified; `s8` (current best score) is untouched.
  - Neither entry hook uses `bl`; branches are direct `b` jumps, strictly preserving caller `LR` (`x30`).

### H. Effective Patch-Map Overlap & Cave Window Collision
- **Defect Identified:** `KF_DIAG=1` and `KF_AI_ROLES=1` Collision.
- **Evidence:**
  - In `scripts/patch-il2cpp-endpoints.py`, `DIAG_PATCHES_ARM64` places:
    - Probe 7800 at `0x159d420` (len 320 B)
    - Probe 6410 at `0x159d764` (len 64 B)
  - `AI_ROLES_PATCHES_ARM64` occupies:
    - Telemetry cave: `0x159D410 .. 0x159D6DC` (716 B)
    - Targeting cave: `0x159D6E0 .. 0x159D854` (372 B)
  - Windows `0x159D410..0x159D854` and `0x159d420` / `0x159d764` directly overlap!
  - `patch-il2cpp-endpoints.py` enforces mutual exclusivity between `KF_DIAG`, `KF_DAMAGE_DIAG`, and `KF_RESULT_DIAG`, but omits `KF_AI_ROLES`:
    ```python
    # Missing guard:
    if os.environ.get("KF_AI_ROLES") == "1" and os.environ.get("KF_DIAG") == "1":
        raise SystemExit("KF_AI_ROLES and KF_DIAG share dead body SetModel (0x159D410..0x159D854) and are mutually exclusive")
    ```
  - For `KF_DIAG=0` (production/offline), the entire window `0x159D410..0x159DA44` (1,588 B) has 0 external incoming branches and zero overlap with existing production caves (`0x159D1BC` ret stub, `0x159D200` UpdateAi, `0x159D3E0` GetDisplayAngles, `0x159DA48` Jay bomb).

---

## 3. Unverified Role Behaviors & Implementation Boundaries
1. **Scramble Carrier (ID 15):** Crystal collection and deposit rely purely on `scoreToDeposit = 5` in `AIScrambleScoreEvaluationEngine`. No native evasion or escape weighting cave is implemented.
2. **Scramble Tank Defender (ID 16):** Guardian escort and anchor defense are motivation-based (`defenseMotivation = 90`). Native coordinate anchoring around friendly guardians is not yet implemented.
3. **Scramble Healer (ID 19):** Sustains via `healDamageRate = 0.5`. Active ally-escort or ally-target healing relies on stock client logic; no dedicated ally-tracking cave exists.
4. **Flag Flight Roles (IDs 28..40):** Mode has no guardians. Native targeting cave specifically bypasses IDs $\notin \{17, 18, 23\}$. Carrier flag retrieval and pedestal interception rely solely on `AIFlagBattleGameHelper` and motivation parameters.
5. **Rapid Ball Roles (IDs 41..53):** Native candidate scoring weighting cave does not check IDs 41..53. Guardian downing burst and ball gathering rely solely on `AIBallShootGameHelper` and motivation parameters.
6. **Trap & Support Roles (Owlbert 22, Grenhawk 20, Sid 21):** Shorter skill intervals (`trapSkillInterval = 2.0s`, `buffSkillInterval = 2.5s`) function via master row data; area-denial placement and ally-following have no custom native targeting routines.
