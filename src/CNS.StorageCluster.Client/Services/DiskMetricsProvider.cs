using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using CNS.StorageCluster.Shared;

namespace CNS.StorageCluster.Client.Services;

public sealed class DiskMetricsProvider
{
    private readonly Random _random = new();
    private string? _cachedDiskType;

    public static (string MacAddress, string IpAddress) GetNetworkIdentity()
    {
        var mac = "00:00:00:00:00:00";
        var ip = "127.0.0.1";

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                              nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                              nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .OrderByDescending(nic => nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet)
                .ThenByDescending(nic => nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .ToList();

            foreach (var nic in interfaces)
            {
                var bytes = nic.GetPhysicalAddress().GetAddressBytes();
                if (bytes.Length == 6 && mac == "00:00:00:00:00:00")
                {
                    mac = string.Join(":", bytes.Select(b => b.ToString("X2")));
                }

                var ipProps = nic.GetIPProperties();
                var unicast = ipProps.UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork &&
                                         !IPAddress.IsLoopback(u.Address));
                if (unicast is not null && ip == "127.0.0.1")
                {
                    ip = unicast.Address.ToString();
                }

                if (mac != "00:00:00:00:00:00" && ip != "127.0.0.1")
                    break;
            }

            if (ip == "127.0.0.1")
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                var ipv4 = host.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
                if (ipv4 is not null) ip = ipv4.ToString();
            }
        }
        catch
        {
            // fallback
        }

        return (mac, ip);
    }

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
        var (macAddress, ipAddress) = GetNetworkIdentity();
        var localTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return new MetricsMessage(
            MessageTypes.Metrics,
            nodeCode,
            DateTime.UtcNow,
            disks,
            macAddress,
            ipAddress,
            localTime);
    }

    public async Task<string> GenerateReportFileAsync(string nodeCode, string serverHost, CancellationToken ct)
    {
        var metrics = await ReadAsync(nodeCode, serverHost, ct);
        var regionName = RegionCatalog.TryGet(nodeCode, out var reg) ? reg.Name : nodeCode;
        var nowLocal = DateTime.Now;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=================================================================");
        sb.AppendLine("           REPORTE DE ESTADO DE DISCOS Y SUCURSAL                ");
        sb.AppendLine("                    CNS STORAGE CLUSTER                          ");
        sb.AppendLine("=================================================================");
        sb.AppendLine($"Sucursal / Regional  : {regionName} ({nodeCode})");
        sb.AppendLine($"Estado del Nodo      : ACTIVO (EN LÍNEA)");
        sb.AppendLine($"Nombre del Equipo    : {Environment.MachineName}");
        sb.AppendLine($"Sistema Operativo    : {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Versión de Cliente   : 1.0.0");
        sb.AppendLine($"Fecha/Hora Emisión   : {nowLocal:dd/MM/yyyy HH:mm:ss}");
        sb.AppendLine("=================================================================");
        sb.AppendLine();
        sb.AppendLine("DETALLE DE DISCOS REPORTADOS:");
        sb.AppendLine("-----------------------------------------------------------------");

        if (metrics.Disks.Count == 0)
        {
            sb.AppendLine("  (No hay información de discos disponible)");
        }
        else
        {
            foreach (var disk in metrics.Disks)
            {
                var stateStr = disk.UtilizationPercent >= 85 ? "ALERTA (>85% USO)" : "OPERATIVO";
                sb.AppendLine($"* Disco              : {disk.DiskName}");
                sb.AppendLine($"  - Tipo de Disco   : {disk.DiskType}");
                sb.AppendLine($"  - Estado          : {stateStr}");
                sb.AppendLine($"  - Capacidad Total : {disk.TotalGb:N2} GB");
                sb.AppendLine($"  - Espacio Usado   : {disk.UsedGb:N2} GB");
                sb.AppendLine($"  - Espacio Libre   : {disk.FreeGb:N2} GB");
                sb.AppendLine($"  - Porcentaje Uso  : {disk.UtilizationPercent:N2}%");
                sb.AppendLine($"  - IOPS            : {disk.Iops:N0}{(disk.IopsSimulated ? " (simulado)" : "")}");
                sb.AppendLine($"  - Latencia Red    : {disk.LatencyMs:N1} ms");
                sb.AppendLine("-----------------------------------------------------------------");
            }
        }

        sb.AppendLine();
        sb.AppendLine("RESUMEN CONSOLIDADO DE ALMACENAMIENTO:");
        sb.AppendLine($"  - Cantidad Discos : {metrics.DiskCount}");
        sb.AppendLine($"  - Capacidad Total : {metrics.TotalGb:N2} GB");
        sb.AppendLine($"  - Espacio Usado   : {metrics.UsedGb:N2} GB");
        sb.AppendLine($"  - Espacio Libre   : {metrics.FreeGb:N2} GB");
        sb.AppendLine($"  - Utilización %   : {metrics.UtilizationPercent:N2}%");
        sb.AppendLine("=================================================================");
        sb.AppendLine($"Reporte generado localmente en el cliente por solicitud del servidor");
        sb.AppendLine("=================================================================");

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var baseFolder = string.IsNullOrWhiteSpace(localData) ? AppContext.BaseDirectory : localData;
        var reportsFolder = Path.Combine(baseFolder, "CNS.StorageCluster", "reports");
        Directory.CreateDirectory(reportsFolder);

        var fileName = $"Reporte_Estado_Discos_{nodeCode}_{nowLocal:yyyyMMdd_HHmmss}.txt";
        var filePath = Path.Combine(reportsFolder, fileName);
        await File.WriteAllTextAsync(filePath, sb.ToString(), System.Text.Encoding.UTF8, ct);

        return filePath;
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
