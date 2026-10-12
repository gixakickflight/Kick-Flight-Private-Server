#!/usr/bin/env bash
# Remote half of .github/workflows/deploy-vps-main.yml. Runs as the configured SSH user; Docker access
# must be available without sudo. Arguments are supplied by the workflow and are not secrets.
set -Eeuo pipefail

if [[ $# -ne 11 && $# -ne 13 ]]; then
  echo "usage: deploy-vps-ci.sh APP_DIR SHA ARCHIVE API PHOTON CDN FARM SITE CADDY FULL BALANCE [IOS_SNAPSHOT_ARCHIVE IOS_SNAPSHOT_SHA256]" >&2
  exit 2
fi

app_dir=$1
release_sha=$2
archive=$3
deploy_api=$4
deploy_photon=$5
deploy_cdn=$6
rebuild_farm=$7
sync_site=$8
deploy_caddy=$9
full_deploy=${10}
deploy_balance=${11}
ios_snapshot_archive=${12:-}
ios_snapshot_sha256=${13:-}

[[ "$app_dir" == /* ]] || { echo "VPS_APP_DIR must be an absolute path" >&2; exit 2; }
[[ "$app_dir" != "/" && -d "$app_dir" ]] || { echo "VPS_APP_DIR must be an existing application directory" >&2; exit 2; }
[[ "$release_sha" =~ ^[0-9a-f]{40}$ ]] || { echo "invalid commit SHA" >&2; exit 2; }
[[ "$archive" == "/tmp/kf-vps-$release_sha.tar.gz" && -f "$archive" ]] || { echo "release archive path is invalid or missing" >&2; exit 2; }
if [[ -n "$ios_snapshot_archive" || -n "$ios_snapshot_sha256" ]]; then
  [[ $# -eq 13 ]] || { echo "iOS snapshot archive and SHA-256 must be supplied together" >&2; exit 2; }
  [[ "$ios_snapshot_archive" == "/tmp/kf-ios-snapshot-$release_sha.tar.gz" && -f "$ios_snapshot_archive" ]] || {
    echo "iOS snapshot archive path is invalid or missing" >&2; exit 2;
  }
  [[ "$ios_snapshot_sha256" =~ ^[0-9a-f]{64}$ ]] || { echo "invalid iOS snapshot SHA-256" >&2; exit 2; }
fi
for flag in "$deploy_api" "$deploy_photon" "$deploy_cdn" "$rebuild_farm" "$sync_site" "$deploy_caddy" "$full_deploy" "$deploy_balance"; do
  [[ "$flag" == true || "$flag" == false ]] || { echo "invalid component flag" >&2; exit 2; }
done
cleanup_upload() {
  rm -f -- "$archive"
  [[ -z "$ios_snapshot_archive" ]] || rm -f -- "$ios_snapshot_archive"
}
trap cleanup_upload EXIT

command -v docker >/dev/null || { echo "Docker is required on the VPS" >&2; exit 1; }
command -v rsync >/dev/null || { echo "rsync is required on the VPS" >&2; exit 1; }
command -v flock >/dev/null || { echo "flock is required on the VPS" >&2; exit 1; }
command -v curl >/dev/null || { echo "curl is required on the VPS" >&2; exit 1; }

[[ -f "$app_dir/.env" ]] || { echo "missing persistent $app_dir/.env" >&2; exit 1; }
[[ -d "$app_dir/.local" ]] || { echo "missing persistent $app_dir/.local resource directory" >&2; exit 1; }

ci_dir="$app_dir/.ci"
releases_dir="$ci_dir/releases"
mkdir -p "$releases_dir"
exec 9>"$ci_dir/deploy.lock"
flock -n 9 || { echo "another VPS deployment is already running" >&2; exit 1; }

state_file="$ci_dir/current-sha"
previous_sha=""
if [[ -f "$state_file" ]]; then
  IFS= read -r previous_sha < "$state_file" || true
fi
if [[ "$previous_sha" == "$release_sha" && -z "$ios_snapshot_archive" ]]; then
  echo "commit $release_sha is already deployed"
  exit 0
fi
previous_release=""
if [[ "$previous_sha" =~ ^[0-9a-f]{40}$ && -d "$releases_dir/$previous_sha" ]]; then
  previous_release="$releases_dir/$previous_sha"
else
  # The first CI run may replace a manually maintained checkout. Keep a source-only snapshot so a
  # failed first rollout can restore those files without copying .env, .local, player data, or assets.
  previous_release="$releases_dir/.pre-ci-$release_sha"
  if [[ ! -f "$previous_release/.kf-managed-files" ]]; then
    rm -rf -- "$previous_release"
    mkdir -p "$previous_release"
    rsync -a --checksum \
      --exclude='.git' --exclude='/.env' --exclude='/.local' --exclude='/.ci' \
      --exclude='/data' --exclude='/captures' --exclude='/logs' --exclude='/certs' \
      --exclude='/deploy/.env' --exclude='/appsettings.Local.json' \
      --exclude='/config/fixtures.local' \
      "$app_dir/" "$previous_release/"
    find "$previous_release" -type f -o -type l | sed "s#^$previous_release/##" | sort > "$previous_release/.kf-managed-files"
  fi
  if [[ ! -f "$ci_dir/deployed-files" ]]; then
    cp -- "$previous_release/.kf-managed-files" "$ci_dir/deployed-files"
  fi
fi

release_dir="$releases_dir/$release_sha"
# KF_RELEASE_SHA for the API container (deploy/docker-compose.vps.yml): the ops telemetry tags each server start
# with it. A rollback switches it back to the commit it restores.
compose_release_sha=$release_sha
rm -rf -- "$release_dir"
mkdir -p "$release_dir"
tar -xzf "$archive" --no-same-owner --no-same-permissions -C "$release_dir"
[[ -f "$release_dir/.kf-managed-files" ]] || { echo "release archive is missing its file manifest" >&2; exit 1; }
ln -s "$app_dir/.local" "$release_dir/.local"

# Verify and stage the optional immutable iOS snapshot before syncing or restarting the API.
# Staging publishes only a new versioned directory; it never replaces or activates the prior r4 tree.
if [[ -z "$ios_snapshot_archive" ]]; then
  python3 - "$release_dir/src/KickFlight.BootstrapApi/catalog-ios-12346.json" <<'PY'
import json
import pathlib
import sys

doc = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
rows = doc.get("resources")
if not isinstance(rows, list) or not rows:
    raise SystemExit("iOS catalog has no resource rows")
snapshots = {
    row["sourcePath"].split("/", 3)[2]
    for row in rows
    if isinstance(row, dict)
    and isinstance(row.get("sourcePath"), str)
    and row["sourcePath"].startswith(".local/ios-resources/")
}
if snapshots in ({"12346-r5"}, {"12346-r6"}, {"12346-r7"}):
    raise SystemExit("iOS catalog requires a verified snapshot archive; refusing deployment before sync/restart")
PY
fi
if [[ -n "$ios_snapshot_archive" ]]; then
  snapshot_name=$(python3 - "$release_dir/src/KickFlight.BootstrapApi/catalog-ios-12346.json" <<'PY'
import json
import pathlib
import re
import sys

doc = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
rows = doc.get("resources")
if not isinstance(rows, list) or not rows:
    raise SystemExit("iOS catalog has no resource rows")
prefix = ".local/ios-resources/"
names = set()
for row in rows:
    source = row.get("sourcePath") if isinstance(row, dict) else None
    if not isinstance(source, str) or not source.startswith(prefix):
        raise SystemExit("iOS catalog sourcePath is outside the managed snapshot root")
    name = source[len(prefix):].split("/", 1)[0]
    if not re.fullmatch(r"12346-r[1-9][0-9]*", name):
        raise SystemExit("iOS catalog contains an invalid snapshot name")
    names.add(name)
if len(names) != 1:
    raise SystemExit("iOS catalog rows do not share one immutable snapshot")
print(names.pop())
PY
)
  python3 "$release_dir/scripts/stage-ios-resource-snapshot.py" stage \
    --archive "$ios_snapshot_archive" --archive-sha256 "$ios_snapshot_sha256" \
    --manifest "$release_dir/src/KickFlight.BootstrapApi/catalog-ios-12346.json" \
    --snapshot "$snapshot_name" --stage-root "$app_dir/.local/ios-resources"
fi

assets_root=$(python3 - "$app_dir/.env" "$app_dir" <<'PY'
import pathlib
import sys

env_path = pathlib.Path(sys.argv[1])
app = pathlib.Path(sys.argv[2]).resolve()
value = None
for raw in env_path.read_text(encoding="utf-8").splitlines():
    line = raw.strip()
    if line.startswith("export "):
        line = line[7:].lstrip()
    if not line or line.startswith("#") or "=" not in line:
        continue
    key, item = line.split("=", 1)
    if key.strip() == "KF_ASSETS_PATH":
        item = item.strip()
        if len(item) >= 2 and item[0] == item[-1] and item[0] in "\"'":
            item = item[1:-1]
        value = item
        break
root = pathlib.Path(value).expanduser() if value else app.parent / "Kick-Flight-Assets"
if not root.is_absolute():
    # The checked-in example is relative to deploy/, the directory containing the Compose file.
    root = app / "deploy" / root
print(root.resolve())
PY
)
[[ -d "$assets_root" ]] || { echo "configured sibling assets directory is missing" >&2; exit 1; }

# All checks run against the staged release before any live repository files or CDN paths change.
python3 "$release_dir/scripts/build-cdn-tree.py" \
  --assets-root "$assets_root" --repo-root "$release_dir" --verify-all-enabled --dry-run

backup_dir="$assets_root/.cdn-rollback-$release_sha"
activated=false
source_sync_started=false
rollback_on_error() {
  status=$?
  trap - EXIT ERR
  cleanup_upload
  if [[ $status -ne 0 && "$activated" == true ]]; then
    echo "deployment failed; restoring the prior source tree and CDN where available" >&2
    cdn_restored=false
    if [[ -d "$backup_dir" ]]; then
      python3 "$release_dir/scripts/build-cdn-tree.py" \
        --assets-root "$assets_root" --restore-backup "$backup_dir" || true
      cdn_restored=true
    fi
    if [[ "$source_sync_started" == true && -n "$previous_release" && -d "$previous_release" ]]; then
      sync_release "$previous_release" || true
      compose_release_sha=${previous_sha:-unknown}
      compose_from_app up -d --build || true
      if [[ "$deploy_balance" == true ]]; then
        sudo -n systemctl daemon-reload && sudo -n systemctl restart kickflight-balance || true
      fi
    elif [[ "$cdn_restored" == true ]]; then
      compose_from_app restart cdn || true
    fi
  fi
  exit "$status"
}

sync_release() {
  local source_dir=$1
  local new_manifest="$source_dir/.kf-managed-files"
  local old_manifest="$ci_dir/deployed-files"
  if [[ -f "$old_manifest" ]]; then
    while IFS= read -r old_path; do
      [[ -n "$old_path" ]] || continue
      case "/$old_path/" in
        *"/../"*|*"//"*|*"/.env/"*|*"/.local/"*|*"/.ci/"*|*"/data/"*|*"/captures/"*|*"/logs/"*|*"/certs/"*) continue ;;
      esac
      if ! grep -Fqx -- "$old_path" "$new_manifest"; then
        rm -f -- "$app_dir/$old_path"
        parent=$(dirname -- "$app_dir/$old_path")
        while [[ "$parent" != "$app_dir" && "$parent" == "$app_dir"/* ]]; do
          rmdir --ignore-fail-on-non-empty -- "$parent" 2>/dev/null || true
          parent=$(dirname -- "$parent")
        done
      fi
    done < "$old_manifest"
  fi
  # The live tree has root-owned versioned directories; elevate only this managed sync.
  # The existing excludes keep host secrets and runtime state outside the replacement scope.
  sudo -n rsync -a --checksum \
    --exclude='.git' --exclude='/.env' --exclude='/.local' --exclude='/.ci' \
    --exclude='/data' --exclude='/captures' --exclude='/logs' --exclude='/certs' \
    --exclude='/deploy/.env' --exclude='/appsettings.Local.json' \
    --exclude='/config/fixtures.local' \
    "$source_dir/" "$app_dir/"
  cp -- "$new_manifest" "$old_manifest.tmp"
  mv -f -- "$old_manifest.tmp" "$old_manifest"
}

compose_from_app() {
  (
    cd "$app_dir"
    local db_mode
    db_mode=$(python3 - "$app_dir/.env" <<'PY'
import pathlib
import sys

mode = "local"
for raw in pathlib.Path(sys.argv[1]).read_text(encoding="utf-8").splitlines():
    line = raw.strip()
    if line.startswith("export "):
        line = line[7:].lstrip()
    if not line or line.startswith("#") or "=" not in line:
        continue
    key, value = line.split("=", 1)
    if key.strip() == "KF_DB_MODE":
        value = value.strip().strip("\"'")
        mode = value or "local"
if mode not in {"local", "external"}:
    raise SystemExit("KF_DB_MODE must be local or external")
print(mode)
PY
)
    local compose_file="$app_dir/deploy/docker-compose.vps.yml"
    if [[ "$db_mode" == external ]]; then
      compose_file="$app_dir/deploy/docker-compose.vps.external-db.yml"
    fi
    KF_ASSETS_PATH="$assets_root" KF_RELEASE_SHA="$compose_release_sha" docker compose \
      --env-file "$app_dir/.env" --project-name deploy \
      -f "$compose_file" "$@"
  )
}
restart_balance_service() {
  jwt_secret_file=$(sudo -n python3 "$release_dir/scripts/resolve-balance-jwt-secret-path.py" \
    "$app_dir" /etc/kickflight-balance.env)
  sudo -n bash "$release_dir/scripts/ensure-balance-jwt-secret.sh" \
    "$release_dir/tools/balance/generate_jwt_secret.py" "$jwt_secret_file" ubuntu
  sudo -n systemctl daemon-reload
  sudo -n systemctl restart kickflight-balance
}
if [[ "$rebuild_farm" == true ]]; then
  [[ ! -e "$backup_dir" ]] || { echo "stale CDN rollback directory exists; inspect it before retrying" >&2; exit 1; }
  trap rollback_on_error EXIT ERR
  activated=true
  python3 "$release_dir/scripts/build-cdn-tree.py" \
    --assets-root "$assets_root" --repo-root "$release_dir" --verify-all-enabled \
    --atomic --backup-dir "$backup_dir"
fi

trap rollback_on_error EXIT ERR
activated=true
source_sync_started=true
sync_release "$release_dir"

compose_from_app config --quiet
if [[ "$full_deploy" == true ]]; then
  compose_from_app up -d --build
  if [[ "$deploy_caddy" == true ]]; then
    # A full compose up may leave a bind-mounted Caddy config stale in the running container.
    compose_from_app up -d --no-deps --force-recreate caddy
  fi
  if [[ "$deploy_api" == true ]]; then
    compose_from_app restart cdn grpc
  fi
else
  if [[ "$deploy_photon" == true ]]; then
    compose_from_app build photon
    compose_from_app up -d --no-deps photon
  fi
  if [[ "$deploy_api" == true ]]; then
    compose_from_app build api
    compose_from_app up -d --no-deps api
  fi
  if [[ "$deploy_cdn" == true ]]; then
    compose_from_app build cdn
    compose_from_app up -d --no-deps cdn
  fi
  if [[ "$deploy_api" == true ]]; then
    # nginx caches the API container address at startup; gRPC has the same upstream behavior.
    compose_from_app restart cdn grpc
  elif [[ "$rebuild_farm" == true ]]; then
    compose_from_app restart cdn
  fi
  if [[ "$deploy_caddy" == true ]]; then
    compose_from_app up -d --no-deps --force-recreate caddy
  fi
fi
if [[ "$deploy_balance" == true ]]; then
  restart_balance_service
fi

http_port=$(python3 - "$app_dir/.env" <<'PY'
import sys
port = "18080"
for raw in open(sys.argv[1], encoding="utf-8"):
    line = raw.strip()
    if line.startswith("KF_HTTP_PORT="):
        port = line.split("=", 1)[1].strip().strip("\"'") or port
        break
print(port)
PY
)
[[ "$http_port" =~ ^[0-9]{1,5}$ ]] || { echo "invalid KF_HTTP_PORT" >&2; exit 1; }
ready=false
for attempt in {1..60}; do
  if curl --fail --silent --show-error --max-time 15 "http://127.0.0.1:$http_port/health/ready" >/dev/null 2>&1; then
    ready=true
    break
  fi
  sleep 2
done
[[ "$ready" == true ]] || { echo "VPS readiness smoke check failed" >&2; exit 1; }

read -r cdn_path cdn_sha < <(python3 - "$app_dir/config/resources/catalog.json" <<'PY'
import json
import sys
for item in json.load(open(sys.argv[1], encoding="utf-8"))["resources"]:
    path = item.get("requestPath", "")
    if item.get("enabled", True) and path.startswith("/cdn/"):
        print(path, item["sha256"])
        break
else:
    raise SystemExit("catalog has no enabled CDN path")
PY
)
smoke_file=$(mktemp)
curl --fail --silent --show-error --max-time 30 \
  "http://127.0.0.1:$http_port$cdn_path" -o "$smoke_file"
actual_sha=$(sha256sum "$smoke_file" | cut -d ' ' -f 1)
rm -f -- "$smoke_file"
[[ "$actual_sha" == "$cdn_sha" ]] || { echo "served CDN checksum does not match catalog" >&2; exit 1; }

if [[ "$sync_site" == true ]]; then
  expected_site=$(sha256sum "$app_dir/deploy/site/index.html" | cut -d ' ' -f 1)
  curl --fail --silent --show-error --max-time 30 "http://127.0.0.1:$http_port/" -o "$smoke_file"
  actual_site=$(sha256sum "$smoke_file" | cut -d ' ' -f 1)
  rm -f -- "$smoke_file"
  [[ "$actual_site" == "$expected_site" ]] || { echo "served website checksum does not match deploy/site/index.html" >&2; exit 1; }
fi

if [[ "$rebuild_farm" == true && -e "$backup_dir" ]]; then
  [[ "$backup_dir" == "$assets_root/.cdn-rollback-$release_sha" && -d "$backup_dir" && ! -L "$backup_dir" ]] || {
    echo "refusing to remove an unexpected CDN rollback path" >&2
    exit 1
  }
  # The prior CDN tree can be root-owned; remove only this generated rollback directory.
  sudo -n rm -rf -- "$backup_dir"
fi
if [[ "$previous_release" == "$releases_dir/.pre-ci-$release_sha" ]]; then
  rm -rf -- "$previous_release"
fi
printf '%s\n' "$release_sha" > "$state_file.tmp"
mv -f -- "$state_file.tmp" "$state_file"
activated=false
cleanup_upload
trap - EXIT ERR
echo "deployed main commit $release_sha; API/Photon/CDN/Caddy readiness and CDN checksum passed"
