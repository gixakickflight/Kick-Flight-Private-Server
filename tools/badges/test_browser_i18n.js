/**
 * End-to-end browser test for bilingual badge catalog UI using Playwright.
 * Runs in an isolated browser profile/context to verify:
 * 1. First-time visitor sees English UI regardless of browser locale (e.g. es-ES).
 * 2. Prominent selector changes UI immediately to Spanish.
 * 3. Selection persists across full page reload.
 * 4. Switching back to English updates UI immediately and persists across reload.
 * 5. Both dropdown selector (#lang-select / #langSelect) and toggle button (#btn-toggle-lang) work in sync.
 */

let firefox;
try {
  firefox = require("playwright").firefox;
} catch (e) {
  if (e.code === "MODULE_NOT_FOUND") {
    console.log("[test_browser_i18n] playwright not installed, skipping browser E2E test.");
    process.exit(0);
  }
  throw e;
}
const assert = require("node:assert");

const TARGET_URL = process.env.KF_BADGES_TEST_URL || "http://127.0.0.1:18766/badges";

async function runBrowserI18nTests() {
  console.log(`[test_browser_i18n] Connecting to ${TARGET_URL}...`);
  const browser = await firefox.launch();

  try {
    // 1. First-time visitor with Spanish locale (es-ES)
    const context = await browser.newContext({
      locale: "es-ES",
    });
    const page = await context.newPage();

    await page.goto(TARGET_URL);
    await page.waitForSelector("#txt-app-title");

    // Verify first load is in English
    const htmlLang = await page.getAttribute("html", "lang");
    const appTitle = await page.textContent("#txt-app-title");
    const modalTitle = await page.textContent("#modal-contributor-title");
    const modalBtn = await page.textContent("#btn-save-player");
    const selVal = await page.inputValue("#lang-select");
    const annText = (await page.textContent("#txt-announcements-link")).trim();
    const filterCatFirst = (await page.textContent("#filter-category option:first-child")).trim();

    assert.strictEqual(htmlLang, "en", "First load must have html[lang]=en");
    assert.strictEqual(appTitle, "Kick Flight Collaborative Badge Catalog");
    assert.strictEqual(modalTitle, "Kick Flight Contributor Name");
    assert.strictEqual(modalBtn, "Continue to Catalog");
    assert.strictEqual(selVal, "en");
    assert.strictEqual(annText, "📢 Announcements");
    assert.strictEqual(filterCatFirst, "All Categories");
    console.log("  ✔ 1. First load with Spanish locale shows English UI");

    // Enter player name and continue
    await page.fill("#input-player-name", "BrowserTestPlayer");
    await page.click("#btn-save-player");
    const currentPlayer = await page.textContent("#current-player-name");
    assert.strictEqual(currentPlayer, "BrowserTestPlayer");
    console.log("  ✔ 2. Contributor name entered and displayed cleanly");

    // 2. Switch to Spanish via prominent selector #lang-select
    await page.selectOption("#lang-select", "es");
    const esHtmlLang = await page.getAttribute("html", "lang");
    const esTitle = await page.textContent("#txt-app-title");
    const esAnnText = (await page.textContent("#txt-announcements-link")).trim();
    const esFilterCatFirst = (await page.textContent("#filter-category option:first-child")).trim();
    const esFilterStatusFirst = (await page.textContent("#filter-status option:first-child")).trim();
    const esGlyphText = (await page.textContent("#btn-glyph")).trim();
    const esContribText = (await page.textContent("#btn-contributors")).trim();

    assert.strictEqual(esHtmlLang, "es");
    assert.strictEqual(esTitle, "Catálogo Colaborativo de Insignias · Kick Flight");
    assert.strictEqual(esAnnText, "📢 Anuncios");
    assert.strictEqual(esFilterCatFirst, "Todas las categorías");
    assert.strictEqual(esFilterStatusFirst, "Todos los estados");
    assert.strictEqual(esGlyphText, "🔤 Clave de Glifos");
    assert.strictEqual(esContribText, "👥 Colaboradores");
    console.log("  ✔ 3. Selector switches UI immediately to Spanish");

    // 3. Reload and verify persistence
    await page.reload();
    await page.waitForSelector("#txt-app-title");
    const reloadedLang = await page.getAttribute("html", "lang");
    const reloadedTitle = await page.textContent("#txt-app-title");
    const reloadedSelVal = await page.inputValue("#lang-select");
    assert.strictEqual(reloadedLang, "es");
    assert.strictEqual(reloadedTitle, "Catálogo Colaborativo de Insignias · Kick Flight");
    assert.strictEqual(reloadedSelVal, "es");
    console.log("  ✔ 4. Spanish preference retained across page reload");

    // 4. Switch back to English
    await page.selectOption("#lang-select", "en");
    const backLang = await page.getAttribute("html", "lang");
    const backTitle = await page.textContent("#txt-app-title");
    const backAnnText = (await page.textContent("#txt-announcements-link")).trim();
    assert.strictEqual(backLang, "en");
    assert.strictEqual(backTitle, "Kick Flight Collaborative Badge Catalog");
    assert.strictEqual(backAnnText, "📢 Announcements");
    console.log("  ✔ 5. Switching back to English updates UI immediately");

    // 5. Reload again and verify English persistence
    await page.reload();
    await page.waitForSelector("#txt-app-title");
    const finalLang = await page.getAttribute("html", "lang");
    const finalTitle = await page.textContent("#txt-app-title");
    const finalSelVal = await page.inputValue("#lang-select");
    assert.strictEqual(finalLang, "en");
    assert.strictEqual(finalTitle, "Kick Flight Collaborative Badge Catalog");
    assert.strictEqual(finalSelVal, "en");
    console.log("  ✔ 6. English preference retained across page reload");

    // 6. Test #btn-toggle-lang button click toggling
    await page.click("#btn-toggle-lang");
    const toggledLang = await page.getAttribute("html", "lang");
    assert.strictEqual(toggledLang, "es");
    await page.click("#btn-toggle-lang");
    const toggledBack = await page.getAttribute("html", "lang");
    assert.strictEqual(toggledBack, "en");
    console.log("  ✔ 7. Toggle button #btn-toggle-lang toggles back and forth in sync");

    console.log("\nAll browser Playwright integration tests passed successfully!");
  } finally {
    await browser.close();
  }
}

if (require.main === module) {
  runBrowserI18nTests().catch((err) => {
    console.error("Browser test failed:", err);
    process.exit(1);
  });
}

module.exports = { runBrowserI18nTests };
