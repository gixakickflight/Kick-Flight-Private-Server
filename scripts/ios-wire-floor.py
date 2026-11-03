#!/usr/bin/env python3
"""Read or atomically latch the monotonic R52 iOS wire floor."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import sys
import tempfile
from typing import Any

SCHEMA = "kickflight.ios-wire-floor.v1"
R52_CONTRACT = "kickflight.ios-r52-test-profile.v1"
SHA_RE = re.compile(r"^[0-9a-f]{64}$")
COMMIT_RE = re.compile(r"^[0-9a-f]{40}$")


class FloorError(ValueError):
    pass


def _unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise FloorError("duplicate JSON field")
        result[key] = value
    return result


def read_floor(path: Path) -> dict[str, Any]:
    if path.is_symlink() or not path.is_file():
        raise FloorError("wire-floor state is missing or is not a regular file")
    try:
        state = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=_unique_object)
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise FloorError("wire-floor state is invalid") from exc
    if not isinstance(state, dict) or set(state) != {
        "schema", "assetVersion", "revisionFloor", "profileArchiveSha256", "firstActivatedByCommit"
    }:
        raise FloorError("wire-floor state fields are invalid")
    if state.get("schema") != SCHEMA or state.get("assetVersion") != 12346 or state.get("revisionFloor") != 52:
        raise FloorError("wire-floor state has an unsupported revision")
    if not isinstance(state.get("profileArchiveSha256"), str) or not SHA_RE.fullmatch(state["profileArchiveSha256"]):
        raise FloorError("wire-floor profile hash is invalid")
    if not isinstance(state.get("firstActivatedByCommit"), str) or not COMMIT_RE.fullmatch(state["firstActivatedByCommit"]):
        raise FloorError("wire-floor activation commit is invalid")
    return state


def latch_floor(path: Path, profile_sha256: str, commit: str) -> str:
    if not SHA_RE.fullmatch(profile_sha256) or not COMMIT_RE.fullmatch(commit):
        raise FloorError("wire-floor activation identity is invalid")
    if path.is_symlink():
        raise FloorError("wire-floor path is a symbolic link")
    if path.exists():
        state = read_floor(path)
        if state["profileArchiveSha256"] != profile_sha256:
            raise FloorError("wire floor is already latched to a different profile")
        return "already-latched"

    path.parent.mkdir(parents=True, exist_ok=True)
    state = {
        "schema": SCHEMA,
        "assetVersion": 12346,
        "revisionFloor": 52,
        "profileArchiveSha256": profile_sha256,
        "firstActivatedByCommit": commit,
    }
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            stream.write(json.dumps(state, sort_keys=True) + "\n")
            stream.flush()
            os.fsync(stream.fileno())
        temporary.chmod(0o444)
        os.replace(temporary, path)
        directory_fd = os.open(path.parent, os.O_RDONLY)
        try:
            os.fsync(directory_fd)
        finally:
            os.close(directory_fd)
    except Exception:
        temporary.unlink(missing_ok=True)
        raise
    return "latched"


def verify_release(release_dir: Path, profile_sha256: str) -> None:
    if not SHA_RE.fullmatch(profile_sha256):
        raise FloorError("expected R52 archive hash is invalid")
    config_path = release_dir / "config/ios-r52-profile-release.json"
    try:
        pin = json.loads(config_path.read_text(encoding="utf-8"), object_pairs_hook=_unique_object)
        options = (release_dir / "src/KickFlight.BootstrapApi/HarnessOptions.cs").read_text(encoding="utf-8")
        program = (release_dir / "src/KickFlight.BootstrapApi/Program.cs").read_text(encoding="utf-8")
        store = (release_dir / "src/KickFlight.BootstrapApi/IosR52TestProfileStore.cs").read_text(encoding="utf-8")
        compose = (release_dir / "deploy/docker-compose.vps.yml").read_text(encoding="utf-8")
        deploy = (release_dir / "scripts/deploy-vps-ci.sh").read_text(encoding="utf-8")
        appsettings = json.loads((release_dir / "src/KickFlight.BootstrapApi/appsettings.json").read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise FloorError("release is missing its R52 runtime contract") from exc

    expected = {
        "schemaVersion": 1,
        "assetVersion": 12346,
        "revisionFloor": 52,
        "testOnly": True,
        "complete": False,
        "runtimeContract": R52_CONTRACT,
        "archiveSha256": profile_sha256,
    }
    if not isinstance(pin, dict) or any(pin.get(key) != value for key, value in expected.items()):
        raise FloorError("release R52 pin does not match the latched profile")
    if not (release_dir / "scripts/ios-wire-floor.py").is_file() or \
            not (release_dir / "scripts/stage-ios-r52-profile.py").is_file() or \
            "EnableIosR52TestProfile" not in options or \
            "IosR52TestProfileStore" not in program or "iosProfileRevision" not in program or \
            "GetListDatabase" not in store or "CdnUrlFormat" not in store or \
            "Harness__EnableIosR52TestProfile" not in compose or \
            "Harness__IosR52TestProfilePath" not in compose or \
            "ios_wire_floor_active" not in deploy or "wait_for_r52_health" not in deploy:
        raise FloorError("release is missing the R52 API route, health, staging, or compose wiring")
    harness = appsettings.get("Harness") if isinstance(appsettings, dict) else None
    if not isinstance(harness, dict) or harness.get("EnableIosR52TestProfile") is not False or \
            harness.get("IosR52TestProfilePath") != ".local/ios-resources/12346-r52/profile":
        raise FloorError("release R52 default must remain disabled at the repository level")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    read = commands.add_parser("read")
    read.add_argument("state", type=Path)
    latch = commands.add_parser("latch")
    latch.add_argument("state", type=Path)
    latch.add_argument("--profile-sha256", required=True)
    latch.add_argument("--commit", required=True)
    verify = commands.add_parser("verify-release")
    verify.add_argument("release", type=Path)
    verify.add_argument("--profile-sha256", required=True)
    supports = commands.add_parser("supports-release")
    supports.add_argument("release", type=Path)
    supports.add_argument("--profile-sha256", required=True)
    args = parser.parse_args()
    try:
        if args.command == "read":
            print(read_floor(args.state)["profileArchiveSha256"])
        elif args.command == "latch":
            print(latch_floor(args.state, args.profile_sha256, args.commit))
        elif args.command == "verify-release":
            verify_release(args.release, args.profile_sha256)
            print("r52-release-compatible")
        else:
            verify_release(args.release, args.profile_sha256)
    except (FloorError, OSError):
        if args.command == "supports-release":
            return 1
        print("iOS wire-floor operation failed", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
