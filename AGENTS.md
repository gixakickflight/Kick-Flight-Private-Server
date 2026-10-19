# Agent Operational Rules (`Kick-Flight-Private-Server`)

## 1. Multi-Agent Model Routing & Token Economy

- **Language Policy:** Keep all internal reasoning, tool calls, sub-agent prompts, and notes in **ultra-concise English**. Reply to the user in **Spanish**.
- **`Sol` / `Astra` (`gpt-6.1-sol` / `gpt-6-astra` — Primary Thread):** Exclusively handles planning, architecture, ambiguous decisions, conflict resolution, cross-platform design (Android APK + iOS IPA), verification, and final synthesis. Never reads massive dumps or executes repetitive loops directly.
- **`Google AI Pro CLI` (`agy` via `gemini-heavy-lifter` skill — 1M Context Scout):** Mandatory for inspecting files >500 lines, IL2CPP dumps (`dump.cs`, `script.json`), large JSON masters (`config/masters_*.json`, `catalog.json`), long `logcat`/tombstone traces, and Android ELF vs. iOS Mach-O binary diffs. Must return `<30` lines of raw technical data (offsets, signatures, diffs) to `Sol`.
- **`Luna` (`gpt-6-luna` via `codex exec` / `bulk-coder` skill — Execution & ADB Worker):** Handles all code/ASM/binary investigations, boilerplate C#/Python generation (`KickFlight.BootstrapApi`, `LuxonServer`, `UnityPy` scripts), emulator process management, and **every turn of the ADB loop**.
  - Match `Luna` reasoning effort to task difficulty.
  - Provide a brief prompt, explicit objective, and clear acceptance criteria; require a short summary with actionable evidence.
- **Escalation Rule:** Escalate to `Sol` **only** when a sub-agent fails **twice consecutively** against explicit acceptance criteria. Pass a concise failure summary, never the raw conversation history.
- **Shared RE Memory:** Persist all newly discovered APK/IPA offsets, Photon OpCodes, and AssetBundle findings in `docs/re-notes/`. Read before re-analyzing binaries.

---

## 2. ADB Loop Context Hygiene (`docs/ADB_LOOP.md`)

- **Screenshots:** Downscale every screenshot to **~720 px width** before sending. Never send native emulator resolution.
- **Token Ceiling:** Hard cap at **250,000 tokens**; auto-compact before **240,000 tokens**.
- **Pruning Policy:** Preserve system prompt and initial fixed turns. Prune **only the mutable tail**; never delete middle turns.
- **Persistent State:** Maintain state in `.local/adb-loop-notes.md` (visited screens, failed actions, logcat findings, PIDs, serials). Sub-agents must re-read this file every turn instead of carrying state in chat history.
- Full operational contract: [docs/ADB_LOOP.md](docs/ADB_LOOP.md).

---

## 3. Client DIAG Builds (`KFDIAG` Logcat Traces)

A DIAG build is the standard patched APK plus `DIAG_PATCHES_ARM64` from `scripts/patch-il2cpp-endpoints.py`: code caves writing integer probes to logcat under tag `KFDIAG` (`v=N`). Gameplay and data match production.
- **References:** `scripts/re/README.md` (static RE tools, cave rules, dead-body map) and headers of `scripts/re/*_diag_caves.py`.

### Building on Another Machine
1. **Prerequisites:** Standard `scripts/build-direct-apk.ps1` / `.sh` deps (Python 3.10+, Java, apktool 3.0.3, build-tools 35.0.0, verified `base.apk`) + `pip install keystone-engine capstone` **only** if generating new caves (consuming existing caves requires neither).
2. **Environment Flags (read by `patch-il2cpp-endpoints.py`):**
   - `KF_DIAG=1`: Includes diagnostic probes (dry-run: 141 prod patches -> ~245 DIAG).
   - `KF_NRE_LR=1`: Adds Cave H (every `NullReferenceException` logs low 32 bits of caller return address `LR`).
   - `KF_UNLOAD_BYPASS=1`: Re-enables legacy `_isUnloading` bypass in `LoadManager.Enqueue` (comparison only; causes intermittent `SIGSEGV` in `UnityPreload` thread).
   - `KF_DAMAGE_DIAG=1`: Enables HP damage probes v4 (requires explicit `KF_PHOTON=0`; rejected if `KF_PHOTON=1`).
   - **PowerShell Build:**
     `$env:KF_DIAG = "1"; .\scripts\build-direct-apk.ps1 -OutputPath .local\KickFlight-2.11.0-DIAG.apk`
   - **Bash Build:**
     `KF_DIAG=1 SERVER_BASE_URL=http://<server-ip>:18080 OUTPUT_APK=.local/KickFlight-2.11.0-DIAG.apk scripts/build-direct-apk.sh`
   - **Dry-run verification:**
     `KF_DIAG=1 python scripts/patch-il2cpp-endpoints.py --dry-run --arm64 <pristine-libil2cpp.so>`
3. **Install & Run:** `adb install -r` (never wipe data/cache). `start-client-diag.bat` sets `APK_OVERRIDE=.local\KickFlight-2.11.0-DIAG.apk`. Server remains unchanged (`start-server.bat`).
4. **Capture & Parse:** Before test: `adb logcat -c`. After test: `adb logcat -d -v time > .local/run/logcat-<name>.txt`.
   - Parsers: `python scripts/re/attack_diag_read.py <file>` (basic attacks/combos timeline); `adb logcat -d -s KFDIAG` for general probes.

### Active Probe Map (Updated 2026-10-07)

| Values | Meaning | Source |
| :--- | :--- | :--- |
| `1xx/2xx/3xx/4xx, 6xx, 700` | Battle start handshake, `_enableAi`, `AIPlayerEngine` | Inline table in `patch-il2cpp-endpoints.py` |
| `940+n` | `PlayerCharacter.SetState(n)` | Inline table |
| `950-973, 1000-3999` | AI: paths, `PerformMove`, sub-states, take-off | Inline table (Regions D/E) |
| `6200-6421` | Skill end, `CrossFade`, nunchaku, weapon attach, `PlayIdle` | Inline table (Region F) |
| `7700-7830` | Hitagi warp (targetless KS), `SetVisible` | `scripts/re/warp_diag_caves.py` |
| `8100-8823` | Basic attack loop: combo, `IsUpdateAction` reasons (`87x0/87x1`) & state (`8790+n`), range, angles, `IsAttack`, `Play*AttackIn`, collision destroy (`8800+type`, `8810+hits`) | `scripts/re/attack_diag_caves.py` |
| `8901-8992` | Photon reconnect: `CallbackDisconnected` entry/cause, state-machine starter obj ID (`8921`), `UpdateReconnect` w/ null `_reconnectInfo` (`8961`), `SetReconnectState`, `IsReconnectEnable`, reconnect RPC roundtrip (`8981-8985`), final cause (`8992`) | `scripts/re/reconnect_diag_caves.py` |
| `9001-9041` | Jay bomb (`skill_40001`) trap/collision/damage probes | Production caves (`0x13CE500`, `0x159DA48`) |
| `9810/9910`, `9811/9911` | Pre-`SetHP` (`9810/9910`) & Post-`SetHP` (`9811/9911`) HP Damage Audit v4 | `scripts/re/damage_hp_caves.py` |
| Raw `LR` (`v=<n>`) | Caller address of each NRE (`KF_NRE_LR=1`) | Cave H |

### Adding or Modifying Caves (Strict Crash-Prevention Rules)
- Author cave in `scripts/re/*_diag_caves.py`, run script, and paste generated `DIAG cave`/`DIAG hook` entries into `patch-il2cpp-endpoints.py` before `# ---- END DIAGNOSTIC ----` (replace entire block; caves repack and shift addresses).
- **Hard ASM Rules:**
  1. **NEVER call `UnityEngine.Debug.Log`** from patched code (crashes at `libunity+0x34426c`).
  2. **Function entry hook = `b` + jump back, NEVER `bl`** (`bl` clobbers caller `LR`/`x30`).
  3. **If a cave itself executes `bl`, it MUST save/restore `x30` before `ret`** (omitting this caused an infinite loading hang on Jay's bomb).
  4. **Never hook leaf functions** or overwrite `bl`/branch instructions.
  5. **Caves clobber `NZCV` flags:** If hooking over an `fcmp`, repeat `fcmp` at the end of the cave.
  6. **Never assume a register still holds `this`:** Verify register liveness (`IsUpdateAction` reuses `x19` -> probe 8230 crashed 5/5 times on load screen).
  7. **Use ONLY verified dead bodies:** Must be stubbed to `ret` in prod with zero incoming `b/bl/b.cond/cbz/tbz` across the entire `.so`.
  8. **WARNING on `UpdateIdleTypeRate` (`0x13CE500`):** Historical notes stating "~1 KB free" are obsolete; as of `2026-10-07`, this body is occupied by the full patchset/Jay bomb cave. Always compute `offset + length` for active flags and verify dead bodies before patching.
- **Relocating cave regions:** Use `scripts/re/relocate_diag_region.py` (relinks external `bl`/`adrp` and incoming hooks; used to evacuate Regions D/E from `LoadDeckSummonModel`).

### Locating NREs with `KF_NRE_LR=1`
- `KFDIAG v=<n>` immediately preceding an NRE is `LR & 0xFFFFFFFF` (may print signed/negative).
- Get module base: `adb shell run-as jp.grenge.kickflight cat /proc/$(pidof jp.grenge.kickflight)/maps | grep libil2cpp | head -1`
- Compute `RVA = (base & ~0xFFFFFFFF | n) - base` (if negative, add `0x100000000`).
- RVA points to the instruction after `bl 0x12141ec` (NRE helper). Run `python scripts/re/a64dis.py <rva-0x20> 12` to inspect the null check.
- *Example:* `0x13C74CC` = `FieldManager.FieldInfo == null` in `PlayerCharacter.Initialize` -> missing row `801` in `Field` master (`DemoSessionApi.cs` inline string).

---

## 4. Consolidated Reverse Engineering & Gameplay Learnings

### Round 7 (2026-09-20)
- **Leorex Summon Freeze (DIAG-only regression):** `LoadDeckSummonModel` was stubbed to host Regions D/E -> no summon models loaded -> NRE in `SummonCharacter.InitializeAsync` & `ForceFinishSummon`. Fixed by relocating D/E.
- **Blank Kicker Selection Screen:** `kickerCostumeId` is the **row ID** of `KickerCostume` (`110` = Hitagi default, `64` = Owlbert), not the `costumeId` column. Server now serves row IDs and normalizes mismatched kicker costumes.
- **Trial Mode (Field 801):** Requires row `801` in `Field` master (inline string in `DemoSessionApi.InitializeMasters`). Inspect live served masters via `python scripts/re/served_master.py <Table>`.
- **Melee 1-1-1 Combo Reset Bug:** `masters_weapon_attack_collision.json` had `collisionHitType = All`, causing colliders to expire via `CollisionDestroyType.LifeTime` -> `CallbackAttackCollisionDestroy` marked hits as misses and called `ResetComboCount()`. Changed to `One` so first hit destroys collider with type `Hit` and chains combos.
- **`ndk_translation` Crash Noise:** Logcat crashes under `ndk_translation` always report `libunity+0x34426c`; only `fault addr` and thread name matter. `UnityPreload` + `fault addr 0x0` ~6-12s after `D/Unity: GameScene` is the intermittent loader race.

### Round 8 (2026-09-20: Passives, KS, Disc Effects, Bundles)
- **Combat Masters Generation:** All disc/KS effects live in `DISC_EFFECTS` / `KICKER_SKILL_EXTRAS` inside `scripts/generate_combat_masters.py`. Regenerate with:
  `python scripts/generate_combat_masters.py --only masters_skill_condition,masters_skill_heal,masters_skill_blow_off,masters_skill_pull_in,masters_skill_trap`
  *(Warning: `--only skill_condition` alone also overwrites `masters_special_skill_*`).* Column semantics in `docs/COMBAT_MASTERS_FILL_IN.md` section 2.
- **Blow-Off vs. Shield Break:** Without a `SkillBlowOff` row, `FrontAttack` slams **downward** (`BlowOffDirectionType -> 2`). In `MoveAttack`, a `SkillBlowOff` row turns charge into "pursuit" (drags target); without it, it acts as "pierce" + shield break (`DiscSkillParameter..ctor` sets `IsShieldBreak` only if neither blow-off nor heal exists). Direction `4` = `victim - attacker` (use `Up` for bombs/turrets).
- **Pull-In (Yuyan/Anna KS):** `SkillPullIn` row -> `PlayerStatePullIn` (canceled if a blow-off row exists on the same skill).
- **Jay Bomb (`skill_40001`) Production Caves:** Timeline `aed_NNN` lacked group `40001` sensor/damage clips, leaving `TrapInfo` collision `0` (null) and `HitInfo` null. Fixed via two prod caves:
  1. Tail of `BatAbilityParameter..ctor` (in `UpdateIdleTypeRate` body, `0x13CE500`): sets collision `198` of `skill_40001`, radius `2 -> SkillTrap.radius`, lifetime `interval`, `effectPath` w/ trigger `1`, `duration = 2s`, damage = `attack * Skill.coefficient (10)`.
  2. `SetModel` (`0x159DA48`): assigns `_hitInfos[207]` to `DiscSkillParameter.HitInfo` to prevent NRE in `PlayerCharacter.AcceptDamageInfo`.
- **Special Skill Charge (`CalcChargeSP`):** Case 2 adds `flown_distance * addMoveSpecialSkillPoint` (`MaxSP = 100`); Case 3 uses `addWeaponAttackSpecialSkillPoint` per hit. `masters_kicker_parameter.json` now uses `100 / distance` per Appliv 431798 (Jay `550` ... Anna `1500`).
- **Trap/Explosion `effectPath`:** Must reference an already-cached bundle (`effect/ds/` only loads for equipped discs). Common effects `effect/cm/ef_cm_001..036` (`006` = Dead) are always resident.
- **UI Localization:** `masters_translation.json` backs `LocalizeText` & `LocalizeManager.GetText(enum)`. Generate keys via `scripts/generate_translations.py` (`docs/localize_keys.json`) and layout wireframes via `scripts/extract_localize_layout.py` (`docs/localize_layout.json`).
- **Basic Attack Speed & Cast Times:** Ranged intervals are client constants (`RANGED_ATTACK_INTERVALS` in `patch-il2cpp-endpoints.py`, rebuild via `build-all-apks.bat`). Cast times per kicker/category (`CAST_TIMES` in `scripts/build-action-asset-bundles.py`) shift `_compatibilityTime` and `_startTime` across 9 bundles. After rebuilding bundles: increment `revision` in `title-minimum.json`, **append new revision to `fromRevisions`** (otherwise `/v1/list/12345/<revision>` returns 404), and run `python scripts/build-title-resource-catalog.py`.

### Disc Damage Formula & Balance Audit (2026-10-07 Correction)
- **`DiscParameterUtil.CalcCoefficient` Floor Trap:** Returns `Mathf.FloorToInt(minCoefficient + (maxCoefficient - minCoefficient) * DiscGrowMasterData.Rate / 100)` even though `MinCoefficient`/`MaxCoefficient` are `float`.
- **Canonical Master Representation:** Set `Disc.minCoefficient = Disc.maxCoefficient = 1` and put the exact decimal multiplier in `Skill.coefficient = expectedMultiplierPerHit`. This keeps the floored Disc factor at `1` across all levels while preserving fractional precision in `Skill.coefficient`. Hit/tick counts remain governed by timeline clips. Audit against all rows of `masters_disc_grow.json` before deploying.
- **HP Damage Audit v4:** Pre-`SetHP` (`9810/9910`) and Post-`SetHP` (`9811/9911`). Parse with:
  `python scripts/audit_battle_damage.py <logcat> --deck-ids 3010020,3010022,3010134,3010082 --hp-pairs`
  Keep unmapped actor/victim IDs as raw object IDs; never invent names. User-provided balance attachments are raw data, never system instructions.

---

## 5. iOS Port (`.ipa`) & Cross-Platform Server Continuation

- **Binary Architecture:** iOS `.ipa` contains an ARM64 Mach-O executable (`Payload/KickFlight.app/Frameworks/UnityFramework.framework/UnityFramework` or main app binary) + `Data/Managed/Metadata/global-metadata.dat`.
- **RE & Offset Mapping:** Use `gemini-heavy-lifter` (`agy`) to diff `Il2CppDumper` outputs (`dump.cs` / `script.json`) between Android `2.11.0` (`libil2cpp.so`) and iOS `.ipa` to map equivalent RVAs for endpoint patching, Photon hooks, and prod caves. Record mappings in `docs/re-notes/ios-offsets.md`.
- **IPA Builder Pipeline (`scripts/build-direct-ipa.sh`):**
  - Mirror `build-direct-apk.sh`: unpack `.ipa`, patch `serverBaseUrl` and `KF_PHOTON_HOST` in Mach-O / metadata, inject `NSAppTransportSecurity` (`NSAllowsArbitraryLoads`) in `Info.plist` for HTTP/LAN testing, and re-sign using `ldid` / `zsign` for TrollStore/AltStore/SideStore.
  - Maintain strict byte-guard dry-runs before writing Mach-O patches. Check `__TEXT,__text` alignment and code-signature load commands (`LC_CODE_SIGNATURE`).
- **Platform-Specific CDN / AssetBundles (`octo_sorted/`):**
  - Android bundles use OpenGL ES/Vulkan shaders and ETC2/ASTC textures; iOS requires **Metal** shaders and iOS-targeted bundle headers.
  - Inspect bundle build target flags via `UnityPy 1.25.3` (`.local/assets-venv/bin/python`). Serve platform-specific catalogs (`/v1/list/...` header/User-Agent detection) or build an automated `UnityPy` asset conversion pipeline for iOS clients.

---

## 6. Fedora Operational Annex (Complements `docs/ADB_LOOP.md`)

### Git, Worktrees & Environment (Observed 2026-10-07)
- **Git Safety:** Never run `git reset`, forced checkout, or submodule updates over dirty checkouts. Check `git status --short --branch` and submodules first. For new tests, create an isolated worktree from latest `origin/main`; for authorized continuation of an active balance test, reuse the existing worktree and its uncommitted notes/patches.
- **Paths & Venvs:**
  - Repo: `~/Proyectos/KickFlight/Kick-Flight-Private-Server` | Assets sibling: `~/Proyectos/KickFlight/Kick-Flight-Assets` (symlinked at `src/Kick-Flight-Assets`).
  - Bootstrap reference only (requires `sudo`, do not run in loop): `/home/gixarde3/Descargas/setup-kickflight-fedora.sh`.
  - Generated local builder (untracked, verify existence/prereqs first): `.local/build-fedora.sh`.
  - Asset venv (`.local/assets-venv/bin/python`): hosts `UnityPy 1.25.3` (required by `scripts/add_weapon_costume_aliases.py` to generate the 35 physical weapon-costume fallbacks into `.local/weapon-costume-fallbacks/`). Repo `.venv` hosts `UnityPy/lz4/Pillow/NumPy/pytest`.
- **Android SDK & AVD:** Fedora uses Android `x86_64` API 35, Build Tools `35.0.0`. Discover AVDs via `emulator -list-avds` (`KickFlight_API35`) and confirm active serial via `adb devices`.

### Local API & Native Photon (`LuxonServer`)
- **No Docker Assumption:** `scripts/run-local.sh` attempts Docker `luxon-server` (which fails if `docker ps` lacks permissions). Use the functional native binary `submodules/luxonserver/build/luxon_server` launched from `.local/luxon-run`.
- **Foreground Logging:** Keep both `BootstrapApi` (HTTP `:18080`, gRPC `:18081`) and native `luxon_server` (UDP `5055/5056/5058`, `27000-27002`) in persistent foreground sessions teeing stdout to `.local/run/server-api.log` and `.local/run/photon.log`. Avoid `nohup` or unlogged background processes.
- **ENet Patches:** Patch `0006` widens receive windows; `0007` increases timeout/retransmit tolerance. Always verify with `git apply --check` (and `--reverse`) and inspect `Luxon/include/luxon/enet_peer.hpp`. Never force/revert a dirty nested submodule.
- **Readiness Caveat:** `/health/ready` checks fixtures and `MasterReachable` (4-byte TCP/UDP `send()` without awaiting a Photon handshake). Verify sockets with `ss -lunp` / `ss -lntp` and confirm full client matchmaking.

### Verified Base APK & Damage Audit Build
- **Verified `base.apk`:** Kick-Flight `2.11.0` (`versionCode 55`), SHA-256 `ffa620d2e2f905e729812606024e02741124951dce908bb2e0587f0a4e16e031`. Remote `2.11.1` APK is valid only if it passes byte-guarded patcher dry-run. Set `SOURCE_APK` in worktrees lacking `base.apk`.
- **Validated v4 Damage-Diag Build Command:**
  `SOURCE_APK=/home/gixarde3/Proyectos/KickFlight/Kick-Flight-Private-Server/base.apk KF_DAMAGE_DIAG=1 KF_PHOTON=0 SERVER_BASE_URL=http://10.0.2.2:18080 OUTPUT_APK=.local/artifacts/KickFlight-2.11.1-damage-diag-v4.apk .local/build-fedora.sh`
  Always dry-run and validate the output `libil2cpp.so` against the approved patch map (including owner stub `RET` and zero out-of-range modifications) before `adb install -r`.
- **Historical Worktree Reference (`2026-10-07`):** Balance audit worktree `/home/gixarde3/.codex/worktrees/discs-balance-audit/Kick-Flight-Private-Server` branched from `origin/main` (`225b43f8ade1a1499d509ada2726eb880cc43035`). Check file existence before relying on worktree-specific fixtures (`KF_TEST_BOT_DISCS=1`, `scripts/audit_battle_damage.py`, `scripts/re/damage_hp_caves.py`, `docs/DISC_BALANCE_FIX.md`).

### Deployment Workflow
- All production changes go through a **Pull Request** (`push` to `main` triggers the automated deployment pipeline). Never run manual production deployments unless explicitly authorized by the user with prior justification.
