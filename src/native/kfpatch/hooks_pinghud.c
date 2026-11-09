// Battle HUD ping (docs/re-notes/ping-hud.md). Slot 26: Colorful.TimeView.SetTime(int) 0x161DE68 (called by TimePresenter.<Bind>b__4_0 on
// every remaining-time change, i.e. ~1 Hz). After the original runs, a clone of TimeView._seconds (TextMeshProUGUI, +0x38) is created once per
// TimeView, parked below the timer, and its text is set to "NN ms" from PhotonManager.GetPing() (0x18D659C). Visibility = g_ping_on, read once
// from PlayerPrefs.GetInt("kf_ping_hud", 1) (default ON); stage 2 (hooks_pinghud_opt.c) flips it from the Options toggle.
// All managed calls are plain il2cpp method pointers (base + RVA, MethodInfo* = NULL) made from the Unity main thread inside a managed frame.
#include <stdio.h>
#include <string.h>
#include "kfpatch.h"

typedef int64_t i64;
typedef struct { float x, y, z; } v3_t;
typedef struct { float x, y; } v2f_t;
extern uintptr_t g_base;

#define RVA_STRING_NEW 0x120A2A4u     // il2cpp_string_new (exported thunk)
#define RVA_GETPING 0x18D659Cu        // PhotonManager.GetPing() static int
#define RVA_GETINT 0x277216Cu         // PlayerPrefs.GetInt(string,int)
#define RVA_INSTANTIATE_P 0x23EF774u  // Object.Instantiate(Object, Transform, bool)
#define RVA_GET_GO 0x29ADEF0u         // Component.get_gameObject
#define RVA_GET_TR 0x29ADE84u         // Component.get_transform
#define RVA_GET_PARENT 0x277944Cu     // Transform.get_parent
#define RVA_GET_LPOS 0x27896C4u       // Transform.get_localPosition (Vector3 in s0-s2)
#define RVA_SET_LPOS 0x2789768u
#define RVA_GET_LSCALE 0x2789FB8u
#define RVA_SET_LSCALE 0x278A05Cu
#define RVA_SET_LAST_SIBLING 0x278B1F4u
#define RVA_SET_PARENT_W 0x278A224u   // Transform.SetParent(Transform, bool worldPositionStays)
#define RVA_GO_SETACTIVE 0x23DED74u
#define RVA_TMP_SETTEXT 0x19ABA80u
#define RVA_TMP_AUTOSIZE 0x19AC8F4u   // TMP_Text.set_enableAutoSizing(bool)

#ifndef KF_PING_DY
#define KF_PING_DY 36.0f      // local-space offset below _seconds (tuned on the emulator; the minimap sits right under the timer)
#endif
#ifndef KF_PING_DX
#define KF_PING_DX 0.0f       // to the right of the timer box, between it and the red flag icon
#endif
#ifndef KF_PING_SCALE
#define KF_PING_SCALE 1.3f
#endif
typedef struct { float r, g, b, a; } col_t;
#define RVA_TMP_SETCOLOR 0x19ABF70u   // TMP_Text.set_color(Color)
#define RVA_TMP_SETALPHA 0x19AC020u   // TMP_Text.set_alpha(float)
#define RVA_TMP_OUTLINE_C 0x19AC45Cu  // TMP_Text.set_outlineColor(Color32)
#define RVA_TMP_OUTLINE_W 0x19AC5B8u  // TMP_Text.set_outlineWidth(float)
#define RVA_TMP_STYLE 0x19ACA2Cu      // TMP_Text.set_fontStyle(FontStyles); 1 = Bold
#define OFF_SECONDS 0x38
#define OFF_COLON 0x40
#define OFF_CACHEDPTR 0x10            // UnityEngine.Object.m_CachedPtr: 0 once the native object is destroyed

KF_ORIG_STUB1(settime, 0x6dba2beb)    // stp d11,d10,[sp,#-0x60]!   (site = `b V`, resumes at +4)

#define FN(ret, rva, ...) ((ret (*)(__VA_ARGS__))(g_base + (rva)))
void* kf_il2cpp_str(const char* s) { return FN(void*, RVA_STRING_NEW, const char*)(s); }

int g_ping_on = -1;                   // -1 = not read yet; shared with the Options toggle (stage 2)
static void* g_view;                  // TimeView the clone belongs to
static void* g_clone;                 // cloned TextMeshProUGUI

// m_CachedPtr is NOT cleared when Unity destroys the native object (stale wrappers survive scene changes), so also ask
// UnityEngine.Object.op_Implicit(Object) 0x23E1338 (= native alive check).
static int alive(void* o) {
    return o && *(void* volatile*)((char*)o + OFF_CACHEDPTR) != NULL && FN(int, 0x23E1338u, void*, void*)(o, NULL) != 0;
}

int kf_ping_pref(void) {
    if (g_ping_on < 0) g_ping_on = FN(int, RVA_GETINT, void*, int, void*)(kf_il2cpp_str("kf_ping_hud"), 1, NULL) ? 1 : 0;
    return g_ping_on;
}

// Name of a UnityEngine.Object equals an ASCII literal? (System.String: length +0x10, UTF-16 chars +0x14)
static int name_is(void* o, const char* lit) {
    void* nm = FN(void*, 0x23EB894u, void*, void*)(o, NULL);   // Object.get_name
    if (!nm) return 0;
    int n = *(int*)((char*)nm + 0x10);
    if (n != (int)strlen(lit)) return 0;
    for (int k = 0; k < n; k++) if (((uint16_t*)((char*)nm + 0x14))[k] != (uint16_t)lit[k]) return 0;
    return 1;
}

// HUD hierarchy (verified on the emulator): Canvas > Panel > Time > GameTime > Second. The minimap is drawn after the timer's own
// siblings, so the clone is re-parented (world pose kept) under "Canvas" as its last child to stay on top.
static void* find_canvas(void* tr) {
    for (int i = 0; tr && i < 8; i++) {
        if (name_is(tr, "Canvas")) return tr;
        tr = FN(void*, RVA_GET_PARENT, void*, void*)(tr, NULL);
    }
    return NULL;
}

// Readable on any background (the minimap sits right under the timer): opaque white, bold, black outline.
static void style_clone(void* c) {
    col_t w = {1.0f, 1.0f, 1.0f, 1.0f};
    FN(void, RVA_TMP_SETCOLOR, void*, col_t, void*)(c, w, NULL);
    FN(void, RVA_TMP_SETALPHA, void*, float, void*)(c, 1.0f, NULL);
    FN(void, RVA_TMP_STYLE, void*, int, void*)(c, 1, NULL);
    FN(void, RVA_TMP_OUTLINE_C, void*, uint32_t, void*)(c, 0xFF000000u, NULL);   // Color32 r,g,b,a = 0,0,0,255
    FN(void, RVA_TMP_OUTLINE_W, void*, float, void*)(c, 0.25f, NULL);
}

#ifdef KF_PING_DUMP
static void dump_enemy(void* canvas, void* view);
static void dnm(void* tr, char* out, int cap) {
    void* n = FN(void*, 0x23EB894u, void*, void*)(tr, NULL);
    int len = n ? *(int*)((char*)n + 0x10) : 0; int i = 0;
    for (; i < len && i < cap - 1; i++) out[i] = (char)((uint16_t*)((char*)n + 0x14))[i];
    out[i] = 0;
}
static void dump_tree(void* tr, int depth, int maxd) {
    int n = FN(int, 0x278B11Cu, void*, void*)(tr, NULL);
    for (int i = 0; i < n && i < 60; i++) {
        void* c = FN(void*, 0x278B6C0u, void*, int, void*)(tr, i, NULL);
        char b[48]; dnm(c, b, sizeof b);
        v3_t p = FN(v3_t, RVA_GET_LPOS, void*, void*)(c, NULL);
        v2f_t sz = FN(v2f_t, 0x2777D18u, void*, void*)(c, NULL);
        v2f_t ap = FN(v2f_t, 0x2777BE4u, void*, void*)(c, NULL);
        KFLOG("hudtree %*s%s lpos(%.0f,%.0f) anch(%.0f,%.0f) size(%.0f,%.0f) kids=%d", depth * 2, "", b, p.x, p.y, ap.x, ap.y, sz.x, sz.y, FN(int, 0x278B11Cu, void*, void*)(c, NULL));
        if (depth + 1 < maxd) dump_tree(c, depth + 1, maxd);
    }
}
static void dump_enemy(void* canvas, void* view) {
    void* bg = *(void* volatile*)((char*)view + 0x60);
    if (alive(bg)) {
        void* btr = FN(void*, RVA_GET_TR, void*, void*)(bg, NULL);
        char b[48], pn[48]; dnm(btr, b, sizeof b);
        void* par = FN(void*, RVA_GET_PARENT, void*, void*)(btr, NULL); dnm(par, pn, sizeof pn);
        void* sprite = FN(void*, 0x2491DB0u, void*, void*)(bg, NULL);
        KFLOG("pingdump bg=%s parent=%s kids=%d sprite=%p", b, pn, FN(int, 0x278B11Cu, void*, void*)(btr, NULL), sprite);
    } else KFLOG("pingdump bg dead");
    void* et = FN(void*, 0x278B3C4u, void*, void*, void*)(canvas, kf_il2cpp_str("Panel/Member/EnemyTeam"), NULL);
    if (!et) { KFLOG("pingdump no EnemyTeam"); return; }
    v3_t ep = FN(v3_t, RVA_GET_LPOS, void*, void*)(et, NULL);
    v2f_t esz = FN(v2f_t, 0x2777D18u, void*, void*)(et, NULL);
    v2f_t epv = FN(v2f_t, 0x2777E4Cu, void*, void*)(et, NULL);
    KFLOG("pingdump EnemyTeam lpos(%.0f,%.0f) size(%.0f,%.0f) pivot(%.2f,%.2f)", ep.x, ep.y, esz.x, esz.y, epv.x, epv.y);
    int n = FN(int, 0x278B11Cu, void*, void*)(et, NULL);
    for (int i = 0; i < n && i < 8; i++) {
        void* c = FN(void*, 0x278B6C0u, void*, int, void*)(et, i, NULL);
        v3_t p = FN(v3_t, RVA_GET_LPOS, void*, void*)(c, NULL);
        v3_t w = FN(v3_t, 0x2789584u, void*, void*)(c, NULL);
        v2f_t sz = FN(v2f_t, 0x2777D18u, void*, void*)(c, NULL);
        KFLOG("pingdump row%d lpos(%.0f,%.0f) world(%.1f,%.1f) size(%.0f,%.0f)", i, p.x, p.y, w.x, w.y, sz.x, sz.y);
    }
}
#endif

static void* type_of(void* obj) {   // System.Type object of obj's class
    void* klass = *(void* volatile*)obj;
    return FN(void*, 0x120A318u, void*)(FN(void*, 0x1208D10u, void*)(klass));
}

static void* g_bg_go;                 // KFPingBg (dark box; parent of the text), toggled instead of the text
static void* g_bg_tr;
#define KF_BG_W 150.0f
#define KF_BG_H 36.0f
#define KF_BG_RIGHT 8.0f              // the HP bars stick out ~8 units past the 100-wide row rect: right-align with the bars
#define KF_BG_GAP 32.0f               // gap between the last enemy row and the box (clears the red flag circle under the row)

// Keep the box right-aligned under the enemy column (Canvas/Panel/Member/EnemyTeam; rows are 100x82, pitch 89, centre pivot): y follows the
// last active row so 1v1 / 3v3 / 4v4 all work. Called every tick, so a late layout pass is picked up.
static void place_bg(void) {
    void* et = FN(void*, RVA_GET_PARENT, void*, void*)(g_bg_tr, NULL);
    int n = FN(int, 0x278B11Cu, void*, void*)(et, NULL);
    float y = 0.0f, h = 82.0f; int found = 0;
    for (int i = n - 1; i >= 0; i--) {
        void* ch = FN(void*, 0x278B6C0u, void*, int, void*)(et, i, NULL);
        if (ch == g_bg_tr || name_is(ch, "KFPingBg")) continue;   // our own box is a child of the same container
        void* go = FN(void*, RVA_GET_GO, void*, void*)(ch, NULL);
        if (!FN(int, 0x23DEDE8u, void*, void*)(go, NULL)) continue;
        y = FN(v3_t, RVA_GET_LPOS, void*, void*)(ch, NULL).y;
        v2f_t sz = FN(v2f_t, 0x2777D18u, void*, void*)(ch, NULL);
        if (sz.y > 1.0f) h = sz.y;
        found = 1; break;
    }
    if (!found) { y = 41.0f; h = 82.0f; }   // no rows yet: park just under the column top
    v2f_t ew = FN(v2f_t, 0x2777D18u, void*, void*)(et, NULL);
    float w = ew.x > 1.0f ? ew.x : 100.0f;
    v3_t q = {w * 0.5f + KF_BG_RIGHT - KF_BG_W * 0.5f, y - h * 0.5f - KF_BG_GAP - KF_BG_H * 0.5f, 0.0f};
    FN(void, RVA_SET_LPOS, void*, v3_t, void*)(g_bg_tr, q, NULL);
    {   static float ly = 1e9f;
        if (q.y != ly) { ly = q.y; v3_t w = FN(v3_t, 0x2789584u, void*, void*)(g_bg_tr, NULL);
            KFLOG("pinghud: bg lpos(%.0f,%.0f) world(%.2f,%.2f) rows=%d lastY=%.0f h=%.0f et=%p active=%d", q.x, q.y, w.x, w.y, n, y, h, et, FN(int, 0x23DEE54u, void*, void*)(g_bg_go, NULL)); } }
}

static void make_clone(void* view) {
    void* sec = *(void* volatile*)((char*)view + OFF_SECONDS);
    void* bgImg = *(void* volatile*)((char*)view + 0x60);   // TimeView._bg: the timer's dark box (Image on the GameTime object itself)
    if (!alive(sec)) { KFLOG("pinghud: skip - _seconds dead (view %p)", view); return; }
    void* secTr = FN(void*, RVA_GET_TR, void*, void*)(sec, NULL);
    void* ptr = FN(void*, RVA_GET_PARENT, void*, void*)(secTr, NULL);
    void* root = find_canvas(ptr);
    void* et = root ? FN(void*, 0x278B3C4u, void*, void*, void*)(root, kf_il2cpp_str("Panel/Member/EnemyTeam"), NULL) : NULL;
    void* c = FN(void*, RVA_INSTANTIATE_P, void*, void*, int, void*)(sec, ptr, 0, NULL);
    if (!c) { KFLOG("pinghud: skip - Instantiate returned null"); return; }
    void* tr = FN(void*, RVA_GET_TR, void*, void*)(c, NULL);
    g_bg_go = NULL; g_bg_tr = NULL;
    if (et && alive(bgImg)) {
        // The timer box is the Image on GameTime (it has the TimeView + digits as children), so it cannot be Instantiate()d: build a new
        // GameObject with an Image of the same class and copy sprite / colour / image type from it.
        void* secGo = FN(void*, RVA_GET_GO, void*, void*)(sec, NULL);
        void* go = FN(void*, 0x1223ED8u, void*)(*(void* volatile*)secGo);
        FN(void, 0x23DE48Cu, void*, void*, void*)(go, kf_il2cpp_str("KFPingBg"), NULL);
        void* img = FN(void*, 0x23DE6D4u, void*, void*, void*)(go, type_of(bgImg), NULL);   // GameObject.AddComponent(Type)
        if (img) {
            FN(void, 0x23DED00u, void*, int, void*)(go, FN(int, 0x23DEC94u, void*, void*)(FN(void*, RVA_GET_GO, void*, void*)(sec, NULL), NULL), NULL);   // layer
            FN(void, 0x2491DB8u, void*, void*, void*)(img, FN(void*, 0x2491DB0u, void*, void*)(bgImg, NULL), NULL);              // Image.sprite
            FN(void, 0x2492084u, void*, int, void*)(img, FN(int, 0x249207Cu, void*, void*)(bgImg, NULL), NULL);                  // Image.type
            FN(void, 0x248A860u, void*, int, void*)(img, 0, NULL);                                                                // raycastTarget = false
            col_t bc = FN(col_t, 0x248A804u, void*, void*)(bgImg, NULL);                                                          // Graphic.color
            FN(void, 0x248A810u, void*, col_t, void*)(img, bc, NULL);
            KFLOG("pinghud: bg colour (%.2f,%.2f,%.2f,%.2f)", bc.r, bc.g, bc.b, bc.a);
            void* btr = FN(void*, RVA_GET_TR, void*, void*)(img, NULL);
            FN(void, RVA_SET_PARENT_W, void*, void*, int, void*)(btr, et, 0, NULL);
            v2f_t bsz = {KF_BG_W, KF_BG_H};
            FN(void, 0x2777DB4u, void*, v2f_t, void*)(btr, bsz, NULL);
            v3_t one = {1.0f, 1.0f, 1.0f};
            FN(void, RVA_SET_LSCALE, void*, v3_t, void*)(btr, one, NULL);
            FN(void, RVA_SET_LAST_SIBLING, void*, void*)(btr, NULL);
            g_bg_go = go; g_bg_tr = btr;
        }
    }
    if (g_bg_tr) {
        FN(void, RVA_SET_PARENT_W, void*, void*, int, void*)(tr, g_bg_tr, 0, NULL);
        v3_t z = {0.0f, 0.0f, 0.0f};
        FN(void, RVA_SET_LPOS, void*, v3_t, void*)(tr, z, NULL);
        v3_t sc = {KF_PING_SCALE, KF_PING_SCALE, 1.0f};
        FN(void, RVA_SET_LSCALE, void*, v3_t, void*)(tr, sc, NULL);
        v2f_t sd = {KF_BG_W / KF_PING_SCALE - 4.0f, KF_BG_H / KF_PING_SCALE};
        FN(void, 0x2777DB4u, void*, v2f_t, void*)(tr, sd, NULL);
        place_bg();
    } else {
        KFLOG("pinghud: fallback - EnemyTeam/_bg missing (et=%p bg=%p), text-only under the timer/Canvas", et, bgImg);
        v3_t p = FN(v3_t, RVA_GET_LPOS, void*, void*)(secTr, NULL);
        p.y -= KF_PING_DY;
        FN(void, RVA_SET_LPOS, void*, v3_t, void*)(tr, p, NULL);
        v3_t sc = {KF_PING_SCALE, KF_PING_SCALE, 1.0f};
        FN(void, RVA_SET_LSCALE, void*, v3_t, void*)(tr, sc, NULL);
        if (root) FN(void, RVA_SET_PARENT_W, void*, void*, int, void*)(tr, root, 1, NULL);
        FN(void, RVA_SET_LAST_SIBLING, void*, void*)(tr, NULL);
        v2f_t sd = {170.0f, 44.0f};
        FN(void, 0x2777DB4u, void*, v2f_t, void*)(tr, sd, NULL);
    }
    FN(void, RVA_TMP_AUTOSIZE, void*, int, void*)(c, 0, NULL);
    style_clone(c);
    g_view = view; g_clone = c;
    KFLOG("pinghud: clone %p of seconds %p (view %p) bg=%p", c, sec, view, g_bg_go);
}

#ifdef KF_PINGHUD_OPTION
void kf_opt_tick(void);
void kf_opt_reset(void);
#endif
i64 hook_settime(i64 a, i64 b, i64 c, i64 d, i64 e, i64 f, i64 g, i64 h) {
    i64 r = settime_orig(a, b, c, d, e, f, g, h);
    void* view = (void*)a;
    if (view) {
#ifdef KF_PINGHUD_OPTION
        kf_opt_tick();
#endif
        if (view != g_view || !alive(g_clone)) {
            g_clone = NULL; g_bg_go = NULL; g_bg_tr = NULL;
#ifdef KF_PINGHUD_OPTION
            kf_opt_reset();
#endif
            make_clone(view);
        }
#ifdef KF_PING_DUMP
        { static int tk; tk++; if ((tk == 4 || tk == 25) && alive(g_clone)) { void* cv = find_canvas(FN(void*, RVA_GET_TR, void*, void*)(g_clone, NULL)); if (cv) dump_enemy(cv, view); } }
#endif
        if (alive(g_clone)) {
            void* go = alive(g_bg_tr) ? g_bg_go : FN(void*, RVA_GET_GO, void*, void*)(g_clone, NULL);
            if (alive(g_bg_tr)) place_bg();
            int on = kf_ping_pref();
            FN(void, RVA_GO_SETACTIVE, void*, int, void*)(go, on, NULL);
            if (on) {
                FN(void, RVA_TMP_SETALPHA, void*, float, void*)(g_clone, 1.0f, NULL);   // never inherit a faded alpha
                char buf[160];
                static int last = -1;
                int ping = FN(int, RVA_GETPING, void*)(NULL);
                if (ping != last) { last = ping; KFLOG("pinghud: ping=%d ms", ping); }   // logged on change only
                const char* col = ping < 80 ? "7CFC00" : ping < 150 ? "FFE000" : "FF5050";   // green / yellow / red
                snprintf(buf, sizeof buf, "<align=center><nobr><color=#%s>%d ms</color></nobr></align>", col, ping);
                FN(void, RVA_TMP_SETTEXT, void*, void*, void*)(g_clone, kf_il2cpp_str(buf), NULL);
            }
        }
    }
    return r;
}
