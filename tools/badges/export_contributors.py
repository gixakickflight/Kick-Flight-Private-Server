#!/usr/bin/env python3
"""Export and review Kick Flight badge catalog contributors.

Used by server administrators to inspect, filter, and export the list of player
names who contributed edits to the collaborative catalog, so that exclusive
contributor badges can be granted manually (no automated granting).

Can query the SQLite database directly or fetch from the badges HTTP API.
"""

from __future__ import annotations

import argparse
import csv
import io
import json
import os
import sys
import urllib.request
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_DB = os.environ.get("KF_BADGES_DB_PATH", str(ROOT / ".local" / "badges" / "badges.sqlite3"))


def fetch_from_api(base_url: str, min_edits: int = 1) -> list[dict[str, Any]]:
    target = f"{base_url.rstrip('/')}/badges/api/contributors"
    req = urllib.request.Request(target, headers={"User-Agent": "kf-contributor-exporter"})
    with urllib.request.urlopen(req, timeout=10) as resp:
        data = json.loads(resp.read().decode("utf-8"))
        contribs = data.get("contributors", [])
        return [c for c in contribs if c.get("total_edits", 0) >= min_edits]


def fetch_from_db(db_path: Path | str, min_edits: int = 1) -> list[dict[str, Any]]:
    from db import BadgeDatabase
    db = BadgeDatabase(db_path)
    contribs = db.get_contributors()
    return [c for c in contribs if c.get("total_edits", 0) >= min_edits]


def format_csv(contributors: list[dict[str, Any]]) -> str:
    output = io.StringIO()
    writer = csv.writer(output)
    writer.writerow(["player_name", "total_edits", "distinct_badges", "first_contribution", "last_contribution", "fields_edited"])
    for c in contributors:
        writer.writerow([
            c["player_name"],
            c["total_edits"],
            c["distinct_badges"],
            c["first_contribution"],
            c["last_contribution"],
            c.get("fields_edited") or "",
        ])
    return output.getvalue()


def format_text(contributors: list[dict[str, Any]]) -> str:
    total_edits = sum(c["total_edits"] for c in contributors)
    lines = [
        "=" * 80,
        "KICK FLIGHT BADGE CATALOG - CONTRIBUTOR EXPORT & REVIEW",
        "=" * 80,
        f"Total Qualified Contributors: {len(contributors)} | Total Edits: {total_edits}",
        "Reward Policy: Exclusive in-game badge delivered manually based on supplied player names.",
        "-" * 80,
        f"{'Player Name':<32} {'Edits':<8} {'Badges':<8} {'Last Active':<20}",
        "-" * 80,
    ]
    for c in contributors:
        lines.append(f"{c['player_name']:<32} {c['total_edits']:<8} {c['distinct_badges']:<8} {c['last_contribution'][:19]:<20}")
    lines.append("-" * 80)
    return "\n".join(lines)


def main() -> None:
    parser = argparse.ArgumentParser(description="Export and review Kick Flight badge catalog contributors")
    parser.add_argument("--db", default=DEFAULT_DB, help="Path to SQLite database")
    parser.add_argument("--url", default=None, help="Badges server base URL (e.g. http://127.0.0.1:8766)")
    parser.add_argument("--format", choices=["text", "csv", "json"], default="text", help="Output format")
    parser.add_argument("--min-edits", type=int, default=1, help="Minimum edits required to be listed")
    parser.add_argument("--output", "-o", default=None, help="Output file path (prints to stdout if omitted)")
    args = parser.parse_args()

    if args.url:
        contributors = fetch_from_api(args.url, min_edits=args.min_edits)
    else:
        db_file = Path(args.db).resolve()
        if not db_file.exists():
            print(f"Error: Database file not found at {db_file}", file=sys.stderr)
            sys.exit(1)
        # Ensure tools/badges directory is in sys.path for db import
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        contributors = fetch_from_db(db_file, min_edits=args.min_edits)

    if args.format == "csv":
        result = format_csv(contributors)
    elif args.format == "json":
        result = json.dumps({
            "status": "ok",
            "contributor_count": len(contributors),
            "total_edits": sum(c["total_edits"] for c in contributors),
            "reward_policy": "Contributors receive an exclusive in-game badge, delivered manually based on supplied player names (no automated granting).",
            "contributors": contributors,
        }, indent=2, ensure_ascii=False)
    else:
        result = format_text(contributors)

    if args.output:
        out_path = Path(args.output).resolve()
        out_path.parent.mkdir(parents=True, exist_ok=True)
        out_path.write_text(result, encoding="utf-8")
        print(f"Exported {len(contributors)} contributors to {out_path}")
    else:
        print(result)


if __name__ == "__main__":
    main()
