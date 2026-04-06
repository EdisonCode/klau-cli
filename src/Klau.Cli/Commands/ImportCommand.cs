using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using Klau.Cli.Auth;
using Klau.Cli.Domain;
using Klau.Cli.Import;
using Klau.Cli.Output;
using Klau.Sdk;

namespace Klau.Cli.Commands;

/// <summary>
/// Thin command adapter — parses CLI args, delegates to ImportPipeline.
/// </summary>
public static class ImportCommand
{
    /// <summary>
    /// Known Klau field names, used for interactive remapping of low-confidence columns.
    /// </summary>
    private static readonly string[] KlauFields =
    [
        "CustomerName", "SiteName", "SiteAddress", "SiteCity", "SiteState", "SiteZip",
        "JobType", "ContainerSize", "TimeWindow", "Priority", "Notes", "RequestedDate", "ExternalId"
    ];

    public static Command Create()
    {
        var fileArg = new Argument<FileInfo?>("file", "Path to the file to import (CSV, TSV, or XLSX)");
        fileArg.Arity = ArgumentArity.ZeroOrOne;
        var dateOption = new Option<string?>("--date", "Dispatch date (YYYY-MM-DD). Defaults to today.");
        var mappingOption = new Option<string?>("--mapping", "Path to a column mapping JSON file.");
        var optimizeOption = new Option<bool>("--optimize", "Run dispatch optimization after import.");
        var exportOption = new Option<string?>("--export", "Export the dispatch plan to a CSV file.");
        var dryRunOption = new Option<bool>("--dry-run", "Validate and preview without importing.");

        var command = new Command("import", "Import jobs from a CSV or Excel file into Klau.")
        {
            fileArg, dateOption, mappingOption, optimizeOption, exportOption, dryRunOption,
        };

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var file = ctx.ParseResult.GetValueForArgument(fileArg);
            var date = ctx.ParseResult.GetValueForOption(dateOption);
            var mappingPath = ctx.ParseResult.GetValueForOption(mappingOption);
            var optimize = ctx.ParseResult.GetValueForOption(optimizeOption);
            var exportPath = ctx.ParseResult.GetValueForOption(exportOption);
            var dryRun = ctx.ParseResult.GetValueForOption(dryRunOption);
            var apiKey = ctx.ParseResult.GetValueForOption(Program.ApiKeyOption);
            var tenantFlag = ctx.ParseResult.GetValueForOption(Program.TenantOption);
            var ct = SafeCancellation.Create(ctx.GetCancellationToken());
            var json = new CliJsonResponse("import");

            ctx.ExitCode = await RunAsync(
                file, date, mappingPath, optimize, exportPath, dryRun, apiKey, tenantFlag, ct, json);

            json.Emit(ctx.ExitCode);
        });

        command.AddCommand(WatchCommand.Create());
        return command;
    }

    internal static async Task<int> RunAsync(
        FileInfo? file,
        string? date,
        string? mappingPath,
        bool optimize,
        string? exportPath,
        bool dryRun,
        string? apiKey,
        string? tenantFlag,
        CancellationToken ct,
        CliJsonResponse? json = null)
    {
        var stopwatch = Stopwatch.StartNew();

        // --- Resolve file if not provided ---
        if (file is null)
        {
            file = TryResolveFile();
            if (file is null)
            {
                json?.SetError("INPUT_ERROR", "No file specified.");
                return ExitCodes.InputError;
            }
        }

        // --- Validate config (flag > env var > stored credentials) ---
        var resolvedKey = CredentialStore.ResolveApiKey(apiKey);
        if (!dryRun && string.IsNullOrWhiteSpace(resolvedKey))
        {
            resolvedKey = await TryFirstRunAuthAsync();
            if (string.IsNullOrWhiteSpace(resolvedKey))
            {
                json?.SetError("CONFIG_ERROR", "No API key found.", "Run: klau login");
                return ExitCodes.ConfigError;
            }
        }

        // --- Validate date ---
        string dispatchDate;
        if (date is not null)
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out _))
            {
                ConsoleOutput.Error($"Invalid date format: \"{date}\".");
                ConsoleOutput.Hint("Expected format: YYYY-MM-DD (e.g. 2026-04-03).");
                json?.SetError("INPUT_ERROR", $"Invalid date format: \"{date}\".",
                    "Expected format: YYYY-MM-DD (e.g. 2026-04-03).");
                return ExitCodes.InputError;
            }
            dispatchDate = date;
        }
        else
        {
            dispatchDate = DateTime.Today.ToString("yyyy-MM-dd");
        }

        // --- Step 1: Read file ---
        ConsoleOutput.Blank();
        SpreadsheetData data;
        ColumnMapping mapping;
        try
        {
            ct.ThrowIfCancellationRequested();
            // Use a longer HTTP timeout than the SDK default (30s) — bulk import
            // processes jobs sequentially and can take 60-90s for large batches.
            using var httpClient = dryRun ? null : CliHttp.CreateClient(TimeSpan.FromSeconds(180));
            using var client = dryRun ? null : new KlauClient(resolvedKey!, httpClient);
            var tenantId = CredentialStore.ResolveTenantId(tenantFlag);
            IKlauClient? api = client is not null && tenantId is not null
                ? client.ForTenant(tenantId)
                : client;
            var pipeline = api is not null ? new ImportPipeline(api) : null;

            (data, mapping) = pipeline is not null
                ? pipeline.ReadAndMap(file.FullName, mappingPath)
                : ReadAndMapStandalone(file.FullName, mappingPath);

            ConsoleOutput.Status($"Reading {file.Name}... {data.Rows.Count} rows ({data.SourceFormat})");

            // --- Check for low-confidence mappings ---
            mapping = ConfirmLowConfidenceMappings(mapping);

            // --- Display mapping ---
            ConsoleOutput.Header("Column mapping:");
            var padWidth = mapping.Matches.Count > 0
                ? mapping.Matches.Max(m => m.CsvHeader.Length) + 2 : 10;
            foreach (var match in mapping.Matches)
                ConsoleOutput.Mapping(match.CsvHeader, match.KlauField, padWidth);
            if (mapping.UnmappedHeaders.Count > 0)
            {
                ConsoleOutput.Blank();
                foreach (var h in mapping.UnmappedHeaders.Take(10))
                    ConsoleOutput.Warning($"Unmapped: \"{h}\"");
                if (mapping.UnmappedHeaders.Count > 10)
                    ConsoleOutput.Warning($"...and {mapping.UnmappedHeaders.Count - 10} more");
            }

            // --- Step 2: Map rows ---
            ct.ThrowIfCancellationRequested();
            var batch = ImportPipeline.MapRows(data, mapping, dispatchDate);

            foreach (var w in batch.Warnings)
                ConsoleOutput.Warning($"Row {w.RowNumber}: {w.Message}");

            // --- Step 3: Validate rows ---
            ValidationReport? validation = null;
            if (batch.Rows.Count > 0)
            {
                validation = RowValidator.Validate(batch.Rows);

                foreach (var w in validation.Warnings)
                    ConsoleOutput.Warning($"Row {w.RowNumber}: {w.Message}");

                if (validation.AddressMissingCount > 0)
                {
                    ConsoleOutput.Blank();
                    ConsoleOutput.Warning($"{validation.AddressMissingCount} of {validation.TotalRows} " +
                        "rows have no address — these jobs cannot be geocoded or routed.");
                    ConsoleOutput.Hint("Add an address column to your export, or map it with --mapping.");
                }

                if (validation.HasBlockingIssues)
                {
                    ConsoleOutput.Error("Over half the rows are missing addresses. " +
                        "This likely means the address column is not mapped correctly.");
                    ConsoleOutput.Hint("Re-run with --dry-run to check your column mapping.");
                    return ExitCodes.InputError;
                }

                if (validation.DuplicateExternalIdCount > 0)
                {
                    ConsoleOutput.Warning($"{validation.DuplicateExternalIdCount} duplicate external ID(s) " +
                        "— duplicates will be rejected by the API.");
                }
            }

            // --- Preview ---
            if (batch.Rows.Count > 0)
            {
                ConsoleOutput.Header($"Preview ({Math.Min(5, batch.Rows.Count)} of {batch.Rows.Count} rows):");
                var previewHeaders = new[] { "Customer", "Address", "City", "Type", "ExternalId" };
                var previewRows = batch.Rows.Take(5).Select(r => new[]
                {
                    Truncate(r.CustomerName, 22),
                    Truncate(r.SiteAddress ?? "", 25),
                    Truncate(r.SiteCity ?? "", 15),
                    r.JobType ?? "",
                    Truncate(r.ExternalId ?? "", 15),
                }).ToList();
                ConsoleOutput.Table(previewHeaders, previewRows);
            }

            // --- Dry run stops here ---
            if (dryRun)
            {
                ConsoleOutput.Summary($"Dry run complete: {batch.Rows.Count} rows mapped, " +
                    $"{batch.Warnings.Count + (validation?.Warnings.Count ?? 0)} " +
                    "warnings. No data sent to Klau.");
                if (json is not null)
                {
                    json.Data["dryRun"] = true;
                    json.Data["rowsMapped"] = batch.Rows.Count;
                    json.Data["warnings"] = batch.Warnings.Count + (validation?.Warnings.Count ?? 0);
                }
                return ExitCodes.Success;
            }

            // --- Step 4: Pre-flight readiness check ---
            ct.ThrowIfCancellationRequested();
            var preflightResult = await PreflightCheck.RunAsync(api!, ct);
            RenderPreflight(preflightResult);
            if (!preflightResult.CanGoLive) return ExitCodes.ConfigError;

            // --- Step 5: Import ---
            ct.ThrowIfCancellationRequested();
            ImportOutcome result;
            using (var spinner = ConsoleOutput.StartSpinner($"Importing {batch.Rows.Count} jobs for {dispatchDate}"))
            {
                result = await pipeline!.ImportAsync(batch,
                    onProgress: (sent, total) =>
                        spinner.Update($"Importing jobs for {dispatchDate} ({sent}/{total})"),
                    ct);
            }
            var exitCode = RenderResult(result);
            PopulateImportJson(json, result, stopwatch);
            if (exitCode != ExitCodes.Success) return exitCode;

            // --- Step 6: Optimize ---
            // Post-import steps (optimize, export) are best-effort — the import
            // already succeeded, so failures here should warn, not erase that success.
            if (optimize)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    ImportOutcome.OptimizationComplete optResult;
                    using (ConsoleOutput.StartSpinner("Optimizing dispatch"))
                    {
                        optResult = await pipeline.OptimizeAsync(dispatchDate, ct);
                    }
                    ConsoleOutput.Success($"Grade: {optResult.Grade ?? "N/A"} " +
                        $"({optResult.PlanQuality ?? 0}/100)  |  Flow: {optResult.FlowScore ?? 0}/100");
                    ConsoleOutput.Success($"Assigned: {optResult.Assigned ?? 0}/{(optResult.Assigned ?? 0) + (optResult.Unassigned ?? 0)}  " +
                        $"|  Drive times: {optResult.DriveTimeSource ?? "N/A"}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // Connection lost or error — but optimization runs server-side regardless.
                    // The background worker continues even when the CLI disconnects.
                    ConsoleOutput.Warning("Lost connection during optimization, but your jobs were imported successfully.");
                    ConsoleOutput.Hint("Optimization is still running in the background — check the Klau dashboard for results.");
                }
            }

            // --- Step 7: Export ---
            if (exportPath is not null)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using (ConsoleOutput.StartSpinner("Exporting dispatch plan"))
                    {
                        await pipeline.ExportAsync(dispatchDate, exportPath, ct);
                    }
                    ConsoleOutput.Success($"Exported to {exportPath}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    ConsoleOutput.Warning($"Export failed: {ex.Message}");
                    ConsoleOutput.Hint("Jobs were imported successfully. Retry export from the Klau dashboard.");
                }
            }

            stopwatch.Stop();
            ConsoleOutput.Summary($"Done in {stopwatch.Elapsed.TotalSeconds:F1}s");
            return ExitCodes.Success;
        }
        catch (FileNotFoundException ex)
        {
            ConsoleOutput.Error($"File not found: {ex.FileName ?? file.FullName}");
            ConsoleOutput.Hint("Check the file path and try again.");
            json?.SetError("FILE_NOT_FOUND", $"File not found: {ex.FileName ?? file.FullName}");
            return ExitCodes.InputError;
        }
        catch (NotSupportedException ex)
        {
            ConsoleOutput.Error(ex.Message);
            ConsoleOutput.Hint($"Supported formats: {FileReader.SupportedFormatsText}");
            json?.SetError("UNSUPPORTED_FORMAT", ex.Message);
            return ExitCodes.InputError;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("limit"))
        {
            ConsoleOutput.Error(ex.Message);
            json?.SetError("LIMIT_EXCEEDED", ex.Message);
            return ExitCodes.InputError;
        }
        catch (FormatException ex)
        {
            ConsoleOutput.Error($"Invalid file format: {ex.Message}");
            ConsoleOutput.Hint("Ensure the file is a valid CSV or XLSX with a header row.");
            json?.SetError("INVALID_FORMAT", ex.Message, "Ensure the file is a valid CSV or XLSX with a header row.");
            return ExitCodes.InputError;
        }
        catch (UnauthorizedAccessException)
        {
            ConsoleOutput.Error($"Permission denied: {file.FullName}");
            ConsoleOutput.Hint("Check file permissions or run with appropriate access.");
            json?.SetError("PERMISSION_DENIED", $"Permission denied: {file.FullName}");
            return ExitCodes.InputError;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ConsoleOutput.Blank();
            ConsoleOutput.Error("Request timed out.");
            ConsoleOutput.Hint("Check your network connection and try again.");
            json?.SetError("TIMEOUT", "Request timed out");
            return ExitCodes.ApiError;
        }
        catch (OperationCanceledException)
        {
            ConsoleOutput.Blank();
            ConsoleOutput.Status("Cancelled.");
            return ExitCodes.Success;
        }
    }

    private static int RenderResult(ImportOutcome result) => result switch
    {
        ImportOutcome.Success s => RenderSuccess(s),
        ImportOutcome.PartialFailure pf => RenderPartialFailure(pf),
        ImportOutcome.ApiError ae => RenderApiError(ae),
        ImportOutcome.ConfigurationMissing cm => RenderConfigMissing(cm),
        ImportOutcome.InputError ie => RenderInputError(ie),
        _ => ExitCodes.Success,
    };

    private static int RenderSuccess(ImportOutcome.Success s)
    {
        ConsoleOutput.Success($"Imported: {s.Imported}  |  Skipped: {s.Skipped}");
        if (s.CustomersCreated > 0 || s.SitesCreated > 0)
            ConsoleOutput.Success($"Auto-created: {s.CustomersCreated} customers, {s.SitesCreated} sites");
        foreach (var e in s.Errors)
            ConsoleOutput.Warning(e);
        return ExitCodes.Success;
    }

    private static int RenderPartialFailure(ImportOutcome.PartialFailure pf)
    {
        ConsoleOutput.Warning($"Partial: {pf.Imported} imported, {pf.Skipped} skipped");
        foreach (var e in pf.Errors)
            ConsoleOutput.Warning(e);
        return ExitCodes.PartialFailure;
    }

    private static int RenderApiError(ImportOutcome.ApiError ae)
    {
        ConsoleOutput.Error($"{ae.Code}: {ae.Message}");
        if (ae.Hint is not null)
            ConsoleOutput.Hint(ae.Hint);
        return ExitCodes.ApiError;
    }

    private static int RenderConfigMissing(ImportOutcome.ConfigurationMissing cm)
    {
        ConsoleOutput.Error(cm.What);
        ConsoleOutput.Hint(cm.Hint);
        return ExitCodes.ConfigError;
    }

    private static int RenderInputError(ImportOutcome.InputError ie)
    {
        ConsoleOutput.Error(ie.Message);
        ConsoleOutput.Hint(ie.Hint);
        return ExitCodes.InputError;
    }

    private static void RenderPreflight(PreflightResult result)
    {
        ConsoleOutput.Header("Pre-flight readiness check:");

        if (result.Issues.Count == 0 && result.CanGoLive)
        {
            ConsoleOutput.Success($"Account ready ({result.ReadyPercentage}% configured)");
            return;
        }

        if (result.ReadyPercentage >= 0)
        {
            ConsoleOutput.Warning($"Account {result.ReadyPercentage}% configured — some items need attention:");
            ConsoleOutput.Blank();
        }

        foreach (var issue in result.Issues)
        {
            if (issue.Required)
            {
                ConsoleOutput.Error($"{issue.Label}: {issue.Detail ?? "not configured"}");
                ConsoleOutput.Hint(issue.Hint);
            }
            else
            {
                ConsoleOutput.Warning($"{issue.Label}: {issue.Detail ?? "not configured"}");
                ConsoleOutput.Hint(issue.Hint);
            }
        }

        if (!result.CanGoLive)
        {
            ConsoleOutput.Blank();
            ConsoleOutput.Error("Blocking issues must be resolved before import.");
            ConsoleOutput.Hint("Set up your fleet in the Klau dashboard or via the SDK, then retry.");
        }
        else if (result.Issues.Any(i => !i.Required))
        {
            ConsoleOutput.Blank();
            ConsoleOutput.Warning("Non-blocking issues above may affect optimization quality.");
        }
    }

    // ── Feature: First-run auth ─────────────────────────────────────────────

    /// <summary>
    /// When no API key is found and the terminal is interactive, walk the user
    /// through inline authentication instead of showing a dead-end error.
    /// Returns the API key on success, or null if auth failed or not interactive.
    /// </summary>
    private static async Task<string?> TryFirstRunAuthAsync()
    {
        if (OutputMode.IsNonInteractive)
        {
            // Non-interactive (piped input, CI, etc.) — fall back to the original error
            ConsoleOutput.Error("No API key found.");
            ConsoleOutput.Hint("Run: klau login");
            return null;
        }

        ConsoleOutput.Blank();
        ConsoleOutput.Status("No credentials found. Let's get you set up.");
        ConsoleOutput.Blank();
        ConsoleOutput.Status("How would you like to authenticate?");
        ConsoleOutput.Status("  1. Log in with email and password (creates an API key automatically)");
        ConsoleOutput.Status("  2. Paste an API key from Settings > Developer in the Klau dashboard");
        ConsoleOutput.Blank();
        Console.Write("  Choice (1 or 2): ");

        var choice = Console.ReadLine()?.Trim();
        ConsoleOutput.Blank();

        switch (choice)
        {
            case "1":
                return await InteractiveAuth.InteractiveLoginAsync();

            case "2":
                return InteractiveAuth.PromptForApiKey();

            default:
                ConsoleOutput.Error("Invalid choice.");
                ConsoleOutput.Hint("Run: klau login");
                return null;
        }
    }

    // ── Feature: Interactive file selection ──────────────────────────────────

    /// <summary>
    /// When no file argument is provided and the terminal is interactive,
    /// scan the current directory for importable files and let the user pick.
    /// Returns the selected FileInfo, or null if none found or not interactive.
    /// </summary>
    private static FileInfo? TryResolveFile()
    {
        if (OutputMode.IsNonInteractive)
        {
            ConsoleOutput.Error("No file specified.");
            ConsoleOutput.Hint("Usage: klau import <file.csv>");
            return null;
        }

        var cwd = Directory.GetCurrentDirectory();
        var candidates = Directory.GetFiles(cwd)
            .Where(f => FileReader.IsSupported(f))
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .Take(9) // Cap at 9 for single-digit selection
            .ToList();

        if (candidates.Count == 0)
        {
            ConsoleOutput.Error("No file specified and no importable files found in the current directory.");
            ConsoleOutput.Hint("Usage: klau import <file.csv>");
            ConsoleOutput.Hint($"Supported formats: {FileReader.SupportedFormatsText}");
            return null;
        }

        ConsoleOutput.Blank();
        ConsoleOutput.Status("No file specified. Found importable files in current directory:");
        ConsoleOutput.Blank();

        for (var i = 0; i < candidates.Count; i++)
        {
            var f = candidates[i];
            var sizeText = FormatFileSize(f.Length);
            var ageText = FormatFileAge(f.LastWriteTime);
            ConsoleOutput.Status($"  {i + 1}. {f.Name} ({sizeText}, modified {ageText})");
        }

        ConsoleOutput.Blank();
        Console.Write($"  Which file to import? (1-{candidates.Count}): ");
        var input = Console.ReadLine()?.Trim();

        if (!int.TryParse(input, out var selection) || selection < 1 || selection > candidates.Count)
        {
            ConsoleOutput.Error("Invalid selection.");
            return null;
        }

        var selected = candidates[selection - 1];
        ConsoleOutput.Blank();
        ConsoleOutput.Status($"Selected: {selected.Name}");
        return selected;
    }

    // ── Feature: Low-confidence mapping confirmation ────────────────────────

    /// <summary>
    /// After column mapping, if any match has confidence below 0.5 and the
    /// terminal is interactive, prompt the user to confirm or reassign.
    /// Returns the (possibly modified) mapping.
    /// </summary>
    private static ColumnMapping ConfirmLowConfidenceMappings(ColumnMapping mapping)
    {
        if (OutputMode.IsNonInteractive)
            return mapping;

        var lowConfidence = mapping.Matches
            .Where(m => m.Confidence < 0.5)
            .ToList();

        if (lowConfidence.Count == 0)
            return mapping;

        ConsoleOutput.Blank();
        ConsoleOutput.Warning("Low confidence mappings detected:");
        foreach (var match in lowConfidence)
        {
            var pct = (int)(match.Confidence * 100);
            ConsoleOutput.Warning($"  \"{match.CsvHeader}\" -> {match.KlauField} ({pct}% confidence)");
        }

        ConsoleOutput.Blank();
        Console.Write("  Accept these mappings? (y/n): ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();

        if (answer is "y" or "yes" or "")
            return mapping;

        // Let the user remap each low-confidence column
        var updatedMatches = mapping.Matches.ToList();
        var updatedUnmapped = mapping.UnmappedHeaders.ToList();
        var usedFields = updatedMatches
            .Where(m => m.Confidence >= 0.5)
            .Select(m => m.KlauField)
            .ToHashSet();

        foreach (var match in lowConfidence)
        {
            var availableFields = KlauFields
                .Where(f => !usedFields.Contains(f))
                .ToList();

            ConsoleOutput.Blank();
            ConsoleOutput.Status($"  \"{match.CsvHeader}\" is currently mapped to {match.KlauField}.");
            ConsoleOutput.Status("  Available fields:");

            for (var i = 0; i < availableFields.Count; i++)
                ConsoleOutput.Status($"    {i + 1}. {availableFields[i]}");

            ConsoleOutput.Status($"    0. Skip (leave unmapped)");
            ConsoleOutput.Blank();
            Console.Write($"  Map \"{match.CsvHeader}\" to (0-{availableFields.Count}): ");
            var fieldInput = Console.ReadLine()?.Trim();

            updatedMatches.Remove(match);

            if (int.TryParse(fieldInput, out var fieldChoice) && fieldChoice >= 1 && fieldChoice <= availableFields.Count)
            {
                var newField = availableFields[fieldChoice - 1];
                updatedMatches.Add(new ColumnMatch(match.CsvHeader, newField, 1.0));
                usedFields.Add(newField);
            }
            else
            {
                // User chose 0 or invalid — move to unmapped
                updatedUnmapped.Add(match.CsvHeader);
            }
        }

        return new ColumnMapping(updatedMatches, updatedUnmapped);
    }

    // ── File reading ────────────────────────────────────────────────────────

    private static (SpreadsheetData, ColumnMapping) ReadAndMapStandalone(string filePath, string? mappingPath)
    {
        var data = FileReader.Read(filePath);
        var csvDir = Path.GetDirectoryName(filePath) ?? Directory.GetCurrentDirectory();

        ColumnMapping mapping;
        if (mappingPath is not null)
            mapping = MappingConfig.FromDictionary(MappingConfig.LoadFromFile(mappingPath), data.Headers);
        else if (MappingConfig.Exists(csvDir))
            mapping = MappingConfig.FromDictionary(MappingConfig.Load(csvDir), data.Headers);
        else
        {
            mapping = ColumnMapper.Map(data.Headers);
            MappingConfig.Save(csvDir, MappingConfig.ToDictionary(mapping));
        }

        return (data, mapping);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : (max > 3 ? value[..(max - 3)] + "..." : value[..max]);

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024} KB",
        _ => $"{bytes / (1024 * 1024.0):F1} MB",
    };

    private static string FormatFileAge(DateTime lastWrite)
    {
        var age = DateTime.Now - lastWrite;
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalDays < 1) return "today";
        if (age.TotalDays < 2) return "yesterday";
        return $"{(int)age.TotalDays}d ago";
    }

    // ── JSON output ────────────────────────────────────────────────────────

    private static void PopulateImportJson(
        CliJsonResponse? json, ImportOutcome result, Stopwatch stopwatch)
    {
        if (json is null) return;

        switch (result)
        {
            case ImportOutcome.Success s:
                json.Data["imported"] = s.Imported;
                json.Data["skipped"] = s.Skipped;
                json.Data["customersCreated"] = s.CustomersCreated;
                json.Data["sitesCreated"] = s.SitesCreated;
                json.Data["errors"] = s.Errors;
                json.Data["durationSeconds"] = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
                break;
            case ImportOutcome.PartialFailure pf:
                json.Data["imported"] = pf.Imported;
                json.Data["skipped"] = pf.Skipped;
                json.Data["errors"] = pf.Errors;
                json.Data["durationSeconds"] = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
                break;
            case ImportOutcome.ApiError ae:
                json.SetError(ae.Code, ae.Message, ae.Hint);
                break;
        }
    }
}
