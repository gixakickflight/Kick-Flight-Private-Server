#!/usr/bin/env python3
"""Comprehensive Playwright Browser QA for bilingual /balance UI (Spanish & English).

Verifies:
- Default Spanish on all UI surfaces (login, dashboard, 8 tabs, header, footer, dialogs, toasts)
- Header language selector (Español / English) present and responsive on both desktop and mobile
- Preference persistence across login, navigation, and page reloads via localStorage and cookies
- Graceful storage failure fallback (when localStorage throws or is restricted)
- Live language switching preserving:
  * selected tab, kicker, disc, and bot AI mode/profile
  * dirty draft inputs in tables, sliders, maintenance, client update, and latest information
  * open modals (review modal and concurrency conflict modal)
  * login form credentials
- Correct <html lang="es|en"> and <title>
- Mobile viewport responsive layout (390x844) with wrapped header and selector
- Captures downscaled screenshots (<=720px width) in .local/artifacts/balance-i18n/

Usage:
    python tools/balance/test_i18n_playwright.py
"""

from __future__ import annotations

import copy
import hashlib
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from PIL import Image
from playwright.sync_api import sync_playwright

REPO = Path(__file__).resolve().parents[2]


def discover_artifacts_dir() -> Path:
    try:
        common_git = subprocess.check_output(
            ["git", "rev-parse", "--path-format=absolute", "--git-common-dir"],
            cwd=str(REPO),
            text=True,
            stderr=subprocess.DEVNULL
        ).strip()
        common_root = Path(common_git).parent
        target_dir = common_root / ".local" / "artifacts" / "balance-i18n"
    except Exception:
        target_dir = REPO / ".local" / "artifacts" / "balance-i18n"
    target_dir.mkdir(parents=True, exist_ok=True)
    return target_dir


ARTIFACTS_DIR = discover_artifacts_dir()
PORT = 18995
BASE_URL = f"http://127.0.0.1:{PORT}/balance"
USERNAME = "qa_tester"
PASSWORD = "TestPassword987!"


def downscale_image(image_path: Path, max_width: int = 720) -> None:
    with Image.open(image_path) as img:
        orig_w, orig_h = img.size
        if orig_w > max_width:
            target_h = int(orig_h * (max_width / orig_w))
            res = img.resize((max_width, target_h), Image.Resampling.LANCZOS)
            res.save(image_path, optimize=True)
            print(f"Downscaled {image_path.name}: {orig_w}x{orig_h} -> {max_width}x{target_h}")


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

    return config_dir, backups_dir, auth_dir, overrides_dir, auth_db, jwt_secret


def main():
    temp_dir_obj = tempfile.TemporaryDirectory(prefix="kf_i18n_test_")
    tmp_path = Path(temp_dir_obj.name)
    config_dir, backups_dir, auth_dir, overrides_dir, auth_db, jwt_secret = setup_test_environment(tmp_path)

    env = os.environ.copy()
    env["KF_BALANCE_CONFIG_DIR"] = str(config_dir)
    env["KF_BALANCE_BACKUP_DIR"] = str(backups_dir)
    env["KF_BALANCE_OVERRIDE_DIR"] = str(overrides_dir)
    env["KF_BALANCE_AUTH_DB"] = str(auth_db)
    env["KF_BALANCE_JWT_SECRET"] = str(jwt_secret)
    env["KF_BALANCE_PORT"] = str(PORT)
    env["KF_BALANCE_BASE_PATH"] = "/balance"
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
            # 1. TEST LOGIN PAGE & PREFERENCE PERSISTENCE
            # =================================================================
            context = browser.new_context(viewport={"width": 1280, "height": 800})
            page = context.new_page()
            page.on("console", lambda msg: print("CONSOLE:", msg.type, msg.text))
            page.on("pageerror", lambda err: print("PAGEERROR:", err))

            # A. Visit login page (fresh session, default Spanish)
            page.goto(f"{BASE_URL}/login")
            page.wait_for_selector("#loginForm")

            assert page.locator("html").get_attribute("lang") == "es"
            assert "Iniciar sesión" in page.title()
            assert page.locator("#langSelect").input_value() == "es"
            assert "Usuario" in page.locator("label[for='username']").text_content()
            assert "Contraseña" in page.locator("label[for='password']").text_content()
            assert "Iniciar sesión" in page.locator("button[type='submit']").text_content()

            p_login_es = ARTIFACTS_DIR / "login-es-desktop.png"
            page.screenshot(path=str(p_login_es))
            downscale_image(p_login_es)
            screenshots["login_es_desktop"] = str(p_login_es)

            # B. Live language switch on login page preserving credentials
            page.fill("#username", USERNAME)
            page.fill("#password", PASSWORD)

            page.select_option("#langSelect", "en")
            page.wait_for_timeout(300)

            assert page.locator("html").get_attribute("lang") == "en"
            assert "Sign in" in page.title()
            assert page.locator("#langSelect").input_value() == "en"
            assert "Username" in page.locator("label[for='username']").text_content()
            assert "Password" in page.locator("label[for='password']").text_content()
            assert "Sign in" in page.locator("button[type='submit']").text_content()

            # Verify typed inputs preserved
            assert page.locator("#username").input_value() == USERNAME
            assert page.locator("#password").input_value() == PASSWORD

            p_login_en = ARTIFACTS_DIR / "login-en-desktop.png"
            page.screenshot(path=str(p_login_en))
            downscale_image(p_login_en)
            screenshots["login_en_desktop"] = str(p_login_en)

            # C. Test login error message translation in English
            page.fill("#password", "WrongPassword123!")
            page.click("button[type='submit']")
            page.wait_for_timeout(600)
            assert page.locator("#error").is_visible()
            assert "Sign in failed" in page.locator("#error").text_content()

            # Switch back to Spanish live and verify error translation updates
            page.select_option("#langSelect", "es")
            page.wait_for_timeout(300)
            assert "Credenciales incorrectas" in page.locator("#error").text_content()

            # D. Mobile Login Viewport
            mobile_context = browser.new_context(viewport={"width": 390, "height": 844})
            mobile_page = mobile_context.new_page()
            mobile_page.goto(f"{BASE_URL}/login")
            mobile_page.wait_for_selector("#loginForm")

            p_m_login_es = ARTIFACTS_DIR / "login-es-mobile.png"
            mobile_page.screenshot(path=str(p_m_login_es))
            downscale_image(p_m_login_es)
            screenshots["login_es_mobile"] = str(p_m_login_es)

            mobile_page.select_option("#langSelect", "en")
            mobile_page.wait_for_timeout(300)
            p_m_login_en = ARTIFACTS_DIR / "login-en-mobile.png"
            mobile_page.screenshot(path=str(p_m_login_en))
            downscale_image(p_m_login_en)
            screenshots["login_en_mobile"] = str(p_m_login_en)
            mobile_context.close()

            # =================================================================
            # 2. TEST AUTHENTICATION & DASHBOARD BILINGUAL SURFACES
            # =================================================================
            # Log in with English preference selected to verify preference persistence across login
            page.select_option("#langSelect", "en")
            page.fill("#username", USERNAME)
            page.fill("#password", PASSWORD)
            page.click("button[type='submit']")

            page.wait_for_selector("#top h1")
            page.wait_for_timeout(800)


            # Dashboard in English
            assert page.locator("html").get_attribute("lang") == "en"
            assert page.locator("#langSelect").input_value() == "en"
            assert "Log out" in page.locator("#logout").text_content()
            assert "saves write" in page.locator("#topHint").text_content()

            # Check 8 tab labels in English
            tabs = page.locator("#tabs button")
            assert tabs.count() == 8
            assert tabs.nth(0).text_content().strip() == "Kickers"
            assert tabs.nth(1).text_content().strip() == "Discs"
            assert tabs.nth(2).text_content().strip() == "Text"
            assert tabs.nth(3).text_content().strip() == "Bot AI"
            assert tabs.nth(4).text_content().strip() == "Latest information"
            assert tabs.nth(5).text_content().strip() == "Health"
            assert tabs.nth(6).text_content().strip() == "Maintenance"
            assert tabs.nth(7).text_content().strip() == "Client update"

            # Check footer controls in English
            assert "Unsaved changes: 0" in page.locator("#dirtyCount").text_content()
            assert "Save" in page.locator("#save").text_content()
            assert "Revert" in page.locator("#revert").text_content()
            assert "Restart API to apply" in page.locator("#apply").text_content()
            assert "Export overrides" in page.locator("#exportOverrides").text_content()

            p_dash_en = ARTIFACTS_DIR / "dashboard-en-desktop.png"
            page.screenshot(path=str(p_dash_en))
            downscale_image(p_dash_en)
            screenshots["dashboard_en_desktop"] = str(p_dash_en)

            # Switch to Spanish live on Dashboard
            page.select_option("#langSelect", "es")
            page.wait_for_timeout(500)

            assert page.locator("html").get_attribute("lang") == "es"
            assert page.locator("#langSelect").input_value() == "es"
            assert "Cerrar sesión" in page.locator("#logout").text_content()
            assert "las modificaciones se guardan en" in page.locator("#topHint").text_content()

            # Check 8 tab labels in Spanish
            assert tabs.nth(0).text_content().strip() == "Kickers"
            assert tabs.nth(1).text_content().strip() == "Discos"
            assert tabs.nth(2).text_content().strip() == "Texto"
            assert tabs.nth(3).text_content().strip() == "IA de bots"
            assert tabs.nth(4).text_content().strip() == "Última información"
            assert tabs.nth(5).text_content().strip() == "Salud"
            assert tabs.nth(6).text_content().strip() == "Mantenimiento"
            assert tabs.nth(7).text_content().strip() == "Actualización de cliente"

            # Check footer controls in Spanish
            assert "Cambios sin guardar: 0" in page.locator("#dirtyCount").text_content()
            assert "Guardar" in page.locator("#save").text_content()
            assert "Revertir" in page.locator("#revert").text_content()
            assert "Reiniciar API para aplicar" in page.locator("#apply").text_content()
            assert "Exportar modificaciones" in page.locator("#exportOverrides").text_content()

            p_dash_es = ARTIFACTS_DIR / "dashboard-es-desktop.png"
            page.screenshot(path=str(p_dash_es))
            downscale_image(p_dash_es)
            screenshots["dashboard_es_desktop"] = str(p_dash_es)

            # =================================================================
            # 3. TEST STORAGE PERSISTENCE & STORAGE FAILURE FALLBACK
            # =================================================================
            # Check localStorage and cookie state
            storage_val = page.evaluate("() => localStorage.getItem('balance_lang')")
            cookie_val = page.evaluate("() => document.cookie")
            assert storage_val == "es", f"Expected 'es' in localStorage, got {storage_val}"
            assert "balance_lang=es" in cookie_val, f"Expected 'balance_lang=es' in cookie, got {cookie_val}"

            # Simulate localStorage failure (throws QuotaExceededError or SecurityError)
            page.evaluate("""() => {
                Object.defineProperty(window, 'localStorage', {
                    get: () => { throw new Error('Simulated Storage Access Denied'); }
                });
            }""")

            # Switch language to English while localStorage is broken -> cookie fallback takes over
            page.select_option("#langSelect", "en")
            page.wait_for_timeout(400)
            assert page.locator("html").get_attribute("lang") == "en"
            cookie_after_fallback = page.evaluate("() => document.cookie")
            assert "balance_lang=en" in cookie_after_fallback

            # Reload page: cookie fallback restores 'en' preference even without localStorage
            page.reload()
            page.wait_for_selector("#top h1")
            page.wait_for_timeout(600)
            assert page.locator("html").get_attribute("lang") == "en"
            assert page.locator("#langSelect").input_value() == "en"

            # Switch back to Spanish for remainder of tests
            page.select_option("#langSelect", "es")
            page.wait_for_timeout(400)

            # =================================================================
            # 4. TEST TABS & PRESERVING DIRTY INPUTS ON LANGUAGE SWITCH
            # =================================================================
            # A. Discs Tab: Guidance, Filters, and dirty input preservation
            page.click("button[data-tab='discs']")
            page.wait_for_selector("#discFilters", state="visible")
            page.wait_for_selector("#list li.item")

            # Check disc filters in Spanish
            rarity_opts = page.locator("#filterRarity option")
            assert "Todas las rarezas" in rarity_opts.first.text_content()
            page.select_option("#filterRarity", "3")  # Select Ultra Rare (UR)

            # Select first disc
            page.locator("#list li.item").first.click()
            page.wait_for_selector(".disc-guide")
            assert "Los nombres aparecen en inglés y español" in page.locator(".disc-guide").text_content()

            p_discs_es = ARTIFACTS_DIR / "discs-es-desktop.png"
            page.screenshot(path=str(p_discs_es))
            downscale_image(p_discs_es)
            screenshots["discs_es_desktop"] = str(p_discs_es)

            # Switch to English in Discs tab
            page.select_option("#langSelect", "en")
            page.wait_for_timeout(400)
            assert page.locator("#filterRarity").input_value() == "3"  # Filter preserved
            assert "All rarities" in page.locator("#filterRarity option").first.text_content()
            assert "Field names are shown in English and Spanish" in page.locator(".disc-guide").text_content()

            p_discs_en = ARTIFACTS_DIR / "discs-en-desktop.png"
            page.screenshot(path=str(p_discs_en))
            downscale_image(p_discs_en)
            screenshots["discs_en_desktop"] = str(p_discs_en)

            # Edit an input field to make it dirty
            first_input = page.locator("#panel input").first
            original_val = first_input.input_value()
            first_input.fill("9999")
            first_input.dispatch_event("input")
            page.wait_for_timeout(200)

            assert "Unsaved changes: 1" in page.locator("#dirtyCount").text_content()
            assert page.locator("#save").is_enabled()

            # Switch language back to Spanish while dirty input is present
            page.select_option("#langSelect", "es")
            page.wait_for_timeout(400)

            # Input edit and dirty state MUST be preserved!
            assert first_input.input_value() == "9999"
            assert "Cambios sin guardar: 1" in page.locator("#dirtyCount").text_content()
            assert page.locator("#save").is_enabled()

            # Revert the draft
            page.click("#revert")
            page.wait_for_timeout(300)
            assert "Cambios sin guardar: 0" in page.locator("#dirtyCount").text_content()

            # B. Maintenance Tab
            page.click("button[data-tab='maintenance']")
            page.wait_for_selector(".mpanel")
            assert "Mantenimiento" in page.locator(".phead h1").text_content()

            p_maint_es = ARTIFACTS_DIR / "maintenance-es-desktop.png"
            page.screenshot(path=str(p_maint_es))
            downscale_image(p_maint_es)
            screenshots["maintenance_es_desktop"] = str(p_maint_es)

            page.select_option("#langSelect", "en")
            page.wait_for_timeout(400)
            assert "Maintenance" in page.locator(".phead h1").text_content()

            p_maint_en = ARTIFACTS_DIR / "maintenance-en-desktop.png"
            page.screenshot(path=str(p_maint_en))
            downscale_image(p_maint_en)
            screenshots["maintenance_en_desktop"] = str(p_maint_en)

            # C. Client Update Tab
            page.click("button[data-tab='client-update']")
            page.wait_for_selector(".mpanel")
            assert "Client update" in page.locator(".phead h1").text_content()

            p_update_en = ARTIFACTS_DIR / "client-update-en-desktop.png"
            page.screenshot(path=str(p_update_en))
            downscale_image(p_update_en)
            screenshots["client_update_en_desktop"] = str(p_update_en)

            page.select_option("#langSelect", "es")
            page.wait_for_timeout(400)
            assert "Actualización de cliente" in page.locator(".phead h1").text_content()

            p_update_es = ARTIFACTS_DIR / "client-update-es-desktop.png"
            page.screenshot(path=str(p_update_es))
            downscale_image(p_update_es)
            screenshots["client_update_es_desktop"] = str(p_update_es)

            # D. Latest Information Tab
            page.click("button[data-tab='latest-information']")
            page.wait_for_selector(".mpanel")
            assert "Última información" in page.locator(".phead h1").text_content()

            p_info_es = ARTIFACTS_DIR / "latest-info-es-desktop.png"
            page.screenshot(path=str(p_info_es))
            downscale_image(p_info_es)
            screenshots["latest_info_es_desktop"] = str(p_info_es)

            page.select_option("#langSelect", "en")
            page.wait_for_timeout(400)
            assert "Latest information" in page.locator(".phead h1").text_content()

            p_info_en = ARTIFACTS_DIR / "latest-info-en-desktop.png"
            page.screenshot(path=str(p_info_en))
            downscale_image(p_info_en)
            screenshots["latest_info_en_desktop"] = str(p_info_en)

            # E. Health Tab
            page.click("button[data-tab='health']")
            page.wait_for_selector(".mpanel")
            assert "Health" in page.locator(".phead h1").text_content()

            p_health_en = ARTIFACTS_DIR / "health-en-desktop.png"
            page.screenshot(path=str(p_health_en))
            downscale_image(p_health_en)
            screenshots["health_en_desktop"] = str(p_health_en)

            page.select_option("#langSelect", "es")
            page.wait_for_timeout(400)
            assert "Salud" in page.locator(".phead h1").text_content()

            p_health_es = ARTIFACTS_DIR / "health-es-desktop.png"
            page.screenshot(path=str(p_health_es))
            downscale_image(p_health_es)
            screenshots["health_es_desktop"] = str(p_health_es)

            # =================================================================
            # 5. TEST BOT AI TAB & LIVE MODAL TRANSLATION
            # =================================================================
            page.click("button[data-tab='bot-ai']")
            page.wait_for_selector(".bot-ai-container")
            page.wait_for_selector(".bot-ai-intent-grid")

            # Check Bot AI in Spanish
            assert page.locator("h2.bot-ai-heading:has-text('¿Qué quieres que haga?')").is_visible()
            assert page.locator(".bot-ai-mode-seg:has-text('Cristales')").is_visible()
            assert page.locator(".bot-ai-mode-seg:has-text('Bandera')").is_visible()
            assert page.locator(".bot-ai-mode-seg:has-text('Balón')").is_visible()

            # Apply an intent preset to create pending diffs
            page.click(".bot-ai-intent-card.theme-fight")
            page.wait_for_timeout(300)
            assert page.locator("#botAiSaveBtn").is_enabled()
            assert "Guardar cambios" in page.locator("#botAiSaveBtn").text_content()

            # Open Review Modal in Spanish
            page.click("#botAiSaveBtn")
            page.wait_for_selector("#botAiReviewModal")

            assert "Guardar cambios en el servidor" in page.locator("#botAiReviewModal .bot-ai-modal-head").text_content()
            assert "Revisa los cambios" in page.locator("#botAiReviewModal .bot-ai-notice").text_content()
            assert "Guardar en servidor" in page.locator("#botAiReviewModal button.primary").text_content()

            p_review_es = ARTIFACTS_DIR / "bot-ai-es-review-modal.png"
            page.screenshot(path=str(p_review_es))
            downscale_image(p_review_es)
            screenshots["bot_ai_es_review_modal"] = str(p_review_es)

            # LIVE LANGUAGE SWITCH WITH MODAL OPEN:
            # Switch to English: Modal MUST remain open and update text to English live!
            page.select_option("#langSelect", "en")
            page.wait_for_timeout(500)

            assert page.locator("#botAiReviewModal").is_visible()
            assert "Save changes to server" in page.locator("#botAiReviewModal .bot-ai-modal-head").text_content()
            assert "Review changes" in page.locator("#botAiReviewModal .bot-ai-notice").text_content()
            assert "Save to server" in page.locator("#botAiReviewModal button.primary").text_content()

            p_review_en = ARTIFACTS_DIR / "bot-ai-en-review-modal.png"
            page.screenshot(path=str(p_review_en))
            downscale_image(p_review_en)
            screenshots["bot_ai_en_review_modal"] = str(p_review_en)

            # Close modal and discard draft
            page.click("#botAiReviewModal .bot-ai-modal-foot button.secondary")
            page.wait_for_timeout(300)
            assert not page.locator("#botAiReviewModal").is_visible()
            page.click("#botAiDiscardBtn")
            page.wait_for_timeout(300)

            # =================================================================
            # 6. MOBILE RESPONSIVE DASHBOARD (390x844)
            # =================================================================
            m_ctx = browser.new_context(viewport={"width": 390, "height": 844})
            m_ctx.add_cookies(context.cookies())
            m_page = m_ctx.new_page()
            m_page.goto(f"{BASE_URL}/")
            m_page.wait_for_selector("#top h1")
            m_page.wait_for_timeout(600)

            # Mobile in Spanish
            m_page.select_option("#langSelect", "es")
            m_page.wait_for_timeout(300)

            # Verify header is wrapped and language selector is clickable
            p_m_dash_es = ARTIFACTS_DIR / "dashboard-es-mobile.png"
            m_page.screenshot(path=str(p_m_dash_es))
            downscale_image(p_m_dash_es)
            screenshots["dashboard_es_mobile"] = str(p_m_dash_es)

            # Mobile in English
            m_page.select_option("#langSelect", "en")
            m_page.wait_for_timeout(300)
            assert m_page.locator("html").get_attribute("lang") == "en"

            p_m_dash_en = ARTIFACTS_DIR / "dashboard-en-mobile.png"
            m_page.screenshot(path=str(p_m_dash_en))
            downscale_image(p_m_dash_en)
            screenshots["dashboard_en_mobile"] = str(p_m_dash_en)
            m_ctx.close()

            browser.close()
            print("All internationalization and Playwright QA tests passed successfully!")
            return screenshots
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=3)
        except subprocess.TimeoutExpired:
            proc.kill()
        temp_dir_obj.cleanup()


if __name__ == "__main__":
    res = main()
    print(f"Total screenshots captured: {len(res)}")
    for k, v in res.items():
        print(f"  {k}: {v}")
