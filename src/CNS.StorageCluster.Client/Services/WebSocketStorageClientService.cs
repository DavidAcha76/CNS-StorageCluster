using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CNS.StorageCluster.Shared;

namespace CNS.StorageCluster.Client.Services;

public sealed class WebSocketStorageClientService(string nodeCode, string host, int port, int initialIntervalSeconds) : IStorageClientService
{
    private readonly DiskMetricsProvider _metricsProvider = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _logLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private ClientWebSocket? _socket;
    private TransportCipher? _transportCipher;
    private int _reportIntervalSeconds = Math.Clamp(initialIntervalSeconds, NetworkDefaults.MinimumReportIntervalSeconds, NetworkDefaults.MaximumReportIntervalSeconds);
    private volatile bool _connected;

    public event Action<string>? Log;
    public event Action<bool>? ConnectionChanged;
    public event Action<MetricsMessage>? MetricsProduced;
    public event Action<int>? IntervalChanged;

    public Task StartAsync()
    {
        if (_runTask is not null && !_runTask.IsCompleted) return Task.CompletedTask;
        _transportCipher ??= TransportCipher.FromEnvironment();
        _cts = new CancellationTokenSource();
        _runTask = RunReconnectLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_runTask is not null)
        {
            try { await _runTask; } catch { }
        }
        SetConnected(false);
    }

    public async Task SetLocalIntervalAsync(int seconds)
    {
        seconds = Math.Clamp(seconds, NetworkDefaults.MinimumReportIntervalSeconds, NetworkDefaults.MaximumReportIntervalSeconds);
        Interlocked.Exchange(ref _reportIntervalSeconds, seconds);
        IntervalChanged?.Invoke(seconds);
        Log?.Invoke($"Intervalo cambiado desde cliente a {seconds} s.");
        if (_connected)
            await SendAsync(new ClientConfigMessage(MessageTypes.ClientConfig, nodeCode, seconds, DateTime.UtcNow), CancellationToken.None);
    }

    private async Task RunReconnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Conexion perdida: {ex.Message}");
            }
            finally
            {
                _socket = null;
                SetConnected(false);
            }

            if (!ct.IsCancellationRequested)
            {
                Log?.Invoke("Reintentando conexion en 5 segundos...");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch { }
            }
        }
    }

    private async Task RunConnectionAsync(CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        var endpoint = new UriBuilder(Uri.UriSchemeWss, host, port, NetworkDefaults.WebSocketPath).Uri;
        Log?.Invoke($"Conectando a {endpoint}...");
        await socket.ConnectAsync(endpoint, ct);
        _socket = socket;

        var (mac, ip) = DiskMetricsProvider.GetNetworkIdentity();
        var localTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var registration = new RegisterMessage(
            MessageTypes.Register,
            nodeCode,
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            "1.0.0",
            Volatile.Read(ref _reportIntervalSeconds),
            mac,
            ip,
            localTime);
        await SendAsync(registration, ct);
        SetConnected(true);
        Log?.Invoke($"Conectado y registrado automaticamente (IP: {ip}, MAC: {mac}, Hora: {localTime}).");

        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receive = ReceiveLoopAsync(socket, connectionCts.Token);
        var send = MetricsLoopAsync(connectionCts.Token);
        await Task.WhenAny(receive, send);
        connectionCts.Cancel();
        try { await Task.WhenAll(receive, send); } catch when (!ct.IsCancellationRequested) { }
    }

    private async Task MetricsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var metrics = await _metricsProvider.ReadAsync(nodeCode, host, ct);
            await SendAsync(metrics, ct);
            MetricsProduced?.Invoke(metrics);
            Log?.Invoke($"Metricas enviadas: {metrics.DiskCount} disco(s) leídos.");
            await Task.Delay(TimeSpan.FromSeconds(Volatile.Read(ref _reportIntervalSeconds)), ct);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var message = await ReceiveWebSocketMessageAsync(socket, ct);
            if (message is null) throw new IOException("El servidor cerro la conexion.");
            if (string.IsNullOrWhiteSpace(message)) continue;

            var type = ProtocolJson.GetMessageType(message);
            switch (type)
            {
                case MessageTypes.Command:
                    var command = JsonSerializer.Deserialize<CommandMessage>(message, ProtocolJson.Options);
                    if (command is not null)
                    {
                        var isGenerateReport = string.Equals(command.Message?.Trim(), "GENERATE_REPORT", StringComparison.OrdinalIgnoreCase) ||
                                               command.Message?.Contains("GENERATE_REPORT", StringComparison.OrdinalIgnoreCase) == true ||
                                               command.Message?.Contains("GENERAR_REPORTE", StringComparison.OrdinalIgnoreCase) == true;

                        if (isGenerateReport)
                        {
                            var filePath = await _metricsProvider.GenerateReportFileAsync(nodeCode, host, ct);
                            var logMsg = $"📄 REPORTE GENERADO: Archivo .txt creado en cliente: {filePath}";
                            Log?.Invoke(logMsg);
                            await WriteServerLogAsync(logMsg, ct);
                            await SendAsync(new AckMessage(MessageTypes.Ack, command.CommandId, nodeCode, $"Reporte .txt generado en cliente: {filePath}", DateTime.UtcNow), ct);
                            Log?.Invoke("ACK de reporte enviado al servidor.");
                        }
                        else
                        {
                            var text = $"MENSAJE DEL SERVIDOR: {command.Message}";
                            Log?.Invoke(text);
                            await WriteServerLogAsync(text, ct);
                            await SendAsync(new AckMessage(MessageTypes.Ack, command.CommandId, nodeCode, "Mensaje recibido y guardado en .log", DateTime.UtcNow), ct);
                            Log?.Invoke("ACK enviado al servidor.");
                        }
                    }
                    break;

                case MessageTypes.ConfigInterval:
                    var config = JsonSerializer.Deserialize<ConfigIntervalMessage>(message, ProtocolJson.Options);
                    if (config is not null)
                    {
                        var seconds = Math.Clamp(config.ReportIntervalSeconds, NetworkDefaults.MinimumReportIntervalSeconds, NetworkDefaults.MaximumReportIntervalSeconds);
                        Interlocked.Exchange(ref _reportIntervalSeconds, seconds);
                        IntervalChanged?.Invoke(seconds);
                        var text = $"CONFIGURACION DEL SERVIDOR: intervalo cambiado a {seconds} s.";
                        Log?.Invoke(text);
                        await WriteServerLogAsync(text, ct);
                        await SendAsync(new AckMessage(MessageTypes.Ack, config.CommandId, nodeCode, $"Intervalo aplicado: {seconds}s", DateTime.UtcNow), ct);
                        Log?.Invoke("ACK de configuracion enviado.");
                    }
                    break;

                case MessageTypes.Error:
                    var error = JsonSerializer.Deserialize<ErrorMessage>(message, ProtocolJson.Options);
                    throw new InvalidOperationException(error?.Message ?? "Error reportado por el servidor.");
            }
        }
    }

    private async Task SendAsync<T>(T message, CancellationToken ct)
    {
        var socket = _socket ?? throw new InvalidOperationException("WebSocket no conectado.");
        var json = ProtocolJson.Serialize(message);
        var payload = Encoding.UTF8.GetBytes(Cipher.Encrypt(json));
        await _sendLock.WaitAsync(ct);
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<string?> ReceiveWebSocketMessageAsync(ClientWebSocket socket, CancellationToken ct)
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
            if (result.EndOfMessage) return Cipher.Decrypt(Encoding.UTF8.GetString(data.ToArray()));
        }
    }

    private async Task WriteServerLogAsync(string text, CancellationToken ct)
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var baseFolder = string.IsNullOrWhiteSpace(localData) ? AppContext.BaseDirectory : localData;
        var folder = Path.Combine(baseFolder, "CNS.StorageCluster", "logs");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"client-{DateTime.Now:yyyy-MM-dd}.log");
        await _logLock.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}", ct);
        }
        finally
        {
            _logLock.Release();
        }
    }

    private TransportCipher Cipher => _transportCipher ??
        throw new InvalidOperationException("El cifrado de transporte no estÃ¡ configurado.");

    private void SetConnected(bool value)
    {
        if (_connected == value) return;
        _connected = value;
        ConnectionChanged?.Invoke(value);
    }
}
