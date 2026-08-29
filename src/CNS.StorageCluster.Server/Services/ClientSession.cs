using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace CNS.StorageCluster.Server.Services;

public sealed class ClientSession
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TcpClient? _client;
    private readonly StreamWriter? _writer;
    private readonly WebSocket? _webSocket;
    private long _lastMetricsReceivedTicks = DateTime.UtcNow.Ticks;

    public string NodeCode { get; }
    public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;
    public DateTime LastMetricsReceivedUtc => new(Interlocked.Read(ref _lastMetricsReceivedTicks), DateTimeKind.Utc);

    public ClientSession(string nodeCode, TcpClient client, StreamWriter writer)
    {
        NodeCode = nodeCode;
        _client = client;
        _writer = writer;
    }

    public ClientSession(string nodeCode, WebSocket webSocket)
    {
        NodeCode = nodeCode;
        _webSocket = webSocket;
    }

    public async Task SendLineAsync(string json, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (_webSocket is not null)
            {
                var payload = Encoding.UTF8.GetBytes(json);
                await _webSocket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, cancellationToken);
                return;
            }
            var writer = _writer ?? throw new InvalidOperationException("Sesion TCP no disponible.");
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void MarkMetricsReceived() =>
        Interlocked.Exchange(ref _lastMetricsReceivedTicks, DateTime.UtcNow.Ticks);

    public void Close()
    {
        if (_webSocket is not null)
        {
            try { _webSocket.Abort(); } catch { }
            return;
        }
        try { _client?.Close(); } catch { }
    }
}
