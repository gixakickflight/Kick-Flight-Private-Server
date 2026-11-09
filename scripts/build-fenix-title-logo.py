#!/usr/bin/env python3
"""Fenix rebrand: replace the Kick Flight title logo texture in the zh localize donor bundle.

Writes one override per language under .local/localize-overrides/<lang>/ui/localize/zh/title/title_logo.unity3d
(Octo bytes, donor with only the Texture2D changed, same 671x240 canvas and ASTC RGBA 6x6 format). Then run
scripts/build-localize-bundles.py (UnityPy 1.25.3, .local/assets-venv) to regenerate the per-language clones, and
scripts/build-title-resource-catalog.py after bumping the revision in config/resources/title-minimum.json.
"""
import importlib.machinery, importlib.util, json, sys
from pathlib import Path
import UnityPy
from PIL import Image
from UnityPy.enums import TextureFormat

ROOT = Path(__file__).resolve().parent.parent
LANGS = ["en", "es", "ja", "pt", "ko", "fr", "de"]
NAME = "ui/localize/zh/title/title_logo.unity3d"
LOGO = ROOT / "assets/branding/fenix/fenix-logo-shadow-1024.png"

l = importlib.machinery.SourceFileLoader("c", str(ROOT / "scripts/build-action-asset-bundles.py"))
s = importlib.util.spec_from_loader(l.name, l); codec = importlib.util.module_from_spec(s); l.exec_module(codec)

doc = json.loads((ROOT / "config/resources/title-minimum.json").read_text(encoding="utf-8-sig"))
src = None
for e in doc["entries"]:
    if NAME in e.get("names", []) and not e.get("id", "").startswith("localize-"):
        src = e["sourcePath"]
assert src, "donor entry not found"
donor = (ROOT / src).resolve()
print("donor", donor)
octo = donor.read_bytes()
unityfs = codec.octo_to_unityfs(octo)
(prefix, info_hash), _b, nodes, raw = codec.parse_unityfs(unityfs)
env = UnityPy.load(unityfs)
tex_objs = [o for o in env.objects if o.type.name == "Texture2D"]
assert len(tex_objs) == 1
obj = tex_objs[0]; tex = obj.read()
W, H = tex.m_Width, tex.m_Height
logo = Image.open(LOGO).convert("RGBA"); logo = logo.crop(logo.getchannel("A").point(lambda v: 255 if v > 8 else 0).getbbox())
sc = min(W * 0.98 / logo.width, H * 0.98 / logo.height)
logo = logo.resize((max(1, round(logo.width * sc)), max(1, round(logo.height * sc))), Image.LANCZOS)
canvas = Image.new("RGBA", (W, H), (0, 0, 0, 0))
canvas.paste(logo, ((W - logo.width) // 2, (H - logo.height) // 2), logo)
fmt = tex.m_TextureFormat
tex.set_image(canvas, target_format=TextureFormat(fmt))
tex.save()
ser_node = [n for n in nodes if n[2] & 4][0]
cab = ser_node[3]
sf = obj.assets_file
ser_bytes = sf.save()
paths, datas = [], []
for n in nodes:
    paths.append(n[3])
    datas.append(ser_bytes if n[3] == cab else raw[n[0]:n[0] + n[1]])
out = codec.unityfs_to_octo(codec.build_unityfs(prefix, info_hash, paths, datas))
for lang in LANGS:
    p = ROOT / ".local/localize-overrides" / lang / NAME
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_bytes(out)
print("wrote overrides", len(out), "bytes; texture", W, H, fmt)
