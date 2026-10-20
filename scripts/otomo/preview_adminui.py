#!/usr/bin/env python3
"""Load KF's namespaces into a local otomo admin website running on its mock API.

The admin website has a fixture API (`npm run dev:mock`, no backend at all).
This adds each namespace from otomo/namespaces/ to the fixtures of a
DISPOSABLE otomo checkout: otomo itself stays game-agnostic, so never commit
the result. The mock keeps edits in memory only; a page reload resets them.

    git -C <otomo> worktree add --detach <otomo>-kf-preview staging
    python scripts/otomo/preview_adminui.py <otomo>-kf-preview
    (cd <otomo>-kf-preview/services/adminui && npm ci && npm run dev:mock)
    # http://localhost:5173/admin/config
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
NAMESPACES = REPO / "otomo" / "namespaces"
STAMP = "2026-10-20T00:00:00Z"
AUTHOR = "kf-preview@localhost"


def fixture(name: str, schema: dict, document: dict) -> dict:
    return {
        "name": name,
        "audience": "client",
        "description": schema.get("description", ""),
        "schema": {"schema_version": 1, "created_by": AUTHOR, "created_at": STAMP, "body": schema},
        "draft": {"document": document, "revision": 1, "base_version": 1, "updated_by": AUTHOR, "updated_at": STAMP},
        "versions": [{
            "version": 1,
            "schema_version": 1,
            "message": "Imported from the KF masters",
            "created_by": AUTHOR,
            "created_at": STAMP,
            "sha256": "0" * 64,
            "document": document,
        }],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("otomo", type=Path, help="a disposable otomo checkout (worktree)")
    args = parser.parse_args()

    mocks = args.otomo / "services" / "adminui" / "src" / "mocks"
    handlers = mocks / "handlers.ts"
    presentation_path = mocks / "fixtures" / "namespace-ui-presentation.json"
    if not handlers.exists():
        print(f"not an otomo checkout: {args.otomo}", file=sys.stderr)
        return 1

    source = handlers.read_text(encoding="utf-8")
    presentation = json.loads(presentation_path.read_text(encoding="utf-8"))

    for directory in sorted(p for p in NAMESPACES.iterdir() if p.is_dir()):
        name = directory.name
        load = lambda f: json.loads((directory / f).read_text(encoding="utf-8"))
        ident = "kf" + "".join(part.capitalize() for part in re.split(r"[^a-zA-Z0-9]", name) if part) + "Fixture"
        filename = f"namespace-{name.replace('.', '-')}.json"

        (mocks / "fixtures" / filename).write_text(
            json.dumps(fixture(name, load("schema.json"), load("document.json")), ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        if ident not in source:
            source = source.replace(
                "import uiPresentationFixture",
                f"import {ident} from '@/mocks/fixtures/{filename}'\nimport uiPresentationFixture",
                1,
            )
            source = source.replace("  uiPresentationFixture,\n]", f"  {ident},\n  uiPresentationFixture,\n]", 1)

        entry = load("presentation.json")
        presentation["draft"]["document"][name] = entry
        for version in presentation["versions"]:
            version["document"][name] = entry
        print(f"added {name}")

    handlers.write_text(source, encoding="utf-8")
    presentation_path.write_text(json.dumps(presentation, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
