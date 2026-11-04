# The domains otomo owns, and what this adapter still keeps

Updated 2026-11-04 at `staging` 8fca9fd. The 2026-11-03 version of this file recorded rank and social as
blocked on a cross-player read; otomo has since grown it, and every domain below translates onto otomo.
The full state, the owner's decisions and the traps are in the platform fork:
`tanukifurhire/kf-otomo` `docs/kf-handoff-2026-11-04.md`.

| Domain | otomo owns | What this adapter keeps |
| --- | --- | --- |
| Identity | auth accounts, tokens | the numeric id ↔ otomo account mapping (`players`) |
| Social | session follows, `rank/board?scope=following` | nothing (migration 6 dropped the three tables) |
| Rank | session points + the published `session.rank` ladder | unit translation only: +200 offset, `rank_<N>`, the ±50/−20 deltas (`RankTranslation`) |
| Economy | wallet, inventory, item attrs (disc levels), tasks | the client's DTO shapes |
| Capsules | economy entitlements, kind `capsule`, `max_open: 3` | slot rendering from the open rows |
| Loadout | session Loadout (`session.loadout`): `decks`, `costume_gears`, `active_deck`; the server-decided gear roll stays in session ext: `pending_gear` | nothing (migration 8 dropped `active_deck_number`) |
| Masters | 61 `kf.masters.*` tables + `session.rank` from the release channel | nothing; live's balance overrides are published there |
| Chat | otomo chat (global channel, retention, rate, NG words, stamp ownership) | the `/.ws` protocol and `chatRoomComment/write`; a seq poll relays new messages |
| Replay | patch recordings, `kicker:<id>` tags, `patch.curated` Featured | the client's encryption of the bytes; a sealed, stateless play URL |
| Missions | economy tasks | id translation only |
| Badges | economy facts + lifetime tracks + `honor:<id>` items (`display_slot`), session display slot `honor` | fact reports, the claim sweep, id translation (`docs/OTOMO_BADGES.md`; migration 9 dropped `player_badge_*`, `player_honors`) |

Still local by known gap: kicker/costume on another player's card (no ext field), and `state.UserName` for the onboarding gate,
`IsNameTaken`, and the caller's own ranking line.

## The suite

598 tests on feature/otomo-bot-ai. The 13 known failures, by name (the python3 bot-AI save test is deleted with its path): the iOS bundle pair,
the economy/onboarding fakes, and `HarnessTests.Download_master…`. Run comparisons
in fresh worktrees: the tests leave saved users under `bin/…/data/users`, which turns two onboarding tests
red on a re-run. `BattleMatchmakingTests.Each_join_moves_the_deadline…` is wall-clock and fails under load.
