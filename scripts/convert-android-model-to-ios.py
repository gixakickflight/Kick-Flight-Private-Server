#!/usr/bin/env python3
"""Repack one Android player model bundle with verified iOS Unity metadata.

This converter deliberately does not copy geometry or texture pixels from an iOS
donor. It requires the source type trees and MonoScript identities to match real
iOS bundles, validates every object reference and Metal shader dependency, then
rewrites the complete SerializedFile through UnityPy for target platform 9.
"""
from __future__ import annotations

import argparse
from collections import defaultdict
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import sys
from typing import Any, Iterable


class ConversionError(ValueError):
    pass


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def require_sha256(data: bytes, expected: str) -> str:
    if not re.fullmatch(r"[0-9a-f]{64}", expected or ""):
        raise ConversionError("expected source SHA-256 must be 64 lowercase hex characters")
    actual = sha256(data)
    if actual != expected:
        raise ConversionError(f"source SHA-256 mismatch: expected {expected}, got {actual}")
    return actual


def type_tree_signature(node: Any) -> tuple:
    """Return a deterministic signature for UnityPy's native TypeTreeNode."""
    if node is None:
        return ()
    return (
        getattr(node, "m_Type", None),
        getattr(node, "m_Name", None),
        getattr(node, "m_ByteSize", None),
        getattr(node, "m_Version", None),
        getattr(node, "m_TypeFlags", None),
        getattr(node, "m_MetaFlag", None),
        tuple(type_tree_signature(child) for child in getattr(node, "m_Children", ()) or ()),
    )


def schema_key(typ: Any) -> tuple[int, str | None]:
    script_id = getattr(typ, "script_id", None)
    return int(typ.class_id), script_id.hex() if script_id else None


def schema_signature(typ: Any) -> tuple:
    return (getattr(typ, "old_type_hash", None), type_tree_signature(getattr(typ, "node", None)))


def add_schema(registry: dict, typ: Any, script: tuple[str, str, str] | None = None) -> None:
    record = {"old_type_hash": bytes(typ.old_type_hash).hex(), "tree": type_tree_signature(typ.node)}
    if script is not None:
        record["script"] = script
    registry.setdefault(schema_key(typ), []).append(record)


def require_ios_schema(source_file: Any, registry: dict) -> dict:
    if not getattr(source_file, "_enable_type_tree", False):
        raise ConversionError("source SerializedFile has no TypeTrees")
    checked = []
    for typ in source_file.types:
        key = schema_key(typ)
        candidates = registry.get(key, [])
        expected = schema_signature(typ)
        matching = [candidate for candidate in candidates
                    if candidate["old_type_hash"] == expected[0].hex()
                    and candidate["tree"] == expected[1]]
        if not matching:
            raise ConversionError(
                f"no matching iOS TypeTree for class_id={key[0]} script_id={key[1]} "
                f"old_type_hash={expected[0].hex()}"
            )
        if typ.class_id == 114 and typ.script_id:
            script_index = int(typ.script_type_index)
            if script_index < 0 or script_index >= len(source_file.script_types):
                raise ConversionError(f"MonoBehaviour has invalid script_type_index={script_index}")
            script_local_id = source_file.script_types[script_index].local_identifier_in_file
            script_obj = source_file.objects.get(script_local_id)
            if script_obj is None or script_obj.type.name != "MonoScript":
                raise ConversionError(f"MonoBehaviour script id {script_local_id} has no MonoScript object")
            script_data = script_obj.read()
            script = (str(script_data.m_Name), str(script_data.m_Namespace), str(script_data.m_AssemblyName))
            if not any(candidate.get("script") == script for candidate in matching):
                raise ConversionError(f"iOS MonoScript identity mismatch: {script}")
            checked.append({"name": script[0], "namespace": script[1], "assembly": script[2],
                            "script_id": key[1], "old_type_hash": expected[0].hex()})
    return {"checked_type_count": len(source_file.types), "checked_scripts": sorted(
        {tuple(sorted(item.items())) for item in checked})}


def collect_pointers(value: Any, result: list[tuple[int, int]] | None = None) -> list[tuple[int, int]]:
    """Collect serialized PPtrs from UnityPy typetrees, without following them."""
    if result is None:
        result = []
    if isinstance(value, dict):
        if "m_FileID" in value and "m_PathID" in value:
            result.append((int(value["m_FileID"]), int(value["m_PathID"])))
            return result
        for child in value.values():
            collect_pointers(child, result)
    elif isinstance(value, (tuple, list)):
        for child in value:
            collect_pointers(child, result)
    return result


def require_reference_graph(serialized_file: Any, objects: Iterable[Any]) -> dict:
    object_ids = {int(obj.path_id) for obj in objects}
    refs = []
    external_paths = [str(item.path) for item in serialized_file.externals]
    for obj in objects:
        tree = obj.read_typetree()
        for file_id, path_id in collect_pointers(tree):
            refs.append((int(obj.path_id), file_id, path_id))
            if path_id == 0:
                continue
            if file_id == 0:
                if path_id not in object_ids:
                    raise ConversionError(f"dangling internal PPtr from {obj.path_id} to {path_id}")
            elif file_id < 0 or file_id > len(external_paths):
                raise ConversionError(f"PPtr from {obj.path_id} has invalid file_id={file_id}")
    return {"object_count": len(object_ids), "reference_count": len(refs),
            "reference_sha256": sha256(json.dumps(sorted(refs), separators=(",", ":")).encode()),
            "external_paths": external_paths}


def require_shader_dependency(serialized_file: Any, objects: Iterable[Any], shader_env: Any) -> dict:
    shader_objects = {int(obj.path_id): obj for obj in shader_env.objects if obj.type.name == "Shader"}
    if not shader_objects:
        raise ConversionError("iOS shader bundle contains no Shader objects")
    shader_file = next(obj.assets_file for obj in shader_env.objects if obj.type.name == "Shader")
    if int(shader_file.target_platform) != 9:
        raise ConversionError(f"shader dependency is not iOS target 9: {shader_file.target_platform}")
    shader_cabs = [name for name in shader_env.file.files if not name.endswith(".resS")]
    if len(shader_cabs) != 1:
        raise ConversionError(f"expected one iOS shader CAB, found {len(shader_cabs)}")
    shader_cab = shader_cabs[0]
    metal_ids = set()
    for path_id, obj in shader_objects.items():
        platforms = {int(item) for item in obj.read().platforms}
        if 14 in platforms:
            metal_ids.add(path_id)
    if not metal_ids:
        raise ConversionError("iOS shader bundle has no baked Metal (platform 14) Shader")

    external_records = []
    for external in serialized_file.externals:
        path = str(external.path)
        if shader_cab.casefold() not in path.casefold():
            raise ConversionError(f"unsupported external dependency: {path}")
        external_records.append(path)
    for obj in objects:
        if obj.type.name != "Material":
            continue
        material = obj.read()
        ptr = material.m_Shader
        file_id, path_id = int(ptr.m_FileID), int(ptr.m_PathID)
        if path_id == 0:
            raise ConversionError(f"Material {obj.path_id} has null shader pointer")
        if file_id == 0:
            local = serialized_file.objects.get(path_id)
            if local is None or local.type.name != "Shader":
                raise ConversionError(f"Material {obj.path_id} shader PPtr is not local or resolvable")
        elif file_id <= len(external_records):
            if path_id not in shader_objects:
                raise ConversionError(f"Material {obj.path_id} shader pathID {path_id} missing from iOS shader CAB")
            if path_id not in metal_ids:
                raise ConversionError(f"Material {obj.path_id} shader pathID {path_id} has no baked Metal variant")
        else:
            raise ConversionError(f"Material {obj.path_id} has invalid external shader fileID={file_id}")
    return {"ios_shader_cab": shader_cab, "ios_shader_count": len(shader_objects),
            "metal_shader_count": len(metal_ids), "material_count": sum(o.type.name == "Material" for o in objects),
            "external_paths": external_records}


def texture_snapshot(objects: Iterable[Any], allowed_formats: set[int]) -> dict:
    import UnityPy  # noqa: F401 - load the installed decoder only for conversion

    rows = []
    for obj in objects:
        if obj.type.name != "Texture2D":
            continue
        texture = obj.read()
        texture_format = int(texture.m_TextureFormat)
        if texture_format not in allowed_formats:
            raise ConversionError(f"Texture2D {obj.path_id} format {texture_format} has no verified iOS donor")
        image = texture.image.convert("RGBA")
        pixel_bytes = image.tobytes()
        rows.append({"path_id": int(obj.path_id), "name": str(texture.m_Name),
                     "width": int(texture.m_Width), "height": int(texture.m_Height),
                     "format": texture_format, "mip_count": int(texture.m_MipCount),
                     "rgba_sha256": sha256(pixel_bytes), "rgba_bytes": len(pixel_bytes),
                     "serialized_sha256": sha256(obj.get_raw_data())})
    return {"count": len(rows), "textures": sorted(rows, key=lambda row: row["path_id"])}


def mesh_snapshot(objects: Iterable[Any]) -> dict:
    rows = []
    for obj in objects:
        if obj.type.name != "Mesh":
            continue
        mesh = obj.read()
        rows.append({"path_id": int(obj.path_id), "name": str(mesh.m_Name),
                     "vertex_count": int(mesh.m_VertexData.m_VertexCount),
                     "index_format": int(mesh.m_IndexFormat), "submesh_count": len(mesh.m_SubMeshes),
                     "serialized_sha256": sha256(obj.get_raw_data())})
    return {"count": len(rows), "meshes": sorted(rows, key=lambda row: row["path_id"])}


def _unitypy():
    try:
        import UnityPy
    except ImportError as exc:
        raise ConversionError("UnityPy is required; use the Kick-Flight-Assets conversion environment") from exc
    return UnityPy


def _load_module(path: Path, name: str):
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise ConversionError(f"cannot load conversion helper {path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


def build_schema_registry(donor_dir: Path, repair_script: Path, octo_helper_path: Path) -> tuple[dict, set[int], dict]:
    UnityPy = _unitypy()
    repair = _load_module(repair_script, "kf_bundle_repair")
    octo = _load_module(octo_helper_path, "kf_octo_helpers")
    registry: dict[tuple, list[dict]] = {}
    allowed_formats: set[int] = set()
    parsed = 0
    skipped_nonbundles = 0
    skipped_unidentified_scripts = 0
    for path in sorted(donor_dir.glob("*.octo")):
        raw = path.read_bytes()
        try:
            repaired = repair.repair_bundle_bytes(raw).data
        except Exception as exc:
            # The iOS snapshot also contains non-Unity resource files; only skip
            # objects that do not carry the encrypted UnityFS signature.
            try:
                standard = octo.octo_to_unityfs(raw)
            except Exception:
                standard = b""
            if standard[:8] != b"UnityFS\0":
                skipped_nonbundles += 1
                continue
            raise ConversionError(f"iOS donor could not be parsed ({path.name}): {exc}") from exc
        env = UnityPy.load(repaired)
        serialized_files = {id(obj.assets_file): obj.assets_file for obj in env.objects}
        if not serialized_files:
            raise ConversionError(f"iOS schema donor {path.name} has no SerializedFiles")
        for obj in env.objects:
            if obj.type.name == "Texture2D":
                allowed_formats.add(int(obj.read().m_TextureFormat))
        for sf in serialized_files.values():
            if sf.unity_version != "2018.4.11f1" or int(sf.target_platform) != 9 or not sf._enable_type_tree:
                raise ConversionError(f"iOS schema donor metadata mismatch: {path.name}")
            file_objects = [obj for obj in env.objects if obj.assets_file is sf]
            scripts_by_path = {int(obj.path_id): obj.read() for obj in file_objects if obj.type.name == "MonoScript"}
            for typ in sf.types:
                script = None
                if typ.class_id == 114 and typ.script_id:
                    index = int(typ.script_type_index)
                    if index < 0 or index >= len(sf.script_types):
                        raise ConversionError(f"iOS donor {path.name} has invalid MonoBehaviour script index")
                    script_obj = scripts_by_path.get(int(sf.script_types[index].local_identifier_in_file))
                    if script_obj is None:
                        # Some donor SerializedFiles carry a script-table entry
                        # without the matching MonoScript object. Their native
                        # tree cannot establish managed identity, so it is not
                        # accepted as an iOS schema witness.
                        skipped_unidentified_scripts += 1
                        continue
                    script = (str(script_obj.m_Name), str(script_obj.m_Namespace), str(script_obj.m_AssemblyName))
                add_schema(registry, typ, script)
        parsed += 1
    if parsed == 0:
        raise ConversionError(f"no iOS Unity bundle donors found in {donor_dir}")
    report = {"ios_schema_bundles_parsed": parsed, "ios_nonbundle_files_skipped": skipped_nonbundles,
              "ios_unidentified_script_types_skipped": skipped_unidentified_scripts,
              "ios_schema_entries": sum(len(items) for items in registry.values()),
              "ios_verified_texture_formats": sorted(allowed_formats)}
    return registry, allowed_formats, report


def convert_model(source_bytes: bytes, expected_source_sha256: str, logical_name: str,
                  shader_bytes: bytes, ios_schema_registry: dict, ios_texture_formats: set[int],
                  repair_script: Path, octo_helper_path: Path) -> tuple[bytes, dict]:
    if not logical_name.startswith("player/") or not logical_name.endswith(".unity3d"):
        raise ConversionError("logical name must be a player/ .unity3d model route")
    source_sha = require_sha256(source_bytes, expected_source_sha256)
    UnityPy = _unitypy()
    repair = _load_module(repair_script, "kf_bundle_repair")
    octo = _load_module(octo_helper_path, "kf_octo_helpers")
    try:
        source_unityfs = repair.repair_bundle_bytes(source_bytes).data
        shader_unityfs = repair.repair_bundle_bytes(shader_bytes).data
        env = UnityPy.load(source_unityfs)
        shader_env = UnityPy.load(shader_unityfs)
    except Exception as exc:
        raise ConversionError(f"cannot parse source or iOS shader bundle: {exc}") from exc
    source_files = {id(obj.assets_file): obj.assets_file for obj in env.objects}
    if len(source_files) != 1:
        raise ConversionError(f"source must contain exactly one SerializedFile, found {len(source_files)}")
    sf = next(iter(source_files.values()))
    if int(sf.target_platform) != 13:
        raise ConversionError(f"source must be Android target 13, got {sf.target_platform}")
    if sf.unity_version != "2018.4.11f1" or not sf._enable_type_tree:
        raise ConversionError("source Unity version or TypeTree metadata is unsupported")
    source_types = sorted({obj.type.name for obj in env.objects})
    if "Mesh" not in source_types or "SkinnedMeshRenderer" not in source_types or "Material" not in source_types:
        raise ConversionError(f"source is not a supported player model bundle: {source_types}")
    schema_report = require_ios_schema(sf, ios_schema_registry)
    graph_before = require_reference_graph(sf, env.objects)
    if not graph_before["external_paths"]:
        raise ConversionError("model has no declared shader dependency")
    shader_report = require_shader_dependency(sf, env.objects, shader_env)
    textures_before = texture_snapshot(env.objects, ios_texture_formats)
    if textures_before["count"] == 0:
        raise ConversionError("model contains no textures")
    meshes_before = mesh_snapshot(env.objects)
    object_fingerprints_before = {
        int(obj.path_id): (obj.type.name, sha256(obj.get_raw_data())) for obj in env.objects
    }
    streamed_before = _streamed_node_hashes(octo, source_unityfs)

    # UnityPy rewrites the complete SerializedFile metadata and object directory;
    # this is not a byte edit of the target-platform integer in the source header.
    sf._m_target_platform = 9
    sf.target_platform = 9
    standard_output = env.file.save(packer="lz4")
    output = octo.unityfs_to_octo(standard_output)
    try:
        output_env = UnityPy.load(octo.octo_to_unityfs(output))
    except Exception as exc:
        raise ConversionError(f"converted Octo does not reparse: {exc}") from exc
    output_files = {id(obj.assets_file): obj.assets_file for obj in output_env.objects}
    if len(output_files) != 1:
        raise ConversionError(f"output contains {len(output_files)} SerializedFiles")
    output_sf = next(iter(output_files.values()))
    if int(output_sf.target_platform) != 9 or output_sf.unity_version != "2018.4.11f1":
        raise ConversionError("serialized output metadata is not Unity 2018.4 iOS target 9")
    if sorted((int(t.class_id), schema_key(t)[1], bytes(t.old_type_hash).hex()) for t in output_sf.types) != sorted(
            (int(t.class_id), schema_key(t)[1], bytes(t.old_type_hash).hex()) for t in sf.types):
        raise ConversionError("output SerializedType table changed during repack")
    object_fingerprints_after = {
        int(obj.path_id): (obj.type.name, sha256(obj.get_raw_data())) for obj in output_env.objects
    }
    if object_fingerprints_after != object_fingerprints_before:
        raise ConversionError("an object payload or pathID changed during iOS repack")
    graph_after = require_reference_graph(output_sf, output_env.objects)
    if graph_after != graph_before:
        raise ConversionError("internal/external PPtr graph changed during iOS repack")
    texture_after = texture_snapshot(output_env.objects, ios_texture_formats)
    if texture_after != textures_before:
        raise ConversionError("texture format, mip metadata, serialized bytes, or decoded RGBA pixels changed")
    mesh_after = mesh_snapshot(output_env.objects)
    if mesh_after != meshes_before:
        raise ConversionError("mesh bytes or geometry metadata changed during iOS repack")
    streamed_after = _streamed_node_hashes(octo, octo.octo_to_unityfs(output))
    if streamed_after != streamed_before:
        raise ConversionError("streamed resource bytes (including texture mip streams) changed")
    # Check dependency resolution against the produced iOS bundle once more.
    shader_report_after = require_shader_dependency(output_sf, output_env.objects, shader_env)
    report = {
        "logical_name": logical_name,
        "source_sha256": source_sha,
        "source_bytes": len(source_bytes),
        "output_sha256": sha256(output),
        "output_bytes": len(output),
        "source_target_platform": 13,
        "output_target_platform": int(output_sf.target_platform),
        "unity_version": output_sf.unity_version,
        "class_names": source_types,
        "schema": schema_report,
        "references": graph_after,
        "shader": shader_report_after,
        "textures": texture_after,
        "meshes": mesh_after,
        "streamed_files": streamed_after,
        "object_count": len(object_fingerprints_after),
    }
    return output, report


def _streamed_node_hashes(octo: Any, unityfs: bytes) -> dict:
    try:
        _, _, nodes, raw = octo.parse_unityfs(unityfs)
    except Exception as exc:
        raise ConversionError(f"cannot inspect UnityFS directory: {exc}") from exc
    result = {}
    for node in nodes:
        name = str(node[3])
        if name.endswith(".resS"):
            data = raw[node[0]:node[0] + node[1]]
            result[name] = {"bytes": len(data), "sha256": sha256(data)}
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True, help="verified Android Octo model bundle")
    parser.add_argument("--source-sha256", required=True, help="expected SHA-256 from the Android catalog")
    parser.add_argument("--logical-name", required=True)
    parser.add_argument("--ios-donor-dir", type=Path, required=True, help="directory of verified iOS .octo bundles")
    parser.add_argument("--ios-shader", type=Path, required=True, help="iOS originalshader.unity3d Octo payload")
    parser.add_argument("--repair-script", type=Path, required=True,
                        help="Kick-Flight-Assets/reconstruct_unity_bundles.py")
    parser.add_argument("--output", type=Path, required=True, help="private output path; existing files are refused")
    parser.add_argument("--report", type=Path, help="optional JSON report path")
    args = parser.parse_args()
    helper = Path(__file__).with_name("build-action-asset-bundles.py")
    try:
        registry, texture_formats, donors_report = build_schema_registry(args.ios_donor_dir, args.repair_script, helper)
        output, report = convert_model(args.source.read_bytes(), args.source_sha256, args.logical_name,
                                       args.ios_shader.read_bytes(), registry, texture_formats,
                                       args.repair_script, helper)
        if args.output.exists():
            raise ConversionError(f"refusing to overwrite output: {args.output}")
        args.output.parent.mkdir(parents=True, exist_ok=True)
        temp = args.output.with_name(args.output.name + ".tmp")
        temp.write_bytes(output)
        temp.replace(args.output)
        report["ios_donors"] = donors_report
        report["output_path"] = str(args.output)
        if args.report:
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(json.dumps(report, indent=2, sort_keys=True), encoding="utf-8")
        print(json.dumps(report, indent=2, sort_keys=True))
        return 0
    except (ConversionError, OSError, ValueError) as exc:
        print(f"model conversion refused: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
