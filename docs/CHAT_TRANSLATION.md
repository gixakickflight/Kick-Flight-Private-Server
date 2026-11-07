# Chat translation

Server-side only; no client change. Chat messages are translated by **otomo chat** (services/chat), into the languages its
`chat.rules` document declares (tenant data: Kick-Flight publishes pt, es, ja, en). The adapter (`KickFlight.BootstrapApi`) makes
no model call and holds no key; it only picks which text each reader gets. With translation off at chat (or
`Chat:Translation:Enabled=false` here) chat behaves exactly as before: everyone reads the original.

## Contract with otomo chat

Each message of `GET /internal/chat/channels/{id}/messages` may carry (`Otomo/IOtomoChat.cs`, `ChatMessageView`):

- `translations`: `{"<lang>": "<text>", ...}`, absent or empty while there is none;
- `translation`: `"pending" | "done" | "failed" | "skipped"`, absent altogether when the chat instance has translation off.

Both are optional: a chat that does not send them is read as "no translation".

## Adapter side (`ChatService.cs`, `ChatTranslation/`)

- **Reader language** (`ChatLanguageResolver`): client IP -> country (DB-IP mmdb) -> `pt` (BR PT AO MZ CV GW ST TL MO), `es` (ES MX
  AR CO PE VE CL EC GT CU BO DO HN PY SV NI CR PA UY PR GQ), `ja` (JP), otherwise `en`. Private/unknown IP, missing DB -> `en`.
  Resolved once when the websocket opens.
- **Client IP** (`ClientIpResolver`): `X-Real-IP` (set by nginx) or the right-most non-proxy `X-Forwarded-For` hop, used only if
  the direct TCP peer is in `Chat:GeoIp:TrustedProxies` (default: loopback, RFC1918, fc00::/7).
- **Read selection** (`ChatService.SelectText`): a reader gets `translations[readerLang]` when present and non-blank, else the
  original body. The author's own connection always gets the original. Snapshots (history on listen) and live pushes use the same
  rule, with whatever translations exist at the time of the read.
- **Own messages:** the websocket is unauthenticated and the shipped client never sends a Firebase `auth` frame, so the
  `kfchat-dummy-token.<playerId>` startup token is dormant. The effective rule is an IP match: every authenticated HTTP request
  records the player's client address in memory (6 h, 20000 entries max, never persisted), and a websocket opened from that address
  counts as that player's. Players behind one NAT see each other's originals, which is the safe direction.
- **Hold-back** (`ChatService.Deliverable`): the relay does not push a message whose `translation` is `pending` until chat reports it
  settled (done/failed/skipped/absent) or `sent_at + Chat:Translation:MaxBroadcastDelayMs` (default 10000) has passed. Messages after a
  held one wait too, and no listener cursor moves past it: the next poll re-reads it, so the order the client sees is chat's.
  Snapshots are never held.

## Settings

| Setting | Default | Meaning |
| --- | --- | --- |
| `Chat:Translation:Enabled` | `true` | Read-side selection and hold-back. `false`: everyone gets the original, nothing is held. |
| `Chat:Translation:MaxBroadcastDelayMs` | `10000` | Longest a `pending` message is held, from its `sent_at` (0-30000). |
| `KF_GEOIP_DB` / `Chat:GeoIp:DatabasePath` | unset (`/var/lib/geoip/dbip-country.mmdb` on the VPS) | DB-IP Country Lite mmdb. Missing -> `en`. |
| `KF_CHAT_FORCE_LANGUAGE` / `Chat:GeoIp:ForceLanguage` | unset | Dev override: `pt`/`es`/`ja`/`en` for every reader. |
| `KF_CHAT_FORCE_COUNTRY` / `Chat:GeoIp:ForceCountry` | unset | Dev override: ISO country used instead of the IP lookup. |
| `Chat:GeoIp:TrustedProxies` | loopback + private ranges | Peers whose forwarding headers are trusted. |

Which languages exist, the model, its key, prompt, context, retries and cost are otomo chat's configuration, not this repository's.

## GeoIP database and attribution

The VPS ships the `geoip-db` compose service (`deploy/geoip/`) that downloads the monthly DB-IP file into the `geoip-data` volume; the
`api` container mounts it read-only and `GeoIpCountryLookup` reloads the file when it changes (checked every 10 minutes). Dev machine:
`scripts/update-geoip-db.sh [path]` (default `.local/geoip/dbip-country.mmdb`) and set `KF_GEOIP_DB`. The database is not committed.

IP Geolocation by [DB-IP](https://db-ip.com) - "IP to Country Lite", CC BY 4.0. Read with the MaxMind.Db NuGet reader.

## Tests

`tests/KickFlight.BootstrapApi.Tests/ChatTranslationTests.cs`: country map, IP extraction with/without trusted proxy, options,
the chat message fields (present/absent), read selection, own-message rule (auth frame and IP), and the hold-back against an
in-memory `IOtomoChat` (held while pending, order kept behind a held message, window expiry, snapshots not held, switch off).
Set `KF_GEOIP_DB` to a real mmdb to also run the lookup test against DB-IP data.
