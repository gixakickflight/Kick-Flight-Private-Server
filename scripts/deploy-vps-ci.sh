#!/usr/bin/env bash
# Remote half of .github/workflows/deploy-vps-main.yml. Runs as the configured SSH user; Docker access
# must be available without sudo. Arguments are supplied by the workflow and are not secrets.
set -Eeuo pipefail

if [[ $# -ne 11 && $# -ne 13 && $# -ne 16 ]]; then
  echo "usage: deploy-vps-ci.sh APP_DIR SHA ARCHIVE API PHOTON CDN FARM SITE CADDY FULL BALANCE [IOS_SNAPSHOT_ARCHIVE IOS_SNAPSHOT_SHA256 [IOS_PROFILE_ARCHIVE IOS_PROFILE_SHA256 ACTIVATE_IOS_PROFILE]]" >&2
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
ios_profile_archive=""
ios_profile_sha256=""
activate_ios_profile_requested=false
ready_file=""
smoke_file=""
if [[ $# -eq 16 ]]; then
  [[ "$ios_snapshot_archive" == "-" ]] && ios_snapshot_archive=""
  [[ "$ios_snapshot_sha256" == "-" ]] && ios_snapshot_sha256=""
  ios_profile_archive=${14}
  ios_profile_sha256=${15}
  activate_ios_profile_requested=${16}
  [[ "$ios_profile_archive" == "-" ]] && ios_profile_archive=""
  [[ "$ios_profile_sha256" == "-" ]] && ios_profile_sha256=""
fi

[[ "$app_dir" == /* ]] || { echo "VPS_APP_DIR must be an absolute path" >&2; exit 2; }
[[ "$app_dir" != "/" && -d "$app_dir" ]] || { echo "VPS_APP_DIR must be an existing application directory" >&2; exit 2; }
[[ "$release_sha" =~ ^[0-9a-f]{40}$ ]] || { echo "invalid commit SHA" >&2; exit 2; }
[[ "$archive" == "/tmp/kf-vps-$release_sha.tar.gz" && -f "$archive" ]] || { echo "release archive path is invalid or missing" >&2; exit 2; }
if [[ -n "$ios_snapshot_archive" || -n "$ios_snapshot_sha256" ]]; then
  [[ $# -eq 13 || $# -eq 16 ]] || { echo "iOS snapshot archive and SHA-256 must be supplied together" >&2; exit 2; }
  [[ "$ios_snapshot_archive" == "/tmp/kf-ios-snapshot-$release_sha.tar.gz" && -f "$ios_snapshot_archive" ]] || {
    echo "iOS snapshot archive path is invalid or missing" >&2; exit 2;
  }
  [[ "$ios_snapshot_sha256" =~ ^[0-9a-f]{64}$ ]] || { echo "invalid iOS snapshot SHA-256" >&2; exit 2; }
fi
if [[ "$activate_ios_profile_requested" != true && "$activate_ios_profile_requested" != false ]]; then
  echo "invalid iOS profile activation flag" >&2; exit 2
fi
if [[ -n "$ios_profile_archive" || -n "$ios_profile_sha256" ]]; then
  [[ $# -eq 16 && "$activate_ios_profile_requested" == true ]] || {
    echo "iOS profile archive may only be supplied for an explicit activation request" >&2; exit 2;
  }
  [[ "$ios_profile_archive" == "/tmp/kf-ios-profile-$release_sha.tar.gz" && -f "$ios_profile_archive" ]] || {
    echo "iOS profile archive path is invalid or missing" >&2; exit 2;
  }
  [[ "$ios_profile_sha256" =~ ^[0-9a-f]{64}$ ]] || { echo "invalid iOS profile SHA-256" >&2; exit 2; }
elif [[ "$activate_ios_profile_requested" == true ]]; then
  echo "explicit iOS profile activation requires its pinned release archive" >&2; exit 2
fi
for flag in "$deploy_api" "$deploy_photon" "$deploy_cdn" "$rebuild_farm" "$sync_site" "$deploy_caddy" "$full_deploy" "$deploy_balance"; do
  [[ "$flag" == true || "$flag" == false ]] || { echo "invalid component flag" >&2; exit 2; }
done
if [[ "$activate_ios_profile_requested" == true ]]; then
  [[ "$deploy_api" == true && "$deploy_photon" == false && "$deploy_cdn" == false && \
     "$rebuild_farm" == false && "$sync_site" == false && "$deploy_caddy" == false && \
     "$full_deploy" == false && "$deploy_balance" == false ]] || {
    echo "iOS profile activation must be an API-only deployment" >&2; exit 2;
  }
fi
cleanup_upload() {
  rm -f -- "$archive"
  [[ -z "$ios_snapshot_archive" ]] || rm -f -- "$ios_snapshot_archive"
  [[ -z "$ios_profile_archive" ]] || rm -f -- "$ios_profile_archive"
  [[ -z "$ready_file" ]] || rm -f -- "$ready_file"
  [[ -z "$smoke_file" ]] || rm -f -- "$smoke_file"
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
ios_wire_floor_file="$ci_dir/ios-wire-floor.json"
ios_wire_floor_sha256=""
ios_wire_floor_active=false
ios_wire_floor_state_valid=true
previous_sha=""
if [[ -f "$state_file" ]]; then
  IFS= read -r previous_sha < "$state_file" || true
fi
# Verify a staged profile with the verifier that ships on the VPS. Until the first deployment of the
# generalized pipeline syncs, $app_dir/scripts still holds the pre-generalization verifier, which only
# knows the 12346/revision-52 profile and takes no identity arguments (that was the only possible latch).
verify_staged_profile() {
  local scripts_dir=$1 av=$2 rev=$3 sha=$4
  if [[ -f "$scripts_dir/stage-ios-profile.py" ]]; then
    python3 "$scripts_dir/stage-ios-profile.py" verify-stage --asset-version "$av" --revision "$rev" \
      --stage-root "$app_dir/.local/ios-resources" --archive-sha256 "$sha"
  elif [[ -f "$scripts_dir/stage-ios-r52-profile.py" && "$av" == 12346 && "$rev" == 52 ]]; then
    python3 "$scripts_dir/stage-ios-r52-profile.py" verify-stage \
      --stage-root "$app_dir/.local/ios-resources" --archive-sha256 "$sha"
  else
    return 1
  fi
}
# Print "<assetVersion> <revision> <sha256>" of the latch using the given scripts directory.
describe_wire_floor() {
  local scripts_dir=$1 line
  if line=$(python3 "$scripts_dir/ios-wire-floor.py" describe "$ios_wire_floor_file" 2>/dev/null); then
    printf '%s\n' "$line"
  else
    # Pre-generalization state verifier: only the 12346/revision-52 floor existed.
    line=$(python3 "$scripts_dir/ios-wire-floor.py" read "$ios_wire_floor_file") || return 1
    printf '12346 52 %s\n' "$line"
  fi
}
ios_profile_asset_version=""
ios_profile_revision=""
if [[ -e "$ios_wire_floor_file" ]]; then
  [[ -f "$app_dir/scripts/ios-wire-floor.py" ]] || {
    echo "iOS wire floor is latched but its state verifier is missing; refusing a downgrade" >&2; exit 1;
  }
  read -r ios_profile_asset_version ios_profile_revision ios_wire_floor_sha256 < <(describe_wire_floor "$app_dir/scripts")
  [[ "$ios_profile_asset_version" =~ ^[1-9][0-9]*$ && "$ios_profile_revision" =~ ^[1-9][0-9]*$ && \
     "$ios_wire_floor_sha256" =~ ^[0-9a-f]{64}$ ]] || {
    echo "iOS wire-floor state could not be read; refusing a downgrade" >&2; exit 1;
  }
  ios_wire_floor_active=true
  verify_staged_profile "$app_dir/scripts" "$ios_profile_asset_version" "$ios_profile_revision" \
    "$ios_wire_floor_sha256" >/dev/null || {
      echo "iOS wire floor is latched but its staged profile failed immutable verification" >&2; exit 1;
    }
fi
ios_profile_enabled=false
if [[ "$ios_wire_floor_active" == true ]]; then
  ios_profile_enabled=true
  [[ "$activate_ios_profile_requested" == true ]] || ios_profile_sha256=$ios_wire_floor_sha256
fi
if [[ "$activate_ios_profile_requested" == true ]]; then
  [[ "$deploy_api" == true || "$full_deploy" == true ]] || {
    echo "iOS profile activation requires an API deployment" >&2; exit 1;
  }
  ios_profile_enabled=true
fi
if [[ "$previous_sha" == "$release_sha" && -z "$ios_snapshot_archive" && -z "$ios_profile_archive" ]]; then
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

# The requested profile identity (assetVersion, revision) comes from the release pin, never from this script.
ios_latch_needed=false
if [[ "$activate_ios_profile_requested" == true ]]; then
  read -r requested_asset_version requested_revision < <(python3 - "$release_dir/config/ios-profile-release.json" "$ios_profile_sha256" <<'PY'
import json
import pathlib
import sys

pin = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
values = [pin.get("assetVersion"), pin.get("revisionFloor")]
if pin.get("archiveSha256") != sys.argv[2] or \
        any(not isinstance(v, int) or isinstance(v, bool) or v <= 0 for v in values):
    raise SystemExit("release profile pin does not match the supplied archive")
print(*values)
PY
)
  [[ "$requested_asset_version" =~ ^[1-9][0-9]*$ && "$requested_revision" =~ ^[1-9][0-9]*$ ]] || {
    echo "release profile pin is invalid" >&2; exit 1;
  }
  if [[ "$ios_wire_floor_active" == true ]]; then
    if [[ "$requested_asset_version" == "$ios_profile_asset_version" && "$requested_revision" == "$ios_profile_revision" ]]; then
      if [[ "$ios_wire_floor_sha256" != "$ios_profile_sha256" ]]; then
        echo "requested profile archive differs from the latched immutable profile" >&2; exit 1
      fi
    elif (( requested_asset_version > ios_profile_asset_version || \
            (requested_asset_version == ios_profile_asset_version && requested_revision > ios_profile_revision) )); then
      # Monotonic upgrade: a strictly newer profile may replace the latch once it is staged and built.
      ios_latch_needed=true
    else
      echo "requested profile is older than the latched immutable profile" >&2; exit 1
    fi
  else
    ios_latch_needed=true
  fi
  ios_profile_asset_version=$requested_asset_version
  ios_profile_revision=$requested_revision
fi

# Verify the existing iOS snapshot when no replacement archive is needed; validate and stage a new
# immutable snapshot archive separately below before syncing or restarting the API.
if [[ -z "$ios_snapshot_archive" && "$ios_profile_enabled" != true ]]; then
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
managed_sources = [row.get("sourcePath") for row in rows if isinstance(row, dict)
                   and isinstance(row.get("sourcePath"), str)
                   and row["sourcePath"].startswith(prefix)]
if not managed_sources:
    print("")
    raise SystemExit(0)
if len(managed_sources) != len(rows):
    raise SystemExit("iOS catalog mixes staged-snapshot and external source paths")
snapshots = set()
for source in managed_sources:
    name, separator, relative = source[len(prefix):].partition("/")
    if not separator or not re.fullmatch(r"12346-r[1-9][0-9]*", name) or not relative.startswith("payload/"):
        raise SystemExit("iOS catalog contains an invalid staged-snapshot sourcePath")
    snapshots.add(name)
if len(snapshots) != 1:
    raise SystemExit("iOS catalog rows do not share one staged snapshot")
print(snapshots.pop())
PY
  )
  if [[ -n "$snapshot_name" ]]; then
    python3 "$release_dir/scripts/stage-ios-resource-snapshot.py" verify-stage \
      --manifest "$release_dir/src/KickFlight.BootstrapApi/catalog-ios-12346.json" \
      --snapshot "$snapshot_name" --stage-root "$app_dir/.local/ios-resources"
  fi
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

if [[ "$ios_profile_enabled" == true ]]; then
  python3 "$release_dir/scripts/ios-wire-floor.py" verify-release "$release_dir" \
    --profile-sha256 "$ios_profile_sha256" \
    --asset-version "$ios_profile_asset_version" --revision "$ios_profile_revision"
  if [[ -n "$ios_profile_archive" ]]; then
    python3 "$release_dir/scripts/stage-ios-profile.py" stage \
      --asset-version "$ios_profile_asset_version" --revision "$ios_profile_revision" \
      --archive "$ios_profile_archive" --archive-sha256 "$ios_profile_sha256" \
      --stage-root "$app_dir/.local/ios-resources"
  fi
  python3 "$release_dir/scripts/stage-ios-profile.py" verify-stage \
    --asset-version "$ios_profile_asset_version" --revision "$ios_profile_revision" \
    --stage-root "$app_dir/.local/ios-resources" --archive-sha256 "$ios_profile_sha256"
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
if [[ "$rebuild_farm" == true ]]; then
  [[ -d "$assets_root" ]] || { echo "configured sibling assets directory is missing" >&2; exit 1; }
  # Validate all resources before the CDN farm is actually rebuilt.
  python3 "$release_dir/scripts/build-cdn-tree.py" \
    --assets-root "$assets_root" --repo-root "$release_dir" --verify-all-enabled --dry-run
fi

backup_dir="$assets_root/.cdn-rollback-$release_sha"
activated=false
source_sync_started=false
rollback_on_error() {
  status=$?
  trap - EXIT ERR
  cleanup_upload
  if [[ -e "$ios_wire_floor_file" ]]; then
    # Re-read the latch: it may have been created or upgraded after the atomic rename but before the caller
    # updated its shell variables. Recovery always runs at the latched identity, never at a pending request.
    ios_wire_floor_active=true
    ios_wire_floor_state_valid=false
    if [[ -f "$release_dir/scripts/ios-wire-floor.py" ]] && \
       latch_line=$(describe_wire_floor "$release_dir/scripts") && \
       read -r latched_asset_version latched_revision latched_sha256 <<<"$latch_line" && \
       [[ "$latched_asset_version" =~ ^[1-9][0-9]*$ && "$latched_revision" =~ ^[1-9][0-9]*$ && \
          "$latched_sha256" =~ ^[0-9a-f]{64}$ ]]; then
      ios_profile_asset_version=$latched_asset_version
      ios_profile_revision=$latched_revision
      ios_wire_floor_sha256=$latched_sha256
      ios_profile_sha256=$latched_sha256
      ios_profile_enabled=true
      ios_wire_floor_state_valid=true
    fi
  fi
  if [[ $status -ne 0 && "$activated" == true ]]; then
    echo "deployment failed; restoring the prior source tree and CDN where available" >&2
    cdn_restored=false
    if [[ -d "$backup_dir" ]]; then
      python3 "$release_dir/scripts/build-cdn-tree.py" \
        --assets-root "$assets_root" --restore-backup "$backup_dir" || true
      cdn_restored=true
    fi
    if [[ -f "$ci_dir/latest-info-backup" ]]; then
      latest_backup=$(cat "$ci_dir/latest-info-backup" 2>/dev/null || true)
      if [[ -n "$latest_backup" && -f "$latest_backup" ]]; then
        cp -f -- "$latest_backup" "$app_dir/.local/maintenance/latest-information.json" || true
      fi
      rm -f -- "$ci_dir/latest-info-backup"
    fi
    if [[ "$ios_wire_floor_active" == true ]]; then
      if [[ "$ios_wire_floor_state_valid" != true ]]; then
        echo "iOS wire-floor state exists but could not be verified; refusing any source rollback" >&2
        exit "$status"
      fi
      echo "iOS wire floor revision $ios_profile_revision is latched; refusing automatic rollback to a native-only API" >&2
      if [[ "$source_sync_started" == true ]]; then
        rollback_release="$release_dir"
        rollback_sha="$release_sha"
        if [[ -n "$previous_release" && -d "$previous_release" ]] && \
           python3 "$release_dir/scripts/ios-wire-floor.py" supports-release "$previous_release" \
             --profile-sha256 "$ios_wire_floor_sha256" \
             --asset-version "$ios_profile_asset_version" --revision "$ios_profile_revision"; then
          rollback_release="$previous_release"
          rollback_sha="$previous_sha"
        fi
        recovery_ok=true
        if ! sync_release "$rollback_release"; then
          echo "iOS profile recovery could not sync a compatible source tree" >&2
          recovery_ok=false
        fi
        compose_release_sha=$rollback_sha
        if [[ "$recovery_ok" == true ]]; then
          if [[ "$full_deploy" == true ]]; then
            if ! compose_from_app up -d --build; then
              echo "iOS profile recovery compose start failed" >&2
              recovery_ok=false
            fi
          elif ! compose_from_app up -d --build api; then
            echo "iOS profile API recovery start failed" >&2
            recovery_ok=false
          fi
        fi
        if [[ "$recovery_ok" == true ]] && ! wait_for_ios_profile_health; then
          echo "iOS profile recovery readiness failed; floor remains latched and operator roll-forward is required" >&2
          recovery_ok=false
        fi
        if [[ "$recovery_ok" == true ]]; then
          echo "iOS profile-compatible recovery is healthy at revision $ios_profile_revision"
        else
          echo "iOS wire floor remains latched; no native-only rollback was attempted" >&2
        fi
      elif [[ "$cdn_restored" == true ]]; then
        compose_from_app restart cdn || true
      fi
      exit "$status"
    fi
    if [[ "$source_sync_started" == true && -n "$previous_release" && -d "$previous_release" ]]; then
      compose_release_sha=${previous_sha:-unknown}
      if [[ "$activate_ios_profile_requested" == true ]]; then
        ios_profile_enabled=false
        if ! sync_release "$previous_release"; then
          echo "pre-latch iOS profile activation failed and prior source restore failed; operator recovery is required" >&2
        elif ! compose_from_app up -d --no-deps --build api; then
          echo "pre-latch iOS profile activation failed and prior API restart failed; operator recovery is required" >&2
        elif ! wait_for_previous_api_health; then
          echo "pre-latch iOS profile activation failed and prior API readiness check failed; operator recovery is required" >&2
        else
          echo "pre-latch iOS profile activation failed; prior API restored and readiness verified"
        fi
      else
        sync_release "$previous_release" || true
        compose_from_app up -d --build || true
        if [[ "$deploy_balance" == true ]]; then
          sudo -n systemctl daemon-reload && sudo -n systemctl restart kickflight-balance || true
        fi
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
    KF_ASSETS_PATH="$assets_root" KF_RELEASE_SHA="$compose_release_sha" \
      KF_IOS_PROFILE_ENABLED="$ios_profile_enabled" \
      KF_IOS_PROFILE_ASSET_VERSION="${ios_profile_asset_version:-0}" \
      KF_IOS_PROFILE_REVISION="${ios_profile_revision:-0}" \
      KF_IOS_R52_PROFILE_ENABLED="$ios_profile_enabled" docker compose \
      --env-file "$app_dir/.env" --project-name deploy \
      -f "$compose_file" "$@"
  )
}
# Route count of the staged profile, taken from its own validated report (3389 for the 12346/52 profile).
ios_profile_resource_count() {
  python3 - "$app_dir/.local/ios-resources/${ios_profile_asset_version}-r${ios_profile_revision}/profile/profile-report.json" <<'PY'
import json
import pathlib
import sys

count = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))["resourceRoutes"]
if not isinstance(count, int) or isinstance(count, bool) or count <= 0:
    raise SystemExit(1)
print(count)
PY
}
wait_for_ios_profile_health() {
  local port resource_count
  resource_count=$(ios_profile_resource_count) || return 1
  port=$(python3 - "$app_dir/.env" <<'PY'
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
  [[ "$port" =~ ^[0-9]{1,5}$ ]] || return 1
  ready_file=$(mktemp)
  for attempt in {1..60}; do
    if curl --fail --silent --max-time 15 "http://127.0.0.1:$port/health/ready" -o "$ready_file" 2>/dev/null && \
       python3 - "$ready_file" "$ios_profile_revision" "$resource_count" <<'PY'
import json
import pathlib
import sys

body = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
if body.get("status") != "ready" or body.get("iosCatalogReady") is not True or \
        body.get("iosProfile") != f"r{sys.argv[2]}-test" or body.get("iosProfileRevision") != int(sys.argv[2]) or \
        body.get("iosResourceCount") != int(sys.argv[3]):
    raise SystemExit(1)
PY
    then
      rm -f -- "$ready_file"
      ready_file=""
      return 0
    fi
    sleep 2
  done
  rm -f -- "$ready_file"
  ready_file=""
  return 1
}
wait_for_previous_api_health() {
  local port
  port=$(python3 - "$app_dir/.env" <<'PY'
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
  [[ "$port" =~ ^[0-9]{1,5}$ ]] || return 1
  ready_file=$(mktemp)
  for attempt in {1..60}; do
    if curl --fail --silent --max-time 15 "http://127.0.0.1:$port/health/ready" -o "$ready_file" 2>/dev/null && \
       python3 - "$ready_file" <<'PY'
import json
import pathlib
import sys

body = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
if body.get("status") != "ready":
    raise SystemExit(1)
PY
    then
      rm -f -- "$ready_file"
      ready_file=""
      return 0
    fi
    sleep 2
  done
  rm -f -- "$ready_file"
  ready_file=""
  return 1
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

# Priority is Repo -> Overrides -> Base. The API serves .local/masters-overrides/masters_<table>.json instead of
# config/masters_<table>.json whenever an override exists, and .local/ is never deployed, so a master change
# published in the repo would be shadowed. Merge the repo's masters changes (previously deployed commit ->
# this commit) into the existing overrides now: after the new release is verified, before the live source tree
# is replaced and before the API is (re)started, so it loads the merged data. BASE comes from the VPS's own
# state (.local/deployed-masters-commit, else .ci/current-sha), not from the workflow's "before" SHA, which
# can differ from what is really deployed after a skipped or failed run. Any error aborts the deployment here
# with every override untouched (the script restores the CDN if the farm was already swapped). Dry run:
#   python3 scripts/merge-master-overrides.py --app-dir "$VPS_APP_DIR" --repo-dir <release>/config --commit <sha> --dry-run
if [[ -f "$release_dir/scripts/merge-master-overrides.py" ]]; then
  if ! python3 "$release_dir/scripts/merge-master-overrides.py" \
      --app-dir "$app_dir" --repo-dir "$release_dir/config" --commit "$release_sha" \
      --releases-dir "$releases_dir" --fallback-base-dir "$previous_release/config"; then
    echo "master override merge failed; aborting before any service restart (overrides were not modified)" >&2
    exit 1
  fi
fi

trap rollback_on_error EXIT ERR
activated=true
source_sync_started=true
sync_release "$release_dir"

# Update persistent Latest Information in-game announcement if docs/updates-page.html exists.
# Inserts new release articles safely and idempotently into .local/maintenance/latest-information.json,
# preserving prior announcement history, snapshot fields, and permissions, with pre-write backups.
if [[ "$sync_site" == true && -f "$app_dir/docs/updates-page.html" && -f "$release_dir/scripts/update-latest-information.py" ]]; then
  ci_info_backup="$ci_dir/latest-info-backup"
  rm -f -- "$ci_info_backup"
  python3 "$release_dir/scripts/update-latest-information.py" \
    --state-file "$app_dir/.local/maintenance/latest-information.json" \
    --source-docs "$app_dir/docs/updates-page.html" \
    --article-id "v2-11-804" \
    --backup-dir "$app_dir/.local/updates-backups" \
    --record-backup-path "$ci_info_backup"
fi
if [[ "$sync_site" == true && -f "$release_dir/tools/badges/prepare_announcement.py" && -f "$app_dir/.local/maintenance/latest-information.json" ]]; then
  python3 "$release_dir/tools/badges/prepare_announcement.py" \
    --state-file "$app_dir/.local/maintenance/latest-information.json" \
    --backup-dir "$app_dir/.local/updates-backups"
fi

compose_from_app config --quiet
ios_api_prebuilt=false
if [[ "$ios_latch_needed" == true ]]; then
  # Complete the only fallible build before latching the irreversible client revision floor.
  compose_from_app build api
  # Remove the old revision-47 responder before the floor can be observed as active.
  compose_from_app stop api
  ios_api_prebuilt=true
fi
if [[ "$ios_latch_needed" == true ]]; then
  python3 "$release_dir/scripts/ios-wire-floor.py" latch "$ios_wire_floor_file" \
    --asset-version "$ios_profile_asset_version" --revision "$ios_profile_revision" \
    --profile-sha256 "$ios_profile_sha256" --commit "$release_sha"
  ios_wire_floor_active=true
  echo "Latched persistent iOS wire floor at revision $ios_profile_revision; the iOS profile will remain enabled on future deployments."
fi
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
    if [[ "$ios_api_prebuilt" != true ]]; then
      compose_from_app build api
    fi
    compose_from_app up -d --no-deps api
  fi
  if [[ "$deploy_cdn" == true ]]; then
    compose_from_app build cdn
    compose_from_app up -d --no-deps cdn
  fi
  if [[ "$deploy_api" == true ]]; then
    if [[ "$ios_profile_enabled" != true ]]; then
      # Preserve the existing restart behavior for legacy deployments; the iOS profile uses the dynamic Docker DNS resolver.
      compose_from_app restart cdn grpc
    fi
  elif [[ "$rebuild_farm" == true ]]; then
    compose_from_app restart cdn
  fi
  if [[ "$deploy_caddy" == true ]]; then
    compose_from_app up -d --no-deps --force-recreate caddy
  fi
  if [[ -f "$app_dir/deploy/badges/Dockerfile" && "$ios_profile_enabled" != true ]]; then
    compose_from_app build badges
    compose_from_app up -d --no-deps badges
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
ios_profile_resource_total=0
if [[ "$ios_profile_enabled" == true ]]; then
  ios_profile_resource_total=$(ios_profile_resource_count)
fi
ready=false
ready_file=$(mktemp)
for attempt in {1..60}; do
  if curl --fail --silent --show-error --max-time 15 "http://127.0.0.1:$http_port/health/ready" -o "$ready_file" 2>/dev/null; then
    if python3 - "$ready_file" "$ios_profile_enabled" "${ios_profile_revision:-0}" "${ios_profile_resource_total:-0}" <<'PY'
import json
import pathlib
import sys

body = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
if body.get("status") != "ready" or body.get("iosCatalogReady") is not True:
    raise SystemExit(1)
enabled = sys.argv[2] == "true"
expected_profile = f"r{sys.argv[3]}-test" if enabled else "native"
if body.get("iosProfile") != expected_profile:
    raise SystemExit(1)
if enabled and (body.get("iosProfileRevision") != int(sys.argv[3]) or body.get("iosResourceCount") != int(sys.argv[4])):
    raise SystemExit(1)
PY
    then
      ready=true
      break
    fi
  fi
  sleep 2
done
[[ "$ready" == true ]] || { echo "VPS readiness smoke check failed" >&2; exit 1; }
rm -f -- "$ready_file"
ready_file=""

if [[ "$ios_profile_enabled" == true ]]; then
  smoke_file=$(mktemp)
  curl --fail --silent --show-error --max-time 30 \
    -H "Host: kick-flight-fenix.us.ci" \
    "http://127.0.0.1:$http_port/v1/list/$ios_profile_asset_version/$ios_profile_revision" -o "$smoke_file"
python3 - "$release_dir/scripts/stage-ios-profile.py" "$smoke_file" "$ios_profile_asset_version" "$ios_profile_revision" <<'PY'
import importlib.util
import pathlib
import sys

script = pathlib.Path(sys.argv[1])
spec = importlib.util.spec_from_file_location("stage_ios_profile", script)
if spec is None or spec.loader is None:
    raise SystemExit("iOS profile protobuf validator is unavailable")
stager = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = stager
spec.loader.exec_module(stager)
stager._validate_database(stager.ProfileSpec(int(sys.argv[3]), int(sys.argv[4])),
                          pathlib.Path(sys.argv[2]).read_bytes(), int(sys.argv[4]))
PY

  read -r ios_cdn_path ios_cdn_sha < <(python3 - "$app_dir/.local/ios-resources/${ios_profile_asset_version}-r${ios_profile_revision}/profile/catalog.json" <<'PY'
import json
import pathlib
import sys

resources = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))["resources"]
rows = sorted((row for row in resources if row.get("enabled") is True), key=lambda row: row["requestPath"])
if not rows:
    raise SystemExit("iOS profile has no enabled CDN rows")
print(rows[0]["requestPath"], rows[0]["sha256"])
PY
)
  curl --fail --silent --show-error --max-time 30 \
    -H "Host: kick-flight-fenix.us.ci" \
    "http://127.0.0.1:$http_port$ios_cdn_path" -o "$smoke_file"
  actual_ios_sha=$(sha256sum "$smoke_file" | cut -d ' ' -f 1)
  [[ "$actual_ios_sha" == "$ios_cdn_sha" ]] || {
    echo "iOS profile CDN smoke payload checksum does not match the staged profile" >&2; exit 1;
  }
  rm -f -- "$smoke_file"
  smoke_file=""
  echo "iOS profile direct-client list and CDN health checks passed at revision $ios_profile_revision"
fi

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

  if [[ -f "$app_dir/deploy/site/updates-2.11.4.html" ]]; then
    expected_updates=$(sha256sum "$app_dir/deploy/site/updates-2.11.4.html" | cut -d ' ' -f 1)
    curl --fail --silent --show-error --max-time 30 "http://127.0.0.1:$http_port/updates-2.11.4.html" -o "$smoke_file"
    actual_updates=$(sha256sum "$smoke_file" | cut -d ' ' -f 1)
    rm -f -- "$smoke_file"
    [[ "$actual_updates" == "$expected_updates" ]] || { echo "served standalone updates checksum does not match deploy/site/updates-2.11.4.html" >&2; exit 1; }
  fi

  if [[ -f "$app_dir/deploy/site/updates-2.11.804.html" ]]; then
    expected_updates=$(sha256sum "$app_dir/deploy/site/updates-2.11.804.html" | cut -d ' ' -f 1)
    curl --fail --silent --show-error --max-time 30 "http://127.0.0.1:$http_port/updates-2.11.804.html" -o "$smoke_file"
    actual_updates=$(sha256sum "$smoke_file" | cut -d ' ' -f 1)
    rm -f -- "$smoke_file"
    [[ "$actual_updates" == "$expected_updates" ]] || { echo "served standalone updates checksum does not match deploy/site/updates-2.11.804.html" >&2; exit 1; }
  fi

  # Announcement content is variable by design; only require the pages to be served.
  for info_path in /webview/information/index /updates; do
    curl --fail --silent --show-error --max-time 15 "http://127.0.0.1:$http_port$info_path" -o /dev/null
  done
  echo "Information pages respond on /webview/information/index and /updates"

  if [[ -f "$app_dir/deploy/badges/Dockerfile" ]]; then
    if curl --fail --silent --show-error --max-time 15 "http://127.0.0.1:$http_port/badges/health" >/dev/null 2>&1; then
      echo "Badges health verified on /badges/health"
    fi
  fi
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
rm -f -- "$ci_dir/latest-info-backup"
activated=false
cleanup_upload
trap - EXIT ERR
echo "deployed main commit $release_sha; API/Photon/CDN/Caddy readiness and CDN checksum passed"
