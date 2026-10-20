# Despliegue en el TrueNAS

El API y el CDN corren en el NAS (`10.10.20.148` en la LAN, `100.116.38.51` por Tailscale; TrueNAS SCALE 25.10.3),
Photon sigue en la VM pública. La dirección vieja `192.168.68.53` ya no existe (la LAN pasó a `10.10.20.0/24`).
Este documento describe la topología, los puertos que se abren y cómo desplegar y verificar.

## Topología

```
Cliente Android
   |
   |  HTTP 18080  (API + todos los assets)
   |  gRPC 18081  (matchmaking: el cliente aprende el puerto en la respuesta de /battle/start)
   v
Router  ->  10.10.20.148 (TrueNAS)
                |
                +-- kickflight-cdn   (nginx)   0.0.0.0:18080
                |      /cdn/<clave>  -> se sirve desde disco (/assets/cdn)
                |      todo lo demas -> proxy al API conservando el Host
                |
                +-- kickflight-api   (.NET 8)  :18081 publicado, :8080 interno (solo nginx)
                |      |
                |      +--> kickflight-postgres  :5432 (sin publicar, solo red de compose)
                |
                +--> 51.79.241.70:5055/5056/5058   (Photon, en la VM)
```

`/cdn/<clave>` es el grueso del tráfico: la base de datos Octo entrega al cliente la plantilla de URL
`{scheme}://{Request.Host}/cdn/{o}` (`Program.cs`) y cada entrada de `config/resources/catalog.json` apunta
a `/cdn/<clave>`. Servirlo desde nginx evita que ~1 GB de descargas pasen por el proceso .NET.

Esa plantilla se arma **por petición**, a partir del `Host` que llega. Por eso `nginx.conf` usa
`proxy_set_header Host $http_host` (`$host` perdería el `:18080` y el cliente descargaría del puerto 80), y
por eso un cliente en la LAN y otro en el nombre DDNS funcionan a la vez sin configuración extra.

## Puertos

| Puerto | Proceso | Contenedor | Nota |
| --- | --- | --- | --- |
| 18080/tcp | nginx | `kickflight-cdn` | API + CDN. Ya reenviado por el router. |
| 18081/tcp | API | `kickflight-api` | gRPC. Publicado directo, sin proxy delante. |
| 8080/tcp | API | `kickflight-api` | Interno, **no publicado**: todo el HTTP entra por nginx. |
| 5432/tcp | PostgreSQL | `kickflight-postgres` | **No publicado**: solo lo alcanza el API por la red de compose. |

No hay puertos UDP: Photon no corre aquí.

Nombres que deben estar en `Harness__DirectClientHosts` (el gate de `Program.cs:170` responde 421 a
cualquier otro): `10.10.20.148`, `tanukifurhirenas.tplinkdns.com`, `kickflightsg.ddns.net`,
`192.168.68.55` y `100.116.38.51` (Tailscale). Son **hostnames sin puerto**: el puerto se descarta antes de comparar.

## Datasets

En el pool `Tanuki_1`:

| Dataset | Uso | Cuota |
| --- | --- | --- |
| `Tanuki_1/kickflight/app` | raíz del repo: `config/`, `content/`, `data/`, `deploy/` | — |
| `Tanuki_1/kickflight/assets` | `octo_sorted/` (árbol de bundles) y `cdn/` (la granja de enlaces) | — |
| `Tanuki_1/kickflight/pgdata` | datos de PostgreSQL | 20 GiB |
| `Tanuki_1/kickflight/build` | contexto de build de Docker (efímero) | — |

`pgdata` lleva `recordsize=16K` (el valor por defecto de 128K provoca amplificación de escritura en
PostgreSQL) y su cuota acota también el crecimiento del WAL.

## Estado de jugador (PostgreSQL)

La identidad, el nombre, las monedas y el rango viven en PostgreSQL. La conexión la selecciona
`PlayerStoreFactory`: con `PlayerStore__ConnectionString` definida se usa `PostgresPlayerStore`; sin ella el
API escribe un JSON por jugador bajo `/srv/repo/data/users`, que es lo que usan `dotnet test` y un
`start-server.bat` local para no necesitar base de datos.

`data/` sigue montado, pero con la cadena de conexión puesta **no se escribe ahí**: los ficheros de
jugador que quedaran de la etapa anterior no se leen ni se importan.

El esquema lo aplica el propio API al arrancar (`PostgresPlayerStore.Migrate`, versionado en `schema_meta`
y serializado con un `pg_advisory_lock`), así que una base vacía queda lista con solo levantar la pila. El
API resuelve el store durante el arranque a propósito: si la base no responde, el contenedor falla al
iniciar en lugar de servir 500 mientras `/health/ready` sigue diciendo "ready".

Forma de las tablas: identidad (`players.uuid`, `players.id`), nombre (`players.display_name`, `NULL`
mientras el jugador no lo haya elegido), las cuatro monedas y el rango (`player_ranks`) son columnas
reales; el resto del estado —decks, discos, engranajes, tirada pendiente— es un único documento
`players.state jsonb`, porque el API siempre carga y guarda el estado entero y normalizarlo solo añadiría
una capa de mapeo y la posibilidad de un guardado a medias.

```bash
# inspeccionar
ssh root@10.10.20.148 'docker exec kickflight-postgres psql -U kickflight -d kickflight -c \
    "select id, uuid, display_name, item_jet_coins from players order by id"'
# el rango de un jugador concreto
ssh root@10.10.20.148 'docker exec kickflight-postgres psql -U kickflight -d kickflight -c \
    "select * from player_ranks order by player_id"'
# copia de seguridad
ssh root@10.10.20.148 'docker exec kickflight-postgres pg_dump -U kickflight kickflight' > kickflight.sql
```

La contraseña es `KF_DB_PASSWORD` (por defecto `kickflight-local`). El puerto no se publica, así que solo
protege la red de compose.

**Onboarding.** Un dispositivo sin nombre recibe `tutorialProgressStatus = 206` en `/startup/index`, que es
lo que abre la ventana de nombre (`GetTutorialInitialSubStep` mapea 206 a `InputName`); con nombre recibe
`207`, que es con lo que `TutorialUtil.IsTutorial` deja de considerarlo tutorial. El nombre lo fija
`POST /tutorial/end`; un rechazo es HTTP 200 con `x-app-status-code: 2005` (solo ese código abre el popup
de reglas del cliente, cuyo texto está horneado en el prefab) y el nombre sigue sin fijarse, así que el
jugador vuelve a la ventana.

## Montajes

El contenedor del API ve la raíz del repo en `/srv/repo` (`KF_REPO_ROOT`) y el árbol de assets en
`/srv/Kick-Flight-Assets`. Esa segunda ruta **no es libre**: el catálogo direcciona los bundles como
`../Kick-Flight-Assets/octo_sorted/...`, así que el árbol tiene que quedar exactamente un nivel por encima
de la raíz del repo. nginx solo monta el dataset de assets.

## Desplegar

```bash
# desde WSL, en la raíz del repo
bash scripts/deploy-nas.sh                    # sube config/content, construye el CDN y la imagen, reinicia
bash scripts/deploy-nas.sh --assets           # fuerza la re-subida del árbol de assets (~1 GB)
bash scripts/deploy-nas.sh --skip-assets      # no toca los assets
bash scripts/deploy-nas.sh --no-restart       # solo prepara, sin levantar la pila
```

El script es idempotente. La imagen se construye **en el NAS** (`docker build`), no en Windows: misma
arquitectura amd64, sin compilación cruzada y sin registry de por medio. El contexto de build es solo
`src/`; `bin/`, `obj/` y `data/` quedan fuera por `.dockerignore`.

Si al desplegar el contenedor del API se recrea (imagen nueva), el script **reinicia `kickflight-cdn` acto
seguido**: nginx resuelve `proxy_pass http://kickflight-api:8080` una sola vez, al arrancar, y se queda con
esa dirección; el API recreado tiene otra en la red de compose y nginx seguiría marcando la muerta. El
síntoma es engañoso — `/cdn/` (que nginx sirve de disco) funciona y **todo lo demás responde 502**, así que
parece una caída del API. A mano, el equivalente es:

```bash
ssh root@10.10.20.148 'docker compose -f /mnt/Tanuki_1/kickflight/app/deploy/docker-compose.nas.yml up -d \
    && docker restart kickflight-cdn'
```

Detalle que importa: `data/` se monta, nunca se hornea en la imagen.

### La granja `/cdn`

`scripts/build-cdn-tree.py` reconstruye `assets/cdn/` a partir de `config/resources/catalog.json`: una
entrada por clave de objeto, nombrada como la clave. Son **hardlinks**, no symlinks — un symlink guarda una
ruta absoluta, lo que obligaría a nginx a montar cada directorio en la misma ruta con la que se creó el
enlace. Un hardlink es el mismo inodo con otro nombre, así que la granja es autocontenida.

Casi todo resuelve dentro del árbol de assets; 14 bundles de UI se direccionan como
`content/resources/...` y viven en el repo. Al estar en otro dataset (`app` vs `assets`) el hardlink es
imposible, así que se copian. La salida del script dice cuántos de cada uno.

## Verificar

```bash
# readiness (tarda ~5 s: sondea Photon antes de responder, un curl -m 3 lo reporta como caido sin estarlo)
curl -m 10 http://10.10.20.148:18080/health/ready

# el CDN lo sirve nginx, no el API
curl -sI http://10.10.20.148:18080/cdn/VsckeT | head -5
docker logs --tail 20 kickflight-cdn        # debe aparecer la peticion
docker logs --tail 20 kickflight-api        # NO debe aparecer

# desde fuera de la LAN
curl -m 10 http://tanukifurhirenas.tplinkdns.com:18080/health/ready
# o por Tailscale (el NAS corre el contenedor ix-tailscale)
curl -m 10 http://100.116.38.51:18080/health/ready

# la identidad sobrevive a un reinicio: el mismo uuid debe recuperar el mismo id y el mismo nombre
# (con scripts/d2c.py, que hace el handshake D2C; ver el docstring del script)
python scripts/d2c.py http://10.10.20.148:18080 --uuid <uuid> --post /startup/index
ssh root@10.10.20.148 'docker compose -f /mnt/Tanuki_1/kickflight/app/deploy/docker-compose.nas.yml restart api'
python scripts/d2c.py http://10.10.20.148:18080 --uuid <uuid> --post /startup/index   # mismo id
```

En el emulador, con la APK reconstruida contra el NAS (`.local/build.sh`, el knob `URL` está en la línea 9):
título -> ventana de nombre -> home -> batalla -> resultado.

## Custom App de TrueNAS

`docker-compose.nas.yml` referencia `kickflight-api:local` (imagen ya construida) porque el asistente de
Custom App **no tiene contexto de build**: la imagen se construye por SSH antes de crear la app.

La pila se puede levantar con `docker compose` directamente (es lo que hace `deploy-nas.sh`). Para
registrarla como Custom App y que aparezca en la UI, crear una app con el contenido de
`docker-compose.nas.yml` como compose personalizado. Antes hay que bajar la pila de compose para no chocar
en los puertos:

```bash
ssh root@10.10.20.148 'cd /mnt/Tanuki_1/kickflight/app/deploy && docker compose -f docker-compose.nas.yml down'
```

## Limitaciones conocidas

- **La APK remota antigua deja de funcionar.** El reenvío de 18080 pasó de este PC al NAS, así que una APK
  compilada contra `kickflightsg.ddns.net:18080` llega al NAS. Hay que recompilar.
- **El ETag del CDN no es el del API.** nginx lo deriva de mtime+tamaño; el API usa el SHA-256 del
  catálogo. Un cliente que mande `If-None-Match` con el ETag del API recibe 200 con el cuerpo completo en
  lugar de un 304: es una optimización perdida, no un fallo.
- **`docker build` en el NAS necesita internet** la primera vez (imágenes base del SDK y del runtime).
