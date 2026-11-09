# Release gate 2.11.806 (Fenix, world.global.fenix)

Artifact: `Fenix-2.11.806.apk`, sha256 `3b442dcdf221cf0de6d4d6fe00718fc9854037ca299e30b6f150bd7e4a3df34e`, 149307029 bytes.
Signing cert sha256 `81ca6334fd069edbc8756a49fc135c91141eba8265cb64d0510ed9b231b21537` (publication key).
aapt: package `world.global.fenix`, versionCode 71, versionName 2.11.806, label Fenix.
Flags: `KF_NATIVE=1`, `KF_PINGHUD_OPTION=1`, production patchFlags (`KF_PHOTON=1`, `KF_AI_ROLES=1`, `KF_AI_ROLE_TARGETING=1`, `KF_AI_ROLE_DEPOSIT=1`, `KF_DIAG=0`, `KF_DAMAGE_DIAG=0`, `KF_RESULT_DIAG=0`, `KF_TARGET_FPS=120`, `KF_PUSH=1`), server/photon host `kick-flight-fenix.us.ci`.

## Evidence
- Pre-rebrand dry-run build (`jp.grenge.kickflight`, same ping HUD code): run 1 SIGSEGV (stale Settings label, fault 0x2e8); fix applied (`alive()` + `op_Implicit`, bounded re-apply, reset on new HUD); run 2 passed title, login, Home, matchmaking, GameScene, ResultScene, 0 SIGSEGV; HUD ~165-186 ms. Logs under `.local/run/release-2.11.806/`.
- Final Fenix APK on the live VPS: STARTUP FAILED with "Communication Error" (`.local/run/release-2.11.806/final/`). Cause: the live VPS still serves title resource revision 40 (`/v1/list/12345/41..43` return 404); the revision 43 catalog and title-logo bundles in this PR deploy only on merge. The gate cannot pass before deployment.

## Decision
Android minimum NOT raised; stays 2.11.805 (AGENTS.md gate). After merge+deploy, re-run the full gate (startup, new account, matchmaking, battle, Settings-then-battle regression, ping HUD toggle, push) with this exact APK, then raise `ClientUpdate__MinimumVersion` in a follow-up PR.

## Tests
`dotnet test`: 594 passed, 11 skipped, 3 failed (2 `IosResourceCatalogTests` and `Health_ready_reports_missing_resource_prerequisites`, all pre-existing baseline failures; the health test string updated to `apk-remote-211806` and only passes where the APK source file is absent, as in CI). `tests/test_endpoint_preflight.py`: 5 passed.

Not verified on a real arm64 device (emulator x86_64 with ndk_translation only).
