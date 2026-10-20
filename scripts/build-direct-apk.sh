#!/usr/bin/env bash
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
python="${PYTHON:-$repo/.venv/bin/python}"
[[ -x "$python" ]] || python="python3"

source_apk="${SOURCE_APK:-$repo/base.apk}"

# Android build tools detection (Fedora/Linux, macOS Homebrew, standard SDK)
android_sdk="${ANDROID_SDK_ROOT:-${ANDROID_HOME:-$HOME/Android/Sdk}}"
android_build_tools="${ANDROID_BUILD_TOOLS:-$android_sdk/build-tools/35.0.0}"
if [[ -d "$android_build_tools" ]]; then
  export PATH="$HOME/.local/bin:$android_build_tools:$android_sdk/platform-tools:$PATH"
elif [[ -d "/opt/homebrew/share/android-commandlinetools/build-tools/35.0.0" ]]; then
  export PATH="/opt/homebrew/share/android-commandlinetools/build-tools/35.0.0:$PATH"
fi

# Load tracked production defaults even when SERVER_BASE_URL is explicitly overridden.
config_file="${CONFIG_PATH:-$repo/config/apk-direct-server.production.json}"
if [[ -f "$config_file" ]]; then
  echo "==> Loading build configuration from $config_file"
  config_server_base_url="$("$python" -c "import json, sys; c=json.load(open(sys.argv[1])); print(c.get('serverBaseUrl', ''))" "$config_file")"
  server_base_url="${SERVER_BASE_URL:-$config_server_base_url}"
  if [[ -z "${KF_PHOTON_HOST:-}" ]]; then
    export KF_PHOTON_HOST="$("$python" -c "import json, sys; c=json.load(open(sys.argv[1])); print(c.get('photonHost', ''))" "$config_file")"
  fi
  if [[ -z "${KF_VERSION_NAME:-}" ]]; then
    export KF_VERSION_NAME="$("$python" -c "import json, sys; c=json.load(open(sys.argv[1])); print(c.get('versionName', ''))" "$config_file")"
  fi
  if [[ -z "${KF_VERSION_CODE:-}" ]]; then
    export KF_VERSION_CODE="$("$python" -c "import json, sys; c=json.load(open(sys.argv[1])); print(c.get('versionCode', ''))" "$config_file")"
  fi
  while IFS='=' read -r flag_name flag_value; do
    if [[ -z "${!flag_name+x}" ]]; then
      export "$flag_name=$flag_value"
    fi
  done < <("$python" -c 'import json,sys; c=json.load(open(sys.argv[1])); [print(f"{k}={v}") for k,v in c.get("patchFlags",{}).items()]' "$config_file")
  configured_source_hash="$("$python" -c "import json, sys; c=json.load(open(sys.argv[1])); print(c.get('expectedSourceSha256', ''))" "$config_file")"
  expected_source_hash="${EXPECTED_SOURCE_SHA256:-$configured_source_hash}"
else
  server_base_url="${SERVER_BASE_URL:-}"
  expected_source_hash="${EXPECTED_SOURCE_SHA256:-}"
fi

if [[ -z "$server_base_url" ]]; then
  echo "Set SERVER_BASE_URL (e.g. http://159.54.134.159:18080) or provide config via CONFIG_PATH" >&2
  exit 2
fi
if [[ "$server_base_url" != http://* ]]; then
  echo "SERVER_BASE_URL must use certificate-free http://" >&2
  exit 2
fi

if [[ ! -f "$source_apk" ]]; then
  echo "Missing base APK: $source_apk" >&2
  exit 1
fi

if [[ -n "$expected_source_hash" ]]; then
  actual_hash="$(sha256sum "$source_apk" | awk '{print $1}')"
  if [[ "${actual_hash,,}" != "${expected_source_hash,,}" ]]; then
    echo "Base APK hash mismatch! Expected $expected_source_hash, got $actual_hash" >&2
    exit 1
  fi
fi

for command_name in apktool zipalign apksigner keytool; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "Missing required command: $command_name" >&2
    exit 1
  fi
done

safe_host="${server_base_url#http://}"
safe_host="${safe_host//:/-}"
artifact_dir="$repo/.local/artifacts"
work_root="$repo/.local/apk-direct-work"
output_apk="${OUTPUT_APK:-$artifact_dir/KickFlight-${KF_VERSION_NAME:-2.11.5}-remote.apk}"

# Keystore resolution: explicit path, then worktree-local production key
if [[ -n "${KEYSTORE:-}" ]]; then
  keystore="$KEYSTORE"
elif [[ -f "$repo/.local/signing/kickflight-test-signing.jks" ]]; then
  keystore="$repo/.local/signing/kickflight-test-signing.jks"
elif [[ -f "$repo/.local/kickflight-test-signing.jks" ]]; then
  keystore="$repo/.local/kickflight-test-signing.jks"
else
  echo "Signing keystore not found in .local/signing/ or .local/" >&2
  exit 1
fi
if [[ ! -f "$keystore" ]]; then
  echo "Signing keystore not found: $keystore" >&2
  exit 1
fi

mkdir -p "$artifact_dir" "$work_root"
work="$(mktemp -d "$work_root/build.XXXXXX")"
cleanup() {
  case "$work" in
    "$work_root"/build.*) rm -rf -- "$work" ;;
    *) echo "Refusing to remove unexpected work directory: $work" >&2 ;;
  esac
}
trap cleanup EXIT

echo "==> Decoding base APK"
apktool d -f -s "$source_apk" -o "$work/decoded"

version_flag=""
if [[ "${KF_DIAG:-}" == "1" ]]; then version_flag="--diag"; fi
"$python" "$repo/scripts/set-apk-version.py" "$work/decoded" ${version_flag:+"$version_flag"}

echo "==> Adjusting AndroidManifest.xml"
"$python" - "$work/decoded/AndroidManifest.xml" <<'PY'
from pathlib import Path
import re, sys

path = Path(sys.argv[1])
text = path.read_text(encoding="utf-8")

app_values = {
    "debuggable": "true",
    "allowNativeHeapPointerTagging": "false",
    "largeHeap": "true",
    "usesCleartextTraffic": "true",
    "requestLegacyExternalStorage": "true",
}

m = re.search(r"<application\b[^>]*>", text, flags=re.S)
if not m:
    raise SystemExit("No <application> tag found in AndroidManifest.xml")
tag = m.group(0)
for name, value in app_values.items():
    attr = f"android:{name}"
    pat = re.compile(rf'\s{re.escape(attr)}="[^"]*"')
    if pat.search(tag):
        tag = pat.sub(f' {attr}="{value}"', tag)
    else:
        tag = tag[:-1] + f' {attr}="{value}">'
text = text[:m.start()] + tag + text[m.end():]
path.write_text(text, encoding="utf-8")
PY

echo "==> Patching IL2CPP endpoints and native caves"
patch_args=(
  --metadata "$work/decoded/assets/bin/Data/Managed/Metadata/global-metadata.dat"
  --arm64 "$work/decoded/lib/arm64-v8a/libil2cpp.so"
  --armv7 "$work/decoded/lib/armeabi-v7a/libil2cpp.so"
  --arm64-unity "$work/decoded/lib/arm64-v8a/libunity.so"
  --base-url "$server_base_url"
)
if [[ -n "${KF_PHOTON_HOST:-}" ]]; then
  patch_args+=(--photon-host "$KF_PHOTON_HOST")
fi
if [[ "${KF_SKIP_PREFLIGHT:-0}" == "1" ]]; then
  patch_args+=(--skip-preflight)
fi

"$python" "$repo/scripts/patch-il2cpp-endpoints.py" "${patch_args[@]}"

echo "==> Patching previous-session exit reporter"
"$python" "$repo/scripts/patch-exit-reporter.py" "$work/decoded" --base-url "$server_base_url"

echo "==> Building APK"
apktool b "$work/decoded" -o "$work/unsigned.apk"

echo "==> Aligning APK"
zipalign -p -f 4 "$work/unsigned.apk" "$work/aligned.apk"

echo "==> Signing APK"
apksigner sign \
  --ks "$keystore" --ks-key-alias kickflight-test \
  --ks-pass pass:android --key-pass pass:android \
  --v1-signing-enabled true \
  --v2-signing-enabled true \
  --v3-signing-enabled true \
  --out "$output_apk" "$work/aligned.apk"

echo "==> Verifying signature and alignment"
apksigner verify --verbose "$output_apk"
zipalign -c -p 4 "$output_apk"

echo "Candidate APK: $output_apk"
echo "SHA-256:       $(sha256sum "$output_apk" | awk '{print $1}')"
echo "File size:     $(stat -c %s "$output_apk") bytes"
echo "Server URL:    $server_base_url"
if [[ -n "${KF_PHOTON_HOST:-}" ]]; then
  echo "Photon Host:   $KF_PHOTON_HOST"
fi
