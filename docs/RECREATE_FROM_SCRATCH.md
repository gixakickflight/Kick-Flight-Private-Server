# Recrear el servidor privado en tu propia infraestructura

Guía de punta a punta para levantar una copia funcional de este servidor privado
en máquinas que no son las de este repositorio: la API, su base de datos
PostgreSQL, el CDN de assets, el servidor Photon y el cliente Android parcheado.

Está escrita para alguien que **no** tiene acceso a la máquina del autor. Cuando
algo depende de un valor local del autor (una IP, una ruta, un dataset) se dice
explícitamente en la tabla de [§9](#9-sustituye-los-valores-del-autor), que es la
sección que hay que leer antes de empezar.

Documentos relacionados, que no se repiten aquí:

| documento | qué cubre |
| --- | --- |
| [ENVIRONMENT_REFERENCE.md](ENVIRONMENT_REFERENCE.md) | todas las claves de configuración y variables de entorno, una por una |
| [ADB_LOOP.md](ADB_LOOP.md) | el bucle de validación en el emulador (contrato de subagente, presupuesto de contexto, screenshots) |
| [PHOTON_SERVER.md](PHOTON_SERVER.md) | el fork de LuxonServer, sus parches y el despliegue en la VM pública |
| [ANDROID_SETUP.md](ANDROID_SETUP.md) | preparación del dispositivo/emulador, CA y proxy de captura |
| [COMBAT_MASTERS_FILL_IN.md](COMBAT_MASTERS_FILL_IN.md) | semántica columna a columna de los masters de combate |
| [../deploy/README.md](../deploy/README.md) | el despliegue concreto del autor en su TrueNAS, paso a paso |
| [../PRESERVATION_POLICY.md](../PRESERVATION_POLICY.md) | lo que este repositorio **no** puede contener |

## 0. Qué NO está en el repositorio

Sin estas piezas no hay servidor, y **no** se pueden clonar desde aquí. Hay que
conseguirlas por separado:

1. **La APK base de Kick-Flight 2.11.0** (`base.apk`, ~143 MB). Es propiedad de
   Grenge y no se distribuye en este repositorio. El parcheo se aplica *sobre*
   ella; el repositorio sólo contiene el parcheador y los assets derivados.
2. **El árbol de assets** (`octo_sorted/`, ~871 MB, 2601 archivos catalogados).
   Se extrae de la APK base con las herramientas del proyecto de assets; el
   servidor lo lee desde fuera del repositorio.
3. **Los bundles locales** (`content/resources/actioneditor/*.bundle` y
   `content/resources/ui-disc/*.bundle`). `*.bundle` está en `.gitignore`, así que
   un clon nuevo tiene `content/resources/` vacío y hay que regenerarlos (ver
   [§8](#8-fase-6--el-bucle-de-desarrollo-diario)).
4. **Un keystore de firma propio.** El cliente parcheado se firma con una clave
   local; cualquiera sirve, pero tiene que ser *tuya* y consistente entre
   compilaciones, porque Android rechaza una actualización firmada con otra clave.
5. **Tus propias máquinas.** El autor usa un TrueNAS como host de la API y del
   PostgreSQL, y una VM pública para Photon. Cualquier equipo con Docker y 2 GB
   de RAM sirve para lo primero; lo segundo necesita una IP pública y los puertos
   abiertos.

### Sobre el SHA-256 de `base.apk`

El repositorio menciona dos hashes distintos y conviene entender por qué antes de
sospechar que tu copia está mal:

| hash | dónde aparece | qué es |
| --- | --- | --- |
| `F79F1B48F86C4F5973C763CBC6C166BD6C42CC83D4E36ECA75D7D1CAB74AD8D1` | `PRESERVATION_POLICY.md`, `config/apk-direct-server.example.json`, `scripts/check-prerequisites.ps1`, `scripts/verify-apk-hash.ps1` | la copia del autor original del repositorio |
| `2993d66ebd27d26f6bc8c8234aefa992c8d60e24afaf0d12b1b63afdaf315112` | `docs/CONTINUATION_PROMPT_BATTLE_CRASH_FIX_V2.md` | la copia con la que están verificadas las guardas del parcheador actual |

Los dos son APK 2.11.0 utilizables. Lo que importa es que **las guardas del
parcheador son específicas de la base**: cada parche declara el par de bytes que
espera encontrar en un offset y aborta si no coincide. Si tu `base.apk` es una
tercera variante, el `--dry-run` fallará en la primera entrada que difiera — eso
es la comprobación funcionando, no un error del parcheador. Compruébalo antes de
construir nada (ver [§5](#5-fase-3--el-cliente-android-parcheado)).

## 1. Arquitectura

Cuatro piezas, y sólo una de ellas tiene que estar en una máquina con IP pública:

```
   Teléfono / emulador (cliente parcheado)
     |                                    \
     | HTTP 18080 (base URL)               \  UDP/TCP 5055/5056/5058 (Photon)
     v                                      v
   [ host de la API ]                    [ VM pública ]
     nginx :18080  ------+                 LuxonServer (fork de Photon)
       /cdn/  -> /assets |                   - matchmaking y salas
       /dl/   -> /assets |                   - el tráfico de partida no pasa
       /      -> API     |                     por la API
     |                   v
     |          API .NET 8 :8080
     |            - sesiones, ranking, tienda, gacha, masters
     |            - D2C, catálogo de assets
     |                   |
     |                   v
     |          PostgreSQL :5432
     |            - jugadores, sesiones, inventario (estado canónico)
     |
     +---- gRPC 18081 (matchmaking) ----+
```

- **El cliente sólo habla con dos sitios**: la base URL (todo el HTTP) y el host
  Photon (la partida). La URL del CDN se la da la API en cada respuesta y se
  deriva del `Host` con el que llegó la petición, así que un cliente en la LAN y
  otro por DDNS reciben cada uno URLs que le funcionan, sin configuración extra.
- **nginx es la única puerta de entrada HTTP.** Está delante de la API en el
  mismo puerto para que el cliente no necesite dos puertos distintos, y sirve
  `/cdn/` directo desde disco (sendfile) para que ~1 GB de descargas no pase por
  el proceso .NET.
- **El puerto 18081 va directo al contenedor de la API**, no por nginx: es
  HTTP/2 (gRPC) y nginx aquí no lo proxya. Si el cliente es remoto, ese puerto
  también tiene que estar abierto y reenviado.
- **PostgreSQL es el estado canónico del jugador**: identidad, nombre, rango,
  inventario y decks. Los archivos JSON por dispositivo siguen existiendo como
  respaldo para quien corra la API sin base de datos.
- **Photon no es parte de la API.** Es un servidor aparte (un fork de
  LuxonServer) porque el cliente habla el protocolo Photon para las partidas; la
  API sólo le dice al cliente a qué host conectarse… y de hecho ni eso: **el host
  Photon va grabado en la APK**, no viene de la API.

## 2. Requisitos por máquina

### 2.1 Máquina de desarrollo (donde construyes el cliente y editas datos)

| requisito | versión | para qué |
| --- | --- | --- |
| Python | 3.10+ | generadores de masters, herramientas de RE, parcheador |
| JDK | 17 (o el que exija apktool 3.0.3) | apktool, apksigner |
| apktool | 3.0.3 | decodificar y reconstruir la APK |
| Android build-tools | 35.0.0 | `zipalign`, `apksigner` |
| ADB + emulador o dispositivo | — | instalar, `logcat`, capturas |
| `keystone-engine` y `capstone` | — | **sólo** si vas a *generar* sondas DIAG nuevas; para construir con las existentes no hacen falta |
| `UnityPy` | — | obligatorio para construir el cliente (lo importa `scripts/patch-log-stacktrace.py`) |
| `lz4`, `pillow`, `numpy` | — | **sólo** para reconstruir bundles de assets |
| .NET SDK | 8.0 | compilar y probar la API |

### 2.2 Host de la API y la base de datos

Docker con Compose. 2 GB de RAM sobran para un servidor de este tamaño. No
necesita IP pública si el cliente va a entrar por la LAN; sí la necesita (o un
DDNS) si quieres que entren teléfonos desde fuera. Hay que abrir **18080 y
18081**.

### 2.3 VM pública de Photon

Ubuntu 24.04 (o equivalente) con `g++-14`/`gcc-14`, `cmake`, `ninja-build` y las
dependencias del proyecto (`libyaml-cpp-dev`, `libssl-dev`). Los puertos 5055,
5056 y 5058 abiertos en el firewall del proveedor. Detalles en
[§6](#6-fase-4--photon-luxonserver) y en [PHOTON_SERVER.md](PHOTON_SERVER.md).

## 3. Fase 1 — Clonar, incluido el submódulo

```bash
git clone <url-de-tu-fork> kickflight
cd kickflight
git submodule update --init --recursive
```

El submódulo `submodules/luxonserver` es el servidor de partidas. `.gitmodules`
apunta a la rama `tanuki-server` de `tanukifurhire/luxonserver`, que es donde
viven como commits los ocho parches que tocan `src/*.cpp` (`0001`-`0005` y
`0008`-`0010`, punta `a3539b8`); los dos restantes van dentro del submódulo
`Luxon/` y se aplican aparte (§6.4). El `main`
de ese fork y el de `Gixarde3/luxonserver` se quedan en el punto de partida
`a84ecc3` (que ya trae el commit de Kick-Flight de Gixarde3, `485d0d0`, pero
ninguno de los diez parches). Si `git submodule update` falla con
`reference is not a tree` / `not our ref`, el commit grabado no está en el
remoto configurado; añade el remoto que sí lo tiene y vuelve a intentarlo:

```bash
cd submodules/luxonserver
git remote add fork https://github.com/tanukifurhire/luxonserver.git
git fetch fork
git checkout <sha-registrado-en-el-repositorio-padre>
```

Puedes ver el sha exacto que espera el repositorio padre con:

```bash
git ls-tree HEAD submodules/luxonserver
```

Si prefieres partir del proyecto original en vez del fork, clona
`https://github.com/niansa/LuxonServer` y aplica los parches de
`scripts/luxonserver/*.patch` (y, dentro de `Luxon/`, `deploy/photon/luxon-patches/*.patch`) en orden numérico con `git am`; es exactamente lo
que se hizo para producir el fork, y [PHOTON_SERVER.md](PHOTON_SERVER.md) lo
documenta paso a paso.

> El submódulo tiene a su vez submódulos propios (`Luxon`, `tracy`, `vcpkg`,
> `doxygen-awesome-css`) que en un clon nuevo están **vacíos**. `Luxon` no es
> opcional: dos de los parches tocan `include/luxon/enet_peer.hpp`, que sólo
> existe ahí. `git submodule update --init --recursive` dentro del submódulo los
> trae.

## 4. Fase 2 — La API y PostgreSQL

Todo esto vive en `deploy/`. Si tu host es un TrueNAS, [deploy/README.md](../deploy/README.md)
tiene el procedimiento del autor con sus datasets y su Custom App; lo que sigue
es la versión independiente del sistema operativo.

### 4.1 Construye la imagen a mano

El compose **no tiene contexto de compilación** (`image: kickflight-api:local`,
sin `build:`), porque en el TrueNAS la imagen se construye aparte y se etiqueta.
Constrúyela tú desde la raíz del repositorio:

```bash
docker build -t kickflight-api:local .
```

El `Dockerfile` es multi-etapa (`sdk:8.0` → `aspnet:8.0`) y espera el árbol de
assets **un nivel por encima de la raíz del repositorio**, porque el catálogo
direcciona sus entradas como `../Kick-Flight-Assets/octo_sorted/...`. En el
contenedor eso se resuelve con `KF_REPO_ROOT=/srv/repo` + un montaje en
`/srv/Kick-Flight-Assets`.

### 4.2 Escribe tu compose

Copia `deploy/docker-compose.nas.yml` y cambia lo que apunta a las máquinas del
autor. La tabla de [§9](#9-sustituye-los-valores-del-autor) los lista todos; los
imprescindibles son estos:

| qué | valor del autor | tu valor |
| --- | --- | --- |
| los ocho montajes `/mnt/Tanuki_1/kickflight/...` | rutas del pool ZFS del autor | tus rutas |
| `PhotonServer__Host` | `51.79.241.70` | la IP de tu VM Photon |
| `Harness__DirectClientHosts__0..3` | IP del NAS, dos DDNS, IP del PC | tus hosts (**sin puerto**) |
| `KF_DB_PASSWORD` | `kickflight-local` por defecto | una contraseña tuya |

El esqueleto, ya sustituido:

```yaml
services:
  postgres:
    image: postgres:17-alpine
    container_name: kickflight-postgres
    restart: unless-stopped
    environment:
      POSTGRES_USER: kickflight
      POSTGRES_DB: kickflight
      POSTGRES_PASSWORD: ${KF_DB_PASSWORD:-kickflight-local}
      PGDATA: /var/lib/postgresql/data/pgdata
    volumes:
      - /tu/ruta/pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U kickflight -d kickflight"]
      interval: 5s
      timeout: 5s
      retries: 12

  cdn:
    image: nginx:1.27-alpine
    container_name: kickflight-cdn
    restart: unless-stopped
    depends_on: [api]
    ports:
      - "18080:18080"
    volumes:
      - /tu/ruta/assets:/assets:ro
      - /tu/ruta/repo/deploy/nginx.conf:/etc/nginx/nginx.conf:ro

  api:
    image: kickflight-api:local
    container_name: kickflight-api
    restart: unless-stopped
    depends_on:
      postgres:
        condition: service_healthy
    ports:
      - "18081:18081"          # gRPC: va directo, no pasa por nginx
    environment:
      HttpPort: "8080"
      GrpcPort: "18081"
      KF_REPO_ROOT: /srv/repo
      PlayerStore__ConnectionString: "Host=postgres;Port=5432;Database=kickflight;Username=kickflight;Password=${KF_DB_PASSWORD:-kickflight-local}"
      PhotonServer__Enabled: "true"
      PhotonServer__Host: "<IP-de-tu-VM-Photon>"
      PhotonServer__MasterServerPort: "5055"
      PhotonServer__GameServerPort: "5056"
      PhotonServer__NameServerPort: "5058"
      Harness__DirectClientHosts__0: "<IP-de-este-host>"
      Harness__DirectClientHosts__1: "<tu-DDNS>"
      Harness__PersistCaptures: "false"
    volumes:
      - /tu/ruta/repo/config:/srv/repo/config
      - /tu/ruta/repo/content:/srv/repo/content
      - /tu/ruta/datos:/srv/repo/data
      - /tu/ruta/repo/.local:/srv/repo/.local:ro
      - /tu/ruta/assets:/srv/Kick-Flight-Assets:ro
```

Cada una de esas claves está explicada en
[ENVIRONMENT_REFERENCE.md](ENVIRONMENT_REFERENCE.md). Las tres que más se olvidan:

- **`Harness__DirectClientHosts__*`** sin el puerto. Si falta tu host, *todas* las
  peticiones del cliente reciben **421** y el juego enseña un error de
  comunicación sin más pista.
- **`PlayerStore__ConnectionString`**: si la quitas, la API arranca con el
  almacén JSON y los jugadores dejan de persistir como esperas.
- **`KF_REPO_ROOT`**: sin ella la API **arranca y falla en la primera petición**,
  porque busca el `.sln` hacia arriba y en un contenedor no hay.

### 4.3 Levántalo y comprueba

```bash
cd /tu/ruta/repo/deploy
docker compose -f docker-compose.nas.yml up -d
docker compose -f docker-compose.nas.yml logs -f api
```

El esquema se crea solo en el primer arranque (tablas `players`, `sessions`,
`player_ranks`, `schema_meta` y la secuencia `player_id_seq`). Compruébalo:

```bash
docker compose -f docker-compose.nas.yml exec postgres \
  psql -U kickflight -d kickflight -c '\dt'
```

Y por HTTP, desde el host:

```bash
curl -m 15 http://<IP-de-este-host>:18080/health/live    # 200 siempre
curl -m 15 http://<IP-de-este-host>:18080/health/ready   # 200 si todo está en pie
```

`/health/ready` tarda ~4,5 s cuando la sonda de Photon tiene que fallar: si usas
`-m 3` verás un timeout que **no** significa que el servidor esté caído. Si
devuelve 503, mira el log de la API: dice exactamente qué fixture, qué entrada de
catálogo o qué puerto Photon falló.

### 4.4 El árbol de assets

nginx sirve `/cdn/<clave>` desde disco, y `<clave>` apunta a un archivo del árbol
de assets. El mapeo lo construye `scripts/build-cdn-tree.py` a partir de
`config/resources/catalog.json`:

```bash
python3 scripts/build-cdn-tree.py --assets-root /tu/ruta/assets --repo-root /tu/ruta/repo
```

> **Ese script borra y reconstruye todo `<assets-root>/cdn` en cada ejecución.**
> No pongas nada duradero ahí dentro: se pierde en la siguiente pasada. Por eso
> los APK para sideload van en `/dl/`, que es un directorio distinto.

Las entradas del catálogo que empiezan por `/cdn/` tienen que existir y, si
declaran `sha256`, coincidir: una discrepancia añade un error de catálogo y deja
`/health/ready` en **503** para siempre. Si has publicado la APK en `/apk/`, usa
`scripts/update-apk-catalog-sha.py` después de cada recompilación.

### 4.5 APK para descarga

`location /dl/` sirve archivos de `<assets-root>/dl/` sin cache inmutable, que es
lo que quieres para una APK que cambia de compilación en compilación. **No hay
script que lo rellene**: copia ahí tu APK a mano.

```bash
cp .local/KickFlight-2.11.0-release-phone.apk /tu/ruta/assets/dl/
# queda en http://<tu-host>:18080/dl/KickFlight-2.11.0-release-phone.apk
```

### 4.6 Lo que el contenedor tal como está no puede hacer

`/srv/repo/.local` se monta **de sólo lectura**, y las rutas de diagnóstico de la
API (`/diag*`) escriben ahí. En el despliegue del autor esas rutas
sencillamente fallan. Si vas a usarlas, monta ese volumen en lectura-escritura.

### 4.7 Desplegar cambios

`scripts/deploy-nas.sh` hace por SSH todo el ciclo del autor (rsync de `config/`,
`content/`, `deploy/` y `src/`; reconstrucción del farm de CDN; `docker build`;
`compose up`; reinicio de nginx si la API se recreó). Está atado a sus rutas y a
su NAS, pero sirve de plantilla: los pasos están ordenados y cada uno tiene su
comentario. En un host propio, el equivalente manual es:

```bash
docker build -t kickflight-api:local .        # desde la raíz del repo
python3 scripts/build-cdn-tree.py --assets-root /tu/ruta/assets --repo-root /tu/ruta/repo
cd deploy && docker compose -f docker-compose.nas.yml up -d && docker restart kickflight-cdn
```

El `docker restart kickflight-cdn` es necesario aunque nginx resuelva el nombre
del contenedor por DNS de Docker: al recrear la API, el `proxy_pass` puede
quedarse con la IP vieja hasta que nginx recargue.

## 5. Fase 3 — El cliente Android parcheado

El cliente no se recompila: se **parchea** la APK de Grenge. `apktool` la
desempaqueta, `scripts/patch-il2cpp-endpoints.py` reescribe las URLs y unos
cuantos literales dentro de `libil2cpp.so` y `global-metadata.dat`, y se vuelve a
empaquetar y firmar. La jugabilidad queda intacta; lo único que cambia es a qué
servidor llama.

### 5.1 La cadena de herramientas

| herramienta | versión | para qué |
| --- | --- | --- |
| apktool | **3.0.3** exactamente | decodificar y reconstruir |
| Android build-tools | **35.0.0** | `zipalign`, `apksigner` |
| JDK | 17 o superior, con `keytool` | generar el keystore, `apksigner` |
| Python | 3.10+ | el parcheador |
| `UnityPy` | última | `scripts/patch-log-stacktrace.py` lo importa **para construir**, no sólo para bundles |
| `keystone-engine`, `capstone` | — | sólo si vas a *generar* sondas DIAG nuevas |
| adb | — | instalar y leer `logcat` |

`UnityPy` es la dependencia que más se olvida: no aparece en ningún
`requirements.txt` ni en `check-prerequisites.ps1`, y sin ella el post-parche de
stack traces falla.

### 5.2 Antes de construir: comprueba tu `base.apk`

El parcheador no valida la APK por SHA-256; valida **byte a byte** cada sitio que
va a tocar. Puedes (y debes) hacer esa comprobación sin construir nada:

```bash
KF_DIAG=1 python scripts/patch-il2cpp-endpoints.py --dry-run --arm64 \
  lib/arm64-v8a/libil2cpp.so
```

El `libil2cpp.so` que le pases tiene que ser el de **tu** `base.apk` ya
desempaquetado con apktool. Si imprime
`Dry-run successful on <ruta>: verified N patches`, tus bytes coinciden y el
build funcionará. Si aborta en la primera entrada, tu `base.apk` es una variante
distinta de las dos que documenta el [§0](#sobre-el-sha-256-de-baseapk) y habrá
que reverificar las guardas — no lo fuerces con `--force`, porque escribir bytes
en el sitio equivocado no se nota hasta que el juego crashea.

### 5.3 Los constructores que hay en el repositorio

Hay dos scripts comprometidos y **ninguno funciona tal cual en una máquina
nueva**; conviene saber por qué antes de perder una tarde:

| script | problema |
| --- | --- |
| `scripts/build-direct-apk.sh` | es de macOS: usa `sed -i ''`, `shasum` y una ruta de build-tools de Homebrew. En Linux hay que cambiar esas tres cosas. |
| `scripts/build-direct-apk.ps1` | apunta por defecto a `C:\Users\Gixar\…\base.apk` y a un `jdk-25` que no existe; además lee `config/apk-direct-server.local.json`, que está ignorado en git (copia el `.example.json`). |

Y el que el autor usa de verdad, `.local/build.sh`, **no está en el repositorio**
(`.local/` está en `.gitignore`). No es un detalle menor: ese script hace tres
cosas que los dos comprometidos **no** hacen, y sin ellas la APK no se comporta
igual.

### 5.4 Lo que tiene que hacer tu build, en orden

1. `apktool d` sobre `base.apk`.
2. Parchear `AndroidManifest.xml`: `debuggable`, `largeHeap`,
   `allowNativeHeapPointerTagging="false"`, **`android:usesCleartextTraffic="true"`**
   (hablas HTTP en claro con tu servidor), y subir `targetSdkVersion` de 29 a
   **30** añadiendo `requestLegacyExternalStorage="true"` — Android 14+ rechaza
   el target 29 heredado de la tienda.
3. `patch-il2cpp-endpoints.py` con `--metadata`, `--arm64`, `--armv7`,
   `--arm64-unity` y `--base-url`.
4. `patch-octo-http-timeout.py <directorio-decodificado>` — sube el timeout HTTP
   de la conexión (10 s) y de lectura (10 s → 120 s). Sin esto, descargar el
   árbol de assets falla a la primera lentitud.
5. `patch-log-stacktrace.py <directorio-decodificado>` — devuelve los stack
   traces a las excepciones de Unity, que en la APK de tienda vienen recortados.
6. `apktool b`, `zipalign -p 4`, `apksigner sign`.

Los pasos 4 y 5 no son opcionales para el desarrollo: son la diferencia entre
"funciona" y "no sé por qué falla".

### 5.5 Las banderas del parcheador

| bandera | por defecto | qué hace |
| --- | --- | --- |
| `--base-url` / `SERVER_BASE_URL` | obligatoria | el host al que llama el cliente; `http://host[:puerto]`, sin ruta |
| `SOURCE_APK` | `base.apk` en la raíz del repo | la APK prístina de entrada |
| `ANDROID_BUILD_TOOLS` | ruta de macOS | dónde están `zipalign`/`apksigner` |
| `OUTPUT_APK` | `.local/artifacts/KickFlight-2.11.0-direct-<host>-<puerto>.apk` | salida |
| `KF_PHOTON` | apagada | activa el matchmaking real; **sin esta bandera la APK usa el puente offline y nunca toca Photon** (parece que funciona) |
| `KF_PHOTON_HOST` | el host de `--base-url` | el host Photon grabado en la APK; **pásala siempre explícita** |
| `KF_DIAG` | apagada | trazas `KFDIAG` en logcat (212 → 358 parches) |
| `KF_NRE_LR` | apagada | con `KF_DIAG`, imprime el origen de cada NRE |
| `KF_RESULT_DIAG` | apagada | traza aislada de la pantalla de resultado; **mutuamente excluyente con `KF_DIAG`** |
| `KF_FORCE_GAME_SCENE` | apagada en `.sh`, **encendida** en `.ps1` | fuerza la transición a la escena de juego |
| `KF_UNLOAD_BYPASS` | apagada | reactiva un bypass viejo del loader; provoca un SIGSEGV intermitente en `UnityPreload` — no la uses |
| `KF_NO_READY_SCENE` | apagada | restaura el bypass de la escena "ready" anterior al 2026-09-21 |

Dos notas sobre esto. `check-prerequisites.ps1` y los recuentos que cita
`AGENTS.md` («141 de producción → ~245 en DIAG») están **desactualizados**: con
el parcheador de hoy son 212 y 358. Y los dos constructores difieren entre sí por
defecto (`KF_FORCE_GAME_SCENE`), así que la misma entrada da APKs distintas según
cuál uses — elige uno y no los mezcles.

### 5.6 Firma

El cliente parcheado se firma con un keystore local. Sirve cualquiera, pero:

- El par de la APK de tienda es de Grenge, así que **la primera instalación
  sobre una copia de la tienda exige desinstalar** (`adb uninstall
  jp.grenge.kickflight`), y eso borra los datos locales del dispositivo.
- El keystore tiene que ser **el mismo** entre compilaciones, o Android rechaza
  cada actualización y hay que desinstalar otra vez.

Los scripts comprometidos generan uno solo si falta
(`.local/kickflight-test-signing.jks`, alias `kickflight-test`, contraseña
`android`) con `keytool -genkeypair … -dname 'CN=KickFlight Local Test,…'`. Si ya
tienes uno, apunta el build al tuyo y guárdalo fuera del repositorio.

### 5.7 Instalar y leer el log

```bash
adb install -r <tu-apk>.apk
adb shell am force-stop jp.grenge.kickflight
adb shell monkey -p jp.grenge.kickflight -c android.intent.category.LAUNCHER 1
```

Para diagnosticar, el contrato del proyecto es limpiar el buffer **antes** de la
prueba y volcarlo **después** (ver [AGENTS.md](../AGENTS.md)):

```bash
adb logcat -c
# ...prueba...
adb logcat -d -v time > .local/run/logcat-<nombre>.txt
adb logcat -d -s KFDIAG
```

Si vas a tener dos emuladores a la vez, ten cuidado: `start-client.bat`,
`capture-logcat.bat` y `screenshot.bat` **no** pasan `-s <serial>`, así que con
dos dispositivos conectados eligen uno cualquiera. Añade `-s emulator-5554`
cuando importe.

## 6. Fase 4 — Photon (LuxonServer)

Esta fase va en una máquina distinta, con IP pública. Es la única pieza que no
puede vivir detrás de tu router si quieres jugar desde fuera.

### 6.1 Dependencias

Ubuntu 24.04 con `g++-14`, `cmake`, `ninja-build` y las librerías del proyecto:

```bash
sudo apt-get update
sudo apt-get install -y g++-14 gcc-14 cmake ninja-build libyaml-cpp-dev libssl-dev git
```

**`g++-14` no es un capricho.** El código usa C++23 ("deducing this") y g++ 13 no
lo compila. La receta con `cmake .. && make` que aparece en documentos antiguos
del repositorio **no funciona** con este árbol; la buena es la de §6.4.

### 6.2 Consigue el código correcto

Es el mismo problema del [§3](#3-fase-1--clonar-incluido-el-submódulo), pero aquí
importa más: si clonas el upstream `niansa/LuxonServer` tal cual, **no tendrás
ninguno de los parches de Kick-Flight** y las partidas fallarán de formas que
parecen errores del cliente. Parte de la rama `tanuki-server` de
`tanukifurhire/luxonserver`, que ya los lleva como commits:

```bash
git clone --branch tanuki-server https://github.com/tanukifurhire/luxonserver.git
```

Si partes de otro sitio (el upstream, o el `main` de `Gixarde3/luxonserver`, que
se queda en `a84ecc3`), tendrás que aplicar los commits de
`scripts/luxonserver/*.patch`.

### 6.3 Inicializa los submódulos anidados

```bash
cd <árbol-de-luxonserver>
git submodule update --init --recursive
```

`Luxon` (donde vive `include/luxon/enet_peer.hpp`) y `tracy` tienen que estar
presentes antes de aplicar los parches 0006 y 0007.

### 6.4 Aplica los parches y compila

Los diez parches no van todos en el mismo sitio:

| parche | fichero | dónde se aplica |
| --- | --- | --- |
| 0001–0005 | `scripts/luxonserver/` | raíz de LuxonServer (`src/*.cpp`) |
| 0006, 0007 | `deploy/photon/luxon-patches/` | **dentro del submódulo `Luxon/`** (`include/luxon/enet_peer.hpp`) |
| 0008–0010 | `scripts/luxonserver/` | raíz de LuxonServer |

El puntero actual (`b35e3cf`, con `Luxon/` en `5401810`) ya lleva 0001–0005 y 0008–0010 como commits (0003
sustituido por `f160000`); 0006 y 0007, rebasados sobre `5401810`, los aplica `deploy/photon/Dockerfile` en cada
build. Si compilas a mano, aplícalos tú dentro de `Luxon/` con `git apply`. Detalle en
[PHOTON_SERVER.md](PHOTON_SERVER.md).

Orden y compilación (verbatim de [PHOTON_SERVER.md](PHOTON_SERVER.md)):

```bash
cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release \
  -DLUXON_SERVER_USE_SANMAKE=OFF \
  -DCMAKE_C_COMPILER=gcc-14 -DCMAKE_CXX_COMPILER=g++-14 && ninja -C build
```

> **Comprueba el estado real de 0008–0010 antes de fiarte.** Si partes de la rama
> `tanuki-server` (punta `a3539b8`) los ocho parches de la raíz ya son commits y este
> paso sobra; `0006` y `0007` se siguen aplicando dentro de `Luxon/`. En el checkout de
> la VM, [PHOTON_SERVER.md](PHOTON_SERVER.md) describe 0008, 0009 y
> 0010 como cambios sin comitear en el árbol de trabajo (observado el 2026-09-21). Se
> comprueba en un segundo con `git apply --check -R <parche>`: si el parche está
> aplicado, el chequeo inverso pasa. El 0010 es el que entrega el mastership a un
> peer que entra en una partida cuyo master se fue, y sin él la reconexión no
> funciona como se espera.

### 6.5 Configura el host público

El `config.yml` del repositorio está pensado para el emulador
(`external_address: 10.0.2.2:<puerto>` en los seis listeners, `EnableIPv6: false`,
`MaxConnections: 100`). **Cópialo y reescribe cada `external_address` con la IP
pública de tu VM**, o los clientes remotos recibirán direcciones inalcanzables:

```bash
sudo mkdir -p /opt/luxon-server
sudo cp build/luxon_server /opt/luxon-server/
sudo cp config.yml /opt/luxon-server/config.yml
sudo sed -i 's/10\.0\.2\.2/<TU-IP-PUBLICA>/g' /opt/luxon-server/config.yml
```

### 6.6 Servicio y firewall

El unit systemd que usa el autor (`Restart=always`, binario en
`/opt/luxon-server/`) **no está en el repositorio**; el que hay,
`submodules/luxonserver/debian/luxon-server.service`, es el del upstream y apunta
a otras rutas. Escribe el tuyo:

```ini
[Unit]
Description=LuxonServer (Photon-compatible)
After=network-online.target

[Service]
Type=simple
WorkingDirectory=/opt/luxon-server
ExecStart=/opt/luxon-server/luxon_server
Restart=always
RestartSec=5
User=luxon

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable --now luxon-server
sudo journalctl -u luxon-server -f
```

Firewall (y el grupo de seguridad del proveedor, si lo hay):

```bash
sudo ufw allow 5055/udp && sudo ufw allow 5055/tcp
sudo ufw allow 5056/udp && sudo ufw allow 5056/tcp
sudo ufw allow 5058/udp && sudo ufw allow 5058/tcp
```

El rango 27000–27002 corresponde a los puertos por defecto de un Photon on-premise
(27000 Name, 27001 Master, 27002 Game). La configuración de este proyecto usa
5055/5056/5058; abre el rango sólo si cambias el `config.yml`.

### 6.7 Actualizar el servidor en marcha

```bash
ninja -C build
sudo systemctl stop luxon-server && sudo cp build/luxon_server /opt/luxon-server/ && sudo systemctl start luxon-server
```

El `stop` antes del `cp` no es opcional: copiar sobre un binario en ejecución
falla con **"Text file busy"**. El autor guarda además una copia de la versión
anterior (`luxon_server.bak-pre0010`) para poder volver atrás.

### 6.8 Cómo se entera el cliente del host Photon

**No se entera por la API.** El host Photon va grabado dentro de la APK al
construirla, y sale de dos banderas del parcheador:

- `KF_PHOTON_HOST` — el host que sustituye al literal `ns.exitgames.com` de la
  APK. Si no la pasas, el parcheador usa **el host de `--base-url`**, y entonces
  el cliente apunta su NameServer al host de la API, donde no escucha nadie: la
  partida falla.
- `KF_PHOTON=1` — activa el flujo de matchmaking real contra LuxonServer. **Sin
  esta bandera la APK usa el puente offline** (sala local con bots) y *nunca
  toca Photon*: parece que todo funciona y por eso es una trampa silenciosa.

Cambiar `PhotonServer__Host` en el compose sólo afecta a la **sonda** de
`/health/*`; para mover la partida de verdad hay que recompilar la APK. Ver
[ENVIRONMENT_REFERENCE.md §4](ENVIRONMENT_REFERENCE.md).

## 7. Fase 5 — Verificación de punta a punta

En este orden. Cada paso aísla una pieza, así que el primero que falle es el que
hay que arreglar.

| # | comprobación | resultado esperado |
| --- | --- | --- |
| 1 | `curl http://<host>:18080/health/live` | `200` |
| 2 | `curl -m 15 http://<host>:18080/health/photon` | `200` (prueba TCP a 5055/5056/5058) |
| 3 | `curl -m 15 http://<host>:18080/health/ready` | `200`; si es `503`, el cuerpo dice qué falta |
| 4 | `curl -H 'Host: otro.example' http://<host>:18080/boot/index` | `421` con `local-host-not-allowed` → la puerta de hosts funciona |
| 5 | `curl -o /dev/null -w '%{http_code}\n' http://<host>:18080/cdn/<una-clave>` | `200`; y en el log de nginx aparece la petición, **no** en el de la API |
| 6 | `nc -vz <host> 18081` | abierto (gRPC del matchmaking) |
| 7 | `docker compose exec postgres psql -U kickflight -d kickflight -c 'select count(*) from players'` | responde (el esquema se creó) |
| 8 | Emulador con la APK parcheada: título → `TAP START` | llega al título y entra |
| 9 | Dispositivo **nuevo** | pide nombre (no llega al home sin él) |
| 10 | Envía un nombre; luego otro dispositivo | entra al home; el segundo dispositivo pide nombre también |
| 11 | Reinicia el contenedor de la API y vuelve a entrar con el mismo dispositivo | **mismo id y mismo nombre** |
| 12 | Partida contra bots | llega a `GameScene` y termina en resultado |
| 13 | Partida con dos clientes | los dos peers se ven; si no, es la fase [§6](#6-fase-4--photon-luxonserver) |
| 14 | Termina una partida y mira `/home/index` | el rango se movió |

El paso 5 merece un momento: si `/cdn/` lo responde la API en vez de nginx, todo
funciona igual pero cada descarga pasa por .NET, y con ~1 GB de assets eso se
nota. La prueba es mirar los dos logs.

## 8. Fase 6 — El bucle de desarrollo diario

### 8.1 Cambiar datos de master

Los masters se leen **una sola vez, al arrancar**. Editar `config/masters_*.json`
no hace nada hasta reiniciar la API. Después, el cliente los vuelve a descargar
solo: `MasterVersion` es un hash de todas las tablas y viaja en cada respuesta.

```bat
start-balance.bat
:: ...editas y guardas...
start-server.bat
```

### 8.2 Regenerar tablas desde los scripts

El orden importa — `generate_kicker_parameters.py` deja los `skillId` en
`1..14` y `generate_combat_masters.py` los remapea a `20001..20014`:

```bat
python scripts/generate_kicker_parameters.py
python scripts/generate_abilities.py
python scripts/generate_masters_json.py
python scripts/generate_combat_masters.py --only masters_skill_condition,masters_skill_heal,masters_skill_blow_off,masters_skill_pull_in,masters_skill_trap
python scripts/generate_translations.py
start-server.bat
```

`--only` acepta **subcadenas del nombre de archivo**, no nombres exactos:
`--only skill_condition` reescribe también `masters_special_skill_condition.json`.
Pasa siempre el nombre completo. Y `--only` implica `--force` para las tablas que
coinciden, así que sobrescribe lo que hayas ajustado a mano o desde el WebUI de
balance.

### 8.3 Reconstruir bundles de assets

Sólo si cambiaste *action timelines* o miniaturas de discos:

```bat
python scripts/build-action-asset-bundles.py
:: edita config/resources/title-minimum.json: sube "revision" en 1 Y añade ese mismo número a "fromRevisions"
python scripts/build-title-resource-catalog.py
start-server.bat
```

```bash
bash scripts/deploy-nas.sh          # reconstruye el farm /cdn y reinicia el stack
python scripts/seed-device-cache.py # opcional, sólo para el emulador local
```

**Subir la revisión son dos ediciones, no una.** Si subes `revision` y no añades
el número a `fromRevisions`, no se genera el fixture `from-<n>` y un cliente ya
actualizado pide una ruta que da 404: el juego lo enseña como *error de
comunicación*. Si no subes `revision`, los fixtures del cliente llevan un delta
vacío y el cliente se queda con los bundles viejos para siempre.

### 8.4 Scripts que no debes ejecutar

| script | qué rompe |
| --- | --- |
| `scripts/build_complete_catalog.py` | reescribe `title-minimum.json` entero con `revision: 19` y pierde las entradas de bundles |
| `scripts/build_kicker_catalog.py` | mismo archivo, `revision: 15` |
| `scripts/generate_discs_master.py` | clobberea `masters_disc.json`/`masters_skill.json` y **deshace el remapeo de skill ids** |
| `scripts/generate_combat_masters.py --force` | reescribe todas las plantillas y descarta el ajuste de balance |
| `scripts/build_real_database.py`, `scripts/translate_real_database.py` | muertos: dependen de una ruta de macOS que no existe |

### 8.5 Dos manos sobre los mismos archivos

El WebUI de balance y los generadores **escriben los mismos
`config/masters_*.json`**. Guardar en el WebUI y luego correr un generador (o al
revés) pierde en silencio el cambio del otro. Hay copias en
`tools/balance/backups/<tabla>-<fecha>.json` y `<archivo>.json.bak` para
reconstruir lo perdido.

## 9. Sustituye los valores del autor

| valor del autor | dónde vive | qué poner |
| --- | --- | --- |
| `192.168.68.53` (host de la API) | `deploy/`, `scripts/deploy-nas.sh`, lista de hosts permitidos | la IP de tu host |
| `tanukifurhirenas.tplinkdns.com`, `kickflightsg.ddns.net` | documentación y APKs ya construidas | tu DDNS |
| `51.79.241.70` (VM de Photon) | `deploy/docker-compose.nas.yml`, APKs ya construidas | la IP de tu VM |
| `root@192.168.68.53` | `scripts/deploy-nas.sh` | tu usuario y host de despliegue |
| `/mnt/Tanuki_1/kickflight` (rutas del host) | `scripts/deploy-nas.sh`, `deploy/docker-compose.nas.yml` | tus rutas |
| `/mnt/c/Users/Tanuki/Kick-Flight/Kick-Flight-Assets` | `scripts/deploy-nas.sh` (`ASSETS_SRC`) | tu árbol de assets |
| `C:\Users\Tanuki\AppData\Local\Android\Sdk\build-tools\35.0.0` | `.local/build.sh` | tu SDK |
| `10.0.2.2` (host del emulador) | `config/server-host.json`, `config/resources/catalog.json` | la IP que ve tu cliente |
| `kickflight-local` / `KF_DB_PASSWORD` | `deploy/docker-compose.nas.yml` | una contraseña nueva, tuya |
| `revision: 26`, `assetVersion: 12345` | `config/resources/title-minimum.json` | súbelos, no los bajes |

`config/server-host.json` merece una línea: de ahí sale el `host` que se escribe
en **cada entrada** de `config/resources/catalog.json`. Si construyes para un
teléfono real o para un servidor remoto y no lo cambias, las descargas de assets
apuntarán al loopback del emulador.

## 10. Problemas conocidos y su causa

Cada fila es un síntoma que ya se investigó hasta la causa raíz. Ninguno es
misterioso; casi todos tienen una única explicación concreta.

| síntoma | causa | arreglo |
| --- | --- | --- |
| La API responde **421** a todo | el `Host` de la petición no está en la lista de hosts permitidos | añade tu host (sin puerto) a la lista; ver [§4.2](#42-escribe-tu-compose) |
| `/health/ready` da 503 por un asset | una entrada de `catalog.json` no existe o su `sha256` no coincide | regenera el farm; si es la APK, `scripts/update-apk-catalog-sha.py` |
| `curl` a `/health/ready` se corta | la sonda de Photon tarda ~4,5 s en fallar | usa `-m 15`; no es que el servidor esté caído |
| Cuenta nueva atascada en la pantalla de carga, sin llegar al tutorial | `kickerCostumeId` enviado por el servidor no es un id de fila del master `KickerCostume` (formato compuesto `2\|kicker\|costume\|variant`) → NRE al construir el home | sirve ids de fila reales; ver la nota de ids compuestos más abajo |
| La pantalla de selección de kicker sale en blanco | mismo problema, en la selección en vez del home | igual |
| "Cargando…" eterno con un kicker concreto | una fila ausente en un master (p. ej. `Field` 801) hace fallar la inicialización en silencio, o los assets del efecto no están en el catálogo | sirve la fila que falta / revisa que el bundle esté cargado |
| El teléfono dice "error de comunicación" tras actualizar assets | falta el *fixture* de la revisión nueva (`/v1/list/12345/<revision>`) o no se añadió a `fromRevisions` | publica la revisión y su fixture; ver [§8.3](#83-reconstruir-bundles-de-assets) |
| Los assets no se actualizan nunca | subiste los bundles pero no `revision`, así que el delta que recibe el cliente está vacío | sube `revision` |
| Descarga de assets a 404 tras recompilar un bundle | la clave del objeto cambia con el md5 del bundle y el farm no se reconstruyó | `build-cdn-tree.py` (lo hace `deploy-nas.sh`) |
| Editas un master y no cambia nada | los masters se leen al arrancar | reinicia la API |
| `cp` falla con "Text file busy" al desplegar Photon | el servicio está corriendo y tiene el binario mapeado | para el servicio, copia, arráncalo |
| La partida usa bots locales y nunca toca Photon | la APK se construyó sin `KF_PHOTON=1` | recompila con las dos banderas; ver [§6.8](#68-cómo-se-entera-el-cliente-del-host-photon) |
| Las partidas fallan al conectar con el servidor Photon | la APK se construyó sin `KF_PHOTON_HOST` y quedó apuntando al host de la API | recompila con el host explícito |
| Los combos cuerpo a cuerpo no encadenan | `collisionHitType` del ataque es `All` y el collider muere por tiempo en vez de por impacto | `One` en el master de colisión |
| `KFDIAG` no imprime nada | estás ejecutando un build de release, no un build DIAG | recompila con `KF_DIAG=1` |

### Ids compuestos de disfraz

Un detalle que cuesta una tarde si no se sabe: los ids de disfraz que el servidor
le entrega al cliente son **ids de fila** del master `KickerCostume`, con formato
`2_000_000 + kickerId*10_000 + costumeId*100 + variante`. El disfraz 1 del kicker
1 es `2010101`, y ese número está **grabado en el binario del cliente**
(`TutorialUtil.KICKER_COSTUME_ID`), así que una tabla secuencial `1..N` falla la
búsqueda del cliente y lanza una `NullReferenceException` mientras construye el
home. Todo id de disfraz que salga del servidor tiene que existir como fila de
ese master.

## 11. Créditos y límites

- El cliente, sus assets y sus datos son propiedad de Grenge. Este repositorio no
  los incluye ni los distribuye; ver
  [PRESERVATION_POLICY.md](../PRESERVATION_POLICY.md).
- Varios masters **no vienen de un archivo** sino de literales calculados en C#
  dentro de `DemoSessionApi.cs`. Si reconstruyes el pipeline de datos desde cero
  tienes que saber cuáles son, porque no los vas a encontrar en `config/`:
  `Field`, `BattleRank`, `BattleRuleField`, `Item`, `RankerMatchBattleSchedule`,
  los `Capsule*`, los `Festival*`, `BattleRuleParameter`, los
  `BattleRuleScramble*`, `Guardian`, `TutorialKickerAi`, `Frame`,
  `BattleRuleRoleParameter` y `KickerAi`, más los calculados `Weapon` (a partir de
  `config/resources/catalog.json`) y `PlayerLevelExp`. Editarlos es tocar C# y
  recompilar, no editar JSON.
- El resto de los masters servidos sí son archivos `config/masters_*.json`, pero
  no todos son seguros de regenerar: ver la tabla de [§8.4](#84-scripts-que-no-debes-ejecutar).
- Photon corre en una máquina cuyo reparto de recursos no controlas. Es la única
  pieza que puede degradarse por causas ajenas a este repositorio.
