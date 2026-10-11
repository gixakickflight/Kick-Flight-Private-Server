#!/usr/bin/env python3
"""Build physically independent, same-kicker/prop weapon fallbacks for missing costumes.

Unity refuses to load two bundles whose serialized CAB names collide, even when Octo presents them under
different logical names. This generator clones each missing costume route, gives its serialized file and
AssetBundle container key the requested identity, and updates self-references to the copied .resS stream.
Original captured bundles are only read. Generated bundles live under .local/weapon-costume-fallbacks/.

Run with the UnityPy 1.25.3 environment:
    .local/assets-venv/bin/python scripts/add_weapon_costume_aliases.py
    .local/assets-venv/bin/python scripts/add_weapon_costume_aliases.py --check
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.machinery
import importlib.util
import json
import re
import string
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parent.parent
TITLE_PATH = ROOT / "config/resources/title-minimum.json"
COSTUME_PATH = ROOT / "config/masters_kicker_costume.json"
PARAMETER_PATH = ROOT / "config/masters_kicker_parameter.json"
OUTPUT_DIR = ROOT / ".local/weapon-costume-fallbacks"
FALLBACK_ID_PREFIX = "weapon-costume-fallback-"
WEAPON_PATH = re.compile(r"weapon/wp_(\d{3})/wp_(\d{3})_(\d{3})_(\d{3})\.unity3d\Z")
ALPHABET = string.ascii_letters + string.digits


def _load_bundle_codec():
    path = ROOT / "scripts/build-action-asset-bundles.py"
    loader = importlib.machinery.SourceFileLoader("kickflight_action_bundle_codec", str(path))
    spec = importlib.util.spec_from_loader(loader.name, loader)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"cannot load UnityFS codec from {path}")
    module = importlib.util.module_from_spec(spec)
    loader.exec_module(module)
    return module


def _load_unitypy():
    try:
        import UnityPy
    except ImportError as error:
        raise SystemExit("UnityPy 1.25.3 is required; run with .local/assets-venv/bin/python") from error
    version = getattr(UnityPy, "__version__", "")
    if version != "1.25.3":
        raise SystemExit(f"expected UnityPy 1.25.3, found {version or 'unknown'}")
    return UnityPy


def _attach_prop(weapon_type: int, prop_id: int) -> bool:
    """Mirror DemoSessionApi's WeaponMaster attachment-row selection."""
    if prop_id in (1, 2):
        return True
    if prop_id == 201 and weapon_type in (4, 10, 11):
        return True
    if prop_id == 101 and weapon_type in (4, 13):
        return True
    return False


def _weapon_parts(name: str) -> tuple[int, int, int] | None:
    match = WEAPON_PATH.fullmatch(name)
    return tuple(map(int, match.groups())) if match else None


def build_fallback_specs(document: dict[str, Any]) -> list[tuple[str, str, dict[str, Any]]]:
    """Return (requested logical name, canonical donor name, donor title entry) for every missing route."""
    source_entries: dict[str, dict[str, Any]] = {}
    source_names: set[str] = set()
    paths_by_kicker: dict[int, set[tuple[int, int]]] = {}
    for entry in document["entries"]:
        if entry.get("id", "").startswith(FALLBACK_ID_PREFIX):
            continue
        names = [name for name in entry.get("names", []) if _weapon_parts(name)]
        if not names:
            continue
        # The first name is the captured bundle's authentic route; following names were old Octo aliases.
        canonical = names[0]
        parts = _weapon_parts(canonical)
        assert parts is not None
        kicker_id, _weapon_prefix, model_id, prop_id = parts
        source_entries[canonical] = entry
        source_names.add(canonical)
        paths_by_kicker.setdefault(kicker_id, set()).add((model_id, prop_id))

    parameters = json.loads(PARAMETER_PATH.read_text(encoding="utf-8"))
    weapon_types = {row["kickerId"]: row["weaponType"] for row in parameters}
    costumes = json.loads(COSTUME_PATH.read_text(encoding="utf-8"))
    specs: list[tuple[str, str, dict[str, Any]]] = []
    requested_seen: set[str] = set()

    for costume in costumes:
        kicker_id = costume["kickerId"]
        props = sorted(
            prop_id
            for model_id, prop_id in paths_by_kicker.get(kicker_id, set())
            if model_id == 1 and _attach_prop(weapon_types[kicker_id], prop_id)
        )
        for prop_id in props:
            for model_id, is_high in (
                (costume["costumeId"], False),
                (costume["costumeId"] + 100, True),
            ):
                requested = f"weapon/wp_{kicker_id:03}/wp_{kicker_id:03}_{model_id:03}_{prop_id:03}.unity3d"
                if requested in source_names or requested in requested_seen:
                    continue
                preferred_model = 101 if is_high else 1
                donor = f"weapon/wp_{kicker_id:03}/wp_{kicker_id:03}_{preferred_model:03}_{prop_id:03}.unity3d"
                if donor not in source_names and is_high:
                    donor = f"weapon/wp_{kicker_id:03}/wp_{kicker_id:03}_001_{prop_id:03}.unity3d"
                if donor not in source_entries:
                    raise ValueError(f"no captured bundle for {requested}; tried {donor}")
                requested_parts = _weapon_parts(requested)
                donor_parts = _weapon_parts(donor)
                assert requested_parts and donor_parts
                if requested_parts[0] != donor_parts[0] or requested_parts[3] != donor_parts[3]:
                    raise ValueError(f"fallback changes kicker or prop: {requested} -> {donor}")
                specs.append((requested, donor, source_entries[donor]))
                requested_seen.add(requested)
    return sorted(specs, key=lambda item: item[0])


def expected_weapon_routes(document: dict[str, Any]) -> set[str]:
    """Enumerate the low/high model paths the client can request for attached props."""
    source_entries = [
        entry for entry in document["entries"]
        if not entry.get("id", "").startswith(FALLBACK_ID_PREFIX)
    ]
    source_names = {
        names[0]
        for entry in source_entries
        if (names := [name for name in entry.get("names", []) if _weapon_parts(name)])
    }
    parameters = json.loads(PARAMETER_PATH.read_text(encoding="utf-8"))
    weapon_types = {row["kickerId"]: row["weaponType"] for row in parameters}
    costumes = json.loads(COSTUME_PATH.read_text(encoding="utf-8"))
    routes: set[str] = set()
    for costume in costumes:
        kicker_id = costume["kickerId"]
        props = {
            parts[3]
            for name in source_names
            if (parts := _weapon_parts(name))
            and parts[0] == kicker_id and parts[2] == 1
            and _attach_prop(weapon_types[kicker_id], parts[3])
        }
        for prop_id in props:
            for model_id in (costume["costumeId"], costume["costumeId"] + 100):
                routes.add(f"weapon/wp_{kicker_id:03}/wp_{kicker_id:03}_{model_id:03}_{prop_id:03}.unity3d")
    return routes


def _base62_digest(value: str, occupied: set[str]) -> str:
    modulus = len(ALPHABET) ** 5
    candidate = int.from_bytes(hashlib.sha256(value.encode("utf-8")).digest()[:8], "big") % modulus
    while True:
        n = candidate
        digits = []
        for _ in range(5):
            digits.append(ALPHABET[n % len(ALPHABET)])
            n //= len(ALPHABET)
        name = "w" + "".join(reversed(digits))
        if name not in occupied:
            occupied.add(name)
            return name
        candidate = (candidate + 1) % modulus


def _fallback_id(logical_name: str) -> str:
    kicker_id, _prefix, model_id, prop_id = _weapon_parts(logical_name)  # type: ignore[misc]
    return f"{FALLBACK_ID_PREFIX}k{kicker_id:03}-m{model_id:03}-p{prop_id:03}"


def clone_weapon_bundle(source_oct: bytes, source_name: str, logical_name: str) -> tuple[bytes, dict[str, Any]]:
    """Clone one Octo weapon bundle and return (Octo bytes, structural report)."""
    UnityPy = _load_unitypy()
    codec = _load_bundle_codec()
    source_parts, logical_parts = _weapon_parts(source_name), _weapon_parts(logical_name)
    if not source_parts or not logical_parts or source_parts[0] != logical_parts[0] or source_parts[3] != logical_parts[3]:
        raise ValueError(f"source and destination must keep kicker/prop: {source_name} -> {logical_name}")

    unityfs = codec.octo_to_unityfs(source_oct)
    (header_prefix, info_hash), _blocks, nodes, raw = codec.parse_unityfs(unityfs)
    serialized_nodes = [node for node in nodes if node[2] & 4]
    if len(serialized_nodes) != 1:
        raise ValueError(f"{source_name}: expected one serialized file, found {len(serialized_nodes)}")
    old_cab_node = serialized_nodes[0]
    old_cab = old_cab_node[3]
    old_cab_data = raw[old_cab_node[0]:old_cab_node[0] + old_cab_node[1]]
    res_nodes = [node for node in nodes if node[3] == old_cab + ".resS"]
    if len(res_nodes) != 1:
        raise ValueError(f"{source_name}: expected one matching {old_cab}.resS, found {len(res_nodes)}")
    res_node = res_nodes[0]
    res_data = raw[res_node[0]:res_node[0] + res_node[1]]

    unique_seed = f"kickflight weapon costume fallback\0{logical_name}"
    new_cab = "CAB-" + hashlib.md5(unique_seed.encode("utf-8")).hexdigest()
    target_bundle_name = logical_name
    source_stem = Path(source_name).stem
    target_stem = Path(logical_name).stem

    env = UnityPy.load(unityfs)
    asset_bundle_objects = [obj for obj in env.objects if obj.type.name == "AssetBundle"]
    if len(asset_bundle_objects) != 1:
        raise ValueError(f"{source_name}: expected one AssetBundle object, found {len(asset_bundle_objects)}")
    asset_bundle_obj = asset_bundle_objects[0]
    serialized_file = asset_bundle_obj.assets_file
    bundle = asset_bundle_obj.read()
    if bundle.m_Name != source_name:
        raise ValueError(f"{source_name}: AssetBundle.m_Name is {bundle.m_Name!r}")
    if len(bundle.m_Container) != 1:
        raise ValueError(f"{source_name}: expected one prefab container entry, found {len(bundle.m_Container)}")
    old_key, asset_info = bundle.m_Container[0]
    if source_stem not in old_key:
        raise ValueError(f"{source_name}: container key {old_key!r} does not contain its source stem")
    new_key = old_key.replace(source_stem, target_stem)
    if new_key == old_key:
        raise ValueError(f"{source_name}: container key did not change for {logical_name}")
    bundle.m_Name = target_bundle_name
    bundle.m_Container = [(new_key, asset_info)]
    asset_bundle_obj.save_typetree(bundle)

    stream_records: list[tuple[str, int, int, int]] = []
    for obj in env.objects:
        if obj.type.name not in ("Texture2D", "Mesh"):
            continue
        value = obj.read()
        stream = getattr(value, "m_StreamData", None)
        if stream is None or not stream.path:
            continue
        if stream.offset + stream.size > len(res_data):
            raise ValueError(f"{source_name}: {obj.type.name} stream extends beyond {old_cab}.resS")
        self_stream = f"archive:/{old_cab}/{old_cab}.resS"
        if stream.path == self_stream:
            stream_records.append((obj.type.name, obj.path_id, stream.offset, stream.size))
            stream.path = f"archive:/{new_cab}/{new_cab}.resS"
            obj.save_typetree(value)
        elif old_cab in stream.path:
            raise ValueError(f"{source_name}: unrecognized self-stream reference {stream.path!r}")

    # An external path may refer to a genuinely shared archive. Rebase only entries that identify this CAB itself.
    for external in serialized_file.externals:
        if old_cab in external.path:
            external.path = external.path.replace(old_cab, new_cab)

    serialized_bytes = serialized_file.save()
    output_paths: list[str] = []
    output_data: list[bytes] = []
    for node in nodes:
        path = node[3]
        data = raw[node[0]:node[0] + node[1]]
        if path == old_cab:
            path, data = new_cab, serialized_bytes
        elif path == old_cab + ".resS":
            path = new_cab + ".resS"
        output_paths.append(path)
        output_data.append(data)
    if len(serialized_bytes) != len(old_cab_data):
        # UnityPy can normalize serialized-file padding while keeping all stream offsets valid.
        for obj in env.objects:
            if obj.type.name not in ("Texture2D", "Mesh"):
                continue
            stream = getattr(obj.read(), "m_StreamData", None)
            if stream and stream.path == f"archive:/{new_cab}/{new_cab}.resS" and stream.offset + stream.size > len(res_data):
                raise ValueError(f"{logical_name}: updated .resS offset is outside the copied stream")
    output = codec.unityfs_to_octo(codec.build_unityfs(header_prefix, info_hash, output_paths, output_data))
    report = {
        "cab": new_cab,
        "container_key": new_key,
        "source_cab": old_cab,
        "resS_size": len(res_data),
        "stream_records": stream_records,
        "source_serialized_size": len(old_cab_data),
        "serialized_size": len(serialized_bytes),
        "external_paths": [item.path for item in serialized_file.externals],
        "source_md5": hashlib.md5(source_oct).hexdigest(),
    }
    verify_weapon_bundle(output, logical_name, report)
    return output, report


def verify_weapon_bundle(octo: bytes, logical_name: str, report: dict[str, Any] | None = None) -> dict[str, Any]:
    UnityPy = _load_unitypy()
    codec = _load_bundle_codec()
    unityfs = codec.octo_to_unityfs(octo)
    (_, _), _blocks, nodes, raw = codec.parse_unityfs(unityfs)
    serialized_nodes = [node for node in nodes if node[2] & 4]
    if len(serialized_nodes) != 1:
        raise ValueError(f"{logical_name}: output has {len(serialized_nodes)} serialized files")
    cab_node = serialized_nodes[0]
    cab = cab_node[3]
    res_nodes = [node for node in nodes if node[3] == cab + ".resS"]
    if len(res_nodes) != 1:
        raise ValueError(f"{logical_name}: output does not have one matching .resS node")
    res_node = res_nodes[0]
    res_data = raw[res_node[0]:res_node[0] + res_node[1]]
    env = UnityPy.load(unityfs)
    asset_bundle_objects = [obj for obj in env.objects if obj.type.name == "AssetBundle"]
    if len(asset_bundle_objects) != 1:
        raise ValueError(f"{logical_name}: output has {len(asset_bundle_objects)} AssetBundle objects")
    asset_bundle_obj = asset_bundle_objects[0]
    sf = asset_bundle_obj.assets_file
    bundle = asset_bundle_obj.read()
    if bundle.m_Name != logical_name or len(bundle.m_Container) != 1:
        raise ValueError(f"{logical_name}: AssetBundle name/container mismatch")
    key, info = bundle.m_Container[0]
    target_stem = Path(logical_name).stem
    if target_stem not in key:
        raise ValueError(f"{logical_name}: container key {key!r} does not contain the requested stem")
    root_id = info.asset.m_PathID
    root = sf.objects.get(root_id)
    if root is None or root.type.name != "GameObject" or info.asset.m_FileID != 0:
        raise ValueError(f"{logical_name}: container root PPtr does not resolve to a local GameObject")

    local_pointers = 0
    external_pointers = 0
    for pointer in bundle.m_PreloadTable:
        if pointer.m_FileID == 0:
            local_pointers += 1
            if pointer.m_PathID not in sf.objects:
                raise ValueError(f"{logical_name}: unresolved local preload PPtr {pointer.m_PathID}")
        else:
            external_pointers += 1
            if not 0 < pointer.m_FileID <= len(sf.externals):
                raise ValueError(f"{logical_name}: invalid external preload PPtr fileID {pointer.m_FileID}")

    stream_count = 0
    animator_refs: list[tuple[str, int, int]] = []
    for obj in env.objects:
        if obj.type.name == "Animator":
            animator = obj.read()
            for field_name in ("m_Controller", "m_Avatar"):
                pointer = getattr(animator, field_name, None)
                if pointer is None:
                    continue
                file_id, path_id = pointer.m_FileID, pointer.m_PathID
                if file_id == 0 and path_id and path_id not in sf.objects:
                    raise ValueError(f"{logical_name}: unresolved Animator.{field_name} PPtr {path_id}")
                if file_id and not 0 < file_id <= len(sf.externals):
                    raise ValueError(f"{logical_name}: invalid Animator.{field_name} external PPtr {file_id}")
                animator_refs.append((field_name, file_id, path_id))
        if obj.type.name not in ("Texture2D", "Mesh"):
            continue
        stream = getattr(obj.read(), "m_StreamData", None)
        if not stream or not stream.path:
            continue
        if stream.offset + stream.size > len(res_data):
            raise ValueError(f"{logical_name}: {obj.type.name} stream exceeds output .resS")
        if cab in stream.path and stream.path != f"archive:/{cab}/{cab}.resS":
            raise ValueError(f"{logical_name}: self stream points to unexpected CAB path {stream.path!r}")
        if stream.path == f"archive:/{cab}/{cab}.resS":
            stream_count += 1
    for external in sf.externals:
        if cab in external.path and not external.path.startswith(f"archive:/{cab}/{cab}"):
            raise ValueError(f"{logical_name}: malformed self external path {external.path!r}")
    result = {
        "cab": cab,
        "container_key": key,
        "root_name": root.read().m_Name,
        "root_type": root.type.name,
        "object_count": len(env.objects),
        "animator_refs": animator_refs,
        "local_preload_pointers": local_pointers,
        "external_preload_pointers": external_pointers,
        "external_paths": [external.path for external in sf.externals],
        "self_stream_count": stream_count,
        "resS_size": len(res_data),
        "md5": hashlib.md5(octo).hexdigest(),
    }
    if report is not None:
        if report["cab"] != cab or report["container_key"] != key:
            raise ValueError(f"{logical_name}: output identity differs from build report")
        if report["external_paths"] != result["external_paths"]:
            raise ValueError(f"{logical_name}: external dependency paths changed during round-trip")
        if report["resS_size"] != len(res_data) or report["source_md5"] == result["md5"]:
            raise ValueError(f"{logical_name}: output stream length or physical identity is invalid")
        if stream_count != len(report["stream_records"]):
            raise ValueError(f"{logical_name}: unexpected self-stream reference count")
    return result


def _create_entries(document: dict[str, Any], write: bool) -> tuple[list[dict[str, Any]], list[tuple[str, str]]]:
    specs = build_fallback_specs(document)
    if len(specs) != 35:
        raise ValueError(f"expected 35 fallback routes, found {len(specs)}")
    # Other generators (kicker thumbnails, per-language localize bundles) append entries after this one. Keep
    # every already-materialized fallback's Octo id/objectName so a rebuild does not renumber it above those
    # neighbours and churn the database on every run. New routes still get fresh ids from the current maximum.
    existing_generated = {
        entry["names"][0]: entry
        for entry in document["entries"]
        if entry.get("id", "").startswith(FALLBACK_ID_PREFIX) and len(entry.get("names", [])) == 1
    }
    old_entries = [entry for entry in document["entries"] if not entry.get("id", "").startswith(FALLBACK_ID_PREFIX)]
    # Remove the old shared Octo aliases while preserving each authentic route at the head of its source entry.
    for entry in old_entries:
        names = entry.get("names", [])
        weapon_names = [name for name in names if _weapon_parts(name)]
        if weapon_names:
            canonical = weapon_names[0]
            entry["names"] = [name for name in names if not _weapon_parts(name) or name == canonical]

    used_ids = {entry["id"] for entry in old_entries}
    used_objects = {entry["objectName"] for entry in old_entries}
    used_octos = {entry.get("octoId") for entry in old_entries}
    next_octo = max((value for value in used_octos if isinstance(value, int)), default=0) + 1
    generated: list[dict[str, Any]] = []
    files_written: list[tuple[str, str]] = []
    if write:
        OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    for index, (logical_name, donor_name, donor_entry) in enumerate(specs):
        entry_id = _fallback_id(logical_name)
        if entry_id in used_ids:
            raise ValueError(f"duplicate generated resource id {entry_id}")
        prior = existing_generated.get(logical_name)
        if prior is not None:
            object_name = prior["objectName"]
            octo_id = int(prior["octoId"])
            if object_name in used_objects or octo_id in used_octos:
                raise ValueError(f"{logical_name}: existing fallback identity collides with the catalog")
        else:
            object_name = _base62_digest(logical_name, used_objects)
            while next_octo in used_octos:
                next_octo += 1
            octo_id = next_octo
            next_octo += 1
        used_objects.add(object_name)
        used_octos.add(octo_id)

        donor_path = (ROOT / donor_entry["sourcePath"]).resolve()
        if not donor_path.is_file():
            raise FileNotFoundError(f"missing donor bundle for {logical_name}: {donor_path}")
        donor_bytes = donor_path.read_bytes()
        cloned, report = clone_weapon_bundle(donor_bytes, donor_name, logical_name)
        if report["source_md5"] == hashlib.md5(cloned).hexdigest():
            raise ValueError(f"{logical_name}: clone byte-for-byte matches source")
        file_name = f"{('A' + object_name).encode('ascii').hex().upper()}_{hashlib.md5(cloned).hexdigest()}.bundle"
        relative_path = Path(".local/weapon-costume-fallbacks") / file_name
        output_path = ROOT / relative_path
        if write:
            if not output_path.is_file() or output_path.read_bytes() != cloned:
                output_path.write_bytes(cloned)
        generated.append({
            "id": entry_id,
            "kind": "assetBundle",
            "octoId": octo_id,
            "names": [logical_name],
            "objectName": object_name,
            "sourcePath": relative_path.as_posix(),
            "logicalName": f"Unity AssetBundle {logical_name}",
            "description": f"Physically independent same-kicker, same-prop costume fallback cloned from {donor_name}; "
                           f"CAB and container key are unique; built by scripts/add_weapon_costume_aliases.py",
        })
        files_written.append((logical_name, hashlib.md5(cloned).hexdigest()))
        used_ids.add(entry_id)

    document["entries"] = old_entries + generated
    # This generator predates later resource additions. Keep its minimum release
    # floor without downgrading a newer title catalog revision (skin thumbnails
    # and other resources may already have advanced it).
    target_revision = max(int(document.get("revision", 0)), 31)
    document["revision"] = target_revision
    document["fromRevisions"] = sorted(set(document.get("fromRevisions", [])) | set(range(target_revision + 1)))
    return generated, files_written


def check_definition(document: dict[str, Any]) -> tuple[int, int]:
    specs = build_fallback_specs(document)
    by_name = {
        name: entry
        for entry in document["entries"]
        for name in entry.get("names", [])
        if _weapon_parts(name)
    }
    generated = [entry for entry in document["entries"] if entry.get("id", "").startswith(FALLBACK_ID_PREFIX)]
    generated_by_name = {entry["names"][0]: entry for entry in generated if len(entry.get("names", [])) == 1}
    if len(specs) != 35 or len(generated) != 35 or len(generated_by_name) != 35:
        raise ValueError(f"expected 35 generated fallbacks; specs={len(specs)}, entries={len(generated)}")
    expected_names = {requested for requested, _donor, _entry in specs}
    if expected_names != set(generated_by_name):
        raise ValueError("generated fallback route coverage differs from the 35 expected missing paths")
    expected_routes = expected_weapon_routes(document)
    if len(expected_routes) != 314:
        raise ValueError(f"expected 314 client weapon routes, found {len(expected_routes)}")
    if not expected_routes.issubset(by_name):
        raise ValueError(f"title definition is missing weapon routes: {sorted(expected_routes - set(by_name))[:5]}")
    revision = int(document.get("revision", 0))
    if revision < 31 or document.get("fromRevisions") != list(range(revision + 1)):
        raise ValueError(f"title minimum must be revision >=31 with fromRevisions 0 through revision ({revision})")
    all_bundle_names = [name for entry in document["entries"] for name in entry.get("names", [])]
    ids = [entry["id"] for entry in document["entries"]]
    objects = [entry["objectName"] for entry in document["entries"]]
    octos = [entry.get("octoId") for entry in document["entries"]]
    if len(set(all_bundle_names)) != len(all_bundle_names) or len(set(ids)) != len(ids):
        raise ValueError("resource names and IDs must be unique")
    if len(set(objects)) != len(objects) or len(set(octos)) != len(octos):
        raise ValueError("objectName and octoId values must be unique")
    clone_paths: set[str] = set()
    clone_cabs: set[str] = set()
    for requested, donor, _donor_entry in specs:
        entry = generated_by_name[requested]
        if entry["sourcePath"] in clone_paths:
            raise ValueError(f"physical clone path is shared: {entry['sourcePath']}")
        clone_paths.add(entry["sourcePath"])
        source = by_name.get(donor)
        if source is None:
            raise ValueError(f"canonical donor route missing: {donor}")
        if source["sourcePath"] == entry["sourcePath"]:
            raise ValueError(f"fallback still points to donor bytes: {requested}")
        source_path = ROOT / entry["sourcePath"]
        if not source_path.is_file():
            raise FileNotFoundError(source_path)
        result = verify_weapon_bundle(source_path.read_bytes(), requested)
        if result["cab"] in clone_cabs:
            raise ValueError(f"duplicate cloned CAB {result['cab']}")
        clone_cabs.add(result["cab"])
        donor_entry = next(entry for candidate, entry in (
            (candidate, entry)
            for entry in document["entries"]
            if not entry.get("id", "").startswith(FALLBACK_ID_PREFIX)
            for candidate in entry.get("names", [])
        ) if candidate == donor)
        donor_source = (ROOT / donor_entry["sourcePath"]).resolve().read_bytes()
        codec = _load_bundle_codec()
        donor_nodes = codec.parse_unityfs(codec.octo_to_unityfs(donor_source))[2]
        donor_cab = next(node[3] for node in donor_nodes if node[2] & 4)
        if donor_cab == result["cab"]:
            raise ValueError(f"{requested}: cloned CAB still matches donor {donor_cab}")
        request_parts, donor_parts = _weapon_parts(requested), _weapon_parts(donor)
        assert request_parts and donor_parts
        if request_parts[0] != donor_parts[0] or request_parts[3] != donor_parts[3]:
            raise ValueError(f"fallback changes kicker or prop: {requested} -> {donor}")
    return len(expected_routes), len(generated)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--check", action="store_true", help="verify generated bundles and title definition")
    mode.add_argument("--dry-run", action="store_true", help="build/verify bundles without writing files")
    args = parser.parse_args()
    document = json.loads(TITLE_PATH.read_text(encoding="utf-8"))
    if args.check:
        routes, entries = check_definition(document)
        print(f"physical weapon fallback check passed: {entries} bundles, {routes} routes, revision {document['revision']}")
        return 0

    generated, files = _create_entries(document, write=not args.dry_run)
    # Always validate the fully materialized document; --dry-run validates generated bytes in memory through clone.
    if args.dry_run:
        print(f"dry run passed: {len(generated)} distinct weapon fallback bundles, revision {document['revision']}")
        for name, digest in files:
            print(f"{name} md5={digest}")
        return 0
    routes, entries = check_definition(document)
    text = json.dumps(document, indent=2, ensure_ascii=False) + "\n"
    encoded = text.replace("\n", "\r\n").encode("utf-8")
    if TITLE_PATH.read_bytes() != encoded:
        TITLE_PATH.write_bytes(encoded)
    print(f"generated and verified {entries} physical weapon fallback bundles for {routes} routes")
    print(f"updated {TITLE_PATH.relative_to(ROOT).as_posix()} to revision {document['revision']}; run scripts/build-title-resource-catalog.py")
    return 0


if __name__ == "__main__":
    sys.exit(main())
