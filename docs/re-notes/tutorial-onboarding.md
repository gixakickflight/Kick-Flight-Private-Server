# Tutorial ladder: what the client builds itself and what it reads from the server (2.11.x arm64)

Read from `libil2cpp.so` with `scripts/re/a64dis.py` / `xref.py` on 2026-10-29.

## Placeholder data

- `NetworkManager.StartupAfterDownload` **0x31B63D0**: while `TutorialUtil.IsTutorial()` (status != 207) it replaces
  the archive's battle ranks, disc deck, disc list and both gacha group lists with `TutorialUtil` placeholders
  (`GetUserDiscDeckListInfo` 0x1479864, `GetUserDiscListInfo` 0x14799A0, `GetUserGachaGroupListInfo` 0x1478E44,
  `GetUserKickerGachaGroupListInfo` 0x14793DC). The kicker list is the server's: `/tutorial/start` callback
  0x31BA0E4 sets `ArchiveData.UserKickerListInfo` from `userKickerList`.
- `TutorialUtil.DISC_ID_LIST` = **[3010003, 3010024, 3010070, 3010013]** (static array data at global-metadata
  0x8323D5, loaded by `TutorialUtil..cctor` 0x147A6D4).
- `GetUserDiscListInfo`: each disc at `CommonUtil.GetDefaultDiscLevel(rarity)` (0x31EFD50: rarity 1-3 -> rarity + 1,
  else 1); for disc 0 the level is +1 once status >= 203 (0xCB). `amount` = `DiscBuildupMaster.Find(rarity, level')
  .TotalDiscAmount` where `level'` is +1 for disc 0 once status >= 202 (0xCA). The Find result is **not
  null-checked** (NRE at 0x1479D24): every rarity needs DiscBuildup rows at its default level and the one above.

## Build-up lesson (203)

- The lesson taps deck slot 0 (`HomeSkillSelectView.<TutorialTapDiscAsync>` 0x159CE7C, `_deck[0]`), i.e. 3010003
  Bombster (rarity 1, level 2 -> 3).
- `SkillDescriptionWindow.SetInfo` 0x183A31C: button active = `_checkEnhanceCost(info) && info.EnableLevelUp`
  (0x183BD2C). No tutorial bypass.
  - `DiscDisplayPresenter.<Initialize>b__5_0` **0x162C268**: `GoodsUtil.GetDiscForceAmount() >= info.EnhanceCost`
    (Item row 3 from the archive, i.e. from `/startup/index`; nothing earlier in the tutorial repeats it).
  - `HomeDiscInfo.EnableLevelUp` 0x1733B08: `Level != MaxLevel && Amount >= MaxAmount`.
- With the served master the lesson's EnhanceCost is **400** (row rarity 1 / level 3). An account below that sees a
  greyed button under the tutorial pointer and cannot continue; no request is sent, so the server log simply stops
  after `/tutorial/capsuleOpen`.
- In the tutorial the button sends `/tutorial/discBuildup {discId}` (`<EnhanceExecuteCorutine>d__66` 0x1840760);
  the callback 0x183E898 does `UserDiscListInfo.UpdateData(userDisc)` and `UserItemListInfo.UpdateData(userItem)`
  (both dereferenced).

## Capsule step (202)

- `CapsuleSlotView.<OpenTutorialCapsuleAsync>b__47_0` 0x16B2E94 -> `new CapsuleOpenInfo(data)` 0x16ACAD8 (null
  lists tolerated) -> `OpenCapsule` 0x16B1FD4: per received disc, merges into the archive list and calls
  `GoodsShopProductListInfo.RefreshDiscExchangeProduct` (the archive ctor 0x31F63A8 pre-fills that object, so a
  tutorial that never called `/home/index` is safe).
- `CapsuleOpenDisplay.<OnEnableStartCoroutine>` 0x16A61DC: per `receivedUserDiscList` row, `DiscMaster[discId]`
  is dereferenced (skill id, rank); `SkillMaster` / `BattleRankMaster` results are stored as-is.

## Retries

- `NetworkManager.SendRequestAsync` (private overload, `cacheId` parameter) resends a failed request with the same
  `x-app-response-cache-id` (`AddRequestHeaderResponseCache`, via `AddDefaultRequestHeader` 0x31B5C0C). That id
  identifies a retry of one request; a new tap gets a new id.
