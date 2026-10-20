# Required Client Update to Android 2.11.4

## Context and Root Cause
When Kick-Flight Android 2.11.4 (versionCode 59) was released in PR #46/#47, legacy 2.11.3 clients continued playing without being forced to update.
1. **Omitted Compose Environment Configuration**: `deploy/docker-compose.vps.yml` lacked `ClientUpdate__MinimumVersion` entirely. `deploy/docker-compose.vps.external-db.yml` still referenced legacy `2.11.1`.
2. **Persistent Stale Disk State**: Since PR #19, `/srv/repo/data` has been bind-mounted to `.local/maintenance/` on the host. The state file `client-update.json` persisted an active minimum version of `2.11.3`. Because `ClientUpdateState.Load()` only used configuration values to seed when no state file existed, redeployments never raised the active floor.
3. **Platform Isolation**: The existing gate lacked OS discrimination. Setting a global minimum of 2.11.4 would inadvertently refuse iOS clients (operating on 2.11.3 under asset version 12346).
4. **Headerless Auth Prepare**: `POST /auth/prepare` does not carry `x-app-application-version`. Unfiltered enforcement blocked authenticating clients before reaching `POST /auth/index`.

## Implementation Details
- **`ClientUpdateState.cs`**:
  - `ClientUpdateSnapshot` now supports platform-aware versions: `MinimumVersion` (Android/default) and `IosMinimumVersion`.
  - Added upgrade semantics on start: when the deployment configuration defines higher minimum versions than stale persisted state, `Load()` automatically elevates the floor and persists the update. Explicitly cleared gates (`""`) remain preserved.
  - `IsOutdated(version, isIos)` evaluates platform-appropriate thresholds (falling back to default minimum if iOS minimum is unspecified).
  - Loopback `/admin/client-update` supports optional `iosMinimumVersion` in GET and POST payloads.
- **`DemoSessionApi.cs`**:
  - `TryHandleForcedUpdate`: Bypasses `POST /auth/prepare` (identical to hard maintenance handling; subsequent `POST /auth/index` enforces the gate).
  - Detects iOS clients via `x-app-asset-platform == 2` or `x-app-platform == "iOS"`.
- **`deploy/docker-compose.vps.yml` & `deploy/docker-compose.vps.external-db.yml`**:
  - Set `ClientUpdate__MinimumVersion: "2.11.4"` and `ClientUpdate__IosMinimumVersion: "2.11.3"`.
- **Download Targets & Stock Dialog**:
  - The stock forced update popup opens `UPDATE_PAGE_URL` (`https://kick-flight-fenix.us.ci/`).
  - `/apk/remote` and `/apk/KickFlight-2.11.4-remote.apk` host Android 2.11.4 (SHA `008123a09affb6c70ada29715b24126d1f23b467e1dd0d2bd9f2197c74a34e24`).
