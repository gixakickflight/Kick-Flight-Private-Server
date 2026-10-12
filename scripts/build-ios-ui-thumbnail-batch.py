#!/usr/bin/env python3
"""Convert missing Android UI thumbnail bundles using validated iOS templates.

Only routes with an existing iOS thumbnail template are considered. Two
documented sibling routes are allowed for kicker skill-large and stamptab; all
outputs still pass the single-bundle structural and pixel/geometry checks.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CONVERTER_PATH = ROOT / "scripts" / "build-ios-ui-bundle-from-donor.py"
spec = importlib.util.spec_from_file_location("ios_ui_converter", CONVERTER_PATH)
converter = importlib.util.module_from_spec(spec)
assert spec and spec.loader
spec.loader.exec_module(converter)

SIBLING_DONOR_ROUTES = {
    "ui/kicker/skill/large": "ui/kicker/skill",
    "ui/kicker/stamptab": "ui/kicker",
}


def route_for(logical_name: str) -> str | None:
    if not logical_name.startswith("ui/") or "/thumbnail" not in logical_name:
        return None
    return logical_name.rsplit("/thumbnail", 1)[0]


def choose_donors(resources: list[dict]) -> dict[str, dict]:
    result = {}
    for row in resources:
        route = route_for(row.get("logicalName", ""))
        if not route:
            continue
        # Canonical _001 thumbnails first, then stable lexicographic order.
        rank = (0 if "_001.unity3d" in row["logicalName"] else 1, row["logicalName"])
        if route not in result or rank < result[route][0]:
            result[route] = (rank, row)
    return {route: item[1] for route, item in result.items()}


def read_source(entry: dict, source_root: Path, assets_root: Path | None, android_audit: Path) -> tuple[bytes, str]:
    relative = Path(entry["sourcePath"])
    candidates = [source_root / relative]
    if assets_root and str(relative).startswith("../Kick-Flight-Assets/"):
        candidates.append(assets_root / str(relative).removeprefix("../Kick-Flight-Assets/"))
    for candidate in candidates:
        if candidate.is_file():
            return candidate.read_bytes(), str(candidate)
    audit = android_audit / f"{entry['objectName']}.cdn"
    if audit.is_file():
        return audit.read_bytes(), str(audit)
    raise FileNotFoundError(f"no local source for {entry['objectName']}: {candidates[0]}")


def read_donor(row: dict, ios_audit: Path) -> tuple[bytes, str]:
    object_name = row["requestPath"].rsplit("/", 1)[-1]
    path = ios_audit / f"{object_name}.octo"
    data = path.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    if digest != row["sha256"]:
        raise ValueError(f"iOS donor SHA-256 mismatch for {row['logicalName']}: {digest}")
    return data, str(path)


def build_batch(android_manifest: dict, ios_manifest: dict, *, source_root: Path,
                assets_root: Path | None, android_audit: Path, ios_audit: Path,
                output_dir: Path | None) -> dict:
    ios_names = {row["logicalName"] for row in ios_manifest["resources"]}
    ios_donors = choose_donors(ios_manifest["resources"])
    android_names = {}
    for entry in android_manifest["entries"]:
        for name in entry.get("names", []):
            android_names[name] = entry

    candidates = []
    for name, entry in sorted(android_names.items()):
        route = route_for(name)
        if name in ios_names or route is None:
            continue
        donor_route = route if route in ios_donors else SIBLING_DONOR_ROUTES.get(route)
        if donor_route and donor_route in ios_donors:
            candidates.append((name, entry, ios_donors[donor_route]))

    output_rows = []
    failures = []
    counts = Counter()
    for index, (name, entry, donor_row) in enumerate(candidates, 1):
        try:
            source, source_path = read_source(entry, source_root, assets_root, android_audit)
            donor, donor_path = read_donor(donor_row, ios_audit)
            bundle, report = converter.make_bundle(source, donor, name)
            output_path = output_dir / Path(name).with_suffix(".octo") if output_dir else None
            if output_path:
                output_path.parent.mkdir(parents=True, exist_ok=True)
                output_path.write_bytes(bundle)
            row = {
                "logical_name": name,
                "android_object_name": entry["objectName"],
                "source_path": source_path,
                "ios_donor": donor_row["logicalName"],
                "ios_donor_sha256": donor_row["sha256"],
                "ios_donor_path": donor_path,
                "output_path": str(output_path) if output_path else None,
                **report,
            }
            output_rows.append(row)
            counts[name.split("/")[1]] += 1
        except Exception as error:  # Report each refused/unreadable item; continue the independent batch.
            failures.append({
                "logical_name": name,
                "android_object_name": entry.get("objectName"),
                "ios_donor": donor_row.get("logicalName"),
                "error": f"{type(error).__name__}: {error}",
            })
        if index % 250 == 0:
            print(f"{index}/{len(candidates)} converted={len(output_rows)} failures={len(failures)}", flush=True)

    return {
        "candidate_count": len(candidates),
        "converted": len(output_rows),
        "failed": len(failures),
        "converted_by_family": dict(sorted(counts.items())),
        "outputs": output_rows,
        "failures": failures,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--android-manifest", type=Path, default=ROOT / "config/resources/title-minimum.json")
    parser.add_argument("--ios-manifest", type=Path, default=ROOT / "src/KickFlight.BootstrapApi/catalog-ios-12346.json")
    parser.add_argument("--source-root", type=Path, default=ROOT,
                        help="root for title-minimum.json sourcePath entries; normally the server checkout")
    parser.add_argument("--assets-root", type=Path,
                        help="sibling Kick-Flight-Assets root for ../Kick-Flight-Assets paths")
    parser.add_argument("--android-audit", type=Path,
                        help="fallback directory with {objectName}.cdn source bundles")
    parser.add_argument("--ios-audit", type=Path,
                        help="directory with verified {requestPath-id}.octo donor bundles")
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    android_audit = args.android_audit or args.source_root / ".local/run/android-source-audit"
    ios_audit = args.ios_audit or args.source_root / ".local/run/ios-r4-source-audit"
    result = build_batch(
        json.loads(args.android_manifest.read_text(encoding="utf-8-sig")),
        json.loads(args.ios_manifest.read_text(encoding="utf-8-sig")),
        source_root=args.source_root,
        assets_root=args.assets_root,
        android_audit=android_audit,
        ios_audit=ios_audit,
        output_dir=args.output_dir,
    )
    report = args.report or args.output_dir / "conversion-report.json"
    report.parent.mkdir(parents=True, exist_ok=True)
    report.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps({key: value for key, value in result.items() if key not in {"outputs", "failures"}}, indent=2))
    print(f"report: {report}")
    return 1 if result["failed"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
