# Static RE helpers for `libil2cpp.so` (arm64)

Used to trace the offline battle-start handshake and the AI blockers (see
`docs/CONTINUATION_PROMPT_BATTLE_CRASH_FIX_V2.md` §7). Everything works on the **pristine**
`libil2cpp.so` from `base.apk` plus the Il2CppDumper output in
`Kick-Flight-Assets/server_revival_analysis/il2cpp/` (RVA == file offset for this binary).

Setup (once):

```bash
pip install capstone
python -c "import zipfile;zipfile.ZipFile('../Kick-Flight-Assets/base.apk').extract('lib/arm64-v8a/libil2cpp.so','.local/re')"
python scripts/re/a64dis.py 0x156E8E0 40   # first run builds .local/re-cache/symbols.pkl from script.json (slow, once)
python scripts/re/reloc.py                 # builds .local/re-cache/reloc.pkl so string/metadata slots resolve
```

`KF_CLEAN_LIBIL2CPP` / `KF_SCRIPT_JSON` override the default input paths.

| Tool | What it does |
| --- | --- |
| `a64dis.py <rva> [n]` | annotated disassembly: method names on `bl`, string literals / TypeInfo / Method* on `adrp`+`ldr` |
| `xref.py <rva>...` | who `bl`/`b`s to these RVAs (virtual calls are `blr` and do not show up) |
| `reloc.py [--find "Text"]` | parse `.rela.dyn`; `--find` prints the slots that point at a string literal |
| `slotref.py <slot>...` | which code materialises a data slot (feed it the slots from `reloc.py --find`) |
| `bundle_tree.py <logical> [--filter re] [--all]` | transform hierarchy (local pos/rot/scale) of every prefab in a catalog bundle (`player/pc_005/pc_005_001`, `weapon/wp_005/wp_005_001_001`); repairs the Octo header via `../Kick-Flight-Assets/reconstruct_unity_bundles.py` |
| `anim_paths.py <kicker> --weapons 001_001,001_201` | clip generic bindings are CRC32 of the full transform path: explains the body bones and brute-forces `<bone>/<rootName>/<weapon node>` for animated weapons → the `Weapon` master bone + rootName |
| `meta_fields.py <Class>...` | il2cpp class fields straight out of `global-metadata.dat` (`--search`, `--dump-fields`): the served master JSON keys are the client's own field names, so this is the table that answers what a master row looks like |
| `served_master.py <Table>` | fetch and decrypt a served master from the running server (uses the direct-client Host header) |
| `bot_special_skill_cave.py` | keystone source of the production "AI casts its special skill" cave (prints replacement/expected hex for the patch table) |

Vtable slot N of an object is at `[klass + 0x128 + N*16]` (the `ldp x9, x1, [x8, #0x178]; blr x9` pattern
is slot 5). IL2CPP class-init boilerplate (`ldrb w8,[x0,#0x127]` ... `bl 0x11f7160`) can be filtered out.

## Runtime tracing

Build with `KF_DIAG=1` to include `DIAG_PATCHES_ARM64` from `patch-il2cpp-endpoints.py`, then read
`adb logcat -s KFDIAG` (values: 1xx UpdateRoomState, 2xx UpdatePlayerState, 3xx BeginAsync coroutine state,
4xx RoomState per UpdateState tick, 6xx `_enableAi` store, 700 AIPlayerEngine.Start, plus the return addresses of
every NRE raise). Resolve addresses with `/proc/<pid>/maps` (low 32 bits of the logged value minus the module base).

Spectator member selection probes (2026-09-29): `9101` enters the member-icon callback
`SpectatorInfoPresenter.<>c__DisplayClass7_0.<Initialize>b__1`, `9102` enters
`GameManager.InitializeSpectatorPlayer`, and `9103` enters `GameManager.SetMainPlayer`. These fixed values
contain no account data. If a tap produces no `9101`, the Unity button callback did not fire; `9101` without
`9102` means the callback took its replay branch; `9102` without `9103` means `InitializeSpectatorPlayer`
returned before setting the target (for example, its spectator gate or null-object guard). `9103` confirms the
selected player reached the manager setter. Their caves occupy the free tail `0x13CE5A4..0x13CE624` of the entry-stubbed
`UpdateIdleTypeRate` body, after the production costume refresh cave.

Rules learned the hard way:

- Never call `UnityEngine.Debug.Log*` from patched code on the x86_64 AVD: its stack-trace capture walks the
  cave frames (no unwind info) and crashes in `libunity.so+0x34426c`. `__android_log_print` (PLT `0x10D6270`)
  is safe.
- Function-entry hooks must use `b` (a `bl` clobbers the caller's LR before the prologue saves it) and the cave
  jumps back to entry+4 after re-executing the displaced instruction. Mid-function hooks may use `bl`+`ret`.
- Free dead code for caves: body of `PlayerBoneController.SetDisplayAngles` (0x13BC318-0x13BC3A8) and
  `GameManager.GetMenuType` (0x1570EB0-0x15710A4); the latter's entry branches to a compatibility cave that returns
  menu type 6 only for Photon `TeamColorType=2` (spectator) and 0 otherwise. Nothing branches into the body (verified).
  `HomeSummonModelController.SetModel` (0x159D1BC-0x159DAB0, entry-stubbed)
  holds the production bot-special-skill cave from 0x159D200 (376 bytes); the rest of its body is free.

## DIAG probe sets (attack, warp, bombs, reconnect)

* `patch-il2cpp-endpoints.py` — async scene-state probes. HomeScene emits `8399` at initial state `-1`, then
  `8400+state` on each `PreBeginAsync.MoveNext` entry; `8420`/`8421` bracket the state-machine pointer low/high
  halves. TitleScene uses `8459`, `8460+state`, and pointer markers `8480`/`8481`. Join each pointer pair and read
  the pending state at object offset `+0x10` while the spinner is visible. These mid-function hooks are after the
  prologue saves LR; their caves replay the displaced state load and branch to the original continuation after
  logging. The scene-transition probe emits `8500+state` and pointer markers `8520`/`8521` from
  `GRE.SceneManager.<_ChangeSceneAsync>d__33.MoveNext`; state 7 awaits `LoadSceneAsync`, state 8 awaits
  `PreBeginSceneAsync`. A `ret` after a cave's `bl` logger would return into the cave, not the MoveNext caller.
* `scripts/re/attack_diag_caves.py` — KFDIAG 8100-8600: combo index of every swing (`8100+n`), target lost (`8200`),
  search-distance result (`8300/8301`), the four `IsAttack` angle checks with |delta| and limit (`8320-8323`),
  `IsAttack` true (`8310`), and which "in" animation played (`8400/8500/8600 + combo`). Minimal caves in the dead
  bodies of the entry-stubbed `PlayerBoneController.InterpolationUpdate` / `SetPose`.
  Read them with `python scripts/re/attack_diag_read.py [logcat.txt]` (collapses per-frame repeats).
* `scripts/re/warp_diag_caves.py` — KFDIAG 7700-7830: `JapaneseSwordSkillAction` no-target end position vs the
  player position, `WarpSkillAction.UpdateFinish` gates, `PlayerCharacter.SetVisible` pointers.
* `scripts/re/reconnect_diag_caves.py` — KFDIAG 8901-8992, the Photon-disconnect freeze. `CallbackDisconnected`
  entry on the object that owns the registered Photon callback: `8901` then the cause, `8902+|_isInitialized`,
  `8904+|byte44`, `8906+|_reconnectInfo != null`, `8908+|_offlineReplayTarget != null`. The same four on the object
  `GameManager.Initialize` ran on, just after `InitializeReconnect` returned: `8921` then `8922`/`8924`/`8926` (the
  pair 8901/8921 is what tells "the callback owner is not the object Initialize ran on" from "its fields were
  cleared"). `8961` marks every frame `UpdateReconnect` saw `_reconnectInfo == NULL` (silent while healthy).
  Also `SetReconnectState` (`8950+n`), the reconnect RPC round trip (`8981` `CallbackRejoinedRoom`, `8982`/`8983`
  the two `SendReconnectShared*`, `8984`/`8985` the two `ReceiveReconnectShared*`), `IsReconnectEnable` (`8930` +
  the room `RoomState`, `8940` + the failing cause, `8971` + each player's index and `PlayerState`) and `8992` +
  `SetReconnectFailedCause`, which named the empty-room mastership bug (patch `0010` in `docs/PHOTON_SERVER.md`). The docstring in the script carries the full reasoning, including why the two
  `<>4__this` guards cannot fire.
* `scripts/re/replay_diag_caves.py` — KFDIAG 9303-9338, replay recording/upload/playback (KF_PHOTON only):
  `BeginSession` / `CanBeginSession` result / recording session created / `EndSession` / `StartPlayback`
  entered, and the four `ResultManager.<UploadReplayDataAsync>` gates (`IsMyPlayerMaster`,
  `IsUploadReplayData`, empty `BattleId`, raw `ReplayWriteResult.WriteState`). The same script also emits the
  production Photon cave that re-implements the real `ReplayManager.get_ReplayMode` (the offline stub's body
  keeps the `CharacterBase.IsMine` cave and the 940+ `SetState` probe). Caves live in return-stubbed bodies
  (`List<LocalClient.InternalMsg>.Contains`, `<CallbackBattleStartSuccess>b__46_0`).
* Regenerate entries with the script and paste them before `# ---- END DIAGNOSTIC ----` in
  `patch-il2cpp-endpoints.py` (replace the whole previous block: caves are re-packed and move); build with
  `KF_DIAG=1 OUT=.local/KickFlight-2.11.0-DIAG.apk bash .local/build.sh` (Tanuki's machine) or
  `KF_DIAG=1 scripts/build-direct-apk.sh` / `.ps1` anywhere. `KF_NRE_LR=1` adds the NRE-origin hook (cave H).
  Values 8790+state (current PlayerStateType) and 8800+destroy type / 8810+hits (`CollisionBase.OnDestroy`) were
  added 2026-09-20; the latter pinned the 1-1-1 combo bug to `collisionHitType All` (see AGENTS.md).
* Dead bodies in use (all entry-stubbed in production, nothing branches into them): `HomeSummonModelController.SetModel`
  (0x159D200-0x159DAB0: gym/AI cave, GetDisplayAngles cave, warp probes), `UnloadModel` (region F),
  `PlayerBoneController.InterpolationUpdate` / `SetPose` / `LateUpdate` (attack probes),
  `PlayerCharacter.UpdateLookTarget` (0x13CD990-0x13CDFE0: regions D + E incl. the safe LOG helper, moved there by
  `scripts/re/relocate_diag_region.py` on 2026-09-20 — they used to sit in `LoadManager.LoadDeckSummonModel`, which
  forced the DIAG build to stub it and so no summon model ever loaded: Leorex/MOVE/TRAP froze the kicker in the
  skill state and it never respawned). `UpdateIdleTypeRate` (0x13CE3E0-0x13CE7C8): DIAG attack probes at
  0x13CE3E0-0x13CE4B0, the **production** bat-bomb TrapInfo cave (`BatAbilityParameter..ctor` tail, 60 bytes) at
  0x13CE500-0x13CE540 (64 bytes); the **production** guarded unlimited-costume archive refresh cave now occupies
  0x13CE540-0x13CE5A4. The former Jay bomb DIAG probes (9001-9041) were retired and their hooks removed; the
  historical generator exits instead of writing caves into that reserved range.
  `HomeSummonModelController.SetModel` tail 0x159DA48-0x159DA78 holds the production bat-bomb HitInfo fallback cave
  (0x159DA78-0x159DAB0: DIAG reconnect probe 8992, not free in DIAG builds).
  `GameManager.<BeginAsync>b__4` (entry-stubbed, 0x1579AC8-0x1579B80): the **production** ready-gate cave at
  0x1579AD0-0x1579B14 and the **production** 3D-listener cave (`ObjectManager.ReceiveAddPlayer` tail ->
  `GameManager.SetListener(main player)`) at 0x1579B14-0x1579B74; 0x1579B74-0x1579B80 free. `<BeginAsync>b__1`
  (entry-stubbed, 0x1579948-0x157999C) holds the **production** rush-pursuit follow-through cave at
  0x1579948-0x1579984 (`scripts/re/rush_pursuit_cave.py`, hooked from `MoveAttackSkillAction.HitCallback` 0x145AC64);
  0x1579984-0x1579994 holds the **production** bounded-player-wait deadline store; 0x1579994-0x157999C free.
  `<BeginAsync>b__3` (0x1579A38-0x1579AC8) is no longer dead: it is rewritten in place as the **production** bounded
  player wait (`scripts/re/begin_async_wait_cave.py`, IsCreatedPlayer or 10 s). DIAG region G (6410/6411/6420/6421)
  moved out of it on 2026-10-06 to `SetModel` 0x159D1C0-0x159D200 and 0x159D764-0x159D784 and the dead `GetMenuType`
  body 0x1570EF8-0x1570F18; the wait's DIAG exit probe (9300 created / 9301 timeout / 9302 skip) sits at
  0x1570FB8-0x1570FF0 (all four windows branch-swept: nothing outside the dead bodies lands in them).
* `scripts/re/error_report_caves.py` — **production** client error reporting (all flavours): in-match anomalies
  (`CrashReportManager.SetAnalysisException` hook 0x17CAEBC) and managed exceptions (`ExceptionHandler.HandleLog`
  type-4 hook 0x30E1C28) go to `/analysis/index` through `AnalysisManager.RegisterGetRegionFailed` instead of
  Crashlytics. Dead body: `Firebase.Crashlytics.ExceptionHandler.getMessageParts` 0x30E1CD4-0x30E1F70 (668 B), whose
  only caller is `HandleLog`+0x54, i.e. the instruction after hook C; entry-stubbed `mov x0, xzr; ret` in every
  flavour, no other incoming branch (full sweep), only data ref is its methodPointers entry. Layout: stub
  0x30E1CD4-0x30E1CDC, production blob (cave C then B) 0x30E1CDC-0x30E1E10; in KF_DIAG builds the instrumented copy
  (KFDIAG 9701-9712) replaces it at the same offset up to 0x30E1E44, then the 9700 `Register` probe
  0x30E1E44-0x30E1E54 and the 9720 `RegisterGetRegionFailed` entry probe 0x30E1E54-0x30E1E6C; 0x30E1E6C-0x30E1F70
  (260 B) free. The caves also use the layout padding of `CrashReportManager` (+0x11 re-entry flag, +0x12/+0x13
  per-session counters) as writable state.
* `scripts/re/title_diag_caves.py` — KFDIAG 9400-9490 (DIAG only), title-screen probes: per-frame `ColorfulManager.ChangeScene`
  from `BootScene.ManagedUpdate` (9400), `<ChangeSceneAsync>` state (9410+s), `SceneManager.ChangeScene` `_isChanging`
  gate (9420+f), `TitleScene.PreBeginAsync` entry + raw LR (9430), `TitleView.Initialize` liveness of the view and
  `_canvasGroup` before `set_alpha` (9440+bits), `set_alpha` returned (9460), `TitleView.OnDestroy` (9470),
  `LoadSprite` completion (9490+bits). Caves fill the previously free `0x30E1E6C-0x30E1F70` of the same dead
  `getMessageParts` body (now 0 B free); hooks at 0x15C9E6C, 0x31EA4B0, 0x1CAC914, 0x31C3DDC, 0x31C797C, 0x31C798C,
  0x31C7DB8, 0x16EB024. The 9500-9549 range is taken by `kicker_diag_caves.py`.
* `scripts/re/rush_push_cave.py` — **production** rush-pursuit "push in front" (2026-10-10): `PlayerStateEntrained`
  `UpdateAction` 0x17D7474 / `IsFinish` 0x17D79DC `bl` hooks, keyed on the MoveAttack attach timer (`PlayerCharacter`
  +0x340 > 0). Chained caves in four free tails, all verified free in the 8 flavours and branch-swept:
  0x1A2AC1C-0x1A2AC50 (`PhotonPropertyManagerBase<object>.CheckInitializeError`, full), 0x159E32C-0x159E354
  (`<LoadModelAsync>d__25.MoveNext`; 20 B prod / 28 B DIAG), 0x31C49A4-0x31C49D4 (`TitleView.SetAllButtonActive`; 36 B),
  and DIAG-only 0x13CE7A4-0x13CE7BC (`UpdateIdleTypeRate` tail). KFDIAG 9800 = push ran this frame, 9801 = IsFinish held
  past the stock 0.5 s (9800-9819 reserved). Remaining free after this: 0x1A2AC1C-0x1A2AC50 none, 0x159E354 none,
  0x31C49C4-0x31C49D4 (16 B), 0x13CE7BC-0x13CE7C8 (12 B), 0x13ED970-0x13ED998 (40 B; 0x13ED970-0x13ED98C now used
  by boot_once_cave.py, 12 B left).
* `scripts/re/boot_once_cave.py` — **production** title-screen root-cause fix (2026-10-11): `BootScene.ManagedUpdate`
  (0x15C9E1C, entry hook `b`) requests `ChangeScene(Title)` once per BootScene instance (latch byte BootScene+0x47,
  allocation padding) instead of every frame. The ~15 `TitleScene.PreBeginAsync` runs came from the production patch
  `bypass _isChangeScene check` (0x1CAC918), which disables the gate that dropped the duplicate requests in stock.
  Cave 0x13ED970-0x13ED98C (dead tail of `<CallbackBattleStartSuccess>b__46_0`).
* `HomeSummonModelController.SetModel` is entry-stubbed, so `SummonAnimator` (+0x40) / `ModelController` (+0x48) /
  `ModelTransform` (+0x38) stay null. Audit of every consumer (2026-10-11): guarded already = `SetGachaRoot`,
  `SetDiscDetailRoot`, `GetBone`, `SetBrightness`, `UnloadModel`, `OutGameModelController.UnloadSummonModel`,
  `SkillDescriptionWindow.OnManagedUpdate`+0x204; patched in place (no cave) = `OutGameModelController.CreateSummonModel`
  0x1552DAC, `PlayIdleLoopIfExist` 0x159DCBC, `PlayHomeAnim` 0x159E0BC (-> epilogue at 0x159E5AC),
  `SkillDescriptionWindow.OnManagedUpdate` 0x183974C and `<PlayCloseModel>b__77_0` 0x183F12C (skip `SetOutlineWidthScale`).
* `scripts/re/_disfull.py` = `a64dis.py` that does not stop at the first `ret` (whole-function listings).
