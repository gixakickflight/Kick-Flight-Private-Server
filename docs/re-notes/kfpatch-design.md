# kfpatch: native patch library design (2026-11-05, branch claude/native-patch-lib)

Status: PHASE 1 scaffolding written, NOT built or tested (Android NDK missing on this machine). Phase 2 not started.

Why: il2cpp dead bodies are exhausted (deployed prod APK already carries DIAG + AI-role caves, no free host bytes).
Note: the patcher rejects KF_DIAG together with KF_AI_ROLES (SystemExit at patch-il2cpp-endpoints.py ~line 793), so the exact
deployed flag combination must be confirmed by the coordinator; deploy-vps-main.yml only stages prebuilt `.local/*.apk`.

## Hook mechanism (iOS portable, no runtime text patching)
* Static 12 B trampoline over the first 3 instructions of a hooked function, written by the patcher with byte guards:
  `adrp x16, slot_page; ldr x16,[x16,#off]; br x16` (generator/validator: `scripts/re/kfpatch_tramp.py`; refuses prologues
  whose first 3 insns are PC-relative, since they are replayed). x16 (IP0) is free at function entry per AAPCS64.
  Not an AGENTS.md section 3 entry-hook-with-`bl`: it is `br`, LR untouched.
* Slot table: libil2cpp.so `.bss` tail. PT_LOAD #1 VA 0x3F48000 filesz 0x781668 memsz 0xA3A8D0 -> bss ends 0x49828D0; page
  0x4983000 is mapped but beyond memsz. Scan of every `adrp` in .text: page 0x4982000 is referenced (10x), pages
  0x4983000+ are not. Slots at RVA 0x4982C00.. (0x400 B, 128 slots) are in the zero-filled remainder of the last page and
  never referenced by il2cpp code. (Safer variant: bump memsz in the PT_LOAD header; iOS equivalent is a `__DATA` section.)
* libkfpatch.so (`src/native/kfpatch`, `scripts/build-kfpatch.sh`) finds libil2cpp base with `dl_iterate_phdr`, builds
  "call original" stubs in an anonymous RWX page (displaced 3 insns + `ldr x16,#8; br x16; .quad fn+12`) and writes the slot
  last. Logs with `__android_log_print` tag KFPATCH.
* Load order: `System.loadLibrary("kfpatch")` from smali in the Application class (before Unity). Chosen over adding
  DT_NEEDED to libil2cpp (needs .dynamic/.dynstr surgery) and because libil2cpp is not yet mapped when an early library
  constructs; the constructor starts a watcher thread (1 ms poll, 20 s cap) that fills slots as soon as libil2cpp appears,
  which is long before the first managed code runs. Hooks on boot-time functions are therefore forbidden (an unfilled slot
  is a null `br`). Alternative if needed: patch a libil2cpp init-array entry.
* Works under ndk_translation: only plain loads/stores/br, no JIT of il2cpp text; the anon RWX page is guest code
  translated normally (to be confirmed on the emulator).

## Not done / blocked
* NDK: `~/Android/Sdk/cmdline-tools/latest/bin/sdkmanager "ndk;27.2.12479018"` (no sudo), then `scripts/build-kfpatch.sh`.
* Not yet written: smali loader + lib copy into the APK and the KF_NATIVE=1 gate in build-direct-apk.sh/.sh/.ps1,
  trampoline entries in patch-il2cpp-endpoints.py (default patch count stays 245).
* Phase 2 plan (disc-detail model): hook `OutGameModelController.CreateSummonModel` (0x1552CDC; first 3 insns PC-independent
  per kfpatch_tramp.py) and reimplement SetModel in C through il2cpp API; exp2 showed the instantiated model has no Animator
  on its root (see disc-detail-3d-model-setmodel.md): use GetComponentInChildren<Animator>, check sm_NNNN_1 prefab via UnityPy.

## Phase 1 status (2026-11-05): DONE and emulator-verified
* Deployed flag set (byte-identical to the staged 2.11.706 APK, 5613 changed bytes): `KF_PHOTON=1 KF_AI_ROLES=1 KF_AI_ROLE_TARGETING=1 KF_AI_ROLE_DEPOSIT=1` (KF_DIAG and KF_AI_ROLES are mutually exclusive).
* Build: `scripts/build-kfpatch.sh` (NDK 27.2.12479018 clang, `KF_PROOF_RVA=0x...` enables the proof hook). `KF_NATIVE=1` in `scripts/build-direct-apk.sh`
  (and untracked `.local/build-fedora.sh`) builds the lib and runs `scripts/patch-kfpatch-apk.py` (lib into `lib/arm64-v8a`, extra
  `classes<N+1>.dex` with `kf.KfPatchProvider` whose static init does `System.loadLibrary("kfpatch")`, manifest provider initOrder 1000).
* Patcher: `KF_NATIVE_HOOKS` in `patch-il2cpp-endpoints.py` adds one byte-guarded 12-byte trampoline per hook. Default dry-run stays 245 patches;
  deployed flags 251 (no native) / 252 (KF_NATIVE=1).
* LESSON: the call-original stub must NOT be an anonymous RWX page written at runtime (ndk_translation SEGV_ACCERR on first execution).
  `KF_ORIG_STUB` (kfpatch.h) emits it at compile time (`.inst` of the 3 displaced words + `adrp/ldr/br` through a data word filled with target+12).
* Proof hook: RVA 0x1552CDC (`OutGameModelController.CreateSummonModel`). On the emulator: `KFPATCH libkfpatch loaded`, `il2cpp base=..., 1 hook(s) armed`,
  opening a disc detail logs `proof hook this=... a1=0x9 a2=0x1` and the original runs; home, disc list and a full battle start work with no new crash.
* Slot table RVA 0x4982C00 is inside the page-rounded RW mapping past `.bss` memsz end (0x49828d0): confirmed writable.

## Phase 2 status: NOT done (stopped, see disc-detail-3d-model-setmodel.md)
Finding: the `sm_0001_1` prefab root DOES carry Animator + MonoBehaviour (UnityPy on the repaired bundle), so the earlier "null Animator" is not a bundle problem.
Reimplementing SetModel + LoadModelAsync in C needs the async bundle load state machine (0x159E254, whose first bytes host a cave at 0x159E25C, so it cannot be trampolined)
and il2cpp field/method resolution with no dump.cs. Next step: generate dump.cs/script.json, then port SetModel/LoadModelAsync to C hooks.

## Phase 2 notes (2026-11-05)
- Dump tool: Il2CppDumper v6.7.46 (net6 build), run with `DOTNET_ROLL_FORWARD=Major` on dotnet 8.0.132, no sudo. Inputs: pristine `libil2cpp.so` (sha c92a03cc...) + `global-metadata.dat` from base.apk. Output (gitignored): `.local/il2cpp-dump/out/{dump.cs,script.json,il2cpp.h,DummyDll,stringliteral.json}`.
- Hook-stub lesson: the `KF_ORIG_STUB` stub MUST be declared with a full 8-arg int64 prototype; a `void(void)` cast let clang drop arg setup after KFLOG, clobbering x0 (SEGV in CharacterAnimatorBase.Initialize).
- SEGV @0x31 on disc detail: NOT reproduced with deployed flags + local host + no KF_NATIVE (kf-normal-local.apk, sha 53876ba5...d06e): 0 CRASH lines on opening disc detail; yellow box empty. So the fault belongs to the KF_NATIVE trampoline/proof-hook builds, not the baseline.
- All-live (all SetModel stubs removed) still empty; opening detail raises MAPERR fault 0x19 at RVA 0x15D60E4 (SearchTargetInfo.Copy null src, caller 0x1806860) and InstantiateSummonModel is never reached. Phase 2 C replacement (MoveNext/SetModel/UnloadModel) not yet implemented.

## Cave migration (2026-11-05)
All prod/AI-role caves hosted in the SetModel / `<LoadModelAsync>d__25.MoveNext` / UnloadModel bodies were moved into libkfpatch
(`src/native/kfpatch/caves_gen.S`, generated by `scripts/re/kfpatch_cavegen.py`; manifest `caves_manifest.json`). The three bodies are
restored to pristine (stubs dropped); that is what makes the disc-detail model load. Equivalence: instruction words are copied
verbatim (`.inst`); only PC-relative instructions change (adrp/adr -> `ldr xN, kf_pool+k`, absolute il2cpp address = base + RVA set at
init; external b/bl/cond-branches -> `ldr x16,pool; br x16` veneers). Hook sites in il2cpp are retargeted to 12-byte slot veneers
(slots 16+) in the dead PlayerCharacter.UpdateLookTarget body @0x13CD990; slots are filled before C hooks are armed (all hook sites are battle-time).
Patcher: `KF_NATIVE=1` activates the restore (the legacy layout is used without it); `KF_NATIVE=1` + `KF_DIAG=1` errors. Proof hook (slot 0) is
OFF by default (only with `KF_PROOF_RVA`). Known: `PlayIdleLoopIfExist` NRE (pristine code, non-fatal) on some discs.

### Per-cave equivalence (verified statically 2026-11-05, pristine libil2cpp + generated blobs)
Rule for all: instruction words copied verbatim; only adrp/adr -> `ldr xN,pool` (same absolute value), external b/bl/cond -> veneer (`ldr x16,pool; br x16`, same target, LR untouched).
Hook-site: a `b`/`bl` to a veneer `adrp x16,slot; ldr x16,[x16]; br x16` (12 B) in the dead UpdateLookTarget body. x16/x17 are IP0/IP1: no x16/x17 read or written within -0x30/+0x40 of any site (capstone scan), so the extra clobber is unobservable. NZCV: every site's following instructions either set flags themselves (0x18072d0 and 0x1808d40 are followed by fcmp) or do not read them (rest).

| Site | Cave (blob) | Displaced insn / exit | Notes |
|---|---|---|---|
| 0x13BEAEC b | 0x159D200 AI gym/special-skill | `stp x20,x19,[sp,#-0x20]!` replicated in cave, exit `b 0x13BEAF0` | function entry, `b` (LR intact) |
| 0x13BC3A8 b | 0x159D3E0 null-safe GetDisplayAngles | entry stps replicated, exit `b 0x13BC3B8`; null path = `ret` | entry hook |
| 0x1807474 b | 0x159D410 AI roles telemetry | `str w0,[x19,#0x7c]` in cave, exit `b 0x1807478`; saves/restores x0..x? and flags area on 0x300 B frame | |
| 0x18072D0 b | 0x159D6E0 targeting weighting | `ldr x1,[x26]` in cave, exit `b 0x18072D4` (fcmp follows) | |
| 0x1808D40 b | 0x159D854 deposit | `ldr s1,[x19,#0x20]` first insn of cave, exit `b 0x1808D44` | |
| 0x1439AEC bl | 0x159DA48 HitInfo fallback | `mov x1,x0` at cave tail before `ret`; x30 saved around inner `bl` | mid-function `bl`: x30 re-set by next bl, epilogue reloads from stack |
| 0x17F8F1C b | 0x159DF70 PlayerStateSkill.End tail | displaced tail `b 0x17F1B08` becomes `bl 0x17F1B08` after stp x29,x30 frame; ends `ret` | x30 = caller LR pushed by the cave |
| 0x184961C b | 0x159E25C CreateSmog step | `fadd s0,s0,s0` (2*radius fallback) in cave, exit `b 0x1849620` | x16 used internally only |
| 0x184983C b | 0x159E294 Smog init | `stp s1,s0,[x19,#0x4c]` in cave, exit `b 0x1849840` | |
| 0x1A2AC44 b | 0x159E32C rush push part 2 | host part 1 (dead CheckInitializeError tail, outside migrated ranges) already pushed x29/x30; cave does `bl get_eulerAngles`, restores, `ret` | |

Boot-time safety: libkfpatch's watcher fills pool + cave slots before the C hooks, polling every 1 ms for libil2cpp.so (mapped when libunity loads it, hundreds of ms before il2cpp_init and tens of seconds before any Tap Start). All 10 sites are battle code (AI, smog, skill end, bone controller, entrained state), none reachable from Title/Home boot, so every slot is filled before first use. The proof hook on CreateSummonModel is removed by default (only `KF_PROOF_RVA`).

## Fail-safe veneers (2.11.804, 2026-11-06): an empty slot never jumps to 0
Problem: every hook site was `adrp x16,slot; ldr x16,[x16,#off]; br x16`. With the slot still 0 (watcher not yet scheduled, libkfpatch failed to load, hook fires before the fill) that is `br 0` = the 2.11.800 pc=0 crash class. A naive in-place `cbz` is impossible: the displaced original instructions would have no room.
Design (`scripts/re/kfpatch_veneer.py`, pure Python, shared by `patch-il2cpp-endpoints.py`): the hook site is ONE `b V` (4 bytes, displaces only I0; LR/args untouched, `b` never `bl` for entry hooks). V lives in verified-dead code:
```
V+0  adrp x16, slot_page        ; x16 = IP0, the only register used
V+4  ldr  x16, [x16, #slot_off]
V+8  cbz  x16, F                ; slot empty -> original code
V+12 br   x16                   ; slot filled -> hook/cave exactly as before
F:   I0 (re-encoded if PC-relative) ; b site+4          (bl sites: I0 ; ret, entered with LR=site+4)
```
I0 is NOT executed before the test (the hook would see a moved sp). The C hook's "call original" therefore replays only I0: `KF_ORIG_STUB1` (kfpatch.h) and `kf_hook_t.disp = 4` (`ret_slot = target + 4`); sites hooked with the legacy 12-byte form (proof/diag hooks, `disp = 0`) keep `KF_ORIG_STUB`. adrp/ldr/cbz/br leave NZCV alone. Per-site fail-safe words are derived from each site patch's byte-guarded original word:

| Slot | Site | Original word | Fail-safe F |
|---|---|---|---|
| 8 (CRI) | il2cpp 0x1BDBEFC | `stp x20,x19,[sp,#-0x20]!` | I0; `b 0x1BDBF00` |
| 16 | 0x13BEAEC | `stp x20,x19,[sp,#-0x20]!` | I0; `b +4` |
| 17 | 0x13BC3A8 | `stp d13,d12,[sp,#-0x50]!` | I0; `b +4` |
| 18 | 0x1807474 | `str w0,[x19,#0x7c]` | I0; `b +4` |
| 19 | 0x18072D0 | `ldr x1,[x26]` | I0; `b +4` |
| 20 | 0x1808D40 | `ldr s1,[x19,#0x20]` | I0; `b +4` |
| 21 | 0x1439AEC (`bl`) | `mov x1,x0` | I0; `ret` |
| 22 | 0x17F8F1C | tail `b 0x17F1B08` | `b 0x17F1B08` (re-encoded) |
| 23 | 0x184961C | `fadd s0,s0,s0` | I0; `b +4` |
| 24 | 0x184983C | `stp s1,s0,[x19,#0x4c]` | I0; `b +4` |
| 25 | 0x1A2AC44 | (inside prod patch, "rush push part 2") | `ldr s0,[sp,#0x10]; ldp x29,x30,[sp],#0x20; mov x0,xzr; ret` (skip get_eulerAngles, same epilogue as the cave) |

il2cpp host: dead body of `PlayerCharacter.UpdateLookTarget` @0x13CD990: 10 cave veneers x 32 B (slots 16-25, `host + 32*(slot-16)`) + the CRI veneer x 24 B at 0x13CDAD0 = 344 B, byte-guarded against the pristine bytes (`KF_VHOST_EXPECTED`); the body spans ~0x13CD984..0x13CE3DC and production patches touch nothing else in it (the DIAG regions D/E that used it are mutually exclusive with KF_NATIVE). libunity host: the first 48 B of the dead function 0xA08DDC (FDE size 2196 B), found by `scripts/re/find_dead_functions.py` (104 candidates in the whole .so; nothing outside the function references it through b/bl/b.cond/cbz/tbz/adr/adrp+add, .rela.dyn addends, raw qwords, dynsym; also 0 rodata-relative hits). libunity veneers: walk @0xA08DDC, release @0xA08DF4; hook sites `b 0xA08DDC` @0x2C3070 and `b 0xA08DF4` @0x2BA824 over `sub sp,sp,#0x50` / `str x24,[sp,#-0x40]!`.
libunity host (CORRECTED 2026-11-06): the first 2.11.804 build hosted the two 24 B libunity veneers in 0xA08DDC, picked by a heuristic finder whose adrp+add window was only 4 insns. That was WRONG: 0xA08DDC (2196 B, SIMD job kernel) is a registered callback - `adrp x3,#0xa08000` at 0xA08868 and `add x3,x3,#0xddc` at 0xA08884 pass its address, together with 0xA09670/0xA096C8/0xA096D0, to a registration call (bl 0x55db74). Overwriting its prologue would crash whenever the job runs. Fixed: host is now 0xEFF890 (FDE 244 B, last quarter of .text; ends in `ret`, preceded by `ret`; first 48 B patched, byte-guarded by `KF_UNITY_VHOST_EXPECTED`). Walk veneer 0xEFF890, release veneer 0xEFF8A8. Proof on the pristine libunity.so (`scripts/re/verify_dead_host.py`, whole-ELF): zero b/bl/b.cond/cbz/cbnz/tbz/tbnz/adr/ldr-literal into the function; adrp->add/ldr/str with a 64-insn window resolves nothing into it (one adrp to the same 4 KB page at 0xF00F10 adds 0xCD4 -> 0xEFFCD4, another function); zero raw qwords (8- and 4-aligned), zero uint32 RVAs, zero .rela.dyn/.rela.plt addends (incl. sym+addend), zero dynsym/.dynamic entries, zero 32-bit relative jump-table style words (scales 1 and 4, 64 bases) in .rodata/.gcc_except_table/.data.rel.ro/.data. Strict screen `scripts/re/find_dead_functions.py`: 9 of 61014 FDE functions pass; six of them (0x78xxxx float<->half kernels) fail the verifier as registered callbacks like 0xA08DDC, 0xE1A558 does not end in a terminator, leaving 0xEFF620 and 0xEFF890. Static proof only (no runtime coverage proof).
il2cpp host proof (pristine libil2cpp.so, same verifier, range 0x13CD990..0x13CE3DC): zero references of every kind into the body. The only references to the method are its entry 0x13CD984: one `bl` at 0x13CB60C (PlayerCharacter update chain) and the managed method-pointer slot `.rela.dyn` RELATIVE @0x42CE2A8 (vtable/reflection reachable); both land on the production patch at 0x13CD984 (`ret`, c0035fd6, unconditional), which precedes the veneers, so a call/reflection invoke returns before touching them. Not a metadata-only claim: the check is on the final 804 bytes (`c0035fd6 ed33016d eb2b026d` then the first veneer `b0ad01b0 ...`).
Test flag: `KF_FILL_DELAY_MS=<ms>` (build-kfpatch.sh, TEST builds only) makes the watcher leave all slots at 0 for that long so the fail-safe path is exercised for real.
