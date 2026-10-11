#!/usr/bin/env bash
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_apk="${SOURCE_APK:-$repo/base.apk}"
server_base_url="${SERVER_BASE_URL:-}"
android_build_tools="${ANDROID_BUILD_TOOLS:-/opt/homebrew/share/android-commandlinetools/build-tools/35.0.0}"
if [[ -d "$android_build_tools" ]]; then
  export PATH="$android_build_tools:$PATH"
fi

if [[ -z "$server_base_url" ]]; then
  echo "Set SERVER_BASE_URL, for example http://10.58.124.80:18080" >&2
  exit 2
fi
if [[ "$server_base_url" != http://* ]]; then
  echo "SERVER_BASE_URL must use certificate-free http://" >&2
  exit 2
fi
for command_name in apktool zipalign apksigner keytool python3; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "Missing required command: $command_name" >&2
    exit 1
  fi
done

safe_host="${server_base_url#http://}"
safe_host="${safe_host//:/-}"
artifact_dir="$repo/.local/artifacts"
work_root="$repo/.local/apk-direct-work"
output_apk="${OUTPUT_APK:-$artifact_dir/KickFlight-2.11.0-direct-${safe_host}.apk}"
keystore="$repo/.local/kickflight-test-signing.jks"
mkdir -p "$artifact_dir" "$work_root"
work="$(mktemp -d "$work_root/build.XXXXXX")"
cleanup() {
  case "$work" in
    "$work_root"/build.*) rm -rf -- "$work" ;;
    *) echo "Refusing to remove unexpected work directory: $work" >&2 ;;
  esac
}
trap cleanup EXIT

apktool d -f -s "$source_apk" -o "$work/decoded"
# versionName 2.11.1 (2.11.1-diag for KF_DIAG=1 builds, so the server can let them through maintenance) / versionCode 56
version_flag=""
if [[ "${KF_DIAG:-}" == "1" ]]; then version_flag="--diag"; fi
python3 "$repo/scripts/set-apk-version.py" "$work/decoded" ${version_flag:+"$version_flag"}
sed -i '' 's|<application |<application android:debuggable="true" android:allowNativeHeapPointerTagging="false" android:largeHeap="true" |' "$work/decoded/AndroidManifest.xml"
python3 "$repo/scripts/patch-il2cpp-endpoints.py" \
  --metadata "$work/decoded/assets/bin/Data/Managed/Metadata/global-metadata.dat" \
  --arm64 "$work/decoded/lib/arm64-v8a/libil2cpp.so" \
  --armv7 "$work/decoded/lib/armeabi-v7a/libil2cpp.so" \
  --arm64-unity "$work/decoded/lib/arm64-v8a/libunity.so" \
  --base-url "$server_base_url"
# Previous-session exit reporter: uploads native crashes / ANRs / LMK kills to <server>/diag/upload at next start.
python3 "$repo/scripts/patch-exit-reporter.py" "$work/decoded" --base-url "$server_base_url"
apktool b "$work/decoded" -o "$work/unsigned.apk"
zipalign -p -f 4 "$work/unsigned.apk" "$work/aligned.apk"

if [[ ! -f "$keystore" ]]; then
  keytool -genkeypair -noprompt \
    -keystore "$keystore" -storepass android -keypass android \
    -alias kickflight-test -keyalg RSA -keysize 2048 -validity 10000 \
    -dname "CN=KickFlight Local Test,OU=Development,O=Local,C=MX"
fi

apksigner sign \
  --ks "$keystore" --ks-key-alias kickflight-test \
  --ks-pass pass:android --key-pass pass:android \
  --out "$output_apk" "$work/aligned.apk"
apksigner verify --verbose "$output_apk"
zipalign -c -p 4 "$output_apk"

echo "Direct APK: $output_apk"
echo "SHA-256:    $(shasum -a 256 "$output_apk" | awk '{print $1}')"
echo "Server URL: $server_base_url"
