// kfpatch: native patch library for the Kick-Flight client (arm64-v8a). See docs/re-notes/kfpatch-design.md.
#pragma once
#include <stdint.h>
#include <android/log.h>

#define KF_TAG "KFPATCH"
#define KFLOG(...) __android_log_print(ANDROID_LOG_INFO, KF_TAG, __VA_ARGS__)  // never UnityEngine.Debug.Log

// Slot table lives in libil2cpp.so .bss tail (see design doc). Static trampolines in il2cpp do
//   adrp x16, SLOT_PAGE ; ldr x16, [x16, #SLOT_OFF] ; br x16
// so the slot must hold the hook entry before any hooked code runs.
#define KF_IL2CPP_SLOT_BASE_RVA 0x4982C00u
#define KF_MAX_HOOKS 64

typedef struct {
    uint32_t slot;          // index into the slot table
    uint32_t target_rva;    // RVA of the hooked il2cpp function (needed to build the "call original" stub)
    void*    hook;          // replacement; receives the original args (AAPCS64), x16 clobbered by the trampoline
    uint64_t* ret_slot;     // KF_ORIG_STUB's resume-address word; filled with target+12 once libil2cpp is mapped (may be NULL)
} kf_hook_t;

// Call-original stub, built at compile time (no RWX pages: ndk_translation faults on code written at runtime).
// I0..I2 = the three displaced instruction words (little-endian u32 of the original bytes, checked PC-independent by
// scripts/re/kfpatch_tramp.py and byte-guarded by the patcher). Usage: KF_ORIG_STUB(foo, 0xa9bd57f6, ...) then call
// foo_orig(a0..a7) (typed int64 x8; cast results as needed) and list &foo_ret as ret_slot.
#define KF_ORIG_STUB(NAME, I0, I1, I2)                                                      \
    __attribute__((visibility("hidden"))) uint64_t NAME##_ret;                              \
    __attribute__((visibility("hidden"), naked))                                            \
    int64_t NAME##_orig(int64_t a0, int64_t a1, int64_t a2, int64_t a3, int64_t a4, int64_t a5, int64_t a6, int64_t a7) { \
        __asm__ volatile(".inst " #I0 "\n.inst " #I1 "\n.inst " #I2 "\n"                   \
                         "adrp x16, " #NAME "_ret\n"                                        \
                         "ldr x16, [x16, :lo12:" #NAME "_ret]\n"                            \
                         "br x16\n");                                                       \
    }

void kf_register_hooks(void);        // hooks_*.c
extern const kf_hook_t kf_hooks[];
extern const int kf_hook_count;
extern const kf_hook_t kf_hooks_diag[];
extern const int kf_hook_diag_count;
