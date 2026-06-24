# Project Progress

Last updated: 2026-06-23

## Latest Update

Snipe-IT preview/sync now creates missing companies and asset categories before hardware writes, and manual runs now save local reports/logs.

Code changed:

- Changed `SnipeImporter` so missing Snipe-IT asset categories are planned and created with `category_type = asset` instead of blocking assets when the operator-provided category, default `Computer`, is absent.
- Split real Snipe-IT writes into a reference phase and an asset phase: companies are created first, then categories, then models, and only after all reference writes succeed are hardware assets created or updated.
- Added `CreatedCategories` to `SnipeImportResult`, status history summaries, TrayApp summaries, and Snipe-IT preflight reporting.
- Added `snipeit-categories-plan.csv` to manual preflight CSV output. Missing-category previews show `Add,<category>,asset`; model rows leave `CategoryId` empty until the real category create returns an id.
- Defaulted the manual window's `Create Missing Companies` checkbox to checked for new local configurations while preserving saved operator choices.
- Connected the no-service `ManualSyncForm` to `JsonFileSyncStatusStore` so completed preview/sync runs save `SyncResult_*.json` under `C:\ProgramData\AteraSnipeSync\History`.
- Added date-split sanitized manual logs under `C:\ProgramData\AteraSnipeSync\Logs\ManualSync_yyyyMMdd.log`; each write recreates the daily file if it was deleted.
- Added `LocalAppSettingsStore.GetDefaultLogDirectory()` for the shared ProgramData log folder.
- Kept automated tests on mocked HTTP handlers only; no real Atera or Snipe-IT API call was added.

Tests changed:

- Replaced the old missing-category failure expectation with missing-category creation coverage.
- Added a test proving company/category/model POSTs occur before the first hardware POST.
- Added coverage for the new category preflight CSV and nullable category id in model preview rows.
- Added status history coverage for category create summaries and category change lists.
- Added local settings coverage for the default ProgramData log directory.

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/module-plans/08-TrayApp-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/technical-specs/05-StatusStore-技术规格.md`
- `docs/technical-specs/08-TrayApp-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `docs/test-guides/05-StatusStore-单元测试指导手册.md`
- `docs/test-guides/08-TrayApp-单元测试指导手册.md`
- `progress.md`

Behavior notes:

- Official Snipe-IT API documentation/OpenAPI was consulted before changing Snipe-IT company/category/model/hardware behavior.
- Company creation remains controlled by `CreateMissingCompanies`; the manual UI now starts with that option checked for new configurations.
- Category creation has no separate toggle in this pass. The module uses the operator-provided category name and creates it as an asset category when missing.
- Manufacturer remains lookup-only; missing manufacturers still warn and allow model creation without `manufacturer_id`.
- Manual preflight CSV remains separate from final status/report history. Preview writes CSV review files and also saves a dry-run `SyncResult_*.json` history record.
- The no-service manual window now saves run reports directly. WorkerService production DI/scheduler wiring is still not implemented.

Latest verification commands:

```powershell
dotnet build .\AteraSnipeSync.sln
dotnet test .\AteraSnipeSync.sln --no-build
git diff --check
```

Latest known result:

```text
dotnet build succeeded with 0 warnings and 0 errors.
dotnet test passed: 133 passed, 0 failed, 0 skipped.
git diff --check succeeded; Git reported LF-to-CRLF conversion warnings only.
```

Remaining module gaps / next steps:

- WorkerService/DI still needs to register and call the completed pipeline pieces, including Status Store and the Notification pipeline.
- Full TrayApp/WorkerService IPC remains future work; the current window is only a no-service manual validation entry.
- TrayApp status viewer still needs to read and display `SyncStatusSnapshot`.
- Real email, Teams, Slack, and webhook senders remain future extensions.
- Manufacturer creation policy remains future work.

## Previous Update

TrayApp manual sync window now exposes an explicit `Load Config` button.

Code changed:

- Added `Load Config` to `ManualSyncForm` action buttons.
- Clicking `Load Config` reloads reusable manual sync settings from the shared local settings store and applies them to the current form.
- The button is disabled while a connection test, preview, or sync run is active.
- Missing config is reported with a sanitized log message; loaded secrets are never printed.

Documentation changed:

- `docs/technical-specs/08-TrayApp-技术规格.md`
- `docs/test-guides/08-TrayApp-单元测试指导手册.md`
- `progress.md`

Behavior notes:

- Startup auto-load behavior remains unchanged; `Load Config` gives the operator a manual reload after copying or editing `appsettings.local.json`.
- The button reads `C:\ProgramData\AteraSnipeSync\appsettings.local.json` through `LocalAppSettingsStore.GetDefaultFilePath()` in the default app path.
- No Atera or Snipe-IT API integration code, DTOs, wire shapes, auth headers, or pagination behavior were changed.
- No automated UI click test was added; existing local settings tests cover the underlying read/write behavior, and the test guide now documents the manual smoke check.

Latest verification commands:

```powershell
dotnet build .\AteraSnipeSync.sln --no-restore /nr:false
dotnet test .\AteraSnipeSync.sln --no-build /nr:false
git diff --check
```

Latest known result:

```text
dotnet build succeeded with 0 warnings and 0 errors.
dotnet test passed: 130 passed, 0 failed, 0 skipped.
git diff --check succeeded; Git reported LF-to-CRLF conversion warnings only.
```

Remaining module gaps / next steps:

- WorkerService/DI still needs to register and call the completed pipeline pieces, including Status Store and the Notification pipeline.
- Full TrayApp/WorkerService IPC remains future work; the current window is only a no-service manual validation entry.
- TrayApp Load Config should receive a manual UI smoke check in the packaged build on the target server.
- TrayApp status viewer still needs to read and display `SyncStatusSnapshot`.
- Real email, Teams, Slack, and webhook senders remain future extensions.

## Earlier Update

Snipe-IT preview planning now builds shared reference plans before matching assets.

Code changed:

- Changed `SnipeImporter` to validate records first, then plan unique company, category, manufacturer, and model references, then scan assets for MAC/serial/name matches.
- Reference-plan failures now block affected assets before `GET /hardware` asset lookups and are still written to `snipeit-assets-plan.csv`.
- Kept asset match lookups per asset, because MAC/serial/name matching is asset-specific.
- Updated progress messages so preview shows reference planning stages and the later asset matching pass.
- Updated `ImportAsync_WritesBlockedAssetRowsToPreflightCsv_WhenPlanningFails` to verify blocked reference-plan records do not query hardware.

Documentation changed:

- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `progress.md`

Behavior notes:

- There is still no intentional `Task.Delay` in the Snipe-IT preview path.
- This does not invent a Snipe-IT batch endpoint or send 50 concurrent requests at once.
- Repeated reference names now cost one Snipe-IT lookup per unique reference value before the asset pass starts.
- Asset pass no longer contains company/category/manufacturer/model missing-reference decision logic; it applies the already-built reference plan.
- Snipe-IT API endpoint paths, payloads, auth headers, and pagination semantics were not changed.

Latest verification commands:

```powershell
dotnet build .\AteraSnipeSync.sln --no-restore /nr:false
dotnet test .\AteraSnipeSync.sln --no-build /nr:false
git diff --check
```

Latest known result:

```text
dotnet build succeeded with 0 warnings and 0 errors.
dotnet test passed: 130 passed, 0 failed, 0 skipped.
git diff --check succeeded; Git reported LF-to-CRLF conversion warnings only.
```

Remaining module gaps / next steps:

- WorkerService/DI still needs to register and call the completed pipeline pieces, including Status Store and the Notification pipeline.
- Full TrayApp/WorkerService IPC remains future work; the current window is only a no-service manual validation entry.
- TrayApp status viewer still needs to read and display `SyncStatusSnapshot`.
- Real email, Teams, Slack, and webhook senders remain future extensions.

## Current Focus

The current workstream has advanced through Snipe-IT Import manual preflight CSV, Worker Scheduler scheduling support, manual sync request shaping, and Sync Orchestrator pipeline execution.

Module 6 Notification now has the first-version safe stub implementation, tests, and test guide.

Snipe-IT asset matching priority remains:

1. MAC address exact match
2. Serial number exact match
3. Asset name high-similarity match

Assets created or updated by the importer include:

```text
Auto Synced from Atera at {UTC timestamp}
```

Manual sync can generate preflight CSV review files before real writes. Scheduler-triggered sync is unattended and must not generate manual preflight CSV files.

Manual UI semantics are now split:

- `Sync Now` directly runs the real sync pipeline and does not generate CSV
- `Preview Changes` generates temporary CSV only; confirm reruns the real sync pipeline and does not execute from the first CSV snapshot

Sync Orchestrator now provides the shared run boundary that manual, scheduled, worker, and future status/report flows should call.

## Important Safety Rules

Automated tests must never call the real Atera or Snipe-IT APIs.

All Atera and Snipe-IT API tests must use mocked HTTP handlers, fake clients, local fixtures, or sanitized hand-written payloads.

Do not add Python probes for real API verification.

Any real-key validation must be manual-only and owner/operator-run. Manual instructions must include exact commands or UI steps, local-only configuration, expected sanitized output, and cleanup steps. Real API keys must not be printed, logged, committed, or stored in tracked files.

## Documents Completed

### Atera Pull

The Atera Pull module plan/spec/test guide define:

- agent/device as the primary resource
- no standalone customer pull
- broad `AgentInfo` preservation for Atera `AgentQueryDTO` fields
- endpoint `GET /api/v3/agents`
- auth header `X-API-KEY`
- pagination with `page` and `itemsInPage`
- mocked HTTP tests only
- manual real API validation rules

### Reconstruction

The Reconstruction technical spec and tests define:

- `InventoryMapper` converts `AteraPullResult` into `SnipeImportBatch`
- missing company/manufacturer/model fallback behavior
- asset tag fallback behavior
- notes construction
- mapping warnings
- MAC addresses are passed from `AgentInfo.MacAddresses` into `SnipeAssetImportRecord.MacAddresses`

### Snipe-IT Import

The Snipe Import module plan/spec/test guide now define:

- official Snipe-IT OpenAPI sources checked before implementation
- Snipe-IT base URL format: `{SnipeItHost}/api/v1`
- bearer token authentication
- company ensure/create
- category lookup only
- manufacturer lookup only
- model ensure/create
- hardware match by MAC, serial, then name similarity
- hardware create/update
- dry-run write suppression
- HTTP 200 with `status = error` handling
- automatic asset notes line: `Auto Synced from Atera at {UTC timestamp}`
- manual sync preflight CSV files in `C:\ProgramData\AteraSnipeSync\Preflight\{runId}\`
- `snipeit-assets-plan.csv`, `snipeit-companies-plan.csv`, and `snipeit-models-plan.csv`
- CSV write failure blocks all Snipe-IT `POST` / `PATCH`

### Worker Scheduler

The Worker Scheduler module plan/spec/test guide now define:

- daily, weekly, and monthly schedules
- multiple local run times
- configured time zone
- monthly `DaysOfMonth` and `RunOnLastDayOfMonth`
- missing monthly dates are skipped, such as the 31st in February
- overlap prevention
- scheduler-triggered requests set `TriggeredBy = "scheduled"`
- scheduler-triggered requests disable manual preflight CSV

### Sync Orchestrator

The Sync Orchestrator module plan/spec/test guide now define:

- full pipeline order: Atera Pull, Reconstruction, then Snipe Import
- stage short-circuit rules
- warning aggregation rules
- import failure promotion to run-level failures
- dry-run propagation from `SyncRunOptions` to `SnipeImportOptions`
- cancellation behavior
- fake-dependency-only automated tests

### Status Store

The Status Store module plan/spec/test guide now define:

- all-history local persistence through `ISyncStatusStore`
- default history directory `C:\ProgramData\AteraSnipeSync\History`
- per-run file naming with UTC finished timestamps
- `SyncRunResult` to structured `SyncHistoryDocument` mapping
- structured resource change sets for assets, companies, models, manufacturers, and categories
- latest snapshot reconstruction by scanning history files
- latest success time reconstruction from history
- missing, empty, malformed, unsupported, and unreadable history behavior
- temp-file atomic write requirements
- no secrets, raw API payloads, full asset dumps, or dense string blobs in history JSON
- required `JsonFileSyncStatusStore` tests and latest verification result

### Notification

The Notification module plan/spec/test guide now define and verify:

- module boundary for notification request construction, event filtering, and publisher substitution
- first-version no-external-send behavior through `NullNotificationPublisher`
- stable event names for scheduled, manual, manual-preview, and generic sync notifications
- severity mapping for information, warning, error, and critical notifications
- `NotificationConfig.Enabled` and `NotificationConfig.OnEvents` filtering semantics
- safe subject/message content rules that exclude secrets and raw payloads
- implemented classes: `NotificationEventTypes`, `NotificationRequestFactory`, `NotificationEventFilter`, and `NullNotificationPublisher`
- implemented unit tests and offline-only testing rules

### TrayApp

The TrayApp module plan/spec/test guide define:

- Atera API key input through a password field
- local save path `C:\ProgramData\AteraSnipeSync\appsettings.local.json`
- schedule editor requirements for daily / weekly / monthly
- local schedule config save/load under `Sync.Schedule`
- manual sync preflight CSV confirmation UI requirements
- manual `Sync Now` and `Preview Changes` are separate actions
- scheduler automatic sync must not show manual confirmation UI
- no real API calls from the settings UI
- mocked/temporary-file-only automated tests

## Code Completed

### Core Atera

Implemented:

- `AteraClient`
- `AgentInfo`
- `AteraPullException`
- `AteraPullFailureKind`
- `AteraPullOptions`
- `IAteraClock`
- `SystemAteraClock`
- `AteraWarningFactory`

`AteraClient` now:

- calls `GET /api/v3/agents`
- sends `X-API-KEY`
- requests `page` and `itemsInPage=500`
- parses the response envelope
- maps valid records to `AgentInfo`
- preserves broad Atera device fields including MAC/IP arrays and raw JSON
- skips malformed single records with warnings
- retries transient failures
- fails without returning partial results after retry exhaustion or malformed pagination state
- avoids logging API keys

### Core Reconstruction

Implemented:

- `InventoryMapper`
- `AssetTagFactory`
- `MappingValueResolver`
- `NotesBuilder`
- `MappingWarningFactory`

`InventoryMapper` now maps:

- serial
- asset tag
- name
- company/manufacturer/model/category/status
- notes
- source identity
- MAC address list

### Core Snipe-IT Import

Implemented:

- `SnipeImporter`
- `SnipeImportPreflightCsvWriter`
- `SnipeImportPreflightPlan`
- `MacAddressNormalizer`
- `AssetNameMatcher`
- `SnipeApiException`
- `SnipeAssetMatch`

Updated contracts:

- `SnipeAssetImportRecord.MacAddresses`
- `SnipeImportOptions.MacAddressCustomFieldDbColumnName`
- `SnipeImportOptions.NameMatchThreshold`
- `SnipeImportOptions.ManualPreflightCsvEnabled`
- `SnipeImportOptions.ManualPreflightCsvDirectory`

`SnipeImporter` supports:

- `GET /companies?name=...`
- `POST /companies`
- `GET /categories?search=...`
- `GET /manufacturers?name=...`
- `GET /models?search=...&category_id=...`
- `POST /models`
- `GET /hardware?limit=50&filter=...`
- `GET /hardware/byserial/{serial}`
- `GET /hardware?limit=50&search=...`
- `POST /hardware`
- `PATCH /hardware/{id}`

Write behavior:

- plans shared company/category/manufacturer/model references before asset matching
- writes manual preflight CSV when enabled after planning and before real writes
- blocks assets with missing reference plans before hardware matching
- creates missing companies only when enabled and not dry-run
- creates missing models only when enabled and not dry-run
- does not create categories
- does not create manufacturers
- creates hardware when no reliable match exists
- updates hardware when a unique MAC, serial, or high-confidence name match exists
- fails ambiguous MAC/name matches instead of updating an arbitrary asset
- appends `Auto Synced from Atera at {UTC timestamp}` to asset notes on create/update

### Core Worker Scheduler

Implemented:

- `SyncScheduleOptions`
- `ScheduleFrequency`
- `ScheduleCalculator`
- `ScheduledSyncRequestFactory`
- `SyncScheduler`

Scheduler behavior:

- calculates next UTC run from configured local schedule
- supports daily, weekly, monthly, multiple run times, and configured time zone
- skips monthly dates that do not exist in a given month
- prevents overlapping scheduled runs when enabled
- runs scheduler requests with `TriggeredBy = "scheduled"`
- disables `ManualPreflightCsvEnabled` for scheduled requests

### Core Manual Sync Requests

Implemented:

- `ManualSyncRequestFactory`

Manual request behavior:

- `CreateSyncNowRequest` sets `TriggeredBy = "manual"`
- `CreateSyncNowRequest` disables dry-run and manual preflight CSV
- `CreatePreviewChangesRequest` sets `TriggeredBy = "manual-preview"`
- `CreatePreviewChangesRequest` enables dry-run and manual preflight CSV
- preview requests require a non-blank preflight directory

### Core Sync Orchestrator

Implemented:

- `SyncOrchestrator`

`SyncOrchestrator` now:

- calls `IAteraClient.PullInventoryAsync`
- calls `IInventoryMapper.Map`
- calls `ISnipeImporter.ImportAsync`
- preserves successful prior stage output when a later stage fails
- aggregates module warnings
- converts stage exceptions to `SyncFailure`
- converts `SnipeImportResult.Failures` to run-level `SyncFailure`
- applies `SyncRunOptions.DryRun` to importer options
- rethrows cancellation
- uses `TimeProvider` for deterministic timestamps in tests

### Core Status Store

Implemented:

- `JsonFileSyncStatusStore`
- `SyncStatusStoreOptions`
- `SyncHistoryDocument`
- `SyncHistoryRunInfo`
- `SyncHistorySummary`
- `SyncHistoryChangeSet`
- `SyncHistoryItem`
- `SyncHistoryWarning`
- `SyncHistoryFailure`

`JsonFileSyncStatusStore` now:

- writes every completed `SyncRunResult` to a separate structured history JSON file
- uses UTC `FinishedAt` timestamps in `SyncResult_yyyyMMdd_HHmmss_fffffffZ.json` file names
- appends a short GUID suffix when a history file name already exists
- writes through a temp file followed by a non-overwriting move
- maps import actions and import failures into structured resource change sets
- preserves empty `deleted` arrays under the current no-delete policy
- skips malformed or unsupported files during latest-status reads
- computes `LastSuccessAt` by scanning prior valid history files
- avoids persisting Atera raw JSON, mapped asset dumps, API keys, or tokens

### Core Notification

Implemented:

- `NotificationEventTypes`
- `NotificationRequestFactory`
- `NotificationEventFilter`
- `NullNotificationPublisher`

`NotificationRequestFactory` now:

- maps scheduled, manual, manual-preview, and unknown triggers to stable event names
- maps success, warning-only success, normal failure, and auth/credential failure to the required severities
- builds concise subjects for each event type
- builds safe summary messages with timestamps, counts, warning count, and first failure context
- redacts unsafe first-failure message text that appears to contain secrets or raw payloads

`NotificationEventFilter` now:

- suppresses all events when notifications are disabled
- suppresses all events when `OnEvents` is empty
- matches configured events after trim with ordinal ignore-case comparison
- ignores blank configured event names

`NullNotificationPublisher` now:

- validates required request fields
- honors cancellation before logging
- writes a debug log with event type, severity, and subject only
- sends no HTTP, SMTP, Teams, Slack, webhook, Atera, or Snipe-IT traffic

### TrayApp Local Settings

Implemented:

- `LocalAppSettingsStore`
- `SettingsForm`
- `ManualSyncForm`
- local JSON config preservation when saving Atera API key
- schedule config save/load under `Sync.Schedule`
- default manual preflight directory helper
- replacement of default `Form1` with a temporary no-service manual sync validation window
- direct manual validation path for `Preview Changes` and `Sync Now` through the existing Core orchestrator

### Interface Organization

Core interface files have been organized into module-local `Interfaces/` folders while keeping namespaces unchanged:

- `Atera/Interfaces/IAteraClient.cs`
- `Atera/Interfaces/IAteraClock.cs`
- `Mapping/Interfaces/IInventoryMapper.cs`
- `Notifications/Interfaces/INotificationPublisher.cs`
- `Scheduling/Interfaces/ISyncScheduler.cs`
- `SnipeIt/Interfaces/ISnipeImporter.cs`
- `Status/Interfaces/ISyncStatusStore.cs`
- `Sync/Interfaces/ISyncOrchestrator.cs`

## Tests Completed

Automated tests now cover:

- Atera official endpoint/auth/pagination shape
- Atera broad `AgentInfo` field preservation
- Atera retry/auth/malformed/pagination/no API-key logging behavior
- Reconstruction mapping and warnings
- MAC address transfer from Atera mapping into Snipe import records
- Snipe-IT company create
- Snipe-IT model create
- Snipe-IT category missing failure
- Snipe-IT MAC-first asset update
- Snipe-IT serial fallback update
- Snipe-IT high-similarity name fallback update
- Snipe-IT asset create when no match exists
- Snipe-IT dry-run suppresses POST/PATCH writes
- Snipe-IT ambiguous MAC match failure
- Snipe-IT HTTP 200 business error failure
- Snipe-IT automatic notes line on create/update payloads
- manual preflight CSV files are written before Snipe-IT mutations
- manual preflight CSV write failure blocks Snipe-IT mutations
- dry-run plus manual preflight writes CSV but suppresses mutations
- Snipe-IT reference-plan-blocked assets are written to CSV and do not query hardware
- repeated Snipe-IT reference names are planned once per unique reference before asset matching
- scheduler daily/weekly/monthly next-run calculation
- monthly 31st skips months without that date
- scheduler request factory disables manual preflight CSV
- scheduler overlap prevention
- sync orchestrator successful pipeline ordering
- sync orchestrator pull/map/import short-circuit behavior
- sync orchestrator warning aggregation
- sync orchestrator import failure promotion
- sync orchestrator dry-run option propagation
- sync orchestrator cancellation rethrow behavior
- manual sync request factory separates `Sync Now` from `Preview Changes`
- status store per-run history JSON persistence
- status store UTC file naming and conflict-safe file creation
- status store structured resource change sets
- status store malformed history skipping and latest snapshot reconstruction
- status store latest success timestamp scanning
- status store cancellation and constructor validation behavior
- notification request event mapping and subject/severity construction
- notification safe summary counts and first-failure context
- notification secret/raw-payload exclusion from generated messages
- notification event filtering for disabled, empty, unmatched, and case-insensitive configured events
- null notification publisher validation and cancellation behavior
- TrayApp/local settings read/write behavior
- schedule config save/load and invalid schedule rejection
- default manual preflight directory shape

## Not Implemented Yet

Still pending:

- WorkerService wiring for `AteraClient`, `InventoryMapper`, `SnipeImporter`, `SyncOrchestrator`, and `SyncScheduler`
- production DI registration
- encrypted secret storage
- TrayApp Snipe-IT configuration UI
- TrayApp schedule editor UI implementation
- formal TrayApp/WorkerService IPC for manual sync
- production TrayApp manual sync preflight confirmation UI implementation
- TrayApp status viewer
- installer/service deployment scripts
- concrete email, Teams, Slack, and webhook notification senders
- category/manufacturer creation policy
- automatic discovery of Snipe-IT MAC custom field db column

## Verification

Last successful commands:

```powershell
dotnet build .\AteraSnipeSync.sln --no-restore /nr:false
dotnet test .\AteraSnipeSync.sln --no-build /nr:false
git diff --check
```

Last known test result:

```text
Passed: 130
Failed: 0
Skipped: 0
```

## Current Workspace Notes

The working tree contains earlier uncommitted changes from Atera Pull, Reconstruction, TrayApp, Snipe-IT Import, and Scheduler work. Do not revert unrelated modified or deleted files unless explicitly requested.

Notable current implementation files:

- `src/AteraSnipeSync.Core/SnipeIt/SnipeImporter.cs`
- `src/AteraSnipeSync.Core/SnipeIt/SnipeImportPreflightCsvWriter.cs`
- `src/AteraSnipeSync.Core/SnipeIt/SnipeImportPreflightPlan.cs`
- `src/AteraSnipeSync.Core/Scheduling/ScheduleCalculator.cs`
- `src/AteraSnipeSync.Core/Scheduling/ScheduledSyncRequestFactory.cs`
- `src/AteraSnipeSync.Core/Scheduling/SyncScheduler.cs`
- `src/AteraSnipeSync.Core/Sync/SyncOrchestrator.cs`
- `src/AteraSnipeSync.Core/Sync/ManualSyncRequestFactory.cs`
- `src/AteraSnipeSync.Core/Status/JsonFileSyncStatusStore.cs`
- `src/AteraSnipeSync.Core/Status/SyncStatusStoreOptions.cs`
- `src/AteraSnipeSync.Core/Notifications/NotificationRequestFactory.cs`
- `src/AteraSnipeSync.Core/Notifications/NotificationEventFilter.cs`
- `src/AteraSnipeSync.Core/Notifications/NullNotificationPublisher.cs`
- `src/AteraSnipeSync.Core/Configuration/LocalAppSettingsStore.cs`
- `src/AteraSnipeSync.TrayApp/ManualSyncForm.cs`
- `src/AteraSnipeSync.TrayApp/Program.cs`
- `tests/AteraSnipeSync.Tests/SnipeIt/SnipeImporterTests.cs`
- `tests/AteraSnipeSync.Tests/Scheduling/ScheduleCalculatorTests.cs`
- `tests/AteraSnipeSync.Tests/Scheduling/ScheduledSyncRequestFactoryTests.cs`
- `tests/AteraSnipeSync.Tests/Scheduling/SyncSchedulerTests.cs`
- `tests/AteraSnipeSync.Tests/Sync/SyncOrchestratorTests.cs`
- `tests/AteraSnipeSync.Tests/Sync/ManualSyncRequestFactoryTests.cs`
- `tests/AteraSnipeSync.Tests/Status/JsonFileSyncStatusStoreTests.cs`
- `tests/AteraSnipeSync.Tests/Notifications/NotificationRequestFactoryTests.cs`
- `tests/AteraSnipeSync.Tests/Notifications/NotificationEventFilterTests.cs`
- `tests/AteraSnipeSync.Tests/Notifications/NullNotificationPublisherTests.cs`
- `tests/AteraSnipeSync.Tests/Configuration/LocalAppSettingsStoreTests.cs`

`AGENTS.md` now includes the progress documentation requirement: every code/test change must update `progress.md` in the same work session.

## Recommended Next Step

Wire WorkerService/DI so the runtime calls `SyncOrchestrator`, saves the resulting `SyncRunResult` through `JsonFileSyncStatusStore`, and publishes filtered notification requests through the completed notification module.
