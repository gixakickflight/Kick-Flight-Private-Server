"""Title-screen DIAG probes (2026-10-10, KF_DIAG=1 only): why TitleScene runs 12-15 times and why TitleView.Initialize NREs.

Symptoms (static analysis): on every launch logcat shows `D/Unity: TitleScene` 12-15 times within ~0.5 s and ~5 s later
`Colorful.TitleView.Initialize` (0x31C7814) throws NullReferenceException 12-14 times ~40 ms apart; the title logo
sprite (`ui/<lang>/title/title_logo`, loaded by Initialize via LoadManager.LoadSprite into `_secondTitle`, TitleView+0x38)
sometimes renders, sometimes is a white/black rectangle, sometimes missing. Production patches (patch-il2cpp-endpoints.py
~1038-1061) already bypass every explicit null check of Initialize, so the suspect is the engine icall
`CanvasGroup.set_alpha(1.0)` (0x31C7988) on a destroyed `_canvasGroup` (TitleView+0x28).

Hypothesis for the repeats (found statically, to be confirmed by 9400/9410/9420): `BootScene.ManagedUpdate` (0x15C9E1C)
calls `ColorfulManager.ChangeScene(SceneTypeExtensions.Name(1)=Title, ...)` EVERY frame (tail `b` at 0x15C9EC4) until the
scene actually changes. Each call starts a `ColorfulManager.<ChangeSceneAsync>d__46` coroutine whose state 0 does
`CrashReportManager.SetLog(name)` (= the `D/Unity: TitleScene` line) and `NetworkManager.CheckBackendAsync`; state 1 calls
`GRE.SceneManager.ChangeScene`, which returns immediately when `SceneManager._isChanging` (+0x58) is already set. So N frames
before the first backend answer => N log lines, but only the first one should really change scene.

Probes (all `KFDIAG v=N`; 9400-9499 were unused; the task asked for 9500-9549 but those are taken by kicker_diag_caves.py)
-------------------------------------------------------------------------------------------------------------------
  9400  BootScene.ManagedUpdate is about to call ColorfulManager.ChangeScene(Title) (hook 0x15C9E6C, one per frame).
  9410+s ColorfulManager.<ChangeSceneAsync>d__46.MoveNext entry, s = state: 9410 = first run (SetLog + CheckBackendAsync),
        9411 = resumed after the backend check (this is where SceneManager.ChangeScene is called). (hook 0x31EA4B0)
  9420+f SceneManager.ChangeScene reached its gate; f = SceneManager._isChanging (+0x58): 9420 = a scene change really starts,
        9421 = rejected as a duplicate. (hook 0x1CAC914)
  9430  TitleScene.PreBeginAsync entered (entry hook 0x31C3DDC); the NEXT line is the low 32 bits of its return address
        (signed int; resolve like an NRE LR, see AGENTS.md) = who started it. Existing probes 8459 / 8460+state (MoveNext)
        and 8500+state (SceneManager._ChangeSceneAsync) already bracket it.
  9440+b TitleView.Initialize, just before CanvasGroup.set_alpha (hook 0x31C797C): bit0 = TitleView alive (m_CachedPtr
        at Object+0x10 != 0), bit1 = _canvasGroup (+0x28) is a non-null managed ref, bit2 = _canvasGroup alive
        (m_CachedPtr != 0). 9447 = healthy; 9443 = canvasGroup destroyed (the set_alpha NRE); 9441 = managed null.
  9460  CanvasGroup.set_alpha returned without an exception (hook 0x31C798C). 9440+b lines minus 9460 lines = the NRE count.
  9470  TitleView.OnDestroy entered (entry hook 0x31C7DB8).
  9490+m LoadManager.<>c__DisplayClass57_0.<LoadSprite>b__0 (LoadSprite completion, hook 0x16EB024; EVERY sprite load, not
        only the logo - read the lines next to the 9440 ones): m = 1 sprite from GetCacheSprite non-null, +2 sprite alive,
        +4 target Image alive. 9497 = sprite assigned fine; 9494 = no sprite (cache miss / failed load) with a live
        Image; 9491/9493 with 9490-9493 = Image dead (assignment skipped by the op_Inequality guard).

Cave space: 0x30E1E6C-0x30E1F70 of the dead body of Firebase.Crashlytics.ExceptionHandler.getMessageParts (entry-stubbed
in every flavour by error_report_caves.py; error_report's DIAG probes end at 0x30E1E6C). Hooks are `b` + jump back; entry
hooks save x0/x30 around the logger; mid-function hooks sit after the prologue that saved x30 and save x0 where x0 is
live or may be assumed live. The logger preserves x1-x18, so x8-x10 are used freely (all dead at their sites).

    python.exe scripts/re/title_diag_caves.py [--disasm]   # prints the DIAG entries for patch-il2cpp-endpoints.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
from error_report_caves import LIB, LOG, asm, disasm, entry  # noqa: E402

HOST_BASE = 0x30E1E6C
HOST_END = 0x30E1F70

PRE_SAVE = "stp x0, x30, [sp, #-0x10]!"
PRE_REST = "ldp x0, x30, [sp], #0x10"

# (name, hook address, pristine word (hex, for the guard), cave source builder(back address))
HOOKS = []


def hook(name, addr, word):
    def deco(fn):
        HOOKS.append((name, addr, word, fn))
        return fn
    return deco


@hook("9400 BootScene.ManagedUpdate -> ChangeScene(Title)", 0x15C9E6C, "e0030032")  # mov w0, #1
def c9400(back):
    return f"""
mov w0, #9400
bl #{LOG:#x}
mov w0, #1                            ; displaced
b #{back:#x}
"""


@hook("9410+state ColorfulManager.<ChangeSceneAsync>.MoveNext", 0x31EA4B0, "681240b9")  # ldr w8, [x19, #0x10]
def c9410(back):
    return f"""
str x0, [sp, #-0x10]!
ldr w8, [x19, #0x10]                  ; displaced (state)
mov w0, #9410
add w0, w0, w8
bl #{LOG:#x}
ldr x0, [sp], #0x10
b #{back:#x}
"""


@hook("9420+_isChanging SceneManager.ChangeScene gate", 0x1CAC914, "c8624139")  # ldrb w8, [x22, #0x58]
def c9420(back):
    return f"""
str x0, [sp, #-0x10]!
ldrb w8, [x22, #0x58]                 ; displaced
mov w0, #9420
add w0, w0, w8
bl #{LOG:#x}
ldr x0, [sp], #0x10
b #{back:#x}
"""


@hook("9430 TitleScene.PreBeginAsync entry (+ raw LR)", 0x31C3DDC, "f44fbea9")  # stp x20, x19, [sp, #-0x20]!
def c9430(back):
    return f"""
{PRE_SAVE}
mov w0, #9430
bl #{LOG:#x}
ldr w0, [sp, #8]                      ; caller's LR (low 32 bits)
bl #{LOG:#x}
{PRE_REST}
stp x20, x19, [sp, #-0x20]!           ; displaced
b #{back:#x}
"""


@hook("9440 TitleView.Initialize before set_alpha", 0x31C797C, "00102e1e")  # fmov s0, #1.0
def c9440(back):
    return f"""
mov w9, #9440
ldr x10, [x19, #0x10]                 ; TitleView m_CachedPtr
cbz x10, t1
add w9, w9, #1
t1:
cbz x20, t3                           ; _canvasGroup managed null
add w9, w9, #2
ldr x10, [x20, #0x10]
cbz x10, t3                           ; destroyed
add w9, w9, #4
t3:
mov w0, w9                          ; x0 is dead here (the next instruction is mov x0, x20)
bl #{LOG:#x}
fmov s0, #1.0                         ; displaced
b #{back:#x}
"""


@hook("9460 set_alpha returned", 0x31C798C, "c00240f9")  # ldr x0, [x22]
def c9460(back):
    return f"""
mov w0, #9460
bl #{LOG:#x}
ldr x0, [x22]                         ; displaced
b #{back:#x}
"""


@hook("9470 TitleView.OnDestroy entry", 0x31C7DB8, "f44fbea9")  # stp x20, x19, [sp, #-0x20]!
def c9470(back):
    return f"""
{PRE_SAVE}
mov w0, #9470
bl #{LOG:#x}
{PRE_REST}
stp x20, x19, [sp, #-0x20]!           ; displaced
b #{back:#x}
"""


@hook("9490+m LoadSprite completion (sprite / image liveness)", 0x16EB024, "750e40f9")  # ldr x21, [x19, #0x18]
def c9490(back):
    return f"""
ldr x21, [x19, #0x18]                 ; displaced (target Image); x0 = sprite from GetCacheSprite stays live
mov w9, #9490
cbz x0, u1
add w9, w9, #1
ldr x10, [x0, #0x10]
cbz x10, u1
add w9, w9, #2
u1:
cbz x21, u2
ldr x10, [x21, #0x10]
cbz x10, u2
add w9, w9, #4
u2:
str x0, [sp, #-0x10]!
mov w0, w9
bl #{LOG:#x}
ldr x0, [sp], #0x10
b #{back:#x}
"""


def build():
    """Assemble every cave back to back; return [(name, hook_addr, pristine_word, cave_addr, cave_bytes)]."""
    out, at = [], HOST_BASE
    for name, addr, word, fn in HOOKS:
        code = asm(fn(addr + 4), at)
        out.append((name, addr, word, at, code))
        at += len(code)
    return out, at


def main():
    lb = open(LIB, "rb").read()
    caves, end = build()
    assert end <= HOST_END, f"overflows the free window: {end:#x} > {HOST_END:#x}"
    blob = b"".join(c[4] for c in caves)
    for name, addr, word, at, code in caves:
        assert lb[addr:addr + 4].hex() == word, f"pristine guard {name} @{addr:#x}: {lb[addr:addr + 4].hex()} != {word}"
    out = ["    # ---- DIAG (KFDIAG 9400-9499): title-screen probes; generated by scripts/re/title_diag_caves.py ----",
           "    # 9400 BootScene per-frame ChangeScene, 9410+s ChangeSceneAsync state, 9420+f SceneManager gate, 9430 PreBeginAsync (+LR),",
           "    # 9440 TitleView.Initialize canvasGroup liveness, 9460 set_alpha ok, 9470 OnDestroy, 9490+m LoadSprite result.",
           entry("DIAG cave: title-screen probes 9400-9490 (free tail 0x30E1E6C-0x30E1F70 of the dead body of ExceptionHandler.getMessageParts)",
                 HOST_BASE, lb[HOST_BASE:end], blob)]
    for name, addr, word, at, code in caves:
        out.append(entry(f"DIAG hook: {name} -> b title-screen probe cave",
                         addr, lb[addr:addr + 4], asm(f"b #{at:#x}", addr)))
    print("\n".join(out))
    print(f"# title DIAG: {len(blob)} B at {HOST_BASE:#x}-{end:#x} ({HOST_END - end} B free)", file=sys.stderr)
    if "--disasm" in sys.argv:
        for name, addr, word, at, code in caves:
            print(f"# {name} hook {addr:#x} -> cave {at:#x}:", file=sys.stderr)
            print(disasm(code, at), file=sys.stderr)


if __name__ == "__main__":
    main()
