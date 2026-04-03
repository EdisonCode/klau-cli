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
            ctx.ExitCode = await RunAsync(apiKey);
        });

        return command;
    }

    private static async Task<int> RunAsync(string? apiKeyFlag)
    {
        ConsoleOutput.Blank();
        ConsoleOutput.Header("Klau CLI diagnostics:");
        var issues = 0;

        // --- Runtime ---
        ConsoleOutput.Success($".NET runtime: {RuntimeInformation.FrameworkDescription}");
        ConsoleOutput.Success($"OS: {RuntimeInformation.OSDescription}");

        // --- Credentials ---
        var apiKey = CredentialStore.ResolveApiKey(apiKeyFlag);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ConsoleOutput.Error("Authentication: not configured");
            ConsoleOutput.Hint("Run: klau login");
            issues++;
        }
        else
        {
            var source = apiKeyFlag is not null ? "--api-key flag"
                : Environment.GetEnvironmentVariable("KLAU_API_KEY") is not null ? "KLAU_API_KEY env var"
                : "~/.config/klau/credentials.json";
            ConsoleOutput.Success($"Authentication: {CredentialStore.Mask(apiKey)} (from {source})");
        }

        // --- API connectivity ---
        if (apiKey is not null)
        {
            try
            {
                using var client = new KlauClient(apiKey);
                var company = await client.Company.GetAsync();
                ConsoleOutput.Success($"API connectivity: OK ({company.Name})");

                // --- Account readiness ---
                try
                {
                    var readiness = await client.Readiness.CheckAsync();
                    if (readiness.CanGoLive)
                    {
                        ConsoleOutput.Success($"Dispatch readiness: {readiness.ReadyPercentage}% configured");
                    }
                    else
                    {
                        ConsoleOutput.Warning($"Dispatch readiness: {readiness.ReadyPercentage}% configured");
                        foreach (var section in readiness.Sections)
                        foreach (var item in section.Items.Where(i => i.IsIncomplete))
                        {
                            var severity = item.Required ? "blocking" : "recommended";
                            ConsoleOutput.Warning($"  {item.Label}: {item.Detail ?? "not configured"} ({severity})");
                        }
                        issues++;
                    }
                }
                catch (Exception ex)
                {
                    ConsoleOutput.Warning($"Dispatch readiness: could not check ({ex.Message})");
                }
            }
            catch (ArgumentException)
            {
                ConsoleOutput.Error("API connectivity: invalid API key format");
                ConsoleOutput.Hint("API keys must start with kl_live_");
                issues++;
            }
            catch (Exception ex)
            {
                ConsoleOutput.Error($"API connectivity: {ex.Message}");
                ConsoleOutput.Hint("Check your internet connection and API key.");
                issues++;
            }
        }

        // --- Config directory ---
        var configDir = CredentialStore.GetConfigDirectory();
        ConsoleOutput.Status($"Config: {configDir}");

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

        return issues == 0 ? ExitCodes.Success : ExitCodes.ConfigError;
    }
}
