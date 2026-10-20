# KF operator tooling vs otomo's admin website: gap analysis

Status: for review, 2026-10-20. This compares the Private Server's operator tools with otomo's admin website and
backend (`C:\Users\Tanuki\otomo`, branch `staging`). Its purpose is to find two kinds of features:

- what KF has that otomo lacks;
- what KF lacks itself that any live-service game would need.

Both kinds become generic otomo work, so that otomo's admin website reaches production quality with KF as its
first real workload. The separation rules are in [`OTOMO_PHASE0_FINDINGS.md`](OTOMO_PHASE0_FINDINGS.md) ("How KF
and otomo relate"). The "otomo item" column describes each item in generic terms, so it can be written up in
otomo without naming this game.

Everything here was read from code and docs; nothing was run. On the KF side: `tools/balance/` (the Balance
WebUI: `index.html`, `server.py`, `auth.py`), `MaintenanceState.cs`, `ClientUpdateState.cs`,
`LatestInformationState.cs`, `Ops/`. On the otomo side: `services/adminui`, `config`, `patch`, `dashboard`,
`gateway_dev`, `admin_auth`, `session`, and `design/`.

## 1. Where otomo is already ahead

KF should eventually adopt these and not rebuild them:

| Capability | KF today | otomo today |
|---|---|---|
| Roles | One role: every account can restart the API or turn on hard maintenance | `viewer < live_ops < admin`, enforced at the gateway and in each service |
| Staff accounts | Inserted into SQLite by hand, no 2FA | Invites, TOTP and recovery codes, reset links, session revocation, root protection |
| Audit | None (file backups and `changedAtUtc` only) | Merged feed from config, admin-auth and session, filterable |
| Config history | One timestamped backup per save, a diff against the newest backup, "revert to base" | Drafts, versions, version diffs, channels (`dev`, `staging`, `live`), release diff, rollback, promote |
| Applying changes | API restart, which drops live matches | Releases go live with no restart (KF consumes them after A3) |
| Client version gate | No UI (curl only), hard-coded message | `min_client_version` set per release, in the manifest and a header |
| Logs and metrics | None | Log search with live tail, per-service charts, firing alerts |

## 2. Gaps: what KF has and otomo needs

Ordered by how much operator work each one unblocks.

### G1. Typed table editor for arrays of objects (plan item O3)

**KF has:** typed editors for the Kicker and Disc tabs:

- Number, checkbox and select inputs per column. Enum values show as `"7 - Burn"`, and an unknown value is kept on
  save, never rewritten.
- Id and reference columns as read-only chips.
- Server-side writes that keep ids, keys and types, and coerce `100.0` to `100` in integer columns.
- Rows can be added (with neutral preset defaults) and deleted, but only in tables that allow it.
- Dirty-field highlighting, an "Unsaved changes: N" count, revert, and Ctrl+S.
- Search and filter, an icon per row, and help text on each field in two languages.
- **Entity panels**: one kicker or disc page that joins rows from several tables by key (`Disc` → `Skill` → effect
  tables).

**otomo has:** typed controls for scalars and arrays of scalars only. Arrays of objects, nested objects and
unions fall back to a raw JSON editor, and `DataTable.vue` is read-only.

**otomo item:**

- A schema-driven grid editor for arrays of objects:
  - a typed cell per column;
  - the `enum` and `enumNames` labels;
  - an `x-otomo-unknown: preserve` policy (keep values the schema doesn't know).
- `ui.presentation` annotations:
  - row label, row icon (a pack or blob reference) and searchable columns;
  - read-only columns;
  - row add/delete policy and row templates.
- **Cross-namespace references** (`x-otomo-ref: {namespace, key}`), shown as links and checked on validation.
- **Entity views**: a presentation-defined page that gathers rows from several namespaces through those
  references. This is the generic form of KF's kicker and disc panels.
- Localized field help from the schema's `description`.

### G2. Maintenance mode (plan item O4)

**KF has:**

- **Modes:** Off, Warning (one notice on home) and Hard (every non-exempt request gets the client's maintenance
  error, and matchmaking stops).
- **Text:** custom title and message. The notice id changes only when the text changes, so players don't see
  the same warning twice.
- **Exemptions:** boot and the results of matches already running are let through.
- **Bypass:** test builds bypass maintenance by a version suffix.
- **Runtime:** applies live and survives a restart.

**otomo has:** nothing.

**otomo item:**

- **Gateway enforcement:**
  - three levels;
  - a list of exempt routes (for example match results);
  - a bypass for staff tokens and for a client-version pattern;
  - the game's own error contract, using a configurable status and body.
- **Admin page:** set the level, title and message.
- **Things KF lacks, to add at the same time:**
  - a scheduled start and end;
  - a countdown the warning level can show;
  - an audit entry for each change;
  - per-platform or per-version targeting.

### G3. News and announcements

**KF has:** the Latest-information editor, with three modes:

- formatted text (headings, bullets, bold, links; raw HTML escaped);
- an uploaded HTML page, previewed in a sandboxed iframe and served to the game's WebView under a sandbox;
- an external URL, served as a 302.

Changes are live and served with `no-store`.

**otomo has:** a seeded `ui.motd` config namespace, and no editor for it.

**otomo item:**

- **Content:** an announcements service with several posts, each having one of the three modes, a priority, and
  a start and end time.
- **Languages:** per-locale bodies.
- **Admin page:** a sandboxed preview and draft or publish.
- **Delivery:** a player-gateway endpoint, plus an HTML endpoint for game WebViews.
- **Things KF lacks, to add at the same time:** scheduling, several posts, and languages.

### G4. Crash and exception aggregation

**KF has:**

- **Grouping:** client exceptions grouped by fingerprint (exception type plus the first three frames, with
  offsets stripped), showing count, users affected, app versions and last seen.
- **Windows:** "this release" (`KF_RELEASE_SHA`), last 24 hours and last 7 days.
- **Problem sessions:** a list of match outcomes and player statuses, with an event-timeline drill-down for each
  session.
- **Writer health:** the telemetry writer's own counters (queue, dropped, errors).

**otomo has:** logs and metrics only. Its dashboard README declares player-level analytics out of scope.

**otomo item:**

- **Ingestion:** a client-report endpoint for exceptions and anomalies, with fingerprinting rules configurable
  per game.
- **Grouping by release:** reports are tied to otomo release ids.
- **Admin pages:** an issues list and an issue detail page (stack, versions, affected players, first and last
  seen).
- **Timelines (generic):** a session or match event timeline, recorded by game servers through O1.
- **Things KF lacks, to add at the same time:** trend charts, alert rules on a new or regressed fingerprint, and
  export.

### G5. Localization editor

**KF has:** the Text tab:

- the translation table grouped by UI screen;
- a **wireframe** that places each text box at its real position and font size on the screen (from
  `docs/localize_layout.json`), with live typing and highlighting for edited and missing text;
- a "keys not on any screen" bucket.

**otomo has:** nothing. Strings would just be an ordinary namespace.

**otomo item:**

- **Data:** a string-table namespace convention (`key → {locale → text}`).
- **Editor:** grouping, side-by-side locale columns, missing and stale markers, length limits, placeholder checks
  (`{0}`) and search.
- **Optional layout manifest:** a game uploads a file of screens, each with boxes of key, x, y, w, h and font
  size. The editor draws the wireframe from it, which keeps the feature engine-agnostic: KF's file comes from
  Unity prefabs, and another game could generate its own.
- **Things KF lacks, to add at the same time:** several locales, and creating missing keys in the UI.

### G6. Player diagnostic and bug-report uploads

**KF has:**

- player and tester uploads (logcat, a native exit reporter), limited to 2 GiB, kept 14 days, written with
  restricted file permissions;
- **no viewer**: files are read on the server's disk.

**otomo has:** nothing.

**otomo item:** a player report or attachment intake tied to the player id and release, stored as blobs with
retention. The admin page lists them, can download them and links them to issues (G4).

## 3. Gaps neither side covers: what a production live game needs

| ID | Need | Notes |
|---|---|---|
| G7 | **Player lookup and moderation** | otomo has a player search backend with no page. KF has nothing. Needed: search by id or name, a profile page, suspend or ban (with reason and expiry; the gateway enforces it), revoke sessions, rename, view and grant inventory (once the economy, O2, exists). Every action audited. |
| G8 | **Scheduled publishing** | Release at a set time, and expire at a set time (events, banners, maintenance windows). Neither side has any scheduling. |
| G9 | **Feature flags with a UI** | otomo seeds a `features.flags` namespace. KF's gym mode toggle is an **unauthenticated public GET that is lost on restart**: it should become a flag. Targeting by percentage, version or player list would be generic. |
| G10 | **Config as code** | KF's "export overrides as a zip, then commit" step shows the need: namespaces exported to and imported from a repo, so seeds and live state stay in agreement (this also feeds plan item A4). |
| G11 | **Alert delivery** | otomo shows firing alerts but sends them nowhere. Live games need Discord, email or webhook routing, and silences. |
| G12 | **Service accounts** (plan item O1) | Needed by the CLI on otomo's roadmap, by game servers reporting events (G4 timelines) and by the economy. |

## 4. KF features that stay in KF

- **Replays:** their storage and featured rotation are game-specific.
- **Battle and Photon semantics:** the outcome rules and match statuses in `OpsStatusRules.cs` are game-specific.
  The timeline mechanism goes to otomo (G4); KF's rules for what counts as abandoned or dropped stay in KF.
- **The kicker and disc panel layouts, and the effect-table presets:** these become KF's own `ui.presentation`
  documents inside otomo. That is data, not otomo code.
- **The DIAG APK routes and the D2C protocol details.**

## 5. Suggested order for otomo's admin website

1. **G1, the grid editor with references and entity views.** It does the most daily good and lets the Balance
   WebUI be retired. It also needs plan items A3/A4 (masters published as Config namespaces), which give it real
   data to test against.
2. **G2, maintenance mode**, with scheduling.
3. **G3, announcements.**
4. **G7, player lookup and moderation.** The backend search already exists.
5. **G4 and G6, issues and reports.**
6. **G5, localization.**
7. **G8-G11.**

For each item, KF adopts the otomo feature on staging (the NAS) as soon as it lands, then on live (Gixarde's VM),
and retires its own version: the Balance WebUI tabs, `MaintenanceState`, `LatestInformationState` and the Health
tab. Problems found with KF's real players come back to otomo as generic issues.
