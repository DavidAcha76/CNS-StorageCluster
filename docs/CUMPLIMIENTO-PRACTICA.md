# Matriz de cumplimiento técnico de la Práctica 1

| Requisito del enunciado | Implementación del proyecto |
|---|---|
| 9 nodos clientes | `RegionCatalog` contiene exactamente ORU, LPZ, SCZ, BEN, TJA, PND, CBB, CHQ y PTS |
| 1 nodo central | `CNS.StorageCluster.Server` |
| Arquitectura cliente-servidor | Cliente Avalonia ↔ servidor ASP.NET Core |
| TCP obligatorio | `TcpClient` / `TcpListener` |
| Socket cliente | `StorageClientService` |
| Socket servidor | `TcpServerService` |
| Bidireccional | METRICS/CLIENT_CONFIG → servidor; COMMAND/CONFIG_INTERVAL → cliente; ACK → servidor |
| Concurrencia | `AcceptTcpClientAsync` + tarea asíncrona independiente por conexión |
| Persistencia | SQL Server + EF Core |
| Histórico de métricas | `MetricRecord` con `TimestampUtc` y `NodeId` |
| Identificador cliente | código regional de 3 letras |
| Estado del nodo | `ACTIVO` / `NO_REPORTA` en `StorageNode` + histórico `NodeEvent` |
| Discos detectados | `DiskMetricsProvider` lee todos los `DriveInfo` fijos o removibles listos en cada ciclo |
| SSD/HDD | Linux `lsblk`; Windows `Get-PhysicalDisk`; fallback `UNKNOWN` |
| Capacidad total | `TotalGb` |
| Espacio utilizado | `UsedGb` |
| Espacio libre | `FreeGb` |
| IOPS | simulación explícita permitida por el enunciado |
| Fecha/hora | `TimestampUtc` por reporte |
| Envío periódico | `MetricsLoopAsync` |
| Intervalo parametrizable | 2–3600 segundos |
| Varios discos y nuevos volúmenes | cada `METRICS` contiene una colección de discos; un volumen agregado aparece en el siguiente ciclo |
| Exactamente 9 clientes | máximo 9 códigos válidos y una sesión simultánea por regional |
| Adición automática | REGISTER crea el nodo en BD y lo asigna `ACTIVO` |
| Detectar nodos sin reporte | `NodeHealthService` y timeout configurable |
| Mostrar “No Reporta” | dashboard y detalle |
| Mensajes personalizados | formulario del detalle de nodo |
| Cliente recibe mensajes | `ReceiveLoopAsync` |
| Cliente guarda .log | `CNS.StorageCluster/logs/client-AAAA-MM-DD.log` dentro de los datos locales del usuario |
| ACK | `AckMessage`, almacenado en `CommandRecord` |
| Intervalo desde cliente | botón `Aplicar desde cliente` |
| Intervalo desde servidor | formulario `Configurar intervalo` |
| Dashboard gráfico | Razor Pages + CSS |
| Lista 9 servidores | 9 tarjetas fijas |
| Capacidad individual | tarjeta y detalle |
| Espacio libre individual | tarjeta y detalle |
| Estado | ACTIVO / NO REPORTA |
| Totales consolidados | total/usado/libre/% utilización |
| Auto-refresh parametrizable | selector 2/5/10/30/60 s |
| Total Capacity | dashboard y detalle |
| Used Capacity | dashboard y detalle |
| Free Capacity | dashboard y detalle |
| % Utilization | por nodo y global |
| Growth Rate | GB/día y GB/mes |
| Overcommit Ratio | `No aplica`: no existe thin provisioning |
| Storage Pool Fragmentation | `No aplica`: no existe pool físico compartido |
| Nodo UP/DOWN | ACTIVO / NO REPORTA |
| Uptime | acumulado desde eventos |
| Failover events | conteo de transiciones a NO_REPORTA |
| Número de nodos activos | dashboard |
| Quorum | `No aplica`: no se implementa consenso |
| Replication health | `No aplica`: no se implementa replicación física |
| Availability ≥ 99.9% | porcentaje calculado y señal CUMPLE/NO CUMPLE |
| Capacidad total global | dashboard |
| Espacio libre global | dashboard |
| Utilización global | dashboard |
| Latencia promedio ponderada | ponderada por capacidad de nodos activos |
| Nodos activos/totales | dashboard |
| Cliente Windows/Linux | Avalonia UI + publicaciones `win-x64` y `linux-x64` |
| 2 clientes reales + 1 central | arquitectura y scripts preparados para esa defensa |

## Aspectos de evaluación que no son código preimplementable

- **Dos requerimientos de último momento:** el docente los define durante la defensa; no pueden conocerse de antemano.
- **Roles/nombres reales, Git y Trello:** deben completarse con los datos reales del equipo.
- **Caracterización de equipo (Ver Reglamento):** requiere el reglamento externo que no forma parte del enunciado entregado.
