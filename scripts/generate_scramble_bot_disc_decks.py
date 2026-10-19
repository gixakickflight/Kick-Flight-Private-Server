#!/usr/bin/env python3
"""Generate static Scramble bot decks from the served Disc/Skill masters.

Cast times are read from build-action-asset-bundles.py's CAST_CATEGORIES and
CAST_TIMES AST literals, so balance timing stays in one canonical source.
Run with --write to update the AI-disc masters and selector catalog, or
--check to verify checked-in output is current.
"""
from __future__ import annotations

import argparse
import ast
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
GENERATED_ID_START = 1000
VARIANTS = 4
PROFILES = (
    "carrier", "hunter", "objective_attacker", "healer",
    "speed_support", "trap_defender", "damage_support", "tank_defender",
)

# Curated only from documented card effects. These IDs provide explicit utility
# evidence; generic attack/mobility/trap candidates are additionally checked
# against the shipped Disc, Skill and sub-skill masters below.
ALLY_HP_HEAL = {3010065, 3010068, 3010104, 3010124}
TEAM_MOVE_SPEED = {3010053}
TEAM_ATTACK_BUFF = {3010055, 3010056}
TEAM_DAMAGE_REDUCTION = {3010058}
TEAM_ENERGY_BUFF = {3010075}
# These cards explicitly return the user to the starting point; enemy-target
# warps and attack dashes do not count as carrier escape utility.
SAFE_ESCAPE = {3010073, 3010074}
OBJECTIVE_TRAPS = {3010031, 3010036, 3010038, 3010039, 3010040, 3010041,
                   3010042, 3010043, 3010044, 3010046, 3010049, 3010050,
                   3010082}

PROFILE_RULES = {
    # category scores encode role utility; cast time is a smaller, category-
    # weighted tie-break so the globally fastest skill never wins by default.
    "carrier": {"must": ("safe_escape", "mobility"), "weights": {"MOVE": 5, "WARP": 4, "ATK(Rush)": 2, "BUFF": 1}},
    "hunter": {"must": ("attack",), "minAttack": 2, "weights": {"ATK(Long Distance)": 5, "ATK(Short Distance)": 4, "ATK(Rush)": 3, "ATK(Circumference)": 2, "TRAP": 1}},
    "objective_attacker": {"must": ("rush", "mobility"), "weights": {"ATK(Rush)": 5, "MOVE": 4, "WARP": 3, "ATK(Short Distance)": 2, "BUFF": 1}},
    "healer": {"must": ("ally_hp_heal",), "weights": {"HEAL": 5, "WARP": 5, "BUFF": 2, "MOVE": 1}},
    "speed_support": {"must": ("team_move_speed",), "weights": {"BUFF": 5, "MOVE": 4, "WARP": 2, "ATK(Long Distance)": 1}},
    "trap_defender": {"must": ("trap",), "minTrap": 2, "weights": {"TRAP": 5, "BUFF": 2, "HEAL": 1, "ATK(Long Distance)": 1}},
    "damage_support": {"must": ("team_attack_buff",), "weights": {"BUFF": 5, "ATK(Long Distance)": 3, "ATK(Short Distance)": 2, "ATK(Rush)": 1}},
    "tank_defender": {"must": ("team_damage_reduction",), "weights": {"BUFF": 5, "TRAP": 4, "HEAL": 2, "ATK(Circumference)": 1}},
}


def read_json(path: str):
    return json.loads((ROOT / path).read_text(encoding="utf-8"))


def source_literals(path: Path, names: set[str]) -> dict:
    tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
    values = {}
    for node in tree.body:
        if isinstance(node, ast.Assign):
            for target in node.targets:
                if isinstance(target, ast.Name) and target.id in names:
                    values[target.id] = ast.literal_eval(node.value)
    missing = names - values.keys()
    if missing:
        raise ValueError(f"Missing canonical timing literals: {sorted(missing)}")
    return values


def make_card_catalog() -> tuple[dict[int, dict], dict[str, int], list[str]]:
    discs = {row["id"]: row for row in read_json("config/masters_disc.json")}
    skills = {row["id"]: row for row in read_json("config/masters_skill.json")}
    extras = {
        "HEAL": {row["skillId"] for row in read_json("config/masters_skill_heal.json")},
        "BUFF": {row["skillId"] for row in read_json("config/masters_skill_condition.json")},
        "TRAP": {row["skillId"] for row in read_json("config/masters_skill_trap.json")},
    }
    mapping = read_json("docs/disc_cards.json")
    type_to_category = mapping["typeToSkillCategoryType"]
    cards = {}
    errors = []
    for raw in mapping["cards"]:
        disc_id = raw.get("discId")
        if disc_id not in discs:
            errors.append(f"card {disc_id} has no Disc master")
            continue
        disc = discs[disc_id]
        skill_id = disc["skillId"]
        skill = skills.get(skill_id)
        if skill is None or skill.get("skillType") != 1:
            errors.append(f"disc {disc_id} has no valid Disc Skill master {skill_id}")
            continue
        type_name = raw["type"]
        cast_category = type_name if type_name.startswith("ATK(") else type_name.split("(", 1)[0]
        skill_category_name = "ATK" if cast_category.startswith("ATK(") else cast_category
        if skill_category_name not in type_to_category:
            errors.append(f"disc {disc_id} has unknown type {type_name}")
            continue
        if skill.get("skillCategoryType") != type_to_category[skill_category_name]:
            errors.append(f"disc {disc_id} category mismatch: card {type_name}, skill {skill.get('skillCategoryType')}")
            continue
        if skill_category_name in extras and skill_id not in extras[skill_category_name]:
            # Keep incomplete cards out of every generated deck; some archived
            # catalog entries deliberately lack their typed effect row.
            continue
        tags = set()
        effect = raw.get("effect", "").lower()
        if skill_category_name.startswith("ATK") or skill_category_name == "ATK":
            tags.add("attack")
        if skill_category_name in {"MOVE", "WARP"}:
            tags.add("mobility")
        if disc_id in SAFE_ESCAPE:
            tags.add("safe_escape")
        if cast_category == "ATK(Rush)":
            tags.add("rush")
        if disc_id in ALLY_HP_HEAL:
            tags.add("ally_hp_heal")
        if disc_id in TEAM_MOVE_SPEED:
            tags.add("team_move_speed")
        if disc_id in TEAM_ATTACK_BUFF:
            tags.add("team_attack_buff")
        if disc_id in TEAM_DAMAGE_REDUCTION:
            tags.add("team_damage_reduction")
        if disc_id in TEAM_ENERGY_BUFF:
            tags.add("team_energy_buff")
        if skill_category_name == "TRAP" and disc_id in OBJECTIVE_TRAPS:
            tags.add("trap")
            tags.add("objective_trap")
        if skill_category_name == "TRAP" and "trap" in effect:
            tags.add("trap")
        cards[disc_id] = {
            "discId": disc_id,
            "name": raw["name"],
            "type": type_name,
            "castCategory": cast_category,
            "skillId": skill_id,
            "tags": tags,
        }
    if errors:
        raise ValueError("Invalid Disc catalog rows:\n- " + "\n- ".join(errors[:40]))
    return cards, type_to_category, []


def kicker_roles() -> dict[int, int]:
    return {row["kickerId"]: row["roleType"] for row in read_json("config/masters_kicker_parameter.json")}


def utility_bonus(profile: str, card: dict, kicker_id: int) -> float:
    tags = card["tags"]
    bonus = 0.0
    # Native passive synergies from KickerDetail: Ruriha HEAL, Owlbert TRAP.
    if kicker_id == 2 and card["castCategory"] == "HEAL":
        bonus += 1.5
    if kicker_id == 5 and card["castCategory"] == "TRAP":
        bonus += 1.5
    if kicker_id == 7 and "team_move_speed" in tags:
        bonus += 1.0
    if kicker_id == 14 and "team_attack_buff" in tags:
        bonus += 1.0
    if profile == "carrier" and "mobility" in tags:
        bonus += 1.5
    if profile == "carrier" and "safe_escape" in tags:
        bonus += 3.0
    if profile == "objective_attacker" and ("rush" in tags or "mobility" in tags):
        bonus += 1.0
    if profile in {"trap_defender", "tank_defender"} and "objective_trap" in tags:
        bonus += 1.0
    if profile == "healer" and "ally_hp_heal" in tags:
        bonus += 1.5
    if profile == "speed_support" and "team_move_speed" in tags:
        bonus += 1.5
    if profile == "damage_support" and "team_attack_buff" in tags:
        bonus += 1.5
    return bonus


def generate_decks(cards: dict[int, dict], role_map: dict[int, int], categories: tuple[str, ...], cast_times: dict,
                   existing_ai_rows: list[dict]) -> tuple[list[dict], list[dict], list[dict]]:
    ordered_cards = sorted(cards.values(), key=lambda c: c["discId"])
    decks, catalog = [], []
    profile_order = {name: index for index, name in enumerate(PROFILES)}
    deck_number = 0
    for kicker_id in sorted(role_map):
        timing = cast_times[kicker_id]
        if len(timing) != len(categories):
            raise ValueError(f"kicker {kicker_id}: cast-time count {len(timing)} != {len(categories)}")
        time_by_category = dict(zip(categories, timing))
        for profile in PROFILES:
            rules = PROFILE_RULES[profile]
            used_decks: set[tuple] = set()
            for variant in range(1, VARIANTS + 1):
                chosen: list[dict] = []
                # Reserve each required utility tag, then enforce profile minima.
                for required_tag in rules["must"]:
                    candidates = [card for card in ordered_cards if required_tag in card["tags"]
                                  and card["discId"] not in {x["discId"] for x in chosen}]
                    if not candidates:
                        raise ValueError(f"No required utility candidates for {profile}: {required_tag}")
                    candidates.sort(key=lambda c: (
                        -(utility_bonus(profile, c, kicker_id)),
                        -rules["weights"].get(c["castCategory"], 0),
                        time_by_category.get(c["castCategory"], 9.0), c["discId"]))
                    # Rank first; variant selects successive ranked choices. This
                    # lets actual category timing change which functionally useful
                    # card wins for two kickers with different cast times.
                    offset = (variant - 1) % len(candidates)
                    chosen.append(candidates[offset])
                while sum("attack" in c["tags"] for c in chosen) < rules.get("minAttack", 0):
                    candidates = [c for c in ordered_cards if "attack" in c["tags"] and c["discId"] not in {x["discId"] for x in chosen}]
                    if not candidates:
                        raise ValueError(f"Not enough unique attack cards for {profile}")
                    chosen.append(max(candidates, key=lambda c: (
                        rules["weights"].get(c["castCategory"], 0)
                        + utility_bonus(profile, c, kicker_id)
                        + rules["weights"].get(c["castCategory"], 0) * max(0.0, 2.5 - time_by_category.get(c["castCategory"], 2.0)) * 0.08,
                        -c["discId"])))
                while sum("trap" in c["tags"] for c in chosen) < rules.get("minTrap", 0):
                    candidates = [c for c in ordered_cards if "trap" in c["tags"] and c["discId"] not in {x["discId"] for x in chosen}]
                    if not candidates:
                        raise ValueError(f"Not enough unique trap cards for {profile}")
                    chosen.append(max(candidates, key=lambda c: (
                        rules["weights"].get(c["castCategory"], 0)
                        + utility_bonus(profile, c, kicker_id)
                        + rules["weights"].get(c["castCategory"], 0) * max(0.0, 2.5 - time_by_category.get(c["castCategory"], 2.0)) * 0.08,
                        -c["discId"])))
                while len(chosen) < 4:
                    pool = [c for c in ordered_cards if c["discId"] not in {x["discId"] for x in chosen}]
                    def score(card: dict) -> tuple:
                        category = card["castCategory"]
                        weight = rules["weights"].get(category, 0)
                        if profile == "hunter" and card["tags"] & {"attack"}:
                            weight += 2
                        if profile == "trap_defender" and "trap" in card["tags"]:
                            weight += 2
                        # Category-specific timing weight, capped to keep utility first.
                        cast = time_by_category.get(category, 2.0)
                        timing_weight = rules["weights"].get(category, 0)
                        timing_bonus = timing_weight * max(0.0, 2.5 - cast) * 0.08
                        # Slight deterministic rotation yields distinct static variants.
                        rotation = ((card["discId"] * 17 + kicker_id * 13 + profile_order[profile] * 7 + variant * 11) % 19) * 0.005
                        category_repeat = sum(x["castCategory"] == category for x in chosen)
                        return (weight + utility_bonus(profile, card, kicker_id) + timing_bonus - 0.4 * category_repeat + rotation,
                                -card["discId"])
                    selected = max(pool, key=score)
                    chosen.append(selected)
                deck_signature = tuple(sorted(card["discId"] for card in chosen))
                if profile == "carrier" and not any("safe_escape" in card["tags"] for card in chosen):
                    raise ValueError(f"Carrier deck lacks a return-to-start escape for kicker={kicker_id}, variant={variant}")
                if deck_signature in used_decks:
                    # Deterministically replace the final filler with the best
                    # candidate that produces a not-yet-used four-card set.
                    prefix_ids = {x["discId"] for x in chosen[:-1]}
                    alternatives = [c for c in ordered_cards if c["discId"] not in prefix_ids]
                    alternatives.sort(key=lambda c: (
                        -(rules["weights"].get(c["castCategory"], 0) + utility_bonus(profile, c, kicker_id)
                          + rules["weights"].get(c["castCategory"], 0) * max(0.0, 2.5 - time_by_category.get(c["castCategory"], 2.0)) * 0.08),
                        c["discId"]))
                    replacement = next((c for c in alternatives
                                         if tuple(sorted(prefix_ids | {c["discId"]})) not in used_decks), None)
                    if replacement is not None:
                        chosen[-1] = replacement
                        deck_signature = tuple(sorted(card["discId"] for card in chosen))
                if len(set(deck_signature)) != 4 or deck_signature in used_decks:
                    raise ValueError(f"Cannot form four distinct variant decks for kicker={kicker_id}, profile={profile}")
                used_decks.add(deck_signature)
                deck_number += 1
                deck_id = GENERATED_ID_START + deck_number
                decks.append({
                    "id": deck_id,
                    "discIds": [card["discId"] for card in chosen],
                })
                catalog.append({
                    "deckId": deck_id, "kickerId": kicker_id, "roleType": role_map[kicker_id],
                    "profile": profile, "variant": variant,
                    "discIds": [card["discId"] for card in chosen],
                    "castTimes": {category: round(time_by_category[category], 3) for category in categories},
                })
    unique_disc_ids = sorted({disc_id for deck in decks for disc_id in deck["discIds"]})
    ai_id_by_disc = {(row["discId"], row["level"]): row["id"] for row in existing_ai_rows}
    generated_disc_ids = [disc_id for disc_id in unique_disc_ids if (disc_id, 10) not in ai_id_by_disc]
    ai_id_by_disc.update({(disc_id, 10): GENERATED_ID_START + index for index, disc_id in enumerate(generated_disc_ids)})
    ai_rows = [{"id": ai_id_by_disc[(disc_id, 10)], "discId": disc_id, "level": 10}
               for disc_id in generated_disc_ids]
    deck_rows = []
    for deck in decks:
        ai_ids = [ai_id_by_disc[(disc_id, 10)] for disc_id in deck["discIds"]]
        deck_rows.append({
            "id": deck["id"],
            "kickerAiDiscId1": ai_ids[0], "kickerAiDiscId2": ai_ids[1],
            "kickerAiDiscId3": ai_ids[2], "kickerAiDiscId4": ai_ids[3],
        })
    return ai_rows, deck_rows, catalog


def dump_json(path: str, value) -> str:
    return json.dumps(value, ensure_ascii=False, indent=2) + "\n"


def validate_outputs(ai_rows: list[dict], deck_rows: list[dict], catalog_rows: list[dict]) -> None:
    ai_by_id = {row["id"]: row for row in ai_rows}
    generated_decks = [row for row in deck_rows if row["id"] >= GENERATED_ID_START]
    deck_by_id = {row["id"]: row for row in generated_decks}
    if len(ai_by_id) != len(ai_rows) or len(deck_by_id) != len(generated_decks):
        raise ValueError("Generated AI-disc/deck master IDs are not unique")
    signatures: dict[tuple[int, str], set[tuple[int, ...]]] = {}
    variants: dict[tuple[int, str], set[int]] = {}
    seen_catalog_decks = set()
    for item in catalog_rows:
        deck_id = item["deckId"]
        deck = deck_by_id.get(deck_id)
        if deck is None or deck_id in seen_catalog_decks:
            raise ValueError(f"Catalog deck {deck_id} is missing from or duplicated in the served deck master")
        seen_catalog_decks.add(deck_id)
        ai_ids = [deck[f"kickerAiDiscId{index}"] for index in range(1, 5)]
        rows = [ai_by_id.get(ai_id) for ai_id in ai_ids]
        if any(row is None or row["level"] != 10 for row in rows):
            raise ValueError(f"Deck {deck_id} has a missing AI-disc row or non-level-10 disc")
        disc_ids = [row["discId"] for row in rows]
        if disc_ids != item["discIds"] or len(set(disc_ids)) != 4:
            raise ValueError(f"Deck {deck_id} catalog and master Disc IDs differ or are not unique")
        key = (item["kickerId"], item["profile"])
        signatures.setdefault(key, set()).add(tuple(sorted(disc_ids)))
        variants.setdefault(key, set()).add(item["variant"])
    if seen_catalog_decks != set(deck_by_id):
        raise ValueError("Generated deck master rows and catalog entries are not one-to-one")
    expected_variants = set(range(1, VARIANTS + 1))
    for key in ((kicker_id, profile) for kicker_id in range(1, 15) for profile in PROFILES):
        if variants.get(key) != expected_variants or len(signatures.get(key, ())) != VARIANTS:
            raise ValueError(f"Deck variants are incomplete or repeated for kicker/profile {key}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--write", action="store_true", help="write the generated masters and catalog")
    mode.add_argument("--check", action="store_true", help="fail if checked-in output is stale")
    args = parser.parse_args()

    action_source = ROOT / "scripts/build-action-asset-bundles.py"
    literals = source_literals(action_source, {"CAST_CATEGORIES", "CAST_TIMES"})
    categories = tuple(literals["CAST_CATEGORIES"])
    cast_times = literals["CAST_TIMES"]
    cards, _, _ = make_card_catalog()
    role_map = kicker_roles()
    if set(cast_times) != set(role_map):
        raise ValueError("Canonical CAST_TIMES kicker IDs do not match KickerParameter masters")
    existing_ai_rows = [row for row in read_json("config/masters_kicker_ai_disc.json") if row["id"] < GENERATED_ID_START]
    ai_rows, deck_rows, catalog_rows = generate_decks(cards, role_map, categories, cast_times, existing_ai_rows)

    output = {
        "config/masters_kicker_ai_disc.json": existing_ai_rows + ai_rows,
        "config/masters_kicker_ai_disc_deck.json": [row for row in read_json("config/masters_kicker_ai_disc_deck.json") if row["id"] < GENERATED_ID_START] + deck_rows,
        "config/scramble_bot_disc_decks.json": {
            "source": "scripts/generate_scramble_bot_disc_decks.py",
            "castTimeSource": "scripts/build-action-asset-bundles.py:CAST_CATEGORIES,CAST_TIMES",
            "profileNames": list(PROFILES),
            "variantsPerKickerProfile": VARIANTS,
            "decks": catalog_rows,
        },
    }
    validate_outputs(output["config/masters_kicker_ai_disc.json"], output["config/masters_kicker_ai_disc_deck.json"], catalog_rows)
    changed = []
    for relpath, value in output.items():
        path = ROOT / relpath
        generated = dump_json(relpath, value)
        current = path.read_text(encoding="utf-8") if path.exists() else ""
        if current != generated:
            changed.append(relpath)
            if args.write:
                path.write_text(generated, encoding="utf-8", newline="\n")
    if args.check and changed:
        print("Stale generated files:\n" + "\n".join(changed), file=sys.stderr)
        return 1
    mode_label = "wrote" if args.write else ("checked" if args.check else "would write")
    print(f"{mode_label} {len(ai_rows)} AI-disc rows, {len(deck_rows)} deck rows, {len(catalog_rows)} catalog decks")
    if changed and not args.write:
        print("Changed: " + ", ".join(changed))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
