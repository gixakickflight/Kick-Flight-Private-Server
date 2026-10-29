#!/usr/bin/env python3
"""Walk a brand-new account through onboarding and the disc level-up against a running adapter, asserting each
response carries the fields the client's DTO declares (dump.cs, Colorful.Networking.*ResponseData).

Usage:
  probe_onboarding.py <base-url> <host-header> <client-version>
                      [--internal <economy internal url> --key-file <gameserver key file>]

Without --internal it walks onboarding only. With it, it also funds a second throwaway account on the internal
plane (class grants under keys of its own) so a paid /disc/buildup can be exercised, and prints every grant it made.
Stdlib only; AES via the openssl binary. Exit status 1 when any assertion fails.
"""

import argparse
import base64
import json
import os
import secrets
import subprocess
import sys
import urllib.error
import urllib.request
import uuid

COMMON = b"1a837b9ee2ae11a07a0f529a4cd4b61c"
FAILURES = []

# The response DTOs (dump.cs), as the client deserializes them.
DTO = {
    "/startup/index": ["userKickerList", "userDiscList", "userItemList", "userMissionProgressList",
                       "tutorialProgressStatus"],
    "/tutorial/start": ["userKickerList"],
    "/tutorial/capsuleOpen": ["dropDiscList", "receivedUserDiscList", "receivedUserItemList", "userPresentList",
                              "receivedRewardList"],
    "/tutorial/discBuildup": ["userDisc", "userItem"],
    "/tutorial/gachaDiscDraw": ["dropDiscList", "receivedUserDiscList", "receivedUserItemList", "userItemList",
                                "modelId", "capsuleEffectType", "userPresentList", "receivedRewardList"],
    "/tutorial/gachaKickerDraw": ["receivedUserKickerList", "userItemList", "userPresentList", "receivedRewardList"],
    "/disc/buildup": ["userDisc", "userItem", "userMissionProgressList"],
}
USER_DISC = ["discId", "amount", "level", "registerDatetime"]


def aes(decrypt, key, iv, data):
    cmd = ["openssl", "enc", "-aes-256-cbc", "-K", key.hex(), "-iv", iv.hex()]
    if decrypt:
        cmd.append("-d")
    return subprocess.run(cmd, input=data, capture_output=True, check=False).stdout


class Session:
    def __init__(self, base, host, version):
        self.base, self.host, self.version = base, host, version
        self.hash = secrets.token_hex(16)
        self.key = self.hash.encode()
        self.token = None
        self.user = None

    def post(self, path, body=None, key=None, cache_id=None):
        key = key or self.key
        iv = os.urandom(16)
        data = iv + aes(False, key, iv, json.dumps(body or {}).encode())
        headers = {"Content-Type": "application/octet-stream", "Host": self.host,
                   "x-app-application-version": self.version}
        if self.token:
            headers["x-app-access-token"] = self.token
        if cache_id:
            headers["x-app-response-cache-id"] = cache_id
        request = urllib.request.Request(self.base + path, data=data, headers=headers, method="POST")
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                status, hdrs, raw = response.status, dict(response.headers), response.read()
        except urllib.error.HTTPError as error:
            status, hdrs, raw = error.code, dict(error.headers), error.read()
        code = hdrs.get("x-app-status-code")
        try:
            parsed = json.loads(raw)  # a plaintext error body
        except ValueError:
            text = aes(True, key, raw[:16], raw[16:]) if len(raw) >= 32 else b""
            parsed = json.loads(text) if text else None
        return status, code, hdrs, parsed

    def auth(self):
        status, code, hdrs, _ = self.post("/auth/index", {"hash": self.hash, "uuid": str(uuid.uuid4())}, COMMON)
        self.token, self.user = hdrs.get("x-app-access-token"), hdrs.get("x-app-user-id")
        check(status == 200 and code == "0" and self.token, f"auth/index http={status} app={code}")
        return self


def check(condition, label):
    print(("  ok   " if condition else "  FAIL ") + label)
    if not condition:
        FAILURES.append(label)
    return condition


def step(session, path, body=None, expect="0", cache_id=None):
    status, code, _, payload = session.post(path, body, cache_id=cache_id)
    print(f"{path:<28} http={status} app={code} {json.dumps(payload)[:300]}")
    check(status == 200 and code == expect, f"{path} answers http 200 / status {expect}")
    if expect == "0" and isinstance(payload, dict):
        for member in DTO.get(path, []):
            check(payload.get(member) is not None, f"{path} carries {member}")
        for disc in [payload.get("userDisc")] + (payload.get("receivedUserDiscList") or []):
            if isinstance(disc, dict):
                check(all(disc.get(m) is not None for m in USER_DISC), f"{path} disc {disc.get('discId')} has {USER_DISC}")
    return payload


def item(payload, item_id):
    return next((row["amount"] for row in payload.get("userItemList", []) if row["itemId"] == item_id), None)


def disc(payload, disc_id):
    return next((row for row in payload.get("userDiscList", []) if row["discId"] == disc_id), None)


def internal_grant(url, key_file, player, grant_class, key):
    service_key = open(key_file, encoding="utf-8").read().strip()
    body = json.dumps({"player_id": player, "key": key, "reason": "onboarding probe", "ref": "probe",
                       "class": grant_class}).encode()
    request = urllib.request.Request(url.rstrip("/") + "/internal/economy/grants", data=body, method="POST",
                                     headers={"Content-Type": "application/json",
                                              "Authorization": "Bearer " + service_key})
    with urllib.request.urlopen(request, timeout=30) as response:
        answer = json.loads(response.read())
    print(f"  grant {grant_class} key={key} -> replayed={answer.get('replayed')}")
    return answer


def otomo_player(session):
    # The adapter token is kf1.<base64url JSON payload>.<signature>; the payload names the otomo account ("o").
    part = session.token.split(".")[1]
    payload = json.loads(base64.urlsafe_b64decode(part + "=" * (-len(part) % 4)))
    return payload["o"]


def onboarding(args):
    print("== fresh account: onboarding")
    s = Session(args.base, args.host, args.version).auth()
    startup = step(s, "/startup/index")
    status = startup["tutorialProgressStatus"]
    print(f"  tutorialProgressStatus={status} disc_force(row 3)={item(startup, 3)}")
    check((item(startup, 3) or 0) > 0, "a fresh account holds starter Disc Force (userItemList row 3)")
    again = step(s, "/startup/index")
    check(item(again, 3) == item(startup, 3), "a second startup pays the starter nothing more")

    # Every ladder step, whatever the status served: the shapes must hold even for a client that resumes mid-way.
    start = step(s, "/tutorial/start")
    kickers = start.get("userKickerList", [])
    check(any(k["kickerId"] != 1 for k in kickers), "tutorial/start serves a kicker other than kicker 1")
    check(all(k.get("userKickerCostumeList") for k in kickers), "every kicker in tutorial/start has a costume")
    for progress in range(101, 113):
        step(s, "/tutorial/battleProgress", {"tutorialProgressStatus": progress})
    step(s, "/tutorial/battleEnd")
    capsule = step(s, "/tutorial/capsuleOpen")
    check(bool(capsule.get("dropDiscList")), "tutorial/capsuleOpen drops a disc")
    lesson = step(s, "/tutorial/discBuildup", {"discId": 3010003})
    check(lesson["userDisc"]["level"] == 3 and lesson["userDisc"]["amount"] >= 1,
          "the lesson answers the placeholder disc one level up (3) with its amount")
    check(lesson["userItem"]["itemId"] == 3 and lesson["userItem"]["amount"] == item(startup, 3),
          "the lesson spends no Disc Force")
    step(s, "/tutorial/gachaDiscDraw")
    kicker_draw = step(s, "/tutorial/gachaKickerDraw")
    check(bool(kicker_draw.get("receivedUserKickerList")), "tutorial/gachaKickerDraw shows a kicker")
    step(s, "/tutorial/kickerChange", {"kickerId": 2, "kickerCostumeId": 2020101})
    name = "ob" + secrets.token_hex(3)
    step(s, "/tutorial/end", {"name": name})
    named = step(s, "/startup/index")
    check(named["tutorialProgressStatus"] == 207, "the name ends onboarding (207)")
    home = step(s, "/home/index")
    check(home.get("userPlayer", {}).get("name") == name, "home/index shows the chosen name")
    for d in named.get("userDiscList", []):
        check(all(d.get(m) is not None for m in USER_DISC), f"startup disc {d['discId']} has {USER_DISC}")
    return s, status


def buildup(args):
    print("== funded throwaway account: /disc/buildup")
    s = Session(args.base, args.host, args.version).auth()
    step(s, "/startup/index")
    player = otomo_player(s)
    tag = secrets.token_hex(4)
    # Boarush (3010013, rarity 2) starts at level 3: 3 -> 4 is 1880 Disc Force and no duplicate.
    internal_grant(args.internal, args.key_file, player, "tutorial.disc", f"probe:{tag}:disc")
    for n in range(6):
        internal_grant(args.internal, args.key_file, player, "starter.disc_force", f"probe:{tag}:force:{n}")
    before = step(s, "/startup/index")
    force_before, row_before = item(before, 3), disc(before, 3010013)
    print(f"  before: disc_force={force_before} disc={row_before}")

    raised = step(s, "/disc/buildup", {"discId": 3010013}, cache_id=f"probe-{tag}-1")
    check(raised["userDisc"]["level"] == row_before["level"] + 1, "the level goes up by one")
    check(raised["userItem"]["amount"] == force_before - 1880, "Disc Force drops by the master's price (1880)")
    retried = step(s, "/disc/buildup", {"discId": 3010013}, cache_id=f"probe-{tag}-1")
    check(retried["userDisc"]["level"] == raised["userDisc"]["level"] and
          retried["userItem"]["amount"] == raised["userItem"]["amount"], "a retried request spends nothing more")
    # 4 -> 5 costs 3300 and three duplicates the account does not have: refused, nothing moves.
    step(s, "/disc/buildup", {"discId": 3010013}, expect="2502", cache_id=f"probe-{tag}-2")
    after = step(s, "/startup/index")
    print(f"  after: disc_force={item(after, 3)} disc={disc(after, 3010013)}")
    check(item(after, 3) == force_before - 1880, "startup row 3 shows the spent Disc Force")
    check(disc(after, 3010013)["level"] == row_before["level"] + 1, "startup shows the new level")

    print("== previously working flows")
    step(s, "/home/index")
    entry = step(s, "/battle/entry", {"battleRuleId": 1})
    if isinstance(entry, dict) and entry.get("battleEntryId"):
        step(s, "/battle/result", {"battleEntryId": entry["battleEntryId"], "killCount": 1, "mvpFlag": False})
    status, code, _, draw = s.post("/gacha/kickerDraw", {"gachaId": 1, "count": 1})
    print(f"/gacha/kickerDraw            http={status} app={code} {json.dumps(draw)[:300]}")
    check(status == 200 and code is not None, "gacha/kickerDraw answers in the game's contract")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("base")
    parser.add_argument("host")
    parser.add_argument("version")
    parser.add_argument("--internal")
    parser.add_argument("--key-file")
    args = parser.parse_args()
    onboarding(args)
    if args.internal and args.key_file:
        buildup(args)
    print(f"== {len(FAILURES)} failure(s)")
    for failure in FAILURES:
        print("   " + failure)
    sys.exit(1 if FAILURES else 0)


if __name__ == "__main__":
    main()
