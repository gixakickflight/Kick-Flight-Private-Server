#!/usr/bin/env python3
"""Regenerate kf.loadout's schema.json and draft.json from the game's masters.

    python3 otomo/namespaces/kf.loadout/gen_schema.py            # rewrite both files
    python3 otomo/namespaces/kf.loadout/gen_schema.py --check    # exit 1 if they are stale
    python3 otomo/namespaces/kf.loadout/gen_schema.py --rules    # print the session.loadout document

Run from the repository root. The masters it reads are the same files the game server and the
client are built from, so the schema's id enums cannot drift from the items the game can
actually show a player:

    config/masters_disc.json              the disc ids a deck slot may name
    config/masters_gear.json              the gear ids a gear slot may name
    config/masters_kicker_costume.json    the costume ids a gear set may be keyed by
    config/masters_initial_disc_deck.json the deck numbers and how many discs a deck holds

The script hand-writes no ids: every list it emits is read from a master, sorted, and de-duped.
That is deliberate - see otomo/LOADOUT.md for why the enums matter and what a master change
costs.
"""

import json
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[2]  # <repo>/otomo/namespaces/kf.loadout -> <repo>
CONFIG = ROOT / "config"

# The gear id that means "this slot is empty". The adapter already writes 0 for an empty slot
# (SessionState.CostumeGears: "0 = empty"), and the client reads the slot back as gearId1..3,
# so the sentinel is the game's own, not one this schema invents.
EMPTY_GEAR = 0


def load(name):
    # The masters are UTF-8 with a BOM and, in places, doubled carriage returns; both are
    # harmless to a JSON parser once the BOM is gone.
    with open(CONFIG / name, encoding="utf-8-sig") as f:
        return json.load(f)


def ids(rows, key="id"):
    return sorted({row[key] for row in rows})


def build():
    discs = ids(load("masters_disc.json"))
    gears = ids(load("masters_gear.json"))
    costumes = ids(load("masters_kicker_costume.json"))
    starter = load("masters_initial_disc_deck.json")

    deck_numbers = sorted(row["number"] for row in starter)
    disc_count = len([k for k in starter[0] if k.startswith("discId") and k[len("discId"):].isdigit()])
    if not deck_numbers or disc_count == 0:
        sys.exit("masters_initial_disc_deck.json defines no deck numbers or no discs")

    deck_ref = "#/$defs/deck"

    schema = {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "title": "Kick-Flight loadout, schema version 1",
        "description": (
            "One player's loadout as otomo's session service stores it: the three disc decks, which of "
            "them is active, the gear on each kicker costume, and a gear roll not yet equipped. The ids "
            "are enumerated from the game's masters, so a deck slot can only name a disc the game ships and "
            "a gear slot only a gear it ships; regenerate with gen_schema.py when the masters change. "
            "Session adds \"revision\" to every document it stores and strip it before validating, so it is "
            "declared here for completeness rather than because a save must carry it."
        ),
        "type": "object",
        "additionalProperties": False,
        "required": ["schema_version", "decks", "active_deck", "costume_gears"],
        "properties": {
            "schema_version": {
                "const": 1,
                "description": "Which schema this document follows. Session picks the schema to check a "
                               "document by this member and refuses a version it has no rules for.",
            },
            "revision": {
                "type": "integer",
                "minimum": 0,
                "description": "How many times this loadout has been saved; 0 or absent means never. "
                               "Session owns this member: it writes the stored value into every document it "
                               "returns and ignores whatever a save carries, because a save names the "
                               "revision it read in If-Match.",
            },
            "decks": {
                "type": "object",
                "description": "Exactly the deck numbers the master defines, each holding one deck of "
                               f"{disc_count} discs in slot order. Keyed by deck number because that is how "
                               "the adapter's save already holds them (Dictionary<int, List<int>>).",
                "additionalProperties": False,
                "required": [str(n) for n in deck_numbers],
                "properties": {str(n): {"$ref": deck_ref} for n in deck_numbers},
            },
            "active_deck": {
                "description": "The deck a match launches with. Required rather than defaulted: which deck "
                               "plays is the player's choice, so a document that does not say must not be "
                               "guessed at.",
                "enum": deck_numbers,
            },
            "costume_gears": {
                "type": "object",
                "description": "The gear equipped on a kicker costume, keyed by the KickerCostume row id "
                               "(2|kicker|costume|variant, e.g. 2010101). Only costumes the player has "
                               "equipped something on need an entry; a costume with no entry has three empty "
                               "slots.",
                "propertyNames": {
                    "description": "A costume id the KickerCostume master defines.",
                    "enum": [str(c) for c in costumes],
                },
                "additionalProperties": {"$ref": "#/$defs/gearSlots"},
            },
            "pending_gear": {
                "description": "A gear rolled by gear/create and not yet equipped or discarded. Absent or "
                               "null means nothing is pending, which is the adapter's {KickerId: 0, GearId: 0} "
                               "written as an explicit empty.",
                "type": ["object", "null"],
                "additionalProperties": False,
                "required": ["kicker_id", "gear_id"],
                "properties": {
                    "kicker_id": {
                        "description": "The kicker the roll was made for.",
                        "type": "integer",
                        "minimum": 1,
                    },
                    "gear_id": {
                        "description": "The rolled gear.",
                        "enum": gears,
                    },
                },
            },
        },
        "$defs": {
            "discId": {
                "description": f"A disc in masters_disc.json ({len(discs)} ids). An enum, not a pattern: the "
                               "ids run 3010001-3010138 with gaps, so a pattern would admit ids the game has "
                               "no row for.",
                "enum": discs,
            },
            "deck": {
                "type": "array",
                "description": f"One deck, in slot order: exactly {disc_count} discs, no slot empty.",
                "minItems": disc_count,
                "maxItems": disc_count,
                "items": {"$ref": "#/$defs/discId"},
            },
            "gearSlots": {
                "type": "array",
                "description": "The three gear slots of one costume, in slot order. 0 is an empty slot.",
                "minItems": 3,
                "maxItems": 3,
                "items": {"$ref": "#/$defs/gearIdOrEmpty"},
            },
            "gearIdOrEmpty": {
                "description": f"A gear in masters_gear.json ({len(gears)} ids), or {EMPTY_GEAR} for an empty "
                               "slot.",
                "enum": [EMPTY_GEAR] + gears,
            },
        },
    }

    # A sample document: the starter decks, the first costume with one gear equipped, a second
    # costume left empty so both forms of a slot are visible, and nothing pending.
    first_two = costumes[:2]
    draft = {
        "schema_version": 1,
        "decks": {str(row["number"]): [row[f"discId{i}"] for i in range(1, disc_count + 1)] for row in starter},
        "active_deck": deck_numbers[0],
        "costume_gears": {
            str(first_two[0]): [gears[0], EMPTY_GEAR, EMPTY_GEAR],
            str(first_two[1]): [EMPTY_GEAR, EMPTY_GEAR, EMPTY_GEAR],
        },
        "pending_gear": None,
    }
    return schema, draft


def render(obj):
    return json.dumps(obj, indent=2, ensure_ascii=False) + "\n"


def main(argv):
    schema, draft = build()
    schema_text, draft_text = render(schema), render(draft)

    if "--rules" in argv:
        # The document Config's session.loadout namespace holds: Session reads this, not the
        # namespace files, and a schema version is one entry in "versions".
        print(render({
            "active_version": 1,
            "versions": {"1": schema},
        }), end="")
        return 0

    if "--check" in argv:
        stale = [name for name, want in (("schema.json", schema_text), ("draft.json", draft_text))
                 if (HERE / name).read_text(encoding="utf-8") != want]
        if stale:
            print(f"stale: {', '.join(stale)} - rerun gen_schema.py", file=sys.stderr)
            return 1
        print("up to date")
        return 0

    (HERE / "schema.json").write_text(schema_text, encoding="utf-8")
    (HERE / "draft.json").write_text(draft_text, encoding="utf-8")
    print(f"wrote schema.json ({len(schema_text)} bytes) and draft.json ({len(draft_text)} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
