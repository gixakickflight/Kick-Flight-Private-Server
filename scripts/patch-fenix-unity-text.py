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

# Logo removal (2.11.807). Loading wordmark = sprite "loading_logo" (368x92) in the "Common" atlas (Texture2D path_id 3,
# 1024x2048 ASTC 6x6) at bottom-left rect (0,895); title intro wordmark = sprite "title_logo_animation" (2000x698) in the
# "Title" atlas (path_id 4, 2048x1024 ASTC 4x4) at rect (0,0). Both rects are cleared to transparent; nothing else is touched.
CLEAR_RECTS = ((3, (1024, 2048), (0, 895, 368, 92), "loading_logo (Common atlas)"),
               (4, (2048, 1024), (0, 0, 2000, 698), "title_logo_animation (Title atlas)"),
               # standalone 360x36 RGBA "Logo@2x" texture (black Kick-Flight wordmark, sprite 526): whole texture
               (117, (360, 36), (0, 0, 360, 36), "Logo@2x (standalone texture)"))
def clear_logo_rects(env):
    from PIL import Image
    done = 0
    for path_id, size, (x, y, w, h), label in CLEAR_RECTS:
        for o in env.objects:
            if o.type.name != "Texture2D" or o.path_id != path_id:
                continue
            tex = o.read()
            if (tex.m_Width, tex.m_Height) != size:
                continue
            img = tex.image.convert("RGBA"); top = img.size[1] - y - h
            img.paste(Image.new("RGBA", (w, h), (0, 0, 0, 0)), (x, top))
            tex.set_image(img, target_format=tex.m_TextureFormat)
            tex.save()
            print(label + " cleared (transparent)")
            done += 1
    return done

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
    cleared = clear_logo_rects(env)
    if total or cleared:
        p.write_bytes(env.file.save(packer="original"))

main()
