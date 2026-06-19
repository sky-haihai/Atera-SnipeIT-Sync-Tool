# Project Progress

Last updated: 2026-06-19

## Latest Update

Module 5 Status Store history documentation has been revised.

Documented behavior:

- `ISyncStatusStore.SaveAsync` writes every completed `SyncRunResult` as a separate history JSON file
- history files are named `SyncResult_yyyyMMdd_HHmmss_fffffffZ.json` using UTC finished timestamps
- history JSON uses structured sections for run metadata, summary, assets, companies, models, manufacturers, categories, warnings, and failures
- assets/companies/models/etc. expose `created`, `updated`, `deleted`, `skipped`, and `failed` arrays for future TrayApp parsing
- current no-delete policy keeps deleted arrays present but empty
- `ISyncStatusStore.ReadLatestAsync` reconstructs latest status by scanning history files
- malformed newest history files are skipped so an older valid file can still be shown
- status JSON must not persist API keys, tokens, raw API payloads, or dense string blobs

Documented in:

- `docs/module-plans/05-StatusStore-功能职责.md`
- `docs/technical-specs/05-StatusStore-技术规格.md`
- `docs/test-guides/05-StatusStore-单元测试指导手册.md`

Implementation status:

- Status Store production implementation is still pending.
- Status Store unit tests are still pending.
- The Status Store unit test guide intentionally remains a pending guide until code/tests exist.

Latest verification:

```text
Not run. Documentation-only update.
```

## Current Focus

The current workstream has advanced through Snipe-IT Import manual preflight CSV, Worker Scheduler scheduling support, manual sync request shaping, and Sync Orchestrator pipeline execution.

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

The Status Store module plan/spec now define:

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
- required future `JsonFileSyncStatusStore` tests

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

- plans lookups first, then writes manual preflight CSV when enabled, then executes real writes
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

### TrayApp Local Settings

Implemented:

- `LocalAppSettingsStore`
- `SettingsForm`
- local JSON config preservation when saving Atera API key
- schedule config save/load under `Sync.Schedule`
- default manual preflight directory helper
- replacement of default `Form1` with `SettingsForm`

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
- TrayApp/local settings read/write behavior
- schedule config save/load and invalid schedule rejection
- default manual preflight directory shape

## Not Implemented Yet

Still pending:

- Status Store implementation
- WorkerService wiring for `AteraClient`, `InventoryMapper`, `SnipeImporter`, `SyncOrchestrator`, and `SyncScheduler`
- production DI registration
- real manual validation runner/tooling
- encrypted secret storage
- TrayApp Snipe-IT configuration UI
- TrayApp schedule editor UI implementation
- TrayApp manual sync preflight confirmation UI implementation
- TrayApp status viewer
- Notification implementation
- installer/service deployment scripts
- category/manufacturer creation policy
- automatic discovery of Snipe-IT MAC custom field db column

## Verification

Last successful commands:

```powershell
dotnet build
dotnet test
```

Last known test result:

```text
Passed: 72
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
- `src/AteraSnipeSync.Core/Configuration/LocalAppSettingsStore.cs`
- `tests/AteraSnipeSync.Tests/SnipeIt/SnipeImporterTests.cs`
- `tests/AteraSnipeSync.Tests/Scheduling/ScheduleCalculatorTests.cs`
- `tests/AteraSnipeSync.Tests/Scheduling/ScheduledSyncRequestFactoryTests.cs`
- `tests/AteraSnipeSync.Tests/Scheduling/SyncSchedulerTests.cs`
- `tests/AteraSnipeSync.Tests/Sync/SyncOrchestratorTests.cs`
- `tests/AteraSnipeSync.Tests/Sync/ManualSyncRequestFactoryTests.cs`
- `tests/AteraSnipeSync.Tests/Configuration/LocalAppSettingsStoreTests.cs`

`AGENTS.md` now includes the progress documentation requirement: every code/test change must update `progress.md` in the same work session.

## Recommended Next Step

Continue with Status Store and WorkerService/DI wiring:

```text
Atera Pull -> Reconstruction -> Snipe-IT Import -> Status/Report/Notification
```

WorkerService should call `SyncOrchestrator` through DI, then save the resulting `SyncRunResult` through Status Store once that module is implemented.
