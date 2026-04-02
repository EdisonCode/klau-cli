using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using Klau.Cli.Import;
using Klau.Cli.Output;
using Klau.Sdk;

namespace Klau.Cli.Commands;

/// <summary>
/// The main import command: reads a CSV, maps columns, imports jobs into Klau,
/// optionally optimizes dispatch, and optionally exports the dispatch plan.
/// </summary>
public static class ImportCommand
{
    public static Command Create()
    {
        var fileArg = new Argument<FileInfo>("file", "Path to the CSV file to import");
        var dateOption = new Option<string?>("--date", "Dispatch date (YYYY-MM-DD). Defaults to today.");
        var mappingOption = new Option<string?>("--mapping", "Path to a .klau-mapping.json file. Auto-detected by default.");
        var optimizeOption = new Option<bool>("--optimize", "Run dispatch optimization after import.");
        var exportOption = new Option<string?>("--export", "Export the dispatch plan to a CSV file at the given path.");

        var command = new Command("import", "Import a CSV file of jobs into Klau.")
        {
            fileArg,
            dateOption,
            mappingOption,
            optimizeOption,
            exportOption,
        };

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var file = ctx.ParseResult.GetValueForArgument(fileArg);
            var date = ctx.ParseResult.GetValueForOption(dateOption);
            var mappingPath = ctx.ParseResult.GetValueForOption(mappingOption);
            var optimize = ctx.ParseResult.GetValueForOption(optimizeOption);
            var exportPath = ctx.ParseResult.GetValueForOption(exportOption);
            var apiKey = ctx.ParseResult.GetValueForOption(Program.ApiKeyOption);

            var ct = ctx.GetCancellationToken();

            await RunAsync(file, date, mappingPath, optimize, exportPath, apiKey, ct);
        });

        // Add watch subcommand
        command.AddCommand(WatchCommand.Create());

        return command;
    }

    internal static Task RunAsync(
        FileInfo file,
        string? date,
        string? mappingPath,
        bool optimize,
        string? exportPath,
        string? apiKey,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        // Resolve API key
        var resolvedKey = apiKey ?? Environment.GetEnvironmentVariable("KLAU_API_KEY");
        if (string.IsNullOrWhiteSpace(resolvedKey))
        {
            ConsoleOutput.Error("No API key provided. Set KLAU_API_KEY or use --api-key.");
            return Task.CompletedTask;
        }

        // Resolve date
        var dispatchDate = date ?? DateTime.Today.ToString("yyyy-MM-dd");

        // Step 1: Read CSV
        ConsoleOutput.Blank();
        CsvData csvData;
        try
        {
            csvData = CsvReader.Read(file.FullName);
            ConsoleOutput.Status($"Reading {file.Name}... {csvData.Rows.Count} rows");
        }
        catch (Exception ex)
        {
            ConsoleOutput.Error($"Failed to read CSV: {ex.Message}");
            return Task.CompletedTask;
        }

        // Step 2: Load or infer column mapping
        var csvDir = file.DirectoryName ?? Directory.GetCurrentDirectory();
        ColumnMapping mapping;

        if (mappingPath is not null)
        {
            // Explicit mapping file
            try
            {
                var dict = MappingConfig.Load(Path.GetDirectoryName(mappingPath) ?? csvDir);
                mapping = MappingConfig.FromDictionary(dict, csvData.Headers);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Error($"Failed to load mapping: {ex.Message}");
                return Task.CompletedTask;
            }
        }
        else if (MappingConfig.Exists(csvDir))
        {
            // Auto-detected mapping file
            var dict = MappingConfig.Load(csvDir);
            mapping = MappingConfig.FromDictionary(dict, csvData.Headers);
            ConsoleOutput.Status($"Using existing mapping from {MappingConfig.FileName}");
        }
        else
        {
            // Infer mapping
            mapping = ColumnMapper.Map(csvData.Headers);
        }

        // Display mapping
        ConsoleOutput.Header("Column mapping (inferred):");
        var maxHeaderLen = mapping.Matches.Count > 0
            ? mapping.Matches.Max(m => m.CsvHeader.Length)
            : 10;

        foreach (var match in mapping.Matches)
            ConsoleOutput.Mapping(match.CsvHeader, match.KlauField, maxHeaderLen + 2);

        if (mapping.UnmappedHeaders.Count > 0)
        {
            ConsoleOutput.Blank();
            foreach (var h in mapping.UnmappedHeaders)
                ConsoleOutput.Warning($"Unmapped column: \"{h}\"");
        }

        // Save mapping if it was inferred
        if (mappingPath is null && !MappingConfig.Exists(csvDir))
        {
            var dict = MappingConfig.ToDictionary(mapping);
            MappingConfig.Save(csvDir, dict);
            ConsoleOutput.Blank();
            ConsoleOutput.Status($"Saved mapping to {MappingConfig.FileName}");
        }

        // Step 3: Map rows to import records
        var records = MapRowsToRecords(csvData, mapping, dispatchDate);

        // Step 4: Show preview (first 5 rows)
        if (records.MappedRows.Count > 0)
        {
            ConsoleOutput.Header("Preview (first 5 rows):");
            var previewHeaders = new[] { "Customer", "Address", "City", "Type", "ExternalId" };
            var previewRows = records.MappedRows
                .Take(5)
                .Select(r => new[]
                {
                    Truncate(r.GetValueOrDefault("CustomerName", ""), 20),
                    Truncate(r.GetValueOrDefault("SiteAddress", ""), 25),
                    Truncate(r.GetValueOrDefault("SiteCity", ""), 15),
                    r.GetValueOrDefault("JobType", ""),
                    Truncate(r.GetValueOrDefault("ExternalId", ""), 15),
                })
                .ToList();
            ConsoleOutput.Table(previewHeaders, previewRows);
        }

        // Step 5: Import via SDK
        ConsoleOutput.Header($"Importing {records.MappedRows.Count} jobs for {dispatchDate}...");

        try
        {
            var client = new KlauClient(resolvedKey);

            // Note: The actual SDK import call would be something like:
            // var result = await client.Import.ImportAndWaitAsync(records, ct);
            // For now we show what the flow would look like.
            ConsoleOutput.Success($"Ready to import {records.MappedRows.Count} jobs (SDK call placeholder)");

            if (records.Warnings.Count > 0)
            {
                foreach (var warning in records.Warnings)
                    ConsoleOutput.Warning(warning);
            }

            // Step 6: Optimize
            if (optimize)
            {
                ConsoleOutput.Header("Optimizing dispatch...");
                // var optimization = await client.Dispatches.OptimizeAndWaitAsync(..., ct);
                ConsoleOutput.Success("Optimization ready (SDK call placeholder)");
            }

            // Step 7: Export
            if (exportPath is not null)
            {
                ConsoleOutput.Header($"Exporting dispatch plan to {exportPath}...");
                // Write dispatch plan CSV
                ConsoleOutput.Success($"Export ready (SDK call placeholder)");
            }
        }
        catch (Exception ex)
        {
            ConsoleOutput.Error($"Import failed: {ex.Message}");
            return Task.CompletedTask;
        }

        stopwatch.Stop();
        ConsoleOutput.Summary($"Done in {stopwatch.Elapsed.TotalSeconds:F1}s");

        return Task.CompletedTask;
    }

    private static MappedRecords MapRowsToRecords(CsvData csv, ColumnMapping mapping, string dispatchDate)
    {
        var rows = new List<Dictionary<string, string>>();
        var warnings = new List<string>();

        // Build header index lookup: csvHeaderIndex -> klauField
        var headerMap = new Dictionary<int, string>();
        foreach (var match in mapping.Matches)
        {
            var idx = Array.IndexOf(csv.Headers, match.CsvHeader);
            if (idx >= 0)
                headerMap[idx] = match.KlauField;
        }

        for (var rowIndex = 0; rowIndex < csv.Rows.Count; rowIndex++)
        {
            var row = csv.Rows[rowIndex];
            var record = new Dictionary<string, string>();

            foreach (var (colIdx, field) in headerMap)
            {
                var value = colIdx < row.Length ? row[colIdx] : "";
                if (!string.IsNullOrWhiteSpace(value))
                    record[field] = value;
            }

            // Validate required fields
            if (!record.ContainsKey("CustomerName") || string.IsNullOrWhiteSpace(record.GetValueOrDefault("CustomerName")))
            {
                warnings.Add($"Row {rowIndex + 2}: missing customer name");
                continue;
            }

            // Add dispatch date if not in the data
            if (!record.ContainsKey("RequestedDate"))
                record["RequestedDate"] = dispatchDate;

            rows.Add(record);
        }

        return new MappedRecords(rows, warnings);
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        return maxLength > 3 ? value[..(maxLength - 3)] + "..." : value[..maxLength];
    }

    internal sealed record MappedRecords(List<Dictionary<string, string>> MappedRows, List<string> Warnings);
}
