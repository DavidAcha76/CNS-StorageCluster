using CNS.StorageCluster.Server.Data;
using CNS.StorageCluster.Server.Models;
using CNS.StorageCluster.Shared;
using Microsoft.EntityFrameworkCore;

namespace CNS.StorageCluster.Server.Services;

public sealed class ClusterQueryService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<DashboardSnapshot> GetDashboardAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var nodes = await db.Nodes.AsNoTracking().ToListAsync(ct);
        var latest = await GetLatestMetricsAsync(db, ct);

        var cards = new List<NodeCard>(RegionCatalog.All.Count);
        foreach (var region in RegionCatalog.All)
        {
            var node = nodes.FirstOrDefault(x => x.Code == region.Code);
            latest.TryGetValue(region.Code, out var metric);
            var availability = node is null
                ? 0
                : await GetAvailabilityPercentAsync(db, node, ct);

            cards.Add(new NodeCard(
                region.Code,
                region.Name,
                node?.Status ?? NodeStates.NoReporta,
                node?.LastSeenUtc,
                metric?.DiskName ?? "-",
                metric?.DiskType ?? "-",
                metric?.TotalGb ?? 0,
                metric?.UsedGb ?? 0,
                metric?.FreeGb ?? 0,
                metric?.UtilizationPercent ?? 0,
                metric?.Iops ?? 0,
                metric?.IopsSimulated ?? true,
                metric?.LatencyMs ?? 0,
                node?.ReportIntervalSeconds ?? NetworkDefaults.DefaultReportIntervalSeconds,
                availability));
        }

        var total = cards.Sum(x => x.TotalGb);
        var used = cards.Sum(x => x.UsedGb);
        var free = cards.Sum(x => x.FreeGb);
        var utilization = total <= 0 ? 0 : used / total * 100;
        var active = cards.Count(x => x.Status == NodeStates.Online);
        var activeCards = cards.Where(x => x.Status == NodeStates.Online && x.TotalGb > 0).ToList();
        var activeCapacity = activeCards.Sum(x => x.TotalGb);
        var weightedLatency = activeCapacity <= 0
            ? 0
            : activeCards.Sum(x => x.LatencyMs * x.TotalGb) / activeCapacity;
        var clusterAvailability = cards.Where(x => x.LastSeenUtc is not null).Select(x => x.AvailabilityPercent).DefaultIfEmpty(0).Average();

        return new DashboardSnapshot(
            cards,
            total,
            used,
            free,
            utilization,
            active,
            RegionCatalog.All.Count,
            weightedLatency,
            clusterAvailability);
    }

    public async Task<NodeDetail?> GetNodeDetailAsync(string code, CancellationToken ct = default)
    {
        if (!RegionCatalog.TryGet(code, out var region)) return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(x => x.Code == region.Code, ct);
        if (node is null)
        {
            return new NodeDetail(
                region.Code, region.Name, NodeStates.NoReporta,
                null, null, null, null, NetworkDefaults.DefaultReportIntervalSeconds,
                null, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, "-", "-", 0, true, 0,
                [], [], []);
        }

        var metrics = await db.Metrics.AsNoTracking()
            .Where(x => x.NodeId == node.Id)
            .OrderByDescending(x => x.TimestampUtc)
            .Take(250)
            .OrderBy(x => x.TimestampUtc)
            .ToListAsync(ct);

        var events = await db.NodeEvents.AsNoTracking()
            .Where(x => x.NodeId == node.Id)
            .OrderBy(x => x.TimestampUtc)
            .ToListAsync(ct);

        var commands = await db.Commands.AsNoTracking()
            .Where(x => x.NodeId == node.Id)
            .OrderByDescending(x => x.SentAtUtc)
            .Take(30)
            .ToListAsync(ct);

        var latest = metrics.LastOrDefault();
        var growthPerDay = CalculateGrowthPerDay(metrics);
        var availability = CalculateAvailability(node, events);
        var failovers = events.Count(x => x.EventType == NodeStates.NoReporta);

        return new NodeDetail(
            node.Code,
            node.RegionName,
            node.Status,
            node.MachineName,
            node.OperatingSystem,
            node.FirstSeenUtc,
            node.LastSeenUtc,
            node.ReportIntervalSeconds,
            latest,
            growthPerDay,
            growthPerDay * 30,
            availability.Percentage,
            availability.OnlineSeconds,
            failovers,
            metrics.Count == 0 ? 0 : metrics.Average(x => x.LatencyMs),
            latest?.TotalGb ?? 0,
            latest?.UsedGb ?? 0,
            latest?.FreeGb ?? 0,
            latest?.UtilizationPercent ?? 0,
            latest?.DiskName ?? "-",
            latest?.DiskType ?? "-",
            latest?.Iops ?? 0,
            latest?.IopsSimulated ?? true,
            latest?.LatencyMs ?? 0,
            metrics,
            events,
            commands);
    }

    private static async Task<Dictionary<string, MetricRecord>> GetLatestMetricsAsync(AppDbContext db, CancellationToken ct)
    {
        var result = new Dictionary<string, MetricRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var region in RegionCatalog.All)
        {
            var metric = await db.Metrics.AsNoTracking()
                .Where(x => x.Node!.Code == region.Code)
                .OrderByDescending(x => x.TimestampUtc)
                .FirstOrDefaultAsync(ct);
            if (metric is not null) result[region.Code] = metric;
        }
        return result;
    }

    private static double CalculateGrowthPerDay(IReadOnlyList<MetricRecord> metrics)
    {
        if (metrics.Count < 2) return 0;
        var first = metrics[0];
        var last = metrics[^1];
        var days = (last.TimestampUtc - first.TimestampUtc).TotalDays;
        if (days <= 0.00001) return 0;
        return (last.UsedGb - first.UsedGb) / days;
    }

    private static AvailabilityStats CalculateAvailability(StorageNode node, IReadOnlyList<NodeEvent> events)
    {
        if (node.FirstSeenUtc is null) return new AvailabilityStats(0, 0);
        var start = node.FirstSeenUtc.Value;
        var end = DateTime.UtcNow;
        var totalSeconds = Math.Max(1, (end - start).TotalSeconds);
        double onlineSeconds = 0;
        DateTime? onlineStart = null;

        foreach (var ev in events.Where(x => x.TimestampUtc >= start).OrderBy(x => x.TimestampUtc))
        {
            if (ev.EventType == NodeStates.Online && onlineStart is null)
            {
                onlineStart = ev.TimestampUtc;
            }
            else if (ev.EventType == NodeStates.NoReporta && onlineStart is not null)
            {
                onlineSeconds += Math.Max(0, (ev.TimestampUtc - onlineStart.Value).TotalSeconds);
                onlineStart = null;
            }
        }

        if (onlineStart is not null)
            onlineSeconds += Math.Max(0, (end - onlineStart.Value).TotalSeconds);

        var percentage = Math.Clamp(onlineSeconds / totalSeconds * 100, 0, 100);
        return new AvailabilityStats(percentage, onlineSeconds);
    }

    private static async Task<double> GetAvailabilityPercentAsync(AppDbContext db, StorageNode node, CancellationToken ct)
    {
        var events = await db.NodeEvents.AsNoTracking()
            .Where(x => x.NodeId == node.Id)
            .OrderBy(x => x.TimestampUtc)
            .ToListAsync(ct);
        return CalculateAvailability(node, events).Percentage;
    }
}

public sealed record AvailabilityStats(double Percentage, double OnlineSeconds);

public sealed record NodeCard(
    string Code,
    string Name,
    string Status,
    DateTime? LastSeenUtc,
    string DiskName,
    string DiskType,
    double TotalGb,
    double UsedGb,
    double FreeGb,
    double UtilizationPercent,
    double Iops,
    bool IopsSimulated,
    double LatencyMs,
    int ReportIntervalSeconds,
    double AvailabilityPercent);

public sealed record DashboardSnapshot(
    IReadOnlyList<NodeCard> Nodes,
    double TotalGb,
    double UsedGb,
    double FreeGb,
    double UtilizationPercent,
    int ActiveNodes,
    int TotalNodes,
    double WeightedLatencyMs,
    double AvailabilityPercent);

public sealed record NodeDetail(
    string Code,
    string Name,
    string Status,
    string? MachineName,
    string? OperatingSystem,
    DateTime? FirstSeenUtc,
    DateTime? LastSeenUtc,
    int ReportIntervalSeconds,
    MetricRecord? Latest,
    double GrowthGbPerDay,
    double GrowthGbPerMonth,
    double AvailabilityPercent,
    double UptimeSeconds,
    int FailoverEvents,
    double AverageLatencyMs,
    double TotalGb,
    double UsedGb,
    double FreeGb,
    double UtilizationPercent,
    string DiskName,
    string DiskType,
    double Iops,
    bool IopsSimulated,
    double LatestLatencyMs,
    IReadOnlyList<MetricRecord> History,
    IReadOnlyList<NodeEvent> Events,
    IReadOnlyList<CommandRecord> Commands);
