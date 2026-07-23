# Status Store - 技术规格

## 1. Scope

Status Store Module 实现 `ISyncStatusStore`，把每一次已完成的 `SyncRunResult` 保存为独立、结构化、可被 TrayApp 解析的本机 history JSON 文件。

本技术规格基于：

- `docs/module-plans/05-StatusStore-功能职责.md`
- `docs/AteraSnipeSync_AI_Agent_Master_Plan.md` 的 `Module 5 - Status Store`
- existing `src/AteraSnipeSync.Core/Status/Interfaces/ISyncStatusStore.cs`
- existing `src/AteraSnipeSync.Core/Status/SyncStatusSnapshot.cs`

本模块不新增或修改 Atera/Snipe-IT API wire contracts，不新增真实 API 测试。

## 2. Namespace and Files

Production code:

- existing `src/AteraSnipeSync.Core/Status/Interfaces/ISyncStatusStore.cs`
- existing `src/AteraSnipeSync.Core/Status/SyncStatusSnapshot.cs`
- new `src/AteraSnipeSync.Core/Status/JsonFileSyncStatusStore.cs`
- new `src/AteraSnipeSync.Core/Status/SyncStatusStoreOptions.cs`
- new `src/AteraSnipeSync.Core/Status/SyncHistoryDocument.cs`
- new `src/AteraSnipeSync.Core/Status/SyncHistoryRunInfo.cs`
- new `src/AteraSnipeSync.Core/Status/SyncHistorySummary.cs`
- new `src/AteraSnipeSync.Core/Status/SyncHistoryChangeSet.cs`
- new `src/AteraSnipeSync.Core/Status/SyncHistoryItem.cs`
- new `src/AteraSnipeSync.Core/Status/SyncHistoryWarning.cs`
- new `src/AteraSnipeSync.Core/Status/SyncHistoryFailure.cs`

Tests:

- new `tests/AteraSnipeSync.Tests/Status/JsonFileSyncStatusStoreTests.cs`

All production status classes live in namespace:

```csharp
namespace AteraSnipeSync.Core.Status;
```

## 3. Public Interface

### 3.1 `ISyncStatusStore`

Existing interface:

```csharp
public interface ISyncStatusStore
{
    Task SaveAsync(
        SyncRunResult result,
        CancellationToken cancellationToken);

    Task<SyncStatusSnapshot?> ReadLatestAsync(
        CancellationToken cancellationToken);
}
```

Required comment:

```csharp
/// <summary>
/// Stores sync run history files and reads the latest local sync status snapshot.
/// </summary>
```

No signature change is required for Module 5 first implementation.

## 4. Public Models

### 4.1 `SyncStatusSnapshot`

Existing public model:

```csharp
public sealed class SyncStatusSnapshot
{
    public required bool IsRunning { get; init; }
    public required string LastResult { get; init; }
    public required DateTimeOffset? LastRunStartedAt { get; init; }
    public required DateTimeOffset? LastRunFinishedAt { get; init; }
    public required DateTimeOffset? LastSuccessAt { get; init; }
    public required bool DryRun { get; init; }
    public required int Pulled { get; init; }
    public required int Mapped { get; init; }
    public required int Created { get; init; }
    public required int Updated { get; init; }
    public required int Skipped { get; init; }
    public required int Failed { get; init; }
    public string? LastError { get; init; }
}
```

Required comment:

```csharp
/// <summary>
/// Presents the latest completed sync run as a compact status view for UI and operational checks.
/// </summary>
```

Field rules:

- `IsRunning` is `false` for every snapshot produced from history
- `LastResult` comes from latest valid history document `Run.Result`
- `LastRunStartedAt` comes from latest valid history document `Run.StartedAtUtc`
- `LastRunFinishedAt` comes from latest valid history document `Run.FinishedAtUtc`
- `LastSuccessAt` is the newest valid history document whose `Run.Result = "Success"`
- `DryRun` comes from latest valid history document `Run.DryRun`
- `Pulled` comes from latest valid history document `Summary.Pulled`
- `Mapped` comes from latest valid history document `Summary.Mapped`
- `Created` comes from latest valid history document `Summary.AssetsCreated`
- `Updated` comes from latest valid history document `Summary.AssetsUpdated`
- `Skipped` comes from latest valid history document `Summary.AssetsSkipped`
- `Failed` comes from latest valid history document `Summary.AssetsFailed`
- `LastError` is `null` when latest result is success; otherwise first latest failure message or `"Sync failed."`

## 5. Options

### 5.1 `SyncStatusStoreOptions`

File:

```text
src/AteraSnipeSync.Core/Status/SyncStatusStoreOptions.cs
```

Signature:

```csharp
public sealed class SyncStatusStoreOptions
{
    public string HistoryDirectoryPath { get; init; } =
        @"C:\ProgramData\AteraSnipeSync\History";
}
```

Required comment:

```csharp
/// <summary>
/// Configures where local sync history JSON files are stored.
/// </summary>
```

Validation:

- constructor or store method must reject null/empty/whitespace `HistoryDirectoryPath`
- tests must override `HistoryDirectoryPath` with a temp directory path
- first implementation must not expose retention options because the module is required to keep all history

## 6. Persisted History Document

### 6.1 `SyncHistoryDocument`

File:

```text
src/AteraSnipeSync.Core/Status/SyncHistoryDocument.cs
```

Signature:

```csharp
internal sealed class SyncHistoryDocument
{
    public required int SchemaVersion { get; init; }
    public required SyncHistoryRunInfo Run { get; init; }
    public required SyncHistorySummary Summary { get; init; }
    public required SyncHistoryChangeSet Assets { get; init; }
    public required SyncHistoryChangeSet Companies { get; init; }
    public required SyncHistoryChangeSet Models { get; init; }
    public required SyncHistoryChangeSet Manufacturers { get; init; }
    public required SyncHistoryChangeSet Categories { get; init; }
    public required IReadOnlyList<SyncHistoryWarning> Warnings { get; init; }
    public required IReadOnlyList<SyncHistoryFailure> Failures { get; init; }
}
```

Required comment:

```csharp
/// <summary>
/// Represents one persisted sync run history record for TrayApp parsing and diagnostics.
/// </summary>
```

Rules:

- `SchemaVersion` must be `1`
- one file contains exactly one completed sync run
- no API keys
- no tokens
- no raw Atera payloads
- no raw Snipe-IT payloads
- no unbounded raw asset inventory dump

### 6.2 `SyncHistoryRunInfo`

File:

```text
src/AteraSnipeSync.Core/Status/SyncHistoryRunInfo.cs
```

Signature:

```csharp
internal sealed class SyncHistoryRunInfo
{
    public required string RunId { get; init; }
    public required string Result { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset FinishedAtUtc { get; init; }
    public required long DurationMs { get; init; }
    public required bool DryRun { get; init; }
}
```

Required comment:

```csharp
/// <summary>
/// Captures run-level timing and result metadata for one persisted sync history file.
/// </summary>
```

Rules:

- `RunId` is a new GUID string generated by Status Store
- `Result` is `"Success"` when `SyncRunResult.Success = true`; otherwise `"Failed"`
- `StartedAtUtc` is `SyncRunResult.StartedAt.ToUniversalTime()`
- `FinishedAtUtc` is `SyncRunResult.FinishedAt.ToUniversalTime()`
- `DurationMs` is non-negative difference between finished and started
- `DryRun` is `SyncRunResult.DryRun`; it must not be inferred from whether import began.

### 6.3 `SyncHistorySummary`

File:

```text
src/AteraSnipeSync.Core/Status/SyncHistorySummary.cs
```

Signature:

```csharp
internal sealed class SyncHistorySummary
{
    public required int Pulled { get; init; }
    public required int Mapped { get; init; }
    public required int AssetsCreated { get; init; }
    public required int AssetsUpdated { get; init; }
    public required int AssetsDeleted { get; init; }
    public required int AssetsSkipped { get; init; }
    public required int AssetsFailed { get; init; }
    public required int CompaniesCreated { get; init; }
    public required int CompaniesUpdated { get; init; }
    public required int CompaniesDeleted { get; init; }
    public required int ModelsCreated { get; init; }
    public required int ModelsUpdated { get; init; }
    public required int ModelsDeleted { get; init; }
    public required int WarningCount { get; init; }
    public required int FailureCount { get; init; }
}
```

Required comment:

```csharp
/// <summary>
/// Stores count-level sync history totals for fast UI summaries without parsing every item list.
/// </summary>
```

Rules:

- `Pulled = result.PullResult?.Summary.AgentCount ?? 0`
- `Mapped = result.ImportBatch?.Summary.MappedAssetCount ?? 0`
- `AssetsCreated = result.ImportResult?.CreatedAssets ?? 0`
- `AssetsUpdated = result.ImportResult?.UpdatedAssets ?? 0`
- `AssetsDeleted = result.ImportResult?.DeletedAssets ?? 0`
- `AssetsSkipped = result.ImportResult?.SkippedAssets ?? 0`
- `AssetsFailed = result.ImportResult?.FailedAssets ?? 0`; run/pipeline failures are counted only by `FailureCount`.
- `CompaniesCreated = result.ImportResult?.CreatedCompanies ?? 0`
- `CompaniesUpdated = 0` unless future import result exposes structured company updates
- `CompaniesDeleted = 0`
- `ModelsCreated = result.ImportResult?.CreatedModels ?? 0`
- `ModelsUpdated = 0` unless future import result exposes structured model updates
- `ModelsDeleted = 0`
- `CategoriesCreated = result.ImportResult?.CreatedCategories ?? 0`
- `CategoriesUpdated = 0` unless future import result exposes structured category updates
- `CategoriesDeleted = 0`
- `WarningCount = result.Warnings.Count`
- `FailureCount = result.Failures.Count`

### 6.4 `SyncHistoryChangeSet`

File:

```text
src/AteraSnipeSync.Core/Status/SyncHistoryChangeSet.cs
```

Signature:

```csharp
internal sealed class SyncHistoryChangeSet
{
    public required IReadOnlyList<SyncHistoryItem> Created { get; init; }
    public required IReadOnlyList<SyncHistoryItem> Updated { get; init; }
    public required IReadOnlyList<SyncHistoryItem> Deleted { get; init; }
    public required IReadOnlyList<SyncHistoryItem> Skipped { get; init; }
    public required IReadOnlyList<SyncHistoryItem> Failed { get; init; }
}
```

Required comment:

```csharp
/// <summary>
/// Groups structured per-resource changes by action outcome for one sync history record.
/// </summary>
```

Rules:

- all arrays must be present, even when empty
- first implementation must set every `Deleted` list to empty
- do not omit empty arrays because TrayApp should not need special null handling

### 6.5 `SyncHistoryItem`

File:

```text
src/AteraSnipeSync.Core/Status/SyncHistoryItem.cs
```

Signature:

```csharp
internal sealed class SyncHistoryItem
{
    public required string Source { get; init; }
    public required string Action { get; init; }
    public required string TargetType { get; init; }
    public required string Name { get; init; }
    public string? Identifier { get; init; }
    public required bool WasExecuted { get; init; }
    public string? Message { get; init; }
}
```

Required comment:

```csharp
/// <summary>
/// Describes one structured resource action from a sync run without requiring UI parsing of dense strings.
/// </summary>
```

Rules:

- `Source` should be `"SnipeImport"` for items derived from import actions/failures
- `Action` should use normalized values: `"Created"`, `"Updated"`, `"Deleted"`, `"Skipped"`, `"Failed"`
- `TargetType` should use normalized values: `"Asset"`, `"Company"`, `"Model"`, `"Manufacturer"`, `"Category"`
- `Name` must be the best available human-readable target name
- `Identifier` may hold serial number, asset tag, source agent id, Snipe-IT id, or other stable identifier if available
- `Message` is optional and must not be the only place where action/result/type/name are stored

### 6.6 `SyncHistoryWarning`

Signature:

```csharp
/// <summary>
/// Stores one non-fatal warning from a sync run in a structured history-friendly shape.
/// </summary>
internal sealed class SyncHistoryWarning
{
    public required string Source { get; init; }
    public string? Code { get; init; }
    public required string Message { get; init; }
}
```

### 6.7 `SyncHistoryFailure`

Signature:

```csharp
/// <summary>
/// Stores one run-level failure from a sync run in a structured history-friendly shape.
/// </summary>
internal sealed class SyncHistoryFailure
{
    public required string Stage { get; init; }
    public string? Code { get; init; }
    public required string Message { get; init; }
}
```

## 7. JSON Shape

Example JSON:

```json
{
  "schemaVersion": 1,
  "run": {
    "runId": "9fb16be12c4b48ff9af5ed7617dd9b51",
    "result": "Success",
    "startedAtUtc": "2026-06-19T18:30:00Z",
    "finishedAtUtc": "2026-06-19T18:30:45Z",
    "durationMs": 45000,
    "dryRun": false
  },
  "summary": {
    "pulled": 10,
    "mapped": 10,
    "assetsCreated": 2,
    "assetsUpdated": 7,
    "assetsDeleted": 0,
    "assetsSkipped": 1,
    "assetsFailed": 0,
    "companiesCreated": 1,
    "companiesUpdated": 0,
    "companiesDeleted": 0,
    "modelsCreated": 1,
    "modelsUpdated": 0,
    "modelsDeleted": 0,
    "warningCount": 0,
    "failureCount": 0
  },
  "assets": {
    "created": [
      {
        "source": "SnipeImport",
        "action": "Created",
        "targetType": "Asset",
        "name": "LAPTOP-001",
        "identifier": "SN-001",
        "wasExecuted": true,
        "message": null
      }
    ],
    "updated": [],
    "deleted": [],
    "skipped": [],
    "failed": []
  },
  "companies": {
    "created": [],
    "updated": [],
    "deleted": [],
    "skipped": [],
    "failed": []
  },
  "models": {
    "created": [],
    "updated": [],
    "deleted": [],
    "skipped": [],
    "failed": []
  },
  "manufacturers": {
    "created": [],
    "updated": [],
    "deleted": [],
    "skipped": [],
    "failed": []
  },
  "categories": {
    "created": [],
    "updated": [],
    "deleted": [],
    "skipped": [],
    "failed": []
  },
  "warnings": [],
  "failures": []
}
```

## 8. Store Implementation

### 8.1 `JsonFileSyncStatusStore`

File:

```text
src/AteraSnipeSync.Core/Status/JsonFileSyncStatusStore.cs
```

Signature:

```csharp
public sealed class JsonFileSyncStatusStore : ISyncStatusStore
{
    public JsonFileSyncStatusStore(
        SyncStatusStoreOptions options,
        ILogger<JsonFileSyncStatusStore> logger);

    public Task SaveAsync(
        SyncRunResult result,
        CancellationToken cancellationToken);

    public Task<SyncStatusSnapshot?> ReadLatestAsync(
        CancellationToken cancellationToken);
}
```

Required comment:

```csharp
/// <summary>
/// Persists each completed sync run as a structured local JSON history file and reads the latest snapshot.
/// </summary>
```

Constructor responsibilities:

- validate `options` is not null
- validate `logger` is not null
- validate `options.HistoryDirectoryPath` is not null/empty/whitespace
- store the full history directory path

`SaveAsync` responsibilities:

- validate `result` is not null
- call `cancellationToken.ThrowIfCancellationRequested()` before disk work
- create parent history directory if missing
- convert `SyncRunResult` to `SyncHistoryDocument`
- generate file name from `result.FinishedAt.ToUniversalTime()`
- avoid overwriting an existing history file by appending short GUID suffix if needed
- serialize `SyncHistoryDocument` with camelCase JSON property names and indented output
- write to a temp file in the same directory
- atomically move temp file to final file name
- delete temp file after success or failed move when possible
- log information after successful write

`ReadLatestAsync` responsibilities:

- call `cancellationToken.ThrowIfCancellationRequested()` before disk work
- return `null` when history directory is missing or empty
- enumerate `SyncResult_*.json`
- order files newest first by parsed UTC timestamp from file name, falling back to last write time if parsing fails
- deserialize newest valid `SyncHistoryDocument`
- skip malformed or unsupported files and log warning
- scan valid history files as needed to find latest success time
- return `SyncStatusSnapshot` derived from latest valid document and latest success document
- return `null` when no valid history document exists

Cancellation behavior:

- `OperationCanceledException` must be allowed to propagate from both public methods
- do not log cancellation as an error

## 9. File Naming

Create a private helper:

```csharp
private static string CreateHistoryFileName(DateTimeOffset finishedAtUtc);
```

Rules:

- use `finishedAtUtc.ToUniversalTime()`
- format as `SyncResult_yyyyMMdd_HHmmss_fffffffZ.json`
- use invariant culture
- do not use local time
- do not include `:` in the file name

Conflict handling:

```text
SyncResult_yyyyMMdd_HHmmss_fffffffZ_{shortGuid}.json
```

The short GUID suffix should only be used if the base file name already exists.

## 10. JSON Serialization

Use `System.Text.Json`.

Required options:

```csharp
private static readonly JsonSerializerOptions JsonOptions = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
};
```

Read behavior:

- unsupported/missing required fields may throw `JsonException`; handle as malformed file
- unsupported `SchemaVersion` must be skipped with warning

Write behavior:

- no API keys
- no tokens
- no raw payloads
- no dense string blobs that combine action/result/type/name

## 11. Atomic Write Detail

Implementation must write to the same directory as target file:

```text
{final-file-name}.{Guid.NewGuid():N}.tmp
```

Replacement rules:

- final history file must not already exist
- write complete JSON to temp path
- close temp file
- use `File.Move(tempPath, finalPath)` to create the final history file
- if final path exists, generate a new conflict-safe final path and retry
- cleanup should best-effort delete leftover temp file

Do not enumerate and delete unrelated `*.tmp` files in the history directory.

## 12. Mapping From `SyncRunResult`

Create a private helper:

```csharp
private static SyncHistoryDocument CreateHistoryDocument(
    SyncRunResult result);
```

Run info:

```csharp
Result = result.Success ? "Success" : "Failed";
StartedAtUtc = result.StartedAt.ToUniversalTime();
FinishedAtUtc = result.FinishedAt.ToUniversalTime();
DryRun = result.DryRun;
DurationMs = Math.Max(0, (long)(FinishedAtUtc - StartedAtUtc).TotalMilliseconds);
```

Summary counts:

```csharp
Pulled = result.PullResult?.Summary.AgentCount ?? 0;
Mapped = result.ImportBatch?.Summary.MappedAssetCount ?? 0;
AssetsCreated = result.ImportResult?.CreatedAssets ?? 0;
AssetsUpdated = result.ImportResult?.UpdatedAssets ?? 0;
AssetsDeleted = result.ImportResult?.DeletedAssets ?? 0;
AssetsSkipped = result.ImportResult?.SkippedAssets ?? 0;
AssetsFailed = result.ImportResult?.FailedAssets ?? 0;
CompaniesCreated = result.ImportResult?.CreatedCompanies ?? 0;
CompaniesUpdated = 0;
CompaniesDeleted = 0;
ModelsCreated = result.ImportResult?.CreatedModels ?? 0;
ModelsUpdated = 0;
ModelsDeleted = 0;
CategoriesCreated = result.ImportResult?.CreatedCategories ?? 0;
CategoriesUpdated = 0;
CategoriesDeleted = 0;
WarningCount = result.Warnings.Count;
FailureCount = result.Failures.Count;
```

Action grouping:

- derive structured items from `result.ImportResult.Actions`
- normalize `TargetType` into `Asset`, `Company`, `Model`, `Manufacturer`, or `Category`
- normalize `ActionType` into `Created`, `Updated`, `Deleted`, `Skipped`, or `Failed`
- place items into the corresponding resource change set and action array
- first implementation must keep all `Deleted` arrays empty unless an explicit `Deleted` action exists in future

Failure grouping:

- convert `SyncRunResult.Failures` to top-level `failures`
- import asset failures may also appear under `assets.failed` when enough target information exists
- do not invent missing target identifiers

## 13. Logging

Use `ILogger<JsonFileSyncStatusStore>`.

Required log events:

- Information: history file saved successfully, include file path and result
- Debug: history directory missing or empty during read
- Warning: malformed history file skipped
- Warning: unsupported schema version skipped
- Warning or Error: IO read failure
- Error: write failure before rethrow

Logs must not include secrets, raw payloads, or full asset lists.

## 14. Test Cases

Create `tests/AteraSnipeSync.Tests/Status/JsonFileSyncStatusStoreTests.cs`.

Required tests:

1. `SaveAsync_WritesSuccessfulHistoryFile`
2. `SaveAsync_WritesFailedHistoryFile`
3. `SaveAsync_CreatesNewFile_ForEveryRun`
4. `SaveAsync_UsesUtcFinishedTimestampInFileName`
5. `SaveAsync_DoesNotOverwrite_WhenFileNameConflicts`
6. `SaveAsync_WritesStructuredAssetCreatedUpdatedSkippedFailedLists`
7. `SaveAsync_WritesStructuredCompanyCategoryAndModelLists`
8. `SaveAsync_WritesDeletedArraysAsEmpty_WhenNoDeleteActionsExist`
9. `SaveAsync_DoesNotPersistSecretsOrRawPayloads`
10. `ReadLatestAsync_ReturnsNull_WhenHistoryDirectoryMissing`
11. `ReadLatestAsync_ReturnsNull_WhenHistoryDirectoryEmpty`
12. `ReadLatestAsync_ReturnsNewestValidSnapshot`
13. `ReadLatestAsync_SkipsMalformedNewestFile_AndReadsNextValidFile`
14. `ReadLatestAsync_ComputesLastSuccessAt_FromHistory`
15. `ReadLatestAsync_ReturnsNull_WhenAllHistoryFilesMalformed`
16. `Constructor_ThrowsArgumentNullException_ForNullOptions`
17. `Constructor_ThrowsArgumentNullException_ForNullLogger`
18. `Constructor_ThrowsArgumentException_ForBlankHistoryDirectory`
19. `SaveAsync_ThrowsArgumentNullException_WhenResultNull`
20. `SaveAsync_HonorsCancellation`
21. `ReadLatestAsync_HonorsCancellation`

Testing rules:

- use temporary directories under the test runner temp path
- construct `SyncRunResult` objects in memory
- construct `SnipeImportResult.Actions` with hand-written `ImportAction` records
- do not use `HttpClient`
- do not call Atera API
- do not call Snipe-IT API
- do not use real API keys or tokens
- delete temporary test directories after each test when practical

## 15. Acceptance Criteria

Implementation is accepted when:

- `dotnet build` succeeds
- `dotnet test` succeeds
- all required Status Store tests pass
- each run writes a distinct `SyncResult_*.json`
- file names use UTC finished timestamps
- JSON contains structured resource change sets for assets, companies, models, manufacturers, and categories
- deleted arrays are always present; asset delete actions are preserved with name, identifier, execution flag and safe message
- `ReadLatestAsync` reads newest valid history and computes latest success time from history
- missing/empty/malformed history does not crash `ReadLatestAsync`
- no secrets or raw API payloads are persisted
- `progress.md` is updated in the same work session as code/tests

## 16. 2026-07 原子写入与保留策略

`SyncStatusStoreOptions` 新增：

```csharp
public TimeSpan HistoryRetentionAge { get; init; } = TimeSpan.FromDays(90);
public int MaxHistoryFiles { get; init; } = 500;
public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(15);
```

新增 `internal static class AtomicFileWriter`，`WriteAllTextAsync(path, contents, token)` 先写 `{path}.{guid}.tmp`，flush 后同卷 `File.Move(temp, path, true)`，finally 清理 temp。`JsonFileSyncStatusStore` 在现有 semaphore 外获取 `<status-root>/.status.lock` 的 exclusive `FileStream(FileShare.None)`；timeout 抛 `TimeoutException`。保存 report/latest 成功后按 last-write UTC 删除超龄文件，再保留最新 `MaxHistoryFiles` 个。锁与清理都必须有单元测试。

## 17. 2026-07 Completion classification and deleted snapshot count

`JsonFileSyncStatusStore.CreateHistoryDocument` classifies only from the orchestrator completion contract：

1. `result.Success == true` → `"Success"`。
2. Otherwise → `"Failed"`。

Resource success counters must not alter this classification. A completed run may contain non-zero failed counts and failure arrays; an incomplete/fatal run remains `Failed` even when some resource actions completed before the interruption. New reports must not write `PartialSuccess`.

`assetsSkipped` remains the persisted schema-v1 name and means unchanged/no HTTP write；do not rename the JSON property without a schema migration。`assetsDeleted` maps the real `SnipeImportResult.DeletedAssets` count。

`SyncStatusSnapshot` adds `public required int Deleted { get; init; }` mapped from `Summary.AssetsDeleted`。`Skipped` remains mapped from `AssetsSkipped` for wire compatibility and is labeled `No change` only at the Tray presentation boundary。`IsSuccess` matches `Success`; a completed run with record failures advances `LastSuccessAt`, while an incomplete/fatal run retains `LastError`。

Required tests：completed run with record failures → Success and preserves structured failure details；failed with no successful counters → Failed；failed with created/updated/skipped/deleted or reference success counters → Failed；snapshot exposes the real Deleted count and preserves the first failure for fatal runs；deleted history item preserves the Snipe-IT asset id in `Identifier`。

## 18. Dry-run and failure-count source of truth

`SyncRunResult` adds `public required bool DryRun { get; init; }`. `SyncOrchestrator.CreateResult` copies `request.Sync.DryRun` into every success, early-return and exception result.

`JsonFileSyncStatusStore`、`WorkerResultSanitizer` and `NotificationRequestFactory` use exactly:

```csharp
var failedAssets = result.ImportResult?.FailedAssets ?? 0;
var dryRun = result.DryRun;
var failureCount = result.Failures.Count;
```

No caller-supplied DryRun fallback remains. Required tests cover Atera Pull failure, Mapping failure, import-stage exception without `ImportResult`, structured asset failure and successful dry-run projection across history, IPC and notification text.
