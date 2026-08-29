using CNS.StorageCluster.Server.Data;
using CNS.StorageCluster.Server.Models;
using CNS.StorageCluster.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CNS.StorageCluster.Server.Services;

public sealed class ClusterQueryService(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<TcpServerOptions> options)
{
    private readonly TcpServerOptions _options = options.Value;

    public async Task<DashboardSnapshot> GetDashboardAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var nodes = await db.Nodes.AsNoTracking().ToListAsync(ct);
        var latest = await GetLatestMetricsAsync(db, ct);
        var now = DateTime.UtcNow;

        var cards = new List<NodeCard>(RegionCatalog.All.Count);
        foreach (var region in RegionCatalog.All)
        {
            var node = nodes.FirstOrDefault(x => x.Code == region.Code);
            latest.TryGetValue(region.Code, out var report);
            var status = NodeObservationPolicy.GetEffectiveStatus(node, _options, now);
            var currentReport = status == NodeStates.Online ? report : null;
            var availability = node is null
                ? 0
                : await GetAvailabilityPercentAsync(db, node, now, ct);

            cards.Add(new NodeCard(
                region.Code,
                region.Name,
                status,
                node?.OperatingSystem,
                node?.LastSeenUtc,
                currentReport?.DiskCount ?? 0,
                currentReport?.DiskSummary ?? "-",
                currentReport?.DiskTypeSummary ?? "-",
                currentReport?.TotalGb ?? 0,
                currentReport?.UsedGb ?? 0,
                currentReport?.FreeGb ?? 0,
                currentReport?.UtilizationPercent ?? 0,
                currentReport?.Iops ?? 0,
                currentReport?.IopsSimulated ?? true,
                currentReport?.LatencyMs ?? 0,
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
            cards.Sum(x => x.DiskCount),
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
                Code: region.Code,
                Name: region.Name,
                Status: NodeStates.NoReporta,
                MachineName: null,
                OperatingSystem: null,
                FirstSeenUtc: null,
                LastSeenUtc: null,
                ReportIntervalSeconds: NetworkDefaults.DefaultReportIntervalSeconds,
                Latest: null,
                GrowthGbPerDay: 0,
                GrowthGbPerMonth: 0,
                AvailabilityPercent: 0,
                UptimeSeconds: 0,
                FailoverEvents: 0,
                AverageLatencyMs: 0,
                DiskCount: 0,
                TotalGb: 0,
                UsedGb: 0,
                FreeGb: 0,
                UtilizationPercent: 0,
                DiskName: "-",
                DiskType: "-",
                Iops: 0,
                IopsSimulated: true,
                LatestLatencyMs: 0,
                CurrentDisks: [],
                History: [],
                Events: [],
                Commands: []);
        }

        var now = DateTime.UtcNow;

        // One database row is stored for each disk in a report cycle.
        var metricRows = await db.Metrics.AsNoTracking()
            .Where(x => x.NodeId == node.Id)
            .OrderByDescending(x => x.TimestampUtc)
            .Take(1000)
            .ToListAsync(ct);
        var history = metricRows
            .GroupBy(x => x.TimestampUtc)
            .OrderBy(x => x.Key)
            .Select(x => CreateSnapshot(x.OrderBy(d => d.DiskName).ToList()))
            .ToList();

        var events = await db.NodeEvents.AsNoTracking()
            .Where(x => x.NodeId == node.Id)
            .OrderBy(x => x.TimestampUtc)
            .ToListAsync(ct);

        var commands = await db.Commands.AsNoTracking()
            .Where(x => x.NodeId == node.Id)
            .OrderByDescending(x => x.SentAtUtc)
            .Take(30)
            .ToListAsync(ct);

        var latest = history.LastOrDefault();
        var status = NodeObservationPolicy.GetEffectiveStatus(node, _options, now);
        var current = status == NodeStates.Online ? latest : null;
        var growthPerDay = CalculateGrowthPerDay(history);
        var availability = CalculateAvailability(node, events, now);
        var failovers = events.Count(x => x.EventType == NodeStates.NoReporta);

        return new NodeDetail(
            Code: node.Code,
            Name: node.RegionName,
            Status: status,
            MachineName: node.MachineName,
            OperatingSystem: node.OperatingSystem,
            FirstSeenUtc: node.FirstSeenUtc,
            LastSeenUtc: node.LastSeenUtc,
            ReportIntervalSeconds: node.ReportIntervalSeconds,
            Latest: current,
            GrowthGbPerDay: growthPerDay,
            GrowthGbPerMonth: growthPerDay * 30,
            AvailabilityPercent: availability.Percentage,
            UptimeSeconds: availability.OnlineSeconds,
            FailoverEvents: failovers,
            AverageLatencyMs: history.Count == 0 ? 0 : history.Average(x => x.LatencyMs),
            DiskCount: current?.DiskCount ?? 0,
            TotalGb: current?.TotalGb ?? 0,
            UsedGb: current?.UsedGb ?? 0,
            FreeGb: current?.FreeGb ?? 0,
            UtilizationPercent: current?.UtilizationPercent ?? 0,
            DiskName: current?.DiskSummary ?? "-",
            DiskType: current?.DiskTypeSummary ?? "-",
            Iops: current?.Iops ?? 0,
            IopsSimulated: current?.IopsSimulated ?? true,
            LatestLatencyMs: current?.LatencyMs ?? 0,
            CurrentDisks: current?.Disks ?? [],
            History: history,
            Events: events,
            Commands: commands);
    }

    private static async Task<Dictionary<string, MetricSnapshot>> GetLatestMetricsAsync(AppDbContext db, CancellationToken ct)
    {
        var result = new Dictionary<string, MetricSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var region in RegionCatalog.All)
        {
            var latestTimestamp = await db.Metrics.AsNoTracking()
                .Where(x => x.Node!.Code == region.Code)
                .OrderByDescending(x => x.TimestampUtc)
                .Select(x => (DateTime?)x.TimestampUtc)
                .FirstOrDefaultAsync(ct);
            if (latestTimestamp is null) continue;

            var disks = await db.Metrics.AsNoTracking()
                .Where(x => x.Node!.Code == region.Code && x.TimestampUtc == latestTimestamp.Value)
                .OrderBy(x => x.DiskName)
                .ToListAsync(ct);
            if (disks.Count > 0) result[region.Code] = CreateSnapshot(disks);
        }
        return result;
    }

    private static MetricSnapshot CreateSnapshot(IReadOnlyList<MetricRecord> disks)
    {
        var total = disks.Sum(x => x.TotalGb);
        var used = disks.Sum(x => x.UsedGb);
        var free = disks.Sum(x => x.FreeGb);
        return new MetricSnapshot(
            disks[0].TimestampUtc,
            disks,
            total,
            used,
            free,
            total <= 0 ? 0 : used / total * 100,
            disks.Sum(x => x.Iops),
            disks.All(x => x.IopsSimulated),
            disks.Average(x => x.LatencyMs));
    }

    private static double CalculateGrowthPerDay(IReadOnlyList<MetricSnapshot> reports)
    {
        if (reports.Count < 2) return 0;
        var first = reports[0];
        var last = reports[^1];
        var days = (last.TimestampUtc - first.TimestampUtc).TotalDays;
        if (days <= 0.00001) return 0;
        return (last.UsedGb - first.UsedGb) / days;
    }

    private AvailabilityStats CalculateAvailability(StorageNode node, IReadOnlyList<NodeEvent> events, DateTime nowUtc)
    {
        if (node.FirstSeenUtc is null) return new AvailabilityStats(0, 0);
        var start = node.FirstSeenUtc.Value;
        var end = nowUtc;
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
        {
            var effectiveEnd = end;
            if (node.LastSeenUtc is not null)
                effectiveEnd = DateTime.Compare(node.LastSeenUtc.Value.AddSeconds(NodeObservationPolicy.GetTimeoutSeconds(node, _options)), end) < 0
                    ? node.LastSeenUtc.Value.AddSeconds(NodeObservationPolicy.GetTimeoutSeconds(node, _options))
                    : end;
            onlineSeconds += Math.Max(0, (effectiveEnd - onlineStart.Value).TotalSeconds);
        }

        var percentage = Math.Clamp(onlineSeconds / totalSeconds * 100, 0, 100);
        return new AvailabilityStats(percentage, onlineSeconds);
    }

    private async Task<double> GetAvailabilityPercentAsync(AppDbContext db, StorageNode node, DateTime nowUtc, CancellationToken ct)
    {
        var events = await db.NodeEvents.AsNoTracking()
            .Where(x => x.NodeId == node.Id)
            .OrderBy(x => x.TimestampUtc)
            .ToListAsync(ct);
        return CalculateAvailability(node, events, nowUtc).Percentage;
    }
}

public sealed record AvailabilityStats(double Percentage, double OnlineSeconds);

public sealed record MetricSnapshot(
    DateTime TimestampUtc,
    IReadOnlyList<MetricRecord> Disks,
    double TotalGb,
    double UsedGb,
    double FreeGb,
    double UtilizationPercent,
    double Iops,
    bool IopsSimulated,
    double LatencyMs)
{
    public int DiskCount => Disks.Count;
    public string DiskSummary => string.Join(", ", Disks.Select(x => x.DiskName));
    public string DiskTypeSummary => string.Join(", ", Disks.Select(x => x.DiskType).Distinct(StringComparer.OrdinalIgnoreCase));
}

public sealed record NodeCard(
    string Code,
    string Name,
    string Status,
    string? OperatingSystem,
    DateTime? LastSeenUtc,
    int DiskCount,
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
    int DiskCount,
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
    MetricSnapshot? Latest,
    double GrowthGbPerDay,
    double GrowthGbPerMonth,
    double AvailabilityPercent,
    double UptimeSeconds,
    int FailoverEvents,
    double AverageLatencyMs,
    int DiskCount,
    double TotalGb,
    double UsedGb,
    double FreeGb,
    double UtilizationPercent,
    string DiskName,
    string DiskType,
    double Iops,
    bool IopsSimulated,
    double LatestLatencyMs,
    IReadOnlyList<MetricRecord> CurrentDisks,
    IReadOnlyList<MetricSnapshot> History,
    IReadOnlyList<NodeEvent> Events,
    IReadOnlyList<CommandRecord> Commands);
