namespace CNS.StorageCluster.Server.Models;

public sealed class StorageNode
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string RegionName { get; set; } = string.Empty;
    public string Status { get; set; } = "NO_REPORTA";
    public string? MachineName { get; set; }
    public string? OperatingSystem { get; set; }
    public string? ClientVersion { get; set; }
    public DateTime? FirstSeenUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public int ReportIntervalSeconds { get; set; } = 10;

    public List<MetricRecord> Metrics { get; set; } = [];
    public List<NodeEvent> Events { get; set; } = [];
    public List<CommandRecord> Commands { get; set; } = [];
}

public sealed class MetricRecord
{
    public long Id { get; set; }
    public int NodeId { get; set; }
    public StorageNode? Node { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string DiskName { get; set; } = string.Empty;
    public string DiskType { get; set; } = "UNKNOWN";
    public double TotalGb { get; set; }
    public double UsedGb { get; set; }
    public double FreeGb { get; set; }
    public double UtilizationPercent { get; set; }
    public double Iops { get; set; }
    public bool IopsSimulated { get; set; }
    public double LatencyMs { get; set; }
}

public sealed class NodeEvent
{
    public long Id { get; set; }
    public int NodeId { get; set; }
    public StorageNode? Node { get; set; }
    public string EventType { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }
    public string? Detail { get; set; }
}

public sealed class CommandRecord
{
    public long Id { get; set; }
    public int NodeId { get; set; }
    public StorageNode? Node { get; set; }
    public string CommandId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string Status { get; set; } = "PENDING";
    public DateTime SentAtUtc { get; set; }
    public DateTime? AckAtUtc { get; set; }
    public string? AckDetail { get; set; }
}
