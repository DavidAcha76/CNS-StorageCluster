using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using CNS.StorageCluster.Shared;

namespace CNS.StorageCluster.Client.Services;

public sealed class DiskMetricsProvider
{
    private readonly Random _random = new();
    private string? _cachedDiskType;

    //Calcula métricas de disco y latencia de red para un nodo específico.
    public async Task<MetricsMessage> ReadAsync(string nodeCode, string serverHost, CancellationToken ct)
    {
        var drives = DriveInfo.GetDrives()
            .Where(IsMonitorableDrive)
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _cachedDiskType ??= await DetectDiskTypeAsync(ct);
        var latency = await TryPingAsync(serverHost, ct);
        var disks = new List<DiskMetrics>(drives.Count);
        foreach (var drive in drives)
        {
            try
            {
                var total = BytesToGb(drive.TotalSize);
                var free = BytesToGb(drive.AvailableFreeSpace);
                var used = Math.Max(0, total - free);
                var utilization = total <= 0 ? 0 : used / total * 100;
                // La práctica permite simular IOPS si la plataforma no lo soporta de forma portable.
                var iops = Math.Round(120 + utilization * 4 + _random.NextDouble() * 180, 1);
                disks.Add(new DiskMetrics(
                    drive.Name,
                    _cachedDiskType,
                    Math.Round(total, 2),
                    Math.Round(used, 2),
                    Math.Round(free, 2),
                    Math.Round(utilization, 2),
                    iops,
                    true,
                    latency));
            }
            catch (IOException)
            {
                // La unidad dejó de estar disponible entre la detección y la lectura.
            }
            catch (UnauthorizedAccessException)
            {
                // Se omiten volúmenes sin permisos, sin cancelar el resto del reporte.
            }
        }
        return new MetricsMessage(
            MessageTypes.Metrics,
            nodeCode,
            DateTime.UtcNow,
            disks);
    }

    private static bool IsMonitorableDrive(DriveInfo drive)
    {
        try
        {
            return drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static double BytesToGb(long bytes) => bytes / 1024d / 1024d / 1024d;

    private static async Task<double> TryPingAsync(string host, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 1200).WaitAsync(ct);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : 0;
        }
        catch { return 0; }
    }

    private static async Task<string> DetectDiskTypeAsync(CancellationToken ct)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var output = await RunProcessAsync("sh", "-c \"lsblk -dn -o ROTA | head -n 1\"", ct);
                return output.Trim() switch { "0" => "SSD", "1" => "HDD", _ => "UNKNOWN" };
            }
            if (OperatingSystem.IsWindows())
            {
                var output = await RunProcessAsync("powershell", "-NoProfile -Command \"(Get-PhysicalDisk | Select-Object -First 1 -ExpandProperty MediaType)\"", ct);
                if (output.Contains("SSD", StringComparison.OrdinalIgnoreCase)) return "SSD";
                if (output.Contains("HDD", StringComparison.OrdinalIgnoreCase)) return "HDD";
            }
        }
        catch { }
        return "UNKNOWN";
    }

    private static async Task<string> RunProcessAsync(string file, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo iniciar proceso.");
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return output;
    }
}
