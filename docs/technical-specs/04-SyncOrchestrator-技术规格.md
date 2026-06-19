# Sync Orchestrator - 技术规格

## 1. Scope

Sync Orchestrator Module 实现 `ISyncOrchestrator`，把 Atera Pull、Reconstruction、Snipe Import 三个模块组合成一次完整 sync run。

本技术规格基于：

- `docs/module-plans/04-SyncOrchestrator-功能职责.md`
- `docs/AteraSnipeSync_AI_Agent_Master_Plan.md` 的 `Module 4 - Sync Orchestrator`

本模块不新增或修改 Atera/Snipe-IT API wire contracts，不新增真实 API 测试。

## 2. Namespace and Files

Production code:

- `src/AteraSnipeSync.Core/Sync/SyncOrchestrator.cs`
- existing `src/AteraSnipeSync.Core/Sync/Interfaces/ISyncOrchestrator.cs`
- existing `src/AteraSnipeSync.Core/Sync/SyncRunRequest.cs`
- existing `src/AteraSnipeSync.Core/Sync/SyncRunOptions.cs`
- existing `src/AteraSnipeSync.Core/Sync/SyncRunResult.cs`
- existing `src/AteraSnipeSync.Core/Common/SyncFailure.cs`

Tests:

- `tests/AteraSnipeSync.Tests/Sync/SyncOrchestratorTests.cs`

All production sync classes live in namespace:

```csharp
namespace AteraSnipeSync.Core.Sync;
```

## 3. Public Class

### 3.1 `SyncOrchestrator`

```csharp
public sealed class SyncOrchestrator : ISyncOrchestrator
{
    public SyncOrchestrator(
        IAteraClient ateraClient,
        IInventoryMapper inventoryMapper,
        ISnipeImporter snipeImporter,
        ILogger<SyncOrchestrator> logger);

    public SyncOrchestrator(
        IAteraClient ateraClient,
        IInventoryMapper inventoryMapper,
        ISnipeImporter snipeImporter,
        ILogger<SyncOrchestrator> logger,
        TimeProvider timeProvider);

    public Task<SyncRunResult> RunOnceAsync(
        SyncRunRequest request,
        CancellationToken cancellationToken);
}
```

Responsibilities:

- validate constructor dependencies
- validate request is not null
- record start and finish timestamps using `TimeProvider.GetUtcNow()`
- call `IAteraClient`, `IInventoryMapper`, `ISnipeImporter` in order
- short-circuit later stages after stage failure
- aggregate warnings
- convert stage exceptions to `SyncFailure`
- convert `SnipeImportResult.Failures` to run-level `SyncFailure`
- return `SyncRunResult`

The constructor taking `TimeProvider` exists for deterministic unit tests.

## 4. Existing Interfaces Consumed

### 4.1 `IAteraClient`

```csharp
Task<AteraPullResult> PullInventoryAsync(
    AteraPullRequest request,
    CancellationToken cancellationToken);
```

The orchestrator passes `request.Atera` and the same cancellation token.

### 4.2 `IInventoryMapper`

```csharp
SnipeImportBatch Map(
    AteraPullResult source,
    MappingOptions options);
```

The orchestrator passes the successful pull result and `request.Mapping`.

### 4.3 `ISnipeImporter`

```csharp
Task<SnipeImportResult> ImportAsync(
    SnipeImportBatch batch,
    SnipeImportOptions options,
    CancellationToken cancellationToken);
```

The orchestrator passes the successful import batch, a Snipe options copy whose `DryRun` equals `request.Sync.DryRun`, and the same cancellation token.

## 5. Dry Run Option Copy

`SnipeImportOptions` is a sealed class with init-only properties. The orchestrator must create a copy before calling importer:

```csharp
private static SnipeImportOptions ApplySyncOptions(
    SnipeImportOptions source,
    SyncRunOptions sync);
```

The returned options must preserve:

- `BaseUrl`
- `ApiToken`
- `CreateMissingCompanies`
- `CreateMissingModels`
- `MacAddressCustomFieldDbColumnName`
- `NameMatchThreshold`
- `ManualPreflightCsvEnabled`
- `ManualPreflightCsvDirectory`

The returned options must set:

- `DryRun = sync.DryRun`

## 6. Run Flow

`RunOnceAsync` must:

1. `ArgumentNullException.ThrowIfNull(request)`
2. create local `warnings` and `failures` lists
3. set `startedAt = timeProvider.GetUtcNow()`
4. call Atera Pull
5. on pull success, append `pullResult.Warnings`
6. on pull failure, append `SyncFailure(Stage = "AteraPull")` and return failed result
7. call Reconstruction
8. on map success, append `importBatch.Warnings`
9. on map failure, append `SyncFailure(Stage = "Reconstruction")` and return failed result
10. call Snipe Import
11. on import success, append `importResult.Warnings`
12. convert each `importResult.Failures` to `SyncFailure(Stage = "SnipeImport")`
13. on import exception, append `SyncFailure(Stage = "SnipeImport")`
14. set `finishedAt = timeProvider.GetUtcNow()`
15. return `SyncRunResult`

`Success = failures.Count == 0`.

## 7. Failure Conversion

Stage exceptions must become:

```csharp
new SyncFailure
{
    Stage = stage,
    Code = exception.GetType().Name,
    Message = $"{stage} failed: {exception.Message}"
}
```

Import result failures must become:

```csharp
new SyncFailure
{
    Stage = "SnipeImport",
    Code = importFailure.Code,
    Message = $"{importFailure.TargetType} '{importFailure.TargetName}' failed: {importFailure.Message}"
}
```

The orchestrator must log stage exceptions with `ILogger<SyncOrchestrator>.LogError`.

## 8. Cancellation Behavior

`OperationCanceledException` must not be converted to `SyncRunResult`.

If any dependency throws `OperationCanceledException`, `RunOnceAsync` must rethrow it.

## 9. Result Population Rules

Pull failure result:

- `Success = false`
- `PullResult = null`
- `ImportBatch = null`
- `ImportResult = null`
- `Warnings = warnings collected before failure`
- `Failures` contains Atera pull failure

Map failure result:

- `Success = false`
- `PullResult = successful pull result`
- `ImportBatch = null`
- `ImportResult = null`
- `Warnings` includes pull warnings
- `Failures` contains reconstruction failure

Import exception result:

- `Success = false`
- `PullResult = successful pull result`
- `ImportBatch = successful import batch`
- `ImportResult = null`
- `Warnings` includes pull and mapping warnings
- `Failures` contains import exception failure

Import returned failures result:

- `Success = false`
- `PullResult = successful pull result`
- `ImportBatch = successful import batch`
- `ImportResult = returned import result`
- `Warnings` includes pull, mapping and import warnings
- `Failures` contains converted import failures

Success result:

- `Success = true`
- all three stage outputs are populated
- `Failures` is empty

## 10. Validation Behavior

Constructor arguments:

- null dependency must throw `ArgumentNullException`

`RunOnceAsync`:

- null request must throw `ArgumentNullException`
- nested request properties are not revalidated in the first implementation; existing downstream modules own their own validation

## 11. Logging

The orchestrator must log:

- information at run start with `TriggeredBy` and `DryRun`
- information at run finish with `Success`
- error when a stage throws non-cancellation exception

Log messages must not include API keys, tokens, raw payloads, or secret values.

## 12. Required Tests

Create `SyncOrchestratorTests` with fake dependencies.

Required tests:

1. `RunOnceAsync_CallsStagesInOrder_AndReturnsSuccessfulResult`
2. `RunOnceAsync_AggregatesWarnings_FromAllStages`
3. `RunOnceAsync_StopsBeforeMappingAndImport_WhenPullFails`
4. `RunOnceAsync_StopsBeforeImport_WhenMappingFails`
5. `RunOnceAsync_ReturnsFailureAndKeepsPriorResults_WhenImportThrows`
6. `RunOnceAsync_ConvertsImportFailuresToRunFailures`
7. `RunOnceAsync_AppliesSyncDryRunToSnipeOptions`
8. `RunOnceAsync_RethrowsOperationCanceledException`
9. `Constructor_ThrowsArgumentNullException_ForNullDependencies`
10. `RunOnceAsync_ThrowsArgumentNullException_WhenRequestNull`

Tests must use fake in-memory implementations of:

- `IAteraClient`
- `IInventoryMapper`
- `ISnipeImporter`

Tests must not call real Atera or Snipe-IT APIs.

## 13. Acceptance Criteria

- `dotnet build` succeeds
- `dotnet test` succeeds
- normal run calls dependencies in order
- stage failures short-circuit correctly
- import result failures make the run unsuccessful
- warnings are aggregated in pull -> mapping -> import order
- dry-run follows `SyncRunOptions.DryRun`
- cancellation is not swallowed
