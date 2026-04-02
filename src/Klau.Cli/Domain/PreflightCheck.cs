using Klau.Cli.Output;
using Klau.Sdk;

namespace Klau.Cli.Domain;

/// <summary>
/// Pre-flight readiness check before import. Verifies the Klau account
/// has drivers, trucks, yards, and dump sites configured — the minimum
/// required for dispatch optimization to produce meaningful results.
/// </summary>
public static class PreflightCheck
{
    /// <summary>
    /// Check account readiness and display results. Returns false if
    /// blocking issues prevent dispatch optimization.
    /// </summary>
    public static async Task<bool> RunAsync(KlauClient client, CancellationToken ct)
    {
        ConsoleOutput.Header("Pre-flight readiness check:");

        try
        {
            var report = await client.Readiness.CheckAsync(ct);

            if (report.CanGoLive)
            {
                ConsoleOutput.Success($"Account ready ({report.ReadyPercentage}% configured)");
                return true;
            }

            ConsoleOutput.Warning($"Account {report.ReadyPercentage}% configured — some items need attention:");
            ConsoleOutput.Blank();

            var blocking = false;
            foreach (var section in report.Sections)
            foreach (var item in section.Items)
            {
                if (item.IsComplete) continue;

                if (item.Required)
                {
                    blocking = true;
                    ConsoleOutput.Error($"{item.Label}: {item.Detail ?? "not configured"}");
                    ConsoleOutput.Hint(GetRemediationHint(item.Key));
                }
                else
                {
                    ConsoleOutput.Warning($"{item.Label}: {item.Detail ?? "not configured"}");
                    ConsoleOutput.Hint(GetRemediationHint(item.Key));
                }
            }

            if (blocking)
            {
                ConsoleOutput.Blank();
                ConsoleOutput.Error("Blocking issues must be resolved before import.");
                ConsoleOutput.Hint("Set up your fleet in the Klau dashboard or via the SDK, then retry.");
                return false;
            }

            ConsoleOutput.Blank();
            ConsoleOutput.Warning("Non-blocking issues above may affect optimization quality.");
            return true;
        }
        catch (Exception ex)
        {
            ConsoleOutput.Warning($"Could not verify account readiness: {ex.Message}");
            ConsoleOutput.Hint("Continuing with import — check your Klau dashboard if optimization fails.");
            return true; // Don't block on readiness check failures
        }
    }

    private static string GetRemediationHint(string key) => key switch
    {
        "drivers" => "Add drivers: Klau dashboard > Drivers, or via SDK: klau.Drivers.CreateAsync(...)",
        "trucks" => "Add trucks: Klau dashboard > Trucks, or via SDK: klau.Trucks.CreateAsync(...)",
        "yards" => "Set up a yard: Klau dashboard > Yards, or via SDK: klau.Yards.CreateAsync(...)",
        "dumpSites" => "Add dump sites: Klau dashboard > Dump Sites, or via SDK: klau.DumpSites.CreateAsync(...)",
        "dumpSiteMaterials" => "Configure material pricing: Klau dashboard > Dump Sites > Materials",
        "companyInfo" => "Set timezone and workdays: Klau dashboard > Settings > Company",
        _ => "Check the Klau dashboard for details.",
    };
}
