# Tray App - 单元测试指导手册

## 1. 测试范围

Current automated tests cover local configuration persistence used by TrayApp and WorkerService. They do not call real Atera or Snipe-IT APIs and do not write to `C:\ProgramData`.

Covered behavior:

- `LocalAppSettingsStore` returns `null` when the local config file does not exist
- saving Atera API key creates local config and trims the value
- saving preserves unrelated JSON sections such as `SnipeIt`, `Sync`, and `Notifications`
- blank API key fails validation
- malformed local JSON fails with `InvalidOperationException`
- schedule config can be saved and loaded from `Sync.Schedule`
- invalid weekly/monthly schedule config is rejected before save
- default manual preflight path uses `C:\ProgramData\AteraSnipeSync\Preflight\{runId}`
- manual `Sync Now` request disables preflight CSV and runs as real manual sync
- manual `Preview Changes` request enables dry-run plus preflight CSV

Current automated tests do not cover:

- WinForms visual password masking
- actual write to machine-level `C:\ProgramData\AteraSnipeSync\appsettings.local.json`
- real API key validation
- future manual sync confirmation UI

## 2. 测试文件

```text
tests/AteraSnipeSync.Tests/Configuration/LocalAppSettingsStoreTests.cs
```

Related production code:

```text
src/AteraSnipeSync.Core/Configuration/LocalAppSettingsStore.cs
src/AteraSnipeSync.TrayApp/SettingsForm.cs
src/AteraSnipeSync.TrayApp/SettingsForm.Designer.cs
```

## 3. 测试命令

Run from repository root:

```powershell
dotnet build AteraSnipeSync.sln --no-restore
dotnet test AteraSnipeSync.sln --no-build
```

Only local settings tests:

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~LocalAppSettingsStoreTests"
```

## 4. Schedule Config Test Notes

Schedule config is saved under:

```json
{
  "Sync": {
    "Schedule": {
      "Enabled": true,
      "Frequency": "Weekly",
      "TimeZoneId": "UTC",
      "RunTimes": ["08:00", "17:30"],
      "DaysOfWeek": ["Monday", "Friday"],
      "DaysOfMonth": [1, 15],
      "RunOnLastDayOfMonth": false,
      "PreventOverlappingRuns": true
    }
  }
}
```

Invalid schedules must not be saved. Examples:

- weekly schedule without selected weekdays
- monthly schedule without `DaysOfMonth` and without `RunOnLastDayOfMonth`
- monthly days outside `1..31`
- duplicate run times

## 5. Manual UI Verification

Manual TrayApp verification:

```powershell
dotnet run --project .\src\AteraSnipeSync.TrayApp\AteraSnipeSync.TrayApp.csproj
```

Check:

- Atera API key input is masked
- entering a test value and clicking `Save` shows success
- `C:\ProgramData\AteraSnipeSync\appsettings.local.json` contains `Atera.ApiKey`
- do not use a real API key for routine UI smoke testing

Future manual sync UI should show the preflight CSV folder and allow confirm/cancel. Scheduler automatic sync must not show that UI, must not wait for confirmation, and must not generate manual preflight CSV files.

Manual sync UI must expose two separate buttons:

- `Sync Now`: direct real sync, no preflight CSV
- `Preview Changes`: generates CSV only; confirm reruns real sync and does not execute from the first CSV snapshot

## 6. 安全规则

- automated tests must not read real API keys
- automated tests must not write to `C:\ProgramData`
- automated tests must not call real Atera or Snipe-IT APIs
- real API keys may only be entered manually by the owner/operator
- `appsettings.local.json` must not be committed to git
