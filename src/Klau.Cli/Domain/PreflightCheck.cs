using Klau.Sdk;

namespace Klau.Cli.Domain;

/// <summary>
/// Result of a pre-flight readiness check.
/// </summary>
public sealed record PreflightResult(
    bool CanGoLive,
    int ReadyPercentage,
    IReadOnlyList<PreflightIssue> Issues);

/// <summary>
/// A single issue found during pre-flight readiness check.
/// </summary>
public sealed record PreflightIssue(
    string Label, string? Detail, string Hint, bool Required, bool Blocking);

/// <summary>
/// Pre-flight readiness check before import. Verifies the Klau account
/// has drivers, trucks, yards, and dump sites configured — the minimum
/// required for dispatch optimization to produce meaningful results.
/// </summary>
public static class PreflightCheck
{
    /// <summary>
    /// Check account readiness and return results. The caller is responsible
    /// for rendering the result to the console.
    /// </summary>
    public static async Task<PreflightResult> RunAsync(IKlauClient client, CancellationToken ct)
    {
        try
        {
            var report = await client.Readiness.CheckAsync(ct);

            if (report.CanGoLive)
                return new PreflightResult(true, report.ReadyPercentage, []);

            var issues = new List<PreflightIssue>();
            var blocking = false;

            foreach (var section in report.Sections)
            foreach (var item in section.Items)
            {
                if (item.IsComplete) continue;

                if (item.Required)
                    blocking = true;

                issues.Add(new PreflightIssue(
                    item.Label,
                    item.Detail,
                    GetRemediationHint(item.Key),
                    item.Required,
                    item.Required));
            }

            return new PreflightResult(!blocking, report.ReadyPercentage, issues);
        }
        catch (Exception ex)
        {
            // Don't block on readiness check failures — return as ready
            // with a single non-blocking warning issue
            return new PreflightResult(true, -1, [
                new PreflightIssue(
                    "Readiness check failed",
                    ex.Message,
                    "Continuing with import — check your Klau dashboard if optimization fails.",
                    Required: false,
                    Blocking: false)
            ]);
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
