# Battle result DTO fields and server data sources (Phase 2a evidence)

Artifact: base.apk 2.11.0, sha256 `ffa620d2e2f905e729812606024e02741124951dce908bb2e0587f0a4e16e031`.
`global-metadata.dat` sha256 `8337973aa337aec9e122d198c1d0108e9e1ab9abc5207305e97a2854c0768692`
(Il2Cpp metadata v24, TypeDef stride 100 B; fieldStart @+44, field_count u16 @+80; sum(field_count)=65223=fields table/12, validated).
Method: read-only python parse of typeDefinitions -> fields -> string table. Field NAMES = high confidence.
Field TYPES = type-table indexes only; mapping t52985=string, t52927=int, t53226=bool, t94793=TeamType is INFERRED from usage (medium).
JSON key = C# field name for public fields (inferred, medium; the existing server already parses `userScoreList` etc. successfully).
Ghidra/REA procedure analysis failed (provider timeouts): no RVAs, no call sites, no serializer attributes verified.

## Request DTOs (Colorful.Networking)
- BattleEndRequestData: battleId, teamScoreList[RequestTeamScore], userScoreList[RequestUserScore]
- BattleEndResponseData: replayUploadFlag
- CustomBattleEndRequestData: customBattleId, teamScoreList, userScoreList
- BattleGoodRequestData: battleId, userIdList (list of target user ids; liker is the authenticated caller) [high names, medium semantics]
- BattleUploadRequestData: battleId, uploadFile
- RequestUserScore: userId, score, mvpFlag, breakawayFlag, badConnectionFlag, aiFlag
- RequestTeamScore: teamType, score, remainingDistance, lastRemainingDistance, upsetScoreDifference, latestGoalTimeSecond
- RequestCustomBattlePlayer: userId, teamType; RequestBattlePlayer: userId, battleEntryId, teamType
- BattleResultRequestData (39 fields): battleEntryId, teamScoreList, userScoreList, plus the CALLER's own stats:
  movingDistance, killCount, killedCount, assistCount, killAssistCount, totalAttackDamage, normalAttackDamage,
  discAttackDamage, kickerSkillAttackDamage, specialSkillAttackDamage, weaknessAttackDamage, totalReceivedDamage,
  hpRecoveryValue, justAvoidCount, attributeChangeCount, acquiredCrystalCount, releasedCrystalCount,
  kickerReleasedCrystalCount, guardianReleasedCrystalCount, droppedCrystalCount, depositCrystalCount,
  usedKickerSkillCount, usedSpecialSkillCount, usedDiscSkillList[RequestUsedDiscSkill{discId,count}],
  flagAcquiredCount, flagMovingDistance, enemyFlagDropCount, enemyFlagHaveTime, flagGoalCount,
  ballShootPoint, ballAcquiredCount, rapidBallAcquiredCount, ballDropCount, rapidBallDropCount,
  battlePlayerSummaryList[RequestBattlePlayerSummary], assistFlag
- RequestBattlePlayerSummary: targetUserId, damage, damageCount, killCount, receivedDamage, receivedDamageCount,
  killedCount, usedKickerSkillCount, usedSpecialSkillCount, usedDiscSkillCount, hpRecoveryValue
- CustomBattleResultRequestData: same as BattleResultRequestData but customBattleId instead of battleEntryId, no assistFlag
- BattleResultResponseData: userExp, userBattleRank, beforeUserBattleRank, playerLevelUpRewardList, receivedUserCapsuleList,
  receivedUserItemList, receivedUserPresentList, userMissionProgressList, userDailyRandomMissionTaskList, receivedRewardList,
  badConnectionReliefFlag, battleCount, battleWinCount, matchmakingTeamId, campaignList, userFestivalTeam, festivalPoint,
  lotteryFestivalPointId
- ResponseBattleReplayPlayer: userId, name, frameId, languageCode, rank, kickerCostumeId, mvpFlag

## Captured real bodies (main repo .local/run/server-api.log)
- /battle/end: 3 samples; /battle/result: 3 samples (all one user). Only byte size + SHA-256 logged (end 1216 B; result 3056-3088 B).
  No field names or values recorded; cannot assess mvp/score sanity. 0 samples for /battle/good, /battle/upload, /customBattle/*.
- Note: result bodies are ~3 KB, consistent with the 39-field stat set above (inferred, low-medium).
- Synthetic fixture only: tests/KickFlight.BootstrapApi.Tests/BattleReplayTests.cs:250-342.

## Server behaviour (worktree, src/KickFlight.BootstrapApi)
- /battle/good (DemoSessionApi.cs:1298-1302): stub, returns `{}`, reads nothing, stores nothing. No likes store anywhere.
  Client sends battleId + userIdList (liker -> targets). No server-side crediting exists; any crediting would rely on
  caller-supplied target list, validate caller and targets against the room roster.
- /battle/end -> RecordMatchEndAsync (:2557-2591): reads battleId/customBattleId, userScoreList{userId,aiFlag,mvpFlag},
  teamScoreList{teamType,score,remainingDistance,lastRemainingDistance}. No caller-in-roster check; last-write-wins in
  BattleReplayService.RecordBattleEnd; currency granted per call without dedupe (:2518-2519).
- /battle/result (:2422-2514): reads only battleEntryId; entry must belong to caller. Ranked rank update is always
  `won: true` (:2467); response battleWinCount hard-coded 0. Per-entry dedupe via ProcessRankedResultOnce / badge entry ids.
- Ops tables: battles.outcome = clean|partial|abandoned|never_started (telemetry health, NOT win/loss);
  battle_participants has no score/winner/stat columns. SessionState has VerifiedBattleCount only.
- /battle/disconnect|timeout|leaving (DemoSessionApi.Ops.cs:14-57): telemetry only.

## Replays (/battle/upload, docs/REPLAYS.md:104-132)
- Handler DemoSessionApi.cs:2707-2749; first upload per battle kept. Archive members: header, battle, result, frames.
- Parser (BattleReplayService.cs:702-844) reads header, battle (playerBattleInfos incl. userId, rank, kickerId, teamType, isAi),
  result (score[], minRemainingDistance[], lastFlagProgressRate[], mvpUserId). `frames` skipped: per-player kills/distance
  etc. are not parsed. Whether frames contain such data: NOT verified.

## Multi-reporter feasibility
- battleId is assigned per room (BattleMatchmakingService.cs:755-773), shared by humans in the room; each client posts its own
  /battle/end and /battle/result (entry id per player). Cross-player agreement on teamScoreList/userScoreList is possible in
  principle (each human sends the full lists), but current code keeps only last write. Logs cannot confirm >=2 humans
  reporting the same battleId (battleId absent from log lines; all 3 samples one user). Luxon sources not available
  (submodules/ empty): no evidence Luxon sends results.

## Stats with NO source on the server today
kills, deaths, assists, damage (all kinds), distance, crystals, flags, goals, ball stats, win/loss, likes received.
Client sends caller-own versions in /battle/result (unlogged, unparsed); likes arrive only via /battle/good (ignored).
Cross-check is possible only with another reporter or the replay frames (unverified).

## Unverified
Field types beyond inferred mapping; serializer renames; real values of captured bodies; call sites/RVAs; frames content;
Luxon behaviour; whether clients send /battle/result for every battle type.

## Phase 2b decisions (core badge tracking, JSON store side)
Trust labels: `server_verified` (server observed it itself) / `client_reported_bounded` (client number, validated and capped) / `none` (unsupported).
Every stat rule in `config/badge_rule_catalog.json` carries entry-level `trust`; the loader ignores a stat kind without `client_reported_bounded` and ignores unknown kinds (fail closed).

Catalog result (211 ids), recounted in phase 2e: 12 server_verified (5 login_days, 5 disc_upgrades, default, registration_complete), 119 client_reported_bounded (115 stat rules + the 4 rank badges 6010401/6010402/6010701/6011101), 80 unsupported/`none` (blocker recorded in `requirements.blocker`). The 4 rank badges moved from server_verified because rank now follows the client-reported team scores.

### Ranked progression (phase 2e)
Rank no longer assumes `won: true`. The outcome is the `BadgeBattleStatEvaluator` team outcome: own team from the server-held room roster, own `teamScoreList` score vs the other team's.

| Validated result | Rank effect |
| --- | --- |
| win (own > other) | `RankProgression.Apply(won: true)` (+50) |
| loss (own < other) | `Apply(won: false)` (-20, floored at rank 7 / 2900) |
| tie | no change (`RankProgression` has no draw; a tie is not a loss) |
| undetermined (no room/team, missing/malformed scores, caller not in userScoreList, entry/timing rejected) | no change, reason logged |
| caller `breakawayFlag`/`badConnectionFlag` | no change (no relief rule exists: `badConnectionReliefFlag` is a constant `false` in the response) |

A capped stat field voids the stat set but not the score outcome. A no-change attempt caches nothing, so a later decidable result for the same `battleEntryId` can still apply once; once applied the snapshot is cached and every retry replays it (idempotent). `battleCount`/`battleWinCount` stay the constants `1`/`0` (no server counters of the same meaning exist).
Solo = the entry was registered without a party (`/battle/entry` never carries one, even if matchmaking later teams the player with others); party = lobby members created via `CreateTeam`/`JoinTeam` (`PartyId` = team id). Wins count under `QueueWins["solo"]`/`["team"]` accordingly.

| Family | Count | Rule kind | Trust / reason |
| --- | --- | --- | --- |
| MVP 6010201-205 | 5 | mvp_count | client_bounded (<=1 mvpFlag per list) |
| Win streak per mode (Scramble/Flag/Rapid) | 9 | win_streak{battleRuleType} | client_bounded |
| Crystals collected 6010601-603 | 3 | stat_total crystals_collected | client_bounded, mode 1 only |
| Flag goals 6010901-903 | 3 | stat_total flag_goals | client_bounded, mode 2 only |
| Likes 6011401-403 | 3 | stat_total likes_received | roster-confirmed via /battle/good |
| Kills / deaths 6012901-905 | 5 | stat_total | client_bounded |
| Solo wins 6013001-003, team wins 6013101 | 4 | queue_wins | client_bounded, low confidence (solo/team = PartyId empty/set, inferred) |
| Guardian 6013201; make-drop 6013202-203 | 3 | stat_total | client_bounded; releasedCrystalCount meaning inferred |
| Flag drops ("defenses") 6013301-302 | 2 | stat_total flag_drops | client_bounded, mode 2 only |
| Kicker wins (14 kickers x5, minus 2 candidates) | 68 | kicker_wins{kickerId} | client_bounded; kicker = server-stored entry kicker |
| Distance 6010301-306 | 6 | stat_total moving_distance | client_bounded, all counted modes; metres (1 = 1 m), thresholds converted km->m; unit confidence MEDIUM (see below) |
| Crests B/beta 6011000-002, 6012000-002 | 6 | unsupported | rule unknown |
| Goals 6011301-302 | 2 | stat_total ball_goals | client_bounded, mode 3 only; ballShootPoint treated as goals (owner decision) |
| Ball drops 6013401-402 | 2 | stat_total ball_drops | client_bounded, mode 3 only; ballDropCount any direction (owner decision) |
| Pattern-guess thresholds | 7 | unsupported | 6011303, 6012603-604, 6013102-103, 6013303, 6013403 |
| 6012906 | 1 | unsupported | threshold 100000 vs 1000 conflict |
| TV 6013501 | 1 | unsupported | feature not modeled |
| Season rank/division | 56 | unsupported | intentionally out of scope: season ranking not run |
| Tournament crests | 9 | unsupported | intentionally out of scope: event-only (beta crests 6012000-002 likewise) |

Decisions
- Caller team: server-held. entry -> `AssignedBattleId` (set at room finalize) -> room `MatchingInfo.battlePlayerList` row with same battleEntryId/userId and kickerAiParameterId==0 -> `teamType`. Body teamType of the caller is never trusted.
- Win = own team score > other team score in `teamScoreList`; tie = not a win; missing side = undecided (Won null, streak untouched). breakaway/badConnection caller: Won null.
- Mode mapping (masters_battle_rule): battleRuleType 1 Crystal Scramble, 2 Flag Flight, 3 Rapid Ball, 100 Trial (not counted). Queue types regular/ranked/festival all count; custom battles (`/customBattle/result`, returns `{}` early) never count.
- Gating: crystal stats only mode 1, flag stats only mode 2. Room battleRuleId must equal entry battleRuleId.
- Caps (per match, any violation voids the whole stat set but participation still counts): kills 60, deaths 60, crystals acquired/released/guardian 500 each, flag goals 30, enemy flag drops 100, Rapid Ball goals (ballShootPoint) 100 and ball drops 200 (mode 3 only; not read in other modes), movingDistance 30000 m (floats are floored), <=8 listed players, <=1 mvp, caller present and not aiFlag, no negatives/non-integers; room age >= 90 s and entry age <= 3 h.
- Exactly once: existing ProcessedBadgeBattleEntryIds gate + bounded `BadgeStats.CreditedBattles` ledger (512). Retry returns stored `ProcessedBadgeBattleAwards`.
- Merge on stale save: per-key max for counters, union for ledgers, streak by monotonic `Battles` with Best = max.
- Likes: `/battle/good {battleId,userIdList}` credited only if liker and target are humans of the same finalized in-memory room aged 90 s..3 h; <=7 targets; ledger key `battleId|likerUserId` on the TARGET row (512). Response always `{}`.
- Response shape of /battle/result unchanged (only receivedUserHonorList filled). battleCount/battleWinCount/matchmakingTeamId stay hard-coded; ranked rank update still always `won:true` (known, out of scope).

Open questions: solo/team meaning; releasedCrystalCount vs "enemies drop crystals"; movingDistance unit confirmation (see below); Flag Flight tie-break; candidate-threshold badges; room roster lives in memory only (a restart between match and result drops credit); no cross-reporter verification.

PG phase must persist: `SessionState.BadgeStats` (Totals, WinsTotal, KickerWins, ModeWins, QueueWins, WinStreaks{Battles,Current,Best}, CreditedBattles, LikeLedger) inside the existing jsonb document, with the same max/union/sequence merge under a per-row lock; likes write to another player's row (needs transaction or row lock).

## movingDistance unit (phase 2d)
- Decision: treated as metres (1 = 1 m, 1000 = 1 km), per project owner. Thresholds in the catalog are converted km -> m (10 km = 10000).
- Evidence for: `config/masters_translation.json` id 450 `battleSummary.movingDistance` = "Distance covered: {0} m" (result screen label is metres; no km string, no m/km switch). No evidence against.
- Not verified: field type (int vs float), accumulation site and any x100 scaling. REA/Ghidra on libil2cpp.so (base.apk 2.11.0, SHA-256 c92a03cc... for the .so) timed out / bridge exited twice, so no RVA exists. The server accepts int or float and floors it.
- Cap: 30000 m per match = 150 m/s (well above flight speed) x 200 s (180 s battle + slack). A value above the cap voids the stat set for that match (same rule as the other caps).
- Revisit: if live data shows typical movingDistance per match far from ~1-5 thousand, re-derive the unit and rewrite targets/cap.

## movingDistance live check (phase 2f, 2026-11-01)
- Local worktree API + native luxon_server + AVD KickFlight_API35, client 2.11.1 (10.0.2.2:18080), 2 AI-fill battles (Crystal Scramble: 1 regular, 1 ranked).
- New permanent Information log "Battle result parsed: ..." (BattleResultLogSummary) prints raw value + JSON type per field. Observed: `movingDistance=244(Number)` and `movingDistance=98(Number)` (integer JSON numbers, ballShootPoint/ballDropCount/killCount 0, capReason none, both battles Loss, credited=True).
- The player was mostly idle (swipes only, 0 kills), so the small values are consistent with metres but do not prove it; typical-match 1-5 thousand not observed.
- The "Distance covered: {0} m" number was NOT located on any result screen reachable in 2 attempts (result list, rank screen, Lv Up bonus, own-row tap show no distance). Displayed-vs-logged comparison therefore not done.
- Verdict: unverified live, medium confidence. Field is an integer, no float scaling seen, nothing contradicts metres. No conversion/cap change. Revisit with an active flight run and a screen that shows the distance (profile/battle history untested).
- Test setup note: BootstrapApi needs `Harness__DirectClientHosts__0=10.0.2.2` (else 421 local-host-not-allowed); the emulator needs `-no-window -gpu swangle_indirect` on this host (swiftshader segfaults).
