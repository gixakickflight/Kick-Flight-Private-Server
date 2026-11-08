#!/usr/bin/env python3
"""Generate complete master data for all 126 Kick-Flight Discs and Skills."""

import json
import re
import glob
from pathlib import Path
from PIL import Image

REPO_ROOT = Path(__file__).resolve().parent.parent
ASSETS_ROOT = (REPO_ROOT / "../Kick-Flight-Assets").resolve()
SPRITES_GLOB = str(ASSETS_ROOT / "PIPELINE_OUTPUT_V3/2_converted_unity_assets/images/Sprite/**/*thumbnail_3010*.png")

def analyze_element(path):
    try:
        img = Image.open(path).convert("RGBA")
        w, h = img.size
        # Sample upper corner pixels
        pixels = [img.getpixel((x, y)) for x in range(30, 80) for y in range(40, 90) if img.getpixel((x, y))[3] > 200]
        if not pixels:
            return 1 # Fire default
        r = sum(p[0] for p in pixels) // len(pixels)
        g = sum(p[1] for p in pixels) // len(pixels)
        b = sum(p[2] for p in pixels) // len(pixels)
        if r > g and r > b:
            return 1 # Fire
        elif b > r and b > g:
            return 2 # Water
        else:
            return 3 # Wind
    except Exception:
        return 1

def main():
    print("Collecting and analyzing 126 disc thumbnails...")
    sprites = sorted(glob.glob(SPRITES_GLOB, recursive=True))
    disc_map = {}
    for s in sprites:
        m = re.search(r"thumbnail_(3010\d+)", s)
        if m:
            disc_id = int(m.group(1))
            if disc_id not in disc_map:
                disc_map[disc_id] = s

    all_ids = sorted(list(disc_map.keys()))
    print(f"Found {len(all_ids)} unique disc IDs: {all_ids[0]} to {all_ids[-1]}")

    # Skill archetypes
    # Categories: 0: Attack, 1: Heal, 2: Buff, 3: Trap, 4: Warp
    # Actions: 1: ShotAttack, 2: AroundAttack, 3: MoveAttack, 4: FrontAttack, 5: BeamAttack, 6: Support, 7: Trap, 8: Warp
    archetypes = [
        # Front Attack (CQC)
        {"cat": 0, "act": 4, "range": 8.0, "speed": 20.0, "cd": 14, "coef": 2.4,
         "names": {
             1: ["Blazing Fang", "Volcanic Claw", "Scorching Slash", "Fire Bite", "Seismic Fist"],
             2: ["Glacial Swipe", "Polar Fang", "Abyssal Slash", "Frozen Rend", "Tidal Impact"],
             3: ["Emerald Slash", "Hurricane Claw", "Cutting Zephyr", "Vortex Thorn", "Cyclone Cut"]
         },
         "desc": "Powerful melee attack that deals large damage to an enemy and throws it off balance."},

        # Move Attack (Dash attack)
        {"cat": 0, "act": 3, "range": 16.0, "speed": 28.0, "cd": 16, "coef": 2.2,
         "names": {
             1: ["Blazing Rush", "Meteor Flight", "Fire Charge", "Crimson Rocket", "Flame Gust"],
             2: ["Tsunami Rush", "Glacial Charge", "Abyssal Dolphin", "Torrent Gust", "Breaking Wave"],
             3: ["Cyclone Flight", "Hurricane Rush", "Fierce Wind Gust", "Celestial Dive", "Tempest Dart"]
         },
         "desc": "Dashes far ahead at high speed, ramming any enemy in the way."},

        # Shot Attack (Projectile)
        {"cat": 0, "act": 1, "range": 25.0, "speed": 35.0, "cd": 13, "coef": 1.9,
         "names": {
             1: ["Magma Shot", "Guided Pyrosphere", "Solar Lance", "Flash Spark", "Fire Bullet"],
             2: ["Sharp Icicle", "Sea Shell", "Frozen Dart", "Piercing Drop", "Aqua Arrow"],
             3: ["Tempest Feather", "Air Needle", "Hurricane Gust", "Sonic Arrow", "Green Dart"]
         },
         "desc": "Fires fast, high-precision guided projectiles at long-range targets."},

        # Around Attack (Radial AoE)
        {"cat": 0, "act": 2, "range": 10.0, "speed": 15.0, "cd": 18, "coef": 2.6,
         "names": {
             1: ["Blazing Nova", "Volcanic Burst", "Fire Ring", "Crimson Supernova", "Flame Circle"],
             2: ["Polar Blizzard", "Abyssal Maelstrom", "Glacial Storm", "Tidal Wave", "Cold Sphere"],
             3: ["Emerald Tornado", "Tempest Dome", "Aerial Vortex", "Radial Hurricane", "Unleashed Wind"]
         },
         "desc": "Unleashes a devastating elemental blast in a circle around the Kicker."},

        # Beam Attack
        {"cat": 0, "act": 5, "range": 30.0, "speed": 45.0, "cd": 20, "coef": 2.8,
         "names": {
             1: ["Solar Laser", "Plasma Beam", "Fire Ray", "Magma Cannon", "Thermal Ray"],
             2: ["Cryo Ray", "Glacial Beam", "Abyssal Laser", "Water Column", "Polar Ray"],
             3: ["Void Beam", "Seismic Ray", "Temporal Laser", "Quantum Cut", "Galactic Beam"]
         },
         "desc": "Channels a powerful continuous beam that pierces defenses and hits every target in line."},

        # Heal (Instant / Regen)
        {"cat": 1, "act": 6, "range": 0.0, "speed": 0.0, "cd": 22, "coef": 1.0,
         "names": {
             1: ["Vitality Flame", "Restoring Warmth", "Restoring Phoenix", "Fire Heart", "Solar Blessing"],
             2: ["Pure Spring", "Dewdrops", "Sea Balm", "Healing Oasis", "Drops of Life"],
             3: ["Healing Breeze", "Breath of Life", "Forest Sap", "Emerald Cure", "Vital Zephyr"]
         },
         "desc": "Instantly restores a large portion of max HP."},

        # Shield / Buff
        {"cat": 2, "act": 6, "range": 0.0, "speed": 0.0, "cd": 24, "coef": 1.0,
         "names": {
             1: ["Lava Mantle", "Fire Shield", "Wall of Fire", "Ruby Armor", "Blazing Bulwark"],
             2: ["Aegis Barrier", "Ice Dome", "Aqua Mantle", "Crystal Shield", "Sapphire Bulwark"],
             3: ["Hurricane Veil", "Cyclone Shield", "Feather Mantle", "Tempest Barrier", "Emerald Aura"]
         },
         "desc": "Deploys a sturdy energy barrier that negates damage and prevents knockdowns."},

        # Trap
        {"cat": 3, "act": 7, "range": 5.0, "speed": 0.0, "cd": 19, "coef": 1.8,
         "names": {
             1: ["Fire Mine", "Volcanic Trap", "Blazing Net", "Explosive Snare", "Magma Pit"],
             2: ["Frost Snare", "Cold Mine", "Tidal Trap", "Glacial Pit", "Ice Prison"],
             3: ["Gravity Trap", "Void Mine", "Gale Snare", "Tempest Net", "Hidden Vortex"]
         },
         "desc": "Sets an invisible trap in the air that immobilizes and damages any enemy that triggers it."},

        # Warp
        {"cat": 4, "act": 8, "range": 50.0, "speed": 0.0, "cd": 25, "coef": 1.0,
         "names": {
             1: ["Fire Step", "Phoenix Leap", "Crimson Teleport", "Solar Blink", "Magma Leap"],
             2: ["Tidal Warp", "Aqua Leap", "Sea Reflection", "Polar Teleport", "Abyssal Blink"],
             3: ["Dimensional Leap", "Tactical Warp", "Zephyr Blink", "Hurricane Leap", "Aerial Teleport"]
         },
         "desc": "Instantly teleports the Kicker to the position of an ally or the crystal Guardian."}
    ]

    discs = []
    skills = []
    rarity_dist = [0, 1, 2, 3] # N, R, SR, UR

    for idx, disc_id in enumerate(all_ids):
        sprite_path = disc_map[disc_id]
        element = analyze_element(sprite_path) # 1: Fire, 2: Water, 3: Wind
        
        # Determine archetype cleanly across index
        arch = archetypes[idx % len(archetypes)]
        
        # Determine rarity:
        # Lower IDs (first 20) -> N and R
        # Middle IDs (20..80) -> R and SR
        # Higher IDs (80..126) -> SR and UR
        if idx < 20:
            rarity = 0 if idx % 2 == 0 else 1
            rank = 0 # Rank D
        elif idx < 60:
            rarity = 1 if idx % 2 == 0 else 2
            rank = 1 if idx < 40 else 2 # Rank C or B
        elif idx < 95:
            rarity = 2 if idx % 3 != 0 else 3
            rank = 3 # Rank A
        else:
            rarity = 3 # UR
            rank = 4 # Rank S

        # Select name
        name_list = arch["names"][element]
        name_base = name_list[idx % len(name_list)]
        suffix = f" {['I', 'II', 'III', 'IV', 'V', 'EX', 'Zero', 'Plus', 'Alpha', 'Omega'][idx % 10]}" if idx >= len(name_list) else ""
        disc_name = f"{name_base}{suffix}"

        # Base stats scaling with rarity
        # N: HP 200..700, Atk 60..200
        # R: HP 350..1100, Atk 110..320
        # SR: HP 500..1500, Atk 160..480
        # UR: HP 700..2100, Atk 220..680
        base_hp = [200, 350, 500, 700][rarity]
        max_hp = [700, 1100, 1500, 2100][rarity]
        base_atk = [60, 110, 160, 220][rarity]
        max_atk = [200, 320, 480, 680][rarity]
        cd_min = arch["cd"]
        cd_max = max(8.0, arch["cd"] - [2.0, 3.0, 4.0, 5.0][rarity])

        coef_min = arch["coef"]
        coef_max = coef_min * [1.4, 1.6, 1.8, 2.0][rarity]

        disc_entry = {
            "id": disc_id,
            "name": disc_name,
            "rarityType": rarity,
            "skillId": disc_id,
            "sortOrder": idx + 1,
            "rank": rank,
            "discType": 1,
            "minHp": base_hp,
            "maxHp": max_hp,
            "hpGrowGroupId": 1,
            "minAttack": base_atk,
            "maxAttack": max_atk,
            "attackGrowGroupId": 1,
            "minCoefficient": round(coef_min, 2),
            "maxCoefficient": round(coef_max, 2),
            "coefficientGrowGroupId": 1,
            "minCoolTime": float(cd_min),
            "maxCoolTime": float(cd_max),
            "coolTimeGrowGroupId": 1,
            "releaseDatetime": "2019-01-01 00:00:00"
        }
        discs.append(disc_entry)

        skill_entry = {
            "id": disc_id,
            "description": arch["desc"],
            "skillType": 1, # Disc
            "skillActionType": arch["act"],
            "skillCategoryType": arch["cat"],
            "targetAreaType": 0,
            "coolTime": cd_min,
            "range": arch["range"],
            "speed": arch["speed"],
            "summonId": 0,
            "attributeType": element,
            "seId": 1001,
            "coefficient": round(coef_max, 2)
        }
        skills.append(skill_entry)

    # Disc Rarity Master
    disc_rarities = [
        {"id": 1, "rarityType": 0, "initialLevel": 1},
        {"id": 2, "rarityType": 1, "initialLevel": 1},
        {"id": 3, "rarityType": 2, "initialLevel": 1},
        {"id": 4, "rarityType": 3, "initialLevel": 1}
    ]

    # Disc Grow Master (Levels 1 to 50)
    disc_grows = []
    for lvl in range(1, 51):
        disc_grows.append({
            "id": lvl,
            "groupId": 1,
            "level": lvl,
            "rate": round(1.0 + (lvl - 1) * 0.05, 3)
        })

    # Disc Buildup Master (Levels 1 to 50 for each rarity)
    disc_buildups = []
    buildup_id = 1
    for r in range(4): # N, R, SR, UR
        for lvl in range(1, 51):
            disc_buildups.append({
                "id": buildup_id,
                "rarityType": r,
                "level": lvl,
                "necessaryDiscForceAmount": lvl * 50 * (r + 1),
                "totalDiscAmount": 1 + lvl * (4 - r)
            })
            buildup_id += 1

    # Initial Disc Decks
    initial_decks = [
        {"id": 1, "number": 1, "discId1": all_ids[0], "discId2": all_ids[1], "discId3": all_ids[2], "discId4": all_ids[3]},
        {"id": 2, "number": 2, "discId1": all_ids[4], "discId2": all_ids[5], "discId3": all_ids[6], "discId4": all_ids[7]},
        {"id": 3, "number": 3, "discId1": all_ids[8], "discId2": all_ids[9], "discId3": all_ids[10], "discId4": all_ids[11]}
    ]

    # Gears Master
    # Color types: 0: Red, 1: Green, 2: Yellow, 3: Blue, 4: White
    # 25 GearSkillTypes
    gear_skills_def = [
        (0, "Max HP Boost", "Increases the Kicker's max HP."),
        (1, "ATK Boost", "Increases the damage of all attacks."),
        (2, "Movement Speed", "Increases flight and movement speed."),
        (3, "Fire Boost", "Increases the damage of Fire skills and discs."),
        (4, "Water Boost", "Increases the damage of Water skills and discs."),
        (5, "Wind Boost", "Increases the damage of Wind skills and discs."),
        (6, "Stun Resistance", "Reduces the duration of stun received."),
        (7, "Paralysis Resistance", "Reduces the chance and duration of paralysis."),
        (8, "Poison Resistance", "Reduces damage over time from poison."),
        (9, "Silence Resistance", "Reduces the duration of skill sealing."),
        (10, "ATK Debuff Resistance", "Mitigates enemy attack reduction."),
        (11, "DEF Debuff Resistance", "Mitigates enemy defense reduction."),
        (12, "Speed Debuff Resistance", "Mitigates enemy flight slowdown."),
        (13, "Cooldown Reduction", "Speeds up the recharge of all equipped discs."),
        (14, "Special Skill Charge Boost", "Increases special gauge gain."),
        (15, "Fast Boost Regeneration", "Recovers the boost gauge faster."),
        (16, "Healing Received Boost", "Strengthens healing effects received."),
        (17, "ATK Buff Booster", "Extends the duration and strength of attack buffs."),
        (18, "DEF Buff Booster", "Extends the duration and strength of defense buffs."),
        (19, "Speed Buff Booster", "Extends the duration of speed buffs."),
        (20, "Faster Respawn", "Reduces respawn seconds in battle."),
        (21, "Special Gauge Retention on Death", "Keeps part of the special gauge when defeated."),
        (22, "Mixed Resistance: ATK and Speed", "Mitigates both attack and speed debuffs."),
        (23, "Mixed Resistance: Silence and Poison", "Partial immunity to silence and poison."),
        (24, "Mixed Synergy: ATK and Speed", "Jointly increases attack and flight buffs.")
    ]

    gear_skills = []
    gears = []
    gear_id = 1
    for skill_type, name, desc in gear_skills_def:
        gear_skills.append({
            "id": skill_type + 1,
            "name": name,
            "gearSkillType": skill_type,
            # GearSameColorBonus rows match on GearSkill.groupId (PlayerGearParameter..ctor, 0x1744488) and nothing else
            # reads it: one group lets the bonus rows below apply to every skill. Split a skill into its own group only
            # together with bonus rows for that group, or it gets no same-colour bonus at all.
            "groupId": 1
        })
        # Generate gears of different rarities and colors for each skill
        for rarity in range(4): # N, R, SR, UR
            for color in range(5): # Red, Green, Yellow, Blue, White
                coef = round(0.02 + 0.02 * rarity + 0.005 * color, 3)
                gears.append({
                    "id": gear_id,
                    "name": f"Gear {name} ({['N', 'R', 'SR', 'UR'][rarity]})",
                    "rarityType": rarity,
                    "gearColorType": color,
                    "gearSkillId": skill_type + 1,
                    "coefficient": coef
                })
                gear_id += 1

    # Gear Same Color Bonus
    gear_color_bonuses = []
    bonus_id = 1
    for r in range(4):
        for count in [2, 3]:
            gear_color_bonuses.append({
                "id": bonus_id,
                "groupId": 1,
                "rarityType": r,
                "count": count,
                # Added to each matching gear's own coefficient (0x1744568), so it is sized from the rarity's middle
                # base value (0.03 / 0.05 / 0.07 / 0.09): +25% of it for two gears of a colour, +50% for three.
                "coefficient": round((0.03 + 0.02 * r) * (0.25 if count == 2 else 0.5), 4)
            })
            bonus_id += 1

    # Write files
    (REPO_ROOT / "config/masters_disc.json").write_text(json.dumps(discs, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    (REPO_ROOT / "config/masters_skill.json").write_text(json.dumps(skills, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    (REPO_ROOT / "config/masters_disc_rarity.json").write_text(json.dumps(disc_rarities, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    (REPO_ROOT / "config/masters_disc_grow.json").write_text(json.dumps(disc_grows, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    (REPO_ROOT / "config/masters_disc_buildup.json").write_text(json.dumps(disc_buildups, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    (REPO_ROOT / "config/masters_initial_disc_deck.json").write_text(json.dumps(initial_decks, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    (REPO_ROOT / "config/masters_gear.json").write_text(json.dumps(gears, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    (REPO_ROOT / "config/masters_gear_skill.json").write_text(json.dumps(gear_skills, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    (REPO_ROOT / "config/masters_gear_same_color_bonus.json").write_text(json.dumps(gear_color_bonuses, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    print(f"Generated:")
    print(f" - {len(discs)} Discs in config/masters_disc.json")
    print(f" - {len(skills)} Skills in config/masters_skill.json")
    print(f" - {len(disc_rarities)} Rarity entries in config/masters_disc_rarity.json")
    print(f" - {len(disc_grows)} Grow levels in config/masters_disc_grow.json")
    print(f" - {len(disc_buildups)} Buildup tiers in config/masters_disc_buildup.json")
    print(f" - {len(initial_decks)} Initial Decks in config/masters_initial_disc_deck.json")
    print(f" - {len(gears)} Gears in config/masters_gear.json")
    print(f" - {len(gear_skills)} Gear Skills in config/masters_gear_skill.json")
    print(f" - {len(gear_color_bonuses)} Gear Color Bonuses in config/masters_gear_same_color_bonus.json")

if __name__ == "__main__":
    main()
