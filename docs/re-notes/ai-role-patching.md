# AI role patching investigation

Status: preliminary, static-only. No source, APK, runtime, or production changes were made.

## Artifact identity

- Published/tested download route: `https://kick-flight-fenix.us.ci/apk/KickFlight-2.11.3-remote.apk` (catalog entry `apk-remote-2113`, last-modified 2026-10-11).
- Downloaded copy: `/tmp/kf-ai-role-analysis/KickFlight-2.11.3-remote.apk`, 149,813,883 bytes, SHA-256 `da4f18cd68422c540e8fe6c202fe8453d264a325627947e1dc23f9a73d15e8cc`.
- Active ARM64 `libil2cpp.so`: `/tmp/kf-ai-role-analysis/libil2cpp-2.11.3.so`, 74,161,912 bytes, SHA-256 `04d48df3fe04c23a5f17958faa96dca8f1f83cb661c10176f02cabe6ee500e2d`.
- Pristine 2.11.0 base SHA-256 `ffa620d2e2f905e729812606024e02741124951dce908bb2e0587f0a4e16e031`; extracted ARM64 lib SHA-256 `c92a03cc227b46bf3281653ca4ac23cc012919e1783ae889d69ff4b1cae9daa0`. The production APK is distinct; all hook observations below compare its extracted lib with that base.

## Confirmed data and transport

- Server `BattleMatchmakingService` currently sets bot `kickerAiParameterId` to `kickerId` (or `100 + kickerId` for gym), and separately sets `kickerAiDiscDeckId` from the selected deck. It sends rank 13 because the AI rows use rank 13. Relevant code: `src/KickFlight.BootstrapApi/BattleMatchmakingService.cs` around `BuildRoster` (lines 1304–1382 in this revision).
- Existing RE notes say `PlayerCharacter.GetKickerAIParameterMaster` indexes `KickerAiParameter` with `get_Item(_kickerAiParameterId)`; this pass did not rederive that lookup natively. Current 14 rows use kicker IDs 1–14. The scout verified the schema has `attackMotivation`, `defenseMotivation`, `scoreMotivation`, `scoreToDeposit`, and interval/accuracy fields; all 14 current rows share behavior values and rank 13. The roles therefore have no current per-row behavior profiles.
- Focused native read of the exact active lib confirms `0x13BEAEC` is `b 0x159D200`. Project RE notes place `_enableAi` at `this+0xE0`, `_kickerAiParameterId` at `+0xC4`, and `AIOption` at `+0xE4`; field offsets alone do not establish read/write behavior. The existing cave source reads the parameter ID and writes the mannequin option; active hook bytes match that patch.
- Custom battle exposes the retail AI parameter/deck pairs through `CustomBattleKickerAi`; its current rows only list IDs 1–14. Regular matchmaking writes the IDs directly into each `MatchingPlayerBattleInfo`.
- **Compatibility constraint:** the active `PlayerCharacter.UpdateAi` cave classifies parameter IDs `>=100` and `-1` as mannequins and sets `AIOption=63`. A new normal role ID must stay below 100 (candidate unused range 15–99) or this branch must be deliberately revised. Keep each copied row's kicker identity/costume and rank 13. There is no proven upper-bound check for 15–99 yet.

## Active production patch sites and occupancy

Byte comparison of the exact active lib against pristine 2.11.0 confirms these relevant production patches are present in the published 2.11.3 artifact:

| Behavior | RVA | pristine bytes | active 2.11.3 bytes |
| --- | ---: | --- | --- |
| `PlayerCharacter.UpdateAi` entry -> existing bot cave | `0x13BEAEC` | `f44fbea9` | `c5790714` |
| Bot cave (`HomeSummonModelController.SetModel` body) | `0x159D200` | `000140b9…` | `08804339…` |
| `PlayerStateNormal.CanTakeOff` ground branch -> helper | `0x17E6FFC` | `e0031f2a` | `7530fd97` |
| Existing helper slot | `0x17331D0` | original method bytes | `e00313aabb3f0d14…` |
| `PlayerStateNormal.IsLandable` entry -> gate | `0x17E0EF4` | `e80f1cfc` | `3b32f617` |
| Existing `IsLandable` gate | `0x156D7E0` | original method bytes | `aa1300f04ac12691…` |
| `PlayerStateDeposit.InputAction` entry -> gate | `0x18D1F10` | `f44fbea9` | `7b4aff17` |
| Existing deposit gate | `0x18A48FC` | original method bytes | `090840f9a90000b4…` |

These patches preserve moving AI bots' airborne destination behavior, prevent a shared local touch-up event from canceling bot deposits, and automate AI special-skill execution. They occupy the relevant hook/helper bodies; none is a free cave. The existing `UpdateAi` cave is already extended for gym-mannequin and special-skill behavior, so additional role logic there needs an explicit size, displaced-instruction, branch-target, and full incoming-reference audit. Do not reuse a historical free-space estimate.

## Decision flow and patch design

- Existing DIAG instrumentation provides known observation seams: `AIPlayerEngine.ExecuteUpdate/Execute/UpdateTarget/SetCurrentTarget` (IDs 900–903), `PlayerCharacter.PerformGetScore` (972), and `PerformMove` (973; 1100 records `AIOption`, 1300 route count, and goal-distance bits). This proves those routines were instrumented for diagnosis, not their complete semantics or that the DIAG hooks are active in the published APK.
- `docs/GYM_MODE_PLAN.md` records `AIEnableMove`, `AIEnableAttack`, `AIEnableHeal`, `PerformMove`, `TryAvoid`, and `JudgeUseAISkill` reading `PlayerCharacter.AIOption` at `+0xE4`; `scripts/re/bot_special_skill_cave.py` reads the parameter ID at `+0xC4`. These are existing project RE notes/source, not newly recovered native consumer bodies and not evidence that `defenseMotivation` means guarding an objective.
- Minimal first experiment: add role-specific copies of valid `KickerAiParameter` rows, assign their IDs per bot from the already-selected server composition, and vary one motivation/threshold at a time while holding kicker, rank, deck and match fixed. This is data-only and tests whether existing client decisions honor those values.
- If data-only weights cannot express objective policy, the intended client strategy is to patch the target-choice point (`PerformMove` / `PerformGetScore` or the immediately preceding `AIPlayerEngine.UpdateTarget`) so it consumes the per-bot parameter ID and mode/objective state. Preserve the original generic combat/route path for all other cases. First verify the exact method RVAs, caller/callee state, candidate goal representation, mode source, parameter lookup, and enough dead-body space on the exact 2.11.3 lib; do not implement a guessed cave.
- User-requested policies (not implemented or confirmed as native behavior):
  - Crystal Scramble composition covers BattleRule master row IDs 1, 5, 6, and 7. Carriers collect crystals and deposit at the guardian when low on HP; Ana escapes evasively. Tanks and Owlbert defend their own guardian. Objective attackers damage the enemy guardian; hunters eliminate enemy collectors.
  - Flag Flight composition covers BattleRule master row IDs 2 and 8. Retrieve the designated flag and deliver it to the goal. The role logic must use the match's actual designated-flag and goal state; do not assume the target is an enemy flag or a home base.
  - Rapid Ball composition covers BattleRule master row IDs 3 and 4. Multiple balls are scattered. Collectors gather balls and score only after the enemy guardian is defeated and the goal opens. Defenders guard their own guardian; assassins eliminate defenders and collectors; objective attackers damage the enemy guardian to unlock the goal.
  - Kicker-specific support policies across applicable compositions: Ruriha heals allies; Grenhawk uses ranged attacks and speed; Sid increases allied damage; Owl uses traps.
- Existing server transport can assign a per-bot `kickerAiParameterId` and deck independently. These requested policies still need native evidence that the client exposes suitable target/state choices and can read the role row; they are design targets, not observed behavior. Do not treat these BattleRule row IDs as client enum values.

## Unresolved / risk

- Exact native consumers and formulas for `attackMotivation`, `defenseMotivation`, `scoreMotivation`, `scoreToDeposit`, and `changeAttributeInterval` are not confirmed here. In particular, `defenseMotivation` has not been shown to mean guarding a flag/base rather than combat or movement scoring.
- Exact `PerformMove`, `PerformGetScore`, and `AIPlayerEngine.UpdateTarget` RVAs/candidate data layout, mode checks, lookup bounds for parameter IDs 15–99, and role-triggered state transitions remain unverified. Existing diagnostics and old notes are not a substitute for this evidence.
- Focused procedure-name searches against the active target stalled; `agy -p` was denied by its headless command-permission gate. No IL2CPP dump was found under the repo/assets checkout. No native consumer RVAs or full disassembly/free-space validation were returned. No claim of full native disassembly or free-space validation is made.
- Runtime proof is pending. Planned validation after implementation: verify every role row lookup and bot roster transport; DIAG trace of mode, role ID, selected target/goal and state transitions; seeded local 2v2/4v4 trials across Scramble, Flag Flight, Rapid Ball; verify carrier/deposit/escape, defense/interception, combat fallback, scoring and regression for normal bots; compare patched lib bytes against exact approved map and preserve the signed/base artifact.

## Evidence pointers

- `docs/GYM_MODE_PLAN.md`; `docs/FLAG_FLIGHT_PLAN.md`; `docs/RAPID_BALL_PLAN.md`; `docs/CUSTOM_BATTLE_PROTOCOL.md`.
- `scripts/re/bot_special_skill_cave.py`; `scripts/re/bot_ground_deposit_caves.py`; `scripts/patch-il2cpp-endpoints.py` around the active production AI/ground/deposit entries.
- Historical native behavior details in `docs/CONTINUATION_PROMPT_BATTLE_CRASH_FIX_V2.md` section 7.4. These describe earlier observed builds; validate each claim against 2.11.3 before reuse.
