using CNS.StorageCluster.Shared;

namespace CNS.StorageCluster.Client.Services;

public interface IStorageClientService
{
    event Action<string>? Log;
    event Action<bool>? ConnectionChanged;
    event Action<MetricsMessage>? MetricsProduced;
    event Action<int>? IntervalChanged;

    Task StartAsync();
    Task StopAsync();
    Task SetLocalIntervalAsync(int seconds);
}
