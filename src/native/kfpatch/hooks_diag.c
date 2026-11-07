// Diagnostic hooks (KF_NATIVE_DIAG=1 at build, matching KF_NATIVE_DIAG hook entries in patch-il2cpp-endpoints.py).
#include <string.h>
#include "kfpatch.h"
#ifdef KF_NATIVE_DIAG
typedef int64_t i64;
extern uintptr_t g_base;
static const char* cname(i64 obj) {
    if (!obj) return "null";
    const char* const* k = *(const char* const**)obj;  // Il2CppObject.klass
    return k ? k[2] : "?";                             // Il2CppClass.name @ +0x10
}
KF_ORIG_STUB(inst, 0xa9bb67fa, 0xa9015ff8, 0xa90257f6)
KF_ORIG_STUB(ainit, 0xf81d0ff5, 0xa9014ff4, 0xa9027bfd)
KF_ORIG_STUB(gcomp, 0xd10103ff, 0xf9000bf5, 0xa9024ff4)
KF_ORIG_STUB(mnext, 0xf81d0ff5, 0xa9014ff4, 0xa9027bfd)
static i64 h_mnext(i64 a, i64 b, i64 c, i64 d, i64 e, i64 f, i64 g, i64 h) {
    i64 mi = **(i64**)(g_base + 0x43BC030);
    i64 mm = ((i64(*)(i64))(g_base + 0x2920B7C))(mi);
    i64 sm = mm ? *(i64*)(mm + 0x1D0) : 0;
    KFLOG("LoadModelAsync.MoveNext this=%p state=%d summonId=%d masterMgr=%p summonMaster=%p", (void*)a, *(int*)(a + 0x10), *(int*)(a + 0x20), (void*)mm, (void*)sm);
    return mnext_orig(a, b, c, d, e, f, g, h);
}
KF_ORIG_STUB(setm, 0xfc180fea, 0x6d0123e9, 0xa9026ffc)
KF_ORIG_STUB(sddr, 0xfc1c0fea, 0x6d00a3e9, 0xf9000ff5)
KF_ORIG_STUB(pidl, 0xf81d0ff5, 0xa9014ff4, 0xa9027bfd)
static i64 h_setm(i64 a, i64 b, i64 c, i64 d, i64 e, i64 f, i64 g, i64 h) {
    KFLOG("SetModel enter this=%p(%s) summon=%d type=%d", (void*)a, cname(a), (int)b, (int)c);
    {
        i64 mi = **(i64**)(g_base + 0x43BC030);
        i64 mm = ((i64(*)(i64))(g_base + 0x2920B7C))(mi);
        i64 sm = mm ? *(i64*)(mm + 0x1D0) : 0;
        i64 row = sm ? ((i64(*)(i64, int, i64))(g_base + 0x1A30A6C))(sm, (int)b, **(i64**)(g_base + 0x4472008)) : 0;
        if (row) {
            float (*gf)(i64, i64) = 0;
            KFLOG("summon row=%p gachaZ=%f ddX=%f ddY=%f ddZ=%f", (void*)row,
                  ((float(*)(i64,i64))(g_base + 0x16533F0))(row, 0), ((float(*)(i64,i64))(g_base + 0x1653430))(row, 0),
                  ((float(*)(i64,i64))(g_base + 0x1653440))(row, 0), ((float(*)(i64,i64))(g_base + 0x1653450))(row, 0));
            (void)gf;
        }
    }
    i64 r = setm_orig(a, b, c, d, e, f, g, h);
    KFLOG("SetModel leave");
    return r;
}
static i64 h_sddr(i64 a, i64 b, i64 c, i64 d, i64 e, i64 f, i64 g, i64 h) {
    KFLOG("SetDiscDetailRoot this=%p", (void*)a);
    return sddr_orig(a, b, c, d, e, f, g, h);
}
static i64 h_pidl(i64 a, i64 b, i64 c, i64 d, i64 e, i64 f, i64 g, i64 h) {
    KFLOG("PlayIdleLoopIfExist this=%p", (void*)a);
    return pidl_orig(a, b, c, d, e, f, g, h);
}
static i64 h_inst(i64 a, i64 b, i64 c, i64 d, i64 e, i64 f, i64 g, i64 h) {
    i64 r = inst_orig(a, b, c, d, e, f, g, h);
    KFLOG("InstantiateSummonModel(summon=%d model=%d parent=%p) -> %p (%s)", (int)b, (int)c, (void*)d, (void*)r, cname(r));
    return r;
}
static i64 h_ainit(i64 a, i64 b, i64 c, i64 d, i64 e, i64 f, i64 g, i64 h) {
    KFLOG("CharacterAnimatorBase.Initialize(this=%p(%s) animator=%p(%s) x2=%p)", (void*)a, cname(a), (void*)b, cname(b), (void*)c);
    return ainit_orig(a, b, c, d, e, f, g, h);
}
static i64 h_gcomp(i64 a, i64 b, i64 c, i64 d, i64 e, i64 f, i64 g, i64 h) {
    i64 r = gcomp_orig(a, b, c, d, e, f, g, h);
    if (strcmp(cname(a), "SummonModelController") == 0)
        KFLOG("GetComponent<T>(comp=%p(%s) mi=%p) -> %p (%s)", (void*)a, cname(a), (void*)b, (void*)r, cname(r));
    return r;
}
const kf_hook_t kf_hooks_diag[] = {
    {1, 0x1454714, (void*)h_inst, &inst_ret},
    {2, 0x16B5564, (void*)h_ainit, &ainit_ret},
    {3, 0x1912FB8, (void*)h_gcomp, &gcomp_ret},
    {4, 0x159E254, (void*)h_mnext, &mnext_ret},
    {5, 0x159D1BC, (void*)h_setm, &setm_ret},
    {6, 0x159DD1C, (void*)h_sddr, &sddr_ret},
    {7, 0x159DC54, (void*)h_pidl, &pidl_ret},
};
const int kf_hook_diag_count = 7;
#else
const kf_hook_t kf_hooks_diag[1] = {{0, 0, 0, 0}};
const int kf_hook_diag_count = 0;
#endif
