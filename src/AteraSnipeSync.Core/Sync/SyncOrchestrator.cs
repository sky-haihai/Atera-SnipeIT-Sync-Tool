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
        return await RunOnceAsync(request, cancellationToken, progress: null).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the sync pipeline once and reports safe stage progress for manual UI callers.
    /// </summary>
    public async Task<SyncRunResult> RunOnceAsync(
        SyncRunRequest request,
        CancellationToken cancellationToken,
        IProgress<SyncProgressUpdate>? progress)
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
        ReportProgress(progress, "Starting sync run.", percent: 0);

        try
        {
            ReportProgress(progress, "Pulling Atera inventory.", percent: 5);
            pullResult = await _ateraClient
                .PullInventoryAsync(request.Atera, cancellationToken, progress)
                .ConfigureAwait(false);
            AddWarningsDistinct(warnings, pullResult.Warnings);
            ReportProgress(progress, $"Atera pull completed with {pullResult.Summary.AgentCount} agent(s).", percent: 35);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            failures.Add(ToStageFailure(AteraPullStage, exception));
            LogStageFailure(AteraPullStage, exception);
            ReportProgress(progress, $"Atera pull failed: {exception.Message}", percent: 100);
            return CreateResult(startedAt, pullResult, importBatch, importResult, warnings, failures);
        }

        try
        {
            ReportProgress(progress, "Mapping Atera agents into Snipe-IT asset records.", percent: 40);
            importBatch = _inventoryMapper.Map(pullResult, request.Mapping);
            AddWarningsDistinct(warnings, importBatch.Warnings);
            ReportProgress(progress, $"Mapping completed with {importBatch.Summary.MappedAssetCount} asset(s).", percent: 45);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            failures.Add(ToStageFailure(ReconstructionStage, exception));
            LogStageFailure(ReconstructionStage, exception);
            ReportProgress(progress, $"Mapping failed: {exception.Message}", percent: 100);
            return CreateResult(startedAt, pullResult, importBatch, importResult, warnings, failures);
        }

        try
        {
            var snipeOptions = ApplySyncOptions(request.SnipeIt, request.Sync);
            ReportProgress(progress, "Planning Snipe-IT import.", percent: 50);
            importResult = await _snipeImporter
                .ImportAsync(importBatch, snipeOptions, cancellationToken, progress)
                .ConfigureAwait(false);

            AddWarningsDistinct(warnings, importResult.Warnings);
            failures.AddRange(importResult.Failures.Select(ToRunFailure));
            ReportProgress(progress, "Snipe-IT import stage completed.", percent: 95);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            failures.Add(ToStageFailure(SnipeImportStage, exception));
            LogStageFailure(SnipeImportStage, exception);
            ReportProgress(progress, $"Snipe-IT import failed: {exception.Message}", percent: 100);
        }

        var finalResult = CreateResult(startedAt, pullResult, importBatch, importResult, warnings, failures);
        ReportProgress(progress, finalResult.Success ? "Sync run completed." : "Sync run completed with failures.", percent: 100);
        return finalResult;
    }

    private static void ReportProgress(
        IProgress<SyncProgressUpdate>? progress,
        string message,
        int percent)
    {
        progress?.Report(new SyncProgressUpdate
        {
            Stage = "Sync",
            Message = message,
            Percent = Math.Clamp(percent, 0, 100)
        });
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
            MacAddressFieldsetName = source.MacAddressFieldsetName,
            ModelCategoryNormalizationTargetName = source.ModelCategoryNormalizationTargetName,
            ModelCategoriesToNormalize = source.ModelCategoriesToNormalize,
            IgnoredMacAddresses = source.IgnoredMacAddresses,
            NameMatchThreshold = source.NameMatchThreshold,
            ManualPreflightCsvEnabled = source.ManualPreflightCsvEnabled,
            ManualPreflightCsvDirectory = source.ManualPreflightCsvDirectory,
            MaxReadRetryAttempts = source.MaxReadRetryAttempts,
            RetryBaseDelay = source.RetryBaseDelay
        };
    }

    private static SyncFailure ToStageFailure(string stage, Exception exception)
    {
        return new SyncFailure
        {
            Stage = stage,
            Code = exception is AteraPullException ateraException
                ? $"AteraPull.{ateraException.FailureKind}"
                : exception.GetType().Name,
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

    /// <summary>
    /// Preserves first-seen warning order while preventing an upstream warning from being aggregated more than once.
    /// </summary>
    private static void AddWarningsDistinct(
        ICollection<ModuleWarning> destination,
        IEnumerable<ModuleWarning> source)
    {
        var existing = destination
            .Select(BuildWarningKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var warning in source)
        {
            if (existing.Add(BuildWarningKey(warning)))
            {
                destination.Add(warning);
            }
        }
    }

    private static string BuildWarningKey(ModuleWarning warning)
    {
        return $"{warning.Source}\u001f{warning.Code}\u001f{warning.Message}";
    }
}
