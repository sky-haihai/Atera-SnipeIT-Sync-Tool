# Status Store - 功能职责

## 1. 模块目标

Status Store Module 负责把每一次完成的 sync run 保存成本机结构化历史记录，供 TrayApp、Worker Scheduler、管理员诊断入口和未来报告模块读取。

本模块第一版不再只保存一份 latest status，而是保存所有历史 sync result：

- 每次 `SaveAsync` 都生成一个新的 history JSON 文件
- history 文件名包含 UTC 日期和时间
- history 文件内容使用结构化 JSON
- TrayApp 后续负责把 UTC 时间按当前系统时区转换成用户可读时间
- `ReadLatestAsync` 通过读取 history 目录中的最新文件返回最近一次快照

本模块只负责本机持久化和读取，不执行同步流程，不判断某个阶段是否应该继续，不调用 Atera API 或 Snipe-IT API，不发送通知，也不负责 UI 展示。

## 2. 输入

本模块接收 `SyncRunResult`：

- `Success`
- `StartedAt`
- `FinishedAt`
- optional `PullResult`
- optional `ImportBatch`
- optional `ImportResult`
- aggregated `Warnings`
- aggregated `Failures`

`SyncRunResult` 必须由 Sync Orchestrator 或测试 fixture 构造。本模块不重新运行 Atera Pull、Reconstruction 或 Snipe Import。

## 3. 输出

本模块输出两类本机读取结果：

- history JSON file：每次 sync run 的完整结构化记录
- `SyncStatusSnapshot?`：最近一次 sync run 的轻量状态快照

`SaveAsync` 的主要输出是一个新的 history JSON 文件。`ReadLatestAsync` 只提供兼容 Scheduler、TrayApp status panel 和现有接口的最近一次摘要。

history file 不存在时，`ReadLatestAsync` 返回 `null`。

history file 损坏、JSON 无法解析或 schema 不兼容时，`ReadLatestAsync` 应跳过损坏文件并尝试读取下一个较新的有效文件；如果没有任何有效 history file，则返回 `null` 并记录 warning log。

## 4. 对外接口

本模块通过 `ISyncStatusStore` 暴露能力：

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

`SaveAsync` 写入已完成 run 的历史记录。当前接口没有 `BeginRunAsync` 或 heartbeat 方法，因此第一版保存的 snapshot 必须表示 completed run，`IsRunning = false`。

`ReadLatestAsync` 只读取本机 history 文件，不访问网络、不触发 sync、不修复或重写损坏文件。

## 5. History 文件命名

默认 history directory：

```text
C:\ProgramData\AteraSnipeSync\History
```

每次 run 必须写入新文件：

```text
SyncResult_yyyyMMdd_HHmmss_fffffffZ.json
```

示例：

```text
SyncResult_20260619_183045_1234567Z.json
```

命名规则：

- 日期和时间必须来自 `SyncRunResult.FinishedAt` 的 UTC 时间
- 文件名不得使用本地时间
- 文件名不得包含 Windows 不安全字符，例如 `:`
- 如果同一个 UTC tick 下发生文件名冲突，应追加短 GUID suffix，避免覆盖旧历史
- `SaveAsync` 不得删除旧 history file

## 6. History JSON 语义

history JSON 必须尽可能结构化，避免把多个业务字段拼成一个拥挤的 string。TrayApp 后续应能直接按字段解析并渲染用户可读页面。

顶层 JSON 应包含：

- `schemaVersion`
- `run`
- `summary`
- `assets`
- `companies`
- `models`
- `manufacturers`
- `categories`
- `warnings`
- `failures`

`run` 必须包含：

- `runId`
- `result`
- `startedAtUtc`
- `finishedAtUtc`
- `durationMs`
- `dryRun`

UTC 时间字段必须用 ISO 8601 round-trip string，并以 `Z` 或明确 UTC offset 表示。TrayApp 负责按系统时区转换，不应由 Status Store 保存本地时间副本。

`result` 第一版至少支持：

- `"Success"`
- `"Failed"`

后续如需要可扩展：

- `"Canceled"`
- `"PartialSuccess"`

## 7. Asset / Company / Model 结构

history JSON 必须把不同资源类型分组记录。

每个资源类型至少包含：

- `created`
- `updated`
- `deleted`
- `skipped`
- `failed`

示例结构：

```json
{
  "assets": {
    "created": [],
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
  }
}
```

每条 item 应尽量结构化，第一版至少包含：

- `source`
- `action`
- `targetType`
- `name`
- `identifier`
- `wasExecuted`
- `message`

如果已有 import action 或 failure 能提供更多结构化字段，技术规格应优先增加独立字段，而不是把详情塞进 `message`。

## 8. 删除记录规则

项目当前总原则是“不做删除”。因此第一版 Status Store 必须保留 `deleted` 数组字段，但正常情况下这些数组为空。

如果未来引入删除能力，删除行为必须由 Snipe Import 或专门删除模块产生结构化 action；Status Store 只负责保存 action，不主动判断或执行删除。

## 9. 状态快照字段语义

`ReadLatestAsync` 返回的 `SyncStatusSnapshot` 从最近一个有效 history JSON 转换得到。

字段语义如下：

- `IsRunning`: 第一版始终为 `false`，表示保存的是已完成 run
- `LastResult`: 最近 history 的 `run.result`
- `LastRunStartedAt`: 最近 history 的 `run.startedAtUtc`
- `LastRunFinishedAt`: 最近 history 的 `run.finishedAtUtc`
- `LastSuccessAt`: 最近一个成功 history 的 `run.finishedAtUtc`
- `DryRun`: 最近 history 的 `run.dryRun`
- `Pulled`: 最近 history 的 `summary.pulled`
- `Mapped`: 最近 history 的 `summary.mapped`
- `Created`: 最近 history 的 `summary.assetsCreated`
- `Updated`: 最近 history 的 `summary.assetsUpdated`
- `Skipped`: 最近 history 的 `summary.assetsSkipped`
- `Failed`: 最近 history 的 `summary.assetsFailed`
- `LastError`: 最近失败 history 的第一条 failure message；成功时为 `null`

因为本模块保存全部历史，`LastSuccessAt` 应通过扫描 history 文件找到最近一次 `result = "Success"` 的记录。

## 10. 原子写入要求

`SaveAsync` 必须使用原子写入策略，避免进程中断时留下半写 JSON。

第一版策略：

1. 确保 history directory 存在
2. 把完整 JSON 写入同目录临时文件
3. flush/close 临时文件
4. 使用 replace/move 把临时文件移动成最终 history 文件名
5. 移动成功后清理临时文件

因为每次写入都是新文件，不应覆盖任何已有 history file。若最终文件名已存在，必须生成不冲突的新文件名。

如果写入失败，必须抛出异常给调用方，并记录 error log。不得静默吞掉写入失败。

## 11. Missing / Malformed History 行为

history directory missing：

- `ReadLatestAsync` 返回 `null`
- 不记录 error log
- 可以记录 debug log

history directory 为空：

- `ReadLatestAsync` 返回 `null`
- 不记录 error log

单个 history file malformed：

- `ReadLatestAsync` 跳过该文件
- 记录 warning log
- 不自动删除、覆盖或修复原文件

所有 history file 都 malformed：

- `ReadLatestAsync` 返回 `null`
- 记录 warning log

history directory unreadable due to IO/permission：

- `ReadLatestAsync` 应记录 warning 或 error log
- 第一版可以返回 `null`，避免 UI status panel 崩溃
- `SaveAsync` 遇到权限或 IO 写入失败时必须抛出异常

## 12. Failure 行为

以下情况应导致 `SaveAsync` 失败并抛出异常：

- `result` 为 `null`
- history directory 无法创建
- 临时文件无法写入
- 临时文件无法移动为最终 history 文件
- cancellation requested

以下情况不应导致 `ReadLatestAsync` 抛出普通异常：

- history directory 不存在
- history directory 为空
- 单个 history file JSON 损坏
- 单个 history file schema 版本不兼容
- 单个 history file 中缺少可选字段

`OperationCanceledException` 不应被包装成普通失败。

## 13. 不负责事项

Status Store Module 不负责：

- 调用 Atera API
- 调用 Snipe-IT API
- 运行 Sync Orchestrator
- 创建、更新或删除 Snipe-IT asset
- 生成 manual preflight CSV
- 发送 email、toast、webhook 或其它通知
- 决定 sync 成败
- 对历史记录做 retention 删除
- 管理 Windows service 生命周期
- 渲染 TrayApp UI
- 把 UTC 时间转换成本地显示时间
- 存储 API key 或 token

这些职责分别属于 Atera Pull、Snipe Import、Sync Orchestrator、Notification、Worker Scheduler、TrayApp 或未来 Reporting 模块。

## 14. 依赖边界

本模块允许依赖：

- `System.Text.Json`
- `System.IO`
- `ILogger<JsonFileSyncStatusStore>`
- `SyncRunResult`
- `SyncStatusSnapshot`

本模块不应依赖：

- `HttpClient`
- Atera client
- Snipe-IT importer
- mapping implementation
- Windows Forms
- WorkerService host classes
- real API credentials

## 15. 成功条件

本模块完成后应满足：

- 每次 successful `SyncRunResult` 都保存成一个独立 history JSON
- 每次 failed `SyncRunResult` 都保存成一个独立 history JSON
- history 文件名使用 UTC finished timestamp
- history JSON 使用结构化字段表达 assets、companies、models、manufacturers、categories 的 created/updated/deleted/skipped/failed items
- 当前“不做删除”原则下 deleted arrays 保留但为空
- `ReadLatestAsync` 可以从 history 中读取最近状态
- `ReadLatestAsync` 可以扫描 history 得到最近一次成功时间
- missing/empty history directory 返回 `null`
- malformed history file 会被跳过并记录 warning
- 写入使用 temp file + atomic move，避免半写文件
- 自动化测试全部使用临时目录和 hand-written result objects，不调用真实 API

## 16. 扩展点

后续可以在不改变模块边界的前提下扩展：

- history schema version migration
- history search/filter API
- active run heartbeat / `BeginRunAsync`
- richer structured action fields
- read-only health endpoint consumed by TrayApp or WorkerService
- optional owner-approved history export

这些扩展不得把 API wire behavior、sync orchestration、notification sending 或 UI rendering 混入本模块。
