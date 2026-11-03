/**
 * Kick-Flight Balance UI - Shared Internationalization (i18n) Module
 *
 * Vanilla JavaScript module supporting Spanish ('es', default) and English ('en').
 * - Serves correctly at /balance and root.
 * - Persists preference via localStorage with cookie fallback and in-memory fallback.
 * - Keyed dictionaries for exact, non-brittle translations.
 * - Zero external dependencies.
 */
(function (root, factory) {
  if (typeof define === "function" && define.amd) {
    define([], factory);
  } else if (typeof module === "object" && module.exports) {
    module.exports = factory();
  } else {
    root.BalanceI18n = factory();
  }
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  const STORAGE_KEY = "balance_lang";
  const COOKIE_NAME = "balance_lang";
  const SUPPORTED_LANGS = ["es", "en"];
  const DEFAULT_LANG = "es";

  const DICTIONARIES = {
    es: {
      common: {
        appName: "Kick-Flight Balance",
        loading: "Cargando…",
        close: "Cerrar",
        cancel: "Cancelar",
        save: "Guardar",
        saving: "Guardando…",
        revert: "Revertir",
        reverting: "Revirtiendo…",
        apply: "Reiniciar API para aplicar",
        applying: "Reiniciando API…",
        export: "Exportar modificaciones",
        retry: "Reintentar",
        error: "Error",
        warning: "Advertencia",
        success: "Éxito",
        active: "Activo",
        inactive: "Inactivo",
        empty: "No hay elementos",
        noResults: "No se encontraron resultados",
        language: "Idioma",
        spanish: "Español",
        english: "English",
        secondsUnit: "s",
        metersUnit: "m",
        percentUnit: "%",
        yes: "Sí",
        no: "No",
        screensCount: "{shown} de {total} pantallas",
        itemsCount: "{shown} de {total} mostrados"
      },
      header: {
        title: "Kick-Flight Balance",
        topHintMasters: "las modificaciones se guardan en <code>.local/masters-overrides</code> (nunca en <code>config/</code>) &mdash; reinicia la API para cargarlas",
        topHintBotAi: "Se guardan en el servidor para nuevas partidas.",
        currentUser: "Usuario: {user}",
        logout: "Cerrar sesión",
        langLabel: "Idioma",
        langSelectAria: "Seleccionar idioma"
      },
      tabs: {
        kickers: "Kickers",
        discs: "Discos",
        text: "Texto",
        botAi: "IA de bots",
        latestInformation: "Última información",
        health: "Salud",
        maintenance: "Mantenimiento",
        clientUpdate: "Actualización de cliente"
      },
      filters: {
        searchPlaceholder: "Filtrar por nombre o id",
        searchAria: "Filtrar lista por nombre o identificador",
        allRarities: "Todas las rarezas",
        allTypes: "Todos los tipos",
        rarityNormal: "Normal",
        rarityRare: "Rara",
        raritySuperRare: "Súper Rara",
        rarityUltraRare: "Ultra Rara"
      },
      footer: {
        unsavedChanges: "Cambios sin guardar: {count}",
        save: "Guardar",
        revert: "Revertir",
        apply: "Reiniciar API para aplicar",
        export: "Exportar modificaciones",
        note: "los cambios se escriben en el directorio de modificaciones; reinicia la API para cargarlos",
        savedSuccess: "Guardado correctamente.",
        saveFailed: "Error al guardar: {err}",
        revertedSuccess: "Modificaciones revertidas.",
        revertFailed: "Error al revertir: {err}",
        appliedSuccess: "API reiniciada correctamente.",
        applyFailed: "Error al reiniciar API: {err}",
        applyPending: "Hay modificaciones pendientes de aplicar.",
        applyUpToDate: "Todo aplicado y sincronizado.",
        noTablesToSave: "Nada que guardar",
        savingTable: "Guardando masters_{table}.json ...",
        savedTableSuccess: "ok se guardó la modificación masters_{table}.json ({rows} filas)",
        savedTableError: "falló masters_{table}.json: {err}",
        savedToOverrideDir: "Guardado en el directorio de modificaciones - haz clic en \"Reiniciar API para aplicar\" para cargarlas",
        revertingMasters: "Recargando los maestros ...",
        revertedMastersSuccess: "Maestros recargados",
        revertedMastersError: "Error al recargar: {err}",
        confirmRestart: "¿Reiniciar la API para cargar las modificaciones guardadas?\n\nSe interrumpen las partidas en curso: los jugadores en partida se desconectan o pierden el estado del combate.\n\n{pending}Comando:\n{cmd}",
        pendingOverrides: "Modificación(es) pendiente(s): {list}\n\n",
        restartingApi: "Reiniciando la API: {cmd}",
        restartedApiSuccess: "API reiniciada y saludable tras {seconds}s ({probes} sondeo(s))",
        stillPendingOverrides: "Aún pendientes: {list}",
        noPendingOverrides: "Sin modificaciones pendientes: la API ha cargado todas las modificaciones"
      },
      login: {
        title: "Iniciar sesión · Kick-Flight Balance",
        heading: "Kick-Flight Balance",
        langLabel: "Idioma",
        usernameLabel: "Usuario",
        usernamePlaceholder: "Nombre de usuario",
        passwordLabel: "Contraseña",
        passwordPlaceholder: "Contraseña",
        submit: "Iniciar sesión",
        submitting: "Iniciando sesión…",
        failed: "Credenciales incorrectas o inicio de sesión fallido",
        locked: "Credenciales incorrectas o cuenta bloqueada"
      },
      kickerPanel: {
        stats: "Estadísticas",
        statsEmpty: "no hay fila en kicker_parameter para este kicker",
        passive: "Habilidad pasiva",
        passiveEmpty: "sin fila de habilidad pasiva",
        skill: "Habilidad Kicker (KS)",
        skillEmpty: "sin fila de habilidad kicker",
        special: "Habilidad especial (SP)",
        specialEmpty: "sin fila de habilidad especial",
        attacks: "Ataques básicos",
        attacksSub: "Impactos, colisiones y proyectiles",
        attacksEmpty: "sin ataques configurados",
        addRow: "+ Añadir fila",
        addRowTitle: "Añadir fila a masters_{table}.json para {link} {val}",
        deleteRow: "Eliminar fila",
        deleteRowTitle: "Eliminar esta fila",
        revertTable: "Revertir tabla",
        overridden: "modificado"
      },
      discPanel: {
        guide: "Los nombres aparecen en inglés y español; las claves originales se conservan. Abre ? con clic, toque o teclado para ver qué cambia el valor. Los multiplicadores de daño son por impacto; la animación determina el número de golpes.",
        parameters: "Parámetros del disco",
        skill: "Habilidad del disco",
        effects: "Efectos y condiciones",
        effectsEmpty: "sin efectos adicionales",
        condition: "Condición",
        trap: "Trampa",
        heal: "Curación",
        blowOff: "Empuje / Salto",
        pullIn: "Atracción",
        collision: "Colisión",
        hit: "Impacto"
      },
      textPanel: {
        title: "Textos de la interfaz (masters_translation.json)",
        screenLabel: "Pantalla:",
        otherScreen: "(claves usadas desde código / enums)",
        searchPlaceholder: "Filtrar por clave o texto…",
        keyCol: "Clave",
        valCol: "Texto en pantalla",
        wireframeToggle: "Mostrar wireframe",
        otherScreenDesc: "filas de traducción que ningún prefab de UI referencia directamente: nombres de enums y textos buscados por clave",
        screenMeta: "{count} textos localizados · masters_translation.json",
        boxesLegend: "cajas: elemento de texto",
        noTranslation: "sin traducción (en blanco en el juego)",
        outsideCanvas: "fuera del canvas (contenido con desplazamiento / layout group)",
        noRowInMasters: "sin fila en masters_translation.json (en blanco en juego) - ejecuta scripts/generate_translations.py"
      },
      botAi: {
        heading: "¿Qué quieres que haga?",
        subheading: "Elige el comportamiento principal del bot o ajusta parámetros individuales más abajo.",
        modeLabel: "Modo de juego",
        rosterTitle: "Kickers en este perfil",
        affectsLabel: "Afecta a: {names}",
        kickerSelectLabel: "Kicker a editar:",
        editingKicker: "Editando kicker: {name}",
        kickerListCount: "14 kickers ({mode})",
        dirtyBadge: "Borrador con cambios sin guardar",
        dirtyModified: "modificado",
        intentChipLabel: "Intención: ",
        intentActiveBadge: "✓ Activo",
        behaviorSelected: "Comportamiento seleccionado:",
        advancedToggle: "🛠️ Ajustes avanzados de comportamiento (opcional)",
        discardDraft: "Descartar borrador",
        reviewSave: "Revisar y guardar",
        saveDirect: "Guardar cambios",
        saveBtn: "Guardar cambios",
        reviewModalHead: "📋 Guardar cambios en el servidor",
        reviewModalNotice: "Revisa los cambios. Al guardar quedarán en el servidor para las nuevas partidas.",
        reviewModalConfirm: "Guardar en servidor",
        reviewModalCancel: "Seguir editando",
        reviewModalDiffDetails: "Ver detalle de valores numéricos",
        conflictModalHead: "⚠️ Conflicto de Concurrencia (409)",
        conflictModalNotice: "El archivo de configuración fue modificado externamente en el disco por otro proceso o usuario. Para evitar sobrescribir cambios ajenos, tu guardado ha sido detenido.",
        conflictModalMemo: "Tus borradores locales se conservan en memoria. Puedes recargar la versión actual del servidor para actualizar la base manteniendo tus cambios pendientes.",
        conflictModalClose: "Cerrar y conservar borrador",
        conflictModalReload: "Recargar del servidor",
        toastSaved: "Configuración de IA de bots guardada exitosamente en el servidor.",
        toastDiscarded: "Borrador descartado.",
        toastSaveFailed: "Error al guardar el perfil: {err}",
        loading: "Cargando perfiles de IA de bots desde el servidor...",
        booting: "Iniciando editor de IA de bots...",
        retryBtn: "Reintentar carga",
        paramIdPrefix: "Identificador de parámetro: ",
        nativeLowHpNotice: "Umbral huida vida baja (constante nativa 35%)",
        nativeLowHpExplanation: "Límite nativo: 35% de HP para retirada (constante del cliente)",
        paramHelp: {
          motAttack: "¿Cuánto prioriza buscar y combatir rivales?",
          motDefense: "¿Cuánto prioriza replegarse o vigilar áreas?",
          motScore: "¿Cuánto prioriza recolectar cristales o avanzar con el balón?",
          dashAttack: "Segundos entre impulsos ofensivos (menor = más frecuente).",
          dashDefense: "Segundos entre impulsos defensivos.",
          dashScore: "Segundos entre impulsos hacia cristales o balón.",
          skillAttack: "Enfriamiento entre usos de habilidades ofensivas.",
          skillHeal: "Enfriamiento entre usos de curaciones.",
          skillBuff: "Enfriamiento entre potenciadores o mejoras.",
          skillTrap: "Enfriamiento entre colocación de trampas.",
          skillEscape: "Enfriamiento entre habilidades de escape o retirada.",
          lowHpRetreat: "% de vida bajo el cual el bot intentará huir o curarse.",
          crystalDeposit: "Cantidad de cristales requeridos para ir al guardián.",
          crystalGreed: "Cristales adicionales a recolectar antes de depositar.",
          engagementDist: "Distancia en metros para iniciar combate."
        },
        paramLabels: {
          motAttack: "⚔️ Agresividad en combate (Ataque)",
          motDefense: "🛡️ Instinto defensivo (Defensa)",
          motScore: "💎 Enfoque en objetivo (Puntuación)",
          dashAttack: "Frecuencia impulso dash en ataque",
          dashDefense: "Frecuencia impulso dash en defensa",
          dashScore: "Frecuencia impulso dash hacia objetivo",
          skillAttack: "Intervalo habilidad de ataque",
          skillHeal: "Intervalo habilidad curativa",
          skillBuff: "Intervalo habilidad de mejora (Buff)",
          skillTrap: "Intervalo habilidad de trampa",
          skillEscape: "Intervalo habilidad de escape",
          lowHpRetreat: "Umbral retirada por salud baja",
          crystalDeposit: "Cristales para depositar",
          crystalGreed: "Avaricia de cristales extra",
          engagementDist: "Distancia de enganche de combate",
          lockon: "Permitir fijar blanco"
        },
        behaviors: {
          moreFight: "Más prioridad a pelear",
          lessFight: "Menos prioridad a pelear",
          moreDefend: "Más prioridad a defender",
          lessDefend: "Menos prioridad a defender",
          moreCollect: "Más prioridad a recoger",
          lessCollect: "Menos prioridad a recoger",
          dashMoreFreq: "Impulsos más frecuentes",
          dashLessFreq: "Impulsos menos frecuentes",
          dashMoreFreqCombat: "Impulsos más frecuentes en combate",
          dashLessFreqCombat: "Impulsos menos frecuentes en combate",
          dashMoreFreqDefend: "Impulsos más frecuentes al defender",
          dashLessFreqDefend: "Impulsos menos frecuentes al defender",
          dashMoreFreqCollect: "Impulsos más frecuentes al recoger",
          dashLessFreqCollect: "Impulsos menos frecuentes al recoger",
          skillFaster: "Usa habilidades más seguido",
          skillSlower: "Usa habilidades con más calma",
          retreatFaster: "Se retira antes al recibir daño",
          retreatLater: "Aguanta más antes de huir",
          depositFaster: "Deposita cristales más rápido",
          depositLater: "Acumula más cristales antes de depositar",
          chaseDistant: "Persigue rivales desde más lejos",
          chaseCloser: "Pelea solo a corta distancia",
          skillAttackMoreFreq: "Habilidades de ataque más frecuentes",
          skillAttackLessFreq: "Habilidades de ataque menos frecuentes",
          skillHealMoreFreq: "Habilidades de curación más frecuentes",
          skillHealLessFreq: "Habilidades de curación menos frecuentes",
          skillBuffMoreFreq: "Habilidades de mejora más frecuentes",
          skillBuffLessFreq: "Habilidades de mejora menos frecuentes",
          skillTrapMoreFreq: "Habilidades de trampa más frecuentes",
          skillTrapLessFreq: "Habilidades de trampa menos frecuentes",
          skillWarpMoreFreq: "Habilidades de teletransporte más frecuentes",
          skillWarpLessFreq: "Habilidades de teletransporte menos frecuentes",
          lockonEnabled: "Fijar blanco activado",
          lockonDisabled: "Fijar blanco desactivado",
          healPreventive: "Curación más preventiva (se cura con menos daño)",
          healGreedy: "Espera a recibir más daño para curarse",
          depositMore: "Acumula más cristales antes de depositar",
          depositLess: "Deposita cristales con mayor frecuencia"
        },
        intentChipPrefix: "Intención: ",
        intentSubtitle: "Selecciona una intención para aplicar una recomendación rápida y editable:",
        sharedDetailsSummary: "¿Por qué afecta a varios kickers?",
        sharedDetailsDesc: "En KickFlight, los kickers de este rol comparten la misma plantilla de IA base. Cualquier ajuste realizado aquí se aplicará a todos ellos al jugar con bots.",
        affectsPrefix: "Afecta a: ",
        exclusivePrefix: "Personaje exclusivo: ",
        editingPrefix: "Editando perfil: ",
        draftModifiedBadge: "Borrador modificado",
        profileSelectLabel: "Perfil a editar:",
        profileSelectAria: "Seleccionar perfil de bot",
        sidebarProfilesCount: "{count} perfiles ({mode})",
        sidebarModifiedSuffix: " · {count} modificado(s)",
        sidebarDraftDotTitle: "Borrador con cambios sin guardar",
        sidebarLoading: "Cargando perfiles...",
        sidebarError: "Error al cargar",
        diffTableParam: "Parámetro",
        diffTableOld: "Valor anterior",
        diffTableNew: "Nuevo valor",
        lockonHelp: "Habilita si el bot puede fijar la mira automáticamente en rivales.",
        lowHpTitle: "Umbral de vida para huir / depositar",
        lowHpVal: "35% (Fijo nativo)",
        lowHpHelp: "⚠️ Límite nativo: El ejecutable cliente de KickFlight tiene este valor compilado estrictamente al 35%. No es modificable desde el servidor porque el cliente ignora valores distintos.",
        applyDisclosureSummary: "ℹ️ ¿Cómo se aplican estos cambios?",
        applyDisclosureP1: "Los cambios se guardan directamente en el servidor y se aplican a todas las nuevas partidas creadas a partir de ese momento.",
        applyDisclosureP2: "El cliente del juego descarga automáticamente los parámetros de IA actualizados al iniciar partida. No es necesario reiniciar el servidor.",
        advSummary: "⚙️ Parámetros técnicos nativos",
        advParamId: "Identificador de parámetro: ",
        advKickerId: "Kicker ID: ",
        advCostumeId: "Disfraz ID: ",
        advRank: "Rango: ",
        advLowHpThreshold: "Umbral huida vida baja: ",
        botAiNotFound: "No se encontró el perfil seleccionado en este modo."
      },
      latestInformation: {
        title: "Última información",
        meta: "Edita la página de avisos que se abre en Kick-Flight desde Avisos → Última información.",
        announcementSection: "Página de anuncio",
        savedAt: "Guardado {time}",
        modeSection: "Modo de anuncio",
        modeText: "Texto sin formato",
        modeHtml: "HTML enriquecido",
        modeUrl: "URL externa",
        pageTitleLabel: "Título del anuncio (opcional)",
        pageTitlePlaceholder: "ej. ¡Evento de fin de semana!",
        bodyLabel: "Contenido del aviso",
        bodyPlaceholder: "Escribe aquí el texto que verán los jugadores al entrar a Noticias...",
        urlLabel: "URL externa (HTTP o HTTPS)",
        urlPlaceholder: "https://ejemplo.com/noticias",
        counterText: "{chars} caracteres (máx. {max})",
        counterHtml: "{bytes} bytes (máx. {max})",
        previewTitle: "Vista previa en vivo",
        saveBtn: "Guardar anuncio",
        savingBtn: "Guardando anuncio...",
        saveSuccess: "Anuncio guardado correctamente.",
        saveFailed: "Error al guardar anuncio: {err}",
        loadFailed: "No se pudo cargar la página actual: {err}",
        sidebarDesc: "Avisos que se muestran en Última información dentro del juego",
        savingPage: "guardando página ...",
        pageSaved: "página guardada; aparecerá la próxima vez que se abra Última información",
        failedStatus: "error: {err}"
      },
      health: {
        title: "Salud",
        meta: "Telemetría en bucle local del servidor de juego (/admin/ops/*): resultados de partidas, excepciones de clientes y desglose de combates",
        windowDeploy: "Esta versión",
        window24h: "Últimas 24 h",
        window7d: "Últimos 7 días",
        summarySection: "Resumen operativo",
        battles: "Partidas",
        exceptions: "Excepciones",
        rate: "Tasa de error",
        drilldownSection: "Detalle de combate",
        sidebarCount: "Resultados, excepciones de clientes y desglose",
        sidebarProblems: "{count} problema(s) en la ventana seleccionada",
        errorPrefix: "no se pudo leer la telemetría: ",
        loadingText: "cargando telemetría...",
        battleIdPlaceholder: "ID de combate (ej. 20261020-123456-abc)",
        inspectBtn: "Inspeccionar combate",
        thFingerprint: "Huella (Fingerprint)",
        thCount: "Ocurrencias",
        thFirstSeen: "Primera vez",
        thLastSeen: "Última vez",
        statusServer: "Servidor API",
        statusPhoton: "Servidor Photon",
        statusFixtures: "Datos cargados",
        statusUptime: "Tiempo activo",
        noExceptions: "Sin excepciones registradas en este período.",
        inspectLoading: "Cargando información del combate...",
        sidebarDesc: "Resultados, excepciones de clientes y desglose de combates",
        matchOutcomes: "Resultados de partidas",
        playerStatuses: "Estados de jugadores",
        clientExceptions: "Excepciones de clientes por huella",
        problemMatches: "Partidas con problemas",
        telemetryWriter: "Escritor de telemetría",
        howItReaches: "Cómo llega a la API",
        drillDownBtn: "inspeccionar",
        openById: "O abrir una partida por ID",
        openBtn: "Abrir",
        loadingTelemetry: "cargando telemetría...",
        couldNotReadTelemetry: "no se pudo leer la telemetría: {err}",
        noExceptionsInWindow: "sin reportes de error en esta ventana",
        noProblemMatchesInWindow: "sin partidas con problemas en esta ventana",
        thKind: "Tipo",
        thCount: "Total",
        thFingerprintSignature: "Huella / firma",
        thUsers: "Usuarios",
        thAppVersions: "Versiones",
        thLast: "Última",
        thBattle: "Partida",
        thOutcome: "Resultado",
        thRoster: "Plantilla",
        thType: "Tipo",
        thParticipants: "Participantes",
        outcomes: {
          clean: "limpia",
          partial: "parcial",
          abandoned: "abandonada",
          neverStarted: "nunca iniciada",
          running: "en curso",
          matching: "emparejando"
        },
        statuses: {
          completed: "completado",
          reconnected: "reconectado",
          reconnectFailed: "reconexión fallida",
          droppedSilent: "desconexión silenciosa",
          relaunched: "reiniciado",
          matchingFailures: "fallos de emparejamiento",
          matchingTimeouts: "tiempos agotados",
          cancelled: "cancelado",
          exceptions: "excepciones"
        },
        metaWindow: "ventana", metaSince: "desde", metaRelease: "versión", metaRun: "ejecución", metaStore: "almacén",
        metaQueue: "cola", metaWritten: "escritos", metaDropped: "descartados", metaErrors: "errores", metaTrackedBattles: "combates seguidos",
        lastError: "último error", outcomeOpen: "abierta", rule: "regla", field: "escenario", humans: "humanos", bots: "bots", created: "creada",
        statusOpen: "abierta", statusDroppedSilent: "desconexión silenciosa", outcomeClean: "limpia", outcomePartial: "parcial", outcomeAbandoned: "abandonada", outcomeNeverStarted: "nunca iniciada", outcomeRunning: "en curso", outcomeMatching: "emparejando",
        exceptionsError: "no se pudieron leer las excepciones: {err}", custom: "personalizada"
      },
      maintenance: {
        title: "Mantenimiento",
        meta: "Control de mantenimiento en bucle local del servidor (/admin/maintenance): inactivo / advertencia / 503 total",
        currentState: "Estado actual",
        sidebarCount: "Modo de mantenimiento del servidor y aviso",
        errorPrefix: "no se pudo leer el estado: ",
        savingPrefix: "estableciendo mantenimiento a {mode} ...",
        savedPrefix: "el mantenimiento ahora está en {mode}",
        failedPrefix: "falló: {err}",
        modeOff: "Inactivo (Servidor abierto a todos)",
        modeWarning: "Aviso previo (Acceso permitido con advertencia)",
        modeHard: "Mantenimiento total (Acceso bloqueado con 503)",
        startsAtLabel: "Inicio programado (ISO 8601 UTC)",
        endsAtLabel: "Fin programado (ISO 8601 UTC)",
        noticeLabel: "Mensaje de aviso / mantenimiento",
        applyBtn: "Actualizar mantenimiento",
        applyingBtn: "Actualizando...",
        statusSuccess: "Mantenimiento actualizado.",
        statusFailed: "Error al actualizar mantenimiento: {err}",
        readFailed: "No se pudo leer el estado: {err}",
        sidebarDesc: "Modo de mantenimiento del servidor y texto de aviso",
        settingMaintenance: "estableciendo mantenimiento a {mode} ...",
        maintenanceIsNow: "el mantenimiento ahora es {mode}",
        failedStatus: "error: {err}"
      },
      clientUpdate: {
        title: "Actualización de cliente",
        meta: "Control del popup de actualización forzosa independientemente para Android e iOS.",
        currentState: "Estado actual en el servidor",
        readFailed: "No se pudo leer el estado: {err}",
        effectiveState: "Estado efectivo: {state}",
        androidGate: "Android",
        iosGate: "iOS",
        minVersionLabel: "Versión mínima requerida",
        currentVersionLabel: "Versión actual de despliegue",
        forceUpdateLabel: "Exigir actualización forzosa",
        storeUrlLabel: "URL de descarga / tienda",
        restoreDefaults: "Restablecer valores de despliegue",
        saveBtn: "Guardar configuración de actualización",
        savingBtn: "Guardando...",
        saveSuccess: "Configuración de actualización guardada.",
        saveFailed: "Error al guardar configuración: {err}",
        sidebarDesc: "Control de versión mínima de cliente por plataforma",
        settingsSaved: "configuración de actualización guardada y persistida",
        failedStatus: "error: {err}"
      },
      apiErrors: {
        unauthorized: "Sesión expirada o no autorizada. Por favor inicia sesión de nuevo.",
        forbidden: "Acceso denegado (origen o credenciales no válidas).",
        notFound: "Recurso no encontrado.",
        methodNotAllowed: "Método no permitido.",
        conflict: "Conflicto de concurrencia: el archivo fue modificado por otro proceso o usuario.",
        internalError: "Error interno del servidor.",
        networkError: "Error de conexión con el servidor.",
        invalidCredentials: "Usuario o contraseña incorrectos.",
        badRequest: "Solicitud no válida o datos mal formateados.",
        tooManyRequests: "Inicio de sesión temporalmente no disponible por exceso de intentos. Intenta más tarde.",
        serviceUnavailable: "Servicio no disponible temporalmente.",
        unsupportedMediaType: "Tipo de medio no compatible."
      }
    },
    en: {
      common: {
        appName: "Kick-Flight Balance",
        loading: "Loading…",
        close: "Close",
        cancel: "Cancel",
        save: "Save",
        saving: "Saving…",
        revert: "Revert",
        reverting: "Reverting…",
        apply: "Restart API to apply",
        applying: "Restarting API…",
        export: "Export overrides",
        retry: "Retry",
        error: "Error",
        warning: "Warning",
        success: "Success",
        active: "Active",
        inactive: "Inactive",
        empty: "No items",
        noResults: "No matching items found",
        language: "Language",
        spanish: "Español",
        english: "English",
        secondsUnit: "s",
        metersUnit: "m",
        percentUnit: "%",
        yes: "Yes",
        no: "No",
        screensCount: "{shown} of {total} screens",
        itemsCount: "{shown} of {total} shown"
      },
      header: {
        title: "Kick-Flight Balance",
        topHintMasters: "saves write <code>.local/masters-overrides</code> (never <code>config/</code>) &mdash; restart the API to load them",
        topHintBotAi: "Saved on the server for future matches.",
        currentUser: "User: {user}",
        logout: "Log out",
        langLabel: "Language",
        langSelectAria: "Select language"
      },
      tabs: {
        kickers: "Kickers",
        discs: "Discs",
        text: "Text",
        botAi: "Bot AI",
        latestInformation: "Latest information",
        health: "Health",
        maintenance: "Maintenance",
        clientUpdate: "Client update"
      },
      filters: {
        searchPlaceholder: "Filter by name or id",
        searchAria: "Filter list by name or id",
        allRarities: "All rarities",
        allTypes: "All types",
        rarityNormal: "Normal",
        rarityRare: "Rare",
        raritySuperRare: "Super Rare",
        rarityUltraRare: "Ultra Rare"
      },
      footer: {
        unsavedChanges: "Unsaved changes: {count}",
        save: "Save",
        revert: "Revert",
        apply: "Restart API to apply",
        export: "Export overrides",
        note: "saves write the override dir; restart the API to load them",
        savedSuccess: "Saved successfully.",
        saveFailed: "Save failed: {err}",
        revertedSuccess: "Overrides reverted.",
        revertFailed: "Revert failed: {err}",
        appliedSuccess: "API restarted successfully.",
        applyFailed: "Restart failed: {err}",
        applyPending: "There are pending overrides to apply.",
        applyUpToDate: "All overrides are applied and active.",
        noTablesToSave: "Nothing to save",
        savingTable: "Saving masters_{table}.json ...",
        savedTableSuccess: "ok wrote override masters_{table}.json ({rows} rows)",
        savedTableError: "fail masters_{table}.json: {err}",
        savedToOverrideDir: "Saved to the override dir - click \"Restart API to apply\" to load them",
        revertingMasters: "Reloading the masters ...",
        revertedMastersSuccess: "Masters reloaded",
        revertedMastersError: "Reload failed: {err}",
        confirmRestart: "Restart the API to load the saved overrides?\n\nLive matches are dropped: players in a match disconnect or lose the battle state.\n\n{pending}Command:\n{cmd}",
        pendingOverrides: "Pending override(s): {list}\n\n",
        restartingApi: "Restarting the API: {cmd}",
        restartedApiSuccess: "API restarted and healthy after {seconds}s ({probes} probe(s))",
        stillPendingOverrides: "Still pending: {list}",
        noPendingOverrides: "No pending overrides: the API has loaded every override"
      },
      login: {
        title: "Sign in · Kick-Flight Balance",
        heading: "Kick-Flight Balance",
        langLabel: "Language",
        usernameLabel: "Username",
        usernamePlaceholder: "Username",
        passwordLabel: "Password",
        passwordPlaceholder: "Password",
        submit: "Sign in",
        submitting: "Signing in…",
        failed: "Sign in failed",
        locked: "Account locked or invalid credentials"
      },
      kickerPanel: {
        stats: "Stats",
        statsEmpty: "no kicker_parameter row for this kicker",
        passive: "Passive ability",
        passiveEmpty: "no kicker_ability row for this kicker",
        skill: "Kicker Skill (KS)",
        skillEmpty: "no skill row for this kicker",
        special: "Special Skill (SP)",
        specialEmpty: "no special_skill row for this kicker",
        attacks: "Basic attacks",
        attacksSub: "Hits, collisions and bullets",
        attacksEmpty: "no weapon attacks configured",
        addRow: "+ Add row",
        addRowTitle: "Add a row to masters_{table}.json for {link} {val}",
        deleteRow: "Delete row",
        deleteRowTitle: "Delete this row",
        revertTable: "Revert table",
        overridden: "overridden"
      },
      discPanel: {
        guide: "Field names are shown in English and Spanish; raw master keys remain unchanged. Open ? with a click, tap, or keyboard to read what the value affects. Damage multipliers are per hit; the animation sets the hit count.",
        parameters: "Disc parameters",
        skill: "Disc skill",
        effects: "Effects and conditions",
        effectsEmpty: "no extra effect rows",
        condition: "Condition",
        trap: "Trap",
        heal: "Heal",
        blowOff: "Blow off",
        pullIn: "Pull in",
        collision: "Collision",
        hit: "Hit"
      },
      textPanel: {
        title: "Client UI Text (masters_translation.json)",
        screenLabel: "Screen:",
        otherScreen: "(keys used from code / enums)",
        searchPlaceholder: "Filter by key or text…",
        keyCol: "Key",
        valCol: "Screen text",
        wireframeToggle: "Show wireframe",
        otherScreenDesc: "translation rows that no UI prefab references directly: enum names and texts looked up by key",
        screenMeta: "{count} localized texts · masters_translation.json",
        boxesLegend: "boxes: text element",
        noTranslation: "no translation (blank in game)",
        outsideCanvas: "outside the canvas (scroll content / layout group)",
        noRowInMasters: "no row in masters_translation.json (blank in game) - run scripts/generate_translations.py"
      },
      botAi: {
        heading: "What do you want it to do?",
        subheading: "Choose the primary bot behavior or fine-tune individual parameters below.",
        modeLabel: "Game mode",
        rosterTitle: "Kickers in this profile",
        affectsLabel: "Affects: {names}",
        kickerSelectLabel: "Kicker to edit:",
        editingKicker: "Editing kicker: {name}",
        kickerListCount: "14 kickers ({mode})",
        dirtyBadge: "Draft with unsaved changes",
        dirtyModified: "modified",
        intentChipLabel: "Intent: ",
        intentActiveBadge: "✓ Active",
        behaviorSelected: "Selected behavior:",
        advancedToggle: "🛠️ Advanced behavior settings (optional)",
        discardDraft: "Discard draft",
        reviewSave: "Review & Save",
        saveDirect: "Save changes",
        saveBtn: "Save changes",
        reviewModalHead: "📋 Save changes to server",
        reviewModalNotice: "Review changes. Once saved, they apply on the server for future matches.",
        reviewModalConfirm: "Save to server",
        reviewModalCancel: "Keep editing",
        reviewModalDiffDetails: "View numeric parameter details",
        conflictModalHead: "⚠️ Concurrency Conflict (409)",
        conflictModalNotice: "The configuration file was modified externally on disk by another process or user. To prevent overwriting external changes, your save was halted.",
        conflictModalMemo: "Your local drafts are preserved in memory. You can reload the current server version to update the base while keeping your pending changes.",
        conflictModalClose: "Close & keep draft",
        conflictModalReload: "Reload from server",
        toastSaved: "Bot AI configuration saved successfully to server.",
        toastDiscarded: "Draft discarded.",
        toastSaveFailed: "Failed to save profile: {err}",
        loading: "Loading bot AI profiles from server...",
        booting: "Initializing bot AI editor...",
        retryBtn: "Retry loading",
        paramIdPrefix: "Parameter identifier: ",
        nativeLowHpNotice: "Low HP retreat threshold (native 35% constant)",
        nativeLowHpExplanation: "Native constraint: 35% HP retreat threshold (client constant)",
        paramHelp: {
          motAttack: "How much does the bot prioritize seeking and fighting rivals?",
          motDefense: "How much does the bot prioritize retreating or guarding areas?",
          motScore: "How much does the bot prioritize collecting crystals or moving with the ball?",
          dashAttack: "Seconds between offensive dashes (lower = more frequent).",
          dashDefense: "Seconds between defensive dashes.",
          dashScore: "Seconds between dashes toward crystals or ball.",
          skillAttack: "Cooldown between offensive skill uses.",
          skillHeal: "Cooldown between heal skill uses.",
          skillBuff: "Cooldown between buff skill uses.",
          skillTrap: "Cooldown between trap placement uses.",
          skillEscape: "Cooldown between escape skill uses.",
          lowHpRetreat: "% of HP below which the bot attempts to retreat or heal.",
          crystalDeposit: "Number of crystals required before moving to guardian.",
          crystalGreed: "Extra crystals to collect before depositing.",
          engagementDist: "Distance in meters to engage targets."
        },
        paramLabels: {
          motAttack: "⚔️ Combat Aggression (Attack)",
          motDefense: "🛡️ Defensive Instinct (Defense)",
          motScore: "💎 Objective Focus (Score)",
          dashAttack: "Dash interval in attack",
          dashDefense: "Dash interval in defense",
          dashScore: "Dash interval to objective",
          skillAttack: "Attack skill interval",
          skillHeal: "Heal skill interval",
          skillBuff: "Buff skill interval",
          skillTrap: "Trap skill interval",
          skillEscape: "Escape skill interval",
          lowHpRetreat: "Low HP retreat threshold",
          crystalDeposit: "Crystals to deposit",
          crystalGreed: "Extra crystal greed",
          engagementDist: "Combat engagement distance",
          lockon: "Allow target lock-on"
        },
        behaviors: {
          moreFight: "Higher priority to fight",
          lessFight: "Lower priority to fight",
          moreDefend: "Higher priority to defend",
          lessDefend: "Lower priority to defend",
          moreCollect: "Higher priority to collect",
          lessCollect: "Lower priority to collect",
          dashMoreFreq: "More frequent dashes",
          dashLessFreq: "Less frequent dashes",
          dashMoreFreqCombat: "More frequent dashes in combat",
          dashLessFreqCombat: "Less frequent dashes in combat",
          dashMoreFreqDefend: "More frequent dashes while defending",
          dashLessFreqDefend: "Less frequent dashes while defending",
          dashMoreFreqCollect: "More frequent dashes while collecting",
          dashLessFreqCollect: "Less frequent dashes while collecting",
          skillFaster: "Uses skills more frequently",
          skillSlower: "Uses skills less frequently",
          retreatFaster: "Retreats sooner when taking damage",
          retreatLater: "Stays in combat longer before retreating",
          depositFaster: "Deposits crystals sooner",
          depositLater: "Hoards more crystals before depositing",
          chaseDistant: "Chases rivals from farther away",
          chaseCloser: "Only fights at close range",
          skillAttackMoreFreq: "More frequent attack skills",
          skillAttackLessFreq: "Less frequent attack skills",
          skillHealMoreFreq: "More frequent healing skills",
          skillHealLessFreq: "Less frequent healing skills",
          skillBuffMoreFreq: "More frequent buff skills",
          skillBuffLessFreq: "Less frequent buff skills",
          skillTrapMoreFreq: "More frequent trap skills",
          skillTrapLessFreq: "Less frequent trap skills",
          skillWarpMoreFreq: "More frequent warp skills",
          skillWarpLessFreq: "Less frequent warp skills",
          lockonEnabled: "Target lock-on enabled",
          lockonDisabled: "Target lock-on disabled",
          healPreventive: "More preventive healing (heals with less damage taken)",
          healGreedy: "Waits to take more damage before healing",
          depositMore: "Hoards more crystals before depositing",
          depositLess: "Deposits crystals more frequently"
        },
        intentChipPrefix: "Intent: ",
        intentSubtitle: "Select an intent preset to apply a quick, editable recommendation:",
        sharedDetailsSummary: "Why does this affect multiple kickers?",
        sharedDetailsDesc: "In KickFlight, kickers with this role share the same base AI template. Any tuning done here applies to all of them in bot matches.",
        affectsPrefix: "Affects: ",
        exclusivePrefix: "Exclusive kicker: ",
        editingPrefix: "Editing profile: ",
        draftModifiedBadge: "Modified draft",
        profileSelectLabel: "Profile to edit:",
        profileSelectAria: "Select bot profile",
        sidebarProfilesCount: "{count} profiles ({mode})",
        sidebarModifiedSuffix: " · {count} modified",
        sidebarDraftDotTitle: "Draft with unsaved changes",
        sidebarLoading: "Loading profiles...",
        sidebarError: "Error loading",
        diffTableParam: "Parameter",
        diffTableOld: "Previous value",
        diffTableNew: "New value",
        lockonHelp: "Enables whether the bot can automatically lock aim on rivals.",
        lowHpTitle: "Low HP retreat / deposit threshold",
        lowHpVal: "35% (Native hardcoded)",
        lowHpHelp: "⚠️ Native limit: The KickFlight client binary has this value hardcoded to 35%. It cannot be altered by the server because the client ignores different values.",
        applyDisclosureSummary: "ℹ️ How do these changes apply?",
        applyDisclosureP1: "Changes are saved directly to the server and apply to all newly created matches from that moment on.",
        applyDisclosureP2: "The game client automatically downloads updated AI parameters at match startup. No server restart is required.",
        advSummary: "⚙️ Native technical parameters",
        advParamId: "Parameter identifier: ",
        advKickerId: "Kicker ID: ",
        advCostumeId: "Costume ID: ",
        advRank: "Rank: ",
        advLowHpThreshold: "Low HP retreat threshold: ",
        botAiNotFound: "Selected profile not found in this mode."
      },
      latestInformation: {
        title: "Latest information",
        meta: "Edit the announcement page opened in Kick-Flight from Notices → Latest information.",
        announcementSection: "Announcement page",
        savedAt: "Saved {time}",
        modeSection: "Announcement mode",
        modeText: "Plain text",
        modeHtml: "Rich HTML",
        modeUrl: "External URL",
        pageTitleLabel: "Announcement title (optional)",
        pageTitlePlaceholder: "e.g. Weekend Special Event!",
        bodyLabel: "Notice content",
        bodyPlaceholder: "Enter notice text displayed to players in the news screen...",
        urlLabel: "External URL (HTTP or HTTPS)",
        urlPlaceholder: "https://example.com/news",
        counterText: "{chars} characters (max {max})",
        counterHtml: "{bytes} bytes (max {max})",
        previewTitle: "Live preview",
        saveBtn: "Save announcement",
        savingBtn: "Saving announcement...",
        saveSuccess: "Announcement saved successfully.",
        saveFailed: "Failed to save announcement: {err}",
        loadFailed: "Could not load current announcement: {err}",
        sidebarDesc: "Announcements shown in the in-game Latest Information screen",
        savingPage: "saving page ...",
        pageSaved: "page saved; it appears the next time Latest information is opened",
        failedStatus: "failed: {err}"
      },
      health: {
        title: "Health",
        meta: "the game server's loopback-only /admin/ops/* telemetry: match outcomes, client exceptions and a battle drill-down",
        windowDeploy: "This release",
        window24h: "Last 24 h",
        window7d: "Last 7 days",
        summarySection: "Operational summary",
        battles: "Battles",
        exceptions: "Exceptions",
        rate: "Error rate",
        drilldownSection: "Battle drill-down",
        sidebarCount: "Outcomes, client exceptions and a battle drill-down",
        sidebarProblems: "{count} problem match(es) in the selected window",
        errorPrefix: "could not read telemetry: ",
        loadingText: "loading telemetry...",
        battleIdPlaceholder: "Battle ID (e.g. 20261020-123456-abc)",
        inspectBtn: "Inspect battle",
        thFingerprint: "Fingerprint",
        thCount: "Occurrences",
        thFirstSeen: "First seen",
        thLastSeen: "Last seen",
        statusServer: "API Server",
        statusPhoton: "Photon Server",
        statusFixtures: "Fixtures loaded",
        statusUptime: "Uptime",
        noExceptions: "No exceptions recorded in this window.",
        inspectLoading: "Loading battle details...",
        sidebarDesc: "Outcomes, client exceptions and a battle drill-down",
        matchOutcomes: "Match outcomes",
        playerStatuses: "Player statuses",
        clientExceptions: "Client exceptions by fingerprint",
        problemMatches: "Problem matches",
        telemetryWriter: "Telemetry writer",
        howItReaches: "How it reaches the API",
        drillDownBtn: "drill down",
        openById: "Or open a battle by id",
        openBtn: "Open",
        loadingTelemetry: "loading telemetry...",
        couldNotReadTelemetry: "could not read telemetry: {err}",
        noExceptionsInWindow: "no client error reports in this window",
        noProblemMatchesInWindow: "no problem matches in this window",
        thKind: "Kind",
        thCount: "Count",
        thFingerprintSignature: "Fingerprint / signature",
        thUsers: "Users",
        thAppVersions: "App versions",
        thLast: "Last",
        thBattle: "Battle",
        thOutcome: "Outcome",
        thRoster: "Roster",
        thType: "Type",
        thParticipants: "Participants",
        outcomes: {
          clean: "clean",
          partial: "partial",
          abandoned: "abandoned",
          neverStarted: "never started",
          running: "running",
          matching: "matching"
        },
        statuses: {
          completed: "completed",
          reconnected: "reconnected",
          reconnectFailed: "reconnect failed",
          droppedSilent: "dropped silent",
          relaunched: "relaunched",
          matchingFailures: "matching failures",
          matchingTimeouts: "matching timeouts",
          cancelled: "cancelled",
          exceptions: "exceptions"
        },
        metaWindow: "window", metaSince: "since", metaRelease: "release", metaRun: "run", metaStore: "store",
        metaQueue: "queue", metaWritten: "written", metaDropped: "dropped", metaErrors: "errors", metaTrackedBattles: "tracked battles",
        lastError: "last error", outcomeOpen: "open", rule: "rule", field: "field", humans: "humans", bots: "bots", created: "created",
        statusOpen: "open", statusDroppedSilent: "dropped silent", outcomeClean: "clean", outcomePartial: "partial", outcomeAbandoned: "abandoned", outcomeNeverStarted: "never started", outcomeRunning: "running", outcomeMatching: "matching",
        exceptionsError: "could not read exceptions: {err}", custom: "custom"
      },
      maintenance: {
        title: "Maintenance",
        meta: "the game server's loopback-only /admin/maintenance toggle: off / soft notice / hard 503",
        currentState: "Current state",
        sidebarCount: "Server maintenance mode and notice text",
        errorPrefix: "could not read the state: ",
        savingPrefix: "setting maintenance to {mode} ...",
        savedPrefix: "maintenance is now {mode}",
        failedPrefix: "failed: {err}",
        modeOff: "Off (Server open to everyone)",
        modeWarning: "Soft notice (Access allowed with notice)",
        modeHard: "Hard 503 (Access blocked)",
        startsAtLabel: "Scheduled start (ISO 8601 UTC)",
        endsAtLabel: "Scheduled end (ISO 8601 UTC)",
        noticeLabel: "Notice / maintenance message",
        applyBtn: "Update maintenance",
        applyingBtn: "Updating...",
        statusSuccess: "Maintenance updated.",
        statusFailed: "Failed to update maintenance: {err}",
        readFailed: "Could not read state: {err}",
        sidebarDesc: "Server maintenance mode and notice text",
        settingMaintenance: "setting maintenance to {mode} ...",
        maintenanceIsNow: "maintenance is now {mode}",
        failedStatus: "failed: {err}"
      },
      clientUpdate: {
        title: "Client update",
        meta: "Control the existing forced-update popup independently for Android and iOS.",
        currentState: "Current server state",
        readFailed: "Could not read state: {err}",
        effectiveState: "Effective state: {state}",
        androidGate: "Android",
        iosGate: "iOS",
        minVersionLabel: "Minimum required version",
        currentVersionLabel: "Current deployed version",
        forceUpdateLabel: "Force client update",
        storeUrlLabel: "Store / download URL",
        restoreDefaults: "Restore deployment defaults",
        saveBtn: "Save update settings",
        savingBtn: "Saving...",
        saveSuccess: "Update settings saved.",
        saveFailed: "Failed to save update settings: {err}",
        sidebarDesc: "Forced client update gate by platform",
        settingsSaved: "client update settings saved and persisted",
        failedStatus: "failed: {err}"
      },
      apiErrors: {
        unauthorized: "Session expired or unauthorized. Please sign in again.",
        forbidden: "Access denied (invalid origin or credentials).",
        notFound: "Resource not found.",
        methodNotAllowed: "Method not allowed.",
        conflict: "Concurrency conflict: configuration modified by another process or user.",
        internalError: "Internal server error.",
        networkError: "Network connection error.",
        invalidCredentials: "Sign in failed",
        badRequest: "Bad request or invalid parameters.",
        tooManyRequests: "Sign in temporarily unavailable due to too many attempts. Please try again later.",
        serviceUnavailable: "Service temporarily unavailable.",
        unsupportedMediaType: "Unsupported media type."
      }
    }
  };

  // Bot AI data localization structures
  const BOT_AI_LOCALIZATION = {
    modes: {
      CrystalScramble: {
        es: { name: "Cristales", icon: "💎", fullName: "Batalla Scramble", desc: "Recolección y depósito de cristales en el guardián." },
        en: { name: "Crystals", icon: "💎", fullName: "Scramble Battle", desc: "Collect and deposit crystals into the guardian." }
      },
      FlagFlight: {
        es: { name: "Bandera", icon: "🚩", fullName: "Vuelo de Bandera", desc: "Captura y defensa de bandera. Sin guardianes ni depósitos." },
        en: { name: "Flag", icon: "🚩", fullName: "Flag Flight", desc: "Capture and defend flags. No guardians or deposits." }
      },
      RapidBall: {
        es: { name: "Balón", icon: "⚽", fullName: "Balón Rápido", desc: "Control del balón y anotación en portería rival." },
        en: { name: "Ball", icon: "⚽", fullName: "Rapid Ball", desc: "Ball control and scoring into rival goal." }
      }
    },
    presets: {
      run_score: {
        es: {
          title: "Quiero que corra y puntúe",
          behaviorDesc: "El bot prioriza recolectar y anotar con impulsos rápidos continuos.",
          summaries: {
            CrystalScramble: "Prioriza recolectar y depositar cristales rápidamente.",
            FlagFlight: "Prioriza capturar la bandera y llevarla a la meta.",
            RapidBall: "Prioriza tomar el balón y avanzar hacia la portería rival."
          },
          nativeLimit: "No altera la velocidad base del kicker."
        },
        en: {
          title: "Score & Run",
          behaviorDesc: "The bot prioritizes collecting and scoring with continuous fast dashes.",
          summaries: {
            CrystalScramble: "Prioritizes collecting and depositing crystals quickly.",
            FlagFlight: "Prioritizes capturing the flag and carrying it to the goal.",
            RapidBall: "Prioritizes taking the ball and advancing to the rival goal."
          },
          nativeLimit: "Does not alter kicker base speed."
        }
      },
      fight: {
        es: {
          title: "Quiero que busque pelea",
          behaviorDesc: "El bot persigue activamente a los rivales y gasta recursos en combate.",
          summaries: {
            CrystalScramble: "Ignora cristales para presionar y derribar oponentes.",
            FlagFlight: "Elimina a los portadores y defensores de bandera rivales.",
            RapidBall: "Ataca a los jugadores rivales para despejar la cancha."
          },
          nativeLimit: "No altera el daño base de armas."
        },
        en: {
          title: "Seek Combat & Brawl",
          behaviorDesc: "The bot actively chases rivals and commits combat resources.",
          summaries: {
            CrystalScramble: "Ignores crystals to pressure and take down opponents.",
            FlagFlight: "Eliminates rival flag carriers and defenders.",
            RapidBall: "Attacks rival players to clear the field."
          },
          nativeLimit: "Does not alter weapon base damage."
        }
      },
      defend_base: {
        es: {
          title: "Quiero que cuide la base",
          behaviorDesc: "El bot vigila el guardián, la bandera o la portería con cautela.",
          summaries: {
            CrystalScramble: "Permanece cerca del guardián para interceptar ladrones.",
            FlagFlight: "Vigila la bandera propia y bloquea avances enemigos.",
            RapidBall: "Protege la portería y despeja balones peligrosos."
          },
          nativeLimit: "No altera la vida máxima del personaje."
        },
        en: {
          title: "Guard Base & Defend",
          behaviorDesc: "The bot guards the guardian, flag, or goal with caution.",
          summaries: {
            CrystalScramble: "Stays near the guardian to intercept thieves.",
            FlagFlight: "Guards friendly flag and blocks enemy pushes.",
            RapidBall: "Protects the goal and clears dangerous balls."
          },
          nativeLimit: "Does not alter character maximum HP."
        }
      },
      support_team: {
        es: {
          title: "Quiero que apoye al equipo",
          behaviorDesc: "El bot asiste a sus compañeros con curaciones, escudos o control.",
          summaries: {
            CrystalScramble: "Escolta al transportador de cristales y lo mantiene vivo.",
            FlagFlight: "Cubre al portador de la bandera durante el trayecto.",
            RapidBall: "Da pases y genera espacio para los rematadores."
          },
          nativeLimit: "Respeta los tiempos de recarga nativos."
        },
        en: {
          title: "Support Team",
          behaviorDesc: "The bot assists teammates with heals, shields, or control.",
          summaries: {
            CrystalScramble: "Escorts the crystal carrier and keeps them alive.",
            FlagFlight: "Covers the flag carrier on their run.",
            RapidBall: "Passes the ball and creates space for finishers."
          },
          nativeLimit: "Respects native cooldown timings."
        }
      },
      balanced: {
        es: {
          title: "Quiero que sea equilibrado",
          behaviorDesc: "Comportamiento estándar equilibrado entre combate y objetivo.",
          summaries: {
            CrystalScramble: "Balancea recolección, apoyo y defensa de forma flexible.",
            FlagFlight: "Alterna ataque y defensa de bandera según la situación.",
            RapidBall: "Juega tácticamente entre recuperación y ataque con balón."
          },
          nativeLimit: "Valores por defecto del juego."
        },
        en: {
          title: "Balanced Play",
          behaviorDesc: "Standard balanced behavior between combat and objective.",
          summaries: {
            CrystalScramble: "Balances collecting, support, and defense flexibly.",
            FlagFlight: "Alternates flag attack and defense based on situation.",
            RapidBall: "Plays tactically between ball recovery and offensive drive."
          },
          nativeLimit: "Default game parameters."
        }
      }
    },
    roles: {
      carrier: {
        es: { title: "Transportador veloz", family: "Objetivo y Movilidad", desc: "Especialista en recolección rápida de cristales y movilidad ágil.", role: "Velocista (Carrier)" },
        en: { title: "Speed Carrier", family: "Objective & Mobility", desc: "Specialist in fast crystal collection and agile mobility.", role: "Speedster (Carrier)" }
      },
      tank_defender: {
        es: { title: "Defensor Tanque", family: "Defensa Pesada", desc: "Tanque robusto para control de zona, intercepción y aguante bajo presión.", role: "Tanque (Anchor)" },
        en: { title: "Tank Defender", family: "Heavy Defense", desc: "Robust tank for zone control, interception, and staying power under pressure.", role: "Tank (Anchor)" }
      },
      hunter: {
        es: { title: "Cazador / Asesino", family: "Combate y Bajas", desc: "Enfocado en perseguir rivales, ganar duelos y mantener presión ofensiva.", role: "Ataque (Hunter)" },
        en: { title: "Hunter / Assassin", family: "Combat & Takedowns", desc: "Focused on chasing opponents, winning duels, and offensive pressure.", role: "Attack (Hunter)" }
      },
      objective_attacker: {
        es: { title: "Atacante de Objetivos", family: "Presión Ofensiva", desc: "Presiona objetivos y asegura zonas clave (Kickers ofensivos genéricos).", role: "Presión general" },
        en: { title: "Objective Attacker", family: "Offensive Pressure", desc: "Pressures objectives and locks down key areas (Generic offensive kickers).", role: "General pressure" }
      },
      healer: {
        es: { title: "Sanador de Apoyo", family: "Soporte", desc: "Soporte con habilidades curativas genérico (Ruriha tiene perfil dedicado).", role: "Curación genérica" },
        en: { title: "Support Healer", family: "Support", desc: "Generic support with healing abilities (Ruriha has dedicated profile).", role: "Generic healing" }
      },
      speed_support: {
        es: { title: "Apoyo de Velocidad", family: "Soporte", desc: "Soporte enfocado en movilidad y aceleración (Grenhawk tiene perfil dedicado).", role: "Movilidad y aceleración" },
        en: { title: "Speed Support", family: "Support", desc: "Support focused on mobility and acceleration (Grenhawk has dedicated profile).", role: "Mobility & acceleration" }
      },
      damage_support: {
        es: { title: "Apoyo de Daño", family: "Soporte", desc: "Soporte auxiliar que combina daño a distancia y utilidad (Sid tiene perfil dedicado).", role: "Daño y control" },
        en: { title: "Damage Support", family: "Support", desc: "Auxiliary support combining ranged damage and utility (Sid has dedicated profile).", role: "Damage & utility" }
      },
      trap_defender: {
        es: { title: "Defensor con Trampas", family: "Defensa y Control", desc: "Zonificación y colocación de trampas defensivas (Owlbert tiene perfil dedicado).", role: "Trampas genéricas" },
        en: { title: "Trap Defender", family: "Defense & Control", desc: "Zoning and defensive trap placement (Owlbert has dedicated profile).", role: "Generic traps" }
      },
      Yuyan: {
        es: { title: "Especialista: Yuyan", family: "Especialización Única", desc: "Configuración dedicada exclusivamente a Yuyan como atacante de objetivos.", role: "Especialización exclusiva" },
        en: { title: "Specialist: Yuyan", family: "Unique Specialization", desc: "Dedicated configuration exclusively for Yuyan as an objective attacker.", role: "Exclusive specialization" }
      },
      Owlbert: {
        es: { title: "Especialista: Owlbert", family: "Especialización Única", desc: "Configuración dedicada exclusivamente a Owlbert como trampero táctico.", role: "Especialización exclusiva" },
        en: { title: "Specialist: Owlbert", family: "Unique Specialization", desc: "Dedicated configuration exclusively for Owlbert as a tactical trapper.", role: "Exclusive specialization" }
      },
      Ruriha: {
        es: { title: "Especialista: Ruriha", family: "Especialización Única", desc: "Configuración dedicada exclusivamente a Ruriha como sanadora de soporte.", role: "Especialización exclusiva" },
        en: { title: "Specialist: Ruriha", family: "Unique Specialization", desc: "Dedicated configuration exclusively for Ruriha as a support healer.", role: "Exclusive specialization" }
      },
      Grenhawk: {
        es: { title: "Especialista: Grenhawk", family: "Especialización Única", desc: "Configuración dedicada exclusivamente a Grenhawk como soporte veloz.", role: "Especialización exclusiva" },
        en: { title: "Specialist: Grenhawk", family: "Unique Specialization", desc: "Dedicated configuration exclusively for Grenhawk as a speed support.", role: "Exclusive specialization" }
      },
      Sid: {
        es: { title: "Especialista: Sid", family: "Especialización Única", desc: "Configuración dedicada exclusivamente a Sid como soporte ofensivo.", role: "Especialización exclusiva" },
        en: { title: "Specialist: Sid", family: "Unique Specialization", desc: "Dedicated configuration exclusively for Sid as an offensive support.", role: "Exclusive specialization" }
      }
    },
    kickers: {
      1: { name: "Tsubame", es: { role: "Velocista (Carrier)" }, en: { role: "Speedster (Carrier)" } },
      2: { name: "Ruriha", es: { role: "Soporte (Healer)" }, en: { role: "Support (Healer)" } },
      3: { name: "Coco", es: { role: "Tanque (Anchor)" }, en: { role: "Tank (Anchor)" } },
      4: { name: "Kite", es: { role: "Ataque (Hunter)" }, en: { role: "Attack (Hunter)" } },
      5: { name: "Owlbert", es: { role: "Soporte (Trampas)" }, en: { role: "Support (Traps)" } },
      6: { name: "Pitophy", es: { role: "Ataque (Hunter)" }, en: { role: "Attack (Hunter)" } },
      7: { name: "Grenhawk", es: { role: "Soporte (Velocidad)" }, en: { role: "Support (Speed)" } },
      8: { name: "Anna", es: { role: "Velocista (Carrier)" }, en: { role: "Speedster (Carrier)" } },
      9: { name: "Jay", es: { role: "Velocista (Carrier)" }, en: { role: "Speedster (Carrier)" } },
      10: { name: "Yuyan", es: { role: "Ataque (Disruptor)" }, en: { role: "Attack (Disruptor)" } },
      11: { name: "Diatrius", es: { role: "Tanque (Anchor)" }, en: { role: "Tank (Anchor)" } },
      12: { name: "Buzzy Big", es: { role: "Tanque (Anchor)" }, en: { role: "Tank (Anchor)" } },
      13: { name: "Hitagi", es: { role: "Ataque (Hunter)" }, en: { role: "Attack (Hunter)" } },
      14: { name: "Sid", es: { role: "Soporte (Daño)" }, en: { role: "Support (Damage)" } }
    }
  };

  function parseCookieLang() {
    try {
      if (typeof document === "undefined" || !document.cookie) return null;
      const match = document.cookie.match(new RegExp("(?:^|;\\s*)" + COOKIE_NAME + "=([^;]+)"));
      if (match && match[1]) {
        const val = decodeURIComponent(match[1]).trim().toLowerCase();
        if (SUPPORTED_LANGS.indexOf(val) !== -1) return val;
      }
    } catch (_) {}
    return null;
  }

  function parseStorageLang() {
    try {
      if (typeof window !== "undefined" && window.localStorage) {
        const val = (window.localStorage.getItem(STORAGE_KEY) || "").trim().toLowerCase();
        if (SUPPORTED_LANGS.indexOf(val) !== -1) return val;
      }
    } catch (_) {}
    return null;
  }

  function getStoredLang() {
    const fromCookie = parseCookieLang();
    if (fromCookie) return fromCookie;
    const fromStorage = parseStorageLang();
    if (fromStorage) return fromStorage;
    return DEFAULT_LANG;
  }

  let currentLang = getStoredLang();
  const listeners = [];

  function saveStoredLang(lang) {
    try {
      if (typeof window !== "undefined" && window.localStorage) {
        window.localStorage.setItem(STORAGE_KEY, lang);
      }
    } catch (_) {}

    try {
      if (typeof document !== "undefined") {
        document.cookie = COOKIE_NAME + "=" + encodeURIComponent(lang) + "; path=/; max-age=31536000; SameSite=Lax";
      }
    } catch (_) {}
  }

  function resolvePath(obj, path) {
    if (!obj || !path) return undefined;
    const parts = path.split(".");
    let curr = obj;
    for (let i = 0; i < parts.length; i++) {
      if (curr === null || curr === undefined || typeof curr !== "object") return undefined;
      curr = curr[parts[i]];
    }
    return curr;
  }

  const BalanceI18n = {
    DEFAULT_LANG: DEFAULT_LANG,
    SUPPORTED_LANGS: SUPPORTED_LANGS,
    dictionaries: DICTIONARIES,
    botAiLocalization: BOT_AI_LOCALIZATION,

    getLang: function () {
      return currentLang;
    },

    setLang: function (lang) {
      const normalized = (lang || "").trim().toLowerCase();
      const validLang = SUPPORTED_LANGS.indexOf(normalized) !== -1 ? normalized : DEFAULT_LANG;
      if (validLang === currentLang) {
        saveStoredLang(validLang);
        if (typeof document !== "undefined" && document.documentElement) {
          document.documentElement.lang = validLang;
        }
        return validLang;
      }
      currentLang = validLang;
      saveStoredLang(validLang);
      if (typeof document !== "undefined" && document.documentElement) {
        document.documentElement.lang = validLang;
      }
      for (let i = 0; i < listeners.length; i++) {
        try {
          listeners[i](validLang);
        } catch (e) {
          if (typeof console !== "undefined") console.error("i18n listener error:", e);
        }
      }
      return validLang;
    },

    onLangChange: function (callback) {
      if (typeof callback === "function" && listeners.indexOf(callback) === -1) {
        listeners.push(callback);
      }
    },

    offLangChange: function (callback) {
      const idx = listeners.indexOf(callback);
      if (idx !== -1) listeners.splice(idx, 1);
    },

    t: function (key, params) {
      const dict = DICTIONARIES[currentLang] || DICTIONARIES[DEFAULT_LANG];
      let val = resolvePath(dict, key);
      if (val === undefined || val === null) {
        const fallbackDict = DICTIONARIES[DEFAULT_LANG];
        val = resolvePath(fallbackDict, key);
      }
      if (val === undefined || val === null) {
        const enDict = DICTIONARIES.en;
        val = resolvePath(enDict, key);
      }
      if (val === undefined || val === null) {
        return key;
      }
      if (typeof val !== "string") return val;
      if (params && typeof params === "object") {
        return val.replace(/\{([a-zA-Z0-9_-]+)\}/g, function (match, pKey) {
          return Object.prototype.hasOwnProperty.call(params, pKey) ? String(params[pKey]) : match;
        });
      }
      return val;
    },

    applyTranslations: function (root) {
      const container = root || (typeof document !== "undefined" ? document : null);
      if (!container || typeof container.querySelectorAll !== "function") return;

      const i18nElements = container.querySelectorAll("[data-i18n]");
      for (let i = 0; i < i18nElements.length; i++) {
        const el = i18nElements[i];
        const key = el.getAttribute("data-i18n");
        if (key) el.textContent = BalanceI18n.t(key);
      }

      const htmlElements = container.querySelectorAll("[data-i18n-html]");
      for (let i = 0; i < htmlElements.length; i++) {
        const el = htmlElements[i];
        const key = el.getAttribute("data-i18n-html");
        if (key) el.innerHTML = BalanceI18n.t(key);
      }

      const placeholderElements = container.querySelectorAll("[data-i18n-placeholder]");
      for (let i = 0; i < placeholderElements.length; i++) {
        const el = placeholderElements[i];
        const key = el.getAttribute("data-i18n-placeholder");
        if (key) el.setAttribute("placeholder", BalanceI18n.t(key));
      }

      const titleElements = container.querySelectorAll("[data-i18n-title]");
      for (let i = 0; i < titleElements.length; i++) {
        const el = titleElements[i];
        const key = el.getAttribute("data-i18n-title");
        if (key) el.setAttribute("title", BalanceI18n.t(key));
      }

      const ariaElements = container.querySelectorAll("[data-i18n-aria-label]");
      for (let i = 0; i < ariaElements.length; i++) {
        const el = ariaElements[i];
        const key = el.getAttribute("data-i18n-aria-label");
        if (key) el.setAttribute("aria-label", BalanceI18n.t(key));
      }

      if (typeof document !== "undefined" && document.documentElement) {
        document.documentElement.lang = currentLang;
      }
    },

    translateApiError: function (rawError) {
      if (!rawError) return BalanceI18n.t("apiErrors.internalError");
      const err = String(rawError).trim();
      const lower = err.toLowerCase();
      if (lower.indexOf("too many requests") !== -1 || lower.indexOf("429") !== -1 || lower.indexOf("temporarily unavailable") !== -1) {
        return BalanceI18n.t("apiErrors.tooManyRequests");
      }
      if (lower.indexOf("unauthorized") !== -1 || lower.indexOf("session") !== -1 || lower.indexOf("401") !== -1 || lower.indexOf("authentication required") !== -1) {
        return BalanceI18n.t("apiErrors.unauthorized");
      }
      if (lower.indexOf("forbidden") !== -1 || lower.indexOf("host") !== -1 || lower.indexOf("origin") !== -1 || lower.indexOf("403") !== -1) {
        return BalanceI18n.t("apiErrors.forbidden");
      }
      if (lower.indexOf("404") !== -1 || lower.indexOf("not found") !== -1 || lower.indexOf("no route") !== -1) {
        return BalanceI18n.t("apiErrors.notFound");
      }
      if (lower.indexOf("405") !== -1 || lower.indexOf("method not allowed") !== -1) {
        return BalanceI18n.t("apiErrors.methodNotAllowed");
      }
      if (lower.indexOf("415") !== -1 || lower.indexOf("unsupported media") !== -1) {
        return BalanceI18n.t("apiErrors.unsupportedMediaType");
      }
      if (lower.indexOf("409") !== -1 || lower.indexOf("conflict") !== -1 || lower.indexOf("modified externally") !== -1 || lower.indexOf("concurrencia") !== -1) {
        return BalanceI18n.t("apiErrors.conflict");
      }
      if (lower.indexOf("400") !== -1 || lower.indexOf("bad request") !== -1) {
        return BalanceI18n.t("apiErrors.badRequest");
      }
      if (lower.indexOf("500") !== -1 || lower.indexOf("internal server error") !== -1) {
        return BalanceI18n.t("apiErrors.internalError");
      }
      if (lower.indexOf("503") !== -1 || lower.indexOf("database unavailable") !== -1) {
        return BalanceI18n.t("apiErrors.serviceUnavailable");
      }
      if (lower.indexOf("sign in failed") !== -1 || lower.indexOf("credential") !== -1 || lower.indexOf("locked") !== -1 || lower.indexOf("username or password") !== -1 || lower.indexOf("login") !== -1) {
        return BalanceI18n.t("login.failed");
      }
      if (lower.indexOf("network") !== -1 || lower.indexOf("fetch") !== -1) {
        return BalanceI18n.t("apiErrors.networkError");
      }
      return err;
    },

    getBotAiMode: function (modeId) {
      const entry = BOT_AI_LOCALIZATION.modes[modeId];
      if (!entry) return { id: modeId, name: modeId, icon: "🎮", fullName: modeId, desc: "" };
      const loc = entry[currentLang] || entry[DEFAULT_LANG];
      return { id: modeId, name: loc.name, icon: loc.icon, fullName: loc.fullName, desc: loc.desc };
    },

    getBotAiPreset: function (presetId) {
      const entry = BOT_AI_LOCALIZATION.presets[presetId];
      if (!entry) return null;
      return entry[currentLang] || entry[DEFAULT_LANG];
    },

    getBotAiRole: function (roleKey) {
      const entry = BOT_AI_LOCALIZATION.roles[roleKey];
      if (!entry) return null;
      return entry[currentLang] || entry[DEFAULT_LANG];
    },

    getBotAiKicker: function (kickerId) {
      const entry = BOT_AI_LOCALIZATION.kickers && BOT_AI_LOCALIZATION.kickers[Number(kickerId)];
      if (!entry) return { id: Number(kickerId), name: "Kicker " + kickerId, role: "General" };
      const loc = entry[currentLang] || entry[DEFAULT_LANG];
      return { id: Number(kickerId), name: entry.name, role: loc.role };
    }
  };

  // Sync html lang immediately on evaluation if possible
  if (typeof document !== "undefined" && document.documentElement) {
    document.documentElement.lang = currentLang;
  }

  return BalanceI18n;
});
