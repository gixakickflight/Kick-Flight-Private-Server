# Android 2.11.5 publication checklist

## Status: BLOCKED_NO_RUNTIME_PASS

This draft prepares publication files only. Do not merge this PR, publish the announcement, create or upload the GitHub release asset, or activate the Android minimum until the exact signed candidate passes the live VPS release gate.

## Candidate identity

- Version: Android 2.11.5, versionCode 60
- Artifact: `.local/artifacts/KickFlight-2.11.5-remote.apk`
- SHA-256: `ae7b4ed16f4544b18348ed5068f02c3a582ed7cbb3b64d46fe9811a704405f4f`
- Signing certificate SHA-256: `81ca6334fd069edbc8756a49fc135c91141eba8265cb64d0510ed9b231b21537`
- Build flags: `KF_PHOTON=1`, `KF_AI_ROLES=1`, `KF_AI_ROLE_TARGETING=1`, `KF_AI_ROLE_DEPOSIT=1`, `KF_DIAG=0`, `KF_DAMAGE_DIAG=0`, `KF_RESULT_DIAG=0`
- Configured API/resource endpoint: `http://159.54.134.159:18080`
- Configured Photon host: `159.54.134.159`

## Required exact-candidate evidence before merge

- [ ] Install and run this exact artifact against the live VPS; record the same SHA-256 and signing certificate identity.
- [ ] Reach startup, complete login, enter matchmaking, and finish an actual battle.
- [ ] Update `.local/vps-apk-runtime-proof.json` with the evidence and set the release gate to PASS.
- [ ] Re-review the public announcements for claims supported by the completed evidence.
- [ ] Confirm the release asset exists at tag `android-2.11.5` as `KickFlight-2.11.5-remote.apk` and matches the pinned SHA-256.
- [ ] Merge only after all checks above pass; automatic CI stages and verifies the APK before deploying the catalog, site, and Android minimum.

The current local proof file records `BLOCKED_NO_RUNTIME_PASS`, `release_gate_passed: false`, and `publication_or_minimum_change: false`. No runtime PASS is claimed here.

## Android/iOS minimums

- Proposed Android minimum: `2.11.5`, gated by the evidence above.
- iOS minimum remains `2.11.3`; this Android release does not change it.
