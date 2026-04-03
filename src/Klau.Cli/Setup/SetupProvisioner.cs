using Klau.Sdk;
using Klau.Sdk.Common;
using Klau.Sdk.Companies;
using Klau.Sdk.Drivers;
using Klau.Sdk.DumpSites;
using Klau.Sdk.Trucks;
using Klau.Sdk.Yards;

namespace Klau.Cli.Setup;

/// <summary>
/// Summarizes what was provisioned during setup.
/// </summary>
public sealed record SetupResult(IReadOnlyList<SetupAction> Actions);

/// <summary>
/// A single provisioning action with its outcome.
/// </summary>
public sealed record SetupAction(
    string ResourceType,
    string Name,
    string Status);

/// <summary>
/// Idempotently provisions a Klau tenant from a <see cref="SetupConfig"/>.
/// For each resource type, it lists existing resources, matches by name
/// (case-insensitive), and creates only what is missing.
/// </summary>
public sealed class SetupProvisioner
{
    private readonly IKlauClient _client;

    public SetupProvisioner(IKlauClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Provision all resources defined in the config.
    /// Returns a result summarizing what was created vs what already existed.
    /// </summary>
    public async Task<SetupResult> ProvisionAsync(
        SetupConfig config,
        CancellationToken ct)
    {
        var actions = new List<SetupAction>();

        // 1. Yard (must come first — trucks and drivers reference it)
        string? yardId = null;
        if (config.Yard is not null)
        {
            (yardId, var yardAction) = await ProvisionYardAsync(config.Yard, ct);
            actions.Add(yardAction);
        }

        // 2. Dump sites
        foreach (var dumpSite in config.DumpSites)
        {
            var action = await ProvisionDumpSiteAsync(dumpSite, ct);
            actions.Add(action);
        }

        // 3. Trucks (resolve homeYardId from yard)
        var truckLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var truck in config.Trucks)
        {
            var (truckId, action) = await ProvisionTruckAsync(truck, yardId, ct);
            actions.Add(action);
            if (truckId is not null)
                truckLookup[truck.Number] = truckId;
        }

        // 4. Drivers (resolve defaultTruckId from truck number, homeYardId from yard)
        foreach (var driver in config.Drivers)
        {
            string? truckId = null;
            if (driver.Truck is not null)
                truckLookup.TryGetValue(driver.Truck, out truckId);

            var action = await ProvisionDriverAsync(driver, yardId, truckId, ct);
            actions.Add(action);
        }

        // 5. Container sizes
        if (config.ContainerSizes is not null && config.ContainerSizes.Count > 0)
        {
            var action = await UpdateContainerSizesAsync(config.ContainerSizes, ct);
            actions.Add(action);
        }

        // 6. Service code mappings
        if (config.ServiceCodes is not null && config.ServiceCodes.Count > 0)
        {
            var action = await UpdateServiceCodesAsync(config.ServiceCodes, ct);
            actions.Add(action);
        }

        return new SetupResult(actions);
    }

    /// <summary>
    /// Build a dry-run plan showing what would be created vs what already exists,
    /// without making any API calls to create resources.
    /// </summary>
    public async Task<SetupResult> DryRunAsync(
        SetupConfig config,
        CancellationToken ct)
    {
        var actions = new List<SetupAction>();

        // Load existing resources once
        var existingYards = await LoadAllAsync(_client.Yards, ct);
        var existingDumpSites = await LoadAllAsync(_client.DumpSites, ct);
        var existingTrucks = await LoadAllAsync(_client.Trucks, ct);
        var existingDrivers = await LoadAllAsync(_client.Drivers, ct);

        // Yard
        if (config.Yard is not null)
        {
            var exists = existingYards.Any(y =>
                y.Name.Equals(config.Yard.Name, StringComparison.OrdinalIgnoreCase));
            actions.Add(new SetupAction("yard", config.Yard.Name,
                exists ? "exists" : "will create"));
        }

        // Dump sites
        foreach (var ds in config.DumpSites)
        {
            var exists = existingDumpSites.Any(d =>
                d.Name.Equals(ds.Name, StringComparison.OrdinalIgnoreCase));
            actions.Add(new SetupAction("dumpSite", ds.Name,
                exists ? "exists" : "will create"));
        }

        // Trucks
        foreach (var truck in config.Trucks)
        {
            var exists = existingTrucks.Any(t =>
                t.Number.Equals(truck.Number, StringComparison.OrdinalIgnoreCase));
            actions.Add(new SetupAction("truck", truck.Number,
                exists ? "exists" : "will create"));
        }

        // Drivers
        foreach (var driver in config.Drivers)
        {
            var exists = existingDrivers.Any(d =>
                d.Name.Equals(driver.Name, StringComparison.OrdinalIgnoreCase));
            actions.Add(new SetupAction("driver", driver.Name,
                exists ? "exists" : "will create"));
        }

        // Container sizes
        if (config.ContainerSizes is not null && config.ContainerSizes.Count > 0)
        {
            actions.Add(new SetupAction("containerSizes",
                string.Join(", ", config.ContainerSizes),
                "will update"));
        }

        // Service codes
        if (config.ServiceCodes is not null && config.ServiceCodes.Count > 0)
        {
            actions.Add(new SetupAction("serviceCodes",
                $"{config.ServiceCodes.Count} mapping(s)",
                "will update"));
        }

        return new SetupResult(actions);
    }

    // ── Yard ────────────────────────────────────────────────────────────────

    private async Task<(string? YardId, SetupAction Action)> ProvisionYardAsync(
        YardConfig config,
        CancellationToken ct)
    {
        try
        {
            var existing = await LoadAllAsync(_client.Yards, ct);
            var match = existing.FirstOrDefault(y =>
                y.Name.Equals(config.Name, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
                return (match.Id, new SetupAction("yard", config.Name, "exists"));

            var yardId = await _client.Yards.CreateAsync(new CreateYardRequest
            {
                Name = config.Name,
                Address = config.Address,
                City = config.City,
                State = config.State,
                Zip = config.Zip,
                Latitude = config.Latitude,
                Longitude = config.Longitude,
                IsDefault = true,
                ServiceRadiusMiles = config.ServiceRadiusMiles,
            }, ct);

            return (yardId, new SetupAction("yard", config.Name, "created"));
        }
        catch (KlauApiException ex)
        {
            return (null, new SetupAction("yard", config.Name, $"failed: {ex.Message}"));
        }
    }

    // ── Dump sites ──────────────────────────────────────────────────────────

    private async Task<SetupAction> ProvisionDumpSiteAsync(
        DumpSiteConfig config,
        CancellationToken ct)
    {
        try
        {
            var existing = await LoadAllAsync(_client.DumpSites, ct);
            var match = existing.FirstOrDefault(d =>
                d.Name.Equals(config.Name, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
                return new SetupAction("dumpSite", config.Name, "exists");

            await _client.DumpSites.CreateAsync(new CreateDumpSiteRequest
            {
                Name = config.Name,
                Address = config.Address,
                City = config.City,
                State = config.State,
                Zip = config.Zip,
                Latitude = config.Latitude,
                Longitude = config.Longitude,
                OpenTime = config.OpenTime,
                CloseTime = config.CloseTime,
                AcceptedSizes = config.AcceptedSizes,
                SiteType = config.SiteType,
            }, ct);

            return new SetupAction("dumpSite", config.Name, "created");
        }
        catch (KlauApiException ex)
        {
            return new SetupAction("dumpSite", config.Name, $"failed: {ex.Message}");
        }
    }

    // ── Trucks ──────────────────────────────────────────────────────────────

    private async Task<(string? TruckId, SetupAction Action)> ProvisionTruckAsync(
        TruckConfig config,
        string? homeYardId,
        CancellationToken ct)
    {
        try
        {
            var existing = await LoadAllAsync(_client.Trucks, ct);
            var match = existing.FirstOrDefault(t =>
                t.Number.Equals(config.Number, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
                return (match.Id, new SetupAction("truck", config.Number, "exists"));

            var truckId = await _client.Trucks.CreateAsync(new CreateTruckRequest
            {
                Number = config.Number,
                Type = config.Type,
                CompatibleSizes = config.Sizes,
                HomeYardId = homeYardId,
                MaxContainers = config.MaxContainers,
            }, ct);

            return (truckId, new SetupAction("truck", config.Number, "created"));
        }
        catch (KlauApiException ex)
        {
            return (null, new SetupAction("truck", config.Number, $"failed: {ex.Message}"));
        }
    }

    // ── Drivers ─────────────────────────────────────────────────────────────

    private async Task<SetupAction> ProvisionDriverAsync(
        DriverConfig config,
        string? homeYardId,
        string? truckId,
        CancellationToken ct)
    {
        try
        {
            var existing = await LoadAllAsync(_client.Drivers, ct);
            var match = existing.FirstOrDefault(d =>
                d.Name.Equals(config.Name, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
                return new SetupAction("driver", config.Name, "exists");

            await _client.Drivers.CreateAsync(new CreateDriverRequest
            {
                Name = config.Name,
                Phone = config.Phone,
                Email = config.Email,
                DriverType = config.DriverType,
                DefaultTruckId = truckId,
                HomeYardId = homeYardId,
            }, ct);

            return new SetupAction("driver", config.Name, "created");
        }
        catch (KlauApiException ex)
        {
            return new SetupAction("driver", config.Name, $"failed: {ex.Message}");
        }
    }

    // ── Container sizes ─────────────────────────────────────────────────────

    private async Task<SetupAction> UpdateContainerSizesAsync(
        List<int> sizes,
        CancellationToken ct)
    {
        var label = string.Join(", ", sizes);
        try
        {
            await _client.Company.UpdateAsync(new UpdateCompanyRequest
            {
                ContainerSizes = sizes,
            }, ct);

            return new SetupAction("containerSizes", label, "updated");
        }
        catch (KlauApiException ex)
        {
            return new SetupAction("containerSizes", label, $"failed: {ex.Message}");
        }
    }

    // ── Service code mappings ───────────────────────────────────────────────

    private async Task<SetupAction> UpdateServiceCodesAsync(
        Dictionary<string, string> codes,
        CancellationToken ct)
    {
        var label = $"{codes.Count} mapping(s)";
        try
        {
            var mappings = codes.Select(kv => new ServiceCodeMapping
            {
                ExternalCode = kv.Key,
                KlauJobType = kv.Value,
            }).ToList();

            await _client.Company.UpdateAsync(new UpdateCompanyRequest
            {
                ImportServiceCodeMappings = mappings,
            }, ct);

            return new SetupAction("serviceCodes", label, "updated");
        }
        catch (KlauApiException ex)
        {
            return new SetupAction("serviceCodes", label, $"failed: {ex.Message}");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<List<Yard>> LoadAllAsync(IYardClient client, CancellationToken ct)
    {
        var items = new List<Yard>();
        await foreach (var item in client.ListAllAsync(ct: ct))
            items.Add(item);
        return items;
    }

    private static async Task<List<DumpSite>> LoadAllAsync(IDumpSiteClient client, CancellationToken ct)
    {
        var items = new List<DumpSite>();
        await foreach (var item in client.ListAllAsync(ct: ct))
            items.Add(item);
        return items;
    }

    private static async Task<List<Truck>> LoadAllAsync(ITruckClient client, CancellationToken ct)
    {
        var items = new List<Truck>();
        await foreach (var item in client.ListAllAsync(ct: ct))
            items.Add(item);
        return items;
    }

    private static async Task<List<Driver>> LoadAllAsync(IDriverClient client, CancellationToken ct)
    {
        var items = new List<Driver>();
        await foreach (var item in client.ListAllAsync(ct: ct))
            items.Add(item);
        return items;
    }
}
