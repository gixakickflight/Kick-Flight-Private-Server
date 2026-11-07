# Disc detail 3D model empty (2026-11-05)

Status: root cause narrowed, fix NOT done. Evidence from emulator runs (KickFlight_API35, local server), logs in `.local/run/`.

Prod patchset stubs `HomeSummonModelController.SetModel` (0x159D1BC), `<LoadModelAsync>d__25.MoveNext` (0x159E254) and
`UnloadModel` (0x159DF5C) (commit 2eeb399, no crash evidence recorded) and uses their dead bodies as cave hosts:
0x159D200 (468 B gym mannequins), 0x159D3E0 (44 B GetDisplayAngles), 0x159DA48 (48 B BatAbility HitInfo),
0x159DF70 (88 B PlayerStateSkill.End tail), 0x159E25C/0x159E294 (56 B each smog), 0x159E32C (20 B rush push).
Hooks: 0x13BEAEC, 0x13BC3A8, 0x1439AEC, 0x17F8F1C, 0x184961C, 0x184983C, 0x1A2AC44.
Union of free dead-body bytes across all flag modes is far below the ~780 B prod + DIAG needs (only UpdateLookTarget
0x13CD984 is free in prod, DIAG fills it), so a relocation needs a two-layout design or new hosts.

Experiments (env `KF_SETMODEL_EXPERIMENT=1`, `KF_SETMODEL_EXPERIMENT_ALL=1`; battle features broken, diagnosis only):
* exp1 (only SetModel live): NRE top frame `ModelManager.InstantiateSummonModel` (bl at 0x159D984) -> bundle not loaded
  because LoadModelAsync is stubbed.
* exp2 (all three bodies live): SetModel now succeeds; NRE moves to `HomeSummonModelController.PlayIdleLoopIfExist`
  (likely `CharacterAnimatorBase.HasState`, `this+0x10` Animator == null). SetModel gets the Animator with
  `GetComponent<Animator>()` on the instantiated model root (0x159DA60); a null Animator is passed to
  `CharacterAnimatorBase.Initialize`. Disc detail box is still empty on screen.
Open: why the instantiated summon model has no Animator on its root (prefab/bundle variant `_1` vs `_0/_2`, or
InstantiateSummonModel parent/layer), and where the disc-detail root/camera puts it (discDetailPosition*/Rotation* are
all 0.0 in masters_summon.json).

## Update 2026-11-05 (native route)
* Prefab check (bundles repaired via Kick-Flight-Assets/reconstruct_unity_bundles.repair_bundle_bytes, then UnityPy): `summon/sm_0001_1` and `_0` roots have
  Transform + Animator + MonoBehaviour, 37 GameObjects, 1 SkinnedMeshRenderer. A missing Animator on the root is therefore not a content bug.
* libkfpatch Phase 1 works (docs/re-notes/kfpatch-design.md). A hook on CreateSummonModel (0x1552CDC) fires when a disc detail opens with args (this, 9, 1).
* Remaining work is porting SetModel/LoadModelAsync to C; KF_SETMODEL_EXPERIMENT is kept (diagnosis-only, off by default).

## Update 2026-11-05 (root cause found)
* Item A: SEGV @0x31 came from the pre-fix KF_ORIG_STUB (void prototype). KF_NATIVE proof build (kf-proof2.apk sha 2f90e909...ac7e) = 0 CRASH over 5 disc-detail opens.
* Earlier "SearchTargetInfo.Copy fault 0x19" in all-live experiments was the AI-roles caves living in the SetModel dead body (KF_AI_ROLES defaults ON; experiment must set KF_AI_ROLES=0 KF_AI_ROLE_TARGETING=0 KF_AI_ROLE_DEPOSIT=0). Not a disc-detail bug.
* With SetModel/MoveNext/UnloadModel live (exp9) the whole chain works: LoadModelAsync MoveNext (state0 -> bundle load -> state1), SetModel -> InstantiateSummonModel -> GetComponent<Animator> ok -> CharacterAnimatorBase.Initialize -> SetDiscDetailRoot -> PlayIdleLoopIfExist.
* Box still empty because SetModel sets the small RenderTexture camera ortho size = DiscDetailPositionZ * 0.8 (float @0x3227238) and the big one = GachaPositionZ * CanvasHeightRate; masters_summon.json had all of them 0. Setting discDetailPositionY=-0.9, discDetailPositionZ=2.6 (all 131 rows) makes the model render (Flamizaurus). Masters are cached at server start (restart BootstrapApi); client then shows a 63 KB "Download Update".
* Remaining: the prod stubs of SetModel (0x159D1BC), <LoadModelAsync>d__25.MoveNext (0x159E254) and UnloadModel (0x159DF5C) must be removed/relocated (their bodies host caves) for the data fix to have any effect.
