#!/usr/bin/env python3
"""Tests for scripts/merge-master-overrides.py (deploy-time repo -> override three-way merge)."""
from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import os
import stat
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
_spec = importlib.util.spec_from_file_location("merge_master_overrides", REPO_ROOT / "scripts" / "merge-master-overrides.py")
assert _spec is not None and _spec.loader is not None
mmo = importlib.util.module_from_spec(_spec)
sys.modules[_spec.name] = mmo   # dataclasses resolve annotations through sys.modules
_spec.loader.exec_module(mmo)

SHA_A = "a" * 40
SHA_B = "b" * 40


def webui_text(rows) -> str:
    """Exactly what tools/balance/server.py write_table() produces."""
    return json.dumps(rows, ensure_ascii=False, indent=1) + "\n"


def row(i, **fields):
    return {"id": i, **fields}


class MergeBase(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.root = Path(self._tmp.name)
        self.base = self.root / "base"
        self.repo = self.root / "repo"
        self.over = self.root / "over"
        self.bak = self.root / "bak"
        for d in (self.base, self.repo, self.over):
            d.mkdir()

    def put(self, directory: Path, table: str, rows, text: str | None = None):
        path = directory / f"masters_{table}.json"
        path.write_text(text if text is not None else webui_text(rows), encoding="utf-8", newline="\n")
        return path

    def run_cli(self, *extra, expect=0):
        argv = ["--base-dir", str(self.base), "--repo-dir", str(self.repo), "--override-dir", str(self.over),
                "--backup-dir", str(self.bak), *extra]
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = mmo.main(argv)
        self.assertEqual(code, expect, out.getvalue())
        return out.getvalue()

    def read(self, table: str):
        return json.loads((self.over / f"masters_{table}.json").read_text(encoding="utf-8"))


class FieldAndRowMergeTests(MergeBase):
    def test_repo_modify_add_delete_with_override_only_edits_kept(self):
        base = [row(1, a=1, b=1), row(2, a=2, b=2), row(3, a=3, b=3)]
        repo = [row(1, a=10, b=1), row(3, a=3, b=3), row(4, a=4, b=4)]       # 1 modified, 2 deleted, 4 added
        override = [row(1, a=1, b=99), row(2, a=2, b=2), row(3, a=33, b=3), row(7, a=7, b=7)]
        self.put(self.base, "t", base); self.put(self.repo, "t", repo); self.put(self.over, "t", override)

        self.run_cli()

        result = self.read("t")
        by_id = {r["id"]: r for r in result}
        self.assertEqual(by_id[1], row(1, a=10, b=99))        # repo field wins, override-only field edit kept
        self.assertNotIn(2, by_id)                            # deleted in repo
        self.assertEqual(by_id[3], row(3, a=33, b=3))         # untouched by repo: override edit survives
        self.assertEqual(by_id[4], row(4, a=4, b=4))          # added in repo
        self.assertEqual(by_id[7], row(7, a=7, b=7))          # override-only row kept

    def test_conflict_repo_wins_and_is_logged(self):
        self.put(self.base, "t", [row(1, a=1)]); self.put(self.repo, "t", [row(1, a=5)])
        self.put(self.over, "t", [row(1, a=3)])
        out = self.run_cli()
        self.assertEqual(self.read("t"), [row(1, a=5)])
        self.assertIn("CONFLICT", out)
        self.assertIn("override edited it too", out)

    def test_deleted_in_repo_but_modified_in_override_is_logged(self):
        self.put(self.base, "t", [row(1, a=1), row(2, a=2)]); self.put(self.repo, "t", [row(1, a=1)])
        self.put(self.over, "t", [row(1, a=1), row(2, a=222)])
        out = self.run_cli()
        self.assertEqual(self.read("t"), [row(1, a=1)])
        self.assertIn("deleted in repo but modified in override", out)

    def test_row_deleted_in_override_stays_deleted_unless_repo_changed_it(self):
        self.put(self.base, "t", [row(1, a=1), row(2, a=2), row(3, a=3)])
        self.put(self.repo, "t", [row(1, a=1), row(2, a=2), row(3, a=30), row(4, a=4)])
        self.put(self.over, "t", [row(1, a=1)])               # balance team removed 2 and 3
        out = self.run_cli()
        ids = [r["id"] for r in self.read("t")]
        self.assertNotIn(2, ids)                              # repo never touched row 2: stays removed
        self.assertIn(3, ids)                                 # repo changed row 3: restored (repo wins)
        self.assertIn(4, ids)
        self.assertIn("deleted in override but changed in repo", out)

    def test_field_added_and_removed_in_repo(self):
        self.put(self.base, "t", [row(1, a=1, old=5)]); self.put(self.repo, "t", [row(1, a=1, new=7)])
        self.put(self.over, "t", [row(1, a=1, old=5)])
        self.run_cli()
        self.assertEqual(self.read("t"), [row(1, a=1, new=7)])

    def test_added_row_is_placed_after_its_repo_predecessor(self):
        self.put(self.base, "t", [row(1), row(3)]); self.put(self.repo, "t", [row(1), row(2), row(3)])
        self.put(self.over, "t", [row(1), row(3)])
        self.run_cli()
        self.assertEqual([r["id"] for r in self.read("t")], [1, 2, 3])

    def test_unchanged_table_and_table_without_override_are_not_touched(self):
        rows = [row(1, a=1)]
        self.put(self.base, "same", rows); self.put(self.repo, "same", rows)
        original = webui_text([row(1, a=999)])
        path = self.put(self.over, "same", None, text=original)
        mtime = path.stat().st_mtime_ns
        self.put(self.base, "noov", rows); self.put(self.repo, "noov", [row(1, a=2)])     # no override file
        self.run_cli()
        self.assertEqual(path.read_text(), original)
        self.assertEqual(path.stat().st_mtime_ns, mtime)
        self.assertFalse((self.over / "masters_noov.json").exists())
        self.assertFalse(self.bak.exists())

    def test_table_new_in_repo_with_existing_override_merges_by_id(self):
        self.put(self.repo, "t", [row(1, a=1), row(2, a=2)])               # absent in BASE
        self.put(self.over, "t", [row(1, a=9, extra=1)])
        self.run_cli()
        self.assertEqual(self.read("t"), [row(1, a=1, extra=1), row(2, a=2)])


class NoStableKeyTests(MergeBase):
    def test_table_without_ids_is_replaced_with_loud_warning(self):
        base = [{"k": 1, "v": 1}]; repo = [{"k": 1, "v": 2}, {"k": 2, "v": 3}]
        self.put(self.base, "nokey", base); self.put(self.repo, "nokey", repo)
        self.put(self.over, "nokey", [{"k": 1, "v": 77}])
        out = self.run_cli()
        self.assertEqual(self.read("nokey"), repo)
        self.assertIn("WARNING no unique 'id' key", out)
        self.assertIn("tables without a stable id key: nokey", out)

    def test_duplicate_ids_count_as_no_stable_key(self):
        self.put(self.base, "dup", [row(1, a=1)]); self.put(self.repo, "dup", [row(1, a=1), row(1, a=2)])
        self.put(self.over, "dup", [row(1, a=1)])
        out = self.run_cli()
        self.assertEqual(self.read("dup"), [row(1, a=1), row(1, a=2)])
        self.assertIn("WARNING", out)


class FormattingAndWriteTests(MergeBase):
    def test_webui_format_is_reproduced_byte_for_byte(self):
        self.put(self.base, "t", [row(1, name="日本語", a=1.0)]); self.put(self.repo, "t", [row(1, name="日本語", a=2.5)])
        path = self.put(self.over, "t", [row(1, name="日本語", a=1.0)])
        self.run_cli()
        self.assertEqual(path.read_text(encoding="utf-8"), webui_text([row(1, name="日本語", a=2.5)]))

    def test_other_conventions_are_preserved(self):
        base = [row(1, a="é")]; repo = [row(1, a="é"), row(2, a="ü")]
        self.put(self.base, "t", base); self.put(self.repo, "t", repo)
        style_text = json.dumps(base, ensure_ascii=True, indent=2)             # indent 2, \u escapes, no final newline
        path = self.put(self.over, "t", None, text=style_text)
        self.run_cli()
        self.assertEqual(path.read_text(), json.dumps(repo, ensure_ascii=True, indent=2))

    def test_backup_uses_webui_naming_and_original_bytes_and_perms_are_kept(self):
        self.put(self.base, "skill_condition", [row(1, a=1)]); self.put(self.repo, "skill_condition", [row(1, a=2)])
        path = self.put(self.over, "skill_condition", [row(1, a=1)])
        original = path.read_bytes()
        os.chmod(path, 0o664)
        self.run_cli()
        backups = list(self.bak.glob("skill_condition-*.json"))
        self.assertEqual(len(backups), 1)
        self.assertRegex(backups[0].name, r"^skill_condition-\d{8}-\d{6}(-\d+)?\.json$")
        self.assertEqual(backups[0].read_bytes(), original)
        self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o664)
        self.assertEqual([p.name for p in self.over.iterdir()], ["masters_skill_condition.json"])   # no temp leftovers

    def test_dry_run_writes_nothing_and_prints_the_plan(self):
        self.put(self.base, "t", [row(1, a=1)]); self.put(self.repo, "t", [row(1, a=2)])
        path = self.put(self.over, "t", [row(1, a=1)])
        before = path.read_bytes()
        out = self.run_cli("--dry-run")
        self.assertEqual(path.read_bytes(), before)
        self.assertFalse(self.bak.exists())
        self.assertIn("id=1 a: 1 -> 2", out)
        self.assertIn("DRY RUN", out)


class IdempotencyAndSafetyTests(MergeBase):
    def test_second_run_is_a_noop(self):
        self.put(self.base, "t", [row(1, a=1), row(2, a=2)])
        self.put(self.repo, "t", [row(1, a=5), row(3, a=3)])
        path = self.put(self.over, "t", [row(1, a=1), row(2, a=2), row(9, a=9)])
        self.run_cli()
        first = path.read_bytes()
        mtime = path.stat().st_mtime_ns
        self.run_cli()
        self.assertEqual(path.read_bytes(), first)
        self.assertEqual(path.stat().st_mtime_ns, mtime)
        self.assertEqual(len(list(self.bak.glob("*.json"))), 1)

    def test_invalid_override_aborts_and_leaves_every_table_untouched(self):
        self.put(self.base, "a", [row(1, a=1)]); self.put(self.repo, "a", [row(1, a=2)])
        good = self.put(self.over, "a", [row(1, a=1)])
        self.put(self.base, "b", [row(1, a=1)]); self.put(self.repo, "b", [row(1, a=2)])
        (self.over / "masters_b.json").write_text("{not json", encoding="utf-8")
        before = good.read_bytes()
        out = self.run_cli(expect=1)
        self.assertIn("ERROR", out)
        self.assertEqual(good.read_bytes(), before)
        self.assertFalse(self.bak.exists())

    def test_concurrent_webui_save_is_detected_before_overwrite(self):
        self.put(self.base, "t", [row(1, a=1)]); self.put(self.repo, "t", [row(1, a=2)])
        path = self.put(self.over, "t", [row(1, a=1)])
        plans = mmo.plan_all(mmo.DirSource(self.base), mmo.DirSource(self.repo), self.over, None)
        path.write_text(webui_text([row(1, a=1, b=42)]))            # the WebUI saves after planning
        saved = path.read_bytes()
        with self.assertRaises(mmo.ConcurrentEdit):
            mmo.apply_plans(plans, self.over, self.bak)
        self.assertEqual(path.read_bytes(), saved)
        # a full run re-plans on the fresh bytes and keeps the WebUI edit
        self.run_cli()
        self.assertEqual(self.read("t"), [row(1, a=2, b=42)])

    def test_failure_after_a_partial_write_rolls_back(self):
        self.put(self.base, "a", [row(1, a=1)]); self.put(self.repo, "a", [row(1, a=2)])
        first = self.put(self.over, "a", [row(1, a=1)])
        self.put(self.base, "b", [row(1, a=1)]); self.put(self.repo, "b", [row(1, a=2)])
        second = self.put(self.over, "b", [row(1, a=1)])
        before = first.read_bytes()
        plans = mmo.plan_all(mmo.DirSource(self.base), mmo.DirSource(self.repo), self.over, None)
        real = mmo.atomic_write
        calls = {"n": 0}

        def flaky(path, data, expect_sha):
            calls["n"] += 1
            if path.name == "masters_b.json" and calls["n"] == 2:
                raise OSError("disk full")
            return real(path, data, expect_sha)

        mmo.atomic_write = flaky
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                with self.assertRaises(OSError):
                    mmo.apply_plans(plans, self.over, self.bak)
        finally:
            mmo.atomic_write = real
        self.assertEqual(first.read_bytes(), before)               # table a restored from its backup
        self.assertEqual(self.read("b"), [row(1, a=1)])
        self.assertIsNotNone(second)


class BaseResolutionTests(MergeBase):
    def setUp(self):
        super().setUp()
        self.app = self.root / "app"
        (self.app / ".local" / "masters-overrides").mkdir(parents=True)
        (self.app / ".ci" / "releases").mkdir(parents=True)
        (self.app / "config").mkdir()
        self.over = self.app / ".local" / "masters-overrides"

    def release(self, sha, rows):
        d = self.app / ".ci" / "releases" / sha / "config"
        d.mkdir(parents=True)
        self.put(d, "t", rows)
        return d

    def cli(self, *extra, expect=0):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = mmo.main(["--app-dir", str(self.app), "--repo-dir", str(self.repo), "--commit", SHA_B, *extra])
        self.assertEqual(code, expect, out.getvalue())
        return out.getvalue()

    def test_bootstrap_without_marker_uses_the_deployed_commit_and_then_writes_the_marker(self):
        (self.app / ".ci" / "current-sha").write_text(SHA_A + "\n")
        self.release(SHA_A, [row(1, a=1)])
        self.put(self.repo, "t", [row(1, a=2)])
        path = self.put(self.over, "t", [row(1, a=1, z=1)])
        out = self.cli()
        self.assertIn("bootstrap: deployed commit from .ci/current-sha", out)
        self.assertEqual(self.read("t"), [row(1, a=2, z=1)])
        marker = self.app / ".local" / "deployed-masters-commit"
        self.assertEqual(marker.read_text().strip(), SHA_B)
        self.assertTrue(path.exists())
        # next deploy of the same commit: marker == commit -> nothing to do
        again = self.cli()
        self.assertIn("already merged", again)

    def test_bootstrap_uses_marker_release_tree_when_present(self):
        (self.app / ".ci" / "current-sha").write_text(SHA_A + "\n")
        self.release(SHA_A, [row(1, a=1)])
        (self.app / ".local" / "deployed-masters-commit").write_text(SHA_A + "\n")
        self.put(self.repo, "t", [row(1, a=3)])
        self.put(self.over, "t", [row(1, a=1)])
        out = self.cli()
        self.assertIn("(marker:", out)
        self.assertEqual(self.read("t"), [row(1, a=3)])

    def test_bootstrap_falls_back_to_live_config_when_the_release_tree_is_gone(self):
        (self.app / ".ci" / "current-sha").write_text(SHA_A + "\n")
        self.put(self.app / "config", "t", [row(1, a=1)])
        self.put(self.repo, "t", [row(1, a=4)])
        self.put(self.over, "t", [row(1, a=1)])
        self.cli()
        self.assertEqual(self.read("t"), [row(1, a=4)])

    def test_no_determinable_base_aborts_instead_of_overwriting_everything(self):
        import shutil
        shutil.rmtree(self.app / "config")
        self.put(self.repo, "t", [row(1, a=4)])
        path = self.put(self.over, "t", [row(1, a=1)])
        before = path.read_bytes()
        out = self.cli(expect=1)
        self.assertIn("cannot determine BASE", out)
        self.assertEqual(path.read_bytes(), before)
        self.assertFalse((self.app / ".local" / "deployed-masters-commit").exists())

    def test_garbage_marker_aborts(self):
        (self.app / ".local" / "deployed-masters-commit").write_text("nonsense\n")
        self.put(self.repo, "t", [row(1)])
        self.put(self.over, "t", [row(1)])
        self.assertIn("refusing to guess", self.cli(expect=1))

    def test_marker_whose_tree_is_missing_aborts(self):
        (self.app / ".local" / "deployed-masters-commit").write_text(SHA_A + "\n")
        (self.app / ".ci" / "current-sha").write_text(SHA_B + "\n")
        self.put(self.repo, "t", [row(1)])
        self.put(self.over, "t", [row(1)])
        self.assertIn("has no tree", self.cli(expect=1))

    def test_dry_run_never_writes_the_marker(self):
        (self.app / ".ci" / "current-sha").write_text(SHA_A + "\n")
        self.release(SHA_A, [row(1, a=1)])
        self.put(self.repo, "t", [row(1, a=2)])
        self.put(self.over, "t", [row(1, a=1)])
        self.cli("--dry-run")
        self.assertFalse((self.app / ".local" / "deployed-masters-commit").exists())
        self.assertEqual(self.read("t"), [row(1, a=1)])


class GitSourceTests(unittest.TestCase):
    def test_git_revisions_as_base_and_repo(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            repo = root / "r"
            (repo / "config").mkdir(parents=True)
            env = {**os.environ, "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@t", "GIT_COMMITTER_NAME": "t",
                   "GIT_COMMITTER_EMAIL": "t@t"}

            def git(*args):
                subprocess.run(["git", "-C", str(repo), *args], check=True, env=env,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE)

            git("init", "-q")
            (repo / "config" / "masters_t.json").write_text(webui_text([row(1, a=1)]))
            git("add", "-A"); git("commit", "-qm", "one")
            (repo / "config" / "masters_t.json").write_text(webui_text([row(1, a=2), row(2, a=2)]))
            git("commit", "-qam", "two")
            over = root / "over"; over.mkdir()
            (over / "masters_t.json").write_text(webui_text([row(1, a=1)]))
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                code = mmo.main(["--git-root", str(repo), "--base-rev", "HEAD~1", "--repo-rev", "HEAD",
                                 "--override-dir", str(over), "--backup-dir", str(root / "bak")])
            self.assertEqual(code, 0, out.getvalue())
            self.assertEqual(json.loads((over / "masters_t.json").read_text()), [row(1, a=2), row(2, a=2)])


class RealConfigTests(unittest.TestCase):
    def test_every_checked_in_master_table_has_a_unique_id_key(self):
        """Tables lacking a stable key would be replaced wholesale by the merge; keep that list empty."""
        missing = []
        for path in sorted((REPO_ROOT / "config").glob("masters_*.json")):
            rows = json.loads(path.read_text(encoding="utf-8"))
            if rows and mmo.index_by_id(rows) is None:
                missing.append(path.name)
        self.assertEqual(missing, [])


if __name__ == "__main__":
    unittest.main()
