"""Boot-time "Communication Error" -> OK reloads Title (production, 2026-10-11): offline-then-OK recovery.

Symptom (emulator, server unreachable at boot): ~30 s after launch a "Communication Error / OK" dialog appears; after the
server is back, OK dismisses it but the client sits on the LOADING splash for ever, with no new request (TAP START dead).

What really happens (static, all addresses pristine 2.11.0 arm64; logs .local/run/logcat-2113b-offline*.txt):
* Not BootScene and not the once-latch of boot_once_cave.py. By the time the request fails BootScene is long gone:
  `GRE.SceneManager.<_ChangeSceneAsync>` has already run Boot's PreEnd/PostEnd/unload (probes 8500-8507) before
  `TitleScene.PreBeginAsync` starts (9430), so `BootScene.ManagedUpdate` (0x15C9E1C) can never run again. The latch is a
  BootScene-instance byte and nothing on the failure path reads it.
* Title's `<PreBeginAsync>d__10.MoveNext` state 1 (0x31C7060..) starts `ColorfulManager.InitializeOctoAsync` (0x31E7704;
  BootRequest through `NetworkManager.SendRequestAsync`, `<InitializeOctoAsync>d__30.MoveNext` 0x31EB4A8). The success
  callback (`<PreBeginAsync>b__0` 0x31C4834) sets a flag; on failure the flag stays 0 and state 2 (0x31C7180 -> 0x31C76D8)
  does `SceneManager.CancelFadeIn()` and returns false. `SceneManager.PreBeginSceneAsync` (0x1CB2258) ignores that result,
  so `_ChangeSceneAsync` still walks to its last state (8508-8510, clears `_isChanging`): Title is "loaded" but never
  initialised. Probe sequence offline: 9400 9410 9411 9421 8500-8507 9430 8460 8461 (30 s) 8462 8508 8509 8510, and nothing
  after the OK tap except `Object.Destroy` of the window frame (830, LR ...7e8 = `CommonWindowFrame.<CloseAnim>b__0`).
* The failure dialog is raised by `NetworkManager.DefaultFailed` -> `OpenErrorWindow` (0x31B2BE0). The stock design is that
  every dialog's OK restarts the boot flow with `ColorfulManager.ChangeScene("Title", fade=false, ...)`:
    - header status 2 (timeout, "Request aborted") -> `<>c.<OpenErrorWindow>b__127_2` 0x31B9C4C: ChangeScene(Title);
    - header status 3 (`Application.internetReachability == NotReachable`) -> `ErrorManager.OpenNetworkNotReachableWindow`
      `b__0` 0x17A9C9C: ChangeScene(Title) when its flag is set (it is);
    - header status 1 (any other transport error, empty/non-JSON body; the dialog then shows no error-code line, code 0)
      -> `OpenErrorWindowOnFailed` (0x31B7F98) default branch -> OK = `<>c__DisplayClass128_0.<OpenErrorWindowOnFailed>b__0`
      (0x31BBD80): `WindowManager.Close(...)`, then `if (code == 0x7cf) ChangeScene(Title)`; any other code just returns.
  The observed dialog (no code line, no `ChangeSceneAsync` = no 9410 after the tap, no "TitleScene" log line) is the third
  kind: stock simply leaves the client on the half-initialised Title.

Fix: in that OK handler, a non-0x7cf code also reloads Title while `ColorfulManager.IsInitializedOcto` (0x31E6D3C, the
bool at ColorfulManager+0x10 that `InitializeOctoAsync` sets on success) is still false, i.e. while the boot request has
not succeeded yet. After boot nothing changes (stock behaviour for in-game errors). One ChangeScene per OK tap, through the
same code the 0x7cf branch uses, so it does not reintroduce the duplicate-load problem and the gate bypass at 0x1CAC918 is
untouched (it is still needed by the battle-return flows; with a single request per tap it is irrelevant here).

Patch (in place + 12 B cave):
  0x31BBE54-0x31BBE5F  the three-instruction `ne` return block of b__0 (`ldp x29,x30 / ldp x20,x19 / ret`, only reached
                       by the `b.ne` at 0x31BBDD4; branch-swept in main()) becomes
                         bl  ColorfulManager.get_IsInitializedOcto        ; LR is saved in b__0's frame, epilogue reloads it
                         cbz w0, 0x31BBDD8                                 ; not initialised -> the 0x7cf path (ChangeScene(Title))
                         b   cave                                          ; initialised -> original epilogue
  cave 0x13ED98C-0x13ED998  that original epilogue (`ldp x29,x30,[sp,#0x10]; ldp x20,x19,[sp],#0x20; ret`), the free tail
                       of the entry-stubbed `NormalMatchingController.<CallbackBattleStartSuccess>b__46_0` body (the
                       12 B left over by boot_once_cave.py; verified free in all flavours by check_cave_hosts.py).
`bl` rule (AGENTS.md): not an entry hook, x30 is saved by the prologue (`stp x29,x30` at 0x31BBD84) and reloaded by the
epilogue, no tail call in between. NZCV is not live (the `ne` decision was taken by the branch that led here).

Expected emulator behaviour: normal boot unchanged (IsInitializedOcto is true after the first InitializeOcto; the handler
is not even reached). Offline: dialog, server back, OK -> 9410 9411 (+9421/9420) 8500.. 9430 and a new BootRequest, then the
normal title (9447 9460 9497 with DIAG). If the server is still down the dialog comes back 30 s later (one per OK tap).
No new KFDIAG probe values (9850-9869 stay free): the existing 9410/9430 lines after the tap are the evidence.

    python.exe scripts/re/title_retry_cave.py     # prints the expected/replacement hex for the patch table + sweep
"""
import os
import struct

from keystone import KS_ARCH_ARM64, KS_MODE_LITTLE_ENDIAN, Ks

HOOK = 0x31BBE54
HOOK_END = 0x31BBE60          # next function (b__5) starts here
GETTER = 0x31E6D3C            # ColorfulManager.get_IsInitializedOcto
RELOAD = 0x31BBDD8            # first instruction of the `code == 0x7cf` path (adrp x8, SceneTypeExtensions TypeInfo page)
NE_BRANCH = 0x31BBDD4         # b.ne 0x31BBE54 (the only way into the block)
CAVE = 0x13ED98C
CAVE_END = 0x13ED998
LIB = os.environ.get("KF_CLEAN_LIBIL2CPP") or os.path.join(
    os.path.dirname(__file__), "..", "..", ".local", "re", "lib", "arm64-v8a", "libil2cpp.so")

ks = Ks(KS_ARCH_ARM64, KS_MODE_LITTLE_ENDIAN)


def asm(src, base):
    return bytes(ks.asm(src, base)[0])


HOOK_SRC = f"""
bl #{GETTER:#x}
cbz w0, #{RELOAD:#x}
b #{CAVE:#x}
"""

CAVE_SRC = """
ldp x29, x30, [sp, #0x10]
ldp x20, x19, [sp], #0x20
ret
"""


def branch_target(pc, word):
    """b/bl/b.cond/cbz/cbnz/tbz/tbnz target, else None."""
    op = word >> 26
    if op in (0b000101, 0b100101):
        imm = word & 0x3FFFFFF
        imm -= (1 << 26) if imm & (1 << 25) else 0
        return pc + imm * 4
    if (word & 0xFF000010) == 0x54000000 or (word & 0x7E000000) == 0x34000000:
        imm = (word >> 5) & 0x7FFFF
        imm -= (1 << 19) if imm & (1 << 18) else 0
        return pc + imm * 4
    if (word & 0x7E000000) == 0x36000000:
        imm = (word >> 5) & 0x3FFF
        imm -= (1 << 14) if imm & (1 << 13) else 0
        return pc + imm * 4
    return None


if __name__ == "__main__":
    lib = open(LIB, "rb").read()
    hook = asm(HOOK_SRC, HOOK)
    cave = asm(CAVE_SRC, CAVE)
    assert len(hook) == HOOK_END - HOOK == 12 and len(cave) == CAVE_END - CAVE == 12
    assert lib[HOOK:HOOK_END] == cave, "the return block is not the plain epilogue the cave replays"
    assert branch_target(NE_BRANCH, struct.unpack_from("<I", lib, NE_BRANCH)[0]) == HOOK
    # nothing but the b.ne at NE_BRANCH may branch into the three replaced instructions
    inside = range(HOOK, HOOK_END, 4)
    hits = [pc for pc in range(0x1000000, 0x3400000, 4)
            if branch_target(pc, struct.unpack_from("<I", lib, pc)[0]) in inside]
    assert hits == [NE_BRANCH], [hex(h) for h in hits]
    print("hook @%#x (12 B): expected=%s" % (HOOK, lib[HOOK:HOOK_END].hex()))
    print("                  replacement=%s" % hook.hex())
    print("cave @%#x (12 B): expected=%s" % (CAVE, lib[CAVE:CAVE_END].hex()))
    print("                  replacement=%s" % cave.hex())
    print("incoming-branch sweep over the whole code window: only the b.ne at %#x" % NE_BRANCH)
