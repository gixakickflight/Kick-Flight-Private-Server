# Release gate 2.11.807 (Fenix, world.global.fenix)

Artifact: `Fenix-2.11.807.apk`, sha256 `5d59d6ed328e12675792d9b2f88aea3cc31fa0e4b275f66d1aa07da51b883164`, 149094037 bytes.
Signing cert sha256 `81ca6334fd069edbc8756a49fc135c91141eba8265cb64d0510ed9b231b21537` (publication key).
aapt: package `world.global.fenix`, versionCode 72, versionName 2.11.807.
Flags: `KF_NATIVE=1`, `KF_PINGHUD_OPTION=1`, `KF_PUSH=1`, otherwise production patchFlags as 2.11.806; server/photon host `kick-flight-fenix.us.ci`.

## Change
Logo removed from the title (7 transparent `title_logo` bundles, Title atlas `title_logo_animation`, blank boot jpg), loading screen (Common atlas `loading_logo`, texture 117 `Logo@2x`) and home/battle-field scenes (repainted textures in 11 field bundles, catalog revision 46). Launcher/notification icons and site mark are a single "F". Catalog sources for the title_logo bundles live in `content/resources/fenix-localize/` (guard: `tests/test_catalog_sources.py`).

## Evidence (emulator x86_64, live VPS, `adb install -r`, no wipes)
- Startup and resource download from live (rev 46), title without logo, login (existing account), Home with repainted banner and no logo, Settings at Home, ranked matchmaking with bots, two battles to ResultScene (canyon and urban-tunnel fields), loading screen without logo, ping HUD OFF then ON in the in-battle Settings. 0 SIGSEGV/FATAL in logcat.
- Push: token registered (`KFPUSH register attempt 1 http 200`), receipt not observed in this run. Push code and Firebase config are unchanged from 2.11.806, where receipt was verified.
- Screenshots (320 px) and logcat: `.local/run/release-2.11.807/final/`.

## Decision
Gate accepted for the exact APK above. Android minimum raised to 2.11.807; iOS minimum unchanged. Release `android-2.11.807` (asset `Fenix-2.11.807.apk`).

## Not verified
Real arm64 device; push receipt; absence of logos on every field billboard (sign detector only finds flat yellow signs); loading-screen yellow box remains (separate sprite).
