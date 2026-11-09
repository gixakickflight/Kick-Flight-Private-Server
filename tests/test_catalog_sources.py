"""Catalog source guards (deploy precheck fails when a shipped source is missing)."""
import hashlib
import json
import subprocess
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
COMMITTED_DIR = "content/resources/fenix-localize/"


def _sources(path):
    data = json.loads((REPO / path).read_text(encoding="utf-8-sig"))
    found = []

    def walk(o):
        if isinstance(o, dict):
            if o.get("sourcePath") and o.get("enabled", True):
                found.append(o)
            for v in o.values():
                walk(v)
        elif isinstance(o, list):
            for v in o:
                walk(v)

    walk(data)
    return found


def _tracked():
    out = subprocess.run(["git", "ls-files"], cwd=REPO, check=True, capture_output=True, text=True).stdout
    return set(out.splitlines())


def test_committed_bundles_never_point_to_local():
    tracked = _tracked()
    committed = {Path(f).name for f in tracked if f.startswith(COMMITTED_DIR) and f.endswith(".bundle")}
    for cfg in ("config/resources/catalog.json", "config/resources/title-minimum.json"):
        for e in _sources(cfg):
            sp = e["sourcePath"]
            if Path(sp).name in committed:
                assert sp == COMMITTED_DIR + Path(sp).name, f"{cfg}: {e.get('requestPath', e.get('id'))} -> {sp}"
                if "sha256" in e:
                    assert hashlib.sha256((REPO / sp).read_bytes()).hexdigest() == e["sha256"], sp


def test_non_local_sources_are_tracked():
    tracked = _tracked()
    for e in _sources("config/resources/catalog.json"):
        sp = e["sourcePath"]
        if not sp.startswith((".local/", "../")):  # .local/ and sibling asset repo are provisioned by deploy
            assert sp in tracked, f"{e['requestPath']} source not in repo: {sp}"
