# Tray App - 技术规格

## 1. 目标

实现 TrayApp 的第一版本地配置窗口，让用户可以在 password field 中输入 Atera API key，并保存到本机 `appsettings.local.json`。

## 2. 文件与命名空间

Production code:

- `src/AteraSnipeSync.TrayApp/SettingsForm.cs`
- `src/AteraSnipeSync.TrayApp/SettingsForm.Designer.cs`
- `src/AteraSnipeSync.TrayApp/ManualSyncForm.cs`
- `src/AteraSnipeSync.TrayApp/Program.cs`
- `src/AteraSnipeSync.Core/Configuration/LocalAppSettingsStore.cs`

Tests:

- `tests/AteraSnipeSync.Tests/Configuration/LocalAppSettingsStoreTests.cs`

## 3. LocalAppSettingsStore

Namespace:

```csharp
namespace AteraSnipeSync.Core.Configuration;
```

Class:

```csharp
public sealed class LocalAppSettingsStore
{
    public const string DefaultFileName = "appsettings.local.json";

    public LocalAppSettingsStore(string filePath);

    public static string GetDefaultFilePath();

    public Task<string?> LoadAteraApiKeyAsync(CancellationToken cancellationToken);

    public Task SaveAteraApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken);

    public Task<ManualSyncSettings?> LoadManualSyncSettingsAsync(CancellationToken cancellationToken);

    public Task SaveManualSyncSettingsAsync(
        ManualSyncSettings settings,
        CancellationToken cancellationToken);
}
```

Manual sync settings model:

```csharp
public sealed class ManualSyncSettings
{
    public string? AteraBaseUrl { get; init; }
    public string? AteraApiKey { get; init; }
    public string? SnipeItBaseUrl { get; init; }
    public string? SnipeItApiToken { get; init; }
    public string? DefaultCompanyName { get; init; }
    public string? DefaultManufacturerName { get; init; }
    public string? DefaultModelName { get; init; }
    public string? DefaultCategoryName { get; init; }
    public IReadOnlyList<string> ModelCategoriesToNormalize { get; init; }
    public int? DefaultStatusId { get; init; }
    public IReadOnlyDictionary<string, string> CompanyAliases { get; init; }
    public IReadOnlyList<string> IgnoredDeviceTypes { get; init; }
    public string? MacAddressCustomFieldDbColumnName { get; init; }
    public IReadOnlyList<string> IgnoredMacAddresses { get; init; }
    public double? NameMatchThreshold { get; init; }
    public bool? CreateMissingCompanies { get; init; }
    public bool? CreateMissingModels { get; init; }
}
```

Responsibilities:

- Resolve default path to `C:\ProgramData\AteraSnipeSync\appsettings.local.json`
- Load `Atera.ApiKey` from JSON
- Save trimmed `Atera.ApiKey`
- Load and save reusable manual sync panel settings under `Atera`, `SnipeIt`, and `Mapping`
- Load and save `Mapping.IgnoredDeviceTypes` as a trimmed string array
- Load and save `SnipeIt.IgnoredMacAddresses` as a normalized, de-duplicated string array
- Preserve manual sync secrets locally without printing or logging them
- Create parent directory if missing
- Preserve existing unrelated JSON sections
- Reject blank API key with `ArgumentException`
- Throw `InvalidOperationException` when existing JSON is malformed or not an object

Security:

- Do not log the API key
- Do not print the API key
- Do not write the API key outside the configured local file
- Do not add real key values to tests or fixtures

## 4. SettingsForm

Namespace:

```csharp
namespace AteraSnipeSync.TrayApp;
```

Class:

```csharp
public partial class SettingsForm : Form
```

Constructor:

```csharp
public SettingsForm();
public SettingsForm(LocalAppSettingsStore settingsStore);
```

UI controls:

- `TextBox ateraApiKeyTextBox`
  - `UseSystemPasswordChar = true`
  - single-line input
- `Button saveButton`
- `Label statusLabel`

Behavior:

- On form load, call `LoadAteraApiKeyAsync`
- If key exists, put it into `ateraApiKeyTextBox`
- On Save click:
  - validate key is not blank
  - save with `LocalAppSettingsStore.SaveAteraApiKeyAsync`
  - show success status
  - show failure status if save fails
- UI must never call real Atera API

## 5. Program

`Program.Main` currently runs `ManualSyncForm` as a temporary no-service manual validation entry. `SettingsForm` remains available as the original local Atera key settings surface.

## 6. Tests

Required tests:

- `LoadAteraApiKeyAsync_ReturnsNull_WhenFileDoesNotExist`
- `SaveAteraApiKeyAsync_CreatesLocalConfigWithAteraApiKey`
- `SaveAteraApiKeyAsync_PreservesExistingSections`
- `SaveAteraApiKeyAsync_ThrowsArgumentException_WhenApiKeyBlank`
- `LoadAteraApiKeyAsync_ThrowsInvalidOperationException_WhenJsonMalformed`
- `LoadManualSyncSettingsAsync_ReturnsNull_WhenFileDoesNotExist`
- `SaveManualSyncSettingsAsync_SavesManualPanelConfig`
- `LoadManualSyncSettingsAsync_ReturnsSavedManualPanelConfig`
- `SaveManualSyncSettingsAsync_SavesIgnoredDeviceTypes`
- `SaveManualSyncSettingsAsync_SavesIgnoredMacAddresses`
- `SaveManualSyncSettingsAsync_PreservesExistingSections`
- `SaveManualSyncSettingsAsync_ThrowsArgumentException_WhenSnipeTokenBlank`

Tests must use temporary files only and must not read or write `C:\ProgramData`.

## 7. Future Schedule Editor Specification

This section defines the planned TrayApp schedule UI. It is not part of the current minimal SettingsForm implementation until the Scheduler module is implemented.

### 7.1 UI Layout

The Schedule tab/window should contain:

- `CheckBox scheduleEnabledCheckBox`
- `ComboBox scheduleFrequencyComboBox`
  - values: `Daily`, `Weekly`, `Monthly`
- one or more time picker controls for local run times
- weekday checklist for weekly schedule
- day-of-month checklist or selector for monthly schedule
- `CheckBox runOnLastDayOfMonthCheckBox`
- `ComboBox timeZoneComboBox`
- `CheckBox preventOverlappingRunsCheckBox`
- `Button saveScheduleButton`
- `Button cancelScheduleButton`
- `Label scheduleStatusLabel`

The layout should feel like a backup job schedule editor:

- frequency is selected first
- only controls relevant to the selected frequency are enabled
- daily shows run time controls only
- weekly shows weekday checklist and run time controls
- monthly shows day-of-month / last-day controls and run time controls
- time zone and overlap options are always visible

### 7.2 Saved Config Shape

TrayApp should save schedule config into local settings while preserving unrelated sections:

```json
{
  "Sync": {
    "Schedule": {
      "Enabled": true,
      "Frequency": "Weekly",
      "TimeZoneId": "America/Edmonton",
      "RunTimes": ["02:00"],
      "DaysOfWeek": ["Monday", "Wednesday", "Friday"],
      "DaysOfMonth": [],
      "RunOnLastDayOfMonth": false,
      "PreventOverlappingRuns": true,
      "MissedRunPolicy": "Skip"
    }
  }
}
```

### 7.3 Validation

TrayApp should validate before saving:

- enabled schedule must have at least one run time
- weekly schedule must have at least one weekday
- monthly schedule must have at least one day-of-month or last-day enabled
- day-of-month values must be 1-31
- time zone id must be selected

TrayApp should show validation errors in the UI and should not save invalid schedule config.

## 8. Future Manual Sync UI Specification

Manual sync is different from scheduled sync, and it has two separate user actions.

### 8.1 `Sync Now`

UI control:

```text
Button syncNowButton
Text: Sync Now
```

Flow:

1. User clicks `Sync Now`
2. TrayApp sends a direct manual sync request to WorkerService through future IPC
3. WorkerService/Orchestrator runs the real sync pipeline
4. The request must set `TriggeredBy = "manual"`
5. The request must set `ManualPreflightCsvEnabled = false`
6. No temporary CSV is generated and no manual confirmation gate is shown
7. Final sync result/report/status is saved after the run

### 8.2 `Preview Changes`

UI control:

```text
Button previewChangesButton
Text: Preview Changes
```

Flow:

1. User clicks `Preview Changes`
2. TrayApp sends a manual preview request to WorkerService through future IPC
3. WorkerService/Orchestrator runs pull/map/import lookup planning with dry-run enabled
4. Snipe Import writes temporary CSV files:
   - `snipeit-assets-plan.csv`
   - `snipeit-companies-plan.csv`
   - `snipeit-categories-plan.csv`
   - `snipeit-models-plan.csv`
5. TrayApp shows the temporary CSV folder and provides open buttons
6. User confirms or cancels
7. Confirm executes the real manual sync by sending the same request shape as `Sync Now`
8. Confirm does not execute from the first CSV snapshot; it reruns the real sync pipeline and recalculates current Snipe-IT state

Manual preflight CSV files must not be treated as final reports. Final sync result/report/status should be written separately after the confirmed run completes.

Scheduler automatic sync must not show this UI and must not wait for confirmation.

### 8.3 Manual Request Factory

Core should provide a request factory so TrayApp/WorkerService do not duplicate option toggling:

Namespace:

```csharp
AteraSnipeSync.Core.Sync
```

Class:

```csharp
public static class ManualSyncRequestFactory
{
    public static SyncRunRequest CreateSyncNowRequest(SyncRunRequest baseRequest);

    public static SyncRunRequest CreatePreviewChangesRequest(
        SyncRunRequest baseRequest,
        string preflightDirectory);
}
```

Behavior:

- `CreateSyncNowRequest` sets `Sync.TriggeredBy = "manual"`
- `CreateSyncNowRequest` sets `Sync.DryRun = false`
- `CreateSyncNowRequest` sets `SnipeIt.DryRun = false`
- `CreateSyncNowRequest` sets `SnipeIt.ManualPreflightCsvEnabled = false`
- `CreateSyncNowRequest` sets `SnipeIt.ManualPreflightCsvDirectory = null`
- `CreatePreviewChangesRequest` sets `Sync.TriggeredBy = "manual-preview"`
- `CreatePreviewChangesRequest` sets `Sync.DryRun = true`
- `CreatePreviewChangesRequest` sets `SnipeIt.DryRun = true`
- `CreatePreviewChangesRequest` sets `SnipeIt.ManualPreflightCsvEnabled = true`
- `CreatePreviewChangesRequest` sets `SnipeIt.ManualPreflightCsvDirectory = preflightDirectory`
- `CreatePreviewChangesRequest` rejects blank `preflightDirectory`

### 8.4 Temporary No-Service Manual Test Window

Until WorkerService/IPC wiring is implemented, `Program.Main` may run a local
manual validation window directly:

```csharp
public sealed class ManualSyncForm : Form
```

Production file:

```text
src/AteraSnipeSync.TrayApp/ManualSyncForm.cs
```

Responsibilities:

- collect Atera API base URL and API key from password-protected local UI fields
- collect Snipe-IT API base URL and API token from local UI fields
- collect mapping defaults needed to build `MappingOptions`
- collect optional company aliases, one per line, using `Atera company=Snipe-IT company`
- collect common ignored Atera device types with a checked list and pass the selected values into `MappingOptions.IgnoredDeviceTypes`
- collect ignored MAC addresses from one `Ignore MAC Addresses` text field; split values on `;`, trim blanks, validate/normalize them, and pass them into `SnipeImportOptions.IgnoredMacAddresses`
- default `Create Missing Companies` to checked for new local configurations
- expose `Load Config` to reload reusable manual panel settings from `appsettings.local.json` into the current UI without logging secrets
- expose `Save Config` to persist reusable manual panel settings in `appsettings.local.json`
- load saved manual panel settings when the form opens
- create a base `SyncRunRequest` without changing Atera or Snipe-IT wire shapes
- call `ManualSyncRequestFactory.CreatePreviewChangesRequest` for `Preview Changes`
- call `ManualSyncRequestFactory.CreateSyncNowRequest` for `Sync Now`
- expose `Test Atera` to validate Atera credentials through the existing `AteraClient.PullInventoryAsync` path
- expose `Test Snipe-IT` to validate Snipe-IT connectivity through documented `GET /hardware?limit=1`
- show a determinate progress bar for manual preview, sync, and connection tests
- show the latest stage/detail text from safe `SyncProgressUpdate` callbacks
- write every sanitized progress detail callback to the daily file log without heartbeat or milestone sampling
- keep manual Preview/Sync UI log output to five deduplicated milestone lines: `Starting sync.`, `Processing models.`, `Processing categories.`, `Processing assets.`, and `Completed.`
- append sanitized manual run log lines to `C:\ProgramData\AteraSnipeSync\Logs\ManualSync_yyyyMMdd.log`, creating the date-specific file when missing
- append one complete failure block for every failed Preview/Sync to `C:\ProgramData\AteraSnipeSync\Logs\ManualSync_Error_yyyyMMdd.log`; include local/UTC run timestamps, preview-or-sync mode, mapped/failure counts, every grouped reason from `BuildFailureReasonLines`, and the saved history report path when available
- do not write successful runs, routine progress messages, credentials, raw request payloads, or raw response bodies to the dedicated error log; the UI 10-reason limit does not apply to this file
- construct `AteraClient`, `InventoryMapper`, `SnipeImporter`, and `SyncOrchestrator` directly in the UI process
- save completed preview/sync `SyncRunResult` reports through `JsonFileSyncStatusStore` under `C:\ProgramData\AteraSnipeSync\History`
- log the saved `SyncResult_*.json` path to the daily file when report/status history is written
- group `SnipeImportResult.Failures` by original code and root message before orchestration adds per-asset target prefixes; show occurrence count plus up to 3 affected target examples for each group
- order grouped reasons by occurrence count descending, then stable code/message order, so a shared reference failure copied to many assets appears before isolated identity conflicts
- group non-import run failures by stage, code, and message with the same ordering; write every distinct reason to the daily file and history report
- require a confirmation dialog before `Sync Now`
- never print or log API keys or tokens
- keep detailed sanitized summaries, warnings, failures, and preflight paths in the daily file/history report rather than the manual run UI log

Limitations:

- this is an operator-run manual validation tool only
- automated tests must not click the UI or call real APIs
- `Test Atera` is read-only but may pull inventory pages because it reuses the existing verified Atera client path
- `Test Snipe-IT` is read-only and must not create, update, or delete Snipe-IT records
- Snipe-IT tokens are persisted only in the local machine `appsettings.local.json` when the operator clicks `Save Config`
- WorkerService/IPC status persistence remains future work; the temporary local manual window writes status/history directly after each completed run
- full TrayApp tray menu, IPC, status viewer, and preview-confirm UX remain future work

### 8.5 Manual Sync Local Config Shape

`Save Config` writes reusable panel settings while preserving unrelated sections:

```json
{
  "Atera": {
    "BaseUrl": "https://app.atera.com/api/v3",
    "ApiKey": "<local api key>"
  },
  "SnipeIt": {
    "BaseUrl": "https://snipe.example.com/api/v1",
    "ApiToken": "<local api token>",
    "DefaultStatusId": 2,
    "ModelCategoriesToNormalize": ["Server", "Laptop", "Desktop"],
    "MacAddressCustomFieldDbColumnName": "_snipeit_mac_address_1",
    "IgnoredMacAddresses": ["00:09:0F:AA:00:01", "00:09:0F:FE:00:01"],
    "NameMatchThreshold": 0.92,
    "CreateMissingCompanies": false,
    "CreateMissingModels": false
  },
  "Mapping": {
    "DefaultCompanyName": "Unknown Company",
    "DefaultManufacturerName": "Unknown Manufacturer",
    "DefaultModelName": "Unknown Model",
    "DefaultCategoryName": "Computer",
    "IgnoredDeviceTypes": ["Server", "SNMP", "TCP"],
    "CompanyAliases": {
      "Atera company name": "Snipe-IT company name"
    }
  }
}
```

## 15. 2026-07 配置、日志与报告加固

Manual Sync 功能测试阶段使用本地文件持久化凭据：

- `LocalAppSettingsStore.LoadAteraApiKeyAsync` 只从本地 JSON 的 `Atera.ApiKey` 读取 key，不使用环境变量覆盖。
- `LocalAppSettingsStore.SaveAteraApiKeyAsync` 验证并裁剪 key，然后通过统一的原子 settings mutation 写入 `Atera.ApiKey`。
- `LocalAppSettingsStore.LoadManualSyncSettingsAsync` 从本地 JSON 读取 `Atera.ApiKey` 与 `SnipeIt.ApiToken`，供 TrayApp 启动时回填。
- `LocalAppSettingsStore.SaveManualSyncSettingsAsync` 验证并裁剪两个凭据，然后分别写入 `Atera.ApiKey` 与 `SnipeIt.ApiToken`，同时保留无关配置 section。
- UI 保存成功后必须说明凭据已写入本地测试配置文件，并提醒该文件包含敏感明文。
- 自动化测试必须使用临时文件验证凭据可由新的 store 实例重新加载，不能读取真实凭据或 `C:\ProgramData`。
- `LoadWorkerSyncSettingsAsync` 及 WorkerService 的环境变量/明文拒绝策略保持不变，不属于本次 Manual Sync 测试改动。

settings store 的所有 mutation 统一走 `UpdateDocumentAsync`：进程内 semaphore + `<config>.lock` exclusive stream + reload latest JSON + `AtomicFileWriter`。这避免 TrayApp/WorkerService lost update。

新增 `internal sealed class DailyLogWriter : IAsyncDisposable`，内部使用有界 `Channel<string>` 单 reader 异步 append，超过容量时合并/丢弃低价值 progress message 并记录一次 dropped count；writer 接受受控文件名前缀，使普通日志使用 `ManualSync`，专用错误日志使用 `ManualSync_Error`；启动时删除该前缀下 30 天前的日志。`ManualSyncForm.AppendLog` 只 enqueue 和更新 UI，不同步写磁盘；失败结果作为一个完整 block enqueue 到 error writer，避免部分 failure reasons 因队列容量被截断。

`RunSyncAsync` 获得 `SyncRunResult` 后调用 status store 时使用 `CancellationToken.None`。Atera Test Connection 构造 `ItemsPerPage=1, MaxPages=1` client options。URL validation 复用 `ApiEndpointValidator`。

把 request construction/validation 从 `ManualSyncForm` 提取到 `ManualSyncSettingsParser`；form 只负责控件状态、用户确认和结果渲染。新增 parser、log writer 与 secret-not-persisted 测试。

## 18. 单一 Category 输入与 Preview DeviceType

`ManualSyncForm` 使用 `_defaultCategoryTextBox` 与 `_modelCategoriesToNormalizeTextBox`。`CreateInputGrid` labels/defaults 为 `Default Category`/`Computer` 和 `Normalize From Categories (;)`/`Server; Laptop; Desktop`。`ApplyManualSyncSettings` 将列表用 `; ` 回填；`BuildManualSyncSettings` 使用 required 分号 parser 生成非空 `ModelCategoriesToNormalize`；`BuildBaseRequest` 只设置 `MappingOptions.DefaultCategoryName`，来源列表不参与普通 Sync。

`NormalizeModelCategoriesAsync` 构造 `SnipeModelCategoryNormalizationOptions` 时使用 `_defaultCategoryTextBox` 与非空的 `_modelCategoriesToNormalizeTextBox` parsed list。Preview assets CSV 最后一列由 Core 输出 `DeviceType`；TrayApp 不二次修改 CSV。Preview failure summary 必须保留 `SnipeImport.ModelCategoryMismatch` 与 `SnipeImport.ModelNameConflict` 原始 code/root message。

配置 store、mapping 与 importer tests 共同覆盖 request 值和 CSV；TrayApp project build必须证明单一 control 的 load/save/request wiring 完整。

## 16. Model Category 归一化按钮与专用日志

`ManualSyncForm` 新增 `_normalizeModelCategoriesButton`，按钮文字为 `Normalize Categories`，click handler 调用：

```csharp
private Task NormalizeModelCategoriesAsync();
```

该方法：

1. 通过 `RequireSnipeBaseUri()`、Snipe token textbox、Default Category textbox 与 Normalize Source Categories textbox 构造 `SnipeModelCategoryNormalizationOptions`；来源列表为空时不调用 API。
2. 在 `C:\ProgramData\AteraSnipeSync\Logs` 创建本次专用日志路径，文件名由 `ModelCategoryNormalizationLog.CreatePath` 生成。
3. 构造 `HttpClient` 与 `SnipeModelCategoryNormalizer`，先调用 `PlanAsync`。
4. no-op 时直接写成功汇总；candidate 非空时使用 `MessageBox.Show(..., YesNo, Warning)` 二次确认。
5. 确认后调用 `ExecuteAsync`，将每个 outcome 和最终汇总同时追加到专用日志及 sanitized UI log。
6. 所有退出路径在 UI 显示 `Category normalization log: {absolute path}`；日志写入使用 `CancellationToken.None`，避免 operator cancellation 丢失审计。

新增 `internal sealed class ModelCategoryNormalizationLog`：

```csharp
public static string CreatePath(string directoryPath, DateTimeOffset timestamp);
public static Task AppendAsync(string path, string message, CancellationToken cancellationToken = default);
```

`CreatePath` 只生成固定前缀且带七位 fractional seconds 的单次运行文件名；`AppendAsync` 创建目录并以带本地时间的完整单行 append。它不接受或格式化 credentials。`SetRunningState` 必须同步 enable/disable 新按钮。自动化测试不点击 WinForms、不访问 ProgramData 或真实 API；Core normalizer 使用 mocked HTTP tests 覆盖 wire behavior，日志 helper 若测试则使用临时目录。

## 17. Progress 文件日志保留规则

`ManualSyncForm.HandleProgressUpdate` 必须把每个 sanitized `SyncProgressUpdate` 逐条交给 file-only logger。reference、agent、asset、snapshot、planning、CSV 与 completion progress 使用同一无抽样规则；不得保留旧的 stage-change、50-item milestone 或 3-second heartbeat predicate。

Manual Preview/Sync 的 UI log 使用 `ManualSyncUiStageTracker`，不直接显示这些 progress detail。详细的当前 callback 仍可显示在独立 progress detail label，但不得追加到 UI log textbox。

## 19. MAC Fieldset Name 文本配置与统一 Model 计划

`ManualSyncSettings` 与 local config 新增：

```csharp
public string? MacAddressFieldsetName { get; init; }
```

配置路径：

```json
{
  "SnipeIt": {
    "MacAddressCustomFieldDbColumnName": "_snipeit_mac_address_1",
    "MacAddressFieldsetName": "Assets with MAC Address"
  }
}
```

`ManualSyncForm` 新增：

```csharp
private readonly TextBox _macFieldsetNameTextBox = new();
```

`CreateInputGrid` 在 MAC DB column 相邻位置添加 label `MAC Fieldset Name`，默认文本可为空；operator 必须按实际实例填写。`ApplyManualSyncSettings` 回填保存值。`BuildManualSyncSettings` 读取 optional trimmed text，并执行：

```csharp
if (string.IsNullOrWhiteSpace(macDbColumn) != string.IsNullOrWhiteSpace(macFieldsetName))
{
    throw new InvalidOperationException(
        "MAC Custom Field DB Column and MAC Fieldset Name must be configured together.");
}
```

`BuildBaseRequest` 设置：

```csharp
MacAddressFieldsetName = settings.MacAddressFieldsetName,
ModelCategoryNormalizationTargetName = settings.DefaultCategoryName,
ModelCategoriesToNormalize = settings.ModelCategoriesToNormalize
```

`ManualSyncRequestFactory`、`SyncOrchestrator.ApplySyncOptions`、`ScheduledSyncRequestFactory` 与 `WorkerRuntimeFactory` 必须完整复制这些字段，禁止 Preview 与 Sync Now 丢失配置。

原独立 `_normalizeModelCategoriesButton` 从 action panel 移除；旧 helper/class 可暂时保留以降低迁移风险，但普通运行不得调用它。Preview 的 `snipeit-models-plan.csv` 成为 category/fieldset 审核入口。

`AppendResult`：

- Preview：输出 `models added={CreatedModels}, models updated={UpdatedModels}`。
- Sync：输出 `models created={CreatedModels}, models updated={UpdatedModels}`。

自动化测试覆盖 settings save/load、request factories 字段复制、缺少配对字段时拒绝，以及 TrayApp/Core build。WinForms 测试不访问真实 API。

## 18. Manufacturer Alias UI 与配置技术规格

`ManualSyncSettings` 新增：

```csharp
public IReadOnlyDictionary<string, string> ManufacturerAliases { get; init; } = new Dictionary<string, string>();
```

`LocalAppSettingsStore` 从 `Mapping.ManufacturerAliases` 读取字典，保存时使用现有 dictionary writer 写回。`HasAnyManualSyncSetting` 必须把非空 manufacturer alias 字典视为有效设置。

`ManualSyncForm` 新增 `_manufacturerAliasesTextBox`，显示在 `Default Manufacturer` 后。load 时按 `source=target` 每行回填；`BuildManualSyncSettings` 解析该字段；`BuildBaseRequest` 设置 `MappingOptions.ManufacturerAliases`。

解析器应复用 company alias 的通用实现，并使用 manufacturer-specific 错误文案 `Atera manufacturer=Snipe-IT manufacturer`。空行和以 `#` 开头的行跳过；每个有效行只允许一个 `=` 且两侧 trim 后非空；`=>` 明确拒绝。

配置 round-trip tests 必须断言 `Dell Inc.=Dell` 被保存和加载。TrayApp build 必须验证新增 UI 接线可编译。

## 19. Manual Sync 逐条文件日志与简化 UI 日志技术规格

Production files:

```text
src/AteraSnipeSync.TrayApp/DailyLogWriter.cs
src/AteraSnipeSync.TrayApp/ManualSyncForm.cs
src/AteraSnipeSync.TrayApp/ManualSyncUiStageTracker.cs
```

`DailyLogWriter` 使用 single-reader、multi-writer 的 unbounded channel；`TryWrite(DateTimeOffset, string)` 对 writer 已接受的每条 entry 返回 `true`，`DisposeAsync` 必须 complete channel 并等待 reader flush。不得使用 `BoundedChannelFullMode.DropWrite`。I/O failure 保存到 `LastError` 并 complete writer，使后续 `TryWrite` 返回 `false`。

`ManualSyncForm.HandleProgressUpdate` 对每个 `SyncProgressUpdate` 都构造完整 detail（包含可用的 `Current/Total`）并调用 file-only logger。该路径不调用 UI+file 的 `AppendLog`，也不应用 heartbeat、stage-change 或每 N 项 milestone 判断。

`ManualSyncUiStageTracker` 负责一次 Preview/Sync 的 UI milestone，公开：

```csharp
internal sealed class ManualSyncUiStageTracker
{
    public string Start();
    public string? Observe(SyncProgressUpdate update);
    public IReadOnlyList<string> Complete();
}
```

- `Start` 每次 run 返回 `Starting sync.`。
- 第一条 model progress 返回 `Processing models.`。
- model 之后的第一条 category progress 返回 `Processing categories.`。
- category 之后的第一条 asset progress 返回 `Processing assets.`；model/category 之前的 Atera agent、mapping asset 或 hardware snapshot detail不得提前触发。
- 同一 milestone 只返回一次。
- `Complete` 按既定顺序补齐未观察到的 model/category/asset milestone，并最后返回 `Completed.`，保证空 batch 或短路成功仍有完整五阶段 UI。

`AppendDetailedResult`、history path 和 dedicated error-log path 使用 file-only logger。成功 run 的 UI log 除五阶段外不得显示 agent/asset 名称、count、failure reason 或路径。异常/取消允许显示一条简短 `Sync failed.` 或 `Sync canceled.`，详细内容只进入文件。

Required automated tests:

- enqueue more entries than the previous bounded capacity, dispose the writer, and assert every accepted entry exists as a separate file line;
- feed agent- and asset-specific progress before model/category milestones and assert those details never become UI log messages;
- assert model/category/asset milestones are ordered and deduplicated;
- assert completion fills missing successful milestones and appends `Completed.`.

## 20. Manual Sync 加权进度技术规格

新增 pure helper：

```csharp
internal sealed class ManualSyncProgressCalculator
{
    public ManualSyncProgressCalculator(bool previewOnly);
    public int Calculate(SyncProgressUpdate update);
}
```

Production file: `src/AteraSnipeSync.TrayApp/ManualSyncProgressCalculator.cs`。该类不访问 UI、文件或 API；每次 Preview/Sync 创建新实例，避免 run 之间共享状态。`ManualSyncForm` 的 progress callback 先调用该 calculator，再更新 progress bar；connection test 与独立 category normalization 可继续使用原通用显示逻辑。

真实 Sync 百分比区间：

| Work | Range |
|---|---:|
| Atera pull + mapping | 0–5% |
| Source batch validation | 5–10% |
| Model/category/reference planning and snapshots | 10–15% |
| Per-asset matching/planning | 15–30% |
| Company/category/model reference writes | 30–35% |
| Per-asset create/update | 35–99% |
| Whole run completed | 100% |

Preview 百分比区间：Atera/mapping 0–5%，validation 5–10%，model/category/reference preparation 10–15%，per-asset matching/planning 15–95%，CSV/dry-run finalize 95–99%，whole run completed 100%。

Calculator 必须使用受控 message prefix 和内部 phase state 区分 `Validating`、`Matching/Planned`、reference creating/created/failed、`Executing/Executed/Failed ... asset`。`Blocked ... asset` 根据当前 phase 留在 validation 或 matching band。snapshot/model/category callback 的 `Current == Total` 只完成其 10–15% 子区间，绝不能套用原 `50 + ratio * 45` 整体公式。

每次 `Calculate` 返回 `max(previousValue, calculatedValue)` 并 clamp 到 0–100。terminal failure 与最终 `Sync run completed` 返回 100；`Completed Snipe-IT import` 和 `Snipe-IT import stage completed` 返回 99。

Required automated tests:

- real Sync with 485 assets: completed model/category work remains at or below 15%; asset execution 0/485 is 35%, 243/485 is approximately 67%, and 485/485 is 99%;
- a completed 485-row hardware/model snapshot cannot jump to 95%;
- Preview maps 485-row matching/planning across 15–95% and reserves 95–99% for CSV/dry-run finalization;
- repeated or out-of-order callbacks never reduce the displayed percentage;
- only whole-run completion or terminal failure reaches 100%.
