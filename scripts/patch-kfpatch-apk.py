#!/usr/bin/env python3
"""KF_NATIVE=1: add libkfpatch.so + its loader (kf.KfPatchProvider) to a decoded APK (`apktool d -s` tree).

  * copies <so> to lib/arm64-v8a/libkfpatch.so;
  * adds classes<N+1>.dex (javac --release 8 + d8, same toolchain as patch-exit-reporter.py);
  * declares the provider (initOrder 1000 = loads first) in AndroidManifest.xml.
Usage: patch-kfpatch-apk.py <decoded-apk-dir> --so .local/artifacts/kfpatch/libkfpatch.so
"""
import argparse, importlib.util, re, shutil, subprocess, sys, tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
SOURCE = HERE / "kfpatch-loader" / "kf" / "KfPatchProvider.java"
PROVIDER = ('<provider android:authorities="jp.grenge.kickflight.kfpatch" android:exported="false" '
            'android:initOrder="1000" android:name="kf.KfPatchProvider"/>')

spec = importlib.util.spec_from_file_location("per", HERE / "patch-exit-reporter.py")
per = importlib.util.module_from_spec(spec); spec.loader.exec_module(per)


def compile_dex(out: Path) -> Path:
    import shutil as sh
    javac = sh.which("javac"); android_jar, d8 = per.find_sdk_tool("android.jar"), per.find_sdk_tool("d8")
    if not (javac and android_jar and d8):
        raise SystemExit(f"cannot compile: javac={javac} android.jar={android_jar} d8={d8}")
    work = Path(tempfile.mkdtemp(prefix="kf-patch-"))
    (work / "classes").mkdir()
    subprocess.check_call([javac, "--release", "8", "-nowarn", "-cp", str(android_jar), "-d", str(work / "classes"), str(SOURCE)])
    cf = [str(p) for p in (work / "classes").rglob("*.class")]
    subprocess.check_call([str(d8), "--release", "--min-api", "21", "--lib", str(android_jar), "--output", str(work)] + cf)
    shutil.copyfile(work / "classes.dex", out); shutil.rmtree(work, ignore_errors=True)
    return out


def main() -> int:
    ap = argparse.ArgumentParser(); ap.add_argument("decoded"); ap.add_argument("--so", required=True)
    a = ap.parse_args(); dec = Path(a.decoded).resolve(); so = Path(a.so)
    if not so.is_file():
        raise SystemExit(f"missing {so}; run scripts/build-kfpatch.sh")
    existing = sorted(int(m.group(1) or 1) for p in dec.glob("classes*.dex") if (m := re.fullmatch(r"classes(\d*)\.dex", p.name)))
    if not existing:
        raise SystemExit(f"no classes*.dex in {dec}")
    target = dec / f"classes{existing[-1] + 1}.dex"
    compile_dex(target)
    (dec / "lib/arm64-v8a").mkdir(parents=True, exist_ok=True)
    shutil.copyfile(so, dec / "lib/arm64-v8a/libkfpatch.so")
    mf = dec / "AndroidManifest.xml"; t = mf.read_text(encoding="utf-8")
    if "kf.KfPatchProvider" in t or t.count("</application>") != 1:
        raise SystemExit("AndroidManifest.xml: already patched or unexpected shape")
    mf.write_text(t.replace("</application>", PROVIDER + "</application>"), encoding="utf-8", newline="\n")
    print(f"kfpatch: {target.name}, lib/arm64-v8a/libkfpatch.so ({so.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
