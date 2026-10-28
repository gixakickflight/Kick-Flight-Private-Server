# The economy: the adapter is a client of otomo

The adapter holds no value. Every balance, ticket, disc and costume the Kick-Flight client is shown comes from
otomo's economy service, read on the player's own access token; every purchase and every draw is one call to that
service. `SessionState` keeps what a player *chose* - their name, their kicker and costume, their decks, their gear
slots, their rank - and nothing they own.

This is the port described by `ECONOMY_RESTORATION_MAP.md` steps 1 and 2 onto otomo, and the counterpart of
`OTOMO_IDENTITY.md`: accounts moved to otomo first, values move now.

## The client

`Otomo/OtomoEconomy.cs` is the client; `Otomo/IOtomoEconomy.cs` is its interface and the wire types, which are
economy's own bodies (`services/economy/internal/api/player.go`) rather than a local invention.
`Otomo/EconomyCatalog.cs` is the translation table, and `Otomo/EconomyPayloads.cs` turns one player's economy state
into the client's arrays.

| Client call | Economy route | Credential |
|---|---|---|
| `GetWalletAsync` | `GET /api/player/economy/wallet` | player token |
| `GetInventoryAsync` | `GET /api/player/economy/inventory` | player token |
| `GetShopAsync` | `GET /api/player/economy/shop` | player token |
| `PurchaseAsync` | `POST /api/player/economy/shop/purchase` | player token |
| `GetPoolsAsync` | `GET /api/player/economy/gacha/pools` | player token |
| `RollAsync` | `POST /api/player/economy/gacha/roll` | player token |
| `GetTasksAsync` | `GET /api/player/economy/tasks` | player token |
| `ClaimAsync` | `POST /api/player/economy/tasks/claim` | player token |
| `ReportProgressAsync` | `POST /internal/economy/progress` | game-server service key, opt-in |

The player token is the one `OtomoIdentity` already caches per device: `KfIdentityTicket` carries otomo's device id,
and `IOtomoIdentity.GetSessionForDeviceIdAsync` hands back that device's session without deriving the id a second
time. No route here takes a player id, because on the player plane the token *is* the subject - there is no argument
a caller could change to reach another player's wallet.

Two transport rules, both tested in `OtomoEconomyTests`:

- **One retry, and only for a failure that says nothing about the request** - a transport error or a 5xx. The retry
  rebuilds the request from the same values, so the same idempotency key travels with it and economy answers a
  replay rather than charging twice.
- **A 4xx is an answer and is never repeated.** It is reported as `OtomoEconomyFailure.Refused` with economy's own
  code (`insufficient_funds`, `unknown_offer`, `not_claimable`, …), so a caller can tell "you cannot afford this"
  from "the economy is down" without parsing a message.

`OtomoEconomyOptions` (`Otomo:Economy` in configuration) is the configuration. `Enabled` is off by default, and with it
off a request that needs a value fails closed - see *Failures* below.

## The translation table

otomo is game-agnostic: an item id, a currency code, an offer id and a pool id are opaque strings there, and no
Kick-Flight vocabulary appears upstream. `EconomyCatalog` is the only place the two vocabularies meet.

| Economy name | Client meaning |
|---|---|
| `jetcoin` (currency) | Item 1, `GoodsType.JetCoinFree` (101) |
| `(not published: no IAP)` (currency) | Item 2, `JetCoinPaid` (102) |
| `(not published: disc build-up not ported)` (currency) | Item 3, `DiscForce` (302) |
| `(not published: retired)` (currency) | Item 4, `KickPoint` (701) |
| `fragment` (item) | Item 5, `GearMaterial` (202) |
| `fragment` (item) | Item 6, `DiscFragment` (303) |
| `kicker_ticket` (item) | Item 7, `KickerGachaTicket` (401) |
| `(not published: disc gacha retired)` (item) | Item 8, `DiscGachaTicket` (402) |
| `disc:<discId>` (item) | a disc in `userDiscList`; owning it is level 10 |
| `skin:<kickerCostumeId>` (item) | a costume in `userKickerList`; owning one owns its kicker |
| `(no offer)`, `ticket.single`, `(no offer)`, `(no offer)` (offers) | goods-shop products 1-4 |
| `cosmetic.standard` (pool) | the kicker gacha (`ResponseGacha.id` 1) |

The startup payload always carries all eight Item rows, in id order, whether or not the player holds any - the
client's counters screen has always been served that way and its row count is part of what it renders. A currency or
item economy holds that has no row here is logged and skipped; it is never folded into a guess.

## What each payload is built from

| Client field | Source |
|---|---|
| `userItemList` | `wallet.balances` + `inventory.items`, mapped by `EconomyCatalog` |
| `userDiscList` | `inventory.items` with the `disc:` prefix, level 10, filtered to the served Disc master |
| `userKickerList` / `userKickerCostumeList` | `inventory.items` with the `skin:` prefix, grouped by kicker, **plus** the save's active costume and kicker 1's tutorial costume |
| `goodsShopProductList` | the adapter's four product rows, priced and gated by the matching shop offer |
| `kickerGachaGroupList` | the served `KickerGacha` row, priced from the matching pool, drawable only while that pool is open |
| `discGachaGroupList` | the served `DiscGacha` row, `drawableCount` 0 and `completedDisplayFlag` true (retired) |
| `/goodsShop/buy` receipt | the purchase result's `items` (their `total` is what the player holds afterwards) |
| `/gacha/kickerDraw` drops | the roll's `draws`, each `skin:<costumeId>` turned back into a costume row id and its kicker |

Two deliberate additions: the active costume and kicker 1's tutorial costume are always served even when the
inventory says nothing, because they are the player's own selection from the save (identity, not value) and a client
handed no kicker cannot render the player at all.

A disc the player owns is level **10**. The economy models possession, not per-disc levels, and the two things that
used to raise a level - the disc gacha and `/disc/buildup` - are gone.

## Retired features

- **`/gacha/discDraw`** answers the DTO's full shape with no drops, `drawableCount` 0 and `completedDisplayFlag`
  true, and logs that it is retired. Discs are possessions of the economy now; nothing draws them. The route stays
  in the switch rather than disappearing, because a missing route is a network error on a screen that otherwise
  works.
- **`/disc/buildup`** reads and validates its request, then answers the disc as it stands at level 10 with the
  player's Disc Force unchanged, and logs that it is retired. There is no per-disc level to raise and no
  player-plane route that would debit Disc Force for one.

Both keep the fields the client deserializes, because the client is a binary nobody can recompile.

## The rank-up grant: not implemented, deliberately

`/battle/result` still applies the rank progression this server has always applied, and a rank-up pays nothing.
Economy deliberately has no player-plane route that pays a class on the player's own assertion, and an adapter is not
a game server: it must not hold a payout-capable key. The three options, and why this port took none of them:

1. **The adapter holds a game-server key and calls `POST /internal/economy/grants` on a rank change.** Cheap, and
   wrong: that key can pay any configured class to any player, and the adapter would hold it in the internet-facing
   process, on the strength of a rank change a client asked for. If it is ever done, it must be a separate
   `Otomo:Economy:ProgressEnabled`-style switch, off by default and documented as a trust decision - Economy
   `ProgressEnabled` is already such a switch, and one is enough to explain.
2. **A new economy route that pays a rank-up.** Same trust problem with more surface: the route would have to decide
   whether the rank change was real, which is a game fact economy does not have.
3. **The match claim path (`T1`).** The right producer is the thing that knows a match finished: the matchmaker or
   the room server writes the claim (`POST /internal/economy/matches`) and the payout follows a match economy
   observed, keyed by its id. That is economy's own design for "a game fact, not a game server's word", and it is
   the option this port recommends. It is not this adapter's ticket: the adapter issues battle entries, it does not
   own matches, and inventing a claim from a client's `/battle/result` would defeat T1 rather than satisfy it.

So rank-up rewards wait for a producer that can tell economy a match happened. Nothing else in this port depends on
it.

## The progress report

`/battle/result` reports four counters to `POST /internal/economy/progress`: `battle.play` (1 per battle),
`battle.win` (1 for a ranked win), `battle.kill` (`killCount` from the request) and `battle.mvp` (`mvpFlag`). Each
key is `<matchId>:<counter>`, where the match id is the battle entry id this server issued, so a retried report
counts once.

It pays nothing: what a counter is worth is a track's step, and the player claims that on their own plane. The
numbers are the client's word, checked only against an entry this server issued - **the same trust level as the
ranked result this handler already applies**, which is why the report is acceptable at all.

It needs the game-server service key, so it is off unless `Otomo:Economy:ProgressEnabled` is true *and* a key is
readable. With it off the adapter reports nothing and `/battle/result` is unchanged; a report that fails is a warning
and never breaks the result screen.

## Failures

| Situation | Answer |
|---|---|
| `Otomo:Economy:Enabled` off, or the economy unreachable, on a read (startup, home, shop, reset, capsule counters) | HTTP 503, `x-app-status-code: 1`, body `{"error":"economy-unavailable","reason":…}` |
| A purchase or a draw economy refused | HTTP 200, `x-app-status-code: 1`, schema-shaped body; the reason is logged |
| A purchase that succeeded but whose counters could not be re-read | HTTP 200 with the purchase's own balances and item totals, and an error in the log - never a 503, which would invite the player to buy again |

Failing closed on a read is the whole point: an empty wallet and an empty disc list are a lie the client would
render, and an operator would only hear about it from a player.

## Configuration

| Variable | Default | Notes |
|---|---|---|
| `Otomo__Economy__Enabled` | `false` | off, every value-bearing route fails closed |
| `Otomo__Economy__BaseUrl` | none | economy's player listener, e.g. `http://otomo-economy:8080`; the `/api/player/economy` prefix is kept |
| `Otomo__Economy__InternalBaseUrl` | BaseUrl | economy's internal listener for the progress report |
| `Otomo__Economy__Timeout` | `00:00:05` | per call; at most two calls are made |
| `Otomo__Economy__RetryDelay` | `00:00:00.100` | wait before the single retry |
| `Otomo__Economy__ProgressEnabled` | `false` | the battle progress report; needs a key as well |
| `Otomo__Economy__ServiceKeyFile` | none | the gameserver service key; read per report, so rotation needs no restart |
| `Otomo__Economy__ServiceKey` | none | inline alternative for a test or a local run |

## What is left in `SessionState`

`UserId`, `UserName`, `KickerId`, `KickerCostumeId`, `ActiveDeckNumber`, `Decks`, `CostumeGears`, `PendingGear`
(and the derived `PlayerId`/`HasName`). That is a player's choices and their gear slots - not a balance, a ticket, a
disc or a currency. `PostgresPlayerStore` migration 4 drops the four currency columns; a save document written before
it still carries the old keys, which are ignored on read and gone on the next write.

## Tests

- `OtomoEconomyTests` - the client against a scripted handler: exact path, bearer token, body field set, retry once
  on a 5xx with the same key, no retry on a 4xx, transport failures, the disabled switch, and the internal progress
  body including `ref`.
- `EconomyPayloadsTests` - the payload builders against a state written by hand: the eight rows, discs at level 10,
  costumes grouped by kicker, draws turned back into costume rows, totals as economy returned them.
- `EconomyEndToEndTests` - the adapter's routes against `FakeOtomoEconomy` over real HTTP: startup, home, a purchase,
  a roll, a refusal, the retired disc draw, and a startup that fails closed when the economy is unreachable.
- `ShopGachaTests` keeps the DTO shapes and the master tables, which are the client's contract rather than a value.
