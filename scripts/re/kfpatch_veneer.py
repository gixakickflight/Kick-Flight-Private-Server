"""Fail-safe kfpatch veneers (pure Python, no capstone) shared by patch-il2cpp-endpoints.py and kfpatch_tramp.py.

Problem (2.11.800 pc=0 crash class): a static `adrp x16,slot; ldr x16,[x16]; br x16` trampoline jumps to 0 when libkfpatch has not
filled the slot yet (watcher not scheduled, libkfpatch failed to load, hook fires early). Fix (2.11.804): every hook site is a
single `b V`; V lives in verified-dead code and tests the slot before jumping:

    V+0   adrp x16, slot_page
    V+4   ldr  x16, [x16, #slot_off]
    V+8   cbz  x16, F                  ; slot empty -> run the displaced original code
    V+12  br   x16                     ; slot filled -> hook / cave (LR and all argument registers untouched)
    F:    <fail-safe words>            ; displaced instruction(s) re-encoded for their new address, then back to the site

The single displaced instruction I0 is NOT executed before the slot test (the C hook's own "call original" stub, KF_ORIG_STUB1 in
kfpatch.h, executes I0 and resumes at site+4, so the hook sees the unmodified entry state). x16 (IP0) is the only register touched.
cbz/adrp/ldr/br do not modify NZCV. Rules from AGENTS.md section 3: b (never bl) at entry hooks, LR untouched, no leaf hooks.
"""
import struct

RET = 0xD65F03C0
BR_X16 = 0xD61F0200
NOP = 0xD503201F
VENEER_FIXED = 16          # adrp, ldr, cbz, br
CBZ_X16_F = 0xB4000000 | (2 << 5) | 16   # cbz x16, +8 (F = V+16; cbz sits at V+8)


def adrp_x16(va: int, target_va: int) -> int:
    page = (target_va >> 12) - (va >> 12)
    assert -(1 << 20) <= page < (1 << 20), "slot out of adrp range"
    return 0x90000010 | ((page & 3) << 29) | (((page >> 2) & 0x7FFFF) << 5)


def ldr_x16(slot_va: int) -> int:
    off = slot_va & 0xFFF
    assert off % 8 == 0
    return 0xF9400210 | ((off // 8) << 10)   # ldr x16, [x16, #off]


def branch(va: int, target: int, link: bool = False) -> int:
    d = target - va
    assert d % 4 == 0
    imm = d // 4
    assert -(1 << 25) <= imm < (1 << 25), "branch out of range"
    return (0x94000000 if link else 0x14000000) | (imm & 0x3FFFFFF)


def decode_b(word: int, va: int) -> int:
    """Target of an unconditional `b` (not bl) at va."""
    assert (word >> 26) == 5, "not a plain b"
    imm = word & 0x3FFFFFF
    if imm & 0x2000000:
        imm -= 1 << 26
    return va + imm * 4


def is_pc_independent(word: int) -> bool:
    """Conservative opcode-class test for words that may be replayed at another address (no b/bl/b.cond/cbz/tbz/adr/adrp/ldr-literal)."""
    if (word >> 26) in (5, 0x25):                       # b / bl
        return False
    if (word >> 24) == 0x54:                            # b.cond
        return False
    if ((word >> 25) & 0x3F) in (0x1A, 0x1B):           # cbz/cbnz/tbz/tbnz
        return False
    if (word & 0x1F000000) == 0x10000000:               # adr / adrp
        return False
    if (word & 0x3B000000) == 0x18000000:               # ldr (literal), incl. SIMD/ldrsw/prfm
        return False
    return True


def failsafe_resume(i0: int, f_va: int, resume_va: int) -> list[int]:
    """Replay one PC-independent displaced instruction and continue at resume_va (F is at f_va)."""
    assert is_pc_independent(i0), f"displaced word {i0:08x} is PC-relative"
    return [i0, branch(f_va + 4, resume_va)]


def failsafe_tail_b(i0: int, site_va: int, f_va: int) -> list[int]:
    """The displaced word is itself a plain `b T` (tail call): re-encode it at its new address."""
    return [branch(f_va, decode_b(i0, site_va))]


def failsafe_bl_site(i0: int) -> list[int]:
    """Hook site is a `bl V` replacing a PC-independent instruction I0: V is entered with LR = site+4, so replay I0 and `ret`."""
    assert is_pc_independent(i0), f"displaced word {i0:08x} is PC-relative"
    return [i0, RET]


def veneer(va: int, slot_va: int, fail_words: list[int], size: int) -> bytes:
    words = [adrp_x16(va, slot_va), ldr_x16(slot_va), CBZ_X16_F, BR_X16] + list(fail_words)
    assert len(words) * 4 <= size, f"veneer needs {len(words) * 4} B > {size} B"
    words += [NOP] * (size // 4 - len(words))
    return struct.pack(f"<{len(words)}I", *words)
