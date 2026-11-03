"""SQLite durable persistence layer for collaborative badge catalog.

Features:
- Schema migrations and automated initialization from seed data.
- Durable SQLite in WAL mode with busy timeout and foreign keys.
- Attribution/audit per individual field edit.
- Concurrency version checks (optimistic locking) to prevent overwrites.
- Hot-backup awareness via sqlite3.Connection.backup().
- Strict size limits and validation on all stored fields.
"""

from __future__ import annotations

import contextlib
import json
import logging
import os
import sqlite3
import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

logger = logging.getLogger("kickflight.badges.db")

MAX_PLAYER_NAME_LENGTH = 32
MAX_NAME_LENGTH = 100
MAX_DESCRIPTION_LENGTH = 1000
MAX_REQUIREMENTS_LENGTH = 1000
MAX_NOTES_LENGTH = 1000

EDITABLE_FIELDS = ("name", "description", "requirements", "notes", "category", "secret")


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


class BadgeDatabase:
    def __init__(self, db_path: Path | str, seed_file: Path | str | None = None):
        self.db_path = Path(db_path).resolve()
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        if seed_file:
            self.seed_file = Path(seed_file).resolve()
        else:
            self.seed_file = Path(__file__).resolve().parent / "data" / "badges_seed.json"
        self._lock = threading.Lock()
        self.init_db()

    @contextlib.contextmanager
    def get_connection(self):
        conn = sqlite3.connect(
            str(self.db_path),
            timeout=10.0,
            check_same_thread=False,
            isolation_level=None,  # autocommit mode, transactions started explicitly
        )
        conn.row_factory = sqlite3.Row
        conn.execute("PRAGMA journal_mode = WAL;")
        conn.execute("PRAGMA synchronous = NORMAL;")
        conn.execute("PRAGMA foreign_keys = ON;")
        conn.execute("PRAGMA busy_timeout = 5000;")
        try:
            yield conn
        finally:
            conn.close()

    def init_db(self) -> None:
        with self._lock, self.get_connection() as conn:
            conn.execute("BEGIN IMMEDIATE;")
            try:
                conn.execute("""
                    CREATE TABLE IF NOT EXISTS schema_migrations (
                        version INTEGER PRIMARY KEY,
                        name TEXT NOT NULL,
                        applied_at TEXT NOT NULL
                    );
                """)
                cur = conn.execute("SELECT version FROM schema_migrations WHERE version = 1;")
                if not cur.fetchone():
                    logger.info("Applying migration 1: Initial badge schema and seed...")
                    conn.execute("""
                        CREATE TABLE IF NOT EXISTS badges (
                            id INTEGER PRIMARY KEY,
                            name TEXT NOT NULL DEFAULT '',
                            description TEXT NOT NULL DEFAULT '',
                            requirements TEXT NOT NULL DEFAULT '',
                            category INTEGER NOT NULL DEFAULT 0,
                            status TEXT NOT NULL DEFAULT 'unknown',
                            secret INTEGER NOT NULL DEFAULT 0,
                            notes TEXT NOT NULL DEFAULT '',
                            sort_order INTEGER NOT NULL DEFAULT 0,
                            version INTEGER NOT NULL DEFAULT 1,
                            image TEXT NOT NULL,
                            original_source TEXT NOT NULL DEFAULT '[]',
                            rule_json TEXT NOT NULL DEFAULT '{}',
                            updated_at TEXT NOT NULL,
                            updated_by TEXT NOT NULL DEFAULT ''
                        );
                    """)
                    conn.execute("""
                        CREATE TABLE IF NOT EXISTS badge_audit_log (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            badge_id INTEGER NOT NULL,
                            field_name TEXT NOT NULL,
                            old_value TEXT,
                            new_value TEXT,
                            player_name TEXT NOT NULL,
                            badge_version INTEGER NOT NULL,
                            client_ip TEXT NOT NULL DEFAULT '',
                            created_at TEXT NOT NULL,
                            FOREIGN KEY (badge_id) REFERENCES badges(id) ON DELETE CASCADE
                        );
                    """)
                    conn.execute("CREATE INDEX IF NOT EXISTS idx_audit_badge ON badge_audit_log(badge_id);")
                    conn.execute("CREATE INDEX IF NOT EXISTS idx_audit_created ON badge_audit_log(created_at);")

                    # Seed badges if empty
                    count_row = conn.execute("SELECT COUNT(*) AS cnt FROM badges;").fetchone()
                    if count_row["cnt"] == 0 and self.seed_file.exists():
                        self._seed_badges(conn)

                    conn.execute(
                        "INSERT INTO schema_migrations (version, name, applied_at) VALUES (?, ?, ?);",
                        (1, "initial_badges_and_audit", utc_now_iso())
                    )
                conn.execute("COMMIT;")
            except Exception:
                conn.execute("ROLLBACK;")
                raise

    def _seed_badges(self, conn: sqlite3.Connection) -> None:
        data = json.loads(self.seed_file.read_text(encoding="utf-8"))
        badges = data.get("badges", [])
        now = utc_now_iso()
        logger.info("Seeding %d badges into SQLite...", len(badges))
        for b in badges:
            conn.execute("""
                INSERT INTO badges (
                    id, name, description, requirements, category, status, secret, notes,
                    sort_order, version, image, original_source, rule_json, updated_at, updated_by
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
            """, (
                b["id"],
                b.get("name", ""),
                b.get("description", ""),
                b.get("requirements", ""),
                b.get("category", 0),
                b.get("status", "unknown"),
                b.get("secret", 0),
                b.get("notes", ""),
                b.get("sort_order", b["id"]),
                1,
                b.get("image", ""),
                json.dumps(b.get("original_source", []), ensure_ascii=False),
                json.dumps(b.get("rule", {}), ensure_ascii=False),
                now,
                "system-seed"
            ))

    def get_seed_metadata(self) -> dict[str, Any]:
        if self.seed_file.exists():
            data = json.loads(self.seed_file.read_text(encoding="utf-8"))
            return {
                "glyph_image": data.get("glyph_image", ""),
                "unmapped_requirements": data.get("unmapped_requirements", []),
            }
        return {"glyph_image": "", "unmapped_requirements": []}

    def get_all_badges(self) -> list[dict[str, Any]]:
        with self.get_connection() as conn:
            rows = conn.execute("""
                SELECT b.id, b.name, b.description, b.requirements, b.category, b.status,
                       b.secret, b.notes, b.sort_order, b.version, b.image, b.original_source,
                       b.rule_json, b.updated_at, b.updated_by,
                       COUNT(a.id) as edit_count
                FROM badges b
                LEFT JOIN badge_audit_log a ON b.id = a.badge_id
                GROUP BY b.id
                ORDER BY b.sort_order ASC, b.id ASC;
            """).fetchall()

            result = []
            for r in rows:
                item = dict(r)
                item["original_source"] = json.loads(item["original_source"])
                item["rule_json"] = json.loads(item["rule_json"])
                result.append(item)
            return result

    def get_badge(self, badge_id: int) -> dict[str, Any] | None:
        with self.get_connection() as conn:
            row = conn.execute("""
                SELECT b.id, b.name, b.description, b.requirements, b.category, b.status,
                       b.secret, b.notes, b.sort_order, b.version, b.image, b.original_source,
                       b.rule_json, b.updated_at, b.updated_by,
                       COUNT(a.id) as edit_count
                FROM badges b
                LEFT JOIN badge_audit_log a ON b.id = a.badge_id
                WHERE b.id = ?
                GROUP BY b.id;
            """, (badge_id,)).fetchone()
            if not row:
                return None
            item = dict(row)
            item["original_source"] = json.loads(item["original_source"])
            item["rule_json"] = json.loads(item["rule_json"])
            return item

    def get_poll_updates(self, since: str | None = None) -> dict[str, Any]:
        with self.get_connection() as conn:
            if since:
                rows = conn.execute("""
                    SELECT id, name, description, requirements, category, status,
                           secret, notes, version, updated_at, updated_by
                    FROM badges
                    WHERE updated_at > ?
                    ORDER BY updated_at ASC;
                """, (since,)).fetchall()
            else:
                rows = conn.execute("""
                    SELECT id, name, description, requirements, category, status,
                           secret, notes, version, updated_at, updated_by
                    FROM badges
                    ORDER BY id ASC;
                """).fetchall()

            total_edits = conn.execute("SELECT COUNT(*) AS cnt FROM badge_audit_log;").fetchone()["cnt"]
            return {
                "server_time": utc_now_iso(),
                "updates": [dict(r) for r in rows],
                "total_edits": total_edits,
            }

    def save_edits(self, player_name: str, edits: list[dict[str, Any]], client_ip: str = "") -> dict[str, Any]:
        """Save collaborative edits with optimistic concurrency checks.

        Returns:
            dict with:
                applied: list of successfully applied edit summaries
                conflicts: list of badges rejected due to version mismatch
                errors: list of validation errors
        """
        # Validate player name
        cleaned_player_name = (player_name or "").strip()
        if not cleaned_player_name:
            return {"status": "error", "message": "Player name is required."}
        if len(cleaned_player_name) > MAX_PLAYER_NAME_LENGTH:
            return {"status": "error", "message": f"Player name cannot exceed {MAX_PLAYER_NAME_LENGTH} characters."}
        if any(ord(c) < 32 for c in cleaned_player_name):
            return {"status": "error", "message": "Player name contains invalid control characters."}

        applied = []
        conflicts = []
        errors = []

        now = utc_now_iso()

        with self._lock, self.get_connection() as conn:
            conn.execute("BEGIN IMMEDIATE;")
            try:
                for edit in edits:
                    badge_id = edit.get("badge_id")
                    if not isinstance(badge_id, int):
                        errors.append({"edit": edit, "error": "Invalid badge_id"})
                        continue

                    base_version = edit.get("base_version")
                    if not isinstance(base_version, int):
                        errors.append({"badge_id": badge_id, "error": "base_version must be an integer"})
                        continue

                    fields = edit.get("fields", {})
                    if not isinstance(fields, dict) or not fields:
                        continue

                    # Fetch current state
                    cur = conn.execute("""
                        SELECT id, name, description, requirements, category, secret, notes, version
                        FROM badges
                        WHERE id = ?;
                    """, (badge_id,)).fetchone()

                    if not cur:
                        errors.append({"badge_id": badge_id, "error": "Badge not found"})
                        continue

                    current_version = cur["version"]
                    if current_version != base_version:
                        # Concurrency conflict detected!
                        conflicts.append({
                            "badge_id": badge_id,
                            "base_version": base_version,
                            "server_version": current_version,
                            "server_fields": {
                                "name": cur["name"],
                                "description": cur["description"],
                                "requirements": cur["requirements"],
                                "notes": cur["notes"],
                                "category": cur["category"],
                                "secret": cur["secret"],
                            }
                        })
                        continue

                    # Validate length limits
                    val_err = self._validate_fields(fields)
                    if val_err:
                        errors.append({"badge_id": badge_id, "error": val_err})
                        continue

                    # Determine changes and record individual audits
                    changed_fields: dict[str, Any] = {}
                    for field, new_val in fields.items():
                        if field not in EDITABLE_FIELDS:
                            continue
                        old_val = cur[field]
                        # Normalize values
                        if field in ("category", "secret"):
                            new_val = int(new_val)
                            old_val = int(old_val)
                        else:
                            new_val = str(new_val).strip()
                            old_val = str(old_val).strip()

                        if new_val != old_val:
                            changed_fields[field] = new_val
                            conn.execute("""
                                INSERT INTO badge_audit_log (
                                    badge_id, field_name, old_value, new_value,
                                    player_name, badge_version, client_ip, created_at
                                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?);
                            """, (
                                badge_id,
                                field,
                                str(old_val),
                                str(new_val),
                                cleaned_player_name,
                                current_version + 1,
                                client_ip,
                                now
                            ))

                    if changed_fields:
                        new_version = current_version + 1
                        set_clauses = [f"{f} = ?" for f in changed_fields.keys()]
                        set_clauses.extend(["version = ?", "updated_at = ?", "updated_by = ?"])
                        params = list(changed_fields.values()) + [new_version, now, cleaned_player_name, badge_id, current_version]
                        
                        update_cur = conn.execute(
                            f"UPDATE badges SET {', '.join(set_clauses)} WHERE id = ? AND version = ?;",
                            params
                        )
                        if update_cur.rowcount == 0:
                            # Concurrent modification during loop
                            conflicts.append({
                                "badge_id": badge_id,
                                "base_version": base_version,
                                "server_version": current_version + 1,
                            })
                            continue

                        applied.append({
                            "badge_id": badge_id,
                            "new_version": new_version,
                            "changed_fields": list(changed_fields.keys()),
                            "updated_at": now,
                            "updated_by": cleaned_player_name,
                        })

                conn.execute("COMMIT;")
            except Exception:
                conn.execute("ROLLBACK;")
                raise

        return {
            "status": "ok" if (applied or (not conflicts and not errors)) else ("conflict" if conflicts else "error"),
            "applied": applied,
            "conflicts": conflicts,
            "errors": errors,
            "server_time": now,
        }

    def _validate_fields(self, fields: dict[str, Any]) -> str | None:
        if "name" in fields and len(str(fields["name"])) > MAX_NAME_LENGTH:
            return f"Name cannot exceed {MAX_NAME_LENGTH} characters."
        if "description" in fields and len(str(fields["description"])) > MAX_DESCRIPTION_LENGTH:
            return f"Description cannot exceed {MAX_DESCRIPTION_LENGTH} characters."
        if "requirements" in fields and len(str(fields["requirements"])) > MAX_REQUIREMENTS_LENGTH:
            return f"Requirements cannot exceed {MAX_REQUIREMENTS_LENGTH} characters."
        if "notes" in fields and len(str(fields["notes"])) > MAX_NOTES_LENGTH:
            return f"Notes cannot exceed {MAX_NOTES_LENGTH} characters."
        if "category" in fields:
            try:
                c = int(fields["category"])
                if c not in (0, 1, 2):
                    return "Category must be 0 (Other), 1 (Battle), or 2 (Kicker)."
            except (ValueError, TypeError):
                return "Category must be an integer."
        if "secret" in fields:
            try:
                s = int(fields["secret"])
                if s not in (0, 1):
                    return "Secret must be 0 or 1."
            except (ValueError, TypeError):
                return "Secret must be 0 or 1."
        return None

    def get_audit_log(self, badge_id: int | None = None, limit: int = 50) -> list[dict[str, Any]]:
        with self.get_connection() as conn:
            if badge_id is not None:
                rows = conn.execute("""
                    SELECT id, badge_id, field_name, old_value, new_value,
                           player_name, badge_version, created_at
                    FROM badge_audit_log
                    WHERE badge_id = ?
                    ORDER BY id DESC
                    LIMIT ?;
                """, (badge_id, limit)).fetchall()
            else:
                rows = conn.execute("""
                    SELECT id, badge_id, field_name, old_value, new_value,
                           player_name, badge_version, created_at
                    FROM badge_audit_log
                    ORDER BY id DESC
                    LIMIT ?;
                """, (limit,)).fetchall()
            return [dict(r) for r in rows]

    def backup(self, destination_path: Path | str) -> None:
        """Create a hot backup of the SQLite database safely under lock."""
        dest = Path(destination_path).resolve()
        dest.parent.mkdir(parents=True, exist_ok=True)
        with self._lock:
            with self.get_connection() as src_conn:
                dest_conn = sqlite3.connect(str(dest))
                try:
                    src_conn.backup(dest_conn)
                finally:
                    dest_conn.close()

    def get_contributors(self) -> list[dict[str, Any]]:
        """Return aggregated list of unique contributors for review and reward distribution."""
        with self.get_connection() as conn:
            rows = conn.execute("""
                SELECT
                    player_name,
                    COUNT(*) AS total_edits,
                    COUNT(DISTINCT badge_id) AS distinct_badges,
                    MIN(created_at) AS first_contribution,
                    MAX(created_at) AS last_contribution,
                    GROUP_CONCAT(DISTINCT field_name) AS fields_edited
                FROM badge_audit_log
                WHERE player_name != '' AND player_name != 'system-seed'
                GROUP BY player_name
                ORDER BY total_edits DESC, last_contribution DESC;
            """).fetchall()
            return [dict(r) for r in rows]


def main() -> None:
    import argparse
    parser = argparse.ArgumentParser(description="Kick Flight Badge Database Utilities")
    parser.add_argument("--db", default=None, help="Path to SQLite database")
    parser.add_argument("--backup", help="Perform hot backup to target file path")
    parser.add_argument("--contributors", action="store_true", help="List all unique contributors")
    parser.add_argument("--status", action="store_true", help="Show database migration and record counts")
    args = parser.parse_args()

    db_path = args.db or os.environ.get("KF_BADGES_DB_PATH", str(Path(__file__).resolve().parents[2] / ".local" / "badges" / "badges.sqlite3"))
    db = BadgeDatabase(db_path)

    if args.backup:
        db.backup(args.backup)
        print(f"Hot backup written successfully to {args.backup}")
    elif args.contributors:
        contribs = db.get_contributors()
        print(f"Total Unique Contributors: {len(contribs)}")
        for c in contribs:
            print(f"- {c['player_name']}: {c['total_edits']} edits across {c['distinct_badges']} badges (last: {c['last_contribution']})")
    elif args.status:
        badges = db.get_all_badges()
        audits = len(db.get_audit_log(limit=1000000))
        contribs = db.get_contributors()
        print(f"Database: {db.db_path}")
        print(f"Total Badges: {len(badges)}")
        print(f"Total Audit Log Records: {audits}")
        print(f"Unique Contributors: {len(contribs)}")
    else:
        parser.print_help()


if __name__ == "__main__":
    main()
