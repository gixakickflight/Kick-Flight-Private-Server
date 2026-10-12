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
    def test_appends_weapon_models_to_r6_without_changing_old_rows(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            old_payload = root / ".local/ios-resources/12346-r6/payload/r6.octo"
            old_payload.parent.mkdir(parents=True)
            old_payload.write_bytes(b"pinned r6 payload")
            old_bytes, old_sha, old_md5, old_crc = builder.file_hashes(old_payload)
            base_rows = []
            for octo_id in range(1, 2253):
                kind = "assetBundle" if octo_id <= 2047 else "resource"
                name = (f"player/base_{octo_id}.unity3d" if kind == "assetBundle"
                        else f"resource/base_{octo_id}")
                base_rows.append({"bytes": old_bytes, "contentType": "application/octet-stream",
                    "crc32": old_crc, "description": "original r6 row", "enabled": True,
                    "host": "kick-flight-fenix.us.ci", "id": f"r6-{octo_id}", "kind": kind,
                    "logicalName": name, "md5": old_md5, "octoId": octo_id, "octoName": name,
                    "requestPath": f"/ios/cdn/{octo_id:06d}", "sha256": old_sha,
                    "sourcePath": ".local/ios-resources/12346-r6/payload/r6.octo"})
            base = {"assetVersion": 12346, "platform": 9, "revision": 45,
                    "schemaVersion": 1, "resources": base_rows}
            coverage_rows, converted = [], []
            output = root / "weapon.octo"
            output.write_bytes(b"converted weapon bundle")
            size, output_sha, _, _ = builder.file_hashes(output)
            for index in range(311):
                logical_name = f"weapon/wp_{index:03d}/wp_{index:03d}_001_001.unity3d"
                source_sha = hashlib.sha256(f"android-{index}".encode()).hexdigest()
                coverage_rows.append({"name": logical_name, "androidExpectedSha256": source_sha,
                                      "androidObjectName": f"{index:06d}"})
                converted.append({"logical_name": logical_name, "source_sha256": source_sha,
                    "source_target_platform": 13, "output_target_platform": 9,
                    "unity_version": "2018.4.11f1", "profile": "weapon",
                    "output_path": str(output), "output_bytes": size, "output_sha256": output_sha,
                    "shader": {"ios_shader_cab": "CAB-96ae749bf375168094b083c67a3f9536",
                               "metal_shader_count": 15, "external_paths": ["archive:/ios-shader-cab"]}})
            report = {"scope": "weapon", "expected_count": 311, "converted": converted,
                      "rejected": [], "summary": {"converted": 311, "rejected": 0,
                                                     "source_bytes": 33_714_065}}
            postflight = {"summary": {"routes": 311, "source_bytes": 33_714_065,
                "weapon_key_included": True, "local_shader_count": 0,
                "all_platforms_13_to_9": True, "all_full_typetree_matches": True,
                "all_raw_object_payloads_preserved": True, "all_reference_graphs_preserved": True,
                "all_output_shader_dependencies_checked": True},
                "routes": [{"logical_name": item["logical_name"],
                    "source_sha256": item["source_sha256"], "output_sha256": item["output_sha256"],
                    "typetree_schema_equal": True, "object_payloads_preserved": True,
                    "reference_graph_equal": True, "metal_shaders": 15,
                    "external_shader_paths": item["shader"]["external_paths"]} for item in converted]}
            catalog, payloads = builder.build_weapon_catalog(
                base, report, postflight,
                android_coverage={"androidEntries": coverage_rows}, source_root=root)
            self.assertEqual(46, catalog["revision"])
            self.assertEqual(2563, len(catalog["resources"]))
            self.assertEqual(2358, sum(row["kind"] == "assetBundle" for row in catalog["resources"]))
            self.assertEqual(205, sum(row["kind"] == "resource" for row in catalog["resources"]))
            for old, new in zip(base_rows, catalog["resources"][:2252]):
                self.assertEqual({key: value for key, value in old.items() if key != "sourcePath"},
                                 {key: value for key, value in new.items() if key != "sourcePath"})
                self.assertTrue(new["sourcePath"].startswith(".local/ios-resources/12346-r7/payload/"))
            self.assertEqual(2563, len(payloads))
            invalid_postflight = {"summary": dict(postflight["summary"]), "routes": postflight["routes"][:-1]}
            with self.assertRaisesRegex(ValueError, "postflight routes"):
                builder.build_weapon_catalog(base, report, invalid_postflight,
                    android_coverage={"androidEntries": coverage_rows}, source_root=root)

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
