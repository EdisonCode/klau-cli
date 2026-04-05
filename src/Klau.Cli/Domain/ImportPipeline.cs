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
    /// Maximum jobs per API request. Keeps each POST well within the HTTP
    /// timeout and avoids server-side sequential processing bottlenecks.
    /// </summary>
    private const int ChunkSize = 200;

    /// <summary>
    /// Import mapped rows into Klau via the SDK, chunking into batches of
    /// <see cref="ChunkSize"/> to stay within API and timeout limits.
    /// All chunks use JobsAsync (POST only). After the loop, readiness is
    /// polled once for the last batch to warm the drive-time cache.
    /// </summary>
    /// <param name="batch">Mapped rows to import.</param>
    /// <param name="onProgress">Optional callback invoked before each chunk with (rowsSentSoFar, totalRows).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ImportOutcome> ImportAsync(
        MappedBatch batch,
        Action<int, int>? onProgress,
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

        var chunks = records.Chunk(ChunkSize).ToList();
        var multiChunk = chunks.Count > 1;

        int totalImported = 0, totalSkipped = 0;
        int totalCustomersCreated = 0, totalSitesCreated = 0;
        var allErrors = new List<string>();
        int rowOffset = 0;
        var batchIds = new List<string>();

        // --- Phase 1: Import all chunks via JobsAsync (no readiness polling) ---
        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = chunks[i];
            var request = new ImportJobsRequest { Jobs = chunk, CreateMissing = true };

            if (multiChunk)
                onProgress?.Invoke(rowOffset, records.Count);

            ImportJobsResult result;
            try
            {
                result = await _client.Import.JobsAsync(request, ct);
            }
            catch (KlauApiException ex) when (totalImported > 0)
            {
                // A chunk failed after earlier chunks succeeded — report what
                // was imported so the user knows the system state.
                allErrors.Add($"Chunk {i + 1}/{chunks.Count} failed: {ex.Message}");
                return new ImportOutcome.PartialFailure(totalImported, totalSkipped, allErrors);
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

            totalImported += result.Imported;
            totalSkipped += result.Skipped;
            totalCustomersCreated += result.CustomersCreated;
            totalSitesCreated += result.SitesCreated;

            if (!string.IsNullOrEmpty(result.BatchId))
                batchIds.Add(result.BatchId);

            foreach (var e in result.Errors)
                allErrors.Add($"Row {e.Row + rowOffset}: {e.Field} - {e.Message}");

            rowOffset += chunk.Length;
        }

        // --- Phase 2: Wait for drive-time cache warm-up (all batches) ---
        if (batchIds.Count > 0)
        {
            var allReady = false;
            try
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
                while (DateTime.UtcNow < deadline)
                {
                    var pending = false;
                    foreach (var batchId in batchIds)
                    {
                        var readiness = await _client.Import.GetReadinessAsync(batchId, ct);
                        if (readiness.Status is not ("ready" or "not_applicable"))
                        {
                            pending = true;
                            break;
                        }
                    }

                    if (!pending)
                    {
                        allReady = true;
                        break;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
            catch (KlauApiException)
            {
                // API error polling readiness — jobs are imported, cache status unknown.
            }
            catch (HttpRequestException)
            {
                // Network error polling readiness — jobs are imported, cache status unknown.
            }

            if (!allReady)
            {
                allErrors.Add("Drive-time cache warm-up timed out. " +
                    "Optimization may use Haversine estimates for new sites.");
            }
        }

        if (totalSkipped > 0 && totalImported == 0)
            return new ImportOutcome.PartialFailure(totalImported, totalSkipped, allErrors);

        // If more than half the rows were skipped, that's a partial failure
        // even though some imported — the user needs to know something is wrong.
        if (totalSkipped > 0 && totalSkipped > totalImported)
            return new ImportOutcome.PartialFailure(totalImported, totalSkipped, allErrors);

        return new ImportOutcome.Success(
            totalImported, totalSkipped,
            totalCustomersCreated, totalSitesCreated, allErrors);
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
