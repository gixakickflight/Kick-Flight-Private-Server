# Masters come from otomo; the adapter follows a release

Status: implemented on `feat/otomo-masters` (config/masters port). One master table is served from otomo
(`Translation`); the other 61 files are still read from `config/`, and moving one more is a two-line change
(below). The mechanism is in place for all of them.

## Why this exists

Master tables used to be files: `config/masters_*.json`, read once in `DemoSessionApi.InitializeMasters`, with
`.local/masters-overrides/*.json` substituted over them. Changing one meant editing a file in the repository and
restarting the server. The port makes a table **data an operator edits in otomo's admin UI and publishes**, and the
running adapter follows the publish: no rebuild, no restart, no file on the host to keep in step.

The client is unchanged. It still calls `POST /download/master` for the list (`{name, hash, url, size}` per table,
encrypted with the in-client common code) and `GET /demo-master/{Name}` for a table (AES-256-CBC, key = ASCII of
`CommonCode`, zero IV). It still decides whether to re-download by comparing `x-app-master-hash`. Only where the
bytes come from changed.

## The shapes

**One namespace per table.** `config/masters_translation.json` is namespace `kf.masters.translation`, whose
document is

```json
{"rows": [ /* exactly the array the file holds */ ]}
```

`kf` is this game's tenant slug; `audience` is `server` because a client never reads a namespace directly — it reads
the adapter's endpoints, as before. The list of namespaces this repository owns is `masters/otomo/namespaces.json`,
and the JSON Schema for each one is `masters/otomo/schema/kf.masters.<table>.json`. **The schema lives in this
repository, not in otomo**: otomo stores it and enforces it when a version is cut, and knows nothing about Kicker,
discs or translation keys.

| KF master file | otomo namespace | schema |
| --- | --- | --- |
| `config/masters_translation.json` | `kf.masters.translation` | `masters/otomo/schema/kf.masters.translation.json` |
| `config/masters_weapon_attack.json` | `kf.masters.weapon_attack` | `masters/otomo/schema/kf.masters.weapon_attack.json` |
| `config/masters_disc_buildup.json` | `kf.masters.disc_buildup` | `masters/otomo/schema/kf.masters.disc_buildup.json` |

The rule is the same for all 62: the file stem after `masters_` is the namespace's last segment and the schema's
file name. `masters/otomo/namespaces.json` carries the full list with each table's row count.

**The mapping is configuration, not a constant.** `Otomo:Masters:Tables` is an array of `<master>=<namespace>`
entries, where `<master>` is the name `DemoSessionApi` files the table under (`Translation`, `KickerAbility`,
`BattleRule`, …) — the same name that appears in `/download/master` and after `/demo-master/`.

```yaml
Otomo:
  Masters:
    Enabled: "true"
    BaseUrl: http://10.169.251.183:18093     # otomo Config's internal listener (:8081), not the gateway
    Channel: staging
    KeyFile: /run/secrets/config_gameserver.key
    Refresh: "00:00:30"
    CacheDir: /srv/repo/data/otomo-masters   # must be writable: the last good bundle lives here
    Tables:
      - Translation=kf.masters.translation
```

## How a publish reaches a running server

```
   otomo admin UI / sync_masters.py
        │ PATCH draft → POST versions → POST /channels/staging/releases
        ▼
   otomo Config ──── GET /internal/config/channels/staging/bundle (Bearer gameserver key,
        │              If-None-Match; answers 304 when nothing moved, else
        │              Digest: sha-256=… , X-Otomo-Channel, X-Otomo-Release, and a
        │              deterministic tar.gz of manifest.json + config/<namespace>.json)
        ▼
   OtomoMasters (adapter)      verify Digest → untar in memory → keep bundle.tar.gz on disk
        │  Updated event
        ▼
   DemoSessionApi.ApplyOtomoTables   re-encrypt the named tables, recompute MasterVersion, under one lock
        │
        ▼  next client launch: /download/master advertises the new hash and /demo-master serves the new bytes
```

* `OtomoMasters.LoadInitialAsync` runs at boot, **before** `DemoSessionApi` is constructed, so the tables it serves
  are the release's from the first request.
* `OtomoMastersRefresher` polls every `Refresh` (30 s). A poll that changes nothing is one conditional request
  answered `304`. A publish therefore becomes visible within one interval and never requires a restart.
* `MasterVersion` is `demo-master-` + the first 16 hex digits of a SHA-256 over every served table's name and
  encrypted bytes, sorted by name — so it changes when, and only when, some table's content does.
* Table bytes are replaced under `_mastersLock` and read under it (`GetEncryptedMaster`, `SnapshotMasters`). A
  request sees one release's tables, never half of one.

## The fallback rule

The rule is deliberately narrow, and it is the whole point of the port: **a table named in `Tables` comes from
otomo and from nowhere else.**

| Situation | Behaviour |
| --- | --- |
| The release does not carry a namespace that `Tables` names | Startup fails (`LoadInitialAsync`); at refresh, the poll is rejected and the tables already serving stay. `MasterJson` throws rather than reading the file |
| otomo is unreachable at boot, the bundle cache on disk is readable | Serve the cached bundle (`Source` = `cache/<channel>/<release>`) and log a warning. Yesterday's balance beats a server that will not start |
| otomo is unreachable at boot and there is no cache | Startup fails. A server told to serve masters it does not have must not answer `/download/master` with something else |
| A poll fails, times out, or answers a non-200 | Warning, and nothing changes. Staleness is recoverable; a crash loop is not |
| A table is **not** in `Tables` | Read from `config/masters_*.json` exactly as before, fallback literal and all |

There is no "otomo then the file" chain for a ported table. Two copies that can drift are the failure this port
exists to remove.

## Moving one more table to otomo

Two edits, both one line:

1. Add `WeaponAttack=kf.masters.weapon_attack` to `Otomo:Masters:Tables`. The namespace already exists — the sync
   script seeded all 62.
2. Change that table's load site in `DemoSessionApi.InitializeMasters` from `LoadJson(...)` to
   `MasterJson("WeaponAttack", contentRoot, "config/masters_weapon_attack.json", "[]")`. The load site lives in
   whichever partial owns the table — `DemoSessionApi.cs` for most, `DemoSessionApi.Gacha.cs` for the gacha and
   lottery tables.

Then delete the file read if nothing else used it, restart once (the binding is read at boot), and from then on an
operator's publish reaches it live. Step 2 is not optional: `LoadInitialAsync` runs before `DemoSessionApi` exists,
so the boot value comes from `MasterJson`, and a table with a binding but a `LoadJson` load site would still be
seeded from the file at startup.

A table whose namespace is missing from the release fails loudly at startup rather than falling back, which is how
you find out that a namespace was never published.

## The operator workflow

```bash
# Once, and again whenever config/masters_*.json changes:
python3 scripts/otomo/sync_masters.py schema                       # regenerate masters/otomo/schema/*.json, commit
python3 scripts/otomo/sync_masters.py push --publish --channel staging --message "seed 62 master tables"

# What an operator does instead of editing a file: edit the row in otomo's admin UI, or
# PATCH /api/admin/config/namespaces/kf.masters.<table>/draft, then cut a version and publish.
```

`push` is idempotent in both directions: a table whose published version already matches the file produces no new
version, and a namespace whose draft an operator has changed is left alone — a draft is someone's work in progress,
and publishing reads versions, never drafts.

## Verified on kf-staging

One row of `kf.masters.translation` edited through otomo's own API (`common.ok`: `Accept` → `Accept (otomo)`),
version cut, published to `staging`, and observed on the running adapter with no restart (container start time and
`RestartCount` unchanged throughout):

| Step | Release | `x-app-master-hash` | `/demo-master/Translation` | `common.ok` |
| --- | --- | --- | --- | --- |
| before | `staging/4` | `demo-master-dedfdecf51b0c1be` | 67,664 bytes | `Accept` |
| after publish | `staging/5` | `demo-master-ed3fbe2ee090a79d` | 67,680 bytes | `Accept (otomo)` |
| after revert | `staging/6` | `demo-master-dedfdecf51b0c1be` | 67,664 bytes (byte-identical) | `Accept` |

Exactly one row differed between before and after (row 5, `common.ok`), and the revert restored the table to the
same bytes and the same master version it started with.

## What is still file-backed

* 62 tables have a file and a namespace; **1** (`Translation`) is served from otomo.
* The other **61** files are read from `config/masters_*.json` at boot, unchanged, including the override directory.
* 22 of the 84 tables the adapter serves have no file and no namespace at all — they are built in code
  (`Field`, `RankerMatchBattleSchedule`, `BattleRuleField`, …) and are not candidates for this port.
