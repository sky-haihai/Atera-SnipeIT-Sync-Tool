# Tray App - 技术规格

## 1. 目标与实现状态

本规格定义把已验证的临时 `ManualSyncForm` 迁移为正式 Windows TrayApp 的具体实现，基于 `docs/module-plans/08-TrayApp-功能职责.md` 和 Worker technical spec。

目标实现必须：

- 使用 `TrayDashboardForm` + embedded `SyncConfigurationPage`，不保留正式 `ManualSyncForm` entry point。
- 所有 API/Core execution 通过 Worker IPC。
- 保存 JSON 后发送 `ReloadSchedule`。
- 显示 SCM status 与 Worker IPC/run status，并用统一 state reducer控制按钮。
- Dashboard 只提供固定文案的 Restart Service，点击时才 UAC elevation；不暴露 Start 或 Re-register 按钮。
- 固定操作 `AteraSnipeItAutoSync`，固定使用 Tray 同目录 Worker EXE。

本阶段不得改变 Atera/Snipe-IT wire contract。Automated tests 不启动 WinForms UI、不调用真实 API、不注册/删除/启停真实 Service。

## 2. Target project structure

```text
src/AteraSnipeSync.TrayApp/
  Program.cs
  TrayApplicationContext.cs
  TraySingleInstanceGuard.cs
  TrayDashboardForm.cs
  SyncConfigurationPage.cs
  DashboardState.cs
  DashboardStateReducer.cs
  WorkerIpcClient.cs
  WorkerIpcOperation.cs
  WorkerServiceStatusReader.cs
  WorkerServiceMaintenanceLauncher.cs
  ElevatedServiceMaintenanceRunner.cs
  ServiceCommandRunner.cs
  SyncProgressCalculator.cs
  SyncUiStageTracker.cs
  TrayUiTheme.cs
  TrayStatusFormatter.cs
  ControlledPathValidator.cs
  DailyLogWriter.cs
```

Core configuration target names：

```text
src/AteraSnipeSync.Core/Configuration/SyncAppSettings.cs
src/AteraSnipeSync.Core/Configuration/LocalAppSettingsStore.cs
src/AteraSnipeSync.Core/Runtime/Windows/WorkerServiceIdentity.cs
```

Migration：

- `ManualSyncForm` → `TrayDashboardForm`。
- `ManualSyncSettings` → `SyncAppSettings`。
- `ManualSyncProgressCalculator` → `SyncProgressCalculator`。
- `ManualSyncUiStageTracker` → `SyncUiStageTracker`。
- legacy `SettingsForm` 的仍有效 fields 合并到 `SyncConfigurationPage` 后删除或从 production entry point 移除。
- tests、linked Compile items 和 test guide references 在 code implementation phase 同步重命名。

每个 class 必须有 role/responsibility boundary comment；key method 必须说明 side effects、validation、cancellation 和 failure behavior。

## 3. Process ownership

TrayApp production process 可以：

- load/save `SyncAppSettings`。
- connect local named pipe。
- render sanitized Worker events。
- query Windows Service Control Manager read-only status。
- launch its own restricted elevated helper mode。
- open controlled ProgramData directories。

TrayApp production process 不得：

- construct Atera/Snipe-IT client、mapper、importer、orchestrator或 status writer。
- create Authorization header或发送 external HTTP。
- execute a local fallback run。
- accept arbitrary service name、display name、executable path或 shell command。

## 4. Program modes and single instance

### 4.1 `Program`

```csharp
[STAThread]
internal static class Program
{
    private static int Main(string[] args);
}
```

Startup order：

```text
parse exact service-maintenance args
  ├─ helper mode -> ElevatedServiceMaintenanceRunner.RunAsync -> exit code
  └─ normal mode -> TraySingleInstanceGuard.TryAcquire
                    -> ApplicationConfiguration.Initialize
                    -> construct dependencies
                    -> Application.Run(TrayApplicationContext)
```

Helper mode 必须在 mutex 前解析，否则已运行的 Tray 会阻止 elevated child。Valid helper args only：

```text
--service-maintenance restart
--service-maintenance reinstall-and-restart
```

任何额外或 unknown maintenance argument 返回 invalid-arguments exit code，不启动 Tray UI。

### 4.2 `TraySingleInstanceGuard`

```csharp
internal sealed class TraySingleInstanceGuard : IDisposable
{
    public static bool TryAcquire(out TraySingleInstanceGuard? guard);
    public void Dispose();
}
```

- mutex name `Local\AteraSnipeSync.TrayApp`。
- second normal instance 显示简短 message后退出。
- abandoned mutex 按 acquired 处理。
- helper mode 不使用该 mutex。

## 5. Shared Windows Service identity

TrayApp 引用 Core 中的唯一 constants：

```csharp
public static class WorkerServiceIdentity
{
    public const string ServiceName = "AteraSnipeItAutoSync";
    public const string DisplayName = "Atera Snipe-IT Auto Sync";
    public const string ExecutableFileName =
        "AteraSnipeSync.WorkerService.exe";
}
```

Worker executable path only：

```csharp
Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory,
    WorkerServiceIdentity.ExecutableFileName))
```

Rules：

- resolved path 必须直接位于 `AppContext.BaseDirectory`，file name ordinal-ignore-case exact match。
- no config/UI/CLI override。
- reinstall parent和 elevated helper 都重新验证 `File.Exists`。
- missing Worker EXE 必须在 UAC、Stop、Delete 前失败。

## 6. `TrayApplicationContext`

```csharp
internal sealed class TrayApplicationContext : ApplicationContext
{
    public TrayApplicationContext(
        WorkerIpcClient ipcClient,
        LocalAppSettingsStore settingsStore,
        WorkerServiceStatusReader serviceStatusReader,
        WorkerServiceMaintenanceLauncher maintenanceLauncher);
}
```

Private state：

```csharp
private readonly NotifyIcon _notifyIcon;
private TrayDashboardForm? _dashboard;
private bool _isExiting;
```

Responsibilities：

- create NotifyIcon and context menu：Open Dashboard、View Last Sync Status、Open Log Folder、Exit Tray App。Open Log Folder 固定打开 `ControlledPathValidator.ProgramDataRoot`（`C:\ProgramData\AteraSnipeSync`）。
- left single-click and double-click both call idempotent `ShowDashboard()`；right-click only opens menu。
- only one live Dashboard；disposed instance才重建。
- Dashboard normal close: cancel close event + Hide。
- Exit Tray: set `_isExiting`、allow actual close/dispose、dispose NotifyIcon、exit message loop。
- never stop Worker Service on Tray exit。

`ShowDashboard()` calls `RefreshSystemStateAsync` after showing。Tooltip contains app name + short SCM/Worker state and respects Windows length limit。

## 7. Dashboard state contracts

```csharp
internal enum WorkerWindowsServiceState
{
    Unknown,
    NotInstalled,
    Stopped,
    StartPending,
    Running,
    StopPending
}

internal enum DashboardLocalActivity
{
    Idle,
    ReloadingSchedule,
    RunningTrayCommand,
    ServiceMaintenance
}

internal sealed record DashboardState(
    WorkerWindowsServiceState ServiceState,
    bool WorkerOnline,
    bool ProtocolCompatible,
    bool WorkerBusy,
    string? ActiveOperation,
    DashboardLocalActivity LocalActivity,
    bool ActiveCommandCanCancel);

internal sealed record DashboardControlState(
    bool ConfigEnabled,
    bool RunButtonsEnabled,
    bool RestartEnabled,
    bool CancelEnabled);

internal static class DashboardStateReducer
{
    public static DashboardControlState Reduce(DashboardState state);
}
```

Reducer rules：

- Run buttons require Service Running + WorkerOnline + ProtocolCompatible + !WorkerBusy + LocalActivity Idle。
- Saving/Reloading/ServiceMaintenance disable run/restart；config save cannot reenter。
- any WorkerBusy disables Preview/Sync and Restart Service。
- Config remains available during Worker run/offline, but unavailable during local service maintenance；the single embedded page prevents duplicate configuration UI。
- Restart enabled when service Running/Stopped or SCM Running but IPC Offline，unless busy/local activity。
- Restart disabled when NotInstalled。
- Cancel enabled only for `RunningTrayCommand` with active cancellable request；never for Scheduled。

All event handlers set LocalActivity before first await。On terminal/failure, they do not directly enable buttons；they call `RefreshSystemStateAsync` and run reducer from fresh SCM/Worker state。

## 8. `TrayDashboardForm`

```csharp
public sealed class TrayDashboardForm : AntdUI.Window
{
    public TrayDashboardForm();

    internal TrayDashboardForm(
        LocalAppSettingsStore settingsStore,
        WorkerIpcClient ipcClient,
        WorkerServiceStatusReader serviceStatusReader,
        WorkerServiceMaintenanceLauncher maintenanceLauncher,
        DailyLogWriter manualLogWriter);

    public Task RefreshSystemStateAsync(
        CancellationToken cancellationToken = default);
}
```

Required UI fields：

- service status label。
- Worker online/protocol label。
- active operation label。
- schedule status + next-run local time label。
- Config、Preview、Sync Now、Cancel。
- one fixed-label Restart Service button；Stopped uses the same label and starts the service。
- AntdUI progress、phase label、latest result summary card、six count metrics and one Open Log Folder button targeting `ControlledPathValidator.ProgramDataRoot`。
- no multiline log/result `TextBox`；per-record progress is file-only。

Required handlers：

```csharp
private Task OpenConfigurationAsync();
private Task ReloadScheduleAsync();
private Task RunSyncCommandAsync(bool previewOnly);
private Task CancelActiveCommandAsync();
private Task RestartServiceAsync();
private void ApplyControlState();
```

Form shown: immediate refresh then start 2-second WinForms status timer while Visible。Hide stops timer。Timer callback uses an interlocked refresh guard，不能重入。Manual command progress and polling update UI only on UI synchronization context。

Visual contract：

- NuGet package `AntdUI` `2.4.3`，target asset `net10.0-windows7.0`。
- `Program` selects `TMode.Light` and the shared Segoe UI application font before creating `TrayApplicationContext`。
- Dashboard uses `AntdUI.Window` + `PageHeader`，AntdUI `Panel` cards，typed `Button` actions，`Label` status/metrics and `Progress` (`Value` normalized to `0F..1F`)。
- `TrayUiTheme` owns canvas/surface/status colors and AntdUI card/button styling；layout-only `TableLayoutPanel`/`FlowLayoutPanel` may remain WinForms containers。
- status colors are derived from current SCM/IPC/schedule state；disabled state remains controlled only by `DashboardStateReducer`。

## 9. `SyncConfigurationPage` and settings store

### 9.1 Settings contract

```csharp
namespace AteraSnipeSync.Core.Configuration;

public sealed class SyncAppSettings
{
    // Existing API, credential, mapping/import and schedule properties.
}
```

`SyncAppSettings` replaces `ManualSyncSettings` across Core/Worker/Tray/tests。It contains Atera API key and Snipe-IT token because plaintext JSON is the accepted temporary storage model。

### 9.2 Store methods

```csharp
public Task<SyncAppSettings?> LoadSyncAppSettingsAsync(
    CancellationToken cancellationToken);

public Task<SyncAppSettings?> LoadWorkerSyncSettingsAsync(
    CancellationToken cancellationToken);

public Task SaveSyncAppSettingsAsync(
    SyncAppSettings settings,
    CancellationToken cancellationToken);
```

- both load methods populate credentials from `Atera.ApiKey`/`SnipeIt.ApiToken`。
- no environment secret precedence/fallback。
- Save validates complete contract、uses existing cross-process lock + atomic replacement、preserves unrelated sections、round-trips credentials。
- no method removes plaintext credential properties。
- messages/exceptions name invalid fields but do not include values。

### 9.3 Page contract

```csharp
internal sealed class SyncConfigurationPage : UserControl
{
    internal SyncConfigurationPage(
        LocalAppSettingsStore settingsStore,
        WorkerIpcClient ipcClient,
        DailyLogWriter manualLogWriter);

    internal event EventHandler? DashboardRequested;
    internal event EventHandler? SettingsSaved;
    internal Task ActivateAsync();
}
```

Fields use AntdUI `Tabs`/`TabPage`、`Input`、masked `Input`、`Select`、`Checkbox`、`Button` and `Label` for API URLs、credentials、all mapping/import settings、Daily/Weekly/Monthly schedule。`API & Credentials` contains `Test Connections` and a bounded result label。Every activation resets defaults and reads JSON。Save runs complete validation, awaits `SaveSyncAppSettingsAsync`, then raises `SettingsSaved`。Failure keeps the embedded page active。

Connection-test flow：

```text
click Test Connections
  -> validate and atomically save the complete current form settings
  -> WorkerIpcClient.ExecuteAsync(ReloadSchedule)
  -> WorkerIpcClient.Start(TestConnections, file-only progress)
  -> show bounded Atera/Snipe-IT success/failure in API & Credentials
  -> write detailed progress/result to ManualSync_yyyyMMdd.log
```

The IPC request contains only the command name；URLs and credentials are loaded by Worker from the shared JSON and never enter IPC/log text。Test failure keeps the configuration page active。

Dashboard flow：

```text
show embedded SyncConfigurationPage
  -> SettingsSaved event
  -> restore Dashboard page
  -> set ReloadingSchedule
  -> WorkerIpcClient.ExecuteAsync(ReloadSchedule)
  -> display applied/next-run or saved-but-not-reloaded warning
  -> RefreshSystemStateAsync
```

JSON save success is not rolled back when reload connection/command fails。

## 10. IPC client

```csharp
internal sealed class WorkerIpcOperation
{
    public required string RequestId { get; init; }
    public required Task<WorkerIpcEvent> Completion { get; init; }
}

internal sealed class WorkerIpcClient
{
    public WorkerIpcOperation Start(
        string command,
        IProgress<SyncProgressUpdate>? progress,
        CancellationToken cancellationToken);

    public Task<WorkerIpcEvent> ExecuteAsync(
        string command,
        CancellationToken cancellationToken);

    public Task<bool> CancelAsync(
        string targetRequestId,
        CancellationToken cancellationToken);
}
```

Default pipe and connect timeout：`WorkerIpcProtocol.DefaultPipeName`、5 seconds。

Validation：

- command from allow-list。
- response version/request id match。
- Accepted at most once and before progress/terminal。
- exactly one terminal event；EOF before terminal throws protocol exception。
- payload matches command/event type。
- message <= protocol maximum。
- no serialized frame logged。

Exceptions：WorkerUnavailableException、WorkerProtocolException、WorkerBusyException(active operation)、WorkerCommandException。All expose sanitized messages only。

`ReloadSchedule` uses `ExecuteAsync` and expects a non-null `ScheduleReload` result。Test/Preview/Sync use `Start` so request id exists before connect/read and Cancel always targets exact operation。

## 11. Worker status refresh

`RefreshSystemStateAsync` performs independently：

1. `WorkerServiceStatusReader.GetStatusAsync` for SCM state。
2. Worker `GetStatus` for IPC online、protocol、busy/active operation、schedule validity/next-run/latest result。
3. If Worker unavailable, retain SCM state and read latest history fallback read-only。
4. Apply one combined DashboardState through reducer。

Stale refresh protection：each refresh receives monotonically increasing generation；only latest generation may commit state。A slow timer response不能覆盖 a newer command terminal refresh。

Formatter：

```csharp
internal static class TrayStatusFormatter
{
    public static string FormatWorkerStatus(
        WorkerStatusSnapshot? status,
        bool workerOnline);

    public static string FormatNextRun(
        DateTimeOffset? nextRunUtc,
        TimeZoneInfo localTimeZone);
}
```

## 12. Combined Connection Test

`TestConnectionsAsync`：

- require reducer RunButtonsEnabled。
- set LocalActivity RunningTrayCommand before await。
- call `Start(WorkerIpcCommands.TestConnections, progress, token)` and store active operation。
- render separate Atera and Snipe-IT result rows。
- one/both endpoint `Succeeded = false` is a completed business result, not IPC failure。
- Busy/offline/protocol/cancel handled separately。
- finally clear local operation then refresh system state；never directly re-enable buttons。

No Tray code creates API header或 parses API response body。

## 13. Preview, Sync Now and Cancel

Preview：send `PreviewChanges`; require SyncResult and validate returned paths under controlled roots before enabling links。

Sync Now：show confirmation with default No, then send `SyncNow`; request contains no Preview snapshot/path/config payload。

Cancel：

- separate connection calls `CancelAsync(active.RequestId, finite timeout)`。
- acknowledgement displays `Cancellation requested.` but operation remains active until terminal/disconnect。
- no acknowledgement displays `Could not confirm cancellation.`。
- Dashboard close/Tray exit does not send Cancel automatically。

`SyncProgressCalculator` and `SyncUiStageTracker` retain current weighted, monotonic semantics under new names。Only structured successful terminal invokes stage completion；transport/busy/cancel不能 fabricate Completed milestone。

## 14. Windows Service status reader

```csharp
internal sealed class WorkerServiceStatusReader
{
    public Task<WorkerWindowsServiceState> GetStatusAsync(
        CancellationToken cancellationToken);
}
```

- queries only `WorkerServiceIdentity.ServiceName`。
- service-not-found maps NotInstalled。
- maps ServiceController statuses to Dashboard enum；unhandled/transient error maps Unknown and logs sanitized warning。
- read-only query does not require elevation；never starts/stops service。

For testability，SCM access is behind an internal adapter injected into the reader。

## 15. Service maintenance public contracts

```csharp
internal enum ServiceMaintenanceOperation
{
    Restart,
    ReinstallAndRestart
}

internal sealed class ServiceMaintenanceResult
{
    public required bool Succeeded { get; init; }
    public required bool Cancelled { get; init; }
    public required string Stage { get; init; }
    public required string Message { get; init; }
}

internal sealed class WorkerServiceMaintenanceLauncher
{
    public Task<ServiceMaintenanceResult> ExecuteElevatedAsync(
        ServiceMaintenanceOperation operation,
        CancellationToken cancellationToken);
}
```

Launcher：

1. resolve current Tray executable and fixed same-directory Worker executable。
2. for Reinstall operation preflight `File.Exists(workerExe)` before UAC。
3. construct `ProcessStartInfo` for current Tray executable with one exact helper arg、`Verb = "runas"`、`UseShellExecute = true`。
4. await child exit asynchronously；no stdout/stderr redirect with runas。
5. map fixed exit code to result；details live in maintenance log。
6. catch `Win32Exception.NativeErrorCode == 1223` as `Cancelled = true`。

Cancellation after child start stops local wait only；it must not kill elevated maintenance midway。Dashboard remains ServiceMaintenance until a background completion observer sees child exit or a fresh SCM status proves stable。

## 16. Elevated helper and command runner

```csharp
internal static class ServiceMaintenanceExitCodes
{
    public const int Success = 0;
    public const int InvalidArguments = 10;
    public const int NotElevated = 11;
    public const int WorkerExecutableMissing = 12;
    public const int ServiceNotInstalled = 13;
    public const int StopFailed = 14;
    public const int DeleteFailed = 15;
    public const int CreateFailed = 16;
    public const int StartFailed = 17;
    public const int Timeout = 18;
    public const int UnexpectedFailure = 19;
}

internal sealed class ElevatedServiceMaintenanceRunner
{
    public Task<int> RunAsync(
        ServiceMaintenanceOperation operation,
        CancellationToken cancellationToken);
}
```

Runner verifies Administrator role before any SCM mutation。It re-resolves fixed Worker path and logs operation/stage/timestamps only。

System command path：

```csharp
Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.System),
    "sc.exe")
```

All arguments use `ProcessStartInfo.ArgumentList`; no shell、PowerShell、cmd.exe or concatenated argument string。

```csharp
internal sealed class ServiceCommandRunner
{
    public Task<ServiceCommandResult> ExecuteAsync(
        string verb,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
```

Only internal fixed calls may invoke it。Logs may include fixed service/display name and executable path but never config content、credentials、command stdout containing sensitive environment or stack traces。

## 17. Restart behavior

Restart sequence for `AteraSnipeItAutoSync`：

- NotInstalled → `ServiceNotInstalled` exit；no create。
- Stopped → Start only。
- Running → `sc.exe stop AteraSnipeItAutoSync` → wait Stopped → Start。
- Start/Stop Pending → wait up to 30 seconds for stable state, then continue appropriate branch。
- other state → failure。

Start command：`sc.exe start AteraSnipeItAutoSync`。Stop/start wait each has 30-second deadline using SCM status polling, not fixed blocking sleep。

Parent receives Success后每 second calls Ping/GetStatus up to 15 seconds。SCM Running but IPC unavailable produces warning，不改写 success为 Worker Online。

## 18. Internal re-register helper behavior

This fixed helper remains for backward-compatible controlled deployment/iteration, but no Dashboard or tray menu item invokes it。Exact sequence：

1. validate fixed Worker EXE exists。
2. query fixed service name。
3. if service Running/Pending, stop/wait Stopped。
4. if service exists, run `sc.exe delete AteraSnipeItAutoSync` and wait NotInstalled up to 30 seconds。
5. create service with fixed values：

```text
sc.exe create AteraSnipeItAutoSync
  binPath= "<absolute same-directory Worker EXE>"
  start= auto
  obj= LocalSystem
  DisplayName= "Atera Snipe-IT Auto Sync"
```

Arguments are separate `ArgumentList` entries; the above is descriptive, not a concatenated command。

6. query SCM and require Stopped/registered。
7. start and wait Running up to 30 seconds。

Failure policy：

- Worker EXE missing: no UAC/Stop/Delete。
- Stop/Delete failure: do not Create。
- Create failure: stop; service may be NotInstalled。
- Start failure: leave newly registered service Stopped for log inspection/retry。
- every exit path flushes `ServiceMaintenance_yyyyMMdd.log` and returns stage-specific exit code。

## 19. Dashboard Service handlers

```csharp
private async Task RestartServiceAsync()
```

Flow：

- require reducer allows Restart Service。
- set ServiceMaintenance and apply state before calling launcher。
- do not show extra confirmation; UAC is the required operator confirmation。
- display cancelled/success/stage failure message。
- on success, poll IPC up to 15 seconds as specified。
- finally query fresh SCM + Worker status and run reducer。
- never infer Running/Online only from child exit code。

While ServiceMaintenance：Config、Preview、Sync、Restart、Cancel all disabled。Form close may hide; operation observer continues and state remains maintenance until completion refresh。

## 20. Logging, paths and sanitization

- IPC raw JSON never logged。
- UI/file log never includes credential、Authorization、raw API body或 stack trace。
- `TrayApplicationContext` owns one `DailyLogWriter(LogsRoot)` and injects it into the reusable Dashboard；`ExitThreadCore` flushes it after disposing the notify icon。
- each Tray-started command writes start、every detailed progress callback、connection endpoint result、terminal summary and structured failures to `ManualSync_yyyyMMdd.log`；connection test starts from Configuration while Preview/Sync start from Dashboard。
- Dashboard renders only stable phase/status text and count summary cards；it never renders detailed progress or failure groups as a log text area。
- service maintenance logs write fixed identity、stage、exit code、sanitized system error and timestamps。
- the single `Open Log Folder` action opens `ControlledPathValidator.ProgramDataRoot` before `Process.Start(UseShellExecute = true)`；no separate Preflight/History/Logs buttons are exposed。
- service executable validation permits only exact file under AppContext.BaseDirectory。
- no automatic retry of Sync/Preview/Test after timeout/busy/error。

## 21. Automated tests

### State reducer/UI orchestration

- Online+Idle enables run/restart。
- Reloading disables run + restart。
- each Scheduled/Test/Preview/Sync busy state disables run + restart。
- Config remains enabled during run but disabled during maintenance。
- NotInstalled disables restart；Stopped enables fixed-label restart。
- Running+IPC Offline disables run but enables restart。
- local state is set before async call；terminal performs refresh before enable。
- stale refresh generation cannot overwrite newer state。

### Config/reload

- plaintext credentials round-trip and password fields populate without logging values。
- atomic save preserves unrelated sections。
- successful embedded-page save sends exactly one ReloadSchedule。
- save failure sends none。
- reload failure keeps JSON and shows saved-but-not-applied。
- run buttons stay disabled from save start through final status refresh。

### IPC/run UI

- combined TestConnections renders two results。
- Preview/Sync path validation and result rendering。
- busy/offline/protocol mismatch/cancel behavior。
- raw request/event contains no config or credential fields。
- renamed progress/stage helpers retain existing tests。

### Service maintenance

- constants equal `AteraSnipeItAutoSync` / `Atera Snipe-IT Auto Sync` / expected EXE。
- helper mode parsed before single-instance guard and never creates UI。
- reinstall missing Worker EXE returns before UAC/SCM calls。
- Restart Running: stop→wait→start；Stopped: start only；Missing: no create。
- Reinstall: stop→delete→wait absent→create fixed identity→start。
- args use discrete ArgumentList and reject arbitrary inputs。
- UAC cancellation maps Cancelled and makes no SCM mutation。
- stage failure stops later commands and maps exact exit code。
- completion re-queries SCM/IPC。

Tests use fake process launcher、fake SCM adapter、fake TimeProvider、temp files and in-process pipe。No test invokes real `sc.exe`、UAC、Explorer、Windows Service或 external API。

## 22. Manual acceptance

1. Publish TrayApp and `AteraSnipeSync.WorkerService.exe` into the same folder。
2. Start Tray normally; verify no UAC and only one tray icon。
3. single-click icon; verify one TrayDashboardForm and separate SCM/IPC states。
4. open Config, edit full settings/schedule, save；verify buttons disabled through ReloadSchedule and next-run refresh。
5. on `API & Credentials`, run combined Connection Test；verify current complete form settings are saved/reloaded first and two bounded endpoint results are shown without exposing secrets。
6. run Preview then Sync Now；verify Worker execution、progress、history and confirmation。
7. close Dashboard；verify hide only and Worker scheduler continues。
8. verify Dashboard has no Test Connections、Start Service or Re-register button；Stopped still shows `Restart Service`。
9. click Restart Service；approve UAC；verify stop/start (or start when stopped) and IPC recovery。
10. click the sole Open Log Folder button and tray-menu item；verify both open `C:\ProgramData\AteraSnipeSync` and there are no separate History/Preflight/Logs buttons。
11. cancel UAC；verify no Service change and Dashboard returns to fresh state。
12. exit Tray；verify Worker Service and scheduled sync continue。

Real-key verification remains manual-only。Credentials must not be printed、logged、committed或 copied into test fixtures。Real Service acceptance must be performed only on the operator's intended local test machine。

## 23. Acceptance criteria

- Program routes helper args before single-instance logic；normal mode runs TrayApplicationContext。
- `TrayDashboardForm`/`SyncConfigurationPage` replace formal ManualSyncForm naming and ownership。
- Tray contains no API client/orchestrator/status-write path。
- Save Config atomically writes JSON then sends ReloadSchedule；failure never falsely reports applied。
- Connection Test is only in API & Credentials；it saves/reloads current settings and sends a payload-free Worker command。
- Reload or any run disables Dashboard run actions and Restart Service until fresh status permits them。
- Restart uses UAC only on click, fixed service identity and same-directory Worker EXE；internal reinstall helper is not UI-reachable。
- Dashboard/tray expose one Open Log Folder action targeting `C:\ProgramData\AteraSnipeSync`。
- Service operations are asynchronous, stage-aware, sanitized and re-query SCM/IPC on completion。
- Tray close/exit never stops Worker automatically；Worker scheduler remains independent。
- full build/test passes with zero real API/SCM mutation。
## 2026-07 Notifications tab test extension

`SyncConfigurationPage` adds AntdUI inputs `SmtpHost`, `SmtpPort`, masked `SmtpUsername`/`SmtpPassword`, `EmailFrom`, `EmailTo`, `WebhookUrl`, checkbox `SmtpUseSsl`, button `Test Notifications`, and one bounded result label。Build/apply and `LocalAppSettingsStore` round-trip every property；SMTP username/password are an all-or-none pair and port must be 1..65535。

`TestNotificationsAsync` follows the existing connection-test safety sequence: `BuildSettings` → atomic `SaveSyncAppSettingsAsync` → payload-free `ReloadSchedule` → `WorkerIpcClient.Start(TestNotifications)` → render `Email: <status> • Webhook: <status>`。It never creates SMTP/HTTP clients in Tray。`WorkerIpcClient` requires a non-null terminal `NotificationTest` result for this command。

### 2026-07 Teams webhook format selector

`SyncConfigurationPage` adds an `AntdUI.Select` immediately before `WebhookUrl` with user-facing values `Teams Workflow (Adaptive Card)` and `Generic JSON`。Private mapping methods convert those labels to/from `WebhookPayloadFormat`; blank/unknown selections fail validation and never guess a payload type。On a null/new or legacy configuration, the selected value is `Teams Workflow (Adaptive Card)`。

`BuildSettings` stores the mapped enum in `NotificationConfig.WebhookPayloadFormat` and `Apply` restores it。For Test Notifications, `NotificationOutcome(configured: true, succeeded: true)` returns `Accepted`; the overall status says all configured endpoints accepted the tests rather than claiming final delivery。The compact label and daily log use only `Accepted`、`Failed` or `Not configured` and remain free of endpoint/recipient data。

## 2026-07 Dashboard status/activity and action layout

`TrayDashboardForm.InitializeComponent` changes the body to three rows：row 0 `Percent 100`，row 1 actions `Absolute 148`，row 2 footer `Absolute 58`。Row 0 has two 50% columns：the left contains a nested two-row TableLayout with `System status` at 40% and `Current activity` at 60%；the right contains `Schedule` at 40% and `Latest run` at 60%。

The Dashboard footer label is exactly `Detailed activity is available in the log folder.`。It describes where details can be found without implying a Daily Schedule frequency；the `DailyLogWriter` rotation behavior and Open Log Folder action remain unchanged。The Dashboard control tree must not contain the former `Detailed activity is written to daily log files.` wording。

The `System status` card contains exactly the Windows Service and Worker IPC pairs。The independent `Current activity` card contains `_phase`、`_progress` and `_message` without a duplicate internal caption。`_progress.Value` and every existing command/reload/service status update remain unchanged。

The actions content is a one-row/four-column TableLayout with four 25% columns。After shared AntdUI styling, `_sync`、`_preview`、`_cancel` and `_restart` set `AutoSize = false`、`Dock = Fill` and bounded horizontal/vertical margins。The titleless surface row height must leave at least the styled 38px button height plus margins after surface margin/padding overhead。

Add an STA WinForms regression test that constructs the Dashboard at `MinimumSize`, recursively performs layout, verifies each action button bounds are fully within its direct parent, verifies one `Current activity` card exists, and verifies the AntdUI progress control shares that card as its nearest AntdUI Panel ancestor。The test uses temp paths and fakes only and does not show the form, query SCM/IPC, launch UAC, or open Explorer。

## 2026-07 Count-only Latest run and offline summary

Remove `_lastResult`、`_pulledCount`、`_mappedCount` and `_failedCount` from `TrayDashboardForm`。Rename `_skippedCount` to `_noChangeCount` and add `_deletedCount`。`CreateMetricsGrid` becomes one row/four equal columns with captions exactly `Created`、`Updated`、`No change`、`Deleted`。The only non-metric label in the card is `_lastRunTime`。

Replace `SetLatestSummary`/six-argument `SetMetricValues` with `SetLatestRun(finishedText, created?, updated?, noChange?, deleted?)`。Online status maps `latest.Created/Updated/Skipped/Deleted`; terminal maps `result.Created/Updated/Skipped/Deleted`。Neither path reads `LastResult` or `Success` for Latest run rendering。

`LatestSyncHistoryReader.ReadSummaryAsync` returns `LatestSyncHistorySummary?` instead of string。The model has required `FinishedAtUtc`、`Created`、`Updated`、`NoChange`、`Deleted`。Reader parses `run.finishedAtUtc` when present and summary `assetsCreated/assetsUpdated/assetsSkipped/assetsDeleted` with missing counts defaulting to zero。It never returns `run.result` text。

For Current activity, `RenderSyncResult` uses only the terminal completion contract: `Success = true` renders phase `Sync completed.` and outcome `Completed` even when failed counts are non-zero; `Success = false` renders `Sync failed.` and outcome `Failed` even when some actions completed before the fatal interruption. `Cancelled = true` remains `Sync canceled.`/`Cancelled`. Remove the partial-success branch and helper. Manual log retains Outcome and Failed count because it is not the Last run card。

## 2026-07 In-place configuration page technical specification

### Class conversion

Replace the top-level configuration window with:

```csharp
internal sealed class SyncConfigurationPage : UserControl
{
    internal SyncConfigurationPage(
        LocalAppSettingsStore settingsStore,
        WorkerIpcClient ipcClient,
        DailyLogWriter manualLogWriter);

    internal event EventHandler? DashboardRequested;
    internal event EventHandler? SettingsSaved;

    internal Task ActivateAsync();
}
```

The implementation is stored in `SyncConfigurationPage.cs`；no production class named `SyncConfigurationForm` may remain and the page must not inherit `Form`/`AntdUI.Window`。Remove standalone/default construction、private log-writer ownership、`OnShown`、`DialogResult`、`AcceptButton`、`CancelButton` and `Close()` behavior。

`ActivateAsync` must disable page action buttons, show `Loading configuration...`, call `LocalAppSettingsStore.LoadSyncAppSettingsAsync`, reset every reused control to its declared default before applying loaded values, display a bounded load error on failure, and re-enable actions in `finally`。The page includes an AntdUI `Cancel` button；Cancel raises `DashboardRequested` without saving。Successful `SaveAsync` raises `SettingsSaved` after atomic local save and does not close/dispose a window。Connection/notification test paths retain their current save/reload/IPC behavior and remain on the page。

### Dashboard ownership and navigation

`TrayDashboardForm` adds one fill-docked `_pageHost`、one `_dashboardPage` control and one `_configurationPage`。`InitializeComponent` adds both page controls to `_pageHost`, initially showing only Dashboard。The PageHeader Configuration click calls:

```csharp
private async Task ShowConfigurationPageAsync();
internal void ShowDashboardPage();
```

`ShowConfigurationPageAsync` checks the existing reducer once, changes the main window title, hides Dashboard, shows/brings Configuration to front immediately, then awaits `ActivateAsync`。It does not change `DashboardState.LocalActivity`。`ShowDashboardPage` hides Configuration, restores Dashboard/title and calls `ApplyControlState` so repeated navigation cannot retain a disabled Config button。

`SettingsSaved` handling first calls `ShowDashboardPage()` and then awaits the existing `ReloadScheduleAsync()`；`DashboardRequested` only calls `ShowDashboardPage()`。Remove `OpenConfigurationAsync` and every `ShowDialog` call。`DashboardLocalActivity.SavingConfiguration` is removed because page visibility is not an exclusive async operation；`ReloadingSchedule` remains the save-success reload state。

Every reload/run/service-maintenance `finally` path must set local activity to Idle and call `ApplyControlState()` before awaiting the final SCM/Worker refresh。The refresh may refine availability, but a refresh failure must not leave controls disabled from the previous exclusive state。

## 2026-07 Hardcoded Unknown fallback UI/config specification

`SyncConfigurationPage.CreateMappingTab` removes the `AddText` calls for keys `DefaultCompany`、`DefaultManufacturer` and `DefaultModel`。`BuildSettings` assigns `SyncApplicationDefaults.CompanyName`、`.ManufacturerName`、`.ModelName` directly；`Apply` removes all `Set` calls for those keys。Default category remains a normal AntdUI input。

`SyncAppSettings` retains its three nullable properties for source compatibility and initializes them to the shared constants。`LocalAppSettingsStore.LoadSyncAppSettingsAsync` always assigns the constants and does not read the legacy JSON properties。`SaveSyncAppSettingsAsync` does not validate caller-supplied values and removes `DefaultCompanyName`、`DefaultManufacturerName`、`DefaultModelName` from the Mapping JSON object。`HasAnySyncAppSetting` must not count the three always-present constants, otherwise an otherwise-empty JSON file would incorrectly become a complete settings snapshot。

Required tests：

- saved complete config omits the three legacy JSON properties；reloaded settings expose the constants。
- legacy JSON containing custom fallback values is normalized to constants。
- Dashboard configuration control tree contains no AntdUI input whose `Name` is one of the removed keys。

### Required tests

- construct the real `TrayDashboardForm` on an STA thread with temp settings/log paths and fakes；assert exactly one configuration page is a descendant of the Dashboard and it is not a `Form`。
- invoke Configuration navigation, assert Dashboard content hidden and configuration visible in the same top-level form。
- click `Cancel`, assert Dashboard visible, configuration hidden and the Configuration button enabled。
- navigate to Configuration a second time without recreating the main form；assert success and no extra top-level form/control instance。
- preserve existing minimum-size button/activity/Latest run assertions。

Tests update offline reader assertions to structured counts with no result string and extend the STA layout test to inspect the Latest run card's exact four captions。

## 2026-07 AntdUI Demo visual alignment specification

### Dashboard header and shared theme

`TrayDashboardForm.InitializeComponent` uses a two-row root layout：row 0 is an absolute 58px AntdUI `PageHeader` and row 1 is the existing percent-filled Dashboard body。Remove the complete dark hero panel, its `Atera → Snipe-IT` label and its subtitle。The header uses `Text = "Auto Sync"`、`SubText = "Dashboard"`、`DividerShow = true` and hosts the existing `_config` button docked right with `SettingOutlined` icon。The button retains the same click handler and reducer-driven enabled state。

`TrayUiTheme` uses Ant Design-like light tokens：canvas `#F1F3F6`、surface `#FFFFFF`、border `#D9DEE7`、primary `#1677FF`、text `#262626`、muted text `#8C8C8C`。Cards retain a 1px border, radius 8 and a low-opacity 6px shadow；buttons use radius 6。Status semantic colors and every control-state rule remain unchanged。

### Configuration boolean controls

`SyncConfigurationPage` replaces every `AntdUI.Checkbox` with `AntdUI.Switch` while retaining the existing private field names and all `.Checked` read/write behavior。Create exactly six base switches with these `Name` values：

- `CreateMissingCompanies`
- `CreateMissingModels`
- `ScheduleEnabled`
- `RunOnLastDayOfMonth`
- `NotificationsEnabled`
- `SmtpUseSsl`

Each switch is 56×32, left-anchored, uses `TrayUiTheme.Primary` for `Fill`, and appears in the grid's value column。Its descriptive text is supplied by the existing first-column AntdUI label：`Create missing companies`、`Create missing models`、`Schedule enabled`、`Also run on the last day of month`、`Notifications enabled` and `Use TLS/SSL for SMTP` respectively。No AntdUI `Checkbox` remains in the TrayApp production UI。

Configuration grid density is explicit：single-line AntdUI Input and Select controls and in-grid test buttons use height 44；multiline Input uses height 96；every value control uses vertical margin 9；the first-column label uses vertical padding 10 and middle-left alignment。Rows remain `AutoSize`, and each `TabPage.AutoScroll` remains enabled so added spacing never forces field overlap。

The Configuration root footer uses an absolute height of 84、one explicit 100% row and 14px vertical padding。`Cancel` and `Save changes` have minimum height 46 and DPI-safe minimum widths 176/156 respectively；footer columns are at least 188/168 wide。This prevents TableLayout preferred-size compression and preserves complete icon/text rendering。

## 2026-07 Schedule frequency-aware field specification

Give `_frequency` the control name `ScheduleFrequency`。After all Schedule rows exist, subscribe to `SelectedValueChanged` and call a private `UpdateScheduleFieldVisibility(TableLayoutPanel grid)` once for initial Daily state。The handler parses `_frequency.Text` case-insensitively；an unparseable transient selection uses Daily visibility until normal form validation reports the unsupported selection。

`UpdateScheduleFieldVisibility` locates the rows containing `_text["WeekDays"]`、`_text["MonthDays"]` and `_lastDay`。A private `SetSettingRowVisible(TableLayoutPanel grid, Control valueControl, bool visible)` sets both the row's column-0 label and column-1 value control to the same visibility and requests layout。Visibility matrix：

| Frequency | Weekly days | Monthly days | Last day of month |
| --- | --- | --- | --- |
| Daily | hidden | hidden | hidden |
| Weekly | visible | hidden | hidden |
| Monthly | hidden | visible | visible |

Do not hide Frequency、Schedule enabled、Windows time zone ID or Run times。Do not blank or rewrite hidden values。`ResetToDefaults` continues deriving the TimeZone input from its `PlaceholderText`, which is initialized with `TimeZoneInfo.Local.Id`；`Apply` continues replacing it with nonblank persisted `schedule.TimeZoneId`。No API、JSON schema or scheduler semantics change。

Extend the STA Configuration control-tree regression：select the Schedule tab, assign each of `Daily`、`Weekly` and `Monthly` to the AntdUI Select, pump/layout the form, and assert the value control plus its row label follow the matrix while the `TimeZone` input remains visible throughout。

### Regression coverage

Extend `DashboardLayoutTests` on its existing STA/temp-only control tree to assert：the Dashboard header is `Auto Sync / Dashboard`；no label contains `Atera → Snipe-IT`；the embedded configuration page contains zero AntdUI `Checkbox` controls；and the six expected base switch names occur exactly once。No test shows a real window or contacts Worker、SCM、Atera、Snipe-IT or any notification endpoint。

## 2026-07 Dashboard card and action refinement specification

### First-row cards

Keep Dashboard body row 0 as two equal columns。Replace the left cell's single combined card with a two-row `TableLayoutPanel` whose row percentages are 40/60：row 0 contains `TrayUiTheme.CreateCard("System status", statusGrid)` and row 1 contains `TrayUiTheme.CreateCard("Current activity", activity)`。The right cell remains a two-row 40/60 layout；rename only its upper card title from `Automation` to `Schedule`。

`statusGrid` contains exactly `Windows Service` and `Worker IPC` rows。Remove the `_activeOperation` AntdUI label field、the `Current operation` `AddStatus` call and its assignment in `RenderSystemState`。Do not remove `DashboardState.ActiveOperation` or Worker status formatting because reducer/busy behavior still uses that state and Current activity messages continue to report active work when relevant。

`activity` contains only `_phase`、`_progress` and `_message` in three rows；the card supplies the `Current activity` heading, so no duplicate heading label exists inside the content。The AntdUI progress control's nearest AntdUI Panel ancestor must be the `Current activity` card, not `System status`。

### Titleless actions and long-button sizing

Add `TrayUiTheme.CreateSurface(Control content, Padding padding)` to create the same border/radius/background/margin surface as a card without constructing a title label or title row。Use it for the four-column action layout with vertical content padding sized to retain the current practical button height inside the existing absolute body row。No AntdUI label with text `Run and service actions` may remain。

Host `_config` inside a right-docked native panel in the Dashboard PageHeader。The host provides inset padding；the AntdUI button fills the host, has `Radius = 6`, a minimum size of at least 176×40 and retains `SettingOutlined`。Set `_openLogFolder.AutoSize = false` and give it a minimum/actual width of at least 176px while retaining `FolderOpenOutlined` and radius 6。At Dashboard `MinimumSize`, both buttons must remain completely inside their direct parent and display their full text/icon bounds。

### Required regression changes

Update the STA `DashboardLayoutTests` to require one `Current activity` card below `System status`；reject `Current operation`、`Automation` and `Run and service actions` labels；require one `Schedule` card；require the four action buttons to share one titleless AntdUI surface；and verify Configuration/Open Log Folder have a non-empty icon, radius 6 or greater, width no smaller than their minimum width, and bounds within their direct parent。

## 2026-07 Dashboard contrast and compact-density specification

### Shared surfaces and buttons

`TrayUiTheme.CreateSurface` sets `BorderColor = #D9DEE7`, `BorderWidth = 1`, `Radius = 8`, `Shadow = 6`, `ShadowColor = #344054`, `ShadowOpacity = 0.12`, `ShadowOffsetY = 2`, and `Margin = 5`. `CreateCard` uses a 28px title row and inner padding `14,10,14,12`.

For `ButtonKind.Secondary`, `StyleButton` sets `BorderWidth = 1`, `DefaultBorderColor = Border`, `DefaultBack = Surface`, `BackHover = #F6F8FA`, and `BackActive = #EEF2F6`. Do not change Primary/Error type selection, enabled-state reduction, click handlers or minimum 38px button height.

### Dashboard dimensions

`TrayDashboardForm` uses `MinimumSize = 920×650`, default `Size = 1040×700`, a 52px header, body padding `10,8,10,10`, a 100px action row and a 48px footer row. Action surface padding is `14,18,14,18`; action button horizontal inner margins are 4px per side except outer edges. The main cards remain two equal columns with 40/60 vertical proportions.

`CreateMetricsGrid` uses 8px top spacing; metric panels use 2px margin, 8/5 inner padding, the shared border color and 1px border. Count and caption behavior is unchanged; Deleted displays the real Worker/History count.

### Regression coverage

The existing STA minimum-size test additionally asserts all four titled cards plus the titleless action surface have `Shadow >= 6`, positive shadow opacity, a 1px border, margin no greater than 5 and compact padding. Preview、Restart Service and Open Log Folder must have a non-null default border color and 1px border. It continues checking all action bounds and does not show a window or call Worker/SCM/API endpoints.

## 2026-07 Worker IPC connection limits

`WorkerIpcServerOptions` adds `int MaxConcurrentConnections = 16` and `TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(10)`. Constructor validation accepts 1..254 connections and timeout greater than zero and no more than two minutes.

`WindowsWorkerPipeFactory.Create` accepts the validated maximum instance count. `WorkerIpcServer` owns a `SemaphoreSlim` of that size, acquires a slot before creating/listening, transfers slot ownership to the accepted handler, and releases it exactly once from connection observation/failure cleanup.

Inject `TimeProvider` into `WorkerIpcServer`. Around only `ReadBoundedLineAsync`, create a timeout `CancellationTokenSource` using that provider and link it with host shutdown. A timeout logs one sanitized warning and closes the connection without invoking `IWorkerCommandHandler`. Once the complete request line is parsed, command execution uses only the host/request tokens and has no IPC duration timeout.

Required tests use isolated pipe names and configurable small limits to prove incomplete first lines time out, completed long commands continue, excess clients cannot allocate another handler until release, shutdown releases slots, and the existing `BuiltinUsers` local-client test still passes.

## 2026-07 Notification outcome switch specification

`SyncConfigurationPage` exposes exactly two outcome switches in addition to `NotificationsEnabled`:

| Switch name | Label | Expanded canonical events |
| --- | --- | --- |
| `NotifySyncCompleted` | `Sync completed` | `ScheduledSyncCompleted`, `ManualSyncCompleted`, `ManualPreviewCompleted`, `SyncCompleted` |
| `NotifySyncFailed` | `Sync failed` | `ScheduledSyncFailed`, `ManualSyncFailed`, `ManualPreviewFailed`, `SyncFailed` |

Keep two ordered static event arrays and use them in `ReadSelectedNotificationEvents()` and `ApplyNotificationEvents(IEnumerable<string>)`. Reading returns all four canonical names for each checked group, with Completed events before Failed events. Applying trims and compares case-insensitively; any recognized event in a group checks the group. Unknown values do nothing, null throws, and an empty list clears both switches. This intentionally normalizes a legacy partial group to the complete group on the next save.

`NotificationEventTypes.NotificationTest` is not part of either group because explicit Test Notifications continues bypassing `NotificationEventFilter`. Do not add JSON properties, IPC fields or sender behavior; `Notifications.Enabled = true` plus both switches off still publishes nothing.

Extend the STA Configuration regression to require only `NotifySyncCompleted` and `NotifySyncFailed`, reject the six removed per-trigger switch names and `NotificationEvents` input, and verify one legacy completed event plus one legacy failed event expands to all eight canonical values. Tests remain local and offline.

## 2026-07 Configuration Cancel label specification

In `SyncConfigurationPage`, retain the existing `_back` AntdUI button, `ArrowLeftOutlined` icon, minimum size, click handler and `DashboardRequested` event behavior, but set its visible `Text` to exactly `Cancel`. The action remains a navigation-without-save operation: it must not call `BuildSettings`, `SaveSyncAppSettingsAsync`, `ReloadSchedule`, Worker IPC or any external API.

Update `DashboardLayoutTests` so the embedded Configuration control tree contains exactly one AntdUI button with `Text == "Cancel"`, contains no button with `Text == "Back to Dashboard"`, and raises the real `Cancel` button click to verify the same page returns to Dashboard and can be opened again. Retain the existing minimum-height assertion and offline/temp-only test boundary.

## 2026-07 Scheduled dry-run control removal specification

Delete `_dryRun` from `SyncConfigurationPage`; remove its Mapping & Import row and every `BuildSettings`, `Apply` and `ResetToDefaults` access. No AntdUI control may use `Name == "DryRun"`, and no Configuration label may contain `Dry run`. Do not replace it with another toggle or hidden default.

The page still builds and atomically saves the complete editable configuration through `SyncAppSettings`, which no longer exposes a `DryRun` property. Extend `DashboardLayoutTests` to exclude `DryRun` from the expected switch catalog and explicitly reject both the removed switch name and label text. Preview behavior is outside this UI setting and remains fixed dry-run through `ManualSyncRequestFactory`.

## 2026-07 Windows 11 / Server 2022 visual consistency specification

### DPI and top-level window

In `AteraSnipeSync.TrayApp.csproj`, set `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>` in the existing property group. `Program.Main` continues calling `ApplicationConfiguration.Initialize()` before creating UI. `TrayUiTheme.ApplyWindow` retains `AutoScaleMode.Dpi`; do not add a manifest DPI override, call `Application.SetHighDpiMode` separately, or disable DPI awareness.

Change the public declaration to:

```csharp
public sealed class TrayDashboardForm : AntdUI.BorderlessForm
```

Change the theme entry point to `public static void ApplyWindow(AntdUI.BorderlessForm form)`. In addition to the existing font, canvas, foreground and centered-start settings, it assigns `UseDwm = false`, `Radius = WindowRadius`, `Shadow = WindowShadow`, `BorderWidth = 1`, `BorderColor = Border` and the shared `WindowShadowColor = Color.FromArgb(100, SurfaceShadow)` to `ShadowColor`. This forces the same AntdUI region/shadow path on Windows 11 and Windows Server 2022 without an opaque heavy frame shadow. `BorderlessForm` owns the normal-window region and removes that rounding while maximized; do not add custom Win32 region or DWM P/Invoke code.

`TrayUiTheme` exposes these internal constants so production layout and tests use one contract:

```csharp
public const int WindowRadius = 10;
public const int WindowShadow = 10;
public const int SurfaceRadius = 10;
public const int NestedSurfaceRadius = 8;
public const int ControlRadius = 6;
public static readonly Size DashboardActionButtonSize = new(156, 40);
```

`CreateSurface` uses `SurfaceRadius`; `CreateMetric` uses `NestedSurfaceRadius`; all existing explicit button/input/select/tab/switch radius assignments use `ControlRadius` where they represent the standard control radius. No color, enabled-state, validation, save, IPC or click behavior changes.

### Compact Dashboard actions

Add `TrayUiTheme.StyleDashboardActionButton(AntdUI.Button button, ButtonKind kind)` as the single setup path for `_sync`, `_preview`, `_cancel` and `_restart`. It first applies `StyleButton`, then sets `AutoSize = false`, `Dock = DockStyle.None`, `Anchor = AnchorStyles.None`, `MinimumSize`, `MaximumSize` and `Size` to `DashboardActionButtonSize`, and clears the generic outer margin. Its comment must state that the fixed logical size is subsequently scaled by WinForms DPI handling and that it intentionally does not consume extra cell space.

The existing four-column `TableLayoutPanel` retains four 25% columns and the titleless surface. Remove the per-button `DockStyle.Fill` assignments and stretching margins. Each action button remains directly parented by this table and is centered by `AnchorStyles.None`. Keep the 100px action row, action-surface padding, button ordering, icons, reducer-controlled state and event handlers unchanged. `Configuration` and `Open Log Folder` retain their existing minimum widths but use `ControlRadius`.

### Required regression coverage

Extend `DashboardLayoutTests.MinimumSize_KeepsActionsVisible_AndSeparatesActivityCard` to assert that the real Dashboard is an `AntdUI.BorderlessForm` with `UseDwm == false`, `Radius == WindowRadius`, `Shadow == WindowShadow`, a 1px border and the shared border/shadow colors. Require the four named cards and action surface to use `SurfaceRadius`, each metric tile to use `NestedSurfaceRadius`, and Dashboard buttons to use `ControlRadius`.

Run the action-layout assertions twice on the same control tree: first at `MinimumSize`, then at `1560×1187`. At each size, recursively layout the form and assert that the four action buttons:

- have `Dock == None` and `Anchor == None`;
- have identical `Size`, `MinimumSize` and `MaximumSize` equal to `DashboardActionButtonSize` after the current process DPI scaling;
- remain centered within their direct table cells and fully inside the parent client bounds;
- retain the same size after the form expands, proving they do not stretch with column width.

The test must compare the expanded size with the already observed minimum-size button size rather than assume a 96-DPI test host. It remains STA, temp-path-only, does not show the form, and does not access Worker, SCM, Atera, Snipe-IT, SMTP or webhook endpoints.

## 2026-07 Tray and executable icon specification

### Project resource contract

`src/AteraSnipeSync.TrayApp/Assets/tray-icon.ico` is the single ICO input. It must contain 16×16, 20×20, 24×24, 32×32, 48×48, 64×64, 128×128 and 256×256 entries. In `AteraSnipeSync.TrayApp.csproj`:

- set `<ApplicationIcon>Assets\tray-icon.ico</ApplicationIcon>` so the Windows apphost executable carries the product icon;
- add the same file as `EmbeddedResource` with logical name `AteraSnipeSync.TrayApp.Assets.tray-icon.ico` so runtime loading does not depend on a loose output file.

Do not add a second ICO, a runtime download, an operator-configurable path or a fallback to `SystemIcons.Application`.

### `TrayIconLoader`

Create `src/AteraSnipeSync.TrayApp/TrayIconLoader.cs`:

```csharp
namespace AteraSnipeSync.TrayApp;

internal static class TrayIconLoader
{
    internal const string ResourceName =
        "AteraSnipeSync.TrayApp.Assets.tray-icon.ico";

    public static Icon Load();
}
```

`Load()` obtains `ResourceName` from `typeof(TrayIconLoader).Assembly`, constructs an `Icon`, clones it before disposing the resource stream/source icon and returns the caller-owned clone. Missing or invalid resources throw `InvalidOperationException` with a bounded message naming the TrayApp icon resource; no file-system or network access occurs.

### `TrayApplicationContext` ownership

Add `private readonly Icon _trayIcon;`. The constructor calls `TrayIconLoader.Load()` before creating `NotifyIcon` and assigns `_trayIcon` to `NotifyIcon.Icon`. `ExitThreadCore()` sets the notify icon invisible, disposes `NotifyIcon`, then disposes `_trayIcon`. Menu items, click handlers, tooltip and Dashboard lifetime remain unchanged.

### Required tests

Add `tests/AteraSnipeSync.Tests/TrayApp/TrayIconLoaderTests.cs` to verify offline that:

- the manifest contains exactly the expected logical resource name;
- the ICO directory header declares icon type 1 and exactly eight entries with the required square sizes, interpreting a zero width/height byte as 256;
- `TrayIconLoader.Load()` returns a usable caller-owned icon containing opaque blue pixels and can be disposed normally.

The build itself must succeed with `ApplicationIcon` enabled. Manual acceptance must inspect the rebuilt `AteraSnipeSync.TrayApp.exe` in Explorer and the running notification-area icon on both light and dark taskbars; no real Worker, SCM, Atera or Snipe-IT call is required.
