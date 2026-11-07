# Release gate: Android 2.11.804 (versionCode 69)

- Artifact: KickFlight-2.11.804-remote.apk (built as .local/artifacts/KickFlight-2.11.804.apk), 149884565 bytes.
- APK SHA-256: fcbdfcc686f916df9562ca037a28b344356de027cf1574860024eabb330816ed
- Signing cert SHA-256 (publication key): 81ca6334fd069edbc8756a49fc135c91141eba8265cb64d0510ed9b231b21537
- aapt: versionName 2.11.804, versionCode 69 (2.11.800 was 65).
- Build flags: scripts/build-direct-apk.sh with KF_NATIVE=1, KF_PHOTON=1, KF_AI_ROLES=1, KF_AI_ROLE_TARGETING=1,
  KF_AI_ROLE_DEPOSIT=1 and the production patchFlags of config/apk-direct-server.production.json; server http://147.93.138.247:18080.
  Includes lib/arm64-v8a/libkfpatch.so. libunity veneer host 0xEFF890 (byte-guarded against the pristine libunity.so).
- Fixes vs 2.11.800 (docs/re-notes/exception-review-2.11.800.md, kfpatch-design.md): kfpatch watcher gave up after 20 s (SIGSEGV pc=0),
  GetAssignments null response (fault 0x18), CRIWARE ACF guard (fault 0x7b0), libunity dependency-walk / ref-count release guards
  (UnityPreload +0xb34 / UnityMain +0x84c), fail-safe `b V` veneers (an empty slot runs the original code, never `br 0`).

## Verification and its limit
- Verified on the emulator only (emulator-5554 KickFlight_API35, adb install -r, no wipe): startup, login, matchmaking and battles
  against the live VPS with this exact APK (details in the maintainer's .local/run logs, not committed).
- NOT verified on a real arm64 device (the emulator runs ndk_translation). Risk accepted by the owner for this release.
- The Android minimum is raised to 2.11.804 in the same PR as the catalog/APK publication (2.11.800 used a follow-up PR).
- Release asset: GitHub release android-2.11.804, asset KickFlight-2.11.804-remote.apk (SHA-256 above), referenced by
  config/resources/catalog.json row apk-remote-211804.
