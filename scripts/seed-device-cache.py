#!/usr/bin/env python3
"""
seed-device-cache.py
Seeds the preserved Kick-Flight Octo assets directly into the Android app's
local cache directory (/data/data/world.global.fenix/files/octo/v1/1/) via ADB.
This completely eliminates initial bundle download times over LAN.
"""

from __future__ import annotations

import argparse
import datetime
import io
import os
from pathlib import Path
import re
import struct
import subprocess
import sys
import tarfile
import time


def get_dotnet_ticks() -> bytes:
    # .NET DateTime.UtcNow.Ticks: 1 tick = 100ns, epoch 0001-01-01
    epoch = datetime.datetime(1, 1, 1, tzinfo=datetime.timezone.utc)
    now = datetime.datetime.now(datetime.timezone.utc)
    ticks = int((now - epoch).total_seconds() * 10_000_000)
    return struct.pack("<Q", ticks)


def main():
    parser = argparse.ArgumentParser(description="Seed Kick-Flight Octo cache to Android device via ADB")
    parser.add_argument("-s", "--device", help="ADB device serial (e.g. emulator-5554 or 3d3dc71d)")
    parser.add_argument("--assets-dir", default="../Kick-Flight-Assets/octo_sorted", help="Path to octo_sorted directory")
    parser.add_argument("--skip-tar", action="store_true", help="Reuse existing octo_cache.tar if present")
    args = parser.parse_args()

    repo_root = Path(__file__).resolve().parent.parent
    assets_dir = (repo_root / args.assets_dir).resolve()
    if not assets_dir.is_dir():
        print(f"Error: assets directory not found: {assets_dir}")
        sys.exit(1)

    # 1. Check ADB connection
    adb_base = ["adb"]
    if args.device:
        adb_base.extend(["-s", args.device])

    try:
        devices_out = subprocess.check_output(adb_base + ["devices"]).decode("utf-8")
    except Exception as e:
        print(f"Error running adb: {e}")
        sys.exit(1)

    print("Connected ADB devices:\n" + devices_out)

    # Try restarting adb as root
    print("Attempting to get root access via adb root...")
    subprocess.run(adb_base + ["root"], check=False)
    time.sleep(2)

    # Get app uid and gid
    try:
        # `pm clear` may remove files/ but recreates the package directory with
        # the correct per-emulator app UID.  Read that stable parent so a clean
        # reset never falls back to ownership from a different AVD image.
        id_out = subprocess.check_output(adb_base + ["shell", "stat -c '%u %g' /data/data/world.global.fenix 2>/dev/null || echo ''"]).decode("utf-8").strip()
        if id_out:
            uid, gid = id_out.split()
            print(f"Detected app ownership: uid={uid}, gid={gid}")
        else:
            print("Error: could not determine app ownership after package reset")
            sys.exit(1)
    except Exception:
        print("Error: could not determine app ownership after package reset")
        sys.exit(1)

    tar_path = repo_root / "octo_cache.tar"
    if not (args.skip_tar and tar_path.exists()):
        print(f"Building cache tarball at {tar_path}...")
        meta_bytes = get_dotnet_ticks()
        file_pattern = re.compile(r"^([0-9A-Fa-f]+)_([0-9a-fA-F]{32})\.")

        with tarfile.open(tar_path, "w") as tar:
            count = 0
            # content/resources holds bundles built locally (scripts/build-action-asset-bundles.py) that use the
            # same <hex('A'+objectName)>_<md5>.bundle naming as the preserved Octo files
            local_dir = repo_root / "content" / "resources"
            for root, _, files in list(os.walk(assets_dir)) + (list(os.walk(local_dir)) if local_dir.is_dir() else []):
                for f in files:
                    m = file_pattern.match(f)
                    if not m:
                        continue
                    hex_prefix, md5_hash = m.group(1), m.group(2).lower()
                    bucket = hex_prefix[-1].upper()

                    full_path = os.path.join(root, f)
                    file_size = os.path.getsize(full_path)

                    target_dir = f"octo/v1/1/{bucket}/{hex_prefix}"
                    data_relpath = f"{target_dir}/{md5_hash}"
                    meta_relpath = f"{target_dir}/.meta"

                    # Add data file
                    tinfo = tarfile.TarInfo(name=data_relpath)
                    tinfo.size = file_size
                    tinfo.mtime = int(time.time())
                    tinfo.uname = "u0_a207"
                    tinfo.gname = "u0_a207"
                    with open(full_path, "rb") as sf:
                        tar.addfile(tinfo, sf)

                    # Add .meta file
                    minfo = tarfile.TarInfo(name=meta_relpath)
                    minfo.size = len(meta_bytes)
                    minfo.mtime = int(time.time())
                    minfo.uname = "u0_a207"
                    minfo.gname = "u0_a207"
                    tar.addfile(minfo, io.BytesIO(meta_bytes))

                    count += 1
                    if count % 500 == 0:
                        print(f"Packed {count} assets...")

            # Add octocacheevai database if present
            pdb_path = repo_root / "config" / "resources" / "octocacheevai.bin"
            if pdb_path.exists():
                pdb_bytes = pdb_path.read_bytes()
                pdb_info = tarfile.TarInfo(name="octo/pdb/1/12345/octocacheevai")
                pdb_info.size = len(pdb_bytes)
                pdb_info.mtime = int(time.time())
                pdb_info.uname = "u0_a207"
                pdb_info.gname = "u0_a207"
                tar.addfile(pdb_info, io.BytesIO(pdb_bytes))
                print(f"Added octo/pdb/1/12345/octocacheevai ({len(pdb_bytes)} bytes)")

            print(f"Successfully packed {count} assets into {tar_path} ({tar_path.stat().st_size / (1024*1024):.1f} MB)")

    # 2. Push tarball to device /data/local/tmp/
    print("Pushing tarball to /data/local/tmp/octo_cache.tar...")
    subprocess.check_call(adb_base + ["push", str(tar_path), "/data/local/tmp/octo_cache.tar"])
    subprocess.check_call(adb_base + ["shell", "chmod 666 /data/local/tmp/octo_cache.tar"])

    # 3. Extract into /data/data/world.global.fenix/files/
    print("Extracting cache into app directory...")
    # Test if adb has root access
    is_root = False
    try:
        whoami = subprocess.check_output(adb_base + ["shell", "whoami"]).decode("utf-8").strip()
        is_root = (whoami == "root")
    except Exception:
        pass

    if is_root:
        print("Using root extraction...")
        extract_cmd = (
            f"mkdir -p /data/data/world.global.fenix/files/octo/v1/1 "
            f"/data/data/world.global.fenix/files/octo/pdb/1/12345 && "
            f"tar -xf /data/local/tmp/octo_cache.tar -C /data/data/world.global.fenix/files/ && "
            f"chown -R {uid}:{gid} /data/data/world.global.fenix/files/octo && "
            f"rm -f /data/local/tmp/octo_cache.tar"
        )
        subprocess.check_call(adb_base + ["shell", extract_cmd])
    else:
        print("Non-root device: checking run-as world.global.fenix...")
        try:
            run_as_test = subprocess.check_output(adb_base + ["shell", "run-as world.global.fenix id"]).decode("utf-8")
            print(f"run-as available: {run_as_test.strip()}")
            extract_cmd = (
                f"run-as world.global.fenix mkdir -p /data/data/world.global.fenix/files/octo/v1/1 "
                f"/data/data/world.global.fenix/files/octo/pdb/1/12345 && "
                f"run-as world.global.fenix tar -xf /data/local/tmp/octo_cache.tar -C /data/data/world.global.fenix/files/ && "
                f"rm -f /data/local/tmp/octo_cache.tar"
            )
            subprocess.check_call(adb_base + ["shell", extract_cmd])
        except subprocess.CalledProcessError as e:
            print(f"Error: Neither root nor run-as succeeded: {e}")
            sys.exit(1)

    print("Cache successfully seeded onto device!")


if __name__ == "__main__":
    main()
