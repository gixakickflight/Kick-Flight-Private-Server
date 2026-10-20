# AI Role Native Semantics & Cave Audit Evidence

Status: static native disassembly audit of active ARM64 `libil2cpp-2.11.3.so` (SHA-256 `04d48df3fe04c23a5f17958faa96dca8f1f83cb661c10176f02cabe6ee500e2d`) using Capstone and `symbols.pkl`. Reproducible script: `scripts/re/audit_ai_roles.py`.

## 1. UpdateAi Cave (0x159D200) Continuation & Occupancy
- **Hook site:** `PlayerCharacter.UpdateAi` (0x13BEAEC) patched to `b 0x159D200`.
- **Occupied range:** `0x159D200` .. `0x159D3D0` (468 bytes active).
- **Control flow:**
  - `0x159D200`: `ldrb w8, [x0, #0xe0]` (`_enableAi`). If 0, branches to `0x159D3CC`.
  - `0x159D208`: `ldr w8, [x0, #0xc4]` (`_kickerAiParameterId`).
  - `0x159D20C`: `cmn w8, #1` (checks if -1). If equal, jumps to gym mannequin setup at `0x159D21C`.
  - `0x159D214`: `cmp w8, #0x64` (100). If `>= 100`, sets `AIOption = 0x3F` (63) and branches to `0x159D3CC`.
  - `0x159D228`: Normal bot special skill automation routine. If state 13 or casting special, returns via `ret` (`0x159D3B8`).
  - `0x159D3CC`: Displaced prologue instruction `stp x20, x19, [sp, #-0x20]!`.
  - `0x159D3D0`: `b 0x13BEAF0` (clean resumption at `PlayerCharacter.UpdateAi + 4`).
- **Dead body occupancy:** Lives in `HomeSummonModelController.SetModel` (`0x159D1BC` .. `0x159DAB0`, stubbed with `ret` at `0x159D1BC`). Unused padding at `0x159D3D4` has only 12 bytes before `0x159D3E0` (occupied by prod `GetDisplayAngles` cave). Contiguous free window is `0x159D410` .. `0x159DA44` (1,588 free bytes).

## 2. KickerAiParameter Lookup & ID Range 15..99
- **Lookup RVA:** `PlayerCharacter.GetKickerAIParameterMaster` (0x13C3260).
- **Instructions:**
  - `0x13C3298`: `ldr w8, [x19, #0xc4]` (loads `_kickerAiParameterId`).
  - `0x13C329C`: `cmn w8, #1` (fallback check). If -1, branches to `0x13C3308`.
  - `0x13C32C8`: calls `MasterManager.get_KickerAiParameterMaster()` (0x1799C18).
  - `0x13C32E8`: `mov w1, w20` (passes parameter ID).
  - `0x13C32F0`: `bl 0x1A30A6C` (`TMasterBase<object>$$get_Item`).
  - `0x13C32F4`: `cbz x0, #0x13C3308` (if null, falls back to `GetKickerAIParameterMaster(characterId, rank)` at `0x13C34FC`).
  - `0x13C3304`: `ret` (returns row directly if found).
- **Validation:** No upper-bound check exists. Any row ID in range 15..99 present in `masters_kicker_ai_parameter.json` is directly returned and accepted. Because the cave at `0x159D200` only triggers mannequin logic for `>= 100`, IDs 15..99 execute full normal AI logic.

## 3. Motivation Consumers & ScoreToDeposit Formula
- **Getter RVAs:**
  - `get_AttackMotivation` (`0x1528344`), `get_DefenseMotivation` (`0x1528354`), `get_ScoreMotivation` (`0x1528364`), `get_ScoreToDeposit` (`0x1528468`).
- **Field Offsets in `PlayerCharacter`:**
  - `+0xC4`: `_kickerAiParameterId` (int)
  - `+0xC8`: `_aiParameterMaster` (`KickerAiParameterMasterData`)
  - `+0xE0`: `_enableAi` (bool)
  - `+0xE4`: `_aiOption` (enum bitmask)
- **ScoreToDeposit Consumer:**
  - `0x1805440`: `AIPlayerEngine.ApplyAIHelper` reads `ownerPlayer._aiParameterMaster` (`0x180545C`), calls `get_ScoreToDeposit` (`0x1805474`), and stores to `AIHelper + 0x20` (`0x1805488`).
  - `0x1808D40`: `AIScrambleScoreEvaluationEngine.CalcScore` loads `[x19, #0x20]`.
  - `0x1808D4C`: `fcmp s0, s1` (current carried crystals `s0` vs `ScoreToDeposit` `s1`).
  - `0x1808D54`: `fcsel s0, s0, s2, lt` (if `s0 < s1`, multiplier is 0.0; if `s0 >= s1`, multiplier is 4.0).
- **Motivation Consumer:**
  - `ISearchTarget.get_TargetAttackMotivation` / `Defense` / `Score` queried per target.
  - `PlayerCharacter` tail-calls master getters (`0x13BE278`, `0x13BE31C`, `0x13BE3C0 -> 0x1528364`).
  - `NPCGuardian` (`0x1469A5C`), `Flag` (`0x15ED638`), `Crystal` (`0x17CC658`), `Ball` (`0x320797C`).

## 4. Target Selection & Behavioral Decisions
- **Target Selection:** `AIPlayerEngine.UpdateTarget` (0x180714C):
  - `0x180746C`: calls `AIEvaluationEngine.GetPurpose()` (`blr x9`).
  - `0x1807474`: `str w0, [x19, #0x7c]` (sets behavior purpose enum).
  - `0x1807480`: `str s8, [x19, #0x78]` (records winning motivation score).
  - `0x1807484`: calls `AIPlayerEngine.SetCurrentTarget(this, target)` (`0x1806824`).
- **Behavior Execution:** `AIPlayerEngine.PerformBehavior` (0x1805C30):
  - `0x1805CB4`: `ldr w8, [x19, #0x7c]`
  - `w8 == 0`: branches to `PerformAttack` (0x180643C).
  - `w8 == 1`: branches to `PerformDefesce` (0x180658C).
  - `w8 == 2`: branches to `PerformGetScore` (0x18066D4).
- **Disc / Skill Filtering:**
  - `PlayerCharacter.SelectAISkill` (`0x13C4750`) calls `JudgeUseAISkill` (`0x13C4F70`).
  - `0x13C4FA4`: checks `SkillParameterBase.GetSkillCategoryType`.
  - `0x13C4FD0`: validates bitmask against `AIOption` (+0xE4).
  - `0x13C503C`: branches to `PlayerCharacter.JudgeAISkillRange` (`0x13C51EC`).
  - `0x13C5268`: queries `SkillRangeValidator` dictionary at `this + 0x130`. If absent, defaults to true (`0x13C52C4: mov w0, #1`).

## 5. Feasibility Plan for Guarded Changes
1. **Phase 1 (Server Master Configuration - Zero APK changes):**
   - Define role-specific `KickerAiParameter` rows in `config/masters_kicker_ai_parameter.json` within ID range 15..99 (e.g. 15=Carrier `scoreToDeposit=5, scoreMotivation=100`, 16=Defender `defenseMotivation=100`, 17=Hunter `attackMotivation=100`).
   - Assign `kickerAiParameterId` dynamically in `BattleMatchmakingService.cs` from composition role type instead of raw `kickerId`.
   - Verified non-breaking: client dictionary lookup and cave boundary natively accept 15..99 without clipping or crash.
2. **Phase 2 (Guarded APK Patch - Only if hard target override needed):**
   - If motivation weights alone do not force guardian/flag prioritization, hook `AIPlayerEngine.UpdateTarget` (0x1807474) or `PerformBehavior` (0x1805CB4).
   - Candidate pool filtering: `AIGameHelper.CollectPlayers` (`0x1802914`) excludes teammates from the target candidate pool. However, candidate pool teammate exclusion alone does NOT prove that all support discs only heal or affect self:
     - Self-heal evaluation: `SkillHealRangeValidator.Create` (`0x1840E60`) passes the bot caster itself (`this.m_playerCharacter`) and `IsInRange` (`0x1840FFC`) tests `m_playerCharacter.get_HpRate()` against threshold.
     - Single-target caster buffs pass `this` to `ConditionInitializeInfo`.
     - Disc skills with area colliders (`OverlapSphere`) evaluate entities inside the physical collider against collision layer masks, which can potentially encompass allies via AoE collision effects.
     - Ally selection logic remains unresolved; do not assume all support heals self solely because the candidate pool excludes allies.

