# Referencia de configuración

Todos los valores que un operador puede tocar: variables de entorno, claves de
configuración, puertos y rutas, agrupados por dónde viven. El compañero de este
documento es [RECREATE_FROM_SCRATCH.md](RECREATE_FROM_SCRATCH.md), que cuenta
*cuándo* hay que ponerlos; aquí sólo está *qué* son.

## 1. Cómo se lee la configuración

La API usa `IConfiguration` de .NET: `src/KickFlight.BootstrapApi/appsettings.json`
más variables de entorno, donde `__` (doble guion bajo) significa anidamiento.
Ejemplo: la clave `Harness:DirectClientHosts` se pone como
`Harness__DirectClientHosts__0`, `...__1`, etcétera.

No existe ningún `appsettings.Desarrollo.json` ni variante por entorno, así que
`ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT` no cambian nada: lo que no está en
`appsettings.json` sólo se puede cambiar por entorno o por línea de comandos.

## 2. Puertos y enlaces

| clave | por defecto | qué hace |
| --- | --- | --- |
| `HttpPort` | `8080` | HTTP/1 en **todas** las interfaces. Detrás de nginx en el despliegue. |
| `GrpcPort` | `18081` | HTTP/2 (gRPC) en todas las interfaces. Es el *matchmaking frontend*. |
| `HttpsPort` | `8443` | sólo se enlaza si `Certificate:Path` apunta a un PFX existente |
| `Certificate:Path` / `Certificate:Password` | vacío | sin PFX no hay listener TLS |

Dos avisos que ahorran una tarde:

- **El puerto gRPC no viene de `appsettings.json`.** El valor por defecto está
  sólo en el código, y además `DemoSessionApi` **anuncia el 18081 como literal**
  al cliente (`matchmakingFrontend {host, port: 18081}`). Cambiar `GrpcPort` sin
  parchear ese literal deja al cliente apuntando al puerto viejo: deja 18081.
- **Nada se enlaza sólo a localhost.** Los tres listeners usan `ListenAnyIP`, así
  que el firewall es lo único que separa la API de la red.

El cliente tiene que poder alcanzar **18080 (HTTP, por nginx) y 18081 (gRPC)**.
Si el host de la API está detrás de NAT, hay que abrir los dos.

## 3. Hosts permitidos (la puerta del 421)

Toda petición que llegue por la ruta genérica pasa por una comprobación de
`Host`. Si el host no está en la lista, la respuesta es **HTTP 421** con
`{"error":"local-host-not-allowed","host":"..."}`.

- La comparación es sobre el host **sin puerto**, en minúsculas y sin el punto
  final, así que se configuran nombres pelados: `midominio.ddns.net`, no
  `midominio.ddns.net:18080`.
- Se permite si el host está en `Harness:FirstPartyHosts` (los tres hosts
  históricos de Grenge, vienen por defecto) **o** en `Harness:DirectClientHosts`
  (la lista que rellena el operador) **o** si `Harness:StrictMode` es `false` y el
  host es una IP.
- Con `StrictMode` en `true` (por defecto), una IP suelta **no** pasa: hay que
  listarla.

| clave | por defecto | qué hace |
| --- | --- | --- |
| `Harness:DirectClientHosts` | `[]` | tu host de la API y tu DDNS; forma de entorno `Harness__DirectClientHosts__0` |
| `Harness:StrictMode` | `true` | en `false`, cualquier IP pasa la puerta y un fixture ausente devuelve 200 en vez de 404 |
| `Harness:FirstPartyHosts` | los tres `*.grenge.jp` | no los toques salvo que sepas por qué |

**Fuera de la puerta**: `/health/*`, `/gym*`, `/webview/*`, `/apk*` y `/diag*` se
registran antes de la ruta genérica y **no** comprueban el host. `/diag` acepta
subidas de hasta 200 MiB y las escribe en disco, así que conviene no publicarlo a
internet sin querer.

## 4. Photon

Hay **dos** configuraciones distintas de Photon y es fácil confundirlas:

| quién | dónde se configura | qué controla |
| --- | --- | --- |
| la API | `PhotonServer:*` (entorno o `appsettings.json`) | sólo la **sonda** de `/health/photon` y `/health/ready` |
| el cliente | `KF_PHOTON_HOST` al construir la APK | el host Photon al que el teléfono se conecta de verdad |

Cambiar `PhotonServer:Host` **no** mueve al cliente: el host Photon va parcheado
dentro de la APK.

| clave | por defecto | qué hace |
| --- | --- | --- |
| `PhotonServer:Enabled` | `true` | en `false` la sonda se declara sana sin probar nada |
| `PhotonServer:Host` | `10.0.2.2` | alias del emulador Android hacia la máquina anfitriona; cámbialo o `/health/ready` devuelve **503** |
| `PhotonServer:MasterServerPort` | `5055` | sonda TCP, con reintento UDP |
| `PhotonServer:GameServerPort` | `5056` | sonda |
| `PhotonServer:NameServerPort` | `5058` | sonda |
| `PhotonServer:HealthCheckTimeoutMs` | `1500` | por sonda |
| `PhotonServer:ContainerName` / `RepositoryUrl` | `luxon-server` / el fork | sólo informativo |

`/health/ready` tarda ~4,5 s cuando la sonda tiene que fallar: un `curl -m 3` que
se corta **no** significa que el servidor esté caído.

## 5. Estado del jugador y PostgreSQL

| clave | por defecto | qué hace |
| --- | --- | --- |
| `PlayerStore:ConnectionString` | vacío | si tiene valor, se usa PostgreSQL |
| `ConnectionStrings:KickFlight` | vacío | alternativa para el mismo valor |

Sin ninguna de las dos, la API **no falla**: registra un aviso y usa los archivos
JSON por dispositivo (`<content root>/data/users`). En un contenedor eso es estado
efímero, y además el rango se guarda sólo en memoria, así que se pierde en cada
reinicio. Para un servidor de verdad, define la cadena de conexión.

Forma de la cadena (la del despliegue de referencia):

```
Host=postgres;Port=5432;Database=kickflight;Username=kickflight;Password=<la tuya>
```

**Si la base de datos no responde, el proceso no arranca.** Es deliberado: el
almacén se resuelve al inicio para que `/health/ready` no pueda decir "listo"
mientras la base está caída.

### Esquema

Se crea y migra solo, al arrancar, con SQL embebido (sin EF Core ni archivos
`.sql` sueltos), dentro de una transacción y bajo un `pg_advisory_lock`, de forma
idempotente. La versión aplicada queda en `schema_meta`.

| objeto | contenido |
| --- | --- |
| `schema_meta` | versiones de migración aplicadas; la API las aplica en orden, bajo advisory lock y dentro de transacciones |
| `player_id_seq` | ids de jugador, empieza en **1000001** |
| `players` | `id`, `otomo_player_id` (único, la cuenta en otomo), `legacy_device_uuid`, `display_name` (NULL hasta que elige nombre), kicker y disfraz activos, deck activo, las cuatro monedas, `state jsonb` (decks, discos, gears, objetos), timestamps |
| `player_ranks` | por `(player_id, battle_rule_type)`: puntos de batalla y rango |
| `player_follows` | aristas dirigidas `(follower_id, followed_id)`, fecha y lectura del aviso de nuevo seguidor; FK con borrado en cascada y sin auto-seguimiento |
| `real_friend_tokens` | un token opaco, estable y reusable por jugador para vincular amistades reales; único por dueño y por valor |
| `player_real_friends` | amistad explícita simétrica, guardada una sola vez como `(player_low_id, player_high_id)`; separada de follows |

El nombre vacío se guarda como `NULL` (`nullif(...,'')`), que es lo que hace que
un jugador nuevo entre por la ventana de nombre.

### Identidad

El uuid del dispositivo se mapea al id numérico con
`insert ... on conflict (uuid) do nothing returning id` y, si no devolvió fila,
un `select`. Un uuid ⇒ un jugador, para siempre.

> **Ojo con el respaldo JSON**: su primer id es `1000002` (calcula
> `max(highWaterMark, 1000001) + 1`), mientras que PostgreSQL empieza en
> `1000001`. Si cambias de uno a otro, los ids no coinciden.

> **Un dispositivo sin uuid** en el cuerpo de `/auth/index` cae al literal
> `default-user`, y todos esos dispositivos comparten una sola cuenta.

## 6. Fixtures, catálogo y raíces

| clave | por defecto | qué hace |
| --- | --- | --- |
| `Harness:FixtureDirectory` | `config/fixtures` | se escanea recursivamente buscando `*.json` |
| `Harness:ResourceCatalogPath` | `config/resources/catalog.json` | catálogo de assets; si falta o tiene duplicados, `/health/ready` se queda en 503 |
| `Harness:CaptureDirectory` | `captures` | dónde se escriben las capturas |
| `Harness:PersistCaptures` | `false` | escribe un JSON por petición |
| `Harness:MaxCapturedBodyBytes` | `65536` | truncado del cuerpo capturado |
| `Harness:OctoCdnUrlFormat` | vacío | fija el formato de URL del CDN en vez de derivarlo del `Host` |
| `KF_REPO_ROOT` | vacío | raíz con la que se resuelven todas las rutas relativas |

`KF_REPO_ROOT` merece un párrafo: las rutas del proyecto (`config/masters_*.json`,
`config/resources/catalog.json`, `../Kick-Flight-Assets/octo_sorted/...`) se
resuelven subiendo por el árbol de directorios hasta encontrar
`KickFlight.PrivateServer.sln`. **Un contenedor no tiene ese archivo**, así que
sin `KF_REPO_ROOT` la búsqueda lanza `DirectoryNotFoundException` y la API muere
en la primera petición. En Docker se define siempre.

## 7. Masters: cómo se sirven

- `GET /demo-master/{nombre}` devuelve la tabla ya cifrada, en crudo.
- `POST /download/master` devuelve el índice `{name, hash, url, size}`, donde
  `url` es `{esquema}://{host}/demo-master/{nombre}` — es decir, **el host de la
  petición**, que es lo que hace que el CDN funcione igual por LAN y por DDNS.
- El cifrado de masters es AES-256-CBC/PKCS7 con una clave fija (`CommonCode`) y
  **IV de 16 ceros**, distinto del cifrado de las respuestas de sesión (clave del
  cliente, IV aleatorio).
- A algunos enteros se les suma un desplazamiento antes de cifrar
  (`Skill.coolTime`, `Disc.minHp/maxHp/minAttack/maxAttack`,
  `KickerAbility.overlapCount`, `SpecialSkillHit.fixedDamage`). No es un secreto
  aleatorio: es compatibilidad con el cliente, y hay que dejarlo como está.
- `MasterVersion` es un hash de los nombres y cifrados de todas las tablas, y
  viaja en la cabecera `x-app-master-hash`. Tocar cualquier master y reiniciar lo
  cambia, y eso es lo que hace que el cliente vuelva a descargar. Es el mecanismo,
  no un efecto secundario.
- Los archivos se leen de `config/masters_*.json`. Si un archivo falta, la API
  **arranca igual** con un valor por defecto en línea, así que un master ausente
  no da error: da datos incompletos. Merece la pena comprobar qué sirves de
  verdad (`scripts/re/served_master.py`) cuando algo "no aparece".

## 8. Rutas que el cliente necesita, en orden

Todas son `POST` salvo las indicadas, y todas van por la ruta genérica (o sea,
sujetas a la puerta del 421).

| ruta | contrato |
| --- | --- |
| `GET /v1/list/12345/{n}` | base Octo en protobuf; el campo del formato de URL se reescribe al `Host` actual |
| `POST /boot/index` | fixture local |
| `POST /auth/prepare`, `POST /auth/index` | `hash` (32 caracteres ASCII, pasa a ser la clave AES) y `uuid`; devuelve `x-app-user-id`, `x-app-access-token` |
| `POST /download/master` | el índice de masters |
| `GET /demo-master/{nombre}` | la tabla cifrada |
| `POST /startup/index` | listas del jugador y `tutorialProgressStatus` **206** sin nombre / **207** con nombre |
| `POST /tutorial/end` | fija el nombre; el rechazo es HTTP **200** con `x-app-status-code: 2005` |
| `POST /home/index` | el jugador, sus decks, sus rangos y la tienda |
| `POST /ping/index` | `{}` |

Un `x-app-access-token` desconocido devuelve **401** con `x-app-status-code: 1`.
Antes caía en la sesión de otro jugador; ya no.

El sobre de respuesta es AES-256-CBC/PKCS7 con la clave del cliente y un IV
aleatorio delante, más las cabeceras `x-app-datetime`, `x-app-master-hash` y
`x-app-status-code`.

## 9. Salud

| endpoint | qué comprueba |
| --- | --- |
| `GET /health/live` | nada; siempre 200 |
| `GET /health/photon` | conecta a los tres puertos Photon; 200 si el MasterServer responde o si la sonda está desactivada |
| `GET /health/ready` | errores de fixtures y de catálogo + Photon; **no** comprueba la base de datos (por eso el almacén se resuelve al arrancar) |

## 10. Banderas del parcheador del cliente

Se pasan al construir (`scripts/patch-il2cpp-endpoints.py`, o el wrapper que
uses). El procedimiento completo está en
[RECREATE_FROM_SCRATCH.md §5](RECREATE_FROM_SCRATCH.md#55-las-banderas-del-parcheador).

| bandera | por defecto | qué hace |
| --- | --- | --- |
| `--base-url` | obligatoria | host HTTP del cliente; sin ruta ni query |
| `--metadata`, `--arm64`, `--armv7` | obligatorias | archivos desempaquetados de la APK |
| `--arm64-unity` | opcional | `libunity.so`; sólo se parchea si el archivo existe |
| `--photon-host` | host de `--base-url`, luego `10.0.2.2` | sustituye el literal `ns.exitgames.com` |
| `KF_PHOTON=1` | apagada | matchmaking real; sin ella, puente offline y **nunca se toca Photon** |
| `KF_DIAG=1` | apagada | trazas `KFDIAG` (recuento de parches 212 → 358) |
| `KF_NRE_LR=1` | apagada | con `KF_DIAG`, imprime el LR de cada NRE (359 parches) |
| `KF_RESULT_DIAG=1` | apagada | traza de la pantalla de resultado (219); **excluyente con `KF_DIAG`** — si defines las dos, el script aborta al importar |
| `KF_FORCE_GAME_SCENE=1` | apagada | fuerza la transición a la escena de juego (213) |
| `KF_UNLOAD_BYPASS=1` | apagada | bypass viejo de `_isUnloading`; causa el SIGSEGV intermitente de `UnityPreload` |
| `KF_NO_READY_SCENE=1` | apagada | restaura el bypass de la escena "ready" previo al 2026-09-21 (217) |
| `KF_UNITY_NO_ALLOCATOR_REBIND=1` | apagada | quita el rebinding de allocators de libunity (parches de unity 17 → 5) |
| `SOURCE_APK` | `base.apk` en la raíz | APK prístina de entrada |
| `ANDROID_BUILD_TOOLS` | ruta de macOS | dónde están `zipalign`/`apksigner` |
| `OUTPUT_APK` | `.local/artifacts/…` | salida |

`KF_DIAG` y `KF_RESULT_DIAG` comparten cuerpo muerto y no se pueden combinar: el
script lo detecta al importar y sale con un mensaje.

Los recuentos que cita `AGENTS.md` («141 de producción → ~245 en DIAG») son de una
versión anterior del parcheador.
## 11. Trampas conocidas

1. Los ids de jugador empiezan en `1000001` en PostgreSQL y en `1000002` en el
   respaldo JSON.
2. `GrpcPort` no está en `appsettings.json` y el `18081` que ve el cliente es un
   literal del código: dos fuentes para un valor.
3. No hay `appsettings.<Entorno>.json`: cambiar `ASPNETCORE_ENVIRONMENT` no
   cambia la configuración.
4. `/diag*` está fuera de la puerta de hosts y escribe archivos de hasta 200 MiB.
5. Un dispositivo sin uuid se convierte en `default-user` y comparte cuenta.
6. `deploy/README.md` cita la puerta de hosts con un número de línea ya viejo.
7. El respaldo JSON guarda el rango sólo en memoria: en un reinicio se pierde.
