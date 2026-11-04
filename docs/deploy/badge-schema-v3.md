> **Superseded.** Migration 9 drops these four tables: badges are economy facts, tracks and `honor:*` items plus a session
> display slot (`docs/OTOMO_BADGES.md`). Kept as the record of how the tables were once deployed.

# Deploying PostgreSQL player-store schema v3 (badge tables)

Applies to the badge restoration change: `PostgresPlayerStore.SchemaVersion` goes 2 -> 3 and migration 3 creates
`player_badge_ledger`, `player_badge_counters`, `player_badge_streaks`, `player_honors` (all `create ... if not exists`,
no existing table or column touched, no SQL backfill).

## Why a normal deploy order matters

- `PostgresPlayerStore.Migrate()` takes `pg_advisory_lock(48095837)`, applies each missing version in one transaction
  together with its `schema_meta` row, then requires `max(version) == SchemaVersion` (exact equality, `PostgresPlayerStore.cs` ~:206-213).
- An OLD binary (SchemaVersion 2) started against a DB whose `schema_meta` max is 3 throws at construction and never
  serves. Old and new binaries therefore cannot run side by side: no rolling/blue-green deploy.
- The migration runs in the store constructor, i.e. before `/health/ready` can report `ready`. The migration is applied once;
  concurrent starters serialize on the advisory lock and skip an already-recorded version.

## Pipeline findings (inspected, nothing modified)

- `.github/workflows/deploy-vps-main.yml`: push to `main` runs `scripts/deploy-vps-ci.sh` over SSH; concurrency group
  `kick-flight-vps-main`, plus `flock` on the host. `src/` changes set only the `api` component (the R52 API-only allowlist does
  not include `PlayerStore/*`, so this takes the standard API path).
- `deploy/docker-compose.vps.yml`: exactly ONE `api` container (no replicas). The script builds, then `docker compose up -d --no-deps api`,
  which recreates the container (old stopped, new started). So the "stop all old instances, then start the new binary" order
  already holds automatically; there is no blue-green or canary to disable.
- The script polls `/health/ready` (60 x 2 s) and fails the deploy if it never becomes ready.
- GAP 1: the pipeline takes NO database backup. The only dump is the nightly `kickflight-db-backup.timer` (02:15 UTC, on the same VPS).
- GAP 2: on failure after sync, `rollback_on_error` (`deploy-vps-ci.sh` ~:383-384) rebuilds the previous source with `up -d --build || true`
  and does not touch the DB. If migration 3 already committed, that old binary crash-loops (`restart: unless-stopped`) on the version gate.
  A failed deploy after the migration therefore needs the manual rollback below (or roll FORWARD with a fixed commit, the preferred path).
- Required pipeline change: none is strictly required, because the deploy is already single-instance stop-then-start. The backup is a MANUAL step
  to do BEFORE merging the PR to `main` (merge triggers the deploy). Optional hardening (not done here): run
  `scripts/backup-kickflight-db.sh` at the start of the remote deploy when `src/KickFlight.BootstrapApi/PlayerStore/` changed.

## Procedure

1. Before merging to `main` (the merge deploys): on the VPS run `scripts/backup-kickflight-db.sh` (custom-format `pg_dump`,
   verified with `pg_restore --list`, mode 0600). Note the dump path it prints; copy it off-host if possible.
2. Merge the reviewed PR. The pipeline recreates the single `api` container: the old process stops, the new binary starts,
   applies migration v3 once under the advisory lock, and only then reports ready. If you ever run extra API instances
   (manual, NAS compose, a second host), stop ALL of them first.
3. Post-deploy checks (use the postgres service; do not print `.env` contents):
   - `select max(version) from schema_meta;` -> `3` (and a row `version = 3` exists).
   - `select to_regclass('player_badge_ledger'), to_regclass('player_badge_counters'), to_regclass('player_badge_streaks'), to_regclass('player_honors');` -> all non-null.
   - `/health/ready` reports `ready`.
   - Smoke `/battle/result` with a real (or test-account) client battle: response keeps its fields, `userBattleRank` moves +50/-20 only on a decided win/loss,
     a retry returns the same snapshot.
   - Honors intact: spot-check a known account's honor list (startup `userHonorList`) and `select count(*) from player_honors;` is >= 0 and grows only on real awards; the default honor `6010000` is always present.

## Rollback (code back to the schema-2 binary)

Verified against `Migrate()` and the merge code:

1. Stop ALL new-binary API instances.
2. `delete from schema_meta where version = 3;` -> `max(version)` is 2, so the schema-2 binary passes its gate. The v3 tables are left in place:
   the old binary does not know them, so they are inert, and keeping them preserves every badge fact earned while v3 was live.
3. Deploy the old commit/binary.
4. Roll forward again later: the new binary sees no row 3, re-runs the idempotent `create ... if not exists`, re-records version 3 and, on each
   write, merges the `players.state` jsonb mirror with the tables (counters by greatest, ledger/honors insert-if-absent, streaks by battles/best).
   The tables remain authoritative for everything earned under v3.

Caveats of the rollback (accept before using it):
- While the old binary runs, badge progress it cannot see is not recorded; it rewrites `players.state` whole (last writer wins) and may drop the
  mirrored badge keys from the jsonb. The tables still hold them, so nothing is lost after roll-forward, but honors earned under the old binary
  only live in whatever shape the old binary stored (they are folded in by union on the next new-binary write).
- Rank changes made under v3 (outcome-based) persist; the old binary's rank logic is unchanged by this schema.
- Full restore (`pg_restore` of the pre-deploy dump) is only for data corruption; it discards all progress since the backup.
