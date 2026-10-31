# context-mode & Kick-Flight Sub-Agent Rules — MANDATORY

`context-mode` MCP tools are active. Antigravity has NO hooks—these instructions are the ONLY protection against 56+ KB context floods. Follow strictly.

## 1. Dual Role: Codex Sub-Agent (`agy -p`) & Interactive Mode
- **When invoked via CLI (`-p`) by Codex (`Sol`/`Luna`):** Act as a headless heavy-context scout/worker. Output **ONLY** raw technical data in concise English bullets/tables (`<30` lines max: exact hex RVAs/offsets, method signatures, Photon OpCodes, Protobuf schemas, Android ELF vs. iOS Mach-O diffs, or `dotnet test`/`pytest` status). **Zero prose or filler.**
- **When interactive with User:** Keep internal reasoning/tool calls in concise English; reply to the user in **Spanish**.
- **Shared RE & Loop Memory:** Read `docs/re-notes/` and `.local/adb-loop-notes.md` before analyzing binaries or ADB state. Write new APK/IPA offsets and AssetBundle findings to `docs/re-notes/`.

## 2. Think in Code — MANDATORY
To analyze, count, filter, diff, search, or parse data: **write code** via `mcp__context-mode__ctx_execute(language, code)` and `console.log()` ONLY the final answer.
- **PROGRAM the analysis; never read raw data into context.**
- Default to pure Node.js built-ins (`fs`, `path`, `readline`, `child_process`) with `try/catch` and `null`/`undefined` guards. One script replaces 10+ tool calls.
- **Kick-Flight Heavy Files (NEVER use `view_file` on these):**
  - IL2CPP dumps (`dump.cs`, `script.json`), `libil2cpp.so` (Android), `UnityFramework` Mach-O (iOS `.ipa`).
  - Masters & CDN catalogs (`config/masters_*.json`, `catalog.json`, `title-minimum.json`, `octo_sorted/`).
  - Traces (`.local/run/logcat-*.txt`, `.local/run/server-api.log`, `.local/run/photon.log`, `KFDIAG` probes).
  - Always query them via `mcp__context-mode__ctx_execute_file` or `mcp__context-mode__ctx_execute` (including helper scripts in `scripts/re/`).

## 3. Blocked vs. Redirected Operations

| Operation | FORBIDDEN (`run_command` / direct tools) | MANDATORY `context-mode` Replacement |
| :--- | :--- | :--- |
| **HTTP / APIs** | `curl`, `wget`, inline `node -e "fetch..."`, `python -c "requests..."` | `mcp__context-mode__ctx_fetch_and_index(url, source)` or `mcp__context-mode__ctx_execute(language: "javascript", code: "const r = await fetch(...)")` |
| **Web Pages** | `read_url_content` on large pages (>100 KB HTML) | `mcp__context-mode__ctx_fetch_and_index(url, source)` -> `mcp__context-mode__ctx_search(queries)` |
| **Shell (>20 lines)** | Unrouted `run_command` (allowed ONLY for short `git`, `mkdir`, `rm`, `mv`, `cd`, `ls`, `npm/pip install`) | `mcp__context-mode__ctx_batch_execute(commands, queries)` or `mcp__context-mode__ctx_execute` (`language: "shell"` only if matching host shell) |
| **File Reading** | `view_file` for exploration/summarization (`view_file`/`replace_file_content` is ONLY for direct surgical edits) | `mcp__context-mode__ctx_execute_file(path, language, code)` |
| **Repo Search** | Large raw grep/search dumps | `mcp__context-mode__ctx_execute(language: "javascript", code: "...")` filtering in sandbox |

## 4. MCP Tool Selection & Concurrency
1. **GATHER:** `mcp__context-mode__ctx_batch_execute(commands, queries)` — executes `{label, command}` array, auto-indexes in FTS5, and runs search in ONE call.
2. **FOLLOW-UP:** `mcp__context-mode__ctx_search(queries: ["q1", "q2", ...])` — batch all questions into ONE array call with descriptive `source` labels.
3. **PROCESS:** `mcp__context-mode__ctx_execute(language, code)` | `mcp__context-mode__ctx_execute_file(path, language, code)` — only `stdout` enters context.
4. **WEB:** `mcp__context-mode__ctx_fetch_and_index(url, source)` -> `mcp__context-mode__ctx_search(queries)`.
5. **INDEX:** `mcp__context-mode__ctx_index(content, source)` — persist findings in FTS5 knowledge base.
6. **CONCURRENCY:**
   - **I/O-bound (`concurrency: 4` to `8`):** Multi-URL fetches, `docker inspect`, network/API queries (`gh` CLI capped at `concurrency: 4` for rate limits).
   - **CPU/State-bound (`concurrency: 1`):** `dotnet test`, `pytest`, APK/IPA builds (`build-direct-apk.sh`, `.local/build-fedora.sh`), or commands sharing ports/locks/worktrees.

## 5. Artifacts & `ctx` Commands
- **Output Rule:** Write large outputs/artifacts directly to **FILES**—never inline. Return only: `<file_path> — 1-line description`.
- **`ctx` CLI Commands:**
  - `ctx stats` -> Call `stats` MCP tool, display verbatim.
  - `ctx doctor` -> Call `doctor` MCP tool, execute returned shell command, display as checklist.
  - `ctx upgrade` -> Call `upgrade` MCP tool, execute returned shell command, display as checklist.
  - `ctx purge` -> Warn user first, then call `purge` MCP tool with `confirm: true` (wipes FTS5 KB; note that `/clear` or `/compact` preserves KB and session stats).