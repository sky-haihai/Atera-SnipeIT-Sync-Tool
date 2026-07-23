# Tray App - 功能职责

## 1. 模块目标

TrayApp 是 Windows 本机 operator UI。它常驻 system tray，用于查看 Worker/Windows Service 状态、编辑完整配置、发起手动操作、显示 progress/result，以及为迭代测试重启或重新注册 Worker Service。

正式架构中 TrayApp 不执行同步业务。所有 Atera/Snipe-IT API 访问、orchestrator execution、preflight 生成、scheduled run 和 history 写入都由 WorkerService 完成。

## 2. 当前基线与本阶段目标

当前临时 `ManualSyncForm` 已经完成并经过 operator 验证：

- Preview 与真实 Sync。
- Atera/Snipe-IT connection test。
- mapping/import config UI。
- preflight CSV、progress bar、sanitized logs、error grouping 和 result rendering。

但它仍是直接执行 API/Core pipeline 的 no-service test program，不是正式 tray application。

本阶段迁移为：

```text
TrayDashboardForm / SyncConfigurationPage
  -> Named Pipe IPC
WorkerService
  -> Scheduler + Core pipeline
```

迁移完成后，旧 direct-run path 必须删除或不可到达；Worker 不可用时不能静默回退到 UI 进程执行。

## 3. TrayApp 职责

- 提供 single-instance Windows tray process、`NotifyIcon` 与 context menu。
- 打开/隐藏一个复用的 `TrayDashboardForm`。
- 通过嵌入式 `SyncConfigurationPage` 编辑并原子保存完整 local JSON，包括当前阶段的明文 API credentials 和 schedule。
- 保存配置后发送 `ReloadSchedule`，显示 Worker 是否应用成功和新的 next-run。
- 通过 IPC 发出 Ping、GetStatus、combined Connection Test、Preview、Sync Now 和 Cancel。
- 使用 AntdUI 统一 Dashboard 与 Configuration 的窗口、页头、卡片、按钮、输入、标签页、状态和进度视觉。
- 同时显示 Windows Service 状态、Worker IPC 状态、active operation、progress 和 latest result 摘要卡片。
- 详细 command/progress/failure 记录只写 daily log file；主界面不提供 log text area，也不回显逐记录 progress message。
- 提供一个 `Open Log Folder` 入口，打开受控根目录 `C:\ProgramData\AteraSnipeSync`，集中访问 logs、history 与 preflight artifacts。
- 提供固定文案的 `Restart Service`，通过点击时 UAC elevation 完成 Service restart/start；不在主界面暴露安装或重新注册操作。
- Worker offline/busy/configuration error 时显示可操作提示。
- TrayApp 退出时不停止 WorkerService。

## 4. 不负责事项

TrayApp 不负责：

- 构造 `AteraClient`、`SnipeImporter` 或 `SyncOrchestrator`。
- 发送 Atera/Snipe-IT HTTP request。
- 保存 completed `SyncRunResult` history。
- scheduled run lifetime 或 scheduled notification。
- 在 Worker offline 时使用本地 fallback pipeline。
- 从任意 UI/JSON/CLI 输入 service name、display name 或 service executable path。
- 生产级 MSI/install/upgrade/rollback；Service maintenance 按钮当前用于受控迭代测试。
- 自动化测试中的真实 API probe 或真实 Windows Service mutation。

## 5. Tray lifecycle 与窗口

TrayApp 正常启动时：

- 使用 named mutex 保证每个 Windows session 只有一个实例。
- 创建 `ApplicationContext` 和 `NotifyIcon`。
- 不自动打开 Dashboard。
- 异步读取 Windows Service 状态并 Ping Worker；Worker offline 不导致 TrayApp 退出。

单击或双击左键 tray icon 都调用同一个 idempotent `ShowDashboard()`。右键 context menu 包含：

- `Open Dashboard`
- `View Last Sync Status`
- `Open Log Folder`
- separator
- `Exit Tray App`

窗口行为：

- Dashboard close 只隐藏到 tray，不 dispose、不停止 Worker、不自动 Cancel。
- 再次打开复用同一个 `TrayDashboardForm`，并立即刷新 SCM 和 IPC 状态。
- Config 在同一 `TrayDashboardForm` 内切换到唯一的 `SyncConfigurationPage`；不创建第二个窗口。
- `Exit Tray App` dispose forms、log writers 和 notify icon，再结束 UI message loop。
- Tray exit 不调用 Windows service stop API。

## 6. Dashboard 内容

Dashboard 必须显示：

- Windows Service：`Not Installed`、`Stopped`、`Start Pending`、`Running`、`Stop Pending` 或 `Unknown`。
- Worker IPC：`Online`、`Offline` 或 protocol mismatch。
- 当前 operation：`Idle`、`Scheduled`、`Connection Test`、`Preview` 或 `Sync Now`。
- schedule reload result 与 next-run。
- `Config`、`Preview`、`Sync Now`。
- 固定文案的 `Restart Service`；Service Stopped 时仍显示该文案并执行 start。
- 一个 `Open Log Folder`，打开 `C:\ProgramData\AteraSnipeSync`。
- 当前 phase、progress bar、最近一次 result summary。
- Tray-started operation 的 `Cancel`。

Dashboard 使用 AntdUI 的 light theme、`Window`、`PageHeader`、`Panel`、`Button`、`Label` 与 `Progress`。背景、卡片、主次操作、错误操作与状态颜色必须由同一主题定义；不得混入旧式 GroupBox/日志 TextBox。最近结果只显示 finish time 和 Created/Updated/No change/Deleted 四项计数。

每个 Tray-started Test/Preview/Sync 的 command start、完整 Worker progress callback、terminal summary 和 structured failure 必须进入 `Logs\ManualSync_yyyyMMdd.log`。连接测试由嵌入式 Configuration 页面启动；Dashboard 只显示 Preview/Sync 的稳定阶段与“详细内容已写日志”的短提示，避免设备级记录成为 UI log。

SCM `Running` 不等同 Worker `Online`；Dashboard 必须分别展示并分别刷新两种状态。

## 7. Dashboard 控件状态

| 状态 | Config | Preview/Sync | Restart Service | Cancel |
|---|---:|---:|---:|---:|
| Worker Online + Idle | 启用 | 启用 | 启用 | 禁用 |
| ReloadSchedule | 禁用 | 禁用 | 禁用 | 禁用 |
| 任意 Worker run | 启用 | 禁用 | 禁用 | 仅 Tray-started run 启用 |
| Service Stopped | 启用 | 禁用 | 启用，执行 Start 但文案不变 | 禁用 |
| Service Not Installed | 启用 | 禁用 | 禁用 | 禁用 |
| Service maintenance 中 | 禁用 | 禁用 | 禁用 | 禁用 |
| SCM Running、IPC Offline | 启用 | 禁用 | 启用 | 禁用 |

UI availability 必须同时依据：

- 当前 local UI operation。
- Windows Service Control Manager 状态。
- Worker `GetStatus` 返回的 busy/active operation。

发出 Reload 或 run request 前立即更新本地 UI state，避免 response 返回前的双击竞态。Reload、run 或 service maintenance 结束后，必须先重新读取 SCM 并调用 `GetStatus`，再决定是否恢复按钮。

UI disable 是第一道保护；Worker global coordinator 仍是最终 non-overlap authority。

## 8. 本机文件位置

```text
Config:              C:\ProgramData\AteraSnipeSync\appsettings.local.json
Preflight:           C:\ProgramData\AteraSnipeSync\Preflight
History:             C:\ProgramData\AteraSnipeSync\History
Logs:                C:\ProgramData\AteraSnipeSync\Logs
Service maintenance: C:\ProgramData\AteraSnipeSync\Logs\ServiceMaintenance_*.log
```

TrayApp 只能打开这些受控目录；IPC request 不接受任意 output path。仓库只提交 placeholder sample config，不提交 ProgramData runtime files。

## 9. 配置与凭据

`SyncConfigurationPage` 编辑：

- Atera API base URL 与 API key。
- Snipe-IT API base URL 与 API token。
- mapping/import options。
- Daily/Weekly/Monthly schedule。
- notification settings 中本阶段已有的可编辑项。

当前阶段接受 JSON 明文凭据：

- secret textbox 使用 password masking，但从 JSON 正常加载、编辑和保存。
- 保存时不得移除 `Atera.ApiKey` 或 `SnipeIt.ApiToken`。
- UI/log/message 不回显完整 secret。
- config mutation 使用 cross-process lock 与 atomic replacement，并保留未编辑 section。

配置字段继续包含可编辑的 default category、aliases、ignored device/MAC、normalization categories、status id、MAC custom field/fieldset、name-match threshold 和 create-missing switches；company/manufacturer/model 的 Unknown fallback 不再是配置字段。

Validation：

- 两个 base URL 必须通过 `ApiEndpointValidator`；Snipe-IT URL 包含 `/api/v1` 且不能是 example host。
- API key/token 和其余 required category/mapping strings 非空。
- default status id > 0；name match threshold 在 `(0, 1]`。
- MAC DB column 与 Fieldset name 同时填写或同时为空。
- model normalization source list 至少一个值。
- alias 每行只允许一个 `=`，两侧 trim 后非空；拒绝旧 `=>` syntax。
- ignored MAC 由 Core normalizer 验证、规范化和去重。
- enabled schedule 必须满足 Worker schedule contract。

## 10. Config Save 与 ReloadSchedule

保存流程固定为：

1. 在 Tray 内完成完整配置和 schedule validation。
2. 原子保存共享 JSON。
3. Dashboard 立即进入 `ReloadingSchedule`；禁用 Preview、Sync Now 和 Restart Service。
4. 发送不带 payload 的 `ReloadSchedule`。
5. Worker 从共享 JSON 读取 schedule，返回 applied state 与 next-run。
6. Tray 刷新 SCM 和 `GetStatus`：Idle 才恢复 run/service buttons；Running 保持禁用。

Worker Offline、reload error 或 timeout 时：

- 不回滚已经成功保存的 JSON。
- 显示“配置已保存，但 Worker 尚未重新加载”的明确 warning。
- 不得显示 applied/success。
- Worker 下次启动时读取新 schedule。

## 11. IPC client behavior

TrayApp 使用 Worker 定义的 protocol version、command names 和 event models。每个 command：

- 生成唯一 request id，使用本机 default pipe name 和有限 connect timeout。
- 校验 response version/request id 和 event order。
- 收到 exactly one terminal event 后完成。
- malformed/out-of-order/mismatched response 视为 protocol failure。

Named Pipe 是两个本机进程的通信通道。version 用于识别 build 不兼容；JSON Lines 用于划分连续 event；ACL/local validation 拒绝 remote/anonymous client；message cap 防止异常输入无界占用内存。

Cancellation：

- Cancel button 通过独立 pipe connection 发送 target request id。
- 不能只取消 Tray-side read token并声称 Worker 已停止。
- scheduled run 不允许从 Tray Cancel。

## 12. Worker availability 与 busy behavior

- Worker offline：保留 config/service maintenance/open-folder/status fallback，禁用 Configuration 内的 Test Connections 和 Dashboard 的 Preview/Sync。
- protocol mismatch：提示 Tray/Worker build 不匹配，不自动重试 run。
- Worker busy：显示 active operation，不排队、不自动重试。
- configuration error：显示 Worker 返回的 sanitized remediation message。
- pipe disconnect：不能声称 command 已取消；operator 通过 status/history 确认最终结果。

## 13. Combined Connection Test

- `API & Credentials` 配置页提供一个 `Test Connections` 按钮；Dashboard 不显示该按钮。
- 点击测试先验证并原子保存当前表单的完整配置，再发送 `ReloadSchedule`，随后发送一个不含 URL/credential payload 的 `TestConnections` command。
- Worker 在同一个 run lease 内先测试 Atera，再测试 Snipe-IT。
- Atera failure 不阻止 Snipe-IT probe，除非 cancellation/service shutdown。
- Configuration UI 分别显示两个系统的 bounded sanitized success/failure；详细 progress/result 只进入 daily log，不接收 raw JSON body。
- Connection Test 与 Scheduled/Preview/Sync Now 共享 Worker coordinator。

## 14. Preview 与 Sync Now

Preview：

1. Worker Online + Idle 时点击 Preview。
2. Worker reload 完整 JSON、pull/map/plan，并在标准 Preflight root 生成 CSV。
3. Worker 保存 Preview history；Tray 显示 result。operator 可通过统一的 `Open Log Folder` 进入 ProgramData 根目录查看 artifacts。
4. Preview 不自动进入真实写入。

Sync Now：

1. Worker Online + Idle 时点击 Sync Now。
2. UI 显示真实写入确认，默认按钮为 No。
3. 确认后发送新的 `SyncNow` command。
4. Worker 重新读取 JSON、pull/map/plan/execute 并保存 result/history。

Sync Now 不使用旧 Preview snapshot，也不生成 Preview CSV。

## 15. Progress、日志和结果

Dashboard 保留经验证的加权、单调 progress 语义：真实 Sync 的 external writes 映射到 35–99%，Preview planning 映射到 15–95%，只有 terminal state 到达 100%。重复/乱序 child progress 不得让 UI 百分比倒退。

稳定 UI milestone：

```text
Starting sync.
Processing models.
Processing categories.
Processing assets.
Completed.
```

失败/取消显示 `Sync failed.` / `Sync canceled.`。所有日志与结果都必须脱敏；不显示 credential、Authorization header、raw request/response 或 stack trace。

Dashboard result 显示 success/failed/cancelled、pulled/mapped、created/updated/skipped/failed、reference summary、warning/failure groups、Preview/report path。

`View Last Sync Status` 优先使用 Worker `GetStatus`；Worker offline 时只读 fallback 到 latest `JsonFileSyncStatusStore`，不得构造 API client或写 history。

## 16. Windows Service identity

以下值固定在代码中，不从 UI、JSON 或任意 helper argument 接收：

```text
Service name:  AteraSnipeItAutoSync
Display name:  Atera Snipe-IT Auto Sync
Executable:    <AppContext.BaseDirectory>\AteraSnipeSync.WorkerService.exe
Startup type:  Automatic
Account:       LocalSystem
```

SCM 查询、Restart、Delete/Create/Start 和 tests 都使用 service name `AteraSnipeItAutoSync`。Windows Services UI 显示 `Atera Snipe-IT Auto Sync`。

## 17. Restart Service

- 按钮点击时才通过 UAC 启动 elevated helper；Tray 正常运行不要求管理员权限。
- Service Running：Stop → wait Stopped → Start → wait Running。
- Service Stopped：直接 Start。
- Service 不存在：失败并提示需要通过部署/安装流程注册 Service；主界面不提供注册按钮。
- Stop/Start 每阶段最多等待 30 秒。
- SCM Running 后每秒检查 Worker IPC，最多 15 秒。
- SCM Running 但 IPC 未恢复时显示“Service 已启动，但 Worker health 尚不可用”。

## 18. Internal re-register helper

为现有受控部署/迭代流程保留内部 helper，但不在 Dashboard 或 tray context menu 暴露入口。内部流程：

1. 在申请 UAC 和停止旧服务前验证同目录 `AteraSnipeSync.WorkerService.exe` 存在。
2. elevated helper 查询固定 service name。
3. 若存在且正在运行，停止并等待 Stopped。
4. 删除旧 registration 并等待 SCM 中消失。
5. 以固定 name/display name/executable、Automatic、LocalSystem 重新注册。
6. 启动并等待 Running。
7. Tray 刷新 SCM 和 IPC 状态。

Delete/Create/Start 任一步失败即停止后续步骤，返回失败 stage并写脱敏 maintenance log。Create failure 时可能保持 Not Installed；Start failure 时保留已注册但 Stopped 的 Service，供查看日志和重试。

## 19. UAC helper 安全边界

- TrayApp 仅接受 `--service-maintenance restart` 和 `--service-maintenance reinstall-and-restart` 两个 helper mode。
- helper mode 在 single-instance guard 之前识别，不创建 NotifyIcon、Dashboard 或第二个 Tray process。
- parent 使用 `ProcessStartInfo.Verb = "runas"`，异步等待，不能冻结 UI。
- UAC 取消是正常取消，不改变 Service，不显示为成功。
- helper 不接受任意 service name、display name、executable path 或额外 shell command。
- 系统命令使用固定 executable 与 `ProcessStartInfo.ArgumentList`，不拼接可注入 command string。
- maintenance log 只写受控 ProgramData Logs，不包含 config/credential。

## 20. Failure behavior

- local validation failure：不保存 config、不发送 Reload。
- config save failure：不发送 Reload。
- reload failure：JSON 保留，UI 不声称 Worker 已应用。
- busy：run buttons 保持禁用，不清除上一次 result/path。
- Worker offline/protocol/pipe error：显示脱敏 guidance，不回退 direct run。
- service executable missing：不申请 UAC、不停止或删除旧 Service。
- Service maintenance stage failure：显示 stage和 log path，重新查询 SCM，不凭 helper exit 假设状态。
- log writer failure：显示一次 warning，不递归写同一 writer。

## 21. Automated test boundary

- IPC client、state reducer、protocol validation、progress、logs、config round-trip 和 service maintenance orchestration 使用 pure automated tests。
- Service tests 使用 fake process runner/service status reader；不得注册、删除、停止或启动真实 Windows Service。
- 所有文件测试使用 temporary directory，不读写真实 ProgramData。
- Worker-side HTTP tests使用 mocked handlers，不调用真实 API。
- 真实 API 和真实 Service registration 仅作为 manual acceptance。

## 22. 本阶段成功条件

- TrayApp 是 single-instance tray process；Dashboard close 只隐藏，Tray exit 不停止 Worker。
- `TrayDashboardForm` 和嵌入式 `SyncConfigurationPage` 替代临时 `ManualSyncForm` entry point。
- Dashboard/Configuration 全部使用 AntdUI；主界面不存在多行 log/result TextBox。
- Tray-started detailed progress 无损写入 daily log，并在 Tray exit 时 flush writer。
- Test/Preview/Sync/Cancel/ReloadSchedule 全部通过 Worker IPC；Test Connections 只位于 `API & Credentials` 配置页。
- Reload 或任意 run 期间对应 run actions 与 Restart Service 禁用。
- TrayApp production code 不构造 API client、orchestrator 或 status writer。
- JSON 明文凭据可 round-trip，但不进入 IPC/log/result。
- Dashboard 只暴露固定文案的 Restart Service；Restart 与保留的 internal re-register helper 只操作 `AteraSnipeItAutoSync`，并使用 display name `Atera Snipe-IT Auto Sync`。
- Worker EXE 缺失时不会删除旧 Service。
- Worker offline/busy/version mismatch/UAC cancel 都有确定 UI behavior。
- automated tests 不访问真实 API或真实 Windows Service。

## 23. 后续扩展

- credential vault/DPAPI migration。
- production installer、upgrade/rollback 和 signed elevated helper。
- service recovery policy UI。
- allowed client SID configuration。
- notification configuration UI。
- schedule pause/resume、manual queue 与 maintenance window。
## 2026-07 Notification configuration test revision

- Notifications tab adds SMTP host/port/TLS/username/password、Email from/to、Webhook URL and one `Test Notifications` button。
- Test action validates and atomically saves the complete current form, reloads Worker configuration, then sends payload-free `TestNotifications` over IPC。
- UI shows compact independent Email/Webhook `Accepted`、`Failed` or `Not configured` results；addresses、credentials、endpoint URL、raw response and stack trace never appear in UI/log/IPC。

### 2026-07 Teams webhook format 与测试结果语义

- Notifications 页在 Webhook URL 前增加 payload format 选择，提供 `Teams Workflow (Adaptive Card)` 与 `Generic JSON`；默认选 Teams，与 `NotificationConfig.WebhookPayloadFormat` 双向加载/保存。
- Test Notifications 收到 channel 的同步成功结果时显示 `Accepted` 而非 `Success`。该状态只说明服务器 SMTP/HTTP endpoint 接收请求；UI 不声称 email inbox、Teams channel 或 Power Automate downstream action 已完成。
- 失败、未配置、busy 与保存/reload 行为保持不变，UI 和 manual log 仍不得暴露 recipient、credential、webhook URL 或 response body。

## 2026-07 Dashboard layout consolidation

- Dashboard 主内容第一行左侧垂直排列 `System status` 与 `Current activity`，右侧垂直排列 `Schedule` 与 `Latest run`；第二行为横跨两列的 titleless actions surface，第三行为 log folder footer。
- `System status` 只展示 Windows Service 与 Worker IPC；`Current activity` 独立展示 phase、AntdUI progress bar 与 bounded activity message。
- Run/Preview/Cancel/Restart Service 使用四等分 `TableLayoutPanel`，每个按钮填充自己的 column，并保留一致 gutter；在 Dashboard minimum width 与 DPI scaling 下不得依赖 FlowLayout wrap，也不得裁掉按钮文字或上下边缘。
- action surface 不包含 title row，并通过显式 padding 为 38px minimum button 与 margin 预留足够高度；disabled/enabled、click handler 和安全边界不因布局变化而改变。

成功条件：minimum-size Dashboard 中四个 run/service 按钮完整可见；progress control 是独立 `Current activity` card 的 descendant；Schedule、Latest run 与 footer 均保持可见。

## 2026-07 Last run count-only presentation

- `Latest run` 不显示 Success、Failed 或 PartialSuccess outcome 标题；只显示 finished time 和四项资产计数：`Created`、`Updated`、`No change`、`Deleted`。
- `No change` 来自现有 `SkippedAssets`/snapshot `Skipped`，其已验证语义是现有资产完全一致且未执行 HTTP write；`Deleted` 当前显示 0，因为产品策略不执行删除。
- Pulled、Mapped、Failed 不再占用 Latest run metric cards；失败详情仍保留在 report、daily log 和 Current activity message。
- Worker online 时使用 `SyncStatusSnapshot`/terminal `WorkerSyncResultSummary` 的四项计数；Worker offline 时 `LatestSyncHistoryReader` 返回同样的 structured count model，不得把 report result string 拼回 UI。
- terminal 完整返回时，Current activity 使用 `Completed`/`Sync completed.`，即使 failure count 非零；失败计数与明细仍保留在 activity message/report。只有取消、中断、阶段异常或其它未完成 run 显示 `Sync canceled.`/`Sync failed.`；Last run card 本身保持 count-only。

成功条件：在线、刚完成和离线 fallback 三条路径都只向 Last run 提供 Created/Updated/No change/Deleted；没有完成记录时显示 em dash；布局测试确认四个 metric captions 且不含 Pulled/Mapped/Failed outcome card。

## 2026-07 Completed 与 fatal run 展示边界

- Dashboard 不再显示 `Partial success` 或 `Sync partially completed.`。
- Worker 返回 `Success = true` 时统一显示 `Sync completed.` 和 `Completed.`；message 继续显示 Created/Updated/No change/Deleted/Failed 等计数。
- Worker 返回 `Success = false` 时统一显示 `Sync failed.`，即使异常前已有成功 action；取消 terminal 继续单独显示 `Sync canceled.`。
- Latest run card 继续只显示 count，不增加 outcome 标题。

## 2026-07 In-place configuration page navigation

### 目标

Configuration 不再创建 modal 或第二个 top-level WinForms window。Operator 点击 Dashboard PageHeader 的 `Configuration` 后，现有 `TrayDashboardForm` 立即把 Dashboard content 替换为完整配置页；`Cancel` 或保存成功后恢复原 Dashboard content。

### 输入与输出

- 输入：共享 `LocalAppSettingsStore`、`WorkerIpcClient`、`DailyLogWriter`，以及 operator 的 Configuration/Cancel/Save/Test 操作。
- 输出：同一主窗体内的 Dashboard 或 Configuration 页面；保存后的 schedule reload 状态仍回到 Dashboard Current activity message；连接和通知测试结果留在 Configuration 页面。

### 职责

- `TrayDashboardForm` 唯一拥有 page host、Dashboard view 和一个可复用 configuration page，并负责页面切换。
- configuration page 每次进入时重新读取 local JSON，避免复用控件后显示上一次已删除/已变更的旧值。
- configuration page 自己管理 Load/Save/Test 按钮互斥；仅查看配置不得把 `DashboardState.LocalActivity` 改成 `SavingConfiguration`，也不得依赖 `DialogResult`、`ShowDialog`、`Close` 或子窗口 FormClosed 事件恢复 Config 按钮。
- Save 成功后通知 `TrayDashboardForm` 返回 Dashboard 并执行现有 `ReloadScheduleAsync`；Save 失败留在配置页显示错误。
- Cancel 不保存，直接恢复 Dashboard；再次点击 Configuration 必须仍可进入。
- 主窗体关闭仍只隐藏 Tray Dashboard，不销毁 Worker 或打开第二个窗口。

### 失败条件

- 配置读取失败：留在配置页，显示 bounded error，重新启用 Cancel/Save/Test actions；不得把主窗体卡在不可导航状态。
- 保存失败：不切回 Dashboard，不执行 schedule reload。
- Worker reload/test 失败：保持已有 sanitized message/log 规则，不泄漏 credentials。

### 不包含

- 不改变配置 JSON schema、Atera/Snipe API wire contracts、Worker scheduling policy 或 notification payload。
- 不增加 browser-style history、多个 configuration instance 或 detachable settings window。

### 成功条件

真实 control-tree 测试必须证明 Configuration 是 `TrayDashboardForm` 内的 `UserControl` 页面而不是 Form；Dashboard → Configuration → Cancel → Configuration 可重复执行，主窗体实例不变且 Configuration 按钮恢复可用。

## 2026-07 Remove Unknown fallback inputs

- Configuration 的 `Mapping & Import` tab 不再显示 Default company、Default manufacturer、Default model 三个输入。
- application-wide fallback 固定为 `Unknown Company`、`Unknown Manufacturer`、`Unknown Model`，operator 不需要也不能从 UI 修改。
- `BuildSettings` 使用共享 `SyncApplicationDefaults` 常量，不从 AntdUI input 读取这三个值；`Apply`/reset 不再查找对应 controls。
- `LocalAppSettingsStore` load 忽略旧 JSON 的三个字段并返回固定常量；save 从 `Mapping` section 移除三个旧字段，避免配置文件继续暗示它们可编辑。
- Default category 仍可编辑，因为它不是 Unknown fallback，且仍影响 category normalization。

成功条件：配置页 control tree 不包含 `DefaultCompany`、`DefaultManufacturer`、`DefaultModel` inputs；保存后的 JSON 不包含三个旧 mapping properties；Worker 始终使用固定值。

## 2026-07 AntdUI Demo visual alignment

- Dashboard 移除深色 hero 及 `Atera → Snipe-IT` 大标题；主窗体只保留紧凑的 AntdUI `PageHeader`，Configuration 操作放入 header 右侧。
- 画布、边框、primary color、card radius 和按钮视觉向 AntdUI 官方 Demo 的轻量布局收敛；卡片使用柔和低透明度阴影而不是重阴影，现有 status、metrics、actions 和 footer 信息层级不变。
- Configuration 的六个基础布尔设置全部使用 AntdUI `Switch` 滑动开关；setting label 保留在 grid 左列，右列只显示原生 switch，不再使用 `Checkbox`。
- UI-only 调整不得改变设置默认值、load/save mapping、验证规则、Worker IPC、schedule reload 或外部 API 行为。
- 自动回归必须证明 Dashboard control tree 不再包含 `Atera → Snipe-IT` hero 文案，Configuration 不含 AntdUI `Checkbox`，且六个具名基础 `Switch` 均存在。

## 2026-07 Dashboard card and action refinement

- Dashboard 第一行仍保持左右两列；左列改为上方独立 `System status` 卡片和下方独立 `Current activity` 卡片，右列仍为上下两张卡片。
- `System status` 只显示 Windows Service 和 Worker IPC；删除重复的 Current operation row。当前运行阶段、progress 与 bounded message 只属于 `Current activity` 卡片。
- 右上卡片标题从 `Automation` 改为 `Schedule`，内部 Schedule/Next run 数据与状态逻辑不变。
- 横跨两列的 action surface 删除 `Run and service actions` 标题和标题留白，只保留 Sync Now、Preview、Cancel、Restart Service 四个等宽按钮。
- Configuration 与 Open Log Folder 必须有固定的 DPI-safe 最小宽度，图标和完整文字不得被裁切；Configuration 的可点击控件使用正常圆角，不能再以 radius 0 的矩形区域呈现。
- 自动回归必须验证新卡片 ownership、移除的重复标题/row、Schedule 标题、无标题 action surface，以及两个长文字按钮的 bounds、icon 和 radius。

## 2026-07 Dashboard contrast and density refinement

- Dashboard 画布必须与白色 surface 有可见但克制的明度差；主卡片和 action surface 使用 1px 中性边框、6px 柔和阴影与 8px 圆角，避免白色区域互相融在一起。
- Secondary/Open Log Folder 按钮必须有 1px 可见边框和浅色 hover/active background；Primary 与 Danger 语义、disabled 状态和点击行为不变。
- Dashboard 默认高度收紧，header、body outer padding、card margin、card inner padding、title row、action row/footer row 和 action button gutters 同步缩小；不得靠压缩字体或可点击高度制造紧凑感。
- Latest run metric tiles 增加细边框并收紧 tile gutter/top spacing；四项指标、运行状态、进度与 footer 文案不变。
- minimum-size 回归必须继续证明按钮不裁切，并新增主卡片/action surface shadow、border、compact margin/padding 和 secondary button border assertions。

成功条件：Dashboard 在默认尺寸下层级清晰、卡片/白色按钮边界可辨，action 区不再占用大块空白；自动化布局测试离线通过。

## 2026-07 Configuration vertical density refinement

- Configuration 四个 tab 的 setting rows 增加上下留白；label、input、select、test button 和 switch 在纵向保持一致的舒展节奏，不能紧贴相邻行。
- 单行 input/select/test button 的实际高度提升到 44px，multiline input 提升到 96px，Switch 提升到 56×32；footer action 高度至少 46px，并由明确的 footer row sizing 防止 TableLayout 按 preferred height 压扁按钮。增加 margin 后由 tab 的既有 AutoScroll 承载较长内容。
- 配置字段、默认值、tab 顺序、save/test 行为与验证规则不变。

## 2026-07 Schedule frequency-aware fields

- Schedule tab 根据 Frequency 动态显示适用字段：Daily 隐藏 Weekly days、Monthly days 与 Also run on the last day of month；Weekly 只显示 Weekly days；Monthly 只显示 Monthly days 与 last-day switch。
- Windows time zone ID 和 Run times 对三种 frequency 始终可见。
- 新配置/无 Schedule 配置时，Windows time zone ID 默认使用当前进程所在 Windows 系统的 `TimeZoneInfo.Local.Id`；加载已有配置时优先显示已保存的 `Schedule.TimeZoneId`。
- 切换 frequency 只改变显示，不清除隐藏字段的当前值；保存仍由现有 `SyncScheduleOptions` 与 `ScheduleCalculator.Validate` 决定有效性。
- 自动回归必须覆盖 Daily、Weekly、Monthly 三种显示组合以及 time-zone row 始终可见。

## 2026-07 Dashboard footer wording

- Dashboard 底部使用 `Detailed activity is available in the log folder.`，说明详细活动可从日志文件夹查看，但不暗示 Schedule 的运行频率。
- 日志按日滚动的实现、Open Log Folder 行为、配置属性和同步运行语义均不变。
- 自动回归必须确认新说明存在，并且 Dashboard control tree 不再包含 `Detailed activity is written to daily log files.`。

## 2026-07 Notification outcome switches

- Notifications tab 不再按 scheduled/manual/preview/fallback 分列八个 event switch，只显示 `Sync completed` 与 `Sync failed` 两个 AntdUI `Switch`。
- Completed switch 代表 `ScheduledSyncCompleted`、`ManualSyncCompleted`、`ManualPreviewCompleted`、`SyncCompleted`；Failed switch 代表对应四个 failed event。保存时仍把所选组展开成现有 canonical `NotificationConfig.OnEvents` names，不改变 local JSON schema 或 Worker `NotificationEventFilter`。
- 加载旧配置时使用 trim + case-insensitive matching：一组中任一已知 event 存在即开启该组 switch。下次保存会把该组规范化为完整四个 event；未知 legacy event 不开启任一组。
- `Notifications enabled` 仍是总开关；两个 outcome switch 都关闭时正常通知不发送。`Test Notifications` 继续绕过 enabled/event filter。
- SMTP、Webhook、Adaptive Card payload、credentials、notification result 文案和 sender 行为均不改变。

成功条件：control tree 只有 `NotifySyncCompleted` 与 `NotifySyncFailed` 两个 outcome switches；读取旧的任一 completed/failed event 能开启对应组，保存选择会产生完整 canonical event group，且自动化测试不发送真实 notification。
- Test button and Save changes mutually disable while either operation is active；detailed sanitized command/result status continues to the daily manual log。

## 2026-07 Worker IPC 资源上限

- 本机 `BuiltinUsers` 继续拥有 pipe 读写权限，现有本机来源检查保持不变。
- Worker 同时处理的 pipe connection 不得超过 16；达到上限时等待已有连接释放，不得无限创建 handler 或 server instance。
- 客户端连接后必须在 10 秒内发送完整的第一行 JSON request。超时只关闭该空闲/半包连接，不限制已接受命令的执行时长，也不取消正在运行的同步。
- host shutdown、无效请求、读取超时和正常终止都必须释放连接名额；超时日志不得包含半包内容或 secret。

成功条件：普通本机 Tray 继续可用，长同步不受 10 秒限制，空闲连接不能无限耗尽 Worker 资源。

## 2026-07 Configuration Cancel 文案

- Configuration footer 左侧导航按钮的显示文案从 `Back to Dashboard` 改为 `Cancel`。
- `Cancel` 继续放弃当前页面尚未保存的编辑并返回 Dashboard；不保存配置、不发送 `ReloadSchedule`，也不改变页面复用或再次进入时重新加载本地 JSON 的行为。
- 本次调整只改变按钮文案；现有左箭头图标、尺寸、启用状态和 `DashboardRequested` 导航事件保持不变。

成功条件：Configuration control tree 只显示一个 `Cancel` footer button，不再显示 `Back to Dashboard`；点击后返回同一个 Dashboard，且未触发保存。

## 2026-07 删除 Scheduled Dry-Run 滑块

- Mapping & Import tab 删除 `Dry run for scheduled/manual Sync Now` 及其 `DryRun` switch，不提供替代设置。
- Configuration load、reset、validation 和 save 不再读取或写入 scheduled dry-run 值；保存时由共享 settings store 清除旧 JSON `Sync.DryRun`。
- Scheduled sync 与 Sync Now 固定真实运行；Preview 仍是独立的无写入预览操作，不能因删除该滑块而失去 dry-run 保护。

成功条件：Configuration control tree 不存在名为 `DryRun` 的 switch 或包含 `Dry run` 的 label；旧配置可正常加载和保存，Preview 仍不写入 Snipe-IT。

## 2026-07 Windows 11 / Server 2022 视觉一致性

- TrayApp 必须自行绘制 Dashboard 正常窗口的圆角、边框和阴影，不能把窗口圆角是否存在交给 Windows 11 或 Windows Server 2022 的 DWM 外观策略决定；最大化窗口继续使用直角以贴合工作区边界。
- 主卡片、titleless action surface、nested metric tile 和可点击控件使用分层但固定的圆角 token。Windows 版本、窗口大小和 RDP 会话不得改变这些语义层级。
- `Sync Now`、`Preview`、`Cancel`、`Restart Service` 四个 Dashboard 主操作保持四列分组，但按钮本身使用统一紧凑逻辑尺寸并在列内居中；扩大窗口或 RDP 分辨率不得把按钮背景拉伸到整个单元格。
- TrayApp 保持 DPI-aware：Windows Forms 和 AntdUI 按当前 monitor/RDP session 的 DPI 清晰缩放字体、图标、圆角和 logical dimensions。不得通过 DPI-unaware 模式换取不同机器截图的物理像素一致。
- Configuration、Open Log Folder、Dashboard reducer、click handlers、close-to-hide、窗口拖动/缩放/最小化/最大化及所有 Worker/SCM 状态行为保持不变。
- 不改变 local JSON、Worker IPC、Atera/Snipe-IT wire contracts、notification 或 scheduler 行为。

成功条件：离线 STA control-tree 回归在 Dashboard minimum size 和扩展尺寸下都证明窗口/表面圆角 token 生效，四个主操作按钮尺寸一致、完整位于父容器内且不随列宽拉伸；Windows 11 与 Windows Server 2022 的 100%/150% 手工检查确认正常窗口外框和内容层级一致。

## 2026-07 Tray 与 EXE 产品图标

- `Assets/tray-icon.ico` 是 TrayApp 的唯一产品图标源，包含 16、20、24、32、48、64、128 与 256px 帧，并使用透明背景。
- 构建必须把同一 ICO 写入 `AteraSnipeSync.TrayApp.exe` 的 Windows application icon，同时把它作为程序集资源提供给运行时 `NotifyIcon`；不得继续显示 `SystemIcons.Application`。
- 运行时只负责从本程序集加载并持有图标，不从当前目录、ProgramData、网络或 operator configuration 读取可替换图标。
- Tray 退出必须在释放 `NotifyIcon` 后释放自有 `Icon`。若构建遗漏嵌入资源，启动应返回明确错误，不得静默显示与 EXE 不一致的系统默认图标。
- 图标替换不改变 single-instance、context menu、Dashboard、Worker IPC、SCM、配置或同步行为。

成功条件：新构建的 EXE、Windows Explorer/快捷方式以及通知区使用同一蓝色“设备资产 + 双向同步”图标；离线自动化验证资源存在、ICO 帧目录完整并可由 `System.Drawing.Icon` 加载，且不调用任何外部服务。

## 2026-07-23 Preview 不占用 Latest run

- `Latest run` 只代表最近一次真实写入模式的 Sync Now 或 scheduled sync；`DryRun = true` 的 Preview 不能替换该卡片的完成时间或四项计数。
- Preview 的 terminal result 仍更新 `Current activity`、进度、结果消息和 manual log，并继续保存独立的本地历史/CSV 审核文件。
- Worker 在线且其 newest status 是 Preview 时，Dashboard 必须从本地历史回退到最近一份有效的非 dry-run sync；Worker 离线时使用同一只读回退规则。
- 如果历史中只有 Preview 或没有有效真实 sync，`Latest run` 保持未运行占位状态，不把 Preview 显示为真实 run。

成功条件：完成 Preview 后，`Latest run` 继续显示 Preview 之前最近一次真实 sync；重启 TrayApp 或 Worker 离线后结果仍一致，且自动化测试只读取临时历史文件。
