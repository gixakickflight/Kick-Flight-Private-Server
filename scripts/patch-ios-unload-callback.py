#!/usr/bin/env python3
"""Apply the verified one-instruction iOS AssetBundle unload callback fix.

This tool only patches the thin arm64 Kick Flight Mach-O executable. It never
edits an IPA, signs an app, installs a build, or interacts with a running app.
The accepted executable hashes are intentionally pinned to the inspected local
batch26 deliveries; update the allowlist only after repeating static review.
"""

from __future__ import annotations

import argparse
import hashlib
import os
import shutil
import stat
import struct
import sys
import tempfile
import uuid
from dataclasses import dataclass
from pathlib import Path


EXPECTED_UUID = uuid.UUID("F800F44E-B06D-3ABC-96F2-FEDC716100AD")
EXPECTED_CPU_TYPE = 0x0100000C  # CPU_TYPE_ARM64
EXPECTED_TEXT_VMADDR = 0x100000000
PATCH_RVA = 0x1D097F8
PATCH_FILE_OFFSET = 0x1D097F8  # __TEXT vmaddr=0x100000000 and fileoff=0
EXPECTED_BYTES = bytes.fromhex("a0000034")  # cbz w0, 0x1d0980c
REPLACEMENT_BYTES = bytes.fromhex("a0000035")  # cbnz w0, 0x1d0980c

# SHA-256 hashes of the executable member (not the enclosing IPA), as inspected
# from the two local pristine batch26 delivery variants.
PRISTINE_TO_PATCHED_SHA256 = {
    "93663acb757ea683dcda18b0a642df1ce324264b52f099bbca269788e02336ab":
    "cf1d2114003855d7b438f3ec39e600253b5157e4a18168b916f247a60fd33a37",
    "0ad347126964a15f0e38e8ec60a1c912db6f35d5a2bd5bccf6b199dd172f62e4":
    "cd12a00e24a1dfa7dbf9c340eb9150a82753d4583c7e6d3a14a482ee4d93059e",
    # Original executable backed up before the successful installed A/B run.
    "d5ab2bc39464ef3ba27fdc04eb2a2463d43645cfce19f4e45b5d70cb30429a71":
    "ba8b994891f70dfa06e9e6bc510e9eb1f7b1c485390e3fb3369cd53dceae6e93",
}

LC_SEGMENT_64 = 0x19
LC_UUID = 0x1B
MH_MAGIC_64 = 0xFEEDFACF
MH_EXECUTE = 0x2


class PatchError(Exception):
    """Raised when an executable does not match the reviewed patch target."""


@dataclass(frozen=True)
class MachOIdentity:
    uuid: uuid.UUID
    cpu_type: int
    file_type: int
    text_vmaddr: int
    text_fileoff: int
    text_filesize: int


def inspect_macho(data: bytes) -> MachOIdentity:
    """Parse enough load commands to verify the expected arm64 Mach-O image."""
    if len(data) < 32:
        raise PatchError("file is too short to be a 64-bit Mach-O")
    magic, cpu_type, _cpu_subtype, file_type, ncmds, sizeofcmds, _flags, _reserved = struct.unpack_from(
        "<IiiIIIII", data, 0
    )
    if magic != MH_MAGIC_64:
        raise PatchError("expected a little-endian thin Mach-O 64-bit executable")
    if cpu_type != EXPECTED_CPU_TYPE:
        raise PatchError(f"expected arm64 CPU type 0x{EXPECTED_CPU_TYPE:x}, got 0x{cpu_type:x}")
    if file_type != MH_EXECUTE:
        raise PatchError(f"expected MH_EXECUTE, got file type {file_type}")
    commands_end = 32 + sizeofcmds
    if commands_end > len(data):
        raise PatchError("Mach-O load commands extend past end of file")

    image_uuid: uuid.UUID | None = None
    text: tuple[int, int, int] | None = None
    cursor = 32
    for _ in range(ncmds):
        if cursor + 8 > commands_end:
            raise PatchError("truncated Mach-O load command")
        command, command_size = struct.unpack_from("<II", data, cursor)
        if command_size < 8 or cursor + command_size > commands_end:
            raise PatchError("invalid Mach-O load command size")
        if command == LC_UUID:
            if command_size < 24:
                raise PatchError("truncated LC_UUID command")
            image_uuid = uuid.UUID(bytes=data[cursor + 8 : cursor + 24])
        elif command == LC_SEGMENT_64:
            if command_size < 72:
                raise PatchError("truncated LC_SEGMENT_64 command")
            segment_name = data[cursor + 8 : cursor + 24].split(b"\0", 1)[0]
            if segment_name == b"__TEXT":
                vmaddr, _vmsize, fileoff, filesize = struct.unpack_from("<QQQQ", data, cursor + 24)
                text = (vmaddr, fileoff, filesize)
        cursor += command_size

    if image_uuid is None:
        raise PatchError("Mach-O has no LC_UUID")
    if text is None:
        raise PatchError("Mach-O has no __TEXT segment")
    return MachOIdentity(image_uuid, cpu_type, file_type, *text)


def _hash(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def verify_identity(
    data: bytes,
    *,
    expected_uuid: uuid.UUID = EXPECTED_UUID,
    patch_offset: int = PATCH_FILE_OFFSET,
) -> MachOIdentity:
    identity = inspect_macho(data)
    if identity.uuid != expected_uuid:
        raise PatchError(f"UUID mismatch: expected {expected_uuid}, got {identity.uuid}")
    if identity.text_vmaddr != EXPECTED_TEXT_VMADDR or identity.text_fileoff != 0:
        raise PatchError(
            "unexpected __TEXT mapping: expected vmaddr 0x100000000/fileoff 0, "
            f"got vmaddr 0x{identity.text_vmaddr:x}/fileoff 0x{identity.text_fileoff:x}"
        )
    if patch_offset + len(EXPECTED_BYTES) > identity.text_filesize:
        raise PatchError("patch file offset is outside the __TEXT segment")
    if patch_offset + len(EXPECTED_BYTES) > len(data):
        raise PatchError("patch file offset is outside the executable")
    return identity


def patch_bytes(
    data: bytes,
    *,
    pristine_to_patched: dict[str, str] = PRISTINE_TO_PATCHED_SHA256,
    patch_offset: int = PATCH_FILE_OFFSET,
    expected_uuid: uuid.UUID = EXPECTED_UUID,
) -> tuple[bytes, str]:
    """Validate and patch bytes, or return an already-patched approved image."""
    verify_identity(data, expected_uuid=expected_uuid, patch_offset=patch_offset)
    source_hash = _hash(data)
    approved_output_hashes = set(pristine_to_patched.values())

    if source_hash in pristine_to_patched:
        if data[patch_offset : patch_offset + len(EXPECTED_BYTES)] != EXPECTED_BYTES:
            raise PatchError(
                f"instruction guard failed at file offset 0x{patch_offset:x}: "
                f"found {data[patch_offset:patch_offset + len(EXPECTED_BYTES)].hex()}"
            )
        patched = bytearray(data)
        patched[patch_offset : patch_offset + len(REPLACEMENT_BYTES)] = REPLACEMENT_BYTES
        result = bytes(patched)
        result_hash = _hash(result)
        expected_result_hash = pristine_to_patched[source_hash]
        if result_hash != expected_result_hash:
            raise PatchError(
                f"patched SHA-256 mismatch: expected {expected_result_hash}, got {result_hash}"
            )
        return result, "patch"

    if source_hash in approved_output_hashes:
        if data[patch_offset : patch_offset + len(REPLACEMENT_BYTES)] != REPLACEMENT_BYTES:
            raise PatchError(
                f"patched-image guard failed at file offset 0x{patch_offset:x}: "
                f"found {data[patch_offset:patch_offset + len(REPLACEMENT_BYTES)].hex()}"
            )
        return data, "already-patched"

    raise PatchError(f"unapproved executable SHA-256: {source_hash}")


def _atomic_write(path: Path, data: bytes, mode: int) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temp_name = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=path.parent)
    temp_path = Path(temp_name)
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temp_path, stat.S_IMODE(mode))
        os.replace(temp_path, path)
    except BaseException:
        try:
            temp_path.unlink()
        except FileNotFoundError:
            pass
        raise


def _write_backup(backup: Path, original: bytes, source_mode: int) -> None:
    if backup.exists():
        if backup.read_bytes() != original:
            raise PatchError(f"backup already exists and does not match source: {backup}")
        return
    _atomic_write(backup, original, source_mode)


def process_file(
    source: Path,
    *,
    output: Path | None = None,
    in_place: bool = False,
    dry_run: bool = False,
    pristine_to_patched: dict[str, str] = PRISTINE_TO_PATCHED_SHA256,
    patch_offset: int = PATCH_FILE_OFFSET,
    expected_uuid: uuid.UUID = EXPECTED_UUID,
) -> str:
    """Patch to a distinct output by default; support explicit backed-up in-place mode."""
    source = source.resolve()
    if in_place and output is not None:
        raise PatchError("choose either --output or --in-place")
    if not in_place and output is None:
        raise PatchError("specify --output or explicitly request --in-place")
    if output is not None:
        output = output.resolve()
        if output == source:
            raise PatchError("output equals input; use --in-place to require a backup")

    original = source.read_bytes()
    patched, action = patch_bytes(
        original,
        pristine_to_patched=pristine_to_patched,
        patch_offset=patch_offset,
        expected_uuid=expected_uuid,
    )
    if dry_run:
        return action

    mode = source.stat().st_mode
    if in_place:
        backup = source.with_name(source.name + ".ios-unload-callback.bak")
        if action == "already-patched":
            if not backup.exists():
                raise PatchError(f"already-patched input has no reversible backup: {backup}")
            backup_data = backup.read_bytes()
            expected_backup_hash = _hash(backup_data)
            if pristine_to_patched.get(expected_backup_hash) != _hash(original):
                raise PatchError("in-place backup is not a matching approved pristine executable")
            return action
        _write_backup(backup, original, mode)
        _atomic_write(source, patched, mode)
        return action

    assert output is not None
    backup = output.with_name(output.name + ".ios-unload-callback.bak")
    if output.exists():
        existing = output.read_bytes()
        expected_existing, existing_action = patch_bytes(
            existing,
            pristine_to_patched=pristine_to_patched,
            patch_offset=patch_offset,
            expected_uuid=expected_uuid,
        )
        if existing_action != "already-patched" or expected_existing != existing:
            raise PatchError(f"output exists but is not the expected patched executable: {output}")
        if not backup.exists() or backup.read_bytes() != original:
            raise PatchError(f"existing output is missing its matching source backup: {backup}")
        return "already-patched"
    if action != "patch":
        raise PatchError("output mode requires an approved pristine input executable")
    _write_backup(backup, original, mode)
    _atomic_write(output, patched, mode)
    return action


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, type=Path, help="inspected thin arm64 kickflight executable")
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--output", type=Path, help="write patched executable and a sibling .bak source copy")
    mode.add_argument("--in-place", action="store_true", help="patch input after creating a sibling .bak copy")
    parser.add_argument("--dry-run", action="store_true", help="validate guards and report without writing")
    args = parser.parse_args(argv)
    try:
        action = process_file(args.input, output=args.output, in_place=args.in_place, dry_run=args.dry_run)
    except (OSError, PatchError) as exc:
        print(f"patch rejected: {exc}", file=sys.stderr)
        return 2
    if args.dry_run:
        print(f"dry-run validated UUID, SHA-256, Mach-O mapping, and instruction guard: {action}")
    else:
        print(f"{action}: callback branch at RVA/file offset 0x{PATCH_RVA:x}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
