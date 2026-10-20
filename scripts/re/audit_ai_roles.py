#!/usr/bin/env python3
"""audit_ai_roles.py - Reproducible audit of native AI role semantics in libil2cpp.so."""
import bisect
import os
import pickle
import struct
from pathlib import Path
from capstone import CS_ARCH_ARM64, CS_MODE_ARM, Cs

REPO = Path(__file__).resolve().parents[2]
SO_PATH = Path(os.environ.get("KF_TARGET_SO", "/tmp/kf-ai-role-analysis/libil2cpp-2.11.3.so"))
MAIN_REPO = Path("/home/gixarde3/Proyectos/KickFlight/Kick-Flight-Private-Server")
SYMS_PATH = REPO / ".local" / "re-cache" / "symbols.pkl"
if not SYMS_PATH.exists():
    SYMS_PATH = MAIN_REPO / ".local" / "re-cache" / "symbols.pkl"

if not SO_PATH.exists():
    SO_PATH = REPO / "base.apk" # fallback

def main():
    with open(SYMS_PATH, "rb") as f:
        syms, strs, meta, mm = pickle.load(f)
    addrs = sorted(syms.keys())
    
    def get_sym(rva):
        idx = bisect.bisect_right(addrs, rva) - 1
        if idx >= 0:
            base = addrs[idx]
            return f"{syms[base]}+0x{rva-base:X}"
        return "unknown"

    with open(SO_PATH, "rb") as f:
        data = f.read()

    md = Cs(CS_ARCH_ARM64, CS_MODE_ARM)
    print("=== 1. UpdateAi Cave (0x159D200) Audit ===")
    cave_insns = list(md.disasm(data[0x159D200:0x159D3DC], 0x159D200))
    print(f"Cave entry: 0x{cave_insns[0].address:X}: {cave_insns[0].mnemonic} {cave_insns[0].op_str}")
    print(f"Displaced return: 0x{cave_insns[-2].address:X}: {cave_insns[-2].mnemonic} {cave_insns[-2].op_str}")
    print(f"Cave exit branch: 0x{cave_insns[-1].address:X}: {cave_insns[-1].mnemonic} {cave_insns[-1].op_str} -> resumes PlayerCharacter.UpdateAi+4 (0x13BEAF0)")
    print(f"Cave total footprint: 0x159D200..0x159D3D0 (0x1D4 / 468 bytes active)")

    print("\n=== 2. GetKickerAIParameterMaster (0x13C3260) ===")
    lookup_insns = list(md.disasm(data[0x13C3298:0x13C3310], 0x13C3298))
    for ins in lookup_insns[:9]:
        print(f"0x{ins.address:X}: {ins.mnemonic} {ins.op_str}")

    print("\n=== 3. Motivation Consumers & ScoreToDeposit ===")
    score_insns = list(md.disasm(data[0x180546C:0x1805490], 0x180546C))
    for ins in score_insns:
        print(f"0x{ins.address:X}: {ins.mnemonic} {ins.op_str}")

    print("\n=== 4. AIScrambleScoreEvaluationEngine Deposit Threshold (0x1808D40) ===")
    deposit_calc = list(md.disasm(data[0x1808D40:0x1808D58], 0x1808D40))
    for ins in deposit_calc:
        print(f"0x{ins.address:X}: {ins.mnemonic} {ins.op_str}")

    print("\n=== 5. AIPlayerEngine.UpdateTarget & Purpose Assignment (0x1807474) ===")
    update_target = list(md.disasm(data[0x1807464:0x1807488], 0x1807464))
    for ins in update_target:
        print(f"0x{ins.address:X}: {ins.mnemonic} {ins.op_str}")

    print("\n=== 6. AIPlayerEngine.PerformBehavior Decision (0x1805CB4) ===")
    perform_beh = list(md.disasm(data[0x1805CB4:0x1805CE0], 0x1805CB4))
    for ins in perform_beh:
        print(f"0x{ins.address:X}: {ins.mnemonic} {ins.op_str}")

if __name__ == "__main__":
    main()
