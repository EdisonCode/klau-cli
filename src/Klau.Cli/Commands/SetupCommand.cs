using System.CommandLine;
using System.CommandLine.Invocation;
using Klau.Cli.Auth;
using Klau.Cli.Domain;
using Klau.Cli.Output;
using Klau.Cli.Setup;
using Klau.Sdk;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Klau.Cli.Commands;

/// <summary>
/// klau setup -- provision a Klau tenant from a YAML configuration file.
///
/// Modes:
///   klau setup file.yaml              -- provision from YAML
///   klau setup file.yaml --dry-run    -- show what would be created
///   klau setup --generate             -- export current config as YAML
///   klau setup --template             -- generate blank template with examples
/// </summary>
public static class SetupCommand
{
    public static Command Create()
    {
        var fileArg = new Argument<FileInfo?>("file", "Path to a YAML setup configuration file.");
        fileArg.Arity = ArgumentArity.ZeroOrOne;

        var dryRunOption = new Option<bool>("--dry-run",
            "Show what would be created without making changes.");
        var generateOption = new Option<bool>("--generate",
            "Export the current tenant's configuration as YAML.");
        var templateOption = new Option<bool>("--template",
            "Generate a template YAML file with example values.");

        var command = new Command("setup",
            "Provision yards, dump sites, trucks, and drivers from a YAML file.")
        {
            fileArg, dryRunOption, generateOption, templateOption,
        };

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var file = ctx.ParseResult.GetValueForArgument(fileArg);
            var dryRun = ctx.ParseResult.GetValueForOption(dryRunOption);
            var generate = ctx.ParseResult.GetValueForOption(generateOption);
            var template = ctx.ParseResult.GetValueForOption(templateOption);
            var apiKey = ctx.ParseResult.GetValueForOption(Program.ApiKeyOption);
            var tenant = ctx.ParseResult.GetValueForOption(Program.TenantOption);
            var ct = ctx.GetCancellationToken();

            ctx.ExitCode = await RunAsync(file, dryRun, generate, template, apiKey, tenant, ct);
        });

        return command;
    }

    internal static async Task<int> RunAsync(
        FileInfo? file,
        bool dryRun,
        bool generate,
        bool template,
        string? apiKey,
        string? tenant,
        CancellationToken ct)
    {
        // --- Template mode (no API key needed) ---
        if (template)
            return EmitTemplate();

        // --- Generate mode ---
        if (generate)
            return await GenerateAsync(apiKey, tenant, ct);

        // --- Provision mode ---
        if (file is null)
        {
            ConsoleOutput.Error("No setup file specified.");
            ConsoleOutput.Hint("Usage: klau setup <file.yaml>");
            ConsoleOutput.Hint("       klau setup --template > template.yaml");
            return ExitCodes.InputError;
        }

        return await ProvisionAsync(file, dryRun, apiKey, tenant, ct);
    }

    // ── Template ────────────────────────────────────────────────────────────

    private static int EmitTemplate()
    {
        var config = ConfigGenerator.GenerateTemplate();
        var yaml = SerializeConfig(config);

        // Write header comment followed by the YAML
        Console.WriteLine("# Klau setup configuration");
        Console.WriteLine("# Edit this file, then run: klau setup <file.yaml>");
        Console.WriteLine("#");
        Console.WriteLine("# Fields marked with 'required' must be provided.");
        Console.WriteLine("# All other fields are optional and show default values.");
        Console.WriteLine();
        Console.Write(yaml);

        return ExitCodes.Success;
    }

    // ── Generate ────────────────────────────────────────────────────────────

    private static async Task<int> GenerateAsync(
        string? apiKey,
        string? tenant,
        CancellationToken ct)
    {
        var resolvedKey = CredentialStore.ResolveApiKey(apiKey);
        if (string.IsNullOrWhiteSpace(resolvedKey))
        {
            ConsoleOutput.Error("No API key found.");
            ConsoleOutput.Hint("Run: klau login");
            return ExitCodes.ConfigError;
        }

        try
        {
            using var client = new KlauClient(resolvedKey);
            var tenantId = CredentialStore.ResolveTenantId(tenant);
            IKlauClient api = tenantId is not null ? client.ForTenant(tenantId) : client;

            var config = await ConfigGenerator.GenerateFromCurrentAsync(api, ct);
            var yaml = SerializeConfig(config);

            Console.WriteLine("# Klau setup configuration (generated from current account)");
            Console.WriteLine($"# Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            Console.WriteLine();
            Console.Write(yaml);

            return ExitCodes.Success;
        }
        catch (ArgumentException)
        {
            ConsoleOutput.Error("Invalid API key format.");
            ConsoleOutput.Hint("API keys must start with kl_live_");
            return ExitCodes.ConfigError;
        }
        catch (Exception ex)
        {
            ConsoleOutput.Error($"Failed to generate config: {ex.Message}");
            return ExitCodes.ApiError;
        }
    }

    // ── Provision ───────────────────────────────────────────────────────────

    private static async Task<int> ProvisionAsync(
        FileInfo file,
        bool dryRun,
        string? apiKey,
        string? tenant,
        CancellationToken ct)
    {
        // Validate file exists
        if (!file.Exists)
        {
            ConsoleOutput.Error($"File not found: {file.FullName}");
            return ExitCodes.InputError;
        }

        // Parse YAML
        SetupConfig config;
        try
        {
            var yaml = await File.ReadAllTextAsync(file.FullName, ct);
            config = DeserializeConfig(yaml);
        }
        catch (Exception ex)
        {
            ConsoleOutput.Error($"Failed to parse YAML: {ex.Message}");
            ConsoleOutput.Hint("Check the file format. Use --template to generate an example.");
            return ExitCodes.InputError;
        }

        // Validate minimal config
        if (config.Yard is null
            && config.DumpSites.Count == 0
            && config.Trucks.Count == 0
            && config.Drivers.Count == 0
            && (config.ContainerSizes is null || config.ContainerSizes.Count == 0)
            && (config.ServiceCodes is null || config.ServiceCodes.Count == 0))
        {
            ConsoleOutput.Error("Setup file is empty. Nothing to provision.");
            ConsoleOutput.Hint("Use --template to generate an example configuration.");
            return ExitCodes.InputError;
        }

        // Resolve API key
        var resolvedKey = CredentialStore.ResolveApiKey(apiKey);
        if (string.IsNullOrWhiteSpace(resolvedKey))
        {
            ConsoleOutput.Error("No API key found.");
            ConsoleOutput.Hint("Run: klau login");
            return ExitCodes.ConfigError;
        }

        try
        {
            using var client = new KlauClient(resolvedKey);
            var tenantId = CredentialStore.ResolveTenantId(tenant);
            IKlauClient api = tenantId is not null ? client.ForTenant(tenantId) : client;

            var provisioner = new SetupProvisioner(api);

            // Show summary of what the file contains
            ConsoleOutput.Blank();
            ConsoleOutput.Header($"Setup plan ({file.Name}):");
            PrintConfigSummary(config);

            if (dryRun)
            {
                // Dry run — check existing resources, show plan
                SetupResult result;
                using (ConsoleOutput.StartSpinner("Checking existing resources"))
                {
                    result = await provisioner.DryRunAsync(config, ct);
                }

                ConsoleOutput.Header("Dry run results:");
                RenderActions(result.Actions);

                var toCreate = result.Actions.Count(a => a.Status.StartsWith("will", StringComparison.Ordinal));
                var existing = result.Actions.Count(a => a.Status == "exists");
                ConsoleOutput.Summary($"Dry run complete: {toCreate} to create, {existing} already exist. No changes made.");

                return ExitCodes.Success;
            }

            // Live provision
            SetupResult provisionResult;
            using (ConsoleOutput.StartSpinner("Provisioning resources"))
            {
                provisionResult = await provisioner.ProvisionAsync(config, ct);
            }

            ConsoleOutput.Header("Setup results:");
            RenderActions(provisionResult.Actions);

            var created = provisionResult.Actions.Count(a => a.Status == "created");
            var existed = provisionResult.Actions.Count(a => a.Status == "exists");
            var updated = provisionResult.Actions.Count(a => a.Status == "updated");
            var failed = provisionResult.Actions.Count(a => a.Status.StartsWith("failed", StringComparison.Ordinal));

            if (failed > 0)
            {
                ConsoleOutput.Summary($"Setup complete with errors: {created} created, {existed} existed, {updated} updated, {failed} failed.");
                return ExitCodes.PartialFailure;
            }

            ConsoleOutput.Summary($"Setup complete: {created} created, {existed} already existed, {updated} updated.");
            return ExitCodes.Success;
        }
        catch (ArgumentException)
        {
            ConsoleOutput.Error("Invalid API key format.");
            ConsoleOutput.Hint("API keys must start with kl_live_");
            return ExitCodes.ConfigError;
        }
        catch (OperationCanceledException)
        {
            ConsoleOutput.Blank();
            ConsoleOutput.Status("Cancelled.");
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            ConsoleOutput.Error($"Setup failed: {ex.Message}");
            return ExitCodes.ApiError;
        }
    }

    // ── Config summary ──────────────────────────────────────────────────────

    private static void PrintConfigSummary(SetupConfig config)
    {
        if (config.Yard is not null)
            ConsoleOutput.Status($"  Yard: {config.Yard.Name}");

        if (config.DumpSites.Count > 0)
            ConsoleOutput.Status($"  Dump sites: {config.DumpSites.Count} ({string.Join(", ", config.DumpSites.Select(d => d.Name))})");

        if (config.Trucks.Count > 0)
            ConsoleOutput.Status($"  Trucks: {config.Trucks.Count} ({string.Join(", ", config.Trucks.Select(t => t.Number))})");

        if (config.Drivers.Count > 0)
            ConsoleOutput.Status($"  Drivers: {config.Drivers.Count} ({string.Join(", ", config.Drivers.Select(d => d.Name))})");

        if (config.ContainerSizes is { Count: > 0 })
            ConsoleOutput.Status($"  Container sizes: [{string.Join(", ", config.ContainerSizes)}]");

        if (config.ServiceCodes is { Count: > 0 })
            ConsoleOutput.Status($"  Service codes: {config.ServiceCodes.Count} mapping(s)");
    }

    // ── Action rendering ────────────────────────────────────────────────────

    private static void RenderActions(IReadOnlyList<SetupAction> actions)
    {
        foreach (var action in actions)
        {
            var label = $"{FormatResourceType(action.ResourceType)}: {action.Name}";

            if (action.Status is "created" or "updated")
                ConsoleOutput.Success(label);
            else if (action.Status == "exists")
                ConsoleOutput.Status($"    {label} (already exists)");
            else if (action.Status.StartsWith("will", StringComparison.Ordinal))
                ConsoleOutput.Status($"    {label} ({action.Status})");
            else if (action.Status.StartsWith("failed", StringComparison.Ordinal))
                ConsoleOutput.Error($"{label} ({action.Status})");
            else
                ConsoleOutput.Status($"    {label} ({action.Status})");
        }
    }

    private static string FormatResourceType(string type) => type switch
    {
        "yard" => "Yard",
        "dumpSite" => "Dump site",
        "truck" => "Truck",
        "driver" => "Driver",
        "containerSizes" => "Container sizes",
        "serviceCodes" => "Service codes",
        _ => type,
    };

    // ── YAML serialization ──────────────────────────────────────────────────

    internal static SetupConfig DeserializeConfig(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return deserializer.Deserialize<SetupConfig>(yaml) ?? new SetupConfig();
    }

    internal static string SerializeConfig(SetupConfig config)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        return serializer.Serialize(config);
    }
}
