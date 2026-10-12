#!/usr/bin/env python3
"""Validate and atomically stage an immutable iOS resource snapshot archive.

Archive layout: catalog.json plus payload/<path relative to the snapshot payload directory>.
The archive is checked against the exact manifest shipped with the API release before any
filesystem changes are made. This tool stages only; it never changes the active snapshot.
"""
from __future__ import annotations

import argparse
import binascii
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import sys
import tarfile
import tempfile
from typing import Any

MAX_ARCHIVE_BYTES = 2_000_000_000
MAX_TOTAL_PAYLOAD_BYTES = 5_000_000_000
MAX_FILES = 10_000
SNAPSHOT_RE = re.compile(r"^12346-r[1-9][0-9]*$")
SHA_RE = re.compile(r"^[0-9a-f]{64}$")
HEX32_RE = re.compile(r"^[0-9a-f]{32}$")


class SnapshotError(ValueError):
    pass


def _unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise SnapshotError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def _safe_relpath(value: str, label: str) -> str:
    if not isinstance(value, str) or not value or "\\" in value or "\x00" in value:
        raise SnapshotError(f"invalid {label}")
    p = PurePosixPath(value)
    if p.is_absolute() or any(part in ("", ".", "..") for part in p.parts):
        raise SnapshotError(f"unsafe {label}: {value}")
    if p.as_posix() != value:
        raise SnapshotError(f"non-canonical {label}: {value}")
    return value


def _md5() -> Any:
    try:
        return hashlib.md5(usedforsecurity=False)
    except TypeError:  # Python implementations without usedforsecurity
        return hashlib.md5()


def _load_manifest(path: Path, snapshot: str) -> tuple[bytes, dict[str, Any], dict[str, str]]:
    raw = path.read_bytes()
    try:
        doc = json.loads(raw, object_pairs_hook=_unique_object)
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise SnapshotError(f"manifest is not valid UTF-8 JSON: {exc}") from exc
    if not isinstance(doc, dict) or not isinstance(doc.get("resources"), list):
        raise SnapshotError("manifest must be an object with a resources array")
    version = doc.get("assetVersion")
    if type(version) is not int or version != 12346:
        raise SnapshotError("manifest assetVersion must be 12346")
    if doc.get("platform") != 9 or type(doc.get("platform")) is not int:
        raise SnapshotError("manifest platform must be iOS profile 9")
    if doc.get("schemaVersion") != 1 or type(doc.get("schemaVersion")) is not int:
        raise SnapshotError("manifest schemaVersion must be 1")
    revision = doc.get("revision")
    if type(revision) is not int or revision < 0:
        raise SnapshotError("manifest revision must be a non-negative integer")
    if snapshot == "12346-r7":
        rows = doc["resources"]
        kinds = {kind: sum(isinstance(row, dict) and row.get("kind") == kind for row in rows)
                 for kind in ("assetBundle", "resource")}
        if revision != 46 or len(rows) != 2563 or kinds != {"assetBundle": 2358, "resource": 205}:
            raise SnapshotError("12346-r7 must be revision 46 with 2,563 rows (2,358 assetBundles / 205 resources)")

    source_prefix = f".local/ios-resources/{snapshot}/payload/"
    expected: dict[str, str] = {}
    ids: set[str] = set()
    total_bytes = 0
    total = len(doc["resources"])
    if total == 0 or total > MAX_FILES:
        raise SnapshotError("manifest resource count is outside supported bounds")
    for index, row in enumerate(doc["resources"]):
        if not isinstance(row, dict):
            raise SnapshotError(f"resource row {index} is not an object")
        if row.get("enabled") is not True:
            raise SnapshotError(f"resource row {index} is not enabled")
        rid = row.get("id")
        if not isinstance(rid, str) or not rid or rid in ids:
            raise SnapshotError(f"resource row {index} has missing or duplicate id")
        ids.add(rid)
        source = row.get("sourcePath")
        if not isinstance(source, str) or not source.startswith(source_prefix):
            raise SnapshotError(f"resource {rid} sourcePath must be below {source_prefix}")
        rel = _safe_relpath(source[len(source_prefix):], f"sourcePath for {rid}")
        member = f"payload/{rel}"
        if member in expected:
            raise SnapshotError(f"duplicate payload path: {member}")
        size = row.get("bytes")
        if type(size) is not int or size < 0:
            raise SnapshotError(f"resource {rid} has invalid byte size")
        total_bytes += size
        if total_bytes > MAX_TOTAL_PAYLOAD_BYTES:
            raise SnapshotError("manifest payload total exceeds supported bounds")
        for key, pattern in (("sha256", SHA_RE), ("md5", HEX32_RE)):
            value = row.get(key)
            if not isinstance(value, str) or not pattern.fullmatch(value):
                raise SnapshotError(f"resource {rid} has invalid {key}")
        crc = row.get("crc32")
        if type(crc) is not int or not 0 <= crc <= 0xFFFFFFFF:
            raise SnapshotError(f"resource {rid} has invalid crc32")
        expected[member] = rid
    return raw, doc, expected


def _hash_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _verify_payload(stream: Any, row: dict[str, Any], destination: Path | None) -> None:
    sha = hashlib.sha256()
    md5 = _md5()
    crc = 0
    size = 0
    out = destination.open("xb") if destination is not None else None
    try:
        while True:
            block = stream.read(1024 * 1024)
            if not block:
                break
            size += len(block)
            if size > row["bytes"]:
                raise SnapshotError("payload exceeds manifest byte size")
            sha.update(block)
            md5.update(block)
            crc = binascii.crc32(block, crc)
            if out is not None:
                out.write(block)
        if size != row["bytes"]:
            raise SnapshotError("payload byte size does not match manifest")
        if sha.hexdigest() != row["sha256"] or md5.hexdigest() != row["md5"] or (crc & 0xFFFFFFFF) != row["crc32"]:
            raise SnapshotError("payload checksum does not match manifest")
    finally:
        if out is not None:
            out.close()


def _trusted_inputs(archive: Path, expected_sha: str, manifest: Path, snapshot: str) -> tuple[bytes, dict[str, Any], dict[str, str]]:
    if not SHA_RE.fullmatch(expected_sha):
        raise SnapshotError("archive SHA-256 must be 64 lowercase hex characters")
    if not SNAPSHOT_RE.fullmatch(snapshot):
        raise SnapshotError("snapshot must be named 12346-rN")
    archive_size = archive.stat().st_size
    if archive_size <= 0 or archive_size > MAX_ARCHIVE_BYTES:
        raise SnapshotError("archive size is outside supported bounds")
    actual_sha = _hash_file(archive)
    if actual_sha != expected_sha:
        raise SnapshotError("archive SHA-256 does not match trusted expected value")
    manifest_bytes, doc, expected = _load_manifest(manifest, snapshot)
    return manifest_bytes, doc, expected


def _scan_archive(
    archive: Path,
    manifest_bytes: bytes,
    doc: dict[str, Any],
    expected: dict[str, str],
    destination_root: Path | None = None,
) -> None:
    """Validate and optionally write a tar.gz in one forward-only decompression pass."""
    rows_by_member = {
        f"payload/{row['sourcePath'].split('/payload/', 1)[1]}": row
        for row in doc["resources"]
    }
    expected_files = {"catalog.json", *expected.keys()}
    expected_dirs = {"payload"}
    for filename in expected:
        parent = PurePosixPath(filename).parent
        while parent != PurePosixPath("."):
            expected_dirs.add(parent.as_posix())
            parent = parent.parent

    seen: set[str] = set()
    files: set[str] = set()
    member_count = 0
    with tarfile.open(archive, mode="r|gz") as tf:
        for member in tf:
            member_count += 1
            if member_count > MAX_FILES + 2:
                raise SnapshotError("archive contains too many entries")
            member_name = member.name[:-1] if member.isdir() and member.name.endswith("/") else member.name
            name = _safe_relpath(member_name, "archive member")
            if name in seen:
                raise SnapshotError(f"duplicate archive member: {name}")
            seen.add(name)
            if member.isdir():
                if name not in expected_dirs:
                    raise SnapshotError(f"unexpected archive directory: {name}")
                continue
            if not member.isfile():
                raise SnapshotError(f"archive member is not a regular file: {name}")
            if member.issparse():
                raise SnapshotError(f"sparse archive member is not allowed: {name}")
            if member.size < 0 or member.size > MAX_ARCHIVE_BYTES:
                raise SnapshotError(f"archive member size is outside bounds: {name}")
            if name not in expected_files:
                raise SnapshotError(f"unexpected archive file: {name}")
            files.add(name)
            stream = tf.extractfile(member)
            if stream is None:
                raise SnapshotError(f"cannot read archive member: {name}")

            if name == "catalog.json":
                if member.size != len(manifest_bytes):
                    raise SnapshotError("archive catalog size differs from release manifest")
                if stream.read(len(manifest_bytes) + 1) != manifest_bytes:
                    raise SnapshotError("archive catalog is not byte-identical to release manifest")
                if destination_root is not None:
                    (destination_root / "catalog.json").write_bytes(manifest_bytes)
                continue

            row = rows_by_member[name]
            if member.size != row["bytes"]:
                raise SnapshotError(f"archive member size differs from manifest: {name}")
            destination = None
            if destination_root is not None:
                relative = PurePosixPath(name).relative_to("payload")
                destination = destination_root / "payload" / Path(*relative.parts)
                destination.parent.mkdir(parents=True, exist_ok=True)
            _verify_payload(stream, row, destination)

    if files != expected_files:
        missing = sorted(expected_files - files)
        extra = sorted(files - expected_files)
        raise SnapshotError(f"archive members differ from manifest (missing={missing[:3]}, extra={extra[:3]})")


def validate_archive(archive: Path, expected_sha: str, manifest: Path, snapshot: str) -> None:
    manifest_bytes, doc, expected = _trusted_inputs(archive, expected_sha, manifest, snapshot)
    _scan_archive(archive, manifest_bytes, doc, expected)


def _existing_matches(target: Path, manifest_bytes: bytes, doc: dict[str, Any]) -> bool:
    if target.is_symlink() or not target.is_dir():
        raise SnapshotError("target exists and is not a real directory")
    catalog = target / "catalog.json"
    if catalog.is_symlink() or not catalog.is_file() or catalog.read_bytes() != manifest_bytes:
        return False
    actual: set[str] = set()
    for base, dirs, files in os.walk(target, followlinks=False):
        base_path = Path(base)
        if any((base_path / item).is_symlink() for item in dirs + files):
            raise SnapshotError("target tree contains a symlink")
        actual.update((base_path / item).relative_to(target).as_posix() for item in files)
    expected_files = {"catalog.json"}
    for row in doc["resources"]:
        # Keep source subdirectories relative to the target payload root.
        src = row["sourcePath"].split("/payload/", 1)[1]
        rel = Path("payload") / Path(*PurePosixPath(src).parts)
        candidate = target / rel
        if candidate.is_symlink() or not candidate.is_file():
            return False
        if candidate.stat().st_size != row["bytes"] or _hash_file(candidate) != row["sha256"]:
            return False
        expected_files.add(rel.as_posix())
    return actual == expected_files


def stage_archive(archive: Path, expected_sha: str, manifest: Path, stage_root: Path, snapshot: str) -> Path:
    # Authenticate the compressed artifact first; decompress it once while validating and writing.
    manifest_bytes, doc, expected = _trusted_inputs(archive, expected_sha, manifest, snapshot)
    root = stage_root
    root.mkdir(parents=True, exist_ok=True)
    if root.is_symlink() or not root.is_dir():
        raise SnapshotError("stage root must be a real directory")
    root = root.resolve(strict=True)
    target = root / snapshot
    if target.exists() or target.is_symlink():
        _scan_archive(archive, manifest_bytes, doc, expected)
        if _existing_matches(target, manifest_bytes, doc):
            return target
        raise SnapshotError("target snapshot already exists with different or incomplete contents")
    temporary = Path(tempfile.mkdtemp(prefix=f".{snapshot}.tmp-", dir=root))
    try:
        (temporary / "payload").mkdir()
        _scan_archive(archive, manifest_bytes, doc, expected, temporary)
        # Flush file data before publishing the directory entry.
        for base, _, files in os.walk(temporary):
            for filename in files:
                with open(Path(base) / filename, "rb") as f:
                    os.fsync(f.fileno())
        for base, dirs, _ in os.walk(temporary, topdown=False):
            directory_fd = os.open(base, os.O_RDONLY)
            try:
                os.fsync(directory_fd)
            finally:
                os.close(directory_fd)
        os.rename(temporary, target)
        root_fd = os.open(root, os.O_RDONLY)
        try:
            os.fsync(root_fd)
        finally:
            os.close(root_fd)
        return target
    except Exception:
        shutil.rmtree(temporary, ignore_errors=True)
        raise


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("validate", "stage"):
        command = sub.add_parser(name)
        command.add_argument("--archive", required=True, type=Path)
        command.add_argument("--archive-sha256", required=True)
        command.add_argument("--manifest", required=True, type=Path)
        command.add_argument("--snapshot", required=True)
        if name == "stage":
            command.add_argument("--stage-root", required=True, type=Path)
    args = parser.parse_args()
    try:
        if args.command == "validate":
            validate_archive(args.archive, args.archive_sha256, args.manifest, args.snapshot)
            print("validated")
        else:
            path = stage_archive(args.archive, args.archive_sha256, args.manifest, args.stage_root, args.snapshot)
            print(path)
        return 0
    except (SnapshotError, OSError, tarfile.TarError) as exc:
        print(f"snapshot validation failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
