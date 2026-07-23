using System.Text.Json.Nodes;
using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Mapping;
using AteraSnipeSync.Core.SnipeIt;
using AteraSnipeSync.Core.Status;
using AteraSnipeSync.Core.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace AteraSnipeSync.Tests.Status;

/// <summary>
/// Verifies file-backed sync status history behavior using only local temp files and hand-built results.
/// </summary>
public sealed class JsonFileSyncStatusStoreTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "AteraSnipeSync.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAsync_WritesSuccessfulHistoryFile()
    {
        var historyDirectory = CreateHistoryDirectory();
        var store = CreateStore(historyDirectory);

        await store.SaveAsync(CreateRunResult(), CancellationToken.None);

        var root = ReadSingleHistoryRoot(historyDirectory);
        Assert.Equal(1, root["schemaVersion"]!.GetValue<int>());
        Assert.Equal("Success", root["run"]!["result"]!.GetValue<string>());
        Assert.False(root["run"]!["dryRun"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(root["run"]!["runId"]!.GetValue<string>()));
        Assert.Equal(3, root["summary"]!["pulled"]!.GetValue<int>());
        Assert.Equal(2, root["summary"]!["mapped"]!.GetValue<int>());
        Assert.Empty(ArrayAt(root, "failures"));
    }

    [Fact]
    public async Task SaveAsync_WritesFailedHistoryFile()
    {
        var historyDirectory = CreateHistoryDirectory();
        var store = CreateStore(historyDirectory);
        var failure = new SyncFailure
        {
            Stage = "SnipeImport",
            Code = "Import.Failed",
            Message = "Import failed."
        };

        await store.SaveAsync(
            CreateRunResult(success: false, importResult: CreateImportResult(failedAssets: 1), failures: [failure]),
            CancellationToken.None);

        var root = ReadSingleHistoryRoot(historyDirectory);
        Assert.Equal("Failed", root["run"]!["result"]!.GetValue<string>());
        Assert.Equal(1, root["summary"]!["assetsFailed"]!.GetValue<int>());
        Assert.Equal("SnipeImport", root["failures"]![0]!["stage"]!.GetValue<string>());
        Assert.Equal("Import failed.", root["failures"]![0]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task SaveAsync_RecordsPipelineFailureWithoutInventingFailedAsset()
    {
        var historyDirectory = CreateHistoryDirectory();
        var store = CreateStore(historyDirectory);
        var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        var result = new SyncRunResult
        {
            Success = false,
            DryRun = true,
            StartedAt = now,
            FinishedAt = now.AddSeconds(1),
            PullResult = null,
            ImportBatch = null,
            ImportResult = null,
            Warnings = [],
            Failures =
            [
                new SyncFailure
                {
                    Stage = "AteraPull",
                    Code = "Atera.AuthenticationFailed",
                    Message = "Authentication failed."
                }
            ]
        };

        await store.SaveAsync(result, CancellationToken.None);

        var root = ReadSingleHistoryRoot(historyDirectory);
        Assert.True(root["run"]!["dryRun"]!.GetValue<bool>());
        Assert.Equal(0, root["summary"]!["assetsFailed"]!.GetValue<int>());
        Assert.Equal(1, root["summary"]!["failureCount"]!.GetValue<int>());
        Assert.Equal(0, (await store.ReadLatestAsync(CancellationToken.None))?.Failed);
    }

    [Fact]
    public async Task SaveAsync_WritesSuccessAndFailureDetails_WhenCompletedRunHasRecordFailures()
    {
        var historyDirectory = CreateHistoryDirectory();
        var store = CreateStore(historyDirectory);
        var failure = new SyncFailure
        {
            Stage = "SnipeImport",
            Code = "Import.PartialFailure",
            Message = "One asset failed after another asset was created."
        };

        await store.SaveAsync(
            CreateRunResult(
                success: true,
                importResult: CreateImportResult(createdAssets: 1, failedAssets: 1),
                failures: [failure]),
            CancellationToken.None);

        var root = ReadSingleHistoryRoot(historyDirectory);
        Assert.Equal("Success", root["run"]!["result"]!.GetValue<string>());
        Assert.Equal(1, root["summary"]!["assetsCreated"]!.GetValue<int>());
        Assert.Equal(1, root["summary"]!["assetsFailed"]!.GetValue<int>());
        Assert.Equal("One asset failed after another asset was created.", root["failures"]![0]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task SaveAsync_WritesFailed_WhenIncompleteRunHasReferenceResourceOutcome()
    {
        var historyDirectory = CreateHistoryDirectory();
        var store = CreateStore(historyDirectory);

        await store.SaveAsync(
            CreateRunResult(
                success: false,
                importResult: CreateImportResult(createdCompanies: 1, failedAssets: 1),
                failures:
                [
                    new SyncFailure
                    {
                        Stage = "SnipeImport",
                        Message = "Asset failed after its company was created."
                    }
                ]),
            CancellationToken.None);

        var root = ReadSingleHistoryRoot(historyDirectory);
        Assert.Equal("Failed", root["run"]!["result"]!.GetValue<string>());
        Assert.Equal(0, root["summary"]!["assetsCreated"]!.GetValue<int>());
        Assert.Equal(1, root["summary"]!["companiesCreated"]!.GetValue<int>());
    }

    [Fact]
    public async Task SaveAsync_CreatesNewFile_ForEveryRun()
    {
        var historyDirectory = CreateHistoryDirectory();
        var store = CreateStore(historyDirectory);
        var result = CreateRunResult();

        await store.SaveAsync(result, CancellationToken.None);
        await store.SaveAsync(result, CancellationToken.None);

        var files = Directory.GetFiles(historyDirectory, "SyncResult_*.json");
        Assert.Equal(2, files.Length);
        Assert.Equal(2, files.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task SaveAsync_UsesUtcFinishedTimestampInFileName()
    {
        var historyDirectory = CreateHistoryDirectory();
        var store = CreateStore(historyDirectory);
        var finishedAt = new DateTimeOffset(2026, 6, 19, 12, 30, 45, TimeSpan.FromHours(-6))
            .AddTicks(1234567);

        await store.SaveAsync(CreateRunResult(finishedAt: finishedAt), CancellationToken.None);

        var filePath = Assert.Single(Directory.GetFiles(historyDirectory, "SyncResult_*.json"));
        Assert.Equal("SyncResult_20260619_183045_1234567Z.json", Path.GetFileName(filePath));
    }

    [Fact]
    public async Task SaveAsync_DoesNotOverwrite_WhenFileNameConflicts()
    {
        var historyDirectory = CreateHistoryDirectory();
        Directory.CreateDirectory(historyDirectory);
        var store = CreateStore(historyDirectory);
        var finishedAt = new DateTimeOffset(2026, 6, 19, 18, 30, 45, TimeSpan.Zero)
            .AddTicks(1234567);
        var conflictPath = Path.Combine(historyDirectory, ExpectedFileName(finishedAt));
        await File.WriteAllTextAsync(conflictPath, "existing-history");

        await store.SaveAsync(CreateRunResult(finishedAt: finishedAt), CancellationToken.None);

        var files = Directory.GetFiles(historyDirectory, "SyncResult_*.json");
        Assert.Equal(2, files.Length);
        Assert.Equal("existing-history", await File.ReadAllTextAsync(conflictPath));
        var createdFileName = Path.GetFileName(files.Single(file => file != conflictPath));
        Assert.Matches(@"^SyncResult_20260619_183045_1234567Z_[0-9a-f]{8}\.json$", createdFileName);
    }

    [Fact]
    public async Task SaveAsync_WritesStructuredAssetCreatedUpdatedSkippedFailedLists()
    {
        var historyDirectory = CreateHistoryDirectory();
        var store = CreateStore(historyDirectory);
        var importResult = CreateImportResult(
            createdAssets: 1,
            updatedAssets: 1,
            skippedAssets: 1,
            failedAssets: 1,
            actions:
            [
                CreateAction("Create", "Asset", "TAG-001", true, "Asset will be created."),
                CreateAction("Update", "Asset", "TAG-002", true, "Asset will be updated."),
                CreateAction("Skip", "Asset", "TAG-003", false, "Asset skipped.")
            ],
            importFailures:
            [
                new ImportFailure
                {
                    TargetType = "Asset",
                    TargetName = "TAG-004",
                    Code = "Asset.Failed",
                    Message = "Asset failed."
                }
            ]);

        await store.SaveAsync(CreateRunResult(importResult: importResult), CancellationToken.None);

        var root = ReadSingleHistoryRoot(historyDirectory);
        Assert.Equal("Created", root["assets"]!["created"]![0]!["action"]!.GetValue<string>());
        Assert.Equal("TAG-001", root["assets"]!["created"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("Updated", root["assets"]!["updated"]![0]!["action"]!.GetValue<string>());
        Assert.Equal("Skipped", root["assets"]!["skipped"]![0]!["action"]!.GetValue<string>());
        Assert.Equal("Failed", root["assets"]!["failed"]![0]!["action"]!.GetValue<string>());
        Assert.Equal("Asset failed.", root["assets"]!["failed"]![0]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task SaveAsync_WritesStructuredCompanyCategoryAndModelLists()
    {
        var historyDirectory = CreateHistoryDirectory();
        var store = CreateStore(historyDirectory);
        var importResult = CreateImportResult(
            createdCompanies: 1,
            createdCategories: 1,
            createdModels: 1,
            actions:
            [
                CreateAction("Create", "Company", "Acme", true, "Company missing."),
                CreateAction("Create", "Category", "Computer", true, "Category missing."),
                CreateAction("Create", "Model", "Latitude", true, "Model missing.")
            ]);

        await store.SaveAsync(CreateRunResult(importResult: importResult), CancellationToken.None);

        var root = ReadSingleHistoryRoot(historyDirectory);
        Assert.Equal("Acme", root["companies"]!["created"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("Company", root["companies"]!["created"]![0]!["targetType"]!.GetValue<string>());
        Assert.Equal(1, root["summary"]!["categoriesCreated"]!.GetValue<int>());
        Assert.Equal("Computer", root["categories"]!["created"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("Category", root["categories"]!["created"]![0]!["targetType"]!.GetValue<string>());
        Assert.Equal("Latitude", root["models"]!["created"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("Model", root["models"]!["created"]![0]!["targetType"]!.GetValue<string>());
    }

    [Fact]
    public async Task SaveAsync_WritesDeletedArraysAsEmpty_WhenNoDeleteActionsExist()
    {
        var historyDirectory = CreateHistoryDirectory();
        var store = CreateStore(historyDirectory);

        await store.SaveAsync(CreateRunResult(), CancellationToken.None);

        var root = ReadSingleHistoryRoot(historyDirectory);
        Assert.Empty(ArrayAt(root, "assets", "deleted"));
        Assert.Empty(ArrayAt(root, "companies", "deleted"));
        Assert.Empty(ArrayAt(root, "models", "deleted"));
        Assert.Empty(ArrayAt(root, "manufacturers", "deleted"));
        Assert.Empty(ArrayAt(root, "categories", "deleted"));
    }

    [Fact]
    public async Task SaveAsync_WritesDeletedAssetAuditAndRealCount()
    {
        var historyDirectory = CreateHistoryDirectory();
        var store = CreateStore(historyDirectory);
        const string message = "AssetId=501; AssetTag=ATERA-9999; Name=Retired Device; AteraAgentId=9999; Reason=MissingFromAtera.";
        var importResult = CreateImportResult(
            deletedAssets: 1,
            actions:
            [
                CreateAction("Delete", "Asset", "ATERA-9999", true, message, identifier: "501")
            ]);

        await store.SaveAsync(CreateRunResult(importResult: importResult), CancellationToken.None);

        var root = ReadSingleHistoryRoot(historyDirectory);
        Assert.Equal(1, root["summary"]!["assetsDeleted"]!.GetValue<int>());
        var deleted = Assert.Single(ArrayAt(root, "assets", "deleted"));
        Assert.Equal("Deleted", deleted!["action"]!.GetValue<string>());
        Assert.Equal("ATERA-9999", deleted["name"]!.GetValue<string>());
        Assert.Equal("501", deleted["identifier"]!.GetValue<string>());
        Assert.Equal(message, deleted["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task SaveAsync_DoesNotPersistSecretsOrRawPayloads()
    {
        var historyDirectory = CreateHistoryDirectory();
        var store = CreateStore(historyDirectory);
        const string rawPayloadSecret = "RAW_PAYLOAD_SECRET_SHOULD_NOT_PERSIST";
        const string mappedAssetSecret = "MAPPED_ASSET_NOTE_SECRET_SHOULD_NOT_PERSIST";
        var pullResult = CreatePullResult(
            1,
            [
                new AgentInfo
                {
                    AgentId = "agent-1",
                    Name = "Device 1",
                    RawJson = rawPayloadSecret
                }
            ]);
        var importBatch = CreateImportBatch(
            1,
            [
                new SnipeAssetImportRecord
                {
                    AssetTag = "TAG-001",
                    Name = "Device 1",
                    CompanyName = "Acme",
                    ManufacturerName = "Dell",
                    ModelName = "Latitude",
                    CategoryName = "Laptop",
                    StatusId = 2,
                    Notes = mappedAssetSecret,
                    SourceSystem = "Atera",
                    SourceId = "agent-1"
                }
            ]);

        await store.SaveAsync(
            CreateRunResult(pullResult: pullResult, importBatch: importBatch),
            CancellationToken.None);

        var json = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(historyDirectory, "SyncResult_*.json")));
        Assert.DoesNotContain(rawPayloadSecret, json, StringComparison.Ordinal);
        Assert.DoesNotContain(mappedAssetSecret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("rawJson", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api-token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadLatestAsync_ReturnsNull_WhenHistoryDirectoryMissing()
    {
        var store = CreateStore(CreateHistoryDirectory());

        var result = await store.ReadLatestAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReadLatestAsync_ReturnsNull_WhenHistoryDirectoryEmpty()
    {
        var historyDirectory = CreateHistoryDirectory();
        Directory.CreateDirectory(historyDirectory);
        var store = CreateStore(historyDirectory);

        var result = await store.ReadLatestAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReadLatestAsync_ReturnsNewestValidSnapshot()
    {
        var historyDirectory = CreateHistoryDirectory();
        var store = CreateStore(historyDirectory);
        var olderFinishedAt = new DateTimeOffset(2026, 6, 19, 18, 30, 0, TimeSpan.Zero);
        var newerFinishedAt = new DateTimeOffset(2026, 6, 19, 18, 45, 0, TimeSpan.Zero);

        await store.SaveAsync(
            CreateRunResult(
                finishedAt: olderFinishedAt,
                importResult: CreateImportResult(createdAssets: 1)),
            CancellationToken.None);
        await store.SaveAsync(
            CreateRunResult(
                finishedAt: newerFinishedAt,
                pullResult: CreatePullResult(7),
                importBatch: CreateImportBatch(6),
                importResult: CreateImportResult(createdAssets: 4, updatedAssets: 2, deletedAssets: 3, skippedAssets: 1)),
            CancellationToken.None);

        var snapshot = await store.ReadLatestAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.False(snapshot.IsRunning);
        Assert.Equal("Success", snapshot.LastResult);
        Assert.Equal(newerFinishedAt.ToUniversalTime(), snapshot.LastRunFinishedAt);
        Assert.Equal(newerFinishedAt.ToUniversalTime(), snapshot.LastSuccessAt);
        Assert.Equal(7, snapshot.Pulled);
        Assert.Equal(6, snapshot.Mapped);
        Assert.Equal(4, snapshot.Created);
        Assert.Equal(2, snapshot.Updated);
        Assert.Equal(3, snapshot.Deleted);
        Assert.Equal(1, snapshot.Skipped);
        Assert.Null(snapshot.LastError);
    }

    [Fact]
    public async Task ReadLatestAsync_SkipsMalformedNewestFile_AndReadsNextValidFile()
    {
        var historyDirectory = CreateHistoryDirectory();
        var store = CreateStore(historyDirectory);
        var validFinishedAt = new DateTimeOffset(2026, 6, 19, 18, 30, 0, TimeSpan.Zero);
        var malformedFinishedAt = new DateTimeOffset(2026, 6, 19, 18, 45, 0, TimeSpan.Zero);
        await store.SaveAsync(CreateRunResult(finishedAt: validFinishedAt), CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(historyDirectory, ExpectedFileName(malformedFinishedAt)),
            "{ not-json");

        var snapshot = await store.ReadLatestAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(validFinishedAt.ToUniversalTime(), snapshot.LastRunFinishedAt);
    }

    [Fact]
    public async Task ReadLatestAsync_ComputesLastSuccessAt_FromHistory()
    {
        var historyDirectory = CreateHistoryDirectory();
        var store = CreateStore(historyDirectory);
        var successFinishedAt = new DateTimeOffset(2026, 6, 19, 18, 30, 0, TimeSpan.Zero);
        var failedFinishedAt = new DateTimeOffset(2026, 6, 19, 18, 45, 0, TimeSpan.Zero);
        await store.SaveAsync(CreateRunResult(finishedAt: successFinishedAt), CancellationToken.None);
        await store.SaveAsync(
            CreateRunResult(
                success: false,
                finishedAt: failedFinishedAt,
                importResult: CreateImportResult(failedAssets: 1),
                failures:
                [
                    new SyncFailure
                    {
                        Stage = "SnipeImport",
                        Code = "SnipeImport.Failed",
                        Message = "Latest run failed."
                    }
                ]),
            CancellationToken.None);

        var snapshot = await store.ReadLatestAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("Failed", snapshot.LastResult);
        Assert.Equal(failedFinishedAt.ToUniversalTime(), snapshot.LastRunFinishedAt);
        Assert.Equal(successFinishedAt.ToUniversalTime(), snapshot.LastSuccessAt);
        Assert.Equal("Latest run failed.", snapshot.LastError);
    }

    [Fact]
    public async Task ReadLatestAsync_CompletedRunWithRecordFailuresAdvancesLastSuccessAndKeepsCounts()
    {
        var historyDirectory = CreateHistoryDirectory();
        var store = CreateStore(historyDirectory);
        var successFinishedAt = new DateTimeOffset(2026, 6, 19, 18, 30, 0, TimeSpan.Zero);
        var partialFinishedAt = new DateTimeOffset(2026, 6, 19, 18, 45, 0, TimeSpan.Zero);
        await store.SaveAsync(CreateRunResult(finishedAt: successFinishedAt), CancellationToken.None);
        await store.SaveAsync(
            CreateRunResult(
                success: true,
                finishedAt: partialFinishedAt,
                importResult: CreateImportResult(updatedAssets: 2, skippedAssets: 1, failedAssets: 1),
                failures:
                [
                    new SyncFailure
                    {
                        Stage = "SnipeImport",
                        Code = "SnipeImport.PartialFailure",
                        Message = "Latest run completed only some assets."
                    }
                ]),
            CancellationToken.None);

        var snapshot = await store.ReadLatestAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("Success", snapshot.LastResult);
        Assert.Equal(partialFinishedAt.ToUniversalTime(), snapshot.LastRunFinishedAt);
        Assert.Equal(partialFinishedAt.ToUniversalTime(), snapshot.LastSuccessAt);
        Assert.Equal(2, snapshot.Updated);
        Assert.Equal(1, snapshot.Skipped);
        Assert.Null(snapshot.LastError);
    }

    [Fact]
    public async Task ReadLatestAsync_ReturnsNull_WhenAllHistoryFilesMalformed()
    {
        var historyDirectory = CreateHistoryDirectory();
        Directory.CreateDirectory(historyDirectory);
        var store = CreateStore(historyDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(historyDirectory, ExpectedFileName(new DateTimeOffset(2026, 6, 19, 18, 30, 0, TimeSpan.Zero))),
            "{ not-json");
        await File.WriteAllTextAsync(
            Path.Combine(historyDirectory, ExpectedFileName(new DateTimeOffset(2026, 6, 19, 18, 45, 0, TimeSpan.Zero))),
            "{ also-not-json");

        var snapshot = await store.ReadLatestAsync(CancellationToken.None);

        Assert.Null(snapshot);
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_ForNullOptions()
    {
        Assert.Throws<ArgumentNullException>(
            () => new JsonFileSyncStatusStore(null!, NullLogger<JsonFileSyncStatusStore>.Instance));
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_ForNullLogger()
    {
        Assert.Throws<ArgumentNullException>(
            () => new JsonFileSyncStatusStore(
                new SyncStatusStoreOptions { HistoryDirectoryPath = CreateHistoryDirectory() },
                null!));
    }

    [Fact]
    public void Constructor_ThrowsArgumentException_ForBlankHistoryDirectory()
    {
        Assert.Throws<ArgumentException>(
            () => new JsonFileSyncStatusStore(
                new SyncStatusStoreOptions { HistoryDirectoryPath = " " },
                NullLogger<JsonFileSyncStatusStore>.Instance));
    }

    [Fact]
    public async Task SaveAsync_ThrowsArgumentNullException_WhenResultNull()
    {
        var store = CreateStore(CreateHistoryDirectory());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => store.SaveAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_HonorsCancellation()
    {
        var store = CreateStore(CreateHistoryDirectory());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.SaveAsync(CreateRunResult(), cancellation.Token));
    }

    [Fact]
    public async Task ReadLatestAsync_HonorsCancellation()
    {
        var store = CreateStore(CreateHistoryDirectory());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.ReadLatestAsync(cancellation.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private string CreateHistoryDirectory(string name = "history")
    {
        return Path.Combine(_tempRoot, name);
    }

    private static JsonFileSyncStatusStore CreateStore(string historyDirectory)
    {
        return new JsonFileSyncStatusStore(
            new SyncStatusStoreOptions { HistoryDirectoryPath = historyDirectory },
            NullLogger<JsonFileSyncStatusStore>.Instance);
    }

    private static JsonNode ReadSingleHistoryRoot(string historyDirectory)
    {
        var filePath = Assert.Single(Directory.GetFiles(historyDirectory, "SyncResult_*.json"));
        return JsonNode.Parse(File.ReadAllText(filePath))!;
    }

    private static JsonArray ArrayAt(JsonNode root, string sectionName, string arrayName)
    {
        return root[sectionName]![arrayName]!.AsArray();
    }

    private static JsonArray ArrayAt(JsonNode root, string arrayName)
    {
        return root[arrayName]!.AsArray();
    }

    private static string ExpectedFileName(DateTimeOffset finishedAt)
    {
        var utc = finishedAt.ToUniversalTime();
        return $"SyncResult_{utc:yyyyMMdd_HHmmss_fffffff}Z.json";
    }

    private static ImportAction CreateAction(
        string actionType,
        string targetType,
        string targetName,
        bool wasExecuted,
        string message,
        string? identifier = null)
    {
        return new ImportAction
        {
            ActionType = actionType,
            TargetType = targetType,
            TargetName = targetName,
            WasExecuted = wasExecuted,
            Identifier = identifier,
            Message = message
        };
    }

    private static SyncRunResult CreateRunResult(
        bool success = true,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? finishedAt = null,
        AteraPullResult? pullResult = null,
        SnipeImportBatch? importBatch = null,
        SnipeImportResult? importResult = null,
        IReadOnlyList<ModuleWarning>? warnings = null,
        IReadOnlyList<SyncFailure>? failures = null)
    {
        var runStartedAt = startedAt ?? new DateTimeOffset(2026, 6, 19, 18, 30, 0, TimeSpan.Zero);
        var runFinishedAt = finishedAt ?? runStartedAt.AddMinutes(5);

        return new SyncRunResult
        {
            Success = success,
            DryRun = importResult?.DryRun ?? false,
            StartedAt = runStartedAt,
            FinishedAt = runFinishedAt,
            PullResult = pullResult ?? CreatePullResult(3),
            ImportBatch = importBatch ?? CreateImportBatch(2),
            ImportResult = importResult ?? CreateImportResult(createdAssets: 1),
            Warnings = warnings ?? [],
            Failures = failures ?? []
        };
    }

    private static AteraPullResult CreatePullResult(
        int agentCount,
        IReadOnlyList<AgentInfo>? agents = null)
    {
        return new AteraPullResult
        {
            Agents = agents ?? [],
            Summary = new PullSummary
            {
                AgentCount = agentCount,
                PulledAt = new DateTimeOffset(2026, 6, 19, 18, 25, 0, TimeSpan.Zero)
            },
            Warnings = []
        };
    }

    private static SnipeImportBatch CreateImportBatch(
        int mappedAssetCount,
        IReadOnlyList<SnipeAssetImportRecord>? assets = null)
    {
        return new SnipeImportBatch
        {
            Assets = assets ?? [],
            Summary = new MappingSummary
            {
                SourceAgentCount = mappedAssetCount,
                MappedAssetCount = mappedAssetCount
            },
            Warnings = []
        };
    }

    private static SnipeImportResult CreateImportResult(
        int createdAssets = 0,
        int updatedAssets = 0,
        int deletedAssets = 0,
        int skippedAssets = 0,
        int failedAssets = 0,
        int createdCompanies = 0,
        int createdCategories = 0,
        int createdModels = 0,
        bool dryRun = false,
        IReadOnlyList<ImportAction>? actions = null,
        IReadOnlyList<ImportFailure>? importFailures = null,
        IReadOnlyList<ModuleWarning>? warnings = null)
    {
        return new SnipeImportResult
        {
            CreatedAssets = createdAssets,
            UpdatedAssets = updatedAssets,
            DeletedAssets = deletedAssets,
            SkippedAssets = skippedAssets,
            FailedAssets = failedAssets,
            CreatedCompanies = createdCompanies,
            CreatedCategories = createdCategories,
            CreatedModels = createdModels,
            UpdatedModels = 0,
            DryRun = dryRun,
            Actions = actions ?? [],
            Failures = importFailures ?? [],
            Warnings = warnings ?? []
        };
    }
}
