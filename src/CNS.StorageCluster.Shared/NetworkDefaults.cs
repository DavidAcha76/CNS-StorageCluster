namespace CNS.StorageCluster.Shared;

public static class NetworkDefaults
{
    // Dirección solicitada para el nodo central.
    public const string ServerHost = "distribuidos.hermesoft.com";
    public const int TcpPort = 5050;
    public const int WebSocketPort = 443;
    public const string WebSocketPath = "/ws/cluster";
    public const int DefaultReportIntervalSeconds = 10;
    public const int MinimumReportIntervalSeconds = 2;
    public const int MaximumReportIntervalSeconds = 3600;
}
