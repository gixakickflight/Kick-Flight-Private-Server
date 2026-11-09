#!/usr/bin/env python3
"""Logo removal (2.11.807): repaint the Kick-Flight logo out of the home-scene textures.

The home 3D scene (field/fld99999 and the event/season variants) atlas textures carry a yellow banner strip with a
repeating "Kick-Flight" wordmark and a yellow sign with the wordmark. This tool decodes the preserved Octo bundles from
the Kick-Flight-Assets checkout, paints the logo area with the surrounding background (same texture size and format,
nothing else in the bundle changes) and writes new Octo bundles to content/resources/fenix-home/.

    .local/assets-venv/bin/python scripts/remove-home-logo.py [--assets ../Kick-Flight-Assets] [--preview DIR]

Then point the matching entries in config/resources/title-minimum.json at the output (scripts/remove-home-logo.py
--update-catalog does it), bump revision/fromRevisions and run scripts/build-title-resource-catalog.py.
"""
import argparse, hashlib, importlib.machinery, importlib.util, json, sys
from pathlib import Path
import numpy as np
import UnityPy
from PIL import Image
from UnityPy.enums import TextureFormat

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "content/resources/fenix-home"
l = importlib.machinery.SourceFileLoader("c", str(ROOT / "scripts/build-action-asset-bundles.py"))
s = importlib.util.spec_from_loader(l.name, l); codec = importlib.util.module_from_spec(s); l.exec_module(codec)

# texture name -> (has banner strip, sign search window x0,y0,x1,y1 at 2048x2048 or None)
TEXTURES = {
    "tx_home":            (True,  (1150, 920, 1600, 1100)),
    "tx_home_xmas":       (False, (1240, 920, 1560, 1100)),
    "tx_home_helloween":  (True,  None),
    "tx_home_newyear":    (True,  (1240, 920, 1560, 1100)),
    "tx_BG01_co2_C":      (False, (980, 1050, 1260, 1210)),
}


def yellow(a):
    return (a[..., 0] > 200) & (a[..., 1] > 150) & (a[..., 2] < 90)


def repaint_strip(a):
    H, W = a.shape[:2]
    flat = (np.abs(a[: H // 3, :, 0].astype(int) - 255) < 8) & (np.abs(a[: H // 3, :, 1].astype(int) - 220) < 14) & (a[: H // 3, :, 2] < 20)
    rows = np.where(flat.mean(axis=1) > 0.05)[0]       # the flat yellow banner (not the orange gradient above it)
    if len(rows) < 30:
        return False
    runs = np.split(rows, np.where(np.diff(rows) > 12)[0] + 1)
    run = max(runs, key=len)                                               # largest flat band
    y0, y1 = run[0], run[-1] + 1
    band = a[y0 + 3:y1 - 3, :, :3].astype(int)
    # background is vertically constant: a column is clean when every pixel matches its first row (text/highlighted text differ)
    text = (np.abs(band - band[:1]).max(axis=(0, 2)) > 12)
    text = np.convolve(text.astype(int), np.ones(3, int), "same") > 0
    clean = np.where(~text)[0]
    if len(clean) < 64:
        return False
    idx = np.searchsorted(clean, np.arange(W))
    left = clean[np.clip(idx - 1, 0, len(clean) - 1)]; right = clean[np.clip(idx, 0, len(clean) - 1)]
    src = np.where(np.abs(np.arange(W) - left) <= np.abs(right - np.arange(W)), left, right)
    ya, yb = y0, y1
    a[ya:yb] = a[ya:yb][:, src]
    return True


def repaint_sign(a, win):
    x0, y0, x1, y1 = win
    m = yellow(a[y0:y1, x0:x1])
    cols = np.where(m.sum(axis=0) > 20)[0]; rows = np.where(m.sum(axis=1) > 20)[0]
    if len(cols) < 50 or len(rows) < 30:
        return False
    cx0, cx1, cy0, cy1 = x0 + cols.min(), x0 + cols.max() + 1, y0 + rows.min(), y0 + rows.max() + 1
    box = a[cy0:cy1, cx0:cx1]
    col = np.median(box[yellow(box)], axis=0).astype(np.uint8)
    a[cy0 + 2:cy1 - 2, cx0 + 2:cx1 - 2] = col
    return True


def process(path, preview=None):
    octo = Path(path).read_bytes()
    unityfs = codec.octo_to_unityfs(octo)
    (prefix, info_hash), _b, nodes, raw = codec.parse_unityfs(unityfs)
    env = UnityPy.load(unityfs)
    done = []
    sf = None; cab = [n for n in nodes if n[2] & 4][0][3]
    for o in env.objects:
        if o.type.name != "Texture2D":
            continue
        t = o.read()
        if t.m_Name not in TEXTURES:
            continue
        strip, win = TEXTURES[t.m_Name]
        a = np.array(t.image.convert("RGBA"))
        ok = []
        if strip: ok.append(("strip", repaint_strip(a)))
        if win: ok.append(("sign", repaint_sign(a, win)))
        if not all(v for _, v in ok):
            sys.exit(f"{path}: {t.m_Name}: detection failed {ok}")
        t.set_image(Image.fromarray(a), target_format=TextureFormat(t.m_TextureFormat)); t.save()
        sf = o.assets_file; done.append(t.m_Name)
        if preview:
            Image.fromarray(a).resize((1024, 1024)).save(Path(preview) / f"{Path(path).name[:14]}_{t.m_Name}.png")
    if not done:
        return None, done
    ser = sf.save()
    paths, datas = [], []
    for n in nodes:
        paths.append(n[3]); datas.append(ser if n[3] == cab else raw[n[0]:n[0] + n[1]])
    return codec.unityfs_to_octo(codec.build_unityfs(prefix, info_hash, paths, datas)), done


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--assets", default=str(ROOT.parent / "Kick-Flight-Assets"))
    ap.add_argument("--preview")
    ap.add_argument("--update-catalog", action="store_true")
    a = ap.parse_args()
    cat = ROOT / "config/resources/title-minimum.json"
    raw = cat.read_bytes(); bom = raw[:3] == b"\xef\xbb\xbf"
    doc = json.loads(raw.decode("utf-8-sig"))
    OUT.mkdir(parents=True, exist_ok=True)
    if a.preview: Path(a.preview).mkdir(parents=True, exist_ok=True)
    changed = 0
    for e in doc["entries"]:
        sp = e.get("sourcePath", "")
        if e.get("kind") != "assetBundle" or not any(n.startswith("field/") for n in e.get("names", [])):
            continue
        src = (ROOT / sp).resolve() if not sp.startswith("content/") else None
        if src is None or not src.exists():
            continue
        out, done = process(src, a.preview)
        if not out:
            continue
        name = f"{src.name.split('_')[0]}_{hashlib.md5(out).hexdigest()}.bundle"
        (OUT / name).write_bytes(out)
        print(e["names"][0], done, "->", name)
        if a.update_catalog:
            e["sourcePath"] = f"content/resources/fenix-home/{name}"
        changed += 1
    if a.update_catalog:
        txt = (json.dumps(doc, indent=2, ensure_ascii=False) + "\n").replace("\n", "\r\n")   # file is CRLF
        cat.write_bytes((b"\xef\xbb\xbf" if bom else b"") + txt.encode("utf-8"))
    print("bundles written:", changed)


main()
