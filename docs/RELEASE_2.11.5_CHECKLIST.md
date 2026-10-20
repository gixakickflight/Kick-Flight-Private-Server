# Android 2.11.5 publication checklist

## Status: BLOCKED_EXTERNAL_HARD_MAINTENANCE

This draft prepares publication files only. Do not merge this PR, publish the announcement, create or upload the GitHub release asset, or activate the Android minimum until the exact signed candidate passes the live VPS release gate.

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
  - `POST /auth/index`: HTTP 503 (`Maintenance: refused /auth/index with 503` at 05:21:54Z and 05:25:35Z).
- Client rendered `TitleMaintenanceWindow` modal as mandated by server protocol.
- Lobby, matchmaking, and battle were **NOT reached** due to active server-side maintenance.
- Proven external blocker: VPS server is actively configured in Hard maintenance mode (`mode: hard`, noticeId=1792467424, message="Updatinng bot behavior 🤖 Mantainance will fniish by 5:00 AM UTC").
- Personal `KickFlight_API35` (port 5554) and local API/Photon were completely untouched.

## Required exact-candidate evidence before merge

- [x] Dedicated test AVD recovered and exact candidate installed; verified on-device SHA-256 and signer certificate.
- [x] Verified TitleScene reached, TAP START accepted, `/boot/index` (200 OK), `/auth/prepare` (200 OK).
- [ ] Await coordinator/operator decision on ending VPS hard maintenance (`POST /admin/maintenance` with `mode: "off"`).
- [ ] Reach startup, complete login, enter matchmaking, and finish an actual battle against live VPS without maintenance 503.
- [ ] Update `.local/vps-apk-runtime-proof.json` with the evidence and set the release gate to PASS.
- [ ] Re-review the public announcements for claims supported by the completed evidence.
- [ ] Confirm the release asset exists at tag `android-2.11.5` as `KickFlight-2.11.5-remote.apk` and matches the pinned SHA-256.
- [ ] Merge only after all checks above pass; automatic CI stages and verifies the APK before deploying the catalog, site, and Android minimum.

The current local proof file records `BLOCKED_EXTERNAL_HARD_MAINTENANCE`, `release_gate_passed: false`, and `publication_or_minimum_change: false`. No runtime PASS is claimed here.

## Android/iOS minimums

- Proposed Android minimum: `2.11.5`, gated by the evidence above.
- iOS minimum remains `2.11.3`; this Android release does not change it.
