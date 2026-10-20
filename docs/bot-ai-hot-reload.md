# Bot AI Hot-Reload System & Operations Guide

## Overview
The bot AI hot-reload system exposes native bot AI behavioral parameters (motivation weights, deposit thresholds, dash/skill intervals, lock-on) via a server-managed JSON configuration file (`config/bot_ai_profiles.json`) validated against `config/bot_ai_profiles.schema.json`. It replaces hardcoded policy dictionaries in `KickFlight.BootstrapApi` with zero-downtime, thread-safe dynamic configuration without requiring APK rebuilds.

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

## 2. Configuration & Validation

- **Config Path:** `config/bot_ai_profiles.json` (resolves relative to repo root, `AppContext.BaseDirectory`, content root, or `KF_BOT_AI_PROFILES_PATH`).
- **Validation Rules (`BotAiConfigurationValidator`):**
  - **Strict Schema:** Unknown/unmapped fields at root, profile, motivations, or interval objects are rejected.
  - **3x13 Coverage:** All 3 modes and 13 profile keys must be present (exactly 39 entries).
  - **Identity:** `kickerId` must strictly match the assigned profile kicker (e.g. carrier = 8, Owlbert = 5, Yuyan = 10).
  - **Rank:** Must equal `13`.
  - **Motivations:** Clamped to `[0..100]`.
  - **Score to Deposit:** Integer $\ge 1$.
  - **Low HP Threshold:** Finite float $\in [0.0..1.0]$.
  - **Intervals:** Finite positive floats ($> 0.0$).
  - **Heuristic Fields:** Extra heuristic fields present in existing masters (`negativeCorrection`, `positiveCorrection`, `minMeasureAccuracy`, `maxMeasureAccuracy`, `changeAttributeInterval`) are automatically preserved from base row templates during master generation.

---

## 3. Hot-Reload & Fault Tolerance

- **Debounced Watcher:** A `FileSystemWatcher` monitors `config/bot_ai_profiles.json` for `Changed`, `Created`, `Renamed`, and `Deleted` events with a 250ms debounce window.
- **Atomic Swap:** Successfully validated configurations are compiled into an immutable `BotAiProfileSnapshot` backed by `FrozenDictionary` and atomically swapped via reference exchange.
- **Fault Tolerance:**
  - **Startup Missing/Invalid:** Falls back to hardcoded 39-profile defaults (`BotAiDefaults`), logging a warning.
  - **Reload Missing/Invalid:** Retains the last active valid snapshot without downtime or crash, logging an error.
  - **Atomic Renames:** Supported by watching both creation/rename events and directory updates.

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
