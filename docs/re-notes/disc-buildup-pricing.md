# Disc build-up: how the client prices a level (2.11.x arm64)

Read from `libil2cpp.so` with `scripts/re/a64dis.py` on 2026-10-29. Field offsets of `DiscBuildupMasterData`:
`rarityType` 0x14, `level` 0x18, `necessaryDiscForceAmount` 0x1C, `totalDiscAmount` 0x20.

- `DiscParameterUtil.CalcBuildupParameter(discMaster, level, amount)` **0x1638624**: `FindAll` rows of the disc's
  rarity (`b__0` 0x1638AF4), then `Find` prev = `level - 1` (`b__1` 0x1638B40), current = `level` (`b__2`
  0x1638B80), next = `level + 1` (`b__3` 0x1638BBC). Returns `BuildupParameter {Amount, MaxAmount, MaxLevel,
  EnhanceCost}`:
  - `Amount` = `amount - current.total` when prev, current and next all exist, else `amount`.
  - `MaxAmount` = `next.total - current.total` when prev exists; `next.total` at the start level (no prev);
    `current.total` at the cap (no next); 0 without a current row.
  - `EnhanceCost` = `next.necessaryDiscForceAmount` (0 at the cap).
  - `MaxLevel` = highest `level` among the rarity's rows.
- `CalcAmount` **0x16388EC** / `CalcMaxAmount` **0x1638908**: the same arithmetic for the bar.

Consequence: `totalDiscAmount` is a **cumulative** copy count, and the step L -> L+1 is priced by row L+1. The server
serves `ResponseUserDisc.amount` as held + `total(level) - total(start)` (`Otomo/EconomyDiscLevels.cs`).

Error dialog: `NetworkManager.OpenErrorWindowOnFailed` **0x31B7F98** special-cases 1202, 1400/1401, 2002-2004
(localized name-rule text), 2100, 2204-2206, 2400-2405, 2601, 2901-2903. Any other code (e.g. 2502, used for a
refused build-up) shows the server's `error.title` / `error.message`.
