# Tray App - 单元测试指导手册

## 1. 测试范围

Current automated tests cover local configuration persistence used by TrayApp and WorkerService. They do not call real Atera or Snipe-IT APIs and do not write to `C:\ProgramData`.

Covered behavior:

- `LocalAppSettingsStore` returns `null` when the local config file does not exist
- saving Atera API key creates local config and trims the value
- saving preserves unrelated JSON sections such as `SnipeIt`, `Sync`, and `Notifications`
- saving manual sync settings persists reusable Atera, Snipe-IT, mapping, alias, and import option values
- loading manual sync settings returns values saved in `Atera`, `SnipeIt`, and `Mapping`
- blank API key fails validation
- blank required manual sync secrets fail validation before save
- malformed local JSON fails with `InvalidOperationException`
- schedule config can be saved and loaded from `Sync.Schedule`
- invalid weekly/monthly schedule config is rejected before save
- default manual preflight path uses `C:\ProgramData\AteraSnipeSync\Preflight\{runId}`
- default manual log path uses `C:\ProgramData\AteraSnipeSync\Logs`
- manual `Sync Now` request disables preflight CSV and runs as real manual sync
- manual `Preview Changes` request enables dry-run plus preflight CSV
- manual progress UI displays a determinate progress bar and current stage/detail text from safe progress callbacks

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
src/AteraSnipeSync.TrayApp/ManualSyncForm.cs
src/AteraSnipeSync.TrayApp/Program.cs
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

Routine UI smoke check:

- Atera API key input is masked
- entering test values and clicking `Save Config` shows success
- `C:\ProgramData\AteraSnipeSync\appsettings.local.json` contains reusable `Atera`, `SnipeIt`, and `Mapping` settings
- changing field values and clicking `Load Config` restores the saved local settings without printing keys or tokens in the log
- do not use a real API key for routine UI smoke testing

Current temporary no-service manual sync check:

- Run the TrayApp command above.
- Enter the Atera API base URL, normally `https://app.atera.com/api/v3`.
- Paste the Atera API key into the masked field.
- Enter the real Snipe-IT API base URL including `/api/v1`; do not leave example values such as `https://snipe.example.com/api/v1`.
- Paste the Snipe-IT API token into the masked field.
- Click `Test Atera` to validate the Atera base URL and API key through the existing read-only inventory pull path.
- Click `Test Snipe-IT` to validate the Snipe-IT base URL and token through `GET /hardware?limit=1`.
- Fill mapping defaults: company, manufacturer, model, category, and default status id.
- Add company aliases when Atera and Snipe-IT names differ, one per line, for example `Moore Equine Veterinary Centre - AR => Moore Equine Veterinary Centre`.
- Leave create-missing options unchecked for the first safety preview unless the operator intentionally wants to test reference creation.
- Click `Save Config` if these values should be reused the next time the manual window opens.
- Click `Load Config` after editing saved local settings or moving a config file onto the machine; confirm the panel reloads the saved values.
- Click `Preview Changes`.
- Confirm the progress area changes from idle to a percentage and shows current work such as Atera page pull, mapping, Snipe-IT planning, dependency lookup, or CSV writing.
- Confirm the log shows only sanitized counts, warnings, failures, and the preflight CSV folder path.
- Confirm the log shows the saved `SyncResult_*.json` report/status path under `C:\ProgramData\AteraSnipeSync\History`.
- Confirm `C:\ProgramData\AteraSnipeSync\Logs\ManualSync_yyyyMMdd.log` exists for the current local date and contains sanitized log lines only.
- Open the CSV folder only on the local machine and inspect the generated CSV files.
- Click `Sync Now` only after the preview output has been reviewed; the UI must show a confirmation dialog first.

Manual real-key verification rules:

- real API keys must be entered manually by the owner/operator only
- do not print, screenshot, log, commit, or paste keys/tokens into tracked files
- do not add this flow to `dotnet test`, build, CI, or scripted probes
- expected safe output is a local UI summary with counts, warnings, failures, and local CSV paths only
- expected saved output is local-only history JSON and date-split log files under `C:\ProgramData\AteraSnipeSync`
- `Test Atera` is read-only but may take longer because it reuses the existing Atera inventory pull path
- `Test Snipe-IT` is read-only and must not create, update, or delete records
- Snipe-IT token is persisted only in the local machine config when the operator clicks `Save Config`
- `Preview Changes` is dry-run for writes, but it still contacts Snipe-IT for lookup planning
- if `Save Config` is clicked, panel values are saved only in `C:\ProgramData\AteraSnipeSync\appsettings.local.json`

Cleanup after manual real-key verification:

- close the TrayApp window
- clear or delete `C:\ProgramData\AteraSnipeSync\appsettings.local.json` if a real Atera key was saved
- remove temporary preflight folders under `C:\ProgramData\AteraSnipeSync\Preflight\` when they are no longer needed
- remove local manual sync log files only when they are no longer needed for operator troubleshooting
- clear shell history only if commands or local paths included sensitive environment details

Future production manual sync UI should still show the preflight CSV folder and allow confirm/cancel through the formal TrayApp/WorkerService path. Scheduler automatic sync must not show that UI, must not wait for confirmation, and must not generate manual preflight CSV files.

## 6. 安全规则

- automated tests must not read real API keys
- automated tests must not write to `C:\ProgramData`
- automated tests must not call real Atera or Snipe-IT APIs
- real API keys may only be entered manually by the owner/operator
- `appsettings.local.json` must not be committed to git
