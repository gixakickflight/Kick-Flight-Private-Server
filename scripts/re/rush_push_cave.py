"""Rush-pursuit "push in front" production patches (arm64) in scripts/patch-il2cpp-endpoints.py.

Problem: rush PURSUIT discs (MoveAttack + SkillBlowOff row: 10001, 10013, 10014, 10096, 10118) entrain the hit target
(patch 1 of scripts/re/rush_pursuit_cave.py, `IsEntrainedBlowOff = true`). PlayerStateEntrained keeps the victim at
attacker.pos + Euler(0, angle, 0) * forward * radius, with `angle` (state +0x30) advancing 720 deg/s, so the victim
ORBITED the dasher for 0.5 s and was then blown off. Wanted: the victim rides in front of the dasher, in the dash
direction, until the dash ends.

Design (smallest safe change; stock entrainment is untouched):
  * Discriminator = `victim.+0x340 > 0` (PlayerCharacter attach timer). It is written ONLY by the
    AcceptAttachCharacter receiver 0x13E3348 (+0x340 = duration, SetParent victim -> dasher, SetIsSyncPosition off) and
    cleared by AcceptDetachCharacter 0x13E3624 / the Update countdown 0x13CBFE0 at expiry. AcceptAttachCharacter has a
    single caller: MoveAttackSkillAction.HitCallback 0x145AF44, i.e. exactly the pursuit hit. The only other source of
    entrainment (BlowoffColliderConditionAction.AddAttackCollision, tornado-type skills) never attaches, so +0x340 is 0
    and the stock orbit runs unchanged. The attach RPC is delivered to every client and every client keeps its own
    +0x340, so the discriminator is the same on the victim's owner and on the other peers (nothing new is serialised).
  * Patch P1 (orbit -> front): PlayerStateEntrained.UpdateAction 0x17D7474 `fadd s1, s1, s2` (angle += dt * 720;
    stored at 0x17D7478 into +0x30) -> `bl` cave. Cave: if victim.+0x340 > 0, angle = attacker.t.eulerAngles.y
    (attacker = state +0x28), else the original `fadd`. The state's own maths then place the victim at
    attacker.pos + attacker.forward * radius (radius lerps from the hit-time distance to 2.0 in 0.25 s) every frame,
    i.e. in front of the dasher (HitCallback's _forwardOnHit is the dasher's transform.forward = yaw of eulerAngles).
    The height offset lerp and the look-at-the-attacker rotation are untouched.
    Registers at the hook: x19 = state, s0 = start radius (+0x34, live until the Mathf.Lerp at 0x17D7484), s8 = min(t*4, 1)
    and s9 = dt (callee-saved, preserved by the callees), x0 = xzr (Lerp's MethodInfo arg) which the cave restores,
    x8/x1/s2/s3 dead. The cave saves x29/x30 and s0 around its two calls (get_t 0x1A7CA3C, get_eulerAngles 0x2789804,
    the same pair the original BeginAction uses), sets no flags that matter (next flag use is a fresh compare) and
    returns with `ret` (mid-function `bl` hook; UpdateAction saved x30 in its prologue and reloads it before `ret`).
  * Patch P2 (hold until the dash ends): PlayerStateEntrained.IsFinish 0x17D79DC `fmov s1, #0.5` (finish when elapsed
    >= 0.5 s) -> `bl` cave: s1 = 0.5, and when elapsed >= 0.5 AND victim.+0x340 > 0.5 s the threshold becomes 31.0.
    The attach lasts "dash remaining / pursuit speed + 0.5" (patch 2 of rush_pursuit_cave.py), so +0x340 > 0.5 means
    the dash is still running; at the dash end MoveAttackSkillAction calls AcceptDetachCharacter (+0x340 = 0) and
    IsFinish releases the victim to the usual final PlayerStateBlowOff nudge. Leaf cave, only s1/s2/x8 touched, s0 kept,
    IsFinish still reloads x30 from its frame (non-leaf function, `bl` hook mid-body).
  * Patch 1 of rush_pursuit_cave.py (CreateCollider w4 = 1) stays: it is what routes the victim into Entrained.

Sync: PlayerStateEntrained runs where the victim's state runs (its owner, +whoever mirrors the state); every peer parents
the victim to the dasher through the attach RPC (SetIsSyncPosition(false)), and the attacker's transform is synchronised, so
all of them derive the position from the dasher. Peers that only run the attach see the rigid attach offset (about one
body height in front); the owner sees radius 2.0 in front. Both are along the dash direction.

Cave space (no free window is large enough in one piece, so the code is chained with `b`). Hosts are dead bodies whose
entry is a `mov..; ret` stub in EVERY flavour (check_cave_hosts.py) and the gap is unused by any other patch in all 8
flavours (scripts/re/ gap scan, 2026-10-10); a whole-.so branch sweep finds only branches from inside the same dead
function into these windows:
  F1 0x1A2AC1C-0x1A2AC50 (52 B)  PhotonPropertyManagerBase<object>.CheckInitializeError   P1 part 1 (13 words, full)
  F3 0x159E32C-0x159E354 (40 B)  HomeSummonModelController.<LoadModelAsync>d__25.MoveNext  P1 part 2
  F2 0x31C49A4-0x31C49D4 (48 B)  TitleView.SetAllButtonActive                              P2
  F5 0x13CE7A4-0x13CE7C8 (36 B)  PlayerCharacter.UpdateIdleTypeRate tail                   DIAG only: P2 probe tail
KFDIAG probes (KF_DIAG=1 only): 9800 = pursuit push ran this frame (UpdateAction), 9801 = IsFinish held the entrainment
past the stock 0.5 s. (9800-9819 reserved for this feature.)

    python.exe scripts/re/rush_push_cave.py   # prints the patch entries (production, then DIAG variants)
"""
import os

from keystone import KS_ARCH_ARM64, KS_MODE_LITTLE_ENDIAN, Ks

LIB = os.environ.get("KF_CLEAN_LIBIL2CPP") or os.path.join(
    os.path.dirname(__file__), "..", "..", ".local", "re", "lib", "arm64-v8a", "libil2cpp.so")

LOG = 0x13BC348
GET_T = 0x1A7CA3C
GET_EULER = 0x2789804
HOOK_ANGLE = 0x17D7474   # fadd s1, s1, s2
HOOK_FINISH = 0x17D79DC  # fmov s1, #0.5
F1, F1_END = 0x1A2AC1C, 0x1A2AC50
F3, F3_END = 0x159E32C, 0x159E354
F2, F2_END = 0x31C49A4, 0x31C49D4
F5, F5_END = 0x13CE7A4, 0x13CE7C8

ks = Ks(KS_ARCH_ARM64, KS_MODE_LITTLE_ENDIAN)


def asm(src, addr):
    src = "\n".join(l.split("//")[0].rstrip() for l in src.splitlines())
    return bytes(ks.asm(src, addr)[0])


P1_A = f"""
ldr x8, [x19, #0x10]          // victim PlayerCharacter (PlayerStateBase.Player)
ldr s3, [x8, #0x340]          // attach timer: > 0 only for a MoveAttack pursuit victim
fcmp s3, #0.0
b.le orig
stp x29, x30, [sp, #-0x20]!
str s0, [sp, #0x10]           // start radius, live across the calls
ldr x0, [x19, #0x28]          // attacker character
mov x1, xzr
bl #{GET_T:#x}
mov x1, xzr
b #{F3:#x}
orig:
fadd s1, s1, s2               // displaced instruction: angle += dt * 720
ret
"""
P1_B = f"""
bl #{GET_EULER:#x}            // s1 = attacker yaw -> new angle
{{probe}}ldr s0, [sp, #0x10]
ldp x29, x30, [sp], #0x20
mov x0, xzr                   // Mathf.Lerp MethodInfo arg, as before the hook
ret
"""
P2 = """
fmov s1, #0.5
fcmp s0, s1                   // elapsed < 0.5: nothing to decide
b.lt out
ldr x8, [x19, #0x10]
ldr s2, [x8, #0x340]
fcmp s2, s1
b.le out                      // attach over / dash ended: stock behaviour
{hold}
out:
ret
"""


def build(diag):
    p1a = asm(P1_A, F1)
    if diag:
        # probe `bl LOG` needs its own address, so assemble piecewise
        p1b = (asm(f"bl #{GET_EULER:#x}\nmov w0, #9800", F3) + asm(f"bl #{LOG:#x}", F3 + 8)
               + asm("ldr s0, [sp, #0x10]\nldp x29, x30, [sp], #0x20\nmov x0, xzr\nret", F3 + 12))
        p2 = asm(P2.format(hold=f"b #{F5:#x}"), F2)
        p5 = (asm("mov x9, x30\nmov w0, #9801", F5) + asm(f"bl #{LOG:#x}", F5 + 8)
              + asm("mov x30, x9\nfmov s1, #31.0\nret", F5 + 12))
    else:
        p1b = asm(P1_B.replace("{probe}", ""), F3)
        p2 = asm(P2.format(hold="fmov s1, #31.0"), F2)
        p5 = b""
    return p1a, p1b, p2, p5


def main():
    so = open(LIB, "rb").read()
    hook_a = asm(f"bl #{F1:#x}", HOOK_ANGLE)
    hook_f = asm(f"bl #{F2:#x}", HOOK_FINISH)
    for diag in (False, True):
        p1a, p1b, p2, p5 = build(diag)
        assert len(p1a) == F1_END - F1 == 52, len(p1a)
        assert len(p1b) <= F3_END - F3 and len(p2) <= F2_END - F2 and len(p5) <= F5_END - F5
        print("DIAG" if diag else "PRODUCTION")
        for name, addr, rep in (("P1 part 1", F1, p1a), ("P1 part 2", F3, p1b), ("P2", F2, p2), ("P2 probe", F5, p5),
                                ("hook angle", HOOK_ANGLE, hook_a), ("hook finish", HOOK_FINISH, hook_f)):
            if rep:
                print(f"  {name:11s} @ {addr:#x} len {len(rep):2d} expected {so[addr:addr+len(rep)].hex()}\n"
                      f"{'':35s}replacement {rep.hex()}")


if __name__ == "__main__":
    main()
