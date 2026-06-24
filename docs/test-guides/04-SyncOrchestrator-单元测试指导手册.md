# Sync Orchestrator - 单元测试指导手册

## 1. 测试目标

本测试手册覆盖 Module4 Sync Orchestrator 当前实现：

- `SyncOrchestrator` 按固定顺序调用 Atera Pull、Reconstruction、Snipe Import
- 任一阶段失败时正确短路
- warnings 从各阶段聚合到 `SyncRunResult.Warnings`
- import asset failures 转换为 run-level `SyncFailure`
- `SyncRunOptions.DryRun` 正确传递到 `SnipeImportOptions.DryRun`
- cancellation 不被包装成普通失败
- started/finished timestamps 可预测

本模块测试不调用真实 Atera 或 Snipe-IT API。

## 2. 测试文件

当前测试位于：

```text
tests/AteraSnipeSync.Tests/Sync/SyncOrchestratorTests.cs
```

Production code 位于：

```text
src/AteraSnipeSync.Core/Sync/SyncOrchestrator.cs
src/AteraSnipeSync.Core/Sync/Interfaces/ISyncOrchestrator.cs
src/AteraSnipeSync.Core/Sync/SyncRunRequest.cs
src/AteraSnipeSync.Core/Sync/SyncRunOptions.cs
src/AteraSnipeSync.Core/Sync/SyncRunResult.cs
src/AteraSnipeSync.Core/Common/SyncFailure.cs
```

## 3. 运行命令

在 repo root 运行：

```powershell
dotnet build
dotnet test
```

本次验证结果：

```text
dotnet build
Build succeeded.
0 Warning(s)
0 Error(s)

dotnet test
Passed! - Failed: 0, Passed: 72, Skipped: 0, Total: 72
```

## 4. Mocking / Fake 策略

`SyncOrchestratorTests` 使用 in-memory fake dependencies：

- `FakeAteraClient : IAteraClient`
- `FakeInventoryMapper : IInventoryMapper`
- `FakeSnipeImporter : ISnipeImporter`
- `FixedStepTimeProvider : TimeProvider`

这些 fake 只记录调用顺序、返回预设对象或抛出预设异常。

测试不使用：

- `HttpClient`
- Atera API key
- Snipe-IT API token
- fixture JSON from real APIs
- real network calls

这符合项目 Real API Testing Policy：自动化测试必须使用 fake/mock/local fixture，不得调用真实 Atera 或 Snipe-IT。

## 5. 覆盖用例

当前测试覆盖：

1. `RunOnceAsync_CallsStagesInOrder_AndReturnsSuccessfulResult`
2. `RunOnceAsync_AggregatesWarnings_FromAllStages`
3. `RunOnceAsync_StopsBeforeMappingAndImport_WhenPullFails`
4. `RunOnceAsync_StopsBeforeImport_WhenMappingFails`
5. `RunOnceAsync_ReturnsFailureAndKeepsPriorResults_WhenImportThrows`
6. `RunOnceAsync_ConvertsImportFailuresToRunFailures`
7. `RunOnceAsync_AppliesSyncDryRunToSnipeOptions`
8. `RunOnceAsync_RethrowsOperationCanceledException`
9. `RunOnceAsync_ReportsStageProgress`
10. `Constructor_ThrowsArgumentNullException_ForNullDependencies`
11. `RunOnceAsync_ThrowsArgumentNullException_WhenRequestNull`

## 6. 重点断言

### 6.1 正常流程

正常流程必须按以下顺序调用：

```text
pull -> map -> import
```

成功结果必须满足：

- `Success = true`
- `PullResult` populated
- `ImportBatch` populated
- `ImportResult` populated
- `Failures` empty

### 6.2 Pull 失败

当 `IAteraClient.PullInventoryAsync` 抛出普通异常：

- 只调用 pull
- 不调用 mapper
- 不调用 importer
- `Success = false`
- `PullResult = null`
- `ImportBatch = null`
- `ImportResult = null`
- `Failures[0].Stage = "AteraPull"`

### 6.3 Mapping 失败

当 `IInventoryMapper.Map` 抛出普通异常：

- 调用 pull 和 map
- 不调用 importer
- 保留 `PullResult`
- `ImportBatch = null`
- `ImportResult = null`
- `Failures[0].Stage = "Reconstruction"`

### 6.4 Import 抛异常

当 `ISnipeImporter.ImportAsync` 抛出普通异常：

- 调用 pull、map、import
- 保留 `PullResult`
- 保留 `ImportBatch`
- `ImportResult = null`
- `Failures[0].Stage = "SnipeImport"`

### 6.5 Import 返回 failures

当 `SnipeImportResult.Failures` 非空：

- `ImportResult` 必须保留
- `Success = false`
- 每条 `ImportFailure` 转换为 `Stage = "SnipeImport"` 的 `SyncFailure`

### 6.6 Dry Run

测试故意构造：

```text
Sync.DryRun = false
SnipeIt.DryRun = true
```

期望 importer 收到：

```text
options.DryRun = false
```

这验证顶层 `SyncRunOptions.DryRun` 是一次 sync run 的最终 dry-run 意图。

### 6.7 Cancellation

当任一阶段抛出 `OperationCanceledException`：

- `RunOnceAsync` 必须 rethrow
- 不返回 `SyncRunResult`
- 不把 cancellation 转成 `SyncFailure`

当前测试覆盖 pull 阶段 cancellation；后续如实现更复杂 cancellation 行为，可增加 map/import 阶段测试。

## 7. 常见失败原因

### 7.1 调用顺序错

如果测试显示 call list 不是：

```text
pull, map, import
```

检查 `SyncOrchestrator.RunOnceAsync` 是否提前调用 importer，或在 pull/map 失败后仍继续执行。

### 7.2 Dry Run 不一致

如果 `RunOnceAsync_AppliesSyncDryRunToSnipeOptions` 失败，检查 `ApplySyncOptions` 是否用 `request.Sync.DryRun` 覆盖了 `request.SnipeIt.DryRun`。

### 7.3 Warnings 丢失

如果 warnings aggregation 测试失败，检查是否按以下顺序 `AddRange`：

```text
AteraPullResult.Warnings
SnipeImportBatch.Warnings
SnipeImportResult.Warnings
```

### 7.4 Cancellation 被吞掉

如果 cancellation 测试失败，检查 catch block 是否先 catch 了 `Exception`。应在每个阶段先 catch `OperationCanceledException` 并 rethrow。

## 8. 后续扩展测试

后续增加 run id、status snapshot、notification hook 或 per-stage timing 时，应补充：

- run id 保留和日志关联测试
- status snapshot construction 测试
- failure notification request construction 测试
- per-stage timing deterministic tests

这些测试仍应使用 fake dependencies，不调用真实 API。
