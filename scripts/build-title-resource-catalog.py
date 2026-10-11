#!/usr/bin/env python3
"""Build the minimal Octo database and local CDN routes for the title screen."""

from __future__ import annotations

import argparse
import base64
import binascii
import hashlib
import json
import subprocess
import sys
from pathlib import Path
from urllib.parse import urlparse


def write_if_changed(path: Path, payload: bytes) -> bool:
    """Avoid touching generated manifests/fixtures when their bytes are stable."""
    if path.is_file() and path.read_bytes() == payload:
        return False
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(payload)
    return True


def encode_varint(value: int) -> bytes:
    if value < 0:
        raise ValueError("Only non-negative protobuf varints are supported")
    output = bytearray()
    while value > 0x7F:
        output.append((value & 0x7F) | 0x80)
        value >>= 7
    output.append(value)
    return bytes(output)


def field_varint(number: int, value: int) -> bytes:
    return encode_varint(number << 3) + encode_varint(value)


def field_bytes(number: int, value: bytes) -> bytes:
    return encode_varint((number << 3) | 2) + encode_varint(len(value)) + value


def field_string(number: int, value: str) -> bytes:
    return field_bytes(number, value.encode("utf-8"))


def read_varint(payload: bytes, offset: int) -> tuple[int, int]:
    value = 0
    shift = 0
    while offset < len(payload):
        byte = payload[offset]
        offset += 1
        value |= (byte & 0x7F) << shift
        if byte < 0x80:
            return value, offset
        shift += 7
        if shift > 63:
            break
    raise ValueError("Invalid protobuf varint")


def validate_data_message(payload: bytes) -> None:
    """Guard the recovered Octo.Proto.Data field/wire contract."""
    fields: list[tuple[int, int]] = []
    offset = 0
    while offset < len(payload):
        tag, offset = read_varint(payload, offset)
        number, wire_type = tag >> 3, tag & 7
        fields.append((number, wire_type))
        if wire_type == 0:
            _, offset = read_varint(payload, offset)
        elif wire_type == 2:
            length, offset = read_varint(payload, offset)
            offset += length
        else:
            raise ValueError(f"Unexpected wire type {wire_type} for Data field {number}")
    expected = [
        (1, 0),
        (2, 2),
        (3, 2),
        (4, 0),
        (5, 0),
        (7, 0),
        (9, 0),
        (10, 2),
        (11, 2),
        (12, 0),
        (13, 0),
    ]
    if fields != expected:
        raise ValueError(f"Unexpected Octo.Proto.Data layout: {fields!r}")


def with_data_state(payload: bytes, state: int) -> bytes:
    """Replace Octo.Proto.Data.state while preserving all other encoded fields."""
    output = bytearray()
    offset = 0
    copied_through = 0
    replaced = False
    while offset < len(payload):
        field_start = offset
        tag, offset = read_varint(payload, offset)
        number, wire_type = tag >> 3, tag & 7
        if wire_type == 0:
            _, offset = read_varint(payload, offset)
        elif wire_type == 2:
            length, offset = read_varint(payload, offset)
            offset += length
        else:
            raise ValueError(f"Unexpected wire type {wire_type} for Data field {number}")
        if number == 9:
            if wire_type != 0 or replaced:
                raise ValueError("Expected exactly one varint Octo.Proto.Data.state field")
            output.extend(payload[copied_through:field_start])
            output.extend(field_varint(9, state))
            copied_through = offset
            replaced = True
    if not replaced:
        raise ValueError("Octo.Proto.Data.state field is missing")
    output.extend(payload[copied_through:])
    return bytes(output)


# The client's initial download (DownloadScene -> ColorfulManager.GetUpdateResourceNames) only fetches the rows
# tagged "common", "title" (+ "tutorial", "localize"); everything else is loaded on demand from the cache and a
# miss is a native crash. Tag every row "common" so a fresh install downloads the whole catalogue up front and
# no scripts/seed-device-cache.py is needed. tagid is 1-based into Database.tagname (Octo.Data.Item.SetData:
# tags[tagid - 1]) and the names must be unique: Octo keys them in a dictionary and a duplicate throws
# ArgumentException in DataManager.SerializeDatabase, leaving the client database half-applied.
DOWNLOAD_TAG_NAMES = ["common"]
DOWNLOAD_TAG_ID = 1


# The Octo client rejects the Chinese-localised bundles on a non-zh device: each `ui/localize/zh/*` file is
# re-requested and the start-up download then aborts with "communication error".
# Neither leaving them untagged nor publishing them with State DELETE kept the
# DownloadScene from queueing them, so they are simply not part of the served database any more (the files stay in
# catalog.json for direct /cdn requests). The es/en client never asks for zh assets anyway.
def is_removed(name: str) -> bool:
    return name.startswith("ui/localize/zh/")


# Octo names a cache file after the item's MD5 (Octo.GetAssetBundleFileName / GetResourceFileName ->
# Item.md5.ToString()), so the alias rows of an entry (same bytes under another name) share one storage file with
# the primary row. The client walks the database rows in list order, 4 downloads at a time (patched
# MaxParallelDownload); a row whose storage file is already complete is skipped instantly, but a row whose twin is
# still in flight (or completed milliseconds ago and not yet registered) is downloaded again, collides with it
# (LockStorage), is retried once and then fails the whole DownloadScene with "communication error".
# Distance in rows means nothing because cache-skips take no time, so the layout separates twins by real
# downloads instead:
#   1. primaries of entries that have aliases, largest first (they are certain to be complete long before the tail),
#   2. primaries of alias-free entries, in definition order (a buffer of genuine downloads),
#   3. all alias rows (round-robin over the entries) - by now every one of them is a cache hit and is skipped.
# The first surviving name of an entry is its primary (zh names are removed, see is_removed).
def order_rows(rows: list[tuple[int, int, str, int, bytes]]) -> list[bytes]:
    """rows = (entry index, alias index among the emitted names, md5, size, message)."""
    entries_with_aliases = {entry_index for entry_index, alias_index, _, _, _ in rows if alias_index > 0}
    primaries = [row for row in rows if row[1] == 0]
    aliased_primaries = sorted(
        (row for row in primaries if row[0] in entries_with_aliases), key=lambda row: (-row[3], row[0])
    )
    plain_primaries = [row for row in primaries if row[0] not in entries_with_aliases]
    aliases = sorted((row for row in rows if row[1] > 0), key=lambda row: (row[1], row[0]))
    ordered = aliased_primaries + plain_primaries + aliases
    first_seen: dict[str, int] = {}
    for index, (_, alias_index, md5, _, _) in enumerate(ordered):
        if alias_index == 0 and md5 in first_seen:
            raise ValueError(f"two primary entries share content {md5}; make one an alias of the other")
        first_seen.setdefault(md5, index)
    return [message for _, _, _, _, message in ordered]


def resolve_path(repo: Path, configured_path: str) -> Path:
    candidate = Path(configured_path)
    return candidate.resolve() if candidate.is_absolute() else (repo / candidate).resolve()


def _unitypy_interpreter(repo: Path) -> Path:
    """Select a Python that has the pinned bundle codec dependency."""
    candidates = [Path(sys.executable)]
    candidates.extend((
        repo / ".local/assets-venv/bin/python",
        repo / ".local/assets-venv/Scripts/python.exe",
    ))
    checked: set[str] = set()
    for candidate in candidates:
        # Preserve the venv symlink path: resolving it points at the base Python and drops venv site-packages.
        executable = str(candidate.absolute())
        if executable in checked or not candidate.is_file():
            continue
        checked.add(executable)
        result = subprocess.run(
            [executable, "-c", "import UnityPy; print(getattr(UnityPy, '__version__', 'unknown'))"],
            capture_output=True,
            text=True,
            check=False,
        )
        if result.returncode == 0 and result.stdout.strip() == "1.25.3":
            return Path(executable)
    raise SystemExit(
        "Physical weapon fallbacks require UnityPy 1.25.3. Install it in "
        ".local/assets-venv and run that environment's Python, or use the "
        "same interpreter that already imports UnityPy 1.25.3."
    )


def materialize_weapon_costume_fallbacks(repo: Path, definition_path: Path) -> None:
    """Generate weapon clones before the catalog reads title entries or clone sourcePaths.

    The generator never calls this catalog builder, so this one-way subprocess cannot recurse.
    Custom --definition builds remain isolated from the project's production title manifest.
    """
    default_definition = (repo / "config/resources/title-minimum.json").resolve()
    if definition_path.resolve() != default_definition:
        return
    generator = repo / "scripts/add_weapon_costume_aliases.py"
    if not generator.is_file():
        raise SystemExit(f"Weapon fallback generator is missing: {generator}")
    interpreter = _unitypy_interpreter(repo)
    result = subprocess.run([str(interpreter), str(generator)], cwd=repo, check=False)
    if result.returncode:
        raise SystemExit(
            "Could not generate physical weapon fallbacks before catalog build. "
            "Check UnityPy 1.25.3 and restore the captured donor bundles from "
            "the sibling Kick-Flight-Assets checkout."
        )


def materialize_kicker_skin_thumbnails(repo: Path, definition_path: Path) -> None:
    """Build the durable generated kicker bundles before resolving catalog sources."""
    default_definition = (repo / "config/resources/title-minimum.json").resolve()
    if definition_path.resolve() != default_definition:
        return
    generator = repo / "scripts/build-kicker-skin-thumbnail-bundles.py"
    if not generator.is_file():
        raise SystemExit(f"Kicker thumbnail generator is missing: {generator}")
    interpreter = _unitypy_interpreter(repo)
    result = subprocess.run([str(interpreter), str(generator)], cwd=repo, check=False)
    if result.returncode:
        raise SystemExit(
            "Could not generate kicker skin thumbnail bundles before catalog build. "
            "Check all 11 final PNGs and UnityPy 1.25.3."
        )


def materialize_localize_bundles(repo: Path, definition_path: Path) -> None:
    """Build the durable per-language localize bundles before the catalog reads title entries.

    The client maps its saved language id to en/es/ja/pt/ko/fr/de and looks each language up by name in the
    Octo database; a shared zh bundle under alias names makes two CABs collide and a missing language name
    fails the load silently (native NRE). The generator never calls this catalog builder, so the one-way
    subprocess cannot recurse. Custom --definition builds stay isolated from the production manifest.
    """
    default_definition = (repo / "config/resources/title-minimum.json").resolve()
    if definition_path.resolve() != default_definition:
        return
    generator = repo / "scripts/build-localize-bundles.py"
    if not generator.is_file():
        raise SystemExit(f"Localize bundle generator is missing: {generator}")
    interpreter = _unitypy_interpreter(repo)
    result = subprocess.run([str(interpreter), str(generator)], cwd=repo, check=False)
    if result.returncode:
        raise SystemExit(
            "Could not generate physical per-language localize bundles before catalog build. "
            "Check the captured donor bundles and UnityPy 1.25.3."
        )


def encode_data(*, octo_id: int, name: str, object_name: str, source: bytes) -> bytes:
    # Tags recovered from the native IL2CPP custom-attribute generators. They
    # deliberately differ from property declaration order: priority=6,
    # tagid=7, dependencie=8, state=9, md5=10, objectName=11,
    # generation=12, uploadVersionId=13.
    return b"".join(
        (
            field_varint(1, octo_id),
            field_string(2, name),
            field_string(3, name),
            field_varint(4, len(source)),
            field_varint(5, binascii.crc32(source) & 0xFFFFFFFF),
            field_varint(7, DOWNLOAD_TAG_ID),  # tagid -> Database.tagname ("common")
            field_varint(9, 1),  # state = ADD
            field_string(10, hashlib.md5(source).hexdigest()),
            field_string(11, object_name),
            field_varint(12, 1),
            field_varint(13, 1),
        )
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--definition", default="config/resources/title-minimum.json")
    parser.add_argument("--direct-config", default="config/apk-direct-server.local.json")
    parser.add_argument("--server-host-config", default="config/server-host.json")
    parser.add_argument(
        "--server-base-url",
        help="Override serverBaseUrl (useful for the first-party proxy mode)",
    )
    parser.add_argument("--catalog", default="config/resources/catalog.json")
    parser.add_argument("--fixtures", default="config/fixtures")
    args = parser.parse_args()

    repo = Path(__file__).resolve().parent.parent
    definition_path = resolve_path(repo, args.definition)
    direct_config_path = resolve_path(repo, args.direct_config)
    server_host_path = resolve_path(repo, args.server_host_config)
    catalog_path = resolve_path(repo, args.catalog)
    fixtures_path = resolve_path(repo, args.fixtures)
    materialize_weapon_costume_fallbacks(repo, definition_path)
    materialize_kicker_skin_thumbnails(repo, definition_path)
    materialize_localize_bundles(repo, definition_path)
    definition = json.loads(definition_path.read_text(encoding="utf-8-sig"))

    if args.server_base_url:
        server_base_url = args.server_base_url.rstrip("/")
    elif server_host_path.exists():
        host_cfg = json.loads(server_host_path.read_text(encoding="utf-8-sig"))
        scheme = host_cfg.get("scheme", "http")
        host = host_cfg.get("host", "10.0.2.2")
        port = host_cfg.get("port", 18080)
        server_base_url = f"{scheme}://{host}:{port}".rstrip("/")
    else:
        direct_config = json.loads(direct_config_path.read_text(encoding="utf-8-sig"))
        server_base_url = direct_config["serverBaseUrl"].rstrip("/")

    parsed_url = urlparse(server_base_url)
    if parsed_url.scheme not in ("http", "https") or not parsed_url.netloc:
        raise ValueError(f"Invalid serverBaseUrl: {server_base_url}")
    url_format = server_base_url + definition["urlPathFormat"]

    asset_rows: list[tuple[int, int, str, int, bytes]] = []
    resource_rows: list[tuple[int, int, str, int, bytes]] = []
    seen_names: set[str] = set()
    managed_catalog_entries: list[dict[str, object]] = []
    reserved_octo_ids = {int(entry["octoId"]) for entry in definition["entries"]}
    if len(reserved_octo_ids) != len(definition["entries"]):
        raise ValueError("title-minimum entries must have unique primary octoId values")
    used_octo_ids: set[int] = set()
    next_alias_id = max(reserved_octo_ids, default=0) + 1

    for entry_index, entry in enumerate(definition["entries"]):
        source_path = resolve_path(repo, entry["sourcePath"])
        source = source_path.read_bytes()
        sha256 = hashlib.sha256(source).hexdigest()
        md5 = hashlib.md5(source).hexdigest()
        emitted = 0
        for alias_index, name in enumerate(entry["names"]):
            if is_removed(name):
                continue
            if name in seen_names:  # e.g. common_se.awb is listed twice on the placeholder wave bank
                continue
            seen_names.add(name)
            if alias_index == 0:
                octo_id = int(entry["octoId"])
            else:
                while next_alias_id in reserved_octo_ids or next_alias_id in used_octo_ids:
                    next_alias_id += 1
                octo_id = next_alias_id
                next_alias_id += 1
            if octo_id in used_octo_ids:
                raise ValueError(f"duplicate Octo id {octo_id} for resource {name}")
            used_octo_ids.add(octo_id)
            message = encode_data(
                octo_id=octo_id,
                name=name,
                object_name=entry["objectName"],
                source=source,
            )
            validate_data_message(message)
            (asset_rows if entry["kind"] == "assetBundle" else resource_rows).append(
                (entry_index, emitted, md5, len(source), message)
            )
            emitted += 1

        req_path = definition["urlPathFormat"].replace("{o}", entry["objectName"])
        if not any(m["requestPath"] == req_path for m in managed_catalog_entries):
            managed_catalog_entries.append(
                {
                    "id": entry["id"],
                    "enabled": True,
                    "host": parsed_url.hostname,
                    "requestPath": req_path,
                    "logicalName": entry["logicalName"],
                    "description": entry["description"],
                    "sourcePath": Path(entry["sourcePath"]).as_posix(),
                    "contentType": "application/octet-stream",
                    "sha256": sha256,
                }
            )

    asset_messages = order_rows(asset_rows)
    resource_messages = order_rows(resource_rows)

    fixtures_path.mkdir(parents=True, exist_ok=True)
    fixture_paths = []
    for from_revision in definition.get("fromRevisions", [0]):
        database = field_varint(1, definition["revision"])
        if from_revision < definition["revision"]:
            # A fresh Octo database needs ADD. For any existing database, UPDATE
            # both replaces same-name rows (including their id) and inserts
            # names absent from that cache. Send the complete manifest so old
            # caches converge regardless of which earlier revision they had.
            state = 1 if from_revision == 0 else 2
            database += b"".join(field_bytes(2, with_data_state(item, state)) for item in asset_messages)
            database += b"".join(field_string(3, tag) for tag in DOWNLOAD_TAG_NAMES)
            database += b"".join(field_bytes(4, with_data_state(item, state)) for item in resource_messages)
        # Octo calls SetUrls for every successfully decoded database, including
        # an up-to-date response with no asset delta. Omitting this field makes
        # the client overwrite its working CDN formats with an empty string.
        database += field_string(5, url_format)
        fixture_id = (
            f'accepted-octo-title-minimum-{definition["assetVersion"]}-from-{from_revision}'
        )
        fixture = {
            "id": fixture_id,
            "enabled": True,
            "host": "kickflight-resource-api.grenge.jp",
            "method": "GET",
            "path": f'/v1/list/{definition["assetVersion"]}/{from_revision}',
            "statusCode": 200,
            "contentType": "application/x-protobuf",
            "headers": {"x-kickflight-fixture": fixture_id},
            "bodyBase64": base64.b64encode(database).decode("ascii"),
        }
        suffix = "" if from_revision == 0 else f'-from-{from_revision}'
        fixture_path = fixtures_path / f'resource-list-{definition["assetVersion"]}{suffix}.json'
        write_if_changed(fixture_path, (json.dumps(fixture, indent=2) + "\n").encode("utf-8"))
        fixture_paths.append(fixture_path)

    catalog = json.loads(catalog_path.read_text(encoding="utf-8-sig"))
    managed_paths = {entry["requestPath"] for entry in managed_catalog_entries}
    catalog["resources"] = [
        entry for entry in catalog.get("resources", []) if entry.get("requestPath") not in managed_paths
    ] + managed_catalog_entries
    write_if_changed(catalog_path, (json.dumps(catalog, indent=2, ensure_ascii=False) + "\n").encode("utf-8"))

    print(f"Octo revision: {definition['revision']}")
    print(f"URL format:    {url_format}")
    print(f"Fixtures:      {', '.join(str(path) for path in fixture_paths)}")
    print(f"CDN entries:   {len(managed_catalog_entries)} in {catalog_path}")


if __name__ == "__main__":
    main()
