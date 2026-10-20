# AI Role Native Semantics & Proposed Master Contract

Status: Verified through static disassembly of `libil2cpp-2.11.3.so` (SHA-256 `04d48df3fe04c23a5f17958faa96dca8f1f83cb661c10176f02cabe6ee500e2d`) and pristine `base.apk` (SHA-256 `ffa620d2e2f905e729812606024e02741124951dce908bb2e0587f0a4e16e031`).

---

## 1. Verified Native AIEvaluationEngine Caller Semantics

### A. Motivation Provider vs. Consumer Semantics
- **Target Motivation Implementations:**
  - `NPCGuardian.get_TargetAttackMotivation` (`0x1469A5C`) -> `mov w0, wzr; ret` (0)
  - `NPCGuardian.get_TargetDefenseMotivation` (`0x1469A64`) -> `mov w0, wzr; ret` (0)
  - `NPCGuardian.get_TargetScoreMotivation` (`0x1469A6C`) -> `mov w0, wzr; ret` (0)
  - `Flag.get_Target*Motivation` (`0x15ED638`, `0x15ED640`, `0x15ED648`) -> all return 0
  - `Crystal.get_Target*Motivation` (`0x17CC658`, `0x17CC660`, `0x17CC668`) -> all return 0
  - `Ball.get_Target*Motivation` (`0x320797C`, `0x3207984`, `0x320798C`) -> all return 0
- **Evaluator Motivation Implementation:**
  - `PlayerCharacter.get_TargetAttackMotivation` (`0x13BE278`) loads `this._aiParameterMaster` (`+0xC8`) and calls `KickerAiParameterMasterData.get_AttackMotivation()`.
  - `PlayerCharacter.get_TargetDefenseMotivation` (`0x13BE31C`) calls `KickerAiParameterMasterData.get_DefenseMotivation()`.
  - `PlayerCharacter.get_TargetScoreMotivation` (`0x13BE3C0`) calls `KickerAiParameterMasterData.get_ScoreMotivation()`.
- **Evaluation Formula (`AIEvaluationEngine.Evaluate` @ `0x1801138`):**
  - Calls `GetMotivation()` on the evaluator (`_evaluator` = the bot `PlayerCharacter`).
  - Calls `CalcScore(target)`.
  - Final score: `score = (float)Motivation * CalcScore(target) * direction/hit factors`.
  - Motivation weights directly govern purpose selection in `UpdateTarget` (`0x180746C`).

### B. Defense vs. Guardian Protection (Evidence-Based Demarcation)
- **AIDefensekEvaluationEngine.CalcScore (`0x17FFE20`):**
  1. Calls `CheckAttribute(target, AITargetAttribute.Attacker)` (`1`). If false, returns 0.0.
  2. Compares `evaluator.TargetTeamType` with `target.TargetTeamType` (`0x17FFF84: cmp w21, w0; b.eq return_zero`). If same team, returns 0.0.
  3. Loads `target._distance` (`[x20, #0x24]`) and compares with `target.TargetAttackRange` (`fcmp s8, s0`).
  4. If `distance <= attackRange`, returns score 1.0; otherwise evaluates `EvaluateByNearDistance`.
- **Verdict:** Native "Defense" has **zero** base or guardian protection logic. It is purely personal threat self-defense (engaging enemy attackers within or near weapon attack range).

### C. Teammate Filtering & Ally Healing / Buff Paths
- **AIGameHelper.CollectPlayers (`0x1802844`):**
  - Iterates player list; calls `player.get_TeamType()` (`0x1802908`).
  - Compares with bot team: `0x1802914: cmp w0, w19; b.eq #0x1802948` (skips teammates).
  - Teammates are **never** included in `SearchTargetInfo`.
- **Ally Healing / Buffs:**
  - `JudgeAISkillRange` (`0x13C51EC`) validates ranges. Self-heal execution is governed by `SkillHealRangeValidator` testing the caster's own HP rate, while single-target buffs apply to the caster; potential AoE ally collision effects (`OverlapSphere`) may still encompass nearby teammates, but explicit ally selection remains unresolved. Do not assume all support discs only affect self solely because the candidate pool excludes allies.

### D. Mode-Specific Natural Logic & Goal Locks
- **Scramble (`AIScrambleScoreEvaluationEngine` @ `0x1808D40`):**
  - Reads `ScoreToDeposit` from `KickerAiParameterMasterData` (`[x19, #0x20]`).
  - If carried crystals `s0 >= ScoreToDeposit`, score multiplier is 4.0; if `< ScoreToDeposit`, multiplier is 0.0.
- **Flag Battle (`AIFlagBattleGameHelper.IsExclusiveScoreMotivation` @ `0x1801AAC`):**
  - Checks if `ScoreMotivation > AttackMotivation && ScoreMotivation > DefenseMotivation`.
  - When true and `ScoreMotivation > threshold (0x48)`, locks the bot into exclusive flag scoring motivation.
- **Rapid Ball (`AIBallShootGameHelper.CheckExclusiveGoalMode` @ `0x17FE268`):**
  - Manages ball possession and exclusive goal-scoring motivation state.

---

## 2. Cave Safety & Occupancy Audit (0x159D3D4 Trap Debunked)

- **Incoming Branch Map into `HomeSummonModelController.SetModel` (`0x159D1BC..0x159DAB0`):**
  - `0x1552D84 -> 0x159D1BC`: original caller, hits `ret` immediately.
  - `0x13BEAEC -> 0x159D200`: `PlayerCharacter.UpdateAi` cave (`0x159D200..0x159D3D0`, 468 bytes active).
  - `0x13BC3A8 -> 0x159D3E0`: **Active Production Cave for `PlayerBoneController.GetDisplayAngles`!**
    - Occupies `0x159D3E0..0x159D408`.
    - **CRITICAL:** `0x159D3D4` has only 12 unused bytes before colliding with `0x159D3E0`. Any cave placed at `0x159D3D4` corrupts `GetDisplayAngles` and crashes basic attack direction calculations!
  - `0x1439AEC -> 0x159DA48`: Jay bomb (`skill_40001`) `HitInfo` cave (`0x159DA48..0x159DA78`).
- **Verified Free Cave Window:**
  - Range: `0x159D410 .. 0x159DA44` (1,588 contiguous free bytes in dead body, zero incoming branches from outside).

---

## 3. Server-Authoritative ID Contract: 13 Profiles x 3 Modes (IDs 15..53)

Replaced conflicting native proposed 8-profile contract with server-authoritative contract defined in `docs/re-notes/ai-role-id-contract.md`:
$$\\text{ParameterId} = 15 + (13 \\times \\text{modeIndex}) + \\text{profileIndex}$$

- **Range:** `15..53` (Mode 0: Scramble 15..27, Mode 1: Flag 28..40, Mode 2: Ball 41..53).
- **Profiles:**
  - 0: `carrier`, 1: `tank_defender`, 2: `hunter`, 3: `objective_attacker`
  - 4: `healer`, 5: `speed_support`, 6: `damage_support`, 7: `trap_defender`
  - 8: `Yuyan` (Spec 10), 9: `Owlbert` (Spec 5), 10: `Ruriha` (Spec 2), 11: `Grenhawk` (Spec 7), 12: `Sid` (Spec 14)

---

## 4. Real Heal/Buff Recipient Call Traces & Semantics

- **Observation on Teammate Exclusion:**
  - `AIGameHelper.CollectPlayers` (`0x1802844`) excludes teammates from `SearchTargetInfo`.
  - **Critical Distinction:** Same-team candidate exclusion alone does NOT prove all heal/buff skills are final self-casts.
  - **Skill Evaluation:** `SkillHealRangeValidator.Create` (`0x1840E60`) passes the bot caster itself (`this.m_playerCharacter`), and `IsInRange` (`0x1840FFC`) tests `m_playerCharacter.get_HpRate()` against threshold.
  - **Skill Execution:** While single-target self-buffs and heals pass `this` to `ConditionInitializeInfo`, disc skills with area colliders (`OverlapSphere`) evaluate entities inside the physical collider against collision layer masks, which can encompass allies if configured for team broadcast.

---

## 5. Implementation Strategy & Guarded Native Patch

1. **Parameter Master Delivery (Proven Baseline):**
   - Server master delivery of IDs 15..53 accepted by native lookup without bounds clipping.
2. **Guarded Native Patch (`KF_AI_ROLES=1`, Default OFF):**
   - Hook site: `AIPlayerEngine.UpdateTarget` (`0x1807474`, 4-byte hook `b 0x159D410`).
   - Cave location: `0x159D410` (inside verified free window `0x159D410..0x159DA44`, 1,588 bytes, 0 incoming branches).
   - Preserves all registers (`x19..x28`, `x29, x30`, `d8..d9`, `NZCV`).
   - Telemetry probes (tag `KFDIAG`):
     - `7500`: Telemetry pulse
     - `7510 + paramId`: Bot profile/mode (IDs 15..53)
     - `7520 + purpose`: Purpose decision (0=Attack, 1=Defense, 2=Score)
     - `7530 + targetClass`: Target class (1=Player, 2=Guardian, 3=Other, 0=None)
     - `7540 + targetTeam`: Target team (0 or 1)
     - `7550 + score`: Evaluated score (clamped 0..49)
     - `7580`: Scramble hunter vs guardian preference audit
     - `7581`: Scramble objective attacker guardian preference audit
