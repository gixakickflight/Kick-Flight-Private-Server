#!/usr/bin/env python3
"""Export a tightly bounded set of readable views from one recovered bundle.

Only PNG textures and Wavefront OBJ mesh views are written. Original bundles
remain untouched and output includes hashes/provenance, not source bytes.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

DEFAULT_ASSETS_ROOT = Path("/home/gixarde3/Proyectos/KickFlight/Kick-Flight-Assets")


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=Path, required=True, help="One Octo .bundle source file")
    parser.add_argument("--output-dir", type=Path, required=True, help="Destination for converted views and manifest")
    parser.add_argument("--name-regex", required=True, help="Regex matched against Unity object's m_Name")
    parser.add_argument("--types", default="Texture2D,Mesh", help="Allowed Unity types (default: Texture2D,Mesh)")
    parser.add_argument("--max-objects", type=int, default=5, help="Hard export cap (default 5; maximum 32)")
    parser.add_argument("--assets-root", type=Path, default=DEFAULT_ASSETS_ROOT, help="Checkout containing reconstruct_unity_bundles.py")
    args = parser.parse_args()
    if not args.bundle.is_file():
        parser.error(f"bundle does not exist: {args.bundle}")
    if not 1 <= args.max_objects <= 32:
        parser.error("--max-objects must be between 1 and 32")
    if not (args.assets_root / "reconstruct_unity_bundles.py").is_file():
        parser.error(f"asset pipeline not found: {args.assets_root}")
    sys.path.insert(0, str(args.assets_root))
    try:
        import UnityPy
        from reconstruct_unity_bundles import repair_bundle_bytes
    except Exception as exc:
        parser.error(f"could not load existing asset-pipeline dependencies: {exc}")
    matcher = re.compile(args.name_regex, re.IGNORECASE)
    allowed = {value.strip() for value in args.types.split(",") if value.strip()}
    original = args.bundle.read_bytes()
    rebuilt = repair_bundle_bytes(original)
    env = UnityPy.load(rebuilt.data)
    selected = []
    for obj in env.objects:
        if obj.type.name not in allowed:
            continue
        try:
            value = obj.read()
            name = getattr(value, "m_Name", "")
        except Exception:
            continue
        if isinstance(name, str) and matcher.search(name):
            selected.append((obj, value, name))
    selected.sort(key=lambda item: (item[0].type.name, item[2], item[0].path_id))
    if len(selected) > args.max_objects:
        parser.error(f"selector matched {len(selected)} objects, exceeding --max-objects={args.max_objects}; narrow --name-regex or raise the explicit cap")
    args.output_dir.mkdir(parents=True, exist_ok=True)
    exported = []
    for obj, value, name in selected:
        safe_name = re.sub(r"[^A-Za-z0-9._-]+", "_", name).strip("._") or "unnamed"
        if obj.type.name == "Texture2D":
            path = args.output_dir / f"{safe_name}-{obj.path_id}.png"
            value.image.save(path)
            width, height = value.m_Width, value.m_Height
            output_type = "PNG"
        elif obj.type.name == "Mesh":
            path = args.output_dir / f"{safe_name}-{obj.path_id}.obj"
            rendered = value.export(format="obj")
            path.write_text(rendered, encoding="utf-8")
            width = height = None
            output_type = "Wavefront OBJ"
        else:
            continue
        data = path.read_bytes()
        exported.append({
            "unity_type": obj.type.name,
            "name": name,
            "path_id": int(obj.path_id),
            "output_file": path.name,
            "output_format": output_type,
            "output_size": len(data),
            "output_sha256": digest(data),
            "width": width,
            "height": height,
        })
    manifest = {
        "schema_version": 1,
        "tool": "export_unity_sample.py",
        "unitypy_version": getattr(UnityPy, "__version__", "unknown"),
        "source_file": args.bundle.name,
        "source_size": len(original),
        "source_sha256": digest(original),
        "reconstructed_size": len(rebuilt.data),
        "reconstructed_sha256": digest(rebuilt.data),
        "repairs": list(rebuilt.repairs),
        "unity_engine_versions": sorted({str(getattr(bundle, "version_engine", "")) for bundle in env.files.values()}),
        "exported_count": len(exported),
        "exports": exported,
    }
    (args.output_dir / "export_manifest.json").write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps({"exported_count": len(exported), "output_dir": str(args.output_dir), "manifest": str(args.output_dir / "export_manifest.json")}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
