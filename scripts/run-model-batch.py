#!/usr/bin/env python3
"""Portable batch runner for verified Android-to-iOS player model conversion."""
import argparse
import gc
import importlib.util
import json
from pathlib import Path
import re
import sys


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path.cwd(), help="Kick-Flight-Private-Server checkout")
    parser.add_argument("--coverage", type=Path, help="Android/iOS route audit JSON")
    parser.add_argument("--donors", type=Path, help="directory of verified iOS donor Octo bundles")
    parser.add_argument("--shader", type=Path, help="iOS DHDZZX shader CAB Octo payload")
    parser.add_argument("--repair-script", type=Path, required=True, help="Kick-Flight-Assets/reconstruct_unity_bundles.py")
    parser.add_argument("--android-source-root", type=Path, help="root used to resolve androidSourcePath (default --repo)")
    parser.add_argument("--out", type=Path, help="output folder; must not already contain model outputs")
    args = parser.parse_args()
    repo = args.repo.resolve()
    coverage = (args.coverage or repo / ".local/run/ios-cdn-coverage.json").resolve()
    donors = (args.donors or repo / ".local/run/ios-r4-source-audit").resolve()
    shader = (args.shader or donors / "DHDZZX.octo").resolve()
    source_root = (args.android_source_root or repo).resolve()
    output_dir = (args.out or repo / ".local/run/ios-model-conversion").resolve()
    helper = Path(__file__).with_name("build-action-asset-bundles.py")
    for path in (coverage, donors, shader, args.repair_script, helper):
        if not path.exists():
            parser.error(f"required input does not exist: {path}")
    if output_dir.exists() and any(output_dir.iterdir()):
        parser.error(f"output directory must be empty or absent: {output_dir}")
    spec = importlib.util.spec_from_file_location("ios_model_converter", Path(__file__).with_name("convert-android-model-to-ios.py"))
    conv = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = conv
    spec.loader.exec_module(conv)
    data = json.loads(coverage.read_text(encoding="utf-8"))
    rows = [r for r in data["androidEntries"] if re.fullmatch(r"player/pc_\d+/pc_\d+_\d+\.unity3d", r["name"]) and not r.get("iosPresentInManifest")]
    if len(rows) != 216:
        raise SystemExit(f"Expected 216 player skin routes, found {len(rows)}")
    registry, formats, donor_report = conv.build_schema_registry(donors, args.repair_script, helper)
    shader_bytes = shader.read_bytes()
    report = {"expected_count": len(rows), "donors": donor_report, "converted": [], "rejected": []}
    output_dir.mkdir(parents=True, exist_ok=True)
    def resolve_android_source(android_path: str) -> Path:
        relative = Path(android_path)
        candidates = [(source_root / relative).resolve(), (repo / relative).resolve()]
        parts = relative.parts
        if len(parts) >= 3 and parts[:2] == ("..", "Kick-Flight-Assets") and parts[2] == "octo_sorted":
            candidates.append((source_root / "octo_sorted" / Path(*parts[3:])).resolve())
            candidates.append((repo.parent / "Kick-Flight-Assets" / "octo_sorted" / Path(*parts[3:])).resolve())
        for candidate in candidates:
            if candidate.is_file():
                return candidate
        raise conv.ConversionError(f"catalog source absent; tried: {candidates}")

    for index, row in enumerate(rows, 1):
        try:
            source_path = resolve_android_source(row["androidSourcePath"])
            source = source_path.read_bytes()
            output, item = conv.convert_model(source, row["androidExpectedSha256"], row["name"], shader_bytes,
                                              registry, formats, args.repair_script, helper)
            dest = output_dir / (row["name"].replace("/", "__").replace(".unity3d", ".octo"))
            if dest.exists():
                raise conv.ConversionError(f"refusing to overwrite existing output: {dest.name}")
            temp = dest.with_name(dest.name + ".tmp")
            temp.write_bytes(output)
            temp.replace(dest)
            item["output_path"] = str(dest)
            report["converted"].append(item)
            status = f"OK {row['name']} => {len(output)} bytes"
        except Exception as exc:
            report["rejected"].append({"logical_name": row["name"], "object_name": row.get("androidObjectName"),
                                       "source_sha256": row["androidExpectedSha256"], "reason": str(exc)})
            status = f"REJECT {row['name']}: {exc}"
        print(f"[{index}/{len(rows)}] {status}", flush=True)
        if index % 10 == 0:
            (output_dir / "progress.json").write_text(json.dumps({"index": index, "converted": len(report["converted"]),
                                                                    "rejected": len(report["rejected"])}))
            gc.collect()
    report["summary"] = {"converted": len(report["converted"]), "rejected": len(report["rejected"]),
                         "source_bytes": sum(r["androidSourceBytes"] for r in rows),
                         "output_bytes": sum(r["output_bytes"] for r in report["converted"])}
    (output_dir / "conversion-report.json").write_text(json.dumps(report, indent=2, sort_keys=True), encoding="utf-8")
    print(json.dumps(report["summary"], sort_keys=True))
    return 0 if not report["rejected"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
