# Native RE Note: AI Role Low-HP Guardian Deposit Evaluation

- **Target Binary:** `/tmp/kf-ai-role-analysis/libil2cpp-2.11.3.so`
- **Analyzed Routine:** `Colorful.AIScrambleScoreEvaluationEngine$$CalcScore` (RVA `0x1808A70` .. `0x1808D58`)
- **Status:** Verified static disassembly and metadata analysis; verified paths described below; gameplay validation pending.

---

## 1. Carried-Score Threshold Branch (`0x1808D40` .. `0x1808D58`)

- **Interface call for carried score (`0x1808CCC` .. `0x1808D3C`):**
  - Evaluator object loaded from `this + 0x18` (`AIEvaluationEngine._evaluator`, type `ISearchTarget`).
  - Calls `ISearchTarget.get_TargetScore()` (Interface Slot 8) via vtable dispatch at `0x1808D3C` (`blr x8`).
  - Returns carried score (crystals) in floating-point register `s0`.
- **Threshold comparison & conditional select (`0x1808D40` .. `0x1808D58`):**
  - `0x1808D40`: `ldr s1, [x19, #0x20]` — Loads `_scoreToDeposit` (32-bit integer field on `AIEvaluationEngine`) into SIMD scalar register `s1`.
  - `0x1808D44`: `fmov s2, #4.00000000` — Loads base target score multiplier `4.0f` into `s2`.
  - `0x1808D48`: `scvtf s1, s1` — Converts integer `_scoreToDeposit` in `s1` to single-precision float.
  - `0x1808D4C`: `fcmp s0, s1` — Compares carried score `s0` against deposit threshold `s1`.
  - `0x1808D50`: `fmov s0, wzr` — Clears `s0` to `0.0f`.
  - `0x1808D54`: `fcsel s0, s0, s2, lt` — If `s0 < s1` (`carried < threshold`), selects `0.0f`; if `s0 >= s1`, selects `4.0f`.
  - `0x1808D58`: `b #0x1808cb0` — Branches to epilogue, returning `s0` in standard float return register.

---

## 2. Own Guardian vs. Enemy Guardian Discrimination

- **Verification:** `AIScrambleScoreEvaluationEngine.CalcScore` **strictly distinguishes and requires own guardian**.
- **Evidence (`0x1808B30` .. `0x1808C48`):**
  - `0x1808B30`: Checks `AITargetAttribute.DepositPlace` (bitmask value `0x10 = 16`) on `target.Target` via `AIEvaluationEngine.CheckAttribute` (RVA `0x17FDA60`). If not a deposit place, branches away (`tbz w0, #0, 0x1808cac`).
  - `0x1808B54` .. `0x1808BC0`: Queries evaluator team via `ISearchTarget.get_TargetTeamType()` (Interface Slot 5) on `this._evaluator` (`[x19, #0x18]`); result stored in `w21`.
  - `0x1808BD0` .. `0x1808C40`: Queries target team via `ISearchTarget.get_TargetTeamType()` (Interface Slot 5) on `target.Target` (`[x20, #0x10]`); result returned in `w0`.
  - `0x1808C44`: `cmp w21, w0` — Compares evaluator team against target deposit place team.
  - `0x1808C48`: `b.ne #0x1808cac` — If teams differ (`evaluator.Team != target.Team`), immediately exits with score `0.0f` (held in `v8.16b`). Enemy guardians are strictly rejected.
- **Deposit In-Progress Bypass (`0x1808C98` .. `0x1808CA8`):**
  - Checks if `_evaluator` has attribute `0x20` (`AITargetAttribute.DepositProgress = 32`).
  - If bit is set (`tbz w0, #0, #0x1808ccc` falls through), immediately assigns `4.0f` (`0x1808CA8: fmov s8, #4.0; b 0x1808CB0`), bypassing threshold check.

---

## 3. HP and MaxHP Accessor Native Evidence & Semantics

- **Native Accessors in `PlayerParameter`:**
  - `Colorful.PlayerParameter$$get_HP`: RVA `0x1754624` (signature: `int32_t get_HP(PlayerParameter *this)`).
    - HP is stored as `ObscuredInt` in `[x0, #0x98]` (16 bytes `q0`) and `[x0, #0xa8]` (`w8`).
    - Decoded via call to `CodeStage.AntiCheat.ObscuredTypes.ObscuredInt$$op_Implicit` (`0x248430c`). Raw integer dereference is impossible without decryption.
  - `Colorful.PlayerParameter$$GetMaxHP`: RVA `0x175471c` (signature: `int32_t GetMaxHP(PlayerParameter *this)`).
    - Computes max HP incorporating master data `HpCorrection` via `KickerParameterMasterData$$get_HpCorrection` (`0x1536528`).
- **Canonical Interface & Wrapper in `PlayerCharacter`:**
  - `Colorful.PlayerCharacter$$get_TargetDamageRate` (RVA `0x13BE18C`, Slot 63 / `ISearchTarget` Slot 9):
    - Retrieves `PlayerParameter` pointer at offset `+0x180` from `PlayerCharacter`.
    - Handles dead status via virtual method at `[klass + 0x268]` (`IsDead()`). If dead, returns `1.0f`.
    - Calls `PlayerParameter$$get_HP` (`0x1754624`) and `PlayerParameter$$GetMaxHP` (`0x175471c`).
    - Divides `(float)currentHP / (float)maxHP` and returns `damageRate = 1.0f - (currentHP / maxHP)`.
    - Tail-branches at `0x13BE26C` to clamp routine `0x23E7EB0` (negative -> 0; otherwise min(value,1)). Returns float in `s0`: 0 at full health, 1 at zero health.
- **Low-HP Mathematical Relation:**
  - Low HP policy `currentHP / maxHP <= 0.35` is mathematically identical to `damageRate >= 0.65` (`1.0 - 0.35 = 0.65`).
  - Float constant `0.65f` in IEEE 754 single-precision: `0x3F266666`.

---

## 4. Evaluator Context & Register Liveness at `0x1808D40`

- **Evaluator Reference:**
  - `x19` holds `this: AIScrambleScoreEvaluationEngine*` continuously across `0x1808A98` .. `0x1808D58`.
  - `[x19, #0x18]` is `_evaluator`, passed directly as `_ownerPlayer` (`PlayerCharacter*`) in `AIPlayerEngine.UpdateTarget` (`0x1807248` .. `0x1807258`).
  - `PlayerCharacter + 0xc4` is `_kickerAiParameterId` (int32).
  - `PlayerCharacter + 0xe0` is `_enableAi` (uint8 bool).
- **Register Liveness at `0x1808D40`:**
  - `x19`: Live (`this: AIScrambleScoreEvaluationEngine *`).
  - `s0`: Live (carried score from `ISearchTarget.get_TargetScore()` at `0x1808D3C`).
  - `x30` (`lr`): Preserved on stack (`sp + 0x40`).
  - Displaced instruction at `0x1808D40`: `ldr s1, [x19, #0x20]`.

---

## 5. Cave Window & Feasibility Analysis

- **Verified Dead Body:** `Colorful.HomeSummonModelController$$SetModel` (`0x159D1BC` .. `0x159DAB0`).
- **Existing Cave Allocations:**
  - `0x159D200` .. `0x159D3DC`: `PlayerCharacter.UpdateAi` cave (476 B active).
  - `0x159D3E0` .. `0x159D40C`: `GetDisplayAngles` production cave (44 B active).
  - `0x159D410` .. `0x159D6DC`: AI roles telemetry cave (716 B active).
  - `0x159D6E0` .. `0x159D854`: AI roles candidate score weighting cave (372 B active).
  - `0x159DA48` .. `0x159DA78`: Jay bomb (`skill_40001`) cave (48 B active).
- **Verified Remaining Contiguous Free Window:**
  - Range: `0x159D854` .. `0x159DA44` (496 bytes available).
- **Cave Size & Query Safety:**
  - Calling `PlayerCharacter$$get_TargetDamageRate` (`0x13BE18C`) directly from the cave via `bl`:
    - Full register preservation: 64 GPR bytes, 32 SIMD bytes, `x30`, `nzcv`.
    - Distance: `0x13BE18C - 0x159D854 = -0x1DF6C8` (within 128MB immediate branch range).
    - Assembled machine code size: **268 bytes**.
    - Capacity check: `268 <= 496` bytes. Fits with 228 bytes remaining.
  - The earlier 268-byte feasibility estimate was not an implemented artifact. See actual implementation below.

## 6. Implemented experimental cave (Sol escalation recovery)

- Generator: `scripts/re/ai_role_deposit_cave.py`; flag `KF_AI_ROLE_DEPOSIT=1` requires `KF_AI_ROLES=1`, default OFF.
- Hook `0x1808D40` displaces `612240bd` (`ldr s1,[x19,#0x20]`) exactly once; branch back `0x1808D44`. No call/branch instruction overwritten.
- Actual cave: `0x159D854..0x159D9F0`, **412 bytes**, leaving 84 bytes before `0x159DA44`. The earlier 268-byte figure was a preliminary estimate.
- All x0..x30, q0..q31, NZCV preserved on 0x300-byte aligned frame; only displaced s1 changes, with integer bits 1 selected when eligible. Canonical HP accessor uses `bl`; original LR restored.
- Gates: nonnull evaluator, enableAi, profile15 (Scramble carrier only), nonnull PlayerParameter, carried crystals>=1 and non-NaN; canonical damageRate>=float32(0.65) and non-NaN. Other profiles/modes, humans/gym bypass.
- Enemy guardian rejection is performed by the original evaluator before this hook. Cave preserves stock SCVTF/FCMP/FCSEL and score4 behavior; it does not force pathing or prove an actual deposit.
- Unicorn2.1.4 tests execute the generated ARM64 with only HP accessor stubbed, including caller register/SIMD/NZCV clobbers: lowHP boundary, healthy/zero/NaN, profile/mode/human/gym exclusions, nulls, SP/LR preservation. Actual Android behavior remains pending.
- Immutable hook and cave byte guards match exact published2.11.3 SO and pristine2.11.0. Full standard patcher dry-run on pristine with roles+targeting+deposit+PHOTON0:245 patches accepted. Full patcher rejects prepatched published SO (existing unrelated baseline guards); no guard weakened.

- Final generator also emits standalone KFDIAG7650 only when lowering threshold1; native Android logger ABI and clobber preservation are covered by Unicorn tests. Final cave412bytes (the preliminary380-byte version had no event logger). All role Python suites28passed, zero skips.

## 7. Android execution evidence

New signed APK SHA3e50dd23d20e4e6c1ef29afd8fd2a3f4c27d9ebb199a81c0ac2a46a054c35cbb installed with install-r after the prior match completed. Standalone7650 observed in real Scramble: threshold write branch executed, process alive without current-process fatal markers. Frozen log `.local/run/logcat-sol-deposit-proof.txt`; counts/times/SHA in ai-role-runtime-validation.md. This supersedes the earlier Android-pending note for branch execution, but does not prove completed deposits or evasion.
