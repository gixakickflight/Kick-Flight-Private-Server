#!/usr/bin/env bash
# Downloads the monthly DB-IP "IP to Country Lite" database (CC BY 4.0, https://db-ip.com) for LOCAL development.
# The VPS keeps its own copy fresh through the geoip-db compose service (deploy/geoip/update-dbip-country.sh);
# this script is the dev-machine equivalent. Usage: scripts/update-geoip-db.sh [output.mmdb]
# Then run the API with KF_GEOIP_DB=<output.mmdb>. A missing file is harmless (chat falls back to English).
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${1:-${KF_GEOIP_DB:-$repo_root/.local/geoip/dbip-country.mmdb}}"
release="${GEOIP_RELEASE:-$(date -u +%Y-%m)}"
url="https://download.db-ip.com/free/dbip-country-lite-${release}.mmdb.gz"

mkdir -p "$(dirname "$out")"
tmp="$(mktemp -d "$(dirname "$out")/.update.XXXXXX")"
trap 'rm -rf "$tmp"' EXIT

curl --fail --location --silent --show-error --connect-timeout 20 --max-time 300 --retry 2 "$url" --output "$tmp/db.gz"
gzip -t "$tmp/db.gz"
gzip -dc "$tmp/db.gz" > "$tmp/db.mmdb"
size="$(wc -c < "$tmp/db.mmdb" | tr -d ' ')"
if [ "$size" -lt 1000000 ]; then echo "downloaded file too small ($size bytes); keeping the current database" >&2; exit 1; fi
chmod 0644 "$tmp/db.mmdb"
mv -f "$tmp/db.mmdb" "$out"
echo "Installed DB-IP Lite country database $release ($size bytes) at $out"
