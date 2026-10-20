# Local APK Signing & Keystore Recommendation for Native Targeting Worker

## 1. Active Installed Package Signature
- **Installed Package:** `jp.grenge.kickflight`
- **Installed Certificate SHA-256:** `4B:07:93:DE:89:74:C2:EB:38:D1:98:41:9C:DA:2B:7A:26:93:FC:C6:0E:A0:09:FC:DA:B4:EE:8F:27:C8:DC:EB`
- **PackageSignatures Version:** `PackageSignatures{6f46f76 version:4, signatures:[eac4a353]}`
- **Source Artifact with Matching Signature:** `/home/gixarde3/.codex/worktrees/discs-balance-audit/Kick-Flight-Private-Server/.local/artifacts/KickFlight-2.11.1-damage-diag-v4.apk`

## 2. Canonical Matching Keystore
- **Canonical Keystore Path:** `/home/gixarde3/.codex/worktrees/discs-balance-audit/Kick-Flight-Private-Server/.local/kickflight-test-signing.jks`
- **Worktree Mirror Path:** `/home/gixarde3/Proyectos/KickFlight/Kick-Flight-Private-Server/.local/worktrees/ai-role-investigation/.local/kickflight-test-signing.jks`
- **Alias:** `kickflight-test`
- **Certificate SHA-256 Digest:** `4B:07:93:DE:89:74:C2:EB:38:D1:98:41:9C:DA:2B:7A:26:93:FC:C6:0E:A0:09:FC:DA:B4:EE:8F:27:C8:DC:EB`

## 3. Discovered Mismatched Keystore (Do Not Use)
- **Mismatched Keystore (auto-generated in clean worktree):** Previous `.local/kickflight-test-signing.jks`
- **Mismatched Certificate SHA-256 Digest:** `53:44:FE:59:F6:44:B6:48:32:38:CE:F4:49:E4:09:8B:89:9C:78:76:CE:6D:B5:30:12:A4:20:CE:F4:38:AE:6B`
- **Failure Symptom:** `INSTALL_FAILED_UPDATE_INCOMPATIBLE: Existing package jp.grenge.kickflight signatures do not match newer version; ignoring!`

## 4. Builder / Signing Tooling Recommendation
1. **Direct Build Integration:**
   Set `KEYSTORE=/home/gixarde3/Proyectos/KickFlight/Kick-Flight-Private-Server/.local/worktrees/ai-role-investigation/.local/kickflight-test-signing.jks` when executing `.local/build-fedora.sh`.
2. **Re-signing Candidate APKs:**
   ```bash
   apksigner sign \
     --ks /home/gixarde3/Proyectos/KickFlight/Kick-Flight-Private-Server/.local/worktrees/ai-role-investigation/.local/kickflight-test-signing.jks \
     --ks-key-alias kickflight-test \
     --v1-signing-enabled true \
     --v2-signing-enabled true \
     --v3-signing-enabled true \
     --out <output-apk> <input-apk>
   ```
3. **Verification Before Install:**
   ```bash
   apksigner verify --verbose --print-certs <output-apk>
   ```
   Ensure Signer #1 SHA-256 matches `4B:07:93:DE:89:74:C2:EB:38:D1:98:41:9C:DA:2B:7A:26:93:FC:C6:0E:A0:09:FC:DA:B4:EE:8F:27:C8:DC:EB` and `v1`, `v2`, `v3` schemes are `true`.
