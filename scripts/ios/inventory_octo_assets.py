#!/usr/bin/env python3
"""Read-only, bounded metadata inventory for recovered Kick Flight Octo Unity bundles.

The tool repairs each Octo wrapper in memory, loads it with the already-installed
UnityPy, and writes a compact JSON inventory. It never writes bundle/object bytes.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from collections import Counter
from pathlib import Path
from typing import Any

DEFAULT_ASSETS_ROOT = Path("/home/gixarde3/Proyectos/KickFlight/Kick-Flight-Assets")


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def object_record(obj: Any) -> dict[str, Any]:
    record: dict[str, Any] = {
        "type": obj.type.name,
        "path_id": int(obj.path_id),
    }
    try:
        value = obj.read()
    except Exception as exc:
        record["read_error"] = f"{type(exc).__name__}: {exc}"
        return record
    name = getattr(value, "m_Name", None)
    if isinstance(name, str) and name:
        record["name"] = name
    if obj.type.name == "AssetBundle":
        entries = []
        for item in getattr(value, "m_Container", []) or []:
            try:
                # UnityPy exposes m_Container as (logical path, AssetInfo) tuples.
                key, info = item
                asset = getattr(info, "asset", None)
                entries.append({
                    "container_key": str(key),
                    "asset_path_id": int(getattr(asset, "path_id", 0)),
                    "asset_file_id": int(getattr(asset, "file_id", 0)),
                    "preload_index": int(getattr(info, "preloadIndex", 0)),
                    "preload_size": int(getattr(info, "preloadSize", 0)),
                })
            except Exception:
                continue
        if entries:
            record["container_entries"] = entries[:2048]
    if obj.type.name == "Texture2D":
        for attr in ("m_Width", "m_Height", "m_TextureFormat", "m_MipCount"):
            value_attr = getattr(value, attr, None)
            if isinstance(value_attr, (int, str)):
                record[attr] = value_attr
    if obj.type.name == "Mesh":
        for attr in ("m_VertexCount",):
            value_attr = getattr(value, attr, None)
            if isinstance(value_attr, int):
                record[attr] = value_attr
    return record


def inspect_bundle(path: Path, UnityPy: Any, rebuild: Any) -> dict[str, Any]:
    source = path.read_bytes()
    record: dict[str, Any] = {
        "source_file": path.name,
        "source_size": len(source),
        "source_sha256": sha256(source),
    }
    try:
        rebuilt = rebuild(source)
        record["reconstructed_size"] = len(rebuilt.data)
        record["reconstructed_sha256"] = sha256(rebuilt.data)
        record["repairs"] = list(rebuilt.repairs)
        record["nodes"] = [
            {"path": node.path, "size": node.size, "flags": node.flags}
            for node in rebuilt.nodes[:128]
        ]
        env = UnityPy.load(rebuilt.data)
        bundles = []
        for bundle in env.files.values():
            bundles.append({
                "signature": getattr(bundle, "signature", None),
                "format_version": getattr(bundle, "version", None),
                "unity_engine_version": getattr(bundle, "version_engine", None),
                "unity_player_version": getattr(bundle, "version_player", None),
            })
        objects = [object_record(obj) for obj in env.objects]
        record["bundle_metadata"] = bundles
        record["object_count"] = len(objects)
        record["type_counts"] = dict(sorted(Counter(obj["type"] for obj in objects).items()))
        record["objects"] = objects
        record["status"] = "ok"
    except Exception as exc:
        record["status"] = "error"
        record["error"] = f"{type(exc).__name__}: {exc}"
    return record


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True, nargs="+", help="Octo bundle file(s) or 3_unity_bundles directory")
    parser.add_argument("--output", type=Path, required=True, help="JSON inventory destination")
    parser.add_argument("--max-bundles", type=int, default=25, help="Hard processing cap (default: 25; maximum: 10000)")
    parser.add_argument("--assets-root", type=Path, default=DEFAULT_ASSETS_ROOT, help="Checkout containing reconstruct_unity_bundles.py")
    parser.add_argument("--name-regex", help="Retain only matching object records; bundle/type totals remain complete")
    parser.add_argument("--include-all-objects", action="store_true", help="Include every object's metadata; default keeps only named objects and AssetBundle entries")
    args = parser.parse_args()
    if not 1 <= args.max_bundles <= 10000:
        parser.error("--max-bundles must be between 1 and 10000")
    assets_root = args.assets_root
    if not (assets_root / "reconstruct_unity_bundles.py").is_file():
        parser.error(f"asset pipeline not found: {assets_root}")
    sys.path.insert(0, str(assets_root))
    try:
        import UnityPy
        from reconstruct_unity_bundles import repair_bundle_bytes
    except Exception as exc:
        parser.error(f"could not load existing asset-pipeline dependencies: {exc}")
    paths: list[Path] = []
    for input_path in args.input:
        if input_path.is_file():
            paths.append(input_path)
        elif input_path.is_dir():
            paths.extend(sorted(input_path.glob("*.bundle"))[: args.max_bundles])
        else:
            parser.error(f"input does not exist: {input_path}")
    paths = list(dict.fromkeys(paths))
    if len(paths) > args.max_bundles:
        parser.error(f"input has {len(paths)} bundles; cap is {args.max_bundles}")
    if not paths:
        parser.error("no .bundle files found")
    matcher = re.compile(args.name_regex, re.IGNORECASE) if args.name_regex else None
    records = [inspect_bundle(path, UnityPy, repair_bundle_bytes) for path in paths]
    for bundle in records:
        if not args.include_all_objects or matcher:
            objects = bundle.get("objects", [])
            if matcher:
                bundle["objects"] = [obj for obj in objects if matcher.search(obj.get("name", ""))]
            elif not args.include_all_objects:
                bundle["objects"] = [obj for obj in objects if obj.get("name") or obj.get("container_entries") or obj.get("read_error")]
    result = {
        "schema_version": 1,
        "tool": "inventory_octo_assets.py",
        "unitypy_version": getattr(UnityPy, "__version__", "unknown"),
        "source_roots": [str(path.resolve()) for path in args.input],
        "processed_bundle_count": len(records),
        "ok_count": sum(record.get("status") == "ok" for record in records),
        "error_count": sum(record.get("status") != "ok" for record in records),
        "records": records,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps({k: result[k] for k in ("processed_bundle_count", "ok_count", "error_count", "unitypy_version")}, ensure_ascii=False))
    return 1 if result["error_count"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
