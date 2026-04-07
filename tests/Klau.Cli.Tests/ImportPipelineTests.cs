using Klau.Cli.Domain;
using Klau.Cli.Import;
using Klau.Sdk;
using Klau.Sdk.Authentication;
using Klau.Sdk.Common;
using Klau.Sdk.Companies;
using Klau.Sdk.Customers;
using Klau.Sdk.Dispatches;
using Klau.Sdk.Divisions;
using Klau.Sdk.Drivers;
using Klau.Sdk.DumpSites;
using Klau.Sdk.DumpTickets;
using Klau.Sdk.Import;
using Klau.Sdk.Jobs;
using Klau.Sdk.Materials;
using Klau.Sdk.Orders;
using Klau.Sdk.Proposals;
using Klau.Sdk.Readiness;
using Klau.Sdk.Storefronts;
using Klau.Sdk.Trucks;
using Klau.Sdk.Webhooks;
using Klau.Sdk.Yards;
using Xunit;

namespace Klau.Cli.Tests;

public class ImportPipelineTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static MappedBatch MakeBatch(int count, string prefix = "MO")
    {
        var rows = Enumerable.Range(1, count).Select(i => new JobImportRow
        {
            CustomerName = $"Customer {i}",
            SiteAddress = $"{i} Main St",
            JobType = "DELIVERY",
            ContainerSize = "30",
            RequestedDate = "2026-04-07",
            ExternalId = $"{prefix}-{i:D4}",
        }).ToList();
        return new MappedBatch(rows, []);
    }

    private static AsyncImportBatchStatus MakeStatus(
        string batchId, ImportBatchStatus status, int total, int processed,
        int imported = 0, int skipped = 0,
        int customersCreated = 0, int sitesCreated = 0,
        DriveTimeCacheStatus driveTimeCacheStatus = DriveTimeCacheStatus.NOT_APPLICABLE,
        IReadOnlyList<ImportError>? errors = null) => new()
    {
        BatchId = batchId,
        Status = status,
        Total = total,
        Processed = processed,
        Imported = imported > 0 ? imported : processed - skipped,
        Skipped = skipped,
        CustomersCreated = customersCreated,
        SitesCreated = sitesCreated,
        DriveTimeCacheStatus = driveTimeCacheStatus,
        Errors = errors ?? [],
    };

    // ── Happy path: all imported ────────────────────────────────────────────

    [Fact]
    public async Task AllImported_ReturnsSuccess()
    {
        var import = new FakeImportClient();
        import.SetSubmitResult("b-1", 26);
        import.EnqueueStatus(MakeStatus("b-1", ImportBatchStatus.COMPLETED, 26, 26, imported: 26,
            customersCreated: 4, sitesCreated: 8));

        var pipeline = new ImportPipeline(new FakeKlauClient(import));
        var result = await pipeline.ImportAsync(MakeBatch(26), null, null, CancellationToken.None);

        var success = Assert.IsType<ImportOutcome.Success>(result);
        Assert.Equal(26, success.Imported);
        Assert.Equal(0, success.Skipped);
        Assert.Equal(4, success.CustomersCreated);
        Assert.Equal(8, success.SitesCreated);
    }

    // ── Progress callback fires on each poll ────────────────────────────────

    [Fact]
    public async Task ProgressCallback_FiresOnEachPoll()
    {
        var import = new FakeImportClient();
        import.SetSubmitResult("b-1", 10);
        import.EnqueueStatus(MakeStatus("b-1", ImportBatchStatus.PROCESSING, 10, 3));
        import.EnqueueStatus(MakeStatus("b-1", ImportBatchStatus.PROCESSING, 10, 7));
        import.EnqueueStatus(MakeStatus("b-1", ImportBatchStatus.COMPLETED, 10, 10, imported: 10));

        var pipeline = new ImportPipeline(new FakeKlauClient(import));
        var progressCalls = new List<(int processed, int total)>();
        var result = await pipeline.ImportAsync(
            MakeBatch(10),
            onProgress: (p, t) => progressCalls.Add((p, t)),
            onDriveTimeCacheWarming: null,
            CancellationToken.None);

        Assert.IsType<ImportOutcome.Success>(result);
        Assert.Equal(3, progressCalls.Count);
        Assert.Equal((3, 10), progressCalls[0]);
        Assert.Equal((7, 10), progressCalls[1]);
        Assert.Equal((10, 10), progressCalls[2]);
    }

    // ── Drive-time cache warming callback ───────────────────────────────────

    [Fact]
    public async Task CacheWarming_InvokesCallback()
    {
        var import = new FakeImportClient();
        import.SetSubmitResult("b-1", 5);
        import.EnqueueStatus(MakeStatus("b-1", ImportBatchStatus.COMPLETED, 5, 5, imported: 5,
            sitesCreated: 2, driveTimeCacheStatus: DriveTimeCacheStatus.WARMING));
        import.EnqueueStatus(MakeStatus("b-1", ImportBatchStatus.COMPLETED, 5, 5, imported: 5,
            sitesCreated: 2, driveTimeCacheStatus: DriveTimeCacheStatus.READY));

        var pipeline = new ImportPipeline(new FakeKlauClient(import));
        var cacheWarmingCalled = false;

        var result = await pipeline.ImportAsync(
            MakeBatch(5), null,
            onDriveTimeCacheWarming: () => cacheWarmingCalled = true,
            CancellationToken.None);

        Assert.True(cacheWarmingCalled);
        var success = Assert.IsType<ImportOutcome.Success>(result);
        Assert.Equal(5, success.Imported);
    }

    // ── Auth failure returns ApiError ────────────────────────────────────────

    [Fact]
    public async Task Unauthorized_ReturnsApiError()
    {
        var import = new FakeImportClient();
        import.ThrowOnSubmit(new KlauApiException("UNAUTHORIZED", "Bad token", 401));

        var pipeline = new ImportPipeline(new FakeKlauClient(import));
        var result = await pipeline.ImportAsync(MakeBatch(5), null, null, CancellationToken.None);

        var error = Assert.IsType<ImportOutcome.ApiError>(result);
        Assert.Equal("UNAUTHORIZED", error.Code);
        Assert.NotNull(error.Hint);
    }

    // ── Validation failure returns ApiError ──────────────────────────────────

    [Fact]
    public async Task ValidationError_ReturnsApiError()
    {
        var import = new FakeImportClient();
        import.ThrowOnSubmit(new KlauApiException("VALIDATION_ERROR", "Invalid job type", 400));

        var pipeline = new ImportPipeline(new FakeKlauClient(import));
        var result = await pipeline.ImportAsync(MakeBatch(5), null, null, CancellationToken.None);

        var error = Assert.IsType<ImportOutcome.ApiError>(result);
        Assert.Equal("VALIDATION_ERROR", error.Code);
    }

    // ── Partial failure: majority skipped ───────────────────────────────────

    [Fact]
    public async Task MajoritySkipped_ReturnsPartialFailure()
    {
        var import = new FakeImportClient();
        import.SetSubmitResult("b-1", 10);
        import.EnqueueStatus(MakeStatus("b-1", ImportBatchStatus.PARTIAL_FAILURE, 10, 10,
            imported: 2, skipped: 8));

        var pipeline = new ImportPipeline(new FakeKlauClient(import));
        var result = await pipeline.ImportAsync(MakeBatch(10), null, null, CancellationToken.None);

        Assert.IsType<ImportOutcome.PartialFailure>(result);
    }

    // ── FAILED batch returns ApiError ───────────────────────────────────────

    [Fact]
    public async Task FailedBatch_ReturnsApiError()
    {
        var import = new FakeImportClient();
        import.SetSubmitResult("b-1", 5);
        import.EnqueueStatus(MakeStatus("b-1", ImportBatchStatus.FAILED, 5, 0));

        var pipeline = new ImportPipeline(new FakeKlauClient(import));
        var result = await pipeline.ImportAsync(MakeBatch(5), null, null, CancellationToken.None);

        var error = Assert.IsType<ImportOutcome.ApiError>(result);
        Assert.Equal("BATCH_FAILED", error.Code);
    }

    // ── Error rows are included in result ───────────────────────────────────

    [Fact]
    public async Task ErrorRows_IncludedInResult()
    {
        var import = new FakeImportClient();
        import.SetSubmitResult("b-1", 10);
        import.EnqueueStatus(MakeStatus("b-1", ImportBatchStatus.COMPLETED, 10, 10,
            imported: 9, skipped: 1, errors: [
                new ImportError { Row = 7, Field = "externalId", Message = "Duplicate external ID" },
            ]));

        var pipeline = new ImportPipeline(new FakeKlauClient(import));
        var result = await pipeline.ImportAsync(MakeBatch(10), null, null, CancellationToken.None);

        var success = Assert.IsType<ImportOutcome.Success>(result);
        Assert.Single(success.Errors);
        Assert.Contains(success.Errors, e => e.Contains("Row 7") && e.Contains("Duplicate"));
    }

    // ── Empty batch returns immediate success ───────────────────────────────

    [Fact]
    public async Task EmptyBatch_ReturnsSuccessWithZeros()
    {
        var import = new FakeImportClient();
        var pipeline = new ImportPipeline(new FakeKlauClient(import));
        var batch = new MappedBatch([], []);

        var result = await pipeline.ImportAsync(batch, null, null, CancellationToken.None);

        var success = Assert.IsType<ImportOutcome.Success>(result);
        Assert.Equal(0, success.Imported);
        Assert.Equal(0, success.Skipped);
    }

    // ── Transient poll error retries, then recovers ───────────────────────

    [Fact]
    public async Task TransientPollError_RetriesThenRecovers()
    {
        var import = new FakeImportClient();
        import.SetSubmitResult("b-1", 5);
        // First poll fails transiently, second succeeds
        import.EnqueueStatusException(new HttpRequestException("Connection reset"));
        import.EnqueueStatus(MakeStatus("b-1", ImportBatchStatus.COMPLETED, 5, 5, imported: 5));

        var pipeline = new ImportPipeline(new FakeKlauClient(import));
        var result = await pipeline.ImportAsync(MakeBatch(5), null, null, CancellationToken.None);

        var success = Assert.IsType<ImportOutcome.Success>(result);
        Assert.Equal(5, success.Imported);
    }

    // ── 3 consecutive poll failures returns PartialFailure ──────────────────

    [Fact]
    public async Task ThreeConsecutivePollFailures_ReturnsPartialFailure()
    {
        var import = new FakeImportClient();
        import.SetSubmitResult("b-1", 5);
        import.EnqueueStatusException(new HttpRequestException("fail 1"));
        import.EnqueueStatusException(new HttpRequestException("fail 2"));
        import.EnqueueStatusException(new HttpRequestException("fail 3"));

        var pipeline = new ImportPipeline(new FakeKlauClient(import));
        var result = await pipeline.ImportAsync(MakeBatch(5), null, null, CancellationToken.None);

        var partial = Assert.IsType<ImportOutcome.PartialFailure>(result);
        Assert.Contains(partial.Errors, e => e.Contains("3 consecutive failures"));
    }

    // ── API error during polling also retries ───────────────────────────────

    [Fact]
    public async Task TransientApiPollError_RetriesThenRecovers()
    {
        var import = new FakeImportClient();
        import.SetSubmitResult("b-1", 5);
        import.EnqueueStatusException(new KlauApiException("SERVER_ERROR", "Internal error", 500));
        import.EnqueueStatusException(new KlauApiException("SERVER_ERROR", "Internal error", 500));
        import.EnqueueStatus(MakeStatus("b-1", ImportBatchStatus.COMPLETED, 5, 5, imported: 5));

        var pipeline = new ImportPipeline(new FakeKlauClient(import));
        var result = await pipeline.ImportAsync(MakeBatch(5), null, null, CancellationToken.None);

        var success = Assert.IsType<ImportOutcome.Success>(result);
        Assert.Equal(5, success.Imported);
    }

    // ── NOT_APPLICABLE cache skips warming ──────────────────────────────────

    [Fact]
    public async Task CacheNotApplicable_SkipsCacheWarming()
    {
        var import = new FakeImportClient();
        import.SetSubmitResult("b-1", 5);
        import.EnqueueStatus(MakeStatus("b-1", ImportBatchStatus.COMPLETED, 5, 5, imported: 5,
            driveTimeCacheStatus: DriveTimeCacheStatus.NOT_APPLICABLE));

        var pipeline = new ImportPipeline(new FakeKlauClient(import));
        var cacheWarmingCalled = false;
        var result = await pipeline.ImportAsync(
            MakeBatch(5), null,
            onDriveTimeCacheWarming: () => cacheWarmingCalled = true,
            CancellationToken.None);

        Assert.False(cacheWarmingCalled);
        Assert.IsType<ImportOutcome.Success>(result);
    }

    // ── Test doubles ────────────────────────────────────────────────────────

    private sealed class FakeImportClient : IImportClient
    {
        private AsyncImportSubmitResult? _submitResult;
        private Exception? _submitException;
        private readonly Queue<object> _statusQueue = new(); // AsyncImportBatchStatus or Exception

        public void SetSubmitResult(string batchId, int jobCount)
        {
            _submitResult = new AsyncImportSubmitResult
            {
                BatchId = batchId,
                JobCount = jobCount,
                Status = ImportBatchStatus.ACCEPTED,
            };
        }

        public void ThrowOnSubmit(Exception ex) => _submitException = ex;

        public void EnqueueStatus(AsyncImportBatchStatus status) => _statusQueue.Enqueue(status);

        public void EnqueueStatusException(Exception ex) => _statusQueue.Enqueue(ex);

        // --- New async methods (used by the pipeline) ---

        public Task<AsyncImportSubmitResult> SubmitJobsAsync(ImportJobsRequest request, CancellationToken ct)
        {
            if (_submitException is not null) throw _submitException;
            return Task.FromResult(_submitResult ?? new AsyncImportSubmitResult
            {
                BatchId = "default",
                JobCount = request.Jobs.Count,
                Status = ImportBatchStatus.ACCEPTED,
            });
        }

        public Task<AsyncImportSubmitResult> SubmitJobsAsync(ImportJobsRequest request,
            KlauRequestOptions options, CancellationToken ct)
            => SubmitJobsAsync(request, ct);

        public Task<AsyncImportBatchStatus> GetBatchStatusAsync(string batchId, CancellationToken ct)
        {
            if (_statusQueue.Count > 0)
            {
                var next = _statusQueue.Dequeue();
                if (next is Exception ex) throw ex;
                return Task.FromResult((AsyncImportBatchStatus)next);
            }
            // Default: completed
            return Task.FromResult(MakeStatus(batchId, ImportBatchStatus.COMPLETED, 0, 0));
        }

        // --- Legacy methods (not used by new pipeline, but required by interface) ---

        public Task<ImportJobsResult> JobsAsync(ImportJobsRequest request, CancellationToken ct)
            => throw new NotImplementedException("Pipeline should use SubmitJobsAsync");

        public Task<BatchReadiness> GetReadinessAsync(string batchId, CancellationToken ct)
            => throw new NotImplementedException("Pipeline should use GetBatchStatusAsync");

        public Task<ImportJobsResult> ImportAndWaitAsync(ImportJobsRequest request,
            TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken ct = default)
            => throw new NotImplementedException("Pipeline should use SubmitJobsAsync + GetBatchStatusAsync");

        public Task<AsyncImportBatchStatus> SubmitAndWaitAsync(ImportJobsRequest request,
            TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken ct = default)
            => throw new NotImplementedException("Pipeline should use SubmitJobsAsync + GetBatchStatusAsync");
    }

    private sealed class FakeKlauClient : IKlauClient
    {
        public FakeKlauClient(IImportClient import) => Import = import;

        public IImportClient Import { get; }

        // Unused — pipeline only touches Import
        public IAuthClient Auth => throw new NotImplementedException();
        public ICompanyClient Company => throw new NotImplementedException();
        public IJobClient Jobs => throw new NotImplementedException();
        public ICustomerClient Customers => throw new NotImplementedException();
        public IDispatchClient Dispatches => throw new NotImplementedException();
        public IStorefrontClient Storefronts => throw new NotImplementedException();
        public IMaterialClient Materials => throw new NotImplementedException();
        public IDumpTicketClient DumpTickets => throw new NotImplementedException();
        public IOrderClient Orders => throw new NotImplementedException();
        public IProposalClient Proposals => throw new NotImplementedException();
        public IDivisionClient Divisions => throw new NotImplementedException();
        public IWebhookClient Webhooks => throw new NotImplementedException();
        public IReadinessClient Readiness => throw new NotImplementedException();
        public IDriverClient Drivers => throw new NotImplementedException();
        public ITruckClient Trucks => throw new NotImplementedException();
        public IYardClient Yards => throw new NotImplementedException();
        public IDumpSiteClient DumpSites => throw new NotImplementedException();
    }
}
