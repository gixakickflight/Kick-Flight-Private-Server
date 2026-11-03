#!/usr/bin/env python3
"""Symbolize native crash addresses in a KF-EXIT-REPORT (v2) / logcat diag file.

usage: symbolize_exit_report.py <diag-file> [--script script.json] [--il2cpp-base 0x7f...] [--top 12]

Per "=== exit N ===" block (or the whole file when there are no blocks) the il2cpp base is the lowest start address
of the libil2cpp.so lines in that block's "maps_snapshot" (written by ExitReportProvider v2 for the exit pid; pass
--il2cpp-base to override, e.g. from `cat /proc/<pid>/maps`). Then:
  * register dumps ("pc 0000007f...", "lr ...", "x30 ...", also "pc=0x..") -> RVA = value - base when it falls
    inside the il2cpp mapping;
  * backtrace frames "#NN pc <relative> .../libil2cpp.so" -> RVA = <relative> (already module-relative);
  * the first signal line ("Fatal signal 11 ... fault addr ...") is echoed.
Each RVA is mapped to the nearest preceding method in script.json (Il2CppDumper ScriptMethod[].Address; the same
RVAs scripts/re/a64dis.py takes). Native tombstone protos (trace: tombstone-proto-base64) are not decoded here:
`base64 -d | protoc --decode_raw` first if no text register dump is present. Note: under ndk_translation every crash
is reported at libunity+0x34426c; only fault addr / thread / il2cpp frames matter.
"""
import argparse, bisect, json, os, re, sys
from pathlib import Path

DEFAULT_SCRIPT = Path.home() / ".codex/worktrees/discs-balance-audit/Kick-Flight-Private-Server/.local/re/dump/script.json"
HEX = r"(?:0x)?([0-9a-fA-F]{6,16})"
REG_RE = re.compile(r"\b(pc|lr|x30|ip|sp)\b[ =:]+" + HEX)
FRAME_RE = re.compile(r"#(\d+)\s+pc\s+" + HEX + r"\s+(\S*libil2cpp\.so\S*)(?:\s+\((.*?)\))?")
MAP_RE = re.compile(r"^([0-9a-f]+)-([0-9a-f]+)\s+\S+\s+([0-9a-f]+)\s+\S+\s+\S+\s+(\S*libil2cpp\.so)\s*$")


def load_methods(path):
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    rows = sorted({(m["Address"], m["Name"]) for m in data["ScriptMethod"]})
    return [r[0] for r in rows], [r[1] for r in rows]


def il2cpp_range(text):
    lo = hi = None
    for line in text.splitlines():
        m = MAP_RE.match(line.strip())
        if m:
            s, e = int(m.group(1), 16), int(m.group(2), 16)
            lo = s if lo is None else min(lo, s)
            hi = e if hi is None else max(hi, e)
    return lo, hi


def sym(addrs, names, rva):
    i = bisect.bisect_right(addrs, rva) - 1
    if i < 0:
        return "?"
    return f"{names[i]}+0x{rva - addrs[i]:X}"


def process(block, title, addrs, names, base_override, top):
    lo, hi = il2cpp_range(block)
    if base_override is not None:
        lo, hi = base_override, base_override + 0x4000000
    print(f"--- {title}: il2cpp base {hex(lo) if lo else 'unknown (no maps snapshot)'}")
    for line in block.splitlines():
        if "Fatal signal" in line or "fault addr" in line:
            print("  signal:", line.strip()[:200])
            break
    seen = 0
    for line in block.splitlines():
        fm = FRAME_RE.search(line)
        if fm and seen < top:
            rva = int(fm.group(2), 16)
            print(f"  frame #{fm.group(1)} RVA 0x{rva:X} {sym(addrs, names, rva)}")
            seen += 1
            continue
        for rm in REG_RE.finditer(line):
            val = int(rm.group(2), 16)
            if lo is not None and lo <= val < hi and seen < top:
                rva = val - lo
                print(f"  {rm.group(1)} 0x{val:X} -> RVA 0x{rva:X} {sym(addrs, names, rva)}")
                seen += 1
    if seen == 0:
        print("  (no il2cpp address found: crash is outside libil2cpp, or no register/backtrace text and no snapshot)")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("diag")
    ap.add_argument("--script", default=os.environ.get("KF_SCRIPT_JSON", str(DEFAULT_SCRIPT)))
    ap.add_argument("--il2cpp-base", type=lambda s: int(s, 0))
    ap.add_argument("--top", type=int, default=12)
    a = ap.parse_args()
    addrs, names = load_methods(a.script)
    text = Path(a.diag).read_text(encoding="utf-8", errors="replace")
    parts = re.split(r"^=== exit (\d+) ===$", text, flags=re.M)
    if len(parts) == 1:
        process(text, "whole file", addrs, names, a.il2cpp_base, a.top)
    else:
        # parts: [header, n1, body1, n2, body2, ...]; the shared logcat tail is appended to the last block
        for n, body in zip(parts[1::2], parts[2::2]):
            pid = re.search(r"process: \S+ pid (\d+)", body)
            reason = re.search(r"reason: (\S+)", body)
            process(body, f"exit {n} {reason.group(1) if reason else ''} pid {pid.group(1) if pid else '?'}",
                    addrs, names, a.il2cpp_base, a.top)
    return 0


if __name__ == "__main__":
    sys.exit(main())
