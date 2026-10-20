#!/usr/bin/env bash
# Deploy the Kick-Flight API + CDN to the TrueNAS.
#
# Run from WSL (the Windows host reaches the NAS over the LAN; see deploy/README.md):
#
#     bash scripts/deploy-nas.sh
#     bash scripts/deploy-nas.sh --skip-assets      # asset tree is already uploaded
#
# Every step is idempotent. Assets are the only slow part (~1 GB), so they are skipped by default once
# present - pass --assets to force a re-sync.
#
# The image is built ON the NAS rather than on this machine: it is amd64 Linux and the NAS runs the same
# architecture, so there is no cross-build and no registry to push through.
set -euo pipefail

NAS="${NAS:-root@10.10.20.148}"
ROOT="/mnt/Tanuki_1/kickflight"
REPO="${REPO:-/mnt/c/Users/Tanuki/Kick-Flight/Kick-Flight-Private-Server}"
ASSETS_SRC="${ASSETS_SRC:-/mnt/c/Users/Tanuki/Kick-Flight/Kick-Flight-Assets}"

force_assets=0   # --assets: re-sync even if the tree is already there
skip_assets=0    # --skip-assets: never touch it
restart=1
while [ $# -gt 0 ]; do
    case "$1" in
        --assets)      force_assets=1 ;;
        --skip-assets) skip_assets=1 ;;
        --no-restart)  restart=0 ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

say() { printf '\n=== %s ===\n' "$*"; }

say "checking access to $NAS"
ssh -o BatchMode=yes -o ConnectTimeout=8 "$NAS" 'echo ok' >/dev/null || {
    echo "cannot ssh to $NAS - is the SSH service up and is this key authorised?" >&2; exit 1; }

say "creating dataset layout"
ssh -o BatchMode=yes "$NAS" "mkdir -p $ROOT/app/data/users $ROOT/build $ROOT/assets"

# config/ is what the server reads at startup; content/ holds the repository-local resource bundles the
# catalog addresses as 'content/resources/...'.
say "syncing config/ and content/"
rsync -a --delete "$REPO/config/"  "$NAS:$ROOT/app/config/"
rsync -a --delete "$REPO/content/" "$NAS:$ROOT/app/content/"
rsync -a "$REPO/deploy/" "$NAS:$ROOT/app/deploy/"

assets_present=$(ssh -o BatchMode=yes "$NAS" "[ -d $ROOT/assets/octo_sorted ] && echo yes || echo no")
if [ "$skip_assets" = 1 ]; then
    say "skipping the asset tree (--skip-assets)"
elif [ "$force_assets" = 1 ] || [ "$assets_present" = no ]; then
    say "uploading the asset tree (~1 GB)"
    rsync -a --info=progress2 --no-inc-recursive \
        "$ASSETS_SRC/octo_sorted/" "$NAS:$ROOT/assets/octo_sorted/"
else
    say "asset tree already present; pass --assets to re-sync"
fi

# The farm is rebuilt every deploy: it is a few seconds, and it is the only way to be sure a key that left
# the catalog stopped being served.
say "building the /cdn tree"
rsync -a "$REPO/scripts/build-cdn-tree.py" "$NAS:$ROOT/build/build-cdn-tree.py"
ssh -o BatchMode=yes "$NAS" \
    "python3 $ROOT/build/build-cdn-tree.py --assets-root $ROOT/assets --repo-root $ROOT/app"

say "staging the build context"
# bin/obj are Windows-built and would poison a Linux restore; data/ is mounted at runtime, never built in.
rsync -a --delete --exclude 'bin/' --exclude 'obj/' --exclude 'data/' "$REPO/src/" "$NAS:$ROOT/build/src/"
rsync -a "$REPO/Dockerfile" "$REPO/.dockerignore" "$NAS:$ROOT/build/"

say "building the image on the NAS"
ssh -o BatchMode=yes "$NAS" "cd $ROOT/build && docker build -t kickflight-api:local ."

if [ "$restart" = 1 ]; then
    say "restarting the stack"
    api_before=$(ssh -o BatchMode=yes "$NAS" "docker inspect -f '{{.Id}}' kickflight-api 2>/dev/null || true")
    ssh -o BatchMode=yes "$NAS" "cd $ROOT/app/deploy && docker compose -f docker-compose.nas.yml up -d"
    api_after=$(ssh -o BatchMode=yes "$NAS" "docker inspect -f '{{.Id}}' kickflight-api 2>/dev/null || true")

    # nginx resolves 'proxy_pass http://kickflight-api:8080' once, at startup, and keeps that address for the
    # life of the worker process. Recreating the API container gives it a new address on the compose network, so
    # nginx would keep dialing the dead one and answer 502 to every proxied request - while /cdn/, which it
    # serves from disk, kept working, which is what makes this look like an API outage rather than a proxy one.
    # Restarting nginx is what re-resolves it. Only when the container actually changed, so a config-only
    # deploy does not bounce the CDN for nothing.
    if [ "$api_before" != "$api_after" ]; then
        say "the API container was recreated; restarting the CDN so nginx re-resolves its upstream"
        ssh -o BatchMode=yes "$NAS" "docker restart kickflight-cdn" >/dev/null
    fi

    ssh -o BatchMode=yes "$NAS" "docker ps --filter name=kickflight --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'"
fi

say "done"
echo "Ready check (allow ~5 s; it probes Photon before answering):"
echo "  curl -m 10 http://10.10.20.148:18080/health/ready"
