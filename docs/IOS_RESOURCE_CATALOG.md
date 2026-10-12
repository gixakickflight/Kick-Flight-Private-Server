# Isolated iOS resource catalog

The iOS profile uses asset version **12346**, platform **9**, source-catalog revision **44**, and wire database revision
**45**. Its immutable payload tree is `/opt/kickflight/.local/ios-resources/12346-r5` on the VPS; the tracked manifest is
`src/KickFlight.BootstrapApi/catalog-ios-12346.json`. The staged `catalog.json` must be byte-identical to that
manifest (see the pinned SHA in `IosResourceCatalogTests`). The catalog preserves all prior 501 rows and adds 1,383 UI
Sprite/Texture thumbnail bundles plus 152 raw audio/video resources as IDs 502–2036. The thumbnails use iOS target-9
serialized templates and round-trip checks; the raw resources are byte-for-byte Android payloads selected by the static
format audit (131 USM, 13 ACB, 8 AWB). This does not imply that every UI asset, model, animation, scene, or shader is
iOS-compatible, or that every raw resource plays at runtime.

An iOS boot request is selected only for a direct-client `POST /boot/index` carrying
`x-app-asset-platform: 2`; its D2C body changes the existing boot response's `assetVersion` to 12346. The forced-update
gate remains earlier in the request path. Requests to `/v1/list/12346/{fromRevision}` are built from the pinned catalog.
Revisions 0 through 44 receive all 2,036 rows (ADD from 0, UPDATE otherwise); revision 45 receives only the revision and
CDN URL. Rows 1–501 keep their previously served IDs and wire data; new UI and raw-resource rows use IDs 502–2036. The catalog
still records the original nullable Octo name for `12Cxc3` as null. Its explicitly reviewed
server fallback `resource/12Cxc3` is placed in the Octo wire `filepath` and `name` fields so the client can safely key
its database update; all other rows use their original Octo names. Wire revision 45 forces clients that may have cached
an earlier catalog revision to request the complete additive asset set. The database points to
`https://kick-flight-fenix.us.ci/ios/cdn/{o}`.

The API image publishes the small iOS catalog manifest and platform-2 boot fixture beside its binaries. Runtime defaults
resolve those files from `AppContext.BaseDirectory`; the VPS container mounts `.local`, `config`, and `content` under
`/srv/repo`, but does not mount the source checkout at `/srv/repo/src`. This keeps readiness validation independent of
the host checkout layout. The focused test pins both published-file hashes.

`GET /ios/cdn/{objectName}` reads only the iOS resource snapshot. A missing, partial, checksum-mismatched, or wrong-version
snapshot fails closed; the server does not fall back to Android `/cdn` resources. Android asset version 12345 and its
fixtures and routes remain separate. `/health/ready` reports `iosCatalogReady`; stage the complete r5 tree before selecting
the iOS boot profile. The API validates the manifest identity, 2,036-row 1,831/205 kind counts, each payload
SHA-256/MD5/CRC32 and size before enabling the profile. The r5 additions are static candidates: their serialization,
checksums, and selected codec-family overlap are verified, but loading, visible rendering, gameplay, and playback were
not validated in PlayCover or on iOS hardware.

This is a static payload and routing validation. The 1,383 generated thumbnail bundles passed serialized-object,
texture-pixel, Sprite-geometry, pointer, and checksum validation. The 152 raw additions passed checksum verification and
were selected because their format families match existing iOS resources. None of the new resources have been validated
in client playback. It does not establish iOS runtime compatibility or that all game assets needed by every scene exist.

## Preparing a later immutable snapshot

`scripts/stage-ios-resource-snapshot.py` validates and stages a snapshot without selecting it. Its archive has
exactly `catalog.json` and the payload files under `payload/`, with payload paths derived from each manifest
`sourcePath` after `.local/ios-resources/<snapshot>/payload/`. The release archive SHA-256 must come from a trusted
repository variable. The stager requires the archive catalog bytes to equal the API release's tracked iOS manifest,
the exact payload member set, safe regular-file paths, and matching size, SHA-256, MD5, and CRC32 for every row. It
rejects links, traversal, duplicate members, and partial snapshots. It writes to a temporary sibling and atomically
renames only after validation; an existing immutable snapshot is accepted only when it already matches exactly.

The VPS deploy workflow can stage an optional GitHub Release asset named `ios-resource-snapshot.tar.gz` from the same
repository. Configure both repository variables `IOS_SNAPSHOT_RELEASE_TAG` and `IOS_SNAPSHOT_ARCHIVE_SHA256`; leave
both empty to skip staging. The workflow downloads only that named asset from the configured repository's release,
checks the pinned SHA-256, transfers it through the existing pinned-host SSH channel, then validates and stages it
before source sync and any API restart. Staging never rewrites r4 or selects a path by itself. The API source release
points at r5 only after the archive has staged successfully.

To recreate the current r5 candidate, first run `scripts/build-ios-ui-thumbnail-batch.py` with the audited Android
sources and iOS donor payloads, then run `scripts/build-ios-ui-resource-snapshot.py` with the r4 manifest, verified iOS
and Android audits, Android resource catalog, conversion report, and output paths. That builder preserves the original
rows, appends only the 1,383 round-trip-validated UI bundles, and copies the 152 allowlisted raw resources unchanged.
The report and source/donor audit artifacts are local inputs and are not shipped in the snapshot archive.
