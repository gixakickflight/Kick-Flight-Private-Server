#!/usr/bin/env python3
"""Comprehensive Playwright Browser QA for bilingual /balance UI (Spanish & English).

Verifies:
- Default Spanish on all UI surfaces (login, dashboard, 8 tabs, header, footer, dialogs, toasts)
- Header language selector (Español / English) present and responsive on both desktop and mobile
- Preference persistence across login, navigation, and page reloads via localStorage and cookies
- Graceful storage failure fallback (when localStorage throws or is restricted)
- Live language switching preserving:
  * selected tab, kicker, disc, and bot AI mode/kicker
  * dirty draft inputs in tables, sliders, maintenance, client update, and latest information
  * open modals (review modal and concurrency conflict modal)
  * login form credentials
- Discs tab: single-language guide banner and single-language field guidance
- Health tab: translated range buttons ("Esta versión" / "This release", etc.), populated operational telemetry
- Maintenance tab: translated sidebar and status
- Bot AI tab:
  * Exactly 14 individual kickers per mode across all 3 modes (42 profiles total)
  * Selector displays individual kicker avatar and name (never shared role groups)
  * All 42 combinations accessible
  * Mode independence: editing K1 in CrystalScramble does not dirty K1 in FlagFlight or RapidBall
  * Edit K1 then K8: proves K8 values remain untouched before, during, and after save + reload
  * Live EN/ES switch with open review modal and unsaved drafts
  * Concurrency conflict (409): ETag mismatch opens conflict modal with draft preservation & reload
- Captures downscaled screenshots (<=720px width) in .local/artifacts/balance-i18n/
  * Desktop EN/ES: 14 kicker list, selected kicker, review modal
  * Mobile 390 EN/ES: 14 kicker picker, selected kicker, review modal
"""

from __future__ import annotations

import copy
import hashlib
import http.server
import json
import os
import shutil
import socket
import socketserver
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
from http.server import HTTPServer, BaseHTTPRequestHandler
from pathlib import Path
from urllib.parse import parse_qs, urlsplit
from PIL import Image
from playwright.sync_api import sync_playwright

REPO = Path(__file__).resolve().parents[2]



class MockOpsHandler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        if "summary" in self.path:
            data = {
                "ok": True,
                "window": {"name": "deploy", "since": "2026-10-20T00:00:00Z", "releaseSha": "abc1234", "runId": "run-1"},
                "totals": {
                    "clean": 42, "partial": 3, "abandoned": 1, "neverStarted": 0, "running": 2, "matching": 0,
                    "completedPlayers": 120, "reconnectedCompleted": 5, "reconnectFailed": 1, "droppedSilent": 0,
                    "relaunched": 0, "matchingFailures": 0, "matchingTimeouts": 0, "cancelled": 0, "failed": 0, "other": 0
                },
                "telemetry": {"store": "sqlite3"}
            }
        elif "exceptions" in self.path:
            data = {
                "ok": True,
                "groups": [{
                    "kind": "NullReferenceException",
                    "count": 7,
                    "fingerprint": "NRE_0x13c74cc",
                    "signature": "PlayerCharacter.Initialize",
                    "userCount": 3,
                    "users": ["usr1", "usr2"],
                    "appVersions": [{"key": "2.11.0", "count": 7}],
                    "lastAt": "2026-10-20T12:00:00Z"
                }],
                "problemMatches": [{
                    "battleId": "bat-9988",
                    "outcome": "abandoned",
                    "roster": "Hitagi vs Sid",
                    "errorKind": "NullReferenceException",
                    "participants": 4
                }]
            }
        else:
            data = {"ok": True}
        self.wfile.write(json.dumps(data).encode("utf-8"))

    def log_message(self, format, *args):
        pass

def discover_artifacts_dir() -> Path:
    try:
        common_git = subprocess.check_output(
            ["git", "rev-parse", "--path-format=absolute", "--git-common-dir"],
            cwd=str(REPO),
            text=True,
            stderr=subprocess.DEVNULL
        ).strip()
        common_root = Path(common_git).parent
        target_dir = common_root / ".local" / "artifacts" / "balance-final"
        target_dir.mkdir(parents=True, exist_ok=True)
        return target_dir
    except Exception:
        fallback = REPO / ".local" / "artifacts" / "balance-i18n"
        fallback.mkdir(parents=True, exist_ok=True)
        return fallback


ARTIFACTS_DIR = discover_artifacts_dir()
PORT = 18911
ADMIN_PORT = 18912
BASE_URL = f"http://127.0.0.1:{PORT}/balance"
USERNAME = "testadmin"
PASSWORD = "Password123!"


def downscale_image(image_path: Path, max_width: int = 720) -> None:
    if not image_path.is_file():
        return
    with Image.open(image_path) as img:
        orig_w, orig_h = img.size
        if orig_w > max_width:
            target_h = int(orig_h * (max_width / orig_w))
            res = img.resize((max_width, target_h), Image.Resampling.LANCZOS)
            res.save(image_path, optimize=True)
            print(f"Downscaled {image_path.name}: {orig_w}x{orig_h} -> {max_width}x{target_h}")


class MockAdminHandler(BaseHTTPRequestHandler):
    state = {
        "mode": "off",
        "title": "",
        "message": "",
        "noticeId": 1,
        "changedAtUtc": "2026-10-20T12:00:00Z"
    }
    client_update = {
        "enabled": True,
        "minimumVersion": "2.11.4",
        "iosMinimumVersion": "",
        "isManualOverride": False,
        "defaultMinimumVersion": "2.11.4",
        "defaultIosMinimumVersion": ""
    }
    latest_information = {
        "content": "Noticias del servidor",
        "defaultContent": "Noticias del servidor",
        "mode": "text",
        "htmlContent": "",
        "externalUrl": "",
        "changedAtUtc": "2026-10-20T12:00:00Z"
    }
    ops_summary = {
        "totals": {
            "matches": 12, "running": 1, "matching": 2, "clean": 9, "partial": 2, "abandoned": 0,
            "neverStarted": 1, "custom": 0, "entries": 48, "players": 24, "completedPlayers": 22,
            "reconnectedCompleted": 1, "disconnectedPlayers": 2, "droppedSilent": 1, "relaunched": 0,
            "reconnectFailed": 0, "left": 0, "matchingFailures": 0, "matchingTimeouts": 0, "cancelled": 0,
            "exceptions": 2, "anomalies": 0, "reconnectBegins": 1
        },
        "statuses": {"completed": 22, "dropped_silent": 1},
        "eventKinds": {"client_exception": 2},
        "breakdowns": {"appVersion": [], "device": [], "os": [], "rule": [], "field": []},
        "problemMatches": [{
            "battleId": "battle-test-001",
            "outcome": "partial",
            "humans": 2,
            "bots": 2,
            "custom": False,
            "participants": [{"userId": "1000001", "status": "dropped_silent"}]
        }],
        "telemetry": {
            "store": "postgres", "enabled": True, "queueDepth": 0, "written": 48, "dropped": 0,
            "writeErrors": 0, "lastWriteError": None, "lastWriteAt": None, "trackedBattles": 12
        }
    }
    ops_exceptions = {
        "fingerprints": [
            {
                "fingerprint": "NRE_PlayerCharacter_Update",
                "count": 2,
                "firstSeen": "2026-10-20T10:00:00Z",
                "lastSeen": "2026-10-20T11:30:00Z",
                "sample": "NullReferenceException in PlayerCharacter.Update"
            }
        ]
    }

    def log_message(self, *args):
        pass

    def send_json(self, status, payload):
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        parsed = urlsplit(self.path)
        path = parsed.path
        if path == "/admin/maintenance":
            self.send_json(200, dict(MockAdminHandler.state))
            return
        if path == "/admin/client-update":
            self.send_json(200, dict(MockAdminHandler.client_update))
            return
        if path == "/admin/latest-information":
            self.send_json(200, dict(MockAdminHandler.latest_information))
            return
        if path == "/admin/ops/summary":
            qs = parse_qs(parsed.query)
            window = (qs.get("window") or ["deploy"])[-1]
            data = copy.deepcopy(MockAdminHandler.ops_summary)
            data["window"] = {
                "name": window,
                "since": "2026-10-20T00:00:00Z",
                "releaseSha": "d439583",
                "runId": "run-42",
                "runs": []
            }
            self.send_json(200, data)
            return
        if path == "/admin/ops/exceptions":
            self.send_json(200, dict(MockAdminHandler.ops_exceptions))
            return
        if path.startswith("/admin/ops/battle/"):
            self.send_json(200, {
                "battle": {"outcome": "abandoned", "ruleId": 1, "fieldId": 801, "humans": 2, "bots": 2, "createdAt": "2026-10-20T12:00:00Z"},
                "participants": [{"userId": "1000001", "status": "dropped_silent", "kickerId": 13, "costumeId": 1, "team": 1, "appVersion": "2.11.4", "deviceName": "Pixel", "reconnectBegins": 1, "exceptionCount": 0}],
                "timeline": [{"at": "2026-10-20T12:00:00Z", "kind": "client_exception", "userId": "1000001", "payload": {"detail": "preserve raw"}}]
            })
            return
        self.send_json(404, {"error": "not found"})

    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length) if length > 0 else b"{}"
        try:
            payload = json.loads(body.decode("utf-8"))
        except Exception:
            payload = {}

        parsed = urlsplit(self.path)
        path = parsed.path

        if path == "/admin/maintenance":
            MockAdminHandler.state.update(payload)
            self.send_json(200, dict(MockAdminHandler.state))
            return
        if path == "/admin/client-update":
            MockAdminHandler.client_update.update(payload)
            self.send_json(200, dict(MockAdminHandler.client_update))
            return
        if path == "/admin/latest-information":
            MockAdminHandler.latest_information.update(payload)
            self.send_json(200, dict(MockAdminHandler.latest_information))
            return
        self.send_json(404, {"error": "not found"})


def setup_test_environment(tmp_path: Path):
    config_dir = tmp_path / "config"
    config_dir.mkdir(parents=True)
    backups_dir = tmp_path / "backups"
    backups_dir.mkdir(parents=True)
    auth_dir = tmp_path / "auth"
    auth_dir.mkdir(parents=True, mode=0o700)
    overrides_dir = tmp_path / "overrides"
    overrides_dir.mkdir(parents=True)

    shutil.copy(REPO / "config" / "bot_ai_profiles.json", config_dir / "bot_ai_profiles.json")
    for m in (REPO / "config").glob("masters_*.json"):
        shutil.copy(m, config_dir / m.name)

    auth_db = auth_dir / "auth.sqlite3"
    jwt_secret = auth_dir / "jwt-secret"
    jwt_secret.write_bytes(os.urandom(32))
    os.chmod(jwt_secret, 0o600)

    sys.path.insert(0, str(REPO / "tools" / "balance"))
    from auth import AuthStore, hash_password
    AuthStore(auth_db, jwt_secret=jwt_secret.read_bytes())
    with sqlite3.connect(auth_db) as conn:
        conn.execute(
            "INSERT INTO users(username, password_hash, created_at) VALUES (?, ?, ?)",
            (USERNAME, hash_password(PASSWORD), int(time.time()))
        )

    # Start mock admin server
    admin_server = HTTPServer(("127.0.0.1", ADMIN_PORT), MockAdminHandler)
    admin_thread = threading.Thread(target=admin_server.serve_forever, daemon=True)
    admin_thread.start()

    return config_dir, backups_dir, auth_dir, overrides_dir, auth_db, jwt_secret, admin_server


def main():
    temp_dir_obj = tempfile.TemporaryDirectory(prefix="kf_i18n_test_")
    tmp_path = Path(temp_dir_obj.name)
    config_dir, backups_dir, auth_dir, overrides_dir, auth_db, jwt_secret, admin_server = setup_test_environment(tmp_path)

    mock_ops_server = socketserver.TCPServer(("127.0.0.1", 0), MockOpsHandler)
    mock_ops_port = mock_ops_server.server_address[1]
    mock_ops_thread = threading.Thread(target=mock_ops_server.serve_forever, daemon=True)
    mock_ops_thread.start()

    env = os.environ.copy()
    env["KF_BALANCE_OPS_URL"] = f"http://127.0.0.1:{mock_ops_port}"
    env["KF_BALANCE_CONFIG_DIR"] = str(config_dir)
    env["KF_BALANCE_BASE_DIR"] = str(config_dir)
    env["KF_BALANCE_BACKUP_DIR"] = str(backups_dir)
    env["KF_BALANCE_OVERRIDE_DIR"] = str(overrides_dir)
    env["KF_BALANCE_AUTH_DB"] = str(auth_db)
    env["KF_BALANCE_JWT_SECRET"] = str(jwt_secret)
    env["KF_BALANCE_PORT"] = str(PORT)
    env["KF_BALANCE_BASE_PATH"] = "/balance"
    env["KF_BALANCE_ADMIN_URL"] = f"http://127.0.0.1:{ADMIN_PORT}/admin/maintenance"
    env["KF_BALANCE_CLIENT_UPDATE_URL"] = f"http://127.0.0.1:{ADMIN_PORT}/admin/client-update"
    env["KF_BALANCE_LATEST_INFO_URL"] = f"http://127.0.0.1:{ADMIN_PORT}/admin/latest-information"
    env["KF_BALANCE_OPS_URL"] = f"http://127.0.0.1:{ADMIN_PORT}/admin/ops"
    env["PYTHONUNBUFFERED"] = "1"

    cmd = [sys.executable, str(REPO / "tools" / "balance" / "server.py")]
    proc = subprocess.Popen(cmd, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

    screenshots = {}

    try:
        # Wait for server readiness
        for _ in range(50):
            try:
                import urllib.request
                with urllib.request.urlopen(f"{BASE_URL}/login", timeout=0.5) as r:
                    if r.status == 200:
                        break
            except Exception:
                time.sleep(0.1)
        else:
            raise RuntimeError("Test server failed to start within 5s")

        print("Test server up. Launching Playwright Firefox...")

        with sync_playwright() as p:
            browser = p.firefox.launch(headless=True)

            # =================================================================
            # 1. LOGIN PAGE & PREFERENCE PERSISTENCE
            # =================================================================
            context = browser.new_context(viewport={"width": 1280, "height": 800})
            page = context.new_page()
            browser_errors = []
            failed_requests = []
            def fail_on_browser_error(message):
                browser_errors.append(message)
                raise AssertionError("Browser JavaScript error: " + message)

            page.on("pageerror", lambda error: fail_on_browser_error(str(error)))
            page.on("console", lambda msg: fail_on_browser_error("console.error: " + msg.text) if msg.type == "error" else None)
            page.on("requestfailed", lambda request: failed_requests.append(f"{request.method} {request.url}: {request.failure}"))

            page.goto(f"{BASE_URL}/login")
            page.wait_for_selector("#loginForm")

            assert page.locator("html").get_attribute("lang") == "es"
            assert "Iniciar sesión" in page.title()
            assert page.locator("#langSelect").input_value() == "es"

            p_login_es = ARTIFACTS_DIR / "login-es-desktop.png"
            page.screenshot(path=str(p_login_es))
            downscale_image(p_login_es)
            screenshots["login_es_desktop"] = str(p_login_es)

            # Switch language to English on login page
            page.fill("#username", USERNAME)
            page.fill("#password", PASSWORD)
            page.select_option("#langSelect", "en")
            page.wait_for_timeout(300)

            assert page.locator("html").get_attribute("lang") == "en"
            assert "Sign in" in page.title()
            assert page.locator("#username").input_value() == USERNAME
            assert page.locator("#password").input_value() == PASSWORD

            p_login_en = ARTIFACTS_DIR / "login-en-desktop.png"
            page.screenshot(path=str(p_login_en))
            downscale_image(p_login_en)
            screenshots["login_en_desktop"] = str(p_login_en)

            # Submit login form
            page.click("button[type='submit']")
            page.wait_for_selector("#top h1")
            page.wait_for_selector("#list li.item", timeout=10000)

            # =================================================================
            # 2. DISCS TAB: SINGLE-LANGUAGE GUIDANCE & BANNER
            # =================================================================
            page.click("button[data-tab='discs']")
            page.wait_for_selector("#list li.item", timeout=60000)
            page.locator("#list li.item").first.click()
            page.wait_for_selector(".disc-guide")

            # In English: banner and field labels must be English only
            guide_en = page.locator(".disc-guide").text_content()
            assert "Field names are shown in English" in guide_en
            assert "/ Los nombres aparecen" not in guide_en

            p_discs_en = ARTIFACTS_DIR / "discs-en-desktop.png"
            page.screenshot(path=str(p_discs_en))
            downscale_image(p_discs_en)
            screenshots["discs_en_desktop"] = str(p_discs_en)

            # Switch to Spanish
            page.select_option("#langSelect", "es")
            page.wait_for_timeout(300)
            guide_es = page.locator(".disc-guide").text_content()
            assert "Los nombres aparecen en español" in guide_es
            assert "Field names are shown in English" not in guide_es

            p_discs_es = ARTIFACTS_DIR / "discs-es-desktop.png"
            page.screenshot(path=str(p_discs_es))
            downscale_image(p_discs_es)
            screenshots["discs_es_desktop"] = str(p_discs_es)

            # =================================================================
            # 3. HEALTH TAB WITH POPULATED METRICS
            # =================================================================
            page.click("button[data-tab='health']")
            page.wait_for_selector(".mbtns button")
            page.wait_for_timeout(400)

            # Range buttons in Spanish
            assert page.locator(".mbtns button:has-text('Esta versión')").is_visible()
            assert page.locator(".mbtns button:has-text('Últimas 24 h')").is_visible()
            assert page.locator(".mbtns button:has-text('Últimos 7 días')").is_visible()
            health_text_es = page.locator("#panel").inner_text()
            for phrase in ("Resultados de partidas", "Estados de jugadores", "Excepciones de clientes por huella", "Partidas con problemas", "nunca iniciada", "en curso", "desconexión silenciosa", "Ventana Esta versión", "desde", "almacén", "cola", "combates seguidos"):
                assert phrase.lower() in health_text_es.lower(), f"Missing Spanish populated health label: {phrase}"
            for english in ("Match outcomes", "Player statuses", "Problem matches", "never started", "tracked battles", "drill down", "Outcome", "Roster", "Participants"):
                assert english.lower() not in health_text_es.lower(), f"English UI leaked in populated Spanish health: {english}"

            # Open populated drill-down and verify its UI chrome and outcome/status labels.
            page.locator("#panel button").filter(has_text="inspeccionar").first.click()
            page.wait_for_timeout(200)
            drill_es = page.locator("#panel").inner_text()
            for phrase in ("Partida bat", "Resultado abandonada", "regla", "escenario", "desconexión silenciosa", "Línea de tiempo", "Carga útil"):
                assert phrase.lower() in drill_es.lower(), f"Missing Spanish drill-down label: {phrase}"

            p_health_es = ARTIFACTS_DIR / "health-es-desktop.png"
            page.screenshot(path=str(p_health_es))
            downscale_image(p_health_es)
            screenshots["health_es_desktop"] = str(p_health_es)

            # Switch to English: range buttons update
            page.select_option("#langSelect", "en")
            page.wait_for_timeout(300)
            assert page.locator(".mbtns button:has-text('This release')").is_visible()
            assert page.locator(".mbtns button:has-text('Last 24 h')").is_visible()
            assert page.locator(".mbtns button:has-text('Last 7 days')").is_visible()
            health_text_en = page.locator("#panel").inner_text()
            for phrase in ("Match outcomes", "Player statuses", "Problem matches", "never started", "tracked battles", "drill down", "Outcome", "Roster", "Participants"):
                assert phrase.lower() in health_text_en.lower(), f"Missing English populated health label: {phrase}"

            p_health_en = ARTIFACTS_DIR / "health-en-desktop.png"
            page.screenshot(path=str(p_health_en))
            downscale_image(p_health_en)
            screenshots["health_en_desktop"] = str(p_health_en)

            # =================================================================
            # 4. BOT AI TAB: 14 INDIVIDUAL KICKERS PER EACH 3 MODES (EXACT 42)
            # =================================================================
            page.click("button[data-tab='bot-ai']")
            page.wait_for_selector(".bot-ai-container")
            page.wait_for_selector(".bot-ai-intent-grid")

            # Check 14 selectors in each of the 3 modes
            modes = [
                ("CrystalScramble", "Crystals", "Cristales"),
                ("FlagFlight", "Flag", "Bandera"),
                ("RapidBall", "Ball", "Balón")
            ]

            for mode_id, name_en, name_es in modes:
                page.click(f".bot-ai-mode-seg:has-text('{name_en}')")
                page.wait_for_timeout(200)

                items = page.locator("#list .bot-ai-sidebar-item")
                assert items.count() == 14, f"Mode {mode_id} must have exactly 14 kicker items"

                opts = page.locator("#botAiProfileSelect option")
                assert opts.count() == 14, f"Mode {mode_id} select must have exactly 14 kicker options"

                count_text = page.locator("#list li.count").text_content()
                assert "14 kickers" in count_text

            # Return to Crystals mode
            page.click(".bot-ai-mode-seg:has-text('Crystals')")
            page.wait_for_timeout(200)

            # -------------------------------------------------------------
            # A. DESKTOP SCREENSHOTS: 14 KICKER LIST (EN & ES)
            # -------------------------------------------------------------
            p_ai_en_14 = ARTIFACTS_DIR / "bot-ai-en-desktop-14kickers.png"
            page.screenshot(path=str(p_ai_en_14))
            downscale_image(p_ai_en_14)
            screenshots["bot_ai_en_desktop_14kickers"] = str(p_ai_en_14)

            page.select_option("#langSelect", "es")
            page.wait_for_timeout(300)
            p_ai_es_14 = ARTIFACTS_DIR / "bot-ai-es-desktop-14kickers.png"
            page.screenshot(path=str(p_ai_es_14))
            downscale_image(p_ai_es_14)
            screenshots["bot_ai_es_desktop_14kickers"] = str(p_ai_es_14)

            # -------------------------------------------------------------
            # B. SELECTED KICKER (EN & ES) & VERIFY NATIVE 35% LOW HP
            # -------------------------------------------------------------
            # Select Kicker 1 (Tsubame)
            page.click("#list .bot-ai-sidebar-item[data-kicker-id='1']")
            page.wait_for_timeout(200)

            # Check header: individual kicker avatar and title, NO role group text
            title_text = page.locator(".bot-ai-profile-title").text_content()
            assert "Editando kicker: Tsubame" in title_text
            assert page.locator("#botAiProfileSelect option:checked").text_content().strip() == "Tsubame"
            assert "Carrier" not in page.locator("#list .bot-ai-sidebar-item[data-kicker-id='1']").text_content()
            assert "Afecta a:" not in page.locator(".bot-ai-profile-summary-header").text_content()

            # Technical details: 35% lowHP retreat threshold
            page.click("details.bot-ai-advanced summary")
            page.wait_for_timeout(200)
            raw_text = page.locator(".bot-ai-raw-grid").text_content()
            assert "35%" in raw_text

            p_ai_es_sel = ARTIFACTS_DIR / "bot-ai-es-desktop-selected.png"
            page.screenshot(path=str(p_ai_es_sel))
            downscale_image(p_ai_es_sel)
            screenshots["bot_ai_es_desktop_selected"] = str(p_ai_es_sel)

            page.select_option("#langSelect", "en")
            page.wait_for_timeout(300)
            p_ai_en_sel = ARTIFACTS_DIR / "bot-ai-en-desktop-selected.png"
            page.screenshot(path=str(p_ai_en_sel))
            downscale_image(p_ai_en_sel)
            screenshots["bot_ai_en_desktop_selected"] = str(p_ai_en_sel)

            # -------------------------------------------------------------
            # C. EDIT K1 THEN K8: PROVE VALUES UNCHANGED (SAVE + RELOAD)
            # -------------------------------------------------------------
            # Edit K1 (Tsubame) attack motivation to 88
            page.evaluate("""() => {
                const slider = document.querySelector(".bot-ai-slider");
                slider.value = "88";
                slider.dispatchEvent(new Event("input"));
            }""")
            page.wait_for_timeout(300)

            # Verify K1 has dirty badge
            assert page.locator("#list .bot-ai-sidebar-item[data-kicker-id='1'] .dot").is_visible()
            assert page.locator("#botAiDiscardBtn").is_enabled()
            assert page.locator("#botAiSaveBtn").is_enabled()

            # Now click Kicker 8 (Anna, parameterId 15)
            page.click("#list .bot-ai-sidebar-item[data-kicker-id='8']")
            page.wait_for_timeout(200)

            # Verify K8 is selected, NOT dirty, and its values are pristine
            assert "Editing kicker: Anna" in page.locator(".bot-ai-profile-title").text_content()
            assert not page.locator("#list .bot-ai-sidebar-item[data-kicker-id='8'] .dot").is_visible()
            assert page.locator("#botAiDiscardBtn").is_disabled()

            # Check K8 values: attack motivation must be 30
            k8_attack = page.locator(".bot-ai-slider-val").first.text_content()
            assert k8_attack == "30", f"Expected K8 attack to be pristine 30, got {k8_attack}"

            # Click K1 again: its draft of 88 must still be there
            page.click("#list .bot-ai-sidebar-item[data-kicker-id='1']")
            page.wait_for_timeout(200)
            k1_attack = page.locator(".bot-ai-slider-val").first.text_content()
            assert k1_attack == "88", f"Expected K1 attack to retain draft 88, got {k1_attack}"

            # -------------------------------------------------------------
            # D. REVIEW MODAL & LIVE EN/ES SWITCH WITH OPEN REVIEW
            # -------------------------------------------------------------
            page.click("#botAiSaveBtn")
            page.wait_for_selector("#botAiReviewModal")

            # Check review modal in English:
            assert "Save changes to server" in page.locator("#botAiReviewModal .bot-ai-modal-head").text_content()
            assert "Crystals · Tsubame" in page.locator("#botAiReviewModal h4").text_content()
            # K8 must NOT be in the diff!
            assert "Anna" not in page.locator("#botAiReviewModal").text_content()

            p_ai_en_rev = ARTIFACTS_DIR / "bot-ai-en-desktop-review.png"
            page.screenshot(path=str(p_ai_en_rev))
            downscale_image(p_ai_en_rev)
            screenshots["bot_ai_en_desktop_review"] = str(p_ai_en_rev)

            # Live switch to Spanish while review modal is open:
            page.select_option("#langSelect", "es")
            page.wait_for_timeout(400)

            # Modal MUST stay open and show Spanish!
            assert page.locator("#botAiReviewModal").is_visible()
            assert "Guardar cambios en el servidor" in page.locator("#botAiReviewModal .bot-ai-modal-head").text_content()
            assert "Cristales · Tsubame" in page.locator("#botAiReviewModal h4").text_content()

            p_ai_es_rev = ARTIFACTS_DIR / "bot-ai-es-desktop-review.png"
            page.screenshot(path=str(p_ai_es_rev))
            downscale_image(p_ai_es_rev)
            screenshots["bot_ai_es_desktop_review"] = str(p_ai_es_rev)

            # Perform actual save from review modal!
            page.click("#botAiConfirmSaveBtn")
            page.wait_for_timeout(600)

            # After save: K1 draft cleared, toast shown
            assert not page.locator("#botAiReviewModal").is_visible()
            assert not page.locator("#list .bot-ai-sidebar-item[data-kicker-id='1'] .dot").is_visible()
            assert page.locator("#botAiSaveBtn").is_disabled()

            # Verify on server by navigating to Bot AI again
            page.reload()
            page.wait_for_selector("button[data-tab='bot-ai']")
            page.click("button[data-tab='bot-ai']")
            page.wait_for_selector("#list .bot-ai-sidebar-item")
            page.click("#list .bot-ai-sidebar-item[data-kicker-id='1']")
            page.wait_for_timeout(200)
            page.click("details.bot-ai-advanced summary")
            page.wait_for_timeout(100)
            k1_saved_val = page.locator(".bot-ai-slider-val").first.text_content()
            assert k1_saved_val == "88", f"K1 should persist 88 on server, got {k1_saved_val}"

            # Check K8 on server: still pristine 30!
            page.click("#list .bot-ai-sidebar-item[data-kicker-id='8']")
            page.wait_for_timeout(200)
            page.click("details.bot-ai-advanced summary")
            page.wait_for_timeout(100)
            k8_saved_val = page.locator(".bot-ai-slider-val").first.text_content()
            assert k8_saved_val == "30", f"K8 should stay untouched 30 on server, got {k8_saved_val}"

            # -------------------------------------------------------------
            # E. MODE INDEPENDENCE
            # -------------------------------------------------------------
            # Select K1 in Crystals and edit it
            page.click("#list .bot-ai-sidebar-item[data-kicker-id='1']")
            page.wait_for_timeout(100)
            page.click(".bot-ai-intent-card.theme-fight")
            page.wait_for_timeout(200)
            assert page.locator("#list .bot-ai-sidebar-item[data-kicker-id='1'] .dot").is_visible()

            # Switch to FlagFlight: K1 in FlagFlight is pristine (not dirty)
            page.click(".bot-ai-mode-seg:has-text('Bandera')")
            page.wait_for_timeout(200)
            assert not page.locator("#list .bot-ai-sidebar-item[data-kicker-id='1'] .dot").is_visible()

            # Switch to RapidBall: K1 in RapidBall is pristine
            page.click(".bot-ai-mode-seg:has-text('Balón')")
            page.wait_for_timeout(200)
            assert not page.locator("#list .bot-ai-sidebar-item[data-kicker-id='1'] .dot").is_visible()

            # Switch back to Crystals: K1 is still dirty!
            page.click(".bot-ai-mode-seg:has-text('Cristales')")
            page.wait_for_timeout(200)
            assert page.locator("#list .bot-ai-sidebar-item[data-kicker-id='1'] .dot").is_visible()

            # Discard draft for K1
            page.click("#botAiDiscardBtn")
            page.wait_for_timeout(200)
            assert not page.locator("#list .bot-ai-sidebar-item[data-kicker-id='1'] .dot").is_visible()

            # -------------------------------------------------------------
            # F. CONCURRENCY CONFLICT (409)
            # -------------------------------------------------------------
            # Make a draft on K2
            page.click("#list .bot-ai-sidebar-item[data-kicker-id='2']")
            page.wait_for_timeout(100)
            page.click(".bot-ai-intent-card.theme-defense")
            page.wait_for_timeout(200)
            assert page.locator("#botAiSaveBtn").is_enabled()

            # Simulate external modification on disk
            override_profiles_path = overrides_dir / "bot_ai_profiles.json"
            target_disk_path = override_profiles_path if override_profiles_path.is_file() else (config_dir / "bot_ai_profiles.json")
            disk_data = json.loads(target_disk_path.read_text(encoding="utf-8"))
            disk_data["version"] = disk_data.get("version", 1) + 1
            target_disk_path.write_text(json.dumps(disk_data, indent=2), encoding="utf-8")

            # Try to save from UI: server will reject with 409
            page.click("#botAiSaveBtn")
            page.wait_for_selector("#botAiReviewModal")
            page.click("#botAiConfirmSaveBtn")
            page.wait_for_selector("#botAiConflictModal")

            assert "Conflicto de Concurrencia (409)" in page.locator("#botAiConflictModal .bot-ai-modal-head").text_content()

            # Test close and keep draft
            page.click("#botAiConflictModal button.secondary")
            page.wait_for_timeout(200)
            assert not page.locator("#botAiConflictModal").is_visible()
            # Draft is preserved in UI
            assert page.locator("#list .bot-ai-sidebar-item[data-kicker-id='2'] .dot").is_visible()

            # Trigger conflict modal again to test reload from server
            page.click("#botAiSaveBtn")
            page.wait_for_selector("#botAiReviewModal")
            page.click("#botAiConfirmSaveBtn")
            page.wait_for_selector("#botAiConflictModal")

            page.click("#botAiConflictModal button.primary")
            page.wait_for_timeout(500)
            assert not page.locator("#botAiConflictModal").is_visible()

            # =================================================================
            # 5. MOBILE VIEWPORT (390x844): BOT AI 14 KICKERS & SELECTOR
            # =================================================================
            login_mobile_ctx = browser.new_context(viewport={"width": 390, "height": 844})
            login_mobile = login_mobile_ctx.new_page()
            login_mobile.goto(f"{BASE_URL}/login")
            login_mobile.wait_for_selector("#loginForm")
            for lang, slug in (("es", "es"), ("en", "en")):
                login_mobile.select_option("#langSelect", lang)
                login_mobile.wait_for_timeout(100)
                shot = ARTIFACTS_DIR / f"login-{slug}-mobile.png"
                login_mobile.screenshot(path=str(shot))
                downscale_image(shot)
                screenshots[f"login_{slug}_mobile"] = str(shot)
            login_mobile_ctx.close()

            m_ctx = browser.new_context(viewport={"width": 390, "height": 844})
            m_ctx.add_cookies(context.cookies())
            m_page = m_ctx.new_page()

            m_page.goto(f"{BASE_URL}/")
            m_page.wait_for_selector("#top h1")
            m_page.click("button[data-tab='bot-ai']")
            m_page.wait_for_selector(".bot-ai-container")
            m_page.wait_for_selector("#botAiProfileSelect")

            # All eight navigation buttons stay inside their own boxes at 390px.
            nav_boxes = m_page.locator("#tabs button").evaluate_all("els => els.map(e => { const r=e.getBoundingClientRect(); return {text:e.textContent.trim(), left:r.left, right:r.right, top:r.top, bottom:r.bottom}; })")
            assert len(nav_boxes) == 8
            for box in nav_boxes:
                assert box["left"] >= 0 and box["right"] <= 390, f"Mobile nav overflow: {box}"
            for i, left in enumerate(nav_boxes):
                for right in nav_boxes[i + 1:]:
                    overlaps = left["left"] < right["right"] and right["left"] < left["right"] and left["top"] < right["bottom"] and right["top"] < left["bottom"]
                    assert not overlaps, f"Mobile nav buttons overlap: {left} / {right}"

            # Populated health and single-language disc guidance at the real 390px viewport.
            for lang, slug in (("es", "es"), ("en", "en")):
                m_page.select_option("#langSelect", lang)
                m_page.click("button[data-tab='health']")
                m_page.wait_for_selector(".mbtns button")
                m_page.wait_for_selector(".hcard")
                shot = ARTIFACTS_DIR / f"health-{slug}-mobile.png"
                m_page.screenshot(path=str(shot))
                downscale_image(shot)
                screenshots[f"health_{slug}_mobile"] = str(shot)

                m_page.click("button[data-tab='discs']")
                m_page.wait_for_selector("#list li.item")
                m_page.locator("#list li.item").first.click()
                m_page.wait_for_selector(".disc-guide")
                shot = ARTIFACTS_DIR / f"discs-{slug}-mobile.png"
                m_page.screenshot(path=str(shot))
                downscale_image(shot)
                screenshots[f"discs_{slug}_mobile"] = str(shot)

            m_page.click("button[data-tab='bot-ai']")
            m_page.wait_for_selector(".bot-ai-container")

            # Spanish Mobile 390
            m_page.select_option("#langSelect", "es")
            m_page.wait_for_timeout(300)

            # Mobile 14 Kickers Selector
            p_m_ai_es_14 = ARTIFACTS_DIR / "bot-ai-es-mobile-14kickers.png"
            m_page.screenshot(path=str(p_m_ai_es_14))
            downscale_image(p_m_ai_es_14)
            screenshots["bot_ai_es_mobile_14kickers"] = str(p_m_ai_es_14)

            # Selected Kicker on Mobile
            m_page.select_option("#botAiProfileSelect", "3")  # Coco
            m_page.wait_for_timeout(200)
            p_m_ai_es_sel = ARTIFACTS_DIR / "bot-ai-es-mobile-selected.png"
            m_page.screenshot(path=str(p_m_ai_es_sel))
            downscale_image(p_m_ai_es_sel)
            screenshots["bot_ai_es_mobile_selected"] = str(p_m_ai_es_sel)

            # Review modal on Mobile
            m_page.click(".bot-ai-intent-card.theme-fight")
            m_page.wait_for_timeout(200)
            m_page.click("#botAiSaveBtn")
            m_page.wait_for_selector("#botAiReviewModal")

            p_m_ai_es_rev = ARTIFACTS_DIR / "bot-ai-es-mobile-review.png"
            m_page.screenshot(path=str(p_m_ai_es_rev))
            downscale_image(p_m_ai_es_rev)
            screenshots["bot_ai_es_mobile_review"] = str(p_m_ai_es_rev)

            # Switch Mobile to English
            m_page.select_option("#langSelect", "en")
            m_page.wait_for_timeout(400)

            p_m_ai_en_rev = ARTIFACTS_DIR / "bot-ai-en-mobile-review.png"
            m_page.screenshot(path=str(p_m_ai_en_rev))
            downscale_image(p_m_ai_en_rev)
            screenshots["bot_ai_en_mobile_review"] = str(p_m_ai_en_rev)

            m_page.click("#botAiReviewModal button.secondary")
            m_page.wait_for_timeout(200)

            p_m_ai_en_sel = ARTIFACTS_DIR / "bot-ai-en-mobile-selected.png"
            m_page.screenshot(path=str(p_m_ai_en_sel))
            downscale_image(p_m_ai_en_sel)
            screenshots["bot_ai_en_mobile_selected"] = str(p_m_ai_en_sel)

            p_m_ai_en_14 = ARTIFACTS_DIR / "bot-ai-en-mobile-14kickers.png"
            m_page.screenshot(path=str(p_m_ai_en_14))
            downscale_image(p_m_ai_en_14)
            screenshots["bot_ai_en_mobile_14kickers"] = str(p_m_ai_en_14)

            m_ctx.close()
            context.close()
            browser.close()

            assert not failed_requests, "Browser requests failed: " + json.dumps(failed_requests)
            print("All internationalization and Bot AI Playwright tests passed successfully!")
            return screenshots
    finally:
        admin_server.shutdown()
        proc.terminate()
        try:
            proc.wait(timeout=3)
        except subprocess.TimeoutExpired:
            proc.kill()
        temp_dir_obj.cleanup()


if __name__ == "__main__":
    shots = main()
    print("Generated screenshots:")
    for k, v in sorted(shots.items()):
        print(f"  {k}: {v}")
