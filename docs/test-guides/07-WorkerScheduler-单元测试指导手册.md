# Worker Scheduler / WorkerService - 单元测试指导手册

## 2026-07 UTC polling and runtime-state regression

Run the scheduling, Worker schedule-manager, state-store and runtime-factory tests together. Coverage includes 61-day monthly gaps, Edmonton 09:00 across DST, later-UTC ambiguous time, invalid spring-forward time, strict UTC `Z` state JSON, corrupt-state rebuild, overdue/restart catch-up once, missed-period coalescing, fingerprint changes, disable/re-enable invalidation, claim persistence before execution, 15-second polling, and exactly one complete settings read per run.

## 1. 测试目标

本手册覆盖 WorkerService 的正式调度与本机 IPC 实现：

- Worker 启动时读取 schedule，Tray 不运行时仍可独立调度。
- `ReloadSchedule` 重读共享 JSON、替换 future schedule 并重新计算 next-run。
- Scheduled、Connection Test、Preview、Sync Now 共用同一个 non-overlap gate。
- 每次 run 重读完整 JSON，并创建独立 runtime snapshot。
- `TestConnections` 在一个 lease 内分别返回 Atera 与 Snipe-IT 的脱敏结果。
- IPC 使用 versioned JSON Lines，保证 Accepted → Progress → terminal 顺序。
- `Cancel` 只作用于匹配的 Tray request id。
- plaintext JSON credentials 可被 Worker 读取，但不会进入 IPC summary 或测试输出。

旧 Core `ScheduleCalculator`、`ScheduledSyncRequestFactory` 与 `SyncScheduler` 测试继续保留，用于回归既有 schedule calculation/request contract；新的 reloadable service loop 由 `WorkerSchedulerTests` 覆盖。

## 2. 测试文件

```text
tests/AteraSnipeSync.Tests/Scheduling/ScheduleCalculatorTests.cs
tests/AteraSnipeSync.Tests/Scheduling/ScheduledSyncRequestFactoryTests.cs
tests/AteraSnipeSync.Tests/Scheduling/SyncSchedulerTests.cs
tests/AteraSnipeSync.Tests/Scheduling/SyncRunCoordinatorTests.cs
tests/AteraSnipeSync.Tests/Configuration/LocalAppSettingsStoreTests.cs
tests/AteraSnipeSync.Tests/WorkerService/WorkerScheduleManagerTests.cs
tests/AteraSnipeSync.Tests/WorkerService/WorkerSchedulerTests.cs
tests/AteraSnipeSync.Tests/WorkerService/WorkerRuntimeFactoryTests.cs
tests/AteraSnipeSync.Tests/WorkerService/WorkerConnectionTesterTests.cs
tests/AteraSnipeSync.Tests/WorkerService/WorkerCommandHandlerTests.cs
tests/AteraSnipeSync.Tests/WorkerService/WorkerIpcServerTests.cs
tests/AteraSnipeSync.Tests/WorkerService/WorkerServiceIdentityTests.cs
```

Worker production code位于：

```text
src/AteraSnipeSync.Core/Runtime/Ipc/
src/AteraSnipeSync.Core/Runtime/Windows/WorkerServiceIdentity.cs
src/AteraSnipeSync.Core/Scheduling/Interfaces/ISyncRunCoordinator.cs
src/AteraSnipeSync.Core/Scheduling/SyncRunCoordinator.cs
src/AteraSnipeSync.Core/Scheduling/WorkerOperationNames.cs
src/AteraSnipeSync.Core/Scheduling/WorkerScheduleSnapshot.cs
src/AteraSnipeSync.Core/Scheduling/ScheduleReloadResult.cs
src/AteraSnipeSync.WorkerService/
```

## 3. 运行测试

Worker 专项测试：

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~WorkerService|FullyQualifiedName~SyncRunCoordinator|FullyQualifiedName~LocalAppSettingsStoreTests.LoadWorkerSyncSettings"
```

完整验证：

```powershell
dotnet test AteraSnipeSync.sln --no-restore
dotnet build AteraSnipeSync.sln --no-restore
```

测试项目 target 为 `net10.0-windows`，因为它直接引用 Windows-targeted WorkerService 并执行本机 Named Pipe ACL/transport 测试。

## 4. 重点测试说明

### Schedule 与 runtime

- `WorkerScheduleManagerTests` 验证 disabled startup、valid reload、version increment、next-run 变化和 invalid reload fail-closed。
- `WorkerSchedulerTests` 验证 scheduled run 取得共享 lease、busy 时 skip、不 queue，并且每次 trigger 都调用 runtime factory。
- `WorkerRuntimeFactoryTests.CreateSyncRuntimeAsync_ReloadsSettingsForEveryRun` 在两次 run 之间替换 fake JSON snapshot，验证新 Atera/Snipe token 与独立 orchestrator 被使用；HTTP handler 如被调用会立即失败。
- `LocalAppSettingsStoreTests.LoadWorkerSyncSettingsAsync_LoadsPlaintextJsonCredentials_WithoutChangingFile` 使用临时 JSON 验证明文 credential round-trip，且读取不会改写文件。

### 全局 non-overlap 与 Cancel

- `SyncRunCoordinatorTests` 验证同一时间只允许一个 lease、active metadata 正确、Dispose idempotent。

## 2026-07 Completed notification integration regression

`WorkerCommandHandlerTests.ExecuteAsync_SyncNow_PublishesCompletedNotification_WhenCompletedRunHasRecordFailures` 使用纯内存 orchestrator 返回一个完整但含一条 asset failure 的 result。断言 IPC terminal 为 Completed、`SyncResult.Success = true`、`Failed = 1`，并且只匹配 `ManualSyncCompleted`，通知 severity 为 Warning、message 为 `Result: Completed`。publisher 是 capturing fake，不发送 SMTP/webhook；Atera/Snipe-IT client 不会被调用。
- `WorkerCommandHandlerTests.ExecuteAsync_SecondRunReturnsBusy_AndCancelTargetsFirstRequest` 让 Preview 阻塞，再尝试 Sync Now；第二项必须立刻 Busy，Cancel 只取消第一个 request。
- `ReloadSchedule` 不取得 run lease，因此 Worker 正在运行时仍可应用 future schedule；Tray UI 是否禁用按钮属于 Tray 状态机测试。

### Connection Test

- Atera probe 使用 fake `IAteraClient`，生产配置限制为一页、一条记录。
- Snipe-IT probe 使用 mocked HTTP handler，断言 `GET /api/v1/hardware?limit=1`、Bearer header、JSON Accept/Content-Type；不连接真实服务。
- Atera failure 后仍必须测试 Snipe-IT；cancellation 则停止剩余 probe。
- 两端 business failure 仍返回一个 completed combined result，并分别标记 `Succeeded = false`。

### IPC 与脱敏

- `WorkerIpcServerTests.PipeFactory_AcceptsLocalClient` 使用真实 Windows Named Pipe，验证 ACL pipe 可接受本机 client；Win32 `ERROR_PIPE_LOCAL (229)` 是本机连接的成功判定之一。
- `LongCommand_WritesAcceptedProgressAndCompletedInOrder` 验证 JSON Lines 顺序与 exactly one terminal result。
- malformed JSON 在 dispatch 前返回 Error，handler call count 必须为 0。
- sync terminal payload 使用 `WorkerSyncResultSummary`；测试序列化结果，确认 raw Atera record 不会穿过 IPC。
- latest status 保存失败时，structured sync outcome 仍返回，message 明确提示 audit status 未保存。
- `WorkerServiceIdentityTests` 固定 service name、display name 与同目录 Worker executable filename，供后续 Tray maintenance 共用。

## 5. Mocking 策略与安全边界

- Atera 自动测试使用 fake `IAteraClient` 或 mocked `HttpMessageHandler`。
- Snipe-IT 自动测试只使用 mocked `HttpMessageHandler`。
- runtime construction 测试使用拒绝所有网络请求的 handler。
- scheduler 使用 fake settings reader、runtime factory、status store、notification publisher 和 controlled time。
- IPC transport 测试只连接当前机器上的随机 pipe name，不操作 Windows Service Control Manager。
- 自动测试不得读取真实 `%ProgramData%` config、不得使用真实 API key/token、不得注册/启动/停止真实 Service。

## 6. 人工验收

本轮代码不自动注册 Service。完成 Tray service-maintenance UI 后，人工验证：

1. 把 TrayApp 与 `AteraSnipeSync.WorkerService.exe` 放在同一目录。
2. 通过 `Re-register & Restart` 注册固定 service name `AteraSnipeItAutoSync`。
3. 关闭 Tray，等待下一次 schedule，确认 Worker 仍执行。
4. 重新打开 Tray，保存新 schedule，确认 `ReloadSchedule` 返回新 next-run。
5. 发起 Preview 时尝试另一个 run，确认 Worker 返回 Busy；Cancel 只取消该 Tray run。
6. 日志、status、IPC capture 和 CSV 中不得出现 API key、Bearer token 或完整 raw API body。

任何真实 API smoke test 都必须由项目 owner 手工执行；credential 只保存在本机未跟踪 JSON，不得打印、记录或提交。
## 2026-07 Notification command/runtime coverage

- `WorkerCommandHandlerTests.ExecuteAsync_TestNotifications_ReturnsIndependentSanitizedResults` 使用 fake tester 验证 payload-free command/result 与 run-gate release。
- `ExecuteAsync_SyncNow_PublishesConfiguredManualNotification` 验证 manual event type 和 publisher 收到相同 immutable config snapshot。
- notification test 不调用 runtime factory/API clients；scheduled/manual publisher 和 test sender 全部离线替身。

## 2026-07 Last run count projection

`WorkerCommandHandlerTests.ExecuteAsync_SyncNow_ReturnsSummaryWithoutRawAteraRecords` 使用 `SnipeImportResult.DeletedAssets = 2` 并断言 `WorkerSyncResultSummary.Deleted = 2`，同时继续证明 raw Atera records 不进入 IPC。`Skipped` 仍作为 IPC wire member 传输，由 Tray 显示为 `No change`；Worker 不为 Latest run 生成 Success/Failed 展示文本。

## 2026-07 Fixed Unknown mapping fallback coverage

`WorkerRuntimeFactoryTests.CreateSyncRuntimeAsync_ReloadsSettingsForEveryRun` 向 `SyncAppSettings` 故意提供冲突的 custom company/manufacturer/model fallback，并断言正式 `BaseRequest.Mapping` 仍使用：

- `Unknown Company`
- `Unknown Manufacturer`
- `Unknown Model`

测试使用拒绝所有网络请求的 `HttpMessageHandler`；runtime composition 不访问 Atera 或 Snipe-IT。

## 2026-07 Scheduled Dry-Run 配置删除回归

- `WorkerRuntimeFactoryTests.CreateSyncRuntimeAsync_ReloadsSettingsForEveryRun` 验证基础请求的 `Sync.DryRun` 与 `SnipeIt.DryRun` 固定为 `false`，且 reader 不再提供独立 dry-run load API。
- `ScheduledSyncRequestFactoryTests.CreateScheduledRequest_ForcesRealScheduledTriggerAndDisablesManualPreflightCsv` 故意传入两个 `DryRun = true` 的基础值，验证 scheduled clone 强制改为 `false`、保留 `TriggeredBy = scheduled`，并禁用 manual preflight CSV。
- `WorkerSchedulerTests.RunScheduledSyncAsync_ReloadsRuntimeForEachRun_AndForcesScheduledRequest` 验证每个实际交给 orchestrator 的 scheduled request 都是非 dry-run。
- `ManualSyncRequestFactoryTests` 继续验证 Preview 固定 dry-run、Sync Now 固定真实运行，防止删除配置开关时破坏 Preview 的无写入边界。

所有测试使用 fake orchestrator、临时文件和拒绝网络的 HTTP handler，不调用真实 Atera 或 Snipe-IT API。
