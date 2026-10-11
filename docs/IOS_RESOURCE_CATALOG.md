# Isolated iOS resource catalog

The iOS profile uses asset version **12346**, platform **9**, and Octo revision **41**. Its immutable payload tree is
`/opt/kickflight/.local/ios-resources/12346-r2` on the VPS; the tracked manifest is
`src/KickFlight.BootstrapApi/catalog-ios-12346.json`. The staged `catalog.json` must be byte-identical to that
manifest (`SHA-256 002375c82ab288b2cfad515fd41d57b1ae501033e41ddac01e8cbabcb7863e8d`). The catalog enumerates 493
raw objects from the pinned 2.11.0 iOS IPA: 440 AssetBundles and 53 resources, totaling 238,014,682 payload bytes.
Bundle inventory reports Unity 2018.4.11f1, target platform 9, and all direct dependencies present. The content-addressed
archive is `12346-r2.tar.gz` (`SHA-256 951e6e7419ccae575d4aa03c234a5064a0b7e826c6c06b58cf774b40677171a3`).

An iOS boot request is selected only for a direct-client `POST /boot/index` carrying
`x-app-asset-platform: 2`; its D2C body changes the existing boot response's `assetVersion` to 12346. The forced-update
gate remains earlier in the request path. Requests to `/v1/list/12346/{fromRevision}` are built from the pinned catalog:
revisions 0 through 40 receive all 493 rows (ADD from 0, UPDATE otherwise), while revision 41 receives the revision and
the CDN URL only. The original nullable Octo name remains absent on the wire; `logicalName` is only a server-side
diagnostic label. The database points to `https://kick-flight-fenix.us.ci/ios/cdn/{o}`.

`GET /ios/cdn/{objectName}` reads only the iOS resource snapshot. A missing, partial, checksum-mismatched, or wrong-version
snapshot fails closed; the server does not fall back to Android `/cdn` resources. Android asset version 12345 and its
fixtures and routes remain separate. `/health/ready` reports `iosCatalogReady`; stage the complete r2 tree before selecting
the iOS boot profile. The API validates the manifest identity, 493-row 440/53 kind counts, each payload SHA-256/MD5/CRC32
and size before enabling the profile.

This is a static payload and routing validation. It does not establish iOS runtime compatibility or that all game assets
needed by every scene exist in the source IPA.
