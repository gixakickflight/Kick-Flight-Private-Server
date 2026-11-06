# Release gate: Android 2.11.800 (versionCode 65)

- Artifact: KickFlight-2.11.800-remote.apk (built as .local/artifacts/KickFlight-2.11.800.apk), 149880469 bytes.
- APK SHA-256: cf9314122fcbd20e0da639967a832d8ed63911cf2b2af2ae745ae48b35c23541
- Signing cert SHA-256 (publication key): 81ca6334fd069edbc8756a49fc135c91141eba8265cb64d0510ed9b231b21537
- aapt: versionName 2.11.800, versionCode 65. Server minimum stays 2.11.706 (System.Version compare: 2.11.800 > 2.11.706; 2.11.8 would have been older, hence 800).
- Build: scripts/build-direct-apk.sh with KF_NATIVE=1 and config/apk-direct-server.production.json flags
  (KF_PHOTON=1, KF_AI_ROLES=1, KF_AI_ROLE_TARGETING=1, KF_AI_ROLE_DEPOSIT=1, KF_DIAG=0, KF_DAMAGE_DIAG=0, KF_RESULT_DIAG=0,
  KF_TARGET_FPS=120, KF_PUSH=1, CHAT_BASE_URL=http://147.93.138.247:18080?ns={0}); server http://147.93.138.247:18080.
  Includes lib/arm64-v8a/libkfpatch.so and the 0x159DCC4 PlayIdleLoopIfExist guard patch.

## Results (live VPS, emulator-5554 KickFlight_API35, adb install -r, no wipe, 2026-11-05)
- Startup, login (Title -> Download -> Home), matchmaking (Rank Battle, Flag Flight), battle (GameScene ~1m45s) and ResultScene -> Home: OK. 0 `E/CRASH`/FATAL in the passing run (.local/run/logcat-release-2.11.800.txt).
- Disc detail: 3D summon model visible in the detail box and in the enlarged view; horizontal swipes rotate the model.
- libkfpatch runtime: `il2cpp base=..., 0 hook(s) armed (+0 diag)` counts only C hooks. Slot table read live from process memory
  (il2cpp base + 0x4982C00): slots 16-25 (the 10 migrated caves) all non-zero, pointing into libkfpatch.
- Maintainer end-to-end run of this exact APK against the live VPS (startup, login, matchmaking, battle): "worked perfectly" (2026-11-05).
- Observed once, first launch right after install: SIGSEGV (fault addr 0x18, UnityMain) ~20 s into the Matching wait screen (no patched
  battle code runs there; host under heavy I/O pressure). Not reproduced in the next run or by the maintainer. Log kept at .local/run/logcat-release-2.11.800-run1-crash.txt.
