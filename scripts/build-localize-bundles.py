#!/usr/bin/env python3
"""Build physically independent per-language localize AssetBundles.

Every entry in ``config/resources/title-minimum.json`` whose names contain ``/localize/`` is a single Chinese
(``zh``) capture that used to be advertised under four logical names (``zh``/``es``/``en``/``ja``) in
the same Octo row. Unity keys loaded bundles by their serialized CAB name, so two of those aliases could never
be resident at once; on a Portuguese device the client asked for ``effect/localize/pt/ui/ef_ui_008`` and the
load failed silently into a native NRE.

This generator clones each capture once per language the client can map from its saved language id
(``LanguageTypeExtensions.GetLanguageCode`` -> en, es, ja, pt, ko, fr, de) and gives the copy:

* a unique serialized CAB name (``CAB-<md5>``),
* the language logical path as ``AssetBundle.m_Name`` and ``m_AssetBundleName``,
* container keys rewritten from ``/localize/zh/`` to ``/localize/<lang>/``,
* self references to the copied ``.resS`` stream (the cloned CAB's own archive).

The original capture stays untouched and is still listed under its ``zh`` primary name; ``ui/localize/zh/*``
rows remain excluded from the served database by ``build-title-resource-catalog.is_removed``. Output bundles
live under ``.local/localize-bundles/`` (gitignored, exactly like the weapon-costume fallbacks).

Content is still the Chinese imagery. A per-language replacement capture can be dropped later under
``.local/localize-overrides/<lang>/<zh logical name>`` (structurally identical to the Chinese donor, only the
texture changed); the generator will rebrand it like any other donor. Texture editing is intentionally not
implemented here.

Run with the UnityPy 1.25.3 environment (no WSL interpreter has UnityPy):

    powershell.exe -NoProfile -Command "cd C:\\Users\\Tanuki\\Kick-Flight\\kf-language-bundles; python scripts/build-localize-bundles.py"
    ... python scripts/build-localize-bundles.py --check
    ... python scripts/build-localize-bundles.py --dry-run
"""

from __future__ import annotations

import argparse
import collections
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
OUTPUT_DIR = ROOT / ".local/localize-bundles"
# Optional future hook: a replacement capture for one language. The file must be the Chinese donor bundle with
# only its texture changed (same CAB/container/m_Name conventions); it is rebranded exactly like the donor.
OVERRIDE_DIR = ROOT / ".local/localize-overrides"
ENTRY_ID_PREFIX = "localize-"
SOURCE_LANG = "zh"
# The single source of truth for every language code the client can produce. Keep this list complete: a missing
# code makes the client's bundle lookup fail silently and crash later (live NRE on pt/effect/localize/pt/...).
LANG_CODES = ["en", "es", "ja", "pt", "ko", "fr", "de"]
LOCALIZE_TOKEN = re.compile(r"(?P<prefix>/localize/)(?P<lang>[a-z]{2})(?=/|\Z)")
ALPHABET = string.ascii_letters + string.digits


def _load_bundle_codec():
    path = ROOT / "scripts/build-action-asset-bundles.py"
    loader = importlib.machinery.SourceFileLoader("kickflight_localize_bundle_codec", str(path))
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
        raise SystemExit("UnityPy 1.25.3 is required; run with the Windows interpreter that has it") from error
    version = getattr(UnityPy, "__version__", "")
    if version != "1.25.3":
        raise SystemExit(f"expected UnityPy 1.25.3, found {version or 'unknown'}")
    return UnityPy


def is_localize_name(name: str) -> bool:
    return "/localize/" in name


def is_localize_entry(entry: dict[str, Any]) -> bool:
    return any(is_localize_name(name) for name in entry.get("names", []))


def _replace_language(name: str, lang: str) -> str:
    return LOCALIZE_TOKEN.sub(lambda match: match.group("prefix") + lang, name, count=1)


def _language_of(name: str) -> str | None:
    match = LOCALIZE_TOKEN.search(name)
    return match.group("lang") if match else None


def source_entries(document: dict[str, Any]) -> list[dict[str, Any]]:
    """Localize captures, excluding this generator's own per-language entries."""
    return [
        entry
        for entry in document["entries"]
        if not entry.get("id", "").startswith(ENTRY_ID_PREFIX) and is_localize_entry(entry)
    ]


def build_clone_specs(document: dict[str, Any]) -> list[dict[str, Any]]:
    """Return one spec per (capture, language): the zh name, its logical language name and donor entry."""
    specs: list[dict[str, Any]] = []
    for entry in source_entries(document):
        names = entry.get("names", [])
        if not names:
            raise ValueError(f"{entry.get('id')}: localize entry has no names")
        # One capture (title_logo) lists en first, but the physical bundle and its container keys are zh. The
        # authentic route is the zh name wherever it sits in the list; it stays as the entry's only name.
        zh_names = [name for name in names if _language_of(name) == SOURCE_LANG]
        if len(zh_names) != 1:
            raise ValueError(f"{entry.get('id')}: expected one {SOURCE_LANG} name, found {names!r}")
        primary = zh_names[0]
        for lang in LANG_CODES:
            logical_name = _replace_language(primary, lang)
            if logical_name == primary:
                raise ValueError(f"{entry.get('id')}: language {lang} did not change {primary!r}")
            specs.append({
                "entry_id": entry["id"],
                "source_name": primary,
                "source_path": entry["sourcePath"],
                "lang": lang,
                "logical_name": logical_name,
            })
    specs.sort(key=lambda spec: spec["logical_name"])
    return specs


def _base62_digest(value: str, occupied: set[str]) -> str:
    """Deterministic six-character Octo objectName, mirroring the weapon-fallback generator."""
    modulus = len(ALPHABET) ** 5
    candidate = int.from_bytes(hashlib.sha256(value.encode("utf-8")).digest()[:8], "big") % modulus
    while True:
        n = candidate
        digits = []
        for _ in range(5):
            digits.append(ALPHABET[n % len(ALPHABET)])
            n //= len(ALPHABET)
        name = "l" + "".join(reversed(digits))
        if name not in occupied:
            occupied.add(name)
            return name
        candidate = (candidate + 1) % modulus


def _generated_id(source_id: str, lang: str) -> str:
    return f"{ENTRY_ID_PREFIX}{lang}-{source_id}"


def _override_path(lang: str, source_name: str) -> Path:
    return OVERRIDE_DIR / lang / source_name


def _load_source_info(source_oct: bytes) -> dict[str, Any]:
    """Load the donor once and record the shape every clone must preserve."""
    UnityPy = _load_unitypy()
    codec = _load_bundle_codec()
    unityfs = codec.octo_to_unityfs(source_oct)
    _header, _blocks, nodes, _raw = codec.parse_unityfs(unityfs)
    serialized_nodes = [node for node in nodes if node[2] & 4]
    if len(serialized_nodes) != 1:
        raise ValueError(f"donor: expected one serialized file, found {len(serialized_nodes)}")
    env = UnityPy.load(unityfs)
    return {
        "cab": serialized_nodes[0][3],
        "object_count": len(env.objects),
        "type_counts": dict(collections.Counter(obj.type.name for obj in env.objects)),
    }


def clone_localize_bundle(
    source_oct: bytes,
    source_name: str,
    logical_name: str,
    lang: str,
    source_info: dict[str, Any],
) -> tuple[bytes, dict[str, Any]]:
    """Clone one Chinese localize bundle and return (Octo bytes, structural report)."""
    UnityPy = _load_unitypy()
    codec = _load_bundle_codec()
    if _language_of(source_name) != SOURCE_LANG:
        raise ValueError(f"{source_name}: donor must be a {SOURCE_LANG} bundle")
    if _language_of(logical_name) != lang:
        raise ValueError(f"{logical_name}: expected language {lang}")

    unityfs = codec.octo_to_unityfs(source_oct)
    (header_prefix, info_hash), _blocks, nodes, raw = codec.parse_unityfs(unityfs)
    serialized_nodes = [node for node in nodes if node[2] & 4]
    if len(serialized_nodes) != 1:
        raise ValueError(f"{source_name}: expected one serialized file, found {len(serialized_nodes)}")
    old_cab_node = serialized_nodes[0]
    old_cab = old_cab_node[3]
    old_cab_data = raw[old_cab_node[0]:old_cab_node[0] + old_cab_node[1]]
    res_nodes = [node for node in nodes if node[3] == old_cab + ".resS"]
    if len(res_nodes) > 1:
        raise ValueError(f"{source_name}: expected at most one {old_cab}.resS, found {len(res_nodes)}")
    res_node = res_nodes[0] if res_nodes else None
    res_data = raw[res_node[0]:res_node[0] + res_node[1]] if res_node else b""

    unique_seed = f"kickflight localize bundle\0{logical_name}"
    new_cab = "CAB-" + hashlib.md5(unique_seed.encode("utf-8")).hexdigest()

    env = UnityPy.load(unityfs)
    asset_bundle_objects = [obj for obj in env.objects if obj.type.name == "AssetBundle"]
    if len(asset_bundle_objects) != 1:
        raise ValueError(f"{source_name}: expected one AssetBundle object, found {len(asset_bundle_objects)}")
    asset_bundle_obj = asset_bundle_objects[0]
    serialized_file = asset_bundle_obj.assets_file
    bundle = asset_bundle_obj.read()
    if bundle.m_Name != source_name:
        raise ValueError(f"{source_name}: AssetBundle.m_Name is {bundle.m_Name!r}")
    if bundle.m_AssetBundleName and bundle.m_AssetBundleName != source_name:
        raise ValueError(f"{source_name}: AssetBundle.m_AssetBundleName is {bundle.m_AssetBundleName!r}")
    if not bundle.m_Container:
        raise ValueError(f"{source_name}: AssetBundle has no container entries")
    new_container = []
    for old_key, asset_info in bundle.m_Container:
        if _language_of(old_key) is None:
            raise ValueError(f"{source_name}: container key {old_key!r} has no /localize/<lang>/ segment")
        new_key = _replace_language(old_key, lang)
        if _language_of(new_key) != lang:
            raise ValueError(f"{source_name}: container key did not adopt language {lang}: {new_key!r}")
        new_container.append((new_key, asset_info))
    bundle.m_Name = logical_name
    bundle.m_AssetBundleName = logical_name
    bundle.m_Container = new_container
    asset_bundle_obj.save_typetree(bundle)

    stream_records: list[tuple[str, int, int, int]] = []
    for obj in env.objects:
        if obj.type.name not in ("Texture2D", "Mesh"):
            continue
        value = obj.read()
        stream = getattr(value, "m_StreamData", None)
        if stream is None or not stream.path:
            continue
        if res_node is None:
            raise ValueError(f"{source_name}: {obj.type.name} has a stream but the bundle has no .resS node")
        if stream.offset + stream.size > len(res_data):
            raise ValueError(f"{source_name}: {obj.type.name} stream extends beyond {old_cab}.resS")
        self_stream = f"archive:/{old_cab}/{old_cab}.resS"
        if stream.path == self_stream:
            stream_records.append((obj.type.name, obj.path_id, stream.offset, stream.size))
            stream.path = f"archive:/{new_cab}/{new_cab}.resS"
            obj.save_typetree(value)
        elif old_cab in stream.path:
            raise ValueError(f"{source_name}: unrecognized self-stream reference {stream.path!r}")

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

    output = codec.unityfs_to_octo(codec.build_unityfs(header_prefix, info_hash, output_paths, output_data))
    report = {
        "cab": new_cab,
        "source_cab": old_cab,
        "lang": lang,
        "source_serialized_size": len(old_cab_data),
        "serialized_size": len(serialized_bytes),
        "resS_size": len(res_data),
        "stream_records": stream_records,
        "external_paths": [item.path for item in serialized_file.externals],
        "source_md5": hashlib.md5(source_oct).hexdigest(),
    }
    verify_localize_bundle(output, logical_name, lang, source_info, report)
    return output, report


def verify_localize_bundle(
    octo: bytes,
    logical_name: str,
    lang: str,
    source_info: dict[str, Any],
    report: dict[str, Any] | None = None,
) -> dict[str, Any]:
    UnityPy = _load_unitypy()
    codec = _load_bundle_codec()
    unityfs = codec.octo_to_unityfs(octo)
    (_, _), _blocks, nodes, raw = codec.parse_unityfs(unityfs)
    serialized_nodes = [node for node in nodes if node[2] & 4]
    if len(serialized_nodes) != 1:
        raise ValueError(f"{logical_name}: output has {len(serialized_nodes)} serialized files")
    cab = serialized_nodes[0][3]
    res_nodes = [node for node in nodes if node[3] == cab + ".resS"]
    if len(res_nodes) > 1:
        raise ValueError(f"{logical_name}: output has {len(res_nodes)} matching .resS nodes")
    res_data = raw[res_nodes[0][0]:res_nodes[0][0] + res_nodes[0][1]] if res_nodes else b""

    env = UnityPy.load(unityfs)
    type_counts = collections.Counter(obj.type.name for obj in env.objects)
    if cab == source_info.get("cab"):
        raise ValueError(f"{logical_name}: clone kept its donor CAB {cab}")
    if len(env.objects) != source_info["object_count"]:
        raise ValueError(
            f"{logical_name}: object count {len(env.objects)} != source {source_info['object_count']}"
        )
    if dict(type_counts) != source_info["type_counts"]:
        raise ValueError(f"{logical_name}: object types differ from source: {dict(type_counts)}")

    asset_bundle_objects = [obj for obj in env.objects if obj.type.name == "AssetBundle"]
    if len(asset_bundle_objects) != 1:
        raise ValueError(f"{logical_name}: output has {len(asset_bundle_objects)} AssetBundle objects")
    asset_bundle_obj = asset_bundle_objects[0]
    sf = asset_bundle_obj.assets_file
    bundle = asset_bundle_obj.read()
    if bundle.m_Name != logical_name or bundle.m_AssetBundleName != logical_name:
        raise ValueError(f"{logical_name}: output AssetBundle name mismatch")
    if not bundle.m_Container:
        raise ValueError(f"{logical_name}: output has no container entries")
    for key, info in bundle.m_Container:
        if _language_of(key) != lang:
            raise ValueError(f"{logical_name}: container key {key!r} is not {lang}")
        root = sf.objects.get(info.asset.m_PathID)
        if info.asset.m_FileID != 0 or root is None:
            raise ValueError(f"{logical_name}: container root PPtr does not resolve locally: {key!r}")

    local_pointers = external_pointers = 0
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
    for obj in env.objects:
        if obj.type.name not in ("Texture2D", "Mesh"):
            continue
        stream = getattr(obj.read(), "m_StreamData", None)
        if not stream or not stream.path:
            continue
        if not res_nodes:
            raise ValueError(f"{logical_name}: {obj.type.name} stream has no output .resS")
        if stream.offset + stream.size > len(res_data):
            raise ValueError(f"{logical_name}: {obj.type.name} stream exceeds output .resS")
        if stream.path == f"archive:/{cab}/{cab}.resS":
            stream_count += 1
        elif cab in stream.path or is_localize_name(stream.path):
            raise ValueError(f"{logical_name}: self stream points to unexpected CAB path {stream.path!r}")
    for external in sf.externals:
        if cab in external.path and not external.path.startswith(f"archive:/{cab}/{cab}"):
            raise ValueError(f"{logical_name}: malformed self external path {external.path!r}")

    result = {
        "cab": cab,
        "container_keys": [key for key, _ in bundle.m_Container],
        "object_count": len(env.objects),
        "type_counts": dict(type_counts),
        "local_preload_pointers": local_pointers,
        "external_preload_pointers": external_pointers,
        "external_paths": [external.path for external in sf.externals],
        "self_stream_count": stream_count,
        "resS_size": len(res_data),
        "md5": hashlib.md5(octo).hexdigest(),
    }
    if report is not None:
        if report["cab"] != cab:
            raise ValueError(f"{logical_name}: output CAB differs from build report")
        if report["external_paths"] != result["external_paths"]:
            raise ValueError(f"{logical_name}: external dependency paths changed during round-trip")
        if report["source_md5"] == result["md5"]:
            raise ValueError(f"{logical_name}: clone is byte-for-byte identical to its source")
        if stream_count != len(report["stream_records"]):
            raise ValueError(f"{logical_name}: unexpected self-stream reference count")
    return result


def _create_entries(document: dict[str, Any], write: bool) -> tuple[list[dict[str, Any]], list[tuple[str, str]]]:
    specs = build_clone_specs(document)
    expected = len(source_entries(document)) * len(LANG_CODES)
    if len(specs) != expected or not specs:
        raise ValueError(f"expected {expected} localize clones, found {len(specs)}")
    if len(source_entries(document)) != 95:
        raise ValueError(f"expected 95 localize captures, found {len(source_entries(document))}")

    base_entries: list[dict[str, Any]] = []
    for entry in document["entries"]:
        if entry.get("id", "").startswith(ENTRY_ID_PREFIX):
            continue  # regenerated below, keeps re-runs idempotent
        if is_localize_entry(entry):
            zh_names = [name for name in entry["names"] if _language_of(name) == SOURCE_LANG]
            if len(zh_names) != 1:
                raise ValueError(f"{entry.get('id')}: expected one {SOURCE_LANG} name, found {entry['names']!r}")
            entry["names"] = [zh_names[0]]  # keep only the authentic zh route; other names become real entries
        base_entries.append(entry)

    used_ids = {entry["id"] for entry in base_entries}
    used_objects = {entry["objectName"] for entry in base_entries}
    used_octos = {entry.get("octoId") for entry in base_entries}
    next_octo = max((value for value in used_octos if isinstance(value, int)), default=0) + 1
    generated: list[dict[str, Any]] = []
    files_written: list[tuple[str, str]] = []
    if write:
        OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    source_info_cache: dict[str, dict[str, Any]] = {}
    for spec in specs:
        source_path = (ROOT / spec["source_path"]).resolve()
        if not source_path.is_file():
            raise FileNotFoundError(f"missing donor bundle for {spec['source_name']}: {source_path}")
        source_oct = source_path.read_bytes()
        source_info = source_info_cache.get(spec["source_path"])
        if source_info is None:
            source_info = _load_source_info(source_oct)
            source_info_cache[spec["source_path"]] = source_info

        override = _override_path(spec["lang"], spec["source_name"])
        if override.is_file():
            donor_oct = override.read_bytes()
        else:
            donor_oct = source_oct
        cloned, _report = clone_localize_bundle(
            donor_oct, spec["source_name"], spec["logical_name"], spec["lang"], source_info
        )
        if hashlib.md5(cloned).hexdigest() == hashlib.md5(source_oct).hexdigest():
            raise ValueError(f"{spec['logical_name']}: clone equals the authentic zh bundle")

        entry_id = _generated_id(spec["entry_id"], spec["lang"])
        if entry_id in used_ids:
            raise ValueError(f"duplicate generated resource id {entry_id}")
        object_name = _base62_digest(spec["logical_name"], used_objects)
        while next_octo in used_octos:
            next_octo += 1
        octo_id = next_octo
        next_octo += 1
        used_octos.add(octo_id)

        file_name = f"{('A' + object_name).encode('ascii').hex().upper()}_{hashlib.md5(cloned).hexdigest()}.bundle"
        relative_path = Path(".local/localize-bundles") / file_name
        output_path = ROOT / relative_path
        if write:
            output_path.parent.mkdir(parents=True, exist_ok=True)
            if not output_path.is_file() or output_path.read_bytes() != cloned:
                output_path.write_bytes(cloned)
        generated.append({
            "id": entry_id,
            "kind": "assetBundle",
            "octoId": octo_id,
            "names": [spec["logical_name"]],
            "objectName": object_name,
            "sourcePath": relative_path.as_posix(),
            "logicalName": f"Unity AssetBundle {spec['logical_name']}",
            "description": (
                f"Physically independent {spec['lang']} localize bundle cloned from {spec['source_name']}; "
                f"CAB and container keys are unique; built by scripts/build-localize-bundles.py"
            ),
        })
        files_written.append((spec["logical_name"], hashlib.md5(cloned).hexdigest()))
        used_ids.add(entry_id)

    document["entries"] = base_entries + generated
    return generated, files_written


def check_definition(document: dict[str, Any]) -> tuple[int, int]:
    captures = source_entries(document)
    generated = [entry for entry in document["entries"] if entry.get("id", "").startswith(ENTRY_ID_PREFIX)]
    expected = len(captures) * len(LANG_CODES)
    if len(captures) != 95:
        raise ValueError(f"expected 95 localize captures, found {len(captures)}")
    if len(generated) != expected:
        raise ValueError(f"expected {expected} generated localize entries, found {len(generated)}")

    generated_by_name: dict[str, dict[str, Any]] = {}
    for entry in generated:
        names = entry.get("names", [])
        if len(names) != 1 or not is_localize_name(names[0]):
            raise ValueError(f"{entry.get('id')}: generated entry must carry exactly one localize name")
        if names[0] in generated_by_name:
            raise ValueError(f"duplicate generated localize name {names[0]}")
        generated_by_name[names[0]] = entry

    for capture in captures:
        if len(capture.get("names", [])) != 1 or _language_of(capture["names"][0]) != SOURCE_LANG:
            raise ValueError(f"{capture['id']}: localize capture must keep only its zh name")
        for lang in LANG_CODES:
            logical_name = _replace_language(capture["names"][0], lang)
            if logical_name not in generated_by_name:
                raise ValueError(f"missing generated bundle for {logical_name}")

    ids = [entry["id"] for entry in document["entries"]]
    names = [name for entry in document["entries"] for name in entry.get("names", [])]
    objects = [entry["objectName"] for entry in document["entries"]]
    octos = [entry.get("octoId") for entry in document["entries"]]
    if len(set(ids)) != len(ids) or len(set(names)) != len(names):
        raise ValueError("resource names and IDs must be unique")
    if len(set(objects)) != len(objects) or len(set(octos)) != len(octos):
        raise ValueError("objectName and octoId values must be unique")

    source_info_cache: dict[str, dict[str, Any]] = {}
    clone_cabs: set[str] = set()
    clone_paths: set[str] = set()
    for capture in captures:
        source_path = (ROOT / capture["sourcePath"]).resolve()
        source_oct = source_path.read_bytes()
        source_info = source_info_cache.get(capture["sourcePath"])
        if source_info is None:
            source_info = _load_source_info(source_oct)
            source_info_cache[capture["sourcePath"]] = source_info
        for lang in LANG_CODES:
            logical_name = _replace_language(capture["names"][0], lang)
            entry = generated_by_name[logical_name]
            if entry["sourcePath"] in clone_paths:
                raise ValueError(f"physical clone path is shared: {entry['sourcePath']}")
            clone_paths.add(entry["sourcePath"])
            output_path = ROOT / entry["sourcePath"]
            if not output_path.is_file():
                raise FileNotFoundError(output_path)
            result = verify_localize_bundle(
                output_path.read_bytes(), logical_name, lang, source_info
            )
            if result["cab"] in clone_cabs:
                raise ValueError(f"duplicate cloned CAB {result['cab']}")
            clone_cabs.add(result["cab"])
    return len(captures), len(generated)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--check", action="store_true", help="verify generated bundles and title definition")
    mode.add_argument("--dry-run", action="store_true", help="build/verify bundles without writing files")
    args = parser.parse_args()
    document = json.loads(TITLE_PATH.read_text(encoding="utf-8-sig"))
    if args.check:
        captures, entries = check_definition(document)
        print(
            f"per-language localize bundle check passed: {entries} bundles for {captures} captures, "
            f"languages {', '.join(LANG_CODES)}"
        )
        return 0

    generated, files = _create_entries(document, write=not args.dry_run)
    if args.dry_run:
        print(f"dry run passed: {len(generated)} distinct localize bundles, languages {', '.join(LANG_CODES)}")
        for name, digest in files:
            print(f"{name} md5={digest}")
        return 0

    captures, entries = check_definition(document)
    text = json.dumps(document, indent=2, ensure_ascii=False) + "\n"
    encoded = text.replace("\n", "\r\n").encode("utf-8")
    if TITLE_PATH.read_bytes() != encoded:
        TITLE_PATH.write_bytes(encoded)
    print(f"generated and verified {entries} physical localize bundles for {captures} captures")
    print(f"updated {TITLE_PATH.relative_to(ROOT).as_posix()}; run scripts/build-title-resource-catalog.py")
    return 0


if __name__ == "__main__":
    sys.exit(main())
