# Publicacion en MonsterASP con WebSocket

La aplicacion de escritorio publicada se conecta por defecto a:

```text
wss://distribuidos.hermesoft.com/ws/cluster
```

Ese canal usa HTTPS/WSS por el puerto 443, que funciona a traves de MonsterASP y Cloudflare. El modo TCP directo por el puerto 5050 se conserva para la red LAN.

## Publicar

1. Vuelve a publicar la carpeta completa de `CNS.StorageCluster.Server` en la raiz del sitio de MonsterASP.
2. Reinicia la aplicacion desde el panel de MonsterASP.
3. En Cloudflare, confirma que WebSockets este habilitado para el dominio y conserva el registro como proxied.
4. Ejecuta `./scripts/publish-clients.ps1` y distribuye los nuevos ejecutables de escritorio.

El cliente trae `distribuidos.hermesoft.com` y el puerto `443`. Con ese puerto selecciona WSS automaticamente. Para la defensa en LAN se puede escribir `5050`; el cliente usara TCP directo como antes.

## Comprobacion

Con el servidor publicado, una solicitud HTTP normal a:

```text
https://distribuidos.hermesoft.com/ws/cluster
```

debe responder `400 Bad Request`. Eso confirma que la ruta existe; el navegador normal no esta realizando el handshake WebSocket. La aplicacion de escritorio debe mostrar `CONECTADO`, registrar la regional y enviar la primera metrica.

Si muestra `404`, se publico una version anterior del servidor. Si muestra un error de handshake, revisa que WebSockets este habilitado en Cloudflare y que el sitio se haya reiniciado en MonsterASP.
