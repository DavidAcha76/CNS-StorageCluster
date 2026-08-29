using System.Text;
using System.Text.Json;
using CNS.StorageCluster.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CNS.StorageCluster.Server.Pages.Nodes;

public sealed class DetailsModel(ClusterQueryService query, TcpServerService tcp) : PageModel
{
    public NodeDetail Node { get; private set; } = null!;
    public string HistoryJson { get; private set; } = "[]";

    public async Task<IActionResult> OnGetAsync(string code, CancellationToken ct)
    {
        var node = await query.GetNodeDetailAsync(code, ct);
        if (node is null) return NotFound();
        Node = node;
        HistoryJson = JsonSerializer.Serialize(node.History.Select(x => new
        {
            t = x.TimestampUtc.ToString("HH:mm:ss"),
            u = x.UtilizationPercent
        }));
        return Page();
    }

    public async Task<IActionResult> OnGetExportTxtAsync(string code, CancellationToken ct)
    {
        var node = await query.GetNodeDetailAsync(code, ct);
        if (node is null) return NotFound();

        var nowLocal = DateTime.Now;
        var sb = new StringBuilder();
        sb.AppendLine("=================================================================");
        sb.AppendLine("           REPORTE DE ESTADO DE DISCOS Y SUCURSAL                ");
        sb.AppendLine("                    CNS STORAGE CLUSTER                          ");
        sb.AppendLine("=================================================================");
        sb.AppendLine($"Sucursal / Regional  : {node.Name} ({node.Code})");
        sb.AppendLine($"Estado del Nodo      : {(node.Status == "ACTIVO" ? "ACTIVO (EN LÍNEA)" : "DESCONECTADO (NO REPORTA)")}");
        sb.AppendLine($"Nombre del Equipo    : {node.MachineName ?? "No registrado"}");
        sb.AppendLine($"Dirección IP         : {node.IpAddress ?? "No registrada"}");
        sb.AppendLine($"Dirección MAC        : {node.MacAddress ?? "No registrada"}");
        sb.AppendLine($"Sistema Operativo    : {node.OperatingSystem ?? "No registrado"}");
        sb.AppendLine($"Versión de Cliente   : {node.ClientVersion ?? "No disponible"}");
        sb.AppendLine($"Último Reporte       : {(node.LastSeenUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") ?? "Sin reportes")}");
        sb.AppendLine($"Fecha/Hora Emisión   : {nowLocal:dd/MM/yyyy HH:mm:ss}");
        sb.AppendLine("=================================================================");
        sb.AppendLine();
        sb.AppendLine("DETALLE DE DISCOS DE LA SUCURSAL:");
        sb.AppendLine("-----------------------------------------------------------------");

        if (node.CurrentDisks.Count == 0)
        {
            sb.AppendLine("  (No hay información de discos disponible para esta sucursal)");
        }
        else
        {
            foreach (var disk in node.CurrentDisks)
            {
                var stateStr = node.Status != "ACTIVO" ? "NO VIGENTE (DESCONECTADO)" : disk.UtilizationPercent >= 85 ? "ALERTA (>85% USO)" : "OPERATIVO";
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
        sb.AppendLine($"  - Cantidad Discos : {node.DiskCount}");
        sb.AppendLine($"  - Capacidad Total : {node.TotalGb:N2} GB");
        sb.AppendLine($"  - Espacio Usado   : {node.UsedGb:N2} GB");
        sb.AppendLine($"  - Espacio Libre   : {node.FreeGb:N2} GB");
        sb.AppendLine($"  - Utilización %   : {node.UtilizationPercent:N2}%");
        sb.AppendLine($"  - Disponibilidad  : {node.AvailabilityPercent:N3}%");
        sb.AppendLine("=================================================================");
        sb.AppendLine($"Reporte generado automáticamente por CNS Storage Cluster Server");
        sb.AppendLine("=================================================================");

        var fileName = $"Reporte_Estado_Discos_{node.Code}_{nowLocal:yyyyMMdd_HHmmss}.txt";
        var fileBytes = Encoding.UTF8.GetBytes(sb.ToString());
        return File(fileBytes, "text/plain; charset=utf-8", fileName);
    }

    public async Task<IActionResult> OnPostSendCommandAsync(string code, string message, CancellationToken ct)
    {
        var result = await tcp.SendCommandAsync(code, message, ct);
        TempData["Flash"] = result.Detail;
        TempData["FlashOk"] = result.Ok;
        return RedirectToPage(new { code });
    }

    public async Task<IActionResult> OnPostSetIntervalAsync(string code, int seconds, CancellationToken ct)
    {
        var result = await tcp.SendIntervalAsync(code, seconds, ct);
        TempData["Flash"] = result.Detail;
        TempData["FlashOk"] = result.Ok;
        return RedirectToPage(new { code });
    }
}
