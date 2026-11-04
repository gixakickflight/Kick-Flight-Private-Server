# Badges on otomo

The adapter evaluates no badge rule. A badge is **earned** when a lifetime track in otomo's economy pays a reward
class, **owned** when the player's economy inventory holds `honor:<id>`, and **shown** through a session display slot.
The rules are tenant data in the Kick-Flight fork (`tanukifurhire/kf-otomo`, branch `kf/badges`); the adapter reports
what happened and translates the answers into the client's DTOs. Nothing here is game-shaped platform code: every
mechanism used is generic in otomo (economy facts and tracks, `design/30-mission-facts.md`; the `streak` measure and
`auto_claim` tracks, #270; items with `display_slot`, #241; session display slots and the `display` map of the internal
profile read, #270).

## The pieces

| Piece | Where | What it is |
|---|---|---|
| Facts | `economy.tasks` `facts` | typed, closed field schemas for what the adapter reports |
| Tracks | `economy.tasks` `tracks` | 37 lifetime tracks, `badge.*`, all `auto_claim: true`: `on`/`when`/`measure` (`count`, `sum`, `max` or `streak`) over a fact, steps pay a class |
| Classes | `economy.rules` `classes` | `badge.<id>` grants the item `honor:<id>`: one class per badge |
| Items | `economy.rules` `items` | `honor:<id>`, not stackable, `display_slot: honor`: 130 of them |
| Slot | `session.rules` `display.slots` | `honor`; the same document carries the `names` rules the adapter used to hold itself |
| Generator | `kf/tools/gen_badges.py` | reads this repo's `config/badge_rule_catalog.json` and `config/masters_honor.json` and writes the generated members of `economy.tasks` and `economy.rules`, deterministically (`--check` fails on drift) |

The generated members are recognisable by prefix (tracks and classes `badge.`, items `honor:`); the rest of those
documents is untouched. The default honor `6010000` is nobody's award: it is always shown as owned, and the empty
display slot means it.

## What the adapter does

* **Reports facts** (`POST /internal/economy/facts`, keyed by the thing that happened, so a retry counts once):

  | Moment | Fact | Key |
  |---|---|---|
  | a verified battle result, once per entry and player | `battle.result` | `badge-battle:<entry id>` |
  | `/startup/index` | `login.day` | `badge-login:<yyyy-MM-dd UTC>` (a count of days, not startups) |
  | a disc level-up, only when economy's `items/advance` answered it and the step is one level | `item.upgraded` | `badge-upgrade:<advance key>` |
  | `/battle/good`, for the player who received it | `like.received` | `badge-like:<battle id>:<liker id>` |
  | `/tutorial/end` | `registration.completed` | `badge-registration` |

  `battle.result` carries `rule_type`, `kicker_id`, `queue` (`solo`/`team`), `credited`, `credit_reason`, `outcome`
  (`win|loss|tie|undetermined`), `won` (a credited win by an unflagged caller), `flagged`, `mvp`, `rank_rule`/`rank_after`
  (the ranked rule type and the rank after it; `0`/`-1` when unranked), and the bounded stats `BadgeBattleStatEvaluator`
  computes (`kills`, `deaths`, `crystals_collected`, `guardian_released_crystals`, `crystals_released`, `flag_goals`,
  `flag_drops`, `ball_goals`, `ball_drops`, `moving_distance`). A battle the evaluator voided is still reported, with
  `credited: false`, zero stats and the reason. The evaluator (the anti-cheat bounds on the client's numbers) stays in the
  adapter: it is translation of a client body into fact fields, not rule evaluation.
* **Reads the badges the report paid.** The tenant's badge tracks are `auto_claim`: a report that crosses a step pays its
  class inside the report's transaction, once, and the answer lists it in `auto_claims` (`result: claimed` with the items it
  granted, or `refused` with a code, in which case the step stays claimable and the adapter logs it and does nothing else).
  The adapter reads the `honor:*` items out of the claimed entries for `receivedUserHonorList` / `newFlag`. A replayed
  report answers its stored result, so a retried `/battle/result` shows the same badges and a second startup the same day
  is not news. There is no claim sweep, no read of the tasks and no player-plane claim.
* **The win streak** is a `streak` measure on `battle.result` (`badge.win_streak.<rule type>`): `continue` is a credited
  win of that rule type, `reset` a credited, unflagged loss or tie of it, and a flagged or undecidable result is neither, so
  the run is unchanged. Economy keeps the run and its peak atomically and holds the steps to the peak; the adapter sends the
  fact and nothing else.
* **Shows**: `userHonorList` is the default plus the `honor:*` items of the inventory. The equipped badge is
  `GET /api/player/session/display`; the client's equip is `PUT /display/honor {item_id: honor:<id>}`, equipping the
  default is `DELETE /display/honor`. **Another player's** badge (a chat line, a ranking row, a profile card, a follow list)
  is `display.honor` of `GET /internal/session/players/{id}`, read for the rows on the screen and kept for 30 seconds. Session checks the slot, the item's `display_slot` and ownership
  (`400 slot_unknown`, `404 item_unknown`, `422 item_not_displayable`, `403 item_not_owned`); a refusal is the game's error
  contract, as an unowned equip always was.
* **Asks session about names.** What a name may be is the tenant's `session.rules` `names` (1 to 10 characters, no control
  character, no zero-width joiner, nothing beyond the BMP, no cooldown): the adapter sends the name to `PATCH /me` first and
  maps a refusal to the client's name-rule error (2005). Session being down is not a verdict, so the name is kept and the
  rename is retried at the next one. The one thing the adapter still decides is that two players do not share a bare name.
* **Keeps nothing**: migration 9 drops `player_badge_ledger`, `player_badge_counters`, `player_badge_streaks` and
  `player_honors`; the save no longer has an honor, a counter, a login day or a processed-battle ledger. No player data
  migrates.

## Coverage: 130 of 130 executable rules ported

The catalog has 211 entries. 130 had an executable rule in `BadgeAwardRules`; all 130 are expressed by the tenant's
documents. The other 81 never awarded anything and are not rules: the default honor and 80 entries of kind `unsupported`
(no known award trigger), grouped by the catalog's own `blocker`:

| Not a rule | Entries |
|---|---|
| `season_rank_not_modeled` | 56 |
| `tournament_award_not_modeled` | 9 |
| `threshold_is_pattern_guess` | 7 |
| `crest_rule_unknown` | 6 |
| `conflicting_threshold_100000_vs_1000` | 1 |
| `tv_feature_not_modeled` | 1 |
| default honor (`default`) | 1 |

The adapter logs once at startup how many catalog rules the tenant documents express and which, if any, are unported
(none today), pointing here.

### Mapping by rule kind

| Catalog kind | Rules | Fact | Track (`measure`, `when`) | Steps |
|---|---|---|---|---|
| `kicker_wins` | 68 | `battle.result` | `badge.kicker_wins.<kickerId>` (14): `count`, `credited ∧ won ∧ kicker_id = K` | the kicker's thresholds, 3 to 5 rungs |
| `stat_total` | 29 | `battle.result` | `badge.stat.<stat>` (10): `sum(<stat>)`, `credited` | one rung per badge |
| `stat_total` (`likes_received`) | 3 | `like.received` | `badge.stat.likes_received`: `count` | 10 / 100 / 1000 |
| `win_streak` | 9 | counter `badge.streak.<rule type>` | `badge.win_streak.<rule type>` (3): gauge counter, lifetime, held to the peak | 3 / 5 / 10 |
| `login_days` | 5 | `login.day` | `badge.login_days`: `count` (the day is the key) | 30 / 100 / 200 / 300 / 365 |
| `mvp_count` | 5 | `battle.result` | `badge.mvp`: `count`, `credited ∧ mvp` | 5 / 10 / 50 / 100 / 500 |
| `disc_upgrades` | 5 | `item.upgraded` | `badge.disc_upgrades`: `count` | 30 / 50 / 100 / 300 / 500 |
| `rank` | 4 | `battle.result` | `badge.rank.<rule type>` (3): `max(rank_after)`, `rank_rule = N` | 7 and 13 / 13 / 13 |
| `queue_wins` | 4 | `battle.result` | `badge.queue_wins.<solo|team>` (2): `count`, `credited ∧ won ∧ queue = q` | 100 / 1000 / 5000, 100 |
| `registration_complete` | 1 | `registration.completed` | `badge.registration`: `count` | 1 |
| `battle_count`, `wins_total`, `mode_wins` | 0 | `battle.result` | (generator supports them; the catalog has none) | |

Kinds the generator mirrors from `BadgeAwardRules.Parse`: stat kinds required `trust = client_reported_bounded`, a
`kickerId > 0`, a `battleRuleType` in 1..3 and so on; an entry that failed those was never executable and is not counted.

### Gaps: none, and the three that were are closed in otomo (#270)

Every executable rule is expressible, and the three differences from the old behaviour that the first cut named are gone:
another player's equipped badge is `display.honor` on the internal profile read; the win streak is a `streak` measure
(atomic, no read-modify-write); a badge is paid by the report (`auto_claim`), not by an adapter-driven claim. What remains
to know:

1. **A refused payment is not retried.** If a ceiling, the item catalogue or a stack limit refuses a step, the report still
   counts, the step stays claimable by the player and the answer says so. The adapter logs it; the player has no claim UI for
   badges, so such a badge stays unpaid until economy's side is fixed and an operator pays it.
2. **Evaluation cost.** Every `battle.result` is evaluated against the 30 tracks that listen to it plus the 3 streaks (33), in
   one transaction. That is a lot of rows for one battle, not an unsupported rule.
3. **Mission screen.** `GET /tasks` also lists the `badge.*` tracks. The adapter skips them in `BuildMissionProgressList`
   (they are not missions) and the mission badge counts only served missions.
4. **No retroactive award.** No player data migrates, and the old counters are not copied.
5. **`login_days` no longer stops counting at the largest target** (the old code did).

## Operating it

1. Publish the fork's `kf/badges` documents (`economy.rules`, `economy.tasks`, `session.rules`) to the instance. The
   `economy.tasks` schema in the fork now covers facts, `on`/`when`/`measure` and `counter_mode`.
2. Session needs `SESSION_ECONOMY_KEY_PATH` (display ownership reads economy's catalogue); the adapter needs
   `Otomo:Economy:ProgressEnabled` and a game-server key for the facts and the gauge (without them nothing is reported and
   badges already owned still show), and `Otomo:Rank:Enabled` for the display slot.
3. Regenerate after a catalog change: `python3 kf/tools/gen_badges.py --catalog <this repo>/config/badge_rule_catalog.json
   --honors <this repo>/config/masters_honor.json --docs kf/tenant/namespaces`, then validate the documents against
   economy's `rules.Load` and session's `rules.Parse` before publishing.
