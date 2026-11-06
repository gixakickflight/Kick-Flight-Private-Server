#!/usr/bin/env python3
"""
generate_combat_masters.py

Creates the combat master tables the client needs for basic attacks, discs, kicker
skills and special skills to run without NullReferenceExceptions, and fixes the
skill-id linkage so disc skills resolve against the ActionMaster that ships inside
the APK (actioneditor/aed_master.unity3d: disc skills 10001-10138, kicker skills 20001-20014).

Everything written here is a structurally valid *template*: ids and keys are right,
the tuning numbers are neutral defaults. Edit the JSON files under config/ to fill in
the real effects (see docs/COMBAT_MASTERS_FILL_IN.md for the meaning of every column).

Existing files are never overwritten unless --force is given; masters_skill.json and
masters_disc.json are rewritten in place (id remap only) and a .bak copy is kept.
"""
from __future__ import annotations

import argparse
import json
import re
import shutil
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
CONFIG = REPO_ROOT / "config"

# enums (from Il2CppDumper dump.cs) ------------------------------------------------
HIT_NONE, HIT_COMMON_S, HIT_COMMON_M, HIT_COMMON_L = 0, 1, 2, 3
HIT_SLASH_S, HIT_SLASH_M, HIT_SLASH_L = 4, 5, 6
HIT_BLOW_S, HIT_BLOW_M, HIT_BLOW_L = 7, 8, 9
HIT_GUN_S, HIT_GUN_M, HIT_GUN_L = 10, 11, 12
COL_BOX, COL_SPHERE, COL_CAPSULE, COL_CYLINDER = 0, 1, 2, 3
COLHIT_ONE, COLHIT_ALL, COLHIT_ALL_REHIT = 0, 1, 2
BONE_RIGHT_HAND, BONE_COMMON = 0, 9
# WeaponType
WT_SWORD, WT_TWO_GUNS, WT_HAMMER, WT_THROWING_STAR, WT_DRONE, WT_ROCKET, WT_GUN, WT_PUNCH_GLOVE = range(8)
WT_BOWGUN, WT_SHIELD, WT_BAT, WT_NUNCHAKU, WT_JAPANESE_SWORD, WT_LASER = range(8, 14)
RANGED = {WT_TWO_GUNS, WT_DRONE, WT_ROCKET, WT_GUN, WT_BOWGUN, WT_LASER, WT_THROWING_STAR}
SLASH = {WT_SWORD, WT_JAPANESE_SWORD, WT_NUNCHAKU, WT_BAT}
# SkillCategoryType
CAT_ATTACK, CAT_HEAL, CAT_BUFF, CAT_TRAP, CAT_WARP, CAT_MOVE = range(6)
# ConditionType (subset)
COND_ATTACK_RATE, COND_SPEED_RATE, COND_DEFENSE_RATE, COND_DAMAGE_RATE = 1, 2, 3, 4
COND_POISON, COND_PARALYSIS, COND_BURN, COND_STUN, COND_SILENT = 5, 6, 7, 8, 9
COND_REGENERATION, COND_SLOWISH = 11, 2
TRIGGER_EXECUTE = 3
TRIGGER_ENTER_TRAP, TRIGGER_ENTER_ENEMY_TRAP, TRIGGER_ENTER_ALLY_TRAP = 4, 5, 6
COND_INHALE, COND_DOT, COND_GRAVITY = 12, 17, 21
# hitLayer used by the shipped ActionMaster collisions (players + guardians)
HIT_LAYER_CHARACTERS = 4864
# fixedDamage (WeaponAttackHit / SpecialSkillHit): when >= 1 the hit does exactly that much damage to PLAYERS instead
# of attack x coefficient (PlayerCharacter.AcceptDamageInfo), so it is 0 for everything except one-shot hits.
# Guardian turrets (NPCGuardian.AcceptDamageInfo) take the raw attack x coefficient damage against their HP, which is
# GuardianParameter.hp per deposited crystal; only when fixedDamage >= 1 does a special case kick in (Kite's special
# drops KickerSpecialSkillMaster.ThrowingStar._guardianDropCrystalCount crystals instead of one-shotting the turret).
# Kickers carry 30-45k HP once four lv10 discs (up to 12044 HP each) are added, so 9999 only left them low. This
# is the value Hitagi's OneShotKiller branch in PlayerCharacter.AcceptDamageInfo hard-codes (0xF423F).
ONE_SHOT_FIXED_DAMAGE = 999999

# Global displacement tuning (play-test 2026-10, confirmed 1.5). Applied when the rows are written, so the presets and
# tables below keep their design values:
#   KNOCKBACK_DISTANCE_SCALE    x every SkillBlowOff / SpecialSkillBlowOff `distance` (discs, kicker skills, specials,
#                               bombs/turrets, Jay's bat bomb; not the rush-disc pursuit row). Basic attacks have no
#                               blow-off (PlayerWeaponAttackInfo.get_BlowOffInfo returns null) and their knockBackFlag
#                               stagger (PlayerStateKnockBack) moves nobody, so there is no distance to scale for them.
#   DISPLACEMENT_HITSTUN_SCALE  x every blow-off `rigorTime` (the stun after landing: PlayerStateBlowOff.StateDuration =
#                               base + rigorTime; launch, slam, push and the rush pursuit all go through it).
#                               The hard-coded 0.5 s of PlayerStateKnockBack (basic-attack / special knockBackFlag
#                               hits, `fmov s8, #0.5` at 0x17DA75C) is NOT scaled: that would be a client patch and is
#                               on hold until the data-only change is play-tested. Pull-in has no stun column
#                               (PlayerStatePullIn constants).
KNOCKBACK_DISTANCE_SCALE = 1.5
DISPLACEMENT_HITSTUN_SCALE = 1.5


def _blow_off_row(bo: dict) -> dict:
    """distance / speed / rigorTime / directionType of a blow-off preset with the global scales applied.
    A preset with "fixed_distance" (the rush pursuit) keeps its distance: it is a client-patch tuning value, not a
    knockback."""
    distance = bo["distance"] if bo.get("fixed_distance") else bo["distance"] * KNOCKBACK_DISTANCE_SCALE
    return {"distance": round(distance, 3), "speed": bo["speed"],
            "rigorTime": round(bo["rigorTime"] * DISPLACEMENT_HITSTUN_SCALE, 3), "directionType": bo["directionType"]}


DISC_ID_BASE = 3010000    # disc ids are 3010001..; skill id = 10000 + n; summon/model id = n
DISC_SKILL_BASE = 10000
BAT_BOMB_SKILL_ID = 40001
BAT_BOMB_COEFFICIENT = 10.0  # Jay's death bomb: attack x 10 (generate_abilities.py design notes)
KICKER_SKILL_BASE = 20000  # Skill.id of kicker skills; SpecialSkill ids are the plain kickerId

# Disc skills whose APK action-editor timeline (actioneditor/aed_NNN.unity3d, EventItemGroup._list[ClipType.ForcedMovement])
# contains a forced-movement event. MoveAttackSkillAction..ctor dereferences that event unconditionally, so
# skillActionType 3 on any other disc is a guaranteed NullReferenceException that soft-locks the kicker.
# Identical in all five captured bundles (kickers 1, 4, 5, 8, 11).
DISC_SKILLS_WITH_FORCED_MOVE = {
    10001, 10010, 10011, 10012, 10013, 10014, 10026, 10027, 10063, 10068, 10073, 10074, 10076, 10077, 10079,
    10083, 10089, 10091, 10096, 10104, 10108, 10112, 10113, 10118, 10122, 10124, 10132,
}
# Non-trap disc skills whose timeline nevertheless contains a "sensor" collider (Collider clip with
# AttachToType 2 and no damage flag). DiscSkillParameter..ctor then reads SkillTrapMaster.GetDataFromSkillId
# unconditionally, so such a skill needs a masters_skill_trap.json row or every battle with it in a deck NREs
# while loading. 10054 Scorpius: its 14 s continuous cylinder is built as a sensor.
DISC_SKILLS_WITH_SENSOR_COLLIDER = {10054}
# Disc skills whose card type ("MOVE") maps to AutoMove (22) but whose APK timeline is a ShotAttack: four Collider
# clips with MissTargetDirection (intParameters[8]) == 2 plus a 0.47 s ForcedMovement. Only ShotAttackSkillAction
# creates timeline bullets (OnCreateCollider) *and* honours the forced move; its ctor sets IsBackShot from that
# MissTargetDirection, which is the "shoots backwards while dashing" of Pyronkey / Spunkle / Tigre. AutoMoveSkillAction
# has no OnCreateCollider, so as type 22 they dashed and never fired (phone report 2026-09-20).
DISC_ACTION_TYPE_OVERRIDES = {10027: 1, 10079: 1, 10112: 1}
# Skill.targetAreaType picks the aim preview a kicker skill shows while its button is held: SkillActionBase.CreateTargetArea
# kicks trigger (targetAreaType + 0/1/2) of the common target-area effect (EffectPath 0x15). TargetAreaType (metadata
# constants): 0 None, 2 Cube, 5 Cylinder, 8 Sphere, 11 CylinderLong, 14 Plane, 17 Cone. Trigger 17/18 is also what
# LaserSkillAction.get_TargetAreaTrigger hard-codes for Sid's statue placement, so a kicker skill served 17 previews
# Sid's construct. PunchGloveSkillAction (Diatrius) sizes its preview from the kicker-skill collision EndScale and its
# IsTargetInArea is SkillActionBase.IsInBox, i.e. a box: Cube.
KICKER_SKILL_TARGET_AREA = {20011: 2}
# Kicker-skill cooldown (Skill.coolTime, seconds) by kickerId. The skill button reads the Skill row that
# KickerParameter.skillId points at (20000 + kickerId), so this is the KS cooldown. Enforced here so a
# regeneration of masters_skill.json keeps the tuning instead of dropping back to stale defaults.
KICKER_SKILL_COOLDOWNS = {
    1: 15,   # Tsubame
    2: 15,   # Ruriha
    3: 15,   # Coco
    4: 25,   # Kite
    5: 15,   # Owlbert
    6: 10,   # Pitophy
    7: 20,   # Grenhawk
    8: 20,   # Anna
    9: 20,   # Jay
    10: 20,  # Yuyan
    11: 15,  # Diatrius
    12: 20,  # Buzzy Big
    13: 35,  # Hitagi
    14: 35,  # Sid
}


def load(name: str):
    return json.loads((CONFIG / name).read_text(encoding="utf-8"))


def write(name: str, rows, force: bool) -> None:
    path = CONFIG / name
    if path.exists() and not force:
        print(f"keep    {name} (exists, {len(load(name))} rows) -- use --force to regenerate")
        return
    path.write_text(json.dumps(rows, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    print(f"wrote   {name} ({len(rows)} rows)")


def remap_skill_ids(force: bool) -> tuple[list, list]:
    """Disc skill ids must equal the ActionMaster ids (10001..10138), not the disc ids."""
    skills = load("masters_skill.json")
    discs = load("masters_disc.json")
    changed = False
    kickers = {k["kickerId"]: k for k in load("masters_kicker_parameter.json")}
    for row in skills:
        if row["id"] > DISC_ID_BASE:
            n = row["id"] - DISC_ID_BASE
            row["id"] = DISC_SKILL_BASE + n
            row["skillType"] = 1  # Disc
            if not row.get("summonId"):
                row["summonId"] = n
            changed = True
        elif 1 <= row["id"] <= 14 and row.get("skillType") != 3:
            # kicker skills: the APK ActionMaster entries with the kicker-skill effects/collisions are
            # skill_20001..skill_20014 (skill_1..14 are empty placeholders), so id = 20000 + kickerId.
            # Skill id 1 with skillType 3 is the guardian's eye laser (Guardian.skillId), left alone.
            row["id"] = KICKER_SKILL_BASE + row["id"]
            row["skillType"] = 2  # Kicker
            changed = True
        if row["skillType"] == 2 and row["id"] in KICKER_SKILL_TARGET_AREA \
                and row.get("targetAreaType") != KICKER_SKILL_TARGET_AREA[row["id"]]:
            row["targetAreaType"] = KICKER_SKILL_TARGET_AREA[row["id"]]
            changed = True
        if row["skillType"] == 2 and row["id"] - KICKER_SKILL_BASE in KICKER_SKILL_COOLDOWNS:
            want_cd = KICKER_SKILL_COOLDOWNS[row["id"] - KICKER_SKILL_BASE]
            if row.get("coolTime") != want_cd:
                row["coolTime"] = want_cd
                changed = True
        # DiscSkillParameter.GetSkillAction only knows action types 1-8, 21, 22; KickerSkillParameter.GetSkillAction
        # only knows the weapon types 9-20, 23, 24. Anything else returns null and the skill state soft-locks
        # (NullReferenceException in PlayerStateSkill.UpdateActionTargeting every frame).
        if row["skillType"] == 2 and row["id"] - KICKER_SKILL_BASE in kickers:
            wt = kickers[row["id"] - KICKER_SKILL_BASE]["weaponType"]
            want = wt + 9 if wt <= 11 else wt + 11
            if row.get("skillActionType") != want:
                row["skillActionType"] = want
                changed = True
        elif row["skillType"] == 1 and row.get("skillActionType") not in (1, 2, 3, 4, 5, 6, 7, 8, 21, 22):
            row["skillActionType"] = 7 if row.get("skillCategoryType") == CAT_TRAP else 1
            changed = True
        if row["skillType"] == 1 and row.get("skillCategoryType") == CAT_TRAP and row.get("skillActionType") != 7:
            row["skillActionType"] = 7
            changed = True
        if row["skillType"] == 1 and row["id"] in DISC_ACTION_TYPE_OVERRIDES                 and row.get("skillActionType") != DISC_ACTION_TYPE_OVERRIDES[row["id"]]:
            row["skillActionType"] = DISC_ACTION_TYPE_OVERRIDES[row["id"]]
            changed = True
        if row["skillType"] == 1 and row.get("skillActionType") == 3 and row["id"] not in DISC_SKILLS_WITH_FORCED_MOVE:
            print(f"skill {row['id']}: MoveAttack without a forced-move event in the APK timeline -> ShotAttack")
            row["skillActionType"] = 1
            changed = True
    kicker_rows = load("masters_kicker_parameter.json")
    kp_changed = False
    for k in kicker_rows:
        if k.get("skillId") != KICKER_SKILL_BASE + k["kickerId"]:
            k["skillId"] = KICKER_SKILL_BASE + k["kickerId"]
            kp_changed = True
    if kp_changed:
        (CONFIG / "masters_kicker_parameter.json").write_text(json.dumps(kicker_rows, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
        print("remapped masters_kicker_parameter.json skillId -> 20000 + kickerId")
    # Jay's kicker ability (BatAbilityParameter) reads KickerAbility.value as a *skill id* and builds a disc-like
    # skill from it; the APK ActionMaster's spare entry skill_40001 is that bat-bomb trap. Without the Skill row
    # PlayerParameter..ctor throws and GameScene never finishes loading whenever Jay is in the match.
    bat_kicker = next((k["kickerId"] for k in kickers.values() if k["weaponType"] == WT_BAT), None)
    if bat_kicker is not None:
        bomb = next((r for r in skills if r["id"] == BAT_BOMB_SKILL_ID), None)
        if bomb is None:
            skills.append({"id": BAT_BOMB_SKILL_ID, "description": "Bat bomb (kicker ability)", "skillType": 1,
                           "skillActionType": 7, "skillCategoryType": CAT_TRAP, "targetAreaType": 8, "coolTime": 0,
                           "range": 5.0, "speed": 0.0, "summonId": 0, "attributeType": 0, "seId": 0,
                           "coefficient": BAT_BOMB_COEFFICIENT})
            changed = True
        elif bomb.get("coefficient") != BAT_BOMB_COEFFICIENT:
            # PlayerAbilityParameterBase.GetDamageInitInfo: blast damage = attack x this coefficient (design: 10x ATK)
            bomb["coefficient"] = BAT_BOMB_COEFFICIENT
            changed = True
        abilities = load("masters_kicker_ability.json")
        ab_changed = False
        for a in abilities:
            if a["kickerId"] == bat_kicker and a.get("value") != BAT_BOMB_SKILL_ID:
                a["value"] = BAT_BOMB_SKILL_ID
                ab_changed = True
        if ab_changed:
            (CONFIG / "masters_kicker_ability.json").write_text(json.dumps(abilities, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
            print(f"remapped masters_kicker_ability.json: kicker {bat_kicker} value -> {BAT_BOMB_SKILL_ID} (bat bomb skill id)")
    for row in discs:
        if row["skillId"] > DISC_ID_BASE:
            row["skillId"] = DISC_SKILL_BASE + (row["skillId"] - DISC_ID_BASE)
            changed = True
    if changed:
        for name, rows in (("masters_skill.json", skills), ("masters_disc.json", discs)):
            shutil.copy(CONFIG / name, CONFIG / (name + ".bak"))
            (CONFIG / name).write_text(json.dumps(rows, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
            print(f"remapped {name} (backup in {name}.bak)")
    else:
        print("skill/disc ids already remapped")
    return skills, discs


def weapon_tables(kickers: list) -> dict[str, list]:
    attack, hit, collision, bullet = [], [], [], []
    for k in kickers:
        kid, wt = k["kickerId"], k["weaponType"]
        ranged = wt in RANGED
        # attackCount is 0-based: WeaponAttackActionBase.get_ComboCount = _totalComboCount % MaxAttackComboCount
        for count in (0, 1, 2):
            rid = kid * 10 + count + 1
            attack.append({"id": rid, "kickerId": kid, "attackCount": count,
                           "coefficient": [1.0, 1.0, 1.2][count], "seId": 0})
            if wt in SLASH:
                fx = [HIT_SLASH_S, HIT_SLASH_M, HIT_SLASH_L][count]
            elif ranged:
                fx = [HIT_GUN_S, HIT_GUN_S, HIT_GUN_M][count]
            else:
                fx = [HIT_BLOW_S, HIT_BLOW_M, HIT_BLOW_L][count]
            hit.append({"id": rid, "kickerId": kid, "attackCount": count, "commonHitEffectType": fx,
                        "hitSeId": 0, "effectPath": "", "parentBone": BONE_COMMON,
                        "offsetX": 0.0, "offsetY": 0.0, "offsetZ": 0.0, "transformType": 0,
                        "shakeVolume": 0.15, "knockBackFlag": count == 2, "fixedDamage": 0})
            # Basic attacks must be CollisionHitType.One: the collider is then destroyed by the first hit
            # (CollisionDestroyType.Hit). With All it lives out its lifetime even after hitting, and
            # WeaponAttackActionBase.CallbackAttackCollisionDestroy(LifeTime) flags the swing as a miss, drops the
            # target and ResetComboCount() fires - every swing was hit 1 (2026-09-20 KFDIAG 8210 after each swing).
            # The melee *AttackEvent picks CollisionInitializeInfo.MoveRadius instead of the radius whenever the
            # attacker is moving (PlayerUpdateTransformInfo.IsMove), so moveRadius 0 made every swing thrown while
            # chasing a zero-size sphere that could never hit (2026-10-05). Same size moving or standing.
            radius = 0.8 if ranged else [2.5, 2.5, 3.0][count]
            collision.append({"id": rid, "kickerId": kid, "attackCount": count,
                              "collisionType": COL_SPHERE, "collisionHitType": COLHIT_ONE,
                              "hitLayer": HIT_LAYER_CHARACTERS,
                              "radius": radius, "length": 0.0,
                              "originCenterFlag": False, "scaleX": 1.0, "scaleY": 1.0, "scaleZ": 1.0,
                              "moveRadius": radius})
            if ranged:
                bullet.append({"id": rid, "kickerId": kid, "attackCount": count,
                               "distance": float(k.get("attackTargetSearchDistance", 15.0)) + 5.0,
                               "speed": 40.0, "resourcePath": "", "loopSeId": 0, "homingAngle": 15.0,
                               "endType": 0, "actionType": 0, "removeOnOwnerDeadFlag": True})
    return {"masters_weapon_attack.json": attack, "masters_weapon_attack_hit.json": hit,
            "masters_weapon_attack_collision.json": collision, "masters_weapon_attack_bullet.json": bullet,
            "masters_weapon_attack_condition.json": []}


# Kicker skills whose effect lives in the SkillCondition / SkillTrap / SkillBlowOff / SkillPullIn tables rather than
# the ActionMaster (the APK only carries their hit boxes and VFX).
# Owlbert (kicker 5, skill 20005 "Dron Centinela", actionType 13): NPCDrone.InitializeAsync reads
# owner.Param.KickerSkillParameter.Conditions[0] (ArgumentOutOfRange with no row, logcat 14Mon09 02:53) - the
# silence the dropped drone applies - and the drone is left behind as a Silent trap.
COND_STEALTH = 24
COND_SHIELD_FORWARD = 26  # ShieldForwardConditionAction: frontal barrier, effectValue = its HP pool (IConditionSacrifice)
COND_AUTO_MOVE = 30
COND_BOOST_HEAL_SPEED_RATE = 31  # PlayerParameter.GetRecoveryBoostPoint multiplies by it
COND_ATTACK_SPEED_RATE = 28  # PlayerParameter.SetAttackInterval divides the weapon interval by it (2.0 = +100 %)
COND_SP_GAUGE_RATE = 32  # SpecialSkillGaugeRateConditionAction.StartAction: AddSP(-(1 - effectValue) * MaxSP) once (0.8 = -20 %)
TRIGGER_RECEIVE_DAMAGE = 1
# Blow-off presets (SkillBlowOff row -> BlowOffInfo on every hit of the skill -> PlayerStateBlowOff on the victim).
# directionType 1 Up = launch, 2 Down = slam to the ground, 4 AttackDirection = pushed along (victim - attacker).
# FrontAttackSkillAction.get_BlowOffDirectionType falls back to Down (2) when the skill has no row: that is why Blox
# and every other "knocks them upwards" short-range disc slammed instead (phone report 2026-09-20).
_KNOCK_UP = {"distance": 6.0, "speed": 20.0, "rigorTime": 0.5, "directionType": 1}      # = Tsubame's special
_SLAM = {"distance": 6.0, "speed": 25.0, "rigorTime": 0.8, "directionType": 2}
_KNOCK_AWAY = {"distance": 8.0, "speed": 25.0, "rigorTime": 0.5, "directionType": 4}
_KNOCK_FAR = {"distance": 14.0, "speed": 30.0, "rigorTime": 0.7, "directionType": 4}
# Dash "pursuit" discs = MoveAttack (skillActionType 3) discs with a SkillBlowOff row: Leorex 10001, Boarush 10013,
# Propedile 10014, Combat Turtle 10096, Airy 10118. DiscSkillParameter..ctor only flags MoveAttack/FrontAttack hits as
# shield-break when the skill has NO blow-off row and no heal row - exactly the card split "dash pierce attack &
# shield break" (Giamoth, Jet Shark, Rush Blade, Assault Lance) vs "dash pursuit attack".
# Retail pursuit hit (MoveAttackSkillAction.HitCallback): the dash is re-targeted to |victim - dasher| + distance at
# GetSpeed(1, speed) x AttackerSpeedCoefficient and the victim is parented to the dasher (AcceptAttachCharacter)
# while PlayerStateBlowOff also pushes it `distance` along victim - dasher, so the charge stopped short and the victim
# ended up to `distance` ahead of the dasher (play-test 2026-10). Two production client patches change that
# (scripts/re/rush_pursuit_cave.py): the dash keeps its own end (EventItem.Distance; `distance` no longer moves the
# dasher, `speed` is still its post-hit speed) and the victim is entrained (PlayerStateEntrained: held within 2 units
# of the dasher for 0.5 s) before it hands over to this blow-off. So `distance` is only the release push off the
# dasher, along victim - dasher: PlayerStateEntrained ends with the victim on a 2.0-unit orbit around the dasher (radius
# lerps to 2.0 in 0.25 s, 720 deg/s), and 1.0 here put it ~3 units away - the melee kickers' attackTargetSearchDistance
# (3.0), so play-test 2026-10-06 saw victims "out of melee range" after Boarush / Combat Turtle. 0.1 leaves it at the
# orbit radius. It stays > 0 (PlayerStateBlowOff.BeginAction sets CountTargetTime = distance / speed and divides by it)
# and is NOT x KNOCKBACK_DISTANCE_SCALE. rigorTime is the stun after it (x DISPLACEMENT_HITSTUN_SCALE like every row).
_RUSH_PURSUIT = {"distance": 0.1, "speed": 30.0, "rigorTime": 0.4, "directionType": 4, "fixed_distance": True}
STATUE_DURATION = 10.0  # Sid's decoy statue lifetime (s) and the length of his attack-speed buff
COND_RESTRAINTED = 14   # RestraintedConditionAction: bound in place (movement disabled) until it expires
COND_RESTRAINT = 15     # RestraintConditionAction: on the caster; GunSkillAction roots while it is held
ANNA_BIND_SECONDS = 3.0  # Anna's binding ray
KICKER_SKILL_EXTRAS = {
    # Ruriha: backstep shot, the ice bullets paralyse the target (ConditionActionController.CheckEnableMove blocks
    # movement for 6 Paralysis). It used to be a SpeedRate 0.6 slow (play-test 2026-10: "slows instead of paralysing").
    20002: {"conditions": [(COND_PARALYSIS, 2.0, 0.0, 1.0, TRIGGER_RECEIVE_DAMAGE)]},
    # Coco: shock dive slams the target into the ground. HammerSkillAction.AttackEvent builds its DamageCollisionData
    # without a BlowOffInfo, but PlayerCharacter.GetDamageInfo hands the receiver the KickerSkillParameter (AttackType
    # KickerSkill), whose BlowOffInfo is this SkillBlowOff row -> PlayerStateBlowOff Down. It used to be a Stun row
    # (play-test 2026-10: "stuns instead of slamming").
    20003: {"blow_off": _SLAM},
    20005: {"conditions": [(COND_SILENT, 8.0, 0.0, 1.0, 5), (COND_SILENT, 8.0, 0.0, 1.0, 4)],
            "trap": {"trapType": 2, "duration": 8.0, "radius": 8.0, "effectValue": 0.0}},
    # Grenhawk: slow shot
    20007: {"conditions": [(COND_SPEED_RATE, 5.0, 0.0, 0.4, TRIGGER_RECEIVE_DAMAGE)]},
    # Anna: binding ray binds the target in place. Restrainted (14) on whoever the ray hits:
    # RestraintedConditionAction.StartAction stops the victim's velocity and plays the restraint effect,
    # ConditionActionController.CheckEnableMove keeps it from moving (6/8/14/16), and UpdateOperatingTime releases it
    # early if the victim is knocked back / blown off / inhaled or the setter (Anna) dies. ExecuteIntervalAction would
    # tick setter attack x effectValue every `interval` s (accumulator >= interval, so 0 = every frame); the interval is
    # longer than the bind so it never ticks (no CommonConditionHit row for 14 is needed). It used to be a SkillPullIn
    # row (play-test 2026-10: "drags enemies instead of binding them").
    # Restraint (15) on the caster: GunSkillAction.OnUpdateAction/UpdateFinish gate their StopVelocity on IsExists(15),
    # so Anna must hold 15 while the ray binds. In 2.11.0 no data path applies it (GunSkillAction never calls
    # GetKickerSkillConditionInitInfo(3)); the production cave in patch-il2cpp-endpoints.py injects this trigger-3 row
    # from RestraintedConditionAction.StartAction (handoff/dsh-anna-1/report.md section 4, handoff/dsh-patch-1).
    20008: {"conditions": [(COND_RESTRAINTED, ANNA_BIND_SECONDS, ANNA_BIND_SECONDS + 1.0, 0.0, TRIGGER_RECEIVE_DAMAGE),
                           (COND_RESTRAINT, ANNA_BIND_SECONDS, 0.0, 0.0, TRIGGER_EXECUTE)]},
    # Jay: BatSkillAction.OnBeginAction is only player.AcceptCondition(trigger 3 rows). Stealth (24,
    # StealthConditionAction) fades the model (BatAttackAction.PlayMaskFade), plays the condition effect and makes him
    # un-lock-on-able (ConditionActionController.EnableLockedOn) until the duration ends or he attacks / takes damage.
    20009: {"conditions": [(COND_STEALTH, 8.0, 0.0, 1.0, TRIGGER_EXECUTE)]},
    # Yuyan: the extended nunchaku hit pulls the victim to her (same PullIn path as Anna's ray)
    20010: {"pull_in": {"distance": 16.0, "speed": 40.0}},
    # Diatrius: meteor impact sends the victim flying a long way in the hit direction
    20011: {"blow_off": {"distance": 18.0, "speed": 40.0, "rigorTime": 0.8, "directionType": 4}},
    # Buzzy Big: ShieldSkillAction.UpdateExecute only does player.AcceptCondition(trigger 3 rows) - without a row the
    # kicker skill fires (cooldown) and spawns nothing. The forward barrier lasts `duration` s or until it has absorbed
    # `effectValue` damage. (ShieldAll is different: its effectValue is an incoming-damage multiplier, see WT_SHIELD.)
    20012: {"conditions": [(COND_SHIELD_FORWARD, 6.0, 0.0, 4000.0, TRIGGER_EXECUTE)]},
    # Sid: decoy statue. LaserSkillAction.OnBeginAction = CreateTrap + player.AcceptCondition(trigger 3 rows).
    # CreateTrap sends the trap with the kicker skill's TrapInfo (= this SkillTrap row, SkillParameterBase..ctor) and
    # Trap.Initialize builds a StatueTrapAction whatever the trapType, because TrapParameter.IsForceTarget is
    # "owner weapon Laser && AttackType KickerSkill" (a copy of Sid's weapon model scaled by the asset
    # KickerSkillMaster.Laser.StatueScale; turrets, bombs and the guardian AI retarget onto IsForceTarget traps, and it
    # sits on LayerType.Statue for bullets). Without a row TrapActionBase.Initialize NREs on Trap.Param.Info
    # (0x31D49C4) and the statue never appears - that was the "Sid's KS places nothing" bug. trapType 4 Decoy: not a
    # setup type (no guardian push-back), no summon entity, not an around-trap. `duration` = statue lifetime.
    # Passive while the statue stands (design: +100 % attack speed): the 2.11.0 LaserAbilityParameter only shortens
    # the cooldown on shield hits, so the buff is served here as an AttackSpeedRate condition on Sid himself that
    # lasts as long as the statue (it does not end early if the statue is destroyed before `duration`).
    20014: {"trap": {"trapType": 4, "duration": STATUE_DURATION, "radius": 2.0, "effectValue": 0.0},
            "conditions": [(COND_ATTACK_SPEED_RATE, STATUE_DURATION, 0.0, 2.0, TRIGGER_EXECUTE)]},
}

# Jay's passive bomb (BatAbilityParameter -> Trap type 10). BatBombTrapAction.OnManagedUpdate explodes it when
# `_time >= TrapInfo.Duration * TrapTimeRate` (IsTimeExecute is forced true by the ctor, so proximity never triggers
# it). The served row is the TrapInfo (SkillParameterBase.InitializeData: no aed timeline group exists for 40001), and
# the production cave in patch-il2cpp-endpoints.py ("BatAbilityParameter..ctor tail") maps: radius -> blast end radius
# (grows from 2 to it over `interval` s = the explosion collider's lifetime), effectPath -> the explosion SPFX
# (SyncEffect; hits also show ActionMaster hit 207 on the victims). Damage = attack x Skill 40001 coefficient.
# effectPath: EffectManager.InstantiateEffect returns null (no burst, no error) for anything LoadManager did not
# cache, and disc effects are only cached for equipped discs - so the production cave "LoadInGameCommonEffect ->
# LoadEffect(SkillTrapMaster[40001].effectPath)" preloads whatever is served here with the common effects. Hyper
# Bomb's explosion (its aed timeline Effect clip, kicked with trigger 1 like our SyncEffect) is ef_ds_0037.
# blow_off: BatAbilityParameter..ctor copies DiscSkillParameter.BlowOffInfo (= the SkillBlowOff row of 40001) into the
# ability and its damage info carries it, so the blast launches victims exactly like Hyper Bomb (same _KNOCK_UP row).
BAT_BOMB_TRAP = {"trapType": 10, "duration": 2.0, "radius": 6.0, "effectValue": 0.0, "interval": 0.5,
                 "effectPath": "effect/ds/ef_ds_0037/ef_ds_0037", "blow_off": _KNOCK_UP}

# Disc TRAP skills (skillActionType 7) by disc card text. Bomb/turret effects carried by damage use trigger 1
# (ReceiveDamage); trigger 5 is only evaluated by ConditionTrapAction, not BombTrapAction/TurretTrapAction.
# Knock-away = a masters_skill_blow_off row (SkillBlowOffMaster.GetDataFromSkillId, read by DiscSkillParameter..ctor and
# attached to the skill's damage); directionType 1 Up, 2 Down, 3 Press, 4 AttackDirection.
# Bombs/turrets launch (Up): AttackDirection is victim - attacker position (PlayerStateBlowOff.BeginAction), and a
# trap's attacker is the kicker who placed it, so "away" would point away from the owner, not from the blast.
_B = {"trapType": 7, "duration": 20.0, "effectValue": 0.0, "interval": 0.0, "blow_off": _KNOCK_UP}  # bomb, knock away
_T = lambda interval, dur=15.0: {"trapType": 3, "duration": dur, "effectValue": 0.0, "interval": interval}  # turret
DISC_TRAPS = {
    # Liberwolf: massive damage area 8 s. Dot ticks = setter attack x effectValue (DotConditionActionInfo..ctor), card
    # says coef 1.0 x19 -> 19 ticks of 1.0 over 8 s
    10031: {"trapType": 8, "duration": 8.0, "effectValue": 0.0, "interval": 0.42,
            "conditions": [(COND_DOT, 8.0, 0.42, 1.0, TRIGGER_ENTER_ENEMY_TRAP)]},
    10036: _B, 10037: _B, 10038: _B, 10039: _B, 10040: _B,                                       # Hyper/Booby Bomb, R/G/B Mine
    10041: {**_B, "blow_off": None, "conditions": [(COND_PARALYSIS, 3.0, 0.0, 1.0, TRIGGER_RECEIVE_DAMAGE)]},   # Hedgefish: bomb + paralyse
    10042: _T(0.4), 10044: _T(0.4),                                                              # Killer Billet, Raging Bulldog: ultra fast
    10043: _T(1.0), 10046: _T(1.0),                                                              # Septicopter, Robo Turret: medium
    10049: {**_B, "blow_off": None, "conditions": [(COND_SILENT, 8.0, 0.0, 1.0, TRIGGER_RECEIVE_DAMAGE)]},      # Aerojammer: bomb + skill seal 8 s
    10050: {"trapType": 1, "duration": 10.0, "effectValue": 0.3, "interval": 0.0},              # Snazzy Snail: greatly slows 10 s
    # Jack o' Lantern: bomb + poison 60 % of max HP over 10 s (Poison ticks = target MaxHP x effectValue)
    10082: {**_B, "conditions": [(COND_POISON, 10.0, 1.0, 0.06, TRIGGER_RECEIVE_DAMAGE)]},
    10086: {"trapType": 8, "duration": 8.0, "effectValue": 0.0, "interval": 1.0,               # Pranky Pumpkin: damage area + ally regen 8 %/s
            "conditions": [(COND_DOT, 8.0, 1.0, 0.4, TRIGGER_ENTER_ENEMY_TRAP), (COND_REGENERATION, 8.0, 1.0, 0.08, TRIGGER_ENTER_ALLY_TRAP)]},
    10097: {**_B, "blow_off": None, "conditions": [(COND_STUN, 2.0, 0.0, 1.0, TRIGGER_RECEIVE_DAMAGE)]},        # Starnova: bomb + stun
    10105: {**_T(1.0, 8.0), "conditions": [(COND_ATTACK_RATE, 5.0, 0.0, 0.7, TRIGGER_RECEIVE_DAMAGE)]},  # Dynaduck: turret 8 s, attack down
    10114: {**_B, "duration": 30.0},                                                             # Glass Bomb: stealth bomb 30 s
    10128: {**_T(0.6), "conditions": [(COND_SILENT, 1.0, 0.0, 1.0, TRIGGER_RECEIVE_DAMAGE)]},  # Hellfire Crow: fast turret, seals 1 s
    10134: {**_T(3.0), "blow_off": _KNOCK_UP},                                                  # Princess Izana: 15 s turret, 5 shots at 0/3/6/9/12; knocks away
}

# Disc MOVE skills. AutoMoveSkillAction (skillActionType 22) does nothing by itself: OnBeginForceMove only calls
# player.AcceptCondition(GetSkillConditionInitInfo(deckIndex, trigger 3)) and OnUpdateAction ends the skill the moment
# ConditionActionController.HasConditionActionSkill is false - so without a SkillCondition row of type 30 (AutoMove)
# the kicker just plays the pose and stops. AutoMoveConditionActionInfo..ctor: OverrideSpeed = Param.GetSpeed(1) *
# effectValue, acceleration fixed 7.5, ends after `duration` or when the player is interrupted (damage / stun). The
# backwards shots of Pyronkey / Spunkle / Tigre are the four rear bullets in their APK timeline (bullets fired
# while the dash runs), so nothing extra is served for them. VerticalLoopSkillAction (21, Cycrane) needs no move row
# (the loop is hard-coded: 420 deg/s, radius 2.5) - only its on-hit condition.
# On-hit status effects: PlayerCharacter.ApplyCondition(DamageInfo) applies the attacker skill's SkillCondition rows
# with trigger 1 (ReceiveDamage) to whoever the skill's collider/bullet hits.
DISC_MOVES = {
    10027: {},                                                                                   # Pyronkey: ShotAttack back-shot (see DISC_ACTION_TYPE_OVERRIDES)
    10063: {"duration": 5.0, "speed": 2.0},                                                     # Leeta: speed up, auto forward 5 s
    10079: {},                                                                                   # Spunkle: ShotAttack back-shot
    10083: {"duration": 5.0, "speed": 3.0},                                                     # Stray Phantom: hyper speed 5 s
    10091: {"duration": 4.0, "speed": 2.0},                                                     # Great Glidears: speed up 4 s
    10112: {"conditions": [(COND_PARALYSIS, 3.0, 0.0, 1.0, TRIGGER_RECEIVE_DAMAGE)]},           # Tigre: ShotAttack back-shot + paralysis
    10122: {"duration": 4.0, "speed": 2.0},                                                     # Gusty Glidears: speed up 4 s
    10026: {"conditions": [(COND_PARALYSIS, 2.0, 0.0, 1.0, TRIGGER_RECEIVE_DAMAGE)]},           # Cycrane: loop, small dmg + paralyse
}

# Every other disc effect, straight from the card texts (docs/disc_cards.json), keyed by skill id. Verified data paths:
#  * heals = [(SkillHealType, coefficient)]. SupportSkillAction.Heal walks every SkillHeal row of the skill:
#      1 Hp          floor(MaxHP x coef) to self / all allies (ActionMaster _targetCount decides who)
#      2 BoostGauge  coef x 120 boost points (HealBoostPoint; the "boost energy recovery" discs)
#      5 Condition   clears the healable status effects (HealCondition)
#    WarpSkillAction.EndFinish heals MaxHP x coef of the FIRST row on arrival (the warp+heal discs).
#    PlayerCharacter.ApplyHealOnDamage reads the first row of the skill that just dealt damage when its type
#    IsExecuteOnDamage: 3 HpDrain = heal coef x damage dealt, 4 SpDrain = steal min(coef x 100, SP) special gauge.
#  * conditions = [(type, duration, interval, effectValue, trigger)]. trigger 3 Execute = on the caster / allies at cast
#    (SupportSkillAction.AddCondition, WarpSkillAction.EndFinish), trigger 1 ReceiveDamage = on whoever the hit lands on
#    (the rows travel inside the DamageInfo). Rates (1 AttackRate, 2 SpeedRate, 4 DamageRate = incoming damage
#    multiplier, ConditionActionController.UpdateParameter; 31 BoostHealSpeedRate) are multipliers; Poison (5) ticks
#    target MaxHP x value every `interval`; Regeneration (11) heals MaxHP x value per tick; 6 Paralysis / 8 Stun /
#    9 Silent ignore the value; 32 SpecialSkillGaugeRate removes (1 - value) x MaxSP once.
#  * blow_off / pull_in: SkillBlowOff / SkillPullIn rows (see the presets above).
_ATK_S, _ATK_M, _ATK_L = 1.1, 1.2, 1.3
_DEF = lambda cut, secs: (COND_DAMAGE_RATE, secs, 0.0, round(1.0 - cut, 2), TRIGGER_EXECUTE)
_HIT = lambda ctype, secs, value=1.0, interval=0.0: (ctype, secs, interval, value, TRIGGER_RECEIVE_DAMAGE)
_POISON = lambda total, secs=10.0: _HIT(COND_POISON, secs, round(total / secs, 3), 1.0)
DISC_EFFECTS = {
    # --- ATK(Rush) ---
    10001: {"blow_off": _RUSH_PURSUIT},                                       # Leorex: high-speed dash pursuit
    10013: {"blow_off": _RUSH_PURSUIT},                                       # Boarush: dash pursuit
    10014: {"blow_off": _RUSH_PURSUIT},                                       # Propedile: dash pursuit
    10096: {"blow_off": _RUSH_PURSUIT, "conditions": [_HIT(COND_ATTACK_RATE, 8.0, 0.7)]},  # Combat Turtle: pursuit + attack down 8 s
    10118: {"blow_off": _RUSH_PURSUIT},                                       # Airy: dash pursuit
    10108: {"heals": [(4, 0.2)]},                                              # Dragarmr: pierce + 20 % SS gauge on hit
    10132: {"heals": [(3, 1.0)]},                                              # Hellark: pierce + recovers 100 % of dealt damage
    # 10010 Giamoth / 10011 Jet Shark / 10012 Rush Blade / 10089 Assault Lance: pierce + shield break = no rows
    # --- ATK(Long Distance) ---
    10002: {"conditions": [_HIT(COND_SPEED_RATE, 5.0, 0.6)]},                 # Geckosaurus: speed down 5 s
    10007: {"conditions": [_HIT(COND_ATTACK_RATE, 8.0, 0.7)]},                # Junk Bullet: attack down 8 s
    10008: {"conditions": [_HIT(COND_STUN, 2.0)]},                            # Jet Hammer: stun
    10009: {"blow_off": _KNOCK_AWAY},                                          # Flamizaurus: knocks away
    10028: {"blow_off": _KNOCK_FAR},                                           # Gastornis: knocks far away
    10032: {"blow_off": _KNOCK_AWAY},                                          # Horwhale
    10034: {"blow_off": _KNOCK_AWAY},                                          # Lapibit
    10035: {"blow_off": _KNOCK_AWAY},                                          # Armed Calis
    10045: {"conditions": [_HIT(COND_PARALYSIS, 3.0)]},                       # Scorpius: paralysis
    10047: {"conditions": [_HIT(COND_PARALYSIS, 3.0)]},                       # Mantallion: paralysis
    10051: {"conditions": [_POISON(0.4)]},                                     # Octoverse: poison 40 % / 10 s
    10052: {"conditions": [_HIT(COND_ATTACK_RATE, 8.0, 0.7)]},                # Morbeel: attack down 8 s
    10084: {"heals": [(3, 0.8)]},                                              # Vampire Bat: recovers 80 % of dealt damage
    10090: {"conditions": [_HIT(COND_SP_GAUGE_RATE, 1.0, 0.8)]},              # Slingshooter: target SS gauge -20 %
    10093: {"blow_off": _KNOCK_AWAY},                                          # Seanake: explosive bomb, knocks away
    10095: {"conditions": [_HIT(COND_PARALYSIS, 2.0)]},                       # Sybilidra: combo + paralysis
    10109: {"conditions": [_HIT(COND_SILENT, 5.0)]},                          # Magistork: seals skills 5 s
    10115: {"blow_off": _KNOCK_AWAY},                                          # Fyredramon: explosive, knocks away
    10116: {"blow_off": _KNOCK_AWAY},                                          # Aquamage: knocks away
    10123: {"heals": [(4, 0.02)]},                                             # Hell Saurer: 20-hit combo, up to 40 % SS gauge
    10127: {"blow_off": _KNOCK_AWAY},                                          # Ghostopolis: pierce + knocks away
    10129: {"conditions": [_POISON(0.3)]},                                     # Poison Bullet: poison 30 % / 10 s
    # --- ATK(Short Distance) --- (FrontAttack: no row = slam)
    10015: {"blow_off": _KNOCK_UP},                                            # Jack Upper: knocks upwards
    10016: {"blow_off": _SLAM},                                                # Walrock: slam
    10017: {"blow_off": _KNOCK_UP},                                            # Rhinot: knocks upwards
    10018: {"blow_off": _KNOCK_UP},                                            # Hatakidora: knocks upwards
    10019: {"blow_off": _SLAM},                                                # Yanchara: slam
    10088: {"blow_off": _SLAM},                                                # Hippograndus: slam
    10102: {"blow_off": _KNOCK_UP},                                            # Blox: massive damage, knocks upwards
    # 10107 Destronsil: shield break = no rows (FrontAttack without blow-off/heal is flagged IsShieldBreak)
    10125: {"blow_off": _SLAM, "conditions": [_HIT(COND_SP_GAUGE_RATE, 1.0, 0.7)]},  # Avasalama: slam + target SS gauge -30 %
    10135: {"blow_off": _SLAM, "heals": [(3, 1.0)]},                          # Wolfang: slam + recovers 100 % of dealt damage
    # --- ATK(Circumference) ---
    10021: {"conditions": [_HIT(COND_PARALYSIS, 3.0)]},                       # Shockhog: paralysis
    10025: {"conditions": [_HIT(COND_PARALYSIS, 3.0)]},                       # Deviathan: paralysis
    10029: {"blow_off": _KNOCK_UP},                                            # Fairy Lizard: knocks upwards
    10030: {"blow_off": _KNOCK_UP},                                            # Uber Beaver: knocks upwards
    10048: {"conditions": [_POISON(0.8)]},                                     # Rosmie: poison 80 % / 10 s
    10103: {"blow_off": _KNOCK_AWAY},                                          # Bunny Burner: knocks away
    10106: {"heals": [(3, 0.6)]},                                              # Draygon: recovers 60 % of dealt damage
    10120: {"blow_off": _KNOCK_AWAY},                                          # Foxy: knocks away
    10138: {"conditions": [_HIT(COND_STUN, 2.0)]},                            # Amun-Ra: stun
    # --- HEAL --- (coef = fraction of MaxHP; BoostGauge coef x 120 points)
    10033: {"heals": [(2, 0.4)]},                                              # Power Dog: medium boost energy recovery
    10065: {"heals": [(1, 0.5)]},                                              # Phoenora: 50 % all allies
    10066: {"heals": [(2, 0.6)]},                                              # Fairy Frog: large boost energy recovery
    10067: {"heals": [(1, 0.3)]},                                              # Cutiemander: 30 % all allies
    10069: {"heals": [(1, 0.6)]},                                              # Peacocktail: 60 %
    10070: {"heals": [(1, 0.3)]},                                              # Pico Leaf: 30 %
    10071: {"conditions": [(COND_REGENERATION, 10.0, 1.0, 0.10, TRIGGER_EXECUTE)]},   # Nurseroid: regen 100 % over 10 s
    10072: {"conditions": [(COND_REGENERATION, 10.0, 1.0, 0.05, TRIGGER_EXECUTE)]},   # Medical BOX: regen 50 % over 10 s
    10078: {"heals": [(2, 0.4)]},                                              # Boost Bottle (energy drink): medium, below the frogs
    10087: {"heals": [(1, 0.8)]},                                              # Sea Slugster: 80 %
    10110: {"conditions": [(COND_REGENERATION, 15.0, 1.0, 0.08, TRIGGER_EXECUTE)]},   # Heasel: regen 120 % over 15 s
    10117: {"heals": [(1, 0.2), (5, 1.0)]},                                    # Bullheader: 20 % all allies + cleanse
    10131: {"heals": [(2, 0.6)]},                                              # Puffy Frog: large boost energy recovery
    10133: {"heals": [(1, 0.25), (5, 1.0)]},                                   # Pegasia: 25 % + cleanse
    10136: {"conditions": [(COND_REGENERATION, 20.0, 1.0, 0.04, TRIGGER_EXECUTE)]},   # Solar Purr: all allies regen 80 % over 20 s
    # --- WARP --- (heal on arrival = MaxHP x first heal row; Killer Locust also gets its trigger-3 buff)
    10068: {"heals": [(1, 0.5)]},                                              # Neolumi: lowest-HP ally + 50 %
    10074: {"heals": [(1, 1.0)]},                                              # Arcolphin: start point + 100 %
    10076: {"heals": [(1, 0.5)]},                                              # Bond Gate: nearby ally + 50 %
    10104: {"heals": [(1, 0.3)]},                                              # Support-roid: lowest-HP ally + 30 %
    10113: {"conditions": [(COND_ATTACK_RATE, 5.0, 0.0, _ATK_M, TRIGGER_EXECUTE)]},   # Killer Locust: attack up 5 s
    10124: {"heals": [(1, 0.3)]},                                              # Principatus: most distant ally + 30 %
    # 10073 Start Gate / 10077 Assassin Gate: warp only
    # --- BUFF --- (trigger 3; ActionMaster _targetCount 4 = all allies, 1 = self)
    10053: {"conditions": [(COND_SPEED_RATE, 10.0, 0.0, _ATK_M, TRIGGER_EXECUTE)]},   # Antelocity: allies speed 10 s
    10055: {"conditions": [(COND_ATTACK_RATE, 15.0, 0.0, _ATK_M, TRIGGER_EXECUTE)]},  # Fierycoon: allies attack medium 15 s
    10056: {"conditions": [(COND_ATTACK_RATE, 20.0, 0.0, _ATK_S, TRIGGER_EXECUTE)]},  # Buffbox: allies attack small 20 s
    10057: {"conditions": [_DEF(0.7, 10.0)]},                                  # Galaxy Shield: -70 % damage 10 s
    10058: {"conditions": [_DEF(0.3, 15.0)]},                                  # Tetra Shield: allies -30 % 15 s
    10059: {"conditions": [(COND_ATTACK_RATE, 15.0, 0.0, _ATK_L, TRIGGER_EXECUTE)]},  # Gigaphone: attack large 15 s
    10060: {"conditions": [(COND_ATTACK_RATE, 30.0, 0.0, _ATK_S, TRIGGER_EXECUTE)]},  # Fightgaroo: attack small 30 s
    10061: {"conditions": [_DEF(0.5, 15.0)]},                                  # Crabshiedion: -50 % 15 s
    10062: {"conditions": [_DEF(0.3, 25.0)]},                                  # Shell Kabuto: -30 % 25 s
    10064: {"conditions": [(COND_SPEED_RATE, 10.0, 0.0, _ATK_S, TRIGGER_EXECUTE)]},   # Spewdor: speed small 10 s
    10075: {"conditions": [(COND_BOOST_HEAL_SPEED_RATE, 10.0, 0.0, 2.0, TRIGGER_EXECUTE)]},  # Skybeast: allies boost recovery large 10 s
    10085: {"conditions": [(COND_ATTACK_RATE, 10.0, 0.0, _ATK_M, TRIGGER_EXECUTE),
                           (COND_SPEED_RATE, 10.0, 0.0, _ATK_M, TRIGGER_EXECUTE)]},   # Fever Candy: attack + speed medium 10 s
    10092: {"conditions": [(COND_BOOST_HEAL_SPEED_RATE, 12.0, 0.0, 2.0, TRIGGER_EXECUTE)]},  # Starion: boost recovery large 12 s
    10094: {"conditions": [_DEF(0.5, 3.0)]},                                   # Flash Barrier: -50 % 3 s
    10101: {"conditions": [_DEF(0.7, 8.0)]},                                   # Dhaluma: allies -70 % 8 s
    10119: {"conditions": [_DEF(1.0, 5.0)]},                                   # Megadeer: -100 % 5 s
}


def _emit(sid: int, x: dict, nid: int, cond: list, heal: list, blow: list, pull: list) -> int:
    for ctype, duration, interval, value, trigger in x.get("conditions", []):
        cond.append({"id": nid, "skillId": sid, "conditionType": ctype, "duration": duration,
                     "interval": interval, "effectValue": value, "triggerType": trigger})
        nid += 1
    for heal_type, coef in x.get("heals", []):
        heal.append({"id": nid, "skillId": sid, "skillHealType": heal_type, "coefficient": float(coef)})
        nid += 1
    if x.get("blow_off"):
        bo = x["blow_off"]
        blow.append({"id": nid, "skillId": sid, **_blow_off_row(bo)})
        nid += 1
    if x.get("pull_in"):
        pi = x["pull_in"]
        pull.append({"id": nid, "skillId": sid, "distance": pi["distance"], "speed": pi["speed"]})
        nid += 1
    return nid


def skill_tables(skills: list) -> dict[str, list]:
    cond, heal, blow, pull, trap = [], [], [], [], []
    nid = 1
    for s in skills:
        sid, cat = s["id"], s.get("skillCategoryType", CAT_ATTACK)
        if sid in KICKER_SKILL_EXTRAS:
            x = KICKER_SKILL_EXTRAS[sid]
            nid = _emit(sid, x, nid, cond, heal, blow, pull)
            if "trap" in x:
                t = x["trap"]
                trap.append({"id": nid, "skillId": sid, "trapType": t["trapType"], "duration": t["duration"],
                             "radius": t["radius"], "effectValue": t["effectValue"], "interval": 0.0, "executeSeId": 0,
                             "effectPath": "", "screenEffectPath": ""})
                nid += 1
            continue
        if sid == BAT_BOMB_SKILL_ID:
            t = BAT_BOMB_TRAP
            trap.append({"id": nid, "skillId": sid, "trapType": t["trapType"], "duration": t["duration"],
                         "radius": t["radius"], "effectValue": t["effectValue"], "interval": t["interval"],
                         "executeSeId": 0, "effectPath": t["effectPath"], "screenEffectPath": ""})
            nid += 1
            nid = _emit(sid, t, nid, cond, heal, blow, pull)
            continue
        if cat == CAT_TRAP or s.get("skillActionType") == 7:  # Trap actions need a TrapInfo or GetSkillAction throws
            # Trap.Initialize picks the action class from trapType: 7 Bomb (BombTrapAction, a summon body that explodes on
            # the first enemy in range; explosion collider/hit/bullet come from the APK ActionMaster), 3 Turret
            # (TurretTrapAction, a summon that shoots the skill's bullet every `interval` s at enemies in range),
            # 8 Condition (ConditionTrapAction, an area applying this skill's SkillCondition rows with trigger 5/4 to
            # enemies inside, every `interval` s), 1 Slow. Bodies are disc summons (Summon row modelId = id). Radius and
            # duration come from the APK sensor collider clip (DiscSkillParameter..ctor rebuilds the TrapInfo from it),
            # the served radius/duration are only the fallback. Table below = the disc card texts.
            t = DISC_TRAPS.get(sid, {"trapType": 1, "duration": 8.0, "effectValue": 0.5, "interval": 0.0})
            trap.append({"id": nid, "skillId": sid, "trapType": t["trapType"],
                         "duration": t["duration"], "radius": float(s.get("range", 5.0)), "effectValue": t["effectValue"],
                         "interval": t["interval"], "executeSeId": 0, "effectPath": "", "screenEffectPath": ""})
            nid += 1
            nid = _emit(sid, t, nid, cond, heal, blow, pull)
            continue
        if sid in DISC_MOVES:
            m = DISC_MOVES[sid]
            if "speed" in m:
                cond.append({"id": nid, "skillId": sid, "conditionType": COND_AUTO_MOVE, "duration": m["duration"],
                             "interval": 0.0, "effectValue": m["speed"], "triggerType": TRIGGER_EXECUTE})
                nid += 1
            nid = _emit(sid, m, nid, cond, heal, blow, pull)
            continue
        if sid in DISC_SKILLS_WITH_SENSOR_COLLIDER:  # sensor collider on a non-trap skill: TrapInfo is still read
            trap.append({"id": nid, "skillId": sid, "trapType": 9, "duration": 14.0, "radius": 7.0,
                         "effectValue": 0.0, "interval": 0.5, "executeSeId": 0, "effectPath": "", "screenEffectPath": ""})
            nid += 1
        if sid in DISC_EFFECTS:
            nid = _emit(sid, DISC_EFFECTS[sid], nid, cond, heal, blow, pull)
        elif cat == CAT_HEAL:
            print(f"skill {sid}: HEAL disc without a DISC_EFFECTS entry -> 30 % MaxHP placeholder")
            heal.append({"id": nid, "skillId": sid, "skillHealType": 1, "coefficient": 0.3})
            nid += 1
        elif cat == CAT_BUFF:
            print(f"skill {sid}: BUFF disc without a DISC_EFFECTS entry -> AttackRate x1.2 placeholder")
            cond.append({"id": nid, "skillId": sid, "conditionType": COND_ATTACK_RATE, "duration": 10.0,
                         "interval": 0.0, "effectValue": 1.2, "triggerType": TRIGGER_EXECUTE})
            nid += 1
    return {"masters_skill_condition.json": cond, "masters_skill_heal.json": heal,
            "masters_skill_blow_off.json": blow, "masters_skill_pull_in.json": pull,
            "masters_skill_trap.json": trap}


def guardian_skill_tables(skills: list) -> dict[str, list]:
    """SkillCollision / SkillHit rows for the guardian turrets (Skill rows with skillType 3 = Guardian.skillId).

    NPCSkillParameter..ctor -> SetCollisionInitInfos / SetAttackHitInfos read these two served masters by skillId
    (TMasterBase.Find); disc and kicker skills get their colliders from the APK ActionMaster instead and never
    look here. Without the collision row NPCGuardianParameter.InitializeBulletInfo dereferences a null
    CollisionInitInfo and GameManager.CreateGuardian kills the battle-loading coroutine (stuck on "Cargando").
    The eye laser is a WeaponAttackBulletInfo whose range comes from Guardian.laserLength; the collision row only
    shapes the bullet's hit sphere."""
    collision, hit = [], []
    nid = 1
    for s in skills:
        if s.get("skillType") != 3:
            continue
        collision.append({"id": nid, "skillId": s["id"], "collisionType": COL_SPHERE, "collisionHitType": COLHIT_ALL,
                          "hitLayer": HIT_LAYER_CHARACTERS, "radius": 1.5, "length": 0.0, "originCenterFlag": True,
                          "scaleX": 1.0, "scaleY": 1.0, "scaleZ": 1.0})
        hit.append({"id": nid, "skillId": s["id"], "commonHitEffectType": HIT_GUN_M, "hitSeId": 0, "effectPath": "",
                    "parentBone": BONE_COMMON, "offsetX": 0.0, "offsetY": 0.0, "offsetZ": 0.0, "transformType": 0,
                    "shakeVolume": 0.2, "knockBackFlag": False, "fixedDamage": 0})
        nid += 1
    return {"masters_skill_collision.json": collision, "masters_skill_hit.json": hit}


def summon_table(skills: list) -> list:
    # Which model variants exist per summon id (summon/sm_{id:D4}_{variant}.unity3d, 0 low / 1 high / 2 middle).
    variants: dict[int, set[int]] = {}
    for r in json.loads((CONFIG / "resources" / "catalog.json").read_text(encoding="utf-8"))["resources"]:
        m = re.search(r"summon/sm_(\d{4})_(\d)\.unity3d$", r.get("logicalName", ""))
        if m:
            variants.setdefault(int(m.group(1)), set()).add(int(m.group(2)))
    # SummonCharacter.Finish: a body whose SummonParameter.SummonCharacterType is AttackTrap (3) goes to
    # SummonStateType.NativeAction and stays as the trap (BombTrapAction / TurretTrapAction are SummonTrapActionBase,
    # the trap IS the body); any other type goes to Finish and the body despawns when the placement ends - the
    # "trap disappears after the placement animation" phone report of 2026-09-20.
    trap_summon_ids = {s.get("summonId") for s in skills
                       if s.get("skillActionType") == 7 and DISC_TRAPS.get(s["id"], {}).get("trapType") in (3, 7)}
    rows = []
    for s in skills:
        n = s.get("summonId") or 0
        if n:
            # Two different lookups must agree (verified on the emulator 2026-09-20, logcat NRE in
            # SummonCharacter.InitializeAsync when they did not):
            #   LoadManager.LoadDeckSummonModel preloads  summon/sm_{Skill.summonId:D4}_{Summon.LowModelId}
            #   PlayerStateSkill.CreateSummon instantiates summon/sm_{Summon.modelId:D4}_{Summon.LowModelId}
            # so Summon.modelId is the id of the summon whose MODEL to use (= its own id here), and
            # LowModelId = 2 if middleModelFlag else 0 must name a bundle that exists in the catalog.
            rows.append({"id": n, "modelId": n, "summonCharacterType": 3 if n in trap_summon_ids else 0,
                         "positionX": 0.0, "positionY": 0.0, "positionZ": 1.5, "seId": 0,
                         # enlarged disc-detail view ortho = gachaPositionZ * CanvasHeightRate; zeros render nothing
                         "gachaPositionX": 0.0, "gachaPositionY": -0.9, "gachaPositionZ": 2.6,
                         "gachaRotationX": 0.0, "gachaRotationY": 0.0, "gachaRotationZ": 0.0,
                         # disc-detail camera ortho size = PositionZ*0.8; zeros render nothing
                         "discDetailPositionX": 0.0, "discDetailPositionY": -0.9, "discDetailPositionZ": 2.6,
                         "discDetailRotationX": 0.0, "discDetailRotationY": 0.0, "discDetailRotationZ": 0.0,
                         "middleModelFlag": 0 not in variants.get(n, {0})})
    return rows


# Special skills. Each XxxSpecialSkillAction reads a different sub-table (see docs/COMBAT_MASTERS_FILL_IN.md §4),
# verified against the action classes in libil2cpp:
#   * Sword/Nunchaku/JapaneseSword (self), TwoGuns/Drone/Bowgun/Shield (allies), Gun/Bat (enemies) call AcceptCondition
#     with the kicker's SpecialSkillCondition rows -> triggerType 3 Execute.
#   * Hammer (Coco) drops an Inhale TRAP (SpecialSkillTrap, TrapType 5; the pull itself is InhaleConditionAction driven
#     by the APK KickerSpecialSkillMaster.Hammer + SpecialSkill.range) that applies the kicker's SpecialSkillCondition
#     rows to whoever enters (ConditionTrapAction: triggerType 5 EnterEnemyTeamTrap / 6 EnterMyTeamTrap / 7 All), then
#     the impact is a DamageCollisionData from SpecialSkillCollision+Hit (+ SpecialSkillBlowOff). Without the trap row
#     Trap.Initialize NREs (TrapInitializeInfo.TrapInfo null) and nothing happens.
#   * Laser (Sid) is an EmptyDamageCollisionData cylinder from SpecialSkillCollision whose stay callback applies the
#     SpecialSkillCondition rows with triggerType 4 EnterTrap (ConditionInitializeInfo.Set(..., trigger 4)) - damage
#     must come from a Dot condition; there is no direct hit.
#   * PunchGlove (Diatrius) drops a SpecialSkillTrap at the end of the cut (type still unconfirmed by the user).
#   * ThrowingStar/RocketLauncher fire a SpecialSkillBullet (collision/hit for the impact).
# Condition tuples: (conditionType, duration, interval, effectValue[, triggerType]).
# Keyed by weaponType; values from the kickers' special-skill descriptions (masters_kicker_detail.json).
# Tsubame's special. SwordSpecialSkillAction.ExecuteSkillEffect applies the trigger-3 rows during the cut-scene (the
# SpecialSkill.duration it loads there is discarded), and BlowoffColliderConditionAction only spawns the tornado once
# the player has left PlayerStateSpecialSkill (state 13), with lifetime = condition duration - elapsed time. The cut
# therefore eats into the tornado: a 10 s row gave ~6 s in play (play-test 2026-10), i.e. the cut takes ~4 s. The
# condition rows are served for ACTIVE + CUT seconds so the tornado and the speed buff last ACTIVE seconds after it.
TSUBAME_SS_ACTIVE_SECONDS = 10.0
TSUBAME_SS_CUT_SECONDS = 4.0
_TSUBAME_SS_CONDITION_SECONDS = TSUBAME_SS_ACTIVE_SECONDS + TSUBAME_SS_CUT_SECONDS
SPECIAL_SKILL_DATA = {
    # Tsubame: speed +20 % and a tornado around her (BlowoffColliderConditionAction builds a DamageCollisionData
    # from SpecialSkillCollision + GetSpecialSkillDamageInitInfo; the knock-up itself is the SpecialSkillBlowOff row)
    WT_SWORD:          {"duration": TSUBAME_SS_ACTIVE_SECONDS,
                        "conditions": [(COND_SPEED_RATE, _TSUBAME_SS_CONDITION_SECONDS, 0.0, 1.2),
                                       (13, _TSUBAME_SS_CONDITION_SECONDS, 0.0, 1.0)],
                        # AllAndRehit: with All, CollisionBase.HitAll keeps every target in AlreadyHitColliders for the
                        # collider's whole life, so each enemy was launched once per special. With 2 a target that
                        # leaves the tornado is dropped from that list and is hit again on re-entry, rate-limited by
                        # BlowoffColliderConditionAction.IsIgnoreHit (1 s per target).
                        "collision": {"collisionType": COL_SPHERE, "radius": 4.0, "length": 0.0, "hitType": COLHIT_ALL_REHIT},
                        "blow_off": {"distance": 6.0, "speed": 20.0, "rigorTime": 0.5, "directionType": 1}},         # 1 = Up
    WT_TWO_GUNS:       {"duration": 10.0, "conditions": [(COND_REGENERATION, 10.0, 1.0, 0.05)]},                      # allies regen 5 %/s
    # Coco: tornado trap that sucks enemies in (Inhale condition on enter; speed/min range from the APK HammerMaster,
    # radius = SpecialSkill.range) then the hammer impact slams them down (collision + blow-off Down)
    WT_HAMMER:         {"duration": 5.0, "range": 100.0,
                        "trap": {"trapType": 5, "duration": 3.5, "radius": 100.0, "effectValue": 0.0},   # in-game the pull reached ~1/10 of this
                        "conditions": [(COND_INHALE, 3.5, 0.0, 1.0, TRIGGER_ENTER_ENEMY_TRAP)],
                        "collision": {"collisionType": COL_SPHERE, "radius": 8.0, "length": 0.0},
                        "blow_off": {"distance": 8.0, "speed": 30.0, "rigorTime": 1.0, "directionType": 2}},         # 2 = Down
    # Kite: one giant shuriken that one-shots everything in its path (SpecialSkillHit.fixedDamage - a coefficient
    # cannot do it, fixedDamage replaces the damage) and flies through walls (a bullet only sphere-casts against the
    # field when its hitLayer contains the Field layer, so drop bit 8); with fixedDamage >= 1 a guardian instead loses
    # the APK KickerSpecialSkillMaster.ThrowingStar._guardianDropCrystalCount crystals
    WT_THROWING_STAR:  {"duration": 5.0, "fixedDamage": ONE_SHOT_FIXED_DAMAGE,
                        # distance well past any map edge (the star must fly off the arena, not stop mid-air at 60 u) and
                        # BulletEndType.FadeOut so the effect does not linger where it ends
                        "bullet": {"distance": 300.0, "speed": 25.0, "homingAngle": 0.0, "actionType": 0, "endType": 1},
                        "collision": {"collisionType": COL_SPHERE, "radius": 6.0, "length": 0.0, "hitLayer": HIT_LAYER_CHARACTERS & ~(1 << 8)}},
    # Owlbert: DroneSpecialSkillAction.ExecuteSkillEffect applies the trigger-3 condition to every ally; the one that
    # spawns the escort drone and lays smog clouds along the ally's path is Smog (18, SmogConditionAction: cloud
    # radius = this special's TrapInfo.Radius, next cloud every radius+1 units, SendAddTrap of trapType 6). Players
    # inside a cloud then get SmogProtection (20, trigger 6 EnterMyTeamTrap) / SmogDisturb (19, trigger 5). Serving
    # 20 on trigger 3 (2026-09-14..19) applied a no-op buff and no drone/smog ever appeared.
    # dsh-patch-2 (2026-10-10): the `interval` column of the SpecialSkillTrap row is now the smog PATCH SPACING in
    # world units: a production cave in SmogConditionAction uses `S = TrapInfo.Interval` when S > 0, otherwise the
    # old 2 * TrapInfo.Radius. TrapInfo.Interval is otherwise read only by TurretTrapAction, never by the Smog trap
    # path, so Owlbert's row is free to use it. This generator still emits interval 0.0 for every trap, so rows keep
    # the old spacing unless the override/WebUI sets the column (the local test row 5 sets 3.0).
    WT_DRONE:          {"duration": 8.0, "range": 100.0,
                        "trap": {"trapType": 6, "duration": 8.0, "radius": 5.0, "effectValue": 0.0},
                        "conditions": [(18, 8.0, 0.0, 1.0), (20, 8.0, 0.0, 1.0, TRIGGER_ENTER_ALLY_TRAP),
                                       (19, 8.0, 0.0, 1.0, TRIGGER_ENTER_ENEMY_TRAP)]},                              # 18 Smog trail on allies, 20/19 inside the clouds
    # Pitophy: RocketLauncherSpecialSkillAction.PlayMuzzleEffect instantiates BulletInfo.Path (= this resourcePath) as the
    # missile SPFX and kicks its triggers; with "" every shot NRE'd in EffectManager and no missile was ever spawned.
    # RocketLauncherSpecialSkillAction.SetTarget only locks enemies within SpecialSkill.range AND in the camera frustum
    # (up to 12); shots with no target get -1 and fly straight, so range must cover the missile's 60 u flight.
    # Each missile (BulletRocketLauncherSkillAction.OnInitialize) applies this special's trigger-3 condition to its target
    # and then steers by RocketLauncherTargetConditionAction (29) found on that target; without the row the missile
    # never homes. duration = how long the lock mark lives (>= flight time).
    WT_ROCKET:         {"duration": 6.0, "range": 60.0, "conditions": [(29, 6.0, 0.0, 1.0)],
                        "bullet": {"distance": 60.0, "speed": 45.0, "homingAngle": 90.0, "actionType": 1,
                                                   "resourcePath": "effect/ss/ef_ss_006_001/ef_ss_006_001"},
                        "collision": {"collisionType": COL_SPHERE, "radius": 1.5, "length": 0.0, "hitType": 0}},
    WT_GUN:            {"duration": 5.0, "conditions": [(16, 5.0, 0.0, 1.0)]},                                        # enemies Prison
    # Diatrius: Condition trap (8) - enemies inside are pulled to the ground and kept there while airborne
    # (Gravity 21, GravityConditionAction : IConditionMovePosition); the trap removes it again on exit
    WT_PUNCH_GLOVE:    {"duration": 6.0, "trap": {"trapType": 8, "duration": 6.0, "radius": 1000.0, "effectValue": 0.0},  # in-game the trap reached ~1/100 of this
                        "conditions": [(COND_GRAVITY, 6.0, 0.0, 1.0, TRIGGER_ENTER_ENEMY_TRAP)]},
    WT_BOWGUN:         {"duration": 12.0, "conditions": [(COND_SPEED_RATE, 12.0, 0.0, 1.2), (28, 12.0, 0.0, 1.3)]},   # allies: move speed +20 %, attack speed +30 % (28 = AttackSpeedRate)
    # ShieldAll (27): IsProtectDamage blocks every non-shield-break hit while it lasts (LimitCount defaults to -1 =
    # unlimited); effectValue is multiplied into the target's DamageRate (ConditionActionController.UpdateParameter
    # 0x17C1DF4) and shield-break hits only use their fixed correction while DamageRate < 1 (AcceptDamageInfo
    # 0x13D36F0). The old 9999999 made Sonic Rush / pierce discs hit a shielded target for int.MaxValue. 0 = blocked.
    WT_SHIELD:         {"duration": 5.0, "conditions": [(27, 5.0, 0.0, 0.0)]},                                        # allies: shield 5 s
    WT_BAT:            {"duration": 8.0, "conditions": [(22, 8.0, 0.0, 1.0)]},                                        # enemies Confusion
    WT_NUNCHAKU:       {"duration": 15.0, "conditions": [(25, 15.0, 0.0, 1.0)]},                                      # self Panda
    WT_JAPANESE_SWORD: {"duration": 15.0, "conditions": [(34, 15.0, 0.0, 1.0)]},                                      # self OneShotKiller
    # Sid: giant beam through walls; everyone inside is paralysed and takes tick damage (Dot) while they stay in it
    WT_LASER:          {"duration": 6.0, "collision": {"collisionType": COL_CYLINDER, "radius": 15.0, "length": 60.0},
                        # TEST (2026-09-14): paralysis applied but the Dot ticks did no damage even after the
                        # CommonConditionHit fix. DotConditionAction.ExecuteIntervalAction skips the damage when
                        # target.IsInvincible(TrapSkill) is true, and PlayerCharacter.IsInvincible is state-based -
                        # suspect the paralysed state counts. Dot only for this test; Poison as a second candidate.
                        "conditions": [(COND_DOT, 6.0, 0.5, 0.15, TRIGGER_ENTER_TRAP),
                                       (COND_POISON, 6.0, 1.0, 0.1, TRIGGER_ENTER_TRAP)]},
}


def special_skill_tables(kickers: list) -> dict[str, list]:
    ss, hit, col, cond, bullet, trap, blow = [], [], [], [], [], [], []
    nid = 1
    for k in kickers:
        kid = k["kickerId"]
        sid = kid
        data = SPECIAL_SKILL_DATA.get(k.get("weaponType"), {})
        ss.append({"id": sid, "kickerId": kid, "duration": float(data.get("duration", 5.0)),
                   "coefficient": float(data.get("coefficient", 3.0)), "range": float(data.get("range", 10.0)), "finishTime": 1.0})
        hit.append({"id": sid, "specialSkillId": sid, "commonHitEffectType": HIT_COMMON_L, "hitSeId": 0,
                    "effectPath": "", "parentBone": BONE_COMMON, "offsetX": 0.0, "offsetY": 0.0, "offsetZ": 0.0,
                    "transformType": 0, "shakeVolume": 0.3, "knockBackFlag": True,
                    "fixedDamage": int(data.get("fixedDamage", 0))})
        c = data.get("collision", {"collisionType": COL_SPHERE, "radius": 10.0, "length": 0.0})
        # collisionHitType All (1) = penetrating bullet (BulletActionBase.HitCallback keeps flying after a field/character
        # hit); One (0) ends the bullet on its first hit. Missiles must not pass through walls.
        col.append({"id": sid, "specialSkillId": sid, "collisionType": c["collisionType"], "collisionHitType": c.get("hitType", COLHIT_ALL),
                    "hitLayer": c.get("hitLayer", HIT_LAYER_CHARACTERS), "radius": c["radius"], "length": c["length"], "originCenterFlag": True,
                    "scaleX": 1.0, "scaleY": 1.0, "scaleZ": 1.0})
        for ctype, duration, interval, value, *trigger in data.get("conditions", []):
            cond.append({"id": nid, "specialSkillId": sid, "conditionType": ctype, "duration": duration,
                         "interval": interval, "effectValue": value, "triggerType": trigger[0] if trigger else TRIGGER_EXECUTE})
            nid += 1
        if "bullet" in data:
            b = data["bullet"]
            bullet.append({"id": sid, "specialSkillId": sid, "distance": b["distance"], "speed": b["speed"], "resourcePath": b.get("resourcePath", ""),
                           "loopSeId": 0, "homingAngle": b["homingAngle"], "endType": b.get("endType", 0), "actionType": b["actionType"],
                           "removeOnOwnerDeadFlag": True})
        if "blow_off" in data:
            bo = data["blow_off"]
            blow.append({"id": sid, "specialSkillId": sid, **_blow_off_row(bo)})
        if "trap" in data:
            t = data["trap"]
            trap.append({"id": sid, "specialSkillId": sid, "trapType": t["trapType"], "duration": t["duration"],
                         "radius": t["radius"], "effectValue": t["effectValue"], "interval": 0.0, "executeSeId": 0,
                         "effectPath": "", "screenEffectPath": ""})
    return {"masters_special_skill.json": ss, "masters_special_skill_hit.json": hit,
            "masters_special_skill_collision.json": col, "masters_special_skill_condition.json": cond,
            "masters_special_skill_bullet.json": bullet, "masters_special_skill_blow_off.json": blow,
            "masters_special_skill_trap.json": trap}


def common_condition_hit_table() -> list:
    rows = []
    # Every condition that deals damage needs a row: CharacterBase.UnzipDamageInfo (AttackType Condition) copies
    # ConditionActionController.GetConditionHitInfo(conditionType) and NREs when the type is missing - Sid's Dot
    # ticks died that way (158 NREs in logcat 14Mon09 01:28).
    for i, ct in enumerate((COND_POISON, COND_PARALYSIS, COND_BURN, COND_STUN, COND_SILENT, COND_DOT, 10), start=1):
        rows.append({"id": i, "commonHitEffectType": HIT_COMMON_S, "hitSeId": 0, "effectPath": "",
                     "parentBone": BONE_COMMON, "offsetX": 0.0, "offsetY": 0.0, "offsetZ": 0.0,
                     "transformType": 0, "shakeVolume": 0.0, "knockBackFlag": False, "fixedDamage": 0,
                     "conditionType": ct})
    return rows


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--force", action="store_true", help="overwrite existing generated tables")
    ap.add_argument("--only", help="comma-separated table-name substrings to (re)write, e.g. special_skill (implies --force for them)")
    args = ap.parse_args()

    skills, _discs = remap_skill_ids(args.force)
    kickers = load("masters_kicker_parameter.json")

    tables: dict[str, list] = {}
    tables.update(weapon_tables(kickers))
    tables.update(skill_tables(skills))
    tables.update(guardian_skill_tables(skills))
    tables["masters_summon.json"] = summon_table(skills)
    tables.update(special_skill_tables(kickers))
    tables["masters_common_condition_hit.json"] = common_condition_hit_table()
    only = [x.strip() for x in args.only.split(",")] if args.only else None
    for name, rows in tables.items():
        if only is not None and not any(o in name for o in only):
            continue
        write(name, rows, args.force or only is not None)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
