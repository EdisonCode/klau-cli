using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
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
    public static Command Create()
    {
        var fileArg = new Argument<FileInfo>("file", "Path to the file to import (CSV, TSV, or XLSX)");
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
            var ct = ctx.GetCancellationToken();

            ctx.ExitCode = await RunAsync(
                file, date, mappingPath, optimize, exportPath, dryRun, apiKey, ct);
        });

        command.AddCommand(WatchCommand.Create());
        return command;
    }

    internal static async Task<int> RunAsync(
        FileInfo file,
        string? date,
        string? mappingPath,
        bool optimize,
        string? exportPath,
        bool dryRun,
        string? apiKey,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        // --- Validate config ---
        var resolvedKey = apiKey ?? Environment.GetEnvironmentVariable("KLAU_API_KEY");
        if (!dryRun && string.IsNullOrWhiteSpace(resolvedKey))
        {
            ConsoleOutput.Error("No API key provided.");
            ConsoleOutput.Hint("Set KLAU_API_KEY environment variable or use --api-key.");
            return ExitCodes.ConfigError;
        }

        // --- Validate date ---
        string dispatchDate;
        if (date is not null)
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out _))
            {
                ConsoleOutput.Error($"Invalid date format: \"{date}\".");
                ConsoleOutput.Hint("Expected format: YYYY-MM-DD (e.g. 2026-04-03).");
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
            using var client = dryRun ? null : new KlauClient(resolvedKey!);
            var pipeline = client is not null ? new ImportPipeline(client) : null;

            (data, mapping) = pipeline is not null
                ? pipeline.ReadAndMap(file.FullName, mappingPath)
                : ReadAndMapStandalone(file.FullName, mappingPath);

            ConsoleOutput.Status($"Reading {file.Name}... {data.Rows.Count} rows ({data.SourceFormat})");

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
                    $"{batch.Warnings.Count} warnings. No data sent to Klau.");
                return ExitCodes.Success;
            }

            // --- Step 3: Import ---
            ct.ThrowIfCancellationRequested();
            ConsoleOutput.Header($"Importing {batch.Rows.Count} jobs for {dispatchDate}...");

            var result = await pipeline!.ImportAsync(batch, dispatchDate, ct);
            var exitCode = RenderResult(result);
            if (exitCode != ExitCodes.Success) return exitCode;

            // --- Step 4: Optimize ---
            if (optimize)
            {
                ct.ThrowIfCancellationRequested();
                ConsoleOutput.Header("Optimizing dispatch...");
                var optResult = await pipeline.OptimizeAsync(dispatchDate, ct);
                ConsoleOutput.Success($"Grade: {optResult.Grade ?? "N/A"} " +
                    $"({optResult.PlanQuality ?? 0}/100)  |  Flow: {optResult.FlowScore ?? 0}/100");
                ConsoleOutput.Success($"Assigned: {optResult.Assigned ?? 0}/{(optResult.Assigned ?? 0) + (optResult.Unassigned ?? 0)}  " +
                    $"|  Drive times: {optResult.DriveTimeSource ?? "N/A"}");
            }

            // --- Step 5: Export ---
            if (exportPath is not null)
            {
                ct.ThrowIfCancellationRequested();
                ConsoleOutput.Header($"Exporting dispatch plan...");
                await pipeline.ExportAsync(dispatchDate, exportPath, ct);
                ConsoleOutput.Success($"Exported to {exportPath}");
            }

            stopwatch.Stop();
            ConsoleOutput.Summary($"Done in {stopwatch.Elapsed.TotalSeconds:F1}s");
            return ExitCodes.Success;
        }
        catch (FileNotFoundException ex)
        {
            ConsoleOutput.Error($"File not found: {ex.FileName ?? file.FullName}");
            ConsoleOutput.Hint("Check the file path and try again.");
            return ExitCodes.InputError;
        }
        catch (NotSupportedException ex)
        {
            ConsoleOutput.Error(ex.Message);
            ConsoleOutput.Hint($"Supported formats: {FileReader.SupportedFormatsText}");
            return ExitCodes.InputError;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("limit"))
        {
            ConsoleOutput.Error(ex.Message);
            return ExitCodes.InputError;
        }
        catch (FormatException ex)
        {
            ConsoleOutput.Error($"Invalid file format: {ex.Message}");
            ConsoleOutput.Hint("Ensure the file is a valid CSV or XLSX with a header row.");
            return ExitCodes.InputError;
        }
        catch (UnauthorizedAccessException)
        {
            ConsoleOutput.Error($"Permission denied: {file.FullName}");
            ConsoleOutput.Hint("Check file permissions or run with appropriate access.");
            return ExitCodes.InputError;
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
}
