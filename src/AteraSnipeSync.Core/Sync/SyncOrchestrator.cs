using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Mapping;
using AteraSnipeSync.Core.SnipeIt;
using Microsoft.Extensions.Logging;

namespace AteraSnipeSync.Core.Sync;

/// <summary>
/// Coordinates one full sync run by invoking pull, mapping, and import modules while preserving module boundaries.
/// </summary>
public sealed class SyncOrchestrator : ISyncOrchestrator
{
    private const string AteraPullStage = "AteraPull";
    private const string ReconstructionStage = "Reconstruction";
    private const string SnipeImportStage = "SnipeImport";

    private readonly IAteraClient _ateraClient;
    private readonly IInventoryMapper _inventoryMapper;
    private readonly ISnipeImporter _snipeImporter;
    private readonly ILogger<SyncOrchestrator> _logger;
    private readonly TimeProvider _timeProvider;

    public SyncOrchestrator(
        IAteraClient ateraClient,
        IInventoryMapper inventoryMapper,
        ISnipeImporter snipeImporter,
        ILogger<SyncOrchestrator> logger)
        : this(ateraClient, inventoryMapper, snipeImporter, logger, TimeProvider.System)
    {
    }

    public SyncOrchestrator(
        IAteraClient ateraClient,
        IInventoryMapper inventoryMapper,
        ISnipeImporter snipeImporter,
        ILogger<SyncOrchestrator> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(ateraClient);
        ArgumentNullException.ThrowIfNull(inventoryMapper);
        ArgumentNullException.ThrowIfNull(snipeImporter);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _ateraClient = ateraClient;
        _inventoryMapper = inventoryMapper;
        _snipeImporter = snipeImporter;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Runs the sync pipeline once, short-circuiting on stage exceptions and rethrowing cancellation.
    /// </summary>
    public async Task<SyncRunResult> RunOnceAsync(
        SyncRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startedAt = _timeProvider.GetUtcNow();
        var warnings = new List<ModuleWarning>();
        var failures = new List<SyncFailure>();
        AteraPullResult? pullResult = null;
        SnipeImportBatch? importBatch = null;
        SnipeImportResult? importResult = null;

        _logger.LogInformation(
            "Starting sync run triggered by {TriggeredBy}. DryRun: {DryRun}.",
            request.Sync.TriggeredBy,
            request.Sync.DryRun);

        try
        {
            pullResult = await _ateraClient
                .PullInventoryAsync(request.Atera, cancellationToken)
                .ConfigureAwait(false);
            warnings.AddRange(pullResult.Warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            failures.Add(ToStageFailure(AteraPullStage, exception));
            LogStageFailure(AteraPullStage, exception);
            return CreateResult(startedAt, pullResult, importBatch, importResult, warnings, failures);
        }

        try
        {
            importBatch = _inventoryMapper.Map(pullResult, request.Mapping);
            warnings.AddRange(importBatch.Warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            failures.Add(ToStageFailure(ReconstructionStage, exception));
            LogStageFailure(ReconstructionStage, exception);
            return CreateResult(startedAt, pullResult, importBatch, importResult, warnings, failures);
        }

        try
        {
            var snipeOptions = ApplySyncOptions(request.SnipeIt, request.Sync);
            importResult = await _snipeImporter
                .ImportAsync(importBatch, snipeOptions, cancellationToken)
                .ConfigureAwait(false);

            warnings.AddRange(importResult.Warnings);
            failures.AddRange(importResult.Failures.Select(ToRunFailure));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            failures.Add(ToStageFailure(SnipeImportStage, exception));
            LogStageFailure(SnipeImportStage, exception);
        }

        return CreateResult(startedAt, pullResult, importBatch, importResult, warnings, failures);
    }

    private SyncRunResult CreateResult(
        DateTimeOffset startedAt,
        AteraPullResult? pullResult,
        SnipeImportBatch? importBatch,
        SnipeImportResult? importResult,
        IReadOnlyList<ModuleWarning> warnings,
        IReadOnlyList<SyncFailure> failures)
    {
        var result = new SyncRunResult
        {
            Success = failures.Count == 0,
            StartedAt = startedAt,
            FinishedAt = _timeProvider.GetUtcNow(),
            PullResult = pullResult,
            ImportBatch = importBatch,
            ImportResult = importResult,
            Warnings = warnings.ToArray(),
            Failures = failures.ToArray()
        };

        _logger.LogInformation("Finished sync run. Success: {Success}.", result.Success);
        return result;
    }

    private static SnipeImportOptions ApplySyncOptions(
        SnipeImportOptions source,
        SyncRunOptions sync)
    {
        return new SnipeImportOptions
        {
            BaseUrl = source.BaseUrl,
            ApiToken = source.ApiToken,
            DryRun = sync.DryRun,
            CreateMissingCompanies = source.CreateMissingCompanies,
            CreateMissingModels = source.CreateMissingModels,
            MacAddressCustomFieldDbColumnName = source.MacAddressCustomFieldDbColumnName,
            NameMatchThreshold = source.NameMatchThreshold,
            ManualPreflightCsvEnabled = source.ManualPreflightCsvEnabled,
            ManualPreflightCsvDirectory = source.ManualPreflightCsvDirectory
        };
    }

    private static SyncFailure ToStageFailure(string stage, Exception exception)
    {
        return new SyncFailure
        {
            Stage = stage,
            Code = exception.GetType().Name,
            Message = $"{stage} failed: {exception.Message}"
        };
    }

    private static SyncFailure ToRunFailure(ImportFailure failure)
    {
        return new SyncFailure
        {
            Stage = SnipeImportStage,
            Code = failure.Code,
            Message = $"{failure.TargetType} '{failure.TargetName}' failed: {failure.Message}"
        };
    }

    private void LogStageFailure(string stage, Exception exception)
    {
        _logger.LogError(exception, "Sync run failed during {Stage}.", stage);
    }
}
