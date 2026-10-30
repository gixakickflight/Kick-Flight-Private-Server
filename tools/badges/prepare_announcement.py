#!/usr/bin/env python3
"""Prepare and apply an English general announcement redirect pointing to /badges.

Uses the actual announcement mechanism in KickFlight.BootstrapApi (LatestInformationState).
Can update data/latest-information.json directly or POST to /admin/latest-information.
"""

from __future__ import annotations

import argparse
import json
import logging
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")

ROOT = Path(__file__).resolve().parents[2]
DATA_PATH = ROOT / "src/KickFlight.BootstrapApi/data/latest-information.json"

DEFAULT_ANNOUNCEMENT = {
    "Content": "Community Badge Catalog is now available! Explore all 211 badges, inspect high-resolution glyph artwork, and collaborate at /badges",
    "ChangedAtUtc": datetime.now(timezone.utc).isoformat(),
    "Mode": "html",
    "HtmlContent": """<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta http-equiv="refresh" content="0; url=/badges"><title>Kick Flight Community Badge Catalog</title><style>:root{color-scheme:dark}body{font-family:system-ui,-apple-system,sans-serif;background:#10141b;color:#f3f6fa;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;padding:20px;box-sizing:border-box}div{background:#1b222d;border:1px solid #303b4b;padding:32px;border-radius:12px;text-align:center;max-width:500px}h2{margin:0 0 10px;color:#57d7d0}p{color:#aab5c5;margin:0 0 18px;line-height:1.5}a{display:inline-block;background:#184b49;color:#9cf5f0;border:1px solid #2b7774;padding:10px 20px;border-radius:8px;text-decoration:none;font-weight:600}a:hover{background:#226462}</style></head><body><div><h2>Community Badge Catalog</h2><p>Redirecting to the collaborative Kick Flight badge catalog...</p><a href="/badges">Open /badges</a></div><script>window.location.replace('/badges');</script></body></html>""",
    "ExternalUrl": "https://kick-flight-fenix.us.ci/badges",
}


def prepare_file(dest: Path = DATA_PATH) -> None:
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_text(json.dumps(DEFAULT_ANNOUNCEMENT, indent=2), encoding="utf-8")
    logging.info("Wrote announcement redirect configuration to %s", dest)


def post_to_admin(api_url: str = "http://127.0.0.1:8080") -> bool:
    target = f"{api_url.rstrip('/')}/admin/latest-information"
    payload = {
        "mode": "html",
        "htmlContent": DEFAULT_ANNOUNCEMENT["HtmlContent"],
    }
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        target,
        data=data,
        headers={"Content-Type": "application/json"},
        method="POST"
    )
    try:
        with urllib.request.urlopen(req, timeout=5) as res:
            logging.info("Successfully posted announcement redirect to %s (HTTP %d)", target, res.status)
            return True
    except Exception as e:
        logging.warning("Could not reach /admin/latest-information at %s: %s", target, e)
        return False


def main() -> None:
    parser = argparse.ArgumentParser(description="Prepare /badges announcement redirect")
    parser.add_argument("--post", action="store_true", help="Post to running API /admin/latest-information")
    parser.add_argument("--api-url", default="http://127.0.0.1:8080", help="BootstrapApi URL")
    args = parser.parse_args()

    prepare_file()
    if args.post:
        post_to_admin(args.api_url)


if __name__ == "__main__":
    main()
