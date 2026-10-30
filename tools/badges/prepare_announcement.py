#!/usr/bin/env python3
"""Safely and idempotently prepare and upsert the Badge Catalog announcement into Latest Information.

Preserves existing persistent mutable state, prior live announcement history,
snapshot metadata fields, and file permissions. Links directly to /badges without
redirecting away from the announcement before it can be read.

Explicitly invites badge contributions and states that active contributors receive
an exclusive in-game badge delivered manually based on supplied player names
(no automated granting).
"""

from __future__ import annotations

import argparse
import fcntl
import json
import logging
import os
import re
import shutil
import stat
import sys
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger("kickflight.badges.announcement")

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_STATE_FILE = ROOT / ".local" / "maintenance" / "latest-information.json"

ARTICLE_ID = "badge-catalog"
ARTICLE_HTML = """    <article class="patch" id="badge-catalog">
      <div class="patch-head"><h2>Community Badge Catalog &amp; Contributor Rewards</h2><time datetime="2026-10-30">2026-10-30</time></div>
      <p class="headline">Explore all 211 Kick-Flight badges and collaborate to decipher conditions and titles!</p>
      <h3>Badge Catalog &amp; Glyphs</h3>
      <ul>
        <li>Access the collaborative catalog at <a href="/badges">/badges</a> to browse all 211 badges, inspect high-resolution alien glyph art, and contribute missing information.</li>
      </ul>
      <h3>Exclusive Contributor Badge</h3>
      <ul>
        <li>Players who contribute by proposing names, conditions, or research notes will receive an exclusive in-game badge.</li>
        <li>Rewards are delivered manually based on supplied player names recorded with your edits. There is no automated granting.</li>
      </ul>
    </article>"""

TEXT_BLOCK = (
    "**Community Badge Catalog & Contributor Rewards**\n"
    "Explore all 211 Kick-Flight badges and collaborate at /badges.\n"
    "Active contributors receive an exclusive in-game badge, delivered manually based on supplied player names (no automated granting)."
)

INITIAL_HTML_WRAPPER = """<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Latest Information</title><style>:root{color-scheme:dark}body{font-family:system-ui,-apple-system,sans-serif;background:#10141b;color:#f3f6fa;margin:0;padding:20px}main{max-width:680px;margin:auto}.patch{background:#1b222d;border:1px solid #303b4b;border-radius:12px;padding:24px;margin-bottom:20px}.patch h2{margin:0;color:#57d7d0}.patch-head{display:flex;justify-content:space-between;align-items:center}time{color:#8b9bb4;font-size:0.85em}h3{color:#9cf5f0;margin:16px 0 8px}ul{margin:0;padding-left:20px;line-height:1.6}a{color:#57d7d0;font-weight:600}</style></head><body><main class="wrap"><h1>Updates</h1>

{article}

</main></body></html>"""


def mask_invisible(text: str) -> str:
    """Mask HTML comments, <head>, <script>, and <style> tags with spaces to preserve indices."""
    masked = re.sub(r'<!--[\s\S]*?-->', lambda m: ' ' * len(m.group(0)), text)
    masked = re.sub(r'<head\b[^>]*>[\s\S]*?</head>', lambda m: ' ' * len(m.group(0)), masked, flags=re.IGNORECASE)
    masked = re.sub(r'<script\b[^>]*>[\s\S]*?</script>', lambda m: ' ' * len(m.group(0)), masked, flags=re.IGNORECASE)
    masked = re.sub(r'<style\b[^>]*>[\s\S]*?</style>', lambda m: ' ' * len(m.group(0)), masked, flags=re.IGNORECASE)
    return masked


def strip_articles_from_comments(html: str) -> str:
    """Remove any article blocks mistakenly injected into HTML comments."""
    def clean_comment(match: re.Match) -> str:
        return re.sub(r'<article[^>]*>[\s\S]*?</article>\s*\n*\s*', '', match.group(0))
    return re.sub(r'<!--[\s\S]*?-->', clean_comment, html)


def remove_article_by_id(html: str, article_id: str) -> str:
    """Remove visible or commented article by id if already present."""
    pattern = rf'<article[^>]*\bid=["\']{re.escape(article_id)}["\'][\s\S]*?</article>\s*\n*\s*'
    return re.sub(pattern, '', html)


def get_visible_article_ids(html: str) -> list[str]:
    """Extract IDs of articles that are rendered outside comments, head, script, and style."""
    masked = mask_invisible(html)
    return re.findall(r'<article[^>]*\bid=["\']([^"\']+)["\']', masked)


def detect_field_casing(data: dict[str, Any]) -> dict[str, str]:
    return {
        "content": "Content" if "Content" in data else "content",
        "mode": "Mode" if "Mode" in data else "mode",
        "htmlContent": "HtmlContent" if "HtmlContent" in data else "htmlContent",
        "externalUrl": "ExternalUrl" if "ExternalUrl" in data else "externalUrl",
        "htmlResourceCsp": "HtmlResourceCsp" if "HtmlResourceCsp" in data else "htmlResourceCsp",
        "defaultContent": "DefaultContent" if "DefaultContent" in data else "defaultContent",
        "changedAtUtc": "ChangedAtUtc" if "ChangedAtUtc" in data else "changedAtUtc",
    }


def insert_article_into_html(html: str, article_block: str, article_id: str = ARTICLE_ID) -> str:
    """Idempotently insert article block as the first visible article inside <main>, preserving all existing articles."""
    cleaned_html = strip_articles_from_comments(html)
    cleaned_html = remove_article_by_id(cleaned_html, article_id)

    masked = mask_invisible(cleaned_html)
    main_match = re.search(r'<main\b[^>]*>', masked)
    if main_match:
        after_main = masked[main_match.end():]
        first_article = re.search(r'<article\b', after_main)
        if first_article:
            insert_pos = main_match.end() + first_article.start()
        else:
            h1_match = re.search(r'</h1>\s*', after_main)
            insert_pos = main_match.end() + (h1_match.end() if h1_match else 0)
    else:
        first_article = re.search(r'<article\b', masked)
        if first_article:
            insert_pos = first_article.start()
        else:
            # Wrap within body if neither main nor article exists
            body_match = re.search(r'<body\b[^>]*>', masked)
            if body_match:
                insert_pos = body_match.end()
            else:
                insert_pos = 0

    new_html = cleaned_html[:insert_pos] + article_block + "\n\n    " + cleaned_html[insert_pos:]

    old_ids = get_visible_article_ids(cleaned_html)
    new_ids = get_visible_article_ids(new_html)
    expected_ids = [article_id] + old_ids
    if new_ids != expected_ids:
        raise ValueError(
            f"Article insertion verification failed. Expected IDs: {expected_ids}, got: {new_ids}"
        )
    return new_html


def upsert_announcement_state(
    state_file: Path,
    backup_dir: Path | None = None,
    dry_run: bool = False,
) -> bool:
    """Safely and idempotently upsert the badge catalog announcement into the state file.

    Returns:
        True if the file was modified/created, False if already present and up-to-date (idempotent).
    """
    state_file = Path(state_file).resolve()
    lock_file = state_file.parent / f".{state_file.name}.lock"
    state_file.parent.mkdir(parents=True, exist_ok=True)

    with open(lock_file, "w") as lf:
        fcntl.flock(lf, fcntl.LOCK_EX)
        try:
            now_iso = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

            if not state_file.exists():
                logger.info("State file %s does not exist; creating initial valid template with badge announcement.", state_file)
                initial_data = {
                    "Content": TEXT_BLOCK,
                    "Mode": "html",
                    "HtmlContent": INITIAL_HTML_WRAPPER.replace("{article}", ARTICLE_HTML),
                    "ExternalUrl": "",
                    "ChangedAtUtc": now_iso,
                }
                if dry_run:
                    logger.info("[dry-run] Initial template would be written to %s", state_file)
                    return True

                tmp_path = state_file.with_name(f"{state_file.name}.tmp.{os.getpid()}")
                with open(tmp_path, "w", encoding="utf-8") as tf:
                    json.dump(initial_data, tf, indent=2, ensure_ascii=False)
                    tf.write("\n")
                os.chmod(tmp_path, 0o644)
                os.replace(tmp_path, state_file)
                return True

            raw = state_file.read_text(encoding="utf-8")
            try:
                data = json.loads(raw)
            except json.JSONDecodeError as err:
                raise ValueError(f"State file {state_file} contains invalid JSON: {err}") from err

            if not isinstance(data, dict):
                raise ValueError(f"State file {state_file} root must be a JSON object")

            casing = detect_field_casing(data)
            mode_key = casing["mode"]
            html_key = casing["htmlContent"]
            content_key = casing["content"]
            changed_key = casing["changedAtUtc"]

            mode_val = data.get(mode_key, "html")

            if mode_val == "html":
                current_html = data.get(html_key, "")
                if not isinstance(current_html, str):
                    current_html = ""

                cleaned_html = strip_articles_from_comments(current_html)
                current_ids = get_visible_article_ids(cleaned_html)

                # Check if already up-to-date and first visible article
                if current_ids and current_ids[0] == ARTICLE_ID and "/badges" in current_html and "exclusive in-game" in current_html and "no automated granting" in current_html:
                    logger.info("Article '%s' is already the first visible article in %s. No changes needed (idempotent).", ARTICLE_ID, state_file)
                    return False

                # Perform safe insertion
                updated_html = insert_article_into_html(current_html, ARTICLE_HTML, ARTICLE_ID)
                data[html_key] = updated_html
                data[changed_key] = now_iso
            else:
                # Text mode
                current_content = data.get(content_key, "")
                if TEXT_BLOCK in current_content:
                    logger.info("Badge announcement text already present in %s. No changes needed (idempotent).", state_file)
                    return False
                data[content_key] = f"{TEXT_BLOCK}\n\n{current_content}".strip()
                data[changed_key] = now_iso

            if dry_run:
                logger.info("[dry-run] Would update %s with badge announcement.", state_file)
                return True

            # Create backup if directory specified
            if backup_dir is not None:
                backup_dir.mkdir(parents=True, exist_ok=True)
                ts = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
                backup_file = backup_dir / f"latest-information-backup-{ts}.json"
                shutil.copy2(state_file, backup_file)
                logger.info("Created backup of prior state at %s", backup_file)

            # Preserve file mode
            st = state_file.stat()
            file_mode = stat.S_IMODE(st.st_mode)

            # Atomic write via temporary file
            tmp_path = state_file.with_name(f"{state_file.name}.tmp.{os.getpid()}")
            with open(tmp_path, "w", encoding="utf-8") as tf:
                json.dump(data, tf, indent=2, ensure_ascii=False)
                tf.write("\n")
            os.chmod(tmp_path, file_mode)
            os.replace(tmp_path, state_file)
            logger.info("Idempotently updated announcement in %s", state_file)
            return True
        finally:
            try:
                fcntl.flock(lf, fcntl.LOCK_UN)
            except OSError:
                pass


def upsert_via_admin_api(api_url: str = "http://127.0.0.1:8080") -> bool:
    """Fetch current latest-information from /admin/latest-information and merge idempotently."""
    base = api_url.rstrip("/")
    admin_url = f"{base}/admin/latest-information"

    # 1. Fetch current state
    try:
        req = urllib.request.Request(admin_url, headers={"User-Agent": "kf-badge-announcement"})
        with urllib.request.urlopen(req, timeout=10) as resp:
            data = json.loads(resp.read().decode("utf-8"))
    except Exception as e:
        logger.warning("Could not reach %s: %s", admin_url, e)
        return False

    mode = data.get("mode", "html")
    html_content = data.get("htmlContent", "")
    content = data.get("content", "")

    if mode == "html":
        cleaned = strip_articles_from_comments(html_content)
        current_ids = get_visible_article_ids(cleaned)
        if current_ids and current_ids[0] == ARTICLE_ID and "/badges" in html_content and "exclusive in-game" in html_content and "no automated granting" in html_content:
            logger.info("Badge announcement is already first visible article at %s (idempotent).", admin_url)
            return True
        new_html = insert_article_into_html(html_content, ARTICLE_HTML, ARTICLE_ID)
        payload = {"mode": "html", "htmlContent": new_html}
    else:
        if TEXT_BLOCK in content:
            logger.info("Badge announcement text already present at %s (idempotent).", admin_url)
            return True
        payload = {"mode": "text", "content": f"{TEXT_BLOCK}\n\n{content}".strip()}

    body = json.dumps(payload).encode("utf-8")
    post_req = urllib.request.Request(
        admin_url,
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST"
    )
    try:
        with urllib.request.urlopen(post_req, timeout=10) as resp:
            logger.info("Successfully upserted badge announcement to %s (HTTP %d)", admin_url, resp.status)
            return True
    except Exception as e:
        logger.error("Failed to POST updated announcement to %s: %s", admin_url, e)
        return False


def main() -> None:
    parser = argparse.ArgumentParser(description="Prepare and upsert /badges announcement idempotently")
    parser.add_argument("--state-file", default=str(DEFAULT_STATE_FILE), help="Path to latest-information.json")
    parser.add_argument("--backup-dir", default=None, help="Directory to store pre-update backup")
    parser.add_argument("--post", action="store_true", help="Post to running BootstrapApi /admin/latest-information")
    parser.add_argument("--api-url", default="http://127.0.0.1:8080", help="BootstrapApi URL")
    parser.add_argument("--dry-run", action="store_true", help="Preview changes without writing")
    args = parser.parse_args()

    backup_dir = Path(args.backup_dir).resolve() if args.backup_dir else None
    upsert_announcement_state(Path(args.state_file), backup_dir=backup_dir, dry_run=args.dry_run)

    if args.post:
        upsert_via_admin_api(args.api_url)


if __name__ == "__main__":
    main()
