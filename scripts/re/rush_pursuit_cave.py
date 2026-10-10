"""Source of the rush-disc "pursuit follow-through" production patches in scripts/patch-il2cpp-endpoints.py (arm64).

Pursuit discs = skillActionType 3 (MoveAttackSkillAction) with a SkillBlowOff row: 10001 Leorex, 10013 Boarush,
10014 Propedile, 10096 Combat Turtle, 10118 Airy. Without the row the same discs are "pierce + shield break" and
none of this runs (HitCallback leaves at `cbz x0` on get_BlowOffInfo 0x145A7F8; CanEntrained needs a BlowOffInfo).

Retail behaviour (pristine RE, 2026-10-05):
  * OnBeginForceMove 0x14586C0 sets _targetPos (+0x14C) = start + forward * EventItem.Distance (the normal dash end);
    OnUpdateForceMove 0x14588C4 drives the dasher along forward by |_targetPos - pos| (0 once it is behind).
  * MoveAttackSkillAction.HitCallback 0x145A43C, on a pursuit hit: _forwardOnHit (+0x178) = forward,
    s8 = +-|victim - dasher| + BlowOffInfo.Distance (fadd s8, s8, s12 at 0x145AC64), _targetPos = pos + forward*s8,
    _actionTime = elapsed + s8 / GetSpeed(1, BlowOff.Speed) + dt, then AcceptAttachCharacter(victim, offset,
    duration + 0.5) parents the victim to the dasher. So the dash stopped Distance past the victim, and the victim,
    parented AND in PlayerStateBlowOff (pushed Distance along victim - dasher), ended up to Distance ahead of it.
  * MoveAttackSkillAction.CreateCollider 0x1459AEC builds the DamageCollisionData with IsEntrainedBlowOff = false
    (`mov w4, wzr` at 0x1459CD0). DamageCollisionData.OnEnter copies it to DamageInitializeInfo.IsEntrainedBlowOff
    (+0x78, serialised by DamageInitializeInfo.Serialize), DamageInfo.Set 0x185B630 puts it on the BlowOffInfo, and
    PlayerCharacter.ApplyBlowOff 0x13D6CB8 -> CanEntrained 0x13D9430 (BlowOffInfo != null && IsEntrainedBlowOff &&
    the victim has no condition 13) picks PlayerStateEntrained (15) instead of PlayerStateBlowOff (6).
    PlayerStateEntrained (BeginAction 0x17D6E5C, UpdateAction 0x17D7364, IsFinish 0x17D79A0) keeps the victim at
    attacker.pos + an offset whose radius lerps to 2.0 within 0.25 s while it circles the attacker at 720 deg/s,
    for 0.5 s (or until the attacker dies / goes invisible), then hands the same damage info to PlayerStateBlowOff.
    Only BlowoffColliderConditionAction.AddAttackCollision passes w4 = 1 in retail.

Patches:
  1. CreateCollider 0x1459CD0 `mov w4, wzr` -> `mov w4, #1`: pursuit victims are entrained (held against the dasher)
     instead of blown off ahead of it. Pierce discs have no BlowOffInfo, so CanEntrained stays false for them.
  2. HitCallback 0x145AC64 `fadd s8, s8, s12` -> `bl CAVE`: s8 = max(0, dot(_targetPos - pos, _forwardOnHit)), the
     remaining distance of the ORIGINAL dash, so the re-targeted end is the normal end, the action time covers it at
     the pursuit speed and the attach lasts until it. BlowOff.Distance then only sizes the final nudge the victim gets
     when Entrained hands over to BlowOff (masters: _RUSH_PURSUIT in generate_combat_masters.py).
     At the hook v9/v10/v11 = dasher position (Transform.get_position at 0x145AC1C), x19 = this; the cave uses only
     s16-s21 (caller-saved, dead here: get_forward is called next), sets no flags and returns with `ret`
     (mid-function `bl` hook, HitCallback saved x30 in its prologue).
Follow-up (2026-10-10, scripts/re/rush_push_cave.py): PlayerStateEntrained's 720 deg/s orbit made the victims circle the
dasher. Two more caves (UpdateAction 0x17D7474, IsFinish 0x17D79DC), keyed on the MoveAttack attach timer
(PlayerCharacter +0x340 > 0), make the victim ride in FRONT of the dasher until the dash ends; stock entrainment is
untouched. Patch 1 below is still what routes the victim into PlayerStateEntrained.
Cave: the dead body of the entry-stubbed GameManager.<BeginAsync>b__1 (stub 0x1579940-0x1579948 "mov w0,#0; ret",
unconditional in production; body 0x1579948-0x157999C, only its own internal branches land in it).

    python scripts/re/rush_pursuit_cave.py   # prints the expected/replacement hex for the patch table
"""
import os

from keystone import KS_ARCH_ARM64, KS_MODE_LITTLE_ENDIAN, Ks

CAVE = 0x1579948
CAVE_END = 0x157999C
HOOK = 0x145AC64
COLLIDER_W4 = 0x1459CD0
LIB = os.environ.get("KF_CLEAN_LIBIL2CPP") or os.path.join(
    os.path.dirname(__file__), "..", "..", ".local", "re", "lib", "arm64-v8a", "libil2cpp.so")

SRC = """
ldr s16, [x19, #0x14c]        // _targetPos (OnBeginForceMove: start + forward * EventItem.Distance)
ldr s17, [x19, #0x150]        // (ldp s-pair offsets stop at 0xfc)
ldr s18, [x19, #0x154]
fsub s16, s16, s9             // - dasher position (v9..v11)
fsub s17, s17, s10
fsub s18, s18, s11
ldr s19, [x19, #0x178]        // _forwardOnHit (stored by HitCallback at 0x145A780)
ldr s20, [x19, #0x17c]
ldr s21, [x19, #0x180]
fmul s8, s16, s19
fmadd s8, s17, s20, s8
fmadd s8, s18, s21, s8
fmov s16, wzr
fmax s8, s8, s16              // remaining distance along the dash, never negative
ret
"""


def main() -> None:
    ks = Ks(KS_ARCH_ARM64, KS_MODE_LITTLE_ENDIAN)
    src = "\n".join(line.split("//")[0].rstrip() for line in SRC.splitlines())
    cave = bytes(ks.asm(src, CAVE)[0])
    assert CAVE + len(cave) <= CAVE_END, hex(CAVE + len(cave))
    hook = bytes(ks.asm(f"bl #{CAVE:#x}", HOOK)[0])
    w4 = bytes(ks.asm("mov w4, #1", COLLIDER_W4)[0])
    so = open(LIB, "rb").read()
    print("cave     @", hex(CAVE), "expected", so[CAVE:CAVE + len(cave)].hex(), "replacement", cave.hex())
    print("hook     @", hex(HOOK), "expected", so[HOOK:HOOK + 4].hex(), "replacement", hook.hex())
    print("entrain  @", hex(COLLIDER_W4), "expected", so[COLLIDER_W4:COLLIDER_W4 + 4].hex(), "replacement", w4.hex())


if __name__ == "__main__":
    main()
