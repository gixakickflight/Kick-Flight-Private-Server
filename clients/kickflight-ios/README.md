# iOS client transport core

`Assets/KickFlight/Transport` is a Unity-independent, `netstandard2.1`
compatible source library. It implements the HTTP/D2C boundary only; it does
not include game behavior, auth-material generation, or Photon.

Run the codec and mocked-HTTP checks with:

```sh
./scripts/ios/test-transport.sh
```

The current harness passes 15 transport/bootstrap checks, including independent
OpenSSL vector reproduction. The arithmetic harness passes 2 checks, including
all 50 current `DiscGrowMasterData` rate rows:

```sh
./scripts/ios/test-logic.sh
```

The harness needs .NET 8, Python 3, and the OpenSSL CLI. The Unity-facing
libraries themselves target `netstandard2.1` and have no Python/OpenSSL runtime
dependency.

The fixed AES vector in the harness was generated independently with Python
`cryptography` using synthetic key bytes `00..1f`, IV bytes `10..1f`, and the
documented boot response JSON; the test script also reproduces it through the
OpenSSL CLI. No server or user credentials are part of the test. The code reads
keys from its caller and does not contain a production auth hash or token.

The transport has a separate-key POST method for auth: the server expects
`/auth/index` request JSON under the common key and encrypts its response under
the submitted session key. Subsequent session POSTs use that session key in
both directions.

Response reads are streamed and bounded. Defaults are 4 MiB for API replies,
8 MiB for master-table downloads, and 256 MiB for generic raw downloads; these
are client resource limits, not limits recovered from the server protocol.
The larger generic ceiling remains configurable for asset CDN payloads, while
master bootstrap uses its smaller dedicated ceiling. Large future asset flows
should download to a file/stream rather than retain multiple full copies in
memory.

## Runtime inputs still required

- Configure the production API origin as HTTPS at runtime or in an explicit
  environment-specific settings asset. HTTP is accepted only when the caller
  opts into local development.
- The client must obtain the D2C common code and the per-session 32-byte ASCII
  key from verified protocol/bootstrap configuration. This library does not
  guess the auth hash, UUID generation/lifecycle, or derive a key.
- Supply application-version, asset-platform, asset-revision, and access-token
  headers based on client configuration and returned session metadata.
- Parse the Octo protobuf schema and game DTOs in their own layers. This code
  downloads raw object bytes and decrypts API JSON; it does not claim that a
  decoded boot response renders a character.
- Unity editor version is intentionally unpinned. The host has no Unity editor,
  Xcode, Mac, or attached iPhone in the inventory environment; compile the
  source in the selected Unity editor and validate the IPA/device path there.

The reconstructed client still needs an independent investigation of Photon
handshake/operations and Android/iOS interoperability. See
`docs/ios-reconstruction/PROTOCOL.md` for the source evidence and gaps.

## Implemented bootstrap subset

`Assets/KickFlight/Bootstrap/AuthenticatedBootstrapper.cs` starts after the
title's `TAP START` action. It authenticates, downloads the master list,
validates each encrypted table's byte count and SHA-256 before decrypting it,
then fetches `/startup/index`. This is not the full title boot path.

Observed server contracts are documented at `DemoSessionApi.cs:896-897`
(master tables use the common key and a zero IV), `899-950,926-950`
(session authentication and token lookup), `1019-1045` (master and startup
routes), `1651-1703` (manifest/hash/header shapes and auth response), and
`D2CCodec.cs:5-43` (AES envelope). These handlers establish the data format,
not the retail client's complete request-generation logic.

Design choices in this client are deliberately explicit: the caller supplies
common key, session hash/key, device UUID, API headers, and JSON adapter; master
tables are fetched serially to make ordering and failure points observable;
size and SHA-256 are checked before decrypt; URLs must be same-origin HTTPS;
redirects are disabled; and no request is retried automatically. The server
source shows auth creates a fresh token, so retrying auth without client-side
reconciliation could create extra sessions. The JSON adapter only parses the
manifest fields needed for transfer; auth/startup/master JSON is returned as
opaque strings so unknown fields survive unchanged.

The mocked harness uses clearly synthetic credentials and recorded response
shapes. It proves only these local codec/ordering/integrity behaviors. It does
not contact the VPS, validate the real boot/Octo exchange, execute Unity, load
the recovered assets, or exercise Photon. `/auth/prepare` is not called here
because its required request body and live client timing remain unverified.
