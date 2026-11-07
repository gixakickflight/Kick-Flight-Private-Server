// libkfpatch.so: fills the il2cpp-side slot table and builds "call original" stubs.
// Load: System.loadLibrary("kfpatch") from smali (Application.attachBaseContext), before Unity starts.
// The constructor starts a short-lived watcher thread that waits for libil2cpp.so to be mapped.
#define _GNU_SOURCE
#include <link.h>
#include <pthread.h>
#include <stdio.h>
#include <string.h>
#include <sys/mman.h>
#include <time.h>
#include <unistd.h>
#include "kfpatch.h"

uintptr_t g_base;
static uintptr_t g_unity;

static int find_il2cpp(struct dl_phdr_info* i, size_t sz, void* d) {
    (void)sz; (void)d;
    if (i->dlpi_name && strstr(i->dlpi_name, "libil2cpp.so")) { g_base = i->dlpi_addr; return 1; }
    return 0;
}

static int find_unity(struct dl_phdr_info* i, size_t sz, void* d) {
    (void)sz; (void)d;
    if (i->dlpi_name && strstr(i->dlpi_name, "libunity.so")) { g_unity = i->dlpi_addr; return 1; }
    return 0;
}

// Arms the libunity slot table (hooks_unity.c). libunity is mapped by UnityPlayer before libil2cpp, so this normally runs first; its
// hook sites (preload dependency walker, scene-load refcount release) are reached only after the first scene starts loading.
static void log_unity_maps(void) {
    FILE* f = fopen("/proc/self/maps", "r");
    if (!f) return;
    char line[512];
    int n = 0;
    while (n < 12 && fgets(line, sizeof line, f)) if (strstr(line, "libunity.so")) { KFLOG("maps: %s", line); n++; }
    fclose(f);
}

// Returns 1 once the slots were written. The slot page is the zero remainder of libunity's last (bss) page; check that it is really
// mapped before touching it (mincore -> ENOMEM otherwise) and retry later if not.
static int arm_unity(void) {
    uint64_t* uslots = (uint64_t*)(g_unity + KF_UNITY_SLOT_BASE_RVA);
    unsigned char vec;
    if (mincore((void*)((uintptr_t)uslots & ~(uintptr_t)0xfff), 4096, &vec) != 0) return 0;
    for (int i = 0; i < kf_hook_unity_count; i++) {
        const kf_hook_t* h = &kf_hooks_unity[i];
        if (h->ret_slot) *h->ret_slot = g_unity + h->target_rva + KF_HOOK_DISP(h);
        __atomic_store_n(&uslots[h->slot], (uint64_t)h->hook, __ATOMIC_RELEASE);  // written last, after ret_slot
    }
    KFLOG("libunity base=%p, %d hook(s) armed", (void*)g_unity, kf_hook_unity_count);
    return 1;
}

// caves_gen.S (scripts/re/kfpatch_cavegen.py): migrated prod caves. Pool = absolute il2cpp addresses, slots = cave entries.
extern uint64_t kf_pool[];
extern const uint64_t kf_pool_rva[];
extern const int kf_pool_count;
extern const uint64_t kf_cave_slot_table[];
extern const int kf_cave_slot_count;

static void* watcher(void* arg) {
    (void)arg;
    // Never give up: the hook sites in libil2cpp are statically patched veneers (`br [slot]`), so an unfilled slot is a null jump.
    // libil2cpp may be mapped long after this library loads (process started by an FCM push without an Activity; Unity only
    // starts when the user later opens the game). 2.11.800 gave up after 20 s -> SIGSEGV pc=0 / x16=0 in GameScene.
    // Fast phase: 1 ms steps for ~20 s (normal cold start). Slow phase: 20 ms steps until the library appears (all hook sites are
    // battle-time code, seconds after libil2cpp is mapped, so a <=20 ms fill delay is safe).
#ifdef KF_FILL_DELAY_MS
    // TEST-ONLY builds (never shipped): leave every slot (libunity and il2cpp) at 0 for KF_FILL_DELAY_MS so the hook sites run their
    // fail-safe path (original displaced code) instead of the hooks. Verifies the 2.11.804 veneers: no `br 0`.
    KFLOG("TEST BUILD: delaying all slot fills by %d ms (hooks must fall through to the original code)", KF_FILL_DELAY_MS);
    for (int left = KF_FILL_DELAY_MS; left > 0; left -= 100) { struct timespec d = {0, 100000000L}; nanosleep(&d, NULL); }
    KFLOG("TEST BUILD: delay over, filling slots now");
#endif
    int unity_done = 0;
    for (unsigned i = 0; !g_base; i++) {
        if (!unity_done) {
            dl_iterate_phdr(find_unity, NULL);
            if (g_unity) {
                unity_done = arm_unity();
                if (!unity_done && (i % 500) == 0) { KFLOG("libunity base=%p: slot page not mapped yet (try %u)", (void*)g_unity, i); log_unity_maps(); }
            }
        }
        dl_iterate_phdr(find_il2cpp, NULL);
        if (g_base) break;
        struct timespec ts = {0, i < 20000u ? 1000000L : 20000000L};
        nanosleep(&ts, NULL);
    }
    for (unsigned k = 0; !unity_done && k < 5000; k++) {  // libil2cpp is mapped but libunity's slot page still is not: keep trying a few s
        dl_iterate_phdr(find_unity, NULL);
        if (g_unity) unity_done = arm_unity();
        if (!unity_done) { struct timespec ts = {0, 1000000L}; nanosleep(&ts, NULL); }
    }
    if (!unity_done) { KFLOG("libunity slot page unmapped, unity guards disabled"); log_unity_maps(); }
    uint64_t* slots = (uint64_t*)(g_base + KF_IL2CPP_SLOT_BASE_RVA);
    for (int i = 0; i < kf_pool_count; i++) kf_pool[i] = g_base + kf_pool_rva[i];  // before any slot is armed
    for (int i = 0; i < kf_cave_slot_count; i++) slots[kf_cave_slot_table[2 * i]] = (uint64_t)(uintptr_t)kf_cave_slot_table + kf_cave_slot_table[2 * i + 1];
    kf_register_hooks();
    for (int i = 0; i < kf_hook_count; i++) {
        const kf_hook_t* h = &kf_hooks[i];
        if (h->ret_slot) *h->ret_slot = g_base + h->target_rva + KF_HOOK_DISP(h);
        slots[h->slot] = (uint64_t)h->hook;  // written last
    }
    for (int i = 0; i < kf_hook_diag_count; i++) {
        const kf_hook_t* h = &kf_hooks_diag[i];
        if (h->ret_slot) *h->ret_slot = g_base + h->target_rva + KF_HOOK_DISP(h);
        slots[h->slot] = (uint64_t)h->hook;
    }
    KFLOG("il2cpp base=%p, %d hook(s) armed (+%d diag)", (void*)g_base, kf_hook_count, kf_hook_diag_count);
    return NULL;
}

__attribute__((constructor)) static void kf_init(void) {
    KFLOG("libkfpatch loaded");
    pthread_t t;
    if (pthread_create(&t, NULL, watcher, NULL) == 0) pthread_detach(t);
}
