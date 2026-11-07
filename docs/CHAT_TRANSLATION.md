# Chat translation

Server-side only; no client change. Every chat message is translated once, on send, into Portuguese, Spanish,
Japanese and English with Google Gemini. Each reader gets the language derived from their country (offline GeoIP).
Without `GEMINI_API_KEY` the feature is off and chat behaves exactly as before.

## Architecture

The in-game chat lives inside `KickFlight.BootstrapApi` (`ChatService.cs`), not in a separate service:

- **Send:** `POST /chatRoomComment/write` (`DemoSessionApi.Chat.cs`) -> `ChatService.WriteAsync`. The reply is `{}`.
- **Read:** the client reads a Firebase-RTDB-compatible websocket, `/.ws` (`Program.cs`, proxied by `deploy/nginx.vps.conf`).
  Messages are pushed live and sent as a snapshot when a room is listened to. There is no polling endpoint.
- **Storage:** in memory + `room-<id>.json` per room under `Chat:DataDirectory` (`/srv/chat` on the VPS), newest 100 kept.
  No database.

Translation flow (`src/KickFlight.BootstrapApi/ChatTranslation/`):

1. `WriteAsync` stores the original exactly as before and returns; the HTTP response does not wait for the model.
2. When enabled, `ChatTranslator` is called with the message and the previous `KF_CHAT_TRANSLATE_CONTEXT_MESSAGES` messages of the
   same room (author + text, oldest first). It builds the prompt (`ChatTranslationPrompt`), calls Gemini through the
   `ITextGenerationClient` interface (`GeminiInteractionsClient`), validates the JSON in C# (`ChatTranslationParser`) and the result
   is stored in the message's `Translations` field (`{pt,es,ja,en}`) in the room file.
3. The live push is held back per room (in write order, because the client drops children dated at or before its last
   fetch) for at most `Chat:Translation:MaxBroadcastDelayMs` (10 s, counted from receipt of the message). After that, or when translation failed, readers get the
   original; a late translation is still saved for later snapshots.
4. **Reading never translates.** `ToNode` picks `Translations[reader language]` if present, else the original `comment`.
   The author's own connection always gets the original (see "Own messages").

Reliability: at most `MaxConcurrency` (4) calls in flight, `MaxPending` (64) waiting (more are dropped), 8 s per attempt, one
retry after a short backoff on 429/5xx/network errors; other 4xx, `status: incomplete` (token limit) and malformed output are not
retried. Messages with no letters (emoji, digits) and stamp-only messages are not sent to the model.

Reader language (`ChatLanguageResolver`): client IP -> country (DB-IP mmdb) -> `pt` (BR PT AO MZ CV GW ST TL MO), `es` (ES MX AR CO
PE VE CL EC GT CU BO DO HN PY SV NI CR PA UY PR GQ), `ja` (JP), otherwise `en`. Private/unknown IP, missing DB -> `en`.
Resolved once when the websocket opens. IP->country results are cached in memory.

Client IP (`ClientIpResolver`): `X-Real-IP` (set by nginx) or the right-most non-proxy `X-Forwarded-For` hop are used only if the
direct TCP peer is in `Chat:GeoIp:TrustedProxies` (default: loopback, RFC1918, fc00::/7; the API is only reachable through the
compose network). From any other peer the headers are ignored. Chat does not use gRPC.

## Own messages

An author should read their own original text. The websocket is unauthenticated, and the real client (verified on the
emulator, 2.11.804) never sends a Firebase `auth` frame, so the `kfchat-dummy-token.<playerId>` token served at startup is
dormant (harmless; kept in case a client sends it). The effective rule is an IP match: every authenticated HTTP request records the
player's resolved client address in memory (6 h, 20000 entries max), and a websocket opened from the same address counts as that
player's connection, so it gets the original for that player's own messages. Consequence: players behind one NAT/IP see each
other's originals instead of translations. Dev: a second test "player" needs a different source address (e.g. bind 127.0.0.2 and
add it to `Harness:DirectClientHosts`).

## Latency and delivery

Verified live with `gemini-3.5-flash-lite` (2026-11-06): calls took ~0.7-1.2 s at first and 2-9 s later in the same session
(raw curl 5.4-8.3 s, HTTP 200), i.e. service-side tail latency. The live push to listeners waits at most `MaxBroadcastDelayMs`
(10000 ms, counted from receipt, so a burst is not held N times that); after that the original is pushed and the translation is attached and persisted when it arrives, so it shows on the
next read (snapshot/reconnect). The first 8 s attempt fits inside the window; a retry (0.5 s backoff + up to 8 s, so up to ~16.5 s after receipt) usually finishes after it and is saved as a late translation. A call that fails both attempts leaves the message untranslated (readers get the original).
Tuning knobs: `Chat:Translation:MaxBroadcastDelayMs`, `RequestTimeoutSeconds`. The log line "Chat message ... pushed to N
listener(s) X ms after receipt (translations attached|none)" shows the outcome per message.

## Environment variables

| Variable | Default | Meaning |
| --- | --- | --- |
| `GEMINI_API_KEY` | unset | Google AI Studio key. Unset/empty disables translation. Never logged. |
| `KF_CHAT_TRANSLATE_MODEL` | `gemini-3.5-flash-lite` | Model id. |
| `KF_CHAT_TRANSLATE_CONTEXT_MESSAGES` | `20` | Previous room messages sent as context (0-100). |
| `KF_GEOIP_DB` | unset (`/var/lib/geoip/dbip-country.mmdb` on the VPS) | DB-IP Country Lite mmdb. Missing -> everyone gets `en`. |
| `KF_CHAT_FORCE_LANGUAGE` | unset | Dev override: `pt`/`es`/`ja`/`en` for every reader (emulator/LAN, where the IP is private). |
| `KF_CHAT_FORCE_COUNTRY` | unset | Dev override: ISO country (e.g. `BR`) used instead of the IP lookup. |

Tuning lives in `appsettings.json` / `Chat__Translation__*` / `Chat__GeoIp__*`: `RequestTimeoutSeconds`, `MaxBroadcastDelayMs`,
`MaxPending`, `MaxConcurrency`, `RetryDelayMs`, `MaxOutputTokens`, `ThinkingLevel`, `BaseUrl`, `ApiRevision`, `TrustedProxies`.

## Creating the key (Google AI Studio)

1. Open https://aistudio.google.com/apikey and create a key in a Google Cloud project.
2. Restrict it to **Gemini API** only (key restrictions in the Cloud console); new unrestricted keys are rejected.
3. VPS: put `GEMINI_API_KEY=...` in the persistent root `.env` (see `deploy/.env.vps.example`). `deploy/docker-compose.vps.yml` forwards
   it to the `api` container; nothing is stored in the repository or in CI secrets (the deploy script reuses the VPS `.env`). Recreate the
   `api` container to apply.
4. Local: `GEMINI_API_KEY=... KF_CHAT_FORCE_LANGUAGE=es dotnet run --project src/KickFlight.BootstrapApi`.

## GeoIP database and attribution

The VPS already ships the `geoip-db` compose service (`deploy/geoip/`) that downloads the monthly DB-IP file into the `geoip-data`
volume; the `api` container mounts that volume read-only and `GeoIpCountryLookup` reloads the file when it changes (checked every 10
minutes). For a dev machine: `scripts/update-geoip-db.sh [path]` downloads the current month (default `.local/geoip/dbip-country.mmdb`)
and you set `KF_GEOIP_DB`. The database is not committed.

IP Geolocation by [DB-IP](https://db-ip.com) - "IP to Country Lite", CC BY 4.0. Read with the MaxMind.Db NuGet reader.

## Privacy, cost, limits

- **Privacy:** with a key set, chat text, the author's display name and up to 20 previous messages of the room are sent to Google
  (`store: false` is requested). Document this in the privacy text before enabling it. Client IPs are only used locally for the country lookup
  and are not stored.
- **Cost:** one request per text message, estimated (not measured) at a few hundred input tokens (system prompt + context) and under 200 output tokens. Lower
  `KF_CHAT_TRANSLATE_CONTEXT_MESSAGES` to reduce it. Check current pricing/quota of the chosen model in AI Studio; the free tier has
  rate limits (excess requests fall back to the original text).
- **Moderation:** translations are not filtered beyond the model's own safety behavior; the existing client NG-word handling is unchanged.
- **Wire shape:** the Gemini Interactions request/response shape was taken from the official docs on 2026-11-06 (`POST
  /v1beta/interactions`, `x-goog-api-key`, `Api-Revision: 2026-05-20`). Error-code semantics other than 429/5xx (retry) and other 4xx
  (permanent) are unverified. Swap `ITextGenerationClient` if the API changes.
- **Existing data:** old `room-*.json` files load unchanged (no `Translations` property -> original text). Old messages are never translated.

## Tests

`tests/KickFlight.BootstrapApi.Tests/ChatTranslationTests.cs` (mocked model client): country map, IP extraction with/without trusted
proxy, prompt/context assembly, Gemini response parsing and validation (malformed, incomplete, partial), HTTP request shape and status
mapping, retry/queue behaviour, storage, read-path selection with fallback and own-message rule, delivery ordering, legacy room-file
migration, and a websocket end-to-end. Set `KF_GEOIP_DB` to a real mmdb to also run the lookup test against DB-IP data.
