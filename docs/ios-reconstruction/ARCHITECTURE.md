# Proposed iOS reconstruction architecture

The first implementation should be a new Unity client that consumes the existing
server contract. Keep each boundary explicit so UI and gameplay code do not
silently guess at protocol behavior.

```text
Unity views and input
        ↓
Domain state and gameplay rules ← verified IL2CPP function records
        ↓
Session/API adapters ───── Asset/cache adapters
        ↓                         ↓
HTTPS D2C API             Octo/protobuf + CDN bundles
        ↓
Existing private API, PostgreSQL and Photon services
```

## Layers

- **Presentation:** Unity scenes, camera, controls, HUD, animation and audio.
  Treat recovered prefabs/resources as inputs only after the asset inventory
  records dependencies and target-engine compatibility.
- **Domain:** explicit session, master-data models and deterministic gameplay
  rules. Keep rules such as coefficient conversion as pure C# functions with
  evidence, known vectors and unit conventions. Never infer behavior from an
  empty `dump.cs` body.
- **Transport:** Unity-independent C# for HTTPS, D2C encryption, status/header
  handling, Octo fetch boundaries and typed protocol errors. Use a .NET 8 test
  harness while keeping production code compatible with the selected Unity
  scripting runtime.
- **Data/cache:** parse master JSON and Octo protobuf separately; fetch immutable
  CDN objects, verify hashes/size where the protocol supplies them, and map
  logical resources to Unity assets. Keep Android server masters and catalog
  data as the shared backend source.

## Provisional engine/build choice

No original Unity project or editor is installed in the current Fedora host.
The recovered Octo bundles are UnityFS v6 serialized by Unity `2018.4.11f1`,
confirmed by parsing all 2,374 bundles. That metadata does not establish which
modern editor can import them unchanged and also build for the available iOS
toolchain. Keep the editor choice provisional until that import/build path and
the Mac/Xcode host are known. The transport and domain libraries currently
target `netstandard2.1`; this is not a claim that a complete Unity project or
an iOS build has been validated.

Unity's official Unity 6.0 requirements list iOS/iPadOS 13+, Metal and Xcode
15+ as the platform baseline, and state that Xcode produces the final
application from Unity's generated project:
[Unity 6 system requirements](https://docs.unity3d.com/6000.0/Documentation/Manual/system-requirements.html).
Apple's currently published App Store upload requirement (since 2026-04-28)
is Xcode 26+ with an iOS 26 SDK; current Xcode/macOS compatibility is listed in
[Apple's Xcode system requirements](https://developer.apple.com/xcode/system-requirements/)
and [submission requirements](https://developer.apple.com/news/upcoming-requirements/?id=04282026a).
Those are external release requirements and may change; verify them again when
the distribution method is known. A development or Ad Hoc IPA still needs a
Mac/Xcode and valid signing/provisioning setup.

## Next small reconstruction candidate

`Colorful.DiscParameterUtil.CalcCoefficient` is a bounded first candidate:
`script.json` maps it to RVA `0x1638520`. A bounded Ghidra import confirmed the
float32 fields, divisor `100.0f`, tail call to `Mathf.FloorToInt`, and floor
conversion instruction. The pure arithmetic translation and a host harness
now cover boundary vectors and all current DiscGrow rate rows. Evidence,
limitations and command lines are in
[`evidence/CALC_COEFFICIENT.md`](evidence/CALC_COEFFICIENT.md); this does not
prove Android runtime parity.

## Open architecture decisions

1. Which Mac can run the Unity editor and Xcode, and which exact Xcode version
   can be provisioned there?
2. Which iPhone model and iOS version will be the minimum real-device target?
3. Is the IPA for development install, Ad Hoc distribution, or TestFlight, and
   which Apple signing account/profile is available?
4. What Unity version and bundle serialization version do the recovered Octo
   bundles require? Can the original bundle hierarchy load on iOS without
   server-side changes?
5. Which transport layer is authoritative for Photon: existing LuxonServer
   source, Android IL2CPP analysis, or both? What precise handshake proves
   interoperability rather than local AI-only results?
6. Which first playable vertical slice is practical after login and masters:
   home scene, local controlled character, or immediate one-player battle?

## Fidelity boundary

A local battle against bots is a useful scene and rules milestone but does not
prove Photon client compatibility. An IPA that signs and opens proves install
and startup only. The completion target remains a real device login, resource
load, visible character, controlled movement, and battle completed through the
existing server, with any substitution documented.
