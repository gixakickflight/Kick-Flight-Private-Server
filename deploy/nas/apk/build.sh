#!/usr/bin/env bash
# Build the Android client for a self-hosted stack (the NAS) with scripts/build-direct-apk.sh, in a Linux container,
# so it needs neither a Windows toolchain nor the Windows host's memory.
#
#   deploy/nas/apk/build.sh <host> <tools-dir> [version-name] [version-code]
#
# <host>       the address clients reach the stack on: API http://<host>:18080, Photon <host>, update page and chat
#              on the same API. Nothing else is baked in; the build reads no production config.
# <tools-dir>  a directory holding base.apk (the verified source APK), apktool_3.0.3.jar, apksigner.jar (Android
#              build-tools 35.0.0 lib/) and the signing keystore. KEYSTORE_FILE / KEYSTORE_ALIAS select the key
#              (an APK installs over an earlier one only when both are signed with the same key).
#
# Push is off (KF_PUSH=0): a self-hosted stack has no FCM credentials. The output goes to
# .local/artifacts/KickFlight-<version>-nas-<host>.apk of the checkout this script is in.
set -euo pipefail
host="${1:?usage: $0 <host> <tools-dir> [version-name] [version-code]}"
tools="$(cd "${2:?tools dir}" && pwd)"
version_name="${3:-2.11.7}"
version_code="${4:-62}"
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
source_sha="${EXPECTED_SOURCE_SHA256:-$(sha256sum "$tools/base.apk" | awk '{print $1}')}"

mkdir -p "$repo/.local/artifacts"
config="$repo/.local/apk-direct-server.nas-$host.json"
cat > "$config" <<JSON
{
  "serverBaseUrl": "http://$host:18080",
  "photonHost": "$host",
  "versionName": "$version_name",
  "versionCode": $version_code,
  "expectedSourceSha256": "$source_sha",
  "patchFlags": {
    "KF_PHOTON": "1", "KF_AI_ROLES": "1", "KF_AI_ROLE_TARGETING": "1", "KF_AI_ROLE_DEPOSIT": "1",
    "KF_DIAG": "0", "KF_DAMAGE_DIAG": "0", "KF_RESULT_DIAG": "0", "KF_TARGET_FPS": "120",
    "KF_PUSH": "0",
    "KF_UPDATE_URL": "http://$host:18080/",
    "CHAT_BASE_URL": "http://$host:18080?ns={0}"
  }
}
JSON

docker build -q -t kf-apk-tools:bookworm "$repo/deploy/nas/apk" >/dev/null
# Capped so an out-of-memory apktool is killed instead of the machine (WSL has crashed on this before).
docker run --rm --memory 3g --memory-swap 4g --user "$(id -u):$(id -g)" -e HOME=/tmp \
  -v "$repo":/src -v "$tools":/tools:ro -w /src \
  -e CONFIG_PATH="/src/.local/apk-direct-server.nas-$host.json" \
  -e SOURCE_APK=/tools/base.apk \
  -e KEYSTORE="/tools/${KEYSTORE_FILE:-kickflight-local-test.jks}" -e KEYSTORE_ALIAS="${KEYSTORE_ALIAS:-kickflight-local-test}" \
  -e OUTPUT_APK="/src/.local/artifacts/KickFlight-$version_name-nas-$host.apk" \
  -e ANDROID_BUILD_TOOLS=/nonexistent -e PYTHON=python3 -e PYTHONUTF8=1 \
  kf-apk-tools:bookworm bash scripts/build-direct-apk.sh
