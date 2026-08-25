using CNS.StorageCluster.Server.Data;
using CNS.StorageCluster.Server.Models;
using CNS.StorageCluster.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CNS.StorageCluster.Server.Services;

public sealed class NodeHealthService(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<TcpServerOptions> options,
    ILogger<NodeHealthService> logger) : BackgroundService
{
    private readonly TcpServerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SafeCheckAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await SafeCheckAsync(stoppingToken);
    }

    private async Task SafeCheckAsync(CancellationToken ct)
    {
        try
        {
            await CheckNodesAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Error comprobando salud de nodos");
        }
    }

    private async Task CheckNodesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var nodes = await db.Nodes
            .Where(x => x.Status == NodeStates.Online && x.LastSeenUtc != null)
            .ToListAsync(ct);

        if (nodes.Count == 0) return;

        var now = DateTime.UtcNow;
        var changed = false;
        foreach (var node in nodes)
        {
            // Evita falsos NO_REPORTA cuando el intervalo de métricas se aumenta desde cliente/servidor.
            // Se toleran tres intervalos de reporte y, como mínimo, el timeout base del servidor.
            var timeoutSeconds = Math.Max(
                Math.Max(5, _options.NodeTimeoutSeconds),
                Math.Clamp(node.ReportIntervalSeconds, NetworkDefaults.MinimumReportIntervalSeconds, NetworkDefaults.MaximumReportIntervalSeconds) * 3);

            var failureAt = node.LastSeenUtc!.Value.AddSeconds(timeoutSeconds);
            if (now <= failureAt) continue;

            node.Status = NodeStates.NoReporta;
            db.NodeEvents.Add(new NodeEvent
            {
                NodeId = node.Id,
                EventType = NodeStates.NoReporta,
                TimestampUtc = failureAt,
                Detail = $"No se recibieron reportes durante {timeoutSeconds} segundos."
            });
            changed = true;
        }

        if (changed) await db.SaveChangesAsync(ct);
    }
}
