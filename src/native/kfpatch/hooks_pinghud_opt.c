// Ping HUD stage 2 (KF_PINGHUD_OPTION=1): Options > battle tab gets a "Ping HUD" ON/OFF row. Slot 27: SettingBattleCategoryView.Initialize
// 0x15E393C (I0 = str x27,[sp,#-0x60]!). After the original runs, the TapLockOn row (parent of _tapLockOn +0x40) is cloned once per view,
// relabelled, and its cloned ToggleSwitchButton gets a native Action<bool> (SetButtonAction 0x31C833C) that stores PlayerPrefs "kf_ping_hud".
#ifdef KF_PINGHUD_OPTION
#include <string.h>
#include "kfpatch.h"

typedef int64_t i64;
extern uintptr_t g_base;
extern int g_ping_on;
void* kf_il2cpp_str(const char* s);
int kf_ping_pref(void);

#define FN(ret, rva, ...) ((ret (*)(__VA_ARGS__))(g_base + (rva)))
typedef struct { float x, y, z; } v3_t;
typedef struct { float x, y; } v2f_t;

#ifndef KF_OPT_DY
#define KF_OPT_DY 0.0f                 // extra local y offset of the new row (tuned on the emulator)
#endif
KF_ORIG_STUB1(sbcv, 0xf81a0ffb)        // str x27,[sp,#-0x60]!

static void* g_opt_view;
static void* g_opt_row;
static void* g_opt_txt;                // cloned label (a LocalizeText component overwrites it with "Tap lock-on" after Start, so re-applied by kf_opt_tick)
static uint64_t g_fake_mi[16] __attribute__((aligned(16)));   // MethodInfo copy (0x80 B) whose methodPointer is the native callback

// m_CachedPtr survives native destruction; ask UnityEngine.Object.op_Implicit(Object) 0x23E1338 as well.
static int alive(void* o) {
    return o && *(void* volatile*)((char*)o + 0x10) != NULL && FN(int, 0x23E1338u, void*, void*)(o, NULL) != 0;
}
static int g_opt_ticks;                // remaining label re-apply ticks after add_row (LocalizeText overwrites within a few seconds)

// Action<bool> target: (closure/this, bool, MethodInfo*)
static void cb_ping(void* target, uint8_t v, void* mi) {
    (void)target; (void)mi;
    g_ping_on = v & 1;
    FN(void, 0x2772058u, void*, int, void*)(kf_il2cpp_str("kf_ping_hud"), g_ping_on, NULL);   // PlayerPrefs.SetInt
    FN(void, 0x2772580u, void*)(NULL);                                                          // PlayerPrefs.Save
    KFLOG("pinghud: option toggled -> %d", g_ping_on);
}

static void* type_of(void* obj) {   // System.Type object of obj's class
    void* klass = *(void* volatile*)obj;
    void* t = FN(void*, 0x1208D10u, void*)(klass);
    return FN(void*, 0x120A318u, void*)(t);
}

#ifdef KF_OPT_DUMP
typedef v2f_t v2_t;
static void nm(void* tr, char* out, int cap) {
    void* n = FN(void*, 0x23EB894u, void*, void*)(tr, NULL);
    int len = n ? *(int*)((char*)n + 0x10) : 0; int i = 0;
    for (; i < len && i < cap - 1; i++) out[i] = (char)((uint16_t*)((char*)n + 0x14))[i];
    out[i] = 0;
}
static void dump_children(void* tr, const char* tag) {
    int n = FN(int, 0x278B11Cu, void*, void*)(tr, NULL);
    char b[64]; nm(tr, b, sizeof b);
    KFLOG("optdump %s %s children=%d", tag, b, n);
    for (int i = 0; i < n && i < 40; i++) {
        void* c = FN(void*, 0x278B6C0u, void*, int, void*)(tr, i, NULL);
        v3_t p = FN(v3_t, 0x27896C4u, void*, void*)(c, NULL);
        v2_t sz = FN(v2_t, 0x2777D18u, void*, void*)(c, NULL);
        nm(c, b, sizeof b);
        KFLOG("optdump  [%d] %s pos(%.0f,%.0f) size(%.0f,%.0f)", i, b, p.x, p.y, sz.x, sz.y);
    }
}
#endif

static void add_row(void* view) {
    void* tap = *(void* volatile*)((char*)view + 0x40);
    void* txt = tap ? *(void* volatile*)((char*)tap + 0xC8) : NULL;   // ToggleSwitchButton._enableText (UnityEngine.UI.Text)
    if (!alive(tap) || !alive(txt)) { KFLOG("pinghud-opt: tap/text missing"); return; }
    void* tapTr = FN(void*, 0x29ADE84u, void*, void*)(tap, NULL);
    void* rowTr = FN(void*, 0x277944Cu, void*, void*)(tapTr, NULL);
    void* parTr = FN(void*, 0x277944Cu, void*, void*)(rowTr, NULL);
#ifdef KF_OPT_DUMP
    dump_children(parTr, "BattleSetting"); dump_children(FN(void*, 0x277944Cu, void*, void*)(parTr, NULL), "BattleSetting.parent");
    dump_children(rowTr, "row");
#endif
    void* crow = FN(void*, 0x23EF774u, void*, void*, int, void*)(rowTr, parTr, 0, NULL);   // Instantiate(rowTr, parent, false)
    if (!crow) return;
    // Rows are absolutely positioned in BattleSetting (verified dump: pitch 150, last row MoveAttackFreeCurve y=-1557, size 970x1615):
    // put the clone one pitch below the last row and grow BattleSetting by one pitch (Content re-lays out the sections below).
    int n = FN(int, 0x278B11Cu, void*, void*)(parTr, NULL);          // children before the clone was added: n-1 is the clone itself
    n -= 1;
    void* last = FN(void*, 0x278B6C0u, void*, int, void*)(parTr, n - 1, NULL);
    void* prev = FN(void*, 0x278B6C0u, void*, int, void*)(parTr, n - 2, NULL);
    v3_t p = FN(v3_t, 0x27896C4u, void*, void*)(last, NULL);
    float pitch = p.y - FN(v3_t, 0x27896C4u, void*, void*)(prev, NULL).y;   // negative
    p.y += pitch;
    v2f_t sd = FN(v2f_t, 0x2777D18u, void*, void*)(parTr, NULL);
    sd.y -= pitch;
    FN(void, 0x2777DB4u, void*, v2f_t, void*)(parTr, sd, NULL);        // RectTransform.set_sizeDelta
    FN(void, 0x2789768u, void*, v3_t, void*)(crow, p, NULL);
    void* ctrTr = FN(void*, 0x278B3C4u, void*, void*, void*)(crow, kf_il2cpp_str("TapLockOnToggle"), NULL);   // Transform.Find
    void* ctxTr = FN(void*, 0x278B3C4u, void*, void*, void*)(crow, kf_il2cpp_str("Text"), NULL);
    void* ctog = ctrTr ? FN(void*, 0x29ADF5Cu, void*, void*, void*)(ctrTr, type_of(tap), NULL) : NULL;   // Component.GetComponent(Type)
    void* ctxt = ctxTr ? FN(void*, 0x29ADF5Cu, void*, void*, void*)(ctxTr, type_of(txt), NULL) : NULL;
    KFLOG("pinghud-opt: row %p toggle %p text %p", crow, ctog, ctxt);
    g_opt_txt = ctxt; g_opt_ticks = 12;
    if (ctxt) FN(void, 0x266BC94u, void*, void*, void*)(ctxt, kf_il2cpp_str("Ping HUD"), NULL);   // UI.Text.set_text
    if (ctog) {
        void* klass = **(void* volatile***)(g_base + 0x43D93F0);                  // System.Action<bool> TypeInfo (GOT slot, as in Initialize)
        void** mi0 = **(void** volatile**)(g_base + 0x44889C8);                    // MethodInfo of <Initialize>b__3 (template)
        void* ctorMi = **(void** volatile**)(g_base + 0x4417500);                  // Method$Action<bool>..ctor
        memcpy(g_fake_mi, mi0, sizeof g_fake_mi);
        g_fake_mi[0] = (uint64_t)(uintptr_t)cb_ping;
        void* d = FN(void*, 0x1223ED8u, void*)(klass);                             // il2cpp object alloc (as in Initialize)
        FN(void, 0x223F0D4u, void*, void*, void*, void*)(d, view, g_fake_mi, ctorMi);   // Action<bool>..ctor(target, MethodInfo)
        FN(void, 0x31C833Cu, void*, void*, void*)(ctog, d, NULL);                  // ToggleSwitchButton.SetButtonAction
        FN(void, 0x31C8404u, void*, int, void*)(ctog, kf_ping_pref(), NULL);       // SetColorActive(isOn)
    }
    g_opt_view = view; g_opt_row = crow;
}

void kf_opt_reset(void) { g_opt_ticks = 0; g_opt_txt = NULL; g_opt_row = NULL; g_opt_view = NULL; }   // new battle HUD: forget any settings UI from an earlier scene

void kf_opt_tick(void) {               // ~1 Hz from hook_settime while the battle HUD runs
    if (g_opt_ticks <= 0) { g_opt_txt = NULL; g_opt_row = NULL; g_opt_view = NULL; return; }   // never touch the settings UI outside its first seconds
    g_opt_ticks--;
    if (alive(g_opt_view) && alive(g_opt_row) && alive(g_opt_txt)) FN(void, 0x266BC94u, void*, void*, void*)(g_opt_txt, kf_il2cpp_str("Ping HUD"), NULL);
    else { g_opt_txt = NULL; g_opt_row = NULL; g_opt_view = NULL; g_opt_ticks = 0; }
}

i64 hook_sbcv_init(i64 a, i64 b, i64 c, i64 d, i64 e, i64 f, i64 g, i64 h) {
    i64 r = sbcv_orig(a, b, c, d, e, f, g, h);
    void* view = (void*)a;
    if (view && (view != g_opt_view || !alive(g_opt_view) || !alive(g_opt_row))) add_row(view);
    return r;
}
#endif
