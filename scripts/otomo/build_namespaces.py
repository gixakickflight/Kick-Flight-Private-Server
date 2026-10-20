#!/usr/bin/env python3
"""Build KF's otomo Config namespaces from the masters.

For each namespace this writes, under otomo/namespaces/<name>/:

- schema.json: the JSON Schema Config validates against. otomo's admin website
  draws its editor from it (an array of records becomes a table);
- presentation.json: this namespace's entry in otomo's `ui.presentation`
  namespace (table columns, frozen and searchable columns, row policy);
- document.json: the current masters, in the namespace's shape.

Enum labels come from tools/balance/enums.json, so the Balance WebUI and otomo
show the same names. Only kf.kickers exists so far.

    python scripts/otomo/build_namespaces.py
    python scripts/otomo/build_namespaces.py --check   # fail if the files are stale
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
CONFIG = REPO / "config"
ENUMS = json.loads((REPO / "tools" / "balance" / "enums.json").read_text(encoding="utf-8"))["enums"]
OUT = REPO / "otomo" / "namespaces"


def enum(name: str, title: str, **extra) -> dict:
    values = ENUMS[name]["values"]
    keys = sorted(values, key=int)
    return {
        "type": "integer",
        "title": title,
        "enum": [int(k) for k in keys],
        "enumNames": [f"{k} - {values[k]}" for k in keys],
        **extra,
    }


def integer(title: str, **extra) -> dict:
    return {"type": "integer", "title": title, **extra}


def number(title: str, **extra) -> dict:
    return {"type": "number", "title": title, **extra}


def string(title: str, **extra) -> dict:
    return {"type": "string", "title": title, **extra}


def read_only(field: dict) -> dict:
    return {**field, "readOnly": True}


def table(title: str, required: list[str], properties: dict, key: str = "id") -> dict:
    return {
        "type": "array",
        "title": title,
        "x-otomo-key": key,
        "items": {
            "type": "object",
            "additionalProperties": False,
            "required": required,
            "properties": properties,
        },
    }


def masters(name: str) -> list[dict]:
    return json.loads((CONFIG / f"masters_{name}.json").read_text(encoding="utf-8"))


# ---------------------------------------------------------------------------
# kf.kickers: what the Balance WebUI's kicker panel shows, as four tables.
# Link columns (ids, kickerId) and the columns the Balance WebUI locks (role,
# weapon, KS skill) are read-only here too.

def kickers() -> tuple[dict, dict, dict]:
    multiplier = lambda title: number(title, minimum=0)
    schema = {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "title": "Kickers",
        "description": "The playable kickers: identity, base stats and passives. Mirrors masters_kicker, "
        "masters_kicker_parameter, masters_kicker_ability and masters_kicker_ability_condition.",
        "type": "object",
        "additionalProperties": False,
        "required": ["kickers", "parameters", "abilities", "abilityConditions"],
        "properties": {
            "kickers": table("Kickers", ["id", "name"], {
                "id": read_only(integer("ID", minimum=1)),
                "name": string("Name", minLength=1),
                "shortName": string("Short name"),
                "nameSpelling": string("Name spelling"),
                "voiceActorName": string("Voice actor line", description="Shown as written on the profile screen."),
                "cv": string("CV"),
            }),
            "parameters": table("Stats", ["id", "kickerId", "roleType", "weaponType", "maxHp", "attack"], {
                "id": read_only(integer("ID", minimum=1)),
                "kickerId": read_only(integer("Kicker", minimum=1, description="The kicker this stats row belongs to.")),
                "roleType": read_only(enum("RoleType", "Role")),
                "weaponType": read_only(enum("WeaponType", "Weapon")),
                "skillId": read_only(integer("Kicker skill", description="Skill master row of the kicker skill (KS).")),
                "maxHp": integer("Max HP", minimum=1),
                "attack": integer("Attack", minimum=0),
                "defense": integer("Defense", minimum=0),
                "speed": number("Speed", minimum=0),
                "moveSpeedCoefficient": multiplier("Move speed ×"),
                "moveTurningSpeedCoefficient": multiplier("Turn speed ×"),
                "moveDashSpeedCoefficient": multiplier("Dash speed ×"),
                "groundMoveSpeedCoefficient": multiplier("Ground speed ×"),
                "groundMoveTurningSpeedCoefficient": multiplier("Ground turn speed ×"),
                "acceleration": multiplier("Acceleration"),
                "attackTargetSearchDistance": number("Target search distance", minimum=0),
                "attackTargetSearchAngle": number("Target search angle", minimum=0, maximum=360),
                "dashAttackRange": number("Dash attack range", minimum=0),
                "dashAttackSpeedWeight": number("Dash attack speed weight", minimum=0),
                "dashAttackTime": number("Dash attack time", minimum=0),
                "dashAttackFollowThroughTime": number("Dash follow-through time", minimum=0),
                "footHeight": number("Foot height", minimum=0),
                "height": number("Height", minimum=0),
                "recoveryBoostPoint": number("Boost recovery (air)", minimum=0),
                "recoveryBoostPointGround": number("Boost recovery (ground)", minimum=0),
                "addMoveSpecialSkillPoint": number(
                    "SP per distance flown", minimum=0,
                    description="Special-skill points gained per unit of distance flown. The gauge holds 100, "
                    "so 100 divided by this is the distance to a full gauge."),
                "addWeaponAttackSpecialSkillPoint": number(
                    "SP per weapon hit", minimum=0, description="Special-skill points gained per basic-attack hit."),
                "hpCorrection": multiplier("HP correction"),
                "attackCorrection": multiplier("Attack correction"),
            }),
            "abilities": table("Passives", ["id", "kickerId", "triggerType"], {
                "id": read_only(integer("ID", minimum=1)),
                "kickerId": read_only(integer("Kicker", minimum=1)),
                "triggerType": enum("KickerAbilityTrigger", "Trigger"),
                "time": number("Time", minimum=0),
                "value": number("Value", description="The passive's amount. For a trap passive it is the trap skill's id."),
                "overlapCount": integer("Stack limit", minimum=1),
            }),
            "abilityConditions": table("Passive conditions", ["id", "kickerAbilityId", "conditionType", "triggerType"], {
                "id": read_only(integer("ID", minimum=1)),
                "kickerAbilityId": read_only(integer("Passive", minimum=1, description="The passive row this condition belongs to.")),
                "conditionType": enum("ConditionType", "Condition"),
                "duration": number("Duration", minimum=0),
                "interval": number("Interval", minimum=0),
                "effectValue": number("Effect value"),
                "triggerType": enum("ConditionTriggerType", "Trigger"),
                "limitCount": integer("Limit", minimum=0),
            }),
        },
    }

    # Kickers and their stats rows need client assets, so rows are fixed; passives can be added.
    fixed = {"add": False, "delete": False}
    presentation = {
        "title": "Kickers",
        "order": ["kickers", "parameters", "abilities", "abilityConditions"],
        "tables": {
            "/kickers": {"label": "name", "frozen": ["id", "name"], "search": ["id", "name", "shortName"], "rows": fixed},
            "/parameters": {"frozen": ["id", "kickerId"], "search": ["id", "kickerId"], "rows": fixed},
            "/abilities": {"frozen": ["id", "kickerId"], "search": ["id", "kickerId"], "rows": {"add": True, "delete": True}},
            "/abilityConditions": {
                "frozen": ["id", "kickerAbilityId"],
                "search": ["id", "kickerAbilityId"],
                "rows": {"add": True, "delete": True},
            },
        },
    }

    document = {
        "kickers": masters("kicker"),
        "parameters": masters("kicker_parameter"),
        "abilities": masters("kicker_ability"),
        "abilityConditions": masters("kicker_ability_condition"),
    }
    return schema, presentation, document


NAMESPACES = {"kf.kickers": kickers}


def render(data: dict) -> str:
    return json.dumps(data, ensure_ascii=False, indent=2) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check", action="store_true", help="fail if a written file would change")
    args = parser.parse_args()

    stale = []
    for name, build in NAMESPACES.items():
        schema, presentation, document = build()
        directory = OUT / name
        directory.mkdir(parents=True, exist_ok=True)
        for filename, data in (("schema.json", schema), ("presentation.json", presentation), ("document.json", document)):
            path = directory / filename
            text = render(data)
            if path.exists() and path.read_text(encoding="utf-8") == text:
                continue
            if args.check:
                stale.append(path.relative_to(REPO).as_posix())
            else:
                path.write_text(text, encoding="utf-8", newline="\n")
                print(f"wrote {path.relative_to(REPO).as_posix()}")
    if stale:
        print("stale, rerun scripts/otomo/build_namespaces.py: " + ", ".join(stale), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
