# Rank: the adapter is a client of otomo's session rank service

A player's battle points and league badge come from otomo's `session` service once `Otomo:Rank` is on:

| Client call | Session route | Credential |
|---|---|---|
| `IOtomoRank.ReadAllAsync` | `GET /api/player/session/rank` | player token |
| `IOtomoRank.ReadOneAsync` | `GET /api/player/session/rank/{rule_type}` | player token |

Both are player-plane routes under the prefix the gateway already forwards, and the player is the token's subject:
nothing in the path or the body names a player, so the adapter cannot read anyone else's points and has no route
that would let it try. The token is the one `OtomoIdentity` already caches per device
(`GetSessionForDeviceIdAsync`), exactly as the economy client gets it.

The contract is otomo's, not this repo's (`services/session/internal/api/rank.go` on `staging`):

- `GET /rank` answers `{"ranks":[{"rule_type","points","tier","updated_at"}]}`, one entry per rule type the
  `session.rank` policy names, plus any rule type the player still has points in that the policy has since dropped.
- `GET /rank/{rule_type}` answers one entry, or `404 unknown_rule_type` when the published ladder does not name that
  rule type.
- A player with no award is **`200` with `points: 0`, the lowest tier and `updated_at: null`** — "not played yet" is
  an answer, not an error, and not a 404.

`Otomo/OtomoRank.cs` is the client, `Otomo/IOtomoRank.cs` its interface and wire types, `Otomo/OtomoRankOptions.cs`
the configuration. `RankSource` is the one place the two sources meet: `IRankSource.UsesOtomo` is the flag, and every
payload builder asks it rather than either source directly.

## The switch

`Otomo:Rank:Enabled` (`Otomo__Rank__Enabled`), **off by default**. Off, not one line of the otomo path runs and every
payload reads `player_ranks` through `IPlayerStore` exactly as before. On, the caller's own standing comes from otomo
and `player_ranks` is neither read nor written on the request path.

`Otomo:Rank:BaseUrl` is optional: empty means `Otomo:BaseUrl`, the gateway identity already uses, so enabling this is
one variable. `Timeout` (3 s) bounds one attempt and `RetryDelay` (100 ms) the single retry.

Transport rules, the same as the economy and matchmaking clients and tested in `OtomoRankTests`:

- **One retry, and only for a failure that says nothing about the request** — a transport error, a timeout or a 5xx.
- **A 4xx is an answer and is never repeated.** It is `OtomoRankFailure.Refused` with otomo's own code
  (`invalid_token`, `unknown_rule_type`, …).
- Two failed attempts are `OtomoRankFailure.Unavailable`: the service is down, the player was not refused.

## Rule types and the shape the client sees

The client's `userBattleRankList` is three rows keyed by integers 1-3 (Crystal Scramble, Flag Flight, Rapid Ball).
otomo names a closed set of rule types and a deployment that has not published `session.rank` has exactly one,
`ranked`. `Otomo:Rank:RuleTypes` maps the three onto it by default, so all three rows show the same standing until a
ladder that separates them is published; remap there when it is.

A rule type with no mapping, or one the ladder does not name, reads as **zero points in the lowest tier** — never
from the store. One source answers, and it is otomo's.

The payload shape is unchanged — the same three fields, the same types:

```json
{"battleRuleType":1,"battlePoint":2900,"rank":7}
```

`battlePoint` is a translation, not otomo's number verbatim. otomo counts from zero; the client's badge set cannot
draw a rank below the first renderable one, so the adapter has always served rank S at 2900 points. `RankSource`
shows `2900 + otomo points` (clamped to the top of the client's ladder) and derives `rank` from that total with the
same `RankProgression` table the client already draws. A player with no awards therefore reads exactly what a new
player reads today. otomo's `tier` string is not sent: the client has no field for a tier name, only the integer
badge.

## What the flag does not change

- **The crown leaderboard list and other players' cards.** otomo's player plane answers for the token's subject only;
  there is no route that orders players or reads someone else's points. The list rows and the caller's `number` in
  `/ranking/*` stay what `player_ranks` last said. With the table dropped (below) every row reads the starting
  standing, ordered by player id: the adapter says "I no longer know" rather than failing the screen.
- **Rank awards from a match result.** With the flag on, `/battle/result` reads the standing and writes nothing.
  Points are awarded by the matchmaker on session's internal listener (`POST /internal/session/rank/awards`, keyed by
  match id, idempotent), which is the authority otomo already has. A second writer here would race it and could pay a
  match twice, so the result screen shows the standing before that award lands.
- **Failure of a read.** Rank is display state, not value: an unreachable or refusing otomo logs and serves the
  starting standing rather than failing the home screen. The economy fails closed because a made-up balance costs a
  player currency; a made-up rank does not.

## `player_ranks` when the flag is on

The table is left in place and **nothing writes it**. That is deliberate: it is the only copy of every player's
old points, and otomo starts everyone at zero, so it is the only thing a rollback to `Otomo:Rank__Enabled=false`
could restore rank from.

There is no startup migration that drops it, and `Otomo:Rank:Enabled` never deletes anything. Dropping it is an
operator step, taken once the rollback is no longer wanted:

```sql
-- after Otomo:Rank has been on, verified, and a rollback is no longer planned
drop table if exists player_ranks;
```

Take it with the API stopped, or restart the API afterwards: the store reads the table's existence at construction
(`to_regclass('player_ranks')`) and, without it, answers the starting standing for reads, ignores writes with one
error in the log, and serves the leaderboard in its id-ordered fallback shape.

A table nobody writes is acceptable only with that sentence written down: **with `Otomo:Rank` on, `player_ranks` is
written by nobody, read only by the leaderboard list, and kept solely so a rollback can restore rank; once a rollback
is no longer wanted it is safe to drop, and the adapter then holds no rank storage at all.**

## Rolling it out

1. Publish `session.rank` if the deployment wants tiers or more than one rule type.
2. Set `Otomo__Rank__Enabled=true` (and `Otomo__Rank__BaseUrl` if the rank routes are not on `Otomo__BaseUrl`).
3. Confirm the home rank rows and `/ranking/*` against a player otomo has awarded points to.
4. Leave `player_ranks` alone. Drop it only per the section above.
