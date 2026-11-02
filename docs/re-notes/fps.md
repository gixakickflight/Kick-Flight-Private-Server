# FPS cap (30 FPS) - research notes (2.11.0, base.apk SHA ffa620d2...)

Status: static research only; no runtime measurement. libil2cpp.so SHA-256 c92a03cc227b46bf3281653ca4ac23cc012919e1783ae889d69ff4b1cae9daa0 (matches base.apk lib/arm64-v8a).
Tooling: REA/Ghidra failed (provider timeout); results via capstone + manual ELF parse. Scans cover direct bl/b only (no blr/vtable).

## Where the 30 comes from
- `GRE.SystemManager.Setting` (0x1CB589C): `ldr w20,[x20,#0x10]` @0x1CB58E8 (SystemSettings._targetFrameRate), `mov w0,w20` @0x1CB5904 (0x2A1403E0), `mov x1,xzr` @0x1CB5908, `bl Application.set_targetFrameRate` @0x1CB590C (icall 0x29A39F4). No constant in Setting.
- `SystemSettings..ctor` (0x1CB7A14): `mov w8,#0x1e; str w8,[x19,#0x10]` @0x1CB7A4C -> C# default 30.
- Serialized: assets/bin/Data/data.unity3d has 10 MonoBehaviours with m_Script PPtr 486 (GRE.SystemManager), 488 bytes each, int32 30 at raw offset 32 (then 750.0/1334.0 and 1125.0/2436.0 floats = target/max resolution). Inference from raw bytes; UnityPy typetree failed on most MonoBehaviours. The serialized 30 overrides the ctor default.
- `SystemManager.SetTargetFrameRate(int)` 0x1CB6304: tail-b to icall, no direct callers (unused).
- `SPFXSettings.Awake` 0x25DC1E4: set_vSyncCount(0) @0x25DC220, set_targetFrameRate(this.FrameRate @+0x18) @0x25DC22C. Second writer; serialized FrameRate unknown (no SPFXSettings MonoBehaviour found in data.unity3d). Could override SystemManager depending on init order -> verify at runtime.
- set_vSyncCount 0x27743F8 callers: SPFXSettings.Awake (0), ARCore SessionConfigApi (0). No game vsync write.
- API RVAs: get_targetFrameRate 0x29A399C, set_targetFrameRate 0x29A39F4, set_vSyncCount 0x27743F8, Time.set_timeScale 0x2788620, get_fixedDeltaTime 0x2788468, get_frameCount 0x2788690. No set_fixedDeltaTime/captureFramerate in dump.
- Caller count of set_targetFrameRate: 13 (first scan) vs 19 (second scan), unreconciled; others are AR/SDK/debug.
- No server/master/config value feeds the cap. No in-game FPS option (GraphicLevel Low/Middle/High/Custom has no FPS field; InitialGraphicSetting* has no frame-rate write). No hidden FrameRate option found.

## Gameplay coupling (partial)
- Photon: wall-clock (Environment.TickCount); SendRate/SerializationRate = 10/10 (PhotonManager.SetRate 0x18D5D9C; GameManager.CompleteGameReady @0x1572b34, MatchingManager.Connect @0x14e1328). Not frame-bound.
- PlayerCharacter UpdateAi/PerformMove/UpdateTransform/UpdateAction/ManagedUpdate: no direct deltaTime/frameCount/fixedDeltaTime. LateUpdateAi, UpdateInvincibleTime use deltaTime. Helper-level movement NOT scanned.
- Time.frameCount users: ReplayPlayFile, Crystal/BallModelController, DamageInitializeInfo.Serialize @0x185bf30 (purpose unknown), EffectManager.SetForceFinishTime reads get_targetFrameRate @0x14a2efc (effect timing may scale with it).
- Unknown: Timeline/animation, per-frame movement in helpers, fixed timestep (physics default 0.02 = 50Hz independent of render rate).

## Android
- boot.config (181 B): no frame/vsync keys. Manifest: no refresh-rate/frame pacing meta. globalgamemanagers/ProjectSettings not in base.apk (Optimized Frame Pacing / QualitySettings.vSyncCount unverified; check data.unity3d QualitySettings).

## Proposed patch (not applied)
Single-instruction, no cave, no bl: at RVA 0x1CB5904 replace `mov w0,w20` (0x2A1403E0, bytes E0 03 14 2A) with `mov w0,#60` (0x52800780, bytes 80 07 80 52) or `#120` (0x52800F00, bytes 00 0F 80 52). Code is live (not a dead body), only a register-move swap; w20 stays loaded, no flag/LR effects. Complies with cave rules 2-7. Suggest env KF_TARGET_FPS (default off) in patch-il2cpp-endpoints.py with byte-guard on original word. Alternative: edit serialized int 30 in data.unity3d (10 objects) - needs asset rebuild, worse.
Test: install, `adb shell dumpsys SurfaceFlinger --latency <layer>` or `dumpsys gfxinfo jp.grenge.kickflight framestats`; log Application.targetFrameRate via a KFDIAG probe only if needed. Note AVD (x86_64 emulator) display is typically 60Hz, so 120 is unmeasurable there; use a real device. Check battle speed (attack timing, effects, EffectManager force-finish) at 60 vs 30.

## Recommendation
60 first (halves risk, matches typical displays); 120 only after battle verification. Unknowns: SPFXSettings override, helper movement/animation coupling, QualitySettings vSync, Unity frame pacing.

## Runtime result (2026-11-02, AVD x86_64 API 35, arm64 via ndk_translation, display 60 Hz)
- Patch applied at 0x1CB5904 and 0x25DC224 (KF_TARGET_FPS; dry-run 242 patches). Only SystemManager.Setting and SPFXSettings.Awake are relevant
  setTargetFrameRate callers (the other 11 are AR/ARKit samples).
- Measured with `dumpsys SurfaceFlinger --latency <SurfaceView layer>` on Home: stock 30.0 fps; KF_TARGET_FPS=60 build 29-31 fps; KF_TARGET_FPS=15 build 15.0 fps.
  => the patch takes effect (15 proves it) but the AVD cannot render above ~30 fps under ndk_translation, so 60/120 and 60-vs-30 battle behaviour are NOT
  verifiable on the AVD. Needs a real 60/120 Hz arm64 device (menu + battle: attack timing, effects, EffectManager force-finish).
