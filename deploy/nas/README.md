# NAS release (kf-staging), by hand

The NAS stack runs in the `kf-staging` Incus instance on the TrueNAS (`ssh root@10.10.20.148`; the host shell is
zsh, so everything runs through `incus exec kf-staging -- bash`). It is released by hand from a staging commit.
No CI job or webhook releases it.

```
deploy/nas/release.sh <staging-commit> --dry-run   # preflight only: nothing on the host changes
deploy/nas/release.sh <staging-commit>             # release (asks before restarting; --yes skips the prompt)
```

Run it from the WSL box in a clone of this repository. It needs `docker` (the images are built locally), ssh to
the Incus host, and network access for the Luxon submodule the first time a Photon image is built.

## What runs where

| Container | Image | Source | Notes |
|---|---|---|---|
| `kickflight-api` | `kickflight-api:local` (= `:<sha>`) | `Dockerfile` | gRPC 18081 published; HTTP 8080 only behind nginx |
| `kickflight-cdn` | `nginx:1.27-alpine` | `deploy/nginx.conf` | 18080: `/cdn/` from `$KF_ROOT/assets/cdn`, everything else proxied |
| `kickflight-postgres` | `postgres:17-alpine` | | not published |
| `kf-photon` | `kickflight/photon:local` (= `:lx<luxon>-p<deploy/photon tree>`) | `deploy/photon/Dockerfile` | host networking |

All four come from `deploy/docker-compose.nas.yml`, project `deploy`, run from `$KF_ROOT/app/deploy`.

Luxon is `submodules/luxonserver` (tanukifurhire/luxonserver, branch `tanuki-server`), built with GCC 14, with the
`deploy/photon/luxon-patches/*.patch` applied to its nested Luxon at build time (0006 receive window, 0007 resend
tolerance, 0011 PhotonPing echo). Incus proxies UDP `100.116.38.51:{5055,5056,5058}` to the guest, where Luxon binds
them directly and advertises `KF_PUBLIC_HOST`.

## Host layout

```
$KF_ROOT/                         /mnt/Tanuki_1/kickflight
  app/                            the "repository root" the API sees as /srv/repo (not a git checkout)
    config/ content/              tracked files of the deployed commit (+ host-only files, reported, kept)
    .local/                       generated, gitignored: localize-bundles/, weapon-costume-fallbacks/, ...
    data/                         writable runtime state (otomo masters cache, replays, diagnostics)
    deploy/
      docker-compose.nas.yml      from the deployed commit
      nginx.conf                  from the deployed commit
      photon/config.yml.template  from the deployed commit
      .env                        HOST VALUES (0600): addresses, otomo URLs, key dir, client hosts
      kf-otomo.env                Otomo__TokenSecret (0600)
      *.key                       otomo service keys (0600)
      release.env                 KF_RELEASE_SHA, written by release.sh
      DEPLOYED_SHA                what is deployed (commit, images, time)
      release-files.txt           the tracked config/content files of the deployed commit
  assets/octo_sorted/             the bundle tree (uploaded once)
  assets/cdn/                     the nginx farm, rebuilt by every release (previous one: assets/.cdn-pre-<sha>)
  pgdata/                         PostgreSQL
  release/<sha>/                  staging area of a release
  kickflight-pre-<sha>.dump       pg_dump taken before releasing <sha>
  app-backup-pre-<sha>.tgz        config/, content/ and deploy files before releasing <sha>
```

Nothing host-specific is in the repository: `deploy/.env` holds every address and path
(template `deploy/nas/nas.env.example`). Compose reads it for interpolation and passes it to the API as an
`env_file`, which is how the `Harness__DirectClientHosts__N` list reaches it. The NAS loads no `appsettings.json`
(`ASPNETCORE_CONTENTROOT=/srv/repo`), so every API setting is env in the compose file.

## The steps release.sh runs

1. Worktree of the commit (`~/.cache/kf-nas-release/<sha>`); it must be on `origin/staging` unless
   `--allow-unpushed`. The commit's tracked `config/` and `content/` are staged on the host in `release/<sha>/`.
2. Preflight (changes nothing):
   - `deploy/.env`, `kf-otomo.env` and the five keys exist; `.env` sets every required value;
   - every catalog row sourced from `.local/` matches its SHA-256 (see "Generated files");
   - `scripts/build-cdn-tree.py --dry-run --verify-all-enabled` against the staged tree and a copy of the catalog
     without its `/apk/` rows (the NAS hosts none of the remote APKs, and the builder refuses a missing source);
   - `docker compose config` of the new compose file with the host's env;
   - host files the commit does not track are listed (kept); a host-only file under `config/fixtures/` is
     flagged, because a fixture that duplicates a repository route disables both copies (404).
3. `kickflight-api:<sha>` and the Photon image are built locally unless the host already has them, and shipped
   with `docker save | docker load`.
4. `pg_dump` to `kickflight-pre-<sha>.dump` (checked with `pg_restore -l`), and `app-backup-pre-<sha>.tgz`.
5. Sync: tracked `config/` and `content/` are written over the host's (files a previous release installed and this
   commit dropped are removed; host-only files are kept), plus the compose file, `nginx.conf` and the Photon template
   (written in place: they are single-file bind mounts).
6. CDN tree rebuilt atomically; the previous tree is kept as `assets/.cdn-pre-<sha>`.
7. `:local` retagged (the previous image stays as `:pre-<sha>`); a `kf-photon` that compose did not create is
   stopped, set to `--restart=no` and renamed `kf-photon-handstarted-<time>` (never removed; its image keeps its
   tag); `docker compose up -d --no-build`; nginx reloaded.
8. Verify: API running with 0 restarts and "Now listening", no `NOT served`/`Unhandled`/`fail:` in its log;
   Luxon running with its UDP listeners; `/health/ready` with only the `/apk/` rows missing; `/health/photon` 200.

## Generated files (.local)

The catalog pins the SHA-256 of 665 `.local/localize-bundles/*` and 35 `.local/weapon-costume-fallbacks/*`. They
are generated, never committed, and need UnityPy 1.25.3, which only the Windows Python has. Generation must run in
a checkout that sits beside `Kick-Flight-Assets` (`C:\Users\Tanuki\Kick-Flight\kf-localize-build`, whose `origin`
is the WSL clone). When the preflight reports a mismatch it prints the exact commands; they are:

```
git -C /mnt/c/Users/Tanuki/Kick-Flight/kf-localize-build fetch origin
git -C /mnt/c/Users/Tanuki/Kick-Flight/kf-localize-build checkout --detach <commit>
powershell.exe -NoProfile -Command "cd C:\Users\Tanuki\Kick-Flight\kf-localize-build; python scripts/build-localize-bundles.py; python scripts/build-localize-bundles.py --check"
tar -C /mnt/c/Users/Tanuki/Kick-Flight/kf-localize-build/.local -cf - localize-bundles | \
  ssh root@10.10.20.148 "incus exec kf-staging -- tar -C /mnt/Tanuki_1/kickflight/app/.local -xf - --no-same-owner"
```

(`scripts/add_weapon_costume_aliases.py` likewise for `weapon-costume-fallbacks`.) Then re-run the release.

## Verifying by hand

```
ssh root@10.10.20.148 "incus exec kf-staging -- bash -c 'curl -s -H \"Host: 100.116.38.51:18080\" http://127.0.0.1:18080/health/ready'"
ssh root@10.10.20.148 "incus exec kf-staging -- bash -c 'curl -s -H \"Host: 100.116.38.51:18080\" http://127.0.0.1:18080/health/photon'"
```

`/health/ready` is `not-ready` only because of the `apk-remote-*` rows (`Source file was not found.`); anything
else is a real error. The API has no healthcheck: judge it by running + 0 restarts + "Now listening" + a clean log.

## Rollback

The API migrates its database at startup, and an older image refuses a newer schema (it crash-loops), so prefer
rolling forward. To go back to the previous release:

```
cd /mnt/Tanuki_1/kickflight/app/deploy
docker compose -f docker-compose.nas.yml stop api
# only if the release migrated the schema:
docker exec -i kickflight-postgres pg_restore -U kickflight -d kickflight --clean --if-exists < ../../kickflight-pre-<sha>.dump
docker tag kickflight-api:pre-<sha> kickflight-api:local
tar -C /mnt/Tanuki_1/kickflight/app -xzf ../../app-backup-pre-<sha>.tgz
python3 /mnt/Tanuki_1/kickflight/release/<sha>/build-cdn-tree.py --assets-root /mnt/Tanuki_1/kickflight/assets --restore-backup /mnt/Tanuki_1/kickflight/assets/.cdn-pre-<sha>
docker compose -f docker-compose.nas.yml up -d --no-build
```

Photon: `docker tag kickflight/photon:pre-<sha> kickflight/photon:local` and `up -d photon`. To go back to the
hand-started container (image `kickflight/photon:pingfix`): `docker compose -f docker-compose.nas.yml stop photon`,
`docker rm` the compose one, `docker rename kf-photon-handstarted-<time> kf-photon`, `docker start kf-photon`.
