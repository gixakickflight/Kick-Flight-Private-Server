from __future__ import annotations

import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest
import zipfile

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "validate-apk-artifact.py"
spec = importlib.util.spec_from_file_location("validate_apk_artifact", SCRIPT)
validator = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(validator)


def _build_mock_elf_arm64() -> bytes:
    # 20 bytes minimum: \x7fELF, class=2, data=1, version=1, osabi=0, pad=8, type=3(ET_DYN), machine=183(AArch64)
    header = bytearray(20)
    header[:4] = b"\x7fELF"
    header[4] = 2  # 64-bit
    header[5] = 1  # 2's complement, little endian
    header[6] = 1  # EV_CURRENT
    struct.pack_into("<H", header, 16, 3)    # ET_DYN
    struct.pack_into("<H", header, 18, 183)  # EM_AARCH64
    return bytes(header)


class ValidateApkArtifactTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.temp_path = Path(self.temp_dir.name)

    def tearDown(self) -> None:
        self.temp_dir.cleanup()

    def _create_mock_apk(
        self,
        name: str = "test.apk",
        include_manifest: bool = True,
        include_dex: bool = True,
        include_so: bool = True,
        so_bytes: bytes | None = None,
        include_meta: bool = True,
        pad_size: int = 100,
    ) -> Path:
        apk_path = self.temp_path / name
        with zipfile.ZipFile(apk_path, "w") as zf:
            if include_manifest:
                zf.writestr("AndroidManifest.xml", b"<manifest/>")
            if include_dex:
                zf.writestr("classes.dex", b"dex\n035\x00")
            if include_so:
                zf.writestr("lib/arm64-v8a/libil2cpp.so", so_bytes if so_bytes is not None else _build_mock_elf_arm64())
            if include_meta:
                zf.writestr("META-INF/MANIFEST.MF", b"Manifest-Version: 1.0\n")
            if pad_size > 0:
                zf.writestr("assets/dummy.dat", b"0" * pad_size)
        return apk_path

    def test_valid_mock_apk_passes(self) -> None:
        apk = self._create_mock_apk()
        sha = validator.sha256_file(apk)
        # Should not raise
        validator.validate_apk(apk, expected_sha256=sha, min_bytes=50)

    def test_missing_file_raises_system_exit(self) -> None:
        missing = self.temp_path / "missing.apk"
        with self.assertRaises(SystemExit) as ctx:
            validator.validate_apk(missing, min_bytes=10)
        self.assertIn("does not exist", str(ctx.exception))

    def test_size_too_small_raises_system_exit(self) -> None:
        apk = self._create_mock_apk(pad_size=0)
        with self.assertRaises(SystemExit) as ctx:
            validator.validate_apk(apk, min_bytes=10_000_000)
        self.assertIn("too small", str(ctx.exception))

    def test_missing_manifest_raises_system_exit(self) -> None:
        apk = self._create_mock_apk(include_manifest=False)
        with self.assertRaises(SystemExit) as ctx:
            validator.validate_apk(apk, min_bytes=50)
        self.assertIn("missing required member: AndroidManifest.xml", str(ctx.exception))

    def test_missing_dex_raises_system_exit(self) -> None:
        apk = self._create_mock_apk(include_dex=False)
        with self.assertRaises(SystemExit) as ctx:
            validator.validate_apk(apk, min_bytes=50)
        self.assertIn("missing required member: classes.dex", str(ctx.exception))

    def test_missing_so_raises_system_exit(self) -> None:
        apk = self._create_mock_apk(include_so=False)
        with self.assertRaises(SystemExit) as ctx:
            validator.validate_apk(apk, min_bytes=50)
        self.assertIn("missing required member: lib/arm64-v8a/libil2cpp.so", str(ctx.exception))

    def test_invalid_elf_magic_raises_system_exit(self) -> None:
        apk = self._create_mock_apk(so_bytes=b"NOT_AN_ELF_BINARY_DATA")
        with self.assertRaises(SystemExit) as ctx:
            validator.validate_apk(apk, min_bytes=50)
        self.assertIn("missing ELF magic", str(ctx.exception))

    def test_wrong_elf_architecture_raises_system_exit(self) -> None:
        bad_header = bytearray(_build_mock_elf_arm64())
        struct.pack_into("<H", bad_header, 18, 62)  # EM_X86_64 = 62
        apk = self._create_mock_apk(so_bytes=bytes(bad_header))
        with self.assertRaises(SystemExit) as ctx:
            validator.validate_apk(apk, min_bytes=50)
        self.assertIn("machine type 62 != 183", str(ctx.exception))

    def test_missing_meta_inf_raises_system_exit(self) -> None:
        apk = self._create_mock_apk(include_meta=False)
        with self.assertRaises(SystemExit) as ctx:
            validator.validate_apk(apk, min_bytes=50)
        self.assertIn("missing META-INF", str(ctx.exception))

    def test_sha256_mismatch_raises_system_exit(self) -> None:
        apk = self._create_mock_apk()
        with self.assertRaises(SystemExit) as ctx:
            validator.validate_apk(apk, expected_sha256="bad" * 21 + "b", min_bytes=50)
        self.assertIn("SHA-256 mismatch", str(ctx.exception))


if __name__ == "__main__":
    unittest.main()
