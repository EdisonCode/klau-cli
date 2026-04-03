using Klau.Sdk;
using Klau.Sdk.Drivers;
using Klau.Sdk.DumpSites;
using Klau.Sdk.Trucks;
using Klau.Sdk.Yards;

namespace Klau.Cli.Setup;

/// <summary>
/// Generates <see cref="SetupConfig"/> instances from the current tenant's
/// live configuration or from a template with example values.
/// </summary>
public static class ConfigGenerator
{
    /// <summary>
    /// Read the current tenant's resources from the SDK and produce
    /// a <see cref="SetupConfig"/> that represents the existing state.
    /// Useful for exporting a known-good config that can be replayed
    /// to another tenant or stored in version control.
    /// </summary>
    public static async Task<SetupConfig> GenerateFromCurrentAsync(
        IKlauClient client,
        CancellationToken ct)
    {
        var config = new SetupConfig();

        // Yards
        var yards = new List<Yard>();
        await foreach (var y in client.Yards.ListAllAsync(ct: ct))
            yards.Add(y);

        if (yards.Count > 0)
        {
            // Use the default yard, or the first one
            var primary = yards.FirstOrDefault(y => y.IsDefault) ?? yards[0];
            config.Yard = new YardConfig
            {
                Name = primary.Name,
                Address = primary.Address,
                City = primary.City,
                State = primary.State,
                Zip = primary.Zip,
                Latitude = primary.Latitude,
                Longitude = primary.Longitude,
                ServiceRadiusMiles = primary.ServiceRadiusMiles.HasValue
                    ? (int)primary.ServiceRadiusMiles.Value
                    : 60,
            };
        }

        // Dump sites
        var dumpSites = new List<DumpSite>();
        await foreach (var ds in client.DumpSites.ListAllAsync(ct: ct))
            dumpSites.Add(ds);

        config.DumpSites = dumpSites.Select(ds => new DumpSiteConfig
        {
            Name = ds.Name,
            Address = ds.Address,
            City = ds.City,
            State = ds.State,
            Zip = ds.Zip,
            Latitude = ds.Lat,
            Longitude = ds.Lng,
            OpenTime = ds.OpenTime,
            CloseTime = ds.CloseTime,
            AcceptedSizes = ds.AcceptedSizes.Count > 0 ? ds.AcceptedSizes.ToList() : null,
            SiteType = ds.SiteType ?? "LANDFILL",
        }).ToList();

        // Trucks
        var trucks = new List<Truck>();
        await foreach (var t in client.Trucks.ListAllAsync(ct: ct))
            trucks.Add(t);

        config.Trucks = trucks.Select(t => new TruckConfig
        {
            Number = t.Number,
            Type = t.Type ?? "ROLL_OFF",
            Sizes = t.CompatibleSizes.Count > 0 ? t.CompatibleSizes.ToList() : [20, 30, 40],
            MaxContainers = t.MaxContainers ?? 1,
        }).ToList();

        // Drivers — resolve truck number from truck ID
        var truckLookup = trucks.ToDictionary(t => t.Id, t => t.Number);

        var drivers = new List<Driver>();
        await foreach (var d in client.Drivers.ListAllAsync(ct: ct))
            drivers.Add(d);

        config.Drivers = drivers.Select(d =>
        {
            string? truckNumber = d.DefaultTruckNumber;
            if (truckNumber is null && d.DefaultTruckId is not null)
                truckLookup.TryGetValue(d.DefaultTruckId, out truckNumber);

            return new DriverConfig
            {
                Name = d.Name,
                Phone = d.Phone,
                Email = d.Email,
                Truck = truckNumber,
                DriverType = d.DriverType ?? "FULL_TIME",
            };
        }).ToList();

        // Company — container sizes and service code mappings
        try
        {
            var company = await client.Company.GetAsync(ct);

            if (company.ContainerSizes.Count > 0)
                config.ContainerSizes = company.ContainerSizes.ToList();

            if (company.ImportServiceCodeMappings is { Count: > 0 } mappings)
            {
                config.ServiceCodes = mappings.ToDictionary(
                    m => m.ExternalCode,
                    m => m.KlauJobType);
            }
        }
        catch
        {
            // Non-critical — continue without company config
        }

        return config;
    }

    /// <summary>
    /// Generate a template <see cref="SetupConfig"/> with example values
    /// that demonstrates all available fields.
    /// </summary>
    public static SetupConfig GenerateTemplate()
    {
        return new SetupConfig
        {
            Yard = new YardConfig
            {
                Name = "Main Yard",
                Address = "123 Industrial Blvd",
                City = "Springfield",
                State = "IL",
                Zip = "62704",
                Latitude = 39.7817,
                Longitude = -89.6501,
                ServiceRadiusMiles = 60,
            },
            DumpSites =
            [
                new DumpSiteConfig
                {
                    Name = "County Landfill",
                    Address = "456 Disposal Rd",
                    City = "Springfield",
                    State = "IL",
                    Zip = "62707",
                    OpenTime = "06:00",
                    CloseTime = "17:00",
                    AcceptedSizes = [10, 20, 30, 40],
                    SiteType = "LANDFILL",
                },
                new DumpSiteConfig
                {
                    Name = "Recycling Center",
                    Address = "789 Green Ave",
                    City = "Springfield",
                    State = "IL",
                    Zip = "62703",
                    OpenTime = "07:00",
                    CloseTime = "16:00",
                    AcceptedSizes = [10, 20, 30],
                    SiteType = "RECYCLING",
                },
            ],
            Trucks =
            [
                new TruckConfig
                {
                    Number = "T-101",
                    Type = "ROLL_OFF",
                    Sizes = [20, 30, 40],
                    MaxContainers = 1,
                },
                new TruckConfig
                {
                    Number = "T-102",
                    Type = "ROLL_OFF",
                    Sizes = [10, 20, 30],
                    MaxContainers = 1,
                },
            ],
            Drivers =
            [
                new DriverConfig
                {
                    Name = "Driver 1",
                    Phone = "555-0101",
                    Email = "driver1@example.com",
                    Truck = "T-101",
                    DriverType = "FULL_TIME",
                },
                new DriverConfig
                {
                    Name = "Driver 2",
                    Phone = "555-0102",
                    Email = "driver2@example.com",
                    Truck = "T-102",
                    DriverType = "FULL_TIME",
                },
            ],
            ContainerSizes = [10, 20, 30, 40],
            ServiceCodes = new Dictionary<string, string>
            {
                ["DEL"] = "DELIVERY",
                ["PU"] = "PICKUP",
                ["SW"] = "SWAP",
                ["DR"] = "DUMP_RETURN",
            },
        };
    }
}
