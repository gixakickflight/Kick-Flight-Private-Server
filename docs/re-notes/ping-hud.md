# Ping HUD (2.11.806, libkfpatch stage 1)

Shows "NN ms" (Photon ping) under the battle timer and hides the WiFi alert icon. Default ON (`PlayerPrefs` int `kf_ping_hud`, default 1).

Verified RVAs (libil2cpp 2.11.0, sha256 c92a03cc...daa0):
- `TimeView.SetTime(int)` 0x161DE68 (hook slot 26, I0 `eb2bba6d`); fields `_minutes` +0x30, `_seconds` +0x38, `_colon` +0x40.
- `PhotonManager.GetPing()` 0x18D659C.
- `RadiowaveAlertView.SetVisible` 0x16C41FC; in-place patch 0x16C4220 `81020012` -> `01008052` (icon never visible).
- Veneer host: dead body of `PlayerCharacter.UpdateLookTarget`, slot 26 veneer at 0x13CDAE8 (24 B).
- HUD hierarchy: Canvas > Panel > Time > GameTime > Second. The clone of `_seconds` is re-parented under "Canvas" (world pose kept) and set last sibling, otherwise the minimap draws over it.
- Unity helpers: Object.get_name 0x23EB894, Transform.get_parent 0x277944C, SetParent 0x278A224, SetAsLastSibling 0x278B1F4, PlayerPrefs.GetInt 0x277216C.

## Stage 2 (Options toggle, `KF_PINGHUD_OPTION=1`; hooks_pinghud_opt.c, slot 27, veneer 0x13CDB00)
Hook `SettingBattleCategoryView.Initialize` 0x15E393C (I0 `fb0f1af8`). Verified on the emulator (in-battle menu button > Settings):
- Layout dump: Content > {SoundSetting, BattleSetting 970x1615, GraphicSetting}; BattleSetting rows are absolutely positioned, pitch 150 (TapLockOn y=-657 ... MoveAttackFreeCurve y=-1557). The clone of the TapLockOn row goes one pitch below the last row and BattleSetting grows by one pitch (`RectTransform.set_sizeDelta` 0x2777DB4); Content re-lays out the Graphics section.
- Row clone = `Object.Instantiate(rowTransform, parent, false)`; children `Text` (UnityEngine.UI.Text, type taken from `_enableText` +0xC8) and `TapLockOnToggle` (type from `_tapLockOn`) found with `Transform.Find` 0x278B3C4 + `Component.GetComponent(Type)` 0x29ADF5C (Type via il2cpp_class_get_type 0x1208D10 + il2cpp_type_get_object 0x120A318).
- `SetButtonAction(ToggleSwitchButton, Action<bool>, mi)` takes the Action<bool> directly. A native Action<bool> is built like Initialize does: object alloc 0x1223ED8 on the class at GOT 0x43D93F0, ctor 0x223F0D4 with a copy of the `<Initialize>b__3` MethodInfo (GOT 0x44889C8) whose first qword is the native callback, ctor MethodInfo GOT 0x4417500. Callback (target, bool, mi): `PlayerPrefs.SetInt("kf_ping_hud")` 0x2772058 + `Save` 0x2772580.
- The clone's label is overwritten with "Tap lock-on" by a LocalizeText after Start, so it is re-applied at ~1 Hz from the SetTime hook (`kf_opt_tick`).
- Il2CppDelegate layout (Action<bool>::.ctor 0x223F0D4): method_ptr +0x10, m_target +0x20, method +0x28.
- `KF_OPT_DUMP=1` adds a hierarchy dump to logcat (tag optdump) for the layout work.

## Layout (final)
Ping sits BELOW the enemy team list (right column), on a dark translucent box like the timer. HUD tree: Canvas > Panel > Member > EnemyTeam (centre pivot, 100 wide, rows `EnemyTeamMemberIcon(Clone)` 100x82, pitch 89, local x 0, y -41,-130,-219,-308; Member/MyTeam is the left twin, Menu/LeftRoot/MenuButton the round menu button).
- Dark box: `TimeView._bg` (+0x60) is the Image on the `GameTime` object itself (colour 0,0,0,0.8; it also parents Minute/Second/colon), so it cannot be Instantiate()d. A new GameObject "KFPingBg" is built (object alloc 0x1223ED8 on GameObject klass, ctor(string) 0x23DE48C, AddComponent(Type) 0x23DE6D4 with the Type of `_bg`'s class), then sprite (0x2491DB8), image type (0x2492084), colour (0x248A810) and layer (0x23DED00) are copied from `_bg`; raycastTarget off (0x248A860). Box 150x36, child of EnemyTeam; the text clone is its child (scale 1.3, rect 111x28). The old `<mark>` backing is removed (did not render).
- Position: `place_bg()` every ~1 Hz tick, right-aligned with the column (right edge = colW/2 + 8 (aligned with the HP bars)), y = last ACTIVE row y - rowH/2 - 32 gap - 18 (skips the box itself, which is also a child of EnemyTeam; follows 1v1/3v3/4v4). The 32 gap clears the red flag circle that hangs under the last row.
- Visibility toggle (Options) switches the box GameObject; the text is a child.
- Fallback (EnemyTeam or `_bg` missing): KFLOG `pinghud: fallback - ...`, text-only clone re-parented to Canvas below the timer (old behaviour).
- Verified on the emulator (320x640, Rapid Ball bot match): `.local/run/shots/I-final.png`, crop `.local/run/shots/I-final-crop.png`. `KF_PING_DUMP=1` dumps `pingdump` lines (bg object, EnemyTeam rows).
