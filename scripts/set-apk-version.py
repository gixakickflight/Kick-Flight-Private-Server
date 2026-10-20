#!/usr/bin/env python3
"""Stamp the private-server client version on an apktool-decoded base.apk (run between `apktool d` and `apktool b`).

base.apk is versionName 2.11.0 / versionCode 55. apktool keeps both in apktool.yml (`versionInfo:`) and writes them
back as aapt --version-name/--version-code on `apktool b`, so apktool.yml is the source of truth; any
android:versionCode/versionName left in the decoded AndroidManifest.xml is rewritten too so the two never disagree.

What reads the version (pristine RE, 2026-10-05): Application.version is the x-app-application-version / User-Agent
header (the server ignores it), the title screen label, the Photon room "version" property (PhotonUtil.IsRoomJoinable
compares only the major.minor `\\d+\\.\\d+` part, so 2.11.0 and 2.11.1 still join each other's rooms) and local replay
files (ReplayJsonFile/ReplayPlayFile.CompareAppVersion need the exact string, so replays saved by 2.11.0 stop being
playable after the update). The Photon AppVersion comes from PhotonServerSettings, not from Application.version.

A "-diag" suffix (--diag; the build recipes pass it for KF_DIAG=1 builds) marks KFDIAG builds so the server can let
them through hard maintenance on x-app-application-version. It is harmless on the client: IsRoomJoinable regexes out
`\\d+\\.\\d+`, ReplayUtility.ParseAppVersionNumbers only String.Splits on '.' (no number parsing) and every other
reader (User-Agent, title label, Octo ErrorReport, SRDebugger, IAP gameVersion) just displays or forwards the string.

    python3 scripts/set-apk-version.py <decoded dir> [--diag] [--version-name 2.11.1] [--version-code 56]
Defaults come from KF_VERSION_NAME / KF_VERSION_CODE, else the constants below.
"""
import argparse
import os
import re
import sys
from pathlib import Path

VERSION_NAME = "2.11.4"
VERSION_CODE = 59
BASE_VERSION_CODE = 55  # base.apk


def stamp(decoded: Path, name: str, code: int) -> None:
    if code <= BASE_VERSION_CODE:
        raise SystemExit(f"versionCode {code} must be above base.apk's {BASE_VERSION_CODE} to install as an update")
    yml = decoded / "apktool.yml"
    text = yml.read_text(encoding="utf-8")
    text, n_code = re.subn(r"(?m)^([ \t]*versionCode:[ \t]*)(['\"]?)\d+\2[ \t]*$", lambda m: f"{m.group(1)}{m.group(2)}{code}{m.group(2)}", text)
    text, n_name = re.subn(r"(?m)^([ \t]*versionName:[ \t]*).*$", lambda m: f"{m.group(1)}{name}", text)
    if n_code != 1 or n_name != 1:
        raise SystemExit(f"{yml}: expected one versionCode and one versionName line, found {n_code}/{n_name}")
    yml.write_text(text, encoding="utf-8")

    manifest = decoded / "AndroidManifest.xml"
    xml = manifest.read_text(encoding="utf-8")
    xml = re.sub(r'android:versionCode="\d+"', f'android:versionCode="{code}"', xml)
    xml = re.sub(r'android:versionName="[^"]*"', f'android:versionName="{name}"', xml)
    manifest.write_text(xml, encoding="utf-8")
    print(f"apk version: versionName {name}, versionCode {code}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("decoded", type=Path, help="apktool output directory (contains apktool.yml)")
    parser.add_argument("--version-name", default=os.environ.get("KF_VERSION_NAME") or VERSION_NAME)
    parser.add_argument("--version-code", type=int, default=int(os.environ.get("KF_VERSION_CODE") or VERSION_CODE))
    parser.add_argument("--diag", action="store_true", help='append "-diag" to versionName (KF_DIAG=1 builds)')
    args = parser.parse_args()
    name = f"{args.version_name}-diag" if args.diag else args.version_name
    stamp(args.decoded, name, args.version_code)
    return 0


if __name__ == "__main__":
    sys.exit(main())
