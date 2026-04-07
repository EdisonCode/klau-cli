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
    /// Import mapped rows into Klau via the async import API. Submits all jobs
    /// in a single request, then polls for processing progress and drive-time
    /// cache readiness. No chunking needed — the server accepts the batch
    /// immediately (202) and processes in the background.
    /// </summary>
    /// <param name="batch">Mapped rows to import.</param>
    /// <param name="onProgress">Optional callback invoked on each poll with (processed, total).</param>
    /// <param name="onDriveTimeCacheWarming">Optional callback when import is done but cache is warming.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ImportOutcome> ImportAsync(
        MappedBatch batch,
        Action<int, int>? onProgress,
        Action? onDriveTimeCacheWarming,
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

        if (records.Count == 0)
            return new ImportOutcome.Success(0, 0, 0, 0, []);

        var request = new ImportJobsRequest { Jobs = records, CreateMissing = true };

        // --- Phase 1: Submit all jobs in one request (returns 202 immediately) ---
        AsyncImportSubmitResult submitResult;
        try
        {
            submitResult = await _client.Import.SubmitJobsAsync(request, ct);
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

        var batchId = submitResult.BatchId;

        // --- Phase 2: Poll for processing progress + drive-time cache ---
        var allErrors = new List<string>();
        var driveTimeCacheNotified = false;
        var consecutivePollFailures = 0;
        const int maxConsecutivePollFailures = 3;
        AsyncImportBatchStatus status;

        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
            while (true)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    allErrors.Add("Import polling timed out after 5 minutes. " +
                        "Jobs may still be processing — check the Klau dashboard.");
                    break;
                }

                ct.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(2), ct);

                AsyncImportBatchStatus pollResult;
                try
                {
                    pollResult = await _client.Import.GetBatchStatusAsync(batchId, ct);
                    consecutivePollFailures = 0;
                }
                catch (Exception ex) when (ex is KlauApiException or HttpRequestException)
                {
                    consecutivePollFailures++;
                    if (consecutivePollFailures >= maxConsecutivePollFailures)
                    {
                        var kind = ex is HttpRequestException ? "Network" : "API";
                        allErrors.Add($"{kind} error while polling import status " +
                            $"({consecutivePollFailures} consecutive failures). " +
                            "Jobs were accepted — check the Klau dashboard for results.");
                        return new ImportOutcome.PartialFailure(0, 0, allErrors);
                    }
                    continue; // Retry on next poll cycle
                }

                status = pollResult;
                onProgress?.Invoke(status.Processed, status.Total);

                // Notify caller when we transition to cache warming
                if (!driveTimeCacheNotified &&
                    status.IsTerminal &&
                    status.DriveTimeCacheStatus == DriveTimeCacheStatus.WARMING)
                {
                    driveTimeCacheNotified = true;
                    onDriveTimeCacheWarming?.Invoke();
                }

                // Fully done: terminal status AND cache ready/not needed
                if (status.IsReadyForOptimization)
                    return BuildOutcome(status, allErrors);

                // Terminal but cache still warming — keep polling
                if (status.IsTerminal &&
                    status.DriveTimeCacheStatus is not DriveTimeCacheStatus.WARMING
                                                  and not DriveTimeCacheStatus.NOT_STARTED)
                    return BuildOutcome(status, allErrors);
            }

            // Timed out — try one last status fetch for the result
            status = await _client.Import.GetBatchStatusAsync(batchId, ct);
            return BuildOutcome(status, allErrors);
        }
        catch (KlauApiException)
        {
            allErrors.Add("Lost connection while polling import status. " +
                "Jobs were accepted — check the Klau dashboard for results.");
            return new ImportOutcome.PartialFailure(0, 0, allErrors);
        }
        catch (HttpRequestException)
        {
            allErrors.Add("Network error while polling import status. " +
                "Jobs were accepted — check the Klau dashboard for results.");
            return new ImportOutcome.PartialFailure(0, 0, allErrors);
        }
    }

    private static ImportOutcome BuildOutcome(AsyncImportBatchStatus status, List<string> errors)
    {
        foreach (var e in status.Errors)
            errors.Add($"Row {e.Row}: {e.Field} - {e.Message}");

        if (status.DriveTimeCacheStatus == DriveTimeCacheStatus.WARMING)
        {
            errors.Add("Drive-time cache warm-up timed out. " +
                "Optimization may use Haversine estimates for new sites.");
        }

        if (status.Status == ImportBatchStatus.FAILED)
            return new ImportOutcome.ApiError("BATCH_FAILED",
                "The import batch failed during processing.", null);

        if (status.Skipped > 0 && status.Imported == 0)
            return new ImportOutcome.PartialFailure(status.Imported, status.Skipped, errors);

        if (status.Skipped > 0 && status.Skipped > status.Imported)
            return new ImportOutcome.PartialFailure(status.Imported, status.Skipped, errors);

        return new ImportOutcome.Success(
            status.Imported, status.Skipped,
            status.CustomersCreated, status.SitesCreated, errors);
    }

    /// <summary>
    /// Run dispatch optimization for the given date.
    /// Starts the job, then polls with a 3-minute deadline to avoid hanging
    /// if the optimization worker stalls.
    /// </summary>
    public async Task<ImportOutcome.OptimizationComplete> OptimizeAsync(
        string dispatchDate,
        CancellationToken ct)
    {
        var job = await _client.Dispatches.StartOptimizationAsync(
            new OptimizeRequest
            {
                Date = dispatchDate,
                OptimizationMode = OptimizationMode.FULL_DAY,
            }, ct);

        // Poll with a deadline — don't hang forever if the worker stalls
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (job.Status is OptimizationJobStatus.PENDING or OptimizationJobStatus.RUNNING)
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    "Optimization is still running but the CLI timed out after 3 minutes. " +
                    "Check the Klau dashboard for results.");

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            job = await _client.Dispatches.GetOptimizationStatusAsync(job.JobId, ct);
        }

        var r = job.Result;
        string? grade = r?.PlanGrade;
        int? quality = r?.PlanQuality;
        int? flow = r?.FlowScore;
        int? assigned = r?.AssignedJobs;
        int? unassigned = r?.UnassignedJobs;
        string? driveSource = r?.DriveTimeSource;

        // Fallback: if the poll response didn't include result, fetch board metrics
        if (grade is null && quality is null)
        {
            try
            {
                var board = await _client.Dispatches.GetBoardAsync(dispatchDate, ct);
                var m = board.Metrics;
                if (m is not null)
                {
                    grade ??= m.PlanGrade;
                    quality ??= m.PlanQuality;
                    flow ??= m.FlowScore;
                    assigned ??= m.AssignedJobs;
                    unassigned ??= m.UnassignedJobs;
                }
            }
            catch { /* board fetch is best-effort */ }
        }

        return new ImportOutcome.OptimizationComplete(
            grade, quality, flow, assigned, unassigned, driveSource);
    }

    /// <summary>
    /// Export the dispatch plan for the given date to a CSV file.
    /// </summary>
    public async Task ExportAsync(string dispatchDate, string outputPath, CancellationToken ct)
    {
        // Guard against path traversal — resolve to absolute and ensure it stays
        // within the current working directory.
        var resolvedPath = Path.GetFullPath(outputPath);
        var cwd = Path.GetFullPath(Directory.GetCurrentDirectory());
        if (!resolvedPath.StartsWith(cwd + Path.DirectorySeparatorChar) && resolvedPath != cwd)
            throw new InvalidOperationException(
                $"Export path must be within the current directory. Resolved to: {resolvedPath}");

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
