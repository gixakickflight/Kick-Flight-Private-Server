# Mode-aware bot composition

For supported team battle rules, matchmaking assigns human parties to balanced sides and fills open slots with bots whose registered decks fit the mode. The policy is selected from the battle-rule IDs in the served battle-rule masters:

| Composition policy | Battle-rule IDs | Match objective used for roster scoring |
| --- | --- | --- |
| Crystal Scramble | 1, 5, 6, 7 | Existing role and archetype coverage |
| FlagFlight | 2, 8 | Favor one mobile carrier, a flag defender, and hunters or attackers that pressure enemy carriers |
| RapidBall | 3, 4 | Favor carriers to collect and score after the enemy guardian falls, defenders to guard the team's guardian, and hunters or attackers to pressure the opposing guardian |

These objectives shape human team partition scores and the bot profile/deck scores. FlagFlight can prefer carrier, hunter, and trap/tank-defender profiles. RapidBall can prefer carrier, hunter, objective-attacker, and tank-defender profiles; guardian pressure considers hunters, mobile-pressure kickers, and attack-role kickers. The selector chooses among existing kicker AI parameter rows and registered AI-disc deck rows. It does not add match mechanics, change objective scores or flags, or change client AI behavior. In particular, the selection policy does not establish that an AI can carry a flag, collect or score a ball, guard a guardian, or coordinate its attacks.

Human team assignment keeps each already admitted party together and first minimizes the difference in human counts between sides, then scores legal partitions with the active mode policy. Four solo humans split 2/2 and eight split 4/4; a three-person party and a solo remain together and land 3/1. Queue admission still selects the largest compatible set of whole parties that fits 4v4: for 3+3+2, the two three-person parties start together while the two-person party remains queued. Seeded choices are repeatable, and battle timing and provisional waiting rosters keep their existing behavior.

Human account IDs, kicker IDs, costume IDs, and four equipped disc IDs stay unchanged. Human AI parameter and deck IDs remain zero. Unknown battle-rule IDs retain the legacy roster and deck path. Gym matches keep mannequin AI parameters, and the explicit local test-deck override remains unchanged.

`config/scramble_bot_disc_decks.json` registers 448 eligible kicker/profile deck variants. Each record links a kicker and profile to an existing `masters_kicker_ai_disc_deck` row, its four resolved disc IDs, the kicker's role type, and cast times by disc category. Each kicker/profile pair has four variants. Generate the catalog and its AI deck/disc master rows with:

```sh
python scripts/generate_scramble_bot_disc_decks.py --write
```

The catalog generator validates disc links and effect/category metadata against the served Disc, Skill, and sub-skill masters. Its profile rules rank registered decks by role weights, mode needs, and each kicker's action timings. The timing source is `CAST_CATEGORIES` and `CAST_TIMES` in `scripts/build-action-asset-bundles.py`; these describe action timing by disc category. Roster tests resolve selected AI decks through their four AI-disc rows and check that runtime disc IDs match the catalog. This verifies that selected deck rows and roster fields stay aligned.

The server assigns each bot its kicker's existing AI parameter row ID and selected AI-disc deck ID; Gym uses its separate mannequin parameter IDs. The selector scores configured kicker roles, archetypes, deck profiles, and available timing data. It does not model player skill, latency, or live match state. Any claim that client AI executes these roles or coordinates with teammates would require separate runtime evidence.
