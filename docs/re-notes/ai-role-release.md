# Android Release 2.11.4 & AI Roles Distribution Notes

## 1. Build & Runtime Flags

- **Client / Patcher Flags (`scripts/patch-il2cpp-endpoints.py`):**
  - `KF_PHOTON=1`: Enables production VPS Photon host and port routing (`10.0.2.2` / VPS IP).
  - `SERVER_BASE_URL=https://kick-flight-fenix.us.ci`: Configures live HTTPS API endpoint.
  - `KF_AI_ROLES=1`: Hooks `AIPlayerEngine.UpdateTarget` and injects telemetry cave at `HomeSummonModelController.SetModel` (`0x159D410..0x159D6DC`).
  - `KF_AI_ROLE_TARGETING=1`: Injects Scramble hunter (17) vs. objective (18/23) candidate weighting cave (`0x159D6E0..0x159D854`). Prerequisite: `KF_AI_ROLES=1`.
  - `KF_AI_ROLE_DEPOSIT=1`: Injects Scramble carrier low-HP deposit threshold cave (`0x159D858..0x159D960`). Prerequisite: `KF_AI_ROLES=1`.
  - `KF_DIAG=0`: Production build. Mutually exclusive with `KF_AI_ROLES` (both compete for `SetModel` dead-body window `0x159D410..0x159DA44`).
- **Server Flag (`deploy/docker-compose.vps.yml` / `src/KickFlight.BootstrapApi`):**
  - `KF_EXPERIMENTAL_AI_ROLES=1`: Serves 39 mode-aware bot master rows across Scramble, Flag Flight, and Rapid Ball for parameter IDs 15..53.

## 2. Signing Certificate

- **Keystore / Certificate Identity:** Official release signing certificate with SHA-256 fingerprint starting with `81ca..`.
- **Compatibility:** Guarantees seamless in-place updates (`adb install -r` / client auto-update) across 2.11.x releases without signature mismatch errors.

## 3. Reproducible Build Command

Executed by artifact worker on isolated build environment:
```bash
SOURCE_APK=base.apk \
KF_PHOTON=1 \
KF_AI_ROLES=1 \
KF_AI_ROLE_TARGETING=1 \
KF_AI_ROLE_DEPOSIT=1 \
SERVER_BASE_URL=https://kick-flight-fenix.us.ci \
OUTPUT_APK=.local/artifacts/KickFlight-2.11.4-remote.apk \
./scripts/build-direct-apk.sh
```
- Signed with release keystore (`cert81ca..`).
- Resulting binary staged to `.local/artifacts/KickFlight-2.11.4-remote.apk`.

## 4. PR-Driven Distribution Pipeline

All external production writes occur exclusively through GitHub Actions CI upon merging PRs to `main`:
1. **Catalog Truth:** Reads trusted catalog row `apk-remote-2114` in `config/resources/catalog.json`. If not present or disabled, artifact distribution step exits cleanly (status `not_configured`).
2. **Idempotent VPS Hash Check:** Using pinned SSH (`VPS_KNOWN_HOSTS`), queries `$VPS_APP_DIR/.local/KickFlight-2.11.4-remote.apk`. If exact SHA-256 matches, download and upload are skipped.
3. **Download & Runner Verification:** Downloads asset `KickFlight-2.11.4-remote.apk` from GitHub Release tag `android-2.11.4`. Verifies SHA-256 against catalog row.
4. **Focused Artifact Validation (`scripts/validate-apk-artifact.py`):**
   - Validates ZIP archive integrity and minimum size (>50 MB).
   - Validates existence of `AndroidManifest.xml`, `classes.dex`, and `lib/arm64-v8a/libil2cpp.so`.
   - Inspects `libil2cpp.so` header: validates 64-bit ARM64 ELF magic (`\x7fELF`, class 2, `e_machine == 183`).
   - Validates presence of `META-INF/` signature entries.
5. **Atomic VPS Staging:** SCPs to staging temp file (`$VPS_APP_DIR/.local/.KickFlight-2.11.4-remote.apk.staging-${GITHUB_SHA}`), re-verifies SHA-256 on VPS, atomically renames (`mv -f`) to `.local/KickFlight-2.11.4-remote.apk`, and sets permissions `644`.
6. **Pre-Activation Execution:** Staging completes BEFORE `scripts/deploy-vps-ci.sh` runs. When `deploy-vps-ci.sh` runs, `build-cdn-tree.py --verify-all-enabled --dry-run` validates the staged APK against `catalog.json` through the `.local` symlink.

## 5. Rollback & Fault Isolation

- **Pre-Service Safety:** Any failure in GitHub release download, SHA verification, artifact structure inspection, or remote transfer aborts deployment before service restarts or CDN modifications.
- **Artifact Preservation:** The VPS `.local/` directory is persistent and excluded from `sync_release` (`rsync --exclude='/.local'`). Previous APKs (`KickFlight-2.11.3-remote.apk`, `2.11.2`, `2.11.1`) are preserved.
- **Service Rollback:** If container startup or readiness fails during `deploy-vps-ci.sh`, automated trap restores previous release SHA and CDN state; old client links remain valid and operational.

## 6. Limitations & Finalize Pending Status

- **CI does not build the APK:** Building the APK requires Java, apktool, Android build-tools, and Keystone/Capstone. Artifact worker builds APK independently.
- **Manual SSH deploy forbidden:** Production deploys only via GitHub Actions `push: branches: [main]`.
- **Finalize Pending Items (NEVER use placeholder hashes):**
  1. Build final `.local/artifacts/KickFlight-2.11.4-remote.apk` with `cert81ca..` and `publicPhoton1`.
  2. Compute exact SHA-256 and byte size of final artifact.
  3. Coordinator creates GitHub Release tag `android-2.11.4` with asset `KickFlight-2.11.4-remote.apk`.
  4. Add catalog row `apk-remote-2114` to `config/resources/catalog.json` with real `sha256` and `sourcePath: .local/KickFlight-2.11.4-remote.apk`.
  5. Update visible version and download link in `deploy/site/index.html` to 2.11.4.

## 7. Announcement & Latest Information Delivery

- **Standalone Web Announcements (`deploy/nginx.vps.conf`):**
  - Restricted version regex route `location ~ ^/updates-[0-9]+\.[0-9]+\.[0-9]+.*\.html$` maps `/updates-2.11.4.html` directly to the static site root `/usr/share/nginx/html/` with `try_files $uri =404` and `Cache-Control: public,max-age=3600`.
  - Preserves exact precedence for `location = /updates` (proxied to API webview `/webview/information/index`) and `location = /updates.html` (301 redirect).
  - Validated by automated deployment smoke test checking SHA-256 against `deploy/site/updates-2.11.4.html`.
- **In-Game Latest Information (`scripts/update-latest-information.py`):**
  - Targets live persistent host file `$app_dir/.local/maintenance/latest-information.json` (`/srv/repo/data/latest-information.json` inside API container).
  - Idempotently extracts `<article class="patch" id="v2-11-4">` from `docs/updates-page.html` and inserts it ahead of existing articles.
  - Strictly preserves all prior live articles (`matchmaking-roles-2026-10-19`, `v2-11-3`, `v2-11-2`) and snapshot metadata (`content`, `externalUrl`, `htmlResourceCsp`, `defaultContent`).
  - Supports both camelCase and PascalCase record JSON casing.
  - Fails closed without modifying disk on non-HTML mode (`mode != "html"`), corrupt JSON, or malformed HTML.
  - Automatically creates initial valid template if state file is missing on clean installs.
  - Pre-write snapshot backup automatically created in `.local/updates-backups/latest-information-backup-<timestamp>.json`.
  - Atomic write via temporary file with preserved permissions (`chmod` + `os.replace`).
  - File locking (`fcntl.flock`) concurrency protection.
  - Automatically invoked during `scripts/deploy-vps-ci.sh` immediately after source synchronization, before API service recreation.
  - Restores backup automatically in `rollback_on_error` if subsequent deployment steps fail.

