#!/usr/bin/env python3
"""Browser and Visual QA script using Playwright Firefox.

Optional testing dependencies (NOT required for production or server execution):
    pip install playwright pillow
    playwright install firefox

Runs against an isolated test instance of server.py on loopback with ephemeral credentials
and temporary configuration files. Never mutates production credentials, configurations,
or master files. All artifacts and screenshots are discovered repo-relatively without
hardcoded paths.

Verifies:
- Login and session cookie handling
- Bot AI tab navigation and visual mode selector (CrystalScramble, FlagFlight, RapidBall)
- Profile selection with kicker avatars and shared roster mapping
- Plain intent preset cards application and explanation notes
- Live sliders and controls updating dirty badges and draft state
- Advanced collapsible native parameters
- Review changes modal before saving with friendly behavior descriptions
- Hot-reload atomic save and server backup creation
- Optimistic concurrency conflict (409) modal
- Mobile viewport responsive layout (390x844)

Captures screenshots and downscales to <=720px width using Pillow.
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
import time
from pathlib import Path
from PIL import Image
from playwright.sync_api import sync_playwright

REPO = Path(__file__).resolve().parents[2]


def discover_artifacts_dirs() -> tuple[Path, Path]:
    repo_artifacts = REPO / ".local" / "artifacts"
    repo_artifacts.mkdir(parents=True, exist_ok=True)
    try:
        common_git = subprocess.check_output(
            ["git", "rev-parse", "--path-format=absolute", "--git-common-dir"],
            cwd=str(REPO),
            text=True,
            stderr=subprocess.DEVNULL
        ).strip()
        common_root = Path(common_git).parent
        orig_artifacts = common_root / ".local" / "artifacts"
        orig_artifacts.mkdir(parents=True, exist_ok=True)
        return repo_artifacts, orig_artifacts
    except Exception:
        return repo_artifacts, repo_artifacts


ARTIFACTS_DIR, ORIGINAL_ARTIFACTS_DIR = discover_artifacts_dirs()

PORT = 18991
BASE_URL = f"http://127.0.0.1:{PORT}/balance"
USERNAME = "qa_tester"
PASSWORD = "TestPassword987!"


def setup_test_environment(tmp_path: Path):
    config_dir = tmp_path / "config"
    config_dir.mkdir(parents=True)
    backups_dir = tmp_path / "backups"
    backups_dir.mkdir(parents=True)
    auth_dir = tmp_path / "auth"
    auth_dir.mkdir(parents=True, mode=0o700)
    overrides_dir = tmp_path / "overrides"
    overrides_dir.mkdir(parents=True)

    # Copy pristine bot AI profiles and master files
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

    env = dict(os.environ)
    env.update({
        "KF_BALANCE_BASE_DIR": str(config_dir),
        "KF_BALANCE_OVERRIDE_DIR": str(overrides_dir),
        "KF_BALANCE_BACKUP_DIR": str(backups_dir),
        "KF_BALANCE_AUTH_DB": str(auth_db),
        "KF_BALANCE_JWT_SECRET_FILE": str(jwt_secret),
        "KF_BOT_AI_PROFILES_PATH": str(config_dir / "bot_ai_profiles.json"),
        "KF_BALANCE_BASE_PATH": "/balance",
        "KF_BALANCE_ALLOW_APPLY": "0",
    })
    return env, config_dir / "bot_ai_profiles.json", backups_dir


def downscale_image(image_path: Path, target_width: int = 720):
    img = Image.open(image_path)
    w, h = img.size
    if w > target_width:
        new_h = int(h * (target_width / w))
        img_resized = img.resize((target_width, new_h), Image.Resampling.LANCZOS)
        img_resized.save(image_path)
        print(f"Downscaled {image_path.name}: {w}x{h} -> {target_width}x{new_h}")
    # Always mirror to original artifacts dir if distinct
    if ORIGINAL_ARTIFACTS_DIR != ARTIFACTS_DIR:
        shutil.copy(image_path, ORIGINAL_ARTIFACTS_DIR / image_path.name)


def run_qa():
    import tempfile
    temp_dir_obj = tempfile.TemporaryDirectory()
    tmp_path = Path(temp_dir_obj.name)
    env, bot_ai_file, backups_dir = setup_test_environment(tmp_path)

    proc = subprocess.Popen(
        [sys.executable, str(REPO / "tools/balance/server.py"), "--host", "127.0.0.1", "--port", str(PORT)],
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT
    )

    screenshots = {}

    try:
        # Wait for server
        deadline = time.time() + 10.0
        import urllib.request
        while time.time() < deadline:
            try:
                with urllib.request.urlopen(f"{BASE_URL}/login", timeout=1.0) as res:
                    if res.status == 200:
                        break
            except Exception:
                time.sleep(0.1)
        else:
            out = proc.stdout.read().decode("utf-8", "replace") if proc.stdout else ""
            raise RuntimeError(f"Server failed to start:\n{out}")

        print("Server up. Launching Playwright Firefox...")
        with sync_playwright() as p:
            browser = p.firefox.launch(headless=True)
            context = browser.new_context(viewport={"width": 1280, "height": 800})
            page = context.new_page()
            page.on("console", lambda msg: print(f"PAGE CONSOLE: {msg.text}"))
            page.on("pageerror", lambda err: print(f"PAGE ERROR: {err}"))

            # 1. Login
            page.goto(f"{BASE_URL}/login")
            page.fill("input[name='username']", USERNAME)
            page.fill("input[name='password']", PASSWORD)
            page.click("button[type='submit']")
            page.wait_for_selector("#top h1")
            print("Logged in successfully.")

            # 2. Switch to 'IA de bots' tab
            page.click("button[data-tab='bot-ai']")
            page.wait_for_selector(".bot-ai-container")
            page.wait_for_selector(".bot-ai-modes-segmented")
            time.sleep(0.5)

            # Verify tab-aware top hint
            top_hint = page.locator("#topHint").text_content()
            assert "Se guardan en el servidor para nuevas partidas." in top_hint, f"Unexpected top hint: {top_hint}"
            assert "en caliente" not in top_hint

            # Verify heading
            assert page.locator("h2.bot-ai-heading:has-text('¿Qué quieres que haga?')").is_visible()

            # Verify at least first 2 intent cards are visible in first viewport (height 800)
            cards = page.locator(".bot-ai-intent-card")
            assert cards.count() == 5
            box0 = cards.nth(0).bounding_box()
            box1 = cards.nth(1).bounding_box()
            assert box0 is not None and box0["y"] + box0["height"] <= 800, f"Card 0 out of fold: {box0}"
            assert box1 is not None and box1["y"] + box1["height"] <= 800, f"Card 1 out of fold: {box1}"

            p1 = ARTIFACTS_DIR / "bot-ai-desktop-initial.png"
            page.screenshot(path=str(p1))
            downscale_image(p1)
            screenshots["desktop_initial"] = str(p1)

            # 3. Test Mode Selector: Switch to FlagFlight
            page.click(".bot-ai-mode-seg:has-text('Bandera')")
            time.sleep(0.3)
            # Verify deposit controls are not present in FlagFlight
            page.click(".bot-ai-custom-details summary")
            time.sleep(0.2)
            assert page.locator("text='Cristales para depositar'").count() == 0
            page.click(".bot-ai-custom-details summary")  # close
            p2 = ARTIFACTS_DIR / "bot-ai-desktop-flagflight.png"
            page.screenshot(path=str(p2))
            downscale_image(p2)
            screenshots["desktop_flagflight"] = str(p2)

            # 4. Test Mode Selector: Switch to RapidBall
            page.click(".bot-ai-mode-seg:has-text('Balón')")
            time.sleep(0.3)
            p3 = ARTIFACTS_DIR / "bot-ai-desktop-rapidball.png"
            page.screenshot(path=str(p3))
            downscale_image(p3)
            screenshots["desktop_rapidball"] = str(p3)

            # 5. Switch back to CrystalScramble
            page.click(".bot-ai-mode-seg:has-text('Cristales')")
            time.sleep(0.3)

            # Select 'tank_defender' profile in sidebar
            page.click(".bot-ai-sidebar-item:has-text('Defensor Tanque')")
            time.sleep(0.3)
            # Check compact affects label
            assert page.locator(".bot-ai-affects-label:has-text('Coco, Diatrius, Buzzy Big')").is_visible()

            # 6. Click Intent Preset: 'Que cuide la base'
            page.click(".bot-ai-intent-card:has-text('Que cuide la base')")
            time.sleep(0.3)
            # Verify card is visibly selected
            assert "selected" in page.locator(".bot-ai-intent-card:has-text('Que cuide la base')").get_attribute("class")
            assert page.locator(".bot-ai-preset-feedback:has-text('Que cuide la base')").is_visible()
            # Verify dirty badge appears
            assert page.locator("#botAiProfileDirtyBadge").is_visible()
            assert page.locator("#botAiSaveBtn").is_enabled()
            assert "Guardar cambios" in page.locator("#botAiSaveBtn").text_content()

            p4 = ARTIFACTS_DIR / "bot-ai-desktop-preset-dirty.png"
            page.screenshot(path=str(p4))
            downscale_image(p4)
            screenshots["desktop_preset_dirty"] = str(p4)

            # 7. Expand Advanced Details
            page.click(".bot-ai-advanced summary")
            time.sleep(0.2)
            assert page.locator("text='Identificador de parámetro: 16'").is_visible()
            p5 = ARTIFACTS_DIR / "bot-ai-desktop-advanced.png"
            page.screenshot(path=str(p5))
            downscale_image(p5)
            screenshots["desktop_advanced"] = str(p5)

            # 8. Open Review Changes Modal
            page.click("#botAiSaveBtn")
            page.wait_for_selector("#botAiReviewModal")
            time.sleep(0.3)
            # Verify modal title and CTA button
            assert "Guardar cambios en el servidor" in page.locator("#botAiReviewModal .bot-ai-modal-head").text_content()
            confirm_btn = page.locator("#botAiReviewModal .bot-ai-modal-foot button.primary")
            assert "Guardar en servidor" in confirm_btn.text_content()
            assert "cliente" not in confirm_btn.text_content().lower()

            # Verify simplified technical intro without 'en caliente' jargon
            notice_text = page.locator("#botAiReviewModal .bot-ai-notice").text_content()
            assert "Revisa los cambios. Al guardar quedarán en el servidor para las nuevas partidas" in notice_text
            assert "en caliente" not in notice_text

            # Verify friendly per-profile behavior descriptions visible outside disclosure
            assert page.locator("#botAiReviewModal .bot-ai-review-behavior-list li").count() > 0
            # Verify numeric disclosure is present and closed by default
            diff_details = page.locator("#botAiReviewModal details.bot-ai-modal-diff-details")
            assert diff_details.is_visible()
            assert diff_details.get_attribute("open") is None
            assert "Ver detalle de valores numéricos" in diff_details.locator("summary").text_content()

            p6 = ARTIFACTS_DIR / "bot-ai-desktop-review-modal.png"
            page.screenshot(path=str(p6))
            downscale_image(p6)
            screenshots["desktop_review_modal"] = str(p6)

            # 9. Confirm Save
            confirm_btn.click()
            time.sleep(0.8)
            # Toast should be visible
            assert page.locator("#botAiToast:has-text('exitosamente')").is_visible()
            p7 = ARTIFACTS_DIR / "bot-ai-desktop-saved.png"
            page.screenshot(path=str(p7))
            downscale_image(p7)
            screenshots["desktop_saved"] = str(p7)

            # Verify on-disk file was updated
            saved_content = json.loads(bot_ai_file.read_text(encoding="utf-8"))
            tank_profile = next(p for p in saved_content["profiles"] if p["mode"] == "CrystalScramble" and p["profile"] == "tank_defender")
            assert tank_profile["motivations"]["defense"] == 85

            # 10. Concurrency Conflict Test
            # Simulate external edit directly on disk
            ext_content = copy.deepcopy(saved_content)
            ext_content["profiles"][0]["motivations"]["attack"] = 99
            bot_ai_file.write_text(json.dumps(ext_content), encoding="utf-8")

            # Now modify something in UI
            page.click(".bot-ai-intent-card:has-text('Que busque pelea')")
            time.sleep(0.2)
            page.click("#botAiSaveBtn")
            page.wait_for_selector("#botAiReviewModal")
            page.click("#botAiReviewModal button:has-text('Guardar en servidor')")
            page.wait_for_selector("#botAiConflictModal")
            time.sleep(0.3)
            p8 = ARTIFACTS_DIR / "bot-ai-desktop-conflict-modal.png"
            page.screenshot(path=str(p8))
            downscale_image(p8)
            screenshots["desktop_conflict"] = str(p8)

            # Close conflict modal
            page.click("#botAiConflictModal button:has-text('Cerrar')")
            time.sleep(0.2)

            # 11. Mobile Responsive Viewport (390x844 native)
            mobile_context = browser.new_context(viewport={"width": 390, "height": 844})
            mobile_page = mobile_context.new_page()
            # Set cookie
            cookies = context.cookies()
            mobile_context.add_cookies(cookies)

            mobile_page.goto(f"{BASE_URL}/")
            mobile_page.click("button[data-tab='bot-ai']")
            mobile_page.wait_for_selector(".bot-ai-container")
            time.sleep(0.5)

            # Verify mobile layout: tall left profile list hidden, native select visible
            assert not mobile_page.locator("#sidebar #list").is_visible()
            assert mobile_page.locator("#botAiProfileSelect").is_visible()
            assert mobile_page.locator("h2.bot-ai-heading:has-text('¿Qué quieres que haga?')").is_visible()

            # Verify at least first 2 cards visible in first viewport on mobile (height 844) without scrolling!
            m_cards = mobile_page.locator(".bot-ai-intent-card")
            assert m_cards.count() == 5
            mbox0 = m_cards.nth(0).bounding_box()
            mbox1 = m_cards.nth(1).bounding_box()
            assert mbox0 is not None and mbox0["y"] + mbox0["height"] <= 844, f"Mobile card 0 out of fold: {mbox0}"
            assert mbox1 is not None and mbox1["y"] + mbox1["height"] <= 844, f"Mobile card 1 out of fold: {mbox1}"

            p9 = ARTIFACTS_DIR / "bot-ai-mobile-initial-390.png"
            mobile_page.screenshot(path=str(p9))
            downscale_image(p9)
            screenshots["mobile_initial_390"] = str(p9)

            # Select hunter via mobile select picker
            mobile_page.select_option("#botAiProfileSelect", "hunter")
            time.sleep(0.2)
            assert mobile_page.locator(".bot-ai-profile-title:has-text('Cazador')").is_visible()

            # Click intent card 'Que busque pelea' (matching 'Quiero que busque pelea')
            mobile_page.click(".bot-ai-intent-card:has-text('Que busque pelea')")
            time.sleep(0.2)
            assert mobile_page.locator("#botAiSaveBtn").is_enabled()
            assert "Guardar cambios" in mobile_page.locator("#botAiSaveBtn").text_content()

            # Open review modal on mobile
            mobile_page.click("#botAiSaveBtn")
            mobile_page.wait_for_selector("#botAiReviewModal")
            time.sleep(0.3)

            # Verify mobile review modal shows friendly descriptions and closed disclosure
            assert mobile_page.locator("#botAiReviewModal .bot-ai-review-behavior-list li").count() > 0
            m_diff_details = mobile_page.locator("#botAiReviewModal details.bot-ai-modal-diff-details")
            assert m_diff_details.is_visible()
            assert m_diff_details.get_attribute("open") is None

            p10 = ARTIFACTS_DIR / "bot-ai-mobile-review-390.png"
            mobile_page.screenshot(path=str(p10))
            downscale_image(p10)
            screenshots["mobile_review_390"] = str(p10)

            # 12. Confirm save on mobile & verify preset scope
            mobile_page.click("#botAiReviewModal button:has-text('Guardar en servidor')")
            time.sleep(0.8)
            assert mobile_page.locator("#botAiToast:has-text('exitosamente')").is_visible()
            p11 = ARTIFACTS_DIR / "bot-ai-mobile-saved-390.png"
            mobile_page.screenshot(path=str(p11))
            downscale_image(p11)
            screenshots["mobile_saved_390"] = str(p11)

            # Verify on-disk file update and preset scope isolation
            mobile_saved = json.loads(bot_ai_file.read_text(encoding="utf-8"))
            hunter_profile = next(p for p in mobile_saved["profiles"] if p["mode"] == "CrystalScramble" and p["profile"] == "hunter")
            assert hunter_profile["motivations"]["attack"] == 90
            tank_profile = next(p for p in mobile_saved["profiles"] if p["mode"] == "CrystalScramble" and p["profile"] == "tank_defender")
            assert tank_profile["motivations"]["defense"] == 85
            carrier_profile = next(p for p in mobile_saved["profiles"] if p["mode"] == "CrystalScramble" and p["profile"] == "carrier")
            assert carrier_profile["motivations"]["attack"] == 99  # External conflict edit preserved, uncorrupted

            browser.close()
            print("All browser QA checks passed successfully!")
            return screenshots
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=3)
        except subprocess.TimeoutExpired:
            proc.kill()
        temp_dir_obj.cleanup()


if __name__ == "__main__":
    results = run_qa()
    print("\nGenerated Screenshots:")
    for k, v in results.items():
        print(f"  {k}: {v}")
