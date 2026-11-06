// Proof hook (Phase 1): logs and calls the original. Target RVA is chosen/verified when the NDK build is available.
#include "kfpatch.h"
#ifndef KF_PROOF_RVA
#define KF_PROOF_RVA 0
#endif
typedef int64_t i64;
KF_ORIG_STUB(proof, 0xa9bd57f6, 0xa9014ff4, 0xa9027bfd)  // stp x22,x21,[sp,#-0x30]!; stp x20,x19,[sp,#0x10]; stp x29,x30,[sp,#0x20]
#define orig_proof proof_orig
static i64 hook_proof(i64 a, i64 b, i64 c, i64 d, i64 e, i64 f, i64 g, i64 h) {
    KFLOG("proof hook this=%p a1=%p a2=%p", (void*)a, (void*)b, (void*)c);
    return orig_proof(a, b, c, d, e, f, g, h);
}
const kf_hook_t kf_hooks[] = {
#if KF_PROOF_RVA
    {0, KF_PROOF_RVA, (void*)hook_proof, &proof_ret},
#endif
    {0, 0, 0, 0},
};
const int kf_hook_count =
#if KF_PROOF_RVA
    1;
#else
    0;
#endif
void kf_register_hooks(void) {}
