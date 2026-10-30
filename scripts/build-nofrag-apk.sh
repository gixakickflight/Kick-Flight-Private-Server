#!/usr/bin/env bash
# Re-sign an existing client APK with the Discs-tab Disc Fragment exchange cell hidden
# (scripts/patch-hide-disc-exchange.py). Everything except lib/arm64-v8a/libil2cpp.so is copied unchanged.
#
#   SOURCE_APK=.local/KickFlight-2.11.5-photon-100.116.38.51.apk \
#   OUTPUT_APK=.local/KickFlight-2.11.5-nofrag.apk scripts/build-nofrag-apk.sh
#
# KEYSTORE / KEY_ALIAS default to the community key used by build-direct-apk.sh (alias kickflight-test).
# An install only updates in place when the key matches the installed APK's signer.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
python="${PYTHON:-python3}"
source_apk="${SOURCE_APK:?set SOURCE_APK}"
output_apk="${OUTPUT_APK:?set OUTPUT_APK}"
keystore="${KEYSTORE:-$repo/.local/kickflight-test-signing.jks}"
key_alias="${KEY_ALIAS:-kickflight-test}"

for command_name in zipalign apksigner; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "Missing required command: $command_name" >&2
    exit 1
  fi
done
if [[ ! -f "$keystore" ]]; then
  echo "Signing keystore not found: $keystore" >&2
  exit 1
fi

work="$(mktemp -d "${TMPDIR:-/tmp}/nofrag.XXXXXX")"
trap 'rm -rf -- "$work"' EXIT

echo "==> Patching $source_apk"
"$python" "$repo/scripts/patch-hide-disc-exchange.py" --apk-in "$source_apk" --apk-out "$work/unsigned.apk"

echo "==> Aligning APK"
zipalign -p -f 4 "$work/unsigned.apk" "$work/aligned.apk"

echo "==> Signing APK"
rm -f "$output_apk"
apksigner sign \
  --ks "$keystore" --ks-key-alias "$key_alias" \
  --ks-pass pass:android --key-pass pass:android \
  --v1-signing-enabled true \
  --v2-signing-enabled true \
  --v3-signing-enabled true \
  --out "$output_apk" "$work/aligned.apk"

echo "==> Verifying signature and alignment"
apksigner verify --verbose --print-certs "$output_apk"
zipalign -c -p 4 "$output_apk"

echo "Candidate APK: $output_apk"
echo "SHA-256:       $(sha256sum "$output_apk" | awk '{print $1}')"
