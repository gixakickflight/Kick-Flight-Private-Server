#!/usr/bin/env python3
"""Replace the visible '(c)Grenge, Inc.' copyright text inside assets/bin/Data/data.unity3d.

Usage: patch-fenix-unity-text.py <decoded-apk-dir>   (run with the UnityPy venv python)
Handles raw serialized strings (length prefix + 4-byte alignment) and TextAsset JSON.
"""
import struct, sys
from pathlib import Path
import UnityPy

OLD = "©Grenge, Inc.".encode()
NEW = "©Fenix".encode()

def fix_raw(data: bytes):
    out = bytearray(); i = 0; n = 0
    while True:
        j = data.find(OLD, i)
        if j < 4 or struct.unpack_from("<i", data, j - 4)[0] != len(OLD):
            if j < 0:
                break
            out += data[i:j + len(OLD)]; i = j + len(OLD); continue
        pad = (4 - len(OLD) % 4) % 4
        out += data[i:j - 4] + struct.pack("<i", len(NEW)) + NEW + b"\0" * ((4 - len(NEW) % 4) % 4)
        i = j + len(OLD) + pad; n += 1
    out += data[i:]
    return bytes(out), n

# Loading screen logo: sprite "loading_logo" (368x92, black "Kick-Flight" wordmark drawn over a yellow box) lives in the
# "Common" SpriteAtlas texture (Texture2D 1024x2048, ASTC 6x6) at bottom-left rect (0,895). Replace it with a black Fenix silhouette.
LOGO_RECT = (0, 895, 368, 92)   # x, y(bottom-origin), w, h
def patch_loading_logo(env, root):
    from PIL import Image
    logo_path = Path(__file__).resolve().parent.parent / "assets/branding/fenix/fenix-logo-1024.png"
    for o in env.objects:
        if o.type.name != "Texture2D" or o.path_id != 3:
            continue
        tex = o.read()
        if (tex.m_Width, tex.m_Height) != (1024, 2048):
            continue
        img = tex.image.convert("RGBA"); H = img.size[1]
        x, y, w, h = LOGO_RECT; top = H - y - h
        logo = Image.open(logo_path).convert("RGBA"); a = logo.getchannel("A")
        logo = logo.crop(a.point(lambda v: 255 if v > 8 else 0).getbbox())
        sc = min(w * 0.92 / logo.width, h * 0.92 / logo.height)
        logo = logo.resize((max(1, round(logo.width * sc)), max(1, round(logo.height * sc))), Image.LANCZOS)
        sil = Image.new("RGBA", logo.size, (0, 0, 0, 0)); sil.putalpha(logo.getchannel("A"))   # black silhouette, alpha kept
        img.paste(Image.new("RGBA", (w, h), (0, 0, 0, 0)), (x, top))
        img.paste(sil, (x + (w - sil.width) // 2, top + (h - sil.height) // 2), sil)
        tex.set_image(img, target_format=tex.m_TextureFormat)
        tex.save()
        print("loading_logo replaced in Common atlas")
        return True
    return False

def main():
    root = Path(sys.argv[1]); p = root / "assets/bin/Data/data.unity3d"
    env = UnityPy.load(str(p)); total = 0
    for o in env.objects:
        if o.type.name == "TextAsset":
            d = o.read(); s = d.m_Script
            raw = s.encode("utf-8", "surrogateescape") if isinstance(s, str) else bytes(s)
            if OLD in raw:
                c = raw.count(OLD); d.m_Script = raw.replace(OLD, NEW).decode("utf-8", "surrogateescape")
                d.save(); total += c
        elif o.type.name == "MonoBehaviour":
            raw = o.get_raw_data()
            if OLD in raw:
                new, n = fix_raw(bytes(raw))
                if n:
                    o.set_raw_data(new); total += n
    print(f"copyright replacements: {total}")
    logo = patch_loading_logo(env, root)
    if total or logo:
        p.write_bytes(env.file.save(packer="original"))

main()
