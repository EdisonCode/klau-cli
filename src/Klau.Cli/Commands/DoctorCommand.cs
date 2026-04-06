using System.CommandLine;
using System.CommandLine.Invocation;
using System.Runtime.InteropServices;
using Klau.Cli.Auth;
using Klau.Cli.Domain;
using Klau.Cli.Output;
using Klau.Sdk;

namespace Klau.Cli.Commands;

/// <summary>
/// klau doctor — diagnose common issues in one command.
/// Checks runtime, connectivity, auth, and account readiness.
/// </summary>
public static class DoctorCommand
{
    public static Command Create()
    {
        var command = new Command("doctor",
            "Check your environment, authentication, and Klau account configuration.");

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var apiKey = ctx.ParseResult.GetValueForOption(Program.ApiKeyOption);
            var tenantFlag = ctx.ParseResult.GetValueForOption(Program.TenantOption);
            var ct = SafeCancellation.Create(ctx.GetCancellationToken());
            var json = new CliJsonResponse("doctor");

            ctx.ExitCode = await RunAsync(apiKey, tenantFlag, ct, json);
            json.Emit(ctx.ExitCode);
        });

        return command;
    }

    internal static async Task<int> RunAsync(string? apiKeyFlag, string? tenantFlag,
        CancellationToken ct, CliJsonResponse? json = null)
    {
        ConsoleOutput.Blank();
        ConsoleOutput.Header("Klau CLI diagnostics:");
        var issues = 0;

        // --- Runtime ---
        var runtime = RuntimeInformation.FrameworkDescription;
        var os = RuntimeInformation.OSDescription;
        ConsoleOutput.Success($".NET runtime: {runtime}");
        ConsoleOutput.Success($"OS: {os}");
        if (json is not null)
        {
            json.Data["runtime"] = runtime;
            json.Data["os"] = os;
        }

        // --- Credentials ---
        var apiKey = CredentialStore.ResolveApiKey(apiKeyFlag);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ConsoleOutput.Error("Authentication: not configured");
            ConsoleOutput.Hint("Run: klau login");
            if (json is not null)
                json.Data["authentication"] = new Dictionary<string, object?> { ["configured"] = false };
            issues++;
        }
        else
        {
            var source = apiKeyFlag is not null ? "--api-key flag"
                : Environment.GetEnvironmentVariable("KLAU_API_KEY") is not null ? "KLAU_API_KEY env var"
                : "~/.config/klau/credentials.json";
            ConsoleOutput.Success($"Authentication: {CredentialStore.Mask(apiKey)} (from {source})");
            if (json is not null)
            {
                json.Data["authentication"] = new Dictionary<string, object?>
                {
                    ["configured"] = true,
                    ["source"] = source,
                    ["maskedKey"] = CredentialStore.Mask(apiKey),
                };
            }
        }

        // --- Tenant ---
        var tenantId = CredentialStore.ResolveTenantId(tenantFlag);
        if (tenantId is not null)
        {
            var tenantSource = !string.IsNullOrWhiteSpace(tenantFlag) ? "--tenant flag" : "stored credentials";
            ConsoleOutput.Success($"Tenant: {tenantId} (from {tenantSource})");
            if (json is not null)
                json.Data["tenant"] = new Dictionary<string, object?> { ["id"] = tenantId, ["source"] = tenantSource };
        }
        else
        {
            ConsoleOutput.Status("Tenant: none (operating as primary account)");
            if (json is not null)
                json.Data["tenant"] = null;
        }

        // --- API connectivity ---
        if (apiKey is not null)
        {
            try
            {
                using var httpClient = CliHttp.CreateClient();
                using var client = new KlauClient(apiKey, httpClient);
                IKlauClient api = tenantId is not null ? client.ForTenant(tenantId) : client;
                var company = await api.Company.GetAsync(ct);
                ConsoleOutput.Success($"API connectivity: OK ({company.Name})");
                if (json is not null)
                    json.Data["apiConnectivity"] = new Dictionary<string, object?> { ["ok"] = true, ["companyName"] = company.Name };

                // --- Account readiness ---
                try
                {
                    var readiness = await api.Readiness.CheckAsync(ct);
                    if (readiness.CanGoLive)
                    {
                        ConsoleOutput.Success($"Dispatch readiness: {readiness.ReadyPercentage}% configured");
                    }
                    else
                    {
                        ConsoleOutput.Warning($"Dispatch readiness: {readiness.ReadyPercentage}% configured");
                        var dispatchKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        {
                            "drivers", "trucks", "yards", "dumpSites",
                            "dumpSiteMaterials", "companyInfo",
                        };
                        foreach (var section in readiness.Sections)
                        foreach (var item in section.Items.Where(i =>
                            i.IsIncomplete && dispatchKeys.Contains(i.Key)))
                        {
                            var severity = item.Required ? "blocking" : "recommended";
                            ConsoleOutput.Warning($"  {item.Label}: {item.Detail ?? "not configured"} ({severity})");
                        }
                        issues++;
                    }
                    if (json is not null)
                    {
                        json.Data["dispatchReadiness"] = new Dictionary<string, object?>
                        {
                            ["readyPercentage"] = readiness.ReadyPercentage,
                            ["canGoLive"] = readiness.CanGoLive,
                        };
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ConsoleOutput.Warning($"Dispatch readiness: could not check ({ex.Message})");
                }
            }
            catch (ArgumentException)
            {
                ConsoleOutput.Error("API connectivity: invalid API key format");
                ConsoleOutput.Hint("API keys must start with kl_live_");
                if (json is not null)
                    json.Data["apiConnectivity"] = new Dictionary<string, object?> { ["ok"] = false, ["error"] = "invalid API key format" };
                issues++;
            }
            catch (Exception ex)
            {
                ConsoleOutput.Error($"API connectivity: {ex.Message}");
                ConsoleOutput.Hint("Check your internet connection and API key.");
                if (json is not null)
                    json.Data["apiConnectivity"] = new Dictionary<string, object?> { ["ok"] = false, ["error"] = ex.Message };
                issues++;
            }
        }

        // --- Config directory ---
        var configDir = CredentialStore.GetConfigDirectory();
        ConsoleOutput.Status($"Config: {configDir}");
        if (json is not null)
            json.Data["configDir"] = configDir;

        // --- Watch mode heartbeat ---
        var cwd = Directory.GetCurrentDirectory();
        var heartbeat = Path.Combine(cwd, ".klau-heartbeat");
        if (File.Exists(heartbeat))
        {
            var lastBeat = File.GetLastWriteTimeUtc(heartbeat);
            var age = DateTime.UtcNow - lastBeat;
            if (age.TotalMinutes < 2)
                ConsoleOutput.Success($"Watch mode heartbeat: alive ({age.TotalSeconds:F0}s ago)");
            else
                ConsoleOutput.Warning($"Watch mode heartbeat: stale ({age.TotalMinutes:F0} min ago)");
        }

        // --- Summary ---
        ConsoleOutput.Blank();
        if (issues == 0)
            ConsoleOutput.Success("All checks passed.");
        else
            ConsoleOutput.Warning($"{issues} issue(s) found. See above for details.");
        ConsoleOutput.Blank();

        if (json is not null)
            json.Data["issues"] = issues;

        return issues == 0 ? ExitCodes.Success : ExitCodes.ConfigError;
    }
}
