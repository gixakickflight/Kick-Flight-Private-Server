# Android 2.11.5 publication checklist

## Status: AUTHORIZED_BY_OWNER_EXCEPTION (STARTUP & CONNECTIVITY VERIFIED)

Explicit owner instruction overrides the earlier full-battle release gate for THIS release (2.11.5) only:
- Owner confirmed maintenance was disabled (`Desactivado`) and issued immediate publication directive: `"No, cno eso es suficiente, publicala de una vez"`.
- Owner explicitly accepts the verified on-device startup (`TitleScene` reached, first-run overlay dismissed, TAP START accepted) and live VPS connectivity proof (`POST /boot/index` 200 OK with `accepted-boot-index-d2c`, `POST /auth/prepare` 200 OK) as sufficient for publication.
- **Honest Test Scope Limits:** Auth, lobby, matchmaking, and battle were **NOT verified** in this run due to the earlier 503 maintenance window; **no fake full runtime PASS is claimed**.
- **Future Releases Policy:** The mandatory full startup-login-matchmaking-battle runtime test gate is preserved and remains strictly required for all future releases.

## Candidate identity

- Version: Android 2.11.5, versionCode 60
- Artifact: `.local/artifacts/KickFlight-2.11.5-remote.apk`
- SHA-256: `ae7b4ed16f4544b18348ed5068f02c3a582ed7cbb3b64d46fe9811a704405f4f`
- Signing certificate SHA-256: `81ca6334fd069edbc8756a49fc135c91141eba8265cb64d0510ed9b231b21537`
- File size: 149,876,184 bytes
- Build flags: `KF_PHOTON=1`, `KF_AI_ROLES=1`, `KF_AI_ROLE_TARGETING=1`, `KF_AI_ROLE_DEPOSIT=1`, `KF_DIAG=0`, `KF_DAMAGE_DIAG=0`, `KF_RESULT_DIAG=0`
- Configured API/resource endpoint: `http://159.54.134.159:18080`
- Configured Photon host: `159.54.134.159`

## Runtime probe findings (2026-10-20)

- Dedicated test AVD `KickFlight_VPSTest` on port 5556 initialized with host GPU (RTX 4060, Vulkan disabled, QEMU PID 342286, `sys.boot_completed=1`).
- Exact candidate APK installed via `adb -s emulator-5556 install -r` (preserving test octo cache); on-device hash and certificate verified.
- Client cold boot reached `TitleScene`. Android first-run immersive cling overlay dismissed, and TAP START was accepted.
- Live VPS telemetry audit (`159.54.134.159:18080`, container `deploy-api-1`):
  - `POST /boot/index`: HTTP 200 OK (`accepted-boot-index-d2c` at 05:21:30Z).
  - `POST /auth/prepare`: HTTP 200 OK (90 bytes payload at 05:21:53Z).
  - `POST /auth/index`: HTTP 503 during probe (`Maintenance: refused /auth/index with 503` at 05:21:54Z and 05:25:35Z; maintenance subsequently disabled by owner).
- Client rendered `TitleMaintenanceWindow` modal as mandated by server protocol.
- Lobby, matchmaking, and battle were **NOT verified** on-device prior to publication authorization.
- Personal `KickFlight_API35` (port 5554) and local API/Photon were completely untouched.

## Publication verification checklist

- [x] Dedicated test AVD recovered and exact candidate installed; verified on-device SHA-256 and signer certificate.
- [x] Verified TitleScene reached, TAP START accepted, `/boot/index` (200 OK), `/auth/prepare` (200 OK).
- [x] Owner explicit publication exception authorized (`"Desactivado"`, `"No, cno eso es suficiente, publicala de una vez"`).
- [x] Recorded startup-connectivity-only proof in checklist and proof files (no fake battle PASS claimed).
- [ ] Confirm the release asset exists at tag `android-2.11.5` as `KickFlight-2.11.5-remote.apk` and matches the pinned SHA-256.
- [ ] Merge PR #53 through GitHub workflow; automatic CI stages and verifies the APK before deploying the catalog, site, and Android minimum.
- [ ] Read-only production verification: automatic CI success, `/apk/remote` matches exact hash, announcements visible, older Android blocked/2.11.5 accepted.

## Android/iOS minimums

- Android minimum: `2.11.5`, gated by CI staging the exact candidate asset first.
- iOS minimum remains `2.11.3`; this Android release does not change it.
