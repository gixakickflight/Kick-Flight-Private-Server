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
    "build_localize_bundles", ROOT / "scripts/build-localize-bundles.py"
)
LOCALIZE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(LOCALIZE)
CATALOG_SPEC = importlib.util.spec_from_file_location(
    "build_title_resource_catalog", ROOT / "scripts/build-title-resource-catalog.py"
)
CATALOG = importlib.util.module_from_spec(CATALOG_SPEC)
CATALOG_SPEC.loader.exec_module(CATALOG)


class LocalizeBundleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.title = json.loads(LOCALIZE.TITLE_PATH.read_text(encoding="utf-8-sig"))
        cls.captures = LOCALIZE.source_entries(cls.title)
        cls.specs = LOCALIZE.build_clone_specs(cls.title)
        cls.generated = [
            entry for entry in cls.title["entries"]
            if entry.get("id", "").startswith(LOCALIZE.ENTRY_ID_PREFIX)
        ]

    def test_definition_covers_95_captures_and_665_physical_bundles(self):
        self.assertEqual(95, len(self.captures))
        self.assertEqual(95 * len(LOCALIZE.LANG_CODES), len(self.specs))
        self.assertEqual(95 * len(LOCALIZE.LANG_CODES), len(self.generated))
        self.assertEqual({"en", "es", "ja", "pt", "ko", "fr", "de"}, set(LOCALIZE.LANG_CODES))
        for capture in self.captures:
            self.assertEqual(1, len(capture["names"]))
            self.assertEqual(LOCALIZE.SOURCE_LANG, LOCALIZE._language_of(capture["names"][0]))
        generated_names = {entry["names"][0] for entry in self.generated}
        for capture in self.captures:
            for lang in LOCALIZE.LANG_CODES:
                self.assertIn(LOCALIZE._replace_language(capture["names"][0], lang), generated_names)

    def test_generated_identities_are_unique(self):
        self.assertEqual(len(self.generated), len({entry["id"] for entry in self.generated}))
        self.assertEqual(len(self.generated), len({entry["objectName"] for entry in self.generated}))
        self.assertEqual(len(self.generated), len({entry.get("octoId") for entry in self.generated}))
        self.assertEqual(len(self.generated), len({entry["sourcePath"] for entry in self.generated}))
        all_names = [name for entry in self.title["entries"] for name in entry.get("names", [])]
        self.assertEqual(len(all_names), len(set(all_names)))

    def test_every_generated_bundle_has_unique_cab_and_language_container(self):
        captures, entries = LOCALIZE.check_definition(self.title)
        self.assertEqual(95, captures)
        self.assertEqual(665, entries)

    def test_clone_is_deterministic_and_does_not_change_captures(self):
        picks = [
            "effect/localize/pt/ui/ef_ui_008/ef_ui_008.unity3d",
            "font/localize/fr/font.unity3d",
            "ui/localize/en/stamp/thumbnail_stamp_6052029.unity3d",
        ]
        spec_by_name = {spec["logical_name"]: spec for spec in self.specs}
        for logical_name in picks:
            spec = spec_by_name[logical_name]
            source = (ROOT / spec["source_path"]).read_bytes()
            source_info = LOCALIZE._load_source_info(source)
            first = LOCALIZE.clone_localize_bundle(
                source, spec["source_name"], logical_name, spec["lang"], source_info
            )[0]
            second = LOCALIZE.clone_localize_bundle(
                source, spec["source_name"], logical_name, spec["lang"], source_info
            )[0]
            self.assertEqual(first, second, logical_name)
            self.assertNotEqual(source, first)
        before = copy.deepcopy(self.captures)
        LOCALIZE.build_clone_specs(copy.deepcopy(self.title))
        self.assertEqual(before, self.captures)

    def test_revision_and_fixtures_track_the_current_database(self):
        revision = self.title["revision"]
        self.assertGreaterEqual(revision, 37)
        self.assertEqual(list(range(revision + 1)), self.title["fromRevisions"])
        current = json.loads(
            (ROOT / f"config/fixtures/resource-list-12345-from-{revision}.json").read_text()
        )
        previous = json.loads(
            (ROOT / f"config/fixtures/resource-list-12345-from-{revision - 1}.json").read_text()
        )
        self.assertEqual(
            f"/v1/list/12345/{revision}", current["path"]
        )
        self.assertLess(len(current["bodyBase64"]), len(previous["bodyBase64"]))
        self.assertNotIn("10.0.2.2", json.dumps(current))

    def test_catalog_builder_runs_generator_before_catalog_reads(self):
        with tempfile.TemporaryDirectory() as temp:
            repo = Path(temp)
            definition = repo / "config/resources/title-minimum.json"
            definition.parent.mkdir(parents=True)
            definition.touch()
            generator = repo / "scripts/build-localize-bundles.py"
            generator.parent.mkdir(parents=True)
            generator.touch()
            with mock.patch.object(CATALOG, "_unitypy_interpreter", return_value=Path("python")):
                with mock.patch.object(CATALOG.subprocess, "run", return_value=SimpleNamespace(returncode=0)) as run:
                    CATALOG.materialize_localize_bundles(repo, definition)
            run.assert_called_once_with(["python", str(generator)], cwd=repo, check=False)

    def test_catalog_builder_reports_failed_generation(self):
        with tempfile.TemporaryDirectory() as temp:
            repo = Path(temp)
            definition = repo / "config/resources/title-minimum.json"
            definition.parent.mkdir(parents=True)
            definition.touch()
            generator = repo / "scripts/build-localize-bundles.py"
            generator.parent.mkdir(parents=True)
            generator.touch()
            with mock.patch.object(CATALOG, "_unitypy_interpreter", return_value=Path("python")):
                with mock.patch.object(CATALOG.subprocess, "run", return_value=SimpleNamespace(returncode=1)):
                    with self.assertRaisesRegex(SystemExit, "per-language localize bundles"):
                        CATALOG.materialize_localize_bundles(repo, definition)


if __name__ == "__main__":
    unittest.main()
