import copy
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    "add_weapon_costume_aliases", ROOT / "scripts/add_weapon_costume_aliases.py"
)
FALLBACKS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(FALLBACKS)
CATALOG_SPEC = importlib.util.spec_from_file_location(
    "build_title_resource_catalog", ROOT / "scripts/build-title-resource-catalog.py"
)
CATALOG = importlib.util.module_from_spec(CATALOG_SPEC)
CATALOG_SPEC.loader.exec_module(CATALOG)


class WeaponCostumeFallbackTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.title = json.loads(FALLBACKS.TITLE_PATH.read_text(encoding="utf-8"))
        cls.by_name = {
            name: entry
            for entry in cls.title["entries"]
            for name in entry.get("names", [])
            if FALLBACKS.WEAPON_PATH.fullmatch(name)
        }
        cls.specs = FALLBACKS.build_fallback_specs(cls.title)

    def test_catalog_covers_314_routes_and_35_physical_fallbacks(self):
        self.assertEqual(314, len(FALLBACKS.expected_weapon_routes(self.title)))
        self.assertTrue(FALLBACKS.expected_weapon_routes(self.title).issubset(self.by_name))
        self.assertEqual(35, len(self.specs))
        self.assertEqual(35, len({entry["sourcePath"] for entry in self.title["entries"]
                                  if entry.get("id", "").startswith(FALLBACKS.FALLBACK_ID_PREFIX)}))

    def test_fallbacks_have_unique_cabs_and_preserve_root_streams_and_pointers(self):
        routes = {requested for requested, _donor, _entry in self.specs}
        self.assertEqual(35, len(routes))
        clone_entries = [
            entry for entry in self.title["entries"]
            if entry.get("id", "").startswith(FALLBACKS.FALLBACK_ID_PREFIX)
        ]
        self.assertEqual(35, len(clone_entries))
        self.assertEqual(35, len({entry["objectName"] for entry in clone_entries}))
        self.assertEqual(35, len({entry["octoId"] for entry in clone_entries}))
        self.assertEqual(35, len({entry["sourcePath"] for entry in clone_entries}))

        cabs = set()
        for requested, donor, _donor_entry in self.specs:
            target = self.by_name[requested]
            donor_target = self.by_name[donor]
            requested_parts = FALLBACKS._weapon_parts(requested)
            donor_parts = FALLBACKS._weapon_parts(donor)
            self.assertEqual(requested_parts[0], donor_parts[0], requested)
            self.assertEqual(requested_parts[3], donor_parts[3], requested)
            self.assertNotEqual(target["sourcePath"], donor_target["sourcePath"], requested)
            result = FALLBACKS.verify_weapon_bundle(
                (ROOT / target["sourcePath"]).read_bytes(), requested
            )
            donor_result = FALLBACKS.verify_weapon_bundle(
                (ROOT / donor_target["sourcePath"]).read_bytes(), donor
            )
            self.assertNotIn(result["cab"], cabs)
            cabs.add(result["cab"])
            self.assertEqual(donor_result["object_count"], result["object_count"])
            self.assertGreater(result["local_preload_pointers"], 0)
            self.assertGreater(result["external_preload_pointers"], 0)
            self.assertGreater(result["self_stream_count"], 0)
            self.assertEqual("GameObject", result["root_type"])
            self.assertEqual(donor_result["root_name"], result["root_name"])
            self.assertEqual(donor_result["animator_refs"], result["animator_refs"])
        tsubame_low = self.by_name["weapon/wp_001/wp_001_022_001.unity3d"]
        tsubame_refs = FALLBACKS.verify_weapon_bundle(
            (ROOT / tsubame_low["sourcePath"]).read_bytes(), "weapon/wp_001/wp_001_022_001.unity3d"
        )["animator_refs"]
        self.assertTrue(tsubame_refs)

    def test_materialization_is_deterministic_and_does_not_change_donors(self):
        first = copy.deepcopy(self.title)
        second = copy.deepcopy(self.title)
        entries1, hashes1 = FALLBACKS._create_entries(first, write=False)
        entries2, hashes2 = FALLBACKS._create_entries(second, write=False)
        self.assertEqual(entries1, entries2)
        self.assertEqual(hashes1, hashes2)
        self.assertEqual(json.dumps(first, sort_keys=True), json.dumps(second, sort_keys=True))
        generated_by_name = {
            entry["names"][0]: entry
            for entry in json.loads(FALLBACKS.TITLE_PATH.read_text(encoding="utf-8"))["entries"]
            if entry.get("id", "").startswith(FALLBACKS.FALLBACK_ID_PREFIX)
        }
        for logical_name, digest in hashes1:
            output = ROOT / generated_by_name[logical_name]["sourcePath"]
            self.assertEqual(digest, FALLBACKS.hashlib.md5(output.read_bytes()).hexdigest())

    def test_current_revision_full_update_and_empty_current_delta(self):
        revision = self.title["revision"]
        self.assertGreaterEqual(revision, 31)
        self.assertEqual(list(range(revision + 1)), self.title["fromRevisions"])
        from_previous = json.loads(
            (ROOT / f"config/fixtures/resource-list-12345-from-{revision - 1}.json").read_text()
        )
        from_current = json.loads(
            (ROOT / f"config/fixtures/resource-list-12345-from-{revision}.json").read_text()
        )
        self.assertGreater(len(from_previous["bodyBase64"]), 1000)
        self.assertLess(len(from_current["bodyBase64"]), len(from_previous["bodyBase64"]))

    def test_catalog_builder_runs_generator_before_catalog_reads(self):
        with tempfile.TemporaryDirectory() as temp:
            repo = Path(temp)
            definition = repo / "config/resources/title-minimum.json"
            definition.parent.mkdir(parents=True)
            definition.touch()
            generator = repo / "scripts/add_weapon_costume_aliases.py"
            generator.parent.mkdir(parents=True)
            generator.touch()
            with mock.patch.object(CATALOG, "_unitypy_interpreter", return_value=Path("python")):
                with mock.patch.object(CATALOG.subprocess, "run", return_value=SimpleNamespace(returncode=0)) as run:
                    CATALOG.materialize_weapon_costume_fallbacks(repo, definition)
            run.assert_called_once_with(["python", str(generator)], cwd=repo, check=False)

    def test_catalog_builder_reports_failed_generation(self):
        with tempfile.TemporaryDirectory() as temp:
            repo = Path(temp)
            definition = repo / "config/resources/title-minimum.json"
            definition.parent.mkdir(parents=True)
            definition.touch()
            generator = repo / "scripts/add_weapon_costume_aliases.py"
            generator.parent.mkdir(parents=True)
            generator.touch()
            with mock.patch.object(CATALOG, "_unitypy_interpreter", return_value=Path("python")):
                with mock.patch.object(CATALOG.subprocess, "run", return_value=SimpleNamespace(returncode=1)):
                    with self.assertRaisesRegex(SystemExit, "restore the captured donor bundles"):
                        CATALOG.materialize_weapon_costume_fallbacks(repo, definition)


if __name__ == "__main__":
    unittest.main()
