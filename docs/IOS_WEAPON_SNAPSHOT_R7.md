# iOS weapon snapshot r7

This change appends the verified Android weapon bundles missing from the pinned
iOS r6 catalog. It preserves all 2,252 r6 rows, IDs, and wire fields; only the
managed `sourcePath` changes to the r7 snapshot directory. The r7 catalog has
2,563 rows: 2,358 AssetBundles and 205 raw resources. Its source revision is 46
and wire revision is 47.

The 311 weapon routes were converted from Android target 13 to iOS target 9
using Unity 2018.4.11f1. The independent postflight reopened every source and
output, checked the full TypeTree signature, object payloads, PPtr graph,
streamed resource hashes, and material shader references against the iOS CAB
with all 15 Metal variants. It includes the runtime failing route
`weapon/wp_001/wp_001_102_001.unity3d`. There are no rejected routes or local
material shader pointers.

The archive SHA-256 is
`343993c8157bc388eedf1828eafed13164f4d8a483192f4ee6e65b6afdc4673b`.
The catalog source is `src/KickFlight.BootstrapApi/catalog-ios-12346.json`.
The archive is a versioned release input; it is not stored in the repository.

To build and validate from the generated reports:

```bash
python3 scripts/build-ios-ui-resource-snapshot.py \
  --base-catalog <pinned-r6-catalog.json> \
  --weapon-conversion-report <conversion-report.json> \
  --weapon-postflight-report <postflight.json> \
  --android-coverage <ios-cdn-coverage.json> \
  --source-root <r6-payload-root> \
  --manifest-out <r7>/catalog.json \
  --archive-out <r7>/ios-resource-snapshot.tar.gz

python3 scripts/stage-ios-resource-snapshot.py validate \
  --archive <r7>/ios-resource-snapshot.tar.gz \
  --archive-sha256 343993c8157bc388eedf1828eafed13164f4d8a483192f4ee6e65b6afdc4673b \
  --manifest <r7>/catalog.json --snapshot 12346-r7
```

The iOS catalog has no `fromRevisions` field; clients request `/v1/list/12346/<wireRevision>`.
The deploy workflow requires the release tag and matching archive SHA variables
whenever catalog rows point at r5, r6, or r7.
