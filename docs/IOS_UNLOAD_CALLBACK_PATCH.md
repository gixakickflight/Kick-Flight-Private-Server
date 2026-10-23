# iOS AssetBundle unload callback fix

## Cause and verified change

The iOS `ResourceManager.AssetBundleUnloadCompleted(string)` body has one name-comparison branch at RVA `0x1d097f8`. The preceding call at `0x1d097f4` is `System.String.op_Inequality`; it returns `1` for different strings and `0` for equal strings. The original `cbz w0` therefore skips cleanup when the callback name matches the active unloading row, leaving `AssetBundleInfo.State == Unloading (3)` and blocking `UnloadAssetBundleAllAsync`'s `WaitWhile(IsUnloading)`.

The one-instruction correction changes `cbz w0, 0x1d0980c` (`a0000034`) to `cbnz w0, 0x1d0980c` (`a0000035`). Equal names then fall through to `RemoveAssetBundleInfo` at RVA `0x1d09808`, which removes the row and reaches `PoolDeactive` at `0x1f77ef4`. Different names still skip removal.

The installed executable UUID is `F800F44E-B06D-3ABC-96F2-FEDC716100AD`. Its `__TEXT` segment maps `vmaddr=0x100000000`, `fileoff=0`, so RVA `0x1d097f8` is also file offset `0x1d097f8`. The target body has one direct branch entry: wrapper RVA `0x1f9437c` tail-branches to `0x1d09710`. Octo reaches the wrapper through the registered unload delegate; the callback setter is called from `ResourceManager.<BeginAsync>d__59.MoveNext` at RVA `0x1f97f08`. The inspected cleanup body and pool-deactivation bytes match the pristine local IPA.

An A/B run with only this branch byte changed observed `RemoveAssetBundleInfo` and `PoolDeactive`, then entered `DownloadScene.PreBegin` and `StartDownload`. In the later observed run, the download prompt was tapped and the trace recorded `downloaded=true`, 7/7 bundles complete, and `downloadError=false` at 17:27:52.311Z on 2026-10-12. The client then loaded master assets and entered the Rank Battle / Rapid Ball matchmaking waiting room. At 17:32 it showed `Remain Count 0 / Loading...`; no gameplay scene or controls were reached. This confirms the download completed, but does not establish full end-to-end functionality.

## Applying the patch

The script accepts only the reviewed UUID, arm64 Mach-O mapping, source SHA-256 allowlist, and expected instruction bytes. It emits a sibling `.ios-unload-callback.bak` before writing. The default output mode leaves the source executable unchanged; explicit `--in-place` also creates the backup first. `--dry-run` validates all guards without creating output or backup files. Re-running against the same source/output is idempotent. Restore an in-place binary by copying its `.ios-unload-callback.bak` over it.

```sh
python3 scripts/patch-ios-unload-callback.py --input /path/to/kickflight --dry-run --output /path/to/kickflight.patched
python3 scripts/patch-ios-unload-callback.py --input /path/to/kickflight --output /path/to/kickflight.patched
```

The script accepts the two pristine batch26 IPA variants below and the preserved installed-app original used for the successful A/B run:

| Source executable SHA-256 | Patched executable SHA-256 | Provenance |
| --- | --- | --- |
| `93663acb757ea683dcda18b0a642df1ce324264b52f099bbca269788e02336ab` | `cf1d2114003855d7b438f3ec39e600253b5157e4a18168b916f247a60fd33a37` | Batch26 IPA variant 1 |
| `0ad347126964a15f0e38e8ec60a1c912db6f35d5a2bd5bcccf6b199dd172f62e4` | `cd12a00e24a1dfa7dbf9c340eb9150a82753d4583c7e6d3a14a482ee4d93059e` | Batch26 IPA variant 2 |
| `d5ab2bc39464ef3ba27fdc04eb2a2463d43645cfce19f4e45b5d70cb30429a71` | `ba8b994891f70dfa06e9e6bc510e9eb1f7b1c485390e3fb3369cd53dceae6e93` | Original PlayCover app backup |

The currently installed, already-patched executable was separately observed with SHA-256 `5cb7ab0c9f926177ed504d6579133407576489992eb941ef6d5425e69eebbd4a`; its UUID matches and branch bytes are `a0000035`. It passed `codesign --verify --deep --strict`, and its 18 entitlements matched the preserved original entitlements. This hash is evidence of the completed local A/B run, not an accepted script input.

The patch changes signed Mach-O bytes and invalidates the app's existing code signature. The script writes only an executable; its output is not a runnable or installable app/IPA by itself. For a local app test, copy the `.app` into a temporary directory, preserve the original app and its `_CodeSignature`/entitlements as a backup, and patch only the copied executable. Extract the copied app's original entitlements before replacing its executable:

```sh
APP_COPY="/tmp/KickFlight-patched.app"
codesign -d --entitlements :- "$APP_COPY" > /tmp/kickflight-entitlements.plist
python3 scripts/patch-ios-unload-callback.py \
  --input "$APP_COPY/kickflight" --output "$APP_COPY/kickflight.patched"
mv "$APP_COPY/kickflight.patched" "$APP_COPY/kickflight"
codesign --force --deep --sign - --entitlements /tmp/kickflight-entitlements.plist \
  --timestamp=none "$APP_COPY"
codesign --verify --deep --strict "$APP_COPY"
```

This example ad-hoc signs the copied app bundle while reusing its extracted entitlements; it does not preserve the original signing identity or make an IPA ready for distribution. Do not add entitlements or other app-wide changes as part of this callback patch. For an IPA workflow, extract the IPA to a temporary directory, apply the script to `Payload/<App>.app/kickflight`, then re-sign the complete app bundle as above before repackaging. Keep the original IPA and signature files untouched.

Unreviewed builds are rejected even if their UUID matches. To support another executable revision, verify its callback implementation and record its source/result hashes before adding it to the script allowlist.

The script patches an extracted executable only. It does not modify or repackage an IPA, sign an app, install a build, or control a running game.
