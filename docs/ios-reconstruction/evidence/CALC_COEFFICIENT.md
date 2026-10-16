# `DiscParameterUtil.CalcCoefficient` static evidence

**Target identity:** `libil2cpp.so`, SHA-256
`c92a03cc227b46bf3281653ca4ac23cc012919e1783ae889d69ff4b1cae9daa0`.
It is ELF64 little-endian AArch64 ET_DYN. The first PT_LOAD has file offset 0
and virtual address 0, so this target's Il2CppDumper RVA equals file offset.
Ghidra imports this ELF with image base `0x100000`; therefore Ghidra address is
`image base + RVA`. The method RVA `0x1638520` is Ghidra address `0x1738520`,
not `0x1638520`. Ghidra bounded the function body to `0x1738520–0x1738617`;
the final instruction at `0x1738614` tail-branches outside the body.

**Metadata mapping:** `dump/script.json` identifies
`Colorful.DiscParameterUtil$$CalcCoefficient` at RVA `0x1638520` with signature
`int32_t (DiscMasterData*, DiscGrowMasterData*, MethodInfo*)`. `dump.cs` fields
identify `DiscMasterData.minCoefficient` at `0x4c`, `maxCoefficient` at `0x50`,
and `DiscGrowMasterData.rate` at `0x1c`.

**Observed instructions:** the method loads two `float` values from the
`DiscMasterData` object at offsets `0x4c` and `0x50`, then reads the rate from
the second object at `0x1c`. It subtracts min from max (`fsub`), multiplies by
rate (`fmul`), divides by the float constant at ELF file offset `0x3226e40`
(`fdiv`), adds min (`fadd`), then branches to the matched
`UnityEngine.Mathf$$FloorToInt` RVA `0x23e7d20`. The constant bytes are
`00 00 c8 42`, IEEE-754 `100.0f`. That helper converts with ARM64 `fcvtms`,
which rounds toward negative infinity. Thus the confirmed arithmetic subset is:

```text
(int)Mathf.FloorToInt(minCoefficient
    + (maxCoefficient - minCoefficient) * rate / 100.0f)
```

The pure C# implementation at
[`DiscParameterUtil.cs`](../../../clients/kickflight-ios/Assets/KickFlight/Logic/DiscParameterUtil.cs)
translates this arithmetic for valid master values. It does not reproduce
IL2CPP object null checks or Unity's exception stack behavior. A null
`DiscGrowMasterData` takes the native zero-return path; a null
`DiscMasterData` reaches the native null-reference helper. Inputs outside
finite normal master ranges have not been compared.

**Ghidra route:** REA 5.0.0's `rea function` default request exceeded its
330-second operation timeout while importing/analyzing this 74 MB library.
Rather than repeat that whole-image operation, the installed Ghidra 12.1.4
`analyzeHeadless` supports `-noanalysis`. The checked-in focused script
[`InspectCalcCoefficient.java`](../../../scripts/re/InspectCalcCoefficient.java)
imports the original ELF without auto-analysis, maps the RVA using the image
base, bounds the body before the tail-call target, disassembles and decompiles
that function, and reads the divisor bytes. The complete bounded logs are
[`calc-coefficient-ghidra-headless.log`](calc-coefficient-ghidra-headless.log)
and [`calc-coefficient-ghidra-script.log`](calc-coefficient-ghidra-script.log).
Independent Capstone listings are in
[`calc-coefficient-capstone.txt`](calc-coefficient-capstone.txt) and
[`mathf-floor-to-int-capstone.txt`](mathf-floor-to-int-capstone.txt); binary
hash record: [`libil2cpp.sha256`](libil2cpp.sha256).

**Verification:** `scripts/ios/test-logic.sh` passes two checks: explicit
fractional/boundary vectors (including a negative value that distinguishes
floor from truncation) and each of the 50 rates in `config/masters_disc_grow.json`.
The oracle rounds each arithmetic stage to `float32` and independently applies
mathematical floor. This verifies the reconstructed helper against static
instructions and current input data. It does not demonstrate Android runtime
execution or gameplay fidelity.
