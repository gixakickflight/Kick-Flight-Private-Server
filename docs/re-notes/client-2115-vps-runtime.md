# Kick Flight 2.11.5 VPS Runtime Proof

- Candidate SHA-256: `ae7b4ed16f4544b18348ed5068f02c3a582ed7cbb3b64d46fe9811a704405f4f`; certificate SHA-256 `81ca6334fd069edbc8756a49fc135c91141eba8265cb64d0510ed9b231b21537`; version 2.11.5/code 60.
- Endpoint literals: API/resource `http://159.54.134.159:18080`; Photon `159.54.134.159`; gRPC target stays dynamic from `StartupResponseData`.
- Flags: Photon, AI roles, role targeting, and deposit enabled; generic/damage/result DIAG disabled.
- Static verification: 5 endpoint preflight tests passed; 245 ARM64, 1 ARMv7, 17 libunity records and 5 metadata replacements exactly reproduced from the pristine base; zero ARM64 overlaps; owner stub `0x159D1BC` is `RET`; signer and zipalign verified.
- Runtime verdict: **BLOCKED / NOT PASS**. Exact candidate was not installed. `KickFlight_VPSTest` and API 35 `KickFlight_PopupTest` exited before boot completion (QEMU 139); TAP START/login/lobby/match/battle were not reached. No screenshot of 2.11.5 exists.
- VPS read-only logs at `2026-10-20T05:09:29Z` (last 30m): zero lines containing `2.11.5` across API/Caddy/gRPC/Photon; API had 68 generic boot/start/battle path lines, not attributable to this candidate.
- Prior baseline 2.11.4 NXDOMAIN evidence remains under original checkout `.local/run/vps-test/`; baseline not rerun in this attempt. Personal `KickFlight_API35`, local API, and Photon were not touched.
- Gate: do not publish or raise the minimum until exact-artifact live VPS gameplay runtime proof passes. Full machine-readable record: original checkout `.local/vps-apk-runtime-proof.json`.
