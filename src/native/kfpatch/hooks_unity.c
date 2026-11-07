// Crash families 4 and 5 (docs/re-notes/exception-review-2.11.800.md): libunity.so 2018.4.11f1 (arm64) guards. Hooks are reached through
// libunity trampolines (patch-il2cpp-endpoints.py UNITY_PATCHES, slots in the libunity .bss tail, RVA KF_UNITY_SLOT_BASE_RVA).
//
// Family 4 (SIGSEGV UnityPreload, pc libunity+0x2cab34): dependency walker 0x2bf2b0 passes an Object* from its {id, object, list} work
// stack to 0x2c3070(obj, id, out), which does `ldr w9,[obj+0xf8+8]`. In production the Object* is garbage (4, 0x14, 0x4297ea6d3dacc19a...
// recycled heap), faulting at 0x104/0x114/non-canonical. Guard: reject an implausible pointer and return false ("not found"), which the
// walker already handles (it falls through to the list scan / returns null). Does NOT fix the underlying use-after-free/race.
// Family 5 (SIGSEGV/SIGBUS UnityMain, pc libunity+0x2ba84c): 0x2ba824(this, elem, newBlock, ...) releases the ref-counted block
// `old = [elem+0x200]` (ldxr/stlxr on old+4). Production old is garbage (0xff000000, ...odd values). Guard: if `old` is implausible, clear
// the slot first (the function then takes its `old == NULL` path and skips the release; the garbage block is leaked, never freed).
#define _GNU_SOURCE
#include <link.h>
#include <string.h>
#include "kfpatch.h"

typedef int64_t i64;
typedef uint64_t u64;

// Sites are a single `b V` (fail-safe veneers in a dead libunity function, scripts/re/kfpatch_veneer.py): only I0 is replayed, resume +4.
// walk: libunity+0x2c3070  sub sp,sp,#0x50   (then stp x25,x24 / stp x23,x22 stay in place)
KF_ORIG_STUB1(uwalk, 0xd10143ff)
// rel: libunity+0x2ba824   str x24,[sp,#-0x40]!   (then stp x23,x22 / stp x21,x20 stay in place)
KF_ORIG_STUB1(urel, 0xf81c0ff8)

// Plausible user-space heap pointer on arm64 Android: >= 4 GiB (every real-device Unity address is far above 16 GB), 8-byte aligned,
// bits 48..55 clear (top byte may carry a TBI/MTE tag). Observed garbage in production fails at least one of these.
static inline int bad_ptr(u64 p) {
    return p < 0x100000000ull || (p & 7u) || ((p >> 48) & 0xffu);
}

// KF_UNITY_TEST (TEST-ONLY builds, never shipped): 1 = walk without guard + injected bad object (reproduces SIGSEGV @0x104, pc=0x2cab34),
// 2 = walk with guard + same injection, 3 = release without guard + injected bad block (SIGSEGV pc=0x2ba84c), 4 = release with guard.
// The injection fires once, on the 50th call after the first hit.
#ifdef KF_UNITY_TEST
#define KF_UNITY_INJECT_AFTER 50
#if KF_UNITY_TEST == 1 || KF_UNITY_TEST == 3
#define KF_UNITY_GUARD 0
#else
#define KF_UNITY_GUARD 1
#endif
#else
#define KF_UNITY_GUARD 1
#endif

static u64 g_walk_calls, g_walk_rej, g_rel_calls, g_rel_rej;

i64 hook_uwalk(i64 a, i64 b, i64 c, i64 d, i64 e, i64 f, i64 g, i64 h) {
    u64 n = __atomic_add_fetch(&g_walk_calls, 1, __ATOMIC_RELAXED);
    if (n == 1) KFLOG("unity walk hook: first call (obj=%p)", (void*)a);
#if defined(KF_UNITY_TEST) && (KF_UNITY_TEST == 1 || KF_UNITY_TEST == 2)
    if (n == KF_UNITY_INJECT_AFTER) {
        KFLOG("SYNTHETIC TEST%d: injecting obj=0x4 (production value) for call #%llu, guard %s", KF_UNITY_TEST, (unsigned long long)n, KF_UNITY_GUARD ? "ON" : "OFF");
        a = 4;
    }
#endif
#if KF_UNITY_GUARD
    if (bad_ptr((u64)a)) {
        u64 r = __atomic_add_fetch(&g_walk_rej, 1, __ATOMIC_RELAXED);
        if (r <= 8) KFLOG("unity walk guard: rejected obj=%p id=%lld (reject #%llu of %llu calls) -> not found", (void*)a, (long long)b, (unsigned long long)r, (unsigned long long)n);
        return 0;
    }
#endif
    return uwalk_orig(a, b, c, d, e, f, g, h);
}

i64 hook_urel(i64 a, i64 b, i64 c, i64 d, i64 e, i64 f, i64 g, i64 h) {
    u64 n = __atomic_add_fetch(&g_rel_calls, 1, __ATOMIC_RELAXED);
    if (n == 1) KFLOG("unity release hook: first call (elem=%p)", (void*)b);
    volatile u64* slot = (volatile u64*)((char*)b + 0x200);
#if defined(KF_UNITY_TEST) && (KF_UNITY_TEST == 3 || KF_UNITY_TEST == 4)
    if (n >= KF_UNITY_INJECT_AFTER && *slot != 0 && !bad_ptr(*slot)) {
        static int done;
        if (!__atomic_exchange_n(&done, 1, __ATOMIC_RELAXED)) {
            KFLOG("SYNTHETIC TEST%d: injecting old block=0xff000000 (production value) over %p for call #%llu, guard %s", KF_UNITY_TEST, (void*)*slot, (unsigned long long)n, KF_UNITY_GUARD ? "ON" : "OFF");
            *slot = 0xff000000ull;
        }
    }
#endif
#if KF_UNITY_GUARD
    u64 old = *slot;
    if (old != 0 && bad_ptr(old)) {
        u64 r = __atomic_add_fetch(&g_rel_rej, 1, __ATOMIC_RELAXED);
        if (r <= 8) KFLOG("unity release guard: rejected old block=%p in elem=%p (reject #%llu of %llu calls) -> cleared", (void*)old, (void*)b, (unsigned long long)r, (unsigned long long)n);
        *slot = 0;
    }
#endif
    return urel_orig(a, b, c, d, e, f, g, h);
}

// Slots in the libunity .bss tail (patch-il2cpp-endpoints.py KF_UNITY_HOOKS): 0 = walk, 1 = release.
const kf_hook_t kf_hooks_unity[] = {
    {0, 0x2C3070, (void*)hook_uwalk, &uwalk_ret, 4},
    {1, 0x2BA824, (void*)hook_urel, &urel_ret, 4},
};
const int kf_hook_unity_count = 2;
