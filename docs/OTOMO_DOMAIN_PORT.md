# The six domains otomo is supposed to own: what this adapter can delete today

Written 2026-11-03 against `staging` (@ `e5dc314`) and otomo `origin/staging` (@ `2b167fb`), plus
the live `otomo-kf` instance's own databases. It records, per domain, what otomo actually answers,
what the adapter would delete to stop owning it, and whether that deletion can be made *complete*
today. A domain that can only be half-removed stays where it is: a translator that answers two of a
domain's four questions from otomo and the other two from a local table is worse than the adapter it
replaced, because the two sources drift silently.

Read this before starting the port. Each "blocked" row names the exact thing otomo does not answer,
so the fix is either an otomo route or a published document - not another week of adapter work.

## What was measured, and how

| Fact | Where it came from |
| --- | --- |
| Routes, bodies and error codes | otomo `services/*/internal/api` at `2b167fb` |
| Which policy documents the instance serves | `config.release.server_manifest` on `otomo-kf` (latest: release 21, channel `staging`) |
| Which tables exist, and whether they are written | `session`, `economy`, `patch` databases on `otomo-kf` |
| What the adapter's suite does today | `dotnet test` in a container on the NAS, 2026-11-03 |

Two measurements matter to every row below:

* The instance publishes **`economy.*` and `kf.masters.*` and nothing else**. There is no
  `session.rank` and no `ext.session.player` in the release manifest.
* Session therefore runs on the compiled default: `rule_types: ["ranked"]`, one tier
  (`bronze`, 0 points), and every rule type a caller names outside that set is refused with
  `400 unknown_rule_type`.

## The domains

### 1. Rank - BLOCKED (needs a published ladder, and a cross-player read that does not exist)

| | |
| --- | --- |
| otomo answers | `GET /api/player/session/rank` and `/rank/{rule_type}` (player token, subject is the caller) -> `{ranks:[{rule_type, points, tier, updated_at}]}`; `POST /internal/session/rank/awards` (game-server key) `{player_id, rule_type, delta, key}` -> `{player_id, rule_type, points, tier, applied}` |
| Adapter would delete | `RankProgression.cs` (142 lines: `Apply`, `RankFor`, `FloorBattlePoint`, `TotalBattlePointByRank`), `IPlayerStore.LoadRank`/`SaveRank` in both stores (`PostgresPlayerStore` 738-760, 1011-1012, 1046; `JsonPlayerStore` 313-316, 529), `player_ranks` (migration 3, dropped by a new migration 6), the save's rank reads at `DemoSessionApi.cs` 96-100 |
| Blocked by | (a) the instance's `session.rank` is `["ranked"]` with one tier, while the client needs three independent standings - `masters_battle_rule.json` maps ranked rule 7 -> battleRuleType 1, rule 8 -> 2, rule 4 -> 3 - so two of the three would be refused; (b) the social ranking screen lists *other* players' points, and there is no cross-player rank route: `GET /api/staff/session/players/{id}` answers `{player_id, display_name, discriminator, ext}` and session has no leaderboard at all (`grep -r leaderboard services/` is empty) |
| To unblock | publish `session.rank` with `rule_types: ["rule_1","rule_2","rule_3"]` and seventeen tiers whose keys are the client's rank numbers, then either an otomo leaderboard route or a decision that the ranking screen shows follows without points |

The per-battle delta (`+50` a win, `-20` a loss) is the one piece that *stays* in the adapter: otomo's
award takes a delta, so the translator still decides what a battle is worth. That is a constant, not
the ladder - `Apply`'s clamp, floor and rank arithmetic all disappear.

### 2. Disc levels and capsules - HALF READY

| | |
| --- | --- |
| otomo answers | per-possession attrs `{level, counter, available_at}` on every inventory row (`services/economy/internal/attrs`); `POST /internal/economy/items/attrs` sets the keys a patch names; `POST /internal/economy/items/advance` moves `level` by `by` up to `max` ("the build-up button without a price: the game has already charged") |
| Adapter would delete | `SessionState.DiscLevels` and `SessionState.DiscBuildupRequests` writes, `StoredDiscLevel`'s read of the save (`DemoSessionApi.cs` 1766), the level write at 1718 |
| Ready | **disc levels**: `disc:<id>` gains `attrs.level`; the consume stays the charge (same key), the advance is the fact it bought; `EconomyItem` grows an `Attrs` member so a read sees it |
| Blocked | **capsule slots**: one `available_at` per *item*, while a slot is `(slotNumber, capsuleId, discCount, unlockAt, source)` across up to three slots of the same capsule type. The state is not expressible on the item row, and inventing a per-slot key would be a second schema economy does not validate |
| Not a table | `DiscLevels` lives inside the `players` document, not a column, so there is no `drop table` for it. A save's level survives as a read-only mirror until the disc's next level-up writes it through; the document key can be dropped after that backfill, not before |

### 3. Missions - ALREADY A TRANSLATOR (nothing left to delete)

`DemoSessionApi.Missions.cs` is 166 lines of id translation over `GET /api/player/economy/tasks` and
`POST /api/player/economy/tasks/claim`; progress is `POST /internal/economy/progress`. There is no
mission table in `PostgresPlayerStore`, no mission counter in `SessionState`, and no local claim
decision: `EconomyPayloads.BuildMissionProgressList` renders economy's steps. The only local state is
`_missionMastersLoaded` - whether the client booted with the mission masters, which is presentation -
and it is in memory, never stored.

### 4. Replay - BLOCKED (upload and fetch are ready; the two channel tabs have no source)

| | |
| --- | --- |
| otomo answers | patch: producer upload, `GET /patch/v1/recordings` ("which recordings was this player in"), `GET /patch/v1/recordings/{id}/blob`; blobs addressed by content hash; `patch.recording` exists on the instance and is empty (recordings are enabled) |
| Adapter would delete | `BattleReplayService.cs` (893 lines): blob and metadata directories, the in-memory index, retention, the featured rotation |
| Blocked by | the client's replay screen is not "my recordings": it is `/battleReplay/channel` (one row per kicker, 20 newest per kicker) and a type-3 "Featured" row computed from the rotation's matches by human-player count. Patch lists by *player*, has no per-kicker or global listing, and no notion of a rotation, so two of the three tabs would answer nothing |
| To unblock | a patch listing by channel (or a decision to serve both tabs empty), plus the featured policy moving to a published document |

### 5. Social - BLOCKED (follows are ready; the ranking reads are not)

| | |
| --- | --- |
| otomo answers | `POST /api/player/session/follows` (by display name + discriminator), `DELETE /follows/{player_id}`, `GET /follows/followers`, `/follows/following`, `/follows/count` -> `{player_id, display_name, discriminator, since}`; friends and blocks on the same plane |
| Adapter would delete | `player_follows`, `real_friend_tokens`, `player_real_friends` (migration 2), `IPlayerStore`'s follow/friend members, `DemoSessionApi.SocialRanking.cs`'s follow reads |
| Blocked by | the same missing cross-player rank read as domain 1: the ranking list (`IPlayerStore.ListRanks`) is the whole player base ordered by battle points, with `battlePoint`, `rank` and a one-based `position` per row. Follows replace *who* is listed; otomo cannot say what any of them scored |
| To unblock | as domain 1, plus a decision on whether the ranking screen keeps points at all |

### 6. Loadout (decks, gear, active deck) - DONE, through `ext`

The loadout is the `ext` of the player's session card (`ext.session.player`): `decks`, `costume_gears`, `active_deck` and
`pending_gear`. Reads are `GET /api/player/session/me` (the caller) and `GET /internal/session/players/{id}` (another
player); every write is `POST /internal/session/players/ext` with `merge: true`, keyed so a retry writes once
(`DemoSessionApi.Loadout.cs`). `SessionState.Decks`, `CostumeGears`, `PendingGear` and `ActiveDeckNumber`, and the
`players.active_deck_number` column (migration 8), are gone. The kicker and costume a player picked stay in the game row:
the schema declares no field for them.

## The suite as it stands

540 tests, 515 passed, 14 failed, 11 skipped, measured twice on a pristine `git archive staging`
extract (once with the whole suite, once filtered to the two tests below), so the set is
deterministic and not a re-run artefact.

The 13 expected failures are the iOS pair (`content/*.bundle` is not in the tree), the bot-AI
concurrency test (`python3`), and ten that assert payloads this branch now sources from economy
(`EconomyEndToEndTests` 3, `ShopGachaTests` 2, `OnboardingEndToEndTests` 5).

The fourteenth is `HarnessTests.Download_master_returns_kicker_costume_and_matches_served_master`:
`Assert.Equal(1, userKickerList.GetArrayLength())`, actual 14. It is **a stale assertion, not a
regression**: `EconomyPayloads.OwnedCostumes` ends by serving the standard costume of all fourteen
kickers unconditionally ("Every kicker is unlocked for every account... identity, not value"), which
is why `EconomyEndToEndTests.A_player_who_owns_nothing_is_served_zeroes_rather_than_a_demo_stock`
fails with the same 14 rows. The two tests still describe the payload from before that block existed.
