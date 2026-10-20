# AI role controls: server and master evidence

Status: static server/master evidence only; native AI field consumers still require independent confirmation. No source, master, APK, or runtime files were modified for this investigation. The mandated `agy` calls for large masters/catalogs were blocked by the headless command-permission gate, so the data below comes from bounded JSON parsing and targeted source ranges.

## `KickerAiParameter`

- `config/masters_kicker_ai_parameter.json` rows 1–25 establish the exact schema: `id`, `kickerId`, `kickerCostumeId`, `attackMotivation`, `defenseMotivation`, `scoreMotivation`, `negativeCorrection`, `positiveCorrection`, `canLockon`, `healDamageRate`, `attackDashInterval`, `defenseDashInterval`, `scoreDashInterval`, `attackSkillInterval`, `healSkillInterval`, `buffSkillInterval`, `trapSkillInterval`, `warpSkillInterval`, `rank`, `minMeasureAccuracy`, `maxMeasureAccuracy`, `scoreToDeposit`, `changeAttributeInterval`.
- Bounded extraction of all 14 rows found unique `id`/`kickerId`/`kickerCostumeId`, but every other field is identical: motivations `60/40/70`; corrections `0/0`; `canLockon=true`; heal rate `0.5`; dash intervals `1/1/1`; skill intervals `3/4/5/5/5`; rank `13`; measure accuracy `0.9..1`; deposit threshold `10`; attribute interval `4`. Thus existing rows identify kickers, not distinct role profiles.
- `DemoSessionApi.cs:602-609` loads the three AI config files and serves them as encrypted `KickerAiParameter`, `KickerAiDisc`, and `KickerAiDiscDeck` masters.
- `BattleMatchmakingService.cs:1354-1364` documents the client lookup: `GetKickerAIParameterMaster` selects by row ID (`get_Item(_kickerAiParameterId)`) and by a row rank not exceeding player rank. Normal bot rank is `13`, and its parameter ID is currently its kicker ID. Distinct profile rows appear structurally possible if the server serves them and assigns their IDs; verify the native lookup accepts the proposed ID/rank arrangement before relying on it.
- Gym reserves IDs `100 + kickerId`: `DemoSessionApi.cs:865-891` clones rows there and zeroes the three motivations, disables lock-on, and sets dash/skill intervals to `9999`. `scripts/re/bot_special_skill_cave.py:11,34-41` documents the matching client patch: IDs `>=100` set `AIOption.Mannequin (63)`. Do not allocate role profiles in this range.
- The exact master field names are confirmed; their gameplay consumers and numeric semantics are not confirmed by server source. Field names alone do not prove carrier/flee/deposit, ally targeting, healing/buff targeting, or defense behavior.
- `DemoSessionApi.cs:270-290` serves `CustomBattleKickerAi` rows mapping each supported kicker to its same-number parameter ID and deck `1`; this is separate from matchmaking's bot roster assignment.

## Mode-aware server settings

- `ScrambleBotCompositionSelector.cs:178-184` maps battle rule IDs to Crystal Scramble, Flag Flight, or Rapid Ball.
- `ScrambleBotDeckCatalog.cs:38-74` accepts catalog fields `kickerId`, `deckId`, `variant`, `roleType`, `profile`, `discIds`, and `castTimes`; profile strings include carrier, hunter, objective attacker, healer, speed support, trap defender, damage support, and tank defender.
- `BattleMatchmakingService.cs:1292-1325` uses that mode-aware selector to choose a bot composition, deck ID, and four disc IDs. This is a server-side loadout/composition control. It does not select a different `KickerAiParameter` row or directly set client AI state. `castTimes` and profile labels therefore show server selection concepts, not proof that the corresponding strategic behavior executes.
- To express separate behavior profiles without changing APK code, a server change would need to provide additional parameter rows and choose their IDs per bot/mode; existing normal bot assembly currently hard-codes `profile.KickerId` (`BattleMatchmakingService.cs:1362`). Whether parameter-only tuning can produce the requested stateful roles remains unverified pending native consumer evidence.

## Published client identity

- The public landing links `/apk/KickFlight-2.11.3-remote.apk` (`deploy/site/index.html:132,171`). `config/resources/catalog.json:104-112` maps that path to `.local/KickFlight-2.11.3-remote.apk`, SHA-256 `da4f18cd68422c540e8fe6c202fe8453d264a325627947e1dc23f9a73d15e8cc`.
- Live read-only HEAD on 2026-10-19 returned HTTP 200, 149,813,883 bytes, and the same ETag/SHA. The old README/VPS guide download path points to 2.11.0 and is stale.
