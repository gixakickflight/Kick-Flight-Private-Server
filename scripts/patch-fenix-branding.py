#!/usr/bin/env python3
"""Fenix rebrand of a decoded (apktool d -s) Kick Flight APK tree.

Run LAST (after the provider-injecting patchers, before `apktool b`):

    python scripts/patch-fenix-branding.py <decoded-dir> [--package world.global.fenix] [--name Fenix]

* AndroidManifest.xml (+ res/**/*.xml): old package id -> new id everywhere (package=, permissions, provider
  authorities, intent-filter categories). Default new id: $KF_PACKAGE_ID or world.global.fenix. Set
  KF_PACKAGE_ID=jp.grenge.kickflight to keep the original id (rename becomes a no-op).
* res/values*/strings.xml: app_name -> display name (default "Fenix").
* Launcher icons (res/mipmap-*/app_icon*.png, adaptive foreground/background, drawable-nodpi/ic_launcher.png,
  notification icons; F only) and assets/title_logo.jpg (blanked boot splash, EXIF kept) from assets/branding/fenix/.
Dex/native code is untouched: Application.identifier / paths are resolved at runtime from the package manager.
"""
import argparse, os, re, sys
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
BRAND = ROOT / "assets/branding/fenix"
OLD_PACKAGE = "jp.grenge.kickflight"
DEFAULT_PACKAGE = os.environ.get("KF_PACKAGE_ID", "world.global.fenix")
BG = (0x1B, 0x1B, 0x2A)
DENSITY_ICON = {"ldpi": 36, "mdpi": 48, "hdpi": 72, "xhdpi": 96, "xxhdpi": 144, "xxxhdpi": 192}


def rename_package(decoded: Path, new: str) -> int:
    if new == OLD_PACKAGE:
        return 0
    n = 0
    files = [decoded / "AndroidManifest.xml"] + sorted((decoded / "res").rglob("*.xml"))
    for f in files:
        t = f.read_text(encoding="utf-8")
        if OLD_PACKAGE in t:
            n += t.count(OLD_PACKAGE)
            f.write_text(t.replace(OLD_PACKAGE, new), encoding="utf-8")
    return n


def rename_app(decoded: Path, name: str) -> int:
    pat = re.compile(r'(<string name="app_name"[^>]*>)[^<]*(</string>)')
    n = 0
    for f in sorted((decoded / "res").glob("values*/strings.xml")):
        t = f.read_text(encoding="utf-8")
        t2, c = pat.subn(lambda m: m.group(1) + name + m.group(2), t)
        if c:
            f.write_text(t2, encoding="utf-8"); n += c
    return n


def square(path: Path, size: int) -> Image.Image:
    return Image.open(path).convert("RGBA").resize((size, size), Image.LANCZOS)


def patch_icons(decoded: Path) -> int:
    """All launcher variants use fenix-icon-1024.png (purple rounded square, yellow F, no flame).
    Adaptive: background = flat icon purple, foreground = the icon scaled into the safe zone (same purple, so seamless)."""
    from PIL import ImageDraw
    icon = Image.open(BRAND / "fenix-icon-1024.png").convert("RGBA")
    fmark = Image.open(BRAND / "fenix-f-1024.png").convert("RGBA")
    bgc = (0x1E, 0x12, 0x38)   # flat icon purple for the adaptive background
    n = 0
    res = decoded / "res"
    for d, px in DENSITY_ICON.items():
        m = res / f"mipmap-{d}"
        if not m.is_dir():
            continue
        for nm in ("app_icon.png", "app_icon_round.png"):
            if (m / nm).exists():
                img = icon.resize((px, px), Image.LANCZOS)
                if nm.endswith("round.png"):
                    big = icon.resize((px * 4, px * 4), Image.LANCZOS)
                    mask = Image.new("L", (px * 4, px * 4), 0)
                    ImageDraw.Draw(mask).ellipse((0, 0, px * 4 - 1, px * 4 - 1), fill=255)
                    sq = Image.new("RGBA", big.size, bgc + (255,)); sq.alpha_composite(big)
                    sq.putalpha(mask); img = sq.resize((px, px), Image.LANCZOS)
                img.save(m / nm); n += 1
        s = round(px * 108 / 48)
        fg = m / "ic_launcher_foreground.png"
        if fg.exists():
            canvas = Image.new("RGBA", (s, s), (0, 0, 0, 0))
            w = round(s * 0.85)   # F mark only (transparent), kept inside the 66/108 adaptive safe zone
            canvas.alpha_composite(fmark.resize((w, w), Image.LANCZOS), ((s - w) // 2, (s - w) // 2))
            canvas.save(fg); n += 1
        bgf = m / "ic_launcher_background.png"
        if bgf.exists():
            Image.new("RGB", (s, s), bgc).save(bgf); n += 1
    p = res / "drawable-nodpi/ic_launcher.png"
    if p.exists():
        icon.resize((72, 72), Image.LANCZOS).save(p); n += 1
    p = res / "drawable/notification_icon_large.png"
    if p.exists():
        icon.save(p); n += 1
    p = res / "drawable/notification_icon.png"
    if p.exists():  # white silhouette of the F (yellow face only, shadow dropped), alpha mask per Android rules
        w = 512
        lg = fmark.resize((w, w), Image.LANCZOS)
        r, g, bch, al = lg.split()
        face = Image.eval(Image.merge("RGB", (r, g, bch)).convert("L"), lambda v: 255 if v > 150 else 0)
        from PIL import ImageChops
        sil = Image.new("RGBA", (w, w), (255, 255, 255, 0)); sil.putalpha(ImageChops.multiply(face, al)); sil.save(p); n += 1
    return n


def _jpeg_segments(data: bytes):
    """Yield (marker, bytes) for header segments up to (excluding) SOS, and the offset of SOS."""
    assert data[:2] == b"\xff\xd8"
    i, out = 2, []
    while True:
        assert data[i] == 0xFF
        marker = data[i + 1]
        if marker == 0xDA:
            return out, i
        ln = int.from_bytes(data[i + 2:i + 4], "big")
        out.append((marker, data[i:i + 2 + ln]))
        i += 2 + ln


def patch_title_jpg(decoded: Path) -> bool:
    """assets/title_logo.jpg is also the client's key store: ColorfulManager.LoadData reads the EXIF block
    (ExifReader.ReadJpeg) to obtain PARALLEL/EC. Only the pixels may change; every original APPn segment
    (EXIF) is carried over verbatim, otherwise the game aborts at boot (ArgumentNullException in
    D2CParallelInfo..ctor)."""
    import io
    p = decoded / "assets/title_logo.jpg"
    if not p.exists():
        return False
    orig = p.read_bytes()
    w, h = Image.open(io.BytesIO(orig)).size
    canvas = Image.new("RGB", (w, h), (255, 255, 255))   # logo removed (2.11.807): plain white, EXIF kept
    buf = io.BytesIO(); canvas.save(buf, "JPEG", quality=95); new = buf.getvalue()
    o_segs, _ = _jpeg_segments(orig)
    n_segs, n_sos = _jpeg_segments(new)
    keep = b"".join(seg for m, seg in o_segs if 0xE0 <= m <= 0xEF)
    body = b"".join(seg for m, seg in n_segs if not (0xE0 <= m <= 0xEF))
    out = b"\xff\xd8" + keep + body + new[n_sos:]
    Image.open(io.BytesIO(out)).load()  # still a valid JPEG
    p.write_bytes(out)
    return True


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("decoded", type=Path)
    ap.add_argument("--package", default=DEFAULT_PACKAGE)
    ap.add_argument("--name", default="Fenix")
    a = ap.parse_args()
    if not (a.decoded / "AndroidManifest.xml").is_file():
        sys.exit(f"not a decoded APK tree: {a.decoded}")
    for need in ("fenix-icon-1024.png", "fenix-f-1024.png"):
        if not (BRAND / need).is_file():
            sys.exit(f"missing {BRAND / need}")
    print(f"package refs renamed: {rename_package(a.decoded, a.package)} -> {a.package}")
    print(f"app_name entries: {rename_app(a.decoded, a.name)} -> {a.name}")
    print(f"icons written: {patch_icons(a.decoded)}; title_logo.jpg: {patch_title_jpg(a.decoded)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
