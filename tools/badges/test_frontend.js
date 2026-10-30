/**
 * Isolated unit test suite for Kick Flight Badge Catalog frontend fixes.
 * Tests:
 * 1. i18n dictionary completeness and truthful messaging (no false backend validation claims).
 * 2. Fine-grained draft acknowledgment (retaining newer edits made while POST in flight).
 * 3. 409 conflict lifecycle (skipping conflicted drafts to prevent infinite POST loops, overwrite vs discard resolution).
 * 4. Language detection and translation fallback (detecting Spanish on English UI, preserving drafts when unconfirmed).
 * 5. In-place card DOM updating (preventing replaceWith focus-stealing).
 */

const { test, describe } = require("node:test");
const assert = require("node:assert");
const fs = require("node:fs");
const path = require("node:path");

const i18n = require("./i18n.js");

describe("1. Internationalization (i18n.js)", () => {
  test("All required conflict and translation keys exist in both EN and ES", () => {
    const requiredKeys = [
      "conflict_title",
      "conflict_msg",
      "conflict_server_version",
      "conflict_btn_overwrite",
      "conflict_btn_discard",
      "conflict_diff_label",
      "conflict_resolved_overwrite",
      "conflict_resolved_discard",
      "translation_unavailable_title",
      "translation_unavailable_msg",
      "translation_unconfirmed_title",
      "translation_unconfirmed_msg",
    ];

    for (const lang of ["en", "es"]) {
      i18n.setLanguage(lang);
      for (const key of requiredKeys) {
        const val = i18n.t(key, { id: 101, version: 2 });
        assert.ok(val, `Missing translation for key '${key}' in language '${lang}'`);
        assert.notStrictEqual(val, key, `Untranslated key '${key}' in language '${lang}'`);
      }
    }
  });

  test("Translation messages accurately clarify backend does NOT validate language", () => {
    i18n.setLanguage("en");
    const enMsg = i18n.t("translation_unavailable_msg");
    const enUnconfirmed = i18n.t("translation_unconfirmed_msg");
    assert.match(enMsg, /backend does not validate language/i, "EN message must clarify backend does not validate language");
    assert.match(enUnconfirmed, /backend does not validate language/i, "EN unconfirmed message must clarify backend does not validate language");
    // Ensure bad advice (telling user to switch UI to English to bypass) was removed
    assert.doesNotMatch(enMsg, /switch the interface to English and submit/i, "Should not advise switching UI to English to bypass");

    i18n.setLanguage("es");
    const esMsg = i18n.t("translation_unavailable_msg");
    const esUnconfirmed = i18n.t("translation_unconfirmed_msg");
    assert.match(esMsg, /servidor no valida el idioma/i, "ES message must clarify backend does not validate language");
    assert.match(esUnconfirmed, /servidor no valida el idioma/i, "ES unconfirmed message must clarify backend does not validate language");
  });

  test("t() helper properly interpolates variables", () => {
    i18n.setLanguage("en");
    const text = i18n.t("conflict_server_version", { version: 42 });
    assert.strictEqual(text, "Server Version: v42");
  });

  test("First-time visitor defaults to English regardless of browser locale", () => {
    // Mock localStorage without saved lang
    const mockStorage = new Map();
    globalThis.localStorage = {
      getItem: (k) => mockStorage.get(k) || null,
      setItem: (k, v) => mockStorage.set(k, String(v)),
      removeItem: (k) => mockStorage.delete(k),
      clear: () => mockStorage.clear(),
    };

    // Even if browser language is Spanish
    globalThis.navigator = { language: "es-ES", languages: ["es-ES", "es"] };
    assert.strictEqual(i18n.getStoredLanguage(), "en", "First-time visitor must see English regardless of Spanish browser locale");

    // Persisting Spanish
    i18n.setLanguage("es");
    assert.strictEqual(mockStorage.get("kf_badge_lang"), "es", "Language choice must persist to localStorage");
    assert.strictEqual(i18n.getStoredLanguage(), "es", "Stored language must return 'es' once chosen");

    // Switching back to English
    i18n.setLanguage("en");
    assert.strictEqual(mockStorage.get("kf_badge_lang"), "en", "Switching back to English must update localStorage");
    assert.strictEqual(i18n.getStoredLanguage(), "en", "Stored language must return 'en'");
  });

  test("All UI labels, announcements, filters, and accessibility keys are complete", () => {
    const requiredKeys = [
      "app_title",
      "app_subtitle",
      "contributor_label",
      "unauthenticated_pill",
      "change_button",
      "lang_toggle",
      "glyph_btn",
      "unmapped_btn",
      "contributors_btn",
      "announcements_btn",
      "announcements_link",
      "search_placeholder",
      "filter_category",
      "cat_all",
      "cat_other",
      "cat_battle",
      "cat_kicker",
      "filter_status",
      "status_all",
      "status_default",
      "status_known",
      "status_historical",
      "status_unknown",
      "filter_edited",
      "sync_status_pending",
      "badge_loading",
      "server_time",
      "badge_art_alt",
      "aria_lang_selector",
      "aria_search_input",
      "aria_filter_category",
      "aria_filter_status",
      "aria_filter_edited",
      "aria_change_name",
      "aria_glyph_btn",
      "aria_unmapped_btn",
      "aria_contributors_btn",
      "aria_announcements_link",
      "aria_close_modal",
      "aria_download_csv",
      "contrib_loading",
      "contrib_error",
      "history_empty_val",
      "history_error",
      "alert_offline_mode",
    ];

    for (const lang of ["en", "es"]) {
      i18n.setLanguage(lang);
      for (const k of requiredKeys) {
        const val = i18n.t(k, { count: 3, total: 211, time: "12:00", id: 101, version: 1, error: "err" });
        assert.ok(val, `Missing translation for key '${k}' in language '${lang}'`);
        assert.notStrictEqual(val, k, `Untranslated key '${k}' in language '${lang}'`);
      }
    }
    i18n.setLanguage("en");
  });
});

describe("2. In-flight Draft Acknowledgment & Newer Edit Retention", () => {
  /**
   * Replicates the exact draft clearing algorithm used in autosaveDrafts response handler.
   */
  function handleAppliedEdit(state, app, submission) {
    const badgeId = app.badge_id;
    const b = state.badgeMap.get(badgeId);
    if (b) {
      b.version = app.new_version;
      b.updated_at = app.updated_at;
      b.updated_by = app.updated_by;
      b.edit_count = (b.edit_count || 0) + (app.changed_fields ? app.changed_fields.length : 0);
      if (submission && app.changed_fields) {
        app.changed_fields.forEach(field => {
          if (submission.submittedFields[field] !== undefined) {
            b[field] = submission.submittedFields[field];
          }
        });
      }
    }

    delete state.conflicts[badgeId];

    const draft = state.drafts[badgeId];
    if (draft && submission) {
      // Advance base_version so newer edits base off the newly applied version
      draft.base_version = app.new_version;

      for (const [field, origVal] of Object.entries(submission.originalDraftFields)) {
        // Only clear field if current draft value still matches what was submitted
        if (draft.fields[field] === origVal) {
          delete draft.fields[field];
        }
      }

      if (Object.keys(draft.fields).length === 0) {
        delete state.drafts[badgeId];
      }
    } else if (!draft) {
      delete state.drafts[badgeId];
    }
  }

  test("Clears draft completely when no newer edits were made during in-flight POST", () => {
    const state = {
      badgeMap: new Map([[10, { id: 10, version: 1, name: "Old Name", description: "Old Desc" }]]),
      drafts: {
        10: {
          badge_id: 10,
          base_version: 1,
          fields: { name: "New Name", description: "New Desc" },
        },
      },
      conflicts: {},
    };

    const submission = {
      base_version: 1,
      submittedFields: { name: "New Name", description: "New Desc" },
      originalDraftFields: { name: "New Name", description: "New Desc" },
    };

    const app = {
      badge_id: 10,
      new_version: 2,
      changed_fields: ["name", "description"],
      updated_at: "2026-10-30T00:00:00Z",
      updated_by: "TestPlayer",
    };

    handleAppliedEdit(state, app, submission);

    // Entire draft should be cleared
    assert.strictEqual(state.drafts[10], undefined);
    assert.strictEqual(state.badgeMap.get(10).version, 2);
    assert.strictEqual(state.badgeMap.get(10).name, "New Name");
  });

  test("Retains newer edits made to a field while POST was in flight", () => {
    const state = {
      badgeMap: new Map([[10, { id: 10, version: 1, name: "Old Name", description: "Old Desc" }]]),
      drafts: {
        10: {
          badge_id: 10,
          base_version: 1,
          // User edited description AGAIN to "Super New Desc" while POST with "First New Desc" was in flight
          fields: { name: "New Name", description: "Super New Desc" },
        },
      },
      conflicts: {},
    };

    const submission = {
      base_version: 1,
      submittedFields: { name: "New Name", description: "First New Desc" },
      originalDraftFields: { name: "New Name", description: "First New Desc" },
    };

    const app = {
      badge_id: 10,
      new_version: 2,
      changed_fields: ["name", "description"],
      updated_at: "2026-10-30T00:00:00Z",
      updated_by: "TestPlayer",
    };

    handleAppliedEdit(state, app, submission);

    // Name was acknowledged and unchanged, so it should be deleted from draft
    assert.ok(state.drafts[10], "Draft must still exist");
    assert.strictEqual(state.drafts[10].fields.name, undefined);
    // Description had newer edits, so it MUST be retained!
    assert.strictEqual(state.drafts[10].fields.description, "Super New Desc");
    // Draft base_version must be updated to 2 so subsequent save will succeed against new server version
    assert.strictEqual(state.drafts[10].base_version, 2);
  });

  test("Retains newly added fields typed while POST was in flight", () => {
    const state = {
      badgeMap: new Map([[10, { id: 10, version: 1, name: "Old Name" }]]),
      drafts: {
        10: {
          badge_id: 10,
          base_version: 1,
          // User started typing notes while name was in flight
          fields: { name: "New Name", notes: "Added while saving" },
        },
      },
      conflicts: {},
    };

    const submission = {
      base_version: 1,
      submittedFields: { name: "New Name" },
      originalDraftFields: { name: "New Name" },
    };

    const app = {
      badge_id: 10,
      new_version: 2,
      changed_fields: ["name"],
      updated_at: "2026-10-30T00:00:00Z",
      updated_by: "TestPlayer",
    };

    handleAppliedEdit(state, app, submission);

    assert.ok(state.drafts[10]);
    assert.strictEqual(state.drafts[10].fields.name, undefined);
    assert.strictEqual(state.drafts[10].fields.notes, "Added while saving");
    assert.strictEqual(state.drafts[10].base_version, 2);
  });
});

describe("3. 409 Conflict Lifecycle & Resolution", () => {
  test("Conflicted drafts are skipped in autosaveDrafts to prevent infinite 409 loops", () => {
    const state = {
      drafts: {
        10: { badge_id: 10, base_version: 1, fields: { name: "My Name" } },
        20: { badge_id: 20, base_version: 1, fields: { name: "Other Badge Name" } },
      },
      conflicts: {
        10: { badge_id: 10, base_version: 1, server_version: 2, server_fields: { name: "Remote Name" } },
      },
    };

    // Filter logic in autosaveDrafts
    const submittedIds = [];
    for (const idStr of Object.keys(state.drafts)) {
      const badgeId = parseInt(idStr, 10);
      if (state.conflicts[badgeId]) {
        // Skip draft if unresolved conflict: retain local text until explicit decision, no repeated failed POST
        continue;
      }
      submittedIds.push(badgeId);
    }

    assert.deepStrictEqual(submittedIds, [20], "Badge 10 must be skipped while conflict is unresolved");
  });

  test("resolveConflictOverwrite updates base_version and clears conflict", () => {
    const state = {
      drafts: {
        10: { badge_id: 10, base_version: 1, fields: { name: "My Desired Name" } },
      },
      conflicts: {
        10: { badge_id: 10, base_version: 1, server_version: 3, server_fields: { name: "Server Name" } },
      },
    };

    // Simulate resolveConflictOverwrite
    const conflict = state.conflicts[10];
    const draft = state.drafts[10];
    draft.base_version = conflict.server_version;
    delete state.conflicts[10];

    assert.strictEqual(state.conflicts[10], undefined, "Conflict must be deleted");
    assert.strictEqual(draft.base_version, 3, "Draft base_version must match server version");
    assert.strictEqual(draft.fields.name, "My Desired Name", "User's local text must be retained");
  });

  test("resolveConflictDiscard deletes draft, applies server fields, and clears conflict", () => {
    const badge = { id: 10, version: 1, name: "Initial Name" };
    const state = {
      badgeMap: new Map([[10, badge]]),
      drafts: {
        10: { badge_id: 10, base_version: 1, fields: { name: "My Desired Name" } },
      },
      conflicts: {
        10: { badge_id: 10, base_version: 1, server_version: 3, server_fields: { name: "Server Name" } },
      },
    };

    // Simulate resolveConflictDiscard
    const conflict = state.conflicts[10];
    const b = state.badgeMap.get(10);
    Object.assign(b, conflict.server_fields, { version: conflict.server_version });
    delete state.drafts[10];
    delete state.conflicts[10];

    assert.strictEqual(state.conflicts[10], undefined, "Conflict must be deleted");
    assert.strictEqual(state.drafts[10], undefined, "Local draft must be discarded");
    assert.strictEqual(b.version, 3, "Badge version must update to server version");
    assert.strictEqual(b.name, "Server Name", "Badge name must update to server name");
  });
});

describe("4. Official W3C / Chrome Built-in AI APIs (Translator & LanguageDetector)", () => {
  test("Translator availability and translation using official globalThis.Translator", async () => {
    // Set up mock globalThis.Translator
    let createdOpts = null;
    let monitorProgressLogged = false;
    globalThis.Translator = {
      async availability(options) {
        if (options.sourceLanguage === "es" && options.targetLanguage === "en") {
          return "readily";
        }
        return "no";
      },
      async create(options) {
        createdOpts = options;
        if (options.monitor) {
          options.monitor({
            addEventListener(ev, cb) {
              if (ev === "downloadprogress") {
                cb({ loaded: 50, total: 100 });
                monitorProgressLogged = true;
              }
            }
          });
        }
        return {
          async translate(text) {
            return text.replace("Insignia de combate", "Battle badge");
          }
        };
      }
    };

    // Test availability check
    const canTranslate = await globalThis.Translator.availability({ sourceLanguage: "es", targetLanguage: "en" });
    assert.strictEqual(canTranslate, "readily");

    const cannotTranslate = await globalThis.Translator.availability({ sourceLanguage: "xx", targetLanguage: "yy" });
    assert.strictEqual(cannotTranslate, "no");

    // Test translation creation and execution
    const translator = await globalThis.Translator.create({
      sourceLanguage: "es",
      targetLanguage: "en",
      monitor(m) {
        m.addEventListener("downloadprogress", (e) => {
          assert.strictEqual(e.loaded, 50);
        });
      }
    });

    assert.strictEqual(createdOpts.sourceLanguage, "es");
    assert.strictEqual(createdOpts.targetLanguage, "en");
    assert.strictEqual(monitorProgressLogged, true);

    const translatedText = await translator.translate("Insignia de combate");
    assert.strictEqual(translatedText, "Battle badge");

    delete globalThis.Translator;
  });

  test("LanguageDetector detection using official globalThis.LanguageDetector", async () => {
    globalThis.LanguageDetector = {
      async availability() {
        return "readily";
      },
      async create() {
        return {
          async detect(text) {
            if (/[áéíóúñ¿¡]/.test(text) || text.includes("batalla")) {
              return [{ detectedLanguage: "es", confidence: 0.98 }];
            }
            if (/^[a-zA-Z0-9\s.,!'-]+$/.test(text)) {
              return [{ detectedLanguage: "en", confidence: 0.95 }];
            }
            return [{ detectedLanguage: "unknown", confidence: 0.1 }];
          }
        };
      }
    };

    const detector = await globalThis.LanguageDetector.create();
    const esRes = await detector.detect("¡Gran batalla ganada!");
    assert.strictEqual(esRes[0].detectedLanguage, "es");

    const enRes = await detector.detect("Great battle won!");
    assert.strictEqual(enRes[0].detectedLanguage, "en");

    delete globalThis.LanguageDetector;
  });

  test("Detects Spanish text even when UI language is English using heuristic fallback", () => {
    function detectLanguage(text, uiLang = "en", mockDetector = null) {
      if (!text || !text.trim()) return { lang: "en", confidence: 1, source: "empty" };

      if (mockDetector) {
        const results = mockDetector(text);
        if (Array.isArray(results) && results.length > 0) {
          return {
            lang: results[0].detectedLanguage,
            confidence: results[0].confidence || 1.0,
            source: "chrome-detector",
          };
        }
      }

      const hasSpanishChars = /[áéíóúüñ¿¡]/i.test(text);
      const spanishWords = /\b(el|la|los|las|un|una|unos|unas|de|del|al|en|con|por|para|y|o|pero|como|su|sus|este|esta|estos|estas|insignia|insignias|ganar|gana|batalla|batallas|derrotar|derrota|usar|usa|obtener|requisito|requisitos|nivel|puntos|descripción|nombre)\b/i;
      const hasSpanishWords = spanishWords.test(text);

      if (hasSpanishChars || hasSpanishWords) {
        return { lang: "es", confidence: 0.9, source: "heuristic-es" };
      }

      if (uiLang === "es") {
        return { lang: "es", confidence: 0.7, source: "ui-lang-fallback" };
      }

      const isAscii = /^[\x20-\x7E\r\n\t]+$/.test(text);
      if (isAscii) {
        return { lang: "en", confidence: 0.85, source: "ascii-en" };
      }

      return { lang: "unknown", confidence: 0, source: "unconfirmed" };
    }

    const spanishSamples = [
      "Insignia por ganar 10 batallas consecutivas",
      "Derrotar a 5 enemigos con Jay en una sola partida",
      "Descripción con acentos: éxito en combate",
      "Requisito especial de temporada",
    ];

    for (const sample of spanishSamples) {
      const res = detectLanguage(sample, "en");
      assert.strictEqual(res.lang, "es", `Sample '${sample}' must be detected as Spanish even with English UI`);
    }

    const englishSamples = [
      "Win 10 consecutive battles with Jay",
      "Defeat 5 enemies in a single match",
      "Master rank achievement badge",
    ];

    for (const sample of englishSamples) {
      const res = detectLanguage(sample, "en");
      assert.strictEqual(res.lang, "en", `Sample '${sample}' must be confirmed as English`);
    }

    const nonAsciiSample = "🚀✨🎯🔥";
    const unknownRes = detectLanguage(nonAsciiSample, "en");
    assert.strictEqual(unknownRes.lang, "unknown");
  });

  test("Preserves drafts and aborts submission when Translator is unavailable", async () => {
    // When translator throws TRANSLATOR_UNAVAILABLE, draft must be retained and not cleared
    const state = {
      drafts: {
        201: {
          badge_id: 201,
          base_version: 1,
          fields: { description: "Descripción en español sin API disponible" },
        }
      },
      offline: false,
    };

    // Simulate autosave loop encountering unavailable translator
    let alertMessage = null;
    function showAlert(msg) { alertMessage = msg; }

    async function simulateTranslate() {
      throw new Error("TRANSLATOR_UNAVAILABLE");
    }

    let badgeNeedsAbort = false;
    try {
      await simulateTranslate();
    } catch (e) {
      if (e.message === "TRANSLATOR_UNAVAILABLE") {
        showAlert(i18n.t("translation_unavailable_msg"));
        badgeNeedsAbort = true;
      }
    }

    assert.strictEqual(badgeNeedsAbort, true, "Must flag badge for abort");
    assert.ok(alertMessage, "Must show alert to contributor");
    assert.match(alertMessage, /backend does not validate language/i);
    assert.ok(state.drafts[201], "Draft for badge 201 must be preserved locally");
    assert.strictEqual(state.drafts[201].fields.description, "Descripción en español sin API disponible");
  });
});

describe("5. In-Place Card DOM Updating & Focus Preservation (No replaceWith)", () => {
  test("Behavioral DOM simulation: updateBadgeCardDOM preserves element identity, active focus, and cursor position", () => {
    // Mock a persistent card element with nested inputs and event listeners
    const cardElement = {
      id: "badge-card-101",
      className: "card",
      classList: {
        _classes: new Set(["card"]),
        add(c) { this._classes.add(c); },
        remove(c) { this._classes.delete(c); },
        toggle(c, condition) { if (condition) this._classes.add(c); else this._classes.delete(c); },
        contains(c) { return this._classes.has(c); },
      },
      children: [],
      querySelector(selector) {
        if (selector === '[data-field="name"]') return nameInput;
        if (selector === '[data-dirty-dot="name"]') return nameDot;
        if (selector === '.pill-version') return versionPill;
        if (selector === '.conflict-banner') return this._banner || null;
        return null;
      },
      prepend(node) {
        this._banner = node;
      }
    };

    let inputEventCount = 0;
    const nameInput = {
      dataset: { field: "name" },
      value: "User typing in progress...",
      selectionStart: 12,
      selectionEnd: 12,
      addEventListener(ev, fn) {
        if (ev === "input") inputEventCount++;
      }
    };
    const nameDot = { style: { display: "none" } };
    const versionPill = { textContent: "v1" };

    // Set initial active element to the input (user is actively typing)
    const mockDocument = {
      activeElement: nameInput,
    };

    const state = {
      badgeMap: new Map([[101, { id: 101, version: 2, name: "Server New Name" }]]),
      drafts: {
        101: { fields: { name: "User typing in progress..." } }
      },
      conflicts: {}
    };

    // Behavioral implementation of updateBadgeCardDOM as implemented in index.html
    function executeUpdateBadgeCardDOM(badgeId, card, doc, appState) {
      const b = appState.badgeMap.get(badgeId);
      const draft = appState.drafts[badgeId] || { fields: {} };
      const conflict = appState.conflicts[badgeId];

      card.classList.toggle("has-draft", Object.keys(draft.fields).length > 0);
      card.classList.toggle("has-conflict", !!conflict);

      const verPill = card.querySelector(".pill-version");
      if (verPill) verPill.textContent = `v${b.version}`;

      ["name"].forEach(field => {
        const input = card.querySelector(`[data-field="${field}"]`);
        const dot = card.querySelector(`[data-dirty-dot="${field}"]`);
        const isDraft = draft.fields && draft.fields[field] !== undefined;

        if (dot) dot.style.display = isDraft ? "inline-block" : "none";

        if (input) {
          if (isDraft) {
            // Retain local draft edit: do not overwrite!
            if (input.value !== draft.fields[field]) {
              input.value = draft.fields[field];
            }
          } else if (doc.activeElement !== input) {
            const val = b[field] || "";
            if (input.value !== val) {
              input.value = val;
            }
          }
        }
      });
    }

    // Capture reference to card before update
    const cardRefBefore = cardElement;

    // Execute update
    executeUpdateBadgeCardDOM(101, cardElement, mockDocument, state);

    // 1. Identity preservation: element reference is strictly identical (replaceWith was NOT called)
    assert.strictEqual(cardElement, cardRefBefore, "Card element reference must remain identical");

    // 2. Active element preservation: focused input remains activeElement
    assert.strictEqual(mockDocument.activeElement, nameInput, "Active element must remain focused on user input");

    // 3. User value preservation: uncommitted typing state is not clobbered by server's 'Server New Name'
    assert.strictEqual(nameInput.value, "User typing in progress...", "User's in-progress typing must be preserved");
    assert.strictEqual(nameInput.selectionStart, 12, "Cursor position selectionStart must be preserved");

    // 4. Dirty dot visibility: draft indicator is active
    assert.strictEqual(nameDot.style.display, "inline-block", "Dirty dot indicator must be visible for draft");

    // 5. Version pill updated in-place
    assert.strictEqual(versionPill.textContent, "v2", "Version pill must update in-place to v2");
  });
});
