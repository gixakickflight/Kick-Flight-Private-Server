#!/usr/bin/env python3
"""Append validated UI thumbnail conversions to an immutable iOS r5 snapshot.

Inputs are a pinned iOS catalog and its independently verified payload audit,
plus the checked conversion-batch report and generated Octo files. Existing iOS
rows retain their IDs and wire metadata. New request IDs prefer the source
Android object name and fall back to a stable digest-derived ID on collision.
The archive is deterministic and contains only catalog.json and payload files.
"""
from __future__ import annotations

import argparse
import binascii
import gzip
import hashlib
import json
from pathlib import Path
import re
import tarfile


SNAPSHOT = "12346-r5"
SOURCE_REVISION = 44
WIRE_REVISION = 45
BASE64 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"
MODEL_SNAPSHOT = "12346-r6"
MODEL_SOURCE_REVISION = 45
MODEL_WIRE_REVISION = 46
WEAPON_SNAPSHOT = "12346-r7"
WEAPON_SOURCE_REVISION = 46
WEAPON_WIRE_REVISION = 47


def file_hashes(path: Path) -> tuple[int, str, str, int]:
    sha = hashlib.sha256()
    md5 = hashlib.md5()
    crc = 0
    size = 0
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            size += len(block)
            sha.update(block)
            md5.update(block)
            crc = binascii.crc32(block, crc)
    return size, sha.hexdigest(), md5.hexdigest(), crc & 0xFFFFFFFF


def id_from_digest(name: str, occupied: set[str]) -> str:
    raw = hashlib.sha256(("ios-r5:" + name).encode()).digest()
    number = int.from_bytes(raw[:8], "big")
    for _ in range(len(BASE64) ** 2):
        value = number
        chars = []
        for _ in range(6):
            chars.append(BASE64[value % len(BASE64)])
            value //= len(BASE64)
        candidate = "".join(reversed(chars))
        if candidate not in occupied:
            return candidate
        number += 1
    raise ValueError("could not allocate a unique six-character request ID")


def payload_relpath(sha256: str, octo_id: int) -> str:
    return f"{sha256}-{octo_id}.octo"


def _read_android_source(entry: dict, source_root: Path, assets_root: Path | None,
                        android_audit: Path) -> Path:
    staged = android_audit / f"{entry['objectName']}.cdn"
    if staged.is_file():
        return staged
    relative = Path(entry["sourcePath"])
    candidates = [source_root / relative]
    if assets_root and str(relative).startswith("../Kick-Flight-Assets/"):
        candidates.append(assets_root / str(relative).removeprefix("../Kick-Flight-Assets/"))
    for candidate in candidates:
        if candidate.is_file():
            return candidate
    raise FileNotFoundError(f"no audited Android source for {entry['id']}: {candidates[0]}")


def build_catalog(base: dict, audit: dict, report: dict, *, android_manifest: dict,
                  android_coverage: dict, source_root: Path, assets_root: Path | None,
                  android_audit: Path) -> tuple[dict, dict[str, Path]]:
    if (base.get("assetVersion"), base.get("platform"), base.get("revision")) != (12346, 9, 43):
        raise ValueError("base catalog must be the pinned iOS 12346/platform-9/revision-43 manifest")
    if len(base.get("resources", [])) != 501:
        raise ValueError("base catalog must contain exactly 501 rows")
    if report.get("candidate_count") != 1383 or report.get("converted") != 1383 or report.get("failed") != 0:
        raise ValueError("conversion report must be the fully validated 1,383-bundle UI batch")
    audit_rows = {row["requestPath"]: row for row in audit.get("results", [])}
    if len(audit_rows) != 501 or audit.get("downloadedVerified") + audit.get("existingVerified") != 501:
        raise ValueError("iOS payload audit must verify all 501 original rows")

    rows = []
    payload_paths: dict[str, Path] = {}
    occupied_ids = set()
    occupied_names = set()
    occupied_paths = set()
    for base_row in base["resources"]:
        row = dict(base_row)
        audit_row = audit_rows.get(row["requestPath"])
        if not audit_row or audit_row.get("status") not in {"downloaded-verified", "existing-verified"}:
            raise ValueError(f"original iOS row lacks verified source: {row['requestPath']}")
        source = Path(audit_row["path"])
        size, sha, md5, crc = file_hashes(source)
        if (size, sha, md5, crc) != (row["bytes"], row["sha256"], row["md5"], row["crc32"]):
            raise ValueError(f"original iOS payload checksum mismatch: {row['requestPath']}")
        rel = payload_relpath(sha, row["octoId"])
        row["sourcePath"] = f".local/ios-resources/{SNAPSHOT}/payload/{rel}"
        if rel in payload_paths:
            raise ValueError(f"duplicate snapshot payload path: {rel}")
        payload_paths[rel] = source
        rows.append(row)
        occupied_ids.add(row["requestPath"].rsplit("/", 1)[-1])
        occupied_names.add(row["logicalName"])
        occupied_paths.add(row["sourcePath"])

    added_names = set()
    for item in sorted(report["outputs"], key=lambda row: row["logical_name"]):
        logical_name = item["logical_name"]
        if not logical_name.startswith("ui/") or not logical_name.endswith(".unity3d"):
            raise ValueError(f"refusing non-UI or non-AssetBundle output: {logical_name}")
        if logical_name in occupied_names or logical_name in added_names:
            raise ValueError(f"duplicate logical name: {logical_name}")
        source = Path(item["output"])
        size, sha, md5, crc = file_hashes(source)
        if (size, sha) != (item["bytes"], item["sha256"]):
            raise ValueError(f"converted output checksum mismatch: {logical_name}")
        candidate = item["source_objectName"]
        if not re.fullmatch(r"[0-9A-Za-z]{6}", candidate) or candidate in occupied_ids:
            candidate = id_from_digest(logical_name, occupied_ids)
        occupied_ids.add(candidate)
        octo_id = len(rows) + 1
        rel = payload_relpath(sha, octo_id)
        source_path = f".local/ios-resources/{SNAPSHOT}/payload/{rel}"
        if rel in payload_paths or source_path in occupied_paths:
            raise ValueError(f"duplicate snapshot payload path: {rel}")
        row = {
            "bytes": size,
            "contentType": "application/octet-stream",
            "crc32": crc,
            "description": "Target-9 UI Sprite/Texture reconstruction from verified Android source",
            "enabled": True,
            "host": "kick-flight-fenix.us.ci",
            "id": f"ios-octo-A-{candidate}",
            "kind": "assetBundle",
            "logicalName": logical_name,
            "md5": md5,
            "octoId": octo_id,
            "octoName": logical_name,
            "requestPath": f"/ios/cdn/{candidate}",
            "sha256": sha,
            "sourcePath": source_path,
        }
        rows.append(row)
        payload_paths[rel] = source
        occupied_names.add(logical_name)
        occupied_paths.add(source_path)
        added_names.add(logical_name)

    # Preserve the 53 existing iOS raw-resource rows exactly. Add only Android
    # raw resources whose verified bytes are not already represented in iOS.
    # The classification is intentionally narrow: this audit checked identical
    # iOS codec-family samples for USM, ACB and AWB, while runtime compatibility
    # for these Android-only payloads remains unverified.
    coverage_by_id = {}
    for row in android_coverage.get("androidEntries", []):
        key = row.get("androidCatalogId")
        if key:
            coverage_by_id.setdefault(key, row)
    ios_hashes = {row["sha256"] for row in rows if row["kind"] == "resource"}
    raw_candidates = []
    android_entries = android_manifest.get("entries", [])
    for entry in android_entries:
        if entry.get("kind") != "resource":
            continue
        evidence = coverage_by_id.get(entry["id"])
        if not evidence or not evidence.get("androidExpectedSha256"):
            raise ValueError(f"missing source audit evidence for Android resource {entry['id']}")
        source = _read_android_source(entry, source_root, assets_root, android_audit)
        size, sha, md5, crc = file_hashes(source)
        expected_size = evidence.get("androidSourceBytes") or evidence.get("androidCdnContentLength")
        if size != expected_size or sha != evidence["androidExpectedSha256"]:
            raise ValueError(f"Android resource audit checksum mismatch: {entry['id']}")
        if sha in ios_hashes:
            continue
        ext = Path(entry["names"][0]).suffix.lower() if entry.get("names") else ""
        if ext not in {".usm", ".acb", ".awb"}:
            raise ValueError(f"unreviewed Android-only resource extension {ext}: {entry['id']}")
        raw_candidates.append((entry, source, size, sha, md5, crc, ext))

    expected_ext_counts = {".usm": 131, ".acb": 13, ".awb": 8}
    actual_ext_counts: dict[str, int] = {}
    for *_, ext in raw_candidates:
        actual_ext_counts[ext] = actual_ext_counts.get(ext, 0) + 1
    if (len(raw_candidates) != 152 or sum(row[2] for row in raw_candidates) != 127_938_170 or
            actual_ext_counts != expected_ext_counts):
        raise ValueError(f"Android raw-resource allowlist does not match audited 152-row corpus: {actual_ext_counts}")
    for entry, source, size, sha, md5, crc, _ext in raw_candidates:
        logical_name = entry["logicalName"]
        if logical_name in occupied_names:
            raise ValueError(f"Android-only resource logical name already exists in iOS catalog: {logical_name}")
        candidate = entry["objectName"]
        if not re.fullmatch(r"[0-9A-Za-z]{6}", candidate) or candidate in occupied_ids:
            candidate = id_from_digest(logical_name, occupied_ids)
        occupied_ids.add(candidate)
        octo_id = len(rows) + 1
        rel = payload_relpath(sha, octo_id)
        source_path = f".local/ios-resources/{SNAPSHOT}/payload/{rel}"
        if rel in payload_paths or source_path in occupied_paths:
            raise ValueError(f"duplicate snapshot payload path: {rel}")
        row = {
            "bytes": size,
            "contentType": "application/octet-stream",
            "crc32": crc,
            "description": "Unmodified Android raw resource; matching iOS codec-family samples were checked",
            "enabled": True,
            "host": "kick-flight-fenix.us.ci",
            "id": f"ios-octo-R-{candidate}",
            "kind": "resource",
            "logicalName": logical_name,
            "md5": md5,
            "octoId": octo_id,
            "octoName": logical_name,
            "requestPath": f"/ios/cdn/{candidate}",
            "sha256": sha,
            "sourcePath": source_path,
        }
        rows.append(row)
        payload_paths[rel] = source
        occupied_names.add(logical_name)
        occupied_paths.add(source_path)

    if len(rows) != 2036 or len(payload_paths) != 2036:
        raise ValueError(f"snapshot should contain 2,036 unique rows and payloads; got {len(rows)}")
    catalog = {key: value for key, value in base.items() if key != "resources"}
    catalog["revision"] = SOURCE_REVISION
    catalog["resources"] = rows
    return catalog, payload_paths


def build_model_catalog(base: dict, report: dict, *, android_coverage: dict,
                        source_root: Path) -> tuple[dict, dict[str, Path]]:
    """Append verified Android player-model conversions to the immutable r5 catalog."""
    if (base.get("assetVersion"), base.get("platform"), base.get("revision")) != (12346, 9, 44):
        raise ValueError("model base must be the pinned iOS 12346/platform-9/revision-44 catalog")
    if len(base.get("resources", [])) != 2036:
        raise ValueError("model base must contain exactly the 2,036-row r5 catalog")
    kind_counts = {kind: sum(row.get("kind") == kind for row in base["resources"])
                   for kind in ("assetBundle", "resource")}
    if kind_counts != {"assetBundle": 1831, "resource": 205}:
        raise ValueError(f"r5 catalog kind counts changed: {kind_counts}")
    if (report.get("expected_count") != 216 or len(report.get("converted", [])) != 216 or
            report.get("rejected") or report.get("summary", {}).get("converted") != 216 or
            report.get("summary", {}).get("rejected") != 0):
        raise ValueError("model report must contain the fully validated 216-bundle batch")

    sources = {row["name"]: row for row in android_coverage.get("androidEntries", [])
               if re.fullmatch(r"player/pc_\d+/pc_\d+_\d+\.unity3d", row.get("name", ""))
               and not row.get("iosPresentInManifest")}
    if len(sources) != 216:
        raise ValueError(f"Android coverage must contain exactly 216 missing player routes, found {len(sources)}")

    rows: list[dict] = []
    payload_paths: dict[str, Path] = {}
    occupied_ids: set[str] = set()
    occupied_names: set[str] = set()
    occupied_paths: set[str] = set()
    for base_row in base["resources"]:
        row = dict(base_row)
        logical_name = row["logicalName"]
        source_path = source_root / row["sourcePath"]
        size, sha, md5, crc = file_hashes(source_path)
        if (size, sha, md5, crc) != (row["bytes"], row["sha256"], row["md5"], row["crc32"]):
            raise ValueError(f"r5 payload checksum mismatch: {logical_name}")
        relative = payload_relpath(sha, int(row["octoId"]))
        row["sourcePath"] = f".local/ios-resources/{MODEL_SNAPSHOT}/payload/{relative}"
        if relative in payload_paths:
            raise ValueError(f"duplicate r5 payload path: {relative}")
        payload_paths[relative] = source_path
        rows.append(row)
        occupied_ids.add(row["requestPath"].rsplit("/", 1)[-1])
        occupied_names.add(logical_name)
        occupied_paths.add(row["sourcePath"])

    added_names: set[str] = set()
    for item in sorted(report["converted"], key=lambda row: row["logical_name"]):
        logical_name = item["logical_name"]
        if not re.fullmatch(r"player/pc_\d+/pc_\d+_\d+\.unity3d", logical_name):
            raise ValueError(f"refusing non-player model output: {logical_name}")
        if logical_name not in sources:
            raise ValueError(f"converted model is absent from the audited Android coverage: {logical_name}")
        if logical_name in occupied_names or logical_name in added_names:
            raise ValueError(f"duplicate logical name: {logical_name}")
        evidence = sources[logical_name]
        if (item.get("source_sha256") != evidence.get("androidExpectedSha256") or
                item.get("source_target_platform") != 13 or item.get("output_target_platform") != 9 or
                item.get("unity_version") != "2018.4.11f1"):
            raise ValueError(f"model conversion evidence mismatch: {logical_name}")
        source = Path(item["output_path"])
        size, sha, md5, crc = file_hashes(source)
        if (size, sha) != (item["output_bytes"], item["output_sha256"]):
            raise ValueError(f"converted output checksum mismatch: {logical_name}")
        candidate = evidence.get("androidObjectName", "")
        if not re.fullmatch(r"[0-9A-Za-z]{6}", candidate) or candidate in occupied_ids:
            candidate = id_from_digest(logical_name, occupied_ids)
        occupied_ids.add(candidate)
        octo_id = len(rows) + 1
        relative = payload_relpath(sha, octo_id)
        source_path = f".local/ios-resources/{MODEL_SNAPSHOT}/payload/{relative}"
        if relative in payload_paths or source_path in occupied_paths:
            raise ValueError(f"duplicate model payload path: {relative}")
        rows.append({
            "bytes": size,
            "contentType": "application/octet-stream",
            "crc32": crc,
            "description": "Target-9 iOS model bundle reconstructed from verified Android source",
            "enabled": True,
            "host": "kick-flight-fenix.us.ci",
            "id": f"ios-octo-A-{candidate}",
            "kind": "assetBundle",
            "logicalName": logical_name,
            "md5": md5,
            "octoId": octo_id,
            "octoName": logical_name,
            "requestPath": f"/ios/cdn/{candidate}",
            "sha256": sha,
            "sourcePath": source_path,
        })
        payload_paths[relative] = source
        occupied_names.add(logical_name)
        occupied_paths.add(source_path)
        added_names.add(logical_name)

    counts = {kind: sum(row.get("kind") == kind for row in rows) for kind in ("assetBundle", "resource")}
    if len(rows) != 2252 or len(payload_paths) != 2252 or counts != {"assetBundle": 2047, "resource": 205}:
        raise ValueError(f"r6 expected 2,252 rows (2,047 assetBundles / 205 resources); got {len(rows)} / {counts}")
    catalog = {key: value for key, value in base.items() if key != "resources"}
    catalog["revision"] = MODEL_SOURCE_REVISION
    catalog["resources"] = rows
    return catalog, payload_paths


def build_weapon_catalog(base: dict, report: dict, postflight: dict, *, android_coverage: dict,
                         source_root: Path) -> tuple[dict, dict[str, Path]]:
    """Append only verified weapon conversions to the immutable r6 catalog."""
    if (base.get("assetVersion"), base.get("platform"), base.get("revision")) != (12346, 9, 45):
        raise ValueError("weapon base must be the pinned iOS 12346/platform-9/revision-45 r6 catalog")
    if len(base.get("resources", [])) != 2252:
        raise ValueError("weapon base must contain exactly the 2,252-row r6 catalog")
    counts = {kind: sum(row.get("kind") == kind for row in base["resources"])
              for kind in ("assetBundle", "resource")}
    if counts != {"assetBundle": 2047, "resource": 205}:
        raise ValueError(f"r6 catalog kind counts changed: {counts}")
    items = report.get("converted", [])
    if (report.get("scope") != "weapon" or report.get("expected_count") != 311 or
            len(items) != 311 or report.get("rejected") or
            report.get("summary", {}).get("converted") != 311 or
            report.get("summary", {}).get("rejected") != 0 or
            report.get("summary", {}).get("source_bytes") != 33_714_065):
        raise ValueError("weapon report must contain the fully validated 311-bundle batch")
    post_summary = postflight.get("summary", {})
    if (post_summary.get("routes") != 311 or post_summary.get("source_bytes") != 33_714_065 or
            post_summary.get("weapon_key_included") is not True or
            post_summary.get("all_platforms_13_to_9") is not True or
            post_summary.get("all_full_typetree_matches") is not True or
            post_summary.get("all_raw_object_payloads_preserved") is not True or
            post_summary.get("all_reference_graphs_preserved") is not True or
            post_summary.get("all_output_shader_dependencies_checked") is not True or
            post_summary.get("local_shader_count") != 0):
        raise ValueError("weapon postflight report does not prove every required conversion invariant")
    report_rows = {row.get("logical_name"): row for row in report.get("converted", [])}
    post_rows = {row.get("logical_name"): row for row in postflight.get("routes", [])}
    if len(post_rows) != 311 or set(report_rows) != set(post_rows):
        raise ValueError("weapon postflight routes do not exactly match the conversion report")
    for name, item in report_rows.items():
        proof = post_rows[name]
        if (proof.get("source_sha256") != item.get("source_sha256") or
                proof.get("output_sha256") != item.get("output_sha256") or
                proof.get("typetree_schema_equal") is not True or
                proof.get("object_payloads_preserved") is not True or
                proof.get("reference_graph_equal") is not True or
                proof.get("metal_shaders") != 15 or
                proof.get("external_shader_paths") != item.get("shader", {}).get("external_paths")):
            raise ValueError(f"weapon postflight evidence mismatch: {name}")

    sources = {row["name"]: row for row in android_coverage.get("androidEntries", [])
               if re.fullmatch(r"weapon/wp_\d+/wp_.*\.unity3d", row.get("name", ""))
               and not row.get("iosPresentInManifest")}
    if len(sources) != 311:
        raise ValueError(f"Android coverage must contain exactly 311 missing weapon routes, found {len(sources)}")

    rows: list[dict] = []
    payload_paths: dict[str, Path] = {}
    occupied_ids: set[str] = set()
    occupied_names: set[str] = set()
    for base_row in base["resources"]:
        row = dict(base_row)
        source = source_root / row["sourcePath"]
        size, sha, md5, crc = file_hashes(source)
        if (size, sha, md5, crc) != (row["bytes"], row["sha256"], row["md5"], row["crc32"]):
            raise ValueError(f"r6 payload checksum mismatch: {row['logicalName']}")
        rel = payload_relpath(sha, int(row["octoId"]))
        row["sourcePath"] = f".local/ios-resources/{WEAPON_SNAPSHOT}/payload/{rel}"
        if rel in payload_paths:
            raise ValueError(f"duplicate r6 payload path: {rel}")
        payload_paths[rel] = source
        rows.append(row)
        occupied_ids.add(row["requestPath"].rsplit("/", 1)[-1])
        occupied_names.add(row["logicalName"])

    for item in sorted(items, key=lambda row: row["logical_name"]):
        name = item["logical_name"]
        evidence = sources.get(name)
        if not evidence or name in occupied_names:
            raise ValueError(f"weapon output absent from audited coverage or duplicate: {name}")
        if (item.get("profile") != "weapon" or item.get("source_sha256") != evidence.get("androidExpectedSha256") or
                item.get("source_target_platform") != 13 or item.get("output_target_platform") != 9 or
                item.get("unity_version") != "2018.4.11f1" or
                item.get("shader", {}).get("ios_shader_cab") != "CAB-96ae749bf375168094b083c67a3f9536" or
                item.get("shader", {}).get("metal_shader_count") != 15):
            raise ValueError(f"weapon conversion evidence mismatch: {name}")
        source = Path(item["output_path"])
        size, sha, md5, crc = file_hashes(source)
        if (size, sha) != (item["output_bytes"], item["output_sha256"]):
            raise ValueError(f"converted weapon checksum mismatch: {name}")
        candidate = evidence.get("androidObjectName", "")
        if not re.fullmatch(r"[0-9A-Za-z]{6}", candidate) or candidate in occupied_ids:
            candidate = id_from_digest(name, occupied_ids)
        occupied_ids.add(candidate)
        octo_id = len(rows) + 1
        rel = payload_relpath(sha, octo_id)
        rows.append({
            "bytes": size, "contentType": "application/octet-stream", "crc32": crc,
            "description": "Target-9 iOS weapon model reconstructed from verified Android source",
            "enabled": True, "host": "kick-flight-fenix.us.ci", "id": f"ios-octo-A-{candidate}",
            "kind": "assetBundle", "logicalName": name, "md5": md5, "octoId": octo_id,
            "octoName": name, "requestPath": f"/ios/cdn/{candidate}", "sha256": sha,
            "sourcePath": f".local/ios-resources/{WEAPON_SNAPSHOT}/payload/{rel}",
        })
        payload_paths[rel] = source
        occupied_names.add(name)

    final_counts = {kind: sum(row.get("kind") == kind for row in rows)
                    for kind in ("assetBundle", "resource")}
    if len(rows) != 2563 or final_counts != {"assetBundle": 2358, "resource": 205}:
        raise ValueError(f"r7 expected 2,563 rows (2,358 assetBundles / 205 resources); got {len(rows)} / {final_counts}")
    catalog = {key: value for key, value in base.items() if key != "resources"}
    catalog["revision"] = WEAPON_SOURCE_REVISION
    catalog["resources"] = rows
    return catalog, payload_paths


def write_archive(catalog_bytes: bytes, payload_paths: dict[str, Path], archive_path: Path) -> None:
    archive_path.parent.mkdir(parents=True, exist_ok=True)
    with archive_path.open("wb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0, compresslevel=6) as gz:
            with tarfile.open(fileobj=gz, mode="w", format=tarfile.USTAR_FORMAT) as tar:
                catalog_info = tarfile.TarInfo("catalog.json")
                catalog_info.size = len(catalog_bytes)
                catalog_info.mode = 0o644
                catalog_info.mtime = 0
                import io
                tar.addfile(catalog_info, io.BytesIO(catalog_bytes))
                # Keep archive order identical to manifest order. The validator
                # consumes expected members in that same order, avoiding repeated
                # decompression seeks through a large gzip stream.
                for relative, source in payload_paths.items():
                    info = tarfile.TarInfo(f"payload/{relative}")
                    info.size = source.stat().st_size
                    info.mode = 0o644
                    info.mtime = 0
                    with source.open("rb") as payload:
                        tar.addfile(info, payload)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-catalog", type=Path, required=True)
    parser.add_argument("--ios-audit", type=Path)
    parser.add_argument("--conversion-report", type=Path)
    parser.add_argument("--model-conversion-report", type=Path,
                        help="validated 216-model conversion report; builds additive 12346-r6")
    parser.add_argument("--weapon-conversion-report", type=Path,
                        help="validated 311-weapon conversion report; builds additive 12346-r7")
    parser.add_argument("--weapon-postflight-report", type=Path,
                        help="independent 311-route readback proving schema, payload and shader invariants")
    parser.add_argument("--android-catalog", type=Path)
    parser.add_argument("--android-coverage", type=Path, required=True)
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--assets-root", type=Path)
    parser.add_argument("--android-audit", type=Path)
    parser.add_argument("--manifest-out", type=Path, required=True)
    parser.add_argument("--archive-out", type=Path, required=True)
    args = parser.parse_args()
    base = json.loads(args.base_catalog.read_text(encoding="utf-8"))
    android_coverage = json.loads(args.android_coverage.read_text(encoding="utf-8"))
    if args.model_conversion_report and args.weapon_conversion_report:
        parser.error("choose only one model conversion report")
    if args.weapon_conversion_report:
        if not args.weapon_postflight_report:
            parser.error("--weapon-conversion-report requires --weapon-postflight-report")
        weapon_report = json.loads(args.weapon_conversion_report.read_text(encoding="utf-8"))
        postflight_report = json.loads(args.weapon_postflight_report.read_text(encoding="utf-8"))
        catalog, payloads = build_weapon_catalog(
            base, weapon_report, postflight_report,
            android_coverage=android_coverage, source_root=args.source_root)
        snapshot, wire_revision = WEAPON_SNAPSHOT, WEAPON_WIRE_REVISION
    elif args.model_conversion_report:
        model_report = json.loads(args.model_conversion_report.read_text(encoding="utf-8"))
        catalog, payloads = build_model_catalog(
            base, model_report, android_coverage=android_coverage, source_root=args.source_root)
        snapshot, wire_revision = MODEL_SNAPSHOT, MODEL_WIRE_REVISION
    else:
        missing = [name for name, value in (("--ios-audit", args.ios_audit),
                    ("--conversion-report", args.conversion_report),
                    ("--android-catalog", args.android_catalog),
                    ("--android-audit", args.android_audit)) if value is None]
        if missing:
            parser.error("UI snapshot mode requires " + ", ".join(missing))
        audit = json.loads(args.ios_audit.read_text(encoding="utf-8"))
        report = json.loads(args.conversion_report.read_text(encoding="utf-8"))
        android_manifest = json.loads(args.android_catalog.read_text(encoding="utf-8-sig"))
        catalog, payloads = build_catalog(
            base, audit, report, android_manifest=android_manifest, android_coverage=android_coverage,
            source_root=args.source_root, assets_root=args.assets_root, android_audit=args.android_audit)
        snapshot, wire_revision = SNAPSHOT, WIRE_REVISION
    rows = catalog["resources"]
    expected_kinds = ((2358, 205) if args.weapon_conversion_report else
                      (2047, 205) if args.model_conversion_report else (1831, 205))
    if (sum(row["kind"] == "assetBundle" for row in rows),
            sum(row["kind"] == "resource" for row in rows)) != expected_kinds:
        raise ValueError(f"candidate kind counts must be {expected_kinds[0]} assetBundles / {expected_kinds[1]} resources")
    manifest_bytes = (json.dumps(catalog, indent=2, ensure_ascii=False) + "\n").encode()
    args.manifest_out.parent.mkdir(parents=True, exist_ok=True)
    args.manifest_out.write_bytes(manifest_bytes)
    write_archive(manifest_bytes, payloads, args.archive_out)
    digest = hashlib.sha256(args.archive_out.read_bytes()).hexdigest()
    print(json.dumps({
        "snapshot": snapshot,
        "source_revision": catalog["revision"],
        "wire_revision": wire_revision,
        "rows": len(rows),
        "assetBundles": expected_kinds[0],
        "resources": expected_kinds[1],
        "payload_bytes": sum(row["bytes"] for row in rows),
        "archive_bytes": args.archive_out.stat().st_size,
        "archive_sha256": digest,
        "manifest": str(args.manifest_out),
        "archive": str(args.archive_out),
    }, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
