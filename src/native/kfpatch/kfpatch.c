// libkfpatch.so: fills the il2cpp-side slot table and builds "call original" stubs.
// Load: System.loadLibrary("kfpatch") from smali (Application.attachBaseContext), before Unity starts.
// The constructor starts a short-lived watcher thread that waits for libil2cpp.so to be mapped.
#define _GNU_SOURCE
#include <link.h>
#include <pthread.h>
#include <string.h>
#include <sys/mman.h>
#include <time.h>
#include <unistd.h>
#include "kfpatch.h"

uintptr_t g_base;

static int find_il2cpp(struct dl_phdr_info* i, size_t sz, void* d) {
    (void)sz; (void)d;
    if (i->dlpi_name && strstr(i->dlpi_name, "libil2cpp.so")) { g_base = i->dlpi_addr; return 1; }
    return 0;
}

// caves_gen.S (scripts/re/kfpatch_cavegen.py): migrated prod caves. Pool = absolute il2cpp addresses, slots = cave entries.
extern uint64_t kf_pool[];
extern const uint64_t kf_pool_rva[];
extern const int kf_pool_count;
extern const uint64_t kf_cave_slot_table[];
extern const int kf_cave_slot_count;

static void* watcher(void* arg) {
    (void)arg;
    for (int i = 0; i < 20000 && !g_base; i++) {  // up to ~20 s, 1 ms steps
        dl_iterate_phdr(find_il2cpp, NULL);
        if (!g_base) { struct timespec ts = {0, 1000000}; nanosleep(&ts, NULL); }
    }
    if (!g_base) { KFLOG("libil2cpp.so not found, hooks disabled"); return NULL; }
    uint64_t* slots = (uint64_t*)(g_base + KF_IL2CPP_SLOT_BASE_RVA);
    for (int i = 0; i < kf_pool_count; i++) kf_pool[i] = g_base + kf_pool_rva[i];  // before any slot is armed
    for (int i = 0; i < kf_cave_slot_count; i++) slots[kf_cave_slot_table[2 * i]] = (uint64_t)(uintptr_t)kf_cave_slot_table + kf_cave_slot_table[2 * i + 1];
    kf_register_hooks();
    for (int i = 0; i < kf_hook_count; i++) {
        const kf_hook_t* h = &kf_hooks[i];
        if (h->ret_slot) *h->ret_slot = g_base + h->target_rva + 12;
        slots[h->slot] = (uint64_t)h->hook;  // written last
    }
    for (int i = 0; i < kf_hook_diag_count; i++) {
        const kf_hook_t* h = &kf_hooks_diag[i];
        if (h->ret_slot) *h->ret_slot = g_base + h->target_rva + 12;
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
