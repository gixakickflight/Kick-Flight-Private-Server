#!/usr/bin/env bash
# Builds libkfpatch.so (arm64-v8a) with the Android NDK clang. Output: .local/artifacts/kfpatch/libkfpatch.so
# NDK: $ANDROID_NDK_ROOT, else the newest ndk/ under the SDK. Install (no sudo):
#   ~/Android/Sdk/cmdline-tools/latest/bin/sdkmanager "ndk;27.2.12479018"
set -Eeuo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
sdk="${ANDROID_SDK_ROOT:-${ANDROID_HOME:-$HOME/Android/Sdk}}"
ndk="${ANDROID_NDK_ROOT:-$(ls -d "$sdk"/ndk/*/ 2>/dev/null | sort -V | tail -1)}"
[[ -n "$ndk" && -d "$ndk" ]] || { echo "NDK not found; run: $sdk/cmdline-tools/latest/bin/sdkmanager \"ndk;27.2.12479018\"" >&2; exit 1; }
cc="$ndk/toolchains/llvm/prebuilt/linux-x86_64/bin/aarch64-linux-android26-clang"
out="$repo/.local/artifacts/kfpatch"; mkdir -p "$out"
srcs=("$repo"/src/native/kfpatch/*.c "$repo"/src/native/kfpatch/caves_gen.S)
SOURCE_DATE_EPOCH=0 "$cc" -shared -fPIC -O2 -fvisibility=hidden -Wall -Wextra \
  -Wl,--build-id=none -Wl,-z,max-page-size=16384 -ffile-prefix-map="$repo"=. \
  ${KF_PROOF_RVA:+-DKF_PROOF_RVA=$KF_PROOF_RVA} ${KF_NATIVE_DIAG:+-DKF_NATIVE_DIAG=1} ${KF_PINGHUD_OPTION:+-DKF_PINGHUD_OPTION=1} ${KF_OPT_DUMP:+-DKF_OPT_DUMP=1} ${KF_PING_DUMP:+-DKF_PING_DUMP=1} ${KF_CRI_TEST:+-DKF_CRI_TEST=$KF_CRI_TEST} ${KF_UNITY_TEST:+-DKF_UNITY_TEST=$KF_UNITY_TEST} ${KF_FILL_DELAY_MS:+-DKF_FILL_DELAY_MS=$KF_FILL_DELAY_MS} \
  -o "$out/libkfpatch.so" "${srcs[@]}" -llog
sha256sum "$out/libkfpatch.so"
