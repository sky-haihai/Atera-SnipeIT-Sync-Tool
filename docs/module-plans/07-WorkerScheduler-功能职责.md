# Worker Scheduler / WorkerService - 功能职责

## 1. 模块目标

WorkerService 是 Atera-SnipeIT Sync Tool 的长期后台进程，也是正式架构中唯一执行同步和只读 API connection test 的进程。

本模块负责：

- 根据 daily / weekly / monthly schedule 自动触发同步；TrayApp 未启动或退出后仍继续运行。
- 接收 TrayApp 通过本机 IPC 发出的状态、schedule reload 和 manual operation 命令。
- 为 Scheduled、Connection Test、Preview 和 Sync Now 构造同一套 Core runtime。
- 保证所有会访问外部 API 的 run 在同一时刻最多执行一个。
- 保存最终 run history/status，并返回脱敏进度和结果。

WorkerService 只负责 runtime composition、调度和命令托管。Atera/Snipe-IT wire contract、mapping 和 import 规则仍由对应 Core 模块实现。

## 2. 当前基线与本阶段目标

当前已经存在：

- `ScheduleCalculator` 与 `SyncScheduler`。
- daily / weekly / monthly schedule configuration。
- `WorkerRuntimeFactory` 对 scheduled pipeline 的 composition。
- Windows Service hosting。
- `JsonFileSyncStatusStore`、notification filter 和 null publisher 接线。
- 已通过 operator 验证的临时 Manual Sync Preview/Sync 流程。

本阶段要补齐：

- WorkerService 本机 IPC command host。
- Preview、Sync Now 和 combined Connection Test 从 TrayApp 迁移到 WorkerService。
- Scheduled/Connection Test/Preview/Sync Now 共用的全局 non-overlap coordinator。
- `ReloadSchedule` 命令与可替换的 scheduler wait。
- active Tray command cancellation。
- Worker runtime 与 IPC 的 mocked automated tests。

本阶段结束后，TrayApp 不再构造 `AteraClient`、`SnipeImporter`、`SyncOrchestrator` 或写入 run history。

## 3. 输入

WorkerService 接收：

- `C:\ProgramData\AteraSnipeSync\appsettings.local.json` 中的完整 local config，包括当前阶段暂存的明文 API credentials。
- `TimeProvider`。
- host lifetime cancellation token。
- TrayApp 发送的 versioned IPC command。

Schedule configuration 支持：

- enabled / disabled。
- `Daily`、`Weekly`、`Monthly`。
- 一个或多个 local run time。
- time zone id。
- weekly weekdays。
- monthly day numbers 与 last-day option。
- prevent overlapping runs。

第一版 missed-run behavior 固定为 `Skip`；在 production contract 真正增加 `MissedRunPolicy` 前，不在配置或 UI 中暴露未实现选项。

## 4. 输出

本模块输出：

- Scheduled/Preview/Sync Now 的 `SyncRunResult`。
- combined Connection Test 中 Atera 与 Snipe-IT 各自的脱敏结果。
- 当前 Worker busy state、active operation、schedule state 和 next-run。
- sanitized IPC progress 与 terminal event。
- `SyncResult_*.json` history/status。
- Preview CSV directory。
- scheduler/IPC/service logs 与 optional notification request。

IPC output 不得包含 API key、token、Authorization header、raw request payload 或 raw response body。

## 5. Worker 是唯一执行进程

以下操作只能在 WorkerService 中执行：

- 构造 Atera/Snipe-IT `HttpClient` 和 API clients。
- 调用 `ISyncOrchestrator.RunOnceAsync`。
- 执行 combined Atera/Snipe-IT connection test。
- 生成 Preview CSV。
- 保存 completed run history/status。
- 触发 scheduled notification。

TrayApp 只编辑配置、保存共享 JSON、发送命令、显示状态/进度/结果，以及打开 Worker 生成的文件夹。

## 6. IPC 命令职责

IPC command set：

- `Ping`：确认 WorkerService 与 protocol version 可用。
- `GetStatus`：返回 latest persisted sync status、当前 busy state、active operation、schedule state 和 next-run。
- `ReloadSchedule`：通知 Worker 从共享 JSON 重新读取 schedule、替换未来调度并计算 next-run；request 不携带 schedule payload。
- `TestConnections`：在一个 run lease 内依次测试 Atera 和 Snipe-IT，并分别返回结果。
- `PreviewChanges`：执行 dry-run/preflight，生成临时 CSV，不进行真实写入。
- `SyncNow`：执行真实 manual sync，不生成 preflight CSV。
- `Cancel`：按 target request id 取消 active Tray-started Connection Test/Preview/Sync Now。

这些 IPC 命令是 TrayApp 的状态、配置通知和手动控制入口。Scheduled Sync 没有对应 IPC command，由 Worker 内部 scheduler 自主触发。

`Ping`、`GetStatus`、`ReloadSchedule` 和 `Cancel` 不是 run，不取得 run lease。`ReloadSchedule` 可以在 run 期间更新未来调度，但不能改变或取消当前 run。

每个 request 必须包含 protocol version、唯一 request id 和 command name；只有 `Cancel` 额外包含 target request id。

每个长任务通过同一连接按顺序返回 accepted、zero or more sanitized progress events，以及 exactly one completed/busy/cancelled/error terminal event。

## 7. IPC 通信和安全边界

Named Pipe 是 TrayApp 与 WorkerService 两个本机进程之间的通信通道，不是外部网络 API。

- protocol version 用于在 Tray 与 Worker build 不兼容时明确失败。
- UTF-8 JSON Lines 表示每行是一条完整 request/event，便于顺序传输 accepted、progress 和 terminal event。
- Windows pipe ACL 和 local-client validation 用于拒绝 remote/anonymous client。
- message length limit 用于拒绝损坏或异常大的输入，避免无界内存占用。
- 未知 version/command、重复 request id、malformed JSON 或超限消息必须在调用任何 API 前拒绝。
- API credentials 不进入 IPC request/event。
- Preview output path 由 WorkerService 在 `C:\ProgramData\AteraSnipeSync\Preflight` 下生成，client 不能指定任意路径。

第一版授权边界是可信本机登录用户；未来如需多用户隔离，应增加 allowed SID policy。

## 8. 配置、凭据与运行快照

共享配置文件：

```text
C:\ProgramData\AteraSnipeSync\appsettings.local.json
```

当前阶段 JSON 保存 API base URL、`Atera.ApiKey`、`SnipeIt.ApiToken`、mapping、import options、schedule 和 notification settings。明文凭据是明确接受的临时实现；本阶段不迁移到 environment variables，也不在保存时移除凭据。

安全规则：

- 凭据只从共享 JSON 读取。
- 凭据不得写入日志、status、history、CSV、IPC 或 exception message。
- 缺少或无效的必填配置/凭据时 fail closed，不调用外部 API。
- Worker 启动时读取 schedule，但 schedule disabled 时不需要构造 API runtime。
- 每次 Scheduled、Connection Test、Preview 或 Sync Now 取得执行机会后，都重新读取一次完整 JSON 并构造不可变运行快照。
- run 开始后的配置修改不影响当前 run；下一次 run 使用最新保存的 JSON。

Tray 保存成功后发送 `ReloadSchedule`。Worker 不轮询配置文件；直接手工编辑 JSON 不保证热加载，需再次通过 Tray 保存或重启 Worker。

## 9. Schedule 生命周期与 reload

- Worker host 启动时加载并验证 schedule，计算 next-run。
- `ReloadSchedule` 从磁盘读取并验证 schedule，不接受 client payload。
- reload 成功后取消旧 schedule wait、原子替换 schedule state，并重新计算 next-run。
- reload 只影响未来 trigger；active run 继续使用原快照。
- reload validation failure 返回脱敏 error，不能声称已应用。
- Tray 通知失败时磁盘 JSON 不回滚；Worker 下次启动时读取新 schedule。
- Worker host 启动遇到 invalid schedule 时保持 IPC 可用但不产生 scheduled run，并通过 status 暴露脱敏配置错误。
- `ReloadSchedule` 成功后清除 schedule error 并恢复未来调度。

## 10. 统一 non-overlap 规则

Scheduled Sync、Connection Test、Preview 和 Sync Now 共用一个 Worker 进程内 singleton run coordinator。

第一版采用不排队策略：

- 新 Connection Test/Preview/Sync Now 在 busy 时立即返回 busy。
- scheduled trigger 在 busy 时 skip 并记录 warning。
- lease 必须在 success、failure、exception 和 cancellation 路径释放。
- 不能只依赖 `SyncScheduler` 自己的 `_isRunning`。
- 连接测试是一个 run；Atera 和 Snipe-IT 两项 probe 不能被其它 run 插入。

## 11. Run 行为

### Scheduled

- `TriggeredBy = "scheduled"`。
- 不生成 preflight CSV，不弹 UI、不等待确认。
- 到期时自动执行并保存 status；TrayApp 是否运行不影响 scheduler。

### Connection Test

- `TriggeredBy = "connection-test"`。
- 先测试 Atera，再测试 Snipe-IT；第一项失败时仍尝试第二项，除非收到 cancellation 或 service shutdown。
- 返回两项独立的 success/error summary，不返回 raw body。

### Preview Changes

- `TriggeredBy = "preview"`，`DryRun = true`。
- Worker 生成独立 preflight directory；Preview 完成后不自动执行真实 Sync。

### Sync Now

- `TriggeredBy = "sync-now"`，`DryRun = false`。
- TrayApp 发命令前完成人工确认；Worker 不依赖 UI confirmation payload。
- Preview 后的 Sync Now 必须重新 pull、map、plan 和执行，不能从旧 CSV snapshot 写入。

## 12. 状态、日志与取消

- Worker status 同时区分 service/IPC availability、Idle/Running、active operation、schedule reload error 和 next-run。
- Preview/Sync 获得 structured result 后，Worker 使用 `CancellationToken.None` 尽力保存 history。
- status save failure 返回 sanitized error 并写 service log；不能伪报成功保存。
- Tray disconnect 不得杀死 Worker host或自动取消 run。
- `Cancel` 只接受 Tray-started request id；Scheduled run 不能由 Tray Cancel。
- service shutdown 取消 scheduler、IPC accept loop 和 active run，并等待 handlers 结束。

## 13. Category、Fieldset、Alias 与 Ignore 配置

Worker runtime 必须完整传递：

- `Mapping.DefaultCategoryName`。
- `Mapping.CompanyAliases` 与 `Mapping.ManufacturerAliases`。
- `Mapping.IgnoredDeviceTypes`。
- `SnipeIt.IgnoredMacAddresses`，同时进入 mapping 与 import options。
- `SnipeIt.MacAddressCustomFieldDbColumnName` 与 `MacAddressFieldsetName`。
- `SnipeIt.ModelCategoriesToNormalize`。
- create-missing switches、status id 与 name-match threshold。

MAC DB column 与 Fieldset name 必须成对配置；缺少 required value 时 composition fail closed。

## 14. 失败行为

以下错误必须形成可操作、脱敏的 service/IPC error：

- malformed local JSON 或 invalid schedule/time zone/run time。
- missing/invalid JSON credential 或 runtime setting。
- Worker busy。
- invalid IPC protocol/message。
- schedule reload failure。
- orchestrator/connection test exception。
- status save/notification failure。
- pipe client disconnect 或 write failure。

单次 command failure 不能终止 IPC accept loop；单次 scheduled run failure不能终止 scheduler loop。

## 15. 不负责事项

本模块不负责：

- 定义或猜测 Atera/Snipe-IT wire shape。
- mapping 或 asset matching 业务规则。
- Tray UI layout 或 Windows Service registration UI。
- 在自动化测试中调用真实 API或操作真实 Windows Service。
- pause/resume queue、blackout date、holiday calendar。
- 生产级 installer/upgrade；当前 Tray service maintenance 只服务于迭代测试。
- 当前阶段的 credential vault/DPAPI migration。

## 16. 成功条件

- WorkerService 在 TrayApp 未启动或退出后仍按 schedule 运行。
- schedule disabled 时 Worker 保持 IPC 可用且不构造 API runtime。
- TrayApp 可通过 IPC 完成 Ping、status、ReloadSchedule、combined Connection Test、Preview、Sync 和 Cancel。
- reload 后 next-run 根据新 schedule 重新计算，active run 不受影响。
- TrayApp 进程中不存在 API client/orchestrator/status writer construction。
- 四种 run 不会重叠。
- Worker 保存 Preview/Sync history，并返回受控 path。
- malformed/unauthorized IPC 不触发 API。
- 所有自动化测试使用 fake clients/orchestrator 或 mocked HTTP handler。

## 17. 扩展点

- allowed client SID configuration。
- schedule pause/resume。
- queued manual run。
- retry-after-failure policy。
- file-watcher/manual JSON hot reload。
- credential protection/secret store。
- active hours、blackout dates、holiday calendar。
- real email/webhook/Teams/Slack notification publisher。
## 2026-07 Notification test command revision

- Worker IPC accepts payload-free `TestNotifications`; notification endpoint and SMTP credentials are always loaded from shared local JSON。
- command acquires the same global run coordinator as Connection Test/Preview/Sync under operation `notification-test`, is cancellable by request id, and never queues behind another run。
- Worker loads only `NotificationConfig`, invokes `INotificationTester`, and returns independent sanitized Email/Webhook outcomes；it must not require or construct Atera/Snipe clients。
- scheduled notification publishing passes the immutable `runtime.NotificationConfig` snapshot to the concrete publisher so filter and delivery use the same configuration。
- manual Preview/Sync completion also builds `ManualPreview*`/`ManualSync*` requests, applies the same filter and snapshot, and attempts delivery；delivery failure is logged separately and never rewrites the completed sync result。

## 2026-07 Latest-run count projection

- Worker IPC terminal `WorkerSyncResultSummary.Deleted` 必须投影 `result.ImportResult?.DeletedAssets ?? 0`；`Skipped` 保留 wire member，但 Tray 将其解释为 `No change`。
- Worker status 的 `LatestSync` 透传 Status Store snapshot 的 Created、Updated、Skipped/No change 与 Deleted，不能要求 Tray 重新解析在线 history file。

## 2026-07 Fixed unknown mapping fallbacks

- Worker runtime 的 missing company/manufacturer/model fallback 固定为 `Unknown Company`、`Unknown Manufacturer`、`Unknown Model`。
- 三个值来自 Core `SyncApplicationDefaults` 常量；Worker 不读取或验证 local JSON 中历史 `Mapping.DefaultCompanyName`、`DefaultManufacturerName`、`DefaultModelName`。
- `MappingOptions` 继续保留通用可注入属性，供 Core 模块测试和非应用级调用使用；只有正式 Tray/Worker application composition 固定这些值。
- 旧 local JSON 的自定义值必须被忽略，防止服务器继续使用已经从 UI 移除的隐式设置。

成功条件：即使 settings snapshot 为三个属性提供不同字符串，`WorkerRuntimeFactory` 生成的 `BaseRequest.Mapping` 仍精确使用三个 hardcoded constants。
- `Success` bool 与 failure list 保持不变，用于运行控制和告警；Dashboard Last run 不再把该 bool 渲染成 Success/Failed 标题。
- additive count projection 不得携带 raw records、API payload 或 secrets。

## 2026-07 UTC 运行状态与固定频率调度

- Tray 保存的 `SyncScheduleOptions` 继续表达本地钟点、Windows time-zone ID 和 daily/weekly/monthly 周期，保证夏令时前后本地钟点不变。
- Worker 必须把真正可执行的 `NextRunUtc` 和已认领的 `LastTriggeredUtc` 以 UTC `Z` 时间写入独立 `schedule-state.json`；后台到点判断只比较 UTC，不比较本地 wall-clock。
- Worker 使用 15 秒固定周期检查，不得为下一次运行创建任意长度的 `Task.Delay`。系统休眠或服务停机造成多个过期周期时只认领并补跑一次，然后跳到第一个未来 occurrence。
- occurrence 在同步开始前原子认领，优先保证最多执行一次。认领后进程崩溃可以漏掉该 occurrence，但重启不得重复执行。
- schedule state 必须带规范化规则指纹。配置变化、状态缺失、格式错误或指纹不匹配时丢弃旧状态，按当前规则生成新的未来 UTC occurrence。
- DST ambiguous 本地时间固定映射到较晚的一个 UTC instant 且每个本地 occurrence 只执行一次；DST invalid 本地时间跳过该 occurrence。
- `WorkerRuntimeFactory` 每个 run 只读取一次完整 `SyncAppSettings`，从该 snapshot 获取 credentials、mapping 和 Notifications。

成功条件：31 日月度规则不存在长 delay 上限；夏令时不重复；重启和休眠使用持久 UTC 状态；每个 run 不混用不同配置版本。

## 2026-07 删除 Scheduled Dry-Run 配置

- scheduled sync 不再提供 dry-run 配置；每次到期运行固定使用 `DryRun = false` 并执行正常 Snipe-IT 写入流程。
- `SyncAppSettings`、`ILocalAppSettingsReader` 和本地 JSON 不再承载 scheduled `DryRun`。保存完整配置时删除旧 `Sync.DryRun` 字段，加载旧文件时忽略该字段。
- `WorkerRuntimeFactory` 的基础请求和 `ScheduledSyncRequestFactory.CreateScheduledRequest` 都必须把 run-level 与 Snipe import-level `DryRun` 固定为 `false`，避免旧值或其它调用者重新启用 scheduled dry-run。
- Dashboard `Preview` 仍固定 `DryRun = true` 并生成 manual preflight CSV；`Sync Now` 仍固定 `DryRun = false`。Importer、run result、history 和 notification 中保留 DryRun 运行态字段，用于准确记录 Preview 与真实运行。

成功条件：旧配置即使包含 `Sync.DryRun = true`，下一次保存也会移除该字段，scheduled request 仍为真实运行；Preview 继续不发送 Snipe-IT POST/PATCH。

## 2026-07 Completed run 通知透传

- Worker 不根据 asset/reference failure count 二次改写 `SyncRunResult.Success`；它必须把 Orchestrator 的 pipeline completion 语义原样投影到 IPC 和 `NotificationRequestFactory`。
- 完整执行但含记录级 failure 的 manual/scheduled run 仍返回 Completed terminal，并匹配 `ManualSyncCompleted`/`ScheduledSyncCompleted` 订阅；通知 severity 为 Warning，失败计数和安全明细继续保留。
- fatal/incomplete result 即使已有成功 action count，仍匹配 `*SyncFailed`。通知发送失败不得反向改写 sync result。

成功条件：Worker command 集成测试证明 completed-with-record-failures 会发布 completed event；无真实 API 或外部通知调用。
