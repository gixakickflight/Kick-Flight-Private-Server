#!/usr/bin/env bash
# Release one adapter commit to the NAS stack (kf-staging Incus instance), by hand. See deploy/nas/README.md.
#
#   deploy/nas/release.sh <commit> [--dry-run] [--allow-unpushed] [--skip-photon] [--yes]
#
# Run from the WSL box, in a clone of the adapter repo. Every step is checked before the next one starts, and
# nothing on the host changes until the preflight (env, keys, generated .local files, every catalog source and
# its SHA-256, the compose file) has passed:
#   1. checks out <commit> into a release worktree and stages its tracked config/ + content/ on the host;
#   2. preflight: host env/keys, .local/ against the catalog's hashes (prints the Windows regeneration command on a
#      mismatch), a --verify-all-enabled dry run of the CDN builder, `docker compose config`;
#   3. builds kickflight-api:<sha> and the Luxon image (deploy/photon/Dockerfile) unless the host already has them,
#      and ships them with docker save | docker load;
#   4. pg_dump of the adapter database (kickflight-pre-<sha>.dump), and a backup of config/, content/ and deploy/;
#   5. syncs tracked config/ + content/, the compose file, nginx.conf and the Photon template;
#   6. rebuilds the nginx /cdn tree (atomic, previous tree kept) from the catalog minus its /apk/ rows;
#   7. retags :local (old one kept as :pre-<sha>), retires a hand-started kf-photon, `docker compose up -d`;
#   8. verifies the API, /health/ready (only the /apk/ rows may be missing), /health/photon and the Luxon listeners.
#
# Host-specific values live only in $KF_ROOT/app/deploy/.env on the host (template: deploy/nas/nas.env.example).
#
# Rollback: the adapter migrates its database at startup, and an older image refuses a newer schema. Roll forward;
# or stop the api, restore kickflight-pre-<sha>.dump, retag kickflight-api:pre-<sha> as :local and `up` again.
set -euo pipefail

NAS_SSH="${KF_NAS_SSH:-root@10.10.20.148}"
NAS_INSTANCE="${KF_NAS_INSTANCE:-kf-staging}"
ROOT="${KF_NAS_ROOT:-/mnt/Tanuki_1/kickflight}"
WORKDIR="${KF_RELEASE_WORKDIR:-$HOME/.cache/kf-nas-release}"
LOCALIZE_CHECKOUT_WIN="${KF_LOCALIZE_CHECKOUT_WIN:-C:\\Users\\Tanuki\\Kick-Flight\\kf-localize-build}"
LOCALIZE_CHECKOUT_WSL="${KF_LOCALIZE_CHECKOUT_WSL:-/mnt/c/Users/Tanuki/Kick-Flight/kf-localize-build}"

dry_run=0 allow_unpushed=0 skip_photon=0 assume_yes=0 target=""
for arg in "$@"; do
  case "$arg" in
    --dry-run) dry_run=1 ;;
    --allow-unpushed) allow_unpushed=1 ;;
    --skip-photon) skip_photon=1 ;;
    --yes) assume_yes=1 ;;
    -h|--help) sed -n '2,25p' "$0"; exit 0 ;;
    -*) echo "unknown option $arg" >&2; exit 2 ;;
    *) target="$arg" ;;
  esac
done
[[ -n "$target" ]] || { echo "usage: $0 <commit> [--dry-run] [--allow-unpushed] [--skip-photon] [--yes]" >&2; exit 2; }

repo="$(git -C "$(dirname "${BASH_SOURCE[0]}")" rev-parse --show-toplevel)"
step() { printf '\n==> %s\n' "$*"; }
die() { printf '!! %s\n' "$*" >&2; exit 1; }

# Runs a bash script (stdin) inside the instance. The NAS host's own shell is zsh, so everything goes through bash.
# Arguments are NAME=value pairs exported to the remote script.
nas() {
  { printf 'set -euo pipefail\n'; for kv in "$@"; do printf 'export %s=%q\n' "${kv%%=*}" "${kv#*=}"; done; cat; } |
    ssh -o BatchMode=yes "$NAS_SSH" "incus exec $NAS_INSTANCE -- bash -s"
}
nas_pipe() { ssh -o BatchMode=yes "$NAS_SSH" "incus exec $NAS_INSTANCE -- $*"; }

# ---------------------------------------------------------------------------------------------------------------
step "Resolve $target"
git -C "$repo" fetch -q origin
commit="$(git -C "$repo" rev-parse --verify "$target^{commit}")"
sha="${commit:0:7}"
echo "commit $commit ($(git -C "$repo" log -1 --format=%s "$commit"))"
if ! git -C "$repo" merge-base --is-ancestor "$commit" origin/staging; then
  [[ $allow_unpushed == 1 ]] || die "$sha is not on origin/staging (pass --allow-unpushed to release it anyway)"
  echo "warning: $sha is not on origin/staging"
fi

wt="$WORKDIR/$sha"
mkdir -p "$WORKDIR"
if [[ ! -d "$wt" ]]; then
  git -C "$repo" worktree add -q --detach "$wt" "$commit"
fi
[[ "$(git -C "$wt" rev-parse HEAD)" == "$commit" ]] || die "$wt is not at $commit"
[[ -z "$(git -C "$wt" status --porcelain --untracked-files=no)" ]] || die "$wt has local changes"

stage_local="$WORKDIR/$sha.stage"
rm -rf "$stage_local"; mkdir -p "$stage_local"
# The NAS hosts none of the remote APKs the catalog lists, and the CDN builder refuses a missing source.
python3 -I - "$wt/config/resources/catalog.json" "$stage_local" <<'PY'
import json, sys, pathlib
catalog, out = sys.argv[1], pathlib.Path(sys.argv[2])
doc = json.load(open(catalog, encoding="utf-8"))
apk = [r for r in doc["resources"] if str(r.get("requestPath", "")).startswith("/apk/")]
doc["resources"] = [r for r in doc["resources"] if r not in apk]
json.dump(doc, open(out / "catalog.nas.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
(out / "apk-ids.txt").write_text("".join(f"{r['id']}\n" for r in apk))
with open(out / "local-rows.tsv", "w") as rows:
    for r in doc["resources"]:
        source = str(r.get("sourcePath", ""))
        if r.get("enabled", True) and source.startswith(".local/"):
            rows.write(f"{source}\t{r.get('sha256', '').lower()}\n")
PY
git -C "$wt" ls-files -- config content > "$stage_local/release-files.txt"
cp "$wt/scripts/build-cdn-tree.py" "$stage_local/"
mkdir -p "$stage_local/deploy/photon"
cp "$wt/deploy/docker-compose.nas.yml" "$wt/deploy/nginx.conf" "$stage_local/deploy/"
cp "$wt/deploy/photon/config.yml.template" "$stage_local/deploy/photon/"

luxon_pin="$(git -C "$wt" rev-parse HEAD:submodules/luxonserver)"
photon_tree="$(git -C "$wt" rev-parse HEAD:deploy/photon)"
photon_image="kickflight/photon:lx${luxon_pin:0:7}-p${photon_tree:0:7}"
api_image="kickflight-api:$sha"
echo "api image    $api_image"
echo "photon image $photon_image (luxonserver $luxon_pin, deploy/photon tree ${photon_tree:0:7})"
echo "catalog      $(grep -c . "$stage_local/local-rows.tsv") .local rows, $(grep -c . "$stage_local/apk-ids.txt") /apk/ rows dropped for the CDN"

# ---------------------------------------------------------------------------------------------------------------
step "Stage $sha on the host ($ROOT/release/$sha)"
stage="$ROOT/release/$sha"
nas STAGE="$stage" <<'EOF'
rm -rf "$STAGE"; mkdir -p "$STAGE/root"
EOF
tar -C "$stage_local" -cf - . | nas_pipe tar -C "$stage" -xf - --no-same-owner
git -C "$wt" archive --format=tar "$commit" config content | nas_pipe tar -C "$stage/root" -xf - --no-same-owner
echo "staged"

# ---------------------------------------------------------------------------------------------------------------
step "Preflight on the host"
set +e
nas ROOT="$ROOT" STAGE="$stage" SHA="$sha" LOCALIZE_WIN="$LOCALIZE_CHECKOUT_WIN" LOCALIZE_WSL="$LOCALIZE_CHECKOUT_WSL" \
    COMMIT="$commit" NAS_SSH="$NAS_SSH" NAS_INSTANCE="$NAS_INSTANCE" <<'EOF'
APP="$ROOT/app"; D="$APP/deploy"; fail=0
for f in .env kf-otomo.env kf-config-gameserver.key economy_gameserver.key session_matchmaker.key chat_adapter.key patch_gameserver.key ops_game_server.key; do
  [[ -s "$D/$f" ]] || { echo "!! missing $D/$f"; fail=1; }
done
[[ -s "$D/.env" ]] && for v in KF_ROOT KF_KEYS_DIR KF_PUBLIC_HOST KF_OTOMO_GATEWAY_URL KF_OTOMO_CONFIG_URL KF_OTOMO_ECONOMY_URL KF_OTOMO_SESSION_URL KF_OTOMO_CHAT_URL KF_OTOMO_PATCH_URL KF_OTOMO_OPS_URL; do
  grep -q "^$v=." "$D/.env" || { echo "!! $D/.env does not set $v (template: deploy/nas/nas.env.example)"; fail=1; }
done
[[ $fail == 0 ]] && echo "env and keys: ok"

# Generated, gitignored files the catalog pins (.local/localize-bundles, .local/weapon-costume-fallbacks).
python3 -I - "$APP" "$STAGE/local-rows.tsv" > "$STAGE/local-check.txt" <<'PY'
import hashlib, sys, pathlib, collections
app, rows = pathlib.Path(sys.argv[1]), sys.argv[2]
bad = collections.Counter(); total = collections.Counter()
for line in open(rows):
    source, expected = line.rstrip("\n").split("\t")
    group = source.split("/")[1]
    total[group] += 1
    path = app / source
    if not path.is_file():
        bad[group] += 1; print(f"missing {source}"); continue
    if hashlib.sha256(path.read_bytes()).hexdigest() != expected:
        bad[group] += 1; print(f"mismatch {source}")
for group in sorted(total):
    print(f"summary {group} {total[group]} {bad[group]}")
PY
while read -r kind group count badn; do
  [[ $kind == summary ]] || continue
  echo ".local/$group: $count rows, $badn missing or mismatched"
  if [[ $badn != 0 ]]; then
    fail=1
    { grep -v '^summary' "$STAGE/local-check.txt" | grep "/$group/" | head -5 | sed 's/^/   /'; } || true
    case "$group" in
      localize-bundles) script=build-localize-bundles.py ;;
      weapon-costume-fallbacks) script=add_weapon_costume_aliases.py ;;
      *) script="" ;;
    esac
    if [[ -n $script ]]; then
      cat <<HINT
   Regenerate on Windows (UnityPy 1.25.3 exists only in Windows Python; the checkout must sit beside Kick-Flight-Assets):
     git -C $LOCALIZE_WSL fetch origin && git -C $LOCALIZE_WSL checkout --detach $COMMIT
     powershell.exe -NoProfile -Command "cd $LOCALIZE_WIN; python scripts/$script; python scripts/$script --check"
   then copy them to the host and re-run this script:
     tar -C $LOCALIZE_WSL/.local -cf - $group | ssh $NAS_SSH "incus exec $NAS_INSTANCE -- tar -C $APP/.local -xf - --no-same-owner"
HINT
    fi
  fi
done < "$STAGE/local-check.txt"

# Every enabled catalog source (assets tree, the new config/content, .local) present with its SHA-256.
ln -sfn "$APP/.local" "$STAGE/root/.local"
if python3 -I "$STAGE/build-cdn-tree.py" --catalog "$STAGE/catalog.nas.json" --assets-root "$ROOT/assets" \
     --repo-root "$STAGE/root" --dry-run --verify-all-enabled > "$STAGE/cdn-dry-run.txt" 2>&1; then
  sed 's/^/cdn: /' "$STAGE/cdn-dry-run.txt"
else
  fail=1; echo "!! CDN builder dry run failed:"; head -20 "$STAGE/cdn-dry-run.txt" | sed 's/^/   /'
fi

# The new compose file against the host's env.
cp "$STAGE/deploy/docker-compose.nas.yml" "$D/docker-compose.nas.yml.next"
if (cd "$D" && docker compose -f docker-compose.nas.yml.next config -q); then echo "compose: ok"; else fail=1; echo "!! compose config failed"; fi
rm -f "$D/docker-compose.nas.yml.next"

# Files on the host that the commit does not track. Reported, never deleted, except files an earlier release of this
# script installed and this commit no longer tracks (they were the repository's).
(cd "$APP" && find config content -type f | LC_ALL=C sort) > "$STAGE/host-files.txt"
LC_ALL=C sort -o "$STAGE/release-files.sorted" "$STAGE/release-files.txt"
LC_ALL=C comm -23 "$STAGE/host-files.txt" "$STAGE/release-files.sorted" > "$STAGE/host-only.txt"
: > "$STAGE/to-delete.txt"
if [[ -s "$D/release-files.txt" ]]; then
  LC_ALL=C sort "$D/release-files.txt" | LC_ALL=C comm -23 - "$STAGE/release-files.sorted" | LC_ALL=C comm -12 - "$STAGE/host-files.txt" > "$STAGE/to-delete.txt"
fi
echo "host-only files (kept): $(wc -l < "$STAGE/host-only.txt"); removed by this release: $(wc -l < "$STAGE/to-delete.txt")"
if grep -q '^config/fixtures/' "$STAGE/host-only.txt"; then
  echo "   warning: host-only fixtures (a fixture that duplicates a repository route disables both copies):"
  { grep '^config/fixtures/' "$STAGE/host-only.txt" | head -10 | sed 's/^/     /'; } || true
fi
exit $fail
EOF
preflight=$?
set -e
[[ $preflight == 0 ]] || die "preflight failed; nothing on the host was changed (staging dir $stage)"
if [[ $dry_run == 1 ]]; then echo; echo "dry run: preflight passed, nothing changed"; exit 0; fi

if [[ $assume_yes != 1 ]]; then
  read -r -p "Release $sha to $NAS_INSTANCE now (restarts the API and Photon)? [y/N] " answer
  [[ "$answer" == y || "$answer" == Y ]] || die "aborted"
fi

# ---------------------------------------------------------------------------------------------------------------
have_image() { nas_pipe docker image inspect "$1" >/dev/null 2>&1; }

step "API image $api_image"
if have_image "$api_image"; then
  echo "already on the host"
else
  docker build -q -t "$api_image" "$wt"
  docker save "$api_image" | nas_pipe docker load
fi

step "Photon image $photon_image"
if [[ $skip_photon == 1 ]]; then
  echo "skipped (--skip-photon)"
elif have_image "$photon_image"; then
  echo "already on the host"
else
  git -C "$wt" submodule update --init --recursive submodules/luxonserver
  docker build -t "$photon_image" -f "$wt/deploy/photon/Dockerfile" "$wt"
  docker save "$photon_image" | nas_pipe docker load
fi

# ---------------------------------------------------------------------------------------------------------------
step "Back up, sync and rebuild the CDN tree"
nas ROOT="$ROOT" STAGE="$stage" SHA="$sha" COMMIT="$commit" API_IMAGE="$api_image" PHOTON_IMAGE="$photon_image" <<'EOF'
APP="$ROOT/app"; D="$APP/deploy"
dump="$ROOT/kickflight-pre-$SHA.dump"
docker exec kickflight-postgres pg_dump -U kickflight -Fc kickflight > "$dump"
docker exec -i kickflight-postgres pg_restore -l < "$dump" > /dev/null
echo "database dump $dump ($(stat -c %s "$dump") bytes, readable by pg_restore)"
tar -C "$APP" -czf "$ROOT/app-backup-pre-$SHA.tgz" config content deploy/docker-compose.nas.yml deploy/nginx.conf \
  $( [[ -f "$D/photon/config.yml.template" ]] && echo deploy/photon/config.yml.template )
echo "config/content/deploy backup $ROOT/app-backup-pre-$SHA.tgz"

while read -r file; do
  if [[ -n "$file" ]]; then rm -f -- "$APP/$file"; echo "removed $file"; fi
done < "$STAGE/to-delete.txt"
tar -C "$STAGE/root" -cf - config content | tar -C "$APP" -xf - --no-same-owner
cp "$STAGE/release-files.txt" "$D/release-files.txt"
echo "synced $(wc -l < "$STAGE/release-files.txt") tracked config/content files"

cp "$D/docker-compose.nas.yml" "$D/docker-compose.nas.yml.bak-pre-$SHA"
cp "$STAGE/deploy/docker-compose.nas.yml" "$D/docker-compose.nas.yml"
# cp writes in place: nginx.conf and the template are single-file bind mounts, and a renamed file would leave the
# running container on the old inode.
cp "$STAGE/deploy/nginx.conf" "$D/nginx.conf"
mkdir -p "$D/photon"; cp "$STAGE/deploy/photon/config.yml.template" "$D/photon/config.yml.template"
printf 'KF_RELEASE_SHA=%s\n' "$COMMIT" > "$D/release.env"
printf 'commit=%s\nbranch=staging\nimage=%s\nphoton=%s\nreleased_at=%s\n' "$COMMIT" "$API_IMAGE" "$PHOTON_IMAGE" "$(date -u +%FT%TZ)" > "$D/DEPLOYED_SHA"

# Keep exactly one previous CDN tree (the one this release replaces).
for old in "$ROOT"/assets/.cdn-pre-*; do
  if [[ -d "$old" && "$old" != "$ROOT/assets/.cdn-pre-$SHA" ]]; then rm -rf -- "$old"; fi
done
rm -rf -- "$ROOT/assets/.cdn-pre-$SHA"
python3 -I "$STAGE/build-cdn-tree.py" --catalog "$STAGE/catalog.nas.json" --assets-root "$ROOT/assets" \
  --repo-root "$APP" --atomic --backup-dir "$ROOT/assets/.cdn-pre-$SHA"
EOF

# ---------------------------------------------------------------------------------------------------------------
step "Switch images and start"
nas SHA="$sha" API_IMAGE="$api_image" PHOTON_IMAGE="$photon_image" SKIP_PHOTON="$skip_photon" ROOT="$ROOT" <<'EOF'
cd "$ROOT/app/deploy"
if docker image inspect kickflight-api:local >/dev/null 2>&1; then
  [[ "$(docker image inspect -f '{{.Id}}' kickflight-api:local)" == "$(docker image inspect -f '{{.Id}}' "$API_IMAGE")" ]] ||
    docker tag kickflight-api:local "kickflight-api:pre-$SHA"
fi
docker tag "$API_IMAGE" kickflight-api:local
if [[ $SKIP_PHOTON != 1 ]]; then
  if docker image inspect kickflight/photon:local >/dev/null 2>&1; then
    [[ "$(docker image inspect -f '{{.Id}}' kickflight/photon:local)" == "$(docker image inspect -f '{{.Id}}' "$PHOTON_IMAGE")" ]] ||
      docker tag kickflight/photon:local "kickflight/photon:pre-$SHA"
  fi
  docker tag "$PHOTON_IMAGE" kickflight/photon:local
fi
# A kf-photon that compose did not create (started by hand with docker run) holds the Luxon ports and the name.
# It is stopped and renamed, never removed, and its image keeps its tag, so it can be started again to roll back.
if docker inspect kf-photon >/dev/null 2>&1 &&
   [[ "$(docker inspect -f '{{index .Config.Labels "com.docker.compose.service"}}' kf-photon)" != photon ]]; then
  retired="kf-photon-handstarted-$(date -u +%Y%m%d%H%M)"
  echo "retiring hand-started kf-photon ($(docker inspect -f '{{.Config.Image}}' kf-photon)) as $retired"
  docker update --restart=no kf-photon >/dev/null
  docker stop kf-photon >/dev/null
  docker rename kf-photon "$retired"
fi
services="postgres api cdn"; [[ $SKIP_PHOTON == 1 ]] || services="$services photon"
docker compose -f docker-compose.nas.yml up -d --no-build $services 2>&1 | tail -6
if docker exec kickflight-cdn nginx -t >/dev/null 2>&1; then docker exec kickflight-cdn nginx -s reload; echo "nginx reloaded"; else docker exec kickflight-cdn nginx -t; exit 1; fi
EOF

# ---------------------------------------------------------------------------------------------------------------
step "Verify"
nas STAGE="$stage" ROOT="$ROOT" SKIP_PHOTON="$skip_photon" <<'EOF'
D="$ROOT/app/deploy"; fail=0
host="$(grep '^KF_PUBLIC_HOST=' "$D/.env" | cut -d= -f2)"
# Logs go to a file first: `docker logs | grep -q` under pipefail reports SIGPIPE, not the match.
for i in $(seq 1 30); do
  docker logs kickflight-api > "$STAGE/api.log" 2>&1 || true
  grep -q 'Now listening' "$STAGE/api.log" && break; sleep 3
done
echo "api: $(docker inspect -f '{{.Config.Image}} {{.State.Status}} restarts={{.RestartCount}} started={{.State.StartedAt}}' kickflight-api)"
docker inspect -f '{{.State.Status}} {{.RestartCount}}' kickflight-api | grep -q '^running 0$' || fail=1
grep -q 'Now listening' "$STAGE/api.log" || { echo "!! the API never logged 'Now listening'"; fail=1; }
# The API logs JSON lines; an error is LogLevel Error/Critical (or the plain-text console format's fail:/crit:).
errors="$(grep -E '"LogLevel":"(Error|Critical)"|NOT served|Unhandled|^(fail|crit):' "$STAGE/api.log" | head -5 || true)"
if [[ -n "$errors" ]]; then printf '%s\n' "$errors" | cut -c1-240; echo "!! errors in the API log (above)"; fail=1; fi
if [[ $SKIP_PHOTON != 1 ]]; then
  echo "photon: $(docker inspect -f '{{.Config.Image}} {{.State.Status}} restarts={{.RestartCount}}' kf-photon)"
  docker inspect -f '{{.State.Status}}' kf-photon | grep -q running || fail=1
  listeners=$(ss -lun | grep -cE ':(5055|5056|5058)\b' || true)
  echo "luxon udp listeners on 5055/5056/5058: $listeners"; [[ $listeners -ge 3 ]] || fail=1
fi
sleep 5
ready="$(curl -s -m 30 -H "Host: $host:18080" http://127.0.0.1:18080/health/ready || true)"
python3 -I - "$STAGE/apk-ids.txt" "$ready" <<'PY' || fail=1
import json, sys
apk = {line.strip() for line in open(sys.argv[1]) if line.strip()}
body = json.loads(sys.argv[2] or "{}")
errors = body.get("errors", [])
other = [e for e in errors if not (e.split(":", 1)[0] in apk and e.endswith("Source file was not found."))]
print(f"/health/ready: {body.get('status')} ({len(errors) - len(other)} missing /apk/ rows, accepted)")
for e in other: print(f"!! {e}")
photon = body.get("photon", {})
print(f"photon as the API sees it: {photon.get('host')} master={photon.get('masterReachable')} game={photon.get('gameReachable')} name={photon.get('nameReachable')}")
sys.exit(1 if other or not photon.get("masterReachable") else 0)
PY
code=$(curl -s -o /dev/null -w '%{http_code}' -m 30 -H "Host: $host:18080" http://127.0.0.1:18080/health/photon || true)
echo "/health/photon: HTTP $code"; [[ $code == 200 ]] || fail=1
grep -i -E 'masters|bundle' "$STAGE/api.log" | tail -3 | cut -c1-220 || true
exit $fail
EOF
echo
echo "released $sha to $NAS_INSTANCE (database dump: $ROOT/kickflight-pre-$sha.dump)"
