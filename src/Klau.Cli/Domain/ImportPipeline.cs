using Klau.Cli.Import;
using Klau.Sdk;
using Klau.Sdk.Common;
using Klau.Sdk.Dispatches;
using Klau.Sdk.Import;

namespace Klau.Cli.Domain;

/// <summary>
/// Orchestrates the import pipeline: read file, map columns, import via SDK,
/// optionally optimize and export. Each step is a discrete concern.
/// </summary>
public sealed class ImportPipeline
{
    private readonly IKlauClient _client;

    public ImportPipeline(IKlauClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Read a spreadsheet file and map columns to Klau fields.
    /// </summary>
    public (SpreadsheetData Data, ColumnMapping Mapping) ReadAndMap(
        string filePath,
        string? explicitMappingPath)
    {
        var data = FileReader.Read(filePath);
        var csvDir = Path.GetDirectoryName(filePath) ?? Directory.GetCurrentDirectory();

        ColumnMapping mapping;
        if (explicitMappingPath is not null)
        {
            var dict = MappingConfig.LoadFromFile(explicitMappingPath);
            mapping = MappingConfig.FromDictionary(dict, data.Headers);
        }
        else if (MappingConfig.Exists(csvDir))
        {
            mapping = MappingConfig.FromDictionary(MappingConfig.Load(csvDir), data.Headers);
        }
        else
        {
            mapping = ColumnMapper.Map(data.Headers);
            MappingConfig.Save(csvDir, MappingConfig.ToDictionary(mapping));
        }

        return (data, mapping);
    }

    /// <summary>
    /// Transform spreadsheet rows into typed domain objects.
    /// </summary>
    public static MappedBatch MapRows(
        SpreadsheetData data,
        ColumnMapping mapping,
        string dispatchDate)
    {
        var rows = new List<JobImportRow>();
        var warnings = new List<RowWarning>();

        var headerMap = new Dictionary<int, string>();
        for (var i = 0; i < data.Headers.Count; i++)
        {
            var match = mapping.Matches.FirstOrDefault(m => m.CsvHeader == data.Headers[i]);
            if (match is not null)
                headerMap[i] = match.KlauField;
        }

        for (var rowIndex = 0; rowIndex < data.Rows.Count; rowIndex++)
        {
            var row = data.Rows[rowIndex];
            var fields = new Dictionary<string, string>();

            foreach (var (colIdx, field) in headerMap)
            {
                var value = colIdx < row.Count ? row[colIdx] : "";
                if (!string.IsNullOrWhiteSpace(value))
                    fields[field] = value.Trim();
            }

            if (!fields.TryGetValue("CustomerName", out var customerName) || string.IsNullOrWhiteSpace(customerName))
            {
                warnings.Add(new RowWarning(rowIndex + 2, "missing customer name, skipped"));
                continue;
            }

            rows.Add(new JobImportRow
            {
                CustomerName = customerName,
                SiteName = fields.GetValueOrDefault("SiteName"),
                SiteAddress = fields.GetValueOrDefault("SiteAddress"),
                SiteCity = fields.GetValueOrDefault("SiteCity"),
                SiteState = fields.GetValueOrDefault("SiteState"),
                SiteZip = fields.GetValueOrDefault("SiteZip"),
                JobType = fields.GetValueOrDefault("JobType"),
                ContainerSize = fields.GetValueOrDefault("ContainerSize"),
                TimeWindow = fields.GetValueOrDefault("TimeWindow"),
                Priority = fields.GetValueOrDefault("Priority"),
                Notes = fields.GetValueOrDefault("Notes"),
                RequestedDate = fields.GetValueOrDefault("RequestedDate") ?? dispatchDate,
                ExternalId = fields.GetValueOrDefault("ExternalId"),
            });
        }

        return new MappedBatch(rows, warnings);
    }

    /// <summary>
    /// Import mapped rows into Klau via the SDK.
    /// Chunks into batches of 200 for API limits.
    /// </summary>
    public async Task<ImportOutcome> ImportAsync(
        MappedBatch batch,
        string dispatchDate,
        CancellationToken ct)
    {
        var records = batch.Rows.Select(row => new ImportJobRecord
        {
            CustomerName = row.CustomerName,
            SiteName = row.SiteName ?? row.CustomerName,
            SiteAddress = row.SiteAddress ?? "",
            SiteCity = row.SiteCity,
            SiteState = row.SiteState,
            SiteZip = row.SiteZip,
            JobType = row.JobType ?? "DELIVERY",
            ContainerSize = row.ContainerSize ?? "30",
            TimeWindow = row.TimeWindow,
            Priority = row.Priority,
            Notes = row.Notes,
            RequestedDate = row.RequestedDate,
            ExternalId = row.ExternalId,
        }).ToList();

        try
        {
            var result = await _client.Import.ImportAndWaitAsync(
                new ImportJobsRequest { Jobs = records, CreateMissing = true },
                timeout: TimeSpan.FromSeconds(120),
                pollInterval: TimeSpan.FromSeconds(2),
                ct: ct);

            var errors = result.Errors.Select(e => $"Row {e.Row}: {e.Field} - {e.Message}").ToList();

            if (result.Skipped > 0 && result.Imported == 0)
                return new ImportOutcome.PartialFailure(result.Imported, result.Skipped, errors);

            return new ImportOutcome.Success(
                result.Imported, result.Skipped,
                result.CustomersCreated, result.SitesCreated, errors);
        }
        catch (KlauApiException ex) when (ex.IsUnauthorized)
        {
            return new ImportOutcome.ApiError(
                ex.ErrorCode ?? "UNAUTHORIZED", ex.Message,
                "Check that your KLAU_API_KEY is valid and not expired.");
        }
        catch (KlauApiException ex) when (ex.IsValidation)
        {
            return new ImportOutcome.ApiError(
                ex.ErrorCode ?? "VALIDATION_ERROR", ex.Message,
                "Check your CSV data for invalid values.");
        }
        catch (KlauApiException ex)
        {
            return new ImportOutcome.ApiError(
                ex.ErrorCode ?? "API_ERROR", ex.Message, null);
        }
    }

    /// <summary>
    /// Run dispatch optimization for the given date.
    /// </summary>
    public async Task<ImportOutcome.OptimizationComplete> OptimizeAsync(
        string dispatchDate,
        CancellationToken ct)
    {
        var optimization = await _client.Dispatches.OptimizeAndWaitAsync(
            new OptimizeRequest
            {
                Date = dispatchDate,
                OptimizationMode = OptimizationMode.FULL_DAY,
            },
            pollInterval: TimeSpan.FromSeconds(3),
            ct: ct);

        var r = optimization.Result;
        return new ImportOutcome.OptimizationComplete(
            r?.PlanGrade, r?.PlanQuality, r?.FlowScore,
            r?.AssignedJobs, r?.UnassignedJobs, r?.DriveTimeSource);
    }

    /// <summary>
    /// Export the dispatch plan for the given date to a CSV file.
    /// </summary>
    public async Task ExportAsync(string dispatchDate, string outputPath, CancellationToken ct)
    {
        var board = await _client.Dispatches.GetBoardAsync(dispatchDate, ct);

        var lines = new List<string>
        {
            "OrderNumber,Driver,Truck,Seq,Type,Customer,Size,ETA,DriveMinutes"
        };

        foreach (var driver in board.Drivers)
        foreach (var job in driver.Jobs.OrderBy(j => j.Sequence))
        {
            lines.Add(string.Join(",",
                Esc(job.ExternalId ?? ""), Esc(driver.Name), driver.TruckNumber ?? "",
                job.Sequence, job.Type, Esc(job.CustomerName),
                job.ContainerSize, job.EstimatedStartTime ?? "",
                job.DriveToMinutes));
        }

        foreach (var job in board.UnassignedJobs)
        {
            lines.Add(string.Join(",",
                Esc(job.ExternalId ?? ""), "UNASSIGNED", "", "", job.Type,
                Esc(job.CustomerName), job.ContainerSize, "", ""));
        }

        await File.WriteAllLinesAsync(outputPath, lines, ct);
    }

    private static string Esc(string v) =>
        v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r')
            ? $"\"{v.Replace("\"", "\"\"").Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ")}\""
            : v;
}
