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
    public int? DefaultStatusId { get; init; }
    public IReadOnlyDictionary<string, string> CompanyAliases { get; init; }
    public string? MacAddressCustomFieldDbColumnName { get; init; }
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
- collect optional company aliases, one per line, using `Atera company => Snipe-IT company`
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
- append progress detail messages to the sanitized log so long previews show current work
- append sanitized manual run log lines to `C:\ProgramData\AteraSnipeSync\Logs\ManualSync_yyyyMMdd.log`, creating the date-specific file when missing
- construct `AteraClient`, `InventoryMapper`, `SnipeImporter`, and `SyncOrchestrator` directly in the UI process
- save completed preview/sync `SyncRunResult` reports through `JsonFileSyncStatusStore` under `C:\ProgramData\AteraSnipeSync\History`
- log the saved `SyncResult_*.json` path when report/status history is written
- require a confirmation dialog before `Sync Now`
- never print or log API keys or tokens
- show only sanitized run summaries, warnings, failures, and preflight CSV folder paths

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
    "MacAddressCustomFieldDbColumnName": "_snipeit_mac_address_1",
    "NameMatchThreshold": 0.92,
    "CreateMissingCompanies": false,
    "CreateMissingModels": false
  },
  "Mapping": {
    "DefaultCompanyName": "Unknown Company",
    "DefaultManufacturerName": "Unknown Manufacturer",
    "DefaultModelName": "Unknown Model",
    "DefaultCategoryName": "Computer",
    "CompanyAliases": {
      "Atera company name": "Snipe-IT company name"
    }
  }
}
```
