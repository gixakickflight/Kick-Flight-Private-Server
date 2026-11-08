# Release gate: Android 2.11.805 (versionCode 70)

- Artifact: KickFlight-2.11.805-remote.apk (.local/artifacts/KickFlight-2.11.805-remote.apk), 149884565 bytes, built from main 86410e5 (PR #91 merge).
- APK SHA-256: fe44c6f4ecddcd3966237405e9fcf30d379f319ec1472fb4eef0d0e51130b0a6
- Signing cert SHA-256 (publication key): 81ca6334fd069edbc8756a49fc135c91141eba8265cb64d0510ed9b231b21537
- aapt: versionName 2.11.805, versionCode 70 (2.11.804 was 69).
- Build flags: scripts/build-direct-apk.sh with KF_NATIVE=1 and the production patchFlags of config/apk-direct-server.production.json
  (KF_PHOTON=1, KF_AI_ROLES=1, KF_AI_ROLE_TARGETING=1, KF_AI_ROLE_DEPOSIT=1, KF_DIAG=0, KF_DAMAGE_DIAG=0, KF_RESULT_DIAG=0,
  KF_TARGET_FPS=120, KF_PUSH=1, CHAT_BASE_URL=http://kick-flight-fenix.us.ci:18080?ns={0}); serverBaseUrl http://kick-flight-fenix.us.ci:18080,
  photonHost kick-flight-fenix.us.ci. Patcher dry-run: 251 patches verified against the pristine base.apk libil2cpp.so. Includes libkfpatch.so.
- Gate (emulator-5554 KickFlight_API35, adb install -r over 2.11.804-diag/69, no wipe; EU VPS 92.242.191.53, maintenance lifted for the gate):
  title reached, login (test account), Home, matchmaking (MatchingScene), Crystal Scramble battle (GameScene) to ResultScene; 0 FATAL/SIGSEGV in logcat.
  Server: /battle/result and /battle/upload handled for the test user, Photon peer connected and room result properties broadcast.
- Logcat: .local/run/logcat-release-2.11.805-gate.txt (maintainer's machine, not committed). Screenshots .local/run/shots/g805-*.png.
- NOT verified on a real arm64 device (the emulator runs ndk_translation).
- Release asset: GitHub release android-2.11.805, asset KickFlight-2.11.805-remote.apk (SHA-256 above), referenced by catalog row apk-remote-211805.
- The Android minimum is raised to 2.11.805 in this follow-up PR (after the gate).
