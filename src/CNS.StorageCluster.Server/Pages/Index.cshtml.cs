using CNS.StorageCluster.Server.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CNS.StorageCluster.Server.Pages;

public sealed class IndexModel(ClusterQueryService query) : PageModel
{
    public DashboardSnapshot Snapshot { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Snapshot = await query.GetDashboardAsync(ct);
    }
}
