using System.Text.Json;

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
    int ReportIntervalSeconds);

public sealed record MetricsMessage(
    string Type,
    string NodeCode,
    DateTime TimestampUtc,
    string DiskName,
    string DiskType,
    double TotalGb,
    double UsedGb,
    double FreeGb,
    double UtilizationPercent,
    double Iops,
    bool IopsSimulated,
    double LatencyMs);

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
