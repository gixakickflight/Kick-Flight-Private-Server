# Reglas de Agentes

## Ruteo de modelos

- Sol se queda en el hilo primario: planeacion, arquitectura, decisiones ambiguas, resolucion de conflictos, verificacion y sintesis final.
- Toda investigación de código, ensamblador o binarios, y toda operación con procesos de emuladores o ejecución del juego, se delega a subagentes Luna. El agente primario razona, planea y decide a partir de sus resúmenes; no realiza esas investigaciones directamente.
- Ajustar el nivel de razonamiento de cada Luna a la dificultad de su tarea. Darle un prompt breve, un objetivo y criterio de éxito explícitos, y pedir un resumen corto con la evidencia necesaria para decidir. Todo turno del loop ADB cae aquí.
- Escalar a Sol unicamente cuando un subagente falle dos veces seguidas contra su criterio de aceptacion explicito, y enviar un resumen, no el historial.

## Higiene de contexto del loop ADB

- Escalar cada screenshot a aproximadamente 720 px de ancho antes de enviarlo. Nunca enviar la resolucion nativa del emulador.
- Mantener un techo duro de 250000 tokens y compactar automaticamente antes de 240000.
- Conservar el system prompt y los primeros turnos fijos. Podar solo la cola mutable; nunca borrar turnos de en medio.
- Mantener el estado persistente en `.local/adb-loop-notes.md`. El subagente debe releerlo en cada turno para conocer pantallas visitadas, acciones fallidas y aprendizajes de logcat; no arrastrar ese estado como historial de conversacion.

El contrato operativo completo esta en [docs/ADB_LOOP.md](docs/ADB_LOOP.md).

## Builds DIAG del cliente (trazas KFDIAG en logcat)

Un build DIAG es la misma APK parcheada de siempre más `DIAG_PATCHES_ARM64` de `scripts/patch-il2cpp-endpoints.py`:
sondas ("caves") que escriben enteros en logcat con el tag `KFDIAG` (`v=N`). Jugabilidad y datos son los de
producción; solo sirve para leer qué hace el cliente por dentro. Documentación de referencia: `scripts/re/README.md`
(herramientas de RE estático, reglas de las caves, mapa de cuerpos muertos) y la cabecera de cada
`scripts/re/*_diag_caves.py` (qué significa cada valor).

### Construirlo en otra máquina

1. Requisitos: los de `scripts/build-direct-apk.ps1` / `.sh` (Python 3.10+, Java, apktool 3.0.3, build-tools 35.0.0,
   `base.apk` con el SHA conocido) más `pip install keystone-engine capstone` solo si vas a **generar** sondas nuevas
   (para construir con las existentes no hace falta: los bytes ya están en la tabla del script).
2. Las variables de entorno las lee el script de parches, así que funcionan con cualquiera de los dos builders:
   - `KF_DIAG=1` incluye las sondas (dry-run: 141 parches de producción → ~245 en DIAG);
   - `KF_NRE_LR=1` añade además la cave H: cada `NullReferenceException` imprime los 32 bits bajos de la dirección
     de retorno del sitio que la lanzó (ver "localizar una NRE" abajo);
   - `KF_UNLOAD_BYPASS=1` reactiva un bypass viejo del `_isUnloading` de `LoadManager.Enqueue` (solo comparación, provoca
     el SIGSEGV intermitente en el hilo UnityPreload).
   ```powershell
   $env:KF_DIAG = "1"; .\scripts\build-direct-apk.ps1 -OutputPath .local\KickFlight-2.11.0-DIAG.apk
   ```
   ```bash
   KF_DIAG=1 SERVER_BASE_URL=http://<ip-servidor>:18080 OUTPUT_APK=.local/KickFlight-2.11.0-DIAG.apk scripts/build-direct-apk.sh
   ```
   Verificación sin construir: `KF_DIAG=1 python scripts/patch-il2cpp-endpoints.py --dry-run --arm64 <libil2cpp.so pristino>`.
3. Instalar (`adb install -r`) y arrancar el cliente como siempre (`start-client-diag.bat` en la máquina de Tanuki
   hace `APK_OVERRIDE=.local\KickFlight-2.11.0-DIAG.apk`). El servidor no cambia: mismo `start-server.bat`.
4. Antes de cada prueba `adb logcat -c`; después `adb logcat -d -v time > .local/run/logcat-<nombre>.txt`.
   Lectores: `python scripts/re/attack_diag_read.py <archivo>` (ataques básicos/combos, línea de tiempo legible),
   `adb logcat -d -s KFDIAG` para todo lo demás (tabla de valores en `scripts/re/README.md` y las cabeceras de
   `attack_diag_caves.py` / `warp_diag_caves.py` / `reconnect_diag_caves.py`).

### Sondas actuales (actualizado 2026-09-22)

| Valores | Qué dicen | Fuente |
| --- | --- | --- |
| 1xx/2xx/3xx/4xx, 6xx, 700 | handshake de inicio de batalla, `_enableAi`, AIPlayerEngine | tabla inline en `patch-il2cpp-endpoints.py` |
| 940+n | `PlayerCharacter.SetState(n)` | idem |
| 950–973, 1000–3999 | IA: rutas, PerformMove, sub-estados, take-off | idem (regiones D/E) |
| 6200–6421 | fin de skill, CrossFade, nunchaku, acople de armas, PlayIdle | idem (región F) |
| 7700–7830 | warp de Hitagi (KS sin objetivo), `SetVisible` | `scripts/re/warp_diag_caves.py` |
| 8100–8823 | bucle de ataque básico: combo, `IsUpdateAction` con motivos (`87x0/87x1`) y estado actual (`8790+n`), alcance, ángulos, `IsAttack`, `Play*AttackIn`, destrucción de colisiones (`8800+tipo`, `8810+impactos`) | `scripts/re/attack_diag_caves.py` |
| 8901–8992 | reconexión Photon tras un corte: entrada de `CallbackDisconnected` y su causa, identidad del objeto que arrancó la máquina de estados (`8921`), `UpdateReconnect` con `_reconnectInfo` nulo (`8961`), `SetReconnectState`, `IsReconnectEnable`, el ida y vuelta de RPC de reconexión (`8981`-`8985`) y la causa final (`8992`) | `scripts/re/reconnect_diag_caves.py` |
| 9100–9113 | fixes de kicker (2026-10-09): puerta del dron de Owlbert (`9100` `_setter.IsMine`, `9101` `_target.IsMine`, `9102` resultado, `9103` el match de dueño de `CreateSmog` pasa) y raíz del bind de Anna (`9110` arranca la condición 15 en el caster, `9111` entra la cueva con `_setterPlayer != null`, `9112` `GetKickerSkillConditionInitInfo(3)` no nulo, `9113` vuelve `AcceptCondition`). Overrides DIAG en `StartDisconnectTime` (A) y `PhotonPropertyManagerBase.CheckInitializeError` (B + sondas pequeñas), cuerpos muertos en TODOS los flavours | `scripts/re/kicker_fix_caves.py` |
| 9190+ | spacing del smog de la SS de Owlbert (2026-10-10; reubicado 2026-10-11): `9190 + int(paso × 10)` en cada `SmogConditionAction.CreateSmog`, donde `paso = SpecialSkillTrap.interval (>0)`, si no `2 × radius`. `9220` = S=3, `9270` = fallback radius 4. Caves de producción y override DIAG en `HomeSummonModelController.<LoadModelAsync>d__25.MoveNext` 0x159E25C (muerto en TODOS los flavours; la revisión anterior en `GameManager.BeginReconnectRoomFailed` pisaba código vivo con KF_PHOTON=1) | `scripts/re/smog_spacing_cave.py` |
| 9303–9338 | replays con `KF_PHOTON=1` (2026-10-12): `9303` entra `ReplayManager.BeginSession`, `9304/9305` resultado de `CanBeginSession`, `9306` `BeginRecordingSession` devolvió una `ReplayRecordingSession`, `9307` entra `EndSession`, `9310` entra `ResultManager.<UploadReplayDataAsync>` estado 0, `9311/9312` `PhotonManager.IsMyPlayerMaster`, `9313/9314` `ArchiveData.IsUploadReplayData`, `9315/9316` `BattleId` no vacío/vacío, `9329`–`9332` `ReplayWriteResult.WriteState` crudo (Invalid=-1 → 9329, Progress=0 → 9330, FinishSucceeded=1 → 9331), `9338` entra `StartPlayback`. Solo en `KF_DIAG=1 & KF_PHOTON=1`; las caves viven en cuerpos muertos ya `ret`-stubbeados (`List<LocalClient.InternalMsg>.Contains` 0x2F277C0 — compartido con la cave del getter real —, `<CallbackBattleStartSuccess>b__46_0` 0x13ED8FC) y el hook de entrada es `b` | `scripts/re/replay_diag_caves.py` |
| 9700–9720 | reporte de errores del cliente (2026-10-10): `9700` `ExceptionHandler.Register` suscribió `HandleLog` (sin él la vía C no corre nunca), `9701` `HandleLog` con `LogType.Exception`, `9702` la cave C llama a `SetAnalysisException`, `9703`/`9704` C descartada por reentrada / presupuesto agotado, `9710`/`9711` la cave B reenvía una anomalía / una excepción (tras el dedupe), `9712` B descartada por presupuesto, `9720` entra `AnalysisManager.RegisterGetRegionFailed` (cualquier llamador). Copia instrumentada de las caves de producción en el mismo cuerpo muerto (`ExceptionHandler.getMessageParts` 0x30E1CD4, muerto en TODOS los flavours) | `scripts/re/error_report_caves.py` |
| LR crudo | origen de cada NRE (`KF_NRE_LR=1`) | cave H |

### Añadir o cambiar una sonda

- Escribe el cuerpo en `scripts/re/attack_diag_caves.py` (o un archivo hermano) y ejecuta el script: imprime las
  entradas `DIAG cave`/`DIAG hook` listas para pegar en `patch-il2cpp-endpoints.py` antes de `# ---- END DIAGNOSTIC ----`
  (reemplaza el bloque anterior completo: las caves se re-empaquetan y cambian de dirección).
- Reglas que costaron crashes: nunca `UnityEngine.Debug.Log` desde código parcheado (muere en `libunity+0x34426c`);
  hook de entrada de función = `b` + salto de vuelta, nunca `bl` (pisa el LR del llamador); un hook `bl` después del
  `ldp x29, x30` del epílogo o antes de un tail call pisa el LR del llamador ("a `bl` hook after the epilogue's x30
  restore or before a tail call clobbers the caller's LR": la sonda 9570 en `ThrowingStarSpecialSkillAction.OnEndCutScene`
  hacía que `b set_State` volviera al epílogo); nada de hooks en
  funciones hoja ni sobre `bl`/saltos; la cave pierde NZCV (si el hook cae sobre un `fcmp`, repítelo al final);
  no asumas que un registro sigue teniendo `this` (`IsUpdateAction` reutiliza x19: la primera sonda 8230 crasheó en
  la pantalla de carga 5/5 veces); solo cuerpos muertos verificados (entrada stub a `ret` en producción y ningún
  salto entrante, comprobado con un barrido de `b/bl/b.cond/cbz/tbz` sobre todo el .so).
- Cuerpos muertos en uso y libres: ver "Dead bodies" en `scripts/re/README.md` (`UpdateIdleTypeRate` tiene ~1 KB libre).
- Reubicar un bloque de caves entero a otro cuerpo: `scripts/re/relocate_diag_region.py` (relinka `bl`/`adrp` externos
  y los hooks entrantes; así salieron las regiones D/E de `LoadDeckSummonModel`, que el DIAG tenía que stubear y por
  eso ningún summon cargaba en DIAG).

### Reporte de errores del cliente (producción, 2026-10-10)

Dos caves de producción (todos los flavours, `scripts/re/error_report_caves.py`, detalles en su docstring) mandan al
servidor lo que el cliente retail solo le daba a Crashlytics, por el canal `/analysis/index` que ya existe:
`AnalysisManager.RegisterGetRegionFailed(cause)` lo envuelve como `{"cause": s}` con la clave de serie
`PhotonGetResionPingFailed` y `AnalysisManager.ManagedUpdate` manda uno por frame.

- B, anomalías de partida: hook en `CrashReportManager.SetAnalysisException` 0x17CAEBC (justo después del dedupe del
  `HashSet`), `cause = "[Analysis]" + msg` (`msg` = `"1:<texto>//2:<texto>..."` de `GameManager.SetAnalysisException`).
  Ya no se llama a `Crashlytics.LogException`.
- C, excepciones gestionadas: hook en `Firebase.Crashlytics.ExceptionHandler.HandleLog` 0x30E1C28 (rama `type == 4`),
  `SetAnalysisException("\n" + message + "\n" + stackTrace, true)` → el mismo `HashSet` deduplica cada mensaje+pila
  único una vez por sesión ANTES de encolar y la cadena acaba en B: `cause = "[Analysis]\n<message>\n<stack>"`
  (truncado a 2500 caracteres antes del dedupe). Se salta `getMessageParts`/`LoggedException`/Crashlytics.
- Guardas: bytes de relleno de `CrashReportManager` (+0x11 bandera "reenviando excepción" contra la reentrada, +0x12/+0x13
  contadores de anomalías/excepciones, tope 16 por tipo y por proceso; tras el tope C no asigna nada). Los Method* de
  `Singleton<T>.get_Instance` salen de slots de metadatos en .bss que inicializa el arranque
  (`ColorfulManager.<BeginAsync>d__21.MoveNext`); las caves hacen `cbz` y descartan si siguen a 0.
- C depende de que Firebase registre `HandleLog` (`Crashlytics.PlatformAccessor..cctor` → `Register` solo si
  `AndroidImpl.IsSDKInitialized()`, es decir `FirebaseCrashlyticsInternal.GetInstance() != null`). Los logcat del
  cliente dicen `FirebaseApp initialization successful` + `CrashlyticsInitProvider initialization successful`, pero la
  prueba es `KFDIAG 9700` en un build DIAG. Si no aparece: `Application.CallLogCallback` (0x29A44DC) NO sirve de
  alternativa, porque el nativo solo lo llama tras `SetLogCallbackDefined(true)`, que solo hace
  `add_logMessageReceived` y el único suscriptor es ese `Register`; la alternativa es forzar el registro convirtiendo
  en `nop` el `tbz w0, #0, #0x30dd044` de 0x30DD018 (el cctor llama a `Register` aunque el SDK no esté inicializado).
- Lado servidor: un `cause` que empieza por `"[Analysis]\n"` es una excepción; `"[Analysis]"` seguido de otra cosa es una
  anomalía (o un `SendPurchaseFailed`); sin prefijo son los fallos de región de serie (`GetPingFailed`, `DisconnectCause`).

### Localizar una NRE con `KF_NRE_LR=1`

`KFDIAG v=<n>` justo antes de la excepción en logcat es `LR & 0xFFFFFFFF` (puede salir negativo). Base del módulo:
`adb shell run-as jp.grenge.kickflight cat /proc/$(pidof jp.grenge.kickflight)/maps | grep libil2cpp | head -1`.
RVA = `(base & ~0xFFFFFFFF | n) - base` (si sale negativo, suma `0x100000000`). Ese RVA es la instrucción siguiente
al `bl 0x12141ec` (helper de NRE) y `python scripts/re/a64dis.py <rva-0x20> 12` enseña qué comprobación de nulo era.
Ejemplo real: `0x13C74CC` = `FieldManager.FieldInfo == null` en `PlayerCharacter.Initialize` → faltaba la fila 801 del
master `Field` (string inline en `DemoSessionApi.cs`), no era el cliente.

### Aprendizajes del 2026-09-20 (ronda 7)

- La regresión "Leorex congela al kicker y no reaparece" era solo DIAG: `LoadDeckSummonModel` estaba stubeado porque
  sus bytes alojaban las regiones D/E → ningún modelo de summon cargado → NRE en `SummonCharacter.InitializeAsync` y
  en cada `ApplyForcedAction → PlayerStateSkill.End → ForceFinishSummon`. Resuelto reubicando las regiones.
- La pantalla de selección de kicker en blanco: `kickerCostumeId` es el **id de fila** de `KickerCostume` (110 = Hitagi
  color estándar, 64 = Owlbert), no la columna `costumeId`. El servidor ahora sirve ids de fila y normaliza un
  costume que no sea del kicker actual.
- El botón de prueba (Trial, campo 801) nunca había cargado: fila 801 del master `Field` ausente (ver arriba).
- Combos cuerpo a cuerpo 1-1-1: cada golpe **conectaba** pero el collider llegaba al fin de su vida útil
  (`CollisionDestroyType.LifeTime`) porque `masters_weapon_attack_collision.json` tenía `collisionHitType = All`;
  `WeaponAttackActionBase.CallbackAttackCollisionDestroy(LifeTime)` marca el golpe como fallo, anula el objetivo y
  `ResetComboCount()`. Con `One` el primer impacto destruye el collider con tipo `Hit` y el combo encadena.
  Cambiado en el generador y en el JSON (pendiente de verificar en juego).
- Los CRASH de logcat bajo ndk_translation siempre muestran `libunity+0x34426c`: solo valen `fault addr` y el nombre
  del hilo. `UnityPreload` + fault 0 ~6-12 s después de `D/Unity: GameScene` es la carrera intermitente del loader.
- Los masters `Field`, `BattleRule` (fallback) y otros son strings inline en `DemoSessionApi.InitializeMasters`;
  `python scripts/re/served_master.py <Tabla>` enseña lo que realmente recibe el cliente.

### Aprendizajes del 2026-09-20 (ronda 8: pasivas, KS y efectos de discos)

- Todos los efectos de discos y KS van ahora en `DISC_EFFECTS` / `KICKER_SKILL_EXTRAS` de
  `scripts/generate_combat_masters.py` (curas, condiciones, blow-off, pull-in) y se regeneran con
  `python scripts/generate_combat_masters.py --only masters_skill_condition,masters_skill_heal,masters_skill_blow_off,masters_skill_pull_in,masters_skill_trap`
  (ojo: `--only skill_condition` a secas también reescribe `masters_special_skill_*`). Semántica verificada de cada
  columna en `docs/COMBAT_MASTERS_FILL_IN.md` §2 (curas = fracción de MaxHP, HpDrain/SpDrain se leen en
  `ApplyHealOnDamage`, DamageRate = daño recibido, Poison = MaxHP del objetivo por tick, Dot = ataque del emisor…).
- Blow-off: sin fila `SkillBlowOff` un `FrontAttack` golpea hacia **abajo** (`FrontAttackSkillAction.get_BlowOffDirectionType`
  → 2); por eso Blox y compañía "slammeaban" en vez de lanzar. En un `MoveAttack` la fila convierte la embestida en
  "pursuit" (arrastra al golpeado, `MoveAttackSkillAction.HitCallback`) y sin fila es "pierce" + shield break
  (`DiscSkillParameter..ctor` solo marca `IsShieldBreak` si no hay blow-off ni cura). Dirección 4 = víctima − atacante,
  así que en bombas/torretas (atacante = quien la puso) se usa Up.
- Pull-in (KS de Yuyan/Anna): fila `SkillPullIn` → `PlayerStatePullIn`; una fila de blow-off en el mismo skill la anula.
- Bomba de Jay: `DiscSkillParameter..ctor` solo rellena `TrapInfo.CollisionMasterId/Radius/LifeTime/EffectPath` desde un
  clip sensor del timeline `aed_NNN`, y ningún bundle tiene grupo 40001 → la explosión pedía la colisión 0 (null) y nunca
  estallaba. Cave de producción en la cola de `BatAbilityParameter..ctor` (cuerpo de `UpdateIdleTypeRate`, 0x13CE500):
  colisión 198 de `skill_40001`, radio 2 → `SkillTrap.radius`, vida `interval`, efecto `effectPath` con trigger 1 (sin
  trigger `SyncEffect.PlayStartEffect` no arranca los `effect/cm`); `duration` = 2 s hasta estallar; daño = ataque ×
  `Skill.coefficient` (10). Segunda cave (`SetModel` 0x159DA48): `DiscSkillParameter.HitInfo` solo lo asigna
  `SetSkillActionHitData` desde un clip de colisión con daño del timeline → para 40001 era null y cada impacto de la
  explosión moría con NRE en `PlayerCharacter.AcceptDamageInfo`; la cave toma `_hitInfos[207]`. Verificado en emulador
  (KFDIAG 9001-9041 + 8811 = 1 impacto, 0 excepciones). Regla nueva: una cave que hace `bl` tiene que guardar/restaurar
  x30 antes de su `ret` (la primera versión se colgaba en bucle = "Cargando" eterno con Jay).
- Carga de la Special Skill: `PlayerCharacter.CalcChargeSP` caso 2 suma `distancia volada × addMoveSpecialSkillPoint`
  (MaxSP = 100), caso 3 `addWeaponAttackSpecialSkillPoint` por golpe. `masters_kicker_parameter.json` lleva ahora
  `100 / distancia` con las distancias por kicker de Appliv 431798 (Jay 550 … Anna 1500 unidades); antes era 1.0 (la barra
  se llenaba en 100 unidades).
- El `effectPath` de una trampa/explosión tiene que ser un bundle cargado en la partida: `EffectManager.InstantiateEffect`
  devuelve null sin error para lo que `LoadManager` no cacheó (los `effect/ds/` solo se cargan para los discos equipados);
  `effect/cm/ef_cm_001..036` (orden del enum `EffectManager.Effect`, 006 = Dead) siempre están.
- Textos de la UI: `masters_translation.json` es el master `Translation`; cada `LocalizeText` de los prefabs y
  `LocalizeManager.GetText(enum)` (`skillCategoryType.attack` → "ATK", etc.) buscan su clave ahí y una clave ausente se
  pinta vacía. `scripts/generate_translations.py` genera todas las claves (prefabs `docs/localize_keys.json` + enums +
  literales); `scripts/extract_localize_layout.py` saca la posición de cada texto (`docs/localize_layout.json`) para la
  pestaña **Text** del WebUI de balance (wireframe por pantalla + edición).
- Velocidad de ataque básico: constantes en el cliente (`XxxAttackAction..cctor`), no es dato de master;
  `RANGED_ATTACK_INTERVALS` en `patch-il2cpp-endpoints.py` (rebuild con `build-all-apks.bat`).
- Tiempos de casteo por kicker y categoría (Appliv 431798) → `CAST_TIMES` en `scripts/build-action-asset-bundles.py`,
  que desplaza `_compatibilityTime` y los `_startTime` de cada disco en los nueve bundles generados (revisión Octo 26).
  Rehacer bundles = `python scripts/build-action-asset-bundles.py`, subir `revision` en `title-minimum.json` **y añadir
  el nuevo número a `fromRevisions`** (el cliente al día pide `/v1/list/12345/<revision>` y sin ese fixture recibe 404 =
  "error de comunicación"), `python scripts/build-title-resource-catalog.py`.
## Anexo operativo Fedora: primer loop local y balance (complementa `docs/ADB_LOOP.md`)

### Checkout, Fedora y assets

- No uses `git reset`, checkout forzado ni actualices submódulos encima de un checkout con modificaciones locales. Revisa `git status --short --branch` y submódulos, trae `origin`, crea un worktree aislado desde el último `origin/main`, inicialízalos y anota el SHA. Archivos untracked de otro worktree no están presentes en el checkout original.
- Distingue trabajo nuevo de continuación: para una prueba nueva usa worktree limpio del último `origin/main`; si el usuario/root autoriza continuar una prueba de balance en curso, reutiliza la worktree activa con sus cambios pendientes y notas, no la reemplaces por main limpio. La ruta del inventario fechado sirve para localizarla; confirma que aún existe.
- El bootstrap Fedora es `/home/gixarde3/Descargas/setup-kickflight-fedora.sh`; instala paquetes y puede pedir `sudo`, así que es referencia de instalación, no un paso de cada prueba. `.local/build-fedora.sh` es generado/ignorado, no fuente versionada; comprueba su existencia y lee sus prerequisitos antes de usarlo.
- Layout observado 2026-10-07: el repo está en `~/Proyectos/KickFlight/Kick-Flight-Private-Server`, los assets en el sibling `~/Proyectos/KickFlight/Kick-Flight-Assets`, y `src/Kick-Flight-Assets` apunta allí con symlink. El seed usa `../Kick-Flight-Assets/octo_sorted` y también `content/resources`; reconstruye el tar salvo que hayas verificado que corresponde a estos assets. El catálogo requiere 35 weapon-costume fallbacks físicos, generados por `scripts/add_weapon_costume_aliases.py` desde donor bundles del sibling a `.local/weapon-costume-fallbacks/`. El builder default ejecuta ese generador y requiere UnityPy 1.25.3; usa `.local/assets-venv/bin/python` si ahí está esa versión. La venv de assets tiene UnityPy/lz4/Pillow/PyCryptodome; la `.venv` del repo tiene UnityPy/lz4/Pillow/NumPy/pytest. Capstone/Keystone son para generar caves nativas, no para consumir bytes DIAG existentes.
- SDK Fedora probado: Android x86_64 API 35 y Build Tools 35.0.0; el builder usa `.venv/bin/python`, apktool, `zipalign`, `apksigner`, `keytool` y permite `ANDROID_BUILD_TOOLS` override. Descubre AVDs con `emulator -list-avds`; `KickFlight_API35` existía el 2026-10-07, pero confirma serial mediante `adb devices`.

### API y Photon locales

- `scripts/run-local.sh` intenta Docker `luxon-server`; aquí `docker ps` falló con permission denied. No supongas que el contenedor inició ni repitas el setup con sudo en el loop. Había un binario nativo funcional en `submodules/luxonserver/build/luxon_server`, iniciado desde `.local/luxon-run`. Mantén API y Photon en sesiones foreground persistentes y tee stdout a `.local/run/server-api.log` y `.local/run/photon.log`; evita `nohup` o procesos efímeros sin logs. API local usa HTTP 18080 y gRPC 18081; Photon native escuchó UDP 5055/5056/5058 y 27000–27002.
- ENet patch 0006 amplía ventanas de recepción; 0007 sube timeout y tolerancia a retransmisiones. Comprueba cada patch con `git apply --check` (y reverso) y revisa `Luxon/include/luxon/enet_peer.hpp`; no fuerces ni reviertas un submódulo anidado modificado. El 2026-10-07 el checkout original tenía 0006 aplicado y 0007 ausente; el worktree activo tenía cambios adicionales y el patch 0007 no se aplicaba/revertía limpiamente.
- `/health/ready` valida fixtures/recursos y el flag MasterReachable. PhotonManager prueba TCP y luego UDP con `send()` de cuatro bytes sin esperar respuesta; el 200 no prueba un handshake Photon, y Game/Name false no necesariamente hacen fallar readiness. Revisa proceso y sockets con `ss -lunp`/`ss -lntp`, luego confirma que el cliente completa matchmaking/partida.

### APK, AVD y medición de daño

- El `base.apk` local verificado el 2026-10-07 es Kick Flight 2.11.0/versionCode 55, SHA-256 `ffa620d2e2f905e729812606024e02741124951dce908bb2e0587f0a4e16e031`. La APK remota 2.11.1 solo sirve como base si pasa el dry-run byte-guarded del patcher. En un worktree sin `base.apk`, establece `SOURCE_APK` a la copia verificada y revalida su hash. Para cambios solo de API/masters reutiliza el APK LAN ya instalado; rebuild/reinstall solo si cambia endpoint/código de cliente o se requiere DIAG. Reinstala con `adb install -r`; no borres datos/cache/AVD.
- Para el HP diagnostic v4 probado se usaron explícitamente `KF_DAMAGE_DIAG=1 KF_PHOTON=0 SERVER_BASE_URL=http://10.0.2.2:18080`; no omitas `KF_PHOTON=0`, porque el builder puede tener otro default. `KF_DAMAGE_DIAG=1 KF_PHOTON=1` está rechazado por guardas del patcher. La ruta cliente baseline/offline no detiene el LuxonServer local. Antes de instalar, valida el `libil2cpp.so` efectivo contra el patch map aprobado, incluyendo owner stub `RET` y cero cambios fuera de rangos permitidos.
- Comando v4 probado en la worktree de balance de esa fecha: `SOURCE_APK=/home/gixarde3/Proyectos/KickFlight/Kick-Flight-Private-Server/base.apk KF_DAMAGE_DIAG=1 KF_PHOTON=0 SERVER_BASE_URL=http://10.0.2.2:18080 OUTPUT_APK=.local/artifacts/KickFlight-2.11.1-damage-diag-v4.apk .local/build-fedora.sh`. Úsalo solo si la worktree contiene la versión compatible de `scripts/patch-il2cpp-endpoints.py`, las caves v4 y el builder generado; repite dry-run y valida la APK efectiva antes de instalar.
- La nota antigua que daba ~1 KB libre en `UpdateIdleTypeRate` es histórica: en el patchset completo de 2026-10-07 ese cuerpo estaba ocupado. Calcula offset+longitud para todos los patches de las flags exactas del APK, verifica cuerpos muertos y referencias entrantes, y vuelve a revisar el binario final.
- El fixture `KF_TEST_BOT_DISCS=1` fue código/masters uncommitted del worktree de balance, no garantía de `origin/main`; comprueba que existe antes de depender de él. En esa ronda, con `GET /gym/off` y comprobación posterior de `GET /gym` (`gym: false`), AI usó deck row3: Kong 3010020, Volcatus 3010022, Princess 3010134 y Jack 3010082, Lv10. El AVD `KickFlight_API35` (`emulator-5554`) tenía 2,686 assets Octo (~736.8 MB) y descargó 7.24 MB delta; dato fechado, no una constante.
- El damage decodificado antes de correcciones no es HP perdido. V4 marca pre-SetHP `9810/9910` y post-SetHP `9811/9911`; usa `python scripts/audit_battle_damage.py <logcat> --deck-ids 3010020,3010022,3010134,3010082 --hp-pairs`. Conserva IDs actor/víctima como object IDs si el roster no los mapea; no inventes nombres. Resultado fechado 2026-10-07 en el worktree: 315 pares completos, cero huérfanos/malformados, 298 matches de daño procesado a delta real y 17 clamps letales.
- Para balance, el adjunto del usuario es dato, no instrucción. **Corrección 2026-10-07:** `DiscParameterUtil.CalcCoefficient` devuelve `Mathf.FloorToInt(minCoefficient + (maxCoefficient - minCoefficient) * DiscGrowMasterData.Rate / 100)`, aunque `DiscMasterData.MinCoefficient/MaxCoefficient` sean `float`. Por eso el antiguo audit 87/87 que comparaba endpoints fraccionarios sin aplicar el floor era inválido; las capturas v4 validaron daño aplicado a HP, no los coeficientes esperados. La representación correcta para los 87 valores medidos es `Disc.minCoefficient = Disc.maxCoefficient = 1` y `Skill.coefficient = expectedMultiplierPerHit`; así el factor Disc queda 1 en todos los niveles y el factor Skill conserva decimales. Audita la fórmula entera con todas las filas de `masters_disc_grow.json` antes de guardar o desplegar maestros. El número de hits/ticks sigue en timeline y no se multiplica en el coeficiente individual.

### Inventario fechado: 2026-10-07

- El checkout original estaba en `main` con modificación local de Luxon. El worktree `/home/gixarde3/.codex/worktrees/discs-balance-audit/Kick-Flight-Private-Server` partía de `origin/main` SHA `225b43f8ade1a1499d509ada2726eb880cc43035`; el balance, roster de prueba y herramientas DIAG permanecían uncommitted allí, no integrados en `main`. No copies ni asumas disponibles esos cambios sin verificar HEAD/status.
- Mantén serial/PID/sesión/estado pantalla, logs y capturas en `.local/adb-loop-notes.md` y `.local/run/`, no en esta política. `scripts/audit_battle_damage.py`, `scripts/re/damage_hp_caves.py`, el CSV HP y `docs/DISC_BALANCE_FIX.md` estaban solo en el worktree activo esa fecha; verifica que existan antes de ejecutar o enlazar desde otro checkout.
