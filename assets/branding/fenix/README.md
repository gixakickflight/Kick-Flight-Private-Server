# Fenix branding

App icon and website mark: a single yellow "F" (Barlow Condensed Black, glyph converted to path) with a dark offset shadow.
The wordmark and flame were retired; the in-game logo (title, loading, home) was removed in 2.11.807.

## Files
| File | Use |
| --- | --- |
| `fenix-icon.svg`, `fenix-icon-{1024,512,192,180,64,32,16}.png`, `favicon.ico` | Launcher icon (all densities, round, adaptive background), PWA/apple-touch icons, favicon |
| `fenix-f.svg`, `fenix-f-1024.png` | Transparent F mark: adaptive-icon foreground, notification silhouette source, website header mark |

Site copies: `deploy/site/assets/fenix-f.png` (header, ~130x240, size by height in CSS), `deploy/site/favicon.ico`, `deploy/site/fenix-icon-{180,192,512}.png`.

## Regenerating
`magick -background none -density 300 fenix-icon.svg -resize WxH out.png` (same for `fenix-f.svg`). The SVGs are self-contained paths.
