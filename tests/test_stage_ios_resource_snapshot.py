import binascii
import hashlib
import importlib.util
import io
import json
import tarfile
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "stage-ios-resource-snapshot.py"
spec = importlib.util.spec_from_file_location("stage_ios_resource_snapshot", SCRIPT)
stager = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(stager)


class StageIosResourceSnapshotTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.payload = b"small Octo payload fixture\x00\x01"
        self.payload_rel = "payload/fixture/example.octo"
        self.source_rel = ".local/ios-resources/12346-r5/payload/fixture/example.octo"
        self.manifest_doc = {
            "assetVersion": 12346,
            "platform": 9,
            "revision": 44,
            "schemaVersion": 1,
            "resources": [{
                "id": "ios-test-1", "enabled": True, "bytes": len(self.payload),
                "sha256": hashlib.sha256(self.payload).hexdigest(),
                "md5": hashlib.md5(self.payload).hexdigest(),
                "crc32": binascii.crc32(self.payload) & 0xFFFFFFFF,
                "sourcePath": self.source_rel,
            }],
        }
        self.manifest = self.root / "catalog-source.json"
        self.manifest.write_text(json.dumps(self.manifest_doc, separators=(",", ":")))
        self.archive = self.root / "snapshot.tar.gz"
        self.write_archive()
        self.archive_sha = hashlib.sha256(self.archive.read_bytes()).hexdigest()
        self.stage_root = self.root / ".local" / "ios-resources"

    def tearDown(self):
        self.temp.cleanup()

    def write_archive(self, *, manifest=None, payload=None, extra=None, traversal=None, symlink=False, duplicate=False, dirs=False, catalog_last=False, omit_payload=False):
        catalog = self.manifest.read_bytes() if manifest is None else manifest
        payload_bytes = self.payload if payload is None else payload
        with tarfile.open(self.archive, "w:gz") as tf:
            if dirs:
                for name in ("payload/", "payload/fixture/"):
                    info = tarfile.TarInfo(name)
                    info.type = tarfile.DIRTYPE
                    tf.addfile(info)
            def add_file(name, data):
                info = tarfile.TarInfo(name)
                info.size = len(data)
                tf.addfile(info, io.BytesIO(data))
            ordered_files = [("catalog.json", catalog), (self.payload_rel, payload_bytes)]
            if catalog_last:
                ordered_files.reverse()
            for name, data in ordered_files:
                if omit_payload and name == self.payload_rel:
                    continue
                add_file(name, data)
            if extra:
                add_file(extra[0], extra[1])
            if traversal:
                add_file(traversal, b"escape")
            if symlink:
                info = tarfile.TarInfo("payload/bad-link")
                info.type = tarfile.SYMTYPE
                info.linkname = "../../outside"
                tf.addfile(info)
            if duplicate:
                add_file(self.payload_rel, payload_bytes)

    def stage(self, archive_sha=None):
        return stager.stage_archive(
            self.archive,
            archive_sha or hashlib.sha256(self.archive.read_bytes()).hexdigest(),
            self.manifest,
            self.stage_root,
            "12346-r5",
        )

    def test_valid_archive_stages_atomically_and_is_idempotent(self):
        legacy = self.stage_root / "12346-r4"
        legacy.mkdir(parents=True)
        marker = legacy / "keep-me"
        marker.write_text("legacy snapshot stays untouched")
        legacy_before = marker.read_bytes()
        target = self.stage()
        self.assertEqual((target / "catalog.json").read_bytes(), self.manifest.read_bytes())
        self.assertEqual((target / "payload/fixture/example.octo").read_bytes(), self.payload)
        self.assertEqual(self.stage(), target)
        self.assertEqual(marker.read_bytes(), legacy_before)
        self.assertEqual(sorted(p.relative_to(target).as_posix() for p in target.rglob("*") if p.is_file()),
                         ["catalog.json", "payload/fixture/example.octo"])

    def test_normal_tar_directory_headers_are_accepted(self):
        self.write_archive(dirs=True)
        target = self.stage()
        self.assertEqual((target / "payload/fixture/example.octo").read_bytes(), self.payload)

    def test_members_are_streamed_in_archive_order_with_catalog_last(self):
        self.write_archive(catalog_last=True)
        target = self.stage()
        self.assertEqual((target / "catalog.json").read_bytes(), self.manifest.read_bytes())
        self.assertEqual((target / "payload/fixture/example.octo").read_bytes(), self.payload)

    def test_missing_member_never_publishes_partial_snapshot(self):
        self.write_archive(omit_payload=True)
        with self.assertRaises(stager.SnapshotError):
            self.stage()
        self.assertFalse((self.stage_root / "12346-r5").exists())
        self.assertFalse(list(self.stage_root.glob(".12346-r5.tmp-*")) if self.stage_root.exists() else [])

    def test_untrusted_archive_hash_fails_without_creating_target(self):
        with self.assertRaises(stager.SnapshotError):
            self.stage("0" * 64)
        self.assertFalse((self.stage_root / "12346-r5").exists())
        self.assertFalse(list(self.stage_root.glob(".12346-r5.tmp-*")) if self.stage_root.exists() else [])

    def test_catalog_and_payload_must_match_manifest(self):
        original = self.manifest.read_bytes()
        altered = dict(self.manifest_doc)
        altered["revision"] = 45
        self.write_archive(manifest=json.dumps(altered).encode())
        with self.assertRaises(stager.SnapshotError):
            self.stage()
        self.write_archive(payload=b"different bytes")
        with self.assertRaises(stager.SnapshotError):
            self.stage()
        self.assertEqual(self.manifest.read_bytes(), original)
        self.assertFalse((self.stage_root / "12346-r5").exists())

    def test_manifest_size_and_each_checksum_are_enforced(self):
        mutations = {
            "bytes": self.manifest_doc["resources"][0]["bytes"] + 1,
            "sha256": "0" * 64,
            "md5": "0" * 32,
            "crc32": self.manifest_doc["resources"][0]["crc32"] ^ 1,
        }
        for field, bad_value in mutations.items():
            with self.subTest(field=field):
                changed = json.loads(json.dumps(self.manifest_doc))
                changed["resources"][0][field] = bad_value
                raw = json.dumps(changed, separators=(",", ":")).encode()
                self.manifest.write_bytes(raw)
                self.write_archive(manifest=raw)
                with self.assertRaises(stager.SnapshotError):
                    self.stage()
                self.assertFalse((self.stage_root / "12346-r5").exists())
        self.manifest.write_text(json.dumps(self.manifest_doc, separators=(",", ":")))

    def test_archive_members_must_be_exact_and_safe(self):
        cases = [
            {"extra": ("payload/unexpected.octo", b"x")},
            {"traversal": "../escape"},
            {"symlink": True},
            {"duplicate": True},
        ]
        for case in cases:
            with self.subTest(case=case):
                self.write_archive(**case)
                with self.assertRaises(stager.SnapshotError):
                    self.stage()
                self.assertFalse((self.stage_root / "12346-r5").exists())
                self.assertFalse(list(self.stage_root.glob(".12346-r5.tmp-*")) if self.stage_root.exists() else [])

    def test_incomplete_or_conflicting_existing_target_is_never_replaced(self):
        target = self.stage()
        payload_path = target / "payload/fixture/example.octo"
        payload_path.write_bytes(b"preserve existing contents")
        before = payload_path.read_bytes()
        with self.assertRaises(stager.SnapshotError):
            self.stage()
        self.assertEqual(payload_path.read_bytes(), before)
        self.assertEqual((target / "catalog.json").read_bytes(), self.manifest.read_bytes())

    def test_wrong_snapshot_name_is_rejected(self):
        with self.assertRaises(stager.SnapshotError):
            stager.validate_archive(self.archive, self.archive_sha, self.manifest, "12345-r5")


if __name__ == "__main__":
    unittest.main()
