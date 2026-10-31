#!/usr/bin/env python3
"""Validate Android APK artifact integrity, architecture, signing, and SHA-256."""

from __future__ import annotations

import argparse
import hashlib
from pathlib import Path
import struct
import sys
import zipfile

DEFAULT_MIN_APK_BYTES = 50_000_000  # 50 MB
REQUIRED_ZIP_ENTRIES = ("AndroidManifest.xml", "classes.dex", "lib/arm64-v8a/libil2cpp.so")
ARM64_ELF_EMACHINE = 183  # EM_AARCH64


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().lower()


def validate_apk(path: Path, expected_sha256: str | None = None, min_bytes: int = DEFAULT_MIN_APK_BYTES) -> None:
    if not path.is_file():
        sys.exit(f"APK file does not exist: {path}")

    size = path.stat().st_size
    if size < min_bytes:
        sys.exit(f"APK file size too small ({size} bytes < {min_bytes} bytes): {path}")

    if not zipfile.is_zipfile(path):
        sys.exit(f"File is not a valid zip/apk archive: {path}")

    with zipfile.ZipFile(path) as zf:
        namelist = set(zf.namelist())
        for req in REQUIRED_ZIP_ENTRIES:
            if req not in namelist:
                sys.exit(f"APK archive missing required member: {req}")

        with zf.open("lib/arm64-v8a/libil2cpp.so") as so_file:
            il2cpp_head = so_file.read(20)
        if len(il2cpp_head) < 20 or il2cpp_head[:4] != b"\x7fELF":
            sys.exit("lib/arm64-v8a/libil2cpp.so missing ELF magic header")
        if il2cpp_head[4] != 2:
            sys.exit("lib/arm64-v8a/libil2cpp.so is not a 64-bit ELF")
        e_machine = struct.unpack("<H", il2cpp_head[18:20])[0]
        if e_machine != ARM64_ELF_EMACHINE:
            sys.exit(f"lib/arm64-v8a/libil2cpp.so machine type {e_machine} != {ARM64_ELF_EMACHINE} (AArch64)")

        has_meta = any(n.startswith("META-INF/") for n in namelist)
        if not has_meta:
            sys.exit("APK archive missing META-INF signature files")

    if expected_sha256:
        actual_sha = sha256_file(path)
        if actual_sha != expected_sha256.strip().lower():
            sys.exit(f"APK SHA-256 mismatch: expected {expected_sha256.lower()}, got {actual_sha}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("apk_path", type=Path, help="Path to APK file")
    parser.add_argument("--expected-sha256", help="Expected SHA-256 checksum (hex)")
    parser.add_argument("--min-bytes", type=int, default=DEFAULT_MIN_APK_BYTES, help="Minimum file size in bytes")
    args = parser.parse_args()

    validate_apk(args.apk_path, args.expected_sha256, args.min_bytes)
    print(f"Verified APK artifact: {args.apk_path} ({args.apk_path.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
