# AI Role Parameter ID Contract & Mode-Aware Native Mapping

Status: Shared deterministic contract between Server (`KickFlight.BootstrapApi`) and Native Worker / RE (`libil2cpp.so` ARM64 / iOS Mach-O).
Created: 2026-10-19.

## 1. ID Formulation & Range
Native lookup in `PlayerCharacter.GetKickerAIParameterMaster` (0x13C3260) calls `TMasterBase<object>$$get_Item` (0x1A30A6C) directly without upper-bound clipping. Cave at 0x159D200 reserves IDs `>= 100` for Gym mannequin behavior (`AIOption = 0x3F`). IDs `15..99` execute standard bot AI logic.

To support mode-aware AI role transport across all three battle modes, IDs are deterministically derived via:
$$\text{ParameterId} = 15 + (13 \times \text{modeIndex}) + \text{profileIndex}$$

- **Range:** `15..53` (strictly $\le 53 < 100$, well within the safe `15..99` boundary).
- **Total Rows:** $3 \text{ modes} \times 13 \text{ profiles/specializations} = 39$ unique master rows.

### Mode Indices (`modeIndex` $\in [0..2]$)
Mapped from `ScrambleBotCompositionSelector.ModeForBattleRuleId(battleRuleId)`:
- `0`: **Crystal Scramble** (`battleRuleId` 1, 5, 6, 7)
- `1`: **Flag Flight** (`battleRuleId` 2, 8)
- `2`: **Rapid Ball** (`battleRuleId` 3, 4)
- *Fallback:* Unsupported or unknown battle rules fall back safely to pristine `kickerId`.

### Profile Indices (`profileIndex` $\in [0..12]$)
Preserves the exact canonical profile order:
- `0`: `carrier` (Base: Kicker 8 / Ana archetype)
- `1`: `tank_defender` (Base: Kicker 3 / Coco archetype)
- `2`: `hunter` (Base: Kicker 4 / Jay archetype)
- `3`: `objective_attacker` (Base: Kicker 10 / Yuyan archetype)
- `4`: `healer` (Base: Kicker 2 / Ruriha archetype)
- `5`: `speed_support` (Base: Kicker 7 / Grenhawk archetype)
- `6`: `damage_support` (Base: Kicker 14 / Sid archetype)
- `7`: `trap_defender` (Base: Kicker 5 / Owlbert archetype)
- `8`: `Yuyan` (Specialization: `objective_attacker` with `kickerId == 10`)
- `9`: `Owlbert` (Specialization: `trap_defender` with `kickerId == 5`)
- `10`: `Ruriha` (Specialization: `healer` with `kickerId == 2`)
- `11`: `Grenhawk` (Specialization: `speed_support` with `kickerId == 7`)
- `12`: `Sid` (Specialization: `damage_support` with `kickerId == 14`)

---

## 2. Full 39-ID Mapping Table

| Profile Name / Key | Profile Index | Scramble (Mode 0) | Flag Flight (Mode 1) | Rapid Ball (Mode 2) | Archetype / Kicker |
| :--- | :---: | :---: | :---: | :---: | :--- |
| `carrier` | 0 | 15 | 28 | 41 | Kicker 8 (Ana) |
| `tank_defender` | 1 | 16 | 29 | 42 | Kicker 3 (Coco) |
| `hunter` | 2 | 17 | 30 | 43 | Kicker 4 (Jay) |
| `objective_attacker` | 3 | 18 | 31 | 44 | Kicker 10 (Yuyan) |
| `healer` | 4 | 19 | 32 | 45 | Kicker 2 (Ruriha) |
| `speed_support` | 5 | 20 | 33 | 46 | Kicker 7 (Grenhawk) |
| `damage_support` | 6 | 21 | 34 | 47 | Kicker 14 (Sid) |
| `trap_defender` | 7 | 22 | 35 | 48 | Kicker 5 (Owlbert) |
| `Yuyan` | 8 | 23 | 36 | 49 | Kicker 10 Spec (`objective_attacker`) |
| `Owlbert` | 9 | 24 | 37 | 50 | Kicker 5 Spec (`trap_defender`) |
| `Ruriha` | 10 | 25 | 38 | 51 | Kicker 2 Spec (`healer`) |
| `Grenhawk` | 11 | 26 | 39 | 52 | Kicker 7 Spec (`speed_support`) |
| `Sid` | 12 | 27 | 40 | 53 | Kicker 14 Spec (`damage_support`) |

---

## 3. Resolution Precedence & Fallback Rules
1. **Gym Isolation:** If `isGym` is true, immediately return `GymAiParameterBase + kickerId` (`>= 100`), guaranteeing mannequin behavior.
2. **Feature Flag Check:** If `KF_EXPERIMENTAL_AI_ROLES` is not enabled (`!= "1"` and `!= "true"`), immediately return pristine `kickerId`.
3. **Mode Resolution:** If battle rule or mode is unknown or unsupported, return pristine `kickerId`.
4. **Specialization Precedence:**
   - Check if `(profile, kickerId)` matches an explicit specialization:
     - `("objective_attacker", 10)` or `"Yuyan"` $\rightarrow$ Profile Index 8.
     - `("trap_defender", 5)` or `"Owlbert"` $\rightarrow$ Profile Index 9.
     - `("healer", 2)` or `"Ruriha"` $\rightarrow$ Profile Index 10.
     - `("speed_support", 7)` or `"Grenhawk"` $\rightarrow$ Profile Index 11.
     - `("damage_support", 14)` or `"Sid"` $\rightarrow$ Profile Index 12.
   - If not specialized, match base profile name $\rightarrow$ Profile Index 0..7.
   - If profile is unrecognized or null $\rightarrow$ return pristine `kickerId`.
5. **Output Calculation:** Return $15 + 13 \times \text{modeIndex} + \text{profileIndex}$.

---

## 4. Master Row Schema & Client Compatibility
Each cloned row in `masters_kicker_ai_parameter.json` must preserve all schema fields:
- `id`: Assigned parameter ID (15..53).
- `kickerId`: Target kicker ID.
- `kickerCostumeId`: Costume row ID.
- `rank`: Exactly `13` (matches bot player rank; `GetKickerAIParameterMaster` selects rows with `row.rank <= player.rank`).
- `attackMotivation`, `defenseMotivation`, `scoreMotivation`, `scoreToDeposit`.
- Dash and skill intervals: `attackDashInterval`, `defenseDashInterval`, `scoreDashInterval`, `attackSkillInterval`, `healSkillInterval`, `buffSkillInterval`, `trapSkillInterval`, `warpSkillInterval`.
- General properties: `canLockon`, `healDamageRate`, `negativeCorrection`, `positiveCorrection`, `minMeasureAccuracy`, `maxMeasureAccuracy`, `changeAttributeInterval`.

---

## 5. Behavioral Semantics & Native Verification Constraints
- **Motivations vs. Strict Targeting:** Motivation weights affect `AIEvaluationEngine.GetPurpose()` and candidate target scores in `AIPlayerEngine.UpdateTarget`. However, **motivation weights alone do not strictly enforce target selection semantics** across all target types. They are behavioral heuristics, not hard target locks.
- **Yuyan Objective Target:** In Scramble, Yuyan's `objective_attacker` motivation configuration prioritizes high attack motivation against enemy objectives (such as the enemy guardian), rather than acting as a defender for the friendly guardian.
- **Flag Flight Rule:** Flag Flight mode has **no guardians**. Bots in Flag Flight must not exhibit guardian-anchored defense or guardian-deposit logic.
