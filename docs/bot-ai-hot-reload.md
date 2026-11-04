# Bot AI Hot-Reload System & Operations Guide

## Overview
The bot AI hot-reload system exposes native bot AI behavioral parameters (motivation weights, deposit thresholds, dash/skill intervals, lock-on) via the otomo config namespace `kf.bot_ai_profiles` (see section 2; validated by `BotAiConfigurationValidator`, mirrored by `config/bot_ai_profiles.schema.json`). It replaces hardcoded policy dictionaries in `KickFlight.BootstrapApi` with zero-downtime, thread-safe dynamic configuration without requiring APK rebuilds.

---

## 1. Architecture & 39-Profile Contract

- **Canonical Formula:**
  $$\text{ParameterId} = 15 + (13 \times \text{modeIndex}) + \text{profileIndex}$$
  - `modeIndex`: `0` (CrystalScramble), `1` (FlagFlight), `2` (RapidBall)
  - `profileIndex`: `0..12` covering base archetypes (`carrier`, `tank_defender`, `hunter`, `objective_attacker`, `healer`, `speed_support`, `damage_support`, `trap_defender`) and kicker specializations (`Yuyan`, `Owlbert`, `Ruriha`, `Grenhawk`, `Sid`).
  - **ID Range:** Strictly bounded to `[15..53]`.
  - **Gym Isolation:** IDs $\ge 100$ (`GymAiParameterBase + kickerId`) are strictly isolated for gym mannequins (`AIOption = 0x3F`). Experimental profiles never exceed 53.
  - **Feature Flag Off:** When `KF_EXPERIMENTAL_AI_ROLES` is unset or `0`, matchmaking assigns pristine `bot.KickerId` (IDs 1..14).

---

## 2. Where the tuning lives: otomo

Bot AI tuning is the otomo config namespace **`kf.bot_ai_profiles`**. Operators edit it in otomo's admin UI and publish a
release; the adapter follows the release, the same way it follows `session.rank` and the `kf.masters.*` tables. The
tenant's copy of the document and its schema are `kf/tenant/documents/` and `kf/tenant/documents-schemas/` in the
`kf-otomo` fork (publish with the tenant publisher to the `staging` channel). The document's shape is exactly the
old file's: `{"$schema", "version", "profiles": [...]}`.

- **Adapter setting:** the namespace must be listed in `Otomo:Masters:Documents` (`appsettings.json` has it). A server
  that sets `Otomo__*` by environment only (kf-staging, content root `/srv/repo`, no appsettings) adds
  `Otomo__Masters__Documents__1: kf.bot_ai_profiles` (index 1 after `session.rank` at 0). If the release does not
  carry the namespace, `OtomoMasters` refuses to start, as it does for `session.rank`.
- **Reload:** `BotAiProfileProvider` subscribes to `OtomoMasters.Updated`. A release whose document differs from the
  last one seen is validated and swapped in atomically; a release that did not change it does nothing.
- **Validation** (`BotAiConfigurationValidator`, unchanged): strict fields, 3 modes by 13 archetypes or 14 kickers,
  `kickerId` matching the profile, rank 13, motivations 0..100, `scoreToDeposit` >= 1, low HP threshold 0..1, intervals
  finite and positive. A document that fails keeps the **last good profiles** and logs **Error**. If there is no good
  document at startup, the provider serves the hardcoded `BotAiDefaults` and logs Error.
- **Local runs only:** with `Otomo:Masters` disabled there is no release, so the provider reads `config/bot_ai_profiles.json`
  (or `KF_BOT_AI_PROFILES_PATH`) once at startup, and `Reload()` re-reads it. There is no file watcher, no durable
  override directory and no seed hierarchy; the file and `config/bot_ai_profiles.schema.json` stay in the repository only as
  that fallback and as the schema the validator mirrors.
- **Removed:** the `FileSystemWatcher`, `.local/masters-overrides/bot_ai_profiles.json`, `KF_BOT_AI_SEED_PATH`, and the balance
  tool's `/api/bot-ai/profiles` routes. The tool (`tools/balance/`) still edits the master tables; it no longer touches bot AI.

---

## 3. Fault tolerance

- **Reload invalid:** the last active snapshot keeps serving; the validation errors are logged at Error.
- **Release without the namespace:** `OtomoMasters` rejects the whole bundle and the server keeps what it had.
- **Atomic swap:** a valid document is compiled into an immutable `BotAiProfileSnapshot` (`FrozenDictionary`) and swapped by reference.

---

## 4. Master Generation & HTTP Coherence

- **Snapshot Isolation:** Exactly one snapshot is captured per master generation run to ensure internal table consistency.
- **Dynamic Master Versioning:** On reload, `DemoSessionApi` regenerates `KickerAiParameter`, re-encrypts the master blob, and atomically updates `MasterVersion` with the new SHA-256 hash.
- **HTTP Coherence:**
  - `POST /download/master` returns the updated SHA-256 hash and size for `KickerAiParameter`.
  - `x-app-master-hash` header on all HTTP responses reflects the new `MasterVersion`.
  - `GET /demo-master/KickerAiParameter` delivers the exact matching encrypted bytes with `Cache-Control: no-cache, no-store, must-revalidate` and `ETag: "{hash}"`.

---

## 5. Known Limitations & Constraints

1. **`lowHpDepositThreshold` Native Cave Restriction:**
   - Validated and stored in server profile records (`[0.0..1.0]`, default `0.35`).
   - **NOT effective for native client tuning:** The native client deposit cave (`0x159D854..0x159D9F0`) implements a fixed 35% HP comparison in ARM64 machine code, and `masters_kicker_ai_parameter.json` has no schema column consumed by the client for this field.
2. **Interval Network Responsiveness:**
   - Dash and skill intervals are transmitted in `masters_kicker_ai_parameter.json` and consumed by native AI decision loops, but their exact responsiveness across multiplayer lockstep frames over UDP remains unverified.
