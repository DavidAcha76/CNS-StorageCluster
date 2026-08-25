namespace CNS.StorageCluster.Server.Services;

public sealed class TcpServerOptions
{
    public int Port { get; set; } = 5050;
    public int NodeTimeoutSeconds { get; set; } = 30;
}
