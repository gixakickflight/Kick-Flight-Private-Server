# AI role targeting and disc decisions: bounded native pass

Status: partial static investigation of the published Android client. No source, master, APK, or runtime files were changed. The exact heal/buff/trap target-selection consumers remain unresolved.

## Artifact and method evidence

- Published 2.11.3 remote APK: SHA-256 `da4f18cd68422c540e8fe6c202fe8453d264a325627947e1dc23f9a73d15e8cc` (149,813,883 bytes).
- Extracted arm64 `libil2cpp.so`: SHA-256 `04d48df3fe04c23a5f17958faa96dca8f1f83cb661c10176f02cabe6ee500e2d` (74,161,912 bytes).
- Bounded Capstone read at the historically identified `PlayerCharacter.UpdateAi` RVA `0x13BEAEC` in that exact library shows the first instruction is `b 0x159D200`. This confirms entry redirection to a cave; it does not establish whether the cave later resumes the original body.
- The bytes immediately following the redirected entry include the old body prologue; the observed body reads `_enableAi` from `this+0xE0` at `0x13BEB20`. Because execution enters the cave first, the observed bytes alone do not establish the current branch/return path.
- Existing local native notes (`docs/GYM_MODE_PLAN.md`, `scripts/re/bot_special_skill_cave.py`) associate `_kickerAiParameterId` with `this+0xC4`, `AIOption` with `this+0xE4`, and name AIOption readers `AIEnableMove`, `AIEnableAttack`, `AIEnableHeal`, `PerformMove`, `TryAvoid`, and `JudgeUseAISkill`. These are historical native notes; I did not independently verify those readers in 2.11.3 during this pass.

## Role transport and limits

- In the research worktree source, `MatchingPlayerBattleInfo` serializes `kickerAiParameterId`, `kickerAiDiscDeckId`, and four disc IDs/levels, with no explicit role field. Bot assembly currently assigns parameter ID from kicker ID (gym uses the reserved `100 + kickerId` range) and selects the deck from composition.
- Existing `KickerAiParameter` rows distinguish kicker identity but use shared motivation/skill-interval values; separate role semantics are not present in those rows as currently served. This is server/master evidence only.
- No evidence from this pass proves whether healing, buffs, or traps target self, allies, or enemies; whether candidate filtering already admits allies; or which fallback runs when no target qualifies.
- REA Ghidra procedure search and close requests did not return within the investigation window. The initial required `agy` invocation was blocked by the headless command-permission gate. No `dump.cs` or `script.json` was present under the checked-out repository/assets paths, so no dump-derived method RVA was claimed.

## Follow-up needed before patch design

- Recover 2.11.3 addresses for `PlayerCharacter.AIEnableHeal`, `JudgeUseAISkill`, the skill target selector, and heal/buff/trap target candidate predicates; inspect the active cave at `0x159D200` before making reachability claims about `UpdateAi`.
- Determine ally/self/enemy filters and no-target fallback from those consumers. Until then, do not add ally candidates or force a skill target at an assumed call site.
- Any eventual cave must preserve live registers/flags, save/restore `x30` around `bl`, use a verified dead body for the exact active patch set, and retain the original continuation. The existing `UpdateAi` cave is already occupied at `0x159D200` in this artifact, so it is not free space.
