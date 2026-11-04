# Android 2.11.7 publication checklist

## Status: SERVER DEPLOYED (PR #75, merge ed8aaef); CLIENT PUBLISHED WITH OWNER-WAIVED BATTLE GATE; POST-MAINTENANCE BATTLE CHECK PENDING

Rollout order: (1) this PR deploys the server (push, server fixes); (2) the client APK, catalog/apk entry, ClientUpdate minimum and announcements are published in a follow-up PR only after the live-VPS release gate passes.

## Change list

- Ranked push notifications: FCM sender, `POST /push/register`, `RankedSearchNotifier`, `Push__*` settings in `deploy/docker-compose.vps.yml` (credentials at `/srv/repo/.local/secrets/fcm-service-account.json`, already on the VPS). Token log leak fixes.
- Client (opt-in `KF_PUSH=1`, now enabled in `config/apk-direct-server.production.json`): `scripts/patch-firebase-push.py` + `scripts/push-registrar/` (Firebase config -> our project, FCM token reported to our server).
- Client prod patches: RemoveDebuff (0x17C42DC); GPGS sign-in neutralized (0x18E5CB0).
- Exit reporter v2 + `scripts/re/symbolize_exit_report.py`; DIAG docs.
- Server fixes: ops `never_started` split; `teamJoin` assetList + `/battle/teamRejoin`.
- Version bump: versionName 2.11.7 / versionCode 62 (`config/apk-direct-server.production.json`, `scripts/set-apk-version.py`, default output name in `scripts/build-direct-apk.sh`).

## Build flags

`KF_PHOTON=1`, `KF_AI_ROLES=1`, `KF_AI_ROLE_TARGETING=1`, `KF_AI_ROLE_DEPOSIT=1`, `KF_DIAG=0`, `KF_DAMAGE_DIAG=0`, `KF_RESULT_DIAG=0`, `KF_TARGET_FPS=120`, `KF_PUSH=1`, `CHAT_BASE_URL=http://159.54.134.159:18080?ns={0}`.
Firebase config: `google-services.json` is NEVER committed. The build reads `.local/firebase/google-services.json` by default (override with `KF_FIREBASE_CONFIG`) and fails clearly if missing.
Endpoint `http://159.54.134.159:18080`; Photon host `159.54.134.159`.

## Dry-run counts

Byte-guarded dry run against pristine base.apk `libil2cpp.so` with production flags: 250 patches verified (2.11.6: 248).

## Test evidence so far (AVD)

- Push end-to-end, all scenarios: PASS.
- 2.11.7 test APK: T1, T2, T4, T5 PASS.
- RemoveDebuff: no regression.
- Known, not fixed: Flag Flight minimap issue (not reproduced).
- Known, ignored: `UnzipDamageInfo` NRE is non-fatal.

## Candidate identity

- Version: Android 2.11.7, versionCode 62
- Release: tag `android-2.11.7`, asset `KickFlight-2.11.7-remote.apk`
- SHA-256: `f7b91e49fbe399854b6160be206f23bf4c4e17dc1476e17c5ca42109c3f3cdf0`
- Signing certificate SHA-256: `81ca6334fd069edbc8756a49fc135c91141eba8265cb64d0510ed9b231b21537` (matches 2.11.6)
- File size: 149,876,242 bytes
- Build flags: see "Build flags" above (production config, `KF_PUSH=1`); manifest `kf.push_url` = `http://159.54.134.159:18080`.
- Verified in output `libil2cpp.so`: patched bytes at 0x17C42DC (RemoveDebuff) and 0x18E5CB0 (GPGS neutralized); same length as pristine.

## Release gate: WAIVED by owner for raising the Android minimum

- The owner explicitly waived the full battle-proof gate (startup, login, matchmaking, battle, push receipt, chat on the exact signed APK against the live VPS) before raising the Android minimum to 2.11.7.
- Evidence actually collected (server in maintenance): the exact release-key APK was installed with `adb install -r` on AVD `KickFlight_API35` (no uninstall/wipe), launched, and after TAP START the live VPS maintenance popup ("Update incoming / The devs are finally working...") was displayed, proving client-to-VPS connectivity.
- Pending: post-maintenance battle check (login, matchmaking, full battle, push registration and ranked-search notification, chat). Not yet performed.

## Android/iOS minimums

- Android minimum: `2.11.7` (old clients intentionally refused).
- iOS minimum unchanged (`2.11.3`).

## Post-deploy verification

- [ ] Deploy workflow success
- [ ] `/apk/KickFlight-2.11.7-remote.apk` serves the exact SHA-256 above
- [ ] 2.11.6 Android client version refused; 2.11.7 accepted
- [ ] Post-maintenance battle check (see above)
