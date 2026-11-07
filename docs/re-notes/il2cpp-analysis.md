# IL2CPP analysis: use Il2CppDumper + a64dis, not Ghidra

Ghidra-backed REA tools (procedure search/list, decompile, xrefs, `analyze_function`) on the 74 MB arm64
`libil2cpp.so` need 30+ min of initial auto-analysis and hit the MCP idle timeout (~1829 s). Do not use them.
APK/IPA structure, manifests, strings and small binaries are fine in REA.

## Dump locations (Android arm64, base.apk 2.11.0; not in this checkout)
Dir: `/home/gixarde3/.codex/worktrees/discs-balance-audit/Kick-Flight-Private-Server/.local/re/dump/`
- `dump.cs` (27 MB), `script.json` (67 MB), `il2cpp.h` (53 MB), `stringliteral.json`, `DummyDll/`
- Matching binary: `.../discs-balance-audit/Kick-Flight-Private-Server/.local/re/lib/arm64-v8a/libil2cpp.so`
- Other dumps (iOS / armv7 / readygate) live under `~/.codex/worktrees/{ios-build,latest-information}/.../.local/`.
- The default `Kick-Flight-Assets/server_revival_analysis/il2cpp/` does not exist on this machine.
- Verify the dump matches the target binary (version/SHA-256) before trusting RVAs. 2.11.6 may differ from 2.11.0.

## Usage
```bash
D=/home/gixarde3/.codex/worktrees/discs-balance-audit/Kick-Flight-Private-Server/.local/re
grep -n "class .*GameScene\|class .*Battle" $D/dump/dump.cs      # class/method + // RVA: comments
export KF_SCRIPT_JSON=$D/dump/script.json KF_CLEAN_LIBIL2CPP=$D/lib/arm64-v8a/libil2cpp.so
cd /home/gixarde3/Proyectos/KickFlight/Kick-Flight-Private-Server
.venv/bin/python scripts/re/a64dis.py 0x156E8E0 40               # annotated disassembly
```
First `a64dis.py` run builds `.local/re-cache/symbols.pkl` (slow, once). See `scripts/re/README.md`.

Note: `a64dis.py` needs capstone, which is not in the system Python. Always run it with the repo venv: `.venv/bin/python scripts/re/a64dis.py <rva> <count>` (plain `python3` fails with a capstone import error).
