using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Mapping;
using AteraSnipeSync.Core.SnipeIt;
using AteraSnipeSync.Core.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace AteraSnipeSync.Tests.Sync;

/// <summary>
/// Verifies SyncOrchestrator stage ordering, short-circuit behavior, dry-run propagation, and result aggregation.
/// </summary>
public sealed class SyncOrchestratorTests
{
    [Fact]
    public async Task RunOnceAsync_CallsStagesInOrder_AndReturnsSuccessfulResult()
    {
        var calls = new List<string>();
        var pullResult = CreatePullResult();
        var importBatch = CreateImportBatch();
        var importResult = CreateImportResult();
        var orchestrator = CreateOrchestrator(
            new FakeAteraClient(calls) { Result = pullResult },
            new FakeInventoryMapper(calls) { Result = importBatch },
            new FakeSnipeImporter(calls) { Result = importResult },
            out var timeProvider);

        var result = await orchestrator.RunOnceAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(["pull", "map", "import"], calls);
        Assert.True(result.Success);
        Assert.Same(pullResult, result.PullResult);
        Assert.Same(importBatch, result.ImportBatch);
        Assert.Same(importResult, result.ImportResult);
        Assert.Empty(result.Failures);
        Assert.Equal(timeProvider.FirstTimestamp, result.StartedAt);
        Assert.Equal(timeProvider.SecondTimestamp, result.FinishedAt);
    }

    [Fact]
    public async Task RunOnceAsync_AggregatesWarnings_FromAllStages()
    {
        var calls = new List<string>();
        var orchestrator = CreateOrchestrator(
            new FakeAteraClient(calls)
            {
                Result = CreatePullResult(CreateWarning("AteraPull", "pull warning"))
            },
            new FakeInventoryMapper(calls)
            {
                Result = CreateImportBatch(CreateWarning("Reconstruction", "map warning"))
            },
            new FakeSnipeImporter(calls)
            {
                Result = CreateImportResult(warnings: [CreateWarning("SnipeImport", "import warning")])
            },
            out _);

        var result = await orchestrator.RunOnceAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(["pull warning", "map warning", "import warning"], result.Warnings.Select(warning => warning.Message));
    }

    [Fact]
    public async Task RunOnceAsync_StopsBeforeMappingAndImport_WhenPullFails()
    {
        var calls = new List<string>();
        var orchestrator = CreateOrchestrator(
            new FakeAteraClient(calls) { Exception = new InvalidOperationException("pull unavailable") },
            new FakeInventoryMapper(calls),
            new FakeSnipeImporter(calls),
            out _);

        var result = await orchestrator.RunOnceAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(["pull"], calls);
        Assert.False(result.Success);
        Assert.Null(result.PullResult);
        Assert.Null(result.ImportBatch);
        Assert.Null(result.ImportResult);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("AteraPull", failure.Stage);
        Assert.Equal("InvalidOperationException", failure.Code);
        Assert.Contains("pull unavailable", failure.Message);
    }

    [Fact]
    public async Task RunOnceAsync_StopsBeforeImport_WhenMappingFails()
    {
        var calls = new List<string>();
        var pullResult = CreatePullResult();
        var orchestrator = CreateOrchestrator(
            new FakeAteraClient(calls) { Result = pullResult },
            new FakeInventoryMapper(calls) { Exception = new InvalidOperationException("mapping failed") },
            new FakeSnipeImporter(calls),
            out _);

        var result = await orchestrator.RunOnceAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(["pull", "map"], calls);
        Assert.False(result.Success);
        Assert.Same(pullResult, result.PullResult);
        Assert.Null(result.ImportBatch);
        Assert.Null(result.ImportResult);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("Reconstruction", failure.Stage);
        Assert.Contains("mapping failed", failure.Message);
    }

    [Fact]
    public async Task RunOnceAsync_ReturnsFailureAndKeepsPriorResults_WhenImportThrows()
    {
        var calls = new List<string>();
        var pullResult = CreatePullResult();
        var importBatch = CreateImportBatch();
        var orchestrator = CreateOrchestrator(
            new FakeAteraClient(calls) { Result = pullResult },
            new FakeInventoryMapper(calls) { Result = importBatch },
            new FakeSnipeImporter(calls) { Exception = new InvalidOperationException("import failed") },
            out _);

        var result = await orchestrator.RunOnceAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(["pull", "map", "import"], calls);
        Assert.False(result.Success);
        Assert.Same(pullResult, result.PullResult);
        Assert.Same(importBatch, result.ImportBatch);
        Assert.Null(result.ImportResult);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("SnipeImport", failure.Stage);
        Assert.Contains("import failed", failure.Message);
    }

    [Fact]
    public async Task RunOnceAsync_ConvertsImportFailuresToRunFailures()
    {
        var calls = new List<string>();
        var importFailure = new ImportFailure
        {
            TargetType = "asset",
            TargetName = "LAPTOP-01",
            Message = "serial conflict",
            Code = "SnipeImport.SerialConflict"
        };
        var orchestrator = CreateOrchestrator(
            new FakeAteraClient(calls),
            new FakeInventoryMapper(calls),
            new FakeSnipeImporter(calls)
            {
                Result = CreateImportResult(failures: [importFailure], failedAssets: 1)
            },
            out _);

        var result = await orchestrator.RunOnceAsync(CreateRequest(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.ImportResult);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("SnipeImport", failure.Stage);
        Assert.Equal("SnipeImport.SerialConflict", failure.Code);
        Assert.Equal("asset 'LAPTOP-01' failed: serial conflict", failure.Message);
    }

    [Fact]
    public async Task RunOnceAsync_AppliesSyncDryRunToSnipeOptions()
    {
        var calls = new List<string>();
        var importer = new FakeSnipeImporter(calls);
        var orchestrator = CreateOrchestrator(
            new FakeAteraClient(calls),
            new FakeInventoryMapper(calls),
            importer,
            out _);
        var request = CreateRequest(syncDryRun: false, snipeDryRun: true);

        await orchestrator.RunOnceAsync(request, CancellationToken.None);

        Assert.NotNull(importer.LastOptions);
        Assert.False(importer.LastOptions.DryRun);
        Assert.Equal(request.SnipeIt.BaseUrl, importer.LastOptions.BaseUrl);
        Assert.Equal(request.SnipeIt.ApiToken, importer.LastOptions.ApiToken);
        Assert.Equal(request.SnipeIt.ManualPreflightCsvDirectory, importer.LastOptions.ManualPreflightCsvDirectory);
    }

    [Fact]
    public async Task RunOnceAsync_RethrowsOperationCanceledException()
    {
        var calls = new List<string>();
        var orchestrator = CreateOrchestrator(
            new FakeAteraClient(calls) { Exception = new OperationCanceledException("cancelled") },
            new FakeInventoryMapper(calls),
            new FakeSnipeImporter(calls),
            out _);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => orchestrator.RunOnceAsync(CreateRequest(), CancellationToken.None));

        Assert.Equal(["pull"], calls);
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_ForNullDependencies()
    {
        var calls = new List<string>();
        var ateraClient = new FakeAteraClient(calls);
        var inventoryMapper = new FakeInventoryMapper(calls);
        var snipeImporter = new FakeSnipeImporter(calls);
        var logger = NullLogger<SyncOrchestrator>.Instance;
        var timeProvider = new FixedStepTimeProvider();

        Assert.Throws<ArgumentNullException>(
            () => new SyncOrchestrator(null!, inventoryMapper, snipeImporter, logger, timeProvider));
        Assert.Throws<ArgumentNullException>(
            () => new SyncOrchestrator(ateraClient, null!, snipeImporter, logger, timeProvider));
        Assert.Throws<ArgumentNullException>(
            () => new SyncOrchestrator(ateraClient, inventoryMapper, null!, logger, timeProvider));
        Assert.Throws<ArgumentNullException>(
            () => new SyncOrchestrator(ateraClient, inventoryMapper, snipeImporter, null!, timeProvider));
        Assert.Throws<ArgumentNullException>(
            () => new SyncOrchestrator(ateraClient, inventoryMapper, snipeImporter, logger, null!));
    }

    [Fact]
    public async Task RunOnceAsync_ThrowsArgumentNullException_WhenRequestNull()
    {
        var orchestrator = CreateOrchestrator(
            new FakeAteraClient([]),
            new FakeInventoryMapper([]),
            new FakeSnipeImporter([]),
            out _);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => orchestrator.RunOnceAsync(null!, CancellationToken.None));
    }

    private static SyncOrchestrator CreateOrchestrator(
        FakeAteraClient ateraClient,
        FakeInventoryMapper inventoryMapper,
        FakeSnipeImporter snipeImporter,
        out FixedStepTimeProvider timeProvider)
    {
        timeProvider = new FixedStepTimeProvider();
        return new SyncOrchestrator(
            ateraClient,
            inventoryMapper,
            snipeImporter,
            NullLogger<SyncOrchestrator>.Instance,
            timeProvider);
    }

    private static SyncRunRequest CreateRequest(bool syncDryRun = true, bool snipeDryRun = true)
    {
        return new SyncRunRequest
        {
            Atera = new AteraPullRequest { ApiKey = "atera-key" },
            Mapping = new MappingOptions
            {
                DefaultCompanyName = "Acme",
                DefaultManufacturerName = "Dell",
                DefaultModelName = "Latitude",
                DefaultCategoryName = "Laptop",
                DefaultStatusId = 2
            },
            SnipeIt = new SnipeImportOptions
            {
                BaseUrl = "https://snipe.example.com/api/v1",
                ApiToken = "snipe-token",
                DryRun = snipeDryRun,
                CreateMissingCompanies = true,
                CreateMissingModels = true,
                MacAddressCustomFieldDbColumnName = "_snipeit_mac_address_5",
                NameMatchThreshold = 0.93,
                ManualPreflightCsvEnabled = true,
                ManualPreflightCsvDirectory = @"C:\ProgramData\AteraSnipeSync\Preflight\run-1"
            },
            Sync = new SyncRunOptions
            {
                DryRun = syncDryRun,
                TriggeredBy = "test"
            }
        };
    }

    private static AteraPullResult CreatePullResult(params ModuleWarning[] warnings)
    {
        return new AteraPullResult
        {
            Agents =
            [
                new AgentInfo
                {
                    AgentId = "agent-1",
                    Name = "LAPTOP-01",
                    RawJson = "{}"
                }
            ],
            Summary = new PullSummary
            {
                AgentCount = 1,
                PulledAt = new DateTimeOffset(2026, 6, 18, 10, 0, 0, TimeSpan.Zero)
            },
            Warnings = warnings
        };
    }

    private static SnipeImportBatch CreateImportBatch(params ModuleWarning[] warnings)
    {
        return new SnipeImportBatch
        {
            Assets =
            [
                new SnipeAssetImportRecord
                {
                    AssetTag = "ATERA-agent-1",
                    Name = "LAPTOP-01",
                    Serial = "ABC123",
                    CompanyName = "Acme",
                    ManufacturerName = "Dell",
                    ModelName = "Latitude",
                    CategoryName = "Laptop",
                    StatusId = 2,
                    Notes = "Imported from Atera",
                    SourceSystem = "Atera",
                    SourceId = "agent-1"
                }
            ],
            Summary = new MappingSummary
            {
                SourceAgentCount = 1,
                MappedAssetCount = 1
            },
            Warnings = warnings
        };
    }

    private static SnipeImportResult CreateImportResult(
        IReadOnlyList<ImportFailure>? failures = null,
        IReadOnlyList<ModuleWarning>? warnings = null,
        int failedAssets = 0)
    {
        return new SnipeImportResult
        {
            CreatedAssets = 1,
            UpdatedAssets = 0,
            SkippedAssets = 0,
            FailedAssets = failedAssets,
            CreatedCompanies = 0,
            CreatedModels = 0,
            DryRun = true,
            Actions = [],
            Failures = failures ?? [],
            Warnings = warnings ?? []
        };
    }

    private static ModuleWarning CreateWarning(string source, string message)
    {
        return new ModuleWarning
        {
            Source = source,
            Message = message,
            Code = $"{source}.Warning"
        };
    }

    /// <summary>
    /// Fake pull boundary that records calls and can return or throw without making HTTP requests.
    /// </summary>
    private sealed class FakeAteraClient(List<string> calls) : IAteraClient
    {
        public AteraPullResult Result { get; init; } = CreatePullResult();
        public Exception? Exception { get; init; }

        public Task<AteraPullResult> PullInventoryAsync(
            AteraPullRequest request,
            CancellationToken cancellationToken)
        {
            calls.Add("pull");

            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult(Result);
        }
    }

    /// <summary>
    /// Fake mapper boundary that records calls and can return or throw without using external systems.
    /// </summary>
    private sealed class FakeInventoryMapper(List<string> calls) : IInventoryMapper
    {
        public SnipeImportBatch Result { get; init; } = CreateImportBatch();
        public Exception? Exception { get; init; }

        public SnipeImportBatch Map(AteraPullResult source, MappingOptions options)
        {
            calls.Add("map");

            if (Exception is not null)
            {
                throw Exception;
            }

            return Result;
        }
    }

    /// <summary>
    /// Fake import boundary that records calls and captures options without making Snipe-IT requests.
    /// </summary>
    private sealed class FakeSnipeImporter(List<string> calls) : ISnipeImporter
    {
        public SnipeImportResult Result { get; init; } = CreateImportResult();
        public Exception? Exception { get; init; }
        public SnipeImportOptions? LastOptions { get; private set; }

        public Task<SnipeImportResult> ImportAsync(
            SnipeImportBatch batch,
            SnipeImportOptions options,
            CancellationToken cancellationToken)
        {
            calls.Add("import");
            LastOptions = options;

            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult(Result);
        }
    }

    /// <summary>
    /// Deterministic time provider for asserting orchestrator start and finish timestamps.
    /// </summary>
    private sealed class FixedStepTimeProvider : TimeProvider
    {
        public DateTimeOffset FirstTimestamp { get; } = new(2026, 6, 18, 10, 0, 0, TimeSpan.Zero);
        public DateTimeOffset SecondTimestamp { get; } = new(2026, 6, 18, 10, 0, 5, TimeSpan.Zero);

        private int _calls;

        public override DateTimeOffset GetUtcNow()
        {
            _calls++;
            return _calls == 1 ? FirstTimestamp : SecondTimestamp;
        }
    }
}
