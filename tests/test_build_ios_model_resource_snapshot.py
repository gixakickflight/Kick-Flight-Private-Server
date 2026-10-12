import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "build-ios-ui-resource-snapshot.py"
spec = importlib.util.spec_from_file_location("build_ios_ui_resource_snapshot", SCRIPT)
builder = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(builder)


class BuildIosModelResourceSnapshotTests(unittest.TestCase):
    def test_appends_models_and_preserves_every_r5_row_field(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            old_payload = root / ".local/ios-resources/12346-r5/payload/r5.octo"
            old_payload.parent.mkdir(parents=True)
            old_payload.write_bytes(b"pinned r5 payload")
            old_bytes, old_sha, old_md5, old_crc = builder.file_hashes(old_payload)
            base_rows = []
            for octo_id in range(1, 2037):
                kind = "assetBundle" if octo_id <= 1831 else "resource"
                name = (f"ui/base_{octo_id}.unity3d" if kind == "assetBundle"
                        else f"resource/base_{octo_id}")
                base_rows.append({
                    "bytes": old_bytes,
                    "contentType": "application/octet-stream",
                    "crc32": old_crc,
                    "description": "original r5 row",
                    "enabled": True,
                    "host": "kick-flight-fenix.us.ci",
                    "id": f"old-{octo_id}",
                    "kind": kind,
                    "logicalName": name,
                    "md5": old_md5,
                    "octoId": octo_id,
                    "octoName": name,
                    "requestPath": f"/ios/cdn/{octo_id:06d}",
                    "sha256": old_sha,
                    "sourcePath": ".local/ios-resources/12346-r5/payload/r5.octo",
                })
            base = {"assetVersion": 12346, "platform": 9, "revision": 44,
                    "schemaVersion": 1, "resources": base_rows}
            coverage_rows = []
            converted = []
            for index in range(216):
                logical_name = f"player/pc_{index // 16:03d}/pc_{index // 16:03d}_{index:03d}.unity3d"
                source_sha = hashlib.sha256(f"android-{index}".encode()).hexdigest()
                output = root / f"model-{index}.octo"
                output.write_bytes(f"ios-model-{index}".encode())
                size, output_sha, _, _ = builder.file_hashes(output)
                coverage_rows.append({"name": logical_name,
                                      "androidExpectedSha256": source_sha,
                                      "androidObjectName": f"{index:06d}"})
                converted.append({"logical_name": logical_name, "source_sha256": source_sha,
                                  "source_target_platform": 13, "output_target_platform": 9,
                                  "unity_version": "2018.4.11f1", "output_path": str(output),
                                  "output_bytes": size, "output_sha256": output_sha})
            report = {"expected_count": 216, "converted": converted, "rejected": [],
                      "summary": {"converted": 216, "rejected": 0}}

            catalog, payloads = builder.build_model_catalog(
                base, report, android_coverage={"androidEntries": coverage_rows}, source_root=root)

            self.assertEqual(45, catalog["revision"])
            self.assertEqual(2252, len(catalog["resources"]))
            self.assertEqual(2047, sum(row["kind"] == "assetBundle" for row in catalog["resources"]))
            self.assertEqual(205, sum(row["kind"] == "resource" for row in catalog["resources"]))
            for old, new in zip(base_rows, catalog["resources"][:2036]):
                self.assertEqual({key: value for key, value in old.items() if key != "sourcePath"},
                                 {key: value for key, value in new.items() if key != "sourcePath"})
                self.assertTrue(new["sourcePath"].startswith(".local/ios-resources/12346-r6/payload/"))
            new_rows = catalog["resources"][2036:]
            self.assertEqual(list(range(2037, 2253)), [row["octoId"] for row in new_rows])
            self.assertEqual({row["logicalName"] for row in new_rows},
                             {item["logical_name"] for item in converted})
            self.assertEqual(2252, len(payloads))


if __name__ == "__main__":
    unittest.main()
