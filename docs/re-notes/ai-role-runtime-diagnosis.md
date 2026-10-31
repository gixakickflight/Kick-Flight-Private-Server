# AI Role Runtime Diagnosis — Log & Roster Trace

**Worktree:** `/home/gixarde3/Proyectos/KickFlight/Kick-Flight-Private-Server/.local/worktrees/ai-role-investigation`  
**Branch:** `research/ai-role-patching` (commit `33d38a7`)  
**Target APK:** `KickFlight-2.11.1-ai-role-targeting-signed-4b07.apk` (SHA-256 `57b2d07f...`, 243 patches)  
**Date:** 2026-10-19  

---

## 1. Workspace & Main Advancement Audit
- **Root Repository (`main`):** `0db556f`, behind `origin/main` by 4 commits (`65c9e21`, `1b37c6b`, `7c8e1a0`, `33d38a7`).
- **Advancement Summary:** English matchmaking news, Scramble team composition, and mode-aware bot deck extensions landed on `origin/main`.
- **Worktree State:** `.local/worktrees/ai-role-investigation` is on `research/ai-role-patching` at `33d38a7` (up to date with `origin/main`). Dirty tracked files (`scripts/patch-il2cpp-endpoints.py`, `BattleMatchmakingService.cs`, `DemoSessionApi.cs`, `ScrambleBotCompositionSelector.cs`, `_identities.json`) and untracked research tests/notes fully preserved.

---

## 2. Server Endpoint & Roster Generation Trace
- **Observed Lifecycle:**
  1. `22:54:35.864Z`: `POST /battle/entry` (200 OK) -> ticket `ticket-a5b9834af` created for user `1000027`.
  2. `22:54:35.963Z`: gRPC `OpenMatchFrontendService.GetAssignments` stream attached; room `battle-792449651` created.
  3. `22:55:16.013Z`: Matchmaking window closed with 1 human (`1000027`); `BattleMatchmakingService.BuildRoster` invoked to fill slots 2..8 with bots.
  4. `22:55:18.015Z`: Stage 3 assignment sent; stream closed by client.
  5. `22:55:28.031Z`: `POST /battle/start` (200 OK).
  6. `22:58:50.935Z`: `POST /battle/end` (200 OK, 180s full match duration).
  7. `22:58:53.760Z`: `POST /battle/result` (200 OK, entry `be-e4eb87508`, rule 1).
- **Roster Path Discrepancy:**
  - `BattleMatchmakingService.BuildRoster` was called (NOT Demo/practice fallback).
  - However, `ScrambleBotDeckCatalog.Load` logged:  
    `"Scramble bot deck catalog is missing at /home/gixarde3/.../src/KickFlight.BootstrapApi/config/scramble_bot_disc_decks.json; legacy bot decks remain active"`.
  - Because `_scrambleBotDecks.Count == 0`, `useModeAwareComposition` evaluated to `false`.
  - `BuildRoster` fell back to legacy bot loop (`BattleMatchmakingService.cs:1342`):  
    `kickerAiParameterId = gym ? GymAiParameterBase + profile.KickerId : profile.KickerId`.
  - Actual `aiParamIds` sent: **1** (human) and **2..8** (bots from `BotProfiles`). Zero experimental role IDs (`15..53`) were assigned.

---

## 3. Root Cause Analysis: 0 KFDIAG Frames
- **Primary Root Cause (Rank 1): Server Catalog Path Resolution Failure**
  - Location: `src/KickFlight.BootstrapApi/ScrambleBotDeckCatalog.cs:11`
  - Cause: Catalog path resolved relative to `contentRoot` (`src/KickFlight.BootstrapApi`), where `config/scramble_bot_disc_decks.json` does not exist (it resides at repo root `config/scramble_bot_disc_decks.json`).
  - Consequence: Fallback to legacy roster assigned bot `kickerAiParameterId` in `2..8`.
  - Client Native Cave Check: In `scripts/re/ai_role_cave.py:108` (`0x159D410`):
    ```armasm
    ldr w23, [x22, #0xc4]   // PlayerCharacter._kickerAiParameterId
    cmp w23, #15
    b.lt lbl_bypass
    cmp w23, #53
    b.gt lbl_bypass
    ```
  - Because all bot parameter IDs were `< 15`, every call to `AIPlayerEngine.UpdateTarget` branched to `lbl_bypass`, suppressing all 75xx telemetry frames.
- **Secondary Factor (Rank 2): General Diagnostic Probes Omitted**
  - Target APK `libil2cpp.so` was verified: `0x1A2A744` (DIAG hook 1) contains pristine `f40300aa` (`KF_DIAG=0`). General 1xx..4xx state probes were intentionally not included in this build.

---

## 4. Root Cause Analysis: PhotonGameServer Not Joined
- **Primary Root Cause (Rank 1): APK Built With Offline Bridge (`KF_PHOTON=0`)**
  - Binary Inspection: `0x1570164` contains `c0035fd6` (`RET`), proving the offline bridge patch was applied.
  - Architecture: Per `scripts/patch-il2cpp-endpoints.py:63`, `KF_PHOTON=0` bridges matchmaking directly into local Unity match execution without joining `PhotonGameServer` on port 5056.
  - Evidence: `photon.log` showed Peer 2 connecting only to NameServer (`5058`) at `16:54:08` for key exchange and disconnecting cleanly at `16:54:12`. No GameServer connection was ever attempted by the client.

---

## 5. Master Revision & Environment State
- **Server Environment:** `KF_EXPERIMENTAL_AI_ROLES=1` was active in process PID 60460.
- **Master Generation:** `DemoSessionApi` loaded 84 tables and generated master hash `demo-master-98e694a64fcb8b94`.
- **Client Cache:** Client sent `x-app-master-hash: demo-master-98e694a64fcb8b94` matching the server exactly. Local response cache was valid; no runtime master re-download was required.

---

## 6. Root Cause Resolution & Implementation Details
- **Fix Applied:**
  1. `src/KickFlight.BootstrapApi/ScrambleBotDeckCatalog.cs`:
     - Added robust `ResolveCatalogPath(contentRoot)`:
       - Retains explicit file path precedence (`File.Exists(contentRoot)`).
       - Checks `contentRoot/config/scramble_bot_disc_decks.json`.
       - Employs `RepositoryPaths.Resolve(RelativePath, contentRoot)` upward solution walk for repo-source content roots (resolves `config/scramble_bot_disc_decks.json` at repo root when invoked with `src/KickFlight.BootstrapApi`).
       - Falls back to `AppContext.BaseDirectory/config/scramble_bot_disc_decks.json` for binary-local execution.
       - Missing catalog retains default relative path for bounded diagnostic warning and returns empty list `[]`.
  2. `src/KickFlight.BootstrapApi/KickFlight.BootstrapApi.csproj`:
     - Added `<Content Include="..\..\config\scramble_bot_disc_decks.json" Link="config\scramble_bot_disc_decks.json" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />` for standalone publish and build output availability.
  3. `src/KickFlight.BootstrapApi/BattleMatchmakingService.cs`:
     - Emits explicit bounded diagnostic log warning in constructor and at `BuildRoster` time when `ExperimentalAiRoleService.IsEnabled && _scrambleBotDecks.Count == 0`, preventing any silent claim of active roles when catalog is unavailable.
  4. `tests/KickFlight.BootstrapApi.Tests/ExperimentalAiRoleTests.cs`:
     - Added 4 regression tests: `NestedAppContentRoot_LoadsCatalogFromRepoRoot_BuildRosterAssignsNonLegacyRoleIds15To53`, `CatalogPathResolution_ExplicitRequestedPath_RetainsPrecedence`, `CatalogPathResolution_TrulyAbsent_FallsBackToEmptyAndLegacyRoster`, `BuildRoster_PreservesDefaultOffAndGymBehavior`.
- **Roster Bot Identification (Actual current API trace):**
  - Human player `1000027` (kicker 1).
  - 7 Bots actual kicker IDs: 5 (Owlbert), 12 (Buzzy Big), 10 (Yuyan), 14 (Sid), 2 (Ruriha), 3 (Coco), 4 (Kite).
  - Previous legacy run assigned `kickerAiParameterId` = `[5, 12, 10, 14, 2, 3, 4]`.
  - Mode-aware composition with 52 decks assigns role-specialized IDs in `15..53`.
