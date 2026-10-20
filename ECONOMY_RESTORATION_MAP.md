# Economy restoration map: inventory, missions, gacha, festivals

Starting points for restoring four client features, taken from the 2.11.x client binary.
Static analysis only: no traffic was captured and no method body was decompiled.

## Provenance

- **Binary:** `KickFlight-2.11.3-remote.apk` as hosted on kick-flight-fenix.us.ci (SHA-256
  `da4f18cd68422c540e8fe6c202fe8453d264a325627947e1dc23f9a73d15e8cc`). Its `libil2cpp.so` carries the
  private server's patches. Metadata, types and every unpatched address are identical to 2.11.0. Before
  trusting an address, check it is not inside a cave region listed in `scripts/re/README.md`.
- **Tools:** `scripts/re/meta_fields.py` for type and field names. A new enum reader that takes
  constants from the metadata's field default-value table (pair 7 and the pair 8 blob). Il2CppDumper
  6.7.46 for `dump.cs`, `script.json` and field types.
- **Checks:** the dump's RVAs match `docs/SOCIAL_RANKING_RESTORATION.md` (`GetFollowList`
  `0x14F91E4`, `SetFollowerNotificationCount` `0x14F8D28`). The enum reader's `GoodsType` values (202,
  303, 401, 402) match what the server already sends.
- **What this does not tell you:** method behaviour. The RVAs below say where to disassemble
  (`scripts/re/a64dis.py`), not what the code decides. Data values (which missions existed, festival
  themes, banner rates) were server-side and are not in the client; they must be authored.

## 0. Shared foundation (do this first)

Everything below grants into the same lists, so one inventory model serves all four features.

**`GoodsType`** (the `goodsType` on every reward, cost and present):

| Value | Name | Value | Name |
|---|---|---|---|
| 101 | JetCoinFree | 402 | DiscGachaTicket |
| 102 | JetCoinPaid | 501 | Capsule |
| 103 | JetCoinFreeOrPaid (cost only) | 601 | Honor (title or icon) |
| 201 | KickerCostume | 602 | Premium |
| 202 | GearMaterial | 603 | Frame (banner) |
| 301 | Disc | 604 | SeasonPass |
| 302 | DiscForce | 605 | Stamp |
| 303 | DiscFragment | 701 | KickPoint |
| 401 | KickerGachaTicket | -1 | Invalid |

**Where the client reads inventory:**

- `StartupResponseData` (`/startup/index`): `userKickerList`, `userDiscList`, `userHonorList`,
  `userItemList`, `userGear`, `userStampList`, `userMissionProgressList`,
  `userDailyRandomMissionTaskList`, `userPremium`, `userSeasonPass`, `userFestivalTeam`, plus the rest of
  the startup fields.
- `HomeResponseData` (`/home/index`): `userPlayer`, `userCapsuleList`, `discGachaGroupList`,
  `kickerGachaGroupList`, `userMissionProgressList`, `goodsShopProductList`,
  `userMissionTaskStatusNotification`, `presentNotificationCount`, and the festival notifications
  (`festivalMatchTeamSelectionNotificationFlag`, `festivalMatchResultNotification`,
  `appFestivalMatchResult`).

**Element shapes** (types from `dump.cs`):

| Type | Fields |
|---|---|
| `ResponseUserPlayer` | `userId:string displayUserId:long name exp:int honorId:int userFrameList:ResponseUserFrame[] lastLoginDatetime lastNameUpdateDatetime kickerId kickerCostumeId discDeckNumber officialFlag:bool` |
| `ResponseUserItem` | `itemId:int amount:int` (currencies and tickets are items, keyed by the `Item` master's `goodsType`) |
| `ResponseUserDisc` | `discId:int amount:int level:int registerDatetime:string` |
| `ResponseUserKicker` | `kickerId kickerCostumeId userKickerCostumeList[]`; each costume has `kickerCostumeId gearId1 gearId2 gearId3` |
| `ResponseUserHonor` | `honorId:int registerDatetime:string` |
| `ResponseUserFrame` | `battleRuleType:BattleRuleType frameId:int` (one frame per rule type) |
| `ResponseUserCapsule` | `slotNumber:int capsuleId:int completeDatetime:string openTimeSecond:int` |
| `ResponseReceivedReward` | `goodsType goodsId amount originalReceivedReward{goodsType goodsId amount}`: the original is what was granted before a conversion, such as a duplicate turned into fragments |
| `ResponseUserPresent` | `userPresentId:string goodsType goodsId amount title message sentDatetime receiptExpirationDatetime` |

**Level and experience.**

- `ResponseUserPlayer.exp` is cumulative. `PlayerLevelExp` (served) maps `level → totalExp`.
- `PlayerLevelUpReward` (not served) is `{id, level, goodsType, goodsId, amount}`.
- `BattleResultResponseData` returns `userExp`, `playerLevelUpRewardList`, `receivedUserCapsuleList`,
  `receivedUserItemList`, `userMissionProgressList` and `festivalPoint`: one battle result advances
  every system at once.

**Capsules.**

- A slot holds a capsule until `completeDatetime`, then `/capsule/open {slotNumber}` opens it.
  `/capsule/immediateOpen {slotNumber}` skips the wait.
- `Capsule` (served) has `lotteryId openTime lotteryDiscRarityGroupId lotteryDiscDropAmountGroupId`.
- `LotteryBattleDropCapsule` (not served) is `{battleRuleType rank battleResultType dropRatio
  capsuleId}`: which capsule a battle drops by rank and result.
- `CapsuleEffectType`: Normal 1, Orange 2, Rainbow 3.

**Initial state.** `InitialGoods {goodsType goodsId amount}` and `InitialParameter {name exp kickerId
discDeckNumber honorId}` (neither served) define a new account. Use them for the starting grant
instead of today's demo balances.

**The client's admin API** (`debug/*`) still has request constructors that name every mutation the
original server supported, with parameters. For example, `DebugAddPresentRequest(GoodsType,
goodsId, amount, presentMessageId, receiptExpirationFlag, oneFlag)` at `0x3222A24`. They're useful as a
checklist; don't expose them on the live server.

## 1. Inventory

| Item | Today | Start |
|---|---|---|
| Discs | Every disc level 10, 99 copies | Grant from gacha, capsules and rewards; `/disc/buildup` spends Disc Force and copies |
| Kickers and skins | No ownership list | `userKickerList` from owned costumes: owning a costume owns its kicker (`GoodsType.KickerCostume`, 201) |
| Gear | Implemented roll | Ownership per costume slot already in `CostumeGears` |
| Currencies, tickets | Demo stock, spending never refused | `userItemList`; refuse on insufficient balance |
| Level, exp | Not tracked | `exp` on the player, raised from `/battle/result` |
| Honors, frames, stamps | Not tracked | Lists in startup; granted by missions, festivals and level-up |
| Capsules | `/capsule/*` return empty drops | Slots, timers, `LotteryBattleDropCapsule` |
| `item/exchange` | No handler | Called by `HomeUIManager.ItemExchangeCoroutine` (response handler `0x15A451C`), empty request, returns `userItemList userPresentList`. Likely converts expired items (`ItemExpiration` master, not served); confirm by disassembling the caller |

## 2. Missions

**Endpoints** (request constructor RVA, client response handler RVA):

| Route | Request | Response | Client handler |
|---|---|---|---|
| `/mission/index` | none (`0x320F424`) | `userMissionProgressList userDailyRandomMissionTaskList` | `BattleDisplayPresenter.OpenMissionListWindowAsync` `0x1446D0C` |
| `/mission/read` | `missionTaskList:RequestMissionTask[] {missionTaskId loopCount}` (`0x320F124`) | `MissionReadResponseData` | `MissionListWindow.ReadMissionAsync` `0x1725A00` |
| `/mission/receipt` | `missionTaskId loopCount` (`0x320F2D0`) | `receivedUserCapsuleList receivedUserDiscList receivedUserHonorList receivedUserItemList receivedUserStampList userPresentList userMissionProgressList receivedRewardList` | `MissionListWindow.CompleteMissionAsync` `0x17242A8` |
| `/mission/change` | `missionTaskId` (`0x320EEE0`): rerolls a daily random mission | `userDailyRandomMissionTask` | `MissionListWindow.ChangeDailyRandomMissionAsync` `0x172406C` |
| `/mission/tweet` | | | Completes `MissionType.Tweet` |

**Masters** (none served):

| Master | Fields |
|---|---|
| `Mission` | `missionType missionTermType missionTargetType randomGroupId title sortOrder missionTermId missionConditionId displayFlag presentFlag` |
| `MissionTask` | `missionId value displayFlag`: the thresholds, one mission has several tasks |
| `MissionReward` | `missionTaskId goodsType goodsId amount` |
| `MissionTerm` | `startDatetime endDatetime entryStartDatetime entryEndDatetime availableTermDay receiptExpirationDatetime displayFlag`: the window that makes a mission daily, weekly or an event |
| `MissionCondition` | `battleRuleId battleRuleType battleEntryType roleType kickerId premiumId seasonPassId missionTargetGroupId tweetText`: filters on which battles count |

**Enums:**

- **`MissionTermType`:** None 1 (lifetime), Daily 2, Limited 3 (a dated term: weekly and events).
- **`MissionTaskStatus`:** InProgress 1, Completion 2, ReceivedReward 3.
- **`MissionTargetType`:** User 1, Follow 2, Guild 3, All 4.
- **`MissionType`:**

| Value | Name | Value | Name |
|---|---|---|---|
| 1 | LoginBonus | 15 | BattleFlagGoal |
| 2 | KickerCollection | 16 | DiscLevel |
| 3 | DiscCollection | 17 | BattleKill |
| 4 | BattlePlay | 18 | BattleKilled |
| 5 | BattleWin | 19 | BattleGood |
| 6 | BattleMvp | 20 | BattleStraightWin |
| 7 | RankUp | 21 | PremiumLoginBonus |
| 8 | IdLink | 22 | BattleReleasedCrystal |
| 9 | Tweet | 23 | BattleEnemyFlagDrop |
| 10 | DiscGachaDraw | 24 | BattleBallShoot |
| 11 | DiscBuildup | 25 | BattleBallDrop |
| 12 | KickerTrainingComplete | 26 | DailyMissionCompletionMission |
| 13 | BattleMovingDistance | 27 | BattleFestivalPoint |
| 14 | BattleAcquiredCrystal | | |

**Progress model.**

- **Progress per mission:** `ResponseUserMissionProgress {missionId loopCount value
  userMissionTaskStatusList}`. `value` is the counter; each task flips to Completion when `value ≥
  MissionTask.value`.
- **Repeats:** `loopCount` lets a mission repeat (lifetime chains), and receipt is per task and loop.
- **Daily random missions:** `ResponseUserDailyRandomMissionTask {missionTaskId number changeCount
  userMissionProgress}`. They're picked from a `randomGroupId`; `changeCount` limits rerolls.
- **Counter inputs:** most counters come from `BattleResultRequestData`, which reports `killCount`,
  `movingDistance`, `acquiredCrystalCount`, `flagGoalCount`, `ballShootPoint`, `enemyFlagDropCount` and
  more per battle. These are reported by the client, so they're forgeable, which is the same trust
  level as ranked results today.

**Icons, banners, Jet Coins** are rewards: `MissionReward.goodsType` = Honor 601, Frame 603 or
JetCoinFree 101. `Honor` (not served) is `{honorType name title sortOrder}`, with `HonorType` Other 0,
Battle 1, Kicker 2. `Frame` (served) is `{name battleRuleId}`.

## 3. Gacha

**Endpoints:**

| Route | Request | Response | Client handler |
|---|---|---|---|
| `/gacha/read` | `gachaGroupIdList:int[]` (`0x320DD2C`): marks banners seen | empty | `ShopDisplayPresenter.SendGachaRead` `0x14B8044` |
| `/gacha/discInformation` | `gachaGroupId` | `informationText noticeText discGachaGroupInformationList dropDiscForceList lotteryDropDiscForceList` | `ShopDiscCellView` `0x14AF0F8`, `DiscGachaDetailInfo` `0x1634B10` |
| `/gacha/kickerInformation` | `gachaGroupId` | `informationText noticeText boxFlag sideBySideFlag pickupFlag lotteryDropKickerList` | `ShopKickerCellView` `0x1410970`, `KickerGachaDetailInfo` `0x1533954` |
| `/gacha/discDraw` | `gachaId` | `dropDiscList receivedUserDiscList receivedUserItemList userItemList gachaDropCountRewardList modelId effect… capsuleEffectType drawableCount drawableDatetime completedDisplayFlag userPresentList userMissionProgressList … receivedRewardList premiumFlag` | `ShopDiscCellView.ExecuteCoroutine` `0x14B06DC`; parsed by `GachaDiscDrawInfo` `0x1567608` |
| `/gacha/kickerDraw` | `gachaId` | `dropKickerCostumeList receivedUserKickerList userItemList effect… userPresentList userMissionProgressList … receivedRewardList gachaBoxStatus` | `ShopKickerCellView.GachaKickerDrawAsync` `0x1411738`; parsed by `GachaKickerDrawInfo` `0x156C264` |

The banners themselves arrive in `/home/index` as `discGachaGroupList` and `kickerGachaGroupList`, each
a `ResponseGachaGroup`:

```
gachaGroupId name gachaType bannerImageFilename startDatetime endDatetime
boxFlag stepFlag newFlag completedDisplayFlag remainingTimeDisplayFlag
gachaList: ResponseGacha[] { id dropAmount drawableCount drawableDatetime costGoodsType costGoodsId
                             costAmount gachaBoxStatus{acquiredAmount totalAmount} stepNumber effect… }
gachaDropCountRewardList: { nextDropCount goodsType goodsId amount }
```

What this tells you:

- **"Skins and kickers" is one gacha.** Kicker draws drop costumes (`ResponseDropKickerCostume
  {kickerId kickerCostumeId}`, `ResponseLotteryDropKicker {kickerCostumeId pickupFlag newFlag
  dropRate}`). The first costume of a kicker unlocks the kicker.
- **Event gachas are banner rows with dates:** `gachaType` (Normal 0, Free 1, Limited 2),
  `startDatetime` and `endDatetime`, and `remainingTimeDisplayFlag` for the countdown.
- **The client supports three special mechanics:**
  - box gacha (`boxFlag`, with acquired and total counts);
  - step-up gacha (`stepFlag`, `stepNumber`);
  - per-player draw limits (`drawableCount`, `drawableDatetime`).
- **The server sends the displayed rates** (`dropRate` on rarity, disc and kicker rows). Compute them
  from the weights you roll.
- **A web page is needed:** `WebviewInformationGacha` is a webview, so the server has to host that
  page.
- **Masters:** all lottery masters are served. Not served: `LotteryDropDiscEffect`,
  `LotteryDiscForce`, `LotteryDiscForceParameter` (Disc Force drops) and `TutorialDiscGacha`.
- **Today:** the draw ignores every weight (`DemoSessionApi.Gacha.cs:303`).

## 4. Festivals (events)

**A festival is a battle rule.** `BattleRuleMasterData` has `festivalName`, `matchType`
(`MatchType`: Regular 1, Ranker 2, Season 3, **Festival 4**), and its own `startDatetime` and
`endDatetime`. Today one placeholder exists: rule 6, "Kick-Flight Festival", dated 2019 to 2020. Each
past festival (Summer, Halloween, Winter) would be its own rule with its own dates.

| Master | Fields | Served |
|---|---|---|
| `Festival` | `battleRuleId theme` | yes (inline C#) |
| `FestivalTeam` | `battleRuleId name color` | yes (inline C#) |
| `FestivalReward` | `battleRuleId festivalResultType goodsType goodsId amount` | no |
| `LotteryFestivalPointParameter` | `lotteryFestivalPointId name coefficient` | no |

`FestivalResultType`: Win 1, Lose 2, Draw 3.

**Endpoints** (none handled today):

| Route | Request | Response | Client handler |
|---|---|---|---|
| `/festival/select` | `festivalTeamId` (`0x320C924`) | `userFestivalTeam {battleRuleId festivalTeamId festivalPoint:int}` | `FestivalTeamSelectWindow.ExecuteCoroutine` `0x17B4620` |
| `/festival/follow` | `battleRuleId` (`0x320C5CC`) | `festivalPlayerList festivalPlayer appFestivalMatchResult`; a player row has `userId name honorId kickerId kickerCostumeId languageCode followStatus rank festivalTeamId festivalPoint` | `BattleRuleFestivalView.SetData` `0x1894C3C` |
| `/festival/result` | `battleRuleId` (`0x320C778`) | `battleRuleId userFestivalTeam festivalTeamResultList festivalRewardList`; a team result has `festivalTeamId festivalPoint:float festivalPointRate:float userCountRate:float festivalResultType` | |
| `/battleSummary/festival` | none (`0x321EE54`) | `festivalMatchResultList`: the history | `BattleSummaryWindow` `0x15B6BE8`, `FestivalSummaryScroller.Initialize` `0x17B3CB8` |

**Flow:**

1. The home screen flags team selection (`festivalMatchTeamSelectionNotificationFlag`).
2. The player picks a team.
3. Each battle on the festival rule returns `festivalPoint` and `lotteryFestivalPointId` from
   `/battle/result`; `LotteryFestivalPointParameter.coefficient` scales the points.
4. After `endDatetime`, the home screen shows `festivalMatchResultNotification` and the result
   window. Rewards come from `FestivalReward` by result type.
5. `MissionType.BattleFestivalPoint` (27) lets missions track festival points.

**Missing art.** The resource catalog only has `banner_gacha_000` and `banner_gacha_101`. There are
no festival, season or event banners, so events need new bundles built with Unity 2018.4 or reused
existing art.

## 5. Suggested order

1. **Inventory and the reward pipeline:** the `GoodsType` dispatch, `received*List` building,
   `InitialGoods` for new accounts, refusing spends. Everything else depends on it.
2. **Experience, level-up rewards and battle capsule drops** from `/battle/result`. This is the first
   visible progression and feeds mission counters.
3. **Gacha with real weights**, rates computed from them, banners with dates. Event gachas are then
   only data.
4. **Missions:** masters authored, counters from battle results, receipt granting through step 1.
5. **Festivals:** team select, points, result calculation after the end date, history. This needs
   step 4 for its missions and new art for its banners.

## 6. Next disassembly targets

The RVAs above are where the client consumes each response. Read these first:

- `GachaDiscDrawInfo..ctor` `0x1567608` and `GachaKickerDrawInfo..ctor` `0x156C264`: which response
  fields the result screen actually uses.
- `MissionListWindow` around `0x17242A8`–`0x1725A00`, and the `MissionProgressManager` / `MissionUtil`
  methods: how the client decides a task is complete and when it asks for receipt.
- `FestivalTeamSelectWindow` and `BattleRuleFestivalView`: what the client shows before and after the
  end date.
- `HomeUIManager.ItemExchangeCoroutine`: what triggers `/item/exchange`.
