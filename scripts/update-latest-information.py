#!/usr/bin/env python3
"""Safely and idempotently insert release articles into persisted Latest Information state.

Preserves all prior live announcement history, snapshot metadata fields, and file permissions.
Backs up previous state before atomic replacement and fails closed on non-HTML mode, corrupt
JSON/HTML, or concurrency conflicts.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import fcntl
import json
import os
from pathlib import Path
import re
import shutil
import stat
import sys

DEFAULT_ARTICLE_ID = "v2-11-4"
DEFAULT_CSP = (
    "default-src https: http: data: blob:; "
    "script-src 'unsafe-inline' https: http: data: blob:; "
    "style-src 'unsafe-inline' https: http: data: blob:; "
    "img-src https: http: data: blob:; "
    "font-src https: http: data: blob:; "
    "connect-src https: http: data: blob:; "
    "media-src https: http: data: blob:; "
    "frame-src https: http:; "
    "object-src 'none'; base-uri 'none'; form-action https: http:"
)
DEFAULT_CONTENT = (
    "**Bienvenido a Kick-Flight**\n"
    "Este es un servidor privado de pruebas. El progreso es local y puede reiniciarse sin aviso.\n\n"
    "**Tienda y gacha**\n"
    "La tienda y el gacha todavia no estan disponibles; llegaran en una proxima actualizacion."
)


def mask_comments(text: str) -> str:
    """Mask HTML comments with spaces so indices match original text without matching comments."""
    return re.sub(r'<!--[\s\S]*?-->', lambda m: ' ' * len(m.group(0)), text)


def strip_articles_from_comments(html: str) -> str:
    """Remove any article blocks mistakenly injected into HTML comments."""
    def clean_comment(match: re.Match) -> str:
        return re.sub(r'<article[^>]*>[\s\S]*?</article>\s*\n*\s*', '', match.group(0))
    return re.sub(r'<!--[\s\S]*?-->', clean_comment, html)


def remove_article_by_id(html: str, article_id: str) -> str:
    """Remove visible or commented article by id if already present elsewhere in document."""
    pattern = rf'<article[^>]*\bid=["\']{re.escape(article_id)}["\'][\s\S]*?</article>\s*\n*\s*'
    return re.sub(pattern, '', html)


def extract_article(source_html: str, article_id: str) -> str:
    cleaned_source = strip_articles_from_comments(source_html)
    pattern = rf'(<article[^>]*\bid=["\']{re.escape(article_id)}["\'][\s\S]*?</article>)'
    match = re.search(pattern, cleaned_source)
    if not match:
        raise ValueError(f"Article with id='{article_id}' not found in source HTML")
    return match.group(1)


def get_visible_article_ids(html: str) -> list[str]:
    """Extract IDs of articles that are rendered outside HTML comments."""
    masked = mask_comments(html)
    return re.findall(r'<article[^>]*\bid=["\']([^"\']+)["\']', masked)


def get_article_ids(html: str) -> list[str]:
    """Alias for backwards compatibility: returns IDs of visible rendered articles."""
    return get_visible_article_ids(html)


def detect_field_casing(data: dict) -> dict[str, str]:
    """Detect whether keys use camelCase or PascalCase to preserve record JSON formatting."""
    mapping = {
        "content": "Content" if "Content" in data else "content",
        "mode": "Mode" if "Mode" in data else "mode",
        "htmlContent": "HtmlContent" if "HtmlContent" in data else "htmlContent",
        "externalUrl": "ExternalUrl" if "ExternalUrl" in data else "externalUrl",
        "htmlResourceCsp": "HtmlResourceCsp" if "HtmlResourceCsp" in data else "htmlResourceCsp",
        "defaultContent": "DefaultContent" if "DefaultContent" in data else "defaultContent",
        "changedAtUtc": "ChangedAtUtc" if "ChangedAtUtc" in data else "changedAtUtc",
    }
    return mapping


def create_initial_template(source_html: str, article_id: str) -> dict:
    """Create a valid LatestInformationSnapshot JSON dictionary if state file is missing."""
    now_utc = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    return {
        "content": DEFAULT_CONTENT,
        "mode": "html",
        "htmlContent": source_html,
        "externalUrl": "",
        "htmlResourceCsp": DEFAULT_CSP,
        "defaultContent": DEFAULT_CONTENT,
        "changedAtUtc": now_utc,
    }


def insert_article_into_html(html: str, article_block: str, article_id: str) -> str:
    """Insert article block as the first visible article inside <main> while preserving all existing articles."""
    # 1. Clean any articles mistakenly placed in comments
    cleaned_html = strip_articles_from_comments(html)

    # 2. Remove article_id if already present elsewhere in body so it is moved to top
    cleaned_html = remove_article_by_id(cleaned_html, article_id)

    # 3. Locate insertion position inside <main> using comment-masked HTML
    masked = mask_comments(cleaned_html)
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
            raise ValueError("HTML content has neither <main> nor visible <article> element for insertion")

    new_html = cleaned_html[:insert_pos] + article_block + "\n\n    " + cleaned_html[insert_pos:]

    old_ids = get_visible_article_ids(cleaned_html)
    new_ids = get_visible_article_ids(new_html)
    expected_ids = [article_id] + old_ids
    if new_ids != expected_ids:
        raise ValueError(
            f"Article insertion verification failed. Expected IDs: {expected_ids}, got: {new_ids}"
        )
    return new_html


def update_latest_information(
    state_file: Path,
    source_docs: Path,
    article_id: str = DEFAULT_ARTICLE_ID,
    backup_dir: Path | None = None,
    record_backup_path: Path | None = None,
    dry_run: bool = False,
) -> bool:
    """Updates latest-information.json idempotently, returning True if updated or False if already present."""
    if not source_docs.is_file():
        raise FileNotFoundError(f"Source docs file not found: {source_docs}")

    source_html = source_docs.read_text(encoding="utf-8")
    article_block = extract_article(source_html, article_id)

    # Concurrency guard: lock file in same directory
    lock_file = state_file.parent / f".{state_file.name}.lock"
    state_file.parent.mkdir(parents=True, exist_ok=True)

    with open(lock_file, "w") as lf:
        fcntl.flock(lf, fcntl.LOCK_EX)
        try:
            if not state_file.exists():
                print(f"State file {state_file} does not exist; creating initial valid template.")
                initial_data = create_initial_template(source_html, article_id)
                if dry_run:
                    print("[dry-run] Initial template would be created.")
                    return True

                tmp_path = state_file.with_name(f"{state_file.name}.tmp.{os.getpid()}")
                with open(tmp_path, "w", encoding="utf-8") as tf:
                    json.dump(initial_data, tf, indent=2, ensure_ascii=False)
                    tf.write("\n")
                os.chmod(tmp_path, 0o644)
                os.replace(tmp_path, state_file)
                print(f"Created {state_file} with article '{article_id}'.")
                return True

            # Existing state file
            raw_text = state_file.read_text(encoding="utf-8")
            try:
                data = json.loads(raw_text)
            except json.JSONDecodeError as err:
                raise ValueError(f"State file {state_file} contains invalid JSON: {err}") from err

            if not isinstance(data, dict):
                raise ValueError(f"State file {state_file} root must be a JSON object")

            casing = detect_field_casing(data)
            mode_key = casing["mode"]
            html_key = casing["htmlContent"]
            changed_key = casing["changedAtUtc"]

            mode_val = data.get(mode_key)
            if mode_val != "html":
                raise ValueError(
                    f"State file mode is '{mode_val}', expected 'html'. Refusing to alter non-HTML mode (fail-closed)."
                )

            current_html = data.get(html_key)
            if not isinstance(current_html, str) or not current_html.strip():
                raise ValueError(f"State file '{html_key}' is missing or empty; fail-closed.")

            cleaned_html = strip_articles_from_comments(current_html)
            current_ids = get_visible_article_ids(cleaned_html)
            if current_ids and current_ids[0] == article_id and cleaned_html == current_html:
                print(f"Article '{article_id}' is already the first visible article in {state_file}. No changes needed (idempotent).")
                return False

            # Insert new article (and clean any comment pollution)
            updated_html = insert_article_into_html(current_html, article_block, article_id)
            data[html_key] = updated_html
            data[changed_key] = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

            if dry_run:
                print(f"[dry-run] Would insert article '{article_id}' ahead of {current_ids}.")
                return True

            # Create backup of old snapshot before atomic write
            if backup_dir is not None:
                backup_dir.mkdir(parents=True, exist_ok=True)
                timestamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
                backup_file = backup_dir / f"latest-information-backup-{timestamp}.json"
                shutil.copy2(state_file, backup_file)
                print(f"Backed up pre-update snapshot to {backup_file}")
                if record_backup_path is not None:
                    record_backup_path.parent.mkdir(parents=True, exist_ok=True)
                    record_backup_path.write_text(str(backup_file), encoding="utf-8")

            # Preserve existing file permissions
            st = state_file.stat()
            file_mode = stat.S_IMODE(st.st_mode)

            # Atomic write via temporary file
            tmp_path = state_file.with_name(f"{state_file.name}.tmp.{os.getpid()}")
            with open(tmp_path, "w", encoding="utf-8") as tf:
                json.dump(data, tf, indent=2, ensure_ascii=False)
                tf.write("\n")
            os.chmod(tmp_path, file_mode)
            os.replace(tmp_path, state_file)

            updated_ids = get_article_ids(updated_html)
            print(f"Successfully inserted '{article_id}'. Active articles: {updated_ids}")
            return True
        finally:
            fcntl.flock(lf, fcntl.LOCK_UN)
            try:
                lock_file.unlink(missing_ok=True)
            except OSError:
                pass


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Safely and idempotently insert an update article into persisted latest-information.json."
    )
    parser.add_argument(
        "--state-file",
        type=Path,
        required=True,
        help="Path to persisted latest-information.json",
    )
    parser.add_argument(
        "--source-docs",
        type=Path,
        required=True,
        help="Path to source docs HTML (e.g. docs/updates-page.html)",
    )
    parser.add_argument(
        "--article-id",
        type=str,
        default=DEFAULT_ARTICLE_ID,
        help=f"Article ID to insert (default: {DEFAULT_ARTICLE_ID})",
    )
    parser.add_argument(
        "--backup-dir",
        type=Path,
        default=None,
        help="Directory to store pre-write backup snapshot",
    )
    parser.add_argument(
        "--record-backup-path",
        type=Path,
        default=None,
        help="Path to record generated backup filename for CI rollback handling",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Check and preview changes without modifying disk",
    )

    args = parser.parse_args()
    try:
        update_latest_information(
            state_file=args.state_file.resolve(),
            source_docs=args.source_docs.resolve(),
            article_id=args.article_id,
            backup_dir=args.backup_dir.resolve() if args.backup_dir else None,
            record_backup_path=args.record_backup_path.resolve() if args.record_backup_path else None,
            dry_run=args.dry_run,
        )
        return 0
    except Exception as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
