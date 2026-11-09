# Release gate 2.11.806 (Fenix, world.global.fenix)

Artifact: `Fenix-2.11.806.apk`, sha256 `6789485fc1df7be2cf1d8569af1728873bb9849b10b8da625a6e43b1e335ae97`, 149450389 bytes.
Signing cert sha256 `81ca6334fd069edbc8756a49fc135c91141eba8265cb64d0510ed9b231b21537` (publication key).
aapt: package `world.global.fenix`, versionCode 71, versionName 2.11.806, label Fenix.
Flags: `KF_NATIVE=1`, `KF_PINGHUD_OPTION=1`, production patchFlags (`KF_PHOTON=1`, `KF_AI_ROLES=1`, `KF_AI_ROLE_TARGETING=1`, `KF_AI_ROLE_DEPOSIT=1`, `KF_DIAG=0`, `KF_DAMAGE_DIAG=0`, `KF_RESULT_DIAG=0`, `KF_TARGET_FPS=120`, `KF_PUSH=1`), server/photon host `kick-flight-fenix.us.ci`.

## Evidence
- Earlier candidate (sha 3b442dcd...) failed the gate: Settings opened at Home, then Battle, gave SIGSEGV on UnityMain 3-4 s after GameScene (2/2). Actual crashing access: `kf_opt_tick` (called ~1 Hz from `hook_settime`) called `UnityEngine.UI.Text.set_text` (x30 = il2cpp 0x266BD40) on a cached raw pointer (`g_opt_txt`) to the Settings label that Unity had destroyed with the view; the managed memory was freed/reused, so `alive()` could pass on foreign memory (fault addr 0, pc 0, x0 = the stale text pointer; in run 2 a heap fault address).
- Fix: no Unity/managed pointer is cached across frames any more. `hooks_pinghud_opt.c` does all row cloning, labelling and toggle wiring synchronously inside the `SettingBattleCategoryView.Initialize` hook and keeps only an il2cpp strong GC handle to the label (bounded 12 s re-apply, freed on reset/death); the toggle callback captures no native state and `PlayerPrefs kf_ping_hud` is re-read at each battle start. `hooks_pinghud.c` keeps view/clone/box transforms as GC handles too (identity compare is safe because the handle keeps the old TimeView alive, so addresses cannot be reused) and resets them on every new TimeView.
- Emulator regression with this exact APK (Fenix, live VPS): Settings at Home open/close x3 then Battle (full battle to ResultScene), repeated 3 times: 0 SIGSEGV; ping HUD visible when ON (~165 ms), hidden when OFF, visible again when ON (in-battle Settings toggle). Evidence under `.local/run/release-2.11.806/final/` (`j*.png`, `q*.png`, `cycle.log`).
- Live gate against `kick-flight-fenix.us.ci`: startup with the Fenix title logo, new account, login, Home, matchmaking (60 s window), three consecutive battles to ResultScene, Settings-then-battle x3, ping toggle OFF/ON, and an FCM push (`ranked_search`, triggered by a ranked search from a second client) received by the Fenix app (`world.global.fenix`).
- The push title still reads "Kick Flight" on the live server because `appsettings.json` set `Push:Title`; changed to "Fenix" in this PR (takes effect on deploy).
- Loading screen: the "Kick-Flight" wordmark is the `loading_logo` sprite (368x92) packed in the `Common` SpriteAtlas texture (Texture2D 1024x2048 ASTC 6x6) inside the APK's `assets/bin/Data/data.unity3d`, not an Octo bundle. `scripts/patch-fenix-unity-text.py` now also replaces that rect with a black Fenix silhouette at APK build time; verified on the emulator (screenshot `.local/run/release-2.11.806/final/load/n03.png`).

## Decision
Gate passed for the exact APK above. Android minimum raised to 2.11.806 in this PR; iOS minimum unchanged. Published as release `android-2.11.806` (asset `Fenix-2.11.806.apk`).

## Tests
`dotnet test`: 594 passed, 11 skipped, 3 failed (2 `IosResourceCatalogTests` and `Health_ready_reports_missing_resource_prerequisites`, all pre-existing baseline failures; the health test string updated to `apk-remote-211806` and only passes where the APK source file is absent, as in CI). `tests/test_endpoint_preflight.py`: 5 passed.

Not verified on a real arm64 device (emulator x86_64 with ndk_translation only).
