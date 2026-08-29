using CNS.StorageCluster.Server.Models;
using CNS.StorageCluster.Shared;

namespace CNS.StorageCluster.Server.Services;

/// <summary>
/// Define cuándo una métrica todavía representa el estado actual de un equipo.
/// </summary>
public static class NodeObservationPolicy
{
    public static int GetTimeoutSeconds(StorageNode node, TcpServerOptions options) =>
        Math.Max(
            Math.Max(5, options.NodeTimeoutSeconds),
            Math.Clamp(node.ReportIntervalSeconds, NetworkDefaults.MinimumReportIntervalSeconds, NetworkDefaults.MaximumReportIntervalSeconds) * 3);

    public static bool IsReporting(StorageNode? node, TcpServerOptions options, DateTime nowUtc)
    {
        if (node?.Status != NodeStates.Online || node.LastSeenUtc is null) return false;
        return nowUtc <= node.LastSeenUtc.Value.AddSeconds(GetTimeoutSeconds(node, options));
    }

    public static string GetEffectiveStatus(StorageNode? node, TcpServerOptions options, DateTime nowUtc) =>
        IsReporting(node, options, nowUtc) ? NodeStates.Online : NodeStates.NoReporta;
}
