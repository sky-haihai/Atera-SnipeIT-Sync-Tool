# Worker Scheduler / WorkerService - 技术规格

## 1. 目标与实现状态

本规格是 WorkerService 正式 runtime、scheduler、IPC host 和 Tray command handling 的实现蓝图，基于 `docs/module-plans/07-WorkerScheduler-功能职责.md`。

现有 `SyncScheduleOptions`、`ScheduleCalculator`、Core sync orchestration 和 Windows Service hosting 继续复用。本阶段在 WorkerService 内实现独立的 `WorkerScheduler`、`WorkerRuntimeFactory`、`Worker` 与 IPC host；旧 Core `SyncScheduler` 保留供既有兼容测试使用，不承载新的 reloadable Worker 调度。

- reloadable schedule state 与 `ReloadSchedule` command。
- Scheduled、Connection Test、Preview、Sync Now 共享的 singleton run coordinator。
- versioned local IPC server。
- combined Connection Test。
- 每次 run 前从 JSON 重建 runtime snapshot。
- Worker status 中的 active operation、schedule state 与 next-run。

本阶段不得改变 Atera/Snipe-IT endpoint、DTO、payload、pagination 或 authentication wire shape。所有 automated tests 使用 fake clients、mocked HTTP handlers 和 temporary files。

## 2. 目标调用关系

```text
Windows Service Host
  ├─ Worker
  │   ├─ WorkerScheduleManager
  │   └─ WorkerScheduler
  │       └─ ISyncRunCoordinator
  │           └─ WorkerRuntimeFactory.CreateSyncRuntimeAsync()
  └─ WorkerIpcServer
      └─ WorkerCommandHandler
          ├─ WorkerScheduleManager       (ReloadSchedule/GetStatus)
          ├─ ISyncRunCoordinator         (Test/Preview/Sync)
          ├─ WorkerRuntimeFactory        (reload JSON per run)
          ├─ WorkerConnectionTester
          └─ ISyncStatusStore
```

WorkerScheduleManager、ISyncRunCoordinator、ISyncStatusStore 和 WorkerRuntimeFactory 都以 singleton 注册。`Ping`、`GetStatus`、`ReloadSchedule`、`Cancel` 不取得 run lease。

## 3. Production files

Core contracts：

```text
src/AteraSnipeSync.Core/Scheduling/Interfaces/ISyncRunCoordinator.cs
src/AteraSnipeSync.Core/Scheduling/SyncRunCoordinator.cs
src/AteraSnipeSync.Core/Scheduling/WorkerScheduleSnapshot.cs
src/AteraSnipeSync.Core/Scheduling/ScheduleReloadResult.cs
src/AteraSnipeSync.Core/Scheduling/WorkerOperationNames.cs
src/AteraSnipeSync.Core/Runtime/Ipc/WorkerIpcProtocol.cs
src/AteraSnipeSync.Core/Runtime/Ipc/WorkerIpcCommands.cs
src/AteraSnipeSync.Core/Runtime/Ipc/WorkerIpcRequest.cs
src/AteraSnipeSync.Core/Runtime/Ipc/WorkerIpcEvent.cs
src/AteraSnipeSync.Core/Runtime/Ipc/WorkerStatusSnapshot.cs
src/AteraSnipeSync.Core/Runtime/Ipc/ConnectionTestResult.cs
src/AteraSnipeSync.Core/Runtime/Ipc/WorkerSyncResultSummary.cs
src/AteraSnipeSync.Core/Runtime/Windows/WorkerServiceIdentity.cs
```

WorkerService：

```text
src/AteraSnipeSync.WorkerService/Program.cs
src/AteraSnipeSync.WorkerService/Worker.cs
src/AteraSnipeSync.WorkerService/WorkerRuntimeFactory.cs
src/AteraSnipeSync.WorkerService/IWorkerRuntimeFactory.cs
src/AteraSnipeSync.WorkerService/WorkerSyncRuntime.cs
src/AteraSnipeSync.WorkerService/WorkerScheduleManager.cs
src/AteraSnipeSync.WorkerService/WorkerScheduler.cs
src/AteraSnipeSync.WorkerService/WorkerIpcServer.cs
src/AteraSnipeSync.WorkerService/WorkerIpcServerOptions.cs
src/AteraSnipeSync.WorkerService/WindowsWorkerPipeFactory.cs
src/AteraSnipeSync.WorkerService/IWorkerCommandHandler.cs
src/AteraSnipeSync.WorkerService/WorkerCommandHandler.cs
src/AteraSnipeSync.WorkerService/WorkerConnectionTester.cs
src/AteraSnipeSync.WorkerService/WorkerResultSanitizer.cs
```

Tests：

```text
tests/AteraSnipeSync.Tests/Scheduling/SyncRunCoordinatorTests.cs
tests/AteraSnipeSync.Tests/WorkerService/WorkerScheduleManagerTests.cs
tests/AteraSnipeSync.Tests/WorkerService/WorkerRuntimeFactoryTests.cs
tests/AteraSnipeSync.Tests/WorkerService/WorkerSchedulerTests.cs
tests/AteraSnipeSync.Tests/WorkerService/WorkerConnectionTesterTests.cs
tests/AteraSnipeSync.Tests/WorkerService/WorkerCommandHandlerTests.cs
tests/AteraSnipeSync.Tests/WorkerService/WorkerIpcServerTests.cs
tests/AteraSnipeSync.Tests/WorkerService/WorkerServiceIdentityTests.cs
tests/AteraSnipeSync.Tests/Configuration/LocalAppSettingsStoreTests.cs
```

每个 class 必须有 concise role/boundary comment；key method comment 必须说明 I/O、side effects、validation 和 failure behavior。

## 4. Windows Service identity

Namespace：

```csharp
namespace AteraSnipeSync.Core.Runtime.Windows;
```

```csharp
public static class WorkerServiceIdentity
{
    public const string ServiceName = "AteraSnipeItAutoSync";
    public const string DisplayName = "Atera Snipe-IT Auto Sync";
    public const string ExecutableFileName =
        "AteraSnipeSync.WorkerService.exe";
}
```

Rules：

- Service registration/SCM lookup 使用 `ServiceName`。
- Windows Services UI 使用 `DisplayName`。
- Worker host 调用 `AddWindowsService(options => options.ServiceName = WorkerServiceIdentity.ServiceName)`；DisplayName 只在 SCM registration 时设置。
- name/display/executable 不从 JSON、environment 或 IPC 覆盖。
- TrayApp 必须引用同一 Core constant class，不能复制不同值。

## 5. Schedule contracts

### 5.1 `SyncScheduleOptions`

Namespace：

```csharp
namespace AteraSnipeSync.Core.Scheduling;
```

```csharp
public sealed class SyncScheduleOptions
{
    public required bool Enabled { get; init; }
    public required ScheduleFrequency Frequency { get; init; }
    public required string TimeZoneId { get; init; }
    public required IReadOnlyList<TimeOnly> RunTimes { get; init; }
    public IReadOnlyList<DayOfWeek> DaysOfWeek { get; init; } = [];
    public IReadOnlyList<int> DaysOfMonth { get; init; } = [];
    public bool RunOnLastDayOfMonth { get; init; }
    public bool PreventOverlappingRuns { get; init; } = true;
}
```

Validation：

- enabled schedule 至少一个 unique RunTime。
- `TimeZoneId` 可由 `TimeZoneInfo.FindSystemTimeZoneById` 解析。
- Weekly 至少一个 weekday。
- Monthly 至少一个 1–31 day 或 `RunOnLastDayOfMonth = true`。
- 月份不存在的 day 在该月 skip。
- DST invalid local time skip；ambiguous time 选较早 UTC occurrence。
- missed run 固定 skip；当前 contract 不增加 `MissedRunPolicy`。

### 5.2 `ScheduleCalculator`

```csharp
public sealed class ScheduleCalculator
{
    public DateTimeOffset? GetNextRunUtc(
        SyncScheduleOptions options,
        DateTimeOffset nowUtc);

    public static void Validate(SyncScheduleOptions options);
}
```

disabled 返回 `null`。该 class 不执行 I/O、logging 或 sync。

## 6. Reloadable schedule manager

Shared snapshot/result contracts 位于 Core，避免 Core IPC models 依赖 WorkerService assembly：

```csharp
namespace AteraSnipeSync.Core.Scheduling;
```

```csharp
public sealed class WorkerScheduleSnapshot
{
    public required long Version { get; init; }
    public required bool ConfigurationValid { get; init; }
    public required bool Enabled { get; init; }
    public SyncScheduleOptions? Options { get; init; }
    public DateTimeOffset? NextRunUtc { get; init; }
    public string? Error { get; init; }
}

public sealed class ScheduleReloadResult
{
    public required bool Applied { get; init; }
    public required WorkerScheduleSnapshot Snapshot { get; init; }
    public required string Message { get; init; }
}
```

Manager 位于 WorkerService：

```csharp
namespace AteraSnipeSync.WorkerService;

public sealed class WorkerScheduleManager
{
    public WorkerScheduleManager(
        ILocalAppSettingsReader settingsReader,
        ScheduleCalculator calculator,
        TimeProvider timeProvider,
        ILogger<WorkerScheduleManager> logger);

    public WorkerScheduleSnapshot Current { get; }

    public Task<ScheduleReloadResult> InitializeAsync(
        CancellationToken cancellationToken);

    public Task<ScheduleReloadResult> ReloadAsync(
        CancellationToken cancellationToken);

    public Task WaitForChangeAsync(
        long observedVersion,
        CancellationToken cancellationToken);

    public void AdvanceAfterTrigger(DateTimeOffset nowUtc);
}
```

Behavior：

- `InitializeAsync` 在 Worker start 调用一次；重复调用返回 current snapshot，不重复初始化。
- `ReloadAsync` 从 `LoadSyncScheduleOptionsAsync` 读取磁盘，不接收 client-provided options。
- valid reload 在 lock 内递增 Version、替换 immutable snapshot、完成旧 change waiter，然后用新的 waiter 接受后续 wait。
- disabled/missing schedule 是 valid snapshot：`Enabled = false`、`NextRunUtc = null`。
- reload validation/read failure 返回 `Applied = false`，记录脱敏 error，并发布 `ConfigurationValid = false`、`Enabled = false` 的新 snapshot；future scheduled trigger 停止，直到成功 reload或 service restart。
- `AdvanceAfterTrigger` 使用 current options 和 supplied time 计算下一次，不重新使用已经到期的 timestamp。
- Error/Message 不含 raw JSON、credential 或 stack trace。
- 没有 background polling/file watcher。直接手工编辑 JSON 只有在 ReloadSchedule 或 Worker restart 后才改变 scheduler。

## 7. `WorkerScheduler` behavior

`WorkerScheduler` 位于 WorkerService，不修改旧 Core scheduler contract：

```csharp
public WorkerScheduler(
    WorkerScheduleManager scheduleManager,
    ISyncRunCoordinator runCoordinator,
    IWorkerRuntimeFactory runtimeFactory,
    ISyncStatusStore statusStore,
    INotificationPublisher notificationPublisher,
    NotificationEventFilter notificationFilter,
    TimeProvider timeProvider,
    ILogger<WorkerScheduler> logger);

public Task StartAsync(CancellationToken cancellationToken);
public Task<bool> RunScheduledSyncAsync(CancellationToken cancellationToken);
```

Main loop：

1. 读取 `scheduleManager.Current`。
2. invalid/disabled/no-next-run 时调用 `WaitForChangeAsync(snapshot.Version, hostToken)`。
3. enabled 时并行等待 next-run delay 与 schedule change。
4. reload change 先完成时放弃旧 delay 并重新读取 snapshot。
5. due time 先完成时调用 `TryAcquire`：busy 则 warning + skip；成功则创建本次 runtime 并执行。
6. success/failure/cancellation 后释放 lease并调用 `AdvanceAfterTrigger`。
7. 单次 load/run/status-save failure 不终止 main loop。

Scheduler 不缓存 `WorkerSyncRuntime`。每次 due run 在 lease 内调用 `CreateSyncRuntimeAsync`，确保使用最新完整 JSON snapshot。

## 8. Global run coordinator

Namespace：

```csharp
namespace AteraSnipeSync.Core.Scheduling;
```

```csharp
public interface ISyncRunCoordinator
{
    bool IsRunning { get; }
    string? ActiveOperationId { get; }
    string? ActiveOperation { get; }
    DateTimeOffset? ActiveStartedUtc { get; }

    bool TryAcquire(
        string operationId,
        string operation,
        DateTimeOffset startedUtc,
        out IDisposable? lease);
}
```

```csharp
public static class WorkerOperationNames
{
    public const string Scheduled = "scheduled";
    public const string ConnectionTest = "connection-test";
    public const string Preview = "preview";
    public const string SyncNow = "sync-now";
}
```

`SyncRunCoordinator` uses one interlocked state object。lease Dispose 必须 idempotent且只释放 matching state。Scheduled、Connection Test、Preview、Sync Now 使用相同 singleton instance；不提供 queue。

## 9. Shared IPC protocol

Namespace：

```csharp
namespace AteraSnipeSync.Core.Runtime.Ipc;
```

### 9.1 Constants

```csharp
public static class WorkerIpcProtocol
{
    public const int Version = 1;
    public const string DefaultPipeName = "AteraSnipeSync.Worker.v1";
    public const int MaxMessageCharacters = 32 * 1024 * 1024;
}

public static class WorkerIpcCommands
{
    public const string Ping = "Ping";
    public const string GetStatus = "GetStatus";
    public const string ReloadSchedule = "ReloadSchedule";
    public const string TestConnections = "TestConnections";
    public const string PreviewChanges = "PreviewChanges";
    public const string SyncNow = "SyncNow";
    public const string Cancel = "Cancel";
}
```

`TestAtera` 和 `TestSnipeIt` 不属于正式 command allow-list。

### 9.2 Request

```csharp
public sealed class WorkerIpcRequest
{
    public required int ProtocolVersion { get; init; }
    public required string RequestId { get; init; }
    public required string Command { get; init; }
    public string? TargetRequestId { get; init; }
}
```

- RequestId 为 1–128 个 `[A-Za-z0-9._-]` characters。
- `Cancel` 必须提供 valid TargetRequestId；其它 command 禁止额外 payload。
- request model 不提供 credentials、schedule、URL、SyncRunRequest 或 output path。

### 9.3 Result models

```csharp
public sealed class ConnectionEndpointTestResult
{
    public required bool Succeeded { get; init; }
    public required string Message { get; init; }
}

public sealed class ConnectionTestResult
{
    public required ConnectionEndpointTestResult Atera { get; init; }
    public required ConnectionEndpointTestResult SnipeIt { get; init; }
}

public sealed class WorkerStatusSnapshot
{
    public required bool IsRunning { get; init; }
    public string? ActiveOperation { get; init; }
    public DateTimeOffset? ActiveStartedUtc { get; init; }
    public required bool ScheduleConfigurationValid { get; init; }
    public required bool ScheduleEnabled { get; init; }
    public DateTimeOffset? NextRunUtc { get; init; }
    public string? ScheduleError { get; init; }
    public SyncStatusSnapshot? LatestSync { get; init; }
}
```

### 9.4 Event

```csharp
public sealed class WorkerIpcEvent
{
    public required int ProtocolVersion { get; init; }
    public required string RequestId { get; init; }
    public required string EventType { get; init; }
    public string? Message { get; init; }
    public SyncProgressUpdate? Progress { get; init; }
    public WorkerSyncResultSummary? SyncResult { get; init; }
    public ConnectionTestResult? ConnectionTest { get; init; }
    public WorkerStatusSnapshot? WorkerStatus { get; init; }
    public ScheduleReloadResult? ScheduleReload { get; init; }
    public string? ActiveOperation { get; init; }
    public string? PreflightDirectory { get; init; }
    public string? ReportPath { get; init; }
}
```

`WorkerSyncResultSummary` 只包含 success/dry-run/cancelled、起止时间、pulled/mapped/created/updated/skipped/failed/warning counts 与脱敏后的 failure；不得包含 Atera agent、Snipe-IT object、raw JSON、HTTP body 或 credential。

Payload rules：

- `GetStatus` Completed only: WorkerStatus。
- `ReloadSchedule` Completed/Error only: ScheduleReload。
- `TestConnections` Completed only: ConnectionTest。
- Preview/Sync Completed only: SyncResult。
- Progress event only: Progress。
- Busy terminal may include ActiveOperation only。

JSON uses camelCase UTF-8, one complete object per line。超限、EOF before newline 或 invalid JSON 返回 error并关闭 connection。

## 10. Pipe security and server

```csharp
public sealed class WindowsWorkerPipeFactory
{
    public NamedPipeServerStream Create(string pipeName);
    public bool IsLocalAuthorizedClient(NamedPipeServerStream pipe);
}
```

- asynchronous byte-mode pipe。
- ACL：SYSTEM/Administrators FullControl，本机 Builtin Users ReadWrite，Anonymous deny。
- connection 后先调用 Win32 `GetNamedPipeClientComputerName` 验证来源；本机连接返回 `ERROR_PIPE_LOCAL (229)` 时视为本机，远端 computer name 不匹配时在读取 request 前断开。
- 不记录 token、serialized frame或 credential。

```csharp
public sealed class WorkerIpcServer : BackgroundService
{
    public WorkerIpcServer(
        WindowsWorkerPipeFactory pipeFactory,
        IWorkerCommandHandler commandHandler,
        ILogger<WorkerIpcServer> logger,
        WorkerIpcServerOptions options);
}
```

- host lifetime 内持续 accept。
- 每个 connection 用独立 handler task；Cancel/GetStatus/ReloadSchedule 可在 long run 期间连接。
- long run 先写 Accepted，再顺序写 Progress，最后 exactly one terminal event。
- response writer single-writer；client disconnect 不终止 Worker host或已开始 run。
- stop 时取消 accept loop，并等待 active handlers。

## 11. Runtime composition and JSON credentials

```csharp
public sealed class WorkerSyncRuntime
{
    public required ISyncOrchestrator Orchestrator { get; init; }
    public required IAteraClient AteraConnectionClient { get; init; }
    public required HttpClient SnipeItHttpClient { get; init; }
    public required SyncRunRequest BaseRequest { get; init; }
    public required NotificationConfig NotificationConfig { get; init; }
}

public interface IWorkerRuntimeFactory
{
    public Task<WorkerSyncRuntime> CreateSyncRuntimeAsync(
        CancellationToken cancellationToken);
}

public sealed class WorkerRuntimeFactory : IWorkerRuntimeFactory;
```

`CreateSyncRuntimeAsync` 每次调用：

- 调用 `LoadWorkerSyncSettingsAsync` 重新读取完整共享 JSON。
- 从 `Atera.ApiKey` 与 `SnipeIt.ApiToken` 读取非空凭据；不读取 environment fallback。
- 验证 base URLs、credentials、mapping/import required fields。
- 构造 AteraClient → InventoryMapper → SnipeImporter → SyncOrchestrator。
- 返回只属于本次 run 的 immutable runtime；不调用外部 API。
- exception/message 不包含配置 JSON或 credentials。

`LocalAppSettingsStore.LoadWorkerSyncSettingsAsync` 不再拒绝 plaintext credentials。正式 `SaveSyncAppSettingsAsync` 使用同一次 atomic mutation 保存完整 API/mapping/import、schedule 与 notification config，并保持 cross-process lock 和 credential round-trip。

## 12. Command handler

```csharp
public interface IWorkerCommandHandler
{
    Task<WorkerCommandResult> ExecuteAsync(
        WorkerIpcRequest request,
        IProgress<SyncProgressUpdate> progress,
        CancellationToken cancellationToken);

    bool TryCancel(string targetRequestId);
}

public sealed class WorkerCommandResult
{
    public required string EventType { get; init; }
    public required string Message { get; init; }
    public WorkerSyncResultSummary? SyncResult { get; init; }
    public ConnectionTestResult? ConnectionTest { get; init; }
    public WorkerStatusSnapshot? WorkerStatus { get; init; }
    public ScheduleReloadResult? ScheduleReload { get; init; }
    public string? ActiveOperation { get; init; }
    public string? PreflightDirectory { get; init; }
    public string? ReportPath { get; init; }
}
```

`WorkerCommandHandler` dispatch：

- Ping：return protocol-ready message。
- GetStatus：merge coordinator + scheduleManager.Current + latest status store read。
- ReloadSchedule：call scheduleManager.ReloadAsync；不取得 run lease。
- TestConnections/Preview/SyncNow：先 TryAcquire，再 register request cancellation source，再创建本次 runtime并执行。
- Cancel：只 cancel matching active Tray request；scheduled operation id 不进入 cancellable request registry。

Busy response 立即返回且不得加载 runtime或调用 API。active request registry entry 在所有 terminal paths 删除，run lease 在 finally 释放。

## 13. Combined connection tester

```csharp
public sealed class WorkerConnectionTester
{
    public Task<ConnectionTestResult> TestAllAsync(
        WorkerSyncRuntime runtime,
        IProgress<SyncProgressUpdate> progress,
        CancellationToken cancellationToken);
}
```

Behavior：

1. emit sanitized Atera testing progress。
2. execute existing read-only Atera probe；convert exception to Atera endpoint result。
3. unless cancelled, emit Snipe-IT progress and execute documented read-only probe。
4. return both endpoint results；overall command is Completed even if one/both endpoints report `Succeeded = false`。
5. cancellation stops remaining probe and returns Cancelled terminal。

Tester 不返回 raw body、header、URL query credential 或 stack trace。Automated tests mock both clients。

## 14. Host registration and lifetime

`Program.cs` DI：

```csharp
builder.Services.AddWindowsService(options =>
    options.ServiceName = WorkerServiceIdentity.ServiceName);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<LocalAppSettingsStore>(...);
builder.Services.AddSingleton<ILocalAppSettingsReader>(...);
builder.Services.AddSingleton<ScheduleCalculator>();
builder.Services.AddSingleton<WorkerScheduleManager>();
builder.Services.AddSingleton<ISyncRunCoordinator, SyncRunCoordinator>();
builder.Services.AddSingleton<WorkerRuntimeFactory>();
builder.Services.AddSingleton<IWorkerRuntimeFactory>(...);
builder.Services.AddSingleton<WorkerConnectionTester>();
builder.Services.AddSingleton<IWorkerCommandHandler, WorkerCommandHandler>();
builder.Services.AddSingleton<WindowsWorkerPipeFactory>();
builder.Services.AddSingleton(new WorkerIpcServerOptions());
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<WorkerIpcServer>();
```

`Worker.ExecuteAsync` first awaits `scheduleManager.InitializeAsync` then runs scheduler。invalid schedule 记录 error但不能阻止 IPC host启动。

Service shutdown：

- cancel scheduler delay/change wait、IPC accept loop 和 active operation。
- await connection handlers and scheduler completion。
- 不启动新 run。
- structured run result 已产生时用 `CancellationToken.None` best-effort save history。

## 15. Error and logging rules

- Logs include operation id/type、stage、sanitized error和 timing。
- Logs/status/IPC/history/CSV never include credentials、Authorization、raw JSON/frame/body。
- Config/schedule error 指明 property path但不包含 property value。
- Status save failure 不覆盖真实 run outcome，但 terminal message 必须说明 audit save failure。
- Connection test business failure 与 transport/protocol failure 分开表示。
- 单个 command exception 不终止 accept loop；单个 scheduled exception 不终止 scheduler。

## 16. Automated tests

### Schedule manager/scheduler

- startup valid/disabled/invalid schedule。
- ReloadSchedule rereads disk and changes next-run/version。
- reload wakes old delay and prevents old trigger。
- reload during run does not cancel active run；next trigger uses new schedule。
- invalid reload disables future scheduled trigger and returns sanitized error。
- no file polling occurs；restart reloads saved schedule。

### Coordinator/runtime

- Scheduled/Test/Preview/Sync pairwise non-overlap。
- manual busy returns before runtime creation。
- scheduled busy skips without queue。
- lease releases on result/exception/cancel。
- each run calls settings load once and receives independent snapshot。
- plaintext JSON credentials load successfully but never appear in messages/log test sink。

### Command handler/connection test

- GetStatus merges active operation, latest status and next-run。
- ReloadSchedule does not acquire run lease。
- one combined test runs Atera then Snipe-IT under one lease。
- Atera failure still attempts Snipe-IT；cancellation stops remaining work。
- Cancel only matches Tray-started request id。
- TestAtera/TestSnipeIt are rejected as unknown commands。

### IPC server

- ordered Accepted → Progress → terminal。
- malformed/version mismatch/unknown/oversize request rejected before API work。
- GetStatus/ReloadSchedule/Cancel can connect while a run connection is active。
- disconnect does not terminate host/run。
- serialized requests/events contain no secret fields。

All tests use fake TimeProvider/clients、temp JSON和 in-process named pipe；不得调用真实 API或真实 Windows Service。

## 17. Acceptance criteria

- Windows host display name is `Atera Snipe-IT Auto Sync`；shared service name constant is `AteraSnipeItAutoSync`。
- Worker starts scheduler without Tray and continues after Tray exits。
- Tray Save → ReloadSchedule causes new next-run calculation without changing active run。
- every run reloads full JSON and uses plaintext credentials only inside Worker runtime。
- Scheduled/Connection Test/Preview/Sync Now never overlap。
- combined TestConnections returns two sanitized endpoint results。
- Worker status reports busy operation、schedule validity/enabled state、next-run and latest sync。
- malformed/unauthorized IPC never calls an external API。
- full build/test passes with zero real API/SCM operations。
## 2026-07 `TestNotifications` IPC extension

Add `WorkerIpcCommands.TestNotifications = "TestNotifications"`, `WorkerOperationNames.NotificationTest = "notification-test"`, `WorkerIpcEvent.NotificationTest`, and the corresponding `WorkerCommandResult.NotificationTest` mapping。The command is long-running for Accepted/terminal protocol purposes and accepts no request payload or target id。

`WorkerCommandHandler` constructor adds `ILocalAppSettingsReader settingsReader` and `INotificationTester notificationTester`。`ExecuteNotificationTestAsync` acquires the global coordinator, registers request cancellation, calls `settingsReader.LoadNotificationConfigAsync`, calls `notificationTester.TestAsync`, returns Completed + result, and always removes the active request/releases the lease。Busy、cancel and sanitized generic error behavior matches other commands；no API runtime factory call is allowed。

`WorkerScheduler.RunScheduledSyncAsync` calls `PublishAsync(notification, runtime.NotificationConfig, cancellationToken)` only after the existing filter accepts the event。

After `WorkerCommandHandler` saves a manual Preview/Sync result, `TryPublishRunNotificationAsync` maps Preview to `manual-preview` and Sync Now to `manual`, filters against `runtime.NotificationConfig`, and calls `PublishAsync` with that exact snapshot。Non-cancellation delivery failures log only the failure type and do not alter `WorkerSyncResultSummary` or terminal Completed state。

## 2026-07 Deleted/no-change IPC projection

`WorkerSyncResultSummary` adds `public required int Deleted { get; init; }`。`WorkerResultSanitizer.CreateSummary` sets `Deleted = result.ImportResult?.DeletedAssets ?? 0` and keeps `Skipped = result.ImportResult?.SkippedAssets ?? 0`。The serialized names remain `deleted` and `skipped`; Tray presentation renames skipped to `No change` without changing the established import counter。

`WorkerStatusSnapshot.LatestSync` already carries `SyncStatusSnapshot`; that snapshot now includes `Deleted`。No Worker code derives operator outcome text for the Last run card。Required tests assert the terminal summary includes a non-zero importer `DeletedAssets` projection and still excludes raw Atera/Snipe data。

## 2026-07 Fixed unknown mapping fallback composition

Core Configuration adds:

```csharp
public static class SyncApplicationDefaults
{
    public const string CompanyName = "Unknown Company";
    public const string ManufacturerName = "Unknown Manufacturer";
    public const string ModelName = "Unknown Model";
}
```

`WorkerRuntimeFactory.CreateSyncRuntimeAsync` must assign those constants directly to `MappingOptions.DefaultCompanyName`、`DefaultManufacturerName` and `DefaultModelName`。It must not call `Require` on the corresponding `SyncAppSettings` properties。The settings properties remain compatibility members during this migration, but cannot override production runtime behavior。

Required test：construct settings with conflicting custom values and assert the resulting `WorkerSyncRuntime.BaseRequest.Mapping` contains the three constants。No HTTP request is made。

## 2026-07 UTC schedule runtime state

### Types and persistence

Add `ScheduleRuntimeState` with required `RuleFingerprint` and nullable UTC `NextRunUtc`/`LastTriggeredUtc`. Add `IScheduleRuntimeStateStore.LoadAsync` and `SaveAsync`, implemented by `JsonFileScheduleRuntimeStateStore` at `%ProgramData%/AteraSnipeSync/schedule-state.json` using `AtomicFileWriter`. Non-UTC values are invalid. Malformed state is logged and treated as missing.

`ScheduleRuleFingerprint.Create(SyncScheduleOptions)` hashes a stable UTF-8 representation of Enabled, Frequency, TimeZoneId, sorted RunTimes/DaysOfWeek/DaysOfMonth, RunOnLastDayOfMonth and PreventOverlappingRuns with SHA-256 hexadecimal output.

### Manager and scheduler

`WorkerScheduleManager.InitializeAsync/ReloadAsync` load the local rule and runtime state. A matching state is retained, including overdue `NextRunUtc`; missing/mismatched state gets the first occurrence strictly after current UTC and is saved. Add `ClaimDueOccurrenceAsync(nowUtc, token)`: under the manager gate, when `NextRunUtc <= nowUtc`, save `LastTriggeredUtc = due` and the first future occurrence after `nowUtc` before publishing the new snapshot and returning true.

`WorkerScheduler.StartAsync` checks once immediately, then uses `PeriodicTimer(TimeSpan.FromSeconds(15), TimeProvider)`. A due claim invokes one scheduled run; false claims do nothing. Remove `WaitForDueTimeOrReloadAsync` and delete Core `SyncScheduler/ISyncScheduler`. Reload updates the manager snapshot immediately and the next periodic check consumes it.

`ScheduleCalculator` continues compiling local recurrence rules but returns UTC candidates only. Ambiguous local time selects the candidate with the smaller UTC offset (later UTC instant); invalid local time is skipped. No scheduler code compares local `DateTime` values.

### Runtime configuration snapshot

`WorkerRuntimeFactory.CreateSyncRuntimeAsync` calls `LoadWorkerSyncSettingsAsync` once and uses `settings.Notifications`. `LoadNotificationConfigAsync` remains available only for the explicit notification-test command.

Required tests cover 61-day gaps without long waits, 15-second fake-time ticks, persisted overdue catch-up once, multiple missed occurrences coalesced, claim-before-run restart safety, fingerprint changes, malformed state, DST fixed local 09:00, ambiguous later instant and invalid-time skip.

## 2026-07 Scheduled dry-run configuration removal specification

Remove `SyncAppSettings.DryRun`, `SyncConfig.DryRun` and `ILocalAppSettingsReader.LoadSyncDryRunAsync`. Remove the corresponding implementation from `LocalAppSettingsStore` and every fake reader. `LoadSyncAppSettingsAsync` ignores a legacy `Sync.DryRun` JSON member. `SaveSyncAppSettingsAsync` calls `syncSection.Remove("DryRun")` before writing `Sync.Schedule`, so an operator save migrates old local files without introducing a new schema version.

`WorkerRuntimeFactory.CreateSyncRuntimeAsync` sets both `baseRequest.SnipeIt.DryRun` and `baseRequest.Sync.DryRun` to `false`. `ScheduledSyncRequestFactory.CreateScheduledRequest` independently sets both cloned values to `false`, while continuing to set `TriggeredBy = "scheduled"` and disable manual preflight CSV output. Its focused test must provide a deliberately dry-run base request and prove the returned scheduled request is non-dry-run.

Do not remove `SyncRunOptions.DryRun`, `SnipeImportOptions.DryRun`, result/history/IPC DryRun fields or importer dry-run behavior: `ManualSyncRequestFactory.CreatePreviewChangesRequest` still requires them to guarantee Preview performs no Snipe-IT writes. Existing Sync Now and Preview request-factory tests remain required.

## 2026-07 Completed run notification pass-through

`WorkerCommandHandler.ExecuteSyncAsync` continues to return a Completed IPC terminal whenever an orchestrator produces a structured result. `WorkerResultSanitizer` copies `result.Success`, failed asset count and safe failure details without deriving a partial-success state. `TryPublishRunNotificationAsync` passes that same result to `NotificationRequestFactory`.

Required integration test: an in-memory orchestrator returns `Success = true`, `FailedAssets = 1`, and one run-level `SyncFailure`; a manual Sync Now command must return Completed with `SyncResult.Success = true`, retain `Failed = 1`, and publish `ManualSyncCompleted` with Warning severity and `Result: Completed`. No Atera/Snipe-IT/SMTP/webhook request is permitted.
