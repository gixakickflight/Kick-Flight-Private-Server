import importlib.util
import io
from pathlib import Path
import tarfile
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "stage-ios-r52-profile.py"
SPEC = importlib.util.spec_from_file_location("stage_ios_r52_profile", SCRIPT)
STAGER = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(STAGER)


class StageIosR52ProfileTests(unittest.TestCase):
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
                STAGER.verify_stage(Path(temp) / "missing", "d" * 64)


if __name__ == "__main__":
    unittest.main()
