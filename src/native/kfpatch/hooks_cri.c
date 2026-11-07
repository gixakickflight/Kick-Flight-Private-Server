// Crash family 3 (SIGSEGV UnityMain, fault addr 0x7b0, GameStartPresenter "Battle Start" voice): docs/re-notes/exception-review-2.11.800.md.
// GRE.AudioManager.CreateSoundObject (0x1BDBEFC) -> new CriAtomExSoundObject(false,true) -> criAtomExSoundObject_Create, which reads
// the CRIWARE ACF state (libcri_ware_unity.so .bss 0x1c5590, set by criAtomEx_RegisterAcf*, cleared by UnregisterAcf) without a null
// check: with no ACF registered it does `ldr w0,[x8,#0x7b0]` on NULL. Guard: if that state is NULL, return a null sound object
// (VoiceInfo.SetInfo/PoolDeactive/PoolFinalize are null-safe) instead of entering CRIWARE; otherwise call the original.
// The offsets below are specific to the shipped libcri_ware_unity.so; the guard self-disables (calls the original) when the
// two code words checked at runtime do not match.
#define _GNU_SOURCE
#include <link.h>
#include <string.h>
#include "kfpatch.h"

typedef int64_t i64;
#define CRI_ACF_STATE_RVA 0x1c5590u   // .bss: pointer to the ACF state, NULL while no ACF is registered
#define CRI_CHECK_FN_RVA 0x59cd8u     // stp x20,x19,[sp,#-0x20]!  (0xa9be4ff4)
#define CRI_CHECK_LDR_RVA 0x59d28u    // ldr w0,[x8,#0x7b0]        (0xb947b100) = the faulting load

KF_ORIG_STUB1(csobj, 0xa9be4ff4)  // stp x20,x19,[sp,#-0x20]!  (site = `b V`, resumes at +4; the next two insns stay in place)

static uintptr_t g_cri;      // base of libcri_ware_unity.so once the layout was verified
static int g_cri_state;      // 0 = not resolved yet, 1 = layout verified, -1 = unknown layout (guard disabled)
static int g_logged;

static int find_cri(struct dl_phdr_info* i, size_t sz, void* d) {
    (void)sz;
    if (i->dlpi_name && strstr(i->dlpi_name, "libcri_ware_unity.so")) { *(uintptr_t*)d = i->dlpi_addr; return 1; }
    return 0;
}

static int cri_ready(void) {
    if (g_cri_state) return g_cri_state > 0;
    uintptr_t b = 0;
    dl_iterate_phdr(find_cri, &b);
    if (!b) return 0;  // not mapped (yet): nothing to guard, retry next call
    if (*(const uint32_t*)(b + CRI_CHECK_FN_RVA) == 0xa9be4ff4u && *(const uint32_t*)(b + CRI_CHECK_LDR_RVA) == 0xb947b100u) {
        g_cri = b;
        g_cri_state = 1;
    } else {
        g_cri_state = -1;
        KFLOG("cri guard disabled: unexpected libcri_ware_unity.so layout");
    }
    return g_cri_state > 0;
}

#ifdef KF_CRI_TEST
// TEST-ONLY builds (never shipped): simulate "no ACF registered" for the duration of this one call by zeroing the ACF state pointer
// and the registered flag (libcri_ware_unity.so .bss 0x1ca58c), then restoring both. KF_CRI_TEST=1: no guard -> calls the original
// (reproduces the production SIGSEGV @0x7b0, pc=cri+0x59d28). KF_CRI_TEST=2: guard active (the shipped logic).
#define CRI_ACF_FLAG_RVA 0x1ca58cu
#endif

i64 hook_csobj(i64 a, i64 b, i64 c, i64 d, i64 e, i64 f, i64 g, i64 h) {
#ifdef KF_CRI_TEST
    if (cri_ready()) {
        void* volatile* st = (void* volatile*)(g_cri + CRI_ACF_STATE_RVA);
        volatile uint32_t* fl = (volatile uint32_t*)(g_cri + CRI_ACF_FLAG_RVA);
        void* st0 = *st; uint32_t fl0 = *fl;
        *st = NULL; *fl = 0;
        i64 r = 0;
#if KF_CRI_TEST == 1
        KFLOG("TEST1: ACF state nulled, guard OFF -> original (expect SIGSEGV 0x7b0)");
        r = csobj_orig(a, b, c, d, e, f, g, h);
#else
        if (g_logged < 5) { g_logged++; KFLOG("TEST2: ACF state nulled, guard ON -> null sound object"); }
#endif
        *st = st0; *fl = fl0;
        return r;
    }
#endif
    if (cri_ready() && *(void* volatile*)(g_cri + CRI_ACF_STATE_RVA) == NULL) {
        if (g_logged < 5) { g_logged++; KFLOG("CreateSoundObject: CRIWARE ACF not registered, returning null sound object (would SIGSEGV @0x7b0)"); }
        return 0;
    }
    return csobj_orig(a, b, c, d, e, f, g, h);
}
