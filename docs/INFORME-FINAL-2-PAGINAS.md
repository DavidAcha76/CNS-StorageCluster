# Informe Final - Práctica 1 Storage Cluster

> Completar los campos entre corchetes con los datos reales del equipo. Mantener el informe final en un máximo de 2 páginas al pasarlo al formato solicitado por el docente.

## 1. Implementación realizada

Se implementó un Storage Cluster lógico con monitoreo centralizado compuesto por nueve identidades regionales y un nodo central. Los clientes son aplicaciones gráficas multiplataforma desarrolladas con Avalonia UI y .NET 10, capaces de ejecutarse en Windows y Linux. Cada cliente obtiene las métricas de todos los discos disponibles: nombre, tipo SSD/HDD, capacidad total, espacio utilizado, espacio libre, porcentaje de utilización, IOPS simulado y fecha/hora del reporte. Un disco o volumen agregado se detecta automáticamente en el siguiente ciclo de lectura.

La comunicación utiliza sockets TCP/IP bidireccionales. Al conectarse, el cliente envía un mensaje REGISTER; el servidor valida la regional, registra automáticamente el cliente en SQL Server y lo marca ACTIVO. Posteriormente recibe mensajes METRICS de forma periódica. El servidor atiende las conexiones concurrentemente mediante programación asíncrona. Si un nodo supera el timeout sin reportar, se registra el evento y pasa a NO_REPORTA.

El servidor central está desarrollado con ASP.NET Core Razor Pages y Entity Framework Core. El dashboard muestra las nueve regionales, capacidad individual, usado/libre, estado, utilización, IOPS, latencia y los totales consolidados. También calcula Growth Rate, Uptime, Availability, failover events, nodos activos y latencia promedio ponderada. Los indicadores asociados a thin provisioning, pool físico, consenso o replicación se muestran como No aplica porque el alcance es un cluster lógico de monitoreo.

La comunicación es bidireccional: desde el dashboard se pueden enviar mensajes personalizados y cambios del intervalo de reporte. El cliente recibe estos mensajes, los registra en un archivo `.log` y responde con ACK. El servidor persiste el estado del ACK para demostrar la recepción.

## 2. Gestión del proyecto

- **Repositorio Git:** [PEGAR URL REAL]
- **Tablero Trello:** [PEGAR URL REAL]
- **Arquitecto:** [NOMBRE]
- **Projects / Gestor:** [NOMBRE]
- **Otros integrantes y responsabilidades:** [COMPLETAR]

Las tareas se organizaron en arquitectura/protocolo, implementación del socket servidor, cliente Windows/Linux, persistencia SQL Server, dashboard, pruebas de red y preparación de defensa. Las fechas críticas y responsabilidades definitivas se encuentran en el microinforme del equipo.

## 3. Pruebas y resultados

La prueba funcional debe realizarse con al menos dos clientes reales y un servidor central. Se recomienda utilizar un cliente Windows y otro Linux para demostrar multiplataforma. Debe verificarse: registro automático, envío periódico de métricas, visualización del dashboard, detección NO_REPORTA al desconectar un nodo, reconexión, mensaje servidor→cliente, creación del archivo `.log`, ACK y cambio remoto del intervalo.

## 4. Conclusiones

La solución demuestra los conceptos centrales de sistemas distribuidos solicitados: comunicación entre procesos mediante sockets TCP, concurrencia, monitoreo centralizado, persistencia histórica, sincronización periódica y tolerancia básica a fallos. La separación entre cliente regional y nodo central permite incorporar nodos automáticamente manteniendo una única fuente de monitoreo. El uso de un protocolo JSON simple facilita observar y defender el flujo de comunicación, mientras que el histórico de métricas y eventos permite analizar la disponibilidad y el comportamiento del almacenamiento a lo largo del tiempo.
