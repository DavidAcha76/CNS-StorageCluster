using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CNS.StorageCluster.Server.Data;
using CNS.StorageCluster.Server.Models;
using CNS.StorageCluster.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CNS.StorageCluster.Server.Services;

public sealed class TcpServerService(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<TcpServerOptions> options,
    ILogger<TcpServerService> logger) : BackgroundService
{
    private readonly TcpServerOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private TcpListener? _listener;

    public IReadOnlyCollection<string> ConnectedNodeCodes => _sessions.Keys.ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, _options.Port);
            _listener.Start();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo iniciar el listener TCP en {Port}; WebSocket seguira disponible.", _options.Port);
            return;
        }
        logger.LogInformation("Servidor TCP escuchando en 0.0.0.0:{Port}", _options.Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                _ = HandleClientAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        string? registeredCode = null;
        ClientSession? ownedSession = null;
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            var firstLine = await reader.ReadLineAsync(serverToken);
            if (string.IsNullOrWhiteSpace(firstLine) || ProtocolJson.GetMessageType(firstLine) != MessageTypes.Register)
            {
                await writer.WriteLineAsync(ProtocolJson.Serialize(new ErrorMessage(MessageTypes.Error, "El primer mensaje debe ser REGISTER.")));
                return;
            }

            var registration = JsonSerializer.Deserialize<RegisterMessage>(firstLine, ProtocolJson.Options);
            if (registration is null || !RegionCatalog.TryGet(registration.NodeCode, out var region))
            {
                await writer.WriteLineAsync(ProtocolJson.Serialize(new ErrorMessage(MessageTypes.Error, "Código regional inválido. Use una de las 9 regionales configuradas.")));
                return;
            }

            registeredCode = region.Code;

            if (!_sessions.ContainsKey(registeredCode) && _sessions.Count >= RegionCatalog.All.Count)
            {
                await writer.WriteLineAsync(ProtocolJson.Serialize(new ErrorMessage(MessageTypes.Error, "El cluster ya tiene 9 clientes conectados.")));
                return;
            }

            // Si el mismo nodo se reconecta después de un corte de red, la conexión nueva reemplaza
            // cualquier socket anterior que haya quedado medio abierto. Sigue existiendo una sola sesión por regional.
            if (_sessions.TryRemove(registeredCode, out var previousSession))
            {
                previousSession.Close();
                logger.LogInformation("Sesión anterior de {Code} reemplazada por una reconexión nueva", registeredCode);
            }

            var session = new ClientSession(registeredCode, client, writer);
            if (!_sessions.TryAdd(registeredCode, session))
            {
                await writer.WriteLineAsync(ProtocolJson.Serialize(new ErrorMessage(MessageTypes.Error, $"No se pudo registrar la sesión de {registeredCode}. Reintente.")));
                return;
            }
            ownedSession = session;

            await RegisterNodeAsync(registration, region, serverToken);
            logger.LogInformation("Nodo {Code} ({Region}) conectado", region.Code, region.Name);

            while (!serverToken.IsCancellationRequested && client.Connected)
            {
                var line = await reader.ReadLineAsync(serverToken);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                await ProcessClientMessageAsync(registeredCode, line, serverToken);
            }
        }
        catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Conexión TCP finalizada con error para {Code}", registeredCode ?? "sin registrar");
        }
        finally
        {
            if (registeredCode is not null && ownedSession is not null &&
                _sessions.TryGetValue(registeredCode, out var current) && ReferenceEquals(current, ownedSession) &&
                _sessions.TryRemove(registeredCode, out var removed))
            {
                removed.Close();
                logger.LogInformation("Nodo {Code} desconectado. Se marcará NO_REPORTA al superar el timeout.", registeredCode);
            }
            try { client.Close(); } catch { }
        }
    }

    public async Task HandleWebSocketAsync(WebSocket socket, CancellationToken serverToken)
    {
        string? registeredCode = null;
        ClientSession? ownedSession = null;
        try
        {
            var firstMessage = await ReceiveWebSocketMessageAsync(socket, serverToken);
            if (string.IsNullOrWhiteSpace(firstMessage) || ProtocolJson.GetMessageType(firstMessage) != MessageTypes.Register)
            {
                await SendWebSocketMessageAsync(socket, ProtocolJson.Serialize(new ErrorMessage(MessageTypes.Error, "El primer mensaje debe ser REGISTER.")), serverToken);
                return;
            }

            var registration = JsonSerializer.Deserialize<RegisterMessage>(firstMessage, ProtocolJson.Options);
            if (registration is null || !RegionCatalog.TryGet(registration.NodeCode, out var region))
            {
                await SendWebSocketMessageAsync(socket, ProtocolJson.Serialize(new ErrorMessage(MessageTypes.Error, "Codigo regional invalido. Use una de las 9 regionales configuradas.")), serverToken);
                return;
            }

            registeredCode = region.Code;
            if (!_sessions.ContainsKey(registeredCode) && _sessions.Count >= RegionCatalog.All.Count)
            {
                await SendWebSocketMessageAsync(socket, ProtocolJson.Serialize(new ErrorMessage(MessageTypes.Error, "El cluster ya tiene 9 clientes conectados.")), serverToken);
                return;
            }

            if (_sessions.TryRemove(registeredCode, out var previousSession))
            {
                previousSession.Close();
                logger.LogInformation("Sesion anterior de {Code} reemplazada por una reconexion WebSocket", registeredCode);
            }

            var session = new ClientSession(registeredCode, socket);
            if (!_sessions.TryAdd(registeredCode, session))
            {
                await SendWebSocketMessageAsync(socket, ProtocolJson.Serialize(new ErrorMessage(MessageTypes.Error, $"No se pudo registrar la sesion de {registeredCode}. Reintente.")), serverToken);
                return;
            }
            ownedSession = session;

            await RegisterNodeAsync(registration, region, serverToken);
            logger.LogInformation("Nodo {Code} ({Region}) conectado por WebSocket", region.Code, region.Name);

            while (!serverToken.IsCancellationRequested)
            {
                var message = await ReceiveWebSocketMessageAsync(socket, serverToken);
                if (message is null) break;
                if (string.IsNullOrWhiteSpace(message)) continue;
                await ProcessClientMessageAsync(registeredCode, message, serverToken);
            }
        }
        catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Conexion WebSocket finalizada con error para {Code}", registeredCode ?? "sin registrar");
        }
        finally
        {
            if (registeredCode is not null && ownedSession is not null &&
                _sessions.TryGetValue(registeredCode, out var current) && ReferenceEquals(current, ownedSession) &&
                _sessions.TryRemove(registeredCode, out var removed))
            {
                removed.Close();
                logger.LogInformation("Nodo {Code} desconectado. Se marcara NO_REPORTA al superar el timeout.", registeredCode);
            }
            else
            {
                try { socket.Abort(); } catch { }
            }
        }
    }

    private static async Task SendWebSocketMessageAsync(WebSocket socket, string json, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string?> ReceiveWebSocketMessageAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var data = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("Se esperaba un mensaje WebSocket de texto.");

            data.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(data.ToArray());
        }
    }

    private async Task RegisterNodeAsync(RegisterMessage message, RegionDefinition region, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var node = await db.Nodes.SingleOrDefaultAsync(x => x.Code == region.Code, ct);
        var now = DateTime.UtcNow;
        var transitionedOnline = node is null || node.Status != NodeStates.Online;

        if (node is null)
        {
            node = new StorageNode
            {
                Code = region.Code,
                RegionName = region.Name,
                FirstSeenUtc = now
            };
            db.Nodes.Add(node);
        }

        node.RegionName = region.Name;
        node.MachineName = message.MachineName;
        node.OperatingSystem = message.OperatingSystem;
        node.ClientVersion = message.ClientVersion;
        node.ReportIntervalSeconds = Math.Clamp(message.ReportIntervalSeconds, NetworkDefaults.MinimumReportIntervalSeconds, NetworkDefaults.MaximumReportIntervalSeconds);
        node.LastSeenUtc = now;
        node.Status = NodeStates.Online;
        await db.SaveChangesAsync(ct);

        if (transitionedOnline)
        {
            db.NodeEvents.Add(new NodeEvent
            {
                NodeId = node.Id,
                EventType = NodeStates.Online,
                TimestampUtc = now,
                Detail = "Cliente registrado/conectado automáticamente."
            });
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task ProcessClientMessageAsync(string nodeCode, string json, CancellationToken ct)
    {
        var type = ProtocolJson.GetMessageType(json);
        switch (type)
        {
            case MessageTypes.Metrics:
                var metrics = JsonSerializer.Deserialize<MetricsMessage>(json, ProtocolJson.Options);
                if (metrics is not null) await PersistMetricsAsync(nodeCode, metrics, ct);
                break;

            case MessageTypes.Ack:
                var ack = JsonSerializer.Deserialize<AckMessage>(json, ProtocolJson.Options);
                if (ack is not null) await PersistAckAsync(nodeCode, ack, ct);
                break;

            case MessageTypes.ClientConfig:
                var config = JsonSerializer.Deserialize<ClientConfigMessage>(json, ProtocolJson.Options);
                if (config is not null) await PersistClientIntervalAsync(nodeCode, config.ReportIntervalSeconds, ct);
                break;
        }
    }

    private async Task PersistMetricsAsync(string nodeCode, MetricsMessage msg, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var node = await db.Nodes.SingleAsync(x => x.Code == nodeCode, ct);
        var now = DateTime.UtcNow;
        var transitionedOnline = node.Status != NodeStates.Online;
        node.LastSeenUtc = now;
        node.Status = NodeStates.Online;
        if (transitionedOnline)
        {
            db.NodeEvents.Add(new NodeEvent
            {
                NodeId = node.Id,
                EventType = NodeStates.Online,
                TimestampUtc = now,
                Detail = "El nodo volvió a reportar métricas."
            });
        }

        db.Metrics.Add(new MetricRecord
        {
            NodeId = node.Id,
            TimestampUtc = msg.TimestampUtc,
            DiskName = msg.DiskName,
            DiskType = msg.DiskType,
            TotalGb = msg.TotalGb,
            UsedGb = msg.UsedGb,
            FreeGb = msg.FreeGb,
            UtilizationPercent = msg.UtilizationPercent,
            Iops = msg.Iops,
            IopsSimulated = msg.IopsSimulated,
            LatencyMs = msg.LatencyMs
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task PersistAckAsync(string nodeCode, AckMessage ack, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var command = await db.Commands
            .Include(x => x.Node)
            .SingleOrDefaultAsync(x => x.CommandId == ack.CommandId && x.Node!.Code == nodeCode, ct);
        if (command is null) return;
        command.Status = "ACK";
        command.AckAtUtc = ack.ReceivedAtUtc;
        command.AckDetail = ack.Detail;
        if (command.Kind == "CONFIG_INTERVAL" && command.Node is not null && int.TryParse(command.Payload, out var interval))
        {
            command.Node.ReportIntervalSeconds = Math.Clamp(interval, NetworkDefaults.MinimumReportIntervalSeconds, NetworkDefaults.MaximumReportIntervalSeconds);
            command.Node.LastSeenUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task PersistClientIntervalAsync(string nodeCode, int seconds, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var node = await db.Nodes.SingleOrDefaultAsync(x => x.Code == nodeCode, ct);
        if (node is null) return;
        node.ReportIntervalSeconds = Math.Clamp(seconds, NetworkDefaults.MinimumReportIntervalSeconds, NetworkDefaults.MaximumReportIntervalSeconds);
        node.LastSeenUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<(bool Ok, string Detail)> SendCommandAsync(string nodeCode, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return (false, "El mensaje está vacío.");
        message = message.Trim();
        if (message.Length > 900) return (false, "El mensaje supera el máximo de 900 caracteres.");
        var commandId = Guid.NewGuid().ToString("N");
        return await SendTrackedAsync(nodeCode, commandId, "COMMAND", message,
            new CommandMessage(MessageTypes.Command, commandId, message, DateTime.UtcNow), ct);
    }

    public async Task<(bool Ok, string Detail)> SendIntervalAsync(string nodeCode, int seconds, CancellationToken ct = default)
    {
        seconds = Math.Clamp(seconds, NetworkDefaults.MinimumReportIntervalSeconds, NetworkDefaults.MaximumReportIntervalSeconds);
        var commandId = Guid.NewGuid().ToString("N");
        return await SendTrackedAsync(nodeCode, commandId, "CONFIG_INTERVAL", seconds.ToString(),
            new ConfigIntervalMessage(MessageTypes.ConfigInterval, commandId, seconds, DateTime.UtcNow), ct);
    }

    private async Task<(bool Ok, string Detail)> SendTrackedAsync<T>(
        string nodeCode,
        string commandId,
        string kind,
        string payload,
        T message,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var node = await db.Nodes.SingleOrDefaultAsync(x => x.Code == nodeCode, ct);
        if (node is null) return (false, "El nodo todavía no se ha registrado.");

        var record = new CommandRecord
        {
            NodeId = node.Id,
            CommandId = commandId,
            Kind = kind,
            Payload = payload,
            SentAtUtc = DateTime.UtcNow,
            Status = "PENDING"
        };
        db.Commands.Add(record);

        if (!_sessions.TryGetValue(nodeCode, out var session))
        {
            record.Status = "FAILED_OFFLINE";
            await db.SaveChangesAsync(ct);
            return (false, "El nodo no está conectado.");
        }

        // Persistir SENT antes de escribir al socket evita perder un ACK muy rápido.
        record.Status = "SENT";
        await db.SaveChangesAsync(ct);

        try
        {
            await session.SendLineAsync(ProtocolJson.Serialize(message), ct);
            return (true, "Mensaje enviado. Esperando ACK del cliente.");
        }
        catch (Exception ex)
        {
            // Si el cliente alcanzó a confirmar antes del error de escritura, no sobrescribir ACK.
            await db.Entry(record).ReloadAsync(ct);
            if (record.Status != "ACK")
            {
                record.Status = "FAILED";
                await db.SaveChangesAsync(ct);
            }
            logger.LogWarning(ex, "No se pudo enviar al nodo {Code}", nodeCode);
            return (false, "No se pudo enviar el mensaje al cliente.");
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var session in _sessions.Values) session.Close();
        _listener?.Stop();
        return base.StopAsync(cancellationToken);
    }
}
