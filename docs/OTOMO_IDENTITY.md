# otomo owns identity; the adapter is an ordinary otomo client

Status: implemented on `feat/otomo-identity` (F1 of tanukifurhire/kf-otomo#1). This note replaces the old
statements that the adapter kept accounts and sessions; there are none left to keep.

## What the adapter no longer owns

| Before | Now |
| --- | --- |
| `players.uuid` was the device the adapter created a player for, on first contact, forever (`ResolvePlayerId`) | otomo decides who a device is. The adapter asks otomo, and files the player's *game* state under otomo's account id |
| `sessions(access_token, player_id, session_key)`, written on every login and consulted on every request | no table. The token handed to the client is signed (HMAC) and carries what a request needs; a restart does not drop it |
| the fixed `demo-access-token-…` every player used to share | gone. It was not signed, so nothing about it could be trusted; it is now rejected |
| player ids allocated per device, so a reinstall was a new player | a device logs in once, and the same device id always reaches the same otomo account, so a reinstall is the same player |
| no passwords existed, but the session and identity code pretended to be an account store | nothing in the adapter resembles an account: no password, no identity index, no session row |

What is left in the adapter is one thing per device: an **in-memory** cache of otomo's access+refresh pair, keyed by
otomo device id, bounded (`Otomo:CacheCapacity`) and evicted on idle (`Otomo:CacheIdle`). It is a cache: losing it
costs one login, never a different player.

## The request path

```
                  POST /auth/index (D2C body: {hash, uuid})
   client ───────────────────────────────────────────────► DemoSessionApi
                                                             │ device uuid → otomo device id (SHA-256, base64url)
                                                             │ cache hit? ──no──► POST {otomo}/auth/anonymous
                                                             │                        {device_id}
                                                             │ ◄── access+refresh pair (sub = otomo player id)
                                                             │ ResolveGamePlayer(otomo id, device uuid) → row id
                                                             │ issue signed KF access token
   client ◄── x-app-access-token, x-app-user-id (numeric) ───┘
                  any later request: x-app-access-token
   client ───────────────────────────────────────────────► verify HMAC → row id → load state
                                                             (no call to otomo at all)
```

* **One login per device**, not per request. Concurrent first requests from one cold device share a single
  `POST /auth/anonymous`: each device's cache entry has its own gate, and the second caller waits for the first.
* **Refresh on expiry** (`Otomo:RefreshSkew` early): `POST {otomo}/auth/refresh`, which rotates inside the same
  family. The same gate serialises it, so two requests from one device cannot present the same refresh token twice -
  otomo treats that as theft and revokes the family.
* **Logout**: `IOtomoIdentity.LogoutAsync(deviceUuid)` → `POST {otomo}/auth/logout`, which revokes that device's
  family only. Other devices of the same account keep their own families, because otomo starts one per login. The
  KF client has no logout route, so this is called by tooling and by the tests.
* A family otomo has already revoked cannot be refreshed; the device signs in again with the same device id, lands
  on the same account, and gets a new family.

## The one thing the KF protocol would not let us change

The wire requires a **numeric** player id: the client parses `x-app-user-id` with `long.TryParse` and renders
"Player NNNN" from its low digits, and `SessionState.PlayerId` is that number. otomo's account id is a uuid. So
`players.id` stays a bigint and `players.otomo_player_id` holds the account:

* the number is a **filing handle for game state** (save document, ranks, follows, replays), never a credential;
* nothing authenticates by it - the signed token carries the otomo identity, and the row is looked up from that;
* it is minted once per account and never changes, so a player keeps their name, rank, inventory and friends when
  they sign in from another device.

Keying every game table by the uuid directly would have meant changing `x-app-user-id`, the rank list, the social
graph and the replay index at once - a protocol change, which this was explicitly not.

## Migration

`PostgresPlayerStore` migration 3:

```sql
alter table players rename column uuid to legacy_device_uuid;
alter table players add column otomo_player_id text unique;
drop table sessions;
```

**What can be mapped, and how.** A pre-otomo row is *adopted*, not rewritten in SQL, in
`ResolveGamePlayer(otomoPlayerId, legacyDeviceUuid)`: the first otomo account that presents a device takes over the
row that device already had (`where legacy_device_uuid = $2 and otomo_player_id is null`), keeping its id, name,
rank, inventory and social graph. Adoption happens on first contact after the deploy, because it cannot happen
earlier: otomo's account id is a uuid otomo assigns and indexes by an HMAC of the device id under a pepper this
process never sees, so no query here can compute it.

**What cannot be mapped.** A player who never connects again stays `otomo_player_id is null`. They are reported, not
guessed:

```sql
-- rows still waiting for their account, with the device that will claim them
select id, legacy_device_uuid, display_name, updated_at
from players
where otomo_player_id is null
order by updated_at desc;
```

No rows are deleted, no ids are merged, and no account is invented for a row nobody claims. The JSON store does the
same thing at file level: `data/users/_identities.json` (device → old id) is read for adoption and never written
again, while `data/users/_otomo-players.json` (account → row id) is the live index.

## Configuration

| key | default | meaning |
| --- | --- | --- |
| `Otomo:Enabled` | `false` | hard switch. Off: the stack boots, but no player request is served |
| `Otomo:BaseUrl` | *(empty)* | otomo auth service, e.g. `http://otomo-auth:8080` |
| `Otomo:Timeout` | `00:00:05` | bound on one otomo call; there is no retry |
| `Otomo:RefreshSkew` | `00:00:30` | refresh this long before the access token expires |
| `Otomo:CacheCapacity` | `4096` | device sessions held in memory |
| `Otomo:CacheIdle` | `00:30:00` | an unused device session is dropped after this |
| `Otomo:TokenSecret` | *(empty)* | HMAC key for the KF access token. Empty = a random key per process, so every restart signs clients out |

Environment form: `Otomo__Enabled`, `Otomo__BaseUrl`, `Otomo__Timeout`, `Otomo__RefreshSkew`,
`Otomo__CacheCapacity`, `Otomo__CacheIdle`, `Otomo__TokenSecret`.

A deployment that restarts must set `Otomo:TokenSecret`; without it, every client is dropped by the restart. The
`deploy/` compose files do not set these yet - adding them is a deployment change, not part of this work.

## Failure behaviour

| situation | what the client gets | what the operator gets |
| --- | --- | --- |
| otomo unreachable, timed out, or 5xx | `503`, `x-app-status-code: 1`, plaintext `{"error":"identity-unavailable","reason":"Unavailable"}` | one `LogError` with the masked device id and the reason; **no retry**, so a down otomo never sees a storm |
| otomo refuses the device (400/401/403) | `401`, `x-app-status-code: 1`, `{"error":"identity-rejected", …}` | one `LogWarning` with the reason |
| a token this server did not sign | `401`, `x-app-status-code: 1`, empty body (what the client already handled) | one `LogWarning` |
| `Otomo:Enabled` is false | `/auth/index` and any request carrying a token: `503` + status `1`; fixtures, CDN, health and `boot/index` keep working | one `LogError` per refusal |
| refresh rejected (revoked family) | transparent: one re-login with the same device id | `LogInformation` saying the family was refused |
| two requests refresh at once | one `POST /auth/refresh`, the other waits and reuses the result | - |

Nothing here answers `500`: a failed identity call is a state of the world, not a bug, and the game's error contract
is what the client knows how to show.

## Verifying it

`tests/KickFlight.BootstrapApi.Tests/OtomoIdentityTests.cs` and `OtomoAdapterEndToEndTests.cs` run against
`FakeOtomoServer`, an in-process otomo auth service reached over real HTTP (`/auth/anonymous`, `/auth/refresh` with
rotation, `/auth/logout` with family revocation). They pin: login once per device and cache reuse; refresh on expiry
inside the family; exactly one login and exactly one refresh when twelve requests race; logout revoking that device
only; the unreachable and switched-off paths; the cache bound and idle eviction; and, end to end through the real
request path, that a second launch reuses the login and that a server without otomo fails closed.

`ServerTestHostFixture` is unchanged: the suite's hosts pick otomo up from `Otomo__*` environment variables that a
module initializer sets, so every existing test authenticates through the fake otomo as a real deployment would.
