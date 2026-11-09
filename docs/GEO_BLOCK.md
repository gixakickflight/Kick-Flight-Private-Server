# Geo-block: Japan, China, South Korea (release 2.11.806)

## What
Firewall-level drop of IPv4+IPv6 traffic from JP/CN/KR source IPs to every public
service port: 80/443 (Caddy, host network), 18080 (HTTP/nginx), 18081 (gRPC),
Photon/Luxon 5055/5056/5058 and 27000-27002 (TCP+UDP). SSH (22) is intentionally
NOT blocked (admin lockout protection). Loopback and Docker-internal traffic
(Caddy -> nginx via 172.18.0.1) are untouched.

## How
- `scripts/kf-geoblock.py` builds nftables table `inet kf_geoblock` from ipdeny.com
  aggregated country zones (v4 + v6), loaded atomically (add+delete+recreate in one
  transaction, so re-runs are idempotent).
- Chain `input` (priority -150) covers host-bound ports; chain `forward` matches
  `ct status dnat` + `ct original proto-dst` to cover Docker-published ports
  (they never hit INPUT). It is its own table, so Docker/ufw rules are not modified.
- Deploy: `scripts/deploy-vps-ci.sh` runs `kf-geoblock.py install` on every deploy
  (non-fatal): copies the tool to `/usr/local/sbin/kf-geoblock`, installs
  `kickflight-geoblock.{service,timer}` (boot + Mondays 04:30 UTC), refreshes.
- Fail-safe: downloads are validated (min entry counts); on failure the last good
  cache in `/var/lib/kickflight-geoblock/` is used; with no cache nothing is applied.
  The ruleset is `nft -c` checked before load; the live table is never flushed to empty.
- Not persisted by nftables.service; the boot timer (`OnBootSec=2min`) reloads it.

## Operate
    sudo kf-geoblock status          # loaded? + drop counters
    sudo kf-geoblock refresh         # re-download + reload now
    sudo systemctl list-timers kickflight-geoblock.timer

## Emergency disable
    sudo kf-geoblock disable         # deletes table, creates /etc/kickflight/geoblock.disabled
    # or just: sudo nft delete table inet kf_geoblock   (back after next refresh unless flag exists)
Re-enable: `sudo rm /etc/kickflight/geoblock.disabled && sudo kf-geoblock refresh`.

## Limits
- No Cloudflare/CDN in front (Caddy+Let's Encrypt, game client hits VPS IP directly), so
  source IPs are real; no CF-IPCountry layer needed. nginx-level 403 was skipped: the
  firewall already sees real IPs, and nginx behind Caddy only sees 172.18.0.1.
- Country data is approximate; VPN/proxy users exiting elsewhere are not blocked, and
  legitimate users on JP/CN/KR VPN exits are. Existing flows are dropped too (rule is
  port-based, not state-based).
- Test locally: `python3 scripts/kf-geoblock.py generate --output out.nft --cache ./cache --check`
  (`unshare -rn nft -c -f out.nft` works without root).
