"""Client error reporting caves (2026-10-10): in-match anomalies and managed exceptions -> /analysis/index.

Why
---
The retail client already collects two kinds of diagnostics and hands both to Firebase Crashlytics, which never
reaches us:

  B. In-match anomalies. `GameManager.StoreAnalysisException` (called from `PlayerCharacter.ResolveZombie`,
     `ResolveWarpFlag`, `AcceptWarp`, `ReconnectSharedInfo.Deserialize`) queues strings; `GameManager.EndAsync`
     flushes them through `GameManager.SetAnalysisException` (0x1575F94, "1:<text>//2:<text>...", <= 1024 chars)
     into `CrashReportManager.SetAnalysisException(msg, checkDuplicate=true)` (0x17CAE14), which dedupes with
     its `_exceptionHashSet` (+0x18), prefixes "[Analysis]" and calls `Crashlytics.LogException`.
     `AnalysisManager.SendPurchaseFailed` is the only other caller.
  C. Managed exceptions. `Firebase.Crashlytics.ExceptionHandler.HandleLog(message, stackTrace, type)`
     (0x30E1BD8) is the `Application.logMessageReceived` subscriber; for `LogType.Exception` (4) it parses the
     message with `getMessageParts` and sends a `LoggedException` to Crashlytics.

Both now go to the private server through the analysis channel the client already speaks:
`AnalysisManager.RegisterGetRegionFailed(cause)` (0x1813BC0) wraps the string as `{"cause": s}` under the stock
key `PhotonGetResionPingFailed` and queues it; `AnalysisManager.ManagedUpdate` posts one queued item per frame to
`/analysis/index`. Wire format of `cause` (the server classifies on it):

    "[Analysis]<n>:<text>//<n>:<text>..."     in-match anomaly (GameManager.SetAnalysisException format)
    "[Analysis]\\n<message>\\n<stackTrace>"     managed exception (C path; starts with "[Analysis]" + newline)
    "[Analysis]<purchase text>"               AnalysisManager.SendPurchaseFailed (rare)
    "GetPingFailed", "<DisconnectCause>", ...  the stock region-failure reports (no prefix, unchanged)

The exception string is truncated to EXC_LIMIT UTF-16 chars before it is deduped and sent.

Hooks (pristine `libil2cpp.so`, RVA == file offset)
--------------------------------------------------
B. `CrashReportManager.SetAnalysisException` 0x17CAEBC `adrp x8, #0x449a000` (the "[Analysis]" literal load
   right before the Concat). Both the dedupe path (fall-through from `HashSet.Add`) and the
   `checkDuplicate=false` path (`tbz w21,#0,#0x17caebc`) arrive here with x19 = message; x20 is `this` on one
   path and the HashSet on the other, so the cave re-fetches the singleton. The hook replaces a PC-relative
   `adrp`: it is NOT replayed, the cave re-materialises the literal with its own `adrp`. The cave counts the
   report (per-session budget, see below), does the original `Concat("[Analysis]", msg)`, fetches
   `Singleton<AnalysisManager>.Instance`, restores SetAnalysisException's frame and tail-calls
   `RegisterGetRegionFailed(am, s)`. `Crashlytics.LogException` is no longer called (it went nowhere).
C. `ExceptionHandler.HandleLog` 0x30E1C28 `mov x1, x21` (first instruction of the `type == 4` branch, after
   `cmp w22,#4; b.ne epilogue`; NZCV is dead there). x21 = message, x20 = stackTrace, the frame saves
   x19-x23/x29/x30. The cave builds `s = Concat(NewLine, message, NewLine, stackTrace)` (truncated), calls
   `CrashReportManager.SetAnalysisException(crm, s, true)` - so the retail HashSet dedupes every unique
   message+stack once per session BEFORE anything is queued - and leaves through HandleLog's own epilogue
   0x30E1CC0. getMessageParts / LoggedException / Crashlytics are skipped.

Guards
------
* Every `Singleton<T>.get_Instance` Method* and the "[Analysis]" literal come from lazily-initialised .bss
  metadata slots. `ColorfulManager.<BeginAsync>d__21.MoveNext` (boot) initialises both Singleton slots before it
  creates the managers; SetAnalysisException's own prologue initialises "[Analysis]". The caves still `cbz` the
  Method* (an uninitialised slot reads 0) and skip the report instead of crashing.
* Re-entry: `CrashReportManager` has 7 bytes of layout padding between `_isSetUserIdComplete` (+0x10, bool)
  and `_exceptionHashSet` (+0x18). +0x11 is an "exception being forwarded" flag: C sets it around its
  SetAnalysisException call and refuses to run while it is set (an exception logged from inside the
  forwarding path cannot recurse). If something below throws, the flag stays set and C stays disabled for the
  session (fail-closed). B reads the same flag to tell an exception (C) from an anomaly.
* Flood cap: +0x12 counts anomalies forwarded, +0x13 exceptions forwarded (u8 each, CAP per session, the
  CrashReportManager singleton lives for the whole process). B increments the matching counter and drops the
  report when it is at CAP; C checks the exception counter before allocating anything, so after CAP unique
  exceptions an NRE-per-frame costs one singleton fetch and two byte loads. Identical repeats are already
  dropped by the HashSet (C still allocates the Concat string to look it up: a few KB per repeat, far less than
  the retail Crashlytics path did). Limitation: the counters are per process, not per match.
* Threading: `logMessageReceived` only fires on the main thread (`Application.CallLogCallback` tests
  `invokedOnMainThread` before invoking it), the same thread as `AnalysisManager.ManagedUpdate`.
* Unwind risk (shared with every cave that calls managed code): if a callee throws, the C++ unwinder reaches a
  return address inside the cave host's FDE range and cannot unwind correctly. The calls made here
  (Concat/Substring/NewLine/HashSet/JsonUtility.ToJson/Queue.Enqueue) only throw on OOM.

Dead body (verified 2026-10-10)
-------------------------------
`Firebase.Crashlytics.ExceptionHandler.getMessageParts` 0x30E1CD4-0x30E1F70 (668 B). Its only caller is
`HandleLog`+0x54 (0x30E1C2C `bl`), i.e. the instruction right after hook C, so it is unreachable in every
flavour once hook C is in; the only data reference is its il2cpp methodPointers entry (reflection), and a full
b/bl/b.cond/cbz/tbz sweep of the pristine .so finds no other branch into it. The production table also
entry-stubs it (`mov x0, xzr; ret`, i.e. "no parts") so the checker sees a dead host. Layout:
  0x30E1CD4  entry stub (8 B)
  0x30E1CDC  production blob: cave C, then cave B
  DIAG builds replace the blob at the same offset with the instrumented copy (expected = production blob +
  the pristine bytes it grows into) and redirect both hooks; the 9700/9720 probes follow it.

KFDIAG probes (KF_DIAG=1 only, 9700-9799 were unused)
------------------------------------------------------
  9700  ExceptionHandler.Register subscribed HandleLog (`strb isRegistered` @0x30DD284): C can run at all.
        Absent => Firebase never registered (PlatformAccessor..cctor saw IsSDKInitialized()==false) - see the
        fallback note in the report / AGENTS.md.
  9701  HandleLog entered with type 4 (Exception).
  9702  C forwards: calling CrashReportManager.SetAnalysisException(s, true).
  9703  C skipped: re-entry flag set.   9704  C skipped: exception budget exhausted.
  9710  B forwarding taken for an anomaly, 9711 for an exception (after dedupe and budget).
  9712  B dropped: budget exhausted.
  9720  AnalysisManager.RegisterGetRegionFailed entered (any caller: ours or the stock region failures).

    python scripts/re/error_report_caves.py   # prints the production + DIAG entries for patch-il2cpp-endpoints.py
"""
import os
import sys

from keystone import Ks, KS_ARCH_ARM64, KS_MODE_LITTLE_ENDIAN

try:
    from capstone import Cs, CS_ARCH_ARM64, CS_MODE_LITTLE_ENDIAN
except Exception:  # pragma: no cover
    Cs = None

_ks = Ks(KS_ARCH_ARM64, KS_MODE_LITTLE_ENDIAN)

LOG = 0x13BC348            # region-A KFDIAG logger (DIAG only): w0 = value, preserves x1-x18 / q0-q7 / q16-q31

# --- metadata slots (GOT-style pointers to lazily initialised .bss storage) ----------------------------------
SLOT_CRM_INSTANCE = 0x4478FA0   # Method$GRE.Singleton<CrashReportManager>.get_Instance()
SLOT_AM_INSTANCE = 0x4486C60    # Method$GRE.Singleton<AnalysisManager>.get_Instance()
SLOT_STR_ANALYSIS = 0x449A6F0   # str "[Analysis]" (SetAnalysisException's own usage list)

# --- callees ----------------------------------------------------------------------------------------------------
GET_INSTANCE = 0x2920B7C        # GRE.Singleton<object>.get_Instance(Method*)
CONCAT2 = 0x1D77484             # System.String.Concat(string, string)
CONCAT4 = 0x1D83BFC             # System.String.Concat(string, string, string, string)
SUBSTRING = 0x1D743C0           # System.String.Substring(int startIndex, int length)
GET_NEWLINE = 0x1BF055C         # System.Environment.get_NewLine() (self-initialising, "\n" on Android)
SET_ANALYSIS = 0x17CAE14        # Colorful.CrashReportManager.SetAnalysisException(string, bool)
REGISTER_REGION = 0x1813BC0     # Colorful.AnalysisManager.RegisterGetRegionFailed(string)

# --- CrashReportManager padding (+0x11..+0x17, between the +0x10 bool and the +0x18 HashSet) ----------------------
OFF_FWD_FLAG = 0x11             # 1 while cave C is forwarding an exception
OFF_CNT_ANOMALY = 0x12          # u8 anomalies forwarded this session
OFF_CNT_EXC = 0x13              # u8 exceptions forwarded this session
CAP = 16                        # per-kind budget per session (server default: 30 entries/minute per user)
EXC_LIMIT = 2500                # max UTF-16 chars of "\n<message>\n<stack>" (server keeps 8 KB UTF-8 per value)
STR_LENGTH = 0x10               # Il2CppString.length

# --- hook sites ---------------------------------------------------------------------------------------------------
HOOK_B = 0x17CAEBC              # SetAnalysisException `adrp x8, #0x449a000` ("[Analysis]")
HOOK_C = 0x30E1C28              # HandleLog `mov x1, x21` (type == 4 branch)
HANDLELOG_EPI = 0x30E1CC0       # HandleLog epilogue (restores x19-x23, x29, x30; ret)
HOOK_REGISTERED = 0x30DD284     # ExceptionHandler.Register `mov w8, #1` before `strb w8, [x19, #0x10]`
HOOK_REGISTER_REGION = REGISTER_REGION  # entry `str x21, [sp, #-0x30]!`

# --- dead body ----------------------------------------------------------------------------------------------------
HOST = 0x30E1CD4                # Firebase.Crashlytics.ExceptionHandler.getMessageParts
HOST_END = 0x30E1F70            # LoggedException..ctor (live)
STUB = bytes.fromhex("e0031faac0035fd6")  # mov x0, xzr; ret
BLOB = HOST + len(STUB)

LIB = os.environ.get("KF_CLEAN_LIBIL2CPP") or os.path.join(
    os.path.dirname(__file__), "..", "..", ".local", "re", "lib", "arm64-v8a", "libil2cpp.so")


def page(addr: int) -> int:
    return addr & ~0xFFF


def lo12(addr: int) -> int:
    return addr & 0xFFF


def asm(text: str, base: int) -> bytes:
    lines = [ln.split(";")[0].strip() for ln in text.splitlines()]
    src = "\n".join(ln for ln in lines if ln)
    enc, _ = _ks.asm(src, base)
    if enc is None:
        raise SystemExit("asm failed at " + hex(base) + ":\n" + src)
    return bytes(enc)


def probe(value: int, diag: bool, extra: str = "") -> str:
    """`w0 = value (+ extra reg); bl LOG` in DIAG builds, nothing in production. x0 must be dead here."""
    if not diag:
        return ""
    out = f"mov w0, #{value}\n"
    if extra:
        out += f"add w0, w0, {extra}\n"
    return out + f"bl #{LOG:#x}\n"


def fetch_instance(slot: int, fail: str) -> str:
    """x0 = Singleton<T>.get_Instance(); branch to `fail` if the Method* slot is not initialised or null."""
    return f"""
adrp x8, #{page(slot):#x}
ldr x8, [x8, #{lo12(slot):#x}]
ldr x0, [x8]                          ; Method* (0 until the owning method's metadata init ran)
cbz x0, {fail}
bl #{GET_INSTANCE:#x}
cbz x0, {fail}
"""


def cave_c(diag: bool) -> str:
    """HandleLog type==4: forward "\\n<message>\\n<stack>" to CrashReportManager.SetAnalysisException(s, true)."""
    return probe(9701, diag) + fetch_instance(SLOT_CRM_INSTANCE, "c_out") + f"""
mov x19, x0                           ; crm (HandleLog's x19 is restored by its epilogue)
ldrb w8, [x19, #{OFF_FWD_FLAG:#x}]
cbnz w8, c_reent                      ; already forwarding: an exception from inside our own path
ldrb w8, [x19, #{OFF_CNT_EXC:#x}]
cmp w8, #{CAP}
b.hs c_cap
mov w8, #1
strb w8, [x19, #{OFF_FWD_FLAG:#x}]
mov x0, xzr
bl #{GET_NEWLINE:#x}                  ; x0 = "\\n"
mov x22, x0
mov x1, x21                           ; message
mov x2, x22
mov x3, x20                           ; stackTrace
mov x4, xzr
bl #{CONCAT4:#x}                      ; "\\n" + message + "\\n" + stackTrace
cbz x0, c_clear
ldr w8, [x0, #{STR_LENGTH:#x}]
cmp w8, #{EXC_LIMIT}
b.le c_send
mov w1, wzr
mov w2, #{EXC_LIMIT}
mov x3, xzr
bl #{SUBSTRING:#x}
c_send:
mov x1, x0
""" + probe(9702, diag) + f"""
mov x0, x19
mov w2, #1                            ; checkDuplicate: the retail HashSet drops repeats before anything is queued
mov x3, xzr
bl #{SET_ANALYSIS:#x}
c_clear:
strb wzr, [x19, #{OFF_FWD_FLAG:#x}]
c_out:
b #{HANDLELOG_EPI:#x}
c_reent:
""" + probe(9703, diag) + f"""
b #{HANDLELOG_EPI:#x}
c_cap:
""" + probe(9704, diag) + f"""
b #{HANDLELOG_EPI:#x}
"""


SET_ANALYSIS_EPILOGUE = """
ldp x29, x30, [sp, #0x20]
ldp x20, x19, [sp, #0x10]
ldp x22, x21, [sp], #0x30
"""


def cave_b(diag: bool) -> str:
    """SetAnalysisException after dedupe: budget, Concat("[Analysis]", msg), tail-call RegisterGetRegionFailed."""
    return fetch_instance(SLOT_CRM_INSTANCE, "b_ret") + f"""
ldrb w9, [x0, #{OFF_FWD_FLAG:#x}]     ; 1 = this report is an exception from cave C
add x10, x0, #{OFF_CNT_ANOMALY:#x}
cbz w9, b_cnt
add x10, x0, #{OFF_CNT_EXC:#x}
b_cnt:
ldrb w8, [x10]
cmp w8, #{CAP}
b.hs b_cap
add w8, w8, #1
strb w8, [x10]
""" + probe(9710, diag, "w9") + f"""
mov x1, x19                           ; message
mov x2, xzr
adrp x8, #{page(SLOT_STR_ANALYSIS):#x}
ldr x8, [x8, #{lo12(SLOT_STR_ANALYSIS):#x}]
ldr x0, [x8]                          ; "[Analysis]" (initialised by this method's prologue)
bl #{CONCAT2:#x}
mov x19, x0
""" + fetch_instance(SLOT_AM_INSTANCE, "b_ret") + f"""
mov x1, x19                           ; cause
mov x2, xzr
""" + SET_ANALYSIS_EPILOGUE + f"""
b #{REGISTER_REGION:#x}               ; tail call with SetAnalysisException's caller LR
b_cap:
""" + probe(9712, diag) + """
b_ret:
""" + SET_ANALYSIS_EPILOGUE + """
ret
"""


# DIAG-only probes (placed after the instrumented blob)
DIAG_REGISTERED_SRC = f"""
mov w0, #9700
bl #{LOG:#x}                          ; x0 dead (void call result), x30 is reloaded by the epilogue
mov w8, #1                            ; displaced
b #{HOOK_REGISTERED + 4:#x}
"""

DIAG_REGISTER_REGION_SRC = f"""
stp x0, x30, [sp, #-0x10]!            ; entry hook: this and the caller's LR are live
mov w0, #9720
bl #{LOG:#x}
ldp x0, x30, [sp], #0x10
str x21, [sp, #-0x30]!                ; displaced prologue
b #{HOOK_REGISTER_REGION + 4:#x}
"""


def build(diag: bool):
    c = asm(cave_c(diag), BLOB)
    b = asm(cave_b(diag), BLOB + len(c))
    return c, b


def entry(desc, offset, expected, replacement):
    return (f'    {{"description": "{desc}", "offset": {offset:#x}, '
            f'"expected": bytes.fromhex("{expected.hex()}"), '
            f'"replacement": bytes.fromhex("{replacement.hex()}")}},')


def disasm(code: bytes, base: int) -> str:
    if Cs is None:
        return "    (capstone unavailable)"
    md = Cs(CS_ARCH_ARM64, CS_MODE_LITTLE_ENDIAN)
    return "\n".join(f"    {i.address:#x}  {i.mnemonic:8s}{i.op_str}" for i in md.disasm(code, base))


def main() -> None:
    lb = open(LIB, "rb").read()
    # 1. pristine guards at the hook sites
    pristine = {
        HOOK_B: "88660190",          # adrp x8, #0x449a000 ("[Analysis]")
        HOOK_C: "e10315aa",          # mov x1, x21
        HOOK_REGISTERED: "e8030032",  # mov w8, #1
        HOOK_REGISTER_REGION: "f50f1df8",  # str x21, [sp, #-0x30]!
        HOST: "f90f1bf8",            # getMessageParts: str x25, [sp, #-0x50]!
        HANDLELOG_EPI: "fd7b43a9",   # ldp x29, x30, [sp, #0x30]
    }
    for off, hexv in pristine.items():
        got = lb[off:off + 4].hex()
        assert got == hexv, f"pristine guard @{off:#x}: {got} != {hexv}"

    prod_c, prod_b = build(False)
    diag_c, diag_b = build(True)
    prod_blob = prod_c + prod_b
    diag_blob = diag_c + diag_b
    diag_reg_at = BLOB + len(diag_blob)
    diag_reg = asm(DIAG_REGISTERED_SRC, diag_reg_at)
    diag_rr_at = diag_reg_at + len(diag_reg)
    diag_rr = asm(DIAG_REGISTER_REGION_SRC, diag_rr_at)
    end = diag_rr_at + len(diag_rr)
    assert BLOB + len(prod_blob) <= HOST_END and end <= HOST_END, f"overflows getMessageParts body: {end:#x}"

    prod_c_at, prod_b_at = BLOB, BLOB + len(prod_c)
    diag_c_at, diag_b_at = BLOB, BLOB + len(diag_c)
    hook_c = asm(f"b #{prod_c_at:#x}", HOOK_C)
    hook_b = asm(f"b #{prod_b_at:#x}", HOOK_B)
    dhook_c = asm(f"b #{diag_c_at:#x}", HOOK_C)
    dhook_b = asm(f"b #{diag_b_at:#x}", HOOK_B)
    dhook_reg = asm(f"b #{diag_reg_at:#x}", HOOK_REGISTERED)
    dhook_rr = asm(f"b #{diag_rr_at:#x}", HOOK_REGISTER_REGION)

    out = []
    out.append("    # ---- Client error reporting (2026-10-10): anomalies + managed exceptions -> /analysis/index ----")
    out.append("    # Generated by scripts/re/error_report_caves.py (wire format, guards and budget in its docstring).")
    out.append("    # B: CrashReportManager.SetAnalysisException after its HashSet dedupe -> \"[Analysis]\"+msg ->")
    out.append("    #    AnalysisManager.RegisterGetRegionFailed (key PhotonGetResionPingFailed, {\"cause\": s}); no Crashlytics.")
    out.append("    # C: ExceptionHandler.HandleLog type 4 -> SetAnalysisException(\"\\n\"+message+\"\\n\"+stack, true) -> B.")
    out.append("    # Dead body: ExceptionHandler.getMessageParts 0x30E1CD4-0x30E1F70, whose only caller (HandleLog+0x54)")
    out.append("    # is skipped by hook C; entry-stubbed below in every flavour.")
    out.append(entry("Firebase ExceptionHandler.getMessageParts entry -> mov x0, xzr; ret (only caller HandleLog+0x54 is bypassed by the error-report hook; body hosts the caves)",
                     HOST, lb[HOST:HOST + len(STUB)], STUB))
    out.append(entry("cave: error report C (HandleLog type 4 -> SetAnalysisException) + B (SetAnalysisException -> RegisterGetRegionFailed) (dead body of ExceptionHandler.getMessageParts)",
                     BLOB, lb[BLOB:BLOB + len(prod_blob)], prod_blob))
    out.append(entry("ExceptionHandler.HandleLog `mov x1, x21` (type 4) -> b error-report cave C",
                     HOOK_C, lb[HOOK_C:HOOK_C + 4], hook_c))
    out.append(entry("CrashReportManager.SetAnalysisException `adrp x8, \\\"[Analysis]\\\"` -> b error-report cave B",
                     HOOK_B, lb[HOOK_B:HOOK_B + 4], hook_b))
    out.append("")
    out.append("    # ---- DIAG (KFDIAG 9700-9720): client error reporting probes; generated by scripts/re/error_report_caves.py ----")
    out.append("    # The instrumented blob replaces the production blob at the same offset (expected = production blob +")
    out.append("    # the pristine dead bytes it grows into); the hook overrides expect the production hook bytes.")
    grow = lb[BLOB + len(prod_blob):BLOB + len(diag_blob)]
    out.append(entry("DIAG cave: error report C/B with probes 9701-9704 / 9710-9712 (same dead body of ExceptionHandler.getMessageParts)",
                     BLOB, prod_blob + grow, diag_blob))
    if dhook_c != hook_c:  # cave C leads both blobs, so the production hook already lands on the probes
        out.append(entry("DIAG hook: ExceptionHandler.HandleLog type 4 -> b instrumented cave C",
                         HOOK_C, hook_c, dhook_c))
    out.append(entry("DIAG hook: CrashReportManager.SetAnalysisException -> b instrumented cave B",
                     HOOK_B, hook_b, dhook_b))
    out.append(entry("DIAG cave: ExceptionHandler.Register subscribed HandleLog -> 9700 (dead body of ExceptionHandler.getMessageParts)",
                     diag_reg_at, lb[diag_reg_at:diag_reg_at + len(diag_reg)], diag_reg))
    out.append(entry("DIAG hook: ExceptionHandler.Register `mov w8, #1` (isRegistered) -> b 9700 probe",
                     HOOK_REGISTERED, lb[HOOK_REGISTERED:HOOK_REGISTERED + 4], dhook_reg))
    out.append(entry("DIAG cave: AnalysisManager.RegisterGetRegionFailed entry -> 9720 (dead body of ExceptionHandler.getMessageParts)",
                     diag_rr_at, lb[diag_rr_at:diag_rr_at + len(diag_rr)], diag_rr))
    out.append(entry("DIAG hook: AnalysisManager.RegisterGetRegionFailed entry -> b 9720 probe",
                     HOOK_REGISTER_REGION, lb[HOOK_REGISTER_REGION:HOOK_REGISTER_REGION + 4], dhook_rr))
    print("\n".join(out))

    err = sys.stderr
    print(f"# production: C {len(prod_c)} B @{prod_b_at:#x} {len(prod_b)}  blob end {BLOB + len(prod_blob):#x}", file=err)
    print(f"# DIAG: C {len(diag_c)} B @{diag_b_at:#x} {len(diag_b)}; 9700 @{diag_reg_at:#x}; 9720 @{diag_rr_at:#x}; "
          f"end {end:#x} (host end {HOST_END:#x}, {HOST_END - end} B free)", file=err)
    if "--disasm" in sys.argv:
        for name, code, base in (("production C", prod_c, prod_c_at), ("production B", prod_b, prod_b_at),
                                 ("DIAG C", diag_c, diag_c_at), ("DIAG B", diag_b, diag_b_at),
                                 ("DIAG 9700", diag_reg, diag_reg_at), ("DIAG 9720", diag_rr, diag_rr_at)):
            print(f"# {name} @{base:#x}:", file=err)
            print(disasm(code, base), file=err)


if __name__ == "__main__":
    main()
