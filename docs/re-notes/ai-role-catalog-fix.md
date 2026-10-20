# AI Role Scramble Bot Deck Catalog Resolution Fix

## 1. Problem Context
When `KickFlight.BootstrapApi` was started from `src/KickFlight.BootstrapApi`, `contentRoot` pointed to the project subdirectory. `ScrambleBotDeckCatalog.Load` attempted `Path.Combine(contentRoot, "config/scramble_bot_disc_decks.json")`, failing to locate the configuration at repository root (`config/scramble_bot_disc_decks.json`). As a result:
- Catalog loading failed with log warning: `Scramble bot deck catalog is missing at ...; legacy bot decks remain active`.
- Empty catalog caused matchmaking to fall back to legacy decks, preventing bot role assignments (IDs 15..53) from activating in Scramble matches.

## 2. Implemented Fix
`ScrambleBotDeckCatalog.ResolveCatalogPath(string contentRoot)` was introduced in `src/KickFlight.BootstrapApi/ScrambleBotDeckCatalog.cs`:
1. Directly inspects explicit file path if provided.
2. Checks relative path under `contentRoot`.
3. Performs upward directory traversal from `contentRoot` stopping at repository root (`KickFlight.PrivateServer.sln`).
4. Checks `AppContext.BaseDirectory` for published binary deployments.

## 3. Verification & Evidence

### A. Focused Automated Tests
- Command:
  ```bash
  dotnet test tests/KickFlight.BootstrapApi.Tests/KickFlight.BootstrapApi.Tests.csproj \
    --filter "FullyQualifiedName~ExperimentalAiRoleTests|FullyQualifiedName~ScrambleBotCompositionTests"
  ```
- Result: **31 passed, 0 failed, 0 skipped** (16 ExperimentalAiRoleTests, 15 ScrambleBotCompositionTests).
- Key verified test cases:
  - `NestedAppContentRoot_LoadsCatalogFromRepoRoot_BuildRosterAssignsNonLegacyRoleIds15To53`: Verifies upward catalog walk loads 448 deck variants and `BuildRoster` assigns non-legacy role IDs 15..53 to all 7 bots.
  - `DeterministicSharedContract_ThirtyNineBoundedUniqueIds`: Verifies exactly 39 unique IDs bounded within [15..53].
  - `EncryptedMasters_ContainsMatchingIds_WhenEnabled`: Verifies D2CCodec encryption/decryption preserves all 39 rows (15..53).

### B. Live API Process & Bounded Log Audit (PID 100379)
- Running Process:
  - PID: 100379 (`dotnet run --project src/KickFlight.BootstrapApi/KickFlight.BootstrapApi.csproj`)
  - Child PID: 100426 (`KickFlight.BootstrapApi`)
  - Process environment confirms: `KF_EXPERIMENTAL_AI_ROLES=1`
- Bounded Log Verification (`.local/run/server-api.log`):
  ```json
  {"Timestamp":"2026-10-19T23:51:48.393Z","LogLevel":"Information","Category":"KickFlight.BootstrapApi.BattleMatchmakingService","Message":"Loaded 448 validated Scramble bot deck variants from /home/gixarde3/Proyectos/KickFlight/Kick-Flight-Private-Server/.local/worktrees/ai-role-investigation/config/scramble_bot_disc_decks.json"}
  ```

### C. Live Master Endpoint Probe
- Command:
  ```bash
  /home/gixarde3/Proyectos/KickFlight/Kick-Flight-Private-Server/.venv/bin/python -c '
  import urllib.request, json
  from Crypto.Cipher import AES
  req = urllib.request.Request("http://127.0.0.1:18080/demo-master/KickerAiParameter", headers={"Host": "kickflight-api.grenge.jp"})
  body = urllib.request.urlopen(req).read()
  key = b"1a837b9ee2ae11a07a0f529a4cd4b61c"
  pt = AES.new(key, AES.MODE_CBC, body[:16]).decrypt(body[16:])[:-body[-1]]
  rows = json.loads(pt)
  ids = [r["id"] for r in rows]
  exp_ids = [i for i in ids if 15 <= i <= 53]
  print(f"Total: {len(rows)}, Experimental: {len(exp_ids)}, Range: {min(exp_ids)}..{max(exp_ids)}")
  '
  ```
- Result:
  - Total rows: 67 (14 base + 14 gym + 39 experimental).
  - Experimental IDs count: 39.
  - Experimental IDs range: 15..53 (all expected IDs present).
