# The Kick-Flight loadout in otomo's session service

Decks and gear move out of the adapter's save document and into otomo's `session` service,
which stores one loadout document per player, validates it against a JSON Schema the game
publishes to Config, and revisions it with `If-Match`. This note records the document, how
the adapter's save maps onto it, what migrating the existing saves involves, and the one
thing that makes this awkward: the schema is built from masters that change.

Nothing here publishes anything. The files under `otomo/namespaces/kf.loadout/` are the
artefact; putting it live is an operator step (§6).

## 1. What lives where

| File | Holds |
|---|---|
| `otomo/namespaces/kf.loadout/namespace.json` | audience (`server`) and description for Config |
| `otomo/namespaces/kf.loadout/schema.json` | JSON Schema draft 2020-12 for **one player's loadout**, schema version 1 |
| `otomo/namespaces/kf.loadout/draft.json` | a sample document, which validates against that schema |
| `otomo/namespaces/kf.loadout/gen_schema.py` | regenerates both from the masters (§5) |

`session` does not read a namespace called `kf.loadout`. It reads `session.loadout`
(`services/session/internal/loadout/rules.go`, `Namespace = "session.loadout"`), whose
document is the *rules*: one schema per accepted version plus two optional knobs.

```json
{ "active_version": 1, "versions": { "1": <schema.json here> } }
```

`gen_schema.py --rules` prints exactly that document. So `kf.loadout` is where the game
authors and reviews the schema, with a draft that Config can validate against it, and
`session.loadout` is the wrapper `session` consumes. Both are published; neither is
hand-edited in the Config UI.

Two knobs, both left out, and why:

- **`lifetime_seconds`** — omitted, so a loadout never expires. A Kick-Flight loadout is a
  standing build a player keeps across sessions, not a per-match pick; expiring it would
  throw away decks for no gain.
- **`ready_policy`** — omitted, i.e. `free`. The adapter accepts a deck change whenever the
  client sends one, `ready` or not, and the launch uses whatever is saved at that moment.
  `lock` would be a new refusal the game's screens have no message for.

## 2. The document

```json
{
  "schema_version": 1,
  "revision": 4,
  "decks": {
    "1": [3010001, 3010002, 3010003, 3010004],
    "2": [3010005, 3010006, 3010007, 3010008],
    "3": [3010009, 3010010, 3010011, 3010012]
  },
  "active_deck": 1,
  "costume_gears": {
    "2010101": [1, 0, 0],
    "2010201": [0, 0, 0]
  },
  "pending_gear": null
}
```

| Member | Required | Default | Why |
|---|---|---|---|
| `schema_version` | **yes**, `const 1` | — | `session` refuses a document that does not name a whole-number version, and it picks the schema from this member. Structural, not a choice. |
| `revision` | no | absent | `session` owns it: `PutLoadout` writes the stored revision into the document with `jsonb_set` on every save, and `parseDocument` deletes the member before validating, so neither a client's value nor a missing one is an error. Declared here so a document read back from `GET` is fully described by this file. |
| `decks` | **yes** | — | Keyed by deck number, `"1"`, `"2"`, `"3"`: exactly the numbers `masters_initial_disc_deck.json` defines, all three present, each an array of exactly **4** disc ids (that master's `discId1..discId4`). Keying by number rather than an array is what makes "one deck per number" structural — an array of three could hold 1, 2, 2. It is also the adapter's current shape (`Dictionary<int, List<int>>`), so migration is a copy. |
| `active_deck` | **yes**, `1\|2\|3` | — | It decides which deck a match launches with. Defaulting it (to 1, as the adapter's field initializer does) would silently launch a deck the player did not pick; a document that does not say must be refused, not guessed. |
| `costume_gears` | **yes**, may be `{}` | `{}` in effect | Keyed by the `KickerCostume` row id (`2\|kicker\|costume\|variant`, e.g. `2010101`). Only costumes with something equipped need an entry; an absent key means three empty slots, which is why `{}` is a valid value for a new player. Each present value is exactly three slots. |
| `pending_gear` | no | absent = `null` | A gear rolled by `gear/create` and not yet equipped or discarded. Optional because it is transient: a save that changes decks must not fail for want of a null, and the adapter's `{KickerId: 0, GearId: 0}` already means "nothing pending", written here as `null`. When present, `kicker_id` (the kicker rolled for) and `gear_id` (a real gear id) are both required. |

**Empty slot representation.** `0` in a gear slot. That is the game's own sentinel, not a new
one: `SessionState.CostumeGears` documents "three gear slots (0 = empty)" and the client
reads the slot back as `gearId1..3`. No real gear has id 0 (`masters_gear.json` is 1..500), so
the sentinel cannot collide with an item. The schema's slot enum is `[0, ...gear ids]`.

**Ids are enumerated, not pattern-matched.** `$defs/discId` lists all 131 ids from
`masters_disc.json`, `$defs/gearIdOrEmpty` all 500 from `masters_gear.json`, and
`costume_gears`' `propertyNames` all 118 from `masters_kicker_costume.json`. A pattern would
have been smaller, but the disc ids run 3010001–3010138 with six gaps: `^30100[0-9]{2}$`
would accept 3010099, an id no row defines, which is exactly the case the schema exists to
refuse. `masters_disc_grow.json` deliberately does not appear: growth is a property of the
player's *owned* disc (level), not of the loadout, and stays in the adapter.

**Not used from `session`'s reference envelope.** The reference schema
(`services/session/internal/loadout/schema/loadout.v1.schema.json`) is "a starting point a
game can copy and tighten", and it suggests `identity`, `slots` and `stats`. This document
keeps only `schema_version` and `revision`, which are the two members `session` itself acts
on. `identity` would repeat the row's `player_id` and the selected kicker/costume, which are
not loadout choices here; `slots` would be a second name for `decks`; and there are no
free-form numeric stats in a Kick-Flight loadout (disc level is owned-disc state). Adding an
empty `stats` object to satisfy a suggestion would be noise in every player's row.

## 3. Mapping from the adapter's save document

`src/KickFlight.BootstrapApi/SessionState.cs` today:

| Save field | Type today | Loadout member | Note |
|---|---|---|---|
| `Decks` | `Dictionary<int, List<int>>`, seeded 1–5 | `decks` | Keys 1–3 carry over unchanged. 4 and 5 are dropped — see §4. |
| `ActiveDeckNumber` | `int`, default 1 | `active_deck` | Same value, now constrained to 1–3. |
| `CostumeGears` | `Dictionary<int, int[]>`, 0 = empty | `costume_gears` | Keyed by costume row id already; short arrays are an adapter bug (`GearSlots` pads them) and the schema refuses anything that is not three. |
| `PendingGear` | `{KickerId, GearId}`, `{0,0}` = nothing | `pending_gear` | `{0,0}` becomes `null`; otherwise the pair maps across. |

Stays in the adapter (not loadout state): `UserId`, `UserName`, `KickerId`,
`KickerCostumeId` (the *selected* kicker/costume is session/screen state, not an equip),
`Discs` (owned discs and their levels), `ItemJetCoins`, `ItemPaidJetCoins`,
`ItemDiscForce`, `ItemKickPoints`, `ItemAmounts`.

## 4. Migrating existing players

Per player, once, at cutover: read the save row, write a loadout document with
`schema_version: 1`, `decks` from `Decks[1..3]`, `active_deck` from `ActiveDeckNumber`,
`costume_gears` from `CostumeGears`, `pending_gear` from `PendingGear`, and send it with
`If-Match: 0` (a first save; the store then makes it revision 1). Then delete those four
fields from the save so nothing writes them twice.

What the migration has to decide, because the schema refuses what the adapter allowed:

- **Decks 4 and 5 are not in the master.** `SessionState.Decks` seeds five decks, but
  `masters_initial_disc_deck.json` defines three, and the loadout has room for three. They
  are dropped. If the client can send `number: 4` or `5`, that save now gets a `422` from
  the schema — the adapter change (a separate piece of work) should map the client's deck
  list onto 1–3 instead of storing whatever number arrives.
- **Deck length.** The adapter stores whatever list a client sends; the schema requires
  exactly four. A stored deck that is short or long cannot be migrated as-is and needs a
  decision per player (pad from `masters_initial_disc_deck.json`, or the deck's own first
  four), which is a data-repair job, not something to do silently in the loadout write.
- **Ids that are no longer in a master.** A save holding a disc or gear id that
  `masters_disc.json` / `masters_gear.json` no longer define would be refused forever: the
  player could not save *anything* until it is cleared. The migration must drop such slots
  (leaving `0` for gear, and repairing the deck) and log them — this is the reason the
  enums are worth their cost.
- **Players with nothing saved.** `404 loadout_not_found` from `GET` is normal before the
  first save, and the adapter should then synthesize the starter document from
  `masters_initial_disc_deck.json` — `draft.json` is exactly that starter.

## 5. The one hard thing: masters change, the schema is published

The ids in the schema are a snapshot of the masters. That buys exact rejection (§2), and it
costs a republish whenever a master moves. The rule that keeps this tractable:

- **Widening (an id added) can stay in version 1.** Republish the rules document; no stored
  document can already hold the new id, and every document that validated before still
  validates, so nothing needs migrating.
  `session` re-reads the rules from the live release before every save (5 s freshness), so
  the new enum is in force without a restart.
- **Tightening (an id retired or corrected) is a new version.** Editing version 1's enum
  would invalidate documents already stored under version 1 — every player holding the
  retired id would fail their next save. Instead add `"2"` to `versions`, set
  `active_version: 2`, and let v1 keep validating what it validated (a retired id remains
  legal in v1 forever). A player moves by reading v1 and writing v2; `player_loadout` is
  keyed by `(player_id, schema_version)`, so both rows coexist and nothing is rewritten in
  place.

Regenerating, in the game repo:

```sh
python3 otomo/namespaces/kf.loadout/gen_schema.py           # rewrite schema.json + draft.json
python3 otomo/namespaces/kf.loadout/gen_schema.py --check   # CI: fail when they are stale
python3 otomo/namespaces/kf.loadout/gen_schema.py --rules   # the session.loadout document to publish
```

The script hand-writes no ids — every list is read from `config/masters_*.json`, sorted and
de-duped — so the diff after a master change is exactly the ids that changed. The current
snapshot is 131 discs, 500 gears, 118 costumes and a 23.8 KB schema, well inside the
1 MB rules cap and the 64 KB document cap.

Publishing (an operator, not this repository): create the `session.loadout` namespace if it
does not exist (`POST /api/admin/config/namespaces`), `PUT` the `--rules` output to its
draft, `POST /api/admin/config/namespaces/session.loadout/versions` with a message, then cut
a release that includes it. `session` never talks to Config directly; it reads the channel's
live release through Patch's server manifest, which is why `namespace.json` says
`"audience": "server"`.

## 6. Not done here

- **The adapter's C# side.** Publishing to a loadout, migrating the save, and reading decks
  back for the client is a separate change in
  `src/KickFlight.BootstrapApi/`. This repository only gained the schema, its sample and
  this note.
- Nothing was published to a live Config, and `kf.kickers` is untouched. The namespace is
  not in `main` until someone merges it.

## 7. How the schema was checked

`verify/kf_loadout_test.go` is a Go test for otomo's `services/session/internal/loadout`
package (drop it there with the fixtures it names). It parses the published `session.loadout`
document through `ParseRules` and validates the draft with `Rules.Validate` — the code that
decides whether a player's save may be stored — then proves that an unknown disc id, an
unknown gear id, an unknown costume id, a fourth deck, a five-disc deck, a two-slot costume
and a missing `active_deck` are each refused, and that a document carrying `revision` (as
`GET` returns it) still validates.
