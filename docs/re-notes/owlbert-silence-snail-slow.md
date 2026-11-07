# Owlbert KS silence and Snazzy Snail slow (2026-11-06)

Client: Kick-Flight 2.11.804 (arm64 `libil2cpp.so` sha256 `53eb19e4...67ab`, from `.local/artifacts/KickFlight-2.11.804.apk`
sha256 `fcbdfcc6...16ed`). RVAs are arm64 il2cpp-dump RVAs. Server data only; no client or patch-table change.

## Rows

| Thing | Ids |
| :--- | :--- |
| Owlbert | kickerId 5, KickerCostume rows 2050101.. |
| Owlbert KS "Dron Centinela" / Hacking Drone | skill 20005, actionType 13 (`DroneSkillAction`) |
| Snazzy Snail | disc 3010050, skill 10050, trapType 1 (Slow) |

The VPS-served `SkillCondition` / `SkillTrap` rows were identical to the repo before the fix (checked with a read-only fetch
of `/demo-master/<Name>`), so this was a data-semantics bug, not deployment drift.

## Root cause

### Owlbert KS (silence never applied)
* The drone applies the silence itself: `DroneSkillAction.CreateDrone` (0x149F0A0) spawns an `NPCDrone`; on arrival
  `NPCDroneStateAttack.Begin` (0x14626A0) loads `w1 = 3` (ConditionTriggerType 3 Execute, insn at 0x1462954) and tail-calls
  `NPCDrone.AcceptCondition` (0x14602A0) -> `GetConditionInitInfo` (0x146021C) ->
  `PlayerParameter.GetKickerSkillConditionInitInfo` (0x1757E90, attackType 3, trigger = argument).
  The trigger filtering of the rows is `PlayerCharacter.ApplyCondition` (0x13DDA60) via `GetConditionsInfoByTriggerType`
  (0x13DE674), which keeps rows whose triggerType is 7 (All) or the requested one.
* The served Silent (9) rows for skill 20005 had triggers 5 (EnterEnemyTeamTrap) and 4 (EnterTrap). Nothing in the drone
  path asks for those, so no row matched. Trigger 4/5/6 rows are only consumed by `ConditionTrapAction`, and that class
  registers rows only for `TrapParameter.AttackType` Skill (2) / SpecialSkill (4) (`AddConditionInitInfo` 0x17C74F8), never
  for KickerSkill (3).
* Same bug class as Jay's KS (docs/CONTINUATION_PROMPT_COMBAT_V6.md, round 6): the skill action only applies trigger 3 rows.
* Fix: `SkillCondition` row id 136 = Silent(9), duration 8, value 1.0, **triggerType 3**; the redundant trigger-4 row (id 137) is
  removed. The `SkillTrap` row (type 2 r8) stays because `SkillParameterBase.InitializeData` reads it; the drone creates no trap.
  `NPCDrone.ManagedUpdate` (0x145FB14) ends the drone when the target's Silent condition disappears.

### Snazzy Snail (no slow)
* `Trap.Initialize` (0x31D4540) maps trapType 1 Slow / 2 Silent / 8 Condition to a condition trap action. The action only applies
  `SkillCondition` rows of the skill (`GetSkillConditionInitInfo` 0x1757C9C); `TrapInfo.get_EffectValue` (0x31D69A4) has no
  direct callers, so the `SkillTrap.effectValue` (0.3) was never used. Skill 10050 had no `SkillCondition` row -> trap with nothing to apply.
* Trigger for enemies: `GetConditionTriggerType` (0x17C8628) returns 4 when `_isSeparateCondition` (+0x68) is clear and 5/6 (enemy/own
  team) when set; `OnInitialize` (0x17C6E28) sets it when the skill has a trigger 5/6 row. So a trigger-5 row (as every other
  enemy-affecting trap disc in `DISC_TRAPS`) is applied to enemies entering the trap.
* Fix: added `SkillCondition` row id 146 = SpeedRate(2), duration 10, interval 0, value 0.3, triggerType 5, for skill 10050.

## Changes
* `config/masters_skill_condition.json`: row 136 trigger 5 -> 3; row 137 (20005, trigger 4) removed; row 146 (10050 SpeedRate) added.
  No other row changed (62 rows before and after).
* `scripts/generate_combat_masters.py`: `KICKER_SKILL_EXTRAS[20005]` and `DISC_TRAPS[10050]` mirror the same rows, with comments.
  A full regeneration is not idempotent against the committed tables (it would also add an Anna Restraint(15) trigger-3 row for 20008 and
  renumber ids), so the JSON was edited surgically; the generator source carries the same data for the next regeneration.
* A served-master hash change makes the client re-download `SkillCondition`; the API needs a restart after editing the JSON.

## Verification status
Emulator (KickFlight_API35, 2.11.804-local build, cert 81ca6334, local API with the fixed masters, gym mode, Rapid Ball arena):
* Snail cast: went on cooldown with water splash; condition status icons (pink running-man) appeared beside all 3 enemy portraits
  right after the cast. Observed once, not compared against a baseline.
* Owlbert KS: "Hacking Drone" banner, drone flew out and locked an enemy (white lock-on square). Silence itself NOT visually confirmed:
  gym mannequin bots never use skills, so there is no observable silenced behaviour.
* No baseline (HEAD masters) A/B in the same arena: Crystal / Flag Flight ranked hours do not put the player next to the bots, those runs
  were inconclusive. Only Rapid Ball (UTC hour % 3 == 2) is usable; the wait for 23:00 UTC was abandoned.
* Flight-speed reduction from SpeedRate was not measured (see below).
Manual test (2026-11-06 ~22:30-22:36 UTC): gym OFF (normal bots), user played two battles on the -local build with the fixed masters and
reported that both effects now work (Owlbert KS silences, Snazzy Snail slows). Two complete battles, `/battle/result` handled, no FATAL/SIGSEGV
in `.local/run/logcat-manual-test.txt`. This is the user's observation; no frame-level measurement or HEAD-master baseline A/B was recorded.
Afterwards the VPS 2.11.804 APK (sha256 fcbdfcc6...16ed) was reinstalled with `adb install -r -d` (no wipe), local API stopped.

## Open / unverified
* `PlayerParameter.GetSpeed` reads `ConditionActionParameter.SpeedRate` (0x17C2344) only when flag bit 0x4 is passed (callers found:
  `DiscSkillParameter.GetRange`, `KickerSkillParameter.GetRange`, `MoveAttackSkillAction.HitCallback`, all with flags 0x1E). No direct
  caller in the PlayerCharacter flight/movement code was found statically, so whether SpeedRate slows free flight (as opposed to dash/skill
  speed) is UNVERIFIED. The Snail row is the correct data for the client mechanism; if flight speed does not drop in play, the cause is the
  client's SpeedRate consumers, not this data (would need a client change, out of scope).
* Concrete class for trapType 1 (Slow) was not identified statically (the dump only shows Inhale and Smog subclasses).
* No REA Ghidra evidence IDs: analysis is Il2CppDumper dump + disassembly; dump-to-binary identity is only checked by RVA/prologue consistency.
