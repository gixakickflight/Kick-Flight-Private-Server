# Servidor directo en macOS usando el hotspot del teléfono

La APK de trabajo está parcheada para sustituir las tres APIs de Grenge por una
URL HTTP de este Mac. En este modo no se usa proxy, certificado CA ni Tailscale.

## Dirección actual

Con el Mac conectado al hotspot:

```bash
ipconfig getifaddr en0
```

La configuración local debe contener esa dirección:

```json
{
  "serverBaseUrl": "http://172.27.12.136:18080"
}
```

Si la IP cambia, actualiza `config/apk-direct-server.local.json`, regenera el
catálogo y vuelve a producir la APK:

```bash
python3 scripts/build-title-resource-catalog.py
SERVER_BASE_URL=http://IP_DEL_MAC:18080 ./scripts/build-direct-apk.sh
```

La APK parcheada existente puede reutilizarse como entrada; no hace falta el
original para cambiar de una IP directa a otra.

## Arrancar el servidor

```bash
./scripts/run-direct.sh
```

Comprueba localmente:

```bash
curl http://IP_DEL_MAC:18080/health/ready
```

## Instalar en Android

Estar conectado al hotspot no activa ADB. Conecta el teléfono por USB y acepta
la autorización de depuración, o habilita Depuración inalámbrica y empareja este
Mac. Después:

```bash
adb devices -l
adb install -r .local/artifacts/KickFlight-2.11.0-direct-IP_DEL_MAC-18080.apk
```

Si Android muestra un conflicto de firma, la APK instalada fue firmada con otra
clave. Para una instalación de pruebas limpia, desinstala primero el paquete
`world.global.fenix`; eso también elimina sus datos locales.

El teléfono debe poder abrir `http://IP_DEL_MAC:18080/health/ready`. Algunos
hotspots bloquean conexiones hacia sus clientes; el hotspot actual debe probarse
antes de investigar el protocolo del juego.
