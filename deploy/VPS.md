# Kick-Flight en OCI con Docker Compose

La instancia corre en una VM de OCI: Docker Compose ejecuta la API .NET, PostgreSQL 17.11, Luxon/Photon, Nginx
para el CDN y la landing, Caddy para HTTPS y un proxy Nginx para gRPC. La API usa PostgreSQL local en la
red privada de Compose y guarda sus datos en el volumen persistente `postgres-data`. El despliegue de CI y
la operación manual usan `deploy/docker-compose.vps.yml`. Las imágenes se construyen en la arquitectura de
la VM.

## Requisitos

- Una VM Linux con Docker Engine y Docker Compose v2.24.4 o posterior (el override de base externa usa `!override`).
- IPv4 pública estable o un nombre DNS que apunte a ella.
- Puertos TCP 80 y 443 para la página con HTTPS automático; además 18080, 18081, 5055, 5056 y 5058.
  Abre también UDP 5055, 5056 y 5058.
- Para la configuración completa de Luxon, abrir TCP y UDP 27000–27002 también.
- Almacenamiento persistente para `.local/` y el árbol `Kick-Flight-Assets/` con los bundles del cliente.

Los puertos 18080 (API/CDN) y 18081 (gRPC) usan TCP. Photon requiere UDP y TCP: el NameServer anuncia
Master/Game y el cliente puede usar ambos transportes. La API solo queda en la red privada de Compose. El
puerto público 18081 termina en nginx, que solo reenvía `/openmatch.Frontend/GetAssignments`; otras rutas,
incluidas las rutas de control, reciben 404. El frontend HTTP deja pasar únicamente los `POST /diag/upload` y
`POST /diag/upload-file` para recibir trazas; el formulario `/diag`, `/gym` y `/gym/*` siguen bloqueados. Las cargas
usan un volumen privado en `.local/diag-uploads`, con máximo de 2 GiB y limpieza de archivos de más de 14 días.
El servicio Caddy escucha en 80/443 en el host y reenvía las peticiones al CDN existente en 18080. Caddy
obtiene y renueva automáticamente el certificado de Let's Encrypt para `kick-flight-fenix.us.ci`; conserva
certificados y estado en los volúmenes Docker `caddy-data` y `caddy-config`. El DNS A del dominio debe
apuntar a la IP pública del VPS y el firewall de OCI y el del host deben permitir TCP 80/443.
La portada pública se sirve desde `deploy/site/index.html` en `/` mediante el contenedor `cdn`; usa la ruta
actual `/apk/Fenix-2.11.806.apk` para descargar la APK. En una instalación limpia, la
APK es el único archivo que se instala manualmente; el primer arranque descarga los recursos del juego desde
el CDN del VPS, por lo que se requiere conexión a internet.

## Despliegue automático desde `main`

El workflow [`.github/workflows/deploy-vps-main.yml`](../.github/workflows/deploy-vps-main.yml) se ejecuta
solo con `push` a `main`; no corre para pull requests y no tiene disparador manual. Serializa despliegues y
omite un evento si su SHA ya no es la punta de `origin/main`. Usa el environment de GitHub Actions llamado
`production`. Cuando el SHA `before` no está disponible, trata el cambio como despliegue completo.

### Preparar el host una vez

El workflow entrega código y fuente de Photon, pero no aprovisiona la VM, PostgreSQL, DNS, firewall, secretos,
APK, assets ni bundles fuera del repositorio. Antes de habilitarlo:

1. Instala Docker Engine con el plugin Docker Compose v2, `rsync`, `flock`, `curl`, Python 3 y herramientas
   estándar de Linux (`bash`, `tar`, `find`, `grep`, `sha256sum`). El usuario SSH de despliegue debe poder
   ejecutar Docker sin `sudo`, escribir en `VPS_APP_DIR` y crear/reemplazar el directorio `cdn/` dentro del
   árbol de assets.
2. Crea el directorio absoluto que se configurará como `VPS_APP_DIR`. Ahí se conservan `.env`, `.local/`,
   `.ci/`, el estado de Compose y los datos locales que el despliegue no administra.
3. Crea `VPS_APP_DIR/.env` con `KF_DB_PASSWORD` (rol de API `kickflight`, sin privilegios de superusuario)
   y `KF_PG_SUPERUSER_PASSWORD` (credencial administrativa separada), además de `KF_PUBLIC_HOST` sin
   esquema ni puerto, `KF_HTTP_PORT` (normalmente `18080`) y, si los assets viven en otra ruta,
   `KF_ASSETS_PATH` con su ruta absoluta. Protege este archivo en el host y no lo subas a Git ni
   a GitHub. La URL externa anterior no debe quedar como configuración efectiva.
4. Prepara `VPS_APP_DIR/.local/` con todos los archivos requeridos por las entradas habilitadas de
   `config/resources/catalog.json`. En el catálogo de esta versión son 36 fuentes: la APK remota y 35 bundles
   de fallback. El workflow nunca copia `.local/`.
5. Prepara el árbol de assets con `octo_sorted/`. Por defecto está en `Kick-Flight-Assets/`, hermano del
   checkout; para otra ubicación define `KF_ASSETS_PATH` en `.env`. El workflow tampoco copia estos assets.

El workflow puede hacer el primer despliegue de código dentro de ese directorio ya preparado, pero no puede
crear estos archivos o recuperar los assets protegidos. Para usarlo sobre una instalación manual existente,
apunta `VPS_APP_DIR` a la raíz que ya contiene `.env`, `.local/` y los assets externos. La primera ejecución
guarda una copia solo del código administrado para poder recuperarlo si falla el rollout.

### Configurar GitHub

Crea o usa el environment **`production`** del repositorio. Agrega ahí los siguientes secretos y variables:

| Tipo | Nombre | Valor |
| --- | --- | --- |
| Secret | `VPS_SSH_PRIVATE_KEY` | Clave privada SSH con acceso al host |
| Secret | `VPS_KNOWN_HOSTS` | Entrada `known_hosts` verificada para `VPS_HOST` |
| Variable | `VPS_HOST` | DNS o dirección IP de la VM |
| Variable | `VPS_USER` | Usuario SSH con acceso a Docker y al directorio de la aplicación |
| Variable | `VPS_APP_DIR` | Ruta absoluta al directorio de la aplicación |
| Variable opcional | `VPS_SSH_PORT` | Puerto SSH; usa `22` si se omite |

SSH usa verificación estricta de host (`StrictHostKeyChecking=yes`); la entrada configurada en
`VPS_KNOWN_HOSTS` debe coincidir con el host y, para un puerto distinto de 22, con el formato
`[host]:puerto`. `KF_DB_PASSWORD`, `KF_PG_SUPERUSER_PASSWORD` y el resto de la configuración de producción
permanecen en el `.env` de la VM, no como secretos de GitHub. El workflow no recibe ni inyecta
`DATABASE_URL` ni cadenas de conexión.

### Qué despliega

El workflow compara el SHA enviado con el cambio respecto al commit anterior y despliega solo componentes
afectados. Empaqueta el contenido versionado y el submódulo recursivo de Photon; deja fuera `.env`, `.local/`,
datos de jugadores, certificados, capturas, logs y el árbol externo de assets. Las imágenes se compilan en
la VM y el proyecto de Compose se fija como `deploy`.

| Cambios en | Acción |
| --- | --- |
| `src/`, `Dockerfile` o archivos Docker de raíz | Construye y reinicia la API; reinicia también los proxies HTTP y gRPC para resolver la nueva dirección del contenedor |
| El submódulo LuxonServer, `.gitmodules` o `deploy/photon/` | Construye y reinicia Photon |
| `deploy/nginx/` o `deploy/nginx.vps.conf` | Construye y reinicia el CDN |
| `deploy/site/` | Sincroniza la landing y verifica su respuesta servida |
| `deploy/caddy/` | Recrea Caddy |
| `config/`, `content/`, scripts generadores registrados, Compose, GeoIP o configuración gRPC | Despliegue completo; cuando corresponde, reconstruye todo el árbol CDN |

`config/resources/catalog.json`, los cambios bajo `content/` y los generadores de catálogo fuerzan también
la reconstrucción del farm CDN. La verificación previa exige que **cada fuente habilitada** del catálogo
exista y coincida con su SHA-256, incluidas las entradas de APK que viven bajo `.local/`. El constructor
prepara el árbol nuevo al lado del actual y lo intercambia atómicamente, conservando el árbol anterior
durante el smoke check.

### Validación y recuperación

Antes de arrancar servicios, el script comprueba la configuración de Compose. Al final espera que
`/health/ready` responda, descarga una entrada habilitada de `/cdn/` y compara su SHA-256 con el catálogo;
si cambió `deploy/site/`, también compara el HTML servido con `deploy/site/index.html`. Un fallo restaura el
árbol CDN anterior cuando hubo rebuild, sincroniza la versión de código previa e intenta volver a construir y
levantar Compose. Es una recuperación de código y archivos estáticos; no revierte migraciones, esquema ni
datos PostgreSQL.

El workflow no ejecuta `dotnet test` ni la suite Python: la comprobación de automatización es de configuración
y salud del servicio desplegado. Ejecuta esas pruebas por separado antes de integrar cambios a `main`. Su
script no usa `rsync --delete`: sincroniza los ficheros versionados que gestiona mediante manifiesto, y excluye
`.env`, `.local/`, `.ci/` y `data/`; el volumen PostgreSQL con nombre queda fuera del árbol sincronizado.

### Primer arranque manual y operación

El workflow no sustituye la preparación de la VM ni de los archivos persistentes. Para revisar una
instalación desde un checkout preparado, desde la raíz del repositorio ejecuta primero la validación de
Compose y catálogo; configura `KF_ASSETS_PATH` en `.env` si la ruta no es la hermana predeterminada:

```sh
docker compose --env-file .env --project-name deploy \
  -f deploy/docker-compose.vps.yml config --quiet
python3 scripts/build-cdn-tree.py --assets-root /ruta/absoluta/Kick-Flight-Assets \
  --repo-root . --verify-all-enabled --dry-run
python3 scripts/build-cdn-tree.py --assets-root /ruta/absoluta/Kick-Flight-Assets \
  --repo-root . --verify-all-enabled --atomic
docker compose --env-file .env --project-name deploy \
  -f deploy/docker-compose.vps.yml up -d --build
curl -fsS http://127.0.0.1:18080/health/ready
```

Usa siempre el mismo proyecto `deploy` al operar manualmente y desde Actions; de lo contrario Compose
creará una segunda pila. El CI usa `deploy/docker-compose.vps.yml` por defecto y solo selecciona el perfil
externo si `.env` contiene `KF_DB_MODE=external`, un marcador que escribe el rollback validado. Así una
`DATABASE_URL` residual no cambia el destino del API. El archivo `.env` está excluido del rsync administrado
y conserva la configuración; el volumen `postgres-data` tampoco lo administra rsync. El estado de
mantenimiento persiste en `.local/maintenance/maintenance.json`, montado como `/srv/repo/data`; conserva
ese archivo al recrear contenedores para no quitar el modo de mantenimiento activo. Hasta integrar este
cambio, un workflow antiguo pide `DATABASE_URL`; como se quita del `.env`, su `docker compose config
--quiet` falla antes de ejecutar `up` y deja los contenedores actuales intactos. El CI viejo puede fallar
hasta desplegar esta actualización, pero no reconectará la API al externo. La configuración del host, APK,
catálogo y assets está en las secciones siguientes. El despliegue selectivo normal se hace con un `push`
que actualice el workflow en `main`; para revertir una versión, revierte el commit en `main` y deja que el
workflow despliegue ese commit.

## Preparar catálogo, APK y assets

El endpoint `/health/ready` comprueba el catálogo entero: cada ruta `sourcePath` debe existir y coincidir con
su SHA-256. En el catálogo actual hay **36 fuentes bajo `.local/`**: la APK
`.local/KickFlight-2.11.0-remote-kickflightsg.apk` y 35 bundles generados en
`.local/weapon-costume-fallbacks/`. Por eso copiar únicamente `octo_sorted/` no deja listo el release.

Prepara junto al checkout la carpeta `Kick-Flight-Assets/octo_sorted/` con los bundles que corresponden al
catálogo y a `config/`/`content/`. Los sourcePaths del proyecto y los generadores de fallback resuelven esa
ruta como una carpeta hermana del repositorio. El árbol suele ser alrededor de 1 GB. Si lo guardas en otro
lugar, crea el enlace relativo `../Kick-Flight-Assets` hacia esa carpeta para los generadores y define la
misma ubicación en `KF_ASSETS_PATH`.

```sh
git submodule update --init --recursive
mkdir -p ../Kick-Flight-Assets
# Copia/sincroniza aquí el directorio octo_sorted con los bundles fuente del juego.
```

Los generadores de bundles requieren Python 3 y UnityPy 1.25.3. La APK también se debe compilar con los
requisitos de `scripts/build-direct-apk.sh`: `base.apk` original, Java, apktool 3.0.3 y Android build-tools
35.0.0. Para ARM64 puedes preparar los assets/APK en otra máquina y sincronizar los resultados junto con el
checkout; el Compose compila Luxon en la arquitectura nativa del VPS.

Genera una APK de producción con el endpoint público del VPS y el Photon NameServer. El **nombre de salida
debe conservarse**: lo usa la entrada actual del catálogo.

```sh
KF_PUBLIC_HOST="$(awk -F= '$1=="KF_PUBLIC_HOST" {print $2}' .env)"
KF_HTTP_PORT="$(awk -F= '$1=="KF_HTTP_PORT" {print $2}' .env)"
SERVER_BASE_URL="http://${KF_PUBLIC_HOST}:${KF_HTTP_PORT}" \
KF_PHOTON=1 \
KF_PHOTON_HOST="${KF_PUBLIC_HOST}" \
OUTPUT_APK=.local/KickFlight-2.11.0-remote-kickflightsg.apk \
scripts/build-direct-apk.sh
```

Ahora regenera el catálogo **después** de producir la APK. Este paso calcula su SHA-256 actual, vuelve a
crear los 35 clones físicos de arma desde los donantes de `octo_sorted/`, y ejecuta los generadores de
miniaturas que forman parte del catálogo. Así los hashes fijados en `catalog.json` coinciden con los bytes
que se servirán. Antes, incrementa `revision` en `title-minimum.json` y añade la revisión anterior a
`fromRevisions`. El builder no incrementa esa versión: sin el cambio, los clientes con la revisión anterior
podrían reutilizar los recursos cacheados y no descargar las fuentes actualizadas.

```sh
python3 - <<'PY'
import json
from pathlib import Path

path = Path("config/resources/title-minimum.json")
title = json.loads(path.read_text(encoding="utf-8"))
previous = int(title["revision"])
title["revision"] = previous + 1
title["fromRevisions"] = sorted(set(title.get("fromRevisions", [])) | {previous})
path.write_text(json.dumps(title, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print(f"Octo title revision: {previous} -> {title['revision']}; added fromRevision {previous}")
PY
python3 -m venv .local/assets-venv
.local/assets-venv/bin/pip install UnityPy==1.25.3
.local/assets-venv/bin/python scripts/build-title-resource-catalog.py \
  --server-base-url "http://${KF_PUBLIC_HOST}:${KF_HTTP_PORT}"
python3 scripts/update-apk-catalog-sha.py
python3 scripts/build-cdn-tree.py --assets-root "$(realpath ../Kick-Flight-Assets)" \
  --repo-root . --verify-all-enabled --dry-run
python3 scripts/build-cdn-tree.py --assets-root "$(realpath ../Kick-Flight-Assets)" \
  --repo-root . --verify-all-enabled --atomic
```

`build-title-resource-catalog.py` actualiza las filas CDN administradas, pero no toca las filas `apk-*`.
Por eso `update-apk-catalog-sha.py` va después de generar el catálogo: actualiza el hash de la APK recién
compilada en `config/resources/catalog.json`. Sin ese paso, `/health/ready` rechaza el catálogo aunque el
archivo de la APK exista.

`build-title-resource-catalog.py` selecciona ese entorno para ejecutar `scripts/add_weapon_costume_aliases.py`
y `scripts/build-kicker-skin-thumbnail-bundles.py`; ambos toman los assets fuente del árbol configurado en
el catálogo. Para no compilar el APK en el VPS, copia a ese mismo checkout el artefacto con el nombre exacto,
el catálogo/fixtures regenerados y los bundles generados bajo `.local/` antes de levantar Compose.

El API lee `config/`, `content/` y `.local/` en modo solo lectura. Las cuentas y el progreso se guardan en
PostgreSQL local, en el volumen `postgres-data`. PostgreSQL solo está en la red privada `backend`; ninguna
configuración publica el puerto 5432. SQLite del panel de balance permanece en su archivo propio y no forma
parte de la base de jugadores.

## PostgreSQL local

```sh
cp deploy/.env.vps.example .env
openssl rand -hex 32  # genera KF_DB_PASSWORD
openssl rand -hex 32  # genera KF_PG_SUPERUSER_PASSWORD
```

Edita el `.env` de la raíz de aplicación: asigna a `KF_PUBLIC_HOST` la IPv4 pública o el DNS, sin `http://`
ni puerto, y genera valores aleatorios distintos para `KF_DB_PASSWORD` y `KF_PG_SUPERUSER_PASSWORD`. El
primero autentica al API con el rol no-superusuario `kickflight`; el segundo se reserva para administración
de PostgreSQL. Si guardas los assets en otra ubicación, cambia `KF_ASSETS_PATH` a una ruta absoluta. El
archivo `.env` contiene credenciales y debe permanecer privado.

La dirección se aplica en dos sitios: Luxon responde a los clientes con esa IP/DNS para los endpoints de
Photon; la API permite ese host en las peticiones del juego. La API prueba la salud de Photon usando el DNS
interno de Compose `photon`, así que no depende de que el VPS pueda hacer hairpin hacia su propia IP pública.

## Construir e iniciar

```sh
docker compose --env-file .env --project-name deploy -f deploy/docker-compose.vps.yml config
docker compose --env-file .env --project-name deploy -f deploy/docker-compose.vps.yml up -d --build
docker compose --env-file .env --project-name deploy -f deploy/docker-compose.vps.yml ps
```

La primera compilación de Luxon descarga dependencias y puede tardar varios minutos. PostgreSQL debe estar
healthy antes de que arranque la API; Photon también debe estar healthy. Nginx espera a que la API esté
healthy. La API aplica migraciones de la base de datos al iniciar. El bootstrap crea la base `kickflight` y
el login `kickflight` como propietario no-superusuario; PostgreSQL queda solo en la red `backend`, sin
publicar `5432`. En volúmenes ya inicializados, los scripts de `docker-entrypoint-initdb.d` no se vuelven a
ejecutar: la migración debe dejar los roles existentes en ese mismo estado.

## Copias y rollback de la base de jugadores

El [registro de migración](../docs/DB_VPS_MIGRATION.md) recoge la comparación del restore y la evidencia
del corte. El VPS conserva los dumps diarios en `/opt/kickflight/.local/db-migration-20261007/backups/`. El timer
`kickflight-db-backup.timer` ejecuta el servicio `kickflight-db-backup.service` todos los días a las 02:15
UTC y retiene 14 días. El directorio es modo `0700` y los dumps modo `0600`.

```sh
sudo install -o ubuntu -g ubuntu -m 0755 scripts/backup-kickflight-db.sh /opt/kickflight/scripts/
sudo install -o ubuntu -g ubuntu -m 0755 scripts/rollback-kickflight-db-to-external.sh /opt/kickflight/scripts/
sudo install -o ubuntu -g ubuntu -m 0644 scripts/player-db-fingerprint.sql /opt/kickflight/scripts/
sudo install -o root -g root -m 0644 deploy/systemd/kickflight-db-backup.service /etc/systemd/system/
sudo install -o root -g root -m 0644 deploy/systemd/kickflight-db-backup.timer /etc/systemd/system/
sudo install -d -o ubuntu -g ubuntu -m 0700 /opt/kickflight/.local/docker-backup-config
sudo systemctl daemon-reload
sudo systemctl enable --now kickflight-db-backup.timer
sudo systemctl start kickflight-db-backup.service  # produce and verify the first dump now
systemctl list-timers kickflight-db-backup.timer
systemctl status kickflight-db-backup.service --no-pager
ls -lh /opt/kickflight/.local/db-migration-20261007/backups/
```

Estos dumps están en el mismo VPS: sirven para errores operativos, pero no protegen contra la pérdida del
host o de su almacenamiento. Mantén una copia cifrada fuera del VPS para recuperación ante desastre.

El rollback ejecutable y versionado está en `/opt/kickflight/scripts/rollback-kickflight-db-to-external.sh`.
Detiene el escritor API, genera y conserva un dump actual de PostgreSQL local, comprueba que la base externa
coincida con el destino privado `external-target.json` y la huella privada `external-baseline.json` del corte y toma otro dump externo antes de
restaurar. Si el externo cambió desde el corte, aborta sin sobrescribirlo. Si coincide, restaura el estado
local en una sola transacción y arranca solo la API con el perfil externo, incluyendo así las escrituras
posteriores al corte. El `.env` externo de antes del corte se conserva como `pre-cutover.env` dentro del
mismo directorio privado; no imprimas ni copies sus secretos. Al completar el rollback, el script actualiza
`.env` con `KF_DB_MODE=external` y la URL protegida para que los despliegues posteriores mantengan ese modo
explícito. Para volver a local después, primero migra las escrituras externas recientes a `postgres-data`;
cambiar solo `KF_DB_MODE` podría retroceder datos.

### Perfil externo de recuperación

Para una recuperación autorizada hacia PostgreSQL externo, usa `deploy/docker-compose.vps.external-db.yml`.
Este modo levanta API, Photon, CDN y gRPC, y conecta la API al `DATABASE_URL` del `.env` en la raíz del
directorio de aplicación. El perfil está excluido del CI normal y se reserva para una recuperación de datos.
Ese mismo `.env` debe definir `KF_PUBLIC_HOST` y puede definir `KF_HTTP_PORT` (por defecto `18080`) y
`KF_ASSETS_PATH` (absoluta; por defecto, `Kick-Flight-Assets/` junto al directorio de aplicación). La URL estándar
`postgresql://usuario:contraseña@host/base?sslmode=require` se convierte a parámetros de Npgsql al iniciar;
la conexión exige TLS. No copies la URL a `deploy/.env` ni a un archivo versionado. Después del corte,
`.env` ya no contiene esa URL; usa el script de rollback anterior en vez de ejecutar este perfil a mano.

Desde la raíz del repositorio:

```sh
docker compose --env-file .env --project-name deploy -f deploy/docker-compose.vps.external-db.yml config
docker compose --env-file .env --project-name deploy -f deploy/docker-compose.vps.external-db.yml up -d --build
docker compose --env-file .env --project-name deploy -f deploy/docker-compose.vps.external-db.yml ps
```

El archivo externo omite el contenedor PostgreSQL local y su volumen; no combines este modo con
`docker-compose.vps.yml`. Conserva los montajes de `config/`, `content/`, `.local/` y `Kick-Flight-Assets`:
el API los necesita para validar el catálogo y servir las fuentes. Los bundles grandes pueden migrarse después
a un CDN externo; habrá que actualizar las URL del catálogo y comprobar `/health/ready` antes de retirar ese
montaje.

En el firewall del proveedor y el firewall del sistema abre los puertos de la tabla anterior. No abras
5432, 8080 ni 5088. 5088 es el panel web de Luxon y no hace falta para jugar.

## Crear una APK para este VPS

La dirección del servidor queda parcheada dentro del cliente; configurar Compose no cambia una APK anterior.
Usa el flujo de generación anterior antes de construir CDN y desplegar. Si cambias `KF_HTTP_PORT`, se
actualiza el valor usado para `SERVER_BASE_URL`. Mantén `KF_DIAG`, `KF_NRE_LR` y otras variables de
diagnóstico desactivadas para el release. La ruta `/apk/remote` sirve la APK con el nombre de catálogo.

## Comprobar y operar

```sh
curl -m 15 -fsS https://kick-flight-fenix.us.ci/health/ready
curl -m 15 -fsS http://127.0.0.1:18080/health/ready
curl -i http://127.0.0.1:18080/diag  # debe devolver 404
docker compose --env-file .env --project-name deploy \
  -f deploy/docker-compose.vps.yml logs --tail=100
cat .ci/current-sha  # commit desplegado por el workflow, si ya corrió
```

`/health/ready` comprueba el catálogo, fixtures y conectividad con Photon. La prueba real es instalar la APK
en un teléfono fuera de la red del VPS, descargar recursos, iniciar batalla, jugar hasta el resultado y
reiniciar el stack comprobando que la misma cuenta conserve su progreso.

Para detener y volver a iniciar los contenedores sin borrar datos:

```sh
docker compose --env-file .env --project-name deploy \
  -f deploy/docker-compose.vps.yml down
docker compose --env-file .env --project-name deploy \
  -f deploy/docker-compose.vps.yml up -d
```

Evita `down -v`: además de los volúmenes de Caddy y GeoIP, borraría `postgres-data` y la base de jugadores.

## Push FCM de "alguien busca ranked"

- El API envía una notificación FCM (HTTP v1) cuando alguien entra a cola ranked. Los clientes registran su token en `POST /push/register`.
- Credenciales (cuenta de servicio de Firebase): en el host `.local/secrets/fcm-service-account.json`, visible en el contenedor `api` como `/srv/repo/.local/secrets/fcm-service-account.json` (el repo se monta de solo lectura). Permisos recomendados: `chmod 600` y directorio `chmod 700`; nunca en git ni en logs.
- Ajustes (`Push__*`, ya definidos en `docker-compose.vps.yml`): `Push__Enabled`, `Push__CredentialsPath`, `Push__Title`, `Push__Body`, `Push__GlobalCooldownSeconds` (240), `Push__PerPlayerCooldownSeconds` (240), `Push__SkipRecentlyActiveSeconds` (120), `Push__TtlSeconds` (60), `Push__MaxConcurrentSends` (8). Por defecto `Enabled=false`; sin archivo de credenciales no hace nada.

## WebUI de balance (masters con overrides persistentes)

El servidor no lee los masters de PostgreSQL: `DemoSessionApi` carga `config/masters_*.json` una sola vez al
arrancar. Para ajustar valores sin tocar `config/` (que el deploy de CI reemplaza entero), la API tiene una capa
de overrides: si existe `<override dir>/masters_<tabla>.json`, se sirve ese fichero en lugar del de `config/`.
El directorio por defecto es `.local/masters-overrides` resuelto con `RepositoryPaths`; dentro del contenedor
es `/srv/repo/.local/masters-overrides` y en el host `/opt/kickflight/.local/masters-overrides` (el mismo
directorio: `.local/` está montado y el rsync de CI lo excluye, así que sobrevive a cada deploy). La API
registra al arrancar una línea con las tablas que han quedado overrideadas. Los masters que son constantes
inline de C# (`Field`, `BattleRank`, `Guardian`, ...) no tienen fichero y no se pueden overridear.

La WebUI (`tools/balance/`) llega con el árbol que sincroniza el CI a `/opt/kickflight/tools/balance/`. Se
ejecuta como servicio systemd `kickflight-balance` en `127.0.0.1:8765`, con el fichero de entorno
`/etc/kickflight-balance.env` (modo 0600) y estos directorios:

* base: `/opt/kickflight/config`
* overrides: `/opt/kickflight/.local/masters-overrides`
* copias: `/opt/kickflight/.local/balance-backups`

Acceso a la WebUI de balance:

```sh
# inicia sesión en https://kick-flight-fenix.us.ci/balance/
```

Al guardar, la WebUI escribe **solo** en el directorio de overrides y guarda copia del fichero anterior. Cada
sección indica por tabla si está overrideada y ofrece **Revert to base** (borra el override, con copia previa) y
**Export overrides** (zip de los ficheros). Para convertir un ajuste en el valor por defecto: descarga el zip,
copia los `masters_*.json` a `config/` del checkout y haz commit; al llegar a `main`, el CI reconstruye la API
(`config/` marca `api` y `full`) y luego los overrides se pueden revertir tabla por tabla. El botón
**Restart API to apply** avisa de que las partidas en curso se caen, ejecuta `docker restart deploy-api-1` y
espera a `/health/ready`; hasta entonces la UI muestra los cambios pendientes. Detalle completo en
`tools/balance/README.md` y `tools/balance/deploy/README.md`.

### Prioridad Repo → Overrides → Base (merge en el despliegue)

Un override sustituye por completo a `config/masters_<tabla>.json`, y `.local/` no se despliega, así que un
cambio de master publicado en el repo quedaba tapado. `scripts/deploy-vps-ci.sh` ejecuta ahora
`scripts/merge-master-overrides.py` tras verificar la release y **antes** de sincronizar el árbol y de
reiniciar la API (la API lee los masters una sola vez al arrancar, así que carga ya los datos fusionados):

- Solo se tocan las tablas que tienen override **y** cuyo JSON del repo cambió entre BASE (último commit
  fusionado) y el commit nuevo. Sin override no se hace nada (la API ya sirve el fichero nuevo del repo).
- Fusión a tres bandas por `id` y por campo: si el repo cambió un campo respecto a BASE, gana el repo (aunque
  el override también lo hubiera editado: se registra como `CONFLICT`); si solo cambió el override, se
  conserva. Filas nuevas en el repo se añaden, filas borradas en el repo se borran, filas solo del override se
  conservan. Una fila que el override borró solo se restaura si el repo la modificó. Una tabla sin `id` único
  se sustituye entera por la del repo con aviso `WARNING` (hoy ninguna tabla de `config/` está en ese caso).
- Mantiene el formato del override (la WebUI escribe `json.dumps(rows, ensure_ascii=False, indent=1)`),
  copia antes el fichero a `.local/balance-backups/<tabla>-<YYYYmmdd-HHMMSS>.json` (mismo nombre que usa la
  WebUI, así su vista de diff compara contra el estado previo), escribe de forma atómica conservando
  propietario y permisos y vuelve a comprobar que la WebUI no haya guardado mientras tanto (si lo hizo,
  replanifica). Si algo falla, los overrides quedan intactos y el despliegue se aborta antes de reiniciar nada.
  Ejecutarlo dos veces con los mismos commits no cambia nada.
- BASE: el marcador `/opt/kickflight/.local/deployed-masters-commit` (se escribe tras cada fusión correcta); su
  árbol está en `.ci/releases/<sha>/config`. Si el marcador no existe (primer despliegue con esta función) se
  usa el commit realmente desplegado según `.ci/current-sha` (y, en la primera ejecución del CI, la copia
  `.pre-ci`). Se usa el estado de la VPS y no `github.event.before`, que puede diferir de lo desplegado tras un
  despliegue omitido o fallido. Si no se puede determinar BASE, aborta; nunca sobrescribe todo.
- Al arrancar, la API avisa por cada override cuyas filas difieren de `config/` (filas modificadas / solo en
  el override / solo en config), para ver ajustes del equipo de balance y arreglos que no llegaron.

Simulación sin escribir nada (en la VPS o en local), con el árbol `config/` de la release nueva:

```sh
python3 scripts/merge-master-overrides.py --app-dir /opt/kickflight \
  --repo-dir /opt/kickflight/.ci/releases/<sha>/config --commit <sha> --dry-run
# local, con revisiones git y una copia de los overrides:
python3 scripts/merge-master-overrides.py --git-root . --base-rev <antes> --repo-rev <despues> \
  --override-dir <copia> --backup-dir <tmp> --dry-run
```

La WebUI corre como servicio del host y no usa bloqueos: la fusión no puede detenerla sin `sudo`, por eso se
apoya en la comprobación optimista antes del `rename`. Tests: `python3 -m unittest tests.test_merge_master_overrides`.
