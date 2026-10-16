# Existing server protocol contract for an iOS client

This is a source-backed inventory for the first Unity/iOS vertical slice. The
server is a compatibility target: this document does not propose changing its
wire behavior. Source references are relative to the repository root and use
line numbers from the inventory checkout at `d6745cef4cce3e91baaf3ad62adae5ab07fdc364`.

## Startup sequence

1. Connect to the configured API origin over HTTPS. The earlier Android trace
   in `docs/DISCOVERED_STARTUP_FLOW.md` records `POST /boot/index`, then
   `GET /v1/list/12345/0`, and a `TAP START` boundary before authentication.
   The accepted boot fixture is binary D2C (`config/fixtures/boot-index.accepted.json:2-13`);
   successful application status is `x-app-status-code: 0`. Its decoded
   `BootResponseData` shape is documented in `docs/DISCOVERED_STARTUP_FLOW.md`.
2. Fetch the Octo database from
   `GET /v1/list/{assetVersion}/{fromRevision}`. Current title metadata is
   asset version `12345`, revision `37`, and accepts revisions 0 through 37
   (`config/resources/title-minimum.json:2-48`). The response is protobuf
   `Octo.Proto.Database`; server routing can rewrite its top-level field 5
   (`urlFormat`) to the requested host (`src/KickFlight.BootstrapApi/Program.cs:270-282,289-310`).
   The client substitutes each object's six-character opaque name into
   `/cdn/{o}`; catalog routes map that name to stored bytes
   (`config/resources/README.md:1-26`, `src/KickFlight.BootstrapApi/Program.cs:227-240`).
3. After the title/start action, call `/auth/prepare` if the Android flow
   requests it, then `POST /auth/index`. The dynamic auth handler decrypts
   JSON with the common code and reads `hash` (exactly 32 ASCII bytes) and
   optional `uuid`; that hash's ASCII bytes become the session AES key. It
   returns `x-app-user-id`, `x-app-access-token`, status `0`, and an encrypted
   `{}` (`src/KickFlight.BootstrapApi/DemoSessionApi.cs:15,926-950,1667-1709`).
   The endpoint is persistent across server restarts through the player store.
4. With `x-app-access-token` on later POSTs, request `POST /download/master`.
   It returns encrypted JSON `masterDownloadList`; each row has `name`, SHA-256
   `hash` of the encrypted master bytes, `url`, and `size`. Fetch each
   `GET /demo-master/{name}` as raw octet-stream bytes
   (`DemoSessionApi.cs:1019-1027,1651-1665`).
5. Request `POST /startup/index`, which returns the player's kicker/disc/item
   state and current tutorial status. A nameless account receives status 206;
   a named account receives 207. New-name flow is `POST /tutorial/end`, then
   another `/startup/index`; the handler explicitly persists the name before
   the response (`DemoSessionApi.cs:25-37,1003-1009,1029-1036,1417-1507,1712-1747`).
6. Request `POST /home/index` for the home screen state. A basic battle then
   crosses additional API routes (`/battle/teamCreate`, `/battle/teamEntry`,
   `/battle/entry`, `/battle/start`, `/battle/end`, `/battle/result`); for a
   custom lobby, `/customBattle/prepare` and `/customBattle/start` are also
   used. See the route dispatch at `DemoSessionApi.cs:1038-1137` and the
   maintained DTO notes in `docs/CUSTOM_BATTLE_PROTOCOL.md:1-40`.

All API POST bodies consumed by the dynamic handlers are D2C binary JSON.
Unknown/missing session tokens are rejected with HTTP 401 and app status 1
(`DemoSessionApi.cs:928-948`). Application status headers must be checked in
addition to HTTP status; forced-update responses are a documented plaintext
exception (`DemoSessionApi.cs:1510-1526`).

## Wire encoding and content

- **D2C envelope:** bytes 0–15 are the IV/vector; remaining bytes are AES-256-CBC
  ciphertext with PKCS#7 padding. Plaintext is UTF-8 JSON. There is no
  compression step in `D2CCodec` (`src/KickFlight.BootstrapApi/D2CCodec.cs:5-43`).
- **Key selection:** `auth/index` bootstraps with the 32-byte ASCII common code;
  after auth, normal request/response bodies use the 32-byte ASCII `hash` from
  the request. Boot/auth fixtures and live responses use `application/octet-stream`.
  Normal response IVs are random; the served master tables are encrypted with
  the common code and a zero IV (`DemoSessionApi.cs:15,896-897,2315-2317`).
- **Master index:** the master list JSON itself is D2C encrypted under the
  session key. Its per-table `hash` is SHA-256 over the complete encrypted
  table, so the iOS client should hash downloaded bytes before decrypting.
  The server's `x-app-master-hash` changes when served table data changes
  (`DemoSessionApi.cs:16-20,749-751,1019-1027,1651-1665`).
- **Octo/CDN:** the database is protobuf, not D2C JSON. It describes resource
  names, paths and integrity metadata; each CDN object is served as the exact
  catalog file with range support and immutable cache headers. Android's Octo
  database builder emits CRC32 and MD5 in the protobuf and computes SHA-256
  and byte size for the local catalog (`scripts/build-title-resource-catalog.py:251-262,323-366`).
  Client validation should follow the fields present in the protobuf rather
  than assume all local catalog metadata is sent over the wire.
- **HTTP requirements:** preserve `x-app-status-code`, `x-app-access-token`,
  `x-app-user-id`, `x-app-datetime`, `x-app-master-hash`, and the asset/version
  request headers. Startup and battle response construction echoes the
  `x-app-asset-platform` and `x-app-asset-revision` values where needed
  (`DemoSessionApi.cs:1072-1085,1810-1827`).

## Battle and Photon boundary

The HTTP server publishes `photonCloudRegionList` with region id 1, code `jp`,
and app id `local-demo-app`, plus a separate `matchmakingFrontend` host and
gRPC port (`DemoSessionApi.cs:1493-1498`). The configured Luxon/Photon service
ports are Master 5055, Game 5056, and Name 5058
(`src/KickFlight.BootstrapApi/PhotonServerOptions.cs:3-14`). Matchmaking is a
separate gRPC service registered by `Program.cs:30,66`; team creation occurs
before joining a Photon room (`DemoSessionApi.cs:1806-1827`).

This proves endpoint/configuration boundaries, but does not prove a usable
Photon client handshake. `/health/ready` only requires Master reachability,
and its UDP probe merely sends four zero bytes without waiting for a reply
(`Program.cs:196-209`, `PhotonServerManager.cs:119-140`). The checked-out
worktree's Luxon submodule is an unpopulated gitlink, so its protocol
implementation was unavailable for source inspection here. Treat Photon
operation codes, parameter dictionaries, room serialization, encryption,
heartbeat/reliability settings, and iOS interoperability as open until the
Luxon source and Android traces/client IL2CPP establish them. A battle against
local AI through server match results is not evidence of Photon interoperability.

## First vertical-slice contract

Implement the API/CDN path first, in this order: TLS boot POST and D2C decode;
Octo protobuf parse and object downloads; auth request/response and token
storage; master index plus per-table download/hash/decrypt; startup/name/home.
Keep network, codecs, parsed DTOs, and Unity presentation separate. Once the
player is visible, test a battle entry only after choosing either (a) an
explicitly local bot battle harness or (b) the separate gRPC + Photon path.
Do not label (a) as multiplayer compatibility.

Android compatibility constraints are explicit in the same server contract:
preserve existing endpoint names and headers, D2C body rules, master-version
cache invalidation, Octo revision negotiation, application asset revision
echoing, and server-produced DTO shapes. Any unavoidable server correction
must retain Android behavior and be assessed against both clients.

## Open questions to close with client REA or traces

- The precise generated/authenticated `hash`, UUID lifecycle, and `/auth/prepare`
  request/response path; only the server's accepted auth shape is source-backed.
- Exact `/boot/index` request fields and all headers needed by the retail client.
- Exact Octo protobuf schema/field validation and which title objects are
  mandatory for the first visible character.
- Full endpoint sequence and payload fields for a stock matchmaking battle,
  beyond the route/DTO subsets already documented for custom battles.
- Photon protocol details and successful iOS-to-existing-server handshake.
- Any compression below HTTP/TLS or inside Photon payloads. No compression is
  applied by the server D2C codec, and no source evidence here establishes
  compression for the Photon channel.

Do not fill these gaps from empty IL2CPP dump bodies or endpoint names alone.
