from __future__ import annotations

import hashlib
import importlib.util
import struct
import sys
import tempfile
import unittest
import uuid
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "patch-ios-unload-callback.py"
SPEC = importlib.util.spec_from_file_location("patch_ios_unload_callback", SCRIPT)
assert SPEC and SPEC.loader
patcher = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = patcher
SPEC.loader.exec_module(patcher)


TEST_OFFSET = 0x100


def make_macho(image_uuid: uuid.UUID, instruction: bytes) -> bytes:
    size = TEST_OFFSET + 4
    blob = bytearray(size)
    sizeofcmds = 24 + 72
    struct.pack_into(
        "<IiiIIIII",
        blob,
        0,
        patcher.MH_MAGIC_64,
        patcher.EXPECTED_CPU_TYPE,
        0,
        patcher.MH_EXECUTE,
        2,
        sizeofcmds,
        0,
        0,
    )
    struct.pack_into("<II", blob, 32, patcher.LC_UUID, 24)
    blob[40:56] = image_uuid.bytes
    struct.pack_into(
        "<II16sQQQQIIII",
        blob,
        56,
        patcher.LC_SEGMENT_64,
        72,
        b"__TEXT",
        patcher.EXPECTED_TEXT_VMADDR,
        size,
        0,
        size,
        7,
        5,
        0,
        0,
    )
    blob[TEST_OFFSET : TEST_OFFSET + 4] = instruction
    return bytes(blob)


def make_hash_map(pristine: bytes) -> dict[str, str]:
    patched = bytearray(pristine)
    patched[TEST_OFFSET : TEST_OFFSET + 4] = patcher.REPLACEMENT_BYTES
    return {
        hashlib.sha256(pristine).hexdigest(): hashlib.sha256(patched).hexdigest(),
    }


class PatchIosUnloadCallbackTests(unittest.TestCase):
    def setUp(self) -> None:
        self.pristine = make_macho(patcher.EXPECTED_UUID, patcher.EXPECTED_BYTES)
        self.hashes = make_hash_map(self.pristine)

    def test_wrong_uuid_is_rejected_before_writing(self) -> None:
        foreign = make_macho(uuid.UUID("00000000-0000-0000-0000-000000000001"), patcher.EXPECTED_BYTES)
        allow = {hashlib.sha256(foreign).hexdigest(): "unused"}
        with self.assertRaisesRegex(patcher.PatchError, "UUID mismatch"):
            patcher.patch_bytes(foreign, pristine_to_patched=allow, patch_offset=TEST_OFFSET)

    def test_wrong_instruction_bytes_are_rejected(self) -> None:
        altered = make_macho(patcher.EXPECTED_UUID, bytes.fromhex("a1000034"))
        allow = {hashlib.sha256(altered).hexdigest(): "unused"}
        with self.assertRaisesRegex(patcher.PatchError, "instruction guard failed"):
            patcher.patch_bytes(altered, pristine_to_patched=allow, patch_offset=TEST_OFFSET)

    def test_unapproved_sha_is_rejected_even_with_matching_bytes(self) -> None:
        with self.assertRaisesRegex(patcher.PatchError, "unapproved executable SHA-256"):
            patcher.patch_bytes(self.pristine, pristine_to_patched={}, patch_offset=TEST_OFFSET)

    def test_dry_run_validates_without_creating_output_or_backup(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "kickflight"
            output = Path(directory) / "kickflight.patched"
            source.write_bytes(self.pristine)
            action = patcher.process_file(
                source,
                output=output,
                dry_run=True,
                pristine_to_patched=self.hashes,
                patch_offset=TEST_OFFSET,
            )
            self.assertEqual(action, "patch")
            self.assertEqual(source.read_bytes(), self.pristine)
            self.assertFalse(output.exists())
            self.assertFalse(Path(str(output) + ".ios-unload-callback.bak").exists())

    def test_output_backup_and_rerun_are_reversible_and_idempotent(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "kickflight"
            output = Path(directory) / "kickflight.patched"
            backup = Path(str(output) + ".ios-unload-callback.bak")
            source.write_bytes(self.pristine)

            first = patcher.process_file(
                source,
                output=output,
                pristine_to_patched=self.hashes,
                patch_offset=TEST_OFFSET,
            )
            patched = output.read_bytes()
            self.assertEqual(first, "patch")
            self.assertEqual(backup.read_bytes(), self.pristine)
            self.assertEqual(patched[TEST_OFFSET : TEST_OFFSET + 4], patcher.REPLACEMENT_BYTES)

            second = patcher.process_file(
                source,
                output=output,
                pristine_to_patched=self.hashes,
                patch_offset=TEST_OFFSET,
            )
            self.assertEqual(second, "already-patched")
            self.assertEqual(output.read_bytes(), patched)
            self.assertEqual(backup.read_bytes(), self.pristine)

    def test_existing_unrelated_backup_blocks_output_patch(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "kickflight"
            output = Path(directory) / "kickflight.patched"
            backup = Path(str(output) + ".ios-unload-callback.bak")
            source.write_bytes(self.pristine)
            backup.write_bytes(b"unrelated prior backup")

            with self.assertRaisesRegex(patcher.PatchError, "backup already exists and does not match source"):
                patcher.process_file(
                    source,
                    output=output,
                    pristine_to_patched=self.hashes,
                    patch_offset=TEST_OFFSET,
                )
            self.assertFalse(output.exists())
            self.assertEqual(backup.read_bytes(), b"unrelated prior backup")

    def test_already_patched_in_place_requires_matching_pristine_backup(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "kickflight"
            patched = bytearray(self.pristine)
            patched[TEST_OFFSET : TEST_OFFSET + 4] = patcher.REPLACEMENT_BYTES
            source.write_bytes(patched)

            with self.assertRaisesRegex(patcher.PatchError, "no reversible backup"):
                patcher.process_file(
                    source,
                    in_place=True,
                    pristine_to_patched=self.hashes,
                    patch_offset=TEST_OFFSET,
                )
            self.assertEqual(source.read_bytes(), bytes(patched))

    def test_in_place_patch_keeps_pristine_backup_and_reruns_cleanly(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "kickflight"
            backup = Path(str(source) + ".ios-unload-callback.bak")
            source.write_bytes(self.pristine)

            first = patcher.process_file(
                source,
                in_place=True,
                pristine_to_patched=self.hashes,
                patch_offset=TEST_OFFSET,
            )
            patched = source.read_bytes()
            self.assertEqual(first, "patch")
            self.assertEqual(backup.read_bytes(), self.pristine)

            second = patcher.process_file(
                source,
                in_place=True,
                pristine_to_patched=self.hashes,
                patch_offset=TEST_OFFSET,
            )
            self.assertEqual(second, "already-patched")
            self.assertEqual(source.read_bytes(), patched)
            self.assertEqual(backup.read_bytes(), self.pristine)


if __name__ == "__main__":
    unittest.main()
