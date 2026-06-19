# Sync Orchestrator - 功能职责

## 1. 模块目标

Sync Orchestrator Module 负责控制一次完整 sync run 的业务流程。

本模块把以下三个核心模块串联起来：

- Atera Pull Module
- Reconstruction Module
- Snipe Import Module

本模块只负责编排、阶段短路、warning/failure 聚合和最终 `SyncRunResult` 生成。它不直接访问 Atera API 或 Snipe-IT API，不做字段 mapping，不负责 UI、状态保存或通知发送。

## 2. 输入

本模块接收 `SyncRunRequest`：

- `AteraPullRequest Atera`
- `MappingOptions Mapping`
- `SnipeImportOptions SnipeIt`
- `SyncRunOptions Sync`

`SyncRunOptions` 必须包含：

- `DryRun`
- `TriggeredBy`

调用方负责从配置系统、TrayApp、Scheduler 或测试 fixture 构造 request。本模块不读取配置文件、环境变量、user secrets 或 UI 控件。

## 3. 输出

本模块返回 `SyncRunResult`，包含：

- run success flag
- start timestamp
- finish timestamp
- optional `AteraPullResult`
- optional `SnipeImportBatch`
- optional `SnipeImportResult`
- aggregated warnings
- aggregated failures

当某个阶段失败时，已成功完成的前序阶段结果必须尽量保留，方便 Status Store、TrayApp、Scheduler、日志和测试入口诊断。

## 4. 对外接口

本模块通过 `ISyncOrchestrator` 暴露能力：

```csharp
public interface ISyncOrchestrator
{
    Task<SyncRunResult> RunOnceAsync(
        SyncRunRequest request,
        CancellationToken cancellationToken);
}
```

## 5. 正常流程

一次 run 必须按以下顺序执行：

1. 记录 `StartedAt`
2. 调用 `IAteraClient.PullInventoryAsync`
3. 把 `AteraPullResult.Warnings` 加入 run warnings
4. 调用 `IInventoryMapper.Map`
5. 把 `SnipeImportBatch.Warnings` 加入 run warnings
6. 调用 `ISnipeImporter.ImportAsync`
7. 把 `SnipeImportResult.Warnings` 加入 run warnings
8. 把 `SnipeImportResult.Failures` 转成 run-level `SyncFailure`
9. 记录 `FinishedAt`
10. 返回 `SyncRunResult`

如果所有阶段都成功，且 import result 没有 failures，则 `SyncRunResult.Success = true`。

## 6. Dry Run 和 TriggeredBy

本模块必须保留 `SyncRunOptions.TriggeredBy` 的语义，用于日志、测试和后续 status/notification/report。

本模块必须确保 `SyncRunOptions.DryRun` 控制传给 Snipe Import Module 的 `SnipeImportOptions.DryRun`。即使调用方传入的 `request.SnipeIt.DryRun` 与 `request.Sync.DryRun` 不一致，本模块也应以 `request.Sync.DryRun` 作为一次 sync run 的顶层 dry-run 意图。

## 7. 失败条件

### 7.1 Atera Pull 失败

如果 Atera Pull 抛出不可恢复异常：

- 停止流程
- 不调用 Reconstruction
- 不调用 Snipe Import
- 返回 `Success = false`
- 添加 `Stage = "AteraPull"` 的 `SyncFailure`

### 7.2 Reconstruction 失败

如果 Reconstruction 抛出不可恢复异常：

- 保留已成功的 `PullResult`
- 停止流程
- 不调用 Snipe Import
- 返回 `Success = false`
- 添加 `Stage = "Reconstruction"` 的 `SyncFailure`

### 7.3 Snipe Import 失败

如果 Snipe Import 抛出不可恢复异常：

- 保留已成功的 `PullResult`
- 保留已生成的 `ImportBatch`
- 返回 `Success = false`
- 添加 `Stage = "SnipeImport"` 的 `SyncFailure`

如果 Snipe Import 正常返回，但 `SnipeImportResult.Failures` 非空：

- 保留 `ImportResult`
- 返回 `Success = false`
- 把每条 import failure 转换为 `Stage = "SnipeImport"` 的 `SyncFailure`

### 7.4 Cancellation

`OperationCanceledException` 必须继续向调用方抛出，不应包装成 `SyncRunResult`。Scheduler 或 UI 调用方负责处理取消。

## 8. 不负责的事情

Sync Orchestrator Module 不负责：

- Atera API endpoint、authentication、pagination、DTO parsing
- Snipe-IT API endpoint、authentication、payload shape、search/create/update behavior
- Atera 到 Snipe-IT 字段 mapping
- status file 保存或读取
- notification/email 发送
- TrayApp UI 展示
- schedule 计算、防止重叠 run、后台循环
- 真实 API key 验证

这些职责分别属于 Atera Pull、Reconstruction、Snipe Import、Status Store、Notification、TrayApp 或 Worker Scheduler 模块。

## 9. 依赖边界

本模块允许依赖：

- `IAteraClient`
- `IInventoryMapper`
- `ISnipeImporter`
- `TimeProvider`
- `ILogger<SyncOrchestrator>`
- shared result/warning/failure models

本模块不应依赖：

- `HttpClient`
- Atera/Snipe-IT concrete HTTP request models
- file system status store implementation
- notification sender implementation
- TrayApp controls
- worker service loop

## 10. 成功条件

本模块完成后应满足：

- 正常流程按顺序调用三个核心模块
- pull 失败时不继续 map/import
- map 失败时不继续 import
- import 抛异常时输出 structured failure
- import 返回 asset failures 时输出 structured run failure
- warnings 被完整聚合
- dry-run 参数正确传递到 Snipe Import
- started/finished timestamps 可通过测试时间来源验证
- 自动化测试全部使用 fake/mock modules，不调用真实 Atera 或 Snipe-IT API

## 11. 扩展点

后续可以在不改变模块边界的前提下扩展：

- run id / correlation id
- richer failure codes
- elapsed duration
- structured per-stage timing
- optional notification hook request construction
- optional status snapshot construction

这些扩展不得把 API wire behavior、UI behavior 或持久化职责混入本模块。
