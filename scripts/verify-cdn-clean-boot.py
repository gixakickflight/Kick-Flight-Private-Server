#!/usr/bin/env python3
"""Verify a fresh, unseeded Kick-Flight install downloads its Octo cache from CDN.

Creates an isolated AVD under .local/cdn-clean/<run-id>/avd-home. It never wipes,
boots, or edits an AVD from the user's normal Android AVD directory.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import signal
import socket
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


REPO = Path(__file__).resolve().parents[1]
PACKAGE = os.environ.get("KF_PACKAGE_ID", "world.global.fenix")
LAUNCH_ACTIVITY = f"{PACKAGE}/com.google.firebase.MessagingUnityPlayerActivity"
API_PORT = 18080
CLIENT_BASE = "http://10.0.2.2:18080"
LOCAL_BASE = f"http://127.0.0.1:{API_PORT}"
DEFAULT_APK = REPO / ".local/artifacts/KickFlight-2.11.0-cdn-clean-20260924.apk"
CAPTURE_DEFAULT = REPO / "src/KickFlight.BootstrapApi/captures"


class VerifyError(RuntimeError):
    pass


def say(message: str) -> None:
    print(message, flush=True)


def run(args: list[str], *, timeout: int = 30, env: dict[str, str] | None = None,
        input_text: str | None = None, check: bool = True) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(args, text=True, input=input_text, capture_output=True,
                            timeout=timeout, env=env)
    if check and result.returncode:
        detail = (result.stderr or result.stdout).strip()[-2500:]
        raise VerifyError(f"Comando falló ({result.returncode}): {' '.join(args)}\n{detail}")
    return result


def resolve_sdk() -> tuple[Path, str, str, str]:
    candidates: list[Path] = []
    for key in ("ANDROID_SDK_ROOT", "ANDROID_HOME"):
        value = os.environ.get(key)
        if value:
            candidates.append(Path(value).expanduser())
    candidates.extend([
        REPO / ".local/android-sdk",
        Path.home() / "Library/Android/sdk",
        Path("/opt/homebrew/share/android-commandlinetools"),
    ])

    sdk = next((p.resolve() for p in candidates if (p / "platform-tools/adb").exists()), None)
    adb = shutil.which("adb")
    emulator = shutil.which("emulator")
    avdmanager = shutil.which("avdmanager")
    if sdk:
        adb = adb or str(sdk / "platform-tools/adb")
        emulator = emulator or str(sdk / "emulator/emulator")
        cmdline = sdk / "cmdline-tools"
        manager_candidates = [cmdline / "latest/bin/avdmanager"]
        if cmdline.exists():
            manager_candidates.extend(sorted(cmdline.glob("*/bin/avdmanager"), reverse=True))
        manager_candidates.append(sdk / "tools/bin/avdmanager")
        avdmanager = avdmanager or next((str(p) for p in manager_candidates if p.is_file()), None)
    if not adb or not emulator or not avdmanager:
        raise VerifyError("Faltan adb, emulator o avdmanager. Instala Android command-line tools y define ANDROID_SDK_ROOT.")
    sdk = sdk or Path(adb).resolve().parent.parent
    return sdk, adb, emulator, avdmanager


def choose_image(sdk: Path) -> str:
    preferred = "system-images;android-35;google_apis;arm64-v8a"
    if (sdk / "system-images/android-35/google_apis/arm64-v8a/package.xml").exists():
        return preferred
    installed: list[tuple[int, str]] = []
    root = sdk / "system-images"
    for abi_dir in root.glob("android-*/google_apis/arm64-v8a"):
        match = re.fullmatch(r"android-(\d+)", abi_dir.parent.parent.name)
        if match and (abi_dir / "package.xml").exists():
            installed.append((int(match.group(1)), f"system-images;{abi_dir.parent.parent.name};google_apis;arm64-v8a"))
    if installed:
        return max(installed)[1]
    raise VerifyError("No hay una imagen arm64 Google APIs instalada. Instala system-images;android-35;google_apis;arm64-v8a y reintenta.")


def http_request(method: str, path: str, *, body: bytes | None = None, timeout: int = 15) -> tuple[int, bytes, dict[str, str]]:
    request = Request(LOCAL_BASE + path, data=body, method=method,
                      headers={"Host": "10.0.2.2", "Connection": "close"})
    try:
        with urlopen(request, timeout=timeout) as response:
            return response.status, response.read(), dict(response.headers.items())
    except HTTPError as error:
        return error.code, error.read(), dict(error.headers.items())
    except (URLError, TimeoutError, OSError) as error:
        raise VerifyError(f"No se pudo consultar {method} {path}: {error}") from error


def api_preflight(catalog: list[dict[str, Any]]) -> dict[str, Any]:
    status, body, headers = http_request("POST", "/boot/index", body=bytes(32))
    app_status = next((v for k, v in headers.items() if k.lower() == "x-app-status-code"), "")
    if status != 200 or app_status != "0" or not body:
        raise VerifyError(f"Gate POST /boot/index Host 10.0.2.2 falló: HTTP {status}, x-app-status-code={app_status!r}, bytes={len(body)}")
    status, body, _ = http_request("GET", "/v1/list/12345/0")
    if status != 200 or not body:
        raise VerifyError(f"Gate GET /v1/list/12345/0 falló: HTTP {status}, bytes={len(body)}")

    candidate = next((row for row in catalog if row.get("enabled") and str(row.get("requestPath", "")).startswith("/cdn/")), None)
    if not candidate:
        raise VerifyError("catalog.json no contiene una ruta CDN habilitada para la prueba de preflight")
    path = str(candidate["requestPath"])
    status, body, _ = http_request("GET", path)
    expected_sha = str(candidate.get("sha256", "")).lower()
    actual_sha = hashlib.sha256(body).hexdigest()
    if status != 200 or not body or (expected_sha and actual_sha != expected_sha):
        raise VerifyError(f"Gate GET {path} falló: HTTP {status}, bytes={len(body)}, sha256={actual_sha}; catálogo={expected_sha}")
    return {"boot_index_http": 200, "boot_index_app_status": app_status,
            "list_revision_zero_http": 200, "cdn_probe_path": path,
            "cdn_probe_http": status, "cdn_probe_bytes": len(body), "cdn_probe_sha256": actual_sha}


def capture_png(adb: str, serial: str, remote: str, output: Path) -> None:
    native = output.with_name(output.stem + "-native.png")
    run([adb, "-s", serial, "shell", "screencap", "-p", remote], timeout=20)
    run([adb, "-s", serial, "pull", remote, str(native)], timeout=30)
    run(["sips", "--resampleWidth", "720", str(native), "--out", str(output)], timeout=30)
    native.unlink(missing_ok=True)
    run([adb, "-s", serial, "shell", "rm", "-f", remote], timeout=10, check=False)


def accessibility_bounds(adb: str, serial: str, label: str, scratch: Path) -> tuple[int, int, int, int] | None:
    """Return a visible Android accessibility node's bounds when Unity exposes it."""
    remote = "/sdcard/cdn-clean-window.xml"
    run([adb, "-s", serial, "shell", "uiautomator", "dump", "--compressed", remote],
        timeout=25, check=False)
    pulled = run([adb, "-s", serial, "pull", remote, str(scratch)], timeout=20, check=False)
    if pulled.returncode or not scratch.is_file():
        run([adb, "-s", serial, "shell", "rm", "-f", remote], timeout=10, check=False)
        return None
    try:
        root = ET.parse(scratch).getroot()
        for node in root.iter("node"):
            text = " ".join((node.attrib.get("text", ""), node.attrib.get("content-desc", ""))).lower()
            if label.lower() not in text:
                continue
            match = re.fullmatch(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", node.attrib.get("bounds", ""))
            if match:
                return tuple(map(int, match.groups()))  # type: ignore[return-value]
    except (ET.ParseError, OSError):
        pass
    finally:
        scratch.unlink(missing_ok=True)
        run([adb, "-s", serial, "shell", "rm", "-f", remote], timeout=10, check=False)
    return None


def device_cache_listing(adb: str, serial: str) -> list[str]:
    result = run([adb, "-s", serial, "shell", "run-as", PACKAGE,
                  "find", "files/octo", "-type", "f"], timeout=120, check=False)
    if result.returncode:
        detail = (result.stderr + result.stdout).lower()
        if "no such file or directory" in detail:
            return []
        raise VerifyError(f"No pude inventariar files/octo con run-as: {detail[-1200:]}")
    return [line.strip() for line in result.stdout.splitlines() if line.strip()]


def cache_stats(paths: list[str]) -> dict[str, int]:
    payloads = sum(1 for p in paths if not p.endswith("/.meta") and not p.endswith("/octocacheevai"))
    metas = sum(1 for p in paths if p.endswith("/.meta"))
    pdb = sum(1 for p in paths if p.endswith("/octocacheevai"))
    return {"files": len(paths), "payloads": payloads, "metadata_files": metas, "pdb_files": pdb}


def screen_size(adb: str, serial: str) -> tuple[int, int]:
    text = run([adb, "-s", serial, "shell", "wm", "size"], timeout=10).stdout
    matches = re.findall(r"(\d+)x(\d+)", text)
    if not matches:
        return (1080, 2400)
    return tuple(map(int, matches[-1]))  # type: ignore[return-value]


def find_sdk_avd_port(adb: str) -> int:
    devices = run([adb, "devices"], timeout=15).stdout
    used = {int(m.group(1)) for m in re.finditer(r"^emulator-(\d+)\s", devices, re.M)}
    for port in range(5554, 5683, 2):
        if port in used:
            continue
        with socket.socket() as sock:
            try:
                sock.bind(("127.0.0.1", port))
            except OSError:
                continue
        with socket.socket() as sock:
            try:
                sock.bind(("127.0.0.1", port + 1))
            except OSError:
                continue
        return port
    raise VerifyError("No hay un par de puertos libres para el nuevo emulador (5554–5682).")


def read_catalog() -> tuple[list[dict[str, Any]], int, int]:
    catalog_data = json.loads((REPO / "config/resources/catalog.json").read_text(encoding="utf-8"))
    title_data = json.loads((REPO / "config/resources/title-minimum.json").read_text(encoding="utf-8"))
    catalog = [row for row in catalog_data.get("resources", []) if isinstance(row, dict)]
    title_entries = title_data.get("entries", [])
    cdn_count = sum(bool(row.get("enabled")) and str(row.get("requestPath", "")).startswith("/cdn/")
                    for row in catalog)
    return catalog, cdn_count, len(title_entries)


def collect_captures(capture_dir: Path, started_at: float) -> list[dict[str, Any]]:
    records: list[dict[str, Any]] = []
    if not capture_dir.is_dir():
        return records
    for path in capture_dir.glob("request-*.json"):
        try:
            if path.stat().st_mtime < started_at:
                continue
            record = json.loads(path.read_text(encoding="utf-8"))
            if isinstance(record, dict):
                records.append(record)
        except (OSError, json.JSONDecodeError):
            continue
    return records


def summarize_server_log(path: Path | None, offset: int) -> dict[str, Any]:
    result: dict[str, Any] = {"log_path": str(path) if path else "", "cdn_completed_records": 0,
                              "cdn_status_counts": {}, "cdn_response_bytes_logged": 0}
    if not path or not path.is_file():
        return result
    try:
        with path.open("rb") as stream:
            stream.seek(offset)
            text = stream.read().decode("utf-8", errors="replace")
    except OSError:
        return result
    status_counts: Counter[str] = Counter()
    response_bytes = 0
    completed_plain = re.compile(r"Completed GET [^\s]*/cdn/[^\s;]+ with (\d{3})", re.I)
    kestrel_plain = re.compile(r"Request finished .*?/cdn/[^\s]*\s+-\s+(\d{3})\s+(\d+)(?:\s|$)", re.I)
    # The server writes one JSON event per line. Its Message is repeated in State.Message,
    # so parse the structured fields once instead of matching both copies in the JSON text.
    for line in text.splitlines():
        try:
            event = json.loads(line)
        except json.JSONDecodeError:
            event = None
        if isinstance(event, dict):
            state = event.get("State") if isinstance(event.get("State"), dict) else {}
            category = str(event.get("Category", ""))
            path_value = str(state.get("Path", ""))
            if path_value.startswith("/cdn/") and str(state.get("Method", "")).upper() == "GET":
                if category.endswith("RequestCaptureMiddleware"):
                    status_counts[str(state.get("StatusCode", "unknown"))] += 1
                elif category.endswith("Hosting.Diagnostics"):
                    try:
                        response_bytes += int(state.get("ContentLength") or 0)
                    except (TypeError, ValueError):
                        pass
            continue
        # Plain-text fallback: one search per line avoids counting duplicated message
        # text, and keeps status and response-size evidence in separate log events.
        completed = completed_plain.search(line)
        if completed:
            status_counts[completed.group(1)] += 1
        kestrel = kestrel_plain.search(line)
        if kestrel:
            try:
                response_bytes += int(kestrel.group(2))
            except ValueError:
                pass
    result.update({"cdn_completed_records": sum(status_counts.values()),
                   "cdn_status_counts": dict(status_counts), "cdn_response_bytes_logged": response_bytes})
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apk", type=Path, help="Parcheado ya construido; default: artefacto cdn-clean actual")
    parser.add_argument("--build", action="store_true", help="Reconstruir con SERVER_BASE_URL=http://10.0.2.2:18080")
    parser.add_argument("--api-port", type=int, default=API_PORT)
    parser.add_argument("--api-log", type=Path, help="Log stdout del servidor existente; para códigos HTTP CDN")
    parser.add_argument("--boot-timeout", type=int, default=360)
    parser.add_argument("--download-timeout", type=int, default=3600)
    parser.add_argument("--settle-seconds", type=int, default=20)
    parser.add_argument("--avd-name", help="Nombre descriptivo; el AVD siempre se crea en el run dir aislado")
    args = parser.parse_args()
    if sys.platform != "darwin":
        raise VerifyError("Este runner requiere macOS (avdmanager, emulator y sips).")
    if args.build and args.apk:
        raise VerifyError("Usa --apk o --build, no ambos.")
    if args.api_port != API_PORT:
        raise VerifyError("El APK solicitado apunta a http://10.0.2.2:18080; --api-port debe ser 18080.")

    run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    run_dir = REPO / ".local/cdn-clean" / run_id
    if run_dir.exists():
        raise VerifyError(f"La carpeta de evidencia ya existe; no se sobrescribe: {run_dir}")
    screenshots = run_dir / "screenshots"
    screenshots.mkdir(parents=True)
    report_path = run_dir / "summary.json"
    summary: dict[str, Any] = {"run_id": run_id, "result": "failed", "phase": "preflight",
                               "started_utc": datetime.now(timezone.utc).isoformat(),
                               "server_url": CLIENT_BASE, "package": PACKAGE,
                               "evidence_dir": str(run_dir)}
    api_proc: subprocess.Popen[bytes] | None = None
    emulator_proc: subprocess.Popen[bytes] | None = None
    logcat_proc: subprocess.Popen[bytes] | None = None
    adb = emulator = avdmanager = ""
    serial = ""
    server_log = args.api_log.resolve() if args.api_log else None
    server_log_offset = server_log.stat().st_size if server_log and server_log.exists() else 0
    capture_dir = CAPTURE_DEFAULT
    capture_started = time.time()

    try:
        summary["phase"] = "catalog-apk"
        catalog, enabled_catalog_routes, expected_payloads = read_catalog()
        title_revision = int(json.loads((REPO / "config/resources/title-minimum.json").read_text())["revision"])
        summary.update({"catalog_cdn_routes_enabled": enabled_catalog_routes,
                        "title_minimum_revision": title_revision,
                        "title_minimum_entries_expected": expected_payloads})
        if expected_payloads != 2593:
            raise VerifyError(f"title-minimum rev{title_revision} cambió: esperaba 2593 entradas, encontré {expected_payloads}.")

        if args.build:
            apk = REPO / ".local/artifacts" / f"KickFlight-2.11.0-cdn-clean-{run_id}.apk"
            build_log = run_dir / "build.log"
            say(f"[build] Construyendo APK hacia {apk}")
            env = os.environ.copy()
            env["SERVER_BASE_URL"] = CLIENT_BASE
            env["OUTPUT_APK"] = str(apk)
            with build_log.open("wb") as stream:
                completed = subprocess.run([str(REPO / "scripts/build-direct-apk.sh")], cwd=REPO,
                                           env=env, stdout=stream, stderr=subprocess.STDOUT, timeout=1800)
            if completed.returncode:
                raise VerifyError(f"Build falló ({completed.returncode}); revisa {build_log}")
        else:
            apk = (args.apk or DEFAULT_APK).expanduser().resolve()
        if not apk.is_file():
            raise VerifyError(f"No existe el APK: {apk}. Pásalo con --apk o reconstruye con --build.")
        apk_sha = hashlib.sha256(apk.read_bytes()).hexdigest()
        summary.update({"apk": str(apk), "apk_sha256": apk_sha})

        sdk, adb, emulator, avdmanager = resolve_sdk()
        image = choose_image(sdk)
        summary["android_system_image"] = image
        env = os.environ.copy()
        env["ANDROID_SDK_ROOT"] = str(sdk)
        env["ANDROID_HOME"] = str(sdk)

        summary["phase"] = "server-start"
        # Reuse a running API only after the required host-specific CDN gates pass.
        api_socket = socket.socket()
        try:
            server_present = api_socket.connect_ex(("127.0.0.1", API_PORT)) == 0
        finally:
            api_socket.close()
        if not server_present:
            capture_dir = run_dir / "request-captures"
            capture_dir.mkdir()
            server_log = run_dir / "server.log"
            server_log_offset = 0
            api_env = env.copy()
            api_env.update({"HTTP_PORT": str(API_PORT), "DIRECT_CLIENT_HOST": "10.0.2.2",
                            "ENABLE_CAPTURE": "true", "Harness__CaptureDirectory": str(capture_dir)})
            say("[server] 18080 no tiene listener; iniciando scripts/run-local.sh")
            log_stream = server_log.open("wb")
            api_proc = subprocess.Popen([str(REPO / "scripts/run-local.sh")], cwd=REPO,
                                        env=api_env, stdout=log_stream, stderr=subprocess.STDOUT,
                                        start_new_session=True)
            deadline = time.monotonic() + 180
            while time.monotonic() < deadline:
                if api_proc.poll() is not None:
                    raise VerifyError(f"El servidor terminó al iniciar; revisa {server_log}")
                try:
                    sock = socket.create_connection(("127.0.0.1", API_PORT), timeout=1)
                    sock.close()
                    server_present = True
                    break
                except OSError:
                    time.sleep(1)
            if not server_present:
                raise VerifyError(f"Timeout iniciando API en 18080; revisa {server_log}")
        else:
            say("[server] API existente detectada en 18080; no se detendrá ni reiniciará")
        summary["phase"] = "server-gates"
        gate = api_preflight(catalog)
        summary.update(gate)
        # Exclude the runner's own /boot, list and one-CDN preflight from app evidence.
        capture_started = time.time()
        if server_log and server_log.exists():
            server_log_offset = server_log.stat().st_size
        summary["server_started_by_runner"] = api_proc is not None
        summary["server_log"] = str(server_log) if server_log else "unavailable; pass --api-log to inspect statuses"

        summary["phase"] = "avd-create"
        avd_home = run_dir / "avd-home"
        avd_home.mkdir()
        avd_name = args.avd_name or f"kfcdn_{run_id.replace('T', '_').replace('Z', '')}"
        avd_path = avd_home / f"{avd_name}.avd"
        if avd_path.exists() or (avd_home / f"{avd_name}.ini").exists():
            raise VerifyError(f"AVD name ya existe dentro del directorio nuevo: {avd_name}")
        env["ANDROID_AVD_HOME"] = str(avd_home)
        summary.update({"avd_name": avd_name, "avd_home": str(avd_home), "avd_path": str(avd_path),
                        "existing_avds_modified": False})
        say(f"[avd] Creando perfil aislado {avd_name} ({image})")
        created = run([avdmanager, "create", "avd", "-n", avd_name, "-k", image,
                       "-d", "pixel_5", "-p", str(avd_path)], timeout=180,
                      env=env, input_text="no\n")
        (run_dir / "avdmanager.log").write_text(created.stdout + created.stderr, encoding="utf-8")
        port = find_sdk_avd_port(adb)
        serial = f"emulator-{port}"
        summary.update({"emulator_port": port, "serial": serial})
        say(f"[avd] Iniciando {serial}; userdata pertenece solo a {avd_name}")
        emulator_log = (run_dir / "emulator.log").open("wb")
        emulator_proc = subprocess.Popen([emulator, "-avd", avd_name, "-port", str(port),
                                          "-no-snapshot-load", "-no-snapshot-save", "-no-boot-anim",
                                          "-gpu", "host"], cwd=REPO, env=env,
                                         stdout=emulator_log, stderr=subprocess.STDOUT,
                                         start_new_session=True)
        summary["phase"] = "avd-boot"
        boot_deadline = time.monotonic() + args.boot_timeout
        while time.monotonic() < boot_deadline:
            if emulator_proc.poll() is not None:
                raise VerifyError(f"Emulador terminó durante el boot; revisa {run_dir / 'emulator.log'}")
            state = run([adb, "devices"], timeout=10, check=False).stdout
            boot = run([adb, "-s", serial, "shell", "getprop", "sys.boot_completed"], timeout=10, check=False)
            if re.search(rf"^{re.escape(serial)}\s+device$", state, re.M) and boot.stdout.strip() == "1":
                break
            time.sleep(2)
        else:
            raise VerifyError(f"Timeout de boot para AVD nuevo {serial}; revisa emulator.log")

        summary["phase"] = "install-and-clean-cache-check"
        installed = run([adb, "-s", serial, "shell", "pm", "path", PACKAGE], timeout=15, check=False).stdout.strip()
        if installed:
            raise VerifyError(f"El AVD recién creado ya contenía {PACKAGE}; se detiene para preservar la prueba limpia.")
        run([adb, "-s", serial, "install", str(apk)], timeout=180)
        installed_path = run([adb, "-s", serial, "shell", "pm", "path", PACKAGE], timeout=20).stdout.strip()
        apk_device_path = installed_path.splitlines()[0].removeprefix("package:").strip()
        device_sha = run([adb, "-s", serial, "shell", "sha256sum", apk_device_path], timeout=20).stdout.split()[0].lower()
        if device_sha != apk_sha:
            raise VerifyError(f"SHA del APK instalado no coincide: host={apk_sha}, dispositivo={device_sha}")
        run([adb, "-s", serial, "shell", "pm", "grant", PACKAGE, "android.permission.POST_NOTIFICATIONS"],
            timeout=15, check=False)

        initial_paths = device_cache_listing(adb, serial)
        initial_stats = cache_stats(initial_paths)
        summary["cache_initial"] = initial_stats
        if initial_stats["files"] != 0:
            raise VerifyError(f"El AVD nuevo no está limpio: encontró {initial_stats['files']} archivos Octo antes del primer launch.")

        logcat_path = run_dir / "logcat.txt"
        logcat_stream = logcat_path.open("wb")
        run([adb, "-s", serial, "logcat", "-c"], timeout=20)
        logcat_proc = subprocess.Popen([adb, "-s", serial, "logcat", "-v", "time"],
                                       stdout=logcat_stream, stderr=subprocess.STDOUT)
        recent = bytearray()
        scene_markers: set[str] = set()
        fatal_markers: set[str] = set()
        with logcat_path.open("rb") as reader:
            reader.seek(0, os.SEEK_END)

            def collect_log_delta() -> str:
                data = reader.read()
                delta = data.decode("utf-8", errors="replace") if data else ""
                if data:
                    for marker in ("TitleScene", "DownloadScene", "HomeScene"):
                        if marker in delta:
                            scene_markers.add(marker)
                    for marker in ("FATAL EXCEPTION", "Fatal signal", "SIGSEGV", "SIGABRT"):
                        if marker in delta:
                            fatal_markers.add(marker)
                    recent.extend(data)
                    if len(recent) > 512_000:
                        del recent[:-512_000]
                return delta

            summary["phase"] = "launch-title"
            say(f"[app] Abriendo {LAUNCH_ACTIVITY} y esperando TitleScene")
            launch = run([adb, "-s", serial, "shell", "am", "start", "-W", "-n", LAUNCH_ACTIVITY],
                         timeout=45, check=False)
            summary["launch_command_output"] = (launch.stdout + launch.stderr).strip()[-1200:]
            if launch.returncode:
                raise VerifyError(f"No se pudo iniciar la actividad launcher explícita {LAUNCH_ACTIVITY}: {summary['launch_command_output']}")
            title_deadline = time.monotonic() + 180
            while time.monotonic() < title_deadline:
                collect_log_delta()
                if "TitleScene" in scene_markers:
                    break
                time.sleep(3)
            else:
                raise VerifyError("El cliente no llegó a TitleScene dentro de 180 s; revisa logcat.txt")
            capture_png(adb, serial, "/sdcard/cdn-clean-title.png", screenshots / "title-720.png")
            width, height = screen_size(adb, serial)
            summary["screen_size_native"] = f"{width}x{height}"

            got_it_bounds = accessibility_bounds(adb, serial, "Got it", run_dir / "uiautomator.xml")
            summary["got_it_dialog_detected"] = got_it_bounds is not None
            if got_it_bounds:
                x1, y1, x2, y2 = got_it_bounds
                run([adb, "-s", serial, "shell", "input", "tap", str((x1 + x2) // 2), str((y1 + y2) // 2)], timeout=15)
                time.sleep(3)
                capture_png(adb, serial, "/sdcard/cdn-clean-title-after-gotit.png",
                            screenshots / "title-after-got-it-720.png")
                collect_log_delta()
                say("[app] Diálogo Got it detectado por accesibilidad y aceptado")

            summary["phase"] = "tap-start-download"
            say("[app] Tap único en TAP START (centro de pantalla)")
            run([adb, "-s", serial, "shell", "input", "tap", str(width // 2), str(height // 2)], timeout=15)
            started = time.monotonic()
            download_captured = False
            home_seen = False
            while time.monotonic() - started < args.download_timeout:
                collect_log_delta()
                if "DownloadScene" in scene_markers and not download_captured:
                    download_captured = True
                    if "HomeScene" not in scene_markers:
                        capture_png(adb, serial, "/sdcard/cdn-clean-download.png", screenshots / "download-720.png")
                        time.sleep(2.5)
                        collect_log_delta()
                    if "HomeScene" not in scene_markers:
                        confirm_x = round(width * 766 / 1080)
                        confirm_y = round(height * 1420 / 2340)
                        run([adb, "-s", serial, "shell", "input", "tap", str(confirm_x), str(confirm_y)], timeout=15)
                        summary["download_confirmation_tapped"] = {"x": confirm_x, "y": confirm_y,
                                                                     "scale_from": "1080x2340"}
                        say(f"[app] DownloadScene confirmada; tap Descarga ({confirm_x},{confirm_y}) y esperando HomeScene")
                    else:
                        summary["download_confirmation_tapped"] = False
                        summary["download_screenshot_skipped"] = "HomeScene was already logged in the same logcat delta"
                if "HomeScene" in scene_markers:
                    home_seen = True
                    break
                if fatal_markers:
                    raise VerifyError(f"El proceso Android reportó {', '.join(sorted(fatal_markers))} durante la descarga; revisa logcat.txt")
                time.sleep(5)
            if not home_seen:
                raise VerifyError(f"No llegó a HomeScene dentro de {args.download_timeout}s; revisa logcat.txt y las capturas.")
            collect_log_delta()
            summary["download_scene_seen"] = "DownloadScene" in scene_markers
            summary["home_scene_seen"] = True
            summary["phase"] = "settle-home"
            time.sleep(max(0, args.settle_seconds))
            collect_log_delta()
            capture_png(adb, serial, "/sdcard/cdn-clean-home.png", screenshots / "home-720.png")
            summary["phase"] = "inventory"
            final_paths = device_cache_listing(adb, serial)
            final_stats = cache_stats(final_paths)
            summary["cache_final"] = final_stats
            summary["cache_file_count_delta"] = final_stats["files"] - initial_stats["files"]
            summary["cache_bytes_approx"] = run([adb, "-s", serial, "shell", "run-as", PACKAGE,
                                                  "du", "-sk", "files/octo"], timeout=120,
                                                 check=False).stdout.strip()

        if logcat_proc and logcat_proc.poll() is None:
            logcat_proc.terminate()
            try:
                logcat_proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                logcat_proc.kill()
        logcat_stream.close()

        records = collect_captures(capture_dir, capture_started)
        cdn_records = [r for r in records if str(r.get("method", "")).upper() == "GET"
                       and str(r.get("path", "")).startswith("/cdn/")]
        unique_paths = sorted({str(r["path"]) for r in cdn_records})
        route_rows = {str(row.get("requestPath")): row for row in catalog}
        catalog_bytes = 0
        catalog_bytes_known = True
        for path in unique_paths:
            source = route_rows.get(path, {}).get("sourcePath")
            source_path = (REPO / str(source)).resolve() if source else None
            if not source_path or not source_path.is_file():
                catalog_bytes_known = False
                continue
            catalog_bytes += source_path.stat().st_size
        summary["cdn_requests"] = {"request_records": len(cdn_records), "distinct_paths": len(unique_paths),
                                   "paths": unique_paths,
                                   "catalog_expected_bytes_for_distinct_paths": catalog_bytes if catalog_bytes_known else None}
        server_summary = summarize_server_log(server_log, server_log_offset)
        summary["cdn_server_log"] = server_summary
        status_counts = server_summary["cdn_status_counts"]
        errors = sum(count for status, count in status_counts.items() if status.startswith(("4", "5")))
        summary["cdn_http_errors_observed"] = errors
        summary["cdn_status_observation_complete"] = server_summary["cdn_completed_records"] >= len(cdn_records)

        complete_cache = (final_stats["payloads"] >= expected_payloads
                          and final_stats["metadata_files"] >= expected_payloads)
        scene_complete = bool(summary.get("download_scene_seen") and summary.get("home_scene_seen"))
        http_clean = errors == 0 and summary["cdn_status_observation_complete"]
        summary["download_complete_by_cache"] = complete_cache
        summary["download_complete_by_scenes"] = scene_complete
        summary["download_behavior"] = ("full catalog cache present" if complete_cache else
                                         "partial/on-demand subset only; not all title-minimum resources are cached")
        summary["phase"] = "complete"
        summary["finished_utc"] = datetime.now(timezone.utc).isoformat()
        summary["result"] = "passed" if scene_complete and complete_cache and http_clean else "partial"
        summary["note"] = ("All title-minimum payloads and metadata are present after a zero-cache install." if complete_cache
                           else "HomeScene alone does not prove the whole catalog downloaded; see cache_final and CDN request counts.")
    except Exception as error:
        summary["error"] = str(error)
        summary["finished_utc"] = datetime.now(timezone.utc).isoformat()
        say(f"[error] {error}")
    finally:
        if logcat_proc and logcat_proc.poll() is None:
            logcat_proc.terminate()
            try:
                logcat_proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                logcat_proc.kill()
        if emulator_proc and emulator_proc.poll() is None:
            try:
                run([adb, "-s", serial, "emu", "kill"], timeout=10, check=False)
                emulator_proc.wait(timeout=20)
            except (subprocess.TimeoutExpired, OSError):
                emulator_proc.terminate()
                try:
                    emulator_proc.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    emulator_proc.kill()
        if api_proc and api_proc.poll() is None:
            try:
                os.killpg(api_proc.pid, signal.SIGTERM)
                api_proc.wait(timeout=20)
            except (subprocess.TimeoutExpired, OSError):
                api_proc.kill()
        summary["finished_utc"] = summary.get("finished_utc", datetime.now(timezone.utc).isoformat())
        report_path.write_text(json.dumps(summary, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        say(f"[summary] {report_path}")
        say(f"[result] {summary.get('result')} — {summary.get('download_behavior', summary.get('error', 'ver summary.json'))}")

    return 0 if summary.get("result") == "passed" else 2 if summary.get("result") == "partial" else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except VerifyError as error:
        say(f"ERROR: {error}")
        raise SystemExit(1)
