using System.Text.Json;
using System.Text.Json.Serialization;

namespace CNS.StorageCluster.Shared;

public static class MessageTypes
{
    public const string Register = "REGISTER";
    public const string Metrics = "METRICS";
    public const string Command = "COMMAND";
    public const string Ack = "ACK";
    public const string ConfigInterval = "CONFIG_INTERVAL";
    public const string ClientConfig = "CLIENT_CONFIG";
    public const string Error = "ERROR";
}

public sealed record RegisterMessage(
    string Type,
    string NodeCode,
    string MachineName,
    string OperatingSystem,
    string ClientVersion,
    int ReportIntervalSeconds,
    string? MacAddress = null,
    string? IpAddress = null,
    string? LocalTime = null);

public sealed record DiskMetrics(
    string DiskName,
    string DiskType,
    double TotalGb,
    double UsedGb,
    double FreeGb,
    double UtilizationPercent,
    double Iops,
    bool IopsSimulated,
    double LatencyMs);

public sealed record MetricsMessage(
    string Type,
    string NodeCode,
    DateTime TimestampUtc,
    IReadOnlyList<DiskMetrics> Disks,
    string? MacAddress = null,
    string? IpAddress = null,
    string? LocalTime = null)
{
    [JsonIgnore]
    public int DiskCount => Disks.Count;
    [JsonIgnore]
    public double TotalGb => Disks.Sum(x => x.TotalGb);
    [JsonIgnore]
    public double UsedGb => Disks.Sum(x => x.UsedGb);
    [JsonIgnore]
    public double FreeGb => Disks.Sum(x => x.FreeGb);
    [JsonIgnore]
    public double UtilizationPercent => TotalGb <= 0 ? 0 : UsedGb / TotalGb * 100;
    [JsonIgnore]
    public double Iops => Disks.Sum(x => x.Iops);
    [JsonIgnore]
    public bool IopsSimulated => Disks.All(x => x.IopsSimulated);
    [JsonIgnore]
    public double LatencyMs => Disks.Count == 0 ? 0 : Disks.Average(x => x.LatencyMs);
}

public sealed record CommandMessage(
    string Type,
    string CommandId,
    string Message,
    DateTime SentAtUtc);

public sealed record ConfigIntervalMessage(
    string Type,
    string CommandId,
    int ReportIntervalSeconds,
    DateTime SentAtUtc);

public sealed record ClientConfigMessage(
    string Type,
    string NodeCode,
    int ReportIntervalSeconds,
    DateTime SentAtUtc);

public sealed record AckMessage(
    string Type,
    string CommandId,
    string NodeCode,
    string Detail,
    DateTime ReceivedAtUtc);

public sealed record ErrorMessage(string Type, string Message);

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static string? GetMessageType(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("type", out var type)
            ? type.GetString()
            : doc.RootElement.TryGetProperty("Type", out type)
                ? type.GetString()
                : null;
    }
}
