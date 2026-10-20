# otomo integration, Phase 0: answers to §9

Status: findings for review, 2026-10-20. This answers the open questions in §9 of the otomo integration plan
(`OTOMO_INTEGRATION.md`, draft of 2026-10-19), alongside `ECONOMY_RESTORATION_MAP.md`. Nothing has been built
or deployed yet. All client evidence is static analysis of the 2.11.x `libil2cpp.so`; nothing was run on a
device.

## Decisions already taken (2026-10-20)

| Topic | Decision |
|---|---|
| Economy vs otomo O2 | **The economy is built in KF first, ready to move to otomo.** Steps 1-2 of the restoration map go into the adapter behind an economy interface, backed by a ledger with idempotency keys in KF's Postgres. When otomo's Economy service (O2) exists, that interface gets an otomo backend. This departs from plan §4.2 for the time being: until O2 exists, economy writes land in KF. |
| Hosting | **The NAS is staging and Gixarde's VM is live.** The NAS (`10.10.20.148`) runs `otomo-staging` and `kf-staging` (plan §8). Each phase that passes there is promoted to Gixarde's VM, where otomo runs as a second Compose project next to the live KF and its real players. §2 of the plan keeps the VM placement. §8 needs rewriting: staging is the NAS, not a second copy on the VM. |
| Existing balances (§9.4) | **Everyone is reset** to the authored starting grant. Name, player id, rank and social data are kept. This needs a player announcement before it goes live. |
| Authored data | Placeholder values are drafted in a generator script and marked as tunable: starting grant, level-up rewards and capsule drops. |
| Order | Phase 0 (this document) is reviewed before implementation starts. |

## How KF and otomo relate

otomo is a generic backend for live-service games, not a Kick Flight backend. The point of integrating them
is that each project drives the other's development:

- **otomo learns from KF.** The Private Server has a large player base on Gixarde's VM, which makes it a real
  workload for deploying and testing otomo. Every gap KF hits, such as O1-O5 in the plan, becomes generic work
  in otomo.
- **KF's admin tools set otomo's admin website's standard.** KF already runs a working operator toolkit: a
  maintenance gate, a latest-information page, typed Kicker, Disc and Text editors, and ops telemetry. otomo's
  admin website mostly falls back to a raw JSON editor. KF shows what a live game's operators actually use, and
  each such feature is rebuilt in otomo in generic form until otomo's website is ready for production. KF
  takes otomo's ledger, config releases and rollback in return.

Rules that keep the two separate:

1. **No game names in otomo.** Code, schemas, docs, issues and commit messages in the otomo repo never say Kick
   Flight, Grenge, D2C, `GoodsType` or any client field. This is already a rule of otomo's public release
   process (plan §5).
2. **Translation is the adapter's job.** Mapping KF's protocol and data onto otomo happens only in
   `src/KickFlight.BootstrapApi`. Examples: master tables to Config namespaces, `GoodsType` to otomo item
   kinds, KF error codes, and the device-id derivation.
3. **Each otomo feature is designed against more than one game.** A feature KF needs is written in otomo as the
   generic primitive, such as weighted tables, grant bundles or idempotent imports. It is checked against at
   least one other game's needs before it is built (plan §10). If something only fits KF, it stays in the
   adapter.
4. **Gaps go upstream as generic issues.** When KF hits a missing or wrong behaviour in otomo, it is filed in
   otomo's tracker in generic terms, with a reproduction that doesn't need KF. The KF-side workaround links to
   that issue and is removed when the fix ships.
5. **Separate deployments.** otomo has its own Compose project, database, release channels and versioning, and
   KF pins the otomo release it tests against. Neither stack reads the other's database.
6. **The economy interface stays generic too.** The KF-first economy (see the decisions table) uses otomo's
   vocabulary (wallet, ledger entry, idempotency key, grant bundle), so the later swap to O2 only changes the
   backend. It is also the first draft of O2's API, written from a real consumer. It still lives in KF and does
   not go into otomo until it passes rule 3.

## §9.1 Capacity: there is room on the NAS; the VM is not measured yet

The NAS (staging) is measured below. Gixarde's VM (live) is not yet: it needs the same `free`/`docker stats`
reading, taken at peak player hours, because the live KF plus otomo there (without observability, about
165 MB idle) is what has to fit.

The NAS is now at `root@10.10.20.148`. The old `192.168.68.53` no longer answers, and `scripts/deploy-nas.sh`
still defaults to it. Everything below was measured on 2026-10-20 with read-only commands.

**The NAS today** (hostname `truenas`):

| Resource | Measurement |
|---|---|
| CPU | Ryzen 3 3200G, 4 cores. Load average 0.22 / 0.13 / 0.10. CPU and memory pressure 0 |
| RAM | 13.9 GB total, no swap. `free` reports 7.7 GB used and 6.2 GB available, but 5.0 GB of the used figure is ZFS ARC (`c_max` 12.9 GB), which the kernel gives back under pressure. Real use is about 2.7 GB |
| Kick Flight containers | `kickflight-api` 121 MB, `kickflight-postgres` 33 MB, `kickflight-cdn` 8 MB. None has a memory limit |
| Other containers | `jarvis-*` (4), `tailscale`, `ddns-updater`, `wg-easy`: about 510 MB in all, each with a limit |
| Biggest host processes | an `asyncio_loop` (650 MB), `middlewared`, `netdata` |
| Disk | `Tanuki_1` has 3.48 TB free. `kickflight/pgdata` uses 19 MB of its 20 GB quota |

**otomo's footprint**, measured on the team45 VM, which runs the full stack, all of it idle:

| Group | Containers | Memory |
|---|---|---|
| Core services | auth, session, config, patch, gateway, gateway_dev, admin-auth, admin-ui, dashboard, allocator, gameplay-proxy, edge | about 110 MB, 8–12 MB each |
| Data stores | postgres 51 MB, valkey 3 MB | about 55 MB |
| Observability | alloy, loki, prometheus, cadvisor, node and postgres exporters | about 340 MB, all with limits |
| Test game servers | gs-1, gs-2 | about 160 MB |

- **Totals:** about 700 MB for everything. This deployment does not need allocator, gameplay-proxy, edge or the
  `gs-*` containers (plan §2), which leaves about 500 MB, or about 165 MB without observability.
- **Fit:** even with a staging copy of everything, that is under 1.5 GB against about 6 GB available (more with the
  ARC shrinking), and the CPU is nearly idle.
- **Caveat:** these are idle numbers. Postgres and Valkey grow with load and data, so set memory limits per
  container in the otomo Compose file. The Kick Flight containers also need limits, since none has one today.

## §9.2 Operator sign-off: open

This is for the operators. Points to sign off on:

- the hosting change above;
- the economy reset;
- the move away from §4.2.

## §9.3 Economy request and response shapes: done

**Full catalogue:** [`docs/ECONOMY_DTOS.md`](ECONOMY_DTOS.md).

- **Scope:** 31 routes and 102 types, with exact field types and the values of every nested enum.
- **Source:** the Il2CppDumper 6.7.46 output already on disk at
  `../Kick-Flight-Assets/server_revival_analysis/il2cpp/` (`dump.cs`, `script.json`, `stringliteral.json`).
- **Mapping:** every route string is confirmed in `stringliteral.json`. Routes are matched to request classes by
  name.

**Live traffic.** The request captures keep only a hash and the size of each D2C body, so no body can be decoded
afterwards. The captures show which economy routes the client actually called:

| Route | Calls |
|---|---|
| `/disc/buildup` | 36 |
| `/goodsShop/buy` | 8 |
| `/present/index` | 6 |
| `/mission/index` | 4 |
| `/capsule/immediateOpen` | 2 |
| `/gacha/discInformation` | 1 |

The draws, `/capsule/open`, `/mission/receipt`, `/shop/*` and `/item/exchange` were never captured.

**Server coverage today:**

- **Real handlers:** `/gacha/*` (5 routes), `/goodsShop/buy` and `/disc/buildup`.
- **Stubs:** capsules, missions, presents and `/shop/*`.
- **No handler at all:** `/item/exchange`. The client calls it from `HomeUIManager.ItemExchangeCoroutine`; its
  request is empty and its response is `userItemList userPresentList`.

### Errors: how the client reacts

This is what decides the server's error contract. All six spend calls behave the same way: `/gacha/discDraw`,
`/gacha/kickerDraw`, `/goodsShop/buy`, `/capsule/open`, `/capsule/immediateOpen` and `/disc/buildup`.

- **No call handles its own failure.** Each one passes `onFailed = null` (call sites `0x14B1DB8`, `0x141279C`,
  `0x163246C`/`0x17ADF24`, `0x16B0DBC`/`0x16B3540`, `0x16B0BE8`, `0x18409AC`).
- **What counts as a failure:** HTTP 200 with a nonzero `x-app-status-code` (`GetResponseHeaderStatus`
  `0x31B7B44`).
- **The generic path:** `NetworkManager.DefaultFailed` `0x31B4E70` → `OpenErrorWindow` → `GetErrorResponse` reads
  the body **as plaintext** → `OpenErrorWindowOnFailed(title, message, code)` `0x31B7F98`.
- **No dedicated "not enough currency" screen.** Any code the client does not special-case opens a generic dialog
  showing the server's title, message and code.
- **Codes with special handling:**

  | Code | Meaning or behaviour |
  |---|---|
  | 1202 | Ban |
  | 1400 | ForceUpdate |
  | 1401 | AgreementUpdate |
  | 1999 | Retried (`0x1A28060`) |
  | 2002–2004 | |
  | 2100, 2101 | Reload the scene |
  | 2204–2206 | |
  | 2400–2404 | |
  | 2601 | GachaSchedule: callback, then a scene change |
  | 2901–2903 | |

- **Economy codes in `ErrorType` that show the generic dialog with the server's text:**

  | Code | Name |
  |---|---|
  | 1001 | GachaMaintenance |
  | 2501 | GoodsMaxAmount |
  | 2502 | GoodsUnreceivable |
  | 2801 | GoodsShopSchedule |
  | 2802 | GoodsShopMax |

- **What the server does today:** `StatusError` (`DemoSessionApi.cs:1420`) sends `x-app-status-code: 1` with an
  encrypted `{}`, so the player sees an empty dialog.
- **The contract to use:** send plaintext `{"error":{"code":N,"title":"…","message":"…"}}`, as commit `4ecf061`
  already does for 2005. A missing `error` object raises an NRE at `0x31B2F28`.
- **Implication for the reset.** When a player cannot afford something, the client gets no special screen, only a
  readable error. The client normally greys out buttons the player cannot afford, so this path should be rare.
- **Not checked yet:** whether the draw button comes back after the dialog closes. Check it on the emulator during
  implementation.

## §9.4 Economy reset: decided

Everyone is reset (see the decisions table). What carries over:

- **Kept:** player id, uuid mapping, name, rank (`player_ranks`), follows and real friends, replays and ops data.
- **Reset:** discs, currencies, items, costumes, gear, exp and capsules.

This is the import of §4.2 with an empty source, under the same idempotency key, `import:v1:{player_id}`. Before
it runs anywhere real, back up with `scripts/backup-kickflight-db.sh`.

## §9.5 Real-money IAP: open

`/shop/prepare` carries `shopProductId storeRegionCode storeCurrencyCode storePrice` and `/shop/verify` carries
only `receipt`. Recommendation: the private server sells nothing, so paid Jet Coins (102) are granted only by the
server and store receipt verification stays out of scope. The operators need to confirm this.

## §9.6 `dropRate` vs `dropRatio`: done

The rate screen shows the **`dropRate` from the `/gacha/*Information` response**. It never reads a master.

- **The response fields reach the display unchanged:**
  - `DiscGachaDetailLotteryDiscRarityInfo..ctor` `0x16347D4` copies `ResponseLotteryDiscRarity.dropRate`
    (`0x1634838`).
  - The disc constructor at `0x163484C` copies `dropRate` at `0x1634A58`.
  - The kicker constructor at `0x1533FE8` copies `dropRate` at `0x1534058`.
- **The masters' own `dropRate` fields are never read.** `LotteryDiscRarityMasterData.get_DropRate` and
  `LotteryDropDiscMasterData.get_DropRate` have no callers. `dropRatio` is used only by the server.
- **Units: a percentage.** The client displays `floor(dropRate × 1000) / 100000` formatted as `{0:0.000%}`, so
  `3.5` shows as "3.500%".
- **Hidden rows:** a value ≤ 0 hides the rate on that row.
- **What the adapter sends:** `dropRate = 100 × weight / Σweights` within each table, computed from the weights it
  actually rolls.

## Facts settled for implementation

- **`BattleResultType`:** Invalid -1, Win 1, Lose 2, Draw 3. `BattleResultRequestData` has no field for the
  result itself, so the server has to derive it from `teamScoreList`/`userScoreList` or from the match.
  `/battle/result` currently treats every result as a win.
- **`CapsuleEffectType`:** Normal 1, Orange 2, Rainbow 3.
- **Capsule slots are fixed in the client.** Slots 1–3 are available to everyone and slots 4–6 need Premium
  (`CommonUtil.MAX_SLOT_COUNT_*`, inlined in `UserCapsuleListInfo.IsFull` `0x18F1E30`). A battle drop with every
  slot full has to go to presents or be discarded.
- **`DiscBuildupRequestData`:** only `discId`. The server takes the cost from `DiscBuildup`
  (`necessaryDiscForceAmount`, `totalDiscAmount` by rarity and level). Today the server reads a `level` the client
  never sends and charges a flat 1100.
- **Concurrency.** The adapter mutates a cached `SessionState` per token with no lock, and `Save` overwrites the
  whole document. A real economy needs a per-player lock, or otomo-style transactions, before any balance is
  enforced.
