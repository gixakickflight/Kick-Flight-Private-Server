#!/usr/bin/env python3
"""Opt-in (KF_PUSH=1): point the client's FCM setup at OUR Firebase project and report the FCM token to OUR server.

Usage: patch-firebase-push.py <decoded-apk-dir> --base-url http://<server>:18080 --firebase-config <google-services.json>
                              [--original-manifest-dir ...] [--rebuild]
Operates on an `apktool d -s` tree (resources decoded, smali NOT decoded). Steps:
  a. res/values/strings.xml: google_app_id, gcm_defaultSenderId, google_api_key, google_crash_reporting_api_key,
     project_id, google_storage_bucket <- config. firebase_database_url and default_web_client_id are left alone.
     assets/google-services-desktop.json (if present): project_info.{project_number,project_id,storage_bucket},
     client[].client_info.mobilesdk_app_id, client[].api_key[].current_key are rewritten; structure kept.
  b. classes2.dex (io.repro.android.Repro): round-trip through apktool/smali; at the start of
     setPushRegistrationID(String) / setUserID(String) insert `invoke-static {p0}, PushRegistrar->onToken/onUserId`
     (static methods, p0 = the String; no new registers) then fall through to the original code.
  c. PushRegistrar (scripts/push-registrar/.../PushRegistrar.java, Java -> javac + d8, prebuilt dex committed as
     scripts/push-registrar/push-registrar.dex) added as classes<N+1>.dex; manifest meta-data kf.push_url=<base-url>.
Fails hard on bad config, wrong package, missing/unknown signatures, or an already patched tree. Nothing is written
until all checks pass.
"""
import argparse, json, os, re, shutil, subprocess, sys, tempfile, zipfile
from pathlib import Path
from xml.sax.saxutils import escape

PACKAGE = "jp.grenge.kickflight"
HERE = Path(__file__).resolve().parent / "push-registrar"
SOURCE = HERE / "jp/kickflight/push/PushRegistrar.java"
PREBUILT = HERE / "push-registrar.dex"
MARKER = b"Ljp/kickflight/push/PushRegistrar;"
REPRO_SMALI = "io/repro/android/Repro.smali"
HOOKS = {
    "setPushRegistrationID": "onToken",
    "setUserID": "onUserId",
}


def die(msg):
    raise SystemExit(f"patch-firebase-push: {msg}")


def load_config(path: Path) -> dict:
    try:
        cfg = json.loads(path.read_text(encoding="utf-8"))
        info = cfg["project_info"]
        client = next(c for c in cfg["client"]
                      if c["client_info"]["android_client_info"]["package_name"] == PACKAGE)
        values = {
            "google_app_id": client["client_info"]["mobilesdk_app_id"],
            "gcm_defaultSenderId": info["project_number"],
            "google_api_key": client["api_key"][0]["current_key"],
            "project_id": info["project_id"],
            "google_storage_bucket": info["storage_bucket"],
        }
    except (OSError, ValueError, KeyError, IndexError, StopIteration, TypeError) as e:
        die(f"invalid firebase config {path} ({type(e).__name__}: missing field or package {PACKAGE})")
    values["google_crash_reporting_api_key"] = values["google_api_key"]
    for k, v in values.items():
        if not isinstance(v, str) or not v.strip():
            die(f"firebase config field for {k} is empty")
    return values


def find_sdk_tool(name):
    sdk = os.environ.get("ANDROID_HOME") or os.environ.get("ANDROID_SDK_ROOT") or ""
    roots = [Path(sdk)] if sdk else []
    roots += [Path.home() / "Android/Sdk", Path.home() / "AppData/Local/Android/Sdk"]
    for root in roots:
        if name == "android.jar":
            jars = sorted(root.glob("platforms/android-3[0-9]/android.jar"))
            if jars:
                return jars[-1]
        else:
            for ext in ("", ".bat", ".exe"):
                t = sorted(root.glob(f"build-tools/*/{name}{ext}"))
                if t:
                    return t[-1]
    return None


def compile_dex() -> Path:
    javac = shutil.which("javac") or shutil.which("javac.exe")
    jar, d8 = find_sdk_tool("android.jar"), find_sdk_tool("d8")
    if not (javac and jar and d8):
        die(f"cannot compile: javac={javac} android.jar={jar} d8={d8}")
    work = Path(tempfile.mkdtemp(prefix="kf-push-"))
    (work / "classes").mkdir()
    subprocess.check_call([javac, "--release", "8", "-nowarn", "-cp", str(jar), "-d", str(work / "classes"), str(SOURCE)])
    cls = [str(p) for p in (work / "classes").rglob("*.class")]
    subprocess.check_call([str(d8), "--release", "--min-api", "21", "--lib", str(jar), "--output", str(work)] + cls,
                          shell=str(d8).endswith(".bat"))
    shutil.copyfile(work / "classes.dex", PREBUILT)
    shutil.rmtree(work, ignore_errors=True)
    return PREBUILT


def patch_strings(decoded: Path, values: dict) -> str:
    path = decoded / "res/values/strings.xml"
    text = path.read_text(encoding="utf-8")
    for key, val in values.items():
        pat = re.compile(rf'(<string\b[^>]*\bname="{re.escape(key)}"[^>]*>)[^<]*(</string>)')
        if len(pat.findall(text)) != 1:
            die(f"strings.xml: expected exactly one <string name=\"{key}\">")
        text = pat.sub(lambda m: m.group(1) + escape(val) + m.group(2), text)
    return text


def patch_desktop_json(decoded: Path, values: dict):
    path = decoded / "assets/google-services-desktop.json"
    if not path.exists():
        return None, []
    data = json.loads(path.read_text(encoding="utf-8"))
    done = []
    pi = data.get("project_info", {})
    for src, dst in (("gcm_defaultSenderId", "project_number"), ("project_id", "project_id"),
                     ("google_storage_bucket", "storage_bucket")):
        if dst in pi:
            pi[dst] = values[src]; done.append(f"project_info.{dst}")
    for c in data.get("client", []):
        ci = c.get("client_info", {})
        if "mobilesdk_app_id" in ci:
            ci["mobilesdk_app_id"] = values["google_app_id"]; done.append("client_info.mobilesdk_app_id")
        for k in c.get("api_key", []):
            if "current_key" in k:
                k["current_key"] = values["google_api_key"]; done.append("api_key.current_key")
    return (path, json.dumps(data, indent=2) + "\n"), done


def patch_repro_dex(decoded: Path) -> bytes:
    dex = decoded / "classes2.dex"
    if not dex.exists():
        die("classes2.dex not found")
    apktool = shutil.which("apktool")
    if not apktool:
        die("apktool not on PATH")
    work = Path(tempfile.mkdtemp(prefix="kf-push-dex-"))
    try:
        orig_manifest = decoded / "original/AndroidManifest.xml"
        if not orig_manifest.exists():
            die("original/AndroidManifest.xml missing (decode with apktool d -s)")
        mini = work / "mini.apk"
        with zipfile.ZipFile(mini, "w") as z:
            z.write(dex, "classes.dex")
            z.write(orig_manifest, "AndroidManifest.xml")
        subprocess.check_call([apktool, "d", "-f", "--no-res", "-o", str(work / "d"), str(mini)],
                              stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
        sm = work / "d/smali" / REPRO_SMALI
        if not sm.exists():
            die(f"{REPRO_SMALI} not found in classes2.dex")
        text = sm.read_text(encoding="utf-8")
        for method, hook in HOOKS.items():
            pat = re.compile(rf"(\.method public static {method}\(Ljava/lang/String;\)V\n\s*\.locals \d+\n)")
            if len(pat.findall(text)) != 1:
                die(f"Repro.{method}(String)V static signature with .locals not found exactly once")
            call = f"\n    invoke-static {{p0}}, Ljp/kickflight/push/PushRegistrar;->{hook}(Ljava/lang/String;)V\n"
            text = pat.sub(lambda m: m.group(1) + call, text)
        sm.write_text(text, encoding="utf-8")
        subprocess.check_call([apktool, "b", "-o", str(work / "out.apk"), str(work / "d")],
                              stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
        with zipfile.ZipFile(work / "out.apk") as z:
            return z.read("classes.dex")
    finally:
        shutil.rmtree(work, ignore_errors=True)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("decoded")
    ap.add_argument("--base-url", required=True)
    ap.add_argument("--firebase-config", required=True)
    ap.add_argument("--rebuild", action="store_true")
    a = ap.parse_args()
    decoded = Path(a.decoded).resolve()
    cfg_path = Path(a.firebase_config)
    if not cfg_path.is_file():
        die(f"firebase config not found: {cfg_path}")
    values = load_config(cfg_path)
    base = a.base_url.rstrip("/")
    if not base.startswith("http"):
        die("--base-url must be http(s)://")

    dex_files = sorted(decoded.glob("classes*.dex"))
    if not dex_files:
        die(f"no classes*.dex in {decoded}")
    for d in dex_files:
        if MARKER in d.read_bytes():
            die(f"already patched ({d.name} contains PushRegistrar)")
    manifest = decoded / "AndroidManifest.xml"
    mtext = manifest.read_text(encoding="utf-8")
    if "kf.push_url" in mtext:
        die("AndroidManifest.xml already has kf.push_url")
    if mtext.count("</application>") != 1:
        die("AndroidManifest.xml: expected exactly one </application>")

    strings_text = patch_strings(decoded, values)
    desktop, desktop_fields = patch_desktop_json(decoded, values)
    new_classes2 = patch_repro_dex(decoded)
    push_dex = compile_dex() if (a.rebuild or not PREBUILT.exists()) else PREBUILT

    nums = sorted(int(m.group(1) or 1) for p in dex_files if (m := re.fullmatch(r"classes(\d*)\.dex", p.name)))
    target = decoded / f"classes{nums[-1] + 1}.dex"
    # all checks passed: write
    (decoded / "res/values/strings.xml").write_text(strings_text, encoding="utf-8")
    if desktop:
        desktop[0].write_text(desktop[1], encoding="utf-8")
    (decoded / "classes2.dex").write_bytes(new_classes2)
    shutil.copyfile(push_dex, target)
    meta = f'<meta-data android:name="kf.push_url" android:value="{escape(base)}"/>'
    manifest.write_text(mtext.replace("</application>", meta + "</application>"), encoding="utf-8", newline="\n")
    print(f"firebase push: strings {sorted(values)} replaced; default_web_client_id and firebase_database_url untouched")
    print(f"  desktop json: {'fields ' + ', '.join(sorted(set(desktop_fields))) if desktop else 'absent'}")
    print(f"  Repro hooks in classes2.dex; PushRegistrar in {target.name}; kf.push_url={base}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
