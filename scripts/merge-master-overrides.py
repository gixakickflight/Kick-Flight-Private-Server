#!/usr/bin/env python3
"""Three-way merge of repository master changes into the live balance overrides.

The API serves <override dir>/masters_<table>.json instead of config/masters_<table>.json whenever an
override exists (see MasterOverrides.cs), and deploys never touch .local/. Without this step a master fix
published in the repo is silently shadowed by the balance team's override file.

Priority is Repo -> Overrides -> Base. At deploy time, for every table that has an override file AND whose
repository JSON changed between the previously merged commit (BASE) and the new commit (REPO):

  * rows are matched by primary key ("id"); a table without a unique "id" on any side falls back to
    "the repo version replaces the whole override" and is reported loudly;
  * per field: REPO != BASE -> REPO wins (conflicts with an override edit are logged); REPO == BASE ->
    the override value is kept (balance edits survive);
  * rows added in REPO are added; rows deleted in REPO are deleted from the override; rows that exist only
    in the override are kept; a row the override deleted is only revived when REPO changed it;
  * tables with no override file are never touched (the API already serves the new repo file).

The override file keeps its own formatting (indent, trailing newline, ensure_ascii; the balance WebUI
writes json.dumps(rows, ensure_ascii=False, indent=1) + "\\n"). Each touched file is backed up first to
<backup dir>/<table>-<YYYYmmdd-HHMMSS>[-n].json (the WebUI naming convention), written atomically
(same-directory temp file + fsync + os.replace) with the original owner and mode, and re-checked for a
concurrent WebUI save right before the rename. All tables are computed in memory first: any error leaves
every override untouched and exits non-zero. A second run with the same BASE/REPO is a no-op.

BASE for the next run is the "deployed masters commit" marker (<app>/.local/deployed-masters-commit). When
the marker is absent (bootstrap) the commit currently deployed on the host is used (<app>/.ci/current-sha;
its tree is <app>/.ci/releases/<sha>/ or, failing that, the live <app>/config). It never degrades to
"overwrite everything": if no BASE can be determined the run aborts.

Usage (dry run on the VPS, before a deploy lands):
  python3 scripts/merge-master-overrides.py --app-dir /opt/kickflight \\
      --repo-dir <new release>/config --commit <new sha> --dry-run
Local / test usage with explicit trees or git revisions:
  python3 scripts/merge-master-overrides.py --git-root . --base-rev A --repo-rev B \\
      --override-dir DIR --backup-dir DIR --dry-run
"""
from __future__ import annotations

import argparse
import copy
import json
import os
import re
import subprocess
import sys
import tempfile
from dataclasses import dataclass, field
from datetime import datetime
from hashlib import sha256
from pathlib import Path
from typing import Any

PREFIX = "[masters-merge]"
SHA_RE = re.compile(r"^[0-9a-f]{40}$")
TABLE_FILE_RE = re.compile(r"^masters_([a-z_]+)\.json$")
MARKER_NAME = "deployed-masters-commit"
MAX_WRITE_RETRIES = 3


class MergeError(Exception):
    """Fail-safe error: nothing may be written when this is raised during planning."""


def log(message: str) -> None:
    print(f"{PREFIX} {message}", flush=True)


# --------------------------------------------------------------------------- #
# Sources of BASE / REPO master files: a directory or a git revision.
# --------------------------------------------------------------------------- #


class DirSource:
    def __init__(self, directory: Path):
        self.directory = Path(directory)
        self.label = str(directory)

    def read(self, file_name: str) -> bytes | None:
        path = self.directory / file_name
        return path.read_bytes() if path.is_file() else None


class GitSource:
    def __init__(self, git_root: Path, rev: str, subdir: str = "config"):
        self.git_root, self.rev, self.subdir = Path(git_root), rev, subdir
        self.label = f"git:{rev}"

    def read(self, file_name: str) -> bytes | None:
        spec = f"{self.rev}:{self.subdir}/{file_name}"
        result = subprocess.run(["git", "-C", str(self.git_root), "cat-file", "blob", spec],
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        return result.stdout if result.returncode == 0 else None


# --------------------------------------------------------------------------- #
# Formatting conventions of an override file
# --------------------------------------------------------------------------- #


@dataclass
class Style:
    indent: int | None = 1
    trailing_newline: bool = True
    ensure_ascii: bool = False
    bom: bool = False

    def dumps(self, rows: Any) -> str:
        text = json.dumps(rows, ensure_ascii=self.ensure_ascii, indent=self.indent)
        return text + ("\n" if self.trailing_newline else "")

    def encode(self, rows: Any) -> bytes:
        return (("﻿" if self.bom else "") + self.dumps(rows)).encode("utf-8")


def detect_style(raw: bytes) -> Style:
    text = raw.decode("utf-8-sig")
    style = Style(bom=raw.startswith(b"\xef\xbb\xbf"), trailing_newline=text.endswith("\n"))
    body = text.strip()
    if "\n" not in body:
        style.indent = None
    else:
        indents = [len(line) - len(line.lstrip(" ")) for line in body.split("\n")[1:] if line.strip()]
        positive = [n for n in indents if n > 0]
        style.indent = min(positive) if positive else 1
    style.ensure_ascii = ("\\u" in text) and all(ord(ch) < 128 for ch in text)
    return style


# --------------------------------------------------------------------------- #
# Parsing / keying
# --------------------------------------------------------------------------- #


def _reject_constant(value: str) -> Any:
    raise ValueError(f"non-standard JSON constant {value}")


def parse_rows(raw: bytes | None, what: str) -> list | None:
    """Parse a master file. None for an absent file; MergeError for malformed JSON or a non-array."""
    if raw is None:
        return None
    try:
        data = json.loads(raw.decode("utf-8-sig"), parse_constant=_reject_constant)
    except (ValueError, UnicodeDecodeError) as error:
        raise MergeError(f"{what}: invalid JSON ({error})") from error
    if not isinstance(data, list):
        raise MergeError(f"{what}: not a JSON array")
    return data


def key_of(value: Any) -> str:
    return json.dumps(value, sort_keys=True, ensure_ascii=True)


def index_by_id(rows: list | None) -> dict[str, dict] | None:
    """{key: row} when every row is an object with a unique "id", else None (no stable key)."""
    if rows is None:
        return {}
    index: dict[str, dict] = {}
    for row in rows:
        if not isinstance(row, dict) or "id" not in row or isinstance(row["id"], (dict, list)):
            return None
        key = key_of(row["id"])
        if key in index:
            return None
        index[key] = row
    return index


# --------------------------------------------------------------------------- #
# Merge
# --------------------------------------------------------------------------- #


@dataclass
class TablePlan:
    table: str
    mode: str = "keyed"                    # keyed | table-replace
    new_rows: list | None = None           # None when nothing changes
    changes: list[str] = field(default_factory=list)
    conflicts: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)
    override_raw: bytes = b""
    style: Style = field(default_factory=Style)
    format_stable: bool = True

    @property
    def changed(self) -> bool:
        return self.new_rows is not None


def _insert_after_predecessor(seq: list, item: Any, repo_order: list, item_key: Any, key_fn) -> None:
    """Insert item into seq right after its nearest repo-order predecessor that is present, else first."""
    present = {key_fn(entry): position for position, entry in enumerate(seq)}
    pos = repo_order.index(item_key)
    for previous in reversed(repo_order[:pos]):
        if previous in present:
            seq.insert(present[previous] + 1, item)
            return
    seq.insert(0, item)


def _merge_row(rid: str, base_row: dict | None, repo_row: dict, override_row: dict,
               plan: TablePlan) -> dict:
    base_row = base_row or {}
    merged = dict(override_row)
    repo_keys = list(repo_row.keys())
    for name in repo_keys:
        repo_changed = name not in base_row or repo_row[name] != base_row[name]
        if not repo_changed:
            continue                                   # repo untouched -> the override value (if any) stays
        if name in merged and merged[name] == repo_row[name]:
            continue                                   # already equal (idempotent re-run / manual fix)
        if name in merged:
            override_edited = name not in base_row or merged[name] != base_row[name]
            text = f"id={rid} {name}: {_short(merged[name])} -> {_short(repo_row[name])}"
            if override_edited:
                plan.conflicts.append(text + "  (override edited it too; repo wins)")
            plan.changes.append(f"~ {text}")
            merged[name] = repo_row[name]
        else:
            plan.changes.append(f"+ id={rid} {name}: {_short(repo_row[name])} (new field)")
            items = list(merged.items())
            at = len(items)
            pos = repo_keys.index(name)
            for previous in reversed(repo_keys[:pos]):
                found = next((i for i, (k, _) in enumerate(items) if k == previous), None)
                if found is not None:
                    at = found + 1
                    break
            items.insert(at, (name, copy.deepcopy(repo_row[name])))
            merged = dict(items)
    for name in list(base_row.keys()):
        if name not in repo_row and name in merged:     # field removed in repo
            if merged[name] != base_row[name]:
                plan.conflicts.append(f"id={rid} {name}: removed in repo, override had edited it; repo wins")
            plan.changes.append(f"- id={rid} {name}: removed ({_short(merged[name])})")
            del merged[name]
    return merged


def _short(value: Any) -> str:
    text = json.dumps(value, ensure_ascii=False)
    return text if len(text) <= 60 else text[:57] + "..."


def merge_table(table: str, base_raw: bytes | None, repo_raw: bytes | None,
                override_raw: bytes) -> TablePlan:
    plan = TablePlan(table=table, override_raw=override_raw)
    what = f"masters_{table}.json"
    override_rows = parse_rows(override_raw, f"override {what}")
    repo_rows = parse_rows(repo_raw, f"repo {what}")
    base_rows = parse_rows(base_raw, f"base {what}")
    assert override_rows is not None
    plan.style = detect_style(override_raw)
    plan.format_stable = plan.style.dumps(override_rows).encode("utf-8") == override_raw.decode("utf-8-sig").encode("utf-8")

    if repo_rows is None:
        plan.warnings.append("table no longer exists in the repo; override left untouched")
        return plan
    if base_rows is not None and base_rows == repo_rows:
        return plan                                     # semantically unchanged between BASE and REPO

    base_idx = index_by_id(base_rows)
    repo_idx = index_by_id(repo_rows)
    over_idx = index_by_id(override_rows)
    if base_idx is None or repo_idx is None or over_idx is None:
        # No stable key: repo wins for the whole table.
        plan.mode = "table-replace"
        sides = [name for name, idx in (("base", base_idx), ("repo", repo_idx), ("override", over_idx))
                 if idx is None]
        plan.warnings.append(f"WARNING no unique 'id' key in {', '.join(sides)}: repo replaces the WHOLE table "
                             f"(override edits to this table are discarded; see the backup)")
        if override_rows != repo_rows:
            plan.changes.append(f"table replaced: {len(override_rows)} override rows -> {len(repo_rows)} repo rows")
            plan.conflicts.append("whole-table replacement (no stable row key)")
            plan.new_rows = copy.deepcopy(repo_rows)
        return plan

    base_idx = base_idx or {}
    repo_order = [key_of(row["id"]) for row in repo_rows]
    result = [dict(row) if isinstance(row, dict) else row for row in override_rows]
    result_idx = {key_of(row["id"]): position for position, row in enumerate(result)}
    new_result: list[dict] = []
    deleted_in_repo = {k for k in base_idx if k not in repo_idx}

    for row in result:
        rid = key_of(row["id"])
        shown = json.dumps(row["id"])
        if rid in repo_idx:
            merged = _merge_row(shown, base_idx.get(rid), repo_idx[rid], row, plan)
            new_result.append(merged)
        elif rid in deleted_in_repo:
            if row != base_idx[rid]:
                plan.conflicts.append(f"id={shown}: deleted in repo but modified in override; repo wins (row removed)")
            plan.changes.append(f"- id={shown}: row deleted (removed in repo)")
        else:
            new_result.append(row)                      # override-only row: keep

    present_keys = {key_of(r["id"]) for r in new_result}
    for rid in repo_order:
        if rid in present_keys:
            continue
        in_base = rid in base_idx
        in_override = rid in result_idx
        repo_row = repo_idx[rid]
        if in_override:
            continue                                    # handled above
        if in_base and base_idx[rid] == repo_row:
            continue                                    # override deleted it, repo unchanged: stay deleted
        if in_base:
            plan.conflicts.append(f"id={json.dumps(repo_row['id'])}: deleted in override but changed in repo; "
                                  "repo wins (row restored)")
        plan.changes.append(f"+ id={json.dumps(repo_row['id'])}: row {'restored' if in_base else 'added'}")
        _insert_after_predecessor(new_result, copy.deepcopy(repo_row), repo_order, rid,
                                  lambda row: key_of(row["id"]) if isinstance(row, dict) else None)
        present_keys.add(rid)

    if new_result != override_rows:
        plan.new_rows = new_result
    return plan


# --------------------------------------------------------------------------- #
# BASE resolution
# --------------------------------------------------------------------------- #


def read_sha_file(path: Path) -> str | None:
    try:
        value = path.read_text(encoding="utf-8").strip()
    except OSError:
        return None
    return value if SHA_RE.match(value) else None


def resolve_base(marker: Path, releases_dir: Path, current_sha_file: Path,
                 fallback_dir: Path | None) -> tuple[str, Path, str]:
    """(base commit label, BASE config dir, how it was chosen). Aborts rather than guessing."""
    marker_sha = read_sha_file(marker) if marker.exists() else None
    if marker.exists() and marker_sha is None:
        raise MergeError(f"marker {marker} exists but does not hold a 40-hex commit; refusing to guess a BASE")
    current = read_sha_file(current_sha_file)
    if marker_sha is not None:
        release = releases_dir / marker_sha / "config"
        if release.is_dir():
            return marker_sha, release, "marker"
        if marker_sha == current and fallback_dir is not None and fallback_dir.is_dir():
            return marker_sha, fallback_dir, "marker (release tree gone; live config of the same commit)"
        raise MergeError(f"marker commit {marker_sha} has no tree at {release}; restore it or pass --base-dir")
    # Bootstrap: no marker yet -> the commit currently deployed on the host.
    if current is not None:
        release = releases_dir / current / "config"
        if release.is_dir():
            return current, release, "bootstrap: deployed commit from .ci/current-sha"
        if fallback_dir is not None and fallback_dir.is_dir():
            return current, fallback_dir, "bootstrap: .ci/current-sha, live config tree"
        raise MergeError(f"bootstrap: deployed commit {current} has no tree; pass --base-dir")
    if fallback_dir is not None and fallback_dir.is_dir():
        return "pre-ci", fallback_dir, "bootstrap: first CI run, pre-CI snapshot of the manual checkout"
    raise MergeError("bootstrap: no marker, no .ci/current-sha and no fallback tree; cannot determine BASE")


# --------------------------------------------------------------------------- #
# Writing
# --------------------------------------------------------------------------- #


def _fsync_dir(path: Path) -> None:
    try:
        fd = os.open(path, os.O_RDONLY)
    except OSError:
        return
    try:
        os.fsync(fd)
    except OSError:
        pass
    finally:
        os.close(fd)


def _copy_owner_mode(src: Path, dst: Path) -> None:
    info = src.stat()
    os.chmod(dst, info.st_mode & 0o7777)
    try:
        os.chown(dst, info.st_uid, info.st_gid)
    except PermissionError:
        pass  # not root and not the owner; keep the current user


def backup_override(backup_dir: Path, table: str, raw: bytes, owner_ref: Path, now: datetime) -> Path:
    """Same naming as the WebUI: <table>-<YYYYmmdd-HHMMSS>[-n].json containing the pre-write bytes."""
    backup_dir.mkdir(parents=True, exist_ok=True)
    stem = f"{table}-{now.strftime('%Y%m%d-%H%M%S')}"
    path = backup_dir / f"{stem}.json"
    suffix = 1
    while path.exists():
        path = backup_dir / f"{stem}-{suffix}.json"
        suffix += 1
    with path.open("xb") as handle:
        handle.write(raw)
        handle.flush()
        os.fsync(handle.fileno())
    try:
        os.chown(path, owner_ref.stat().st_uid, owner_ref.stat().st_gid)
    except (PermissionError, OSError):
        pass
    return path


def atomic_write(path: Path, data: bytes, expect_sha: str) -> None:
    """Replace path with data unless it changed since expect_sha (a concurrent WebUI save)."""
    handle = tempfile.NamedTemporaryFile(dir=path.parent, prefix=f".{path.name}.", suffix=".kfmerge.tmp",
                                         delete=False)
    tmp = Path(handle.name)
    try:
        with handle:
            handle.write(data)
            handle.flush()
            os.fsync(handle.fileno())
        _copy_owner_mode(path, tmp)
        if sha256(path.read_bytes()).hexdigest() != expect_sha:
            raise ConcurrentEdit(path.name)
        os.replace(tmp, path)
        _fsync_dir(path.parent)
    except BaseException:
        tmp.unlink(missing_ok=True)
        raise


class ConcurrentEdit(Exception):
    pass


def write_marker(marker: Path, commit: str) -> None:
    marker.parent.mkdir(parents=True, exist_ok=True)
    tmp = marker.with_name(marker.name + ".tmp")
    tmp.write_text(commit + "\n", encoding="utf-8")
    if marker.exists():
        _copy_owner_mode(marker, tmp)
    os.replace(tmp, marker)


# --------------------------------------------------------------------------- #
# Orchestration
# --------------------------------------------------------------------------- #


def plan_all(base_src, repo_src, override_dir: Path, only: set[str] | None) -> list[TablePlan]:
    plans: list[TablePlan] = []
    for path in sorted(override_dir.glob("masters_*.json")):
        match = TABLE_FILE_RE.match(path.name)
        if match is None or (only and match.group(1) not in only):
            continue
        table = match.group(1)
        plan = merge_table(table, base_src.read(path.name), repo_src.read(path.name), path.read_bytes())
        plans.append(plan)
    return plans


def apply_plans(plans: list[TablePlan], override_dir: Path, backup_dir: Path) -> list[tuple[TablePlan, Path, Path]]:
    done: list[tuple[TablePlan, Path, Path]] = []
    now = datetime.now()
    try:
        for plan in plans:
            if not plan.changed:
                continue
            target = override_dir / f"masters_{plan.table}.json"
            raw = target.read_bytes()
            if raw != plan.override_raw:
                # The WebUI saved since planning: the caller re-plans on the fresh bytes.
                raise ConcurrentEdit(target.name)
            backup = backup_override(backup_dir, plan.table, raw, target, now)
            try:
                atomic_write(target, plan.style.encode(plan.new_rows), sha256(raw).hexdigest())
            except ConcurrentEdit:
                backup.unlink(missing_ok=True)
                raise
            done.append((plan, target, backup))
            log(f"wrote {target.name} (backup: {backup})")
    except BaseException:
        for plan, target, backup in reversed(done):          # undo what this run already wrote
            try:
                atomic_write(target, backup.read_bytes(), sha256(target.read_bytes()).hexdigest())
                log(f"rolled back {target.name} from {backup}")
            except Exception as error:  # noqa: BLE001 - best effort, reported
                log(f"ERROR could not roll back {target.name}: {error}; restore it from {backup}")
        raise
    return done


def report(plans: list[TablePlan], verbose: bool) -> None:
    for plan in plans:
        for warning in plan.warnings:
            log(f"{plan.table}: {warning}")
        if not plan.changed:
            if verbose:
                log(f"{plan.table}: no change")
            continue
        extra = "" if plan.format_stable else " (override formatting was not canonical; it is normalized on write)"
        log(f"{plan.table}: {len(plan.changes)} change(s), {len(plan.conflicts)} conflict(s){extra}")
        for change in plan.changes:
            log(f"  {change}")
        for conflict in plan.conflicts:
            log(f"  CONFLICT {conflict}")


def run(args: argparse.Namespace) -> int:
    app = Path(args.app_dir) if args.app_dir else None
    override_dir = Path(args.override_dir or (app / ".local" / "masters-overrides" if app else ""))
    backup_dir = Path(args.backup_dir or (app / ".local" / "balance-backups" if app else ""))
    if not override_dir.parts:
        raise MergeError("--override-dir (or --app-dir) is required")
    marker = Path(args.marker) if args.marker else (app / ".local" / MARKER_NAME if app else None)

    # REPO source
    if args.repo_rev:
        repo_src = GitSource(Path(args.git_root), args.repo_rev)
        commit = args.commit or args.repo_rev
    elif args.repo_dir:
        repo_src = DirSource(Path(args.repo_dir))
        commit = args.commit
    else:
        raise MergeError("--repo-dir or --repo-rev is required")
    if commit and not SHA_RE.match(commit):
        if args.repo_rev:
            commit = subprocess.run(["git", "-C", args.git_root, "rev-parse", commit], text=True,
                                    stdout=subprocess.PIPE, stderr=subprocess.PIPE).stdout.strip() or None
        else:
            raise MergeError("--commit must be a full 40-hex SHA")

    # BASE source
    if args.base_rev:
        base_src = GitSource(Path(args.git_root), args.base_rev)
        base_note = f"git revision {args.base_rev}"
        base_commit = args.base_rev
    elif args.base_dir:
        base_src = DirSource(Path(args.base_dir))
        base_note = f"directory {args.base_dir}"
        base_commit = None
    else:
        if app is None and not (args.releases_dir and args.current_sha_file and marker):
            raise MergeError("no BASE: pass --base-dir/--base-rev, or --app-dir for marker resolution")
        if commit and marker and marker.exists() and read_sha_file(marker) == commit:
            log(f"masters of {commit} are already merged (marker equals the new commit); nothing to do")
            return 0
        releases = Path(args.releases_dir or app / ".ci" / "releases")
        current_file = Path(args.current_sha_file or app / ".ci" / "current-sha")
        fallback = Path(args.fallback_base_dir) if args.fallback_base_dir else (app / "config" if app else None)
        base_commit, base_dir, how = resolve_base(marker, releases, current_file, fallback)
        base_src = DirSource(base_dir)
        base_note = f"{base_commit} ({how}: {base_dir})"
        if commit and base_commit == commit:
            # Bootstrap on a host whose deployed commit is already the new one: record it, merge nothing.
            log(f"masters of {commit} are already deployed (BASE equals the new commit); nothing to do")
            if not args.dry_run and not marker.exists():
                write_marker(marker, commit)
            return 0

    log(f"BASE={base_note} REPO={repo_src.label} overrides={override_dir}"
        f"{' [DRY RUN]' if args.dry_run else ''}")
    if not override_dir.is_dir():
        log("override directory does not exist; nothing to merge")
        if commit and marker and not args.dry_run:
            write_marker(marker, commit)
        return 0

    only = {t.strip() for t in args.only.split(",") if t.strip()} if args.only else None
    plans = plan_all(base_src, repo_src, override_dir, only)
    report(plans, args.verbose)
    touched = [p for p in plans if p.changed]
    keyless = [p.table for p in plans if p.mode == "table-replace"]
    log(f"{len(plans)} override table(s) inspected, {len(touched)} to change, "
        f"{sum(len(p.conflicts) for p in plans)} conflict(s)"
        + (f"; tables without a stable id key: {', '.join(keyless)}" if keyless else ""))
    if args.dry_run:
        return 0

    for attempt in range(MAX_WRITE_RETRIES):
        try:
            apply_plans(plans, override_dir, backup_dir)
            break
        except ConcurrentEdit as error:
            log(f"{error} changed while merging (WebUI save?); re-planning, attempt {attempt + 1}")
            plans = plan_all(base_src, repo_src, override_dir, only)
    else:
        raise MergeError("overrides kept changing under the merge; aborting with them untouched")
    if commit and marker:
        write_marker(marker, commit)
        log(f"marker {marker} -> {commit}")
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--app-dir", help="VPS application root: defaults for overrides, backups, marker, .ci")
    parser.add_argument("--repo-dir", help="config/ directory of the new commit (REPO)")
    parser.add_argument("--repo-rev", help="git revision to read REPO from (needs --git-root)")
    parser.add_argument("--base-dir", help="explicit BASE config/ directory (skips marker resolution)")
    parser.add_argument("--base-rev", help="explicit BASE git revision (needs --git-root)")
    parser.add_argument("--git-root", default=".", help="git checkout for --base-rev/--repo-rev")
    parser.add_argument("--commit", help="SHA of the REPO commit; recorded in the marker after a real run")
    parser.add_argument("--override-dir")
    parser.add_argument("--backup-dir")
    parser.add_argument("--marker", help=f"default <app>/.local/{MARKER_NAME}")
    parser.add_argument("--releases-dir", help="default <app>/.ci/releases")
    parser.add_argument("--current-sha-file", help="default <app>/.ci/current-sha")
    parser.add_argument("--fallback-base-dir", help="BASE tree when the release dir is missing; default <app>/config")
    parser.add_argument("--only", help="comma-separated table names, e.g. skill_condition,disc")
    parser.add_argument("--dry-run", action="store_true", help="print the planned changes, write nothing")
    parser.add_argument("--verbose", action="store_true")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        return run(args)
    except MergeError as error:
        log(f"ERROR {error}")
        log("overrides were left untouched")
        return 1
    except Exception as error:  # noqa: BLE001 - fail safe: report and abort the deploy
        log(f"ERROR unexpected {type(error).__name__}: {error}")
        log("overrides were left untouched or restored from backup")
        return 1


if __name__ == "__main__":
    sys.exit(main())
