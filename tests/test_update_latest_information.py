#!/usr/bin/env python3
"""Automated tests for safe idempotent Latest Information update script and Nginx routing."""
from __future__ import annotations

import json
import importlib.util
from pathlib import Path
import re
import stat
import tempfile
import unittest

REPO_ROOT = Path(__file__).resolve().parent.parent
script_path = REPO_ROOT / "scripts" / "update-latest-information.py"
spec = importlib.util.spec_from_file_location("update_latest_information", script_path)
if spec is None or spec.loader is None:
    raise ImportError(f"Cannot load spec from {script_path}")
uli = importlib.util.module_from_spec(spec)
spec.loader.exec_module(uli)


SAMPLE_SOURCE_DOCS = """<!doctype html>
<html lang="en">
<head><title>Updates</title></head>
<body>
  <main class="wrap">
    <h1>Updates</h1>

    <article class="patch" id="v2-11-4">
      <div class="patch-head"><h2>Patch 2.11.4</h2><time datetime="2026-10-19">2026-10-19</time></div>
      <p class="headline">Experimental Scramble AI update. Requires new client APK.</p>
      <h3>Scramble Mode &amp; AI Roles</h3>
      <ul>
        <li>Introduced experimental Scramble role targeting.</li>
        <li>Introduced low HP carrier deposit threshold.</li>
        <li>Standalone announcement and details: <a href="/updates-2.11.4.html">Release 2.11.4 Announcement</a>.</li>
      </ul>
    </article>

    <article class="patch" id="matchmaking-roles-2026-10-19">
      <div class="patch-head"><h2>New matchmaking and more bot variety</h2><time datetime="2026-10-19">2026-10-19</time></div>
      <p class="headline">Better team composition.</p>
    </article>

    <article class="patch" id="v2-11-3">
      <div class="patch-head"><h2>Patch 2.11.3</h2><time datetime="2026-10-11">2026-10-11</time></div>
      <p class="headline">Stability update.</p>
    </article>

    <article class="patch" id="v2-11-2">
      <div class="patch-head"><h2>Patch 2.11.2</h2><time datetime="2026-10-10">2026-10-10</time></div>
      <p class="headline">Replays are back!</p>
    </article>
  </main>
</body>
</html>
"""

SAMPLE_LIVE_SNAPSHOT = {
    "content": "# Kick-Flight Fenix - Patch 2.11.2\n\n## Replays are back!",
    "mode": "html",
    "htmlContent": """<!doctype html>
<html lang="en">
<head><title>Kick Flight Updates</title></head>
<body>
  <main class="wrap">
    <h1>Updates</h1>

    <article class="patch" id="matchmaking-roles-2026-10-19">
      <div class="patch-head"><h2>New matchmaking and more bot variety</h2><time datetime="2026-10-19">2026-10-19</time></div>
      <p class="headline">Better team composition.</p>
    </article>

    <article class="patch" id="v2-11-3">
      <div class="patch-head"><h2>Patch 2.11.3</h2><time datetime="2026-10-11">2026-10-11</time></div>
      <p class="headline">Stability update.</p>
    </article>

    <article class="patch" id="v2-11-2">
      <div class="patch-head"><h2>Patch 2.11.2</h2><time datetime="2026-10-10">2026-10-10</time></div>
      <p class="headline">Replays are back!</p>
    </article>
  </main>
</body>
</html>""",
    "externalUrl": "",
    "htmlResourceCsp": "default-src https:;",
    "defaultContent": "**Default text**",
    "changedAtUtc": "2026-10-19T14:56:37Z",
}


class TestUpdateLatestInformation(unittest.TestCase):
    def setUp(self) -> None:
        self.tmpdir = tempfile.TemporaryDirectory()
        self.tmp_path = Path(self.tmpdir.name)
        self.source_docs = self.tmp_path / "updates-page.html"
        self.source_docs.write_text(SAMPLE_SOURCE_DOCS, encoding="utf-8")
        self.state_file = self.tmp_path / "latest-information.json"
        self.backup_dir = self.tmp_path / "updates-backups"
        self.record_backup = self.tmp_path / "latest-info-backup"

    def tearDown(self) -> None:
        self.tmpdir.cleanup()

    def test_insert_into_existing_html_preserves_history_and_fields(self) -> None:
        self.state_file.write_text(json.dumps(SAMPLE_LIVE_SNAPSHOT, indent=2), encoding="utf-8")

        result = uli.update_latest_information(
            state_file=self.state_file,
            source_docs=self.source_docs,
            article_id="v2-11-4",
            backup_dir=self.backup_dir,
            record_backup_path=self.record_backup,
        )
        self.assertTrue(result)

        updated_data = json.loads(self.state_file.read_text(encoding="utf-8"))
        self.assertEqual(updated_data["mode"], "html")
        self.assertEqual(updated_data["content"], SAMPLE_LIVE_SNAPSHOT["content"])
        self.assertEqual(updated_data["externalUrl"], SAMPLE_LIVE_SNAPSHOT["externalUrl"])
        self.assertEqual(updated_data["htmlResourceCsp"], SAMPLE_LIVE_SNAPSHOT["htmlResourceCsp"])
        self.assertEqual(updated_data["defaultContent"], SAMPLE_LIVE_SNAPSHOT["defaultContent"])

        article_ids = uli.get_article_ids(updated_data["htmlContent"])
        self.assertEqual(
            article_ids,
            ["v2-11-4", "matchmaking-roles-2026-10-19", "v2-11-3", "v2-11-2"],
            "Prior announcement history must be preserved in exact order with v2-11-4 at top",
        )

    def test_idempotency_second_run_is_noop(self) -> None:
        self.state_file.write_text(json.dumps(SAMPLE_LIVE_SNAPSHOT, indent=2), encoding="utf-8")

        # First run: inserts
        r1 = uli.update_latest_information(
            state_file=self.state_file,
            source_docs=self.source_docs,
            article_id="v2-11-4",
            backup_dir=self.backup_dir,
        )
        self.assertTrue(r1)
        snapshot_after_first = self.state_file.read_text(encoding="utf-8")
        backup_files_count = len(list(self.backup_dir.glob("*.json")))
        self.assertEqual(backup_files_count, 1)

        # Second run: no-op
        r2 = uli.update_latest_information(
            state_file=self.state_file,
            source_docs=self.source_docs,
            article_id="v2-11-4",
            backup_dir=self.backup_dir,
        )
        self.assertFalse(r2)
        snapshot_after_second = self.state_file.read_text(encoding="utf-8")
        self.assertEqual(snapshot_after_first, snapshot_after_second)
        self.assertEqual(len(list(self.backup_dir.glob("*.json"))), 1, "No redundant backup on no-op")

    def test_backup_created_before_atomic_write(self) -> None:
        self.state_file.write_text(json.dumps(SAMPLE_LIVE_SNAPSHOT, indent=2), encoding="utf-8")

        uli.update_latest_information(
            state_file=self.state_file,
            source_docs=self.source_docs,
            article_id="v2-11-4",
            backup_dir=self.backup_dir,
            record_backup_path=self.record_backup,
        )
        self.assertTrue(self.record_backup.is_file())
        backup_path = Path(self.record_backup.read_text(encoding="utf-8").strip())
        self.assertTrue(backup_path.is_file())

        backup_data = json.loads(backup_path.read_text(encoding="utf-8"))
        self.assertEqual(backup_data["htmlContent"], SAMPLE_LIVE_SNAPSHOT["htmlContent"])
        self.assertNotIn("v2-11-4", uli.get_article_ids(backup_data["htmlContent"]))

    def test_missing_state_creates_valid_template(self) -> None:
        self.assertFalse(self.state_file.exists())

        result = uli.update_latest_information(
            state_file=self.state_file,
            source_docs=self.source_docs,
            article_id="v2-11-4",
            backup_dir=self.backup_dir,
        )
        self.assertTrue(result)
        self.assertTrue(self.state_file.is_file())

        created_data = json.loads(self.state_file.read_text(encoding="utf-8"))
        self.assertEqual(created_data["mode"], "html")
        self.assertIn("v2-11-4", uli.get_article_ids(created_data["htmlContent"]))
        self.assertIn("matchmaking-roles-2026-10-19", uli.get_article_ids(created_data["htmlContent"]))

    def test_invalid_mode_fails_closed_and_leaves_file_unchanged(self) -> None:
        text_snapshot = dict(SAMPLE_LIVE_SNAPSHOT)
        text_snapshot["mode"] = "text"
        original_content = json.dumps(text_snapshot, indent=2)
        self.state_file.write_text(original_content, encoding="utf-8")

        with self.assertRaises(ValueError) as cm:
            uli.update_latest_information(
                state_file=self.state_file,
                source_docs=self.source_docs,
                article_id="v2-11-4",
                backup_dir=self.backup_dir,
            )
        self.assertIn("Refusing to alter non-HTML mode", str(cm.exception))
        self.assertEqual(self.state_file.read_text(encoding="utf-8"), original_content)
        self.assertFalse(self.backup_dir.exists())

    def test_corrupt_json_fails_closed_and_leaves_file_unchanged(self) -> None:
        corrupt_text = "{ this is not valid json : [[{"
        self.state_file.write_text(corrupt_text, encoding="utf-8")

        with self.assertRaises(ValueError) as cm:
            uli.update_latest_information(
                state_file=self.state_file,
                source_docs=self.source_docs,
                article_id="v2-11-4",
            )
        self.assertIn("invalid JSON", str(cm.exception))
        self.assertEqual(self.state_file.read_text(encoding="utf-8"), corrupt_text)

    def test_corrupt_html_fails_closed_and_leaves_file_unchanged(self) -> None:
        bad_html_snapshot = dict(SAMPLE_LIVE_SNAPSHOT)
        bad_html_snapshot["htmlContent"] = "plain text without markup"
        original_content = json.dumps(bad_html_snapshot, indent=2)
        self.state_file.write_text(original_content, encoding="utf-8")

        with self.assertRaises(ValueError):
            uli.update_latest_information(
                state_file=self.state_file,
                source_docs=self.source_docs,
                article_id="v2-11-4",
            )
        self.assertEqual(self.state_file.read_text(encoding="utf-8"), original_content)

    def test_missing_article_in_docs_fails_closed(self) -> None:
        self.state_file.write_text(json.dumps(SAMPLE_LIVE_SNAPSHOT, indent=2), encoding="utf-8")

        with self.assertRaises(ValueError) as cm:
            uli.update_latest_information(
                state_file=self.state_file,
                source_docs=self.source_docs,
                article_id="non-existent-article-id",
            )
        self.assertIn("not found in source HTML", str(cm.exception))

    def test_casing_support_pascal_case_preserved(self) -> None:
        pascal_snapshot = {
            "Content": "# Header",
            "Mode": "html",
            "HtmlContent": SAMPLE_LIVE_SNAPSHOT["htmlContent"],
            "ExternalUrl": "",
            "HtmlResourceCsp": "default-src https:;",
            "DefaultContent": "default",
            "ChangedAtUtc": "2026-10-19T00:00:00Z",
        }
        self.state_file.write_text(json.dumps(pascal_snapshot, indent=2), encoding="utf-8")

        result = uli.update_latest_information(
            state_file=self.state_file,
            source_docs=self.source_docs,
            article_id="v2-11-4",
            backup_dir=self.backup_dir,
        )
        self.assertTrue(result)

        updated_data = json.loads(self.state_file.read_text(encoding="utf-8"))
        self.assertIn("HtmlContent", updated_data)
        self.assertIn("Mode", updated_data)
        self.assertIn("Content", updated_data)
        self.assertIn("ChangedAtUtc", updated_data)
        self.assertNotIn("htmlContent", updated_data)

    def test_preserve_permissions(self) -> None:
        self.state_file.write_text(json.dumps(SAMPLE_LIVE_SNAPSHOT, indent=2), encoding="utf-8")
        self.state_file.chmod(0o600)

        uli.update_latest_information(
            state_file=self.state_file,
            source_docs=self.source_docs,
            article_id="v2-11-4",
            backup_dir=self.backup_dir,
        )
        current_mode = stat.S_IMODE(self.state_file.stat().st_mode)
        self.assertEqual(current_mode, 0o600)

    def test_nginx_route_pattern_standalone_updates_isolated(self) -> None:
        nginx_conf = (REPO_ROOT / "deploy" / "nginx.vps.conf").read_text(encoding="utf-8")
        self.assertIn("location ~ ^/updates-[0-9]+\\.[0-9]+\\.[0-9]+.*\\.html$", nginx_conf)

        pattern = re.compile(r"^/updates-[0-9]+\.[0-9]+\.[0-9]+.*\.html$")
        self.assertTrue(pattern.match("/updates-2.11.4.html"))
        self.assertTrue(pattern.match("/updates-2.11.3.html"))
        self.assertTrue(pattern.match("/updates-2.11.4-beta.html"))

        # Must NOT match general /updates API webview route or legacy /updates.html redirect
        self.assertFalse(pattern.match("/updates"))
        self.assertFalse(pattern.match("/updates.html"))
        self.assertFalse(pattern.match("/updates/something"))
        self.assertFalse(pattern.match("/"))

        # Verify exact routes remain declared
        self.assertIn("location = /updates {", nginx_conf)
        self.assertIn("location = /updates.html { return 301 /updates; }", nginx_conf)

    def test_exact_observed_state_comment_pollution_recovery(self) -> None:
        """Reproduce exact live production corruption where v2-11-4 was trapped in an HTML comment."""
        corrupted_html = """<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>Kick Flight Updates</title>
  <!--
    Patch notes page. The live copy is the Latest Information HTML page (balance WebUI -> Latest information ->
    Content source "HTML page"); it is shown in-game and at https://kick-flight-fenix.us.ci/updates. This file is
    only the starting template. To add a patch: copy one <article class="patch" id="v2-11-4">
      <div class="patch-head"><h2>Patch 2.11.4</h2><time datetime="2026-10-19">2026-10-19</time></div>
      <p class="headline">Experimental Scramble AI update. Requires new client APK.</p>
      <h3>Scramble Mode &amp; AI Roles</h3>
      <ul>
        <li>Introduced experimental Scramble role targeting.</li>
        <li>Introduced low HP carrier deposit threshold.</li>
        <li>Standalone announcement and details: <a href="/updates-2.11.4.html">Release 2.11.4 Announcement</a>.</li>
      </ul>
    </article>

    <article class="patch"> block to the top of <main>.
    Links and images are absolute because the same page also opens inside the game's WebView.
  -->
</head>
<body>
  <header class="topbar">
    <div class="wrap topbar-inner">
      <a class="home-link" href="https://kick-flight-fenix.us.ci/">Download</a>
    </div>
  </header>
  <main class="wrap">
    <h1>Updates</h1>

    <article class="patch" id="matchmaking-roles-2026-10-19">
      <div class="patch-head"><h2>New matchmaking and more bot variety</h2><time datetime="2026-10-19">2026-10-19</time></div>
      <p class="headline">Better team composition.</p>
    </article>

    <article class="patch" id="v2-11-3">
      <div class="patch-head"><h2>Patch 2.11.3</h2><time datetime="2026-10-11">2026-10-11</time></div>
      <p class="headline">Stability update.</p>
    </article>

    <article class="patch" id="v2-11-2">
      <div class="patch-head"><h2>Patch 2.11.2</h2><time datetime="2026-10-10">2026-10-10</time></div>
      <p class="headline">Replays are back!</p>
    </article>
  </main>
</body>
</html>"""

        snapshot = dict(SAMPLE_LIVE_SNAPSHOT)
        snapshot["htmlContent"] = corrupted_html
        self.state_file.write_text(json.dumps(snapshot, indent=2), encoding="utf-8")

        # 1. Verify why previous claims were wrong (naive regex matches comment text)
        naive_ids = re.findall(r'<article[^>]*\bid=["\']([^"\']+)["\']', corrupted_html)
        self.assertEqual(naive_ids, ["v2-11-4", "matchmaking-roles-2026-10-19", "v2-11-3", "v2-11-2"])

        # But visible articles outside comments show matchmaking first!
        pre_visible_ids = uli.get_visible_article_ids(corrupted_html)
        self.assertEqual(pre_visible_ids, ["matchmaking-roles-2026-10-19", "v2-11-3", "v2-11-2"])

        # 2. Run fix
        result = uli.update_latest_information(
            state_file=self.state_file,
            source_docs=self.source_docs,
            article_id="v2-11-4",
            backup_dir=self.backup_dir,
            record_backup_path=self.record_backup,
        )
        self.assertTrue(result)

        # 3. Verify resulting visible articles and order
        updated_data = json.loads(self.state_file.read_text(encoding="utf-8"))
        updated_html = updated_data["htmlContent"]
        post_visible_ids = uli.get_visible_article_ids(updated_html)
        self.assertEqual(
            post_visible_ids,
            ["v2-11-4", "matchmaking-roles-2026-10-19", "v2-11-3", "v2-11-2"]
        )

        # 4. Verify comment has been sanitized (no article tags inside comment)
        comment_match = re.search(r'<!--([\s\S]*?)-->', updated_html)
        self.assertIsNotNone(comment_match)
        self.assertNotIn("id=\"v2-11-4\"", comment_match.group(1))
        self.assertNotIn("<article class=\"patch\" id=", comment_match.group(1))

        # 5. Verify v2-11-4 is inside <main> and precedes matchmaking-roles
        main_match = re.search(r'<main\b[^>]*>([\s\S]*?)</main>', updated_html)
        self.assertIsNotNone(main_match)
        main_content = main_match.group(1)
        v2114_pos = main_content.find('id="v2-11-4"')
        matchmaking_pos = main_content.find('id="matchmaking-roles-2026-10-19"')
        self.assertGreater(v2114_pos, 0)
        self.assertGreater(matchmaking_pos, v2114_pos)

        # 6. Verify idempotency on second run
        second_result = uli.update_latest_information(
            state_file=self.state_file,
            source_docs=self.source_docs,
            article_id="v2-11-4",
            backup_dir=self.backup_dir,
            record_backup_path=self.record_backup,
        )
        self.assertFalse(second_result)
        after_second_data = json.loads(self.state_file.read_text(encoding="utf-8"))
        self.assertEqual(
            uli.get_visible_article_ids(after_second_data["htmlContent"]),
            ["v2-11-4", "matchmaking-roles-2026-10-19", "v2-11-3", "v2-11-2"]
        )

    def test_fresh_template_with_instruction_comment_inserts_in_main(self) -> None:
        """Verify inserting into fresh template preserves instruction comment and targets main."""
        fresh_html = """<!doctype html>
<html lang="en">
<head>
  <title>Kick Flight Updates</title>
  <!--
    Patch notes page. The live copy is the Latest Information HTML page.
    To add a patch: copy one <article class="patch"> block to the top of <main>.
  -->
</head>
<body>
  <main class="wrap">
    <h1>Updates</h1>
    <article class="patch" id="matchmaking-roles-2026-10-19">
      <div class="patch-head"><h2>Matchmaking</h2></div>
    </article>
  </main>
</body>
</html>"""
        snapshot = dict(SAMPLE_LIVE_SNAPSHOT)
        snapshot["htmlContent"] = fresh_html
        self.state_file.write_text(json.dumps(snapshot, indent=2), encoding="utf-8")

        result = uli.update_latest_information(
            state_file=self.state_file,
            source_docs=self.source_docs,
            article_id="v2-11-4",
            backup_dir=self.backup_dir,
        )
        self.assertTrue(result)

        updated_data = json.loads(self.state_file.read_text(encoding="utf-8"))
        updated_html = updated_data["htmlContent"]
        visible_ids = uli.get_visible_article_ids(updated_html)
        self.assertEqual(visible_ids, ["v2-11-4", "matchmaking-roles-2026-10-19"])

        # Comment is untouched and preserved
        self.assertIn("To add a patch: copy one <article class=\"patch\"> block", updated_html)
        self.assertNotIn("v2-11-4", re.search(r'<!--([\s\S]*?)-->', updated_html).group(1))


if __name__ == "__main__":
    unittest.main()
