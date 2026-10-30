/**
 * Bilingual internationalization dictionary and helpers for Kick Flight Badge Catalog.
 * English-first with Spanish translations and robust fallbacks.
 */

const I18N_STRINGS = {
  en: {
    app_title: "Kick Flight Collaborative Badge Catalog",
    app_subtitle: "Community restoration of all 211 badges. English-first catalog with attribution and versioning.",
    contributor_label: "Contributor",
    unauthenticated_pill: "Unauthenticated",
    change_button: "Change",
    lang_toggle: "Español",
    glyph_btn: "🔤 Glyph Key",
    unmapped_btn: "📋 Guide Notes",
    search_placeholder: "Search by ID, name, or condition...",
    filter_category: "Category",
    cat_all: "All Categories",
    cat_other: "Other (0)",
    cat_battle: "Battle (1)",
    cat_kicker: "Kicker (2)",
    filter_status: "Status",
    status_all: "All Statuses",
    status_default: "Default",
    status_known: "Known",
    status_historical: "Historical",
    status_unknown: "Unknown",
    filter_edited: "Show Edited Only",
    sync_status_synced: "Synced",
    sync_status_saving: "Autosaving...",
    sync_status_offline: "Offline (Drafts saved)",
    sync_status_error: "Sync error",
    badge_count: "Showing {count} of {total} badges",
    
    // Card fields
    card_name_label: "Badge Name (EN)",
    card_desc_label: "Description (Theme & Visuals)",
    card_req_label: "Requirements (Unlock Condition)",
    card_notes_label: "Research Notes",
    card_provenance: "Provenance & Raw Catalog Data",
    card_history_btn: "History ({count})",
    card_last_edit: "Last edit by {player} ({time})",
    card_version: "v{version}",
    card_secret: "Secret Badge",

    // Contributor Modal
    modal_title: "Kick Flight Contributor Name",
    modal_desc: "Please enter your Kick Flight player name to attribute your edits in the collaborative catalog.",
    modal_unauth_warning: "⚠️ Note: This self-entered name is NOT authenticated or verified against your Kick Flight account or password. Anyone can enter any display name; it is used solely to record who suggested each edit in the public audit log.",
    modal_input_placeholder: "Player name (1-32 chars)",
    modal_save_btn: "Continue to Catalog",
    modal_error_empty: "Please enter a valid player name.",
    modal_error_length: "Player name must be 32 characters or fewer.",

    // Glyph Modal
    glyph_modal_title: "Kick Flight Glyph Key Reference",
    glyph_modal_subtitle: "Reference sheet mapping in-game alien glyph shapes to standard alphanumeric characters (A-Z).",
    glyph_modal_desc: "This reference was decoded from the original game textures and sprite assets. Use it to decipher the stylized writing on badge banners and ribbons.",
    glyph_close_btn: "Close",

    // Unmapped Requirements Modal
    unmapped_modal_title: "Historical Guide Unmapped Requirements",
    unmapped_modal_desc: "These 14 requirements were transcribed from the historical RAR Japanese game guide (rar-games.jp). Their original badge IDs are unconfirmed. Match these conditions to unknown badges when visual evidence aligns.",
    unmapped_col_name: "Transcribed Title",
    unmapped_col_condition: "Condition",
    unmapped_col_note: "Reference Notes",
    unmapped_close_btn: "Close",

    // Audit History Modal
    history_modal_title: "Badge {id} Edit History",
    history_no_edits: "No community edits recorded yet for this badge.",
    history_col_time: "Timestamp",
    history_col_player: "Contributor",
    history_col_field: "Field",
    history_col_old: "Previous Value",
    history_col_new: "New Value",
    history_col_version: "Version",
    history_close_btn: "Close",

    // Conflict Notification
    conflict_title: "Concurrency Conflict Detected",
    conflict_msg: "Another contributor modified badge {id} while you were editing. To prevent overwriting their work, the server rejected your changes. Your local draft has been preserved below so you do not lose any work.",
    conflict_server_version: "Server Version: v{version}",

    // Translation Error
    translation_unavailable_title: "Chrome Translation API Unavailable",
    translation_unavailable_msg: "All editable contributions must be saved in English. When typing in Spanish, the Chrome Built-in Translator API is required to convert your text before saving.\n\nThe Translation API was not detected in this browser. To contribute in Spanish:\n1. Use Google Chrome and enable chrome://flags/#translation-api\n2. Or switch the interface to English and submit contributions directly in English.\n\nYour draft has been safely preserved locally and Spanish was NOT saved as English.",
    translation_downloading: "Downloading Chrome translation model ({progress}%)...",
    translation_ready: "Translation model ready.",
  },

  es: {
    app_title: "Catálogo Colaborativo de Insignias · Kick Flight",
    app_subtitle: "Restauración comunitaria de las 211 insignias. Catálogo inglés-primero con atribución y control de versiones.",
    contributor_label: "Colaborador",
    unauthenticated_pill: "No autenticado",
    change_button: "Cambiar",
    lang_toggle: "English",
    glyph_btn: "🔤 Clave de Glifos",
    unmapped_btn: "📋 Notas de Guía",
    search_placeholder: "Buscar por ID, nombre o condición...",
    filter_category: "Categoría",
    cat_all: "Todas las categorías",
    cat_other: "Otra (0)",
    cat_battle: "Batalla (1)",
    cat_kicker: "Kicker (2)",
    filter_status: "Estado",
    status_all: "Todos los estados",
    status_default: "Predeterminada",
    status_known: "Conocida",
    status_historical: "Histórica",
    status_unknown: "Desconocida",
    filter_edited: "Mostrar solo editadas",
    sync_status_synced: "Sincronizado",
    sync_status_saving: "Guardando...",
    sync_status_offline: "Sin conexión (Borrador guardado)",
    sync_status_error: "Error de sincronización",
    badge_count: "Mostrando {count} de {total} insignias",
    
    // Card fields
    card_name_label: "Nombre de insignia (EN)",
    card_desc_label: "Descripción (Temática y Arte)",
    card_req_label: "Requisitos (Condición para obtener)",
    card_notes_label: "Notas de investigación",
    card_provenance: "Procedencia y Datos Maestros",
    card_history_btn: "Historial ({count})",
    card_last_edit: "Última edición por {player} ({time})",
    card_version: "v{version}",
    card_secret: "Insignia Secreta",

    // Contributor Modal
    modal_title: "Nombre de Colaborador de Kick Flight",
    modal_desc: "Por favor introduce tu nombre de jugador de Kick Flight para atribuir tus aportes en el catálogo colaborativo.",
    modal_unauth_warning: "⚠️ Nota: Este nombre introducido por ti NO está autenticado ni verificado con tu cuenta ni contraseña de Kick Flight. Cualquiera puede ingresar cualquier nombre; se utiliza únicamente para registrar quién propuso cada edición en el registro público de auditoría.",
    modal_input_placeholder: "Nombre de jugador (1-32 caracteres)",
    modal_save_btn: "Continuar al Catálogo",
    modal_error_empty: "Por favor introduce un nombre válido.",
    modal_error_length: "El nombre no puede exceder 32 caracteres.",

    // Glyph Modal
    glyph_modal_title: "Referencia de Glifos de Kick Flight",
    glyph_modal_subtitle: "Hoja de referencia que mapea los glifos alienígenas del juego a caracteres alfanuméricos estándar (A-Z).",
    glyph_modal_desc: "Esta referencia fue extraída de las texturas originales del juego. Úsala para descifrar el texto estilizado en los emblemas y listones de las insignias.",
    glyph_close_btn: "Cerrar",

    // Unmapped Requirements Modal
    unmapped_modal_title: "Requisitos No Mapeados de Guía Histórica",
    unmapped_modal_desc: "Estos 14 requisitos fueron transcritos de la guía histórica japonesa RAR (rar-games.jp). Sus IDs exactos de insignia no están confirmados. Asocia estas condiciones a insignias desconocidas cuando la evidencia visual coincida.",
    unmapped_col_name: "Título Transcrito",
    unmapped_col_condition: "Condición",
    unmapped_col_note: "Notas de Referencia",
    unmapped_close_btn: "Cerrar",

    // Audit History Modal
    history_modal_title: "Historial de Ediciones · Insignia {id}",
    history_no_edits: "Aún no hay ediciones comunitarias registradas para esta insignia.",
    history_col_time: "Fecha/Hora",
    history_col_player: "Colaborador",
    history_col_field: "Campo",
    history_col_old: "Valor Anterior",
    history_col_new: "Nuevo Valor",
    history_col_version: "Versión",
    history_close_btn: "Cerrar",

    // Conflict Notification
    conflict_title: "Conflicto de Concurrencia Detectado",
    conflict_msg: "Otro colaborador modificó la insignia {id} mientras editabas. Para no sobrescribir su aporte, el servidor rechazó tus cambios. Tu borrador local se ha conservado abajo para que no pierdas trabajo.",
    conflict_server_version: "Versión en Servidor: v{version}",

    // Translation Error
    translation_unavailable_title: "API de Traducción de Chrome No Disponible",
    translation_unavailable_msg: "Todas las contribuciones deben almacenarse en inglés. Al escribir en español, se requiere la API de traducción integrada de Chrome para convertir el texto antes de guardar.\n\nNo se detectó la API en este navegador. Para contribuir en español:\n1. Usa Google Chrome y activa chrome://flags/#translation-api\n2. O cambia el idioma a inglés y envía tus contribuciones directamente en inglés.\n\nTu borrador se ha conservado de forma segura en local y el texto en español NO se guardó como inglés.",
    translation_downloading: "Descargando modelo de traducción de Chrome ({progress}%)...",
    translation_ready: "Modelo de traducción listo.",
  }
};

let currentLang = "en";

function setLanguage(lang) {
  if (lang !== "en" && lang !== "es") lang = "en";
  currentLang = lang;
  try {
    localStorage.setItem("kf_badge_lang", lang);
  } catch (e) {}
  document.documentElement.lang = lang;
}

function getStoredLanguage() {
  try {
    const saved = localStorage.getItem("kf_badge_lang");
    if (saved === "en" || saved === "es") return saved;
    const browser = navigator.language || "";
    if (browser.startsWith("es")) return "es";
  } catch (e) {}
  return "en";
}

function t(key, vars = {}) {
  const dict = I18N_STRINGS[currentLang] || I18N_STRINGS["en"];
  let str = dict[key] || I18N_STRINGS["en"][key] || key;
  for (const [k, v] of Object.entries(vars)) {
    str = str.replace(new RegExp(`\\{${k}\\}`, "g"), v);
  }
  return str;
}
