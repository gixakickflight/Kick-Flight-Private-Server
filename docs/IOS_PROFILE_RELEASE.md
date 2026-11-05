# iOS test profile release

An iOS test profile is an explicitly activated review package for one `(assetVersion, revision)`. It is **test-only and incomplete**: the original wire map is not verified, reconstructed row IDs are unverified, and field 901 is experimental. Keep upstream disabled. The currently pinned profile is `assetVersion=12346`, revision 52 (R52).

The profile identity is data, not code. `config/ios-profile-release.json` is the single source of truth for the release tag, asset name, archive SHA-256, `assetVersion` and `revisionFloor`. The workflow, `scripts/stage-ios-profile.py`, `scripts/ios-wire-floor.py`, `scripts/deploy-vps-ci.sh`, the compose file and the API all derive paths, URLs and health values from it:

| Value | Derived as | Current pin |
| --- | --- | --- |
| Stage directory | `.local/ios-resources/<assetVersion>-r<revision>/` (`stage-manifest.json` + `profile/`) | `12346-r52` |
| Remote temp archive | `/tmp/kf-ios-profile-<GITHUB_SHA>.tar.gz` | same pattern |
| List route | `/v1/list/<assetVersion>/<fromRevision>` | `/v1/list/12346/52` |
| Health | `iosProfile=r<revision>-test`, `iosProfileRevision`, `iosResourceCount` | `r52-test`, 52, 3,389 |

The archive (about 878 MB for R52) is a GitHub Release asset, not a repository file. The workflow verifies it against the pinned SHA-256 and the stager validates the report for self-consistency against the real payload and fixtures (fixture count is `revision + 1`, payload and route counts come from the report and are re-checked file by file) before transfer. Staging uses an atomic directory rename and removes write bits. The API container mounts `.local` read-only and verifies profile hashes at startup. Do not edit or replace a staged profile in place; `verify-stage` rejects changed content or writable permissions.

The profile supports only the explicitly requested direct-client host `kick-flight-fenix.us.ci`. The normal `push` workflow never activates a profile. A push containing only the reviewed iOS profile path set deploys the API code only and skips Android APK and native R7 snapshot transfer. The setting stays disabled until a maintainer runs **Deploy VPS (main)** with `activate_ios_profile=true` (the old `activate_ios_r52_profile` input is a deprecated alias with the same effect). That opt-in uses a separate job with `contents:write` to download the **draft** release named in the pin and transfers the verified archive directly to the VPS over pinned SSH; no workflow artifact is created. The deploy job keeps `contents:read`.

Activation builds the API image first, stops the old API, atomically writes the read-only `.ci/ios-wire-floor.json` (`assetVersion`, `revisionFloor`, archive hash, activating commit) and immediately starts the new API. If the build fails, the floor is untouched. If startup fails after the latch, recovery only reapplies a compatible release and checks readiness. The floor is monotonic: a later activation may replace it only with a strictly greater `(assetVersion, revision)`; later deployments force the latched profile on and verify the same archive hash. Automatic rollback after activation may only use a release whose runtime contract can serve the latched profile (generic `kickflight.ios-test-profile.v1`, or the pre-generalization R52 contract for exactly 12346/52).

Android remains on `assetVersion=12345` and the existing `/cdn/` routing. The opt-in only selects the iOS list fixtures and `/ios/cdn/` catalog of the active profile. The native R7 snapshot is a separate immutable profile.

## How to publish the next profile (for example revision 53)

1. Build the review package for the new `(assetVersion, revision)`. Its `profile-report.json` must use schema `r56.bootstrapapi.offline-profile.r<revision>-direct-review.v1`, list fixtures `resource-list-<assetVersion>-from-0..<revision-1>.json` plus `resource-list-<assetVersion>.json`, and sanitized `sourceProfile`.
2. Pack it: `python3 scripts/stage-ios-profile.py pack --pin config/ios-profile-release.json ...` (or pass `--asset-version` and `--revision`) and record the printed SHA-256.
3. Upload the archive as a **draft** GitHub Release under the tag/asset name you choose; leave it unpublished.
4. Open a PR that only edits `config/ios-profile-release.json` (`releaseTag`, `assetName`, `archiveSha256`, `assetVersion`, `revisionFloor`). No code change is needed. Merge it; the push deploys API code only and does not activate.
5. Run **Deploy VPS (main)** manually on `main` with `activate_ios_profile=true`. It stages `.local/ios-resources/<assetVersion>-r<revision>/`, upgrades the floor and verifies health `r<revision>-test`.
6. Verify `/health/ready` reports the new `iosProfileRevision` and that `/v1/list/<assetVersion>/<revision>` answers.

No GitHub secret or variable changes are required.
