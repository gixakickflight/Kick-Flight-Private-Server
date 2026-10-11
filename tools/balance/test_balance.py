#!/usr/bin/env python3
"""Local acceptance test for the balance WebUI's master-override flow.

Uses only the standard library.  Copies ``config/masters_*.json`` into a temp *base* directory,
starts a mock ``/admin/maintenance`` endpoint plus a fake ``docker`` (so the API start-time and the
apply command can be exercised without Docker), then drives the WebUI HTTP API:

* reads come from the override file when present, else from the base file;
* saves write **only** to the override dir and never touch the base file;
* revert deletes the override (after a backup);
* export returns a zip of the override files;
* apply runs a configurable command and polls a health command;
* pending overrides are detected against the API container start time;
* effect-row add/delete and the core-table validation still work;
* maintenance is proxied to the mock admin endpoint;
* the Health tab's /admin/ops/* proxy forwards the window query, validates battle ids and passes the
  API's 400 body through;
* manually provisioned session authentication is enforced.

    python3 tools/balance/test_balance.py
"""

from __future__ import annotations

import io
import http.client
import json
import os
import shutil
import stat
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import zipfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
ROOT = Path("/tmp/kf-balance-test")
BASE = ROOT / "base"
OVERRIDES = ROOT / "overrides"
BACKUPS = ROOT / "backups"
FAKEBIN = ROOT / "fakebin"
STARTED_AT = ROOT / "started-at"
APPLIED_MARKER = ROOT / "applied.marker"
PORT = 18765
ADMIN_PORT = 19080
USER = "balance"
PASSWORD = "correct horse battery staple"
LOCKED_USER = "locked-balance-user"
LOCKED_PASSWORD = "another correct horse battery staple"
BASE_PATH = "/balance"
AUTH_DB = ROOT / "balance-auth" / "auth.sqlite3"
JWT_SECRET = ROOT / "balance-auth" / "jwt-secret"
SESSION_COOKIE = None

FAILURES = []


def check(name, condition, detail=""):
    status = "PASS" if condition else "FAIL"
    print("[%s] %s%s" % (status, name, (": " + detail) if detail and not condition else ""))
    if not condition:
        FAILURES.append(name)


def persistent_post(connection, path, payload, origin, cookie=None):
    headers = {"Content-Type": "application/json", "Origin": origin}
    if cookie:
        headers["Cookie"] = cookie
    connection.request("POST", BASE_PATH + path, body=json.dumps(payload).encode(), headers=headers)
    response = connection.getresponse()
    status = response.status
    response_headers = dict(response.getheaders())
    body = response.read()
    return status, response_headers, body


class MockAdmin(BaseHTTPRequestHandler):
    state = {"mode": "off", "title": "", "message": "", "noticeId": 0,
             "changedAtUtc": "2026-10-05T00:00:00Z"}

    def log_message(self, *args):
        pass

    def send_json(self, status, payload):
        body = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path.startswith("/admin/ops/"):
            self.handle_ops()
            return
        if self.path != "/admin/maintenance":
            if self.path != "/admin/latest-information":
                self.send_json(404, {})
                return
            self.send_json(200, dict(MockAdmin.latest_information))
            return
        self.send_json(200, dict(MockAdmin.state))

    def handle_ops(self):
        from urllib.parse import parse_qs, urlsplit
        parsed = urlsplit(self.path)
        window = (parse_qs(parsed.query).get("window") or [None])[-1]
        MockAdmin.ops_calls.append((parsed.path, window))
        if parsed.path == "/admin/ops/summary":
            if window not in (None, "deploy", "restart", "24h", "7d"):
                self.send_json(400, {"error": "unknown window '%s'" % window,
                                     "windows": ["deploy", "restart", "24h", "7d"]})
                return
            payload = dict(MockAdmin.ops_summary)
            payload["window"] = {"name": window or "deploy", "since": "2026-10-10T00:00:00Z",
                                 "releaseSha": "test-sha", "runId": "run-test", "runs": []}
            self.send_json(200, payload)
            return
        if parsed.path == "/admin/ops/exceptions":
            self.send_json(200, dict(MockAdmin.ops_exceptions))
            return
        if parsed.path.startswith("/admin/ops/battle/"):
            self.send_json(200, dict(MockAdmin.ops_battle))
            return
        self.send_json(404, {"error": "unknown ops path"})

    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        payload = json.loads(self.rfile.read(length) or b"{}")
        if self.path == "/admin/latest-information":
            mode = payload.get("mode", "text")
            MockAdmin.latest_information.update(payload)
            MockAdmin.latest_information["mode"] = mode
            MockAdmin.latest_information["changedAtUtc"] = "2026-10-08T00:00:00Z"
            self.send_json(200, dict(MockAdmin.latest_information))
            return
        MockAdmin.state["mode"] = payload.get("mode", "off")
        MockAdmin.state["title"] = payload.get("title", "")
        MockAdmin.state["message"] = payload.get("message", "")
        MockAdmin.state["noticeId"] += 1
        self.send_json(200, dict(MockAdmin.state))


MockAdmin.latest_information = {"content": "Default news", "defaultContent": "Default news",
                               "mode": "text", "htmlContent": "", "externalUrl": "",
                               "changedAtUtc": "2026-10-05T00:00:00Z"}

# Canned /admin/ops/* telemetry; MockAdmin.ops_calls records (path, window) so the proxy can be checked.
MockAdmin.ops_calls = []
MockAdmin.ops_summary = {
    "totals": {"matches": 4, "running": 0, "matching": 1, "clean": 3, "partial": 1, "abandoned": 0,
               "neverStarted": 2, "custom": 0, "entries": 9, "players": 6, "completedPlayers": 5,
               "reconnectedCompleted": 0, "disconnectedPlayers": 2, "droppedSilent": 1, "relaunched": 1,
               "reconnectFailed": 1, "left": 0, "matchingFailures": 2, "matchingTimeouts": 1, "cancelled": 1,
               "exceptions": 4, "anomalies": 0, "reconnectBegins": 1},
    "statuses": {"completed": 5, "dropped_silent": 1},
    "eventKinds": {"client_exception": 4},
    "breakdowns": {"appVersion": [], "device": [], "os": [], "rule": [], "field": []},
    "problemMatches": [{"battleId": "battle-123", "outcome": "partial", "humans": 2, "bots": 2, "custom": False,
                        "participants": [{"userId": "1000001", "status": "dropped_silent"}]}],
    "telemetry": {"store": "postgres", "enabled": True, "queueDepth": 0, "written": 10, "dropped": 0,
                  "writeErrors": 0, "lastWriteError": None, "lastWriteAt": None, "trackedBattles": 3},
}
MockAdmin.ops_exceptions = {
    "window": {"name": "deploy"}, "total": 4,
    "groups": [{"fingerprint": "0123456789abcdef", "kind": "client_exception",
                "signature": "NullReferenceException\nat Foo ()", "count": 4, "userCount": 2,
                "users": ["1000001", "1000002"], "devices": [], "os": [],
                "appVersions": [{"key": "2.11.2", "count": 4}], "battles": ["battle-123"],
                "firstAt": "2026-10-10T00:00:00Z", "lastAt": "2026-10-10T01:00:00Z", "sample": "boom"}],
}
MockAdmin.ops_battle = {
    "battle": {"battleId": "battle-123", "outcome": "partial", "ruleId": 7, "fieldId": 801, "humans": 2, "bots": 2,
               "custom": False, "createdAt": "2026-10-10T00:00:00Z"},
    "participants": [{"userId": "1000001", "status": "dropped_silent", "kickerId": 3010001, "costumeId": 64,
                      "team": 0, "appVersion": "2.11.2", "deviceName": "Pixel", "reconnectBegins": 1,
                      "exceptionCount": 2}],
    "timeline": [{"at": "2026-10-10T00:00:00Z", "kind": "start", "userId": "1000001", "payload": {"rule_id": 7}}],
}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        return None


def request(path, method="GET", body=None, auth=True, raw=False, headers=None, *, port=PORT, raw_path=False):
    global SESSION_COOKIE
    data = json.dumps(body).encode() if body is not None else None
    request_path = path if raw_path or path == BASE_PATH or path.startswith(BASE_PATH + "/") else BASE_PATH + path
    req = urllib.request.Request("http://127.0.0.1:%d%s" % (port, request_path), data=data, method=method)
    if method in ("POST", "PUT", "DELETE") or data is not None:
        req.add_header("Content-Type", "application/json")
        req.add_header("Origin", "http://127.0.0.1:%d" % port)
    for key, value in (headers or {}).items():
        req.add_header(key, value)
    if auth and SESSION_COOKIE:
        req.add_header("Cookie", SESSION_COOKIE)
    try:
        with urllib.request.urlopen(req, timeout=15) as response:
            payload = response.read()
            set_cookie = response.headers.get("Set-Cookie")
            if set_cookie and "Max-Age=0" not in set_cookie:
                SESSION_COOKIE = set_cookie.split(";", 1)[0]
            elif set_cookie and "Max-Age=0" in set_cookie:
                SESSION_COOKIE = None
            if raw:
                return response.status, payload, dict(response.headers)
            text = payload.decode()
            try:
                parsed = json.loads(text) if text else None
            except ValueError:
                parsed = payload
            return response.status, parsed
    except urllib.error.HTTPError as error:
        payload = error.read()
        set_cookie = error.headers.get("Set-Cookie")
        if set_cookie and "Max-Age=0" not in set_cookie:
            SESSION_COOKIE = set_cookie.split(";", 1)[0]
        if raw:
            return error.code, payload, dict(error.headers)
        try:
            return error.code, (json.loads(payload.decode()) if payload else None)
        except ValueError:
            return error.code, payload.decode()


def build_fake_docker():
    FAKEBIN.mkdir(parents=True, exist_ok=True)
    script = FAKEBIN / "docker"
    script.write_text(
        "#!/bin/sh\n"
        "if [ \"$1\" = \"inspect\" ]; then\n"
        "  cat \"$KF_FAKE_STARTED_AT\" 2>/dev/null || printf '%s\\n' '2000-01-01T00:00:00Z'\n"
        "  exit 0\n"
        "fi\n"
        "exit 1\n",
        encoding="utf-8",
    )
    script.chmod(script.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)


def started_at(value):
    STARTED_AT.write_text(value + "\n", encoding="utf-8")


def override_file(name):
    return OVERRIDES / ("masters_%s.json" % name)


def base_file(name):
    return BASE / ("masters_%s.json" % name)


def main():
    if ROOT.exists():
        shutil.rmtree(ROOT)
    BASE.mkdir(parents=True)
    OVERRIDES.mkdir(parents=True)
    BACKUPS.mkdir(parents=True)
    for path in (REPO / "config").glob("masters_*.json"):
        shutil.copy2(path, BASE / path.name)
    build_fake_docker()
    started_at("2099-01-01T00:00:00Z")   # nothing pending until a test says otherwise
    APPLIED_MARKER.unlink(missing_ok=True)

    admin = ThreadingHTTPServer(("127.0.0.1", ADMIN_PORT), MockAdmin)
    admin.daemon_threads = True
    threading.Thread(target=admin.serve_forever, daemon=True).start()

    env = dict(os.environ)
    env.update({
        "KF_BALANCE_BASE_DIR": str(BASE),
        "KF_BALANCE_OVERRIDE_DIR": str(OVERRIDES),
        "KF_BALANCE_BACKUP_DIR": str(BACKUPS),
        "KF_BALANCE_AUTH_DB": str(AUTH_DB),
        "KF_BALANCE_JWT_SECRET_FILE": str(JWT_SECRET),
        "KF_BALANCE_BASE_PATH": BASE_PATH,
        "KF_BALANCE_ALLOWED_ORIGINS": "https://balance.test",
        "KF_BALANCE_ADMIN_URL": "http://127.0.0.1:%d/admin/maintenance" % ADMIN_PORT,
        "KF_BALANCE_LATEST_INFO_URL": "http://127.0.0.1:%d/admin/latest-information" % ADMIN_PORT,
        "KF_BALANCE_OPS_URL": "http://127.0.0.1:%d/admin/ops" % ADMIN_PORT,
        "KF_BALANCE_ALLOWED_HOSTS": "balance.test",
        "KF_BALANCE_API_CONTAINER": "fake-api",
        # A real restart changes the container start time; the fake one moves it forward too.
        "KF_BALANCE_APPLY_CMD": "printf applied > %s && printf '2099-06-01T00:00:00Z\\n' > %s"
                                % (APPLIED_MARKER, STARTED_AT),
        "KF_BALANCE_HEALTH_CMD": "true",
        "KF_BALANCE_APPLY_TIMEOUT": "3",
        "KF_BALANCE_HEALTH_INTERVAL": "0.2",
        "KF_FAKE_STARTED_AT": str(STARTED_AT),
        "PATH": str(FAKEBIN) + os.pathsep + env.get("PATH", ""),
    })
    sys.path.insert(0, str(REPO / "tools/balance"))
    from auth import AuthStore, hash_password
    AUTH_DB.parent.mkdir(parents=True, mode=0o700)
    JWT_SECRET.write_bytes(os.urandom(32))
    os.chmod(JWT_SECRET, 0o600)
    auth_store = AuthStore(AUTH_DB, jwt_secret=JWT_SECRET.read_bytes())
    with __import__("sqlite3").connect(AUTH_DB) as connection:
        connection.execute(
            "INSERT INTO users(username,password_hash,created_at) VALUES(?,?,?)",
            (USER, hash_password(PASSWORD), int(time.time())),
        )
        connection.execute(
            "INSERT INTO users(username,password_hash,created_at) VALUES(?,?,?)",
            (LOCKED_USER, hash_password(LOCKED_PASSWORD), int(time.time())),
        )
    for _ in range(8):
        auth_store.record_account_failure(LOCKED_USER)
    webui = subprocess.Popen(
        [sys.executable, str(REPO / "tools/balance/server.py"),
         "--host", "127.0.0.1", "--port", str(PORT)],
        env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
    )
    try:
        wait_for_port(webui)

        # -- authentication -------------------------------------------------------
        status, page = request("/login", auth=False)
        check("login page is public", status == 200 and b"autocomplete=\"current-password\"" in page)
        bare_req = urllib.request.Request("http://127.0.0.1:%d%s" % (PORT, BASE_PATH))
        try:
            opener = urllib.request.build_opener(NoRedirect)
            with opener.open(bare_req, timeout=5) as response:
                status, redirect_headers = response.status, response.headers
        except urllib.error.HTTPError as error:
            status, redirect_headers = error.code, error.headers
        check("bare mount redirects to its slash form",
              status == 308 and redirect_headers.get("Location") == BASE_PATH + "/")
        status, _ = request("/api/discs", auth=False)
        check("GET without a session is 401", status == 401, "got %s" % status)
        page_req = urllib.request.Request("http://127.0.0.1:%d%s/" % (PORT, BASE_PATH))
        try:
            opener = urllib.request.build_opener(NoRedirect)
            with opener.open(page_req, timeout=5) as response:
                page_status, page_headers = response.status, response.headers
        except urllib.error.HTTPError as error:
            page_status, page_headers = error.code, error.headers
        check("unauthenticated UI redirects to the login page",
              page_status == 303 and page_headers.get("Location") == BASE_PATH + "/login")
        status, _ = request("/icons/disc_1.png", auth=False)
        check("static icons also require a session", status == 401, "got %s" % status)
        status, _ = request("/api/overrides/export", auth=False)
        check("override export requires a session", status == 401, "got %s" % status)
        status, _ = request("/api/latest-information", auth=False)
        check("Latest Information is protected by the dashboard session", status == 401, "got %s" % status)
        status, _ = request("/api/apply", "POST", {}, auth=False)
        check("unauthorized apply is denied before side effects", status == 401 and not APPLIED_MARKER.exists(),
              "got %s" % status)
        status, _ = request("/api/logout", "POST", {}, auth=False)
        check("logout also requires an authenticated session", status == 401, "got %s" % status)
        locked_status, locked_body = request(
            "/api/login", "POST", {"username": LOCKED_USER, "password": LOCKED_PASSWORD}, auth=False
        )
        unknown_status, unknown_body = request(
            "/api/login", "POST", {"username": "missing-user", "password": LOCKED_PASSWORD}, auth=False
        )
        check("locked account and unknown user have identical generic response",
              locked_status == unknown_status == 401 and locked_body == unknown_body and
              locked_body.get("error") == "invalid username or password",
              "locked=%s unknown=%s" % (locked_status, unknown_status))
        status, _ = request("/api/apply", "POST", {}, auth=False, headers={"Origin": ""})
        check("state-changing requests require Origin", status == 403, "got %s" % status)
        status, failed = request("/api/login", "POST", {"username": USER, "password": "wrong password"},
                                 auth=False, raw=False)
        check("failed login gives a generic credential error",
              status == 401 and failed.get("error") == "invalid username or password")
        status, _, login_headers = request(
            "/api/login", "POST", {"username": USER.upper(), "password": PASSWORD}, auth=False, raw=True
        )
        cookie_header = login_headers.get("Set-Cookie", "")
        check("valid login sets a scoped secure session cookie",
              status == 200 and "Secure" in cookie_header and "HttpOnly" in cookie_header and
              "SameSite=Strict" in cookie_header and "Path=/balance" in cookie_header and
              "Domain=" not in cookie_header)
        check("session token is stored only as a hash",
              bool(SESSION_COOKIE) and __session_is_hashed(AUTH_DB, SESSION_COOKIE.split("=", 1)[1]))
        status, session = request("/api/session")
        check("session endpoint identifies the signed-in user",
              status == 200 and session.get("username") == USER)
        status, discs = request("/api/discs")
        check("GET with a session is 200 + disc list",
              status == 200 and isinstance(discs, list) and discs, "status %s" % status)

        # POST error/success responses close persistent HTTP/1.1 connections because early
        # auth/CSRF responses and logout may leave the request body unread in the stream.
        local_origin = "http://127.0.0.1:%d" % PORT
        unauthorized_connection = http.client.HTTPConnection("127.0.0.1", PORT, timeout=5)
        try:
            denied_status, denied_headers, _ = persistent_post(
                unauthorized_connection, "/api/apply", {}, local_origin
            )
            relogin_status, _, _ = persistent_post(
                unauthorized_connection, "/api/login", {"username": USER, "password": PASSWORD}, local_origin
            )
            check("unauthorized POST body cannot poison the next request",
                  denied_status == 401 and denied_headers.get("Connection") == "close" and relogin_status == 200,
                  "denied %s, next %s" % (denied_status, relogin_status))
        finally:
            unauthorized_connection.close()

        forbidden_connection = http.client.HTTPConnection("127.0.0.1", PORT, timeout=5)
        try:
            forbidden_status, forbidden_headers, _ = persistent_post(
                forbidden_connection, "/api/apply", {}, "https://evil.example"
            )
            relogin_status, _, _ = persistent_post(
                forbidden_connection, "/api/login", {"username": USER, "password": PASSWORD}, local_origin
            )
            check("forbidden-Origin POST body cannot poison the next request",
                  forbidden_status == 403 and forbidden_headers.get("Connection") == "close" and
                  relogin_status == 200, "denied %s, next %s" % (forbidden_status, relogin_status))
        finally:
            forbidden_connection.close()

        logout_connection = http.client.HTTPConnection("127.0.0.1", PORT, timeout=5)
        try:
            login_status, login_headers, _ = persistent_post(
                logout_connection, "/api/login", {"username": USER, "password": PASSWORD}, local_origin
            )
            session_cookie = login_headers.get("Set-Cookie", "").split(";", 1)[0]
            logout_status, logout_headers, _ = persistent_post(
                logout_connection, "/api/logout", {}, local_origin, session_cookie
            )
            relogin_status, _, _ = persistent_post(
                logout_connection, "/api/login", {"username": USER, "password": PASSWORD}, local_origin
            )
            check("logout body cannot poison the next request",
                  login_status == 200 and logout_status == 200 and
                  logout_headers.get("Connection") == "close" and relogin_status == 200,
                  "login %s, logout %s, next %s" % (login_status, logout_status, relogin_status))
        finally:
            logout_connection.close()

        # The application is mounted behind /balance; routes outside that prefix are not exposed.
        status, _ = request("/api/discs", auth=True, raw_path=True)
        check("routes outside the configured mount prefix are 404", status == 404, "got %s" % status)

        # -- CSRF / DNS-rebinding protection --------------------------------------
        status, _ = request("/api/discs", headers={"Host": "evil.example"})
        check("foreign Host -> 403", status == 403, "got %s" % status)
        status, _ = request("/api/discs", headers={"Origin": "http://evil.example"})
        check("foreign Origin -> 403", status == 403, "got %s" % status)
        status, _ = request("/api/maintenance", "POST", {"mode": "off"},
                            headers={"Content-Type": "text/plain"})
        check("text/plain POST -> 415", status == 415, "got %s" % status)
        status, _ = request("/api/maintenance", "POST", {"mode": "off"},
                            headers={"Origin": "https://evil.example"})
        check("foreign HTTPS Origin -> 403", status == 403, "got %s" % status)
        status, _ = request("/api/discs")
        check("legit request -> 200", status == 200, "got %s" % status)
        status, _ = request("/api/discs", headers={
            "Host": "balance.test", "Origin": "https://balance.test"
        })
        check("configured HTTPS public origin is accepted", status == 200, "got %s" % status)
        status, _ = request("/api/discs", headers={
            "Host": "balance.test", "Origin": "https://127.0.0.1:%d" % PORT
        })
        check("Origin authority must match the request Host", status == 403, "got %s" % status)
        status, _ = request("/api/discs", headers={"Origin": "http://127.0.0.1:%d" % PORT})
        check("same-origin Origin -> 200", status == 200, "got %s" % status)

        # -- override read: no override yet -> base file --------------------------
        status, tables = request("/api/tables")
        by_name = {t["name"]: t for t in (tables or [])}
        check("GET /api/tables lists the base tables",
              status == 200 and "skill_condition" in by_name and by_name["skill_condition"]["source"] == "base")
        check("nothing is overridden at the start",
              all(not t["overridden"] for t in by_name.values()))
        base_bytes = base_file("skill_condition").read_bytes()
        status, rows = request("/api/table/skill_condition")
        check("read falls back to the base file",
              status == 200 and rows == json.loads(base_bytes.decode()), "status %s" % status)

        # -- write: goes to the override dir, base file untouched -----------------
        disc = next(d for d in discs if d.get("skillId"))
        skill_id = disc["skillId"]
        status, schema = request("/api/effect-schema")
        spec = schema["tables"]["skill_condition"]
        new_id = (max(r["id"] for r in rows) + 1) if rows else 1
        new_row = {"id": new_id, "skillId": skill_id}
        new_row.update(spec["defaults"])
        status, result = request("/api/table/skill_condition", "POST", {"rows": rows + [new_row]})
        check("POST writes the override", status == 200 and result and result.get("ok") is True,
              "status %s body %s" % (status, result))
        check("override file exists", override_file("skill_condition").is_file())
        check("base file was not modified", base_file("skill_condition").read_bytes() == base_bytes)
        on_disk = json.loads(override_file("skill_condition").read_text())
        check("new row is in the override file",
              any(r["id"] == new_id and r["skillId"] == skill_id for r in on_disk))
        check("backup of the previous state was written", any(BACKUPS.iterdir()))

        # -- override read: now the override wins --------------------------------
        status, tables = request("/api/tables")
        by_name = {t["name"]: t for t in (tables or [])}
        check("table is reported as overridden", by_name["skill_condition"]["overridden"] is True)
        status, rows2 = request("/api/table/skill_condition")
        check("read returns the override row", any(r["id"] == new_id for r in rows2))

        # -- pending detection against the container start time -------------------
        started_at("2000-01-01T00:00:00Z")   # older than the override -> pending
        status, apply_state = request("/api/apply")
        check("override newer than the API start is pending",
              status == 200 and "skill_condition" in (apply_state.get("pending") or []),
              "status %s %s" % (status, apply_state))
        started_at("2099-01-01T00:00:00Z")   # newer than the override -> not pending
        status, apply_state = request("/api/apply")
        check("override older than the API start is not pending",
              status == 200 and "skill_condition" not in (apply_state.get("pending") or []),
              "status %s %s" % (status, apply_state))

        # -- export zip -----------------------------------------------------------
        status, payload, headers = request("/api/overrides/export", raw=True)
        names = []
        if status == 200:
            with zipfile.ZipFile(io.BytesIO(payload)) as archive:
                names = archive.namelist()
        check("export returns a zip with the override file",
              status == 200 and "masters_skill_condition.json" in names,
              "status %s names %s" % (status, names))
        check("export has an attachment filename",
              "attachment" in headers.get("Content-Disposition", ""))

        # -- revert ---------------------------------------------------------------
        base_before_revert = base_file("skill_condition").read_bytes()
        status, result = request("/api/table/skill_condition/revert", "POST")
        check("revert succeeds", status == 200 and result and result.get("ok") is True,
              "status %s body %s" % (status, result))
        check("override file is gone", not override_file("skill_condition").exists())
        check("base file is still untouched after revert",
              base_file("skill_condition").read_bytes() == base_before_revert)
        status, rows3 = request("/api/table/skill_condition")
        check("read falls back to base after revert", rows3 == json.loads(base_before_revert.decode()))
        status, tables = request("/api/tables")
        by_name = {t["name"]: t for t in (tables or [])}
        check("table is no longer overridden", by_name["skill_condition"]["overridden"] is False)
        status, result = request("/api/table/skill_condition/revert", "POST")
        check("reverting a non-overridden table is 404", status == 404, "status %s" % status)

        # -- effect add/delete still works on the override flow -------------------
        status, table = request("/api/table/skill_condition")
        next_id = (max(r["id"] for r in table) + 1) if table else 1
        added = {"id": next_id, "skillId": skill_id}
        added.update(spec["defaults"])
        status, result = request("/api/table/skill_condition", "POST", {"rows": table + [added]})
        check("add an effect row through the override flow",
              status == 200 and result and result.get("ok") is True, "status %s" % status)
        status, table2 = request("/api/table/skill_condition")
        status, result = request("/api/table/skill_condition", "POST",
                                 {"rows": [r for r in table2 if r["id"] != next_id]})
        check("delete the effect row through the override flow",
              status == 200 and result and result.get("ok") is True, "status %s" % status)

        # -- validation -----------------------------------------------------------
        bad_type = dict(spec["defaults"])
        bad_type.update({"id": 999999, "skillId": skill_id})
        bad_type["conditionType"] = "not-a-number"
        status, result = request("/api/table/skill_condition", "POST", {"rows": table + [bad_type]})
        check("wrong type is rejected with 400", status == 400, "status %s" % status)
        status, core = request("/api/table/disc")
        status2, _ = request("/api/table/disc", "POST", {"rows": core + [dict(core[0], id=99999999)]})
        check("core table still refuses add/delete", status2 == 400, "status %s" % status2)

        # -- apply ----------------------------------------------------------------
        APPLIED_MARKER.unlink(missing_ok=True)
        status, applied = request("/api/apply", "POST")
        check("POST /api/apply runs the command and reports health",
              status == 200 and applied and applied.get("ok") is True and
              applied["health"]["ok"] is True, "status %s body %s" % (status, applied))
        check("apply command was executed", APPLIED_MARKER.is_file())

        # -- maintenance ----------------------------------------------------------
        status, state = request("/api/maintenance")
        check("GET /api/maintenance proxies the admin endpoint",
              status == 200 and state.get("mode") == "off", "status %s %s" % (status, state))
        status, state = request("/api/maintenance", "POST",
                                {"mode": "warning", "title": "T", "message": "M"})
        check("POST warning reaches the admin endpoint",
              status == 200 and state.get("mode") == "warning" and state.get("message") == "M",
              "status %s %s" % (status, state))
        status, state = request("/api/maintenance", "POST", {"mode": "hard"})
        check("POST hard reaches the admin endpoint",
              status == 200 and state.get("mode") == "hard", "status %s %s" % (status, state))
        status, _ = request("/api/maintenance", "POST", {"mode": "bogus"})
        check("invalid maintenance mode is rejected", status == 400, "status %s" % status)

        # -- Latest Information -------------------------------------------------
        status, news = request("/api/latest-information")
        check("GET /api/latest-information proxies the game API",
              status == 200 and news.get("content") == "Default news" and news.get("mode") == "text",
              "status %s %s" % (status, news))
        status, news = request("/api/latest-information", "POST", {"content": "## Update\n\n- New battle mode"})
        check("POST /api/latest-information saves announcement text",
              status == 200 and news.get("content") == "## Update\n\n- New battle mode" and news.get("mode") == "text",
              "status %s %s" % (status, news))
        html_page = "<!doctype html><html><style>body{color:navy}</style><body><h1>News</h1></body></html>"
        status, news = request("/api/latest-information", "POST", {"mode": "html", "htmlContent": html_page})
        check("POST /api/latest-information saves editable HTML mode",
              status == 200 and news.get("mode") == "html" and news.get("htmlContent") == html_page,
              "status %s %s" % (status, news))
        status, news = request("/api/latest-information", "POST",
                               {"mode": "url", "externalUrl": "https://news.example.test/announcements"})
        check("POST /api/latest-information saves HTTP(S) URL mode",
              status == 200 and news.get("mode") == "url" and
              news.get("externalUrl") == "https://news.example.test/announcements",
              "status %s %s" % (status, news))
        status, _ = request("/api/latest-information", "POST", {"mode": "text", "content": 123})
        check("invalid Latest Information body is rejected", status == 400, "status %s" % status)
        status, _ = request("/api/latest-information", "POST", {"mode": "text", "content": "x" * 12001})
        check("Latest Information text length is bounded", status == 400, "status %s" % status)
        status, _ = request("/api/latest-information", "POST", {"mode": "html", "htmlContent": "é" * 524289})
        check("Latest Information HTML UTF-8 byte size is bounded", status == 400, "status %s" % status)
        status, _ = request("/api/latest-information", "POST", {"mode": "url", "externalUrl": "javascript:alert(1)"})
        check("Latest Information rejects non-HTTP URL schemes", status == 400, "status %s" % status)
        status, _ = request("/api/latest-information", "POST", {"mode": "url", "externalUrl": "https://user:pass@example.com/"})
        check("Latest Information rejects credentialed URLs", status == 400, "status %s" % status)

        # -- Health tab (/admin/ops/* proxy) --------------------------------------
        status, _ = request("/api/ops/summary", auth=False)
        check("ops summary requires the dashboard session", status == 401, "got %s" % status)
        status, _ = request("/api/ops/summary", headers={"Host": "evil.example"})
        check("ops routes keep the DNS-rebinding Host check", status == 403, "got %s" % status)

        MockAdmin.ops_calls = []
        status, summary = request("/api/ops/summary?window=24h")
        check("GET /api/ops/summary proxies the API and forwards the window",
              status == 200 and summary.get("totals", {}).get("clean") == 3 and
              MockAdmin.ops_calls[-1] == ("/admin/ops/summary", "24h"),
              "status %s calls %s" % (status, MockAdmin.ops_calls))
        status, _ = request("/api/ops/summary")
        check("GET /api/ops/summary without a window lets the API default",
              status == 200 and MockAdmin.ops_calls[-1] == ("/admin/ops/summary", None),
              "status %s calls %s" % (status, MockAdmin.ops_calls))

        status, exceptions = request("/api/ops/exceptions?window=7d")
        check("GET /api/ops/exceptions proxies the fingerprint groups",
              status == 200 and exceptions.get("groups", [{}])[0].get("fingerprint") == "0123456789abcdef" and
              MockAdmin.ops_calls[-1] == ("/admin/ops/exceptions", "7d"),
              "status %s calls %s" % (status, MockAdmin.ops_calls))

        status, battle = request("/api/ops/battle/battle-123")
        check("GET /api/ops/battle/<id> proxies the drill-down",
              status == 200 and battle.get("battle", {}).get("battleId") == "battle-123" and
              MockAdmin.ops_calls[-1] == ("/admin/ops/battle/battle-123", None),
              "status %s calls %s" % (status, MockAdmin.ops_calls))
        status, _ = request("/api/ops/battle/")
        check("an empty battle id is rejected", status == 400, "got %s" % status)
        status, _ = request("/api/ops/battle/bad%2Fid")
        check("a battle id with a slash is rejected", status == 400, "got %s" % status)

        status, unknown = request("/api/ops/summary?window=this-release")
        check("an unknown window keeps the API's 400 body",
              status == 400 and "windows" in (unknown or {}), "status %s body %s" % (status, unknown))

        # -- logout ---------------------------------------------------------------
        status, _ = request("/api/logout", "POST", {})
        check("logout revokes the server-side session", status == 200)
        status, _ = request("/api/discs")
        check("revoked session cannot access protected routes", status == 401, "got %s" % status)

        # -- apply failure is reported, not swallowed -----------------------------
        check("failing apply command returns an error", _failing_apply_works(env, "exit 3"))
        check("apply that exits 0 without restarting is an error", _failing_apply_works(env, "true"))

        # The limiter is also exercised through the real HTTP endpoint (after all later
        # acceptance cases that need to authenticate from this test client's IP).
        rate_status = None
        saw_credential_failure = False
        for _ in range(6):
            rate_status, _ = request(
                "/api/login", "POST", {"username": "missing-user", "password": PASSWORD}, auth=False
            )
            saw_credential_failure = saw_credential_failure or rate_status == 401
            if rate_status == 429:
                break
        status, _, rate_headers = request(
            "/api/login", "POST", {"username": "missing-user", "password": PASSWORD}, auth=False, raw=True
        )
        check("login failures are rate-limited by source IP",
              saw_credential_failure and rate_status == 429 and status == 429 and "Retry-After" in rate_headers,
              "last credential status %s, blocked status %s" % (rate_status, status))
    finally:
        webui.terminate()
        try:
            webui.wait(timeout=5)
        except subprocess.TimeoutExpired:
            webui.kill()
        admin.shutdown()

    print()
    if FAILURES:
        print("FAILED (%d): %s" % (len(FAILURES), ", ".join(FAILURES)))
        return 1
    print("ALL TESTS PASSED")
    return 0


def wait_for_port(webui):
    deadline = time.time() + 15
    while time.time() < deadline:
        if webui.poll() is not None:
            break
        try:
            request("/login", auth=False)
            print("WebUI is up on port %d" % PORT)
            return
        except Exception:
            time.sleep(0.2)
    output = webui.stdout.read().decode("utf-8", "replace") if webui.stdout else ""
    print("WebUI did not start:\n" + output)
    raise SystemExit(1)


def _failing_apply_works(base_env, command):
    env = dict(base_env)
    env["KF_BALANCE_APPLY_CMD"] = command
    proc = subprocess.Popen(
        [sys.executable, str(REPO / "tools/balance/server.py"), "--host", "127.0.0.1",
         "--port", str(PORT + 1)],
        env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
    )
    try:
        for _ in range(80):
            try:
                req = urllib.request.Request("http://127.0.0.1:%d%s/api/login" %
                                             (PORT + 1, BASE_PATH), data=json.dumps(
                                                 {"username": USER, "password": PASSWORD}
                                             ).encode(), method="POST")
                req.add_header("Content-Type", "application/json")
                req.add_header("Origin", "http://127.0.0.1:%d" % (PORT + 1))
                with urllib.request.urlopen(req, timeout=10) as response:
                    cookie = response.headers["Set-Cookie"].split(";", 1)[0]
                req = urllib.request.Request("http://127.0.0.1:%d%s/api/apply" %
                                             (PORT + 1, BASE_PATH), data=b"{}", method="POST")
                req.add_header("Content-Type", "application/json")
                req.add_header("Origin", "http://127.0.0.1:%d" % (PORT + 1))
                req.add_header("Cookie", cookie)
                with urllib.request.urlopen(req, timeout=10) as response:
                    return False
            except urllib.error.HTTPError as error:
                if error.code == 502:
                    return True
                return False
            except urllib.error.URLError:
                time.sleep(0.1)
        return False
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()


def __session_is_hashed(database, token):
    import base64
    import hashlib
    import json
    import sqlite3
    claims = json.loads(base64.urlsafe_b64decode(token.split(".")[1] + "=="))
    with sqlite3.connect(database) as connection:
        row = connection.execute("SELECT token_hash FROM sessions").fetchone()
    return bool(row and row[0] == hashlib.sha256(claims["jti"].encode("ascii")).hexdigest()
                and row[0] != claims["jti"])


if __name__ == "__main__":
    sys.exit(main())
