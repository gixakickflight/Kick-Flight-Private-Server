# Kick Flight: The Fenix Returns

Servidor privado de comunidad para **Kick-Flight**. El cliente se parchea a partir de la APK base 2.11.0; la última versión de cliente publicada en el catálogo del repositorio es la **2.11.804** (Android). Este repositorio contiene la API compatible con el cliente, el servidor Photon basado en LuxonServer, los datos de juego editables, la herramienta web de balance y las herramientas para preparar el CDN y una APK parcheada.

La instancia pública corre en una VM de OCI. Docker Compose levanta la API, Photon, el CDN y la landing; la persistencia de jugadores usa PostgreSQL administrado por Neon. Caddy publica la web por HTTPS en `kick-flight-fenix.us.ci`.

## Instancia pública

- Sitio y descarga: [kick-flight-fenix.us.ci](https://kick-flight-fenix.us.ci/)
- APK Android 2.11.804: [descargar](https://kick-flight-fenix.us.ci/apk/KickFlight-2.11.804-remote.apk)
- Estado de la API: [health/ready](https://kick-flight-fenix.us.ci/health/ready)

La APK ya apunta a esta instancia. Tras instalarla, el primer inicio requiere internet para bajar los recursos del juego desde el CDN. Las partidas usan Photon; la API y Photon son servicios separados dentro de la pila.

## Qué incluye

- **API compatible con el cliente** (`src/KickFlight.BootstrapApi/`, ASP.NET Core / .NET 8): sesiones, estado del jugador, masters cifrados, catálogo de recursos, descarga de APK (`/apk*`), chat por WebSocket (`/.ws`), subida de diagnósticos (`/diag*`) y matchmaking gRPC (`Protos/openmatch.proto`). Puertos locales por defecto: HTTP `18080` y gRPC `18081`.
- **Photon / LuxonServer** (`submodules/luxonserver`, rama `tanuki-server`): partidas en tiempo real; puertos UDP `5055`, `5056`, `5058` (ver [`docs/PHOTON_SERVER.md`](docs/PHOTON_SERVER.md)).
- **Ranked**: progresión de rango (`RankProgression.cs`), rotación de modos (`RankedModeRotation.cs`) y, en la versión reciente, escalado de HP/ataque del guardián según el rango.
- **Notificaciones push (FCM) para búsqueda ranked**: registro de token en `POST /push/register` y envío desde `src/KickFlight.BootstrapApi/Push/`. Está desactivado por defecto (`Push:Enabled`) y en el cliente es opcional (`KF_PUSH=1`).
- **Telemetría de operaciones**: endpoints administrativos `/admin/ops/*` y pestaña de salud de la herramienta de balance ([`docs/OPS_TELEMETRY.md`](docs/OPS_TELEMETRY.md)).
- **Endpoints de administración** (solo loopback según los comentarios del código): `/admin/maintenance`, `/admin/client-update`, `/admin/latest-information`, `/admin/ops/summary`, `/admin/ops/exceptions`, `/admin/ops/battle/{battleId}`.
- **Salud**: `/health/live`, `/health/ready`, `/health/photon`.
- **Herramienta web de balance** (`tools/balance/`): edita los `masters_*.json` de combate (incluye una pestaña de Guardianes) escribiendo overrides en `.local/masters-overrides/`; ver [`tools/balance/README.md`](tools/balance/README.md). Se inicia con `start-balance.bat`.
- **Herramienta de insignias** (`tools/badges/`, contenedor `badges` en Compose; `start-badges.bat`).
- **Pipeline de cliente**: parcheo de `libil2cpp.so`, versión, push y reporte de salidas; builds DIAG con trazas `KFDIAG` (ver más abajo).
- **Soporte iOS (en progreso)**: enrutamiento de plataforma iOS en la API y scripts de conversión de recursos; ver `docs/IOS_*.md`. El estado exacto del cliente iOS no está verificado en este README.

## Arquitectura desplegada

```text
Cliente Android
  ├─ HTTPS :443 ──> Caddy ──> Nginx
  │                              ├─ landing
  │                              ├─ /cdn/ ──> bundles del juego
  │                              └─ API HTTP, incluida la descarga de APK ──> ASP.NET Core
  ├─ gRPC :18081 ──> Nginx gRPC ──> API
  └─ Photon TCP/UDP ──> LuxonServer

API ── TLS ──> PostgreSQL en Neon
```

La VM OCI ejecuta los contenedores definidos en [`deploy/docker-compose.vps.external-db.yml`](deploy/docker-compose.vps.external-db.yml). Nginx entrega la landing y los bundles; reenvía las rutas HTTP del juego y la descarga de la APK a la API. Caddy termina HTTPS para `kick-flight-fenix.us.ci`. El modo de base externa omite el contenedor PostgreSQL local y lee `DATABASE_URL` del entorno; la documentación de despliegue usa Neon como proveedor.

El cliente llega a Photon por los puertos TCP/UDP configurados para NameServer, MasterServer y GameServer. El puerto público de gRPC pasa por un proxy Nginx dedicado. La base de datos no se publica desde la VM.

La portada está en [`deploy/site/index.html`](deploy/site/index.html) y ofrece español e inglés. Recuerda la selección en el navegador; si todavía no hay una selección guardada, usa el idioma del navegador y la sugerencia regional disponible. Los textos del juego están en `config/masters_translation.json` y se mantienen con `scripts/generate_translations.py`; esa tabla es independiente del idioma de la landing.

## Desarrollo local

Requisitos: SDK de .NET 8, Python 3 y el submódulo de Photon:

```bash
git submodule update --init --recursive
```

### Windows

- `start-server.bat`: arranca la API en `http://0.0.0.0:18080` (gRPC `18081`). Los masters se leen al arrancar; vuelve a ejecutarlo tras editar `config\masters_*.json`.
- `start-client.bat`, `start-client-diag.bat`, `start-client-merged.bat`, etc.: lanzan el cliente en el emulador con la APK correspondiente.
- `start-balance.bat` (herramienta de balance, `http://127.0.0.1:8765`) y `start-badges.bat`.

### Linux / macOS

```bash
cp config/apk-direct-server.example.json config/apk-direct-server.local.json   # ignorado por Git; ajusta serverBaseUrl
./scripts/run-direct.sh        # servidor directo local
./scripts/run-local.sh         # API en HTTP_PORT (18080 por defecto) + LuxonServer
```

`scripts/run-local.sh` intenta usar Docker para el contenedor `luxon-server`. Si Docker no está disponible o no tienes permisos, compila `luxon_server` desde `submodules/luxonserver` y ejecútalo nativamente junto a la API (ver [`docs/PHOTON_SERVER.md`](docs/PHOTON_SERVER.md)). Comprueba los sockets con `ss -lunp` / `ss -lntp`; `/health/ready` por sí solo no garantiza que Photon complete un handshake.

Más detalle de configuración, proxy Android y solución de problemas: [`docs/RECREATE_FROM_SCRATCH.md`](docs/RECREATE_FROM_SCRATCH.md), [`docs/ENVIRONMENT_REFERENCE.md`](docs/ENVIRONMENT_REFERENCE.md) y [`docs/ANDROID_SETUP.md`](docs/ANDROID_SETUP.md).

### Pruebas

```bash
dotnet test KickFlight.PrivateServer.sln --nologo
python3 tests/test_teamtype.py
```

Existen además pruebas Python en `tests/` y en `tools/balance/` y `tools/badges/`; los comandos exactos por suite no están verificados aquí.

### Masters de combate

Los efectos de discos y habilidades se generan con `scripts/generate_combat_masters.py` (ver [`docs/COMBAT_MASTERS_FILL_IN.md`](docs/COMBAT_MASTERS_FILL_IN.md) y [`docs/DISC_BALANCE_FIX.md`](docs/DISC_BALANCE_FIX.md)). Revisa la advertencia de `--only` en `AGENTS.md` antes de regenerar. Los textos de la interfaz se mantienen con `scripts/generate_translations.py`.

## APK y recursos

El cliente Android se obtiene parcheando la APK base de Kick-Flight 2.11.0 (`base.apk`, no incluida en el repositorio); no se compila desde el código fuente del juego. Para producir una APK local se requieren esa APK base, un keystore propio y las herramientas de Android indicadas en [`docs/RECREATE_FROM_SCRATCH.md`](docs/RECREATE_FROM_SCRATCH.md). Los archivos protegidos, los assets originales y los artefactos locales no se distribuyen en este repositorio.

El endpoint del servidor y el host Photon quedan grabados en la APK al construirla. `scripts/build-direct-apk.sh` prepara y firma el cliente; `KF_PHOTON=1` y `KF_PHOTON_HOST` activan y configuran Photon para una compilación remota; `SOURCE_APK`, `OUTPUT_APK`, `SERVER_BASE_URL`, `KF_VERSION_NAME`, `KF_VERSION_CODE` y `KF_PUSH` también son leídas por el pipeline. Existe además `scripts/build-direct-apk.ps1` para Windows.

Los parches de IL2CPP viven en `scripts/patch-il2cpp-endpoints.py` (con verificación byte a byte y `--dry-run`). Los builds **DIAG** (`KF_DIAG=1`) añaden sondas `KFDIAG` en logcat; sus reglas y mapa de sondas están en [`AGENTS.md`](AGENTS.md) y [`scripts/re/README.md`](scripts/re/README.md). Los hallazgos de ingeniería inversa se guardan en [`docs/re-notes/`](docs/re-notes/). La guía [`deploy/VPS.md`](deploy/VPS.md) detalla el artefacto publicado y sus rutas.

Los recursos se definen en `config/resources/catalog.json`. `scripts/build-title-resource-catalog.py` actualiza el catálogo del juego y `scripts/update-apk-catalog-sha.py` registra el hash de la APK. `scripts/build-cdn-tree.py` reconstruye el árbol completo `/cdn/` a partir del catálogo y de los assets preparados localmente; la carpeta de assets debe existir fuera del repositorio. Revisa los argumentos y el destino antes de ejecutarlo, porque cada ejecución reemplaza el árbol CDN de salida.

## Despliegue

La configuración que representa el despliegue actual es la de VM con PostgreSQL externo:

```bash
docker compose --env-file .env \
  --project-name deploy \
  -f deploy/docker-compose.vps.external-db.yml up -d --build
```

`DATABASE_URL` y los valores de host se suministran desde archivos de entorno locales ignorados por Git. No pegues credenciales en comandos compartidos ni las agregues al repositorio. El Compose alternativo [`deploy/docker-compose.vps.yml`](deploy/docker-compose.vps.yml) incluye un PostgreSQL local para instalaciones que no usen Neon.

La guía [`deploy/VPS.md`](deploy/VPS.md) cubre la VM, reglas de red, la base externa, Caddy, la landing y el orden para generar la APK, el catálogo y el CDN. Incluye los endpoints de salud y comandos para operar el stack.

### CI/CD

El workflow [`deploy-vps-main.yml`](.github/workflows/deploy-vps-main.yml) despliega únicamente en `push` a `main`. Omite eventos atrasados si el commit ya no es la punta de `main` y serializa los despliegues. Se activa cuando el workflow y sus cambios llegan a `main`; no se ejecuta en pull requests. Tiene además un `workflow_dispatch` con una entrada booleana (`activate_ios_profile`; `activate_ios_r52_profile` queda como alias obsoleto) para activar el perfil iOS fijado en `config/ios-profile-release.json`. El flujo de trabajo normal es: rama, pull request revisado y merge a `main`, que dispara el despliegue; no se despliega a producción a mano.

El despliegue selecciona los servicios según los archivos cambiados: API, Photon, CDN, balance, insignias y Caddy se reconstruyen cuando corresponde; cambios en `deploy/site/` sincronizan la landing; cambios de `config/`, `content/` o generadores marcados fuerzan la reconstrucción de la pila y del árbol CDN. Cambios de Compose, GeoIP o gRPC fuerzan la pila completa. El workflow hace checkout recursivo de submódulos y compila las imágenes en la VM.

En GitHub, el environment `production` necesita estos secretos y variables:

| Tipo | Nombre |
| --- | --- |
| Secret | `VPS_SSH_PRIVATE_KEY` |
| Secret | `VPS_KNOWN_HOSTS` |
| Variable | `VPS_HOST` |
| Variable | `VPS_USER` |
| Variable | `VPS_APP_DIR` (ruta absoluta) |
| Variable opcional | `VPS_SSH_PORT` (por defecto `22`) |

La VM debe tener Docker Compose, `rsync`, `flock`, `curl` y Python 3. El usuario SSH debe ejecutar Docker sin `sudo`. Antes del primer despliegue, prepara en `VPS_APP_DIR` el `.env` de producción y el directorio persistente `.local/`, además del árbol de assets `octo_sorted/`; el workflow no sube estos datos ni crea la VM, Neon o sus reglas de red. `DATABASE_URL` permanece en el `.env` del host, nunca en GitHub. Consulta [la guía de despliegue](deploy/VPS.md) para preparar y verificar ese estado.

Antes del cambio, valida todas las fuentes habilitadas del catálogo. Después comprueba `/health/ready`, el SHA-256 de un recurso servido por `/cdn/` y, si cambió el sitio, el SHA-256 de la landing servida. Ante un fallo, intenta restaurar el código y árbol CDN previos y vuelve a construir y levantar el stack. No revierte migraciones ni datos de Neon. El workflow comprueba salud de runtime; las pruebas de .NET y Python se ejecutan aparte con los comandos de desarrollo de arriba.

## Estructura

| Ruta | Contenido |
| --- | --- |
| `src/KickFlight.BootstrapApi/` | API .NET 8, sesiones, estado del jugador, datos de juego y matchmaking gRPC |
| `submodules/luxonserver/` | servidor de partidas Photon/LuxonServer |
| `config/` | masters, fixtures y catálogo de recursos |
| `content/` | bundles y archivos generados para el cliente |
| `scripts/` | build de APK, datos, CDN, despliegue y herramientas de diagnóstico |
| `deploy/` | Compose, Caddy, Nginx, Photon, insignias y landing pública |
| `tools/` | herramientas web de balance (`balance/`) e insignias (`badges/`) |
| `tests/` | pruebas Python y proyecto `KickFlight.BootstrapApi.Tests` |
| `docs/` | configuración, despliegue, Android, iOS, balance y documentación de ingeniería inversa |
| `AGENTS.md` | reglas operativas, builds DIAG y aprendizajes de ingeniería inversa |

Los archivos de trabajo como `.env`, la APK base, el keystore, los bundles grandes y los artefactos bajo `.local/` deben mantenerse fuera del control de versiones. [`PRESERVATION_POLICY.md`](PRESERVATION_POLICY.md) describe los límites del repositorio y [`docs/PHOTON_SERVER.md`](docs/PHOTON_SERVER.md) documenta el servidor Photon.

## Índice de documentación

- Puesta en marcha: [`docs/RECREATE_FROM_SCRATCH.md`](docs/RECREATE_FROM_SCRATCH.md), [`docs/ENVIRONMENT_REFERENCE.md`](docs/ENVIRONMENT_REFERENCE.md), [`docs/ANDROID_SETUP.md`](docs/ANDROID_SETUP.md), [`docs/ADB_LOOP.md`](docs/ADB_LOOP.md).
- Despliegue y datos: [`deploy/VPS.md`](deploy/VPS.md), [`deploy/README.md`](deploy/README.md), [`docs/DB_VPS_MIGRATION.md`](docs/DB_VPS_MIGRATION.md), [`docs/OPS_TELEMETRY.md`](docs/OPS_TELEMETRY.md), [`docs/REPLAYS.md`](docs/REPLAYS.md).
- Photon y partidas: [`docs/PHOTON_SERVER.md`](docs/PHOTON_SERVER.md), [`docs/BOT_MATCHMAKING.md`](docs/BOT_MATCHMAKING.md), [`docs/CUSTOM_BATTLE_PROTOCOL.md`](docs/CUSTOM_BATTLE_PROTOCOL.md).
- Balance y combate: [`tools/balance/README.md`](tools/balance/README.md), [`docs/COMBAT_MASTERS_FILL_IN.md`](docs/COMBAT_MASTERS_FILL_IN.md), [`docs/DISC_BALANCE_FIX.md`](docs/DISC_BALANCE_FIX.md).
- iOS: [`docs/IOS_RESOURCE_CATALOG.md`](docs/IOS_RESOURCE_CATALOG.md), [`docs/IOS_PROFILE_RELEASE.md`](docs/IOS_PROFILE_RELEASE.md).
- Ingeniería inversa: [`scripts/re/README.md`](scripts/re/README.md), [`docs/re-notes/`](docs/re-notes/), [`AGENTS.md`](AGENTS.md).
- Chat: [`docs/CHAT_TRANSLATION.md`](docs/CHAT_TRANSLATION.md) (traducción del chat: la hace otomo chat; el adaptador elige el idioma del lector).

## Atribuciones

- IP Geolocation by [DB-IP](https://db-ip.com): la traducción del chat elige el idioma según el país del cliente con la base "IP to Country Lite" de DB-IP, licenciada CC BY 4.0.
