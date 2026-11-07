import base64
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "stage-ios-profile.py"
SPEC = importlib.util.spec_from_file_location("stage_ios_profile", SCRIPT)
STAGER = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
sys.modules["stage_ios_profile"] = STAGER
SPEC.loader.exec_module(STAGER)


class StageIosProfileTests(unittest.TestCase):
    def test_safe_relative_rejects_absolute_and_parent_paths(self):
        for value in ("/etc/passwd", "../escape", "payload/../escape", "payload\\escape"):
            with self.subTest(value=value), self.assertRaises(STAGER.ProfileError):
                STAGER._safe_relative(value)

    def test_extract_rejects_tar_symbolic_links(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive_path = root / "unsafe.tar.gz"
            with tarfile.open(archive_path, "w:gz") as archive:
                link = tarfile.TarInfo("payload-r7/object.octo")
                link.type = tarfile.SYMTYPE
                link.linkname = "../../outside"
                archive.addfile(link)
            destination = root / "extract"
            destination.mkdir()
            with self.assertRaises(STAGER.ProfileError):
                STAGER._extract_archive(archive_path, destination)

    def test_extract_rejects_duplicate_archive_members(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive_path = root / "duplicate.tar.gz"
            with tarfile.open(archive_path, "w:gz") as archive:
                for _ in range(2):
                    member = tarfile.TarInfo("profile-report.json")
                    member.size = 2
                    archive.addfile(member, io.BytesIO(b"{}"))
            destination = root / "extract"
            destination.mkdir()
            with self.assertRaises(STAGER.ProfileError):
                STAGER._extract_archive(archive_path, destination)

    def test_verify_stage_refuses_a_missing_stage_root(self):
        with tempfile.TemporaryDirectory() as temp:
            with self.assertRaises(STAGER.ProfileError):
                STAGER.verify_stage(STAGER.ProfileSpec(12346, 52), Path(temp) / "missing", "d" * 64)


def _varint(value: int) -> bytes:
    out = bytearray()
    while True:
        byte = value & 0x7F
        value >>= 7
        out.append(byte | (0x80 if value else 0))
        if not value:
            return bytes(out)


def _field(number: int, wire: int, payload: bytes) -> bytes:
    key = _varint((number << 3) | wire)
    return key + (_varint(len(payload)) + payload if wire == 2 else payload)


def _octo_database(revision: int, rows: int, from_revision: int) -> bytes:
    body = _field(1, 0, _varint(revision))
    state = 1 if from_revision == 0 else 2
    for row in range(rows):
        body += _field(2, 2, _field(1, 0, _varint(1000 + row)) + _field(9, 0, _varint(state)))
    if rows:
        body += _field(3, 2, b"common")
    return body + _field(5, 2, STAGER.URL_FORMAT.encode())


def _sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def build_synthetic_profile(root: Path, spec, *, routes: int = 3, rows: int = 2) -> Path:
    """Write a minimal but fully self-consistent review package for an arbitrary (assetVersion, revision)."""
    profile = root / "profile"
    (profile / "fixtures").mkdir(parents=True)
    (profile / "payload-r7").mkdir()
    resources = []
    manifest = []
    payload_bytes = 0
    for index in range(routes):
        name = f"object-{index:04d}.octo"
        data = f"payload-{index}".encode()
        (profile / "payload-r7" / name).write_bytes(data)
        digest = _sha(data)
        payload_bytes += len(data)
        manifest.append((f"payload-r7/{name}", len(data), digest))
        resources.append({"id": f"synthetic-{index}", "enabled": True, "host": STAGER.HOST,
                          "requestPath": STAGER.CDN_PREFIX + name, "logicalName": f"resource/{index}",
                          "sourcePath": f"payload-r7/{name}", "contentType": "application/octet-stream",
                          "sha256": digest})
    catalog = json.dumps({"schemaVersion": 1, "resources": resources}).encode()
    (profile / "catalog.json").write_bytes(catalog)
    title = json.dumps({"assetVersion": spec.asset_version, "revision": spec.revision,
                        "previousRevision": spec.revision - 1, "fromRevisions": list(range(spec.revision)),
                        "urlPathFormat": STAGER.CDN_PREFIX + "{o}"}).encode()
    (profile / "title-minimum.json").write_bytes(title)
    fixture_hashes = {}
    for name in sorted(spec.fixture_names):
        current = name == f"resource-list-{spec.asset_version}.json"
        from_revision = spec.revision if current else int(name.rsplit("-", 1)[1].removesuffix(".json"))
        body = _octo_database(spec.revision, 0 if current else rows, from_revision)
        fixture = json.dumps({"id": name, "enabled": True, "host": STAGER.HOST, "method": "GET",
                              "path": f"/v1/list/{spec.asset_version}/{from_revision}", "statusCode": 200,
                              "contentType": "application/x-protobuf",
                              "bodyBase64": base64.b64encode(body).decode()}).encode()
        (profile / "fixtures" / name).write_bytes(fixture)
        fixture_hashes[name] = _sha(fixture)
    payload_set = hashlib.sha256()
    for source, size, digest in sorted(manifest):
        payload_set.update(f"{source}\0{size}\0{digest}\n".encode())
    fixture_set = hashlib.sha256()
    for name, digest in sorted(fixture_hashes.items()):
        fixture_set.update(f"{name}\0{digest}\n".encode())
    report = {
        "schema": spec.report_schema, "assetVersion": spec.asset_version, "revision": spec.revision,
        "previousRevision": spec.revision - 1, "fixtureCount": spec.fixture_count,
        "currentFixtureRevision": spec.revision, "canonicalHost": STAGER.HOST,
        "urlFormatInFixtures": STAGER.URL_FORMAT, "cdnRequestPrefix": STAGER.CDN_PREFIX,
        "testOnly": True, "complete": False, "originalWireMapVerified": False,
        "upstreamMustRemainDisabled": True, "noNetworkRequests": True, "noServerStarted": True,
        "noVpsWrite": True, "servedRows": rows, "resourceRoutes": routes, "payloadCount": routes,
        "payloadBytes": payload_bytes, "unverifiedReconstructedRowIds": 0,
        "fromRevisions": list(range(spec.revision)), "fullUpdateFixtureRevisions": list(range(spec.revision)),
        "sourceProfile": "local/unsanitized/path", "packageCatalogSha256": _sha(catalog),
        "titleMinimumSha256": _sha(title), "fixtureSetSha256": fixture_set.hexdigest(),
        "payloadSetSha256": payload_set.hexdigest(), "fixtureSha256": fixture_hashes,
    }
    (profile / "profile-report.json").write_text(json.dumps(report), encoding="utf-8")
    (profile / "README.md").write_text("synthetic review package\n", encoding="utf-8")
    (profile / "rollback.md").write_text("synthetic rollback notes\n", encoding="utf-8")
    return profile


def _unfreeze(path: Path) -> None:
    for item in [path, *path.rglob("*")]:
        item.chmod(0o755 if item.is_dir() else 0o644)


class GenericProfileIdentityTests(unittest.TestCase):
    """The profile identity is data (the release pin), never code: any (assetVersion, revision) works."""

    def _pack(self, root: Path, spec, **kwargs):
        profile = build_synthetic_profile(root, spec, **kwargs)
        archive = root / "profile.tar.gz"
        sha = STAGER.pack_profile(spec, profile, archive)
        return archive, sha

    def test_revision_53_profile_is_packed_validated_staged_and_verified(self):
        spec = STAGER.ProfileSpec(12347, 53)
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive, sha = self._pack(root, spec)
            self.assertEqual("12347-r53", spec.stage_name)
            self.assertEqual("valid", "valid" if STAGER.validate_archive(spec, archive, sha) else "")
            stage_root = root / "stage"
            self.assertEqual("staged", STAGER.stage_archive(spec, archive, sha, stage_root))
            self.assertEqual("already-staged", STAGER.stage_archive(spec, archive, sha, stage_root))
            summary = STAGER.verify_stage(spec, stage_root, sha)
            self.assertEqual(3, summary["payloadCount"])
            self.assertEqual(54, summary["fixtureCount"])
            marker = json.loads((stage_root / "12347-r53" / "stage-manifest.json").read_text())
            self.assertEqual({"schema": STAGER.STAGE_MARKER_SCHEMA, "archiveSha256": sha,
                              "assetVersion": 12347, "revisionFloor": 53}, marker)
            self.assertTrue((stage_root / "12347-r53" / "profile" / "catalog.json").is_file())
            # A different identity must never validate the same bytes.
            for other in (STAGER.ProfileSpec(12347, 52), STAGER.ProfileSpec(12346, 53)):
                with self.subTest(other=other), self.assertRaises(STAGER.ProfileError):
                    STAGER.validate_archive(other, archive, sha)
            _unfreeze(stage_root)

    def test_pin_file_drives_the_cli_end_to_end(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            spec = STAGER.ProfileSpec(12347, 53)
            archive, sha = self._pack(root, spec)
            pin = root / "ios-profile-release.json"
            pin.write_text(json.dumps({"assetVersion": 12347, "revisionFloor": 53, "archiveSha256": sha}))
            stage_root = root / "stage"
            base = [sys.executable, str(SCRIPT)]
            for command in (["validate"], ["stage", "--stage-root", str(stage_root)]):
                result = subprocess.run([*base, command[0], "--pin", str(pin), "--archive", str(archive),
                                         "--archive-sha256", sha, *command[1:]],
                                        capture_output=True, text=True, check=False)
                self.assertEqual(0, result.returncode, result.stderr)
            result = subprocess.run([*base, "verify-stage", "--pin", str(pin), "--stage-root", str(stage_root),
                                     "--archive-sha256", sha], capture_output=True, text=True, check=False)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual("immutable-stage-valid", json.loads(result.stdout)["status"])
            self.assertTrue((stage_root / "12347-r53").is_dir())
            _unfreeze(stage_root)

    def test_legacy_r52_stage_marker_is_still_accepted(self):
        spec = STAGER.ProfileSpec(12346, 52)
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive, sha = self._pack(root, spec)
            stage_root = root / "stage"
            STAGER.stage_archive(spec, archive, sha, stage_root)
            target = stage_root / "12346-r52"
            _unfreeze(stage_root)
            legacy = {"schema": "kickflight.ios-r52-stage.v1", "archiveSha256": sha, "revisionFloor": 52}
            (target / "stage-manifest.json").write_text(json.dumps(legacy, sort_keys=True) + "\n")
            STAGER._freeze_tree(target)
            self.assertEqual(3, STAGER.verify_stage(spec, stage_root, sha)["payloadCount"])
            _unfreeze(stage_root)
            (target / "stage-manifest.json").write_text(json.dumps({**legacy, "revisionFloor": 53}) + "\n")
            STAGER._freeze_tree(target)
            with self.assertRaises(STAGER.ProfileError):
                STAGER.verify_stage(spec, stage_root, sha)
            _unfreeze(stage_root)

    def test_tampered_payload_is_rejected_for_any_revision(self):
        spec = STAGER.ProfileSpec(12347, 53)
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            profile = build_synthetic_profile(root, spec)
            (profile / "payload-r7" / "object-0000.octo").write_bytes(b"tampered")
            with self.assertRaises(STAGER.ProfileError):
                STAGER.pack_profile(spec, profile, root / "bad.tar.gz")


if __name__ == "__main__":
    unittest.main()
