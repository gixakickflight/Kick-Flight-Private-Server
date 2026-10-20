# AI Role Scramble Targeting Implementation & Native Scoring Trace

Status: Statically proven and verified via native disassembly and reproducible patch generator on ARM64 `libil2cpp-2.11.3.so` (pristine base SHA-256 `ffa620d2e2f905e729812606024e02741124951dce908bb2e0587f0a4e16e031`).
Created: 2026-10-19.

---

## 1. Native Scoring Call Site & Evaluation Architecture

### A. Location & Register Allocation
In `AIPlayerEngine.UpdateTarget` (`0x180714C`):
- `x19`: `this` (`AIPlayerEngine`). `[x19, #0x40]` = `_ownerPlayer` (`PlayerCharacter`).
- `x21`: `List<SearchTargetInfo>` (enumerated candidates).
- `w27`: Outer loop index over `_evaluators` (`[x19, #0x38]`).
- `x23`: Current `AIEvaluationEngine` under evaluation.
- `w24`: Inner loop index over candidate targets in `x21`.
- `s8`: Current highest candidate score so far.

```arm64
0x18072B8: bl #0x2ee66d0             // List<SearchTargetInfo>.get_Item(w24) -> target in x0
0x18072BC: ldr x8, [x23]             // x23 is current AIEvaluationEngine, x8 = vtable
0x18072C0: mov x1, x0                // x1 = SearchTargetInfo (candidate target)
0x18072C4: mov x0, x23               // x0 = evaluator (this)
0x18072C8: ldp x9, x2, [x8, #0x188]  // virtual call: AIEvaluationEngine.Evaluate(target)
0x18072CC: blr x9                    // returns raw float candidate score in s0
0x18072D0: ldr x1, [x26]             // [HOOK_UPDATE_TARGET_SCORING] -> b 0x159D6E0
0x18072D4: fcmp s0, s8               // candidate max comparison
0x18072D8: mov x0, x21
0x18072DC: csel x20, x23, x20, gt    // best evaluator
0x18072E0: csel w22, w24, w22, gt    // best target index
0x18072E4: fcsel s8, s0, s8, gt      // best score
```

### B. Evaluator and Target Classification
- **Evaluator Classification:**
  - Evaluator `klass = [x23]`, `name = [klass, #0x10]`.
  - `AIAttackEvaluationEngine`: starts with ASCII `'A','I','A','t'` (`0x74414941`).
  - Score (`AIScrambleScoreEvaluationEngine`) and Defense (`AIDefensekEvaluationEngine`) are preserved without weighting.
- **Candidate Target Classification:**
  - `target = List.get_Item(w24)` = `_items[w24]`.
  - `targetEntity = [target, #0x10]` (`ISearchTarget`).
  - `klass = [targetEntity]`, `name = [klass, #0x10]`.
  - `PlayerCharacter`: starts with ASCII `'P','l','a','y'` (`0x79616C50`).
  - `NPCGuardian`: starts with ASCII `'N','P','C','G'` (`0x4743504E`).

---

## 2. Targeting Cave Logic & Preservation Semantics

Hook site: `0x18072D0` (`ldr x1, [x26]` displaced).
Cave window: `0x159D6E0` .. `0x159D854` (372 bytes, inside free dead body `0x159D410`..`0x159DA44`).

1. **Positive Score Gate (`s0 > 0.0`):**
   Stock `AIAttackEvaluationEngine.CalcScore` (`0x17FD530`) returns `0.0` for friendly teammates, dead entities, or out-of-range targets. The cave inspects `fcmp s0, #0.0` and immediately bypasses if `s0 <= 0.0`, preserving stock feasibility, range, and friendly exclusion.
2. **Attack Purpose Gate:**
   Only weights when `x23` is `AIAttackEvaluationEngine` (`0x74414941`). Crystal deposit and score motivations remain governed by `AIScrambleScoreEvaluationEngine` and `ScoreToDeposit`.
3. **Bot AI & Scramble Role Gate:**
   Checks `_enableAi` (`[ownerPlayer, #0xe0]`) and `_kickerAiParameterId` (`[ownerPlayer, #0xc4]`).
   Only Scramble Hunter (`17`) and Objective Attacker (`18`, `23`) are active:
   - **Hunter (ID 17):**
     - Hostile `PlayerCharacter`: `s0 = s0 * 4.0`
     - Hostile `NPCGuardian`: `s0 = s0 * 0.25`
     - *Result:* 16:1 score preference ratio for hostile players over guardians.
   - **Objective Attacker (ID 18, 23):**
     - Hostile `NPCGuardian`: `s0 = s0 * 4.0`
     - Hostile `PlayerCharacter`: `s0 = s0 * 0.25`
     - *Result:* 16:1 score preference ratio for hostile guardians over players.
4. **Preservation of All Other Entities:**
   - Flag Flight (IDs 28..40) and Rapid Ball (IDs 41..53) untouched.
   - Human players (`_enableAi == 0`), gym mannequins (`>= 100`), and other Scramble roles (Carrier 15, Tank 16, Healer 19) untouched.
5. **Full State Integrity:**
   Preserves all 32 SIMD/vector registers, general registers `x0..x18`, `x30` (`lr`), and `nzcv` condition flags.

---

## 3. Byte-Level Patch Map & Window Occupancy

Total Dead Body: `HomeSummonModelController.SetModel` (`0x159D1BC` .. `0x159DAB0`).
Verified Contiguous Free Window: `0x159D410` .. `0x159DA44` (1,588 bytes).

| Component | Offset Range | Length | Description / Expected Bytes |
| :--- | :--- | :---: | :--- |
| **Telemetry Cave** | `0x159D410` .. `0x159D6DC` | 716 B | `KF_AI_ROLES=1` decision / probe frame logger |
| *Padding* | `0x159D6DC` .. `0x159D6E0` | 4 B | 16-byte alignment padding |
| **Targeting Cave** | `0x159D6E0` .. `0x159D854` | 372 B | `KF_AI_ROLE_TARGETING=1` Scramble candidate weighting |
| *Remaining Window* | `0x159D854` .. `0x159DA44` | 496 B | Free and unreferenced |
| **Telemetry Hook** | `0x1807474` | 4 B | `b 0x159D410` (displaces `str w0, [x19, #0x7c]`) |
| **Targeting Hook** | `0x18072D0` | 4 B | `b 0x159D6E0` (displaces `ldr x1, [x26]`) |

Negative guards verify pristine `41 03 40 f9` at `0x18072D0` and `60 7e 00 b9` at `0x1807474`. External incoming branches into `0x159D410`..`0x159DA44` are verified strictly 0.

---

## 4. Remaining Roles & Future Work
- **Carrier (ID 15) & Tank Defender (ID 16):** Carrier crystal deposit is already handled by `ScoreToDeposit` in `AIScrambleScoreEvaluationEngine`. Tank objective defense will anchor around friendly guardian positioning once friendly anchor coordinates are mapped.
- **Flag Flight & Rapid Ball Roles:** Future mode targeting will hook into `AIFlagBattleGameHelper` and `AIBallShootGameHelper` respectively, using reserved Profile IDs 28..53.

---

## 5. Independent Review Evidence & Verification Limitations

### A. Proof of Friendly and Feasibility Exclusion
- Native disassembly of `AIAttackEvaluationEngine.CalcScore` (`0x17FD530`):
  - Teammate branch: `0x17FD6A8: cmp w21, w0; b.eq #0x17fda40` compares `ownerPlayer.Team` (`w21`) with `targetEntity.Team` (`w0`). Equal teams branch directly to the epilogue returning `s0 = 0.0f` (`fmov s8, wzr`).
  - Target validity: `0x17FD57C` & `0x17FD59C`: `bl #0x17fda60; tbz w0, #0, #0x17fda40` returns `0.0f` on dead, non-targetable, or invalid entities.
  - Out-of-range: clamped or zeroed before return.
- Cave gate: `fcmp s0, #0.0; b.le bypass` guarantees that zero, negative, and NaN scores (`fcmp` unordered sets `V=1, N=0`, satisfying condition `LE`) bypass weighting. Friendly, dead, and infeasible targets cannot receive positive score adjustments.

### B. Evaluator and Target Disambiguation
- Evaluators: Verified against all 5 concrete `AIEvaluationEngine` derived classes in binary. Only `AIAttackEvaluationEngine` matches ASCII prefix `0x74414941` (`"AIAt"`). `AIScrambleScoreEvaluationEngine` ("AISc"), `AIDefensekEvaluationEngine` ("AIDe"), `AIBallShootScoreEvaluationEngine` ("AIBa"), and `AIScoreEvaluationEngine` ("AISc") are excluded.
- Targets: Verified against all 9 classes implementing `ISearchTarget`. ASCII prefixes `0x79616C50` (`"Play"` -> `PlayerCharacter`) and `0x4743504E` (`"NPCG"` -> `NPCGuardian`) are unambiguous across the interface hierarchy. Other targets (`Crystal`, `Ball`, `Flag`, `Gimmick`) safely bypass without adjustment.

### C. Memory Safety & Layout Integrity
- List indexing: `List<SearchTargetInfo>` in `x21` validated with bound check `cmp w24, w14; b.hs bypass`. Array element access `[x14 + w24*8, #0x20]` matches IL2CPP `List<T>.get_Item` (`0x2ee66d0`) layout bit-for-bit.
- Pointer safety: Explicit `cbz` null guards protect `x21` (list), `x14` (items array), `x14` (element), `x15` (target entity), `x16` (klass), `x17` (name), `x19` (engine), `x12` (owner), and `x23` (evaluator).
- Register & flag preservation:
  - `s0` modified by design (x4.0 or x0.25).
  - `s8` (current maximum score) and loop registers (`x19..x27`) untouched.
  - GPRs `x0..x18`, `x30`, and SIMD `q1..q4` preserved across stack frame `0x120`.
  - Condition flags `nzcv` restored before displaced instruction `ldr x1, [x26]`. The following instruction at `0x18072D4` (`fcmp s0, s8`) immediately overwrites condition flags.

### D. Verified Artifacts & Signing
- Targeting APK: `.local/artifacts/KickFlight-2.11.1-ai-role-targeting-signed-4b07.apk`
- SHA-256: `57b2d07f70489aa4b3b23901f7ca7dffd39d5e05ef903e384093bd80f22f15e9`
- Signing Certificate SHA-256: `4B:07:93:DE:89:74:C2:EB:38:D1:98:41:9C:DA:2B:7A:26:93:FC:C6:0E:A0:09:FC:DA:B4:EE:8F:27:C8:DC:EB`
- Telemetry APK preserved: `.local/artifacts/KickFlight-2.11.1-ai-roles-signed-4b07.apk` untouched.

### E. Limitations
- Weighting is constrained strictly to Scramble mode roles 17 (Hunter) and 18, 23 (Objective Attacker).
- Does not modify Carrier (15), Tank Defender (16), Healer (19), Flag Flight (28..40), Rapid Ball (41..53), Gym mannequins (`>= 100`), or human players (`_enableAi == 0`).
- Runtime execution requires client installation on authorized device (`adb install -r`) with server running `KF_EXPERIMENTAL_AI_ROLES=1`.
