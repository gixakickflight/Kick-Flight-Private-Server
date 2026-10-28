#!/usr/bin/env python3
"""Generate Kick-Flight's economy documents from the game's own masters.

The four files this writes are the namespaces otomo's economy service reads:

    economy.rules   currencies, the item catalogue, reward classes, faucet ceilings
    economy.shop    offers: what costs what, what it grants, per-player limits, windows
    economy.gacha   pools: cost, weighted entries, pity, how a duplicate is handled
    economy.tasks   tracks: what counts progress, the term it resets on, the steps that pay

They are *data*, not code, and they are published to otomo with the ordinary config
draft -> release flow. An operator tunes a payout, a price or a disc set by editing one of
these in the admin UI's table editor and publishing a release; nothing here needs a deploy.

Why generate rather than hand-write: the item catalogue is 131 discs and 118 kicker costumes
taken from the game's masters, and a hand-written copy drifts the first time a disc ships.

Usage:
    python scripts/otomo/build_economy_data.py [--check]

--check reports whether the committed files are stale, like build_namespaces.py does, so CI
can fail on a master change that was not regenerated.
"""

from __future__ import annotations

import argparse
import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
MASTERS = ROOT / "config"
OUT = ROOT / "otomo" / "namespaces" / "economy"

# ---------------------------------------------------------------------------
# The numbers. Everything in this block is a placeholder shaped by the economy design's own
# pacing formulas, and it is the part an operator is expected to tune.
# ---------------------------------------------------------------------------

JETCOIN_CASUAL = 120        # per casual match
JETCOIN_RANKED = 200        # per ranked match
JETCOIN_RANKUP = {"c": 300, "b": 500, "a": 800, "s": 1200}
TICKETS_RANKUP_S = 5        # the kicker gacha opens at S, so S pays a taste of it
TICKET_PRICE = 800          # jetcoin for one kicker ticket: the main jetcoin sink
SHARD_PER_DUPLICATE = 15
SHARD_PRICE = 60            # a guaranteed skin, for shards a duplicate paid
EVENT_COIN_PER_GRANT = 200
EVENT_SKIN_PRICE = 300
PITY_AFTER = 80
POOL_COST = 1               # kicker tickets per pull

# A daily/weekly/lifetime mission ladder. Targets are counts of the counter, and the class is
# what a rung pays; the service never sees the word "mission".
DAILY_RESET_HOUR = 4        # UTC, which is 12:00 in the operator's own timezone

# ---------------------------------------------------------------------------
# Tiers. Every disc in the master is reachable, which is the design's "casual ranks unlock the
# existing roster": rarity 0 -> C, 1 -> B, 2 -> A, 3 -> S.
# ---------------------------------------------------------------------------

TIER_BY_RARITY = {0: "c", 1: "b", 2: "a", 3: "s"}


def read(name: str):
    with open(MASTERS / name, encoding="utf-8") as handle:
        return json.load(handle)


def build():
    discs = read("masters_disc.json")
    costumes = read("masters_kicker_costume.json")

    disc_items = [
        {"id": f"disc:{d['id']}", "stackable": False}
        for d in sorted(discs, key=lambda d: d["id"])
    ]
    skin_items = [
        {"id": f"skin:{c['id']}", "stackable": False}
        for c in sorted(costumes, key=lambda c: c["id"])
    ]
    items = disc_items + skin_items + [{"id": "fragment", "stackable": True, "max_stack": 9999}]

    # --- classes -----------------------------------------------------------
    classes: dict[str, list[dict]] = {
        "match.casual": [{"currency": "jetcoin", "amount": JETCOIN_CASUAL}],
        "match.ranked": [{"currency": "jetcoin", "amount": JETCOIN_RANKED}],
        "mission.daily.small": [{"currency": "jetcoin", "amount": 100}],
        "mission.daily.large": [{"currency": "jetcoin", "amount": 300}],
        "mission.weekly": [
            {"currency": "jetcoin", "amount": 900},
            {"currency": "kicker_ticket", "amount": 1},
        ],
        "mission.lifetime": [{"currency": "jetcoin", "amount": 500}],
        "season.end": [{"currency": "jetcoin", "amount": 1500}],
        "event.grant": [{"currency": "event_coin", "amount": EVENT_COIN_PER_GRANT}],
    }

    by_tier: dict[str, list[int]] = {"c": [], "b": [], "a": [], "s": []}
    for d in sorted(discs, key=lambda d: d["id"]):
        by_tier[TIER_BY_RARITY.get(d["rarityType"], "s")].append(d["id"])

    for tier in ("c", "b", "a", "s"):
        grants = [{"currency": "jetcoin", "amount": JETCOIN_RANKUP[tier]}]
        grants += [{"item": f"disc:{disc_id}", "amount": 1} for disc_id in by_tier[tier]]
        if tier == "s":
            grants.append({"currency": "kicker_ticket", "amount": TICKETS_RANKUP_S})
        classes[f"rankup.{tier}"] = grants

    rules = {
        "currencies": ["jetcoin", "kicker_ticket", "shard", "event_coin"],
        "items": items,
        "classes": classes,
        "ceilings": [
            # A per-match faucet is bounded per match by the class; these bound a day.
            {"faucet": "match.casual", "currency": "jetcoin", "amount": 3000, "window": "24h"},
            {"faucet": "match.ranked", "currency": "jetcoin", "amount": 5000, "window": "24h"},
            # The platform's own backstop: nothing pays more than this per player per day,
            # whatever a release says. It is what makes a compromised document a bounded
            # incident rather than an economy that is over.
            {"faucet": "*", "currency": "jetcoin", "amount": 20000, "window": "24h"},
            {"faucet": "*", "currency": "kicker_ticket", "amount": 20, "window": "24h"},
            {"faucet": "*", "currency": "event_coin", "amount": 2000, "window": "24h"},
        ],
    }

    # --- shop --------------------------------------------------------------
    forever = {"count": 1, "window": "876000h"}  # one per player, ever
    offers = [
        # The jetcoin sink: jetcoin buys kicker tickets, and tickets buy cosmetic pulls.
        {
            "id": "ticket.single",
            "cost": [{"currency": "jetcoin", "amount": TICKET_PRICE}],
            "grants": [{"currency": "kicker_ticket", "amount": 1}],
            "limit": {"count": 0},
            "window": {},
        }
    ]
    for c in sorted(costumes, key=lambda c: c["id"]):
        # Shards buy any missing item, one per player: the design's duplicate-conversion rule.
        offers.append({
            "id": f"shard.skin.{c['id']}",
            "cost": [{"currency": "shard", "amount": SHARD_PRICE}],
            "grants": [{"item": f"skin:{c['id']}", "amount": 1}],
            "limit": forever,
            "window": {},
        })

    shop = {"offers": offers}

    # --- gacha -------------------------------------------------------------
    entries = []
    for c in sorted(costumes, key=lambda c: c["id"]):
        # costumeId 1 is each kicker's default colour; the alternates are the chase.
        if c["costumeId"] == 1:
            entries.append({"item": f"skin:{c['id']}", "weight": 20, "rarity": "standard"})
        else:
            entries.append({"item": f"skin:{c['id']}", "weight": 2, "rarity": "variant"})

    gacha = {
        "pools": [{
            "id": "cosmetic.standard",
            "cost": [{"currency": "kicker_ticket", "amount": POOL_COST}],
            "entries": entries,
            "pity": {"after": PITY_AFTER, "rarity": "variant"},
            "duplicates": {"policy": "currency", "currency": "shard", "amount": SHARD_PER_DUPLICATE},
            "window": {},
        }]
    }

    # --- tasks -------------------------------------------------------------
    daily = {"kind": "daily", "reset_hour": DAILY_RESET_HOUR}
    weekly = {"kind": "weekly", "reset_hour": DAILY_RESET_HOUR}
    lifetime = {"kind": "lifetime"}
    tracks = [
        {
            "id": "mission.daily.play", "counter": "battle.play", "term": daily,
            "steps": [
                {"target": 3, "class": "mission.daily.small"},
                {"target": 6, "class": "mission.daily.large"},
            ],
        },
        {
            "id": "mission.daily.win", "counter": "battle.win", "term": daily,
            "steps": [
                {"target": 1, "class": "mission.daily.small"},
                {"target": 3, "class": "mission.daily.large"},
            ],
        },
        {
            "id": "mission.weekly.play", "counter": "battle.play", "term": weekly,
            "steps": [{"target": 20, "class": "mission.weekly"}],
        },
        {
            # The lifetime chain: reaching the last rung restarts the ladder, and each pass
            # costs the final target again, so it is earned rather than farmed.
            "id": "mission.lifetime.play", "counter": "battle.play", "term": lifetime,
            "steps": [
                {"target": 50, "class": "mission.lifetime"},
                {"target": 200, "class": "mission.lifetime"},
            ],
            "repeat": True,
        },
    ]
    tasks = {"tracks": tracks}

    return {
        "economy.rules": rules,
        "economy.shop": shop,
        "economy.gacha": gacha,
        "economy.tasks": tasks,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true",
                        help="report stale files instead of writing them")
    args = parser.parse_args()

    docs = build()
    stale = []
    OUT.mkdir(parents=True, exist_ok=True)
    for name, body in docs.items():
        path = OUT / f"{name}.json"
        text = json.dumps(body, indent=2, sort_keys=False) + "\n"
        if path.exists() and path.read_text(encoding="utf-8") == text:
            continue
        stale.append(path)
        if not args.check:
            path.write_text(text, encoding="utf-8")

    if args.check:
        for path in stale:
            print(f"stale: {path.relative_to(ROOT)}", file=sys.stderr)
        return 1 if stale else 0

    for path in stale:
        print(f"wrote {path.relative_to(ROOT)}")
    totals = ", ".join(f"{name}: {len(json.dumps(body))} bytes" for name, body in docs.items())
    print(f"economy data: {totals}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
