#!/usr/bin/env python3
"""Balance WebUI for the Kick-Flight combat master tables.

Serves ``tools/balance/index.html``, the icon folder and a small JSON API over the
``masters_*.json`` directory the game server reads, so the KS/SS/disc/kicker numbers can be
tuned from the browser and effect rows can be added or deleted.  Python standard library only.

Local (edits this checkout's ``config/``)::

    python tools/balance/server.py [--port 8765]

Against the deployed server (edits the directory the API mounts, toggles maintenance through the
loopback admin endpoint) see ``tools/balance/deploy/`` and ``tools/balance/README.md``.  Paths,
the auth password and the maintenance endpoint all come from ``KF_BALANCE_*`` environment
variables, never from hardcoded secrets.
"""

from __future__ import annotations

import argparse
import io
import ipaddress
import json
import math
import os
import re
import shlex
import sqlite3
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import zipfile
from http.cookies import SimpleCookie
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, quote, unquote, urlsplit

from auth import (ACCOUNT_LOGIN_MAX_FAILURES, AuthStore, LoginRateLimiter, load_jwt_secret,
                  normalize_username)

BALANCE_DIR = Path(__file__).resolve().parent
REPO_ROOT = BALANCE_DIR.parent.parent
CONFIG_DIR = REPO_ROOT / "config"   # the checkout default; the deployment overrides it below
DOCS_DIR = REPO_ROOT / "docs"
ICON_DIR = BALANCE_DIR / "icons"
INDEX_FILE = BALANCE_DIR / "index.html"
LOGIN_FILE = BALANCE_DIR / "login.html"
DISC_CARDS_FILE = DOCS_DIR / "disc_cards.json"
# Named client enums (value -> member name) and the {table.field: enum} mapping the UI turns into
# dropdowns; the file itself documents each enum's source.  See tools/balance/README.md.
ENUMS_FILE = BALANCE_DIR / "enums.json"


def _env_path(name, default):
    """Path override so the same script can run next to the deployed config/ or its backups."""
    value = os.environ.get(name)
    if not value:
        return default
    return Path(value).expanduser()


# The server the WebUI must edit mounts its masters from a config/ directory (the game server reads
# config/masters_*.json at startup, there is no masters table in Postgres - see the report).  In the
# VPS deployment that directory is /opt/kickflight/config, not this checkout.
# KF_BALANCE_MASTERS_DIR is kept as a backwards-compatible alias for KF_BALANCE_BASE_DIR.
MASTERS_DIR = _env_path("KF_BALANCE_BASE_DIR", _env_path("KF_BALANCE_MASTERS_DIR", CONFIG_DIR))
# Tuned tables are persisted here, never in MASTERS_DIR: the CI deploy rsyncs config/ with --delete, but
# .local/ is excluded and survives.  The API's master override layer reads this directory (reads only).
OVERRIDE_DIR = _env_path("KF_BALANCE_OVERRIDE_DIR", REPO_ROOT / ".local" / "masters-overrides")
BACKUP_DIR = _env_path("KF_BALANCE_BACKUP_DIR", BALANCE_DIR / "backups")

# Authentication is mandatory. The local DB is never committed, and an empty user table denies
# every login until an operator inserts an account manually.
AUTH_DB_PATH = _env_path("KF_BALANCE_AUTH_DB", REPO_ROOT / ".local" / "balance-auth" / "auth.sqlite3")
JWT_SECRET_PATH = _env_path("KF_BALANCE_JWT_SECRET_FILE", AUTH_DB_PATH.parent / "jwt-secret")
SESSION_COOKIE_NAME = "__Secure-kf_balance_session"
BASE_PATH = os.environ.get("KF_BALANCE_BASE_PATH", "").strip()
if BASE_PATH in ("", "/"):
    BASE_PATH = ""
else:
    BASE_PATH = "/" + BASE_PATH.strip("/")
    if (not re.fullmatch(r"/[A-Za-z0-9._~-]+(?:/[A-Za-z0-9._~-]+)*", BASE_PATH)
            or any(part in (".", "..") for part in BASE_PATH.split("/"))):
        raise RuntimeError("KF_BALANCE_BASE_PATH must be a simple URL path, for example /balance")
AUTH_STORE: AuthStore | None = None
LOGIN_LIMITER = LoginRateLimiter()
# PBKDF2 is deliberately expensive. Cap simultaneous hashes to the two CPU cores available on
# the VPS so distinct source IPs cannot turn ThreadingHTTPServer into an unbounded hash farm.
AUTH_WORK_SLOTS = threading.BoundedSemaphore(2)
ALLOWED_ORIGINS_EXTRA = tuple(
    origin.strip().rstrip("/").lower()
    for origin in os.environ.get("KF_BALANCE_ALLOWED_ORIGINS", "").split(",")
    if origin.strip()
)

# CSRF / DNS-rebinding protection.  The tool is bound to loopback and normally reached through an SSH
# tunnel at 127.0.0.1:8765, but a page open in the user's browser can still send it a "simple" POST
# (text/plain, form-urlencoded, ...) without a CORS preflight and toggle maintenance or restart the
# API.  Every request therefore needs a Host that belongs to the tunnel (the loopback names on the
# bound port, plus KF_BALANCE_ALLOWED_HOSTS for LAN/proxy use), every state-changing request needs an
# application/json Content-Type (which does force a preflight, one the server never answers), and an
# Origin header, when the browser sends one, must be exactly http://<allowed host>.
ALLOWED_HOSTS_EXTRA = tuple(
    host.strip().lower()
    for host in os.environ.get("KF_BALANCE_ALLOWED_HOSTS", "").split(",")
    if host.strip()
)
TRUSTED_PROXIES = frozenset(
    address.strip()
    for address in os.environ.get("KF_BALANCE_TRUSTED_PROXIES", "").split(",")
    if address.strip()
)
JSON_CONTENT_TYPE = "application/json"

# Apply: saving writes override files, but the API reads them once at startup, so the restart command and
# the readiness probe are configurable.  Defaults target the VPS container (its HTTP port is unpublished).
API_CONTAINER = os.environ.get("KF_BALANCE_API_CONTAINER", "deploy-api-1")
APPLY_CMD = os.environ.get("KF_BALANCE_APPLY_CMD", "docker restart %s" % API_CONTAINER)
HEALTH_URL = os.environ.get("KF_BALANCE_HEALTH_URL", "http://127.0.0.1:8080/health/ready")
HEALTH_CMD = os.environ.get(
    "KF_BALANCE_HEALTH_CMD", "docker exec %s curl -fsS %s" % (API_CONTAINER, HEALTH_URL)
)
APPLY_TIMEOUT = float(os.environ.get("KF_BALANCE_APPLY_TIMEOUT", "120"))
HEALTH_INTERVAL = float(os.environ.get("KF_BALANCE_HEALTH_INTERVAL", "2"))

# Maintenance toggle.  The game server only accepts it from its own loopback
# (MaintenanceState.IsLocalAdmin) and the VPS deployment never publishes the API's HTTP port, so the
# default path is curl *inside* the api container.  KF_BALANCE_ADMIN_URL points at the real endpoint
# directly instead, which is what a native/dev API on the same host needs.
ADMIN_URL = os.environ.get("KF_BALANCE_ADMIN_URL", "")
ADMIN_CONTAINER = os.environ.get("KF_BALANCE_ADMIN_CONTAINER", API_CONTAINER)
ADMIN_INTERNAL_URL = os.environ.get(
    "KF_BALANCE_ADMIN_INTERNAL_URL", "http://127.0.0.1:8080/admin/maintenance"
)
# Escape hatches: full shell commands (stdout = JSON; the set command gets the JSON body on stdin)
# for hosts where docker exec + curl is not available.  Left empty, the default docker exec is used.
ADMIN_CMD = os.environ.get("KF_BALANCE_ADMIN_CMD", "")
ADMIN_SET_CMD = os.environ.get("KF_BALANCE_ADMIN_SET_CMD", "")
ADMIN_TIMEOUT = 15

# Latest Information uses its own loopback-only API endpoint, with independent overrides for split dev setups.
LATEST_INFO_URL = os.environ.get("KF_BALANCE_LATEST_INFO_URL", "")
LATEST_INFO_CONTAINER = os.environ.get("KF_BALANCE_LATEST_INFO_CONTAINER", ADMIN_CONTAINER)
LATEST_INFO_INTERNAL_URL = os.environ.get(
    "KF_BALANCE_LATEST_INFO_INTERNAL_URL", "http://127.0.0.1:8080/admin/latest-information"
)
LATEST_INFO_CMD = os.environ.get("KF_BALANCE_LATEST_INFO_CMD", "")
LATEST_INFO_SET_CMD = os.environ.get("KF_BALANCE_LATEST_INFO_SET_CMD", "")
LATEST_INFO_MAX_CHARS = 12000
LATEST_INFO_MAX_HTML_BYTES = 1024 * 1024
LATEST_INFO_MAX_URL_CHARS = 2048

# Health tab: the game API's read-only /admin/ops/* telemetry endpoints.  Reached exactly like the other
# loopback-only admin routes (docker exec curl inside the API container by default), with the same escape
# hatches.  KF_BALANCE_OPS_CMD, when set, must contain "{url}" (the shell-quoted absolute URL).
OPS_URL = os.environ.get("KF_BALANCE_OPS_URL", "")
OPS_CONTAINER = os.environ.get("KF_BALANCE_OPS_CONTAINER", ADMIN_CONTAINER)
OPS_INTERNAL_URL = os.environ.get("KF_BALANCE_OPS_INTERNAL_URL", "http://127.0.0.1:8080/admin/ops")
OPS_CMD = os.environ.get("KF_BALANCE_OPS_CMD", "")
OPS_BATTLE_ID_RE = re.compile(r"^[A-Za-z0-9._:-]{1,128}$")

TABLE_NAME_RE = re.compile(r"^[a-z_]+$")
ICON_NAME_RE = re.compile(r"^(?:kicker|disc)_[0-9]+\.png$")
TABLE_URL_RE = re.compile(r"^/api/table/([^/]*)$")
REVERT_URL_RE = re.compile(r"^/api/table/([^/]*)/revert$")
DIFF_URL_RE = re.compile(r"^/api/diff/([^/]*)$")
OPS_BATTLE_URL_RE = re.compile(r"^/api/ops/battle/([^/]*)$")

MAX_BODY_BYTES = 32 * 1024 * 1024
MAX_REPORTED_ERRORS = 12
WRITE_LOCK = threading.Lock()

# Effect tables that may gain/lose rows.  The game server reads a skill's rows by `skillId`
# (DemoSessionApi loads SkillCondition/SkillHeal/SkillBlowOff/SkillPullIn/SkillTrap into the skill's
# parameter), so a new row only needs a fresh id and the skill it belongs to.  The schema fixes the
# column set and the type each column is served as; the UI builds new rows from DEFAULT_EFFECT_ROWS
# (fetched through /api/effect-schema) so both sides agree on the exact shape.
EFFECT_SCHEMAS = {
    "skill_condition": {
        "id": "int", "skillId": "int", "conditionType": "int", "duration": "float",
        "interval": "float", "effectValue": "float", "triggerType": "int",
    },
    "skill_heal": {
        "id": "int", "skillId": "int", "skillHealType": "int", "coefficient": "float",
    },
    "skill_blow_off": {
        "id": "int", "skillId": "int", "distance": "float", "speed": "float",
        "rigorTime": "float", "directionType": "int",
    },
    "skill_pull_in": {
        "id": "int", "skillId": "int", "distance": "float", "speed": "float",
    },
    "skill_trap": {
        "id": "int", "skillId": "int", "trapType": "int", "duration": "float",
        "radius": "float", "effectValue": "float", "interval": "float", "executeSeId": "int",
        "effectPath": "str", "screenEffectPath": "str",
    },
    "skill_collision": {
        "id": "int", "skillId": "int", "collisionType": "int", "collisionHitType": "int",
        "hitLayer": "int", "radius": "float", "length": "float", "originCenterFlag": "bool",
        "scaleX": "float", "scaleY": "float", "scaleZ": "float",
    },
    "skill_hit": {
        "id": "int", "skillId": "int", "commonHitEffectType": "int", "hitSeId": "int",
        "effectPath": "str", "parentBone": "int", "offsetX": "float", "offsetY": "float",
        "offsetZ": "float", "transformType": "int", "shakeVolume": "float",
        "knockBackFlag": "bool", "fixedDamage": "int",
    },
}

# Neutral "add row" payloads matching scripts/generate_combat_masters.py presets (a 10 s AttackRate
# buff, a 30 % MaxHP heal, a slam-style blow-off, a long-range pull-in, a bomb trap).
DEFAULT_EFFECT_ROWS = {
    "skill_condition": {
        "conditionType": 1, "duration": 10.0, "interval": 0.0, "effectValue": 1.2, "triggerType": 1,
    },
    "skill_heal": {"skillHealType": 1, "coefficient": 0.3},
    "skill_blow_off": {"distance": 6.0, "speed": 25.0, "rigorTime": 0.5, "directionType": 2},
    "skill_pull_in": {"distance": 8.0, "speed": 25.0},
    "skill_trap": {
        "trapType": 7, "duration": 8.0, "radius": 5.0, "effectValue": 0.0, "interval": 0.0,
        "executeSeId": 0, "effectPath": "", "screenEffectPath": "",
    },
    "skill_collision": {
        "collisionType": 1, "collisionHitType": 1, "hitLayer": 4864, "radius": 1.5,
        "length": 0.0, "originCenterFlag": True, "scaleX": 1.0, "scaleY": 1.0, "scaleZ": 1.0,
    },
    "skill_hit": {
        "commonHitEffectType": 11, "hitSeId": 0, "effectPath": "", "parentBone": 9,
        "offsetX": 0.0, "offsetY": 0.0, "offsetZ": 0.0, "transformType": 0, "shakeVolume": 0.2,
        "knockBackFlag": False, "fixedDamage": 0,
    },
}


# --------------------------------------------------------------------------- #
# reading
# --------------------------------------------------------------------------- #


def base_path(name):
    """MASTERS_DIR/masters_<name>.json for a well-formed name that exists, else None."""
    if not TABLE_NAME_RE.match(name):
        return None
    path = MASTERS_DIR / ("masters_%s.json" % name)
    return path if path.is_file() else None


def override_path(name):
    """OVERRIDE_DIR/masters_<name>.json when a tuned override exists, else None."""
    if not TABLE_NAME_RE.match(name):
        return None
    path = OVERRIDE_DIR / ("masters_%s.json" % name)
    return path if path.is_file() else None


def table_source(name):
    """'override', 'base' or None: where the effective rows of a table come from."""
    if override_path(name) is not None:
        return "override"
    if base_path(name) is not None:
        return "base"
    return None


def table_path(name):
    """The file the effective rows are read from (override first), else None."""
    if not TABLE_NAME_RE.match(name):
        return None
    return override_path(name) or base_path(name)


def read_table(name):
    """Return the effective row list of masters_<name>.json (override file wins)."""
    path = table_path(name)
    if path is None:
        raise FileNotFoundError(name)
    with path.open(encoding="utf-8") as handle:
        rows = json.load(handle)
    if not isinstance(rows, list):
        raise ValueError("%s is not a JSON array" % path)
    return rows


def list_tables():
    """Every base or override master table with its override state, for the UI badges."""
    names = set()
    for directory in (MASTERS_DIR, OVERRIDE_DIR):
        if not directory.is_dir():
            continue
        for path in directory.glob("masters_*.json"):
            name = path.name[len("masters_"):-len(".json")]
            if TABLE_NAME_RE.match(name):
                names.add(name)
    tables = []
    for name in sorted(names):
        override = override_path(name)
        base = base_path(name)
        source = "override" if override is not None else ("base" if base is not None else None)
        if source is None:
            continue
        tables.append(
            {
                "name": name,
                "source": source,
                "overridden": override is not None,
                "base": base is not None,
                "overrideMtime": override.stat().st_mtime if override is not None else None,
                "baseMtime": base.stat().st_mtime if base is not None else None,
            }
        )
    return tables


def load_disc_cards():
    """docs/disc_cards.json -> {disc id (str): card dict}.

    The file stores the cards as a list under the ``cards`` key; a mapping keyed
    directly by disc id is also accepted so the tool keeps working if the file is
    reshaped.
    """
    if not DISC_CARDS_FILE.is_file():
        return {}
    with DISC_CARDS_FILE.open(encoding="utf-8") as handle:
        doc = json.load(handle)
    source = doc.get("cards") if isinstance(doc, dict) else doc
    cards = {}
    if isinstance(source, list):
        for card in source:
            if isinstance(card, dict) and "discId" in card:
                cards[str(card["discId"])] = card
    elif isinstance(source, dict):
        for key, card in source.items():
            if key.startswith("_") or not isinstance(card, dict):
                continue
            cards[str(card.get("discId", key))] = card
    return cards


def load_enums():
    """``enums.json`` -> ``{"enums": {name: {source, values}}, "fields": {"table.field": name}}``.

    Read fresh on every request (the file is tiny) so adding an enum needs no server restart.
    Raises OSError/ValueError with a readable message when the file is missing or malformed; the
    /api/enums route answers 500 and the UI then keeps every field as a plain number input.
    """
    with ENUMS_FILE.open(encoding="utf-8") as handle:
        doc = json.load(handle)
    if not isinstance(doc, dict):
        raise ValueError("enums.json must be a JSON object")
    enums = doc.get("enums")
    fields = doc.get("fields")
    if not isinstance(enums, dict) or not isinstance(fields, dict):
        raise ValueError('enums.json must have object "enums" and object "fields"')
    for name, definition in enums.items():
        values = definition.get("values") if isinstance(definition, dict) else None
        if not isinstance(values, dict) or not values:
            raise ValueError("enum %r must be an object with a non-empty \"values\" object" % name)
        for value, label in values.items():
            if not isinstance(label, str) or not label:
                raise ValueError("enum %r value %r has no name" % (name, value))
    for field, name in fields.items():
        if name not in enums:
            raise ValueError("field %r points at undefined enum %r" % (field, name))
    return {"enums": enums, "fields": fields}


# --------------------------------------------------------------------------- #
# validation
# --------------------------------------------------------------------------- #


def value_kind(value):
    """Classify a JSON value the way the writer needs to reproduce it."""
    if isinstance(value, bool):
        return "bool"
    if isinstance(value, int):
        return "int"
    if isinstance(value, float):
        return "float"
    if isinstance(value, str):
        return "str"
    if value is None:
        return "null"
    if isinstance(value, list):
        return "list"
    if isinstance(value, dict):
        return "dict"
    return "other"


def describe(value):
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, (int, float)):
        return repr(value)
    if isinstance(value, str):
        return "a string (%r)" % (value if len(value) <= 40 else value[:40] + "...")
    return "a %s" % value_kind(value)


def normalize_id(value):
    """Ids keep their type; 100.0 is accepted for an int id and stored as 100."""
    if isinstance(value, bool):
        return None
    if isinstance(value, int):
        return value
    if isinstance(value, float) and math.isfinite(value) and value.is_integer():
        return int(value)
    return None


def coerce_value(kind, value, where):
    """(coerced value, None) or (None, error message) preserving the field type."""
    if kind == "bool":
        if isinstance(value, bool):
            return value, None
        return None, "%s: expected true/false, got %s" % (where, describe(value))
    if kind == "int":
        if isinstance(value, bool):
            return None, "%s: expected an integer, got %s" % (where, describe(value))
        if isinstance(value, int):
            return value, None
        if isinstance(value, float):
            if not math.isfinite(value):
                return None, "%s: expected a finite integer" % where
            if value.is_integer():
                return int(value), None
            return None, "%s: expected an integer, got %s" % (where, describe(value))
        return None, "%s: expected an integer, got %s" % (where, describe(value))
    if kind == "float":
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            return None, "%s: expected a number, got %s" % (where, describe(value))
        number = float(value)
        if not math.isfinite(number):
            return None, "%s: expected a finite number" % where
        return number, None
    if kind == "str":
        if isinstance(value, str):
            return value, None
        return None, "%s: expected a string, got %s" % (where, describe(value))
    if kind == "null":
        if value is None:
            return value, None
        return None, "%s: expected null, got %s" % (where, describe(value))
    return None, "%s: unsupported field type %s" % (where, kind)


class Problems:
    """Collects validation errors without letting the list grow without bound."""

    def __init__(self):
        self.messages = []
        self.withheld = 0

    def add(self, message):
        if len(self.messages) < MAX_REPORTED_ERRORS:
            self.messages.append(message)
        else:
            self.withheld += 1

    def result(self):
        messages = list(self.messages)
        if self.withheld:
            messages.append("... and %d more problem(s)" % self.withheld)
        return messages


def validate_row(current, posted, table, problems):
    """Return the row to write, or None when it fails validation."""
    row_id = current.get("id")
    where = "row %s" % (row_id,)
    if not isinstance(posted, dict):
        problems.add("%s: expected an object, got %s" % (where, describe(posted)))
        return None

    current_keys = set(current)
    posted_keys = set(posted)
    if posted_keys != current_keys:
        parts = []
        missing = sorted(current_keys - posted_keys)
        extra = sorted(posted_keys - current_keys)
        if missing:
            parts.append("missing key(s) %s" % ", ".join(missing))
        if extra:
            parts.append("unknown key(s) %s" % ", ".join(extra))
        problems.add("%s: %s" % (where, "; ".join(parts)))
        return None

    out = {}
    ok = True
    for key, current_value in current.items():
        value = posted[key]
        field = "%s field %r" % (where, key)
        kind = value_kind(current_value)
        if kind in ("list", "dict", "other"):
            # Structured columns are not editable; keeping them byte-identical is
            # what lets the writer round-trip tables that carry nested data.
            if value != current_value:
                problems.add("%s: structured values cannot be edited" % field)
                ok = False
            out[key] = current_value
            continue
        coerced, error = coerce_value(kind, value, field)
        if error is not None:
            problems.add(error)
            ok = False
        else:
            out[key] = coerced
    return out if ok else None


def validate_new_row(table, posted, problems):
    """Validate a row the UI added: exact schema keys, a fresh positive id, the served types."""
    schema = EFFECT_SCHEMAS.get(table)
    if schema is None:
        problems.add("table %r does not accept new rows" % table)
        return None
    if not isinstance(posted, dict):
        problems.add("new row: expected an object, got %s" % describe(posted))
        return None

    posted_keys = set(posted)
    schema_keys = set(schema)
    if posted_keys != schema_keys:
        parts = []
        missing = sorted(schema_keys - posted_keys)
        extra = sorted(posted_keys - schema_keys)
        if missing:
            parts.append("missing key(s) %s" % ", ".join(missing))
        if extra:
            parts.append("unknown key(s) %s" % ", ".join(extra))
        problems.add("new row: %s" % "; ".join(parts))
        return None

    out = {}
    ok = True
    for key, kind in schema.items():
        value = posted[key]
        if key == "id":
            normalized = normalize_id(value)
            if normalized is None or normalized <= 0:
                problems.add("new row id %s is not a positive integer" % describe(value))
                ok = False
                continue
            value = normalized
        coerced, error = coerce_value(kind, value, "new row field %r" % key)
        if error is not None:
            problems.add(error)
            ok = False
        else:
            out[key] = coerced
    return out if ok else None


def validate_rows(table, current_rows, posted_rows):
    """(rows to write, None) or (None, [messages]) - never a partial result.

    Effect tables (EFFECT_SCHEMAS) accept added and deleted rows; every other table keeps the
    original rule (same ids, same key set, same value types, no add/remove).
    """
    problems = Problems()
    if not isinstance(posted_rows, list):
        return None, ["body.rows must be a list, got %s" % describe(posted_rows)]

    editable = table in EFFECT_SCHEMAS

    current_by_id = {}
    for row in current_rows:
        if not isinstance(row, dict) or "id" not in row:
            return None, [
                "masters_%s.json has a row without an id; refusing to write" % table
            ]
        if row["id"] in current_by_id:
            return None, [
                "masters_%s.json has duplicate id %s; refusing to write"
                % (table, row["id"])
            ]
        current_by_id[row["id"]] = row

    if not editable and len(posted_rows) != len(current_rows):
        return None, [
            "expected %d rows, got %d: adding or removing rows is not supported"
            % (len(current_rows), len(posted_rows))
        ]

    posted_by_id = {}
    for index, posted in enumerate(posted_rows):
        if not isinstance(posted, dict) or "id" not in posted:
            problems.add("posted row %d: missing an \"id\"" % index)
            continue
        normalized = normalize_id(posted["id"])
        key = normalized if normalized is not None else posted["id"]
        if key in posted_by_id:
            problems.add("posted row %d: duplicate id %s" % (index, posted["id"]))
            continue
        posted_by_id[key] = posted

    missing_ids = sorted(
        (row_id for row_id in current_by_id if row_id not in posted_by_id),
        key=lambda value: (isinstance(value, str), str(value)),
    )
    unknown_ids = sorted(
        (row_id for row_id in posted_by_id if row_id not in current_by_id),
        key=lambda value: (isinstance(value, str), str(value)),
    )
    if missing_ids and not editable:
        problems.add("missing row(s) for id(s): %s" % ", ".join(map(str, missing_ids)))
    if unknown_ids and not editable:
        problems.add("unknown row id(s): %s" % ", ".join(map(str, unknown_ids)))

    new_rows = []
    for current in current_rows:
        posted = posted_by_id.get(current["id"])
        if posted is None:
            continue   # deleted; only reachable for an editable table
        row = validate_row(current, posted, table, problems)
        if row is not None:
            new_rows.append(row)
    for key in unknown_ids:
        row = validate_new_row(table, posted_by_id[key], problems)
        if row is not None:
            new_rows.append(row)

    messages = problems.result()
    if messages:
        return None, messages
    if not editable and len(new_rows) != len(current_rows):
        return None, ["could not validate every row of masters_%s.json" % table]
    return new_rows, None


# --------------------------------------------------------------------------- #
# writing
# --------------------------------------------------------------------------- #


def backup_table(name, original_text):
    """Copy the file about to be replaced (override, else base) into the backup directory."""
    BACKUP_DIR.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    stem = "%s-%s" % (name, stamp)
    path = BACKUP_DIR / ("%s.json" % stem)
    suffix = 1
    while path.exists():
        path = BACKUP_DIR / ("%s-%d.json" % (stem, suffix))
        suffix += 1
    with path.open("w", encoding="utf-8", newline="\n") as handle:
        handle.write(original_text)
    return path


def write_table(name, rows):
    """Write the override file atomically; the base config/ file is never touched."""
    path = OVERRIDE_DIR / ("masters_%s.json" % name)
    OVERRIDE_DIR.mkdir(parents=True, exist_ok=True)
    text = json.dumps(rows, ensure_ascii=False, indent=1) + "\n"
    tmp = path.with_name(path.name + ".tmp")
    with tmp.open("w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)
    tmp.replace(path)
    return text


def export_overrides_zip():
    """In-memory zip of every override file, so the tuned values can be committed to git later."""
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(OVERRIDE_DIR.glob("masters_*.json")) if OVERRIDE_DIR.is_dir() else []:
            archive.write(path, arcname=path.name)
    return buffer.getvalue()


BACKUP_NAME_RE = re.compile(r"-(\d{8}-\d{6})(?:-(\d+))?\.json$")


def backup_recency(path):
    """Order backups by when they were written.

    The name is <table>-<yyyymmdd>-<hhmmss>[-<n>].json; the collision counter of a
    same-second backup has to be compared numerically, because "-1" sorts before
    "." in the plain name and ordering by the raw file name would pick the older
    file.
    """
    match = BACKUP_NAME_RE.search(path.name)
    if match is None:
        return ("", 0)
    return (match.group(1), int(match.group(2)) if match.group(2) else 0)


def latest_backup(name):
    if not BACKUP_DIR.is_dir():
        return None
    candidates = list(BACKUP_DIR.glob("%s-*.json" % name))
    if not candidates:
        return None
    return max(candidates, key=backup_recency)


def diff_against_latest_backup(name, current_rows):
    """Field-level differences between the working file and the newest backup."""
    backup = latest_backup(name)
    if backup is None:
        return []
    try:
        with backup.open(encoding="utf-8") as handle:
            backup_rows = json.load(handle)
    except (OSError, ValueError):
        return []
    if not isinstance(backup_rows, list):
        return []

    before = {row["id"]: row for row in backup_rows if isinstance(row, dict) and "id" in row}
    after = {row["id"]: row for row in current_rows if isinstance(row, dict) and "id" in row}

    changes = []
    for row_id in sorted(set(before) | set(after), key=lambda v: (isinstance(v, str), str(v))):
        old = before.get(row_id)
        new = after.get(row_id)
        if old is None or new is None:
            fields = sorted(set(old or new))
            for field in fields:
                changes.append(
                    {
                        "id": row_id,
                        "field": field,
                        "before": (old or {}).get(field),
                        "after": (new or {}).get(field),
                    }
                )
            continue
        for field in sorted(set(old) | set(new)):
            if old.get(field) != new.get(field):
                changes.append(
                    {
                        "id": row_id,
                        "field": field,
                        "before": old.get(field),
                        "after": new.get(field),
                    }
                )
    changes.sort(key=lambda c: (isinstance(c["id"], str), str(c["id"]), c["field"]))
    return changes


# --------------------------------------------------------------------------- #
# maintenance admin proxy
# --------------------------------------------------------------------------- #


def _run_admin(command, body):
    """Run the loopback admin command (docker exec ... curl) and parse its JSON stdout."""
    try:
        completed = subprocess.run(
            command, input=body, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            timeout=ADMIN_TIMEOUT, shell=isinstance(command, str),
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        return 502, {"ok": False, "error": "admin command failed: %s" % error}
    text = completed.stdout.decode("utf-8", "replace").strip()
    if completed.returncode != 0:
        detail = completed.stderr.decode("utf-8", "replace").strip() or text
        return 502, {"ok": False, "error": "admin command exited %d: %s" % (completed.returncode, detail)}
    if not text:
        return 502, {
            "ok": False,
            "error": "empty response from the admin endpoint: the API only answers its own "
                     "loopback and the command must run there (docker exec / SSH tunnel)",
        }
    try:
        return 200, json.loads(text)
    except ValueError:
        return 502, {"ok": False, "error": "admin endpoint returned non-JSON: %s" % text[:200]}


def _http_admin(method, url, body):
    request = urllib.request.Request(url, data=body, method=method)
    if body is not None:
        request.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(request, timeout=ADMIN_TIMEOUT) as response:
            text = response.read().decode("utf-8", "replace").strip()
            return response.status, (json.loads(text) if text else None)
    except urllib.error.HTTPError as error:
        detail = error.read().decode("utf-8", "replace").strip()
        return error.code, {"ok": False, "error": detail or ("HTTP %d" % error.code)}
    except (urllib.error.URLError, ValueError, OSError) as error:
        return 502, {"ok": False, "error": "admin request failed: %s" % error}


def _http_ops(url):
    """GET the ops endpoint over HTTP and keep the API's own status and JSON error body."""
    request = urllib.request.Request(url, method="GET")
    try:
        with urllib.request.urlopen(request, timeout=ADMIN_TIMEOUT) as response:
            text = response.read().decode("utf-8", "replace").strip()
            return response.status, (json.loads(text) if text else None)
    except urllib.error.HTTPError as error:
        detail = error.read().decode("utf-8", "replace").strip()
        try:
            payload = json.loads(detail) if detail else None
        except ValueError:
            payload = None
        return error.code, payload if payload is not None else {"ok": False, "error": detail or ("HTTP %d" % error.code)}
    except (urllib.error.URLError, ValueError, OSError) as error:
        return 502, {"ok": False, "error": "ops request failed: %s" % error}


def admin_state():
    """(status, payload) of GET /admin/maintenance."""
    if ADMIN_URL:
        return _http_admin("GET", ADMIN_URL, None)
    if ADMIN_CMD:
        return _run_admin(ADMIN_CMD, None)
    return _run_admin(["docker", "exec", ADMIN_CONTAINER, "curl", "-sS", ADMIN_INTERNAL_URL], None)


def admin_set(payload):
    """(status, payload) of POST /admin/maintenance with the given JSON body."""
    body = json.dumps(payload).encode("utf-8")
    if ADMIN_URL:
        return _http_admin("POST", ADMIN_URL, body)
    if ADMIN_SET_CMD:
        return _run_admin(ADMIN_SET_CMD, body)
    return _run_admin(
        ["docker", "exec", "-i", ADMIN_CONTAINER, "curl", "-sS", "-X", "POST",
         ADMIN_INTERNAL_URL, "-H", "Content-Type: application/json", "-d", "@-"],
        body,
    )


def latest_information_state():
    """(status, payload) of GET /admin/latest-information."""
    if LATEST_INFO_URL:
        return _http_admin("GET", LATEST_INFO_URL, None)
    if LATEST_INFO_CMD:
        return _run_admin(LATEST_INFO_CMD, None)
    return _run_admin(["docker", "exec", LATEST_INFO_CONTAINER, "curl", "-sS", LATEST_INFO_INTERNAL_URL], None)


def latest_information_set(payload):
    """(status, payload) of POST /admin/latest-information with the given JSON body."""
    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    if LATEST_INFO_URL:
        return _http_admin("POST", LATEST_INFO_URL, body)
    if LATEST_INFO_SET_CMD:
        return _run_admin(LATEST_INFO_SET_CMD, body)
    return _run_admin(
        ["docker", "exec", "-i", LATEST_INFO_CONTAINER, "curl", "-sS", "-X", "POST",
         LATEST_INFO_INTERNAL_URL, "-H", "Content-Type: application/json", "-d", "@-"],
        body,
    )


def _with_window(url, window):
    if not window:
        return url
    separator = "&" if "?" in url else "?"
    return "%s%swindow=%s" % (url, separator, quote(window, safe=""))


def _ops_url(path, window):
    return _with_window((OPS_URL or OPS_INTERNAL_URL) + path, window)


def _run_ops(url):
    """docker exec curl, keeping both the JSON body and the HTTP status curl reports with -w."""
    command = ["docker", "exec", OPS_CONTAINER, "curl", "-sS", "-w", "\n%{http_code}", url]
    try:
        completed = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=ADMIN_TIMEOUT)
    except (OSError, subprocess.TimeoutExpired) as error:
        return 502, {"ok": False, "error": "ops command failed: %s" % error}
    text = completed.stdout.decode("utf-8", "replace").strip()
    if completed.returncode != 0:
        detail = completed.stderr.decode("utf-8", "replace").strip() or text
        return 502, {"ok": False, "error": "ops command exited %d: %s" % (completed.returncode, detail)}
    body, _, status_text = text.rpartition("\n")
    try:
        status = int(status_text)
    except ValueError:
        status = 502
    body = body.strip()
    try:
        payload = json.loads(body) if body else None
    except ValueError:
        return 502, {"ok": False, "error": "ops endpoint returned non-JSON: %s" % body[:200]}
    return status, payload


def ops_state(path, window=None):
    """(status, payload) of GET /admin/ops<path>; the window query is optional."""
    url = _ops_url(path, window)
    if OPS_URL:
        return _http_ops(url)
    if OPS_CMD:
        return _run_admin(OPS_CMD.format(url=shlex.quote(url)), None)
    return _run_ops(url)


# --------------------------------------------------------------------------- #
# apply (restart the API and wait for readiness)
# --------------------------------------------------------------------------- #


def _decode(data):
    return (data or b"").decode("utf-8", "replace").strip()


def _combined(completed):
    out = _decode(completed.stdout)
    err = _decode(completed.stderr)
    if out and err:
        return out + "\n" + err
    return out or err


def run_health_probe():
    """(ok, detail) of the configured readiness command."""
    try:
        completed = subprocess.run(
            HEALTH_CMD, shell=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            timeout=ADMIN_TIMEOUT,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        return False, "health probe failed: %s" % error
    output = _combined(completed)[:400]
    if completed.returncode != 0:
        return False, output or ("health probe exited %d" % completed.returncode)
    return True, output


def api_started_at():
    """Epoch seconds when the API container started, or None when it cannot be read."""
    try:
        completed = subprocess.run(
            ["docker", "inspect", "-f", "{{.State.StartedAt}}", API_CONTAINER],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=ADMIN_TIMEOUT,
        )
    except (OSError, subprocess.TimeoutExpired):
        return None
    if completed.returncode != 0:
        return None
    text = _decode(completed.stdout)
    if not text:
        return None
    try:
        return datetime.fromisoformat(text.replace("Z", "+00:00")).timestamp()
    except ValueError:
        return None


def pending_overrides():
    """Override files newer than the running API container's start time."""
    names = []
    if OVERRIDE_DIR.is_dir():
        for path in sorted(OVERRIDE_DIR.glob("masters_*.json")):
            name = path.name[len("masters_"):-len(".json")]
            if TABLE_NAME_RE.match(name):
                names.append((name, path.stat().st_mtime))
    started = api_started_at()
    pending = [name for name, mtime in names if started is not None and mtime > started]
    result = {
        "apiStartedAt": started,
        "apiStartedAtIso": (datetime.fromtimestamp(started).isoformat() if started is not None else None),
        "pending": pending,
        "overrides": [name for name, _ in names],
    }
    if started is None:
        result["note"] = ("could not read the API container start time (%s); "
                          "pending status unknown" % API_CONTAINER)
    return result


def apply_status():
    """Current pending-override state plus a live readiness probe."""
    state = pending_overrides()
    healthy, detail = run_health_probe()
    state["container"] = API_CONTAINER
    state["applyCmd"] = APPLY_CMD
    state["health"] = {"ok": healthy, "detail": detail}
    return state


def apply_changes():
    """(status, payload) after running the restart command and waiting for health."""
    started = time.time()
    # A command that exits 0 without restarting anything (a bare "docker" from an unquoted systemd
    # Environment= line did exactly that) must not report success, so compare the container start time.
    container_started_before = api_started_at()
    try:
        completed = subprocess.run(
            APPLY_CMD, shell=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            timeout=APPLY_TIMEOUT,
        )
    except subprocess.TimeoutExpired as error:
        return 502, {
            "ok": False, "command": APPLY_CMD,
            "error": "apply command timed out after %ss" % APPLY_TIMEOUT,
            "output": _decode(error.stdout),
        }
    except OSError as error:
        return 502, {"ok": False, "command": APPLY_CMD, "error": "apply command failed: %s" % error}

    output = _combined(completed)
    if completed.returncode != 0:
        return 502, {
            "ok": False, "command": APPLY_CMD,
            "error": "apply command exited %d" % completed.returncode,
            "output": output,
        }

    deadline = time.time() + APPLY_TIMEOUT
    attempts = 0
    healthy = False
    detail = ""
    while time.time() < deadline:
        attempts += 1
        healthy, detail = run_health_probe()
        if healthy:
            break
        time.sleep(HEALTH_INTERVAL)

    result = {
        "ok": healthy,
        "command": APPLY_CMD,
        "output": output,
        "health": {"ok": healthy, "detail": detail, "attempts": attempts},
        "elapsedSeconds": round(time.time() - started, 1),
    }
    if healthy and container_started_before is not None and api_started_at() == container_started_before:
        result["ok"] = False
        result["error"] = ("%s was not restarted (start time unchanged): check the apply command %r"
                           % (API_CONTAINER, APPLY_CMD))
        return 502, result
    if healthy:
        result["pending"] = pending_overrides()["pending"]
        return 200, result
    result["error"] = "API did not become ready: %s" % detail
    return 502, result


# --------------------------------------------------------------------------- #
# HTTP
# --------------------------------------------------------------------------- #


class BalanceHandler(BaseHTTPRequestHandler):
    server_version = "BalanceTool/3.0"
    protocol_version = "HTTP/1.1"
    timeout = 60

    # -- helpers ----------------------------------------------------------- #

    def allowed_hosts(self):
        """Host values accepted for this instance: the loopback names on the bound port + env extras."""
        try:
            port = self.server.server_address[1]
        except (AttributeError, IndexError, TypeError):
            port = 0
        hosts = {
            "127.0.0.1:%d" % port,
            "localhost:%d" % port,
            "[::1]:%d" % port,
        }
        hosts.update(ALLOWED_HOSTS_EXTRA)
        return hosts

    def host_allowed(self):
        """Reject DNS-rebinding: the Host must name this loopback service, not an attacker's domain."""
        return (self.headers.get("Host") or "").strip().lower() in self.allowed_hosts()

    def origin_allowed(self):
        """Accept loopback HTTP origins and explicitly configured HTTPS origins only."""
        origin = self.headers.get("Origin")
        if origin is None:
            return True
        origin = origin.strip().lower()
        try:
            parsed = urlsplit(origin)
        except ValueError:
            return False
        if parsed.path or parsed.query or parsed.fragment or parsed.username or parsed.password:
            return False
        authority = parsed.netloc
        request_host = (self.headers.get("Host") or "").strip().lower()
        if authority not in self.allowed_hosts() or authority != request_host:
            return False
        if parsed.scheme == "http":
            return authority in {"127.0.0.1:%d" % self.server.server_address[1],
                                 "localhost:%d" % self.server.server_address[1],
                                 "[::1]:%d" % self.server.server_address[1]}
        return parsed.scheme == "https" and origin in ALLOWED_ORIGINS_EXTRA

    def content_type_is_json(self):
        return (self.headers.get("Content-Type") or "").strip().lower().split(";", 1)[0].strip() == JSON_CONTENT_TYPE

    def reject(self, status, message):
        """Refuse a request that did not pass the CSRF checks; close so an unread body is discarded."""
        self.close_connection = True
        self.send_error_json(status, message)

    def origin_and_host_ok(self, *, require_origin=False):
        """False (after answering 403) when the Host or Origin does not belong to this service."""
        if not self.host_allowed():
            self.reject(403, "forbidden: Host %r is not allowed" % (self.headers.get("Host") or ""))
            return False
        if require_origin and not self.headers.get("Origin"):
            self.reject(403, "an allowed Origin is required")
            return False
        if not self.origin_allowed():
            self.reject(403, "forbidden: Origin %r is not allowed" % (self.headers.get("Origin") or ""))
            return False
        return True

    def state_change_ok(self):
        """The CSRF gate for POST/PUT/DELETE: Host + Origin first, then the JSON Content-Type."""
        if not self.origin_and_host_ok(require_origin=True):
            return False
        if not self.content_type_is_json():
            self.reject(415, "Content-Type must be application/json")
            return False
        return True

    def request_path(self):
        """Return the path relative to BASE_PATH, or None for a request outside the mounted app."""
        path = urlsplit(self.path).path
        if BASE_PATH:
            if path == BASE_PATH:
                return ""
            if not path.startswith(BASE_PATH + "/"):
                return None
            path = path[len(BASE_PATH):]
        return path or "/"

    def session_token(self):
        cookie = SimpleCookie()
        try:
            cookie.load(self.headers.get("Cookie", ""))
        except Exception:
            return None
        morsel = cookie.get(SESSION_COOKIE_NAME)
        return morsel.value if morsel is not None else None

    def client_ip(self):
        """Use proxy-provided client IP only when the immediate peer is explicitly trusted."""
        peer = self.client_address[0]
        if peer not in TRUSTED_PROXIES:
            return peer
        forwarded = (self.headers.get("X-Real-IP") or "").strip()
        try:
            return str(ipaddress.ip_address(forwarded))
        except ValueError:
            return peer

    def require_auth(self, *, browser_page=False):
        store = getattr(self.server, "auth_store", None) or AUTH_STORE
        username = store.resolve_session(self.session_token()) if store is not None else None
        if username:
            self.auth_username = username
            return True
        path = self.request_path()
        if browser_page and path in ("/", "/index.html"):
            self.send_body(303, b"", "text/plain; charset=utf-8",
                           {"Location": BASE_PATH + "/login"})
            return False
        self.send_error_json(401, "authentication required")
        return False

    def set_session_cookie(self, token, max_age):
        cookie_path = BASE_PATH or "/"
        return (f"{SESSION_COOKIE_NAME}={token}; Path={cookie_path}; Max-Age={max_age}; "
                "Secure; HttpOnly; SameSite=Strict")

    def clear_session_cookie(self):
        cookie_path = BASE_PATH or "/"
        return (f"{SESSION_COOKIE_NAME}=; Path={cookie_path}; Max-Age=0; "
                "Secure; HttpOnly; SameSite=Strict")

    def send_body(self, status, body, content_type, extra_headers=None):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        if self.close_connection:
            self.send_header("Connection", "close")
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("X-Frame-Options", "DENY")
        self.send_header("Referrer-Policy", "no-referrer")
        for key, value in (extra_headers or {}).items():
            self.send_header(key, value)
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(body)

    def send_json(self, status, payload):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_body(status, body, "application/json; charset=utf-8")

    def send_error_json(self, status, message):
        self.send_json(status, {"ok": False, "error": message})

    def send_file(self, path, content_type):
        try:
            body = path.read_bytes()
        except OSError as error:
            self.send_error_json(500, "could not read %s: %s" % (path, error))
            return
        self.send_body(200, body, content_type)

    def read_body(self, max_bytes=MAX_BODY_BYTES):
        """(bytes, None) or (None, (status, message))."""
        raw_length = self.headers.get("Content-Length")
        if raw_length is None:
            return None, (400, "missing Content-Length header")
        try:
            length = int(raw_length)
        except ValueError:
            return None, (400, "invalid Content-Length header")
        if length <= 0:
            return None, (400, "empty request body")
        if length > max_bytes:
            return None, (413, "request body larger than %d bytes" % max_bytes)
        body = self.rfile.read(length)
        if len(body) != length:
            return None, (400, "truncated request body")
        return body, None

    # -- routes ------------------------------------------------------------ #

    def do_GET(self):  # noqa: N802 - name fixed by BaseHTTPRequestHandler
        # The Host check also covers GET so a DNS-rebinding page cannot read the masters.
        if not self.origin_and_host_ok():
            return
        path = self.request_path()
        if path is None:
            self.send_error_json(404, "not found")
            return
        if BASE_PATH and path == "":
            self.send_body(308, b"", "text/plain; charset=utf-8", {"Location": BASE_PATH + "/"})
            return
        if path == "/login":
            try:
                html = LOGIN_FILE.read_text(encoding="utf-8")
            except OSError as error:
                self.send_error_json(500, "could not read login page: %s" % error)
                return
            html = html.replace("__BALANCE_BASE_PATH__", BASE_PATH)
            self.send_body(200, html.encode("utf-8"), "text/html; charset=utf-8")
            return
        if path == "/api/login":
            self.send_error_json(405, "method not allowed")
            return
        if not self.require_auth(browser_page=True):
            return
        if path == "/api/session":
            self.send_json(200, {"ok": True, "username": self.auth_username})
            return
        if path in ("/", "/index.html"):
            try:
                html = INDEX_FILE.read_text(encoding="utf-8")
            except OSError as error:
                self.send_error_json(500, "could not read balance UI: %s" % error)
                return
            html = html.replace("__BALANCE_BASE_PATH__", BASE_PATH)
            self.send_body(200, html.encode("utf-8"), "text/html; charset=utf-8")
            return
        if path == "/favicon.ico":
            self.send_body(204, b"", "image/x-icon")
            return
        if path == "/api/kickers":
            self.handle_kickers()
            return
        if path == "/api/discs":
            self.handle_discs()
            return
        if path == "/api/effect-schema":
            self.handle_effect_schema()
            return
        if path == "/api/enums":
            self.handle_enums()
            return
        if path == "/api/tables":
            self.send_json(200, list_tables())
            return
        if path == "/api/overrides/export":
            self.handle_export_overrides()
            return
        if path == "/api/apply":
            self.send_json(200, apply_status())
            return
        if path == "/api/maintenance":
            self.handle_get_maintenance()
            return
        if path == "/api/latest-information":
            self.handle_get_latest_information()
            return
        if path == "/api/ops/summary":
            self.handle_ops_summary()
            return
        if path == "/api/ops/exceptions":
            self.handle_ops_exceptions()
            return
        match = OPS_BATTLE_URL_RE.match(path)
        if match:
            self.handle_ops_battle(match.group(1))
            return
        if path == "/api/layout":
            # docs/localize_layout.json: where every LocalizeText of the UI prefabs sits (scripts/extract_localize_layout.py)
            layout = DOCS_DIR / "localize_layout.json"
            if not layout.is_file():
                self.send_json(200, [])
                return
            self.send_file(layout, "application/json; charset=utf-8")
            return
        match = TABLE_URL_RE.match(path)
        if match:
            self.handle_get_table(match.group(1))
            return
        match = DIFF_URL_RE.match(path)
        if match:
            self.handle_diff(match.group(1))
            return
        if path.startswith("/icons/"):
            self.handle_icon(path[len("/icons/"):])
            return
        self.send_error_json(404, "no route for %s" % path)

    def do_HEAD(self):  # noqa: N802
        self.do_GET()

    def do_POST(self):  # noqa: N802
        # Rejecting before reading a body can leave it in the HTTP/1.1 stream. Close after
        # every POST response so a proxy/client cannot parse that body as the next request.
        self.close_connection = True
        if not self.state_change_ok():
            return
        path = self.request_path()
        if path is None:
            self.send_error_json(404, "not found")
            return
        if path == "/api/login":
            self.handle_login()
            return
        if not self.require_auth():
            return
        if path == "/api/logout":
            self.handle_logout()
            return
        if path == "/api/maintenance":
            self.handle_post_maintenance()
            return
        if path == "/api/latest-information":
            self.handle_post_latest_information()
            return
        if path == "/api/apply":
            self.handle_apply()
            return
        match = REVERT_URL_RE.match(path)
        if match:
            self.handle_revert_table(match.group(1))
            return
        match = TABLE_URL_RE.match(path)
        if not match:
            self.send_error_json(404, "no route for %s" % path)
            return
        self.handle_post_table(match.group(1))

    def do_PUT(self):  # noqa: N802
        self.handle_unsupported_state_change()

    def do_DELETE(self):  # noqa: N802
        self.handle_unsupported_state_change()

    def handle_unsupported_state_change(self):
        """No put/delete routes exist; still apply the CSRF gate before answering 405."""
        self.close_connection = True
        if not self.state_change_ok():
            return
        if not self.require_auth():
            return
        self.send_error_json(405, "method %s is not supported" % self.command)

    def handle_login(self):
        body, error = self.read_body(max_bytes=8192)
        if error:
            self.send_error_json(*error)
            return
        try:
            payload = json.loads(body)
        except (json.JSONDecodeError, UnicodeDecodeError):
            self.send_error_json(400, "invalid login payload")
            return
        username = payload.get("username") if isinstance(payload, dict) else None
        password = payload.get("password") if isinstance(payload, dict) else None
        if not isinstance(username, str) or not isinstance(password, str):
            self.send_error_json(400, "username and password are required")
            return
        try:
            password_size = len(password.encode("utf-8"))
        except UnicodeEncodeError:
            self.send_error_json(400, "invalid login payload")
            return
        if len(username) > 128 or password_size > 1024:
            self.send_error_json(400, "invalid login payload")
            return
        store = getattr(self.server, "auth_store", None) or AUTH_STORE
        if store is None:
            self.send_error_json(503, "authentication database unavailable")
            return
        normalized = normalize_username(username)
        ip_address = self.client_ip()
        ip_retry_after = LOGIN_LIMITER.blocked_for(ip_address, normalized)
        account_blocked = store.account_retry_after(
            normalized, max_failures=ACCOUNT_LOGIN_MAX_FAILURES,
        ) > 0
        if ip_retry_after:
            self.send_error_json_with_headers(429, "login temporarily unavailable",
                                               {"Retry-After": str(ip_retry_after)})
            return
        if not AUTH_WORK_SLOTS.acquire(blocking=False):
            self.send_error_json_with_headers(429, "login temporarily unavailable", {"Retry-After": "1"})
            return
        try:
            if account_blocked:
                store.dummy_authenticate(password)
                authenticated = None
            else:
                authenticated = store.authenticate(normalized, password)
        finally:
            AUTH_WORK_SLOTS.release()
        if account_blocked:
            # Never verify a throttled account's submitted password. The dummy PBKDF above matches
            # unknown-user work and the generic error prevents username enumeration.
            self.send_error_json(401, "invalid username or password")
            return
        if authenticated is None:
            LOGIN_LIMITER.record_failure(ip_address, normalized)
            store.record_account_failure(normalized, max_failures=ACCOUNT_LOGIN_MAX_FAILURES)
            self.send_error_json(401, "invalid username or password")
            return
        LOGIN_LIMITER.clear(ip_address, normalized)
        token, expires_at = store.create_session(authenticated)
        max_age = max(0, expires_at - int(time.time()))
        self.send_json_with_headers(200, {"ok": True}, {"Set-Cookie": self.set_session_cookie(token, max_age)})

    def handle_logout(self):
        store = getattr(self.server, "auth_store", None) or AUTH_STORE
        if store is not None:
            store.revoke_session(self.session_token())
        self.send_json_with_headers(200, {"ok": True}, {"Set-Cookie": self.clear_session_cookie()})

    def send_json_with_headers(self, status, payload, headers):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_body(status, body, "application/json; charset=utf-8", headers)

    def send_error_json_with_headers(self, status, message, headers):
        self.send_json_with_headers(status, {"ok": False, "error": message}, headers)

    # -- endpoint bodies --------------------------------------------------- #

    def handle_kickers(self):
        try:
            kicker_rows = read_table("kicker")
            parameter_rows = read_table("kicker_parameter")
        except (OSError, ValueError, FileNotFoundError) as error:
            self.send_error_json(500, str(error))
            return
        parameters = {
            row.get("kickerId"): row for row in parameter_rows if isinstance(row, dict)
        }
        kickers = []
        for row in kicker_rows:
            if not isinstance(row, dict) or "id" not in row:
                continue
            parameter = parameters.get(row["id"], {})
            kickers.append(
                {
                    "id": row["id"],
                    "name": row.get("name") or "Kicker %s" % row["id"],
                    "weaponType": parameter.get("weaponType"),
                    "roleType": parameter.get("roleType"),
                    "skillId": parameter.get("skillId"),
                }
            )
        self.send_json(200, kickers)

    def handle_discs(self):
        try:
            disc_rows = read_table("disc")
        except (OSError, ValueError, FileNotFoundError) as error:
            self.send_error_json(500, str(error))
            return
        cards = load_disc_cards()
        discs = []
        for row in disc_rows:
            if not isinstance(row, dict) or "id" not in row:
                continue
            card = cards.get(str(row["id"])) or {}
            discs.append(
                {
                    "id": row["id"],
                    "name": row.get("name") or card.get("name") or "Disc %s" % row["id"],
                    "rarityType": row.get("rarityType"),
                    "discType": row.get("discType"),
                    "skillId": row.get("skillId"),
                    "effect": card.get("effect") or "",
                    "type": card.get("type") or "",
                    "attribute": card.get("attribute") or "",
                    "rarity": card.get("rarity") or "",
                }
            )
        self.send_json(200, discs)

    def handle_effect_schema(self):
        """Column types and neutral payloads the UI needs to add an effect row."""
        self.send_json(200, {
            "tables": {
                table: {"fields": schema, "defaults": DEFAULT_EFFECT_ROWS.get(table, {})}
                for table, schema in EFFECT_SCHEMAS.items()
            }
        })

    def handle_enums(self):
        """Named client enums plus the {table.field: enum} map the UI renders as dropdowns."""
        try:
            self.send_json(200, load_enums())
        except (OSError, ValueError) as error:
            self.send_error_json(500, "could not load enums.json: %s" % error)

    def handle_get_maintenance(self):
        status, payload = admin_state()
        if status == 200 and payload is None:
            status, payload = 502, {
                "ok": False,
                "error": "empty response from the admin endpoint (is the API's loopback reachable "
                         "from where this tool runs?)",
            }
        self.send_json(status, payload)

    def handle_post_maintenance(self):
        body, error = self.read_body()
        if error is not None:
            self.send_error_json(error[0], error[1])
            return
        try:
            payload = json.loads(body.decode("utf-8"))
        except (UnicodeDecodeError, ValueError) as decode_error:
            self.send_error_json(400, "body is not valid JSON: %s" % decode_error)
            return
        if not isinstance(payload, dict) or not isinstance(payload.get("mode"), str):
            self.send_error_json(400, "body must be an object with a string \"mode\"")
            return
        if payload["mode"] not in ("off", "warning", "hard"):
            self.send_error_json(400, "mode must be off, warning or hard")
            return
        forward = {"mode": payload["mode"]}
        for key in ("title", "message"):
            if isinstance(payload.get(key), str):
                forward[key] = payload[key]
        status, result = admin_set(forward)
        if status == 200 and result is None:
            status, result = 502, {
                "ok": False,
                "error": "empty response from the admin endpoint (is the API's loopback reachable "
                         "from where this tool runs?)",
            }
        self.send_json(status, result)

    def handle_get_latest_information(self):
        status, payload = latest_information_state()
        if status == 200 and payload is None:
            status, payload = 502, {"ok": False, "error": "empty response from the latest-information admin endpoint"}
        self.send_json(status, payload)

    def query_window(self):
        """The ?window= value of the current request, or None so the API applies its own default (deploy)."""
        values = parse_qs(urlsplit(self.path).query).get("window") or []
        return values[-1].strip() if values else None

    def handle_ops_summary(self):
        self.send_ops("/summary", self.query_window())

    def handle_ops_exceptions(self):
        self.send_ops("/exceptions", self.query_window())

    def handle_ops_battle(self, battle_id):
        battle_id = unquote(battle_id)
        if not OPS_BATTLE_ID_RE.fullmatch(battle_id):
            self.send_error_json(400, "invalid battle id")
            return
        self.send_ops("/battle/" + quote(battle_id, safe=""), None)

    def send_ops(self, path, window):
        status, payload = ops_state(path, window)
        if status == 200 and payload is None:
            status, payload = 502, {
                "ok": False,
                "error": "empty response from the ops endpoint: the API only answers its own loopback "
                         "and the command must run there (docker exec / SSH tunnel)",
            }
        self.send_json(status, payload)

    def handle_post_latest_information(self):
        body, error = self.read_body()
        if error is not None:
            self.send_error_json(error[0], error[1])
            return
        try:
            payload = json.loads(body.decode("utf-8"))
        except (UnicodeDecodeError, ValueError) as decode_error:
            self.send_error_json(400, "body is not valid JSON: %s" % decode_error)
            return
        if not isinstance(payload, dict):
            self.send_error_json(400, "body must be a Latest Information object")
            return
        mode = payload.get("mode", "text")  # Legacy clients sent only {content: ...}.
        if mode == "text" and isinstance(payload.get("content"), str):
            if len(payload["content"]) > LATEST_INFO_MAX_CHARS:
                self.send_error_json(400, "content must be %d characters or fewer" % LATEST_INFO_MAX_CHARS)
                return
            forward = {"mode": "text", "content": payload["content"]}
        elif mode == "html" and isinstance(payload.get("htmlContent"), str):
            html_content = payload["htmlContent"]
            if len(html_content.encode("utf-8")) > LATEST_INFO_MAX_HTML_BYTES:
                self.send_error_json(400, "HTML must be %d UTF-8 bytes or fewer" % LATEST_INFO_MAX_HTML_BYTES)
                return
            forward = {"mode": "html", "htmlContent": html_content}
        elif mode == "url" and isinstance(payload.get("externalUrl"), str):
            external_url = payload["externalUrl"].strip()
            try:
                parsed = urlsplit(external_url)
                valid_url = (len(external_url) <= LATEST_INFO_MAX_URL_CHARS and
                             not any(ord(character) < 0x20 or ord(character) == 0x7f for character in external_url) and
                             parsed.scheme.lower() in ("http", "https") and bool(parsed.hostname) and
                             parsed.username is None and parsed.password is None)
                _ = parsed.port  # Make malformed ports fail validation too.
            except ValueError:
                valid_url = False
            if not valid_url:
                self.send_error_json(400, "URL must be an absolute HTTP or HTTPS URL without embedded credentials")
                return
            forward = {"mode": "url", "externalUrl": external_url}
        else:
            self.send_error_json(400, "mode must be text, html, or url with its corresponding string value")
            return
        status, result = latest_information_set(forward)
        if status == 200 and result is None:
            status, result = 502, {"ok": False, "error": "empty response from the latest-information admin endpoint"}
        self.send_json(status, result)

    def handle_get_table(self, name):
        path = table_path(name)
        if path is None:
            self.send_error_json(404, "unknown table %r" % name)
            return
        try:
            rows = read_table(name)
        except (OSError, ValueError) as error:
            self.send_error_json(500, str(error))
            return
        self.send_json(200, rows)

    def handle_post_table(self, name):
        if table_path(name) is None:
            self.send_error_json(404, "unknown table %r" % name)
            return
        body, error = self.read_body()
        if error is not None:
            self.send_error_json(error[0], error[1])
            return
        try:
            payload = json.loads(body.decode("utf-8"))
        except (UnicodeDecodeError, ValueError) as decode_error:
            self.send_error_json(400, "body is not valid JSON: %s" % decode_error)
            return
        if not isinstance(payload, dict) or "rows" not in payload:
            self.send_error_json(400, "body must be an object with a \"rows\" array")
            return

        path = override_path(name) or base_path(name)
        with WRITE_LOCK:
            try:
                original_text = path.read_text(encoding="utf-8")
                current_rows = json.loads(original_text)
            except (OSError, ValueError) as read_error:
                self.send_error_json(500, "could not read masters_%s.json: %s" % (name, read_error))
                return
            if not isinstance(current_rows, list):
                self.send_error_json(500, "masters_%s.json is not a JSON array" % name)
                return

            rows, messages = validate_rows(name, current_rows, payload["rows"])
            if rows is None:
                self.send_error_json(400, "; ".join(messages))
                return

            try:
                backup_table(name, original_text)
            except OSError as backup_error:
                self.send_error_json(500, "could not write the backup: %s" % backup_error)
                return
            try:
                write_table(name, rows)
            except OSError as write_error:
                self.send_error_json(500, "could not write the override: %s" % write_error)
                return

        self.send_json(200, {
            "ok": True,
            "written": len(rows),
            "override": str(OVERRIDE_DIR / ("masters_%s.json" % name)),
        })

    def handle_revert_table(self, name):
        """Delete the override file so the table falls back to the base config/ file."""
        if not TABLE_NAME_RE.match(name):
            self.send_error_json(404, "unknown table %r" % name)
            return
        with WRITE_LOCK:
            path = override_path(name)
            if path is None:
                self.send_error_json(404, "masters_%s.json has no override to revert" % name)
                return
            try:
                original_text = path.read_text(encoding="utf-8")
            except OSError as read_error:
                self.send_error_json(500, "could not read the override: %s" % read_error)
                return
            try:
                backup_table(name, original_text)
            except OSError as backup_error:
                self.send_error_json(500, "could not write the backup: %s" % backup_error)
                return
            try:
                path.unlink()
            except OSError as delete_error:
                self.send_error_json(500, "could not delete the override: %s" % delete_error)
                return
        self.send_json(200, {
            "ok": True,
            "reverted": name,
            "source": "base" if base_path(name) is not None else None,
        })

    def handle_export_overrides(self):
        try:
            payload = export_overrides_zip()
        except OSError as error:
            self.send_error_json(500, "could not build the archive: %s" % error)
            return
        self.send_body(
            200, payload, "application/zip",
            {"Content-Disposition": 'attachment; filename="masters-overrides.zip"'},
        )

    def handle_apply(self):
        status, payload = apply_changes()
        self.send_json(status, payload)

    def handle_diff(self, name):
        if table_path(name) is None:
            self.send_error_json(404, "unknown table %r" % name)
            return
        try:
            rows = read_table(name)
        except (OSError, ValueError) as error:
            self.send_error_json(500, str(error))
            return
        self.send_json(200, {"changed": diff_against_latest_backup(name, rows)})

    def handle_icon(self, name):
        if not ICON_NAME_RE.match(name):
            self.send_error_json(404, "unknown icon %r" % name)
            return
        path = ICON_DIR / name
        if not path.is_file():
            self.send_error_json(404, "unknown icon %r" % name)
            return
        self.send_file(path, "image/png")


def build_server(port, host="127.0.0.1", auth_store=None):
    server = ThreadingHTTPServer((host, port), BalanceHandler)
    server.auth_store = auth_store
    return server


def lan_addresses():
    """IPv4 addresses of this machine's interfaces, best effort (for the URL hints only)."""
    import socket

    found = []
    try:
        for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
            ip = info[4][0]
            if ip not in found and not ip.startswith("127."):
                found.append(ip)
    except OSError:
        pass
    return found


def main(argv=None):
    global MASTERS_DIR, OVERRIDE_DIR, BACKUP_DIR
    parser = argparse.ArgumentParser(description="Balance WebUI for masters_*.json")
    parser.add_argument(
        "--port", type=int, default=int(os.environ.get("KF_BALANCE_PORT", "8765")),
        help="TCP port to listen on (default: 8765, env KF_BALANCE_PORT)",
    )
    parser.add_argument(
        "--host", default=os.environ.get("KF_BALANCE_HOST", "127.0.0.1"),
        help="interface to bind (default: 127.0.0.1 = loopback only; 0.0.0.0 = every device on the LAN)",
    )
    parser.add_argument(
        "--masters-dir", default=None,
        help="base directory the game server ships its masters in (env KF_BALANCE_BASE_DIR, alias KF_BALANCE_MASTERS_DIR)",
    )
    parser.add_argument(
        "--override-dir", default=None,
        help="where saves are written and overrides read from (env KF_BALANCE_OVERRIDE_DIR)",
    )
    parser.add_argument(
        "--backup-dir", default=None,
        help="where overwritten files are copied (env KF_BALANCE_BACKUP_DIR)",
    )
    parser.add_argument(
        "--open", action="store_true", help="open the UI in the default browser once the server is up"
    )
    args = parser.parse_args(argv)

    if args.masters_dir:
        MASTERS_DIR = Path(args.masters_dir).expanduser()
    if args.override_dir:
        OVERRIDE_DIR = Path(args.override_dir).expanduser()
    if args.backup_dir:
        BACKUP_DIR = Path(args.backup_dir).expanduser()

    if not INDEX_FILE.is_file() or not LOGIN_FILE.is_file():
        print("missing %s" % INDEX_FILE, file=sys.stderr)
        return 1
    if not MASTERS_DIR.is_dir():
        print("base masters directory does not exist: %s" % MASTERS_DIR, file=sys.stderr)
        return 1
    if OVERRIDE_DIR == MASTERS_DIR:
        print("override directory must differ from the base masters directory", file=sys.stderr)
        return 1
    try:
        OVERRIDE_DIR.mkdir(parents=True, exist_ok=True)
    except OSError as error:
        print("could not create override directory %s: %s" % (OVERRIDE_DIR, error), file=sys.stderr)
        return 1

    try:
        jwt_secret = load_jwt_secret(JWT_SECRET_PATH)
        auth_store = AuthStore(AUTH_DB_PATH, jwt_secret=jwt_secret)
    except (OSError, RuntimeError, sqlite3.Error) as error:
        print("could not initialize required authentication: %s" % error, file=sys.stderr)
        return 1
    except ValueError as error:
        print("could not initialize required authentication: %s" % error, file=sys.stderr)
        return 1

    try:
        server = build_server(args.port, args.host, auth_store)
    except OSError as error:
        print("could not bind %s:%d: %s" % (args.host, args.port, error), file=sys.stderr)
        return 1

    server.daemon_threads = True
    local_url = "http://127.0.0.1:%d%s/" % (args.port, BASE_PATH)
    print("Balance WebUI listening on %s" % local_url)
    if args.host == "0.0.0.0":
        for ip in lan_addresses():
            print("  from the LAN:  http://%s:%d%s/" % (ip, args.port, BASE_PATH))
        print("  WARNING: use a TLS reverse proxy and configure allowed Host/Origin values before public access")
    print("authentication database: %s (users must be inserted manually)" % AUTH_DB_PATH)
    if ALLOWED_ORIGINS_EXTRA:
        print("allowed public origins: %s" % ", ".join(ALLOWED_ORIGINS_EXTRA))
    print("base masters: %s" % MASTERS_DIR)
    print("overrides (written here): %s" % OVERRIDE_DIR)
    print("backups: %s" % BACKUP_DIR)
    if ADMIN_URL:
        print("maintenance via %s" % ADMIN_URL)
    else:
        print("maintenance via docker exec %s curl %s" % (ADMIN_CONTAINER, ADMIN_INTERNAL_URL))
    print("apply via %r; health %r" % (APPLY_CMD, HEALTH_CMD))
    print("press Ctrl+C to stop")
    sys.stdout.flush()
    if args.open:
        import webbrowser

        webbrowser.open(local_url)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nstopping")
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
