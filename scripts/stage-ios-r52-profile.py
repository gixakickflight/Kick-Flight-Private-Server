#!/usr/bin/env python3
"""Package, validate, and atomically stage the reviewed R52 iOS profile.

This profile is deliberately separate from the R5-R7 native iOS snapshot format. It contains a
test-only report, title minimum, prebuilt Octo fixtures, a resource catalog, and payload-r7 files.
It never changes the active API setting; deployment opts in separately.
"""
from __future__ import annotations

import argparse
import base64
import binascii
import gzip
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import sys
import tarfile
import tempfile
from typing import Any, BinaryIO, Iterable

ASSET_VERSION = 12346
REVISION = 52
EXPECTED_ROWS = 3719
EXPECTED_RESOURCES = 3389
EXPECTED_PAYLOAD_BYTES = 971_731_473
MAX_ARCHIVE_BYTES = 2_000_000_000
MAX_PAYLOAD_BYTES = 5_000_000_000
MAX_ARCHIVE_MEMBERS = 10_000
MAX_MEMBER_BYTES = 2_000_000_000
HOST = "kick-flight-fenix.us.ci"
CDN_PREFIX = "/ios/cdn/"
URL_FORMAT = "https://kick-flight-fenix.us.ci/ios/cdn/{o}"
REPORT_SCHEMA = "r56.bootstrapapi.offline-profile.r52-direct-review.v1"
STAGE_NAME = "12346-r52"
PROFILE_SOURCE = "r56-artifact-recovery/bootstrap-profile-r52-field901-experimental"
SHA_RE = re.compile(r"^[0-9a-f]{64}$")
FIXTURE_NAMES = {
    *(f"resource-list-{ASSET_VERSION}-from-{revision}.json" for revision in range(REVISION)),
    f"resource-list-{ASSET_VERSION}.json",
}


class ProfileError(ValueError):
    pass


def _unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ProfileError("duplicate JSON key")
        result[key] = value
    return result


def _load_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=_unique_object)
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ProfileError(f"invalid JSON file: {path.name}") from exc


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _safe_relative(value: str) -> str:
    if not isinstance(value, str) or not value or "\\" in value or "\x00" in value:
        raise ProfileError("invalid archive path")
    path = PurePosixPath(value)
    if path.is_absolute() or path.as_posix() != value or any(part in ("", ".", "..") for part in value.split("/")):
        raise ProfileError("unsafe archive path")
    return value


def _validate_report(report: dict[str, Any], *, require_sanitized_source: bool) -> None:
    expected = {
        "schema": REPORT_SCHEMA,
        "assetVersion": ASSET_VERSION,
        "revision": REVISION,
        "previousRevision": 51,
        "fixtureCount": 53,
        "currentFixtureRevision": REVISION,
        "servedRows": EXPECTED_ROWS,
        "resourceRoutes": EXPECTED_RESOURCES,
        "payloadCount": EXPECTED_RESOURCES,
        "payloadBytes": EXPECTED_PAYLOAD_BYTES,
        "canonicalHost": HOST,
        "urlFormatInFixtures": URL_FORMAT,
        "cdnRequestPrefix": CDN_PREFIX,
        "testOnly": True,
        "complete": False,
        "originalWireMapVerified": False,
        "experimentalField901": True,
        "unverifiedReconstructedRowIds": 1156,
        "upstreamMustRemainDisabled": True,
        "noNetworkRequests": True,
        "noServerStarted": True,
        "noVpsWrite": True,
        "originalshaderPresent": True,
        "mobileDiffuseShaderPresent": False,
        "particleStandardUnlitShaderPresent": False,
        "downloadsShaderCandidatesAdded": False,
        "androidAssetVersion": 12345,
    }
    if any(report.get(key) != value for key, value in expected.items()):
        raise ProfileError("profile report does not match the pinned R52 review package")
    revisions = list(range(REVISION))
    if report.get("fromRevisions") != revisions or report.get("fullUpdateFixtureRevisions") != revisions:
        raise ProfileError("profile report has an invalid revision window")
    source = report.get("sourceProfile")
    if not isinstance(source, str) or not source:
        raise ProfileError("profile source marker is missing")
    if require_sanitized_source and source != PROFILE_SOURCE:
        raise ProfileError("profile source marker is not sanitized")
    if not require_sanitized_source and ("\n" in source or "\r" in source):
        raise ProfileError("profile source marker is invalid")
    for key in ("packageCatalogSha256", "titleMinimumSha256", "fixtureSetSha256", "payloadSetSha256"):
        if not isinstance(report.get(key), str) or not SHA_RE.fullmatch(report[key]):
            raise ProfileError("profile report contains an invalid checksum")
    fixture_sha = report.get("fixtureSha256")
    if not isinstance(fixture_sha, dict) or set(fixture_sha) != FIXTURE_NAMES or any(
        not isinstance(value, str) or not SHA_RE.fullmatch(value) for value in fixture_sha.values()
    ):
        raise ProfileError("profile fixture hash manifest is invalid")


def _read_varint(data: bytes, index: int) -> tuple[int, int]:
    value = 0
    shift = 0
    while index < len(data) and shift < 64:
        byte = data[index]
        index += 1
        value |= (byte & 0x7F) << shift
        if byte < 0x80:
            return value, index
        shift += 7
    raise ProfileError("malformed Octo protobuf varint")


def _fields(data: bytes) -> Iterable[tuple[int, int, int | bytes]]:
    index = 0
    while index < len(data):
        key, index = _read_varint(data, index)
        number, wire = key >> 3, key & 7
        if number == 0:
            raise ProfileError("invalid Octo protobuf field number")
        if wire == 0:
            value, index = _read_varint(data, index)
            yield number, wire, value
        elif wire == 1:
            if index > len(data) - 8:
                raise ProfileError("truncated Octo protobuf field")
            yield number, wire, data[index:index + 8]
            index += 8
        elif wire == 2:
            size, index = _read_varint(data, index)
            if size > len(data) - index:
                raise ProfileError("truncated Octo protobuf field")
            end = index + size
            yield number, wire, data[index:end]
            index = end
        elif wire == 5:
            if index > len(data) - 4:
                raise ProfileError("truncated Octo protobuf field")
            yield number, wire, data[index:index + 4]
            index += 4
        else:
            raise ProfileError("unsupported Octo protobuf wire type")


def _validate_row(data: bytes, expected_state: int) -> int:
    identifiers = [value for number, wire, value in _fields(data) if number == 1 and wire == 0]
    states = [value for number, wire, value in _fields(data) if number == 9 and wire == 0]
    if len(identifiers) != 1 or len(states) != 1 or states[0] != expected_state:
        raise ProfileError("R52 Octo row ID/state is invalid")
    return int(identifiers[0])


def _validate_database(data: bytes, from_revision: int) -> None:
    expected_state = 1 if from_revision == 0 else 2
    version: list[int] = []
    url_formats: list[str] = []
    ids: set[int] = set()
    kinds = {2: 0, 4: 0}
    common_count = 0
    for number, wire, value in _fields(data):
        if number == 1 and wire == 0:
            version.append(int(value))
        elif number == 5 and wire == 2:
            try:
                url_formats.append(bytes(value).decode("utf-8", errors="strict"))
            except UnicodeDecodeError as exc:
                raise ProfileError("R52 CDN URL is not UTF-8") from exc
        elif number == 3 and wire == 2:
            common_count += 1
            if value != b"common":
                raise ProfileError("R52 Octo common group is invalid")
        elif number in kinds and wire == 2:
            kinds[number] += 1
            identifier = _validate_row(bytes(value), expected_state)
            if identifier in ids:
                raise ProfileError("R52 Octo fixture contains duplicate IDs")
            ids.add(identifier)
    row_count = len(ids)
    expected_rows = 0 if from_revision == REVISION else EXPECTED_ROWS
    if version != [REVISION] or url_formats != [URL_FORMAT] or row_count != expected_rows:
        raise ProfileError("R52 Octo fixture version, URL, or row count is invalid")
    if expected_rows and (kinds != {2: 3131, 4: 588} or common_count != 1):
        raise ProfileError("R52 Octo fixture group counts are invalid")
    if not expected_rows and (kinds != {2: 0, 4: 0} or common_count != 0):
        raise ProfileError("R52 current-revision fixture must contain no rows")


def _validate_title(path: Path) -> None:
    title = _load_json(path)
    if not isinstance(title, dict) or title.get("assetVersion") != ASSET_VERSION or \
            title.get("revision") != REVISION or title.get("previousRevision") != 51 or \
            title.get("fromRevisions") != list(range(REVISION)) or \
            title.get("urlPathFormat") != CDN_PREFIX + "{o}":
        raise ProfileError("R52 title minimum is invalid")


def _validate_fixture(path: Path, revision: int) -> None:
    fixture = _load_json(path)
    expected_path = f"/v1/list/{ASSET_VERSION}/{revision}"
    if not isinstance(fixture, dict) or fixture.get("enabled") is not True or \
            fixture.get("host") != HOST or fixture.get("method") != "GET" or \
            fixture.get("path") != expected_path or fixture.get("statusCode") != 200 or \
            fixture.get("contentType", "").lower() != "application/x-protobuf":
        raise ProfileError("R52 list fixture route metadata is invalid")
    body = fixture.get("bodyBase64")
    if not isinstance(body, str):
        raise ProfileError("R52 list fixture body is missing")
    try:
        data = base64.b64decode(body, validate=True)
    except (ValueError, binascii.Error) as exc:
        raise ProfileError("R52 list fixture body is not valid base64") from exc
    _validate_database(data, revision)


def _expected_files(resources: list[dict[str, Any]]) -> set[str]:
    result = {"profile-report.json", "title-minimum.json", "catalog.json"}
    result.update(f"fixtures/{name}" for name in FIXTURE_NAMES)
    result.update(resource["sourcePath"] for resource in resources)
    return result


def _validate_tree(profile_root: Path, *, allow_review_docs: bool, require_sanitized_source: bool) -> dict[str, Any]:
    if not profile_root.is_dir() or profile_root.is_symlink():
        raise ProfileError("R52 profile root is missing or a symbolic link")
    report_path = profile_root / "profile-report.json"
    catalog_path = profile_root / "catalog.json"
    title_path = profile_root / "title-minimum.json"
    fixture_dir = profile_root / "fixtures"
    payload_dir = profile_root / "payload-r7"
    if not fixture_dir.is_dir() or fixture_dir.is_symlink() or not payload_dir.is_dir() or payload_dir.is_symlink():
        raise ProfileError("R52 profile directories are missing or symbolic links")

    report = _load_json(report_path)
    if not isinstance(report, dict):
        raise ProfileError("R52 profile report must be an object")
    _validate_report(report, require_sanitized_source=require_sanitized_source)
    if _sha256_file(title_path) != report["titleMinimumSha256"]:
        raise ProfileError("R52 title minimum checksum mismatch")
    _validate_title(title_path)
    if _sha256_file(catalog_path) != report["packageCatalogSha256"]:
        raise ProfileError("R52 catalog checksum mismatch")
    catalog = _load_json(catalog_path)
    if not isinstance(catalog, dict) or catalog.get("schemaVersion", 1) != 1:
        raise ProfileError("R52 catalog schema is invalid")
    resources = catalog.get("resources")
    if not isinstance(resources, list) or len(resources) != EXPECTED_RESOURCES:
        raise ProfileError("R52 resource route count is invalid")

    ids: set[str] = set()
    routes: set[str] = set()
    sources: set[str] = set()
    payload_manifest = hashlib.sha256()
    payload_manifest_rows: list[tuple[str, int, str]] = []
    payload_bytes = 0
    expected_payload_names: set[str] = set()
    for row in resources:
        if not isinstance(row, dict):
            raise ProfileError("R52 resource row is invalid")
        rid = row.get("id")
        route = row.get("requestPath")
        source = row.get("sourcePath")
        digest = row.get("sha256")
        logical_name = row.get("logicalName")
        if row.get("enabled") is not True or row.get("host") != HOST or \
                not isinstance(rid, str) or not rid or rid in ids or \
                not isinstance(logical_name, str) or not logical_name or \
                not isinstance(route, str) or not route.startswith(CDN_PREFIX) or \
                not route[len(CDN_PREFIX):] or not all(c.isascii() and (c.isalnum() or c in "-_.") for c in route[len(CDN_PREFIX):]) or \
                route in routes or not isinstance(source, str) or not source.startswith("payload-r7/") or \
                source in sources or not SHA_RE.fullmatch(digest or ""):
            raise ProfileError("R52 resource route or metadata is not unique and constrained")
        _safe_relative(source)
        source_path = profile_root / source
        if not source_path.is_file() or source_path.is_symlink():
            raise ProfileError("R52 payload file is missing or a symbolic link")
        size = source_path.stat().st_size
        actual_hash = _sha256_file(source_path)
        if actual_hash != digest:
            raise ProfileError("R52 payload checksum mismatch")
        payload_bytes += size
        if payload_bytes > MAX_PAYLOAD_BYTES:
            raise ProfileError("R52 payload total exceeds the supported bound")
        payload_manifest_rows.append((source, size, actual_hash))
        ids.add(rid)
        routes.add(route)
        sources.add(source)
        expected_payload_names.add(source[len("payload-r7/"):])
    for source, size, digest in sorted(payload_manifest_rows):
        payload_manifest.update(f"{source}\0{size}\0{digest}\n".encode())
    if payload_bytes != EXPECTED_PAYLOAD_BYTES or payload_bytes != report["payloadBytes"] or \
            len(sources) != EXPECTED_RESOURCES or payload_manifest.hexdigest() != report["payloadSetSha256"]:
        raise ProfileError("R52 payload count, total size, or set checksum mismatch")
    actual_payload_names = set()
    for path in payload_dir.iterdir():
        if path.is_symlink() or not path.is_file():
            raise ProfileError("R52 payload directory contains a non-regular entry")
        actual_payload_names.add(path.name)
    if actual_payload_names != expected_payload_names:
        raise ProfileError("R52 payload directory contains missing or extra files")

    if set(p.name for p in fixture_dir.iterdir()) != FIXTURE_NAMES or any(
        p.is_symlink() or not p.is_file() for p in fixture_dir.iterdir()
    ):
        raise ProfileError("R52 fixture directory contains missing or extra files")
    fixture_manifest = hashlib.sha256()
    fixture_hashes = report["fixtureSha256"]
    for name in sorted(FIXTURE_NAMES):
        path = fixture_dir / name
        digest = _sha256_file(path)
        if digest != fixture_hashes.get(name):
            raise ProfileError("R52 fixture checksum mismatch")
        fixture_manifest.update(f"{name}\0{digest}\n".encode())
        revision = REVISION if name == f"resource-list-{ASSET_VERSION}.json" else int(name.rsplit("-", 1)[1].removesuffix(".json"))
        _validate_fixture(path, revision)
    if fixture_manifest.hexdigest() != report["fixtureSetSha256"]:
        raise ProfileError("R52 fixture set checksum mismatch")

    actual_top = {p.name for p in profile_root.iterdir()}
    expected_top = {"profile-report.json", "title-minimum.json", "catalog.json", "fixtures", "payload-r7"}
    if allow_review_docs:
        expected_top |= {"README.md", "rollback.md"}
    if actual_top != expected_top or any(p.is_symlink() for p in profile_root.iterdir()):
        raise ProfileError("R52 profile has missing or unexpected top-level entries")
    if not allow_review_docs and report.get("sourceProfile") != PROFILE_SOURCE:
        raise ProfileError("R52 profile report contains an unsanitized source path")
    return {"payloadCount": len(resources), "payloadBytes": payload_bytes, "fixtureCount": len(FIXTURE_NAMES)}


def _profile_members(root: Path, report_bytes: bytes) -> list[tuple[str, Path | bytes]]:
    report = _load_json(root / "profile-report.json")
    catalog = _load_json(root / "catalog.json")
    expected = _expected_files(catalog["resources"])
    members: list[tuple[str, Path | bytes]] = []
    for name in sorted(expected):
        members.append((name, report_bytes if name == "profile-report.json" else root / name))
    return members


def pack_profile(source: Path, output: Path) -> str:
    if output.exists():
        raise ProfileError("refusing to replace an existing R52 archive")
    _validate_tree(source, allow_review_docs=True, require_sanitized_source=False)
    report = _load_json(source / "profile-report.json")
    report["sourceProfile"] = PROFILE_SOURCE
    report_bytes = (json.dumps(report, ensure_ascii=False, indent=2) + "\n").encode()
    output.parent.mkdir(parents=True, exist_ok=True)
    temp_path = output.with_name(f".{output.name}.{os.getpid()}.tmp")
    try:
        with temp_path.open("xb") as raw:
            with gzip.GzipFile(filename="", mode="wb", compresslevel=1, fileobj=raw, mtime=0) as gz:
                with tarfile.open(fileobj=gz, mode="w", format=tarfile.PAX_FORMAT) as archive:
                    for member_name, source_entry in _profile_members(source, report_bytes):
                        _safe_relative(member_name)
                        info = tarfile.TarInfo(member_name)
                        info.uid = info.gid = 0
                        info.uname = info.gname = ""
                        info.mode = 0o644
                        info.mtime = 0
                        if isinstance(source_entry, bytes):
                            info.size = len(source_entry)
                            archive.addfile(info, io.BytesIO(source_entry))
                        else:
                            info.size = source_entry.stat().st_size
                            with source_entry.open("rb") as stream:
                                archive.addfile(info, stream)
        if temp_path.stat().st_size > MAX_ARCHIVE_BYTES:
            raise ProfileError("R52 archive exceeds the per-asset release limit")
        os.rename(temp_path, output)
    except Exception:
        temp_path.unlink(missing_ok=True)
        output.unlink(missing_ok=True)
        raise
    return _sha256_file(output)


def _extract_archive(archive_path: Path, destination: Path) -> None:
    seen: set[str] = set()
    count = 0
    total = 0
    with tarfile.open(archive_path, mode="r:gz") as archive:
        for member in archive:
            count += 1
            if count > MAX_ARCHIVE_MEMBERS:
                raise ProfileError("R52 archive contains too many members")
            name = _safe_relative(member.name)
            if name in seen or not member.isfile() or member.issparse() or member.size < 0 or member.size > MAX_MEMBER_BYTES:
                raise ProfileError("R52 archive contains a duplicate or unsafe member")
            seen.add(name)
            total += member.size
            if total > MAX_PAYLOAD_BYTES + 50_000_000:
                raise ProfileError("R52 archive expands beyond the supported bound")
            target = destination.joinpath(*PurePosixPath(name).parts)
            target.parent.mkdir(parents=True, exist_ok=True)
            source = archive.extractfile(member)
            if source is None:
                raise ProfileError("R52 archive member could not be read")
            with source, target.open("xb") as output:
                shutil.copyfileobj(source, output, length=1024 * 1024)
                output.flush()
                os.fsync(output.fileno())


def validate_archive(archive_path: Path, expected_sha: str) -> dict[str, Any]:
    if not SHA_RE.fullmatch(expected_sha):
        raise ProfileError("expected archive SHA-256 is invalid")
    size = archive_path.stat().st_size
    if size <= 0 or size > MAX_ARCHIVE_BYTES or _sha256_file(archive_path) != expected_sha:
        raise ProfileError("R52 archive size or trusted checksum is invalid")
    with tempfile.TemporaryDirectory(prefix="kf-r52-validate-") as temp:
        profile = Path(temp) / "profile"
        profile.mkdir()
        _extract_archive(archive_path, profile)
        summary = _validate_tree(profile, allow_review_docs=False, require_sanitized_source=True)
    return {**summary, "archiveBytes": size, "archiveSha256": expected_sha}


def stage_archive(archive_path: Path, expected_sha: str, stage_root: Path) -> str:
    summary = validate_archive(archive_path, expected_sha)
    if stage_root.exists() and (stage_root.is_symlink() or not stage_root.is_dir()):
        raise ProfileError("R52 stage root is not a real directory")
    stage_root.mkdir(parents=True, exist_ok=True)
    target = stage_root / STAGE_NAME
    marker_name = "stage-manifest.json"
    marker = {"schema": "kickflight.ios-r52-stage.v1", "archiveSha256": expected_sha, "revisionFloor": REVISION}

    if target.exists():
        verify_stage(stage_root, expected_sha)
        return "already-staged"

    temporary = Path(tempfile.mkdtemp(prefix=f".{STAGE_NAME}.stage-", dir=stage_root))
    try:
        profile = temporary / "profile"
        profile.mkdir()
        _extract_archive(archive_path, profile)
        _validate_tree(profile, allow_review_docs=False, require_sanitized_source=True)
        (temporary / marker_name).write_text(json.dumps(marker, sort_keys=True) + "\n", encoding="utf-8")
        with (temporary / marker_name).open("rb") as marker_file:
            os.fsync(marker_file.fileno())
        _freeze_tree(temporary)
        os.rename(temporary, target)
        return "staged"
    except Exception:
        shutil.rmtree(temporary, ignore_errors=True)
        raise


def _freeze_tree(root: Path) -> None:
    for path in sorted(root.rglob("*"), key=lambda item: len(item.parts), reverse=True):
        if path.is_symlink():
            raise ProfileError("R52 stage contains a symbolic link")
        path.chmod(0o555 if path.is_dir() else 0o444)
    root.chmod(0o555)


def verify_stage(stage_root: Path, expected_sha: str) -> dict[str, Any]:
    if not SHA_RE.fullmatch(expected_sha):
        raise ProfileError("expected R52 archive SHA-256 is invalid")
    if stage_root.is_symlink() or not stage_root.is_dir():
        raise ProfileError("R52 stage root is missing or is a symbolic link")
    target = stage_root / STAGE_NAME
    if target.is_symlink() or not target.is_dir():
        raise ProfileError("R52 stage directory is missing or is a symbolic link")
    marker_path = target / "stage-manifest.json"
    if marker_path.is_symlink() or not marker_path.is_file():
        raise ProfileError("R52 stage manifest is missing or is a symbolic link")
    expected_marker = {"schema": "kickflight.ios-r52-stage.v1", "archiveSha256": expected_sha,
                       "revisionFloor": REVISION}
    if _load_json(marker_path) != expected_marker:
        raise ProfileError("R52 stage manifest does not match the pinned archive")
    profile = target / "profile"
    summary = _validate_tree(profile, allow_review_docs=False, require_sanitized_source=True)
    for path in (target, marker_path, profile, *profile.rglob("*")):
        if path.is_symlink() or path.stat().st_mode & 0o222:
            raise ProfileError("R52 stage is writable; refusing to trust cached payload hashes")
    if {path.name for path in target.iterdir()} != {"profile", "stage-manifest.json"}:
        raise ProfileError("R52 stage contains unexpected files")
    return summary


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    pack = commands.add_parser("pack", help="create a sanitized deterministic R52 release asset")
    pack.add_argument("--profile", type=Path, required=True)
    pack.add_argument("--output", type=Path, required=True)
    validate = commands.add_parser("validate", help="verify the trusted R52 archive without staging it")
    validate.add_argument("--archive", type=Path, required=True)
    validate.add_argument("--archive-sha256", required=True)
    stage = commands.add_parser("stage", help="atomically stage an immutable R52 archive")
    stage.add_argument("--archive", type=Path, required=True)
    stage.add_argument("--archive-sha256", required=True)
    stage.add_argument("--stage-root", type=Path, required=True)
    verify = commands.add_parser("verify-stage", help="verify an immutable staged R52 profile")
    verify.add_argument("--stage-root", type=Path, required=True)
    verify.add_argument("--archive-sha256", required=True)
    args = parser.parse_args()
    try:
        if args.command == "pack":
            digest = pack_profile(args.profile, args.output)
            print(json.dumps({"status": "packed", "archiveSha256": digest, "archiveBytes": args.output.stat().st_size}))
        elif args.command == "validate":
            print(json.dumps({"status": "valid", **validate_archive(args.archive, args.archive_sha256)}))
        elif args.command == "stage":
            result = stage_archive(args.archive, args.archive_sha256, args.stage_root)
            print(json.dumps({"status": result, "archiveSha256": args.archive_sha256,
                              "revisionFloor": REVISION, "payloadCount": EXPECTED_RESOURCES,
                              "payloadBytes": EXPECTED_PAYLOAD_BYTES}))
        else:
            summary = verify_stage(args.stage_root, args.archive_sha256)
            print(json.dumps({"status": "immutable-stage-valid", "archiveSha256": args.archive_sha256,
                              "revisionFloor": REVISION, **summary}))
    except (OSError, ProfileError, tarfile.TarError, json.JSONDecodeError) as exc:
        print(f"R52 profile operation failed: {type(exc).__name__}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
