namespace CNS.StorageCluster.Shared;

public sealed record RegionDefinition(string Code, string Name);

public static class RegionCatalog
{
    public static readonly IReadOnlyList<RegionDefinition> All =
    [
        new("ORU", "Oruro"),
        new("LPZ", "La Paz"),
        new("SCZ", "Santa Cruz"),
        new("BEN", "Beni"),
        new("TJA", "Tarija"),
        new("PND", "Pando"),
        new("CBB", "Cochabamba"),
        new("CHQ", "Chuquisaca"),
        new("PTS", "Potosí")
    ];

    public static bool TryGet(string? code, out RegionDefinition region)
    {
        region = All.FirstOrDefault(r =>
            string.Equals(r.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase))!;
        return region is not null;
    }
}

public static class NodeStates
{
    public const string Online = "ACTIVO";
    public const string NoReporta = "NO_REPORTA";
}
