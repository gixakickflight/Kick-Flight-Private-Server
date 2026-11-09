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
    if total:
        p.write_bytes(env.file.save(packer="original"))

main()
