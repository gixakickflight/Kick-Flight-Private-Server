#!/usr/bin/env python3
"""Read or atomically latch the monotonic iOS test-profile wire floor (assetVersion, revision)."""
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
CONTRACT = "kickflight.ios-test-profile.v1"
# Backward-compat: releases published before the profile pipeline was generalized.
LEGACY_CONTRACT = "kickflight.ios-r52-test-profile.v1"
PIN_PATH = "config/ios-profile-release.json"
LEGACY_PIN_PATH = "config/ios-r52-profile-release.json"
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


def _positive(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) and value > 0


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
    if state.get("schema") != SCHEMA or not _positive(state.get("assetVersion")) or not _positive(state.get("revisionFloor")):
        raise FloorError("wire-floor state has an unsupported revision")
    if not isinstance(state.get("profileArchiveSha256"), str) or not SHA_RE.fullmatch(state["profileArchiveSha256"]):
        raise FloorError("wire-floor profile hash is invalid")
    if not isinstance(state.get("firstActivatedByCommit"), str) or not COMMIT_RE.fullmatch(state["firstActivatedByCommit"]):
        raise FloorError("wire-floor activation commit is invalid")
    return state


def latch_floor(path: Path, asset_version: int, revision: int, profile_sha256: str, commit: str) -> str:
    """Latch the floor. An existing floor may only be replaced by a strictly greater (assetVersion, revision)."""
    if not SHA_RE.fullmatch(profile_sha256) or not COMMIT_RE.fullmatch(commit) or \
            not _positive(asset_version) or not _positive(revision):
        raise FloorError("wire-floor activation identity is invalid")
    if path.is_symlink():
        raise FloorError("wire-floor path is a symbolic link")
    upgraded = False
    if path.exists():
        state = read_floor(path)
        current = (state["assetVersion"], state["revisionFloor"])
        if current == (asset_version, revision):
            if state["profileArchiveSha256"] != profile_sha256:
                raise FloorError("wire floor is already latched to a different profile")
            return "already-latched"
        if (asset_version, revision) < current:
            raise FloorError("wire floor is already latched to a newer profile")
        upgraded = True

    path.parent.mkdir(parents=True, exist_ok=True)
    state = {
        "schema": SCHEMA,
        "assetVersion": asset_version,
        "revisionFloor": revision,
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
    return "upgraded" if upgraded else "latched"


def _read_text(root: Path, relative: str, *alternatives: str) -> str:
    for name in (relative, *alternatives):
        candidate = root / name
        if candidate.is_file():
            return candidate.read_text(encoding="utf-8")
    raise OSError("missing release file")


def verify_release(release_dir: Path, profile_sha256: str, asset_version: int | None = None,
                   revision: int | None = None) -> None:
    """Check that a release can serve the latched profile.

    Accepts the generic pipeline (config/ios-profile-release.json) and the legacy r52 pipeline
    (config/ios-r52-profile-release.json) so a rollback onto an older release remains possible.
    """
    if not SHA_RE.fullmatch(profile_sha256):
        raise FloorError("expected profile archive hash is invalid")
    try:
        legacy = not (release_dir / PIN_PATH).is_file()
        pin_path = release_dir / (LEGACY_PIN_PATH if legacy else PIN_PATH)
        pin = json.loads(pin_path.read_text(encoding="utf-8"), object_pairs_hook=_unique_object)
        options = (release_dir / "src/KickFlight.BootstrapApi/HarnessOptions.cs").read_text(encoding="utf-8")
        program = (release_dir / "src/KickFlight.BootstrapApi/Program.cs").read_text(encoding="utf-8")
        store = _read_text(release_dir, "src/KickFlight.BootstrapApi/IosTestProfileStore.cs",
                           "src/KickFlight.BootstrapApi/IosR52TestProfileStore.cs")
        compose = (release_dir / "deploy/docker-compose.vps.yml").read_text(encoding="utf-8")
        deploy = (release_dir / "scripts/deploy-vps-ci.sh").read_text(encoding="utf-8")
        appsettings = json.loads((release_dir / "src/KickFlight.BootstrapApi/appsettings.json").read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise FloorError("release is missing its iOS profile runtime contract") from exc

    if not isinstance(pin, dict) or not _positive(pin.get("assetVersion")) or not _positive(pin.get("revisionFloor")):
        raise FloorError("release profile pin is invalid")
    target_asset_version = pin["assetVersion"] if asset_version is None else asset_version
    target_revision = pin["revisionFloor"] if revision is None else revision
    expected = {
        "schemaVersion": 1,
        "testOnly": True,
        "complete": False,
        "runtimeContract": LEGACY_CONTRACT if legacy else CONTRACT,
    }
    if (pin["assetVersion"], pin["revisionFloor"]) == (target_asset_version, target_revision):
        expected["archiveSha256"] = profile_sha256
    elif legacy:
        # The legacy pipeline can only serve the profile it pinned.
        raise FloorError("release profile pin does not match the latched profile")
    # A generic release may pin a different (typically newer, not yet activated) profile: its API and stager
    # are driven by the identity passed at runtime, so it can still serve the latched profile.
    if any(pin.get(key) != value for key, value in expected.items()):
        raise FloorError("release profile pin does not match the latched profile")
    stager = "scripts/stage-ios-r52-profile.py" if legacy else "scripts/stage-ios-profile.py"
    if legacy:
        tokens = [(options, "EnableIosR52TestProfile"), (program, "IosR52TestProfileStore"),
                  (program, "iosProfileRevision"), (store, "GetListDatabase"), (store, "CdnUrlFormat"),
                  (compose, "Harness__EnableIosR52TestProfile"), (compose, "Harness__IosR52TestProfilePath"),
                  (deploy, "ios_wire_floor_active"), (deploy, "wait_for_r52_health")]
    else:
        tokens = [(options, "EnableIosTestProfile"), (program, "IosTestProfileStore"),
                  (program, "iosProfileRevision"), (store, "GetListDatabase"), (store, "CdnUrlFormat"),
                  (compose, "Harness__EnableIosTestProfile"), (compose, "Harness__IosTestProfilePath"),
                  (deploy, "ios_wire_floor_active"), (deploy, "wait_for_ios_profile_health")]
    if not (release_dir / "scripts/ios-wire-floor.py").is_file() or not (release_dir / stager).is_file() or \
            any(token not in text for text, token in tokens):
        raise FloorError("release is missing the iOS profile API route, health, staging, or compose wiring")
    harness = appsettings.get("Harness") if isinstance(appsettings, dict) else None
    flag = "EnableIosR52TestProfile" if legacy else "EnableIosTestProfile"
    if not isinstance(harness, dict) or harness.get(flag) is not False:
        raise FloorError("release iOS profile default must remain disabled at the repository level")
    if legacy and harness.get("IosR52TestProfilePath") != ".local/ios-resources/12346-r52/profile":
        raise FloorError("release iOS profile default must remain disabled at the repository level")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    read = commands.add_parser("read", help="print the latched profile archive sha256")
    read.add_argument("state", type=Path)
    describe = commands.add_parser("describe", help="print '<assetVersion> <revision> <sha256>' of the latch")
    describe.add_argument("state", type=Path)
    latch = commands.add_parser("latch")
    latch.add_argument("state", type=Path)
    latch.add_argument("--asset-version", type=int, required=True)
    latch.add_argument("--revision", type=int, required=True)
    latch.add_argument("--profile-sha256", required=True)
    latch.add_argument("--commit", required=True)
    for name in ("verify-release", "supports-release"):
        verify = commands.add_parser(name)
        verify.add_argument("release", type=Path)
        verify.add_argument("--profile-sha256", required=True)
        verify.add_argument("--asset-version", type=int)
        verify.add_argument("--revision", type=int)
    args = parser.parse_args()
    try:
        if args.command == "read":
            print(read_floor(args.state)["profileArchiveSha256"])
        elif args.command == "describe":
            state = read_floor(args.state)
            print(state["assetVersion"], state["revisionFloor"], state["profileArchiveSha256"])
        elif args.command == "latch":
            print(latch_floor(args.state, args.asset_version, args.revision, args.profile_sha256, args.commit))
        elif args.command == "verify-release":
            verify_release(args.release, args.profile_sha256, args.asset_version, args.revision)
            print("ios-profile-release-compatible")
        else:
            verify_release(args.release, args.profile_sha256, args.asset_version, args.revision)
    except (FloorError, OSError):
        if args.command == "supports-release":
            return 1
        print("iOS wire-floor operation failed", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
