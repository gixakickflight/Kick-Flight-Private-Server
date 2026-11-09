# Fenix branding

Fenix wordmark and app icon (replaces all Kick Flight / Grenge branding).

## Design
- Wordmark "Fenıx" set in **Barlow Condensed Black** (SIL Open Font License), glyphs converted to paths (no font dependency).
- Flat yellow `#FFDB02`, letters stepping up 12 px each, size 340, tracking -4, transform `rotate(-10) skewX(-6)`.
- The dot over the "i" is a flame/feather sweeping up-right with a slit.
- `fenix-logo-shadow*` adds a dark offset (`#1A1030`) for use in game, over busy backgrounds; `fenix-logo*` (flat) is for the website.
- The icon is a Barlow "F" plus the flame on a dark purple rounded square.
- The wordmark is ~1.23:1 (1024x833), not the old 670x240 banner. Size it by height in CSS.

## Files
| File | Use |
| --- | --- |
| `fenix-logo.svg`, `fenix-logo-{1024,670,335}.png` | Website / flat logo |
| `fenix-logo-shadow.svg`, `fenix-logo-shadow-{1024,670,335}.png` | In-game title/splash/loading |
| `fenix-icon.svg`, `fenix-icon-{1024,512,192,180,64,32,16}.png`, `favicon.ico` | App launcher icon, PWA/apple-touch icons, favicon |
| `preview.png` | Overview of all variants |

Site copies: `deploy/site/assets/fenix-logo.png` (670), `deploy/site/favicon.ico`, `deploy/site/fenix-icon-{180,192,512}.png`.

## Regenerating
The source scripts were written outside the repo: `glyphs.py` extracts Barlow glyph outlines with fontTools, `build.py` lays out the SVGs, and `flame.txt` holds the flame path. The PNGs were rendered from the SVGs with headless Chrome. To change the design, edit the SVGs directly; they are self-contained paths.
