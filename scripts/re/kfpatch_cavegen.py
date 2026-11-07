#!/usr/bin/env python3
"""Migrates the prod caves hosted in the SetModel / <LoadModelAsync>d__25.MoveNext / UnloadModel dead bodies into libkfpatch.

Reads the active NATIVE_PATCHES of scripts/patch-il2cpp-endpoints.py (all AI-role flags on, no KF_NATIVE), picks the cave
patches that live inside the three bodies and writes:
  src/native/kfpatch/caves_gen.S       the same instruction words, with PC-relative instructions rewritten
  src/native/kfpatch/caves_manifest.json  blobs (offset, sha256), entries (target rva -> slot) used by the patcher
Rewrites (everything else is copied verbatim as .inst, so behaviour is byte-for-byte the original cave):
  b/bl/b.cond/cbz/cbnz/tbz/tbnz to another cave blob -> label, to il2cpp code -> veneer (ldr x16,pool; br x16)
  adrp/adr -> ldr xN, pool (absolute il2cpp address filled with the module base at init)
  ldr (literal) inside the same blob is kept; any other PC-relative use aborts.
Hook sites elsewhere in il2cpp that branched into the bodies are retargeted by the patcher to 12-byte slot veneers placed in
the dead body of PlayerCharacter.UpdateLookTarget (0x13CD990..).
    .venv/bin/python scripts/re/kfpatch_cavegen.py
"""
import hashlib, importlib.util, json, os, struct, sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
RANGES = [(0x159D1BC, 0x159DAB0), (0x159DF5C, 0x159E0A8), (0x159E254, 0x159E354)]
STUBS = {0x159D1BC, 0x159DF5C, 0x159E254}          # entry "bypass null exception" stubs: dropped (bodies restored)
CAVE_SLOT_BASE = 16                                  # slots 0..15 are for C hooks
VENEER_HOST = 0x13CD990                              # dead PlayerCharacter.UpdateLookTarget body (free unless KF_DIAG)
COND = ["eq", "ne", "hs", "lo", "mi", "pl", "vs", "vc", "hi", "ls", "ge", "lt", "gt", "le", "al", "nv"]


def inr(a):
    return any(lo <= a < hi for lo, hi in RANGES)


def load_patches():
    os.environ.update(KF_PHOTON="1", KF_AI_ROLES="1", KF_AI_ROLE_TARGETING="1", KF_AI_ROLE_DEPOSIT="1")
    os.environ.pop("KF_NATIVE", None)
    _argv = sys.argv; sys.argv = ["x", "--dry-run"]
    spec = importlib.util.spec_from_file_location("kfp", REPO / "scripts/patch-il2cpp-endpoints.py")
    m = importlib.util.module_from_spec(spec)
    try:
        spec.loader.exec_module(m)
    except SystemExit:
        pass
    sys.argv = _argv
    return m.NATIVE_PATCHES["arm64-v8a"]


def simm(w, shift, bits):
    v = (w >> shift) & ((1 << bits) - 1)
    return v - (1 << bits) if v >> (bits - 1) else v


def branch_target(w, pc):
    """(kind, target) for b/bl, or None."""
    op = w >> 26
    if op in (5, 0x25):
        return ("bl" if op == 0x25 else "b", pc + simm(w, 0, 26) * 4)
    return None


def main():
    pristine = Path(sys.argv[1] if len(sys.argv) > 1 else REPO / ".local/run/lib/libil2cpp.pristine.so").read_bytes()
    patches = load_patches()
    caves = [p for p in patches if inr(int(p["offset"])) and int(p["offset"]) not in STUBS]
    caves.sort(key=lambda p: int(p["offset"]))
    blobs = [(int(p["offset"]), p["replacement"], p["description"]) for p in caves]

    def blob_of(t):
        for i, (o, b, _) in enumerate(blobs):
            if o <= t < o + len(b):
                return i
        return None

    # entries: branches from outside the blobs into a blob
    entries = {}
    hook_sites = []
    for p in patches:
        o = int(p["offset"])
        if inr(o):
            continue
        rep = p["replacement"]
        for i in range(0, len(rep) - 3, 4):
            w = struct.unpack_from("<I", rep, i)[0]
            bt = branch_target(w, o + i)
            if bt and blob_of(bt[1]) is not None:
                entries.setdefault(bt[1], None)
                hook_sites.append((o + i, bt[0], bt[1]))
    for k, t in enumerate(sorted(entries)):
        entries[t] = CAVE_SLOT_BASE + k

    pool = []        # il2cpp RVAs
    pool_idx = {}

    def pidx(rva):
        if rva not in pool_idx:
            pool_idx[rva] = len(pool)
            pool.append(rva)
        return pool_idx[rva]

    veneers = {}     # il2cpp target -> label

    def veneer(t):
        if t not in veneers:
            veneers[t] = f".Lv{len(veneers)}"
        return veneers[t]

    def label(t):
        i = blob_of(t)
        return f".Lb{i}+{t - blobs[i][0]}"

    # embedded string data (referenced by adr from the same blob) is the blob tail from the lowest such target
    data_from = {}
    for bi, (o, rep, _) in enumerate(blobs):
        for i in range(0, len(rep), 4):
            w = struct.unpack_from("<I", rep, i)[0]
            if (w & 0x9f000000) == 0x10000000:
                val = o + i + ((simm(w, 5, 19) << 2) | ((w >> 29) & 3))
                if o <= val < o + len(rep):
                    data_from[bi] = min(data_from.get(bi, 1 << 40), val & ~3)
    out = []
    warn = []
    for bi, (o, rep, desc) in enumerate(blobs):
        out.append(f"// blob {bi}: 0x{o:x} len {len(rep)}  {desc[:110]}")
        out.append(f".Lb{bi}:")
        assert len(rep) % 4 == 0
        for i in range(0, len(rep), 4):
            pc = o + i
            w = struct.unpack_from("<I", rep, i)[0]
            same = lambda t: blob_of(t) == bi
            if pc >= data_from.get(bi, 1 << 40):
                out.append(f"  .inst 0x{w:08x}  // data")
                continue
            bt = branch_target(w, pc)
            if bt:
                kind, t = bt
                if same(t):
                    out.append(f"  .inst 0x{w:08x}")
                elif blob_of(t) is not None:
                    out.append(f"  {kind} {label(t)}")
                else:
                    if not (0x1000 <= t < 0x4A00000):
                        warn.append(f"blob {bi} +{i:#x}: branch to {t:#x} looks like data")
                    out.append(f"  {kind} {veneer(t)}")
                continue
            if (w & 0xff000010) == 0x54000000:  # b.cond
                t = pc + simm(w, 5, 19) * 4
                c = COND[w & 15]
                if same(t):
                    out.append(f"  .inst 0x{w:08x}")
                elif blob_of(t) is not None:
                    out.append(f"  b.{c} {label(t)}")
                else:
                    out.append(f"  b.{c} {veneer(t)}")
                continue
            if (w & 0x7e000000) == 0x34000000:  # cbz/cbnz
                t = pc + simm(w, 5, 19) * 4
                if same(t):
                    out.append(f"  .inst 0x{w:08x}")
                else:
                    r = ("x" if w >> 31 else "w") + str(w & 31)
                    tgt = label(t) if blob_of(t) is not None else veneer(t)
                    out.append(f"  {'cbnz' if (w >> 24) & 1 else 'cbz'} {r}, {tgt}")
                continue
            if (w & 0x7e000000) == 0x36000000:  # tbz/tbnz
                t = pc + simm(w, 5, 14) * 4
                if same(t):
                    out.append(f"  .inst 0x{w:08x}")
                else:
                    bit = ((w >> 31) << 5) | ((w >> 19) & 31)
                    r = ("x" if w >> 31 else "w") + str(w & 31)
                    tgt = label(t) if blob_of(t) is not None else veneer(t)
                    out.append(f"  {'tbnz' if (w >> 24) & 1 else 'tbz'} {r}, #{bit}, {tgt}")
                continue
            if (w & 0x9f000000) == 0x90000000:  # adrp
                imm = ((simm(w, 5, 19) << 2) | ((w >> 29) & 3))
                val = (pc & ~0xFFF) + (imm << 12)
                out.append(f"  ldr x{w & 31}, kf_pool+{8 * pidx(val)}  // adrp 0x{val:x}")
                continue
            if (w & 0x9f000000) == 0x10000000:  # adr
                imm = ((simm(w, 5, 19) << 2) | ((w >> 29) & 3))
                val = pc + imm
                if same(val):
                    out.append(f"  .inst 0x{w:08x}")
                    continue
                if blob_of(val) is not None:
                    warn.append(f"blob {bi} +{i:#x}: adr into another blob ({val:#x}) unsupported")
                out.append(f"  ldr x{w & 31}, kf_pool+{8 * pidx(val)}  // adr 0x{val:x}")
                continue
            if (w & 0x3b000000) == 0x18000000:  # ldr literal
                t = pc + simm(w, 5, 19) * 4
                if same(t):
                    out.append(f"  .inst 0x{w:08x}")
                else:
                    warn.append(f"blob {bi} +{i:#x}: ldr literal to {t:#x} unsupported")
                    out.append(f"  .inst 0x{w:08x}")
                continue
            out.append(f"  .inst 0x{w:08x}")
    if warn:
        print("WARNINGS:\n" + "\n".join(warn))
        if any("unsupported" in w for w in warn):
            sys.exit("abort: unsupported PC-relative use")

    asm = ["// GENERATED by scripts/re/kfpatch_cavegen.py - do not edit. See docs/re-notes/kfpatch-design.md (cave migration).",
           ".text", ".p2align 4"]
    asm.append(".hidden kf_pool\n.hidden kf_pool_rva\n.hidden kf_pool_count\n.hidden kf_cave_slot_table\n.hidden kf_cave_slot_count")
    asm += out
    asm.append(".p2align 2")
    for t, lab in veneers.items():
        asm.append(f"{lab}:  // -> il2cpp 0x{t:x}\n  ldr x16, kf_pool+{8 * pidx(t)}\n  br x16")
    asm += [".data", ".p2align 3", ".global kf_pool", "kf_pool:"]
    asm += [f"  .quad 0  // 0x{r:x}" for r in pool]
    asm += [".section .rodata", ".p2align 3", ".global kf_pool_rva", "kf_pool_rva:"]
    asm += [f"  .quad 0x{r:x}" for r in pool]
    asm += [".global kf_pool_count", "kf_pool_count:", f"  .word {len(pool)}", ".p2align 3"]
    asm += [".global kf_cave_slot_table", "kf_cave_slot_table:"]
    for t, slot in sorted(entries.items(), key=lambda kv: kv[1]):
        asm.append(f"  .quad {slot}, {label(t)} - kf_cave_slot_table  // 0x{t:x}")
    asm += [".global kf_cave_slot_count", "kf_cave_slot_count:", f"  .word {len(entries)}"]
    (REPO / "src/native/kfpatch/caves_gen.S").write_text("\n".join(asm) + "\n")

    man = {
        "veneer_host": VENEER_HOST,
        "ranges": RANGES,
        "stubs": sorted(STUBS),
        "blobs": [{"offset": o, "len": len(b), "sha256": hashlib.sha256(b).hexdigest(), "desc": d[:140]} for o, b, d in blobs],
        "entries": [{"target": t, "slot": s} for t, s in sorted(entries.items(), key=lambda kv: kv[1])],
        "hook_sites": [{"site": s, "kind": k, "target": t} for s, k, t in sorted(hook_sites)],
        "pool_count": len(pool),
        "veneer_expected_hex": pristine[VENEER_HOST:VENEER_HOST + 12 * len(entries)].hex(),
    }
    (REPO / "src/native/kfpatch/caves_manifest.json").write_text(json.dumps(man, indent=1) + "\n")
    print(f"{len(blobs)} blobs, {sum(len(b) for _, b, _ in blobs)} B, {len(entries)} entries, {len(veneers)} veneers, {len(pool)} pool words")
    for t, s in sorted(entries.items(), key=lambda kv: kv[1]):
        print(f"  slot {s} -> 0x{t:x}")


if __name__ == "__main__":
    main()
