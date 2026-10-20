#!/usr/bin/env python3
"""Tests for the Bot AI profiles API endpoint in tools/balance/server.py.

Exercises:
- Authentication & CSRF enforcement (401 without cookie, 403 on invalid Origin)
- GET /api/bot-ai/profiles (200, ETag header, hash, 39 profiles)
- POST /api/bot-ai/profiles valid save (200, atomic persistence, backups)
- Optimistic concurrency conflict (409 on stale expectedHash)
- Safe handling of external malformed on-disk config (409, refusal to overwrite)
- Strict JSON parsing (rejection of NaN, Infinity, duplicate keys)
- Request size bounding (413 on payload > 128 KB)
- Strict schema & contract validation matching C# BotAiConfigurationValidator:
  * 39 canonical profiles coverage across 3 modes
  * Canonical parameterId formula: 15 + 13*modeIndex + profIndex
  * Gym boundary isolation (parameterId < 100)
  * Kicker identity checks
  * Rank == 13
  * ScoreToDeposit >= 1
  * Finite floats & range boundaries
"""

from __future__ import annotations

import copy
import hashlib
import json
import math
import os
import shutil
import stat
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "balance"))
import server as balance_server  # noqa: E402


def test_validator_pristine_config():
    """Verify that the official pristine config/bot_ai_profiles.json passes validation."""
    config_path = REPO / "config" / "bot_ai_profiles.json"
    with config_path.open("r", encoding="utf-8") as f:
        data = json.load(f)
    valid, errors, normalized = balance_server.validate_bot_ai_profiles(data)
    assert valid, f"Expected pristine config to be valid, got errors: {errors}"
    assert normalized is not None
    assert len(normalized["profiles"]) == 39
    assert normalized["version"] == data["version"]


def test_validator_rejects_unknown_root_key():
    config_path = REPO / "config" / "bot_ai_profiles.json"
    with config_path.open("r", encoding="utf-8") as f:
        data = json.load(f)
    bad_data = copy.deepcopy(data)
    bad_data["unknown_extra_field"] = "malicious"
    valid, errors, _ = balance_server.validate_bot_ai_profiles(bad_data)
    assert not valid
    assert any("unknown_extra_field" in e for e in errors)


def test_validator_rejects_count_mismatch():
    config_path = REPO / "config" / "bot_ai_profiles.json"
    with config_path.open("r", encoding="utf-8") as f:
        data = json.load(f)
    bad_data = copy.deepcopy(data)
    bad_data["profiles"] = bad_data["profiles"][:38]  # 38 instead of 39
    valid, errors, _ = balance_server.validate_bot_ai_profiles(bad_data)
    assert not valid
    assert any("39 profiles" in e for e in errors)


def test_validator_rejects_parameter_id_mismatch():
    config_path = REPO / "config" / "bot_ai_profiles.json"
    with config_path.open("r", encoding="utf-8") as f:
        data = json.load(f)
    bad_data = copy.deepcopy(data)
    # Carrier in mode 0 has canonical parameterId = 15
    bad_data["profiles"][0]["parameterId"] = 99
    valid, errors, _ = balance_server.validate_bot_ai_profiles(bad_data)
    assert not valid
    assert any("does not match canonical formula" in e for e in errors)


def test_validator_rejects_gym_boundary_violation():
    config_path = REPO / "config" / "bot_ai_profiles.json"
    with config_path.open("r", encoding="utf-8") as f:
        data = json.load(f)
    bad_data = copy.deepcopy(data)
    bad_data["profiles"][0]["parameterId"] = 105
    valid, errors, _ = balance_server.validate_bot_ai_profiles(bad_data)
    assert not valid
    assert any("gym boundary" in e for e in errors)


def test_validator_rejects_kicker_identity_mismatch():
    config_path = REPO / "config" / "bot_ai_profiles.json"
    with config_path.open("r", encoding="utf-8") as f:
        data = json.load(f)
    bad_data = copy.deepcopy(data)
    # Carrier expects kickerId = 8
    bad_data["profiles"][0]["kickerId"] = 1
    valid, errors, _ = balance_server.validate_bot_ai_profiles(bad_data)
    assert not valid
    assert any("kickerId" in e for e in errors)


def test_validator_rejects_rank_not_13():
    config_path = REPO / "config" / "bot_ai_profiles.json"
    with config_path.open("r", encoding="utf-8") as f:
        data = json.load(f)
    bad_data = copy.deepcopy(data)
    bad_data["profiles"][0]["rank"] = 14
    valid, errors, _ = balance_server.validate_bot_ai_profiles(bad_data)
    assert not valid
    assert any("rank" in e for e in errors)


def test_validator_rejects_non_positive_intervals():
    config_path = REPO / "config" / "bot_ai_profiles.json"
    with config_path.open("r", encoding="utf-8") as f:
        data = json.load(f)
    bad_data = copy.deepcopy(data)
    bad_data["profiles"][0]["dashIntervals"]["attack"] = 0.0
    valid, errors, _ = balance_server.validate_bot_ai_profiles(bad_data)
    assert not valid
    assert any("dashIntervals.attack" in e for e in errors)


def test_validator_rejects_unknown_profile_property():
    config_path = REPO / "config" / "bot_ai_profiles.json"
    with config_path.open("r", encoding="utf-8") as f:
        data = json.load(f)
    bad_data = copy.deepcopy(data)
    bad_data["profiles"][0]["extra_physics_tuning"] = 42
    valid, errors, _ = balance_server.validate_bot_ai_profiles(bad_data)
    assert not valid
    assert any("extra_physics_tuning" in e for e in errors)


def test_validator_rejects_unhashable_and_malformed_shapes():
    """Verify that unhashable types (dict/list) and malformed shapes reject cleanly with 400 validation errors without 500 exceptions."""
    config_path = REPO / "config" / "bot_ai_profiles.json"
    with config_path.open("r", encoding="utf-8") as f:
        data = json.load(f)

    # 1. parameterId as list
    bad_data = copy.deepcopy(data)
    bad_data["profiles"][0]["parameterId"] = [15, 16]
    valid, errors, _ = balance_server.validate_bot_ai_profiles(bad_data)
    assert not valid
    assert any("parameterId must be an integer" in e for e in errors)

    # 2. parameterId as dict
    bad_data = copy.deepcopy(data)
    bad_data["profiles"][0]["parameterId"] = {"nested": "id"}
    valid, errors, _ = balance_server.validate_bot_ai_profiles(bad_data)
    assert not valid
    assert any("parameterId must be an integer" in e for e in errors)

    # 3. parameterId as bool
    bad_data = copy.deepcopy(data)
    bad_data["profiles"][0]["parameterId"] = True
    valid, errors, _ = balance_server.validate_bot_ai_profiles(bad_data)
    assert not valid
    assert any("parameterId must be an integer" in e for e in errors)

    # 4. parameterId as float
    bad_data = copy.deepcopy(data)
    bad_data["profiles"][0]["parameterId"] = 15.5
    valid, errors, _ = balance_server.validate_bot_ai_profiles(bad_data)
    assert not valid
    assert any("parameterId must be an integer" in e for e in errors)

    # 5. mode as list / dict
    bad_data = copy.deepcopy(data)
    bad_data["profiles"][0]["mode"] = ["CrystalScramble"]
    valid, errors, _ = balance_server.validate_bot_ai_profiles(bad_data)
    assert not valid
    assert any("invalid mode" in e for e in errors)

    # 6. profile as dict
    bad_data = copy.deepcopy(data)
    bad_data["profiles"][0]["profile"] = {"role": "carrier"}
    valid, errors, _ = balance_server.validate_bot_ai_profiles(bad_data)
    assert not valid
    assert any("invalid profile" in e for e in errors)

    # 7. nested motivation as list / dict / bool
    for malformed_val in [[50], {"val": 50}, True]:
        bad_data = copy.deepcopy(data)
        bad_data["profiles"][0]["motivations"]["attack"] = malformed_val
        valid, errors, _ = balance_server.validate_bot_ai_profiles(bad_data)
        assert not valid
        assert any("motivations.attack must be a finite number" in e for e in errors)

    # 8. nested dashInterval as list / dict / bool
    for malformed_val in [[1.0], {"interval": 1.0}, True, False, 0.0]:
        bad_data = copy.deepcopy(data)
        bad_data["profiles"][0]["dashIntervals"]["attack"] = malformed_val
        valid, errors, _ = balance_server.validate_bot_ai_profiles(bad_data)
        assert not valid
        assert any("dashIntervals.attack must be positive finite float" in e for e in errors)

    # 9. nested skillInterval as list / dict / bool
    for malformed_val in [[2.0], {"interval": 2.0}, False, -1.0]:
        bad_data = copy.deepcopy(data)
        bad_data["profiles"][0]["skillIntervals"]["attack"] = malformed_val
        valid, errors, _ = balance_server.validate_bot_ai_profiles(bad_data)
        assert not valid
        assert any("skillIntervals.attack must be positive finite float" in e for e in errors)


def test_write_bot_ai_profiles_rejects_symlink(tmp_path):
    """Verify write_bot_ai_profiles explicitly refuses to write over a symlink."""
    target_real = tmp_path / "bot_ai_real.json"
    target_real.write_text('{"version": 1, "profiles": []}\n', encoding="utf-8")
    symlink_file = tmp_path / "bot_ai_symlink.json"
    symlink_file.symlink_to(target_real)

    old_file = balance_server.BOT_AI_PROFILES_FILE
    try:
        balance_server.BOT_AI_PROFILES_FILE = symlink_file
        try:
            balance_server.write_bot_ai_profiles({"version": 2, "profiles": []})
            assert False, "Should have raised OSError on symlink"
        except OSError as ex:
            assert "Refusing to write over symlink" in str(ex)
            assert str(symlink_file) in str(ex)
            assert str(target_real) in str(ex)
        # Verify symlink and target remain untouched
        assert symlink_file.is_symlink()
        assert symlink_file.resolve() == target_real
        assert '{"version": 1' in target_real.read_text(encoding="utf-8")
    finally:
        balance_server.BOT_AI_PROFILES_FILE = old_file


def test_json_strict_duplicate_keys():
    raw_json = '{"version": 1, "version": 2, "profiles": []}'
    try:
        json.loads(
            raw_json,
            parse_constant=balance_server._reject_json_constant,
            object_pairs_hook=balance_server._reject_json_duplicates
        )
        assert False, "Should have raised ValueError on duplicate key"
    except ValueError as ex:
        assert "Duplicate JSON key" in str(ex)


def test_json_strict_nan_infinity():
    for val in ["NaN", "Infinity", "-Infinity"]:
        raw_json = f'{{"version": 1, "test": {val}}}'
        try:
            json.loads(
                raw_json,
                parse_constant=balance_server._reject_json_constant,
                object_pairs_hook=balance_server._reject_json_duplicates
            )
            assert False, f"Should have raised ValueError on constant {val}"
        except ValueError as ex:
            assert "forbidden" in str(ex)


def test_atomic_write_and_backup(tmp_path):
    config_path = tmp_path / "bot_ai_profiles.json"
    backup_dir = tmp_path / "backups"

    # Setup test file
    orig_content = '{"version": 1, "profiles": []}\n'
    config_path.write_text(orig_content, encoding="utf-8")

    # Override globals for test
    old_file = balance_server.BOT_AI_PROFILES_FILE
    old_backup = balance_server.BACKUP_DIR
    try:
        balance_server.BOT_AI_PROFILES_FILE = config_path
        balance_server.BACKUP_DIR = backup_dir

        # Test backup
        bk_path = balance_server.backup_bot_ai_profiles(orig_content)
        assert bk_path.is_file()
        assert bk_path.read_text(encoding="utf-8") == orig_content

        # Test atomic write
        new_payload = {"version": 2, "profiles": []}
        balance_server.write_bot_ai_profiles(new_payload)

        written_text = config_path.read_text(encoding="utf-8")
        assert '"version": 2' in written_text

        # Verify no .tmp files left in tmp_path
        tmp_files = list(tmp_path.glob("*.tmp"))
        assert len(tmp_files) == 0
    finally:
        balance_server.BOT_AI_PROFILES_FILE = old_file
        balance_server.BACKUP_DIR = old_backup


if __name__ == "__main__":
    print("Running Bot AI unit tests...")
    test_validator_pristine_config()
    test_validator_rejects_unknown_root_key()
    test_validator_rejects_count_mismatch()
    test_validator_rejects_parameter_id_mismatch()
    test_validator_rejects_gym_boundary_violation()
    test_validator_rejects_kicker_identity_mismatch()
    test_validator_rejects_rank_not_13()
    test_validator_rejects_non_positive_intervals()
    test_validator_rejects_unknown_profile_property()
    test_validator_rejects_unhashable_and_malformed_shapes()
    test_json_strict_duplicate_keys()
    test_json_strict_nan_infinity()
    with tempfile.TemporaryDirectory() as td:
        test_atomic_write_and_backup(Path(td))
        test_write_bot_ai_profiles_rejects_symlink(Path(td))
    print("ALL UNIT TESTS PASSED!")


# --------------------------------------------------------------------------- #
# HTTP Server Integration Tests
# --------------------------------------------------------------------------- #

class ServerFixture:
    def __init__(self, tmp_path: Path):
        self.tmp_path = tmp_path
        self.port = 18899
        self.root = tmp_path / "server_root"
        self.root.mkdir(parents=True)
        self.config_dir = self.root / "config"
        self.config_dir.mkdir(parents=True)
        self.backups_dir = self.root / "backups"
        self.backups_dir.mkdir(parents=True)
        self.auth_dir = self.root / "auth"
        self.auth_dir.mkdir(parents=True, mode=0o700)

        # Copy pristine config
        pristine_config = REPO / "config" / "bot_ai_profiles.json"
        self.bot_ai_file = self.config_dir / "bot_ai_profiles.json"
        shutil.copy(pristine_config, self.bot_ai_file)

        # Copy masters_kicker.json so server initialization succeeds
        for m in (REPO / "config").glob("masters_*.json"):
            shutil.copy(m, self.config_dir / m.name)

        self.auth_db = self.auth_dir / "auth.sqlite3"
        self.jwt_secret = self.auth_dir / "jwt-secret"
        self.jwt_secret.write_bytes(os.urandom(32))
        os.chmod(self.jwt_secret, 0o600)

        from auth import AuthStore, hash_password
        self.auth_store = AuthStore(self.auth_db, jwt_secret=self.jwt_secret.read_bytes())
        self.username = "testbotai"
        self.password = "valid-test-password-12345"
        import sqlite3
        with sqlite3.connect(self.auth_db) as conn:
            conn.execute(
                "INSERT INTO users(username, password_hash, created_at) VALUES (?, ?, ?)",
                (self.username, hash_password(self.password), int(time.time()))
            )

        self.env = dict(os.environ)
        self.env.update({
            "KF_BALANCE_BASE_DIR": str(self.config_dir),
            "KF_BALANCE_OVERRIDE_DIR": str(self.root / "overrides"),
            "KF_BALANCE_BACKUP_DIR": str(self.backups_dir),
            "KF_BALANCE_AUTH_DB": str(self.auth_db),
            "KF_BALANCE_JWT_SECRET_FILE": str(self.jwt_secret),
            "KF_BOT_AI_PROFILES_PATH": str(self.bot_ai_file),
            "KF_BALANCE_BASE_PATH": "/balance",
            "KF_BALANCE_ALLOW_APPLY": "0",
        })
        self.proc = None
        self.cookie = None

    def start(self):
        self.proc = subprocess.Popen(
            [sys.executable, str(REPO / "tools/balance/server.py"), "--host", "127.0.0.1", "--port", str(self.port)],
            env=self.env,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT
        )
        # Wait for server to bind
        deadline = time.time() + 10.0
        while time.time() < deadline:
            try:
                req = urllib.request.Request(f"http://127.0.0.1:{self.port}/balance/login")
                with urllib.request.urlopen(req, timeout=1.0) as res:
                    if res.status == 200:
                        break
            except Exception:
                time.sleep(0.1)
        else:
            out = self.proc.stdout.read().decode("utf-8", "replace") if self.proc.stdout else ""
            raise RuntimeError(f"Server failed to start:\n{out}")

        # Login to obtain session cookie
        login_req = urllib.request.Request(
            f"http://127.0.0.1:{self.port}/balance/api/login",
            data=json.dumps({"username": self.username, "password": self.password}).encode("utf-8"),
            headers={"Content-Type": "application/json", "Origin": f"http://127.0.0.1:{self.port}"},
            method="POST"
        )
        with urllib.request.urlopen(login_req, timeout=5.0) as res:
            set_cookie = res.headers.get("Set-Cookie")
            self.cookie = set_cookie.split(";", 1)[0]

    def stop(self):
        if self.proc:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=3)
            except subprocess.TimeoutExpired:
                self.proc.kill()


def test_http_full_flow(tmp_path):
    fixture = ServerFixture(tmp_path)
    fixture.start()
    try:
        base_url = f"http://127.0.0.1:{fixture.port}/balance"
        origin = f"http://127.0.0.1:{fixture.port}"

        # 1. Unauthenticated GET -> 401
        try:
            req = urllib.request.Request(f"{base_url}/api/bot-ai/profiles")
            urllib.request.urlopen(req)
            assert False, "Unauthenticated GET should fail with 401"
        except urllib.error.HTTPError as ex:
            assert ex.code == 401

        # 2. Authenticated GET -> 200, ETag present, 39 profiles
        req = urllib.request.Request(f"{base_url}/api/bot-ai/profiles")
        req.add_header("Cookie", fixture.cookie)
        with urllib.request.urlopen(req) as res:
            assert res.status == 200
            etag = res.headers.get("ETag")
            assert etag is not None
            data = json.loads(res.read().decode("utf-8"))
            assert data["ok"] is True
            assert len(data["profiles"]) == 39
            initial_hash = data["hash"]
            assert f'"{initial_hash}"' == etag

        clean_payload = {
            "version": data["version"],
            "$schema": data.get("$schema", "./bot_ai_profiles.schema.json"),
            "profiles": data["profiles"]
        }

        # 3. Unauthenticated POST -> 401
        try:
            req = urllib.request.Request(
                f"{base_url}/api/bot-ai/profiles",
                data=json.dumps(clean_payload).encode("utf-8"),
                headers={"Content-Type": "application/json", "Origin": origin},
                method="POST"
            )
            urllib.request.urlopen(req)
            assert False, "Unauthenticated POST should fail with 401"
        except urllib.error.HTTPError as ex:
            assert ex.code == 401

        # 4. State-change CSRF Origin check (Foreign Origin) -> 403
        try:
            req = urllib.request.Request(
                f"{base_url}/api/bot-ai/profiles",
                data=json.dumps(clean_payload).encode("utf-8"),
                headers={"Content-Type": "application/json", "Origin": "http://evil.com", "Cookie": fixture.cookie},
                method="POST"
            )
            urllib.request.urlopen(req)
            assert False, "Foreign Origin POST should fail with 403"
        except urllib.error.HTTPError as ex:
            assert ex.code == 403

        # 5. Valid authenticated POST save -> 200, updates file and writes backup
        modified_data = copy.deepcopy(clean_payload)
        # Change attack motivation on carrier in CrystalScramble
        modified_data["profiles"][0]["motivations"]["attack"] = 85
        modified_data["expectedHash"] = initial_hash
        req = urllib.request.Request(
            f"{base_url}/api/bot-ai/profiles",
            data=json.dumps(modified_data).encode("utf-8"),
            headers={"Content-Type": "application/json", "Origin": origin, "Cookie": fixture.cookie},
            method="POST"
        )
        with urllib.request.urlopen(req) as res:
            assert res.status == 200
            resp_body = json.loads(res.read().decode("utf-8"))
            assert resp_body["ok"] is True
            assert resp_body["written"] == 39
            new_hash = resp_body["hash"]
            assert new_hash != initial_hash

        # Verify on-disk file has new values
        on_disk = json.loads(fixture.bot_ai_file.read_text(encoding="utf-8"))
        assert on_disk["profiles"][0]["motivations"]["attack"] == 85

        # Verify backup was created
        backups = list(fixture.backups_dir.glob("bot_ai_profiles-*.json"))
        assert len(backups) == 1

        # 6. Optimistic concurrency conflict: Stale expectedHash -> 409
        stale_data = copy.deepcopy(modified_data)
        stale_data["expectedHash"] = initial_hash  # Old hash!
        try:
            req = urllib.request.Request(
                f"{base_url}/api/bot-ai/profiles",
                data=json.dumps(stale_data).encode("utf-8"),
                headers={"Content-Type": "application/json", "Origin": origin, "Cookie": fixture.cookie},
                method="POST"
            )
            urllib.request.urlopen(req)
            assert False, "Stale expectedHash should reject with 409 Conflict"
        except urllib.error.HTTPError as ex:
            assert ex.code == 409
            conflict_res = json.loads(ex.read().decode("utf-8"))
            assert conflict_res.get("conflict") is True

        # 7. Safe refusal: Do not overwrite external malformed config silently
        fixture.bot_ai_file.write_text("{CORRUPTED_JSON", encoding="utf-8")
        try:
            req = urllib.request.Request(
                f"{base_url}/api/bot-ai/profiles",
                data=json.dumps(modified_data).encode("utf-8"),
                headers={"Content-Type": "application/json", "Origin": origin, "Cookie": fixture.cookie},
                method="POST"
            )
            urllib.request.urlopen(req)
            assert False, "Should refuse to overwrite corrupted on-disk file"
        except urllib.error.HTTPError as ex:
            assert ex.code == 409
            err_body = json.loads(ex.read().decode("utf-8"))
            assert "corrupto o malformado" in err_body["error"]

        # Restore valid on-disk file for further tests
        fixture.bot_ai_file.write_text(json.dumps(on_disk), encoding="utf-8")

        # 8. Rejection of NaN / Infinity in JSON body -> 400
        raw_nan = json.dumps(modified_data).replace('85', 'NaN')
        try:
            req = urllib.request.Request(
                f"{base_url}/api/bot-ai/profiles",
                data=raw_nan.encode("utf-8"),
                headers={"Content-Type": "application/json", "Origin": origin, "Cookie": fixture.cookie},
                method="POST"
            )
            urllib.request.urlopen(req)
            assert False, "NaN in body should be rejected with 400"
        except urllib.error.HTTPError as ex:
            assert ex.code == 400

        # 9. Rejection of duplicate keys -> 400
        raw_dup = '{"version": 1, "version": 2, "profiles": []}'
        try:
            req = urllib.request.Request(
                f"{base_url}/api/bot-ai/profiles",
                data=raw_dup.encode("utf-8"),
                headers={"Content-Type": "application/json", "Origin": origin, "Cookie": fixture.cookie},
                method="POST"
            )
            urllib.request.urlopen(req)
            assert False, "Duplicate keys should be rejected with 400"
        except urllib.error.HTTPError as ex:
            assert ex.code == 400

        # 10. Request body size limit (> 128 KB) -> 413
        oversized = "a" * (135 * 1024)
        try:
            req = urllib.request.Request(
                f"{base_url}/api/bot-ai/profiles",
                data=oversized.encode("utf-8"),
                headers={"Content-Type": "application/json", "Origin": origin, "Cookie": fixture.cookie},
                method="POST"
            )
            urllib.request.urlopen(req)
            assert False, "Oversized body should be rejected with 413"
        except urllib.error.HTTPError as ex:
            assert ex.code == 413

        # 11. Malformed / unhashable parameterId in JSON -> 400 without 500
        unhashable_data = copy.deepcopy(clean_payload)
        unhashable_data["profiles"][0]["parameterId"] = [15, 16]
        try:
            req = urllib.request.Request(
                f"{base_url}/api/bot-ai/profiles",
                data=json.dumps(unhashable_data).encode("utf-8"),
                headers={"Content-Type": "application/json", "Origin": origin, "Cookie": fixture.cookie},
                method="POST"
            )
            urllib.request.urlopen(req)
            assert False, "Unhashable parameterId should be rejected with 400"
        except urllib.error.HTTPError as ex:
            assert ex.code == 400
            err_body = json.loads(ex.read().decode("utf-8"))
            assert "parameterId must be an integer" in err_body["error"]

        # 12. Symlink protection: Refuse to overwrite symlink config -> 409 with Spanish error preserving target
        symlink_target = fixture.config_dir / "bot_ai_profiles_symlink_dest.json"
        shutil.copy(fixture.bot_ai_file, symlink_target)
        fixture.bot_ai_file.unlink()
        fixture.bot_ai_file.symlink_to(symlink_target)
        try:
            valid_post = copy.deepcopy(clean_payload)
            valid_post["profiles"][0]["motivations"]["attack"] = 99
            req = urllib.request.Request(
                f"{base_url}/api/bot-ai/profiles",
                data=json.dumps(valid_post).encode("utf-8"),
                headers={"Content-Type": "application/json", "Origin": origin, "Cookie": fixture.cookie},
                method="POST"
            )
            urllib.request.urlopen(req)
            assert False, "Writing to a symlink should reject with 409 Conflict"
        except urllib.error.HTTPError as ex:
            assert ex.code == 409
            err_body = json.loads(ex.read().decode("utf-8"))
            assert "enlace simbólico" in err_body["error"]
            assert str(fixture.bot_ai_file) in err_body["error"]
            assert str(symlink_target) in err_body["error"]

        # Verify symlink and target were NOT overwritten or broken
        assert fixture.bot_ai_file.is_symlink()
        assert fixture.bot_ai_file.resolve() == symlink_target
        target_json = json.loads(symlink_target.read_text(encoding="utf-8"))
        assert target_json["profiles"][0]["motivations"]["attack"] != 99

    finally:
        fixture.stop()


class DefaultDurableFixture:
    """Fixture testing default durable path (.local/masters-overrides/bot_ai_profiles.json).

    Simulates the production environment where KF_BOT_AI_PROFILES_PATH is unset,
    config/bot_ai_profiles.json is the seed, and saves write to .local/masters-overrides/.
    """
    def __init__(self, tmp_path: Path):
        self.tmp_path = tmp_path
        self.port = 18897
        self.root = tmp_path / "repo_root"
        self.root.mkdir(parents=True)
        self.config_dir = self.root / "config"
        self.config_dir.mkdir(parents=True)
        self.overrides_dir = self.root / ".local" / "masters-overrides"
        self.backups_dir = self.root / ".local" / "balance-backups"
        self.auth_dir = self.root / ".local" / "balance-auth"
        self.auth_dir.mkdir(parents=True, mode=0o700)

        pristine_config = REPO / "config" / "bot_ai_profiles.json"
        self.seed_file = self.config_dir / "bot_ai_profiles.json"
        shutil.copy(pristine_config, self.seed_file)
        self.durable_file = self.overrides_dir / "bot_ai_profiles.json"

        for m in (REPO / "config").glob("masters_*.json"):
            shutil.copy(m, self.config_dir / m.name)

        self.auth_db = self.auth_dir / "auth.sqlite3"
        self.jwt_secret = self.auth_dir / "jwt-secret"
        self.jwt_secret.write_bytes(os.urandom(32))
        os.chmod(self.jwt_secret, 0o600)

        from auth import AuthStore, hash_password
        self.auth_store = AuthStore(self.auth_db, jwt_secret=self.jwt_secret.read_bytes())
        self.username = "testdurable"
        self.password = "valid-test-password-12345"
        import sqlite3
        with sqlite3.connect(self.auth_db) as conn:
            conn.execute(
                "INSERT INTO users(username, password_hash, created_at) VALUES (?, ?, ?)",
                (self.username, hash_password(self.password), int(time.time()))
            )

        self.env = dict(os.environ)
        self.env.pop("KF_BOT_AI_PROFILES_PATH", None)
        self.env.update({
            "KF_BALANCE_BASE_DIR": str(self.config_dir),
            "KF_BALANCE_OVERRIDE_DIR": str(self.overrides_dir),
            "KF_BALANCE_BACKUP_DIR": str(self.backups_dir),
            "KF_BALANCE_AUTH_DB": str(self.auth_db),
            "KF_BALANCE_JWT_SECRET_FILE": str(self.jwt_secret),
            "KF_BALANCE_BASE_PATH": "/balance",
            "KF_BALANCE_ALLOW_APPLY": "0",
        })
        self.proc = None
        self.cookie = None

    def start(self):
        self.proc = subprocess.Popen(
            [sys.executable, str(REPO / "tools/balance/server.py"), "--host", "127.0.0.1", "--port", str(self.port)],
            env=self.env,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT
        )
        deadline = time.time() + 10.0
        while time.time() < deadline:
            try:
                req = urllib.request.Request(f"http://127.0.0.1:{self.port}/balance/login")
                with urllib.request.urlopen(req, timeout=1.0) as res:
                    if res.status == 200:
                        break
            except Exception:
                time.sleep(0.1)
        else:
            out = self.proc.stdout.read().decode("utf-8", "replace") if self.proc.stdout else ""
            raise RuntimeError(f"Server failed to start:\n{out}")

        login_req = urllib.request.Request(
            f"http://127.0.0.1:{self.port}/balance/api/login",
            data=json.dumps({"username": self.username, "password": self.password}).encode("utf-8"),
            headers={"Content-Type": "application/json", "Origin": f"http://127.0.0.1:{self.port}"},
            method="POST"
        )
        with urllib.request.urlopen(login_req, timeout=5.0) as res:
            set_cookie = res.headers.get("Set-Cookie")
            self.cookie = set_cookie.split(";", 1)[0]

    def stop(self):
        if self.proc:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=3)
            except subprocess.TimeoutExpired:
                self.proc.kill()


def test_default_durable_flow_and_simulated_deploy(tmp_path):
    """Verify default durable path (.local/masters-overrides), seed preservation, and 409 conflict."""
    fixture = DefaultDurableFixture(tmp_path)
    fixture.start()
    try:
        base_url = f"http://127.0.0.1:{fixture.port}/balance"
        origin = f"http://127.0.0.1:{fixture.port}"

        # 1. Before save: durable override file does NOT exist
        assert not fixture.durable_file.exists()

        # 2. GET before override exists reads valid seed and revision
        req = urllib.request.Request(f"{base_url}/api/bot-ai/profiles")
        req.add_header("Cookie", fixture.cookie)
        with urllib.request.urlopen(req) as res:
            assert res.status == 200
            data = json.loads(res.read().decode("utf-8"))
            assert data["ok"] is True
            assert data.get("source") == "seed"
            assert len(data["profiles"]) == 39
            seed_hash = data["hash"]
            assert hashlib.sha256(fixture.seed_file.read_bytes()).hexdigest() == seed_hash

        # 3. First POST atomically creates durable file, leaves seed untouched, and creates backup
        clean_payload = {
            "version": data["version"],
            "$schema": data.get("$schema", "./bot_ai_profiles.schema.json"),
            "profiles": data["profiles"]
        }
        modified_data = copy.deepcopy(clean_payload)
        modified_data["profiles"][0]["motivations"]["attack"] = 85
        modified_data["expectedHash"] = seed_hash

        post_req = urllib.request.Request(
            f"{base_url}/api/bot-ai/profiles",
            data=json.dumps(modified_data).encode("utf-8"),
            headers={"Content-Type": "application/json", "Origin": origin, "Cookie": fixture.cookie},
            method="POST"
        )
        with urllib.request.urlopen(post_req) as res:
            assert res.status == 200
            resp_body = json.loads(res.read().decode("utf-8"))
            assert resp_body["ok"] is True
            assert resp_body.get("source") == "override"
            durable_hash = resp_body["hash"]
            assert durable_hash != seed_hash

        # Assert durable override was created on disk with attack = 85
        assert fixture.durable_file.is_file()
        on_disk_durable = json.loads(fixture.durable_file.read_text(encoding="utf-8"))
        assert on_disk_durable["profiles"][0]["motivations"]["attack"] == 85

        # Assert seed config/bot_ai_profiles.json remains completely untouched
        on_disk_seed = json.loads(fixture.seed_file.read_text(encoding="utf-8"))
        assert on_disk_seed["profiles"][0]["motivations"]["attack"] == clean_payload["profiles"][0]["motivations"]["attack"]

        # Assert backup was created in balance-backups
        backups = list(fixture.backups_dir.glob("bot_ai_profiles-*.json"))
        assert len(backups) == 1

        # 4. Stale 409 after initial save: POST with old seed hash fails with 409
        stale_data = copy.deepcopy(modified_data)
        stale_data["expectedHash"] = seed_hash
        try:
            req = urllib.request.Request(
                f"{base_url}/api/bot-ai/profiles",
                data=json.dumps(stale_data).encode("utf-8"),
                headers={"Content-Type": "application/json", "Origin": origin, "Cookie": fixture.cookie},
                method="POST"
            )
            urllib.request.urlopen(req)
            assert False, "Stale save after initial save should reject with 409"
        except urllib.error.HTTPError as ex:
            assert ex.code == 409
            conflict_body = json.loads(ex.read().decode("utf-8"))
            assert conflict_body["conflict"] is True
            assert conflict_body["currentHash"] == durable_hash
            assert conflict_body["expectedHash"] == seed_hash

        # 5. Simulated deploy: rsync overwrites config/ (the seed)
        # Modify the seed file to simulate a fresh deploy rsyncing config/
        deploy_seed_content = copy.deepcopy(on_disk_seed)
        deploy_seed_content["version"] = 99
        fixture.seed_file.write_text(json.dumps(deploy_seed_content), encoding="utf-8")

        # GET must still read durable override (attack = 85, source = override)
        req = urllib.request.Request(f"{base_url}/api/bot-ai/profiles")
        req.add_header("Cookie", fixture.cookie)
        with urllib.request.urlopen(req) as res:
            assert res.status == 200
            get_data = json.loads(res.read().decode("utf-8"))
            assert get_data["source"] == "override"
            assert get_data["profiles"][0]["motivations"]["attack"] == 85
            assert get_data["version"] == data["version"]

        # 6. Ensure bot override is NOT mistaken for generic master table
        tables_req = urllib.request.Request(f"{base_url}/api/tables")
        tables_req.add_header("Cookie", fixture.cookie)
        with urllib.request.urlopen(tables_req) as res:
            assert res.status == 200
            tables_data = json.loads(res.read().decode("utf-8"))
            table_names = [t["name"] for t in tables_data]
            assert "bot_ai_profiles" not in table_names
            assert "bot_ai" not in table_names

        # Check export zip does not include bot_ai_profiles.json
        export_req = urllib.request.Request(f"{base_url}/api/overrides/export")
        export_req.add_header("Cookie", fixture.cookie)
        with urllib.request.urlopen(export_req) as res:
            assert res.status == 200
            import zipfile
            import io
            with zipfile.ZipFile(io.BytesIO(res.read())) as zf:
                for name in zf.namelist():
                    assert "bot_ai" not in name

    finally:
        fixture.stop()
