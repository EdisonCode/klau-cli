using System.CommandLine;
using System.CommandLine.Invocation;
using Klau.Cli.Auth;
using Klau.Cli.Domain;
using Klau.Cli.Output;
using Klau.Sdk;
using Klau.Sdk.Common;
using Klau.Sdk.Dispatches;

namespace Klau.Cli.Commands;

/// <summary>
/// klau optimize — run dispatch optimization for a date.
/// Standalone command for optimizing days with unassigned jobs,
/// retrying after a connection drop, or re-optimizing after adding jobs.
/// </summary>
public static class OptimizeCommand
{
    public static Command Create()
    {
        var dateOption = new Option<string?>("--date",
            "Dispatch date (YYYY-MM-DD). Defaults to today.");
        var modeOption = new Option<string?>("--mode",
            "Optimization mode: full-day (default), new-job, or rebalance.");
        var exportOption = new Option<string?>("--export",
            "Export the dispatch plan to a CSV file after optimization.");

        var command = new Command("optimize",
            "Run dispatch optimization for a date. Use after importing jobs, " +
            "or anytime there are unassigned jobs on the board.")
        {
            dateOption, modeOption, exportOption,
        };

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var date = ctx.ParseResult.GetValueForOption(dateOption);
            var mode = ctx.ParseResult.GetValueForOption(modeOption);
            var exportPath = ctx.ParseResult.GetValueForOption(exportOption);
            var apiKey = ctx.ParseResult.GetValueForOption(Program.ApiKeyOption);
            var tenantFlag = ctx.ParseResult.GetValueForOption(Program.TenantOption);
            var ct = SafeCancellation.Create(ctx.GetCancellationToken());
            var json = new CliJsonResponse("optimize");

            ctx.ExitCode = await RunAsync(date, mode, exportPath, apiKey, tenantFlag, ct, json);
            json.Emit(ctx.ExitCode);
        });

        return command;
    }

    internal static async Task<int> RunAsync(
        string? date,
        string? mode,
        string? exportPath,
        string? apiKey,
        string? tenantFlag,
        CancellationToken ct,
        CliJsonResponse? json = null)
    {
        // --- Resolve credentials ---
        var resolvedKey = CredentialStore.ResolveApiKey(apiKey);
        if (string.IsNullOrWhiteSpace(resolvedKey))
        {
            ConsoleOutput.Error("No API key found.");
            ConsoleOutput.Hint("Run: klau login");
            json?.SetError("CONFIG_ERROR", "No API key found.", "Run: klau login");
            return ExitCodes.ConfigError;
        }

        // --- Resolve date ---
        string dispatchDate;
        if (date is not null)
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out _))
            {
                ConsoleOutput.Error($"Invalid date format: \"{date}\".");
                ConsoleOutput.Hint("Expected format: YYYY-MM-DD (e.g. 2026-04-07).");
                json?.SetError("INPUT_ERROR", $"Invalid date format: \"{date}\".");
                return ExitCodes.InputError;
            }
            dispatchDate = date;
        }
        else
        {
            dispatchDate = DateTime.Today.ToString("yyyy-MM-dd");
        }

        // --- Resolve mode ---
        var optimizationMode = ParseMode(mode);
        if (optimizationMode is null && mode is not null)
        {
            ConsoleOutput.Error($"Unknown optimization mode: \"{mode}\".");
            ConsoleOutput.Hint("Valid modes: full-day, new-job, rebalance");
            json?.SetError("INPUT_ERROR", $"Unknown optimization mode: \"{mode}\".");
            return ExitCodes.InputError;
        }

        ConsoleOutput.Blank();

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
            using var client = new KlauClient(resolvedKey, httpClient);
            var tenantId = CredentialStore.ResolveTenantId(tenantFlag);
            IKlauClient api = tenantId is not null ? client.ForTenant(tenantId) : client;

            // --- Optimize ---
            ct.ThrowIfCancellationRequested();
            OptimizationJob job;
            using (ConsoleOutput.StartSpinner($"Optimizing dispatch for {dispatchDate}"))
            {
                job = await api.Dispatches.StartOptimizationAsync(
                    new OptimizeRequest
                    {
                        Date = dispatchDate,
                        OptimizationMode = optimizationMode ?? OptimizationMode.FULL_DAY,
                    }, ct);

                var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
                while (job.Status is OptimizationJobStatus.PENDING or OptimizationJobStatus.RUNNING)
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        ConsoleOutput.Warning("Optimization is taking longer than expected.");
                        ConsoleOutput.Hint("It's still running — check the Klau dashboard for results, or retry: klau optimize");
                        if (json is not null)
                        {
                            json.Data["date"] = dispatchDate;
                            json.Data["timedOut"] = true;
                        }
                        return ExitCodes.Success;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    job = await api.Dispatches.GetOptimizationStatusAsync(job.JobId, ct);
                }
            }

            if (job.Status == OptimizationJobStatus.SKIPPED)
            {
                ConsoleOutput.Warning($"Optimization skipped: {job.Reason ?? "business day has ended"}");
                if (json is not null)
                {
                    json.Data["date"] = dispatchDate;
                    json.Data["skipped"] = true;
                    json.Data["reason"] = job.Reason;
                }
                return ExitCodes.Success;
            }

            if (job.Status == OptimizationJobStatus.FAILED)
            {
                ConsoleOutput.Error($"Optimization failed: {job.Reason ?? "unknown error"}");
                ConsoleOutput.Hint("Check the Klau dashboard for details, or retry.");
                json?.SetError("OPTIMIZATION_FAILED", job.Reason ?? "unknown error");
                return ExitCodes.ApiError;
            }

            var r = job.Result;
            ConsoleOutput.Success($"Grade: {r?.PlanGrade ?? "N/A"} " +
                $"({r?.PlanQuality ?? 0}/100)  |  Flow: {r?.FlowScore ?? 0}/100");
            ConsoleOutput.Success($"Assigned: {r?.AssignedJobs ?? 0}/{(r?.AssignedJobs ?? 0) + (r?.UnassignedJobs ?? 0)}  " +
                $"|  Drive times: {r?.DriveTimeSource ?? "N/A"}");

            if (r?.UnassignedJobs > 0)
                ConsoleOutput.Warning($"{r.UnassignedJobs} job(s) could not be assigned — check driver availability and time windows.");

            if (json is not null)
            {
                json.Data["date"] = dispatchDate;
                json.Data["mode"] = (optimizationMode ?? OptimizationMode.FULL_DAY).ToString().ToLowerInvariant();
                json.Data["grade"] = r?.PlanGrade;
                json.Data["planQuality"] = r?.PlanQuality;
                json.Data["flowScore"] = r?.FlowScore;
                json.Data["assigned"] = r?.AssignedJobs;
                json.Data["unassigned"] = r?.UnassignedJobs;
                json.Data["driveTimeSource"] = r?.DriveTimeSource;
            }

            // --- Export ---
            if (exportPath is not null)
            {
                ct.ThrowIfCancellationRequested();
                var pipeline = new ImportPipeline(api);
                using (ConsoleOutput.StartSpinner("Exporting dispatch plan"))
                {
                    await pipeline.ExportAsync(dispatchDate, exportPath, ct);
                }
                ConsoleOutput.Success($"Exported to {exportPath}");
                if (json is not null)
                    json.Data["exportPath"] = exportPath;
            }

            return ExitCodes.Success;
        }
        catch (KlauApiException ex) when (ex.IsUnauthorized)
        {
            ConsoleOutput.Error("Authentication failed.");
            ConsoleOutput.Hint("Check that your API key is valid: klau doctor");
            json?.SetError("UNAUTHORIZED", ex.Message, "Check that your API key is valid: klau doctor");
            return ExitCodes.ConfigError;
        }
        catch (KlauApiException ex)
        {
            ConsoleOutput.Error($"{ex.ErrorCode}: {ex.Message}");
            json?.SetError(ex.ErrorCode ?? "API_ERROR", ex.Message);
            return ExitCodes.ApiError;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ConsoleOutput.Error("Request timed out.");
            ConsoleOutput.Hint("Optimization may still be running — check the Klau dashboard.");
            json?.SetError("TIMEOUT", "Request timed out.", "Optimization may still be running — check the Klau dashboard.");
            return ExitCodes.ApiError;
        }
        catch (OperationCanceledException)
        {
            ConsoleOutput.Blank();
            ConsoleOutput.Status("Cancelled.");
            return ExitCodes.Success;
        }
    }

    private static OptimizationMode? ParseMode(string? mode) => mode?.ToLowerInvariant() switch
    {
        null or "" or "full-day" or "full_day" => OptimizationMode.FULL_DAY,
        "new-job" or "new_job" => OptimizationMode.NEW_JOB,
        "rebalance" => OptimizationMode.REBALANCE,
        _ => null,
    };
}
