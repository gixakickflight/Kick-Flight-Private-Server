# AI roles: implementation and validation status

## Implemented behind default-OFF flags

- Server `KF_EXPERIMENTAL_AI_ROLES=1`: mode-aware bot roster parameter IDs15..53 and39 matching encrypted master rows (13profiles x3modes); humans/base IDs and gym100+kickerId preserved. Yuyan has objective specialization in Scramble/Rapid; Flag keeps its existing assignment.
- Fixed `ScrambleBotDeckCatalog` lookup when API content root is `src/KickFlight.BootstrapApi`: explicit file, direct config, bounded ancestor lookup to solution root, published output fallback. Existing missing-catalog warning/legacy fallback preserved. Live API now loads448 validated variants.
- ARM64 `KF_AI_ROLES=1`: framed final-target telemetry. `KF_AI_ROLE_TARGETING=1`: positive attack candidates only, Scramble hunter17 prefers players x4 / guardians x0.25; objective18/23 reverses those weights. Stock invalid/friendly/dead candidate exclusions stay in force. This does not route bots toward distant objectives.
- New ARM64 `KF_AI_ROLE_DEPOSIT=1`: Scramble carrier15 carrying>=1 crystal and damageRate>=float32(0.65) uses integer deposit threshold1. Stock evaluator already requires an allied deposit place. All other profiles/modes/humans/gym bypass. Native score4/selection logic remains stock.
- Targeting/deposit each require roles. Full `KF_DIAG=1` plus roles is rejected before writes because its caves overlap. Independent damage/result diagnostic maps were checked for overlap and remain supported.

## Exact native evidence

- Published APK2.11.3:149813883 bytes, SHA256 da4f18cd68422c540e8fe6c202fe8453d264a325627947e1dc23f9a73d15e8cc.
- Published SO SHA25604d48df3fe04c23a5f17958faa96dca8f1f83cb661c10176f02cabe6ee500e2d; both temporary artifacts reverified.
- Telemetry:0x1807474 ->0x159D410; scoring:0x18072D0 ->0x159D6E0.
- Deposit:0x1808D40 ->0x159D854, returns0x1808D44. Original instruction612240bd loads integer threshold into s1. Canonical damage-rate accessor0x13BE18C computes1-HP/maxHP and tail-clamps via0x23E7EB0. Accessor uses actual ObscuredInt decoding; no guessed raw HP reads.
- Deposit cave412bytes ends0x159D9F0, leaving84bytes before0x159DA44. Full x0..x30/q0..q31/NZCV preservation, 0x300 aligned stack frame, LR restored around both native calls. KFDIAG7650 means threshold lowered during candidate evaluation; it does not mean final target selected or crystals deposited.
- Entry-RET owner and existing production cave regions preserved. New deposit guard bytes match both verified published library and pristine base. Full standard patcher dry-run accepts245patches on pristine with PHOTON0; it rejects using the already patched published library as a generic rebuild base at unrelated baseline guards. No guard weakened.

## Tests and artifact

- `dotnet test tests/KickFlight.BootstrapApi.Tests/KickFlight.BootstrapApi.Tests.csproj --no-restore --filter 'FullyQualifiedName~ExperimentalAiRoleTests|FullyQualifiedName~ScrambleBotComposition|FullyQualifiedName~ModeAwareBot' --verbosity quiet`:31passed (16role and15composition), no failures/skips. Covers actual serialized roster, nested-root catalog, default/gym behavior, master encryption and IDs.
- `.venv/bin/python -m unittest discover -s tests -p 'test_ai_role*.py' -q`:28passed, no skips. Optional Unicorn2.1.4 executes generated deposit ARM64 with only HP/logger calls stubbed and clobbering caller state; verifies threshold boundary, healthy/zero/NaN/null/profile/mode cases, logger ABI and preservation. Wrong hook bytes reject without partial writes. This is ISA emulation, not Android gameplay validation.
- New signed local APK: `.local/artifacts/KickFlight-2.11.1-ai-role-deposit-signed-4b07.apk`; version56/2.11.1 LAN diagnostic built from pristine2.11.0, not the published2.11.3 installer. Build uses SOURCE_APK explicitly, SERVER_BASE_URL=http://10.0.2.2:18080, KF_PHOTON=0 and all3role flags. Artifact audit in ignored `.local/run/ai-role-deposit-artifact-audit.json` records SHA/size/certificate, exact245patch comparison and zero unexpected native bytes.
- First runtime capture used targeting APK SHA25657b2d07f70489aa4b3b23901f7ca7dffd39d5e05ef903e384093bd80f22f15e9. After that battle completed, deposit APK SHA2563e50dd23d20e4e6c1ef29afd8fd2a3f4c27d9ebb199a81c0ac2a46a054c35cbb was installed successfully with install-r; exact device hash verified, same cache/data preserved. Deposit runtime capture is separate.

## Runtime evidence and limits

- Live local API has experimental flag,448catalog variants and39experimental master rows verified. Gym false. No production actions.
- Existing AVD/cache retained. Result screen and later communication dialog did not advance on instantaneous taps;700ms stationary swipe allowed transition to fresh Scramble matching/battle. This does not establish the root cause of the earlier UI failure.
- Explicit quoted logcat filters are necessary: `KFDIAG:V Unity:V '*:S'`. Unquoted wildcard caused an earlier zsh capture command to exit before creating its file. Empty capture was not evidence of no native decisions.
- `.local/run/logcat-sol-role-battle.txt` contains real framed decisions for15/16/18/25, including attack/player, defense/player and score/guardian decisions. Latest frozen counts belong in the companion runtime-validation report.
- Marker7591 is emitted whenever an objective-profile final target is a guardian, regardless of purpose. The observed7591 was purposeScore: this demonstrates guardian selection for depositing, not attack weighting against an enemy guardian. No claim of successful enemy-guardian attack or hunt preference from that observation.

## Still unverified or unimplemented

- Actual deposit completion and evasive fleeing behavior. Native low-HP threshold branch/event7650 HAS now executed on Android; see runtime-validation report.
- Tank guardian anchoring, Owlbert spatial trap protection, assassins prioritizing enemy collectors, Ruriha ally healing/following, Grenhawk range/ulti/speed support and Sid escort/buff recipient selection.
- Native role-specific routing/targeting in Flag Flight and Rapid Ball. Server profile rows exist; stock helper behavior and custom-mode rules are not proven by those rows. No additional flag-capture rule assumed.
- Motivation/interval values are experimental heuristics; defenseMotivation has not been established as defending the guardian. Existing cooldown/threshold fields alone do not prove ally targeting or coordinated roles.
- Readiness503 remains the missing historical2.11.2installer locally; host API/gym works. Do not fabricate that artifact or change catalog to hide it.

## Repository and operational boundary

Research worktree/branch: `.local/worktrees/ai-role-investigation`, `research/ai-role-patching`, based on latest checked origin/main33d38a7. Preserve uncommitted notes, runtime identities/user files and submodules. Nothing staged/committed/pushed/deployed. Runtime data and private logs are excluded from intended PR scope. Changes destined for production must go through a PR.

## Final Android deposit update

Installed signed APK SHA2563e50dd23d20e4e6c1ef29afd8fd2a3f4c27d9ebb199a81c0ac2a46a054c35cbb,149876126bytes. Real standalone7650 observed after native threshold write; frozen trace counts/SHA/event timestamps in [runtime validation](ai-role-runtime-validation.md). Actual completed deposit, enemy-guardian attack preference and remaining roles still not established. No production actions.
