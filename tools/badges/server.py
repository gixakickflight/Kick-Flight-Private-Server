#!/usr/bin/env python3
"""Public Collaborative Badge Catalog Server for Kick-Flight.

Serves the collaborative /badges web interface and JSON API:
- Durably backed by SQLite in WAL mode with migrations.
- Optimistic concurrency version checks on edits.
- Attribution/audit per individual field edit.
- 30-second polling and autosave support.
- Loopback-only protected hot backup endpoint.
- No unauthenticated admin endpoints.
"""

from __future__ import annotations

import argparse
import csv
import io
import json
import logging
import os
import sys
from datetime import datetime, timezone
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

from db import BadgeDatabase, MAX_PLAYER_NAME_LENGTH

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(name)s: %(message)s")
logger = logging.getLogger("kickflight.badges.server")

BADGES_DIR = Path(__file__).resolve().parent
REPO_ROOT = BADGES_DIR.parent.parent
INDEX_FILE = BADGES_DIR / "index.html"
I18N_FILE = BADGES_DIR / "i18n.js"
SEED_FILE = BADGES_DIR / "data" / "badges_seed.json"

DEFAULT_PORT = int(os.environ.get("KF_BADGES_PORT", "8766"))
DEFAULT_HOST = os.environ.get("KF_BADGES_HOST", "0.0.0.0")
DEFAULT_DB_PATH = os.environ.get("KF_BADGES_DB_PATH", str(REPO_ROOT / ".local" / "badges" / "badges.sqlite3"))

LOOPBACK_HOSTS = frozenset(("127.0.0.1", "::1", "localhost"))


def is_loopback(address: str) -> bool:
    if not address:
        return False
    clean = address.split("%")[0].strip("[]")
    return clean in LOOPBACK_HOSTS or clean.startswith("127.")


class BadgesRequestHandler(BaseHTTPRequestHandler):
    server_version = "KickFlightBadges/1.0"

    @property
    def db(self) -> BadgeDatabase:
        return self.server.db  # type: ignore

    def _send_headers(self, status: int, content_type: str, cache_control: str = "no-cache") -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Cache-Control", cache_control)
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("X-Frame-Options", "SAMEORIGIN")
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type, X-Requested-With")
        self.end_headers()

    def do_OPTIONS(self) -> None:
        self._send_headers(HTTPStatus.NO_CONTENT, "text/plain")

    def do_GET(self) -> None:
        parsed = urlsplit(self.path)
        path = parsed.path.rstrip("/")
        if not path:
            path = "/"

        if path in ("", "/"):
            # Redirect root / to /badges
            self.send_response(HTTPStatus.FOUND)
            self.send_header("Location", "/badges")
            self.end_headers()
            return

        if path in ("/badges", "/badges/index.html"):
            if not INDEX_FILE.exists():
                self._send_headers(HTTPStatus.NOT_FOUND, "text/plain")
                self.wfile.write(b"index.html not found")
                return
            content = INDEX_FILE.read_bytes()
            self._send_headers(HTTPStatus.OK, "text/html; charset=utf-8")
            self.wfile.write(content)
            return

        if path == "/badges/i18n.js":
            if not I18N_FILE.exists():
                self._send_headers(HTTPStatus.NOT_FOUND, "text/plain")
                self.wfile.write(b"i18n.js not found")
                return
            content = I18N_FILE.read_bytes()
            self._send_headers(HTTPStatus.OK, "application/javascript; charset=utf-8")
            self.wfile.write(content)
            return

        if path == "/badges/health":
            body = {
                "status": "ok",
                "db": "healthy",
                "db_path": str(self.db.db_path),
                "timestamp": datetime.now(timezone.utc).isoformat(),
            }
            data = json.dumps(body).encode("utf-8")
            self._send_headers(HTTPStatus.OK, "application/json; charset=utf-8")
            self.wfile.write(data)
            return

        if path == "/badges/api/catalog":
            badges = self.db.get_all_badges()
            meta = self.db.get_seed_metadata()
            payload = {
                "status": "ok",
                "server_time": datetime.now(timezone.utc).isoformat(),
                "badge_count": len(badges),
                "glyph_image": meta["glyph_image"],
                "unmapped_requirements": meta["unmapped_requirements"],
                "badges": badges,
            }
            data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
            self._send_headers(HTTPStatus.OK, "application/json; charset=utf-8")
            self.wfile.write(data)
            return

        if path == "/badges/api/poll":
            query = parse_qs(parsed.query)
            since = query.get("since", [None])[0]
            poll_data = self.db.get_poll_updates(since=since)
            data = json.dumps(poll_data, ensure_ascii=False).encode("utf-8")
            self._send_headers(HTTPStatus.OK, "application/json; charset=utf-8")
            self.wfile.write(data)
            return

        if path == "/badges/api/audit":
            query = parse_qs(parsed.query)
            badge_id_str = query.get("badge_id", [None])[0]
            limit_str = query.get("limit", ["50"])[0]
            badge_id = int(badge_id_str) if badge_id_str and badge_id_str.isdigit() else None
            limit = min(int(limit_str) if limit_str.isdigit() else 50, 200)
            logs = self.db.get_audit_log(badge_id=badge_id, limit=limit)
            payload = {
                "status": "ok",
                "badge_id": badge_id,
                "count": len(logs),
                "logs": logs,
            }
            data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
            self._send_headers(HTTPStatus.OK, "application/json; charset=utf-8")
            self.wfile.write(data)
            return

        if path == "/badges/api/contributors":
            query = parse_qs(parsed.query)
            fmt = query.get("format", ["json"])[0].lower()
            contribs = self.db.get_contributors()
            total_edits = sum(c["total_edits"] for c in contribs)

            if fmt == "csv":
                output = io.StringIO()
                writer = csv.writer(output)
                writer.writerow(["player_name", "total_edits", "distinct_badges", "first_contribution", "last_contribution", "fields_edited"])
                for c in contribs:
                    writer.writerow([
                        c["player_name"],
                        c["total_edits"],
                        c["distinct_badges"],
                        c["first_contribution"],
                        c["last_contribution"],
                        c["fields_edited"] or "",
                    ])
                csv_bytes = output.getvalue().encode("utf-8")
                self.send_response(HTTPStatus.OK)
                self.send_header("Content-Type", "text/csv; charset=utf-8")
                self.send_header("Content-Disposition", 'attachment; filename="kickflight_badge_contributors.csv"')
                self.send_header("Cache-Control", "no-cache")
                self.end_headers()
                self.wfile.write(csv_bytes)
                return

            if fmt == "text":
                lines = [
                    "Kick Flight Badge Catalog - Contributors Leaderboard & Export",
                    f"Generated: {datetime.now(timezone.utc).isoformat()}",
                    f"Total Unique Contributors: {len(contribs)} | Total Edits: {total_edits}",
                    "Reward Policy: Exclusive in-game badge delivered manually by admins based on supplied player names.",
                    "-" * 80,
                    f"{'Player Name':<32} {'Edits':<8} {'Badges':<8} {'Last Active':<20}",
                    "-" * 80,
                ]
                for c in contribs:
                    lines.append(f"{c['player_name']:<32} {c['total_edits']:<8} {c['distinct_badges']:<8} {c['last_contribution'][:19]:<20}")
                text_bytes = "\n".join(lines).encode("utf-8")
                self._send_headers(HTTPStatus.OK, "text/plain; charset=utf-8")
                self.wfile.write(text_bytes)
                return

            # Default JSON
            payload = {
                "status": "ok",
                "contributor_count": len(contribs),
                "total_edits": total_edits,
                "reward_policy": "Contributors receive an exclusive in-game badge, delivered manually based on supplied player names (no automated granting).",
                "contributors": contribs,
            }
            data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
            self._send_headers(HTTPStatus.OK, "application/json; charset=utf-8")
            self.wfile.write(data)
            return

        # 404 for unknown endpoints
        self._send_headers(HTTPStatus.NOT_FOUND, "application/json; charset=utf-8")
        self.wfile.write(json.dumps({"error": "not_found", "path": path}).encode("utf-8"))

    def do_POST(self) -> None:
        parsed = urlsplit(self.path)
        path = parsed.path.rstrip("/")

        if path == "/badges/api/edits":
            content_length = int(self.headers.get("Content-Length", 0))
            if content_length > 1024 * 1024:  # 1 MB max body
                self._send_headers(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, "application/json; charset=utf-8")
                self.wfile.write(json.dumps({"error": "Payload exceeds 1MB limit"}).encode("utf-8"))
                return

            body = self.rfile.read(content_length)
            try:
                payload = json.loads(body.decode("utf-8"))
            except Exception as e:
                self._send_headers(HTTPStatus.BAD_REQUEST, "application/json; charset=utf-8")
                self.wfile.write(json.dumps({"error": f"Invalid JSON: {e}"}).encode("utf-8"))
                return

            player_name = payload.get("player_name", "")
            edits = payload.get("edits", [])
            if not isinstance(edits, list):
                self._send_headers(HTTPStatus.BAD_REQUEST, "application/json; charset=utf-8")
                self.wfile.write(json.dumps({"error": "edits must be a list"}).encode("utf-8"))
                return

            client_ip = self.headers.get("X-Real-IP") or self.client_address[0]
            result = self.db.save_edits(player_name, edits, client_ip=client_ip)

            status_code = HTTPStatus.OK
            if result.get("status") == "conflict":
                status_code = HTTPStatus.CONFLICT
            elif result.get("status") == "error":
                status_code = HTTPStatus.BAD_REQUEST

            data = json.dumps(result, ensure_ascii=False).encode("utf-8")
            self._send_headers(status_code, "application/json; charset=utf-8")
            self.wfile.write(data)
            return

        if path == "/badges/api/backup":
            client_ip = self.headers.get("X-Real-IP") or self.client_address[0]
            if not is_loopback(client_ip):
                self._send_headers(HTTPStatus.FORBIDDEN, "application/json; charset=utf-8")
                self.wfile.write(json.dumps({"error": "Loopback only"}).encode("utf-8"))
                return

            content_length = int(self.headers.get("Content-Length", 0))
            dest_path = None
            if content_length > 0:
                try:
                    payload = json.loads(self.rfile.read(content_length).decode("utf-8"))
                    dest_path = payload.get("destination")
                except Exception:
                    pass

            if not dest_path:
                ts = datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")
                dest_path = str(self.db.db_path.parent / f"badges_backup_{ts}.sqlite3")

            try:
                self.db.backup(dest_path)
                data = json.dumps({"status": "ok", "backup_path": str(dest_path)}).encode("utf-8")
                self._send_headers(HTTPStatus.OK, "application/json; charset=utf-8")
                self.wfile.write(data)
            except Exception as e:
                logger.error("Backup failed: %s", e)
                data = json.dumps({"status": "error", "message": str(e)}).encode("utf-8")
                self._send_headers(HTTPStatus.INTERNAL_SERVER_ERROR, "application/json; charset=utf-8")
                self.wfile.write(data)
            return

        self._send_headers(HTTPStatus.NOT_FOUND, "application/json; charset=utf-8")
        self.wfile.write(json.dumps({"error": "not_found", "path": path}).encode("utf-8"))


class BadgesServer(ThreadingHTTPServer):
    def __init__(self, server_address, RequestHandlerClass, db: BadgeDatabase):
        super().__init__(server_address, RequestHandlerClass)
        self.db = db


def main() -> None:
    parser = argparse.ArgumentParser(description="Kick-Flight Badges Collaborative Server")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT, help="Port to listen on")
    parser.add_argument("--host", type=str, default=DEFAULT_HOST, help="Host to bind to")
    parser.add_argument("--db", type=str, default=DEFAULT_DB_PATH, help="Path to SQLite database")
    args = parser.parse_args()

    db = BadgeDatabase(args.db, seed_file=SEED_FILE)
    server = BadgesServer((args.host, args.port), BadgesRequestHandler, db)
    logger.info("Starting Badges server on http://%s:%d (db: %s)", args.host, args.port, args.db)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        logger.info("Shutting down Badges server...")
        server.server_close()


if __name__ == "__main__":
    main()
