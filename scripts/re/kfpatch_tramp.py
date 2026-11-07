"""Static kfpatch trampoline generator/validator (no cave needed).

NOTE (2.11.804): production hooks no longer use this 12-byte `br [slot]` form (it jumps to 0 while the slot is empty). They use a single
`b V` + fail-safe veneer, see scripts/re/kfpatch_veneer.py. This form remains for proof/diag hooks only.


Trampoline (12 B, written over the first 3 instructions of a hooked il2cpp function):
    adrp x16, slot_page ; ldr x16, [x16, #slot_off] ; br x16
The displaced 3 instructions must be PC-independent so libkfpatch can replay them in its original-call stub.
    python scripts/re/kfpatch_tramp.py <pristine.so> <fn_rva> <slot_va>
"""
import struct, sys

import capstone

PCREL = ("adrp", "adr", "b", "bl", "cbz", "cbnz", "tbz", "tbnz", "ldr", "ldrsw", "prfm")


def trampoline(fn_rva: int, slot_va: int) -> bytes:
    page = (slot_va >> 12) - (fn_rva >> 12)
    assert -(1 << 20) <= page < (1 << 20), "slot out of adrp range"
    adrp = 0x90000010 | ((page & 3) << 29) | (((page >> 2) & 0x7FFFF) << 5)
    off = slot_va & 0xFFF
    assert off % 8 == 0
    ldr = 0xF9400210 | ((off // 8) << 10)  # ldr x16, [x16, #off]
    return struct.pack("<III", adrp, ldr, 0xD61F0200)


def displaced_ok(data: bytes, fn_rva: int) -> list[str]:
    md = capstone.Cs(capstone.CS_ARCH_ARM64, capstone.CS_MODE_ARM)
    bad = []
    for ins in md.disasm(data[fn_rva:fn_rva + 12], fn_rva):
        pc_rel = ins.mnemonic in PCREL and (ins.mnemonic != "ldr" or "[" not in ins.op_str) or ins.mnemonic.startswith("b.")
        if pc_rel:
            bad.append(f"{ins.address:#x} {ins.mnemonic} {ins.op_str}")
    return bad


if __name__ == "__main__":
    so, fn, slot = sys.argv[1], int(sys.argv[2], 16), int(sys.argv[3], 16)
    d = open(so, "rb").read()
    print("tramp:", trampoline(fn, slot).hex(), "pc-relative displaced:", displaced_ok(d, fn) or "none")
