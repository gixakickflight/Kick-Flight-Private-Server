# iOS CDN UI bundle conversion proof

This proof converts one-texture UI thumbnails using an existing iOS AssetBundle
as the serialization template. Each accepted source and donor contains one
`AssetBundle`, one `Sprite`, and one `Texture2D` in Unity `2018.4.11f1`.

## Conversion and validation

Run `scripts/build-ios-ui-bundle-from-donor.py` with an Android Octo source, a
verified iOS Octo donor, and the destination logical name. The batch proof
used same-route donors; for `ui/kicker/skill/large` and
`ui/kicker/stamptab`, it used a sibling iOS kicker thumbnail donor after the
same structural checks passed.
The script keeps the iOS SerializedFile (target platform 9), copies the
source's decoded RGBA pixels, updates Texture2D dimensions to match those
pixels, copies the serialized Sprite data including geometry/UVs/submeshes and
physics data, and remaps its texture pointer to the donor's internal
Texture2D. It updates the AssetBundle container names and rebuilds the Octo
wrapper and CAB identifier.

The script refuses mismatched Unity versions, non-iOS donors, unexpected
object classes, external SerializedFile references, alpha-texture references,
or Sprite Atlas/scriptable-object/bone dependencies. It reloads the result and
checks exact source-vs-output Sprite field equality (apart from name and the
texture pointer), RGBA byte equality, iOS platform/version, internal Sprite and
Texture2D pointers, both AssetBundle container references, and zero external
files. These checks validate the bundle structure; the client has not loaded
these generated outputs yet.

## Generated samples

The source Android assets are local `Kick-Flight-Assets` bundles. Donors were
downloaded from the iOS CDN and their SHA-256 matched the iOS catalog before
conversion. Generated Octo files and the individual JSON reports are in the
temporary output directory `/private/tmp/kf-ios-cdn-ui-output/converted/` and
are not committed.

| Logical name | iOS donor / donor SHA-256 | Output bytes | Output SHA-256 | Sprite vertices | External refs |
| --- | --- | ---: | --- | ---: | ---: |
| `ui/league/thumbnail_league_7_1.unity3d` | `thumbnail_league_0` / `e148f002ccd527c30b03c6ad90b06f19db2879cf65c5a382793e91ddde84df0a` | 12,108 | `d9f69f779ef71f7098c9b7562dca39e3dbecf9fddfe2e604d5b1fea95135838a` | 20 | 0 |
| `ui/disc/thumbnail_3010046.unity3d` | `thumbnail_3010003` / `09a7cea8a44f1b93fa11f1eed579cc31bbb04470e926a55de07be1ec30fc1a90` | 417,649 | `6ea876ba2c12217cb9ebd431d03a280dd09dfbb4a89b5d4036e35b9bc1e360a1` | 814 | 0 |
| `ui/badge/thumbnail_badges_6010102.unity3d` | `thumbnail_badges_000` / `a1eab305297cac69494f6495da1e7b67a6550201cdbbdc8380e8dbe52c321d8b` | 132,479 | `440010f6b426c266ca828bfc22799dfe37bca0349be654e1a58cbeb720002941` | 36 | 0 |

The full missing-thumbnail batch produced 1,383 outputs, each passing those
same round-trip checks:

- 479 kicker thumbnails
- 552 localized stamp thumbnails
- 86 disc thumbnails
- 211 badge thumbnails
- 38 goods thumbnails
- 16 league thumbnails
- 1 BattleRule thumbnail

The exact logical names, donor rows, source object names, sizes, hashes and
geometry signature hashes are in `conversion-report.json`. This batch includes
the rank-S `thumbnail_league_7_1`. The snapshot builder appends these 1,383
bundles to the 501 original iOS rows while preserving the original rows' IDs and
wire metadata. The snapshot also adds 152 unchanged raw audio/video resources
(131 USM, 13 ACB, 8 AWB), selected by a separate static format audit. The
generated r5 snapshot has 2,036 rows (1,831 AssetBundles and 205 raw resources);
its compressed archive SHA-256 is
`f319edc167bf367d0897640de77164926b66f14a20880cc678cc1faa37c10115`.
`stage-ios-resource-snapshot.py validate` checked every member's size, SHA-256,
MD5, CRC32 and the byte-identical catalog. The source change and archive are a
candidate for the regular release workflow; they have not been published or
loaded by the client.

## Scope and remaining conversion work

This proves the Sprite/Texture2D path for these thumbnail bundles. It does not
convert other UI resources, model, animation, scene, material, or shader
bundles. Those need separate class/dependency analysis and, when shader
bytecode must be rebuilt, a Unity editor and Metal toolchain. This machine has
neither Unity Editor nor the `metal` compiler, so it cannot claim full
Android-to-iOS conversion. Catalog revisioning and immutable r5 snapshot
packaging preserve a validated candidate but do not prove runtime compatibility.
