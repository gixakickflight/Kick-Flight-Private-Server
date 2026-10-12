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
    parser.add_argument("--ios-audit", type=Path, required=True)
    parser.add_argument("--conversion-report", type=Path, required=True)
    parser.add_argument("--android-catalog", type=Path, required=True)
    parser.add_argument("--android-coverage", type=Path, required=True)
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--assets-root", type=Path)
    parser.add_argument("--android-audit", type=Path, required=True)
    parser.add_argument("--manifest-out", type=Path, required=True)
    parser.add_argument("--archive-out", type=Path, required=True)
    args = parser.parse_args()
    base = json.loads(args.base_catalog.read_text(encoding="utf-8"))
    audit = json.loads(args.ios_audit.read_text(encoding="utf-8"))
    report = json.loads(args.conversion_report.read_text(encoding="utf-8"))
    android_manifest = json.loads(args.android_catalog.read_text(encoding="utf-8-sig"))
    android_coverage = json.loads(args.android_coverage.read_text(encoding="utf-8"))
    catalog, payloads = build_catalog(
        base, audit, report, android_manifest=android_manifest, android_coverage=android_coverage,
        source_root=args.source_root, assets_root=args.assets_root, android_audit=args.android_audit)
    rows = catalog["resources"]
    if (sum(row["kind"] == "assetBundle" for row in rows),
            sum(row["kind"] == "resource" for row in rows)) != (1831, 205):
        raise ValueError("candidate kind counts must be 1,831 assetBundles / 205 resources")
    manifest_bytes = (json.dumps(catalog, indent=2, ensure_ascii=False) + "\n").encode()
    args.manifest_out.parent.mkdir(parents=True, exist_ok=True)
    args.manifest_out.write_bytes(manifest_bytes)
    write_archive(manifest_bytes, payloads, args.archive_out)
    digest = hashlib.sha256(args.archive_out.read_bytes()).hexdigest()
    print(json.dumps({
        "snapshot": SNAPSHOT,
        "source_revision": SOURCE_REVISION,
        "wire_revision": WIRE_REVISION,
        "rows": len(rows),
        "assetBundles": 1831,
        "resources": 205,
        "payload_bytes": sum(row["bytes"] for row in rows),
        "archive_bytes": args.archive_out.stat().st_size,
        "archive_sha256": digest,
        "manifest": str(args.manifest_out),
        "archive": str(args.archive_out),
    }, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
