# iOS reconstruction inventory (2026-10-08)

## Scope and checkout

This inventory is recorded in a managed worktree rooted at
`/home/gixarde3/.codex/worktrees/ios-reconstruction-inventory/Kick-Flight-Private-Server`.
It was created after `git fetch origin` from `origin/main` at
`d6745cef4cce3e91baaf3ad62adae5ab07fdc364` (detached HEAD). The worktree was
clean at creation. The original checkout was preserved: its `AGENTS.md` has a
local modification and its LuxonServer submodule is locally modified. No
server state or deployment was changed.

## Evidence ledger

| ID | Observation | Evidence and reproducible check | Status / limit |
|---|---|---|---|
| E-APK-1 | Base client is Android package `jp.grenge.kickflight`, version 2.11.0 / versionCode 55, arm64 native IL2CPP. APK SHA-256 is `ffa620d2e2f905e729812606024e02741124951dce908bb2e0587f0a4e16e031`. | `sha256sum /home/gixarde3/Proyectos/KickFlight/Kick-Flight-Private-Server/base.apk`; `.../Android/Sdk/build-tools/35.0.0/aapt dump badging .../base.apk`; `unzip -l .../base.apk | rg 'lib/arm64-v8a/libil2cpp.so|global-metadata.dat'`. | Directly observed. APK present only in original checkout; it was not copied to this worktree. |
| E-RE-1 | Supplied native target exists at `/home/gixarde3/.codex/worktrees/discs-balance-audit/Kick-Flight-Private-Server/.local/re/lib/arm64-v8a/libil2cpp.so`, 74,161,912 bytes, SHA-256 `c92a03cc227b46bf3281653ca4ac23cc012919e1783ae889d69ff4b1cae9daa0`; ELF64, little endian, ET_DYN, AArch64, Android 21, stripped, BuildID `268b78e2a492622b3b838a4ae387a8a7b26f1f9f`. Matching metadata and dump files exist beside it. | `stat`, `file`, `sha256sum`, `readelf -h`; see evidence bundle hashes below. | Directly observed. Files remain in the prior worktree and were not copied. |
| E-RE-2 | ELF first `PT_LOAD` has file offset 0 / virtual address 0. Il2CppDumper maps `Colorful.DiscParameterUtil$$CalcCoefficient` to RVA `0x1638520`, signature `int32_t (DiscMasterData*, DiscGrowMasterData*, MethodInfo*)`. Ghidra imports this ELF at image base `0x100000`, so the correct Ghidra function address is `0x1738520`. | `readelf -l <libil2cpp.so>`; inspect `dump/script.json` and `dump.cs`; run `scripts/re/InspectCalcCoefficient.java` using the documented Ghidra command. | Selective Ghidra import/decompile corroborates the bounded function body `[0x1738520,0x1738617]`; do not use RVA directly as a Ghidra address. |
| E-RE-3 | Bounded Ghidra disassembly and independent Capstone evidence show float32 interpolation using min/max offsets `0x4c`/`0x50`, rate offset `0x1c`, divisor `100.0f` at ELF offset `0x3226e40`, followed by `Mathf.FloorToInt`; helper uses `fcvtms` (floor toward negative infinity). | See `evidence/CALC_COEFFICIENT.md`, `evidence/calc-coefficient-ghidra-headless.log`, `evidence/calc-coefficient-ghidra-script.log`, both Capstone text files, and `scripts/re/InspectCalcCoefficient.java`. Reproduce ELF hash with `sha256sum <libil2cpp.so>` and use `readelf -l` to verify ELF base. | Static evidence confirms the numeric arithmetic subset. It does not confirm Android runtime behavior; null handling and exception behavior are excluded from the pure helper. |
| E-DUMP-1 | Present Il2CppDumper companions: `dump/dump.cs` 26,904,664 bytes (SHA-256 `b5e75926b67d60a57051b376d47c791bce609d9349cbeca7e500a5495f98edad`); `dump/il2cpp.h` 52,713,306 bytes (`9699806b248a49f2d783b1f136d766856cddc6d0273ca0a0c48032eff1c15e48`); `dump/script.json` 67,125,151 bytes (`e3aed44a78d7b8a2d352a285afdb129b1e1cda40b4ad51dfe52191f54e622ae8`); `dump/stringliteral.json` 1,352,515 bytes (`a915b961b56a9ff3d335915c4615472972ee8b1febb6ee6b9e83d0c0d8675e0e`); `global-metadata.dat` 15,837,732 bytes (`8337973aa337aec9e122d198c1d0108e9e1ab9abc5207305e97a2854c0768692`). | `stat -c '%n %s'`, `file`, `sha256sum` on the six paths in E-RE-1's directory. | `dump.cs` declares methods with empty bodies. It is type/name context, not recovered C# source. |
| E-REA-1 | Installed CLI is REA 5.0.0. Ghidra 12.1.4, Java 25.0.4 (full 64-bit JDK), and `analyzeHeadless` are configured and pass `rea doctor --provider ghidra --json` checks. The provider inventory contains Ghidra and native/artifact providers. | `GHIDRA_INSTALL_DIR=/home/gixarde3/.local/share/ghidra/ghidra_12.1.4_PUBLIC JAVA_HOME=/usr/lib/jvm/java-25-openjdk /home/gixarde3/.local/bin/rea doctor --provider ghidra --json`; corresponding `rea providers --json` and `rea capabilities --json`. | `environment_healthy=false` only because optional Hopper/IDA and other-client registrations are absent; this does not make Ghidra unavailable. Codex REA MCP tools are absent from this session, so use the CLI. |
| E-REA-2 | REA CLI 5.0.0's native `function` request selected Ghidra 12.1.4 but returned `provider_timeout` at 330 seconds. The supported Ghidra headless `-noanalysis` import route then completed the one bounded function in about 13 seconds. | CLI timeout was observed with `rea function <libil2cpp.so> 0x1638520 --provider ghidra --format json`; bounded result and script are in `evidence/calc-coefficient-ghidra-headless.log` and `scripts/re/InspectCalcCoefficient.java`. | REA's full-image default path is too slow on this ELF in this session. The selective direct Ghidra result, not REA pseudocode, is the saved analysis evidence. |
| E-HOST-1 | Local host is Fedora 44 x86_64. Available: Java 25, .NET SDK/runtime, Android SDK platform 35 and Build Tools 35.0.0, `adb`, Android emulator, and AVD `KickFlight_API35`. `adb devices -l` listed no connected device. | `uname -a`, `/etc/os-release`, `command -v ...`, `ls /home/gixarde3/Android/Sdk/{platforms,build-tools}`, `adb devices -l`, `emulator -list-avds`. | Android testing remains possible in an AVD; there is no iPhone attached to this host. |
| E-HOST-2 | No Unity editor/Hub, Xcode (`xcodebuild`/`xcrun`), Apple host, or libimobiledevice tools (`idevice_id`, `ideviceinfo`, `idevicesyslog`) were found on PATH or in the checked common install paths. | `command -v Unity UnityHub xcodebuild xcrun idevice_id ideviceinfo idevicesyslog`; `uname -s`; checked `/Applications/Xcode.app` and `/home/gixarde3/.local/share`. | A Mac with Xcode, Apple signing credentials/profile and a physical iPhone are required to finish a signed-device IPA build and validation. The user has a pending build-host/signing/device question; Fedora source work can continue. |
| E-ASSET-1 | Sibling `Kick-Flight-Assets` checkout and server `content/resources` are present. The server worktree contains Octo CDN/catalog generation and bundle build tooling. The Octo snapshot has 2,374 UnityFS v6 bundles; UnityPy parsed all with engine version `2018.4.11f1`. | `docs/ios-reconstruction/ASSETS.md` and `assets-sample-evidence.json` record the full scan and typed sample/export evidence; `scripts/ios/inventory_octo_assets.py` and `scripts/ios/export_unity_sample.py` reproduce bounded checks. | Serialized bundle engine metadata is confirmed. This does not determine a modern editor patch/toolchain for iOS builds or prove unchanged loading on a newly reconstructed project. |
| E-PROTOCOL-1 | Source docs and server implementation describe separate HTTPS API/CDN and Photon paths. | See `docs/ios-reconstruction/PROTOCOL.md` (source/line references) and `docs/DISCOVERED_STARTUP_FLOW.md`. | API/CDN contract has source support; working iOS Photon interoperability remains unknown until a client handshake is implemented and observed. |
| E-LOGIC-1 | Added a pure C# `CalcCoefficient(float,float,float)` arithmetic helper and test harness. | `clients/kickflight-ios/Assets/KickFlight/Logic/DiscParameterUtil.cs`; `scripts/ios/test-logic.sh`; `config/masters_disc_grow.json`. | `2/2` checks passed, including all 50 current rate rows. This is host test evidence for the arithmetic helper, not a runtime comparison with Android. |

### Initial RE target command

```bash
export GHIDRA_INSTALL_DIR=/home/gixarde3/.local/share/ghidra/ghidra_12.1.4_PUBLIC
export JAVA_HOME=/usr/lib/jvm/java-25-openjdk
/home/gixarde3/.local/bin/rea function \
  /home/gixarde3/.codex/worktrees/discs-balance-audit/Kick-Flight-Private-Server/.local/re/lib/arm64-v8a/libil2cpp.so \
  0x1638520 --provider ghidra --format json
```

The REA timeout has been resolved for this narrow question through the supported
Ghidra `-noanalysis` path. Use the focused Java script and saved log instead of
repeating the full-image REA function analysis. The ledger retains the REA
limitation and the exact RVA/image-base adjustment.

## Classification

- **Reusable:** private API, resource catalog/CDN, master JSON data, existing
  server-side battle/session contracts, and recovered UnityFS 6 assets serialized
  by Unity `2018.4.11f1`. Asset import, platform compatibility, and dependency
  reconstruction still need editor/runtime validation.
- **Reconstructable with evidence:** small pure rules in IL2CPP, API request and
  response DTOs, startup/session handling, and later movement/combat functions.
  Recovered pseudocode and ARM64 instructions are evidence, not source code.
- **Unknown/high effort:** iOS Photon interoperability, which modern Unity editor
  and asset conversion path can consume the recovered `2018.4.11f1` bundles while
  meeting the available iOS build toolchain, gameplay feel/animation timing,
  physical iOS performance, and signed IPA distribution until Mac/Xcode/device/
  signing access exists.
