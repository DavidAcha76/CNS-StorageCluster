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
