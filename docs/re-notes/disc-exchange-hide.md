# Discs tab: hiding the Disc Fragment exchange cell (2.11.5, arm64)

The block above the "Gacha de discos" banner ("Owned: 0", fragment icon, `100` cost button, "No disc fragments")
is not data driven, so the server cannot remove it. It is the extra cell that `Colorful.ShopDiscScroller`
(TypeDefIndex 14402, the Discs tab's `IEnhancedScrollerDelegate`) adds outside the tutorial:

- `GetNumberOfCells` (0x14B3B40) returns `_data.Count + (IsTutorial ? 0 : 1)`.
- `GetCellView` (0x14B394C): when not in the tutorial and `dataIndex == _data.Count - 1` it calls
  `ShopDiscCellView.SetDiscExchangeData` (0x14A9D84); otherwise `SetData(_data[min(dataIndex, Count - 1)])`.
- `GetCellHeight` (0x14B36A4) has the same branch (exchange height from `CostDiscFragmentProductCount > 3`).

Patch (RVA == file offset, `scripts/patch-hide-disc-exchange.py`): force the tutorial branch in all three.

| RVA | Method | Original | Patched |
| --- | --- | --- | --- |
| `0x14B3BA0` | `GetNumberOfCells` | `14 00 00 52` `eor w20, w0, #1` | `f4 03 1f 2a` `mov w20, wzr` |
| `0x14B3A18` | `GetCellView` | `00 04 00 37` `tbnz w0, #0, 0x14B3A98` | `20 00 00 14` `b 0x14B3A98` |
| `0x14B370C` | `GetCellHeight` | `c0 05 00 37` `tbnz w0, #0, 0x14B37C4` | `2e 00 00 14` `b 0x14B37C4` |

Notes:
- `JumpTo` (0x14B344C) bottom-aligns the last group via `GetCellHeight(Count - 1)`; with the patch that is the
  group's own height, which is the tutorial behaviour.
- `ShopDiscCellView.SetData` deactivates `_exchangeParent` (0xE8), so recycled cells never show the block.
- `Initialize`, `OnInitializeDisplay` (still loads the exchange banner sprite), Kickers/Exchange tabs are untouched.
- Same bytes in pristine base.apk 2.11.0 and the 2.11.5 builds. armeabi-v7a is not patched.

Build: `SOURCE_APK=<2.11.5 apk> OUTPUT_APK=.local/KickFlight-2.11.5-nofrag.apk scripts/build-nofrag-apk.sh`
(community key by default; `KEYSTORE`/`KEY_ALIAS` to match another installed signer).
