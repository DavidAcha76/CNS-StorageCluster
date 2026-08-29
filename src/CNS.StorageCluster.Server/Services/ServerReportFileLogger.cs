using System.Text;

namespace CNS.StorageCluster.Server.Services;

/// <summary>
/// Conserva en disco los reportes operativos de los equipos observados por el servidor.
/// </summary>
public sealed class ServerReportFileLogger(
    IHostEnvironment environment,
    ILogger<ServerReportFileLogger> logger)
{
    private readonly string _logDirectory = Path.Combine(environment.ContentRootPath, "logs");
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task WriteAsync(string level, string message, CancellationToken ct = default)
    {
        try
        {
            var timestamp = DateTimeOffset.UtcNow;
            var path = Path.Combine(_logDirectory, $"server-{timestamp:yyyy-MM-dd}.log");
            var entry = $"[{timestamp:O}] [{level}] {message}{Environment.NewLine}";

            await _writeLock.WaitAsync(ct);
            try
            {
                Directory.CreateDirectory(_logDirectory);
                await File.AppendAllTextAsync(path, entry, new UTF8Encoding(false), ct);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo guardar un reporte de equipo en archivo");
        }
    }
}
