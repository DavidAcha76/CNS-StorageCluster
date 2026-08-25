# Red LAN y distribuidos.hermesoft.com

Los clientes están configurados por defecto para:

```text
distribuidos.hermesoft.com:5050
```

El nombre de dominio es válido para `TcpClient`: .NET resuelve DNS y abre un socket TCP hacia la IP resultante.

## Para despliegue real

1. El registro DNS `distribuidos.hermesoft.com` debe apuntar a la máquina/red del nodo central.
2. `5050/TCP` debe estar permitido en firewall.
3. Si existe router/NAT, debe redireccionar `5050/TCP` al servidor.
4. El proceso `CNS.StorageCluster.Server` debe estar ejecutándose; el `TcpListener` escucha en `0.0.0.0:5050`.
5. La web puede publicarse en HTTPS mediante IIS/reverse proxy hacia Kestrel `8080`.

## Para cumplir literalmente la restricción LAN durante la defensa

El documento indica que el ejercicio debe funcionar en LAN. Puedes conservar el mismo hostname configurando resolución local hacia la IP privada del servidor.

Ejemplo, si el servidor central tiene `192.168.1.50`, en Windows abre como administrador:

```text
C:\Windows\System32\drivers\etc\hosts
```

y añade:

```text
192.168.1.50 distribuidos.hermesoft.com
```

En Linux añade la misma línea a:

```text
/etc/hosts
```

Así la aplicación sigue apuntando a `distribuidos.hermesoft.com`, pero el tráfico se mantiene dentro de la LAN.

## Si se usa realmente a través de Internet

El protocolo de la práctica es TCP crudo y este proyecto no cifra el contenido por sí solo. El contexto del enunciado menciona túneles seguros para WAN; por eso, si los clientes van a llegar al dominio desde fuera de la LAN, coloca la comunicación dentro de una VPN/túnel seguro y expón `5050/TCP` únicamente por ese túnel. Para la defensa académica en LAN no es necesario agregar esa capa al código.
