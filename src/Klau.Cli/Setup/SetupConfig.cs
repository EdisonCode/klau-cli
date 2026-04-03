namespace Klau.Cli.Setup;

/// <summary>
/// YAML-mappable configuration for provisioning a Klau tenant.
/// Represents the desired state of yards, dump sites, trucks, drivers,
/// container sizes, and service code mappings.
/// </summary>
public sealed class SetupConfig
{
    public YardConfig? Yard { get; set; }
    public List<DumpSiteConfig> DumpSites { get; set; } = [];
    public List<TruckConfig> Trucks { get; set; } = [];
    public List<DriverConfig> Drivers { get; set; } = [];
    public List<int>? ContainerSizes { get; set; }
    public Dictionary<string, string>? ServiceCodes { get; set; }
}

public sealed class YardConfig
{
    public required string Name { get; set; }
    public required string Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int ServiceRadiusMiles { get; set; } = 60;
}

public sealed class DumpSiteConfig
{
    public required string Name { get; set; }
    public required string Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? OpenTime { get; set; }
    public string? CloseTime { get; set; }
    public List<int>? AcceptedSizes { get; set; }
    public string SiteType { get; set; } = "LANDFILL";
}

public sealed class TruckConfig
{
    public required string Number { get; set; }
    public string Type { get; set; } = "ROLL_OFF";
    public required List<int> Sizes { get; set; }
    public int MaxContainers { get; set; } = 1;
}

public sealed class DriverConfig
{
    public required string Name { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Truck { get; set; }
    public string DriverType { get; set; } = "FULL_TIME";
}
