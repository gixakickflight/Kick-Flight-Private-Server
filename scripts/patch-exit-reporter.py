#!/usr/bin/env python3
"""Add the previous-session exit reporter (kf.ExitReportProvider) to a decoded APK.

The in-game error reporter dies with the process, so native crashes, low-memory kills and ANRs never reach the
server. kf.ExitReportProvider (scripts/exit-reporter/kf/ExitReportProvider.java, docstring there) runs at the next
process start and uploads Android's record of every abnormal exit (ApplicationExitInfo: reason, ANR dump, native
tombstone) plus the app's own logcat tail to <server>/diag/upload. The player does nothing.

What this does to <decoded-apk-dir> (an `apktool d -s` tree):
  * adds classes<N+1>.dex with the provider (compiled here when javac + d8 + an android.jar are available, else the
    committed scripts/exit-reporter/exit-reporter.dex is used; --rebuild forces a compile and refreshes that file);
  * declares the provider and the meta-data kf.exit_report_url=<base-url> in AndroidManifest.xml.

Usage: patch-exit-reporter.py <decoded-apk-dir> --base-url http://<server>:18080 [--rebuild]
Reading the reports on the VPS: /opt/kickflight/.local/diag-uploads/logcat-*.txt whose first line is
"KF-EXIT-REPORT v1" (grep -l KF-EXIT-REPORT); native tombstones: base64 -d | protoc --decode_raw.
"""
import argparse, os, re, shutil, subprocess, sys, tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent / "exit-reporter"
SOURCE = HERE / "kf" / "ExitReportProvider.java"
PREBUILT = HERE / "exit-reporter.dex"
PROVIDER = ('<provider android:authorities="jp.grenge.kickflight.kfexitreport" android:exported="false" '
            'android:initOrder="200" android:name="kf.ExitReportProvider"/>')


def find_sdk_tool(name: str):
    sdk = os.environ.get("ANDROID_HOME") or os.environ.get("ANDROID_SDK_ROOT") or ""
    candidates = [Path(sdk)] if sdk else []
    candidates += [Path.home() / "AppData/Local/Android/Sdk", Path("/mnt/c/Users/Tanuki/AppData/Local/Android/Sdk"),
                   Path("C:/Users/Tanuki/AppData/Local/Android/Sdk")]
    for root in candidates:
        if name == "android.jar":
            jars = sorted(root.glob("platforms/android-3[0-9]/android.jar"))
            if jars:
                return jars[-1]
        else:
            for ext in ("", ".bat", ".exe"):
                tools = sorted(root.glob(f"build-tools/*/{name}{ext}"))
                if tools:
                    return tools[-1]
    return None


def compile_dex() -> Path:
    javac = shutil.which("javac") or shutil.which("javac.exe")
    android_jar, d8 = find_sdk_tool("android.jar"), find_sdk_tool("d8")
    if not (javac and android_jar and d8):
        raise SystemExit(f"cannot compile: javac={javac} android.jar={android_jar} d8={d8}")
    work = Path(tempfile.mkdtemp(prefix="kf-exit-"))
    classes = work / "classes"
    classes.mkdir()
    subprocess.check_call([javac, "--release", "8", "-nowarn", "-cp", str(android_jar), "-d", str(classes),
                           str(SOURCE)])
    class_files = [str(p) for p in classes.rglob("*.class")]
    subprocess.check_call([str(d8), "--release", "--min-api", "21", "--lib", str(android_jar),
                           "--output", str(work)] + class_files, shell=str(d8).endswith(".bat"))
    shutil.copyfile(work / "classes.dex", PREBUILT)
    shutil.rmtree(work, ignore_errors=True)
    return PREBUILT


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("decoded")
    ap.add_argument("--base-url", required=True)
    ap.add_argument("--rebuild", action="store_true")
    args = ap.parse_args()
    decoded = Path(args.decoded).resolve()

    dex = compile_dex() if (args.rebuild or not PREBUILT.exists()) else PREBUILT
    existing = sorted(int(m.group(1) or 1) for p in decoded.glob("classes*.dex")
                      if (m := re.fullmatch(r"classes(\d*)\.dex", p.name)))
    if not existing:
        raise SystemExit(f"no classes*.dex in {decoded}")
    target = decoded / f"classes{existing[-1] + 1}.dex"
    shutil.copyfile(dex, target)

    manifest = decoded / "AndroidManifest.xml"
    text = manifest.read_text(encoding="utf-8")
    if "kf.ExitReportProvider" in text:
        raise SystemExit("AndroidManifest.xml already has kf.ExitReportProvider")
    meta = f'<meta-data android:name="kf.exit_report_url" android:value="{args.base_url.rstrip("/")}"/>'
    if text.count("</application>") != 1:
        raise SystemExit("AndroidManifest.xml: expected exactly one </application>")
    text = text.replace("</application>", f"{PROVIDER}{meta}</application>")
    manifest.write_text(text, encoding="utf-8", newline="\n")
    print(f"exit reporter: {target.name} ({dex.stat().st_size} bytes), report url {args.base_url.rstrip('/')}/diag/upload")
    return 0


if __name__ == "__main__":
    sys.exit(main())
