# Servidor Photon (LuxonServer) para partidas de 2 a 8 jugadores

Las partidas con varios humanos necesitan un servidor compatible con Photon: tras `/battle/start` el cliente entra a la
sala `battle-NNN` por Photon (NameServer 5058 -> MasterServer 5055 -> GameServer 5056, UDP). Los bots los sigue
poniendo nuestro API (`BattleMatchmakingService.BuildRoster`).

## Despliegue actual (VPS con Docker, revisado 2026-10-10)

* Photon corre en el VPS como el servicio `photon` de `deploy/docker-compose.vps.yml` (contenedor `deploy-photon-1`),
  construido con `deploy/photon/Dockerfile` a partir de `submodules/luxonserver`. El workflow
  `.github/workflows/deploy-vps-main.yml` lo reconstruye y recrea cuando cambian `submodules/luxonserver`,
  `.gitmodules` o cualquier cosa bajo `deploy/photon/` (flag `photon`). Recrear el contenedor tira las partidas en
  curso: desplegar en una ventana tranquila.
* Puntero del submódulo: `b35e3cf` (rama `tanuki-server` de `tanukifurhire/luxonserver`), con el submódulo anidado
  `Luxon/` en `5401810` (Gixarde3, "recover peers stuck on reliable receive window": deja de confirmar los paquetes
  fiables que caen fuera de la ventana para que el emisor los reenvíe, y desconecta al peer cuya ventana no avanza en
  20 s seguidos de descartes).
* Qué parches de `0001`-`0010` están vivos y por qué vía (comprobado el 2026-10-10 contra `b35e3cf`/`5401810`):

  | parche | estado en la imagen |
  | --- | --- |
  | 0001, 0002, 0004, 0005, 0008, 0009, 0010 | commits del puntero `b35e3cf` (`0004` ampliado después por `b35e3cf`, que añade actor y propiedades al log de cada op) |
  | 0003 | sustituido por `f160000` ("let late joiners into closed battle rooms"): el `GameClosed` se salta para **cualquier** sala `battle-*`, no solo para quien ya está en `expected_users` |
  | 0006, 0007 | **no** están en ningún commit de `Luxon/`; los aplica el `Dockerfile` en la etapa de build desde `deploy/photon/luxon-patches/` (`git apply`, la build falla si no aplican y comprueba con `grep` que los valores 4096 / 32 / 30000 quedaron en `enet_peer.hpp`) |

  Antes del 2026-10-10 el `Dockerfile` no aplicaba `0006`/`0007`: la imagen viva (creada el 2026-10-07) tenía la
  ventana fiable de 128 y 7 reenvíos / 10 s de cabecera, y en 48 h registró ~27 400 `Receive window
  incoming_reliable is too small! Dropping reliable packet!`. `0008` ya fijaba 30 s en `ServerManager::setup()`, así
  que el timeout de desconexión efectivo era 30 s también entonces; lo que faltaba era la ventana y los 32 reenvíos.
* `0006`/`0007` se rebasaron sobre `5401810` (solo cambia el contexto de `0007`; `0006` además actualiza
  `apps/enet_reliable_window_test.cpp`, que tenía 128/129 fijos). No se solapan con `5401810`: `0006` solo cambia el
  tamaño de la ventana, y la lógica de no-ACK y de peer atascado de `5401810` se dispara con `out_of_window()` sea cual
  sea ese tamaño; `0007` gobierna los reenvíos **salientes** del servidor, `5401810` los descartes **entrantes**.
* Al subir el puntero de `submodules/luxonserver` o de `Luxon/`, la build de Docker dirá si `0006`/`0007` siguen
  aplicando; si el nuevo `Luxon/` ya los lleva como commits, borra el parche correspondiente de
  `deploy/photon/luxon-patches/` (si no, `git apply` falla porque el parche ya está aplicado).
* Config: `deploy/photon/config.yml.template` (`envsubst` de `KF_PUBLIC_HOST` al arrancar). Logs:
  `docker logs deploy-photon-1`.

## Despliegue anterior (VM con systemd, 2026-09-21; histórico)

* Host: `51.79.241.70` (Ubuntu 24.04), servicio `luxon-server.service` (systemd, `Restart=always`).
* Binario: `/opt/luxon-server/luxon_server`, config `/opt/luxon-server/config.yml` (= `config.yml` del fork con
  `external_address: 51.79.241.70:<puerto>`). Fuente en `~/kickflight/luxonserver` (fork `Gixarde3/luxonserver`
  @ `a84ecc3` + `scripts/luxonserver/000{1..5}-*.patch` aplicados con `git am` en el fork, y `0006`/`0007-*.patch`
  (hoy en `deploy/photon/luxon-patches/`) aplicados dentro del submodulo `Luxon/` del fork). En la VM, `0008-*.patch`,
  `0009-*.patch` y `0010-*.patch` quedaron aplicados en el árbol de trabajo sin commit (observado el 2026-09-21); el
  diff exportado se verificó contra el servidor con `git apply --check -R`.
* Estado del fork el 2026-09-22: la rama `tanuki-server` de `tanukifurhire/luxonserver` (base `a84ecc3`, punta
  entonces `a3539b8`) llevaba como commits los ocho parches que tocan `src/*.cpp` (`0001`-`0005` y `0008`-`0010`);
  `0006` y `0007` iban dentro del submódulo anidado `Luxon/` (entonces en `9424d75`), que esa rama no tocaba.
  `Gixarde3/luxonserver` sólo tiene `main` en `a84ecc3`: los SHA `1e9f1e4`, `e093b87`, `1bc49a1`, `4320af8`,
  `d85e14b`, `f0a50b8` eran los del clon de la VM producidos por `git am`, no commits de ningún remoto.
* Al subir la rama, `git-lfs` avisa de un objeto pendiente, el de `WASMImpl/C/cwsdpmi.exe` (el único fichero del
  árbol que va por LFS), y GitHub lo rechaza (`can not upload new objects to public fork`); ninguno de los ocho
  commits que se suben añade objetos LFS, porque sólo tocan `src/*.cpp`, así que la subida va con
  `GIT_LFS_SKIP_PUSH=1`. El pin
  arrastra el mismo objeto LFS que ya traía el commit base, así que un clon necesita lo mismo que necesitaba
  clonando `Gixarde3/luxonserver`.
* Recompilar y desplegar tras tocar el servidor: `ninja -C build` en `~/kickflight/luxonserver`, y después
  `sudo systemctl stop luxon-server && sudo cp build/luxon_server /opt/luxon-server/ && sudo systemctl start
  luxon-server`. El `cp` falla con "Text file busy" si el servicio sigue arriba. Copia de seguridad del binario
  anterior: `/opt/luxon-server/luxon_server.bak-pre0010`.
* Compilado con `g++-14` (el código usa C++23 "deducing this"; g++ 13 no compila):
  `cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release -DLUXON_SERVER_USE_SANMAKE=OFF -DCMAKE_C_COMPILER=gcc-14 -DCMAKE_CXX_COMPILER=g++-14 && ninja -C build`.
  Submodulos necesarios: `Luxon` y `tracy` (solo cabeceras).
* ufw: UDP+TCP 5055, 5056, 5058 y 27000-27002.
* Logs: `sudo journalctl -u luxon-server -f`.

## Ventana de emparejamiento normal (2026-09-21)

`/battle/entry` + el stream `openmatch.Frontend/GetAssignments` ya no emparejan exactamente a dos humanos: el primero
abre una ventana que se extiende con cada join y en la que pueden entrar más humanos, y al cerrarse se rellenan con bots los huecos. La composición VPS no fija la ventana: rige el default de la API (40 s más los incrementos por join).
La prioridad es que los humanos jueguen juntos; los bots son el relleno, no el disparador.

* La ventana base empieza con la primera entrada (`KF_MATCH_WINDOW_SECONDS`, default de la API `40`; `0` la desactiva en entornos que configuren la API directamente). La composición VPS no pasa la variable, así que un `.env` del servidor no la cambia. Cada humano
  que entra a la sala abierta la extiende: el 2º +5 s, el 3º +4,5 s, el 4º +4 s … el 8º +2 s (`5 - 0,5 * (n - 2)`), así
  que una sala llena suma 64,5 s. El primer incremento se cambia con `KF_MATCH_JOIN_INCREMENT_SECONDS` (default `5`;
  `0` = los joins no extienden la ventana). Los equipos cuentan un join por humano.
* El proxy gRPC VPS mantiene `GetAssignments` abierto durante `300` segundos entre lecturas del upstream. Esto cubre la
  ventana de 40 s, los incrementos de hasta 24,5 s para ocho humanos, la espera de 2 s entre Etapa 2 y Etapa 3 y la
  retención de 30 s de la asignación final (~97 s en total).
* La cuenta atrás del cliente es `clamp(expiración - ahora, 0, 120)` y solo se queda con la expiración más tardía que
  recibe: la API manda el plazo real de la sala + 3 s en UTC (`yyyy-MM-ddTHH:mm:ssZ`) y lo reenvía en cada join. Una
  expiración fija lejana (la antigua `2030-01-01`) dejaba el contador clavado en 120.
* Mientras la ventana siga abierta, el 2º, 3º… jugador entra **en la sala del primero** (`_pendingRoom`), nunca en
  una sala nueva; sus clientes reciben el roster actualizado. Un `RunMatchWindowAsync` por sala relee el deadline tras cada espera, así que un join lo mueve sin cierres anticipados.
  Con 8 humanos (4 por equipo) la partida arranca sin esperar a que expire.
* Etapas del stream con la ventana abierta: la Etapa 1 pasa a ser "los humanos que hay ya" (sin bots) y cada entrada
  se **retransmite** a los clientes que esperan para que vean llenarse las ranuras. La Etapa 2 (8 ranuras con bots) y
  la Etapa 3 (`Connection = battle-NNN`) llegan cuando la ventana se cierra, igual que antes.
* Replay: una asignación completada solo se repite si la sala está **finalizada** (`IsFinalized`). Con la ventana
  abierta el roster es parcial a propósito y `MatchingInfo` sigue vacío, así que un replay por "roster no vacío"
  repartiría un `Connection` antes de que la partida exista.
* Un stream que muere antes de que la sala arranque (salir de la pantalla de matching destruye `GetAssignments`)
  retira a ese humano; con el último se descarta la sala. `/battle/cancel` no llega a este servicio: es un stub, así
  que el fin del stream es la única señal de retirada.
* Equipos: los humanos alternan Azul/Rojo por orden canónico (`userId`), así que nunca se separan por más de uno, y
  los bots rellenan Azul hasta 4 y luego Rojo. Sale 4v4 con los humanos repartidos para cualquier número de humanos
  de 1 a 8 (espejo en `tests/test_teamtype.py`).

**Sin verificar (Photon)**: el tope de la sala es el `MaxPlayers` que manda el cliente que la crea
(`handler_gameserver.cpp:460-462`) y luxon solo lo aplica si `max_peers > 0` (su `MaxGamePeers` es 0). Ese literal no
aparece en este repo: si el cliente pide 2, el 3er humano recibiría `GameFull` y haría falta un parche luxon
(`0011-*`, todavía sin escribir) que lo suba a 8 en salas `battle-*`. Comprobar con 2 y luego 3 humanos.

**Sin verificar (cliente)**: el cliente despacha cualquier update con `Connection` vacío como `UpdatePlayers` y
refresca las ranuras, así que updates de más son inocuos; pero en este repo nunca se ha enviado un roster intermedio
de 2 a 7 entradas (siempre 1 y luego 8). Si la pantalla de matching se atasca con uno, el plan B es no retransmitir y
dejar a los que esperan con su roster de 1 entrada hasta la Etapa 2.

## Cliente

El literal `ns.exitgames.com` del APK se reemplaza por `--photon-host` / `KF_PHOTON_HOST` (por defecto el host del
`--base-url`). Los APK de sabor Photon se construyen con `KF_PHOTON=1 KF_PHOTON_HOST=51.79.241.70` y se sirven en
`/apk/photon-remote`, `/apk/photon-diag`, `/apk/photon-diag-remote` (`start-client-photon-diag.bat` para el emulador).
Sin `KF_PHOTON=1` el APK usa el puente offline (sala local con bots) y nunca toca Photon.

## Parche `0001-pending-battle-join.patch`

Los dos humanos reciben el id de sala en el Stage 3 a la vez; el segundo `JoinGame` llega al MasterServer antes de
que el creador haya entrado al GameServer (`is_created == false`) y `validate_join` respondía `GameIdNotExists`, que
el cliente trata como desconexion fatal ("Title Disconnect Error" en el jugador 2). El parche deja pasar ese caso para
ids `battle-*`; el handler del GameServer ya crea o une al llegar. Está en la rama `tanuki-server` de
`tanukifurhire/luxonserver` y el puntero del submodulo de este repo (`b35e3cf`) lo incluye.

## Parche `0003-expected-users-join-closed-battle-room.patch`

El cliente maestro cierra la sala (`IsOpen=false`) unos ~100 ms despues de crearla; si el otro humano llega al
GameServer despues de eso, `validate_join` respondia `GameClosed` (32764) y ese jugador (el mas lento, al azar) recibia
"Title Disconnect Error". Ambos humanos entran por el MasterServer y estan en `expected_users`, asi que para ids
`battle-*` la reserva vale aunque la sala ya este cerrada. Sustituido desde el 2026-10-05 por `f160000`, que salta el
`GameClosed` para toda sala `battle-*` (el MasterServer valida el join antes de añadir al jugador a `expected_users`,
así que la excepción de `0003` nunca cubría al que llegaba tarde). `0002` solo agrega el log de por que se rechaza un join
(`journalctl -u luxon-server | grep rejected`).

## Parche `0005-replay-room-properties-to-late-joiner.patch`

El maestro escribe las propiedades de sala (estado de batalla) ~40 ms despues de entrar; si el `JoinGame` del otro
humano llega despues, esas propiedades solo viajan en la respuesta del join y el cliente (que sale de MatchingScene
con `CallbackRoomPropertiesUpdate`, es decir con el *evento* PropertiesUpdate) se queda en el lobby. El servidor
reenvia ahora las propiedades actuales a quien entra segundo en una sala `battle-*` como evento, justo despues de
la respuesta del join. `0004` solo registra cada operacion (op code) que manda cada peer del GameServer.

## Parche `0006-luxon-enet-receive-window-4096.patch` (submodulo `Luxon/`, `deploy/photon/luxon-patches/`)

La reimplementacion ENet de Luxon tenia una ventana de recepcion fiable de 128 secuencias por canal (ENet real usa
4096). Con 8 kickers sincronizados por el maestro, un paquete fiable atrasado desbordaba la ventana en pocos frames,
el servidor tiraba paquetes fiables (`[ENet] Receive window incoming_reliable is too small!`) y el cliente acababa
desconectado: todos los kickers desaparecian y el reloj se paraba (2026-09-21). Ventanas ahora 4096 / 1024 / 256.

## Parche `0007-luxon-enet-resend-tolerance.patch` (submodulo `Luxon/`, `deploy/photon/luxon-patches/`)

`Client is now stale` en Luxon significa que un comando fiable enviado por el servidor no fue confirmado tras
`max_resends` reintentos o `disconnect_timeout_ms`. Con los valores de Luxon (7 / 10 s) una rafaga de perdida solo en
sentido servidor->cliente bastaba para expulsar al maestro (el emulador seguia vivo y confirmado hasta ese instante,
2026-09-21). Ahora 32 reintentos / 30 s (valores de ENet); el cliente Photon conserva su propio timeout de 10 s.

## Parche `0008-luxon-enet-disconnect-timeout.patch`

`ServerManager::setup()` construye su `EnetPeerConfig` a mano y fijaba `cfg.disconnect_timeout_ms = 5000`, con lo que
anulaba en silencio el valor de cabecera (30000) que subió `0007`: para el timeout de desconexión, `0007` era código
muerto y el valor real seguía siendo 5 s. Cualquier peer con un comando fiable sin confirmar durante 5 s era declarado
`Stale` y expulsado (`disconnect(true)`), y eso es lo que sacaba de la partida a clientes vivos durante cortes breves:
medido en partida, el peer del emulador quedaba `Stale` ~5 s después del último comando confirmado, no a los 30 s que
se creían vigentes. `server_manager.cpp` usa ahora 30000, igual que la cabecera.

Consecuencia práctica (2026-09-21): un corte de red de menos de ~10 s ya no expulsa a nadie por parte del servidor
(el servidor aguanta 30 s). A partir de ahí el que se desconecta es el propio cliente Photon con su timeout, y desde
ese momento la partida depende de que la reconexión del cliente funcione: ver el parche
"skip AnalysisManager.SendGameReconnectBegin" en `scripts/patch-il2cpp-endpoints.py`, que evita que el
`NullReferenceException` de un `GameManager._reconnectInfo` nulo aborte `GameManager.CallbackDisconnected` antes de
`ReconnectAsync` (dejaba la partida congelada para siempre).

## Parche `0009-luxon-token-takeover.patch`

Mientras un cliente está conectado, sus datos persistentes viven solo en `Peer::persistent` y no en el pool, así que
un cliente que reconecta con el token que se le entregó fallaba la autenticación ("Authentication failure: Got no
persistent peer data") hasta que la conexión vieja era reapeada como `Stale`, cosa que con `0008` tarda 30 s de
timeouts de keepalive. Medido en partida: PUN gastaba ~28 s reintentando el rejoin (71 intentos de estado 1 -> 2)
antes de poder volver a entrar a la sala. El parche busca el token entre las conexiones vivas y se queda con sus datos
persistentes, tirando la conexión vieja: un cliente que presenta un token de una sesión viva *es* esa sesión (PUN solo
reconecta con su propio token). `src/authentication.cpp`.

## Parche `0010-luxon-master-on-join.patch`

`Game::add_peer` no tocaba `master_actor`, y `Game::remove_peer` solo lo reasigna cuando la sala **no** queda vacía
(`game.cpp:226-228`). Como una partida sobrevive a sus peers (`empty_game_ttl`), la sala puede quedarse a cero con
`master_actor` apuntando todavía al actor que se fue; y `last_actor_id` nunca rebobina, así que el siguiente peer
recibe otro id y **la sala se queda sin maestro**. El valor por defecto de `master_actor` (`game.hpp:127`, `= 1`)
coincide con el primer actor que reparte `create_peer`, que es lo que hace que en retail el creador sea el maestro.

Es fatal para la reconexión de Kick-Flight: `ReconnectInfo` aprende quién es el maestro del property de sala
`MasterClientId` (248), que viaja en la respuesta de join; un cliente que reconecta a una sala de batalla vaciada
recibe "el maestro es otro", `UpdateReconnectWait` toma su rama no-maestra, sale cada frame por
`ReconnectInfo.IsSerializeRead == false` (esperando un `ReconnectSharedInfo` de un maestro que ya no existe) y acaba
en `BeginReconnectFailed` al agotar el presupuesto. Medido con la sonda 8992 en el cliente: causa **112 x 164**.

El parche asigna el mando al que entra cuando ningún peer vivo lo tiene:
`if (!find_peer(master_actor)) master_actor = fres.actor_id;` — la misma regla que ya aplica `remove_peer` cuando la
sala no está vacía. Con eso el cliente que reconecta es maestro y su propia rama de reanudación funciona (el parche
cliente en 0x1576B18 de `patch-il2cpp-endpoints.py`, que hasta ahora era inerte, pasa a ser el camino activo).

## Revisión del handoff pendiente (2026-09-23)

Comparé el submódulo `a3539b8` y `0001`-`0010` con `stash@{0}` (`5d60911`, basado en `a84ecc3`). El código actual
ya deja pasar en MasterServer un `battle-*` existente mientras `is_created == false`, reserva al usuario en
`expected_users` antes de responder con la dirección/token del GameServer y permite al primer peer crear la sala en
GameServer. Por tanto cubre la carrera concreta documentada en `0001` (el segundo `JoinGame` llega antes de que el
primero cree la sala). La lógica añadida por el stash exige que exista ya alguna reserva antes de permitir ese handoff;
en el orden normal de solicitudes serializadas de MasterServer es un endurecimiento equivalente, no un cambio del
handoff al GameServer. No modifica el encaminamiento ni la aceptación del GameServer.

Decisión: no aplicar el stash completo ni portar sus hunks todavía. El stash restaura además cambios vigentes de
`0001`-`0010` (timeout ENet, takeover de token, replay de propiedades y elección de maestro), lo que sería una regresión.
Como la reproducción reportada el 2026-09-23 sigue dejando clientes en `Master Finding game` en tres intentos, esa
observación contradice que el parche actual haya resuelto el síntoma de extremo a extremo; los cambios del stash no
explican ni reparan por sí solos ese estancamiento. El siguiente diagnóstico requiere logs del intercambio Photon para
separar una respuesta de JoinGame ausente/rechazada de una respuesta exitosa cuya dirección o conexión GameServer no
progresa. Esta revisión fue estática: no se usó ADB ni se hizo build.
