import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import textwrap
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "ios-wire-floor.py"
WORKFLOW = Path(__file__).resolve().parents[1] / ".github" / "workflows" / "deploy-vps-main.yml"
SPEC = importlib.util.spec_from_file_location("ios_wire_floor", SCRIPT)
FLOOR = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(FLOOR)

R52_PUSH_PATHS = {
    ".github/workflows/deploy-vps-main.yml",
    "config/ios-r52-profile-release.json",
    "deploy/docker-compose.vps.yml",
    "docs/IOS_R52_PROFILE_RELEASE.md",
    "scripts/convert-android-model-to-ios.py",
    "scripts/deploy-vps-ci.sh",
    "scripts/ios-wire-floor.py",
    "scripts/run-model-batch.py",
    "scripts/stage-ios-r52-profile.py",
    "src/KickFlight.BootstrapApi/HarnessOptions.cs",
    "src/KickFlight.BootstrapApi/IosR52TestProfileStore.cs",
    "src/KickFlight.BootstrapApi/Program.cs",
    "src/KickFlight.BootstrapApi/ResourceCatalogStore.cs",
    "src/KickFlight.BootstrapApi/appsettings.json",
    "tests/KickFlight.BootstrapApi.Tests/IosR52ProfileRoutingTests.cs",
    "tests/test_convert_android_model_to_ios.py",
    "tests/test_ios_wire_floor.py",
    "tests/test_stage_ios_r52_profile.py",
}


class IosWireFloorTests(unittest.TestCase):
    def run_workflow_detector(self, changed_paths):
        with tempfile.TemporaryDirectory() as temp:
            repo = Path(temp)
            subprocess.run(["git", "init", "-q"], cwd=repo, check=True)
            subprocess.run(["git", "config", "user.name", "Workflow Test"], cwd=repo, check=True)
            subprocess.run(["git", "config", "user.email", "workflow-test@example.invalid"], cwd=repo, check=True)
            (repo / "README.md").write_text("base\n", encoding="utf-8")
            subprocess.run(["git", "add", "README.md"], cwd=repo, check=True)
            subprocess.run(["git", "commit", "-qm", "base"], cwd=repo, check=True)
            before = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repo, text=True).strip()
            for path in changed_paths:
                destination = repo / path
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_text("fixture\n", encoding="utf-8")
                subprocess.run(["git", "add", "--", path], cwd=repo, check=True)
            subprocess.run(["git", "commit", "-qm", "deployment changes"], cwd=repo, check=True)
            after = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repo, text=True).strip()

            workflow = WORKFLOW.read_text(encoding="utf-8")
            marker = 'python3 - "$BEFORE_SHA" "$GITHUB_SHA" >> "$GITHUB_OUTPUT" <<\'PY\'\n'
            block = workflow.split(marker, 1)[1].split("\n          PY", 1)[0]
            result = subprocess.run(
                [sys.executable, "-c", textwrap.dedent(block), before, after],
                cwd=repo, check=True, capture_output=True, text=True,
            )
            return dict(line.split("=", 1) for line in result.stdout.splitlines())

    def test_r52_merge_push_is_api_only_and_skips_android_and_r7_staging(self):
        flags = self.run_workflow_detector(sorted(R52_PUSH_PATHS))
        self.assertEqual("true", flags["deploy"])
        self.assertEqual("true", flags["api"])
        self.assertEqual("true", flags["r52_api_only"])
        for component in ("photon", "cdn", "farm", "site", "caddy", "balance", "full"):
            self.assertEqual("false", flags[component])

        workflow = WORKFLOW.read_text(encoding="utf-8")
        self.assertEqual(2, workflow.count("steps.changes.outputs.r52_api_only != 'true'"))
        self.assertIn("inputs.activate_ios_r52_profile", workflow)

    def test_api_only_push_verifies_reused_native_snapshot_without_an_archive(self):
        deploy = (SCRIPT.parent / "deploy-vps-ci.sh").read_text(encoding="utf-8")
        start = deploy.index('if [[ -z "$ios_snapshot_archive" && "$ios_r52_profile_enabled" != true ]]; then')
        end = deploy.index('if [[ -n "$ios_snapshot_archive" ]]; then', start)
        no_archive_guard = deploy[start:end]
        self.assertIn("stage-ios-resource-snapshot.py\" verify-stage", no_archive_guard)
        self.assertIn("--manifest", no_archive_guard)
        self.assertIn("--stage-root", no_archive_guard)
        self.assertIn('"$snapshot_name"', no_archive_guard)
        self.assertNotIn("refusing deployment", no_archive_guard)

    def test_extra_non_r52_path_uses_normal_full_deployment_classification(self):
        flags = self.run_workflow_detector(sorted(R52_PUSH_PATHS | {"config/masters_honor.json"}))
        self.assertEqual("false", flags["r52_api_only"])
        self.assertEqual("true", flags["full"])
        for component in ("api", "photon", "cdn", "farm", "site", "caddy", "balance"):
            self.assertEqual("true", flags[component])

    def test_activation_failure_before_latch_leaves_no_floor(self):
        with tempfile.TemporaryDirectory() as temp:
            state = Path(temp) / ".ci" / "ios-wire-floor.json"
            with self.assertRaises(FLOOR.FloorError):
                FLOOR.latch_floor(state, "bad-hash", "a" * 40)
            self.assertFalse(state.exists())

    def test_latch_is_atomic_idempotent_and_rejects_downgrade_profile(self):
        with tempfile.TemporaryDirectory() as temp:
            state = Path(temp) / ".ci" / "ios-wire-floor.json"
            sha = "a" * 64
            commit = "b" * 40
            self.assertEqual("latched", FLOOR.latch_floor(state, sha, commit))
            self.assertEqual(sha, FLOOR.read_floor(state)["profileArchiveSha256"])
            self.assertFalse(state.stat().st_mode & 0o222)
            self.assertEqual("already-latched", FLOOR.latch_floor(state, sha, "c" * 40))
            with self.assertRaises(FLOOR.FloorError):
                FLOOR.latch_floor(state, "d" * 64, "c" * 40)
            self.assertEqual(sha, FLOOR.read_floor(state)["profileArchiveSha256"])

    def test_invalid_persisted_floor_fails_closed(self):
        with tempfile.TemporaryDirectory() as temp:
            state = Path(temp) / "ios-wire-floor.json"
            state.write_text(json.dumps({"schema": "unknown", "revisionFloor": 47}), encoding="utf-8")
            with self.assertRaises(FLOOR.FloorError):
                FLOOR.read_floor(state)

    def test_rollback_requires_the_real_r52_runtime_and_same_profile_pin(self):
        with tempfile.TemporaryDirectory() as temp:
            release = Path(temp)
            (release / "config").mkdir()
            api = release / "src/KickFlight.BootstrapApi"
            api.mkdir(parents=True)
            (release / "scripts").mkdir()
            (release / "deploy").mkdir()
            sha = "a" * 64
            (release / "config/ios-r52-profile-release.json").write_text(json.dumps({
                "schemaVersion": 1, "assetVersion": 12346, "revisionFloor": 52,
                "testOnly": True, "complete": False,
                "runtimeContract": FLOOR.R52_CONTRACT, "archiveSha256": sha,
            }), encoding="utf-8")
            (api / "HarnessOptions.cs").write_text("EnableIosR52TestProfile", encoding="utf-8")
            (api / "IosR52TestProfileStore.cs").write_text("GetListDatabase CdnUrlFormat", encoding="utf-8")
            (api / "Program.cs").write_text('IosR52TestProfileStore "iosProfileRevision"', encoding="utf-8")
            (api / "appsettings.json").write_text(json.dumps({"Harness": {
                "EnableIosR52TestProfile": False,
                "IosR52TestProfilePath": ".local/ios-resources/12346-r52/profile",
            }}), encoding="utf-8")
            (release / "deploy/docker-compose.vps.yml").write_text(
                "Harness__EnableIosR52TestProfile Harness__IosR52TestProfilePath", encoding="utf-8")
            (release / "scripts/stage-ios-r52-profile.py").write_text("# stager", encoding="utf-8")
            (release / "scripts/ios-wire-floor.py").write_text("# floor", encoding="utf-8")
            (release / "scripts/deploy-vps-ci.sh").write_text(
                "ios_wire_floor_active wait_for_r52_health", encoding="utf-8")

            FLOOR.verify_release(release, sha)
            with self.assertRaises(FLOOR.FloorError):
                FLOOR.verify_release(release, "f" * 64)
            (api / "Program.cs").write_text("route missing", encoding="utf-8")
            with self.assertRaises(FLOOR.FloorError):
                FLOOR.verify_release(release, sha)

    def test_deploy_builds_and_stops_before_floor_then_starts_only_api(self):
        deploy = (SCRIPT.parents[0] / "deploy-vps-ci.sh").read_text(encoding="utf-8")
        config = deploy.index("compose_from_app config --quiet")
        prebuild = deploy.index("compose_from_app build api", config)
        stop_old = deploy.index("compose_from_app stop api", prebuild)
        latch = deploy.index("ios-wire-floor.py\" latch", stop_old)
        start_api = deploy.index("compose_from_app up -d --no-deps api", latch)
        self.assertLess(config, prebuild)
        self.assertLess(prebuild, stop_old)
        self.assertLess(stop_old, latch)
        self.assertLess(latch, start_api)
        self.assertIn("wait_for_r52_health", deploy)
        self.assertIn("wait_for_previous_api_health", deploy)
        self.assertIn("prior API readiness check failed; operator recovery is required", deploy)


if __name__ == "__main__":
    unittest.main()
