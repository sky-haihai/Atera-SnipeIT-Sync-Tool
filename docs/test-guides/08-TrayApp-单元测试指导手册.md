# TrayApp - 单元测试指导手册

## 2026-07 Worker IPC connection regression

`WorkerIpcServerTests` uses random local pipe names to verify a 10-second-configurable first-line deadline, no timeout after a complete command begins, one-slot saturation/release, and continued BuiltinUsers local connectivity. `WorkerCommandHandlerTests.ExecuteAsync_PipelineFailureReportsRunFailureButZeroFailedAssets` verifies the IPC summary keeps run failures separate from asset failures. No real service or API is started.

## 1. 测试目标

TrayApp 自动测试验证正式 Dashboard 架构，不启动真实 WinForms UI、不访问真实 API、不操作真实 Windows Service：

- Dashboard 根据 SCM、Worker、local activity 和 active request 统一决定按钮状态。
- Worker IPC client 验证 version/request id、Accepted → Progress → terminal 顺序与 command payload。
- Cancel 使用独立 pipe connection，并发送准确的 target request id。
- 完整 JSON 配置（含 plaintext credentials、mapping/import、schedule、notifications）一次原子保存。
- Tray 只显示脱敏 result/history，不构造 API client或 local fallback pipeline。
- Dashboard/Configuration 使用 AntdUI 2.4.3 controls；Dashboard 不包含 log TextBox，详细 progress 只进入 daily log。
- service maintenance 只接受两个固定 helper mode，missing Worker EXE 在 UAC/SCM 前失败。
- Restart/Re-register 使用固定 service identity 和离散 `ArgumentList`，stage failure 停止后续命令。
- 已验证的 weighted progress、stable UI milestones 和 lossless daily log 行为在正式命名下继续生效。

## 2. 生产文件

```text
src/AteraSnipeSync.TrayApp/Program.cs
src/AteraSnipeSync.TrayApp/TrayApplicationContext.cs
src/AteraSnipeSync.TrayApp/TraySingleInstanceGuard.cs
src/AteraSnipeSync.TrayApp/TrayDashboardForm.cs
src/AteraSnipeSync.TrayApp/SyncConfigurationPage.cs
src/AteraSnipeSync.TrayApp/DashboardState.cs
src/AteraSnipeSync.TrayApp/DashboardStateReducer.cs
src/AteraSnipeSync.TrayApp/WorkerIpcClient.cs
src/AteraSnipeSync.TrayApp/WorkerIpcOperation.cs
src/AteraSnipeSync.TrayApp/WorkerServiceStatusReader.cs
src/AteraSnipeSync.TrayApp/WorkerServiceMaintenanceLauncher.cs
src/AteraSnipeSync.TrayApp/ElevatedServiceMaintenanceRunner.cs
src/AteraSnipeSync.TrayApp/ServiceCommandRunner.cs
src/AteraSnipeSync.TrayApp/ControlledPathValidator.cs
src/AteraSnipeSync.TrayApp/LatestSyncHistoryReader.cs
src/AteraSnipeSync.TrayApp/SyncProgressCalculator.cs
src/AteraSnipeSync.TrayApp/SyncUiStageTracker.cs
src/AteraSnipeSync.TrayApp/TrayUiTheme.cs
src/AteraSnipeSync.TrayApp/DailyLogWriter.cs
src/AteraSnipeSync.Core/Configuration/SyncAppSettings.cs
src/AteraSnipeSync.Core/Configuration/LocalAppSettingsStore.cs
```

`ManualSyncForm`、`SettingsForm`、`ManualSyncSettings` 已删除。Tray production entry point 不再包含 direct API/Core execution path。

## 3. 测试文件

```text
tests/AteraSnipeSync.Tests/TrayApp/DashboardStateReducerTests.cs
tests/AteraSnipeSync.Tests/TrayApp/WorkerIpcClientTests.cs
tests/AteraSnipeSync.Tests/TrayApp/ServiceMaintenanceTests.cs
tests/AteraSnipeSync.Tests/TrayApp/LatestSyncHistoryReaderTests.cs
tests/AteraSnipeSync.Tests/TrayApp/SyncProgressCalculatorTests.cs
tests/AteraSnipeSync.Tests/TrayApp/SyncLoggingTests.cs
tests/AteraSnipeSync.Tests/Configuration/LocalAppSettingsStoreTests.cs
```

## 4. 运行命令

Tray 专项：

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~TrayApp"
```

配置与 Worker 兼容性：

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~LocalAppSettingsStoreTests|FullyQualifiedName~WorkerService"
```

完整验证：

```powershell
dotnet build AteraSnipeSync.sln --no-restore
dotnet test AteraSnipeSync.sln --no-build --no-restore
git diff --check
```

## 5. Dashboard reducer

`DashboardStateReducerTests` 覆盖：

- Worker Online + Idle：Config、Preview/Sync、Restart enabled，Cancel disabled。
- Saving/Reloading/ServiceMaintenance：run 与 Restart disabled。
- Scheduled/Connection Test/Preview/Sync Now 任一 active：run/Restart disabled。
- Config 在 Worker run/offline 时保留；maintenance 时 disabled。
- NotInstalled：只允许 Config，Restart disabled。
- Stopped：固定文案的 Restart Service enabled，backend 执行 Start。
- SCM Running + IPC Offline：run disabled，Restart enabled。
- Cancel 只在 `RunningTrayCommand + active request id` 时 enabled；Scheduled 永不允许 Cancel。

Reducer 是 pure function。Form event handler 在第一个 await 前写入 LocalActivity；terminal/failure 后必须刷新 SCM + Worker，再通过 reducer恢复控件。

## 6. IPC client

`WorkerIpcClientTests` 使用随机本机 pipe name 和 in-process fake server，验证：

- request 不含 config、URL、credential 或任意 output path。
- Accepted、Progress 与 terminal 顺序正确时转发 progress 并返回 terminal。
- mismatched protocol/request id、malformed/out-of-order event 抛出 `WorkerProtocolException`。
- Busy 映射为 `WorkerBusyException`，Error 映射为脱敏 `WorkerCommandException`。
- EOF before terminal 或未换行 frame 被拒绝。
- Cancel command 通过独立连接携带精确 target request id。

测试只传输 synthetic JSON event；不连接 production Worker、不调用 API。

## 7. 配置保存

`LocalAppSettingsStoreTests` 验证：

- `SyncAppSettings` 从 JSON round-trip Atera/Snipe-IT plaintext credentials。
- API、mapping/import、`Sync.Schedule` 与 `Notifications` 在同一次 atomic mutation 中保存。
- unrelated properties 保留。
- shared cross-process lock 与 temp-file replacement 继续使用。
- blank credential、invalid ignored MAC、empty normalization list、invalid schedule 在写盘前失败。
- Worker `LoadWorkerSyncSettingsAsync` 使用相同完整设置 contract，不读 environment fallback。

Form-level password fields通过 `UseSystemPasswordChar` mask；自动测试不把 secret 写入 assertion failure message、log 或 IPC fixture。

## 8. Service maintenance

`ServiceMaintenanceTests` 只使用 fake status reader、fake command runner 和 fake elevated process delegate：

- helper parser 只接受 `restart` 与 `reinstall-and-restart`，extra argument rejected。
- reinstall Worker EXE missing：elevated launcher call count 必须为 0。
- UAC error 1223 映射为 normal Cancelled result。
- elevated child arguments固定为 `--service-maintenance <operation>`。
- Restart Running：`stop → wait Stopped → start → wait Running`。
- Restart Stopped：只 start。
- Restart NotInstalled：返回需要部署/安装 Service 的提示，不 create。
- Reinstall：`stop → delete → wait absent → create fixed identity → start`。
- create failure 后不 start。
- create arguments必须包含：

```text
AteraSnipeItAutoSync
Atera Snipe-IT Auto Sync
<same-directory>\AteraSnipeSync.WorkerService.exe
start= auto
obj= LocalSystem
```

Automated tests不得执行真实 `sc.exe`、UAC、ServiceController mutation 或 process elevation。

## 9. Progress、logging 与 offline fallback

- `SyncProgressCalculatorTests` 覆盖真实 Sync 35–99% asset execution、Preview 15–95% planning、monotonic out-of-order callbacks 和 terminal 100%。
- `SyncLoggingTests` 验证 3,000 条 detailed records 无丢失，并只向 Dashboard 输出稳定 milestones；人工 UI 检查同时确认不存在 log TextBox。
- `LatestSyncHistoryReaderTests` 在临时目录读取最新 `SyncResult_*.json` 的 count-only structured summary；该 reader 不读取 `run.result` 展示文本、保持 read-only，也不构造 `JsonFileSyncStatusStore` writer。
- Preview/Sync failure 与 cancellation 不生成虚假的 `Completed.` milestone。

## 10. 人工验收

真实 tray icon、in-place Configuration page、UAC 与 Windows Service 只能人工验收：

1. 将 TrayApp publish output 与 `AteraSnipeSync.WorkerService.exe` 放在同一目录。
2. 启动 TrayApp；确认不弹 UAC、不自动打开 Dashboard、只有一个 tray icon。
3. 单击和双击 icon；确认只复用一个 `TrayDashboardForm`。
4. Config 保存完整设置；确认 Dashboard 在 save/reload/final refresh 期间禁用 run/Restart，并显示 Worker 返回的 next-run。
5. 在 `API & Credentials` 运行 combined Test Connections；确认先保存/Reload 当前完整设置，Atera/Snipe-IT 分别显示 bounded result，且 IPC/log 不含 URL 或 credential。
6. 运行 Preview/Sync Now；确认由 Worker 执行，Dashboard 只展示 AntdUI progress、稳定 phase 和 result count cards，Sync Now 默认 No。
7. 点击唯一的 `Open Log Folder`；确认打开 `C:\ProgramData\AteraSnipeSync`，其中 `Logs\ManualSync_yyyyMMdd.log` 包含详细 progress/result；Dashboard 不显示逐设备记录或多行 log text。
8. 运行期间确认 Preview/Sync/Restart disabled，Cancel 只对当前 Tray operation enabled。
9. 关闭 Dashboard；确认只隐藏且 Worker schedule 继续。
10. 确认 Dashboard 不存在 Test Connections、Start Service、Re-register、Open Preflight、Open History 或 Open Logs 按钮。
11. 点击 Restart Service；批准 UAC，确认 Running 时 restart、Stopped 时 start，且按钮文案始终不变；SCM 与 Worker IPC 分别恢复。
12. 确认 tray menu 也只有一个 Open Log Folder，并打开相同 ProgramData root。
13. 取消 UAC；确认 service 未变化。
14. Exit Tray App；确认 Worker Service 不停止且 daily log writer 已 flush。

真实 API key/token 只保存在本机未跟踪 JSON，不得打印、记录或提交。真实 Service 验收只在 operator 指定的本机测试环境执行。
## 2026-07 Notifications tab acceptance

- `WorkerIpcClientTests.Start_TestNotifications_UsesPayloadFreeRequestAndRequiresTerminalResult` 验证 request 不含 SMTP/webhook payload，terminal 必须包含两通道 result。
- 人工检查 Notifications 页 AntdUI SMTP/Email/Webhook fields、password mask、TLS switch、Test Notifications button 和 compact result label。
- 点击测试后确认先保存/reload，按钮互斥禁用，最终显示 `Email: Accepted/Failed/Not configured • Webhook: ...`；UI/daily log 不显示 SMTP password、addresses 或 endpoint URL。

## 2026-07 Teams webhook selector acceptance

- Notifications 页的 AntdUI Webhook format 下拉框默认显示 `Teams Workflow (Adaptive Card)`，另可选择 `Generic JSON`；保存、关闭并重新打开后应恢复所选格式。
- 使用 Teams Flow URL 测试时选择 Teams 格式；测试返回 2xx 后 compact label 显示 `Webhook: Accepted`，总体文案明确 downstream delivery 未确认。
- Email 的同步成功也显示 `Email: Accepted`；最终 inbox/quarantine 状态需查邮件系统 trace。
- `Failed` 与 `Not configured` 保持原语义；manual daily log 只记录 channel 状态，不包含 URL、recipient 或 credential。

## 2026-07 Dashboard minimum-size layout regression

`DashboardLayoutTests.MinimumSize_KeepsActionsVisible_AndSeparatesActivityCard` 在独立 STA thread 中创建真实 `TrayDashboardForm` control tree，但不 show window、不启动 timer、不连接 Worker pipe、不查询 SCM、不触发 UAC，也不打开 Explorer。

测试把 form 设为 `MinimumSize` 并递归执行 layout，然后验证：

- `Sync Now`、`Preview`、`Cancel`、`Restart Service` 的 left/top/right/bottom 均在直接父 TableLayout cell 的 client bounds 内。
- 每个 action button 的实际高度至少 38px，实际宽度不小于 shared styled minimum width。
- 存在独立的 `System status` 与 `Current activity` AntdUI cards；System status 不包含 `Current operation` row。
- 唯一 AntdUI progress control 的最近 AntdUI Panel ancestor 是 title 为 `Current activity` 的 card。
- 右上卡片 title 为 `Schedule`，不存在旧 `Automation` title。
- 四个 action buttons 共享一个没有 title row 的 AntdUI surface，control tree 不包含 `Run and service actions` label。
- Configuration 与 Open Log Folder 的完整 bounds 位于父控件内，实际宽度不小于最小宽度，并保留 icon 与 radius 6。

Focused command：

```powershell
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~DashboardLayoutTests" --logger "console;verbosity=minimal"
```

人工视觉验收仍需在目标服务器 DPI/display scaling 下进行：把 Dashboard 缩到允许的最小尺寸，确认四个 action button 的文字、圆角和上下边缘完整；确认左侧 System status 与 Current activity 上下独立，右侧 Schedule 与 Latest run 没有重叠，Configuration/Open Log Folder 的图标和完整文字可见。

## 2026-07 Last run count-only regression

- `LatestSyncHistoryReaderTests.ReadSummaryAsync_ReturnsNewestSanitizedCounts` 验证离线 history 投影 `FinishedAtUtc`、Created、Updated、NoChange 和 Deleted，即使 history 的 `run.result` 是 `PartialSuccess` 也不向界面返回 outcome 文本。
- `ReadSummaryAsync_DefaultsMissingDeletedCountToZero` 验证旧 history 没有 `assetsDeleted` 时安全显示 0。
- `DashboardLayoutTests` 验证 Latest run card 精确包含 `Created`、`Updated`、`No change`、`Deleted`，且不包含 `Pulled`、`Mapped`、`Failed` metric captions。
- 人工运行时，完整 terminal 即使同时有成功资产计数和 failure，Current activity 也显示 `Sync completed.`/`Completed.`；失败计数仍写入 message/log。只有未完成/fatal result 显示 `Sync failed.`，取消显示 `Sync canceled.`。Latest run card 始终只显示四项计数和完成时间。

## 2026-07 In-place Configuration navigation regression

`DashboardLayoutTests.ConfigurationNavigation_ReusesEmbeddedPage_AndCanOpenAgainAfterCancel` 在一个 STA WinForms message loop 中使用真实 `TrayDashboardForm` control tree，并验证：

- Configuration 是该主窗体下唯一的 `SyncConfigurationPage : UserControl`，不是 `Form`，也没有第二个 top-level window。
- 第一次加载遇到 malformed local JSON 时，错误留在页面内，`Cancel` 仍重新启用。
- Dashboard → Configuration → Cancel → Configuration 可以重复执行；同一个 configuration page instance 被复用，Configuration 按钮不会保留 disabled/open 状态。
- 每次进入都重新读取 local JSON；后一次配置删除 Atera key 后，输入框被重置为空，不残留上一次页面值。
- `Save changes` 成功后原子保存、返回 Dashboard、尝试 Worker schedule reload，并在最终 refresh 后重新启用 Configuration。
- 测试 Worker pipe 使用随机不可用名称和短 timeout；不会启动 Worker、访问 API、查询真实 SCM、发送通知或显示窗口。

Focused command：

```powershell
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-build --filter "FullyQualifiedName~DashboardLayoutTests|FullyQualifiedName~DashboardStateReducerTests" --logger "console;verbosity=minimal"
```

人工验收：在服务器打开 Dashboard，点击 Configuration，确认同一个窗口原地显示四个配置 tabs 和正常的窗口控制按钮；点击 Cancel、再次进入、保存后再次进入，三条路径都不应出现新窗口或失效的 Configuration 按钮。

## 2026-07 Hardcoded Unknown fallback regression

- `DashboardLayoutTests.ConfigurationNavigation_ReusesEmbeddedPage_AndCanOpenAgainAfterCancel` 额外检查 Configuration control tree 不存在名称为 `DefaultCompany`、`DefaultManufacturer`、`DefaultModel` 的 AntdUI input。
- `LocalAppSettingsStoreTests.SaveSyncAppSettingsAsync_SavesCompleteConfig` 检查保存后的 Mapping JSON 不包含三个 legacy properties。
- `LoadSyncAppSettingsAsync_ReturnsSavedCompleteConfig` 检查 reload 后三个 compatibility properties 为共享 constants。
- `LoadSyncAppSettingsAsync_IgnoresLegacyCustomUnknownFallbacks` 使用带自定义旧值的临时 JSON，确认读取时强制规范化为 `Unknown Company`、`Unknown Manufacturer`、`Unknown Model`。
- Default category 仍必须出现在 UI/JSON，因为它不是 Unknown fallback，且被 model category normalization 使用。

所有测试只使用临时配置文件和本地 control tree，不调用真实 API。

## 2026-07 AntdUI Demo visual-alignment regression

`DashboardLayoutTests` 在现有 STA/temp-only control tree 上继续验证：

- Dashboard 唯一主 header 显示 `Auto Sync` / `Dashboard`，control tree 中不存在包含 `→` 的旧 hero label。
- embedded Configuration 不含任何 AntdUI `Checkbox`。
- `CreateMissingCompanies`、`CreateMissingModels`、`ScheduleEnabled`、`RunOnLastDayOfMonth`、`NotificationsEnabled`、`SmtpUseSsl` 六个基础 AntdUI `Switch` 各存在一次，且最小尺寸固定为 56×32。
- 单行 Configuration inputs 不低于 44px、multiline inputs 不低于 96px；Cancel/Save footer buttons 的最小高度不低于 46px。
- Dashboard footer 的 label 精确为 `Detailed activity is available in the log folder.`，且 control tree 不含旧文案 `Detailed activity is written to daily log files.`；Configuration 不含名为 `DryRun` 的 switch 或包含 `Dry run` 的 label。
- 原 minimum-size action bounds、card ownership、Latest run captions 与重复导航断言继续通过。

人工视觉验收：退出当前正在运行的旧 TrayApp 后启动新 build；确认无深色 `Atera → Snipe-IT` hero，顶部为紧凑浅色 header，Configuration 位于右上角并带设置图标；确认所有配置布尔项是加高后的蓝色滑动开关，label 位于开关左侧，setting rows 有清晰纵向间距，Save/Cancel 不再被 footer 压扁，tabs、input、footer 与按钮在实际 DPI 下无重叠。

## 2026-07 Dashboard contrast and compact-density regression

`DashboardLayoutTests.MinimumSize_KeepsActionsVisible_AndSeparatesActivityCard` 继续在 STA/temp-only control tree 上验证：

- Dashboard minimum size 为 920×650，四个 action buttons 在收紧后的 100px action row 内仍完整可见且高度至少 38px。
- System status、Current activity、Schedule、Latest run 和无标题 action surface 都保留至少 6px 的低透明度 shadow、1px border、5px margin；四张标题卡的 padding 为 `14,10,14,12`。
- action surface padding 为 `14,18,14,18`，不会恢复原先过大的 action 空白区。
- Preview、Restart Service 和 Open Log Folder 都有显式 1px secondary border；Sync Now 的 Primary 与 Cancel 的 Danger/disabled 语义保持原样。
- Latest run metric tiles 继续显示四项 count，并由生产代码使用更紧凑的 gutter、top spacing 和 1px border。

Focused command：

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore -m:1 -nr:false -p:OutDir=<isolated-temp-output> --filter "FullyQualifiedName~DashboardLayoutTests" --logger "console;verbosity=minimal"
```

人工视觉验收：关闭旧 TrayApp，启动新 build，确认浅灰画布能衬出白色卡片，阴影柔和不发黑；Preview、Restart Service、Open Log Folder 边界清楚；外边距、卡片间距与 action 区明显比旧版紧凑，同时文字、图标和按钮不裁切。

## 2026-07 Schedule frequency visibility regression

`DashboardLayoutTests.ConfigurationNavigation_ReusesEmbeddedPage_AndCanOpenAgainAfterCancel` 选择真实 Schedule tab 和 `ScheduleFrequency` AntdUI Select，然后验证：

- Daily：Weekly days、Monthly days、Also run on the last day of month 的 label/value rows 全部隐藏。
- Weekly：只显示 Weekly days；Monthly days 与 last-day switch 隐藏。
- Monthly：隐藏 Weekly days，显示 Monthly days 与 last-day switch。
- Windows time zone ID 在三种 frequency 下始终显示。
- 测试读取 control 自身的 WinForms Visible state，不需要显示 top-level window；仍不启动 Worker、访问 API 或修改真实配置。

时区人工检查：删除/不提供本地 Schedule 配置后重新进入 Configuration，Windows time zone ID 应显示当前机器的 `TimeZoneInfo.Local.Id`；保存一个不同的有效 Windows time-zone ID 后再次进入，应显示保存值而不是强制覆盖为当前系统时区。

## 2026-07 Notification outcome switch regression

`DashboardLayoutTests.ConfigurationNavigation_ReusesEmbeddedPage_AndCanOpenAgainAfterCancel` 在现有 STA/temp-only Configuration control tree 上验证：

- 原 `NotificationEvents` AntdUI text input 已不存在。
- `NotifySyncCompleted` 与 `NotifySyncFailed` 两个 AntdUI switches 各存在一次并满足共享 56×32 minimum size；六个按 scheduled/manual/preview 拆分的旧 switches 不再存在。
- `ApplyNotificationEvents` 对 persisted event name 进行 trim 和 case-insensitive matching；任一旧 completed/failed event 开启对应 outcome switch，未知 legacy value 不开启其它 switch。
- `ReadSelectedNotificationEvents` 把 Completed 展开为四个 completed canonical events，把 Failed 展开为四个 failed canonical events；两者关闭时返回空列表。
- 测试只构造本机 control tree，不执行 Save/Test button，不连接 Worker、SMTP、Teams Flow 或其它 webhook endpoint。

Focused command：

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore -m:1 -nr:false -p:OutDir=<isolated-temp-output> --filter "FullyQualifiedName~DashboardLayoutTests" --logger "console;verbosity=minimal"
```

人工验收：启动 rebuilt TrayApp，打开 Configuration → Notifications，确认总开关下方只显示 `Sync completed` 和 `Sync failed` 两个 outcome switches。分别选择并保存，重新进入后应恢复相同选择；Completed 包含 scheduled/manual/preview/other completed，Failed 同理。`Test Notifications` 仍应在两个 switches 全关时发送一次显式测试。

## 2026-07 Configuration Cancel 文案回归

`DashboardLayoutTests` 使用现有 STA/temp-only control tree 验证 Configuration footer 中恰有一个 `Cancel` 按钮且不存在 `Back to Dashboard` 按钮。测试点击真实 `Cancel` 后确认返回 Dashboard、Configuration 可再次进入；该路径不执行配置保存、schedule reload、Worker IPC 或外部 API 调用。

Dashboard 自身也包含一个用于取消活动同步的 `Cancel`。minimum-size 布局断言因此先以唯一的 `Sync Now` 按钮定位 Dashboard action surface，再仅在该 surface 内检查 Sync Now、Preview、Cancel 和 Restart Service，避免与隐藏的 Configuration footer 按钮混淆。

验证命令：

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore -m:1 -nr:false -p:OutDir=<isolated-temp-output> --filter "FullyQualifiedName~DashboardLayoutTests" --logger "console;verbosity=minimal"
```

人工验收：打开 Configuration，确认左下角显示 `Cancel`；修改任意字段但不保存，点击 Cancel 后重新进入 Configuration，确认未保存的修改没有被保留。

## 2026-07 Scheduled Dry-Run 控件删除回归

`DashboardLayoutTests.ConfigurationNavigation_ReusesEmbeddedPage_AndCanOpenAgainAfterCancel` 从期望 switch catalog 删除 `DryRun`，并显式拒绝名为 `DryRun` 的 AntdUI switch 和包含 `Dry run` 的 Configuration label。其余六个基础 switches 与两个 notification outcome switches 仍保持唯一名称和 56×32 minimum size。

`LocalAppSettingsStoreTests.SaveSyncAppSettingsAsync_SavesCompleteConfig` 验证新配置不写 `Sync.DryRun`；`SaveSyncAppSettingsAsync_PreservesUneditedProperties` 从一个含旧 `DryRun = true` 的临时 JSON 开始，验证原子保存会删除旧字段，同时保留 `IntervalMinutes` 和无关 notification property。所有文件都位于测试临时目录。

人工验收：打开 Configuration → Mapping & Import，确认页面不再显示 Dry Run 开关；保存一次配置后，在本机未跟踪的 `appsettings.local.json` 中确认 `Sync.DryRun` 已被移除。不要打印或复制 API credentials。

## 2026-07 Windows 11 / Server 2022 视觉一致性回归

`DashboardLayoutTests.MinimumSize_KeepsActionsVisible_AndSeparatesActivityCard` 在既有 STA、临时配置/日志和 fake service reader 环境中新增以下验证，不显示真实窗口：

- `TrayDashboardForm` 的直接基类是 `AntdUI.BorderlessForm`，`UseDwm` 为 false，窗口 radius/shadow/border/color 与 `TrayUiTheme` token 一致。
- System status、Current activity、Schedule、Latest run 和 titleless action surface 使用 10px surface radius；四个 metric tiles 使用 8px nested radius；Dashboard buttons 使用 6px control radius。
- 在 920×650 minimum size 和 1560×1187 expanded size 各执行两次递归 layout；四个 run actions 均保持 `Dock=None`、`Anchor=None`、相同的 `Size/MinimumSize/MaximumSize`，位于所属 25% table cell 中心且没有裁切。
- expanded layout 必须与 minimum layout 的已观测按钮尺寸相同。测试比较运行时 DPI 缩放后的尺寸，不假定测试主机一定是 96 DPI。

聚焦命令：

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~DashboardLayoutTests" --logger "console;verbosity=normal"
```

### 手工跨系统检查

1. 使用同一发布产物分别启动 Windows 11 和 Windows Server 2022 TrayApp；先关闭旧 TrayApp 进程，避免观察到 stale executable。
2. 在普通窗口状态确认外框四角、四张主卡片、action surface、metric tiles 和 buttons 具有清晰的分层圆角；最大化后外框应为直角，恢复后重新出现圆角。
3. 分别在 100% 和 150% display/RDP scale 检查默认尺寸与扩大窗口：文字、图标应清晰且不裁切；四个 run actions 只按 DPI 比例缩放，不随窗口列宽变宽。
4. 检查最小化、最大化、恢复、拖动、四边缩放、右上角关闭隐藏、从 tray 重开 Dashboard，以及 Configuration/Cancel 导航。
5. 这些检查不要求执行 Preview/Sync、连接测试或通知测试，也不需要真实 Atera、Snipe-IT、SMTP 或 webhook credentials。

### 常见失败判断

- Server 2022 普通窗口仍为直角：优先确认运行的是新构建，并检查 Dashboard 是否仍继承 `AntdUI.Window` 或 `UseDwm` 是否被重新启用。
- 蓝色 Sync Now 背景占满整列：检查 action button 是否重新设置了 `DockStyle.Fill`，或 fixed size 是否被移除。
- 150% 截图物理像素比 100% 大：这是 PerMonitorV2 的预期；只有逻辑比例、密度和清晰度需要一致。
- 字体清晰但控件重叠：检查 `ApplicationHighDpiMode` 是否为 `PerMonitorV2`、window 是否保持 `AutoScaleMode.Dpi`，以及发布目录是否混入旧 DLL。

## 2026-07 Tray 与 EXE 产品图标回归

`TrayIconLoaderTests` 完全离线验证同一 `Assets/tray-icon.ico` 的运行时契约：

- TrayApp manifest 只包含逻辑名 `AteraSnipeSync.TrayApp.Assets.tray-icon.ico` 的一个产品图标资源。
- ICO directory header 的 reserved/type/count 分别为 0/1/8；八个方形帧依次为 16、20、24、32、48、64、128、256px，其中 ICO 的 0 尺寸字节按 256px 解释。
- `TrayIconLoader.Load()` 返回可释放的方形图标，左上角透明且至少包含一个高不透明蓝色像素；测试不显示 `NotifyIcon` 或 Dashboard。

聚焦命令：

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore -m:1 -nr:false -p:OutDir=<isolated-temp-output> --filter "FullyQualifiedName~TrayIconLoaderTests" --logger "console;verbosity=minimal"
```

EXE 图标由 MSBuild `ApplicationIcon` 生成，不能只靠测试程序集中的托管资源断言。构建后使用 Explorer 查看新产物，或在本机 PowerShell 通过 `[System.Drawing.Icon]::ExtractAssociatedIcon(<AteraSnipeSync.TrayApp.exe>)` 提取核对；通知区应使用同一资源。分别在浅色与深色任务栏检查 16/20/24/32px，不应再出现通用 Windows application icon。该人工检查不需要启动 Worker、操作 SCM、执行同步或配置真实 credentials。

常见失败判断：

- EXE 仍显示旧图标但提取结果正确：先关闭旧 TrayApp，并刷新 Explorer icon cache 或改用新 publish 目录确认，避免缓存误判。
- EXE 正确但通知区仍是默认图标：检查 `TrayApplicationContext` 是否重新使用 `SystemIcons.Application`，以及运行的 EXE 是否为最新 build。
- 启动提示 bundled icon resource missing/invalid：检查 `.csproj` 的 `EmbeddedResource` logical name 是否与 `TrayIconLoader.ResourceName` 完全一致，并确认 ICO 未损坏。
