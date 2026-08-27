using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CNS.StorageCluster.Shared;

namespace CNS.StorageCluster.Client.Services;

public sealed class StorageClientService(string nodeCode, string host, int port, int initialIntervalSeconds) : IStorageClientService
{
    private readonly DiskMetricsProvider _metricsProvider = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _logLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private StreamWriter? _writer;
    private int _reportIntervalSeconds = Math.Clamp(initialIntervalSeconds, NetworkDefaults.MinimumReportIntervalSeconds, NetworkDefaults.MaximumReportIntervalSeconds);
    private volatile bool _connected;

    public event Action<string>? Log;
    public event Action<bool>? ConnectionChanged;
    public event Action<MetricsMessage>? MetricsProduced;
    public event Action<int>? IntervalChanged;

    public Task StartAsync()
    {
        if (_runTask is not null && !_runTask.IsCompleted) return Task.CompletedTask;
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
        {
            await SendAsync(new ClientConfigMessage(MessageTypes.ClientConfig, nodeCode, seconds, DateTime.UtcNow), CancellationToken.None);
        }
    }

    private async Task RunReconnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log?.Invoke($"Conexión perdida: {ex.Message}");
            }
            finally
            {
                _writer = null;
                SetConnected(false);
            }

            if (!ct.IsCancellationRequested)
            {
                Log?.Invoke("Reintentando conexión en 5 segundos...");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch { }
            }
        }
    }

    private async Task RunConnectionAsync(CancellationToken ct)
    {
        using var tcp = new TcpClient { NoDelay = true };
        Log?.Invoke($"Conectando a {host}:{port}...");
        await tcp.ConnectAsync(host, port, ct);
        using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        _writer = writer;

        var registration = new RegisterMessage(
            MessageTypes.Register,
            nodeCode,
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            "1.0.0",
            Volatile.Read(ref _reportIntervalSeconds));
        await SendAsync(registration, ct);
        SetConnected(true);
        Log?.Invoke("Conectado y registrado automáticamente en el nodo central.");

        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receive = ReceiveLoopAsync(reader, connectionCts.Token);
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
            Log?.Invoke($"Métricas enviadas: {metrics.DiskCount} disco(s) leídos.");
            await Task.Delay(TimeSpan.FromSeconds(Volatile.Read(ref _reportIntervalSeconds)), ct);
        }
    }

    private async Task ReceiveLoopAsync(StreamReader reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) throw new IOException("El servidor cerró la conexión.");
            if (string.IsNullOrWhiteSpace(line)) continue;
            var type = ProtocolJson.GetMessageType(line);

            switch (type)
            {
                case MessageTypes.Command:
                    var command = JsonSerializer.Deserialize<CommandMessage>(line, ProtocolJson.Options);
                    if (command is not null)
                    {
                        var text = $"MENSAJE DEL SERVIDOR: {command.Message}";
                        Log?.Invoke(text);
                        await WriteServerLogAsync(text, ct);
                        await SendAsync(new AckMessage(MessageTypes.Ack, command.CommandId, nodeCode, "Mensaje recibido y guardado en .log", DateTime.UtcNow), ct);
                        Log?.Invoke("ACK enviado al servidor.");
                    }
                    break;

                case MessageTypes.ConfigInterval:
                    var config = JsonSerializer.Deserialize<ConfigIntervalMessage>(line, ProtocolJson.Options);
                    if (config is not null)
                    {
                        var seconds = Math.Clamp(config.ReportIntervalSeconds, NetworkDefaults.MinimumReportIntervalSeconds, NetworkDefaults.MaximumReportIntervalSeconds);
                        Interlocked.Exchange(ref _reportIntervalSeconds, seconds);
                        IntervalChanged?.Invoke(seconds);
                        var text = $"CONFIGURACIÓN DEL SERVIDOR: intervalo cambiado a {seconds} s.";
                        Log?.Invoke(text);
                        await WriteServerLogAsync(text, ct);
                        await SendAsync(new AckMessage(MessageTypes.Ack, config.CommandId, nodeCode, $"Intervalo aplicado: {seconds}s", DateTime.UtcNow), ct);
                        Log?.Invoke("ACK de configuración enviado.");
                    }
                    break;

                case MessageTypes.Error:
                    var error = JsonSerializer.Deserialize<ErrorMessage>(line, ProtocolJson.Options);
                    throw new InvalidOperationException(error?.Message ?? "Error reportado por el servidor.");
            }
        }
    }

    private async Task SendAsync<T>(T message, CancellationToken ct)
    {
        var writer = _writer ?? throw new InvalidOperationException("Socket no conectado.");
        await _sendLock.WaitAsync(ct);
        try
        {
            await writer.WriteLineAsync(ProtocolJson.Serialize(message).AsMemory(), ct);
            await writer.FlushAsync(ct);
        }
        finally
        {
            _sendLock.Release();
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
        finally { _logLock.Release(); }
    }

    private void SetConnected(bool value)
    {
        if (_connected == value) return;
        _connected = value;
        ConnectionChanged?.Invoke(value);
    }
}
