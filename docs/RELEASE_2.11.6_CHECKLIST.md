# Android 2.11.6 publication checklist

## Status: GATE RUN ON AVD; PUBLISHED WITH OWNER-ACCEPTED CAVEAT

Owner instruction: "proceed with PR B despite the one crash" (see Caveat).

## Candidate identity

- Version: Android 2.11.6, versionCode 61
- Release: tag `android-2.11.6`, asset `KickFlight-2.11.6-remote.apk`
- SHA-256: `9b87b6ebb21046b0c2fb1cca503181a6f99664a1896ffb5aef546d85cf0f4ab1`
- Signing certificate SHA-256: `81ca6334fd069edbc8756a49fc135c91141eba8265cb64d0510ed9b231b21537` (matches 2.11.5)
- File size: 149,876,184 bytes
- Build flags: `KF_PHOTON=1`, `KF_AI_ROLES=1`, `KF_AI_ROLE_TARGETING=1`, `KF_AI_ROLE_DEPOSIT=1`, `KF_DIAG=0`, `KF_DAMAGE_DIAG=0`, `KF_RESULT_DIAG=0`, `KF_TARGET_FPS=120`, `CHAT_BASE_URL=http://159.54.134.159:18080?ns={0}`
- Endpoint: `http://159.54.134.159:18080`; Photon host `159.54.134.159`
- Byte-guarded dry run against pristine `libil2cpp.so`: 248 patches verified; `mov w0,#120` at 0x1CB5904 and 0x25DC224.
- Git base of the build: `7fab8712018a7f58a2d7a5150f06066bae72dc51` plus the 2.11.6 version/config edits in this PR.

## Gate evidence (AVD `KickFlight_API35_release`, exact release-key APK, live VPS)

- Startup, login, home, matchmaking, full battle to ResultScene (LOSE 16-34): PASS (second attempt).
- Chat: own message appears; second player (second AVD) sees the first player's message with name and badge; tapping the avatar opens the profile: PASS.
- Caveat: the first battle crashed ~22 s after GameScene (SIGSEGV libunity+0x34426c, UnityMain, fault addr 0x7b0). A relaunch and second battle had zero crashes. Cause not determined (known intermittent loader race, or a regression). Owner chose to proceed.
- FPS: patch verified to apply; NOT measured above 30 FPS on the emulator (AVD is capped around 30).
- Evidence (local, not committed): `.local/run/logcat-release-2.11.6-gate-*.txt`, `.local/run/shots/rel-*.png`.

## Android/iOS minimums

- Android minimum: `2.11.6` (old clients intentionally refused).
- iOS minimum unchanged (`2.11.3`).

## Post-deploy verification

- [ ] Deploy workflow success
- [ ] `/apk/KickFlight-2.11.6-remote.apk` serves the exact SHA-256 above
- [ ] 2.11.5 Android client version refused; 2.11.6 accepted
