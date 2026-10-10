"""BootScene requests ChangeScene(Title) once (production, 2026-10-11): title-screen root cause fix.

Root cause (see the 9400-9490 DIAG runs in AGENTS.md): `BootScene.ManagedUpdate` (0x15C9E1C) calls
`ColorfulManager.ChangeScene("Title")` EVERY frame until the scene really changes (~15 frames, the time
`NetworkManager.CheckBackendAsync` takes). Stock relies on the gate in `GRE.SceneManager.ChangeScene` (0x1CAC914,
`if (_isChangeScene) return;`) to drop all but the first one. Our production patch at 0x1CAC918 turns that gate into an
unconditional pass (it was added so a stale `_isChangeScene` could never block a later scene change), so all ~15
requests ran `_ChangeSceneAsync` -> `PreBeginAsync`: the Title scene was loaded ~15 times in a row, each load destroying
the TitleView of the previous one while its `TitleView.Initialize` / sprite loads were still pending (hence the
destroyed `_canvasGroup`, the sprite loads landing on dead Images and the white logo).

Fix: keep the gate bypass for every other caller, and make the one per-frame caller one-shot. Entry hook of
`BootScene.ManagedUpdate` (`b` + jump back to entry+4, rules in AGENTS.md): the first call latches a byte in the
BootScene instance itself (+0x47: BootScene's last field, `<IsAddSeOutGame>` bool, is at +0x40, so +0x41..+0x47 is
allocation padding that no field uses; a new BootScene instance starts with 0, so a second boot requests again) and
runs the original body; later frames `ret` immediately (the function is void and the hook sits before its prologue).
x8 is a scratch register at a void function's entry; NZCV is not live.

Cave window: 0x13ED970-0x13ED98C, the free tail of the entry-stubbed NormalMatchingController.<CallbackBattleStartSuccess>
b__46_0 body (0x13ED998 is where it ends; verified free in all flavours by scripts/re/check_cave_hosts.py).

    python.exe scripts/re/boot_once_cave.py     # prints the expected/replacement hex for the patch table
"""
import os

from keystone import KS_ARCH_ARM64, KS_MODE_LITTLE_ENDIAN, Ks

HOOK = 0x15C9E1C          # BootScene.ManagedUpdate entry: str x19, [sp, #-0x20]!
HOOK_WORD = "f30f1ef8"
CAVE = 0x13ED970
CAVE_END = 0x13ED998
LATCH = 0x47
LIB = os.environ.get("KF_CLEAN_LIBIL2CPP") or os.path.join(
    os.path.dirname(__file__), "..", "..", ".local", "re", "lib", "arm64-v8a", "libil2cpp.so")

ks = Ks(KS_ARCH_ARM64, KS_MODE_LITTLE_ENDIAN)


def asm(src, base):
    return bytes(ks.asm(src, base)[0])


CAVE_SRC = f"""
ldrb w8, [x0, #{LATCH:#x}]
cbnz w8, done
mov w8, #1
strb w8, [x0, #{LATCH:#x}]
str x19, [sp, #-0x20]!
b #{HOOK + 4:#x}
done:
ret
"""

if __name__ == "__main__":
    lib = open(LIB, "rb").read()
    code = asm(CAVE_SRC, CAVE)
    assert CAVE + len(code) <= CAVE_END, "cave overflows its window"
    assert lib[HOOK:HOOK + 4].hex() == HOOK_WORD
    print("cave @%#x (%d B): expected=%s" % (CAVE, len(code), lib[CAVE:CAVE + len(code)].hex()))
    print("                  replacement=%s" % code.hex())
    print("hook @%#x: expected=%s replacement=%s" % (HOOK, HOOK_WORD, asm(f"b #{CAVE:#x}", HOOK).hex()))
