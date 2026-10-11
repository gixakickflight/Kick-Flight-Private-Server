# Ops telemetry (server runs, battles, participants, client events)

The ops telemetry is the durable record behind the balance WebUI **Health** tab and the
`/admin/ops/*` endpoints. It answers "how did yesterday's matches end, which app builds and devices
played them, and which client exceptions kept repeating". It is deliberately small: this server sees
hundreds of matches a day, not millions, so the reports are computed in C# over the rows of a window
(the same code for PostgreSQL and the NDJSON fallback) instead of in SQL.

Implementation: `src/KickFlight.BootstrapApi/Ops/`

| file | role |
|---|---|
| `OpsTelemetryService.cs` | singleton `BackgroundService`: `Record*` calls from request handlers, the matchmaking room events, a bounded channel + batch writer, and the 60 s status sweep |
| `OpsStatusRules.cs` | pure participant-status / match-outcome functions shared by the handlers and the sweep |
| `OpsStores.cs` | `PostgresOpsStore` (deployment) and `NdjsonOpsStore` (local/dev fallback) |
| `OpsReports.cs` | the dashboard numbers and the window resolution |
| `OpsFingerprint.cs` | client error-report classification and fingerprinting |
| `OpsAdmin.cs` | the loopback-only `/admin/ops/*` endpoints |
| `DemoSessionApi.Ops.cs` | `/battle/disconnect`, `/battle/timeout`, `/battle/leaving` handlers |

## Where the rows go

`PlayerStore:ConnectionString` / `PlayerStore:DatabaseUrl` (or `ConnectionStrings:KickFlight`) decides
the mode, exactly like the player store:

* connection string configured → **PostgreSQL**, the same database as the player store but its own
  `ops_schema_meta` schema-version table (so rolling the API back past this feature cannot be blocked
  by the player store's migration counter);
* no connection string → **NDJSON** under `Ops:Directory` (default `<content root>/data/ops`), one
  `ops-YYYYMMDD.ndjson` file per UTC day with `{"t": run|battle|participant|event, "r": row}` lines.
  Battles and participants are appended again on every change and the last line per key wins on read.

Configuration keys (all optional):

| key | default | meaning |
|---|---|---|
| `Ops:Enabled` | `true` | disables every hook and the writer; no rows are written |
| `Ops:Directory` | `<content root>/data/ops` | NDJSON fallback directory (relative paths resolve against the content root) |
| `Ops:ReleaseSha` | `KF_RELEASE_SHA` env | release tag recorded in `server_runs`; `unknown` when unset |
| `Ops:QueueCapacity` | `10000` | channel capacity; a full queue drops (and counts) snapshots instead of slowing a request |

A database that is down never fails the API start: `PostgresOpsStore` only connects from
`InitializeAsync`, which the background service retries with backoff while the bounded queue absorbs
events. The player store is what intentionally fails the start on an unreachable database.

## Tables

`server_runs` — one row per API start.

| column | type | notes |
|---|---|---|
| `id` | text PK | `run-<utc timestamp>-<random>`, the API's `RunId` |
| `started_at` | timestamptz | process start (UTC) |
| `release_sha` | text | `KF_RELEASE_SHA` set by the deploy workflow (`unknown` locally) |
| `master_version` | text | the master hash the process loaded |
| `min_client_version` | text | the forced-update minimum at start |

`battles` — one row per match, updated as it evolves.

| column | type | notes |
|---|---|---|
| `battle_id` | text PK | matchmaking battle id, or `custom-<id>` for a custom battle |
| `run_id` | text | the run that first created the row |
| `created_at`, `updated_at` | timestamptz | first seen / last snapshot |
| `rule_id`, `match_type`, `field_id` | integer | `match_type` is nullable |
| `humans`, `bots` | integer | final roster sizes (bots filled once the window closed) |
| `custom` | boolean | a `/customBattle/start` record |
| `first_start_at` | timestamptz | first `/battle/start` of a human |
| `battle_time_seconds` | integer | from the BattleRule master (default 300), used by the silent-drop deadline |
| `outcome` | text NULL | `clean` / `partial` / `abandoned` / `never_started`, or NULL while open |

`battle_participants` — one row per human per match, PK `(battle_id, user_id)`.

| column | type | notes |
|---|---|---|
| `battle_entry_id` | text | the client's battle entry / ticket entry id |
| `created_at`, `updated_at` | timestamptz | entry / last snapshot |
| `kicker_id`, `costume_id` | integer | current kicker (row ids) |
| `team` | integer | 0 Blue, 1 Red, `-1` until the roster is final |
| `is_diag` | boolean | DIAG client build |
| `app_version`, `device_name`, `os_version`, `ip` | text | `x-app-*` headers / client address, each capped at 128 chars |
| `start_at`, `end_at`, `result_at` | timestamptz | `/battle/start`, `/battle/end`, `/battle/result` |
| `reconnect_begins` | integer | `GameReconnectBegin` analysis entries |
| `reconnect_failed_cause`, `disconnect_cause` | text NULL | cleaned to one bounded line |
| `exception_count` | integer | fingerprinted `client_exception` reports |
| `status` | text | derived, see below |

`client_events` — the append-only event log: one row per `/analysis` entry plus the lifecycle events
(`entry`, `room_join`, `room_drop`/`room_leave`, `assigned`, `start`, `end`, `result`, `cancel`,
`battle_disconnect`, `battle_timeout`, `battle_leaving`, ...).

| column | type | notes |
|---|---|---|
| `id` | bigint identity PK | ordering within the same timestamp |
| `at` | timestamptz | event time (UTC) |
| `run_id` | text | the run that recorded it |
| `user_id`, `battle_id` | text NULL | battle id is NULL for `/analysis` entries that predate any room |
| `kind` | text | analysis key, or `client_exception` / `client_anomaly` for fingerprinted reports |
| `app_version`, `device`, `os` | text | device headers |
| `fingerprint` | text NULL | 16 hex chars for `client_exception` / `client_anomaly` |
| `payload` | jsonb | `{key, value}` plus `ip`/`platform`/`user_agent`, the `context` (rule, field, kicker, roster) and `signature` for reports |

Retention: the writer prunes rows older than 30 days once per day (`OpsTelemetryService.Retention`).

## Statuses and outcomes

The participant `status` and the battle `outcome` are pure functions of the recorded facts
(`OpsStatusRules`), so the 60 s sweep and every event handler agree regardless of arrival order.

Participant statuses — open: `matching` (waiting for the window), `assigned` (roster sent, no
`/battle/start` yet), `playing`; final: `completed` / `reconnected_completed`, `reconnect_failed:<cause>`,
`disconnected:<cause>`, `left`, `dropped_silent` (and `dropped_silent+relaunched` when the player
re-authenticated within 5 minutes of its last sign of life), `matching_timeout`, `cancelled`,
`matching_failed:<cause>`, `requeued` (moved to the next room, not a failure).

Match outcomes: `clean` (every assigned human completed), `partial`, `abandoned`, `never_started`
(no human ever reached `/battle/start`, or the room was dropped before its window closed).

## Endpoints

All three are loopback-only, like the other `/admin/*` routes (`MaintenanceAdmin.IsLocalAdmin`):
a request carrying `X-Real-IP` / `X-Forwarded-For`, or arriving from a non-loopback address, gets a
plain 404. The VPS never publishes the API's HTTP port, so the balance tool runs them with
`docker exec deploy-api-1 curl ...`.

| route | result |
|---|---|
| `GET /admin/ops/summary?window=deploy\|restart\|24h\|7d` | totals (match outcomes and player statuses), status/breakdown dictionaries, `problemMatches` and the writer stats |
| `GET /admin/ops/exceptions?window=...` | `client_exception` / `client_anomaly` reports grouped by fingerprint |
| `GET /admin/ops/battle/{battleId}` | one battle's row, its participants and the event timeline (404 for an unknown id) |

Windows: `deploy` (default) starts at the first server run with the current `KF_RELEASE_SHA`, so
restarts of the same release keep counting; `restart` starts at this process; `24h` / `7d` are
rolling. An unknown name is a 400 with the accepted list. Each endpoint briefly waits for the writer
queue to drain (2 s) so a row recorded just before the call is in the answer.

## Client error reports

A patched client ships exceptions and anomalies through `/analysis` as the
`PhotonGetResionPingFailed` key with a `cause` starting `"[Analysis]"`. A report with a stack trace
becomes `client_exception`, anything else `client_anomaly`. The fingerprint is the SHA-256 of the
exception type (or the first message line with digits masked) plus the first three stack frames with
IL offsets / addresses / line numbers removed, truncated to 16 hex chars, so reports of the same
failure group together even across app builds.
