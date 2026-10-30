#!/usr/bin/env bash
set -Eeuo pipefail

app_dir=${KF_APP_DIR:-/opt/kickflight}
backup_dir=${KF_BADGES_BACKUP_DIR:-$app_dir/.local/badges/backups}
retention_days=${KF_BADGES_BACKUP_RETENTION_DAYS:-14}
compose_file="$app_dir/deploy/docker-compose.vps.yml"
env_file="$app_dir/.env"

[[ "$retention_days" =~ ^[1-9][0-9]*$ ]] || { echo "invalid backup retention" >&2; exit 2; }

install -d -m 0700 -- "$backup_dir"
umask 077
timestamp=$(date -u +%Y%m%dT%H%M%SZ)
final="$backup_dir/badges-$timestamp.sqlite3"
[[ ! -e "$final" ]] || final="$backup_dir/badges-$timestamp-$$.sqlite3"
tmp=$(mktemp "$backup_dir/.badges-$timestamp.XXXXXX")
cleanup() { rm -f -- "$tmp"; }
trap cleanup EXIT

# 1. Attempt backup via loopback API first if service is responsive
backed_up=false
if curl --fail --silent --show-error --max-time 5 -X POST "http://127.0.0.1:8766/badges/api/backup" \
    -H "Content-Type: application/json" -d "{\"destination\":\"$tmp\"}" >/dev/null 2>&1; then
  backed_up=true
fi

# 2. If API not directly accessible on loopback, try via docker compose exec
if [[ "$backed_up" != true && -f "$compose_file" && -f "$env_file" ]] && command -v docker >/dev/null 2>&1; then
  compose=(docker compose --env-file "$env_file" --project-name deploy -f "$compose_file")
  if "${compose[@]}" ps --status running -q badges 2>/dev/null | grep -q .; then
    "${compose[@]}" exec -T badges python -c '
import sqlite3, os
src = sqlite3.connect(os.environ.get("KF_BADGES_DB_PATH", "/data/badges.sqlite3"))
dst = sqlite3.connect("/tmp/bk.sqlite3")
src.backup(dst)
dst.close()
src.close()
'
    docker cp "$("${compose[@]}" ps -q badges):/tmp/bk.sqlite3" "$tmp"
    "${compose[@]}" exec -T badges rm -f /tmp/bk.sqlite3
    backed_up=true
  fi
fi

# 3. If neither container nor loopback API, attempt direct local SQLite backup if db file exists
if [[ "$backed_up" != true ]]; then
  host_db="${KF_BADGES_DB_PATH:-$app_dir/.local/badges/badges.sqlite3}"
  if [[ -f "$host_db" ]]; then
    python3 -c "
import sqlite3, sys
src = sqlite3.connect(sys.argv[1])
dst = sqlite3.connect(sys.argv[2])
src.backup(dst)
dst.close()
src.close()
" "$host_db" "$tmp"
    backed_up=true
  fi
fi

[[ "$backed_up" == true && -s "$tmp" ]] || {
  echo "Failed to create badges SQLite backup: service or DB unavailable" >&2
  exit 1
}

# Verify SQLite integrity of backup
python3 -c "
import sqlite3, sys
conn = sqlite3.connect(sys.argv[1])
cur = conn.cursor()
cur.execute('PRAGMA integrity_check;')
row = cur.fetchone()
if not row or row[0] != 'ok':
    sys.exit(f'Backup integrity check failed: {row}')
conn.close()
" "$tmp"

chmod 0600 -- "$tmp"
mv -- "$tmp" "$final"

find "$backup_dir" -maxdepth 1 -type f -name 'badges-*.sqlite3' -mmin "+$((retention_days * 1440))" -delete
printf 'Badges SQLite backup created: %s\n' "$final"
