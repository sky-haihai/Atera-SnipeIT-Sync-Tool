# Atera to Snipe-IT Sync System - 简化总策划案

> 用途：给 AI coding agent 作为项目总方向文档。  
> 范围：只定义系统目标、模块边界、模块职责、对外接口、扩展点、第一版实现流程。  
> 不包含：具体 .NET 落地细节、类文件布局、NuGet 包选择、具体 HTTP 实现、UI 控件设计、安装器细节。  
> 后续每个模块应单独生成：
>
> 1. `功能职责.md`
> 2. `技术规格.md`
> 3. `生成代码（包括测试）`
> 4. `单元测试指导手册.md`

---

## 1. 项目目标

开发一个模块化同步系统，将 Atera 中的 managed devices / customers 同步到 Snipe-IT。

系统需要支持：

- 从 Atera 拉取设备和客户原始数据
- 将 Atera 原始数据重建为 Snipe-IT 可导入记录
- 在 Snipe-IT 中创建或更新：
  - Companies
  - Asset Models
  - Hardware Assets
- 支持 dry-run 模式
- 支持长期后台运行
- 支持本地配置
- 支持同步状态记录
- 支持后续扩展，例如：
  - 失败时发送邮件
  - 失败时发送 Teams / webhook 通知
  - 手动触发同步
  - 暂停 / 恢复同步
  - 添加更多数据源
  - 添加更多资产系统目标

---

## 2. 设计原则

### 2.1 模块独立

每个模块只负责自己的事情。

模块之间通过明确接口通信，不直接依赖彼此的内部实现。

### 2.2 可测试

每个模块都必须可以单独测试。

特别要求：

- Atera Pull Module 可以用 mocked Atera API 测试
- Reconstruction Module 必须是纯逻辑模块
- Snipe-IT Import Module 可以用 mocked Snipe-IT API 测试
- Orchestrator 可以通过 mock modules 测试流程
- Notification Module 可以通过 fake sender 测试

### 2.3 可扩展

系统需要为后续扩展预留接口，而不是把逻辑写死。

例如：

- 同步失败后，不应该只写 log；应该可以触发 notification pipeline
- import 结果不应该只是文字；应该返回结构化 result
- module 不应该直接读取全局配置；应该接收明确的配置对象
- orchestrator 不应该知道每个通知方式的内部实现

### 2.4 Dry Run 安全

当 dry-run 开启时：

- 可以读取 Atera
- 可以转换数据
- 可以查询 Snipe-IT
- 可以报告计划创建 / 更新的内容
- 不允许创建 company
- 不允许创建 model
- 不允许创建 asset
- 不允许更新 asset

### 2.5 不做删除

第一版不删除、不归档 Snipe-IT asset。

如果设备从 Atera 消失，第一版只是不再更新它。

---

## 3. 系统模块总览

```text
Atera Pull Module
    ↓
Reconstruction Module
    ↓
Snipe-IT Import Module
    ↓
Run Result
    ↓
Status Store / Logging / Notification
```

Runtime 层：

```text
Worker Service / Scheduled Runner
    → 负责定时调用 orchestrator

Tray App / Local UI
    → 负责配置和状态查看，不直接执行核心同步逻辑
```

---

## 4. Module 1 - Atera Pull Module

### 4.1 职责

Atera Pull Module 只负责从 Atera 获取原始数据。

它不应该理解 Snipe-IT，也不应该做资产匹配。

### 4.2 输入

```text
Atera API configuration
Cancellation signal
```

### 4.3 输出

```text
Raw Atera inventory data
```

包含：

```text
- Raw agents
- Raw customers
- Pull metadata
- Pull warnings
```

### 4.4 对外接口

```csharp
public interface IAteraClient
{
    Task<AteraPullResult> PullInventoryAsync(
        AteraPullRequest request,
        CancellationToken cancellationToken);
}
```

### 4.5 Request Model

```csharp
public sealed class AteraPullRequest
{
    public required string ApiKey { get; init; }
}
```

### 4.6 Result Model

```csharp
public sealed class AteraPullResult
{
    public required IReadOnlyList<AgentInfo> Agents { get; init; }
    public required IReadOnlyList<AteraCustomerDto> Customers { get; init; }
    public required PullSummary Summary { get; init; }
    public required IReadOnlyList<ModuleWarning> Warnings { get; init; }
}
```

### 4.7 必须支持

```text
- GET agents
- GET customers
- pagination
- authentication failure handling
- retryable failure handling
- malformed record handling
- pull summary
```

### 4.8 不允许做

```text
- 不生成 Snipe-IT asset tag
- 不判断 company 是否存在
- 不创建 Snipe-IT asset
- 不更新 Snipe-IT asset
- 不写入最终 status.json
```

---

## 5. Module 2 - Reconstruction Module

### 5.1 职责

Reconstruction Module 负责把 Atera 原始数据转换成 Snipe-IT import records。

这个模块应该是纯逻辑模块。

### 5.2 输入

```text
AteraPullResult
Mapping configuration
```

### 5.3 输出

```text
SnipeImportBatch
```

### 5.4 对外接口

```csharp
public interface IInventoryMapper
{
    SnipeImportBatch Map(
        AteraPullResult source,
        MappingOptions options);
}
```

### 5.5 Mapping Options

```csharp
public sealed class MappingOptions
{
    public required string DefaultCompanyName { get; init; }
    public required string DefaultManufacturerName { get; init; }
    public required string DefaultModelName { get; init; }
    public required string DefaultCategoryName { get; init; }
    public required int DefaultStatusId { get; init; }
}
```

### 5.6 Output Model

```csharp
public sealed class SnipeImportBatch
{
    public required IReadOnlyList<SnipeAssetImportRecord> Assets { get; init; }
    public required MappingSummary Summary { get; init; }
    public required IReadOnlyList<ModuleWarning> Warnings { get; init; }
}
```

### 5.7 Asset Import Record

```csharp
public sealed class SnipeAssetImportRecord
{
    public required string AssetTag { get; init; }
    public required string Name { get; init; }
    public string? Serial { get; init; }
    public required string CompanyName { get; init; }
    public required string ManufacturerName { get; init; }
    public required string ModelName { get; init; }
    public required string CategoryName { get; init; }
    public required int StatusId { get; init; }
    public required string Notes { get; init; }
    public required string SourceSystem { get; init; }
    public required string SourceId { get; init; }
}
```

### 5.8 Identity Rules

Primary identity:

```text
Serial number
```

Fallback identity:

```text
ATERA-{AgentID}
```

### 5.9 必须支持

```text
- serial 优先匹配规则
- fallback asset tag 生成
- missing company fallback
- missing manufacturer fallback
- missing model fallback
- required Snipe-IT fields construction
- notes construction
- mapping warnings
```

### 5.10 不允许做

```text
- 不调用 Atera API
- 不调用 Snipe-IT API
- 不读写文件
- 不写 log file
- 不处理 retry
- 不创建任何外部资源
```

---

## 6. Module 3 - Snipe-IT Import Module

### 6.1 职责

Snipe-IT Import Module 负责把 `SnipeImportBatch` 写入 Snipe-IT。

它负责确保 company、model、asset 存在，并创建或更新 asset。

### 6.2 输入

```text
SnipeImportBatch
Snipe-IT API configuration
Import options
```

### 6.3 输出

```text
SnipeImportResult
```

### 6.4 对外接口

```csharp
public interface ISnipeImporter
{
    Task<SnipeImportResult> ImportAsync(
        SnipeImportBatch batch,
        SnipeImportOptions options,
        CancellationToken cancellationToken);
}
```

### 6.5 Import Options

```csharp
public sealed class SnipeImportOptions
{
    public required string BaseUrl { get; init; }
    public required string ApiToken { get; init; }
    public required bool DryRun { get; init; }
    public required bool CreateMissingCompanies { get; init; }
    public required bool CreateMissingModels { get; init; }
}
```

### 6.6 Result Model

```csharp
public sealed class SnipeImportResult
{
    public required int CreatedAssets { get; init; }
    public required int UpdatedAssets { get; init; }
    public required int SkippedAssets { get; init; }
    public required int FailedAssets { get; init; }
    public required int CreatedCompanies { get; init; }
    public required int CreatedModels { get; init; }
    public required bool DryRun { get; init; }
    public required IReadOnlyList<ImportAction> Actions { get; init; }
    public required IReadOnlyList<ImportFailure> Failures { get; init; }
    public required IReadOnlyList<ModuleWarning> Warnings { get; init; }
}
```

### 6.7 Import Action

```csharp
public sealed class ImportAction
{
    public required string ActionType { get; init; }
    public required string TargetType { get; init; }
    public required string TargetName { get; init; }
    public required bool WasExecuted { get; init; }
    public string? Message { get; init; }
}
```

### 6.8 必须支持

```text
- ensure company exists
- create missing company
- ensure asset model exists
- create missing model
- search asset by serial
- fallback search asset by generated asset tag
- create hardware asset
- update hardware asset
- read Snipe-IT JSON response body
- handle 200 OK with error payload
- dry-run mode
- structured import result
```

### 6.9 不允许做

```text
- 不从 Atera 拉数据
- 不做 Atera → Snipe mapping
- 不决定 sync schedule
- 不写 status.json
- 不负责发送 email
```

---

## 7. Module 4 - Sync Orchestrator

### 7.1 职责

Sync Orchestrator 负责把三个核心模块串起来。

它控制一次完整 sync run 的流程。

### 7.2 输入

```text
SyncRunRequest
```

### 7.3 输出

```text
SyncRunResult
```

### 7.4 对外接口

```csharp
public interface ISyncOrchestrator
{
    Task<SyncRunResult> RunOnceAsync(
        SyncRunRequest request,
        CancellationToken cancellationToken);
}
```

### 7.5 Sync Run Request

```csharp
public sealed class SyncRunRequest
{
    public required AteraPullRequest Atera { get; init; }
    public required MappingOptions Mapping { get; init; }
    public required SnipeImportOptions SnipeIt { get; init; }
    public required SyncRunOptions Sync { get; init; }
}
```

### 7.6 Sync Run Options

```csharp
public sealed class SyncRunOptions
{
    public required bool DryRun { get; init; }
    public required string TriggeredBy { get; init; }
}
```

### 7.7 Sync Run Result

```csharp
public sealed class SyncRunResult
{
    public required bool Success { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset FinishedAt { get; init; }
    public required AteraPullResult? PullResult { get; init; }
    public required SnipeImportBatch? ImportBatch { get; init; }
    public required SnipeImportResult? ImportResult { get; init; }
    public required IReadOnlyList<ModuleWarning> Warnings { get; init; }
    public required IReadOnlyList<SyncFailure> Failures { get; init; }
}
```

### 7.8 必须支持

```text
- 调用 Atera Pull
- 调用 Reconstruction
- 调用 Snipe-IT Import
- 聚合 warnings
- 聚合 failures
- 生成结构化 run result
- 支持 dry-run
- 支持 triggeredBy，例如 scheduled/manual/test
```

### 7.9 不允许做

```text
- 不直接写 Atera API
- 不直接写 Snipe-IT API
- 不包含 UI 逻辑
- 不硬编码配置来源
- 不直接发送 email
```

---

## 8. Module 5 - Status Store

### 8.1 职责

Status Store 负责保存最新同步结果，供 Tray App 或管理员查看。

### 8.2 输入

```text
SyncRunResult
```

### 8.3 输出

```text
Persisted status record
```

### 8.4 对外接口

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

### 8.5 Status Snapshot

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

### 8.6 必须支持

```text
- 保存最新 run result
- 读取最新 status
- 处理 status file missing
- 处理 status file malformed
- 原子写入，避免写坏文件
```

### 8.7 不允许做

```text
- 不调用 Atera API
- 不调用 Snipe-IT API
- 不决定 sync 是否成功
- 不发送通知
```

---

## 9. Module 6 - Notification Module

### 9.1 职责

Notification Module 负责在特定事件发生时发送通知。

第一版可以只定义接口，不实现真实 email。

### 9.2 目标

为后续扩展保留位置，例如：

```text
- sync failed 时发 email
- import failed count > 0 时发 email
- auth failure 时发 urgent notification
- dry-run summary 发给管理员
- webhook / Teams / Slack notification
```

### 9.3 输入

```text
NotificationRequest
```

### 9.4 输出

```text
NotificationResult
```

### 9.5 对外接口

```csharp
public interface INotificationPublisher
{
    Task PublishAsync(
        NotificationRequest request,
        CancellationToken cancellationToken);
}
```

### 9.6 Notification Request

```csharp
public sealed class NotificationRequest
{
    public required string EventType { get; init; }
    public required string Severity { get; init; }
    public required string Subject { get; init; }
    public required string Message { get; init; }
    public required SyncRunResult? SyncResult { get; init; }
}
```

### 9.7 第一版实现

第一版可以提供：

```text
NullNotificationPublisher
```

行为：

```text
- 接收 notification request
- 不发送任何外部消息
- 可记录 debug log
```

### 9.8 后续可扩展实现

```text
EmailNotificationPublisher
TeamsWebhookNotificationPublisher
SlackNotificationPublisher
GenericWebhookNotificationPublisher
```

---

## 10. Module 7 - Runtime Scheduler / Worker

### 10.1 职责

Runtime Scheduler 负责按时间间隔触发 sync。

它是运行层，不是业务模块。

### 10.2 输入

```text
Runtime configuration
```

### 10.3 输出

```text
Periodic sync run
```

### 10.4 对外接口

```csharp
public interface ISyncScheduler
{
    Task StartAsync(CancellationToken cancellationToken);
}
```

### 10.5 必须支持

```text
- configurable interval
- default interval = 30 minutes
- prevent overlapping runs
- call orchestrator
- save status
- publish notification on failure
- handle cancellation
```

### 10.6 不允许做

```text
- 不直接写 Atera API
- 不直接写 Snipe-IT API
- 不做 mapping
- 不直接包含 UI 逻辑
```

---

## 11. Module 8 - Tray App / Local UI

### 11.1 职责

Tray App 是可选本地 UI。

它用于编辑配置和查看状态。

它不负责真正同步业务。

### 11.2 对外行为

```text
- 显示 system tray icon
- 打开 Settings window
- 保存 local config
- 读取 latest status
- 打开 log folder
- 退出 tray app 不影响 service
```

### 11.3 第一版功能

```text
- Open Settings
- View Last Sync Status
- Open Log Folder
- Exit Tray App
```

### 11.4 后续功能

```text
- Run Sync Now
- Pause Sync
- Resume Sync
- Reload Config
```

这些后续功能需要 IPC，例如 Named Pipe。

### 11.5 不允许做

```text
- 不直接拉 Atera 数据
- 不直接导入 Snipe-IT
- 不绕过 orchestrator 执行同步
- 不要求用户登录后 service 才能工作
```

---

## 12. Config Contract

### 12.1 配置目标

系统配置应该能支持：

```text
- Atera API key
- Snipe-IT base URL
- Snipe-IT API token
- default status ID
- sync interval
- dry-run
- notification options
```

### 12.2 对外配置模型

```csharp
public sealed class AppConfig
{
    public required AteraConfig Atera { get; init; }
    public required SnipeItConfig SnipeIt { get; init; }
    public required SyncConfig Sync { get; init; }
    public required NotificationConfig Notifications { get; init; }
}
```

### 12.3 Notification Config

```csharp
public sealed class NotificationConfig
{
    public required bool Enabled { get; init; }
    public required IReadOnlyList<string> OnEvents { get; init; }
    public string? EmailTo { get; init; }
    public string? WebhookUrl { get; init; }
}
```

### 12.4 第一版配置来源

第一版建议：

```text
appsettings.local.json
environment variables
```

Tray App 可以编辑 local config。

---

## 13. 第一版 MVP 范围

第一版要保证能真实跑通，但不要做太多高级功能。

### 13.1 必须做

```text
- Atera Pull Module
- Reconstruction Module
- Snipe-IT Import Module
- Sync Orchestrator
- Status Store
- Runtime Scheduler / Worker
- Dry-run mode
- Basic logging
- Basic local config
- Basic Tray App
```

### 13.2 可以只定义接口，暂不实现完整功能

```text
- Notification Module
- IPC command channel
- Installer
```

### 13.3 第一版不做

```text
- 删除 Snipe-IT asset
- archive disappeared devices
- complex UI
- manual Run Now
- Pause / Resume
- multi-tenant config UI
- advanced mapping override UI
- full installer
```

---

## 14. 第一版实现流程

### Step 1 - 创建总项目骨架

目标：

```text
建立 solution 和 project 边界。
```

输出：

```text
- Core project
- Worker project
- Tray App project
- Tests project
```

验收：

```text
- solution 可以 build
- tests project 可以运行空测试
- Core 不依赖 Worker
- Core 不依赖 Tray App
```

---

### Step 2 - 定义共享数据模型和接口

目标：

```text
先固定模块之间的 contract。
```

输出：

```text
- IAteraClient
- IInventoryMapper
- ISnipeImporter
- ISyncOrchestrator
- ISyncStatusStore
- INotificationPublisher
- request/result models
- warning/failure/action models
```

验收：

```text
- 所有接口能 compile
- 没有具体 API 实现
- 没有 UI 代码
```

---

### Step 3 - 实现 Reconstruction Module

目标：

```text
先做纯逻辑转换模块，因为它最容易测试。
```

输出：

```text
- InventoryMapper
- mapping fallback rules
- notes construction
- mapping summary
- mapper unit tests
```

验收：

```text
- serial 优先
- missing serial 时生成 ATERA-{AgentID}
- missing company/model/manufacturer 有 fallback
- unit tests pass
```

---

### Step 4 - 实现 Atera Pull Module

目标：

```text
实现 Atera 数据读取。
```

输出：

```text
- AteraClient
- Atera DTOs
- pagination handling
- auth failure handling
- retryable failure handling
- mocked API tests
```

验收：

```text
- 可以拉 agents
- 可以拉 customers
- pagination 正常
- auth failure 有明确 failure
- retryable failure 有 retry 或明确失败结果
```

---

### Step 5 - 实现 Snipe-IT Import Module

目标：

```text
实现 Snipe-IT company/model/asset create/update。
```

输出：

```text
- SnipeImporter
- Snipe-IT DTOs
- company ensure logic
- model ensure logic
- asset search/create/update logic
- dry-run handling
- mocked API tests
```

验收：

```text
- company missing 时可创建
- model missing 时可创建
- asset serial 存在时更新
- asset tag fallback 存在时更新
- 都不存在时创建
- dry-run 不执行写入
- 200 OK error payload 能识别为失败
```

---

### Step 6 - 实现 Sync Orchestrator

目标：

```text
串联 pull → map → import。
```

输出：

```text
- SyncOrchestrator
- SyncRunResult aggregation
- warnings aggregation
- failures aggregation
- orchestrator unit tests
```

验收：

```text
- 正常流程按顺序调用三个模块
- pull 失败时不继续 import
- map 失败时不继续 import
- import 失败时输出 structured failure
- dry-run 参数正确传递
```

---

### Step 7 - 实现 Status Store

目标：

```text
把最新同步结果保存成可读取状态。
```

输出：

```text
- file-based status store
- status snapshot model
- atomic write
- read latest status
- status store tests
```

验收：

```text
- sync result 可以保存
- Tray App 可以读取
- missing status file 不崩溃
- malformed status file 不崩溃
```

---

### Step 8 - 实现 Notification Stub

目标：

```text
先保留通知扩展点，不做真实 email。
```

输出：

```text
- NullNotificationPublisher
- notification request model
- scheduler/orchestrator failure path 中预留调用点
```

验收：

```text
- sync failed 时可以构造 notification request
- NullNotificationPublisher 不发送外部请求
- 以后可以替换成 EmailNotificationPublisher
```

---

### Step 9 - 实现 Worker Scheduler

目标：

```text
让 sync 可以定时运行。
```

输出：

```text
- scheduled worker
- configurable interval
- prevent overlapping runs
- status save after run
- notification on failure
```

验收：

```text
- 可以按 interval 运行
- 同一时间不会重叠跑两个 sync
- 每次运行后写 status
- 失败时调用 notification publisher
- cancellation 可以停止服务
```

---

### Step 10 - 实现 Basic Tray App

目标：

```text
提供最小可用本地 UI。
```

输出：

```text
- system tray icon
- Open Settings
- View Last Sync Status
- Open Log Folder
- Exit
```

验收：

```text
- Tray App 可以启动
- 可以保存 appsettings.local.json
- 可以读取 status.json
- 退出 Tray App 不影响 Worker Service
```

---

### Step 11 - End-to-End Dry Run

目标：

```text
在不写入 Snipe-IT 的情况下跑完整流程。
```

输出：

```text
- fixture Atera data
- mocked Snipe-IT API
- dry-run end-to-end test
```

验收：

```text
- pull 成功
- map 成功
- import 只报告 planned actions
- 没有执行 write request
- run result 和 status 正确
```

---

### Step 12 - Manual Server Test

目标：

```text
在 Windows Server 2016+ 环境验证第一版。
```

输出：

```text
- published build
- local config
- installed/running worker
- tray app manual run
```

验收：

```text
- Worker 可以启动
- Worker 可以持续运行
- logout 后 Worker 继续运行
- Tray App 可查看状态
- logs/status/config 路径正常
```

---

## 15. 每个模块的 AI Agent 生成流程

每个模块都按以下流程生成，不要直接跳到代码。

### Phase A - 功能职责.md

内容：

```text
- 模块目标
- 输入
- 输出
- 对外接口
- 成功条件
- 失败条件
- 不负责的事情
- 扩展点
```

验收：

```text
边界清楚，没有混入其他模块职责。
```

---

### Phase B - 技术规格.md

内容：

```text
- 具体 class/interface 设计
- method signature
- data models
- error handling
- logging requirement
- config requirement
- test cases
```

验收：

```text
AI agent 可以根据该规格直接写代码。
```

---

### Phase C - 生成代码和测试

内容：

```text
- production code
- unit tests
- mocked dependencies
- sample config if needed
```

验收：

```text
代码可 build，测试可运行。
```

---

### Phase D - 单元测试指导手册.md

内容：

```text
- 如何运行测试
- 每个测试验证什么
- 如何添加新测试
- 如何 mock 外部依赖
- 常见失败原因
```

验收：

```text
新人可以照着手册跑测试和扩展测试。
```

---

## 16. 扩展性要求

### 16.1 通知扩展

必须保留：

```csharp
INotificationPublisher
```

以后可以加入：

```text
EmailNotificationPublisher
TeamsNotificationPublisher
WebhookNotificationPublisher
```

---

### 16.2 数据源扩展

未来可能不只 Atera。

可以保留抽象概念：

```text
Inventory Source
```

但第一版只实现 Atera。

---

### 16.3 目标系统扩展

未来可能不只 Snipe-IT。

可以保留抽象概念：

```text
Inventory Import Target
```

但第一版只实现 Snipe-IT。

---

### 16.4 Mapping 扩展

未来可能支持：

```text
- company name override
- model name override
- category mapping table
- manufacturer normalization
- custom field mapping
```

第一版不做 UI，但 mapping options 不要写死。

---

### 16.5 Runtime 扩展

未来可能支持：

```text
- manual run now
- pause/resume
- reload config
- remote status query
```

第一版只做 scheduled run。

---

## 17. 第一版完成标准

第一版完成时，必须满足：

```text
[ ] 可以从 Atera 拉数据
[ ] 可以转换成 Snipe-IT import batch
[ ] 可以 dry-run
[ ] 可以创建 missing company
[ ] 可以创建 missing model
[ ] 可以创建 asset
[ ] 可以更新 asset
[ ] 可以按 serial 匹配
[ ] 可以 fallback 到 ATERA-{AgentID}
[ ] 可以输出 structured run result
[ ] 可以写 latest status
[ ] 可以定时运行
[ ] 可以失败时调用 notification stub
[ ] Tray App 可以编辑配置
[ ] Tray App 可以查看状态
[ ] 所有核心模块有单元测试
```

---

## 18. 总结

本项目的核心不是“写一个脚本”，而是建立一个可扩展的同步系统。

第一版只实现最小可用闭环：

```text
Atera Pull
→ Reconstruction
→ Snipe-IT Import
→ Orchestrator
→ Status Store
→ Scheduled Worker
→ Basic Tray App
```

但接口必须为未来扩展保留空间：

```text
Notification
Manual Run
Pause/Resume
Mapping Overrides
Additional Sources
Additional Import Targets
```

所有模块开发必须遵守：

```text
功能职责.md
→ 技术规格.md
→ 生成代码和测试
→ 单元测试指导手册.md
```
