# AI Role Delivery & Inventory Validation Status

Status: PROVISIONAL (Static analysis, cave assembly, server plumbing, and local signing verified; on-device ADB loop execution pending).
Worktree: `/home/gixarde3/Proyectos/KickFlight/Kick-Flight-Private-Server/.local/worktrees/ai-role-investigation`
Branch: `research/ai-role-patching` (base `33d38a7`)
Date: 2026-10-19

---

## 1. Workspace Inventory & Preservation Audit

### Root Repository (`/home/gixarde3/Proyectos/KickFlight/Kick-Flight-Private-Server`)
- **Branch / HEAD:** `main` (`0db556f`, behind origin/main by 4).
- **Tracked Changes:** Exactly 1 tracked modified file: `AGENTS.md` (preexisting dirty file from initial checkout). Zero newly modified tracked source files in root.
- **Untracked Preexisting:** `.agents/skills/agy-worker/SKILL.md`.
- **Root Hygiene Closure:**
  - Audited 4 agent-created root copies (`docs/re-notes/ai-role-id-contract.md`, `docs/re-notes/ai-role-local-signing.md`, `scripts/re/ai_role_cave.py`, `scripts/re/ai_role_diag_read.py`). None were tracked or preexisting.
  - Compared with canonical research-worktree versions (contract/signing/diag-read identical; cave script divergent in worktree). Canonical worktree versions preserved without overwrite.
  - Root copies moved to ignored backup `.local/ai-role-root-copies/<relativepath>`.
  - Root untracked status restored: only preexisting `.agents/` remains.
- **Submodules:** `submodules/luxonserver` at `b35e3cf1fe72c1645f2888937ae0ca562705592c` (clean, matches origin/tanuki-server).

### Research Worktree (`.local/worktrees/ai-role-investigation`)
- **Branch / HEAD:** `research/ai-role-patching` (`33d38a7`).
- **Submodules:** `submodules/luxonserver` uninitialized pointer `-b35e3cf...` (clean/pristine).
- **Runtime State & Authentication Preservation:**
  - `src/KickFlight.BootstrapApi/data/users/_identities.json` modified by legitimate localauth.
  - Backed up privately to ignored `.local/runtime-state/_identities.json` (dir mode `0700`, file mode `0600`).
  - Active runtime state preserved without revert; excluded from PR.
- **Tracked Modified Files:**
  - `scripts/patch-il2cpp-endpoints.py` (added AI role telemetry and targeting hooks/caves).
  - `src/KickFlight.BootstrapApi/BattleMatchmakingService.cs`
  - `src/KickFlight.BootstrapApi/DemoSessionApi.cs`
  - `src/KickFlight.BootstrapApi/ScrambleBotCompositionSelector.cs`
  - `src/KickFlight.BootstrapApi/data/users/_identities.json` (runtime localauth state, excluded from PR).
- **Untracked Research Notes & Tests:**
  - `docs/re-notes/ai-role-*.md` (9 specification and verification notes).
  - `scripts/re/ai_role_cave.py`, `scripts/re/ai_role_diag_read.py`, `scripts/re/audit_ai_roles.py`.
  - `src/KickFlight.BootstrapApi/ExperimentalAiRoleService.cs`.
  - `tests/KickFlight.BootstrapApi.Tests/ExperimentalAiRoleTests.cs`, `tests/test_ai_role_*.py`.

---

## 2. Artifact & Signing Verification

### Target APK
- **Artifact Path:** `/home/gixarde3/Proyectos/KickFlight/Kick-Flight-Private-Server/.local/worktrees/ai-role-investigation/.local/artifacts/KickFlight-2.11.1-ai-role-targeting-signed-4b07.apk`
- **SHA-256 Digest:** `57b2d07f70489aa4b3b23901f7ca7dffd39d5e05ef903e384093bd80f22f15e9`
- **Certificate SHA-256:** `4B:07:93:DE:89:74:C2:EB:38:D1:98:41:9C:DA:2B:7A:26:93:FC:C6:0E:A0:09:FC:DA:B4:EE:8F:27:C8:DC:EB`
- **Signature Schemes:** v1=true, v2=true, v3=true (signed via matching canonical keystore `.local/kickflight-test-signing.jks`).
- **Telemetry APK (Baseline):** `.local/artifacts/KickFlight-2.11.1-ai-roles-signed-4b07.apk` (SHA-256 `3c55d631027a202122c972ec037fa711b947e8141b73b5f2e6a1b388f38373a6`).
- **Finalization Status:**
  - Build & Static Signing: Finalized.
  - On-Device Runtime Validation: PARTIAL / FAILED (APK installed with cache preserved; client navigated Title -> Home -> Matching -> GameScene and posted /battle/result, but 0 KFDIAG frames generated and Photon room connection did not complete; transition error UNKNOWN).
  - Exact Runtime Log Paths:
    - Logcat: `/home/gixarde3/Proyectos/KickFlight/Kick-Flight-Private-Server/.local/run/logcat-battle-latest.txt`
    - Photon: `/home/gixarde3/Proyectos/KickFlight/Kick-Flight-Private-Server/.local/run/photon.log`
    - Bootstrap API: tmux session `kf-server` / `/home/gixarde3/Proyectos/KickFlight/Kick-Flight-Private-Server/.local/run/server-api.log`
    - ADB Loop Notes: `/home/gixarde3/Proyectos/KickFlight/Kick-Flight-Private-Server/.local/adb-loop-notes.md`

---

## 3. Worker & Reviewer Progress Summary

1. **Native Targeting Reviewer:**
   - Evaluated candidate scoring call site at `0x18072D0` inside `AIPlayerEngine.UpdateTarget` (`0x180714C`).
   - Disassembly verified: `fcmp s0, #0.0; b.le bypass` guarantees friendly, dead, or out-of-range targets (which evaluate to `<= 0.0`) are never boosted.
   - Classification safety: ASCII prefix checks `0x74414941` (`AIAt`), `0x79616C50` (`Play`), and `0x4743504E` (`NPCG`) cleanly isolate Scramble Hunter (`17`) and Objective Attacker (`18`, `23`).
   - Documentation recorded in `docs/re-notes/ai-role-targeting-implementation.md`.

2. **Signer / Runtime Worker:**
   - Identified keystore mismatch (`53:44...` vs active package `4B:07...`).
   - Sourced canonical `4B:07...` test keystore from discs audit worktree.
   - Re-signed targeting candidate APK with valid v1-v3 signatures matching package `jp.grenge.kickflight`.
   - Documentation recorded in `docs/re-notes/ai-role-local-signing.md`.

---

## 4. Remaining Objectives & Technical Roadmap

1. **Low-HP Crystal Deposit:**
   - Extend `AIScrambleScoreEvaluationEngine` threshold logic so bots holding crystals prioritize base deposit when health falls below 30% regardless of primary offensive role.
2. **Guardian Defense, Escort & Ally Selection:**
   - Implement anchor coordinate weighting for Tank/Defender (ID 16) around friendly guardian positions.
   - Add ally escort / healer targeting logic for Support/Healer (ID 19).
3. **Native Flag Flight & Rapid Ball Roles:**
   - Map evaluation engines and hook points for `AIFlagBattleGameHelper` and `AIBallShootGameHelper`.
   - Assign and validate reserved profile IDs 28..40 (Flag) and 41..53 (Rapid).
4. **Live Device Validation:**
   - Deploy signed APK via `adb install -r`, capture logcat `KFDIAG` telemetry during bot battles, and verify candidate selection ratios in live match traces.
