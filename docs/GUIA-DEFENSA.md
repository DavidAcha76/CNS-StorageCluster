# Guía corta para la defensa

## Arquitectura

Cliente-servidor con monitoreo centralizado. Los clientes regionales ejecutan una app Avalonia en Windows o Linux. El nodo central ejecuta ASP.NET Core Razor Pages, escucha sockets TCP en `5050` y persiste la información en SQL Server. Los clientes vienen configurados para `distribuidos.hermesoft.com:5050`.

## Flujo de comunicación

1. Cliente abre `TcpClient` hacia `distribuidos.hermesoft.com:5050`.
2. Envía `REGISTER`.
3. Servidor valida que sea una de las nueve regionales y la registra automáticamente en BD.
4. Cliente obtiene el primer disco y envía `METRICS` periódicamente.
5. Servidor guarda el histórico y actualiza `LastSeen`/estado.
6. Servidor puede enviar `COMMAND` o `CONFIG_INTERVAL`.
7. Cliente muestra el mensaje/configuración, lo guarda en `.log` y responde `ACK`.
8. Servidor persiste el ACK.

## Concurrencia

`TcpServerService` mantiene un `TcpListener`. Cada `TcpClient` aceptado se procesa de forma asíncrona e independiente. La base de datos usa un `DbContext` independiente por operación y las escrituras al mismo socket se serializan con `SemaphoreSlim`.

## Tolerancia básica a fallos

El cliente tiene reconexión automática cada 5 segundos. Si una conexión antigua queda medio abierta, una reconexión nueva del mismo código regional reemplaza la sesión anterior. `NodeHealthService` comprueba la última actividad y usa un timeout que respeta el intervalo configurado para evitar falsos `NO_REPORTA`.

## Gestión de estados

- `ACTIVO`: nodo conectado/reportando.
- `NO_REPORTA`: superó el tiempo permitido sin enviar reportes.
- Cada cambio de estado se persiste en `NodeEvents`.

## Modelo de datos

- `Nodes`: identidad, equipo, sistema operativo, estado, primera/última conexión e intervalo.
- `Metrics`: histórico del disco, timestamp, utilización, IOPS y latencia.
- `NodeEvents`: transiciones ACTIVO/NO_REPORTA para uptime/failovers.
- `Commands`: mensajes/configuraciones y ACK.

## KPIs para mostrar

- Total Capacity, Used Capacity, Free Capacity y % Utilization.
- Growth Rate GB/día y GB/mes.
- Nodo UP/DOWN.
- Uptime acumulado.
- Failover events.
- Número de nodos activos.
- Availability y comparación con 99.9%.
- Capacidad total global, libre global y utilización global.
- Latencia promedio ponderada.
- Overcommit: No aplica por no usar thin provisioning.
- Storage Pool Fragmentation: No aplica porque no existe pool físico compartido.
- Quorum: No aplica porque no existe algoritmo de consenso.
- Replication Health: No aplica porque no existe replicación física.

## ¿Por qué TCP?

Porque el enunciado lo exige y permite una conexión persistente bidireccional en la que ambos extremos pueden enviar mensajes.

## ¿Por qué JSON delimitado por línea?

TCP es un flujo de bytes y no conserva límites de mensajes. Cada objeto JSON termina en salto de línea; `ReadLineAsync` reconstruye un mensaje completo de manera simple y fácil de explicar.

## ¿Cómo demostrar Windows/Linux?

Es exactamente el mismo proyecto Avalonia/.NET. Se publica con runtime `win-x64` para Windows y `linux-x64` para Linux.

## Prueba sugerida en vivo

1. Mostrar dashboard con nueve regionales.
2. Conectar cliente Windows.
3. Conectar cliente Linux.
4. Mostrar que ambos aparecen ACTIVO y envían métricas.
5. Enviar un mensaje a uno de ellos y enseñar el `.log` + ACK.
6. Cambiar el intervalo desde el servidor.
7. Desconectar un cliente, esperar timeout y mostrar `NO_REPORTA`.
8. Reconectarlo y mostrar que regresa a ACTIVO.
9. Abrir el detalle y explicar histórico, uptime, failover y growth rate.
