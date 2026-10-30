#!/usr/bin/env python3
"""Hide the Disc Fragment exchange cell on the gacha screen's Discs tab (arm64 libil2cpp.so).

Colorful.ShopDiscScroller (TypeDefIndex 14402) is the EnhancedScroller delegate of the Discs tab. Outside the
tutorial it shows one cell more than there are gacha groups, and the cell at dataIndex == _data.Count - 1 is the
exchange block (ShopDiscCellView.SetDiscExchangeData: the "Owned: 0" frame, fragment icon, cost button and
"No disc fragments" text). In the tutorial the same three methods skip that cell. The patch makes them always take
their own tutorial branch, so the tab lists exactly the gacha groups and nothing else in the client changes:

  0x14B3BA0  GetNumberOfCells  eor  w20, w0, #1      -> mov w20, wzr         (count + 0 instead of count + 1)
  0x14B3A18  GetCellView       tbnz w0, #0, 0x14B3A98 -> b 0x14B3A98         (never SetDiscExchangeData)
  0x14B370C  GetCellHeight     tbnz w0, #0, 0x14B37C4 -> b 0x14B37C4         (never the exchange cell height)

RVA == file offset in this binary. The bytes are the same in base.apk (2.11.0) and every 2.11.5 build, since
patch-il2cpp-endpoints.py does not touch ShopDiscScroller. ShopDiscCellView.SetData already deactivates
_exchangeParent, so recycled cells stay clean. armeabi-v7a is not patched.

    python3 scripts/patch-hide-disc-exchange.py --so <libil2cpp.so> [--dry-run]
    python3 scripts/patch-hide-disc-exchange.py --apk-in <signed.apk> --apk-out <unsigned.apk>
The APK mode copies every entry unchanged except lib/arm64-v8a/libil2cpp.so (patched) and the old v1 signature
files (dropped); zipalign and apksigner come next (scripts/build-nofrag-apk.sh).
"""
import argparse
import re
import sys
import zipfile
from pathlib import Path

LIB = "lib/arm64-v8a/libil2cpp.so"

# (rva, original, patched, description)
PATCHES = [
    (0x14B3BA0, "14000052", "f4031f2a", "ShopDiscScroller.GetNumberOfCells: eor w20,w0,#1 -> mov w20,wzr"),
    (0x14B3A18, "00040037", "20000014", "ShopDiscScroller.GetCellView: tbnz w0,#0,0x14B3A98 -> b 0x14B3A98"),
    (0x14B370C, "c0050037", "2e000014", "ShopDiscScroller.GetCellHeight: tbnz w0,#0,0x14B37C4 -> b 0x14B37C4"),
]

SIGNATURE_FILE = re.compile(r"^META-INF/([^/]+\.(SF|RSA|DSA|EC)|MANIFEST\.MF)$", re.I)


def patch_bytes(data: bytearray) -> int:
    """Apply PATCHES in place; returns how many were written. Refuses on unexpected bytes."""
    written = 0
    for rva, orig, new, desc in PATCHES:
        cur = bytes(data[rva:rva + 4]).hex()
        if cur == new:
            print(f"  already  0x{rva:X}  {cur}  {desc}")
            continue
        if cur != orig:
            raise SystemExit(f"byte guard failed at 0x{rva:X}: found {cur}, expected {orig} ({desc})")
        data[rva:rva + 4] = bytes.fromhex(new)
        print(f"  patch    0x{rva:X}  {orig} -> {new}  {desc}")
        written += 1
    return written


def patch_so(path: Path, dry_run: bool) -> None:
    data = bytearray(path.read_bytes())
    patch_bytes(data)
    if not dry_run:
        path.write_bytes(data)


def patch_apk(src: Path, dst: Path) -> None:
    with zipfile.ZipFile(src) as zin, zipfile.ZipFile(dst, "w") as zout:
        for info in zin.infolist():
            if SIGNATURE_FILE.match(info.filename):
                print(f"  drop     {info.filename}")
                continue
            data = zin.read(info)
            if info.filename == LIB:
                buf = bytearray(data)
                patch_bytes(buf)
                data = bytes(buf)
            zout.writestr(info, data, compress_type=info.compress_type)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--so", type=Path, help="arm64 libil2cpp.so to patch in place")
    ap.add_argument("--dry-run", action="store_true", help="with --so: check the byte guards only")
    ap.add_argument("--apk-in", type=Path, help="source APK")
    ap.add_argument("--apk-out", type=Path, help="unsigned, unaligned output APK")
    args = ap.parse_args()
    if args.so:
        patch_so(args.so, args.dry_run)
    elif args.apk_in and args.apk_out:
        patch_apk(args.apk_in, args.apk_out)
    else:
        ap.error("pass --so, or --apk-in and --apk-out")
    return 0


if __name__ == "__main__":
    sys.exit(main())
