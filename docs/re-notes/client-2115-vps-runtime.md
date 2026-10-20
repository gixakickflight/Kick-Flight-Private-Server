# Client 2.11.5 Live VPS Runtime Proof & Diagnostic Notes

## 1. Candidate Artifact Specification
- **Path:** `.local/worktrees/release-2.11.5-connection-fix/.local/artifacts/KickFlight-2.11.5-remote.apk`
- **SHA-256:** `ae7b4ed16f4544b18348ed5068f02c3a582ed7cbb3b64d46fe9811a704405f4f`
- **File Size:** 149,876,184 bytes
- **Signer Certificate SHA-256:** `81ca6334fd069edbc8756a49fc135c91141eba8265cb64d0510ed9b231b21537`
- **Package Name:** `jp.grenge.kickflight`
- **Version:** `2.11.5` (versionCode `60`)
- **Metadata Literals (`global-metadata.dat`):**
  - `colorful_api` [2294]: `http://159.54.134.159:18080`
  - `resource_api` [2304]: `http://159.54.134.159:18080`
  - `authority` [9244]: `159.54.134.159:18080/`
  - `photon_host` [2039]: `159.54.134.159`
  - `update_url` [9231]: `https://kick-flight-fenix.us.ci/`

## 2. Dedicated Emulation Environment Recovery
- **Host System:** Fedora 44 x86_64, SELinux Enforcing, NVIDIA GeForce RTX 4060 (driver 615.71.09).
- **Environment:** Dedicated test AVD `KickFlight_VPSTest` on port 5556 (`emulator-5556`).
  - Command: `env DISPLAY=:0 WAYLAND_DISPLAY= QT_QPA_PLATFORM=xcb XAUTHORITY=/run/user/1000/.mutter-Xwaylandauth.GMH7W3 emulator -avd KickFlight_VPSTest -port 5556 -gpu host -feature -Vulkan -no-snapshot-load -no-boot-anim -no-audio`
  - QEMU PID `342286` running stably in tmux session `kf-vpstest`; `sys.boot_completed=1` verified.
  - Personal AVD `KickFlight_API35` (port 5554) and local API/Photon remained completely untouched.

## 3. Runtime Verification & Observed VPS Traffic
- **Installation:** Candidate APK installed via `adb -s emulator-5556 install -r` preserving test octo cache.
- **Device SHA-256:** `ae7b4ed16f4544b18348ed5068f02c3a582ed7cbb3b64d46fe9811a704405f4f` confirmed on device filesystem.
- **Execution Progression:**
  1. Cold start under PID `3256` loaded Berberis ARM64 translator, Unity 2018.4.11f1 engine, and CriWare sound system.
  2. Scene transition: Loaded `TitleScene`.
  3. First-run Android immersive cling overlay dismissed at `(260, 206)`.
  4. TAP START executed and accepted at `(160, 328)`.
- **Live VPS Telemetry Audit (`159.54.134.159:18080`, container `deploy-api-1`):**
  - **`POST /boot/index`:** Received from `189.214.156.28` (`Kick-Flight/2.11.5`) at `2026-10-20T05:21:30.932Z`. Answered HTTP 200 OK (`accepted-boot-index-d2c`).
  - **`POST /auth/prepare`:** Received at `2026-10-20T05:21:53.444Z`. Answered HTTP 200 OK (90 bytes).
  - **`POST /auth/index`:** Received at `2026-10-20T05:21:54.795Z` and `2026-10-20T05:25:35.446Z` with `x-app-application-version: 2.11.5`.
    - Server response: `Maintenance: refused /auth/index with 503`.
    - Body: `{"error":{"code":"1000","title":"Maintenance","message":"Updatinng bot behavior 🤖\n\nMantainance will fniish by 5:00 AM UTC"}}`.
  - **In-Game Behavior:** Client displayed `TitleMaintenanceWindow` modal as mandated by server protocol.
- **Stages Not Reached:** Lobby, matchmaking, and battle were not reached due to active server maintenance.

## 4. Proven External Blocker & Verdict
- **Runtime Verdict:** `BLOCKED_EXTERNAL_HARD_MAINTENANCE`
- **Blocker:** VPS is locked in Hard Maintenance mode (`mode: hard`) set at `2026-10-20T03:53:54Z` by administrator.
- **Gate status:** Do not merge, publish, upload GitHub release assets, or raise the Android minimum until administrator lifts hard maintenance mode and full runtime PASS (startup, login, matchmaking, battle) is confirmed.
