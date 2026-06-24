using System.Globalization;
using System.Text.Json;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.SnipeIt;
using AteraSnipeSync.Core.Sync;
using Microsoft.Extensions.Logging;

namespace AteraSnipeSync.Core.Status;

/// <summary>
/// Persists each completed sync run as a structured local JSON history file and reads the latest snapshot.
/// </summary>
public sealed class JsonFileSyncStatusStore : ISyncStatusStore
{
    private const int CurrentSchemaVersion = 1;
    private const string FileNamePrefix = "SyncResult_";
    private const string FileNameExtension = ".json";
    private const string HistoryTimestampFormat = "yyyyMMdd_HHmmss_fffffff'Z'";
    private const int HistoryTimestampLength = 24;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _historyDirectoryPath;
    private readonly ILogger<JsonFileSyncStatusStore> _logger;

    public JsonFileSyncStatusStore(
        SyncStatusStoreOptions options,
        ILogger<JsonFileSyncStatusStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(options.HistoryDirectoryPath))
        {
            throw new ArgumentException(
                "History directory path must not be blank.",
                nameof(options));
        }

        _historyDirectoryPath = Path.GetFullPath(options.HistoryDirectoryPath);
        _logger = logger;
    }

    /// <summary>
    /// Writes one completed sync result to a distinct history JSON file using a temp-file move.
    /// </summary>
    public async Task SaveAsync(
        SyncRunResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();

        string? tempPath = null;

        try
        {
            Directory.CreateDirectory(_historyDirectoryPath);

            var document = CreateHistoryDocument(result);
            var finalPath = CreateUniqueHistoryFilePath(document.Run.FinishedAtUtc);
            tempPath = Path.Combine(
                _historyDirectoryPath,
                $"{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            MoveTempFileWithoutOverwrite(tempPath, ref finalPath, document.Run.FinishedAtUtc);

            _logger.LogInformation(
                "Saved sync history file {HistoryFilePath} with result {SyncResult}.",
                finalPath,
                document.Run.Result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "Failed to save sync history file in {HistoryDirectoryPath}.",
                _historyDirectoryPath);
            throw;
        }
        finally
        {
            DeleteTempFileIfPresent(tempPath);
        }
    }

    /// <summary>
    /// Reads history files newest-first and returns the latest valid status snapshot.
    /// </summary>
    public async Task<SyncStatusSnapshot?> ReadLatestAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<string> historyFiles;

        try
        {
            if (!Directory.Exists(_historyDirectoryPath))
            {
                _logger.LogDebug(
                    "Sync history directory {HistoryDirectoryPath} does not exist.",
                    _historyDirectoryPath);
                return null;
            }

            historyFiles = Directory
                .EnumerateFiles(_historyDirectoryPath, $"{FileNamePrefix}*{FileNameExtension}", SearchOption.TopDirectoryOnly)
                .OrderByDescending(GetHistorySortTime)
                .ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Failed to enumerate sync history files from {HistoryDirectoryPath}.",
                _historyDirectoryPath);
            return null;
        }

        if (historyFiles.Count == 0)
        {
            _logger.LogDebug(
                "Sync history directory {HistoryDirectoryPath} is empty.",
                _historyDirectoryPath);
            return null;
        }

        SyncHistoryDocument? latestDocument = null;
        DateTimeOffset? latestSuccessAt = null;

        foreach (var filePath in historyFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var document = await TryReadHistoryDocumentAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                continue;
            }

            latestDocument ??= document;

            if (latestSuccessAt is null && IsSuccess(document))
            {
                latestSuccessAt = document.Run.FinishedAtUtc;
            }

            if (latestDocument is not null && (IsSuccess(latestDocument) || latestSuccessAt.HasValue))
            {
                break;
            }
        }

        if (latestDocument is null)
        {
            _logger.LogWarning(
                "No valid sync history files were found in {HistoryDirectoryPath}.",
                _historyDirectoryPath);
            return null;
        }

        return CreateSnapshot(latestDocument, latestSuccessAt);
    }

    private static SyncHistoryDocument CreateHistoryDocument(SyncRunResult result)
    {
        var startedAtUtc = result.StartedAt.ToUniversalTime();
        var finishedAtUtc = result.FinishedAt.ToUniversalTime();
        var durationMs = Math.Max(0, (long)(finishedAtUtc - startedAtUtc).TotalMilliseconds);
        var warnings = result.Warnings ?? [];
        var failures = result.Failures ?? [];
        var importActions = result.ImportResult?.Actions ?? [];
        var importFailures = result.ImportResult?.Failures ?? [];

        var assets = CreateChangeSetLists();
        var companies = CreateChangeSetLists();
        var models = CreateChangeSetLists();
        var manufacturers = CreateChangeSetLists();
        var categories = CreateChangeSetLists();

        foreach (var action in importActions)
        {
            AddImportAction(action, assets, companies, models, manufacturers, categories);
        }

        foreach (var failure in importFailures)
        {
            AddImportFailure(failure, assets, companies, models, manufacturers, categories);
        }

        return new SyncHistoryDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            Run = new SyncHistoryRunInfo
            {
                RunId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                Result = result.Success ? "Success" : "Failed",
                StartedAtUtc = startedAtUtc,
                FinishedAtUtc = finishedAtUtc,
                DurationMs = durationMs,
                DryRun = result.ImportResult?.DryRun ?? false
            },
            Summary = new SyncHistorySummary
            {
                Pulled = result.PullResult?.Summary.AgentCount ?? 0,
                Mapped = result.ImportBatch?.Summary.MappedAssetCount ?? 0,
                AssetsCreated = result.ImportResult?.CreatedAssets ?? 0,
                AssetsUpdated = result.ImportResult?.UpdatedAssets ?? 0,
                AssetsDeleted = 0,
                AssetsSkipped = result.ImportResult?.SkippedAssets ?? 0,
                AssetsFailed = result.ImportResult?.FailedAssets ?? failures.Count,
                CompaniesCreated = result.ImportResult?.CreatedCompanies ?? 0,
                CompaniesUpdated = 0,
                CompaniesDeleted = 0,
                ModelsCreated = result.ImportResult?.CreatedModels ?? 0,
                ModelsUpdated = 0,
                ModelsDeleted = 0,
                CategoriesCreated = result.ImportResult?.CreatedCategories ?? 0,
                CategoriesUpdated = 0,
                CategoriesDeleted = 0,
                WarningCount = warnings.Count,
                FailureCount = failures.Count
            },
            Assets = ToChangeSet(assets),
            Companies = ToChangeSet(companies),
            Models = ToChangeSet(models),
            Manufacturers = ToChangeSet(manufacturers),
            Categories = ToChangeSet(categories),
            Warnings = warnings
                .Select(warning => new SyncHistoryWarning
                {
                    Source = RequiredText(warning.Source, "Unknown"),
                    Code = OptionalText(warning.Code),
                    Message = RequiredText(warning.Message, "Warning.")
                })
                .ToArray(),
            Failures = failures
                .Select(failure => new SyncHistoryFailure
                {
                    Stage = RequiredText(failure.Stage, "Unknown"),
                    Code = OptionalText(failure.Code),
                    Message = RequiredText(failure.Message, "Sync failed.")
                })
                .ToArray()
        };
    }

    private static void AddImportAction(
        ImportAction action,
        (List<SyncHistoryItem> Created, List<SyncHistoryItem> Updated, List<SyncHistoryItem> Deleted, List<SyncHistoryItem> Skipped, List<SyncHistoryItem> Failed) assets,
        (List<SyncHistoryItem> Created, List<SyncHistoryItem> Updated, List<SyncHistoryItem> Deleted, List<SyncHistoryItem> Skipped, List<SyncHistoryItem> Failed) companies,
        (List<SyncHistoryItem> Created, List<SyncHistoryItem> Updated, List<SyncHistoryItem> Deleted, List<SyncHistoryItem> Skipped, List<SyncHistoryItem> Failed) models,
        (List<SyncHistoryItem> Created, List<SyncHistoryItem> Updated, List<SyncHistoryItem> Deleted, List<SyncHistoryItem> Skipped, List<SyncHistoryItem> Failed) manufacturers,
        (List<SyncHistoryItem> Created, List<SyncHistoryItem> Updated, List<SyncHistoryItem> Deleted, List<SyncHistoryItem> Skipped, List<SyncHistoryItem> Failed) categories)
    {
        var actionType = NormalizeAction(action.ActionType);
        var targetType = NormalizeTargetType(action.TargetType);

        if (actionType is null || targetType is null)
        {
            return;
        }

        var item = new SyncHistoryItem
        {
            Source = "SnipeImport",
            Action = actionType,
            TargetType = targetType,
            Name = RequiredText(action.TargetName, "<unknown>"),
            Identifier = null,
            WasExecuted = action.WasExecuted,
            Message = OptionalText(action.Message)
        };

        AddToResourceChangeSet(targetType, actionType, item, assets, companies, models, manufacturers, categories);
    }

    private static void AddImportFailure(
        ImportFailure failure,
        (List<SyncHistoryItem> Created, List<SyncHistoryItem> Updated, List<SyncHistoryItem> Deleted, List<SyncHistoryItem> Skipped, List<SyncHistoryItem> Failed) assets,
        (List<SyncHistoryItem> Created, List<SyncHistoryItem> Updated, List<SyncHistoryItem> Deleted, List<SyncHistoryItem> Skipped, List<SyncHistoryItem> Failed) companies,
        (List<SyncHistoryItem> Created, List<SyncHistoryItem> Updated, List<SyncHistoryItem> Deleted, List<SyncHistoryItem> Skipped, List<SyncHistoryItem> Failed) models,
        (List<SyncHistoryItem> Created, List<SyncHistoryItem> Updated, List<SyncHistoryItem> Deleted, List<SyncHistoryItem> Skipped, List<SyncHistoryItem> Failed) manufacturers,
        (List<SyncHistoryItem> Created, List<SyncHistoryItem> Updated, List<SyncHistoryItem> Deleted, List<SyncHistoryItem> Skipped, List<SyncHistoryItem> Failed) categories)
    {
        var targetType = NormalizeTargetType(failure.TargetType);
        if (targetType is null)
        {
            return;
        }

        var item = new SyncHistoryItem
        {
            Source = "SnipeImport",
            Action = "Failed",
            TargetType = targetType,
            Name = RequiredText(failure.TargetName, "<unknown>"),
            Identifier = null,
            WasExecuted = false,
            Message = OptionalText(failure.Message)
        };

        AddToResourceChangeSet(targetType, "Failed", item, assets, companies, models, manufacturers, categories);
    }

    private static void AddToResourceChangeSet(
        string targetType,
        string actionType,
        SyncHistoryItem item,
        (List<SyncHistoryItem> Created, List<SyncHistoryItem> Updated, List<SyncHistoryItem> Deleted, List<SyncHistoryItem> Skipped, List<SyncHistoryItem> Failed) assets,
        (List<SyncHistoryItem> Created, List<SyncHistoryItem> Updated, List<SyncHistoryItem> Deleted, List<SyncHistoryItem> Skipped, List<SyncHistoryItem> Failed) companies,
        (List<SyncHistoryItem> Created, List<SyncHistoryItem> Updated, List<SyncHistoryItem> Deleted, List<SyncHistoryItem> Skipped, List<SyncHistoryItem> Failed) models,
        (List<SyncHistoryItem> Created, List<SyncHistoryItem> Updated, List<SyncHistoryItem> Deleted, List<SyncHistoryItem> Skipped, List<SyncHistoryItem> Failed) manufacturers,
        (List<SyncHistoryItem> Created, List<SyncHistoryItem> Updated, List<SyncHistoryItem> Deleted, List<SyncHistoryItem> Skipped, List<SyncHistoryItem> Failed) categories)
    {
        switch (targetType)
        {
            case "Asset":
                AddToChangeSet(assets, actionType, item);
                break;
            case "Company":
                AddToChangeSet(companies, actionType, item);
                break;
            case "Model":
                AddToChangeSet(models, actionType, item);
                break;
            case "Manufacturer":
                AddToChangeSet(manufacturers, actionType, item);
                break;
            case "Category":
                AddToChangeSet(categories, actionType, item);
                break;
        }
    }

    private static void AddToChangeSet(
        (List<SyncHistoryItem> Created, List<SyncHistoryItem> Updated, List<SyncHistoryItem> Deleted, List<SyncHistoryItem> Skipped, List<SyncHistoryItem> Failed) changeSet,
        string actionType,
        SyncHistoryItem item)
    {
        switch (actionType)
        {
            case "Created":
                changeSet.Created.Add(item);
                break;
            case "Updated":
                changeSet.Updated.Add(item);
                break;
            case "Deleted":
                changeSet.Deleted.Add(item);
                break;
            case "Skipped":
                changeSet.Skipped.Add(item);
                break;
            case "Failed":
                changeSet.Failed.Add(item);
                break;
        }
    }

    private static (List<SyncHistoryItem> Created, List<SyncHistoryItem> Updated, List<SyncHistoryItem> Deleted, List<SyncHistoryItem> Skipped, List<SyncHistoryItem> Failed) CreateChangeSetLists()
    {
        return ([], [], [], [], []);
    }

    private static SyncHistoryChangeSet ToChangeSet(
        (List<SyncHistoryItem> Created, List<SyncHistoryItem> Updated, List<SyncHistoryItem> Deleted, List<SyncHistoryItem> Skipped, List<SyncHistoryItem> Failed) changeSet)
    {
        return new SyncHistoryChangeSet
        {
            Created = changeSet.Created,
            Updated = changeSet.Updated,
            Deleted = changeSet.Deleted,
            Skipped = changeSet.Skipped,
            Failed = changeSet.Failed
        };
    }

    private static SyncStatusSnapshot CreateSnapshot(
        SyncHistoryDocument latestDocument,
        DateTimeOffset? latestSuccessAt)
    {
        var latestResultIsSuccess = IsSuccess(latestDocument);

        return new SyncStatusSnapshot
        {
            IsRunning = false,
            LastResult = latestDocument.Run.Result,
            LastRunStartedAt = latestDocument.Run.StartedAtUtc,
            LastRunFinishedAt = latestDocument.Run.FinishedAtUtc,
            LastSuccessAt = latestSuccessAt,
            DryRun = latestDocument.Run.DryRun,
            Pulled = latestDocument.Summary.Pulled,
            Mapped = latestDocument.Summary.Mapped,
            Created = latestDocument.Summary.AssetsCreated,
            Updated = latestDocument.Summary.AssetsUpdated,
            Skipped = latestDocument.Summary.AssetsSkipped,
            Failed = latestDocument.Summary.AssetsFailed,
            LastError = latestResultIsSuccess
                ? null
                : latestDocument.Failures.FirstOrDefault()?.Message ?? "Sync failed."
        };
    }

    private async Task<SyncHistoryDocument?> TryReadHistoryDocumentAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous);
            var document = await JsonSerializer.DeserializeAsync<SyncHistoryDocument>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);

            if (document is null || document.Run is null || document.Summary is null)
            {
                _logger.LogWarning("Skipped malformed sync history file {HistoryFilePath}.", filePath);
                return null;
            }

            if (document.SchemaVersion != CurrentSchemaVersion)
            {
                _logger.LogWarning(
                    "Skipped sync history file {HistoryFilePath} with unsupported schema version {SchemaVersion}.",
                    filePath,
                    document.SchemaVersion);
                return null;
            }

            return document;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            _logger.LogWarning(
                exception,
                "Skipped malformed sync history file {HistoryFilePath}.",
                filePath);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "Failed to read sync history file {HistoryFilePath}.",
                filePath);
            return null;
        }
    }

    private void MoveTempFileWithoutOverwrite(
        string tempPath,
        ref string finalPath,
        DateTimeOffset finishedAtUtc)
    {
        while (true)
        {
            try
            {
                File.Move(tempPath, finalPath, overwrite: false);
                return;
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                finalPath = CreateConflictHistoryFilePath(finishedAtUtc);
            }
        }
    }

    private string CreateUniqueHistoryFilePath(DateTimeOffset finishedAtUtc)
    {
        var filePath = Path.Combine(_historyDirectoryPath, CreateHistoryFileName(finishedAtUtc));
        return File.Exists(filePath) ? CreateConflictHistoryFilePath(finishedAtUtc) : filePath;
    }

    private string CreateConflictHistoryFilePath(DateTimeOffset finishedAtUtc)
    {
        var baseName = Path.GetFileNameWithoutExtension(CreateHistoryFileName(finishedAtUtc));

        while (true)
        {
            var shortGuid = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
            var filePath = Path.Combine(_historyDirectoryPath, $"{baseName}_{shortGuid}{FileNameExtension}");

            if (!File.Exists(filePath))
            {
                return filePath;
            }
        }
    }

    private void DeleteTempFileIfPresent(string? tempPath)
    {
        if (tempPath is null || !File.Exists(tempPath))
        {
            return;
        }

        try
        {
            File.Delete(tempPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "Failed to delete temporary sync history file {TempFilePath}.",
                tempPath);
        }
    }

    private static string CreateHistoryFileName(DateTimeOffset finishedAtUtc)
    {
        var utc = finishedAtUtc.ToUniversalTime();
        var timestamp = utc.ToString(HistoryTimestampFormat, CultureInfo.InvariantCulture);
        return $"{FileNamePrefix}{timestamp}{FileNameExtension}";
    }

    private static DateTimeOffset GetHistorySortTime(string filePath)
    {
        if (TryParseHistoryTimestamp(filePath, out var timestamp))
        {
            return timestamp;
        }

        return new DateTimeOffset(File.GetLastWriteTimeUtc(filePath));
    }

    private static bool TryParseHistoryTimestamp(string filePath, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var fileName = Path.GetFileName(filePath);

        if (!fileName.StartsWith(FileNamePrefix, StringComparison.OrdinalIgnoreCase)
            || fileName.Length < FileNamePrefix.Length + HistoryTimestampLength + FileNameExtension.Length)
        {
            return false;
        }

        var value = fileName.Substring(FileNamePrefix.Length, HistoryTimestampLength);
        return DateTimeOffset.TryParseExact(
            value,
            HistoryTimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }

    private static bool IsSuccess(SyncHistoryDocument document)
    {
        return string.Equals(document.Run.Result, "Success", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeAction(string actionType)
    {
        var normalized = OptionalText(actionType);

        return normalized?.ToUpperInvariant() switch
        {
            "CREATE" or "CREATED" => "Created",
            "UPDATE" or "UPDATED" => "Updated",
            "DELETE" or "DELETED" => "Deleted",
            "SKIP" or "SKIPPED" => "Skipped",
            "FAIL" or "FAILED" => "Failed",
            _ => null
        };
    }

    private static string? NormalizeTargetType(string targetType)
    {
        var normalized = OptionalText(targetType);

        return normalized?.ToUpperInvariant() switch
        {
            "ASSET" or "ASSETS" or "HARDWARE" => "Asset",
            "COMPANY" or "COMPANIES" => "Company",
            "MODEL" or "MODELS" => "Model",
            "MANUFACTURER" or "MANUFACTURERS" => "Manufacturer",
            "CATEGORY" or "CATEGORIES" => "Category",
            _ => null
        };
    }

    private static string RequiredText(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string? OptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
