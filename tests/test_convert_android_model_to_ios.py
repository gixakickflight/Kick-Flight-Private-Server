import importlib.util
from pathlib import Path
import sys
import unittest


SCRIPT = Path(__file__).parents[1] / "scripts/convert-android-model-to-ios.py"
spec = importlib.util.spec_from_file_location("ios_model_converter_test", SCRIPT)
converter = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = converter
spec.loader.exec_module(converter)


class FakeType:
    class_id = 43
    script_id = None
    old_type_hash = bytes.fromhex("01" * 16)
    node = None


class FakeSerializedFile:
    _enable_type_tree = True
    types = [FakeType()]


class ConverterGuardsTest(unittest.TestCase):
    def test_catalog_sha_mismatch_is_rejected_before_parse(self):
        with self.assertRaisesRegex(converter.ConversionError, "SHA-256 mismatch"):
            converter.require_sha256(b"source", "00" * 32)

    def test_ios_schema_must_match_hash_and_tree(self):
        sf = FakeSerializedFile()
        wrong_hash = {(43, None): [{"old_type_hash": "02" * 16, "tree": ()}]}
        with self.assertRaisesRegex(converter.ConversionError, "no matching iOS TypeTree"):
            converter.require_ios_schema(sf, wrong_hash)

    def test_matching_class_hash_without_type_tree_is_not_enough(self):
        sf = FakeSerializedFile()
        wrong_tree = {(43, None): [{"old_type_hash": "01" * 16, "tree": ("not", "empty")}]}
        with self.assertRaisesRegex(converter.ConversionError, "no matching iOS TypeTree"):
            converter.require_ios_schema(sf, wrong_tree)

    def test_model_route_restriction_is_enforced(self):
        with self.assertRaisesRegex(converter.ConversionError, "logical name"):
            converter.convert_model(b"", "00" * 32, "ui/example.bundle", b"", {}, set(), Path("missing"), Path("missing"))


if __name__ == "__main__":
    unittest.main()
