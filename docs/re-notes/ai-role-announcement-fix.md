# Release 2.11.4 Latest Information Announcement Fix

## 1. Problem Summary & Observed Production State

- **Reported Incident:** After PR #46 merged (`af6e6d2`) and CI run 39355017722 claimed success, the public GENERAL NEWS page (`/updates`) and in-game Latest Information WebView showed:
  1. `matchmaking-roles-2026-10-19` ("New matchmaking and more bot variety")
  2. `v2-11-3` ("Patch 2.11.3")
  3. `v2-11-2` ("Patch 2.11.2")
  Release 2.11.4 was missing from the rendered view.
- **Live Response Audit (`https://kick-flight-fenix.us.ci/updates` & `/webview/information/index`):**
  - Visible `<article>` tags in DOM: `['matchmaking-roles-2026-10-19', 'v2-11-3', 'v2-11-2']`.
  - Unrendered HTML comments containing `v2-11-4`: 1 occurrence in `<head>`.
  - Standalone release page (`/updates-2.11.4.html`): 200 OK, standalone content valid.

## 2. Proven Root Cause

1. **Comment Match Flaw in `scripts/update-latest-information.py`:**
   - The starting HTML template in `docs/updates-page.html` contains an instructional comment in `<head>`:
     `<!-- ... To add a patch: copy one <article class="patch"> block to the top of <main>. ... -->`
   - `insert_article_into_html` performed an unanchored global search: `first_article = re.search(r'<article\b', html)`.
   - This matched the string `<article class="patch">` inside the `<!-- ... -->` comment at index ~500 in `<head>`.
   - The article block `<article class="patch" id="v2-11-4">` was sliced and inserted directly inside the HTML comment.
2. **False-Positive Presence Checks in Previous Claims:**
   - `get_article_ids(html)` used `re.findall(r'<article[^>]*\bid=["\']([^"\']+)["\']', html)`.
   - This regex matched `<article ... id="v2-11-4">` inside the comment, falsely reporting:
     `Active articles: ['v2-11-4', 'matchmaking-roles-2026-10-19', 'v2-11-3', 'v2-11-2']`.
   - Browsers correctly parse `<!-- ... -->` as a comment node and ignore all tags inside, rendering `matchmaking-roles-2026-10-19` as the first visible article.

## 3. Implemented Fix

1. **Comment & Unrendered Block Masked DOM Traversal (`scripts/update-latest-information.py`):**
   - Added `mask_comments(text)` and `mask_invisible(text)` which mask HTML comments, `<head>`, `<script>`, and `<style>` blocks with spaces to preserve character offsets while preventing unrendered regex matches.
   - Added `strip_articles_from_comments(html)` to sanitize and recover existing persistent state where `v2-11-4` was trapped in comments.
   - Constrained article insertion to `<main>`: finds the first visible `<article>` tag strictly within `<main>` (or following `</h1>`).
   - Added `get_visible_article_ids(html)` to parse only visible articles outside comments, head, script, and style tags.
2. **Automated Smoke Assertion (`scripts/deploy-vps-ci.sh`):**
   - Added post-deployment smoke test asserting that both `/webview/information/index` and `/updates` return HTML whose first visible parsed article is `v2-11-4`, followed by `matchmaking-roles-2026-10-19`, `v2-11-3`, and `v2-11-2`.
   - Deployment smoke test strictly ignores HTML comments, `<head>`, `<script>`, and `<style>` tags to inspect visible DOM only. Fails deployment closed if false-positive or trapped comments occur.

## 4. Verification

- `tests/test_update_latest_information.py`:
  - `test_exact_observed_state_comment_pollution_recovery`: exercises exact observed production snapshot; proves naive regex false positive vs visible parser; verifies recovery, comment sanitization, and visible placement ahead of all 3 old articles.
  - `test_fresh_template_with_instruction_comment_inserts_in_main`: verifies fresh template insertion ignores instruction comment.
  - Idempotency verified: re-running on updated state performs no modifications and preserves exact article order.

## 5. Pull Request

- **PR #47:** https://github.com/gixakickflight/Kick-Flight-Private-Server/pull/47

