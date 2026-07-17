# Project Progress

Last updated: 2026-07-17

## Latest Update

Snipe-IT objects first created by this program now carry an immutable UTC creation marker in their notes.

Code changed:

- Added `First added by Atera-SnipeIT Sync Tool at {UTC timestamp}` to every current create payload: Company, Asset Category, Model, and Hardware Asset.
- Extended the hardware snapshot model to retain Snipe-IT notes so later asset updates preserve the original first-added marker instead of replacing it with the latest sync time.
- Existing assets without the program marker are not backfilled with a guessed creation time; they continue to receive only the normal latest `Auto Synced from Atera at {UTC timestamp}` line.
- Existing Model category/Fieldset updates remain scoped to their existing fields and do not replace Model notes. Manufacturer remains lookup-only and is not created by this program.

Tests changed:

- Added `ImportAsync_AddsFirstAddedNoteToEveryCreatedObject` for the Company, Category, Model, and Hardware POST payloads.
- Extended the asset notes test to verify the original first-added marker survives a later PATCH.
- Added `ImportAsync_DoesNotBackfillFirstAddedNote_WhenUpdatingExistingAssetWithoutMarker`.
- All tests continue to use mocked HTTP handlers; no real Atera or Snipe-IT API was called.

Documentation changed:

- Updated the Snipe Import module responsibilities, technical specification, and unit-test guide with the creation-marker contract, supported object types, timestamp format, preservation behavior, and mocked test coverage.
- Consulted the official Snipe-IT API documentation and official Snipe-IT source. The OpenAPI explicitly documents hardware `notes`; the current official Company, Category, and AssetModel definitions also expose `notes` as writable fields.

Verification:

- `dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~SnipeImporterTests.ImportAsync_AddsFirstAddedNoteToEveryCreatedObject|FullyQualifiedName~SnipeImporterTests.ImportAsync_AddsAutoSyncedNoteToCreateAndUpdatePayloads|FullyQualifiedName~SnipeImporterTests.ImportAsync_DoesNotBackfillFirstAddedNote_WhenUpdatingExistingAssetWithoutMarker"` -> passed 3/3.
- `dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~SnipeImporterTests"` -> passed 73/73.
- `dotnet test AteraSnipeSync.sln --no-restore` -> passed 224/224.
- `dotnet build AteraSnipeSync.sln --no-restore` -> succeeded with 0 warnings and 0 errors.
- Automated tests did not call real APIs.

Remaining gaps / next steps:

- Optional owner/operator smoke test against a backed-up Snipe-IT instance: create one missing Company/Category/Model/Asset and confirm all four notes display the same first-added UTC marker. Keep real API credentials local and out of logs and tracked files.

## Previous Update

Manual Sync progress is now weighted by actual work, so completed model/category snapshots cannot consume the progress reserved for slow per-asset writes.

Code changed:

- Added `ManualSyncProgressCalculator`, a per-run monotonic calculator used only by Preview/Sync progress callbacks. Connection tests and category normalization retain their existing generic progress behavior.
- Real Sync weights are now: Atera pull/mapping 0–5%, source validation 5–10%, model/category/reference preparation 10–15%, per-asset matching/planning 15–30%, reference writes 30–35%, per-asset create/update 35–99%, and whole-run completion 100%.
- Preview weights are: initial work 0–15%, per-asset matching/planning 15–95%, CSV/dry-run finalization 95–99%, and whole-run completion 100%.
- Child `Current/Total` values are evaluated only inside their phase. A completed 485-row hardware/model snapshot therefore remains at or below 15% instead of incorrectly setting the whole run to 95%.
- Percentages never move backwards when callbacks repeat or arrive out of phase. Terminal failure and final whole-run completion can reach 100%; import-stage completion stops at 99%.

Tests changed:

- Added five `ManualSyncProgressCalculatorTests` covering a 485-asset real Sync, snapshot isolation, Preview weighting, monotonic out-of-order callbacks, and terminal failure.
- The 485-asset real-Sync assertions verify 35% at execution start, approximately 67% at 243/485, 99% at 485/485, and 100% only after final run completion.

Documentation changed:

- Updated the TrayApp module responsibilities, technical specification, and unit-test guide with the real-Sync and Preview weight tables and manual acceptance checks.

Verification:

- `dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~ManualSyncProgressCalculatorTests"` -> passed 5/5.
- `dotnet test AteraSnipeSync.sln --no-restore` -> passed 219/219.
- `dotnet build AteraSnipeSync.sln --no-restore` -> succeeded with 0 warnings and 0 errors.
- `git diff --check -- <changed progress code/docs/tests>` -> passed; only existing line-ending conversion notices were reported.

Remaining gaps / next steps:

- Optional owner/operator smoke test with the 485-agent environment: confirm model/category preparation stays at or below 15%, asset execution starts around 35%, and the bar advances through the long-running asset writes. No real API was called by automated tests.

## Previous Update

Manual Sync now keeps every detailed progress entry in the daily file while showing only stable phase milestones in the App log.

Code changed:

- Split TrayApp logging into UI-only, file-only, and shared paths. Every `SyncProgressUpdate` is now queued to `ManualSync_yyyyMMdd.log` with its full safe detail and optional `(Current/Total)` instead of passing through heartbeat/milestone sampling.
- Added `ManualSyncUiStageTracker`; a successful Preview/Sync UI log now emits only `Starting sync.`, `Processing models.`, `Processing categories.`, `Processing assets.`, and `Completed.` once and in order. Agent/asset names, detailed counts, grouped reasons, CSV paths, and history paths remain file-only.
- Changed `DailyLogWriter` from a bounded `DropWrite` channel to a lossless asynchronous unbounded channel. Disposal completes the channel and waits for all accepted entries to flush; writer failure closes the queue so later writes report failure.
- Detailed completion output now writes every warning and grouped failure reason to the file rather than applying the former UI display limits.

Tests changed:

- Added `ManualSyncLoggingTests` covering a 3,000-entry flush (above the former 2,048-entry capacity), suppression of record-specific agent/asset details, ordered/deduplicated UI milestones, and empty-batch milestone completion.
- Linked the two pure TrayApp logging classes into the unit-test assembly; tests use only a temporary local directory and call no external API.

Documentation changed:

- Updated the TrayApp module responsibilities, technical specification, and unit-test guide to define lossless detailed file logging and the five-line Manual Sync UI policy.
- Replaced the obsolete requirement that per-reference progress appear in the UI; reference details remain complete in the daily file.

Verification:

- `dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~ManualSyncLoggingTests"` -> passed 3/3.
- `dotnet build src\AteraSnipeSync.TrayApp\AteraSnipeSync.TrayApp.csproj --no-restore` -> succeeded with 0 warnings and 0 errors.
- `dotnet test AteraSnipeSync.sln --no-restore` -> passed 214/214.
- `dotnet build AteraSnipeSync.sln --no-restore` -> succeeded for Core, TrayApp, Tests, and WorkerService with 0 warnings and 0 errors.
- `git diff --check -- <changed TrayApp logging/docs/tests>` -> passed; only existing line-ending conversion notices were reported.

Remaining gaps / next steps:

- Optional owner/operator smoke test: run Preview Changes and inspect the App log for exactly the five successful milestones, then compare `ManualSync_yyyyMMdd.log` to confirm consecutive per-agent/per-asset progress entries are all present. No real API probe was added to automated tests.

## Earlier Update

The split Computer/Server category design remains withdrawn, while Model normalization is now safely limited to operator-selected source categories.

Code changed:

- Restored the single `MappingOptions.DefaultCategoryName` and `ManualSyncSettings.DefaultCategoryName` contract across Core, TrayApp, WorkerService, scheduled/manual request construction, local settings, and the sample configuration.
- All non-ignored Atera device types, including `DeviceType=Server`, now map to the same default category (`Computer` in UI/sample configuration).
- `DeviceType` remains preserved on import records and as the final preview asset CSV column for audit only; it no longer routes category selection.
- Local settings load the single field first and can temporarily fall back to `DefaultComputerCategoryName` from the withdrawn split design. Saving writes only `DefaultCategoryName` and removes both split fields.
- Added a saved `Normalize From Categories (;)` text field to Manual Sync, defaulting to `Server; Laptop; Desktop`; the value persists as `SnipeIt.ModelCategoriesToNormalize`.
- `Normalize Categories` resolves the existing target Asset Category and paginates the complete `GET /models` snapshot, but only Models whose current category name matches the operator list can become candidates.
- Matching is trim/HTML-decode/case-insensitive. Selected unused Models are included, while Printer, Network Equipment, and every other unlisted inventory category are left unchanged.
- Empty source lists fail before API requests or settings writes. The confirmation dialog and dedicated log include the reviewed source list, scanned count, and candidate count.
- Confirmed execution still updates each candidate through `PUT /models/{id}` with required `name` and `category_id`, continues after independent failures, and never creates/deletes Models, Categories, or Assets.
- Existing preview Model uniqueness/category mismatch gates remain active, so a Server device whose existing Model is still in Server category is blocked before real reference creation until normalization is run.

Tests changed:

- Updated mapping coverage to prove Server, non-Server, and blank DeviceType values all use the single default category while preserving DeviceType.
- Updated Snipe importer preview coverage so `DeviceType=Server` still expects Computer and blocks an existing Server-category Model, with DeviceType retained in the Blocked CSV row.
- Rebuilt normalizer mocked-HTTP coverage around paginated `/models`: default selected categories, unlisted-category skip, custom case-insensitive source lists, empty-list rejection, unused candidates, Computer skip, malformed/conflicting rows, target-category gates, PUT payload, partial failure, no-op, and cancellation.
- Updated local-settings tests for single-category round trip, persisted normalization source lists, empty-list rejection, and fallback migration from the withdrawn split fields.
- All HTTP tests use mocked handlers; no automated test called real Atera or Snipe-IT APIs.

Documentation changed:

- Updated module responsibilities and technical specifications for Reconstruction, SnipeImport, WorkerScheduler, and TrayApp before production code, explicitly superseding the split-category design.
- Updated the corresponding unit-test guides after tests were implemented.
- Rechecked official Snipe-IT `GET /models` pagination and `PUT /models/{id}` required `name`/`category_id` documentation before changing normalizer wire behavior.

Verification commands:

```powershell
dotnet build AteraSnipeSync.sln --no-restore
dotnet test AteraSnipeSync.sln --no-build
git diff --check
```

Latest known result:

```text
Solution build: succeeded with 0 warnings and 0 errors.
Full solution tests: 199 passed, 0 failed, 0 skipped.
Diff check: passed; only LF-to-CRLF conversion notices were reported.
```

Remaining manual-only verification:

- Back up Snipe-IT, load the existing local configuration in TrayApp, confirm `Default Category=Computer` and `Normalize From Categories (;)=Server; Laptop; Desktop`, then save once.
- Run `Normalize Categories`, verify the confirmation includes only those source categories, and audit `PowerEdge R740` plus an unused selected Model.
- Also audit at least one Model in an unlisted category such as Printer and confirm it was not changed.
- Run Preview Changes afterward and confirm Server devices still show `DeviceType=Server` in the final CSV column while their requested category is Computer. Do not run Sync Now until preview has no Model category/name-number conflicts.

## Previous Update

Manual preflight asset CSV now exposes complete MAC and batch-identity conflict evidence.

Code changed:

- Added `MacAddresses`, `ConflictingFields`, `ConflictingValue`, and `ConflictingAssets` columns to `snipeit-assets-plan.csv`.
- `MacAddresses` exports every valid source MAC in normalized colon-separated form, de-duplicated and joined with `; ` for Add, Modify, and Blocked rows.
- Refactored batch identity detection to retain each conflicting field, normalized value, and every peer source record instead of reducing the result to one message string.
- `ConflictingValue` labels each value, for example `MAC address=00:11:22:33:44:55`.
- `ConflictingAssets` identifies each peer as `AssetTag=<tag> | Name=<name> | SourceId=<id>` so duplicate Atera agents and renamed devices remain distinguishable even when they share an asset tag.
- Conflict columns are populated only for `SnipeImport.DuplicateBatchIdentity`; unrelated Blocked rows keep the columns empty while still exposing their MAC list.
- Existing CSV quoting and formula-neutralization behavior applies to the new columns.

Tests changed:

- Added `ImportAsync_WritesMacAndConflictDetailsToPreflightCsv_WhenBatchIdentityCollides`.
- Covered multiple MACs on one asset, normalized alternate MAC formats, asset-tag/MAC/serial collisions, MAC-only multi-peer collisions, peer SourceIds, and write suppression.
- Updated existing CSV row assertions for the expanded column order.
- No automated test called a real Atera or Snipe-IT API.

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `progress.md`

Latest verification commands:

```powershell
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-restore --filter FullyQualifiedName~SnipeImporterTests
dotnet build AteraSnipeSync.sln --no-restore
dotnet test AteraSnipeSync.sln --no-build --no-restore
git diff --check
```

Latest known result:

```text
SnipeImporterTests passed: 44 passed, 0 failed, 0 skipped.
Full solution build succeeded with 0 warnings and 0 errors.
Full solution tests passed: 161 passed, 0 failed, 0 skipped.
git diff --check succeeded; Git reported line-ending conversion warnings only.
```

Operator verification:

- Restart the newly built TrayApp and run Preview Changes again.
- Open `snipeit-assets-plan.csv`; the former MAC-only failures should now name the exact normalized MAC and all colliding Asset Tag/name/SourceId peers.

## Previous Update

TrayApp now writes a dedicated daily error log containing every grouped failure reason for failed Preview and Sync runs.

Code changed:

- Added a second asynchronous `DailyLogWriter` configured for `ManualSync_Error` files under `C:\ProgramData\AteraSnipeSync\Logs`.
- A failed completed run writes one atomic queue entry containing mode, local/UTC timestamps, pull/map/warning/failure counts, every grouped failure reason, and the saved `SyncResult_*.json` history path when available.
- Dedicated error output is not limited to the 10 reasons displayed in the UI and does not include routine progress messages.
- Successful Preview/Sync runs do not add an error block.
- Exceptions raised before a structured run result is available are also written as a standalone sanitized error entry.
- Generalized `DailyLogWriter` with a validated file-name prefix while retaining bounded background writes and 30-day retention.
- TrayApp shutdown now flushes both the normal and dedicated error log writers.

Documentation changed:

- `docs/module-plans/08-TrayApp-功能职责.md`
- `docs/technical-specs/08-TrayApp-技术规格.md`
- `docs/test-guides/08-TrayApp-单元测试指导手册.md`
- `progress.md`

Latest verification commands:

```powershell
dotnet build AteraSnipeSync.sln --no-restore
dotnet test AteraSnipeSync.sln --no-build --no-restore
git diff --check
```

Latest known result:

```text
Full solution build succeeded with 0 warnings and 0 errors.
Full solution tests passed: 160 passed, 0 failed, 0 skipped.
git diff --check succeeded; Git reported line-ending conversion warnings only.
```

Operator verification:

- Restart the newly built TrayApp and run a failed Preview or Sync.
- Confirm `C:\ProgramData\AteraSnipeSync\Logs\ManualSync_Error_yyyyMMdd.log` contains the complete failure block and history JSON path.
- Confirm credentials, raw request payloads, and raw API response bodies are absent.

## Earlier Update

Real sync no longer plans a duplicate Snipe-IT company when the company snapshot returns an HTML-escaped name.

Diagnosis:

- Reviewed `SyncResult_20260715_180323_8913568Z.json` and the matching TrayApp log for the 490-asset real run.
- 75 assets were independently blocked by `SnipeImport.DuplicateBatchIdentity`.
- The other 415 assets completed reference and hardware planning, but the import stopped before all asset writes when `POST /companies` attempted to create `Ktunaxa Kinbasket Child & Family Services`.
- Snipe-IT rejected that create with `name: The name has already been taken`, proving the company already existed even though the company snapshot lookup had planned it as missing.
- The official Snipe-IT API overview states that API output text is automatically escaped and consumers must unescape it. The importer previously indexed escaped snapshot text such as `&amp;` without decoding it, so it did not match the unescaped Atera company name.

Code changed:

- Changed shared Snipe-IT response string parsing to HTML-decode exactly once before trimming and matching API text.
- Existing company ids are now reused when Snipe-IT returns an escaped company name, preventing the invalid duplicate-company create and the resulting shared reference stop.
- Changed TrayApp failure-reason ordering to show the largest grouped occurrence count first. A shared `x415` reference failure now appears before isolated `x1` identity conflicts instead of being hidden beyond the first 10 reasons.

Tests changed:

- Added `ImportAsync_MatchesHtmlEscapedCompanyName_FromCompanySnapshot`.
- Verified a mocked `Ktunaxa Kinbasket Child &amp; Family Services` snapshot row matches the unescaped source name, sends no `POST /companies`, and uses the existing company id in the hardware create payload.
- No automated test or diagnostic probe called the real Snipe-IT API.

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `docs/module-plans/08-TrayApp-功能职责.md`
- `docs/technical-specs/08-TrayApp-技术规格.md`
- `docs/test-guides/08-TrayApp-单元测试指导手册.md`
- `progress.md`

Latest verification commands:

```powershell
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-restore --filter FullyQualifiedName~SnipeImporterTests
dotnet build AteraSnipeSync.sln --no-restore
dotnet test AteraSnipeSync.sln --no-build --no-restore
git diff --check
```

Latest known result:

```text
SnipeImporterTests passed: 43 passed, 0 failed, 0 skipped.
Full solution build succeeded with 0 warnings and 0 errors.
Full solution tests passed: 160 passed, 0 failed, 0 skipped.
```

Remaining operator validation / next steps:

- Run the newly built TrayApp and repeat Preview. `Ktunaxa Kinbasket Child & Family Services` should no longer appear as an `Add` company.
- Review the remaining 75 `DuplicateBatchIdentity` assets; they remain intentionally blocked, while the other 415 assets can proceed independently once all reference creates succeed.
- The next real sync may expose a later model or asset validation rule that Preview cannot validate without writes; the improved Tray log will put the highest-impact root reason first.

## Earlier Update

The future read-only Health Check module has been recorded as a formal roadmap backlog item.

Documentation changed:

- Added `docs/ROADMAP.md` with the module goal, initial duplicate/reference-integrity rules, proposed finding contract, safety boundaries, open design decisions, and backlog exit criteria.
- Linked the roadmap from `docs/PROJECT_PLAN.md` and added Health Check after the current delivery sequence.
- Added the Health Check/Data Integrity extension to `docs/AteraSnipeSync_AI_Agent_Master_Plan.md` without adding it to the first-version completion criteria.

Scope decision:

- The future module is read-only and reports suspected operator mistakes such as duplicate models, categories, companies, manufacturers, custom fields, and invalid reference relationships.
- It must provide the affected record ids and evidence, never choose, merge, delete, rename, or repair records automatically.
- No production code or automated tests changed in this documentation-only update.

Latest verification command:

```powershell
git diff --check
```

Latest known result:

```text
Documentation diff check succeeded; Git reported line-ending conversion warnings only.
```

## Earlier Update

SnipeImport model ambiguity is now isolated to the duplicated model name/category key instead of blocking every asset that uses the shared model snapshot.

Diagnosis:

- Reviewed the operator-provided `snipeit-assets-plan.csv` containing 490 blocked rows.
- 415 rows inherited one `SnipeImport.AmbiguousModelMatch` for duplicate Snipe-IT model name `Dell pro 14` in category id 10, even though none of those 415 mapped records had `ModelName = Dell pro 14`.
- The remaining 75 rows were independently blocked by `SnipeImport.DuplicateBatchIdentity`: 61 MAC-only conflicts, 9 asset-tag/serial conflicts, and 5 asset-tag/MAC/serial conflicts.
- Root cause for the 415-row fan-out was `SnipeModelLookup.Create`: one duplicate lookup key threw while constructing the snapshot index, and the outer reference planner treated the entire model snapshot as failed.

Code changed:

- Changed `SnipeModelLookup` to retain unique model name/category keys and track ambiguous keys separately.
- Changed lookup behavior so `SnipeImport.AmbiguousModelMatch` is thrown only when an asset actually requests the duplicated model name/category key.
- Unrelated unique model keys from the same paginated snapshot now continue through hardware matching and Add/Modify planning.
- Kept batch identity conflict blocking unchanged; those 75 source-data conflicts remain intentionally blocked.

Tests changed:

- Added `ImportAsync_BlocksOnlyAssetsReferencingAmbiguousModelKey` with one unique model and one duplicated model key in the same mocked snapshot.
- Verified the unique model proceeds to hardware planning while only the asset requesting the duplicated key fails.
- Kept all API-facing tests on mocked HTTP handlers only; no real Snipe-IT API call was added.

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `progress.md`

Behavior notes:

- Official Snipe-IT `/models` documentation was consulted before changing the run-local model lookup behavior.
- The change does not choose an arbitrary Snipe-IT model when duplicate ids share the requested normalized name/category key.
- A future asset whose mapped model is exactly the ambiguous key remains blocked until the duplicate Snipe-IT model records are resolved.

Latest verification commands:

```powershell
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-restore --filter FullyQualifiedName~SnipeImporterTests
dotnet build AteraSnipeSync.sln --no-restore
dotnet test AteraSnipeSync.sln --no-build --no-restore
git diff --check
```

Latest known result:

```text
SnipeImporterTests passed: 42 passed, 0 failed, 0 skipped.
Full solution build succeeded with 0 warnings and 0 errors.
Full solution tests passed: 159 passed, 0 failed, 0 skipped.
git diff --check succeeded; Git reported LF-to-CRLF conversion warnings only.
```

Remaining module gaps / next steps:

- Re-run Preview Changes with `_snipeit_mac_address_1`; unrelated model keys should now plan normally.
- Review the remaining `DuplicateBatchIdentity` rows before real sync; shared asset tag/serial/MAC identities require an operator-approved source-data or matching policy decision.
- Resolve duplicate Snipe-IT `Dell pro 14` models in category id 10 if that exact model key must be imported later.

## Earlier Update

Snipe-IT real-sync failures now preserve and classify the detailed API reason instead of collapsing object-shaped validation messages into a generic error.

Code changed:

- Changed `SnipeImporter` to parse the official Snipe-IT `messages` string, object, and array error shapes.
- Added field-level validation output such as `asset_tag: ...` and `status_id: ...`, with HTML decoding, de-duplication, and a 2000-character safety cap.
- Added request context to HTTP-derived failures: operation target, HTTP method, and relative endpoint are now included without logging authorization headers, tokens, request payloads, or raw response JSON.
- Added granular failure codes for validation, authentication, authorization, rate limiting, server failures, network failures, timeouts, and generic HTTP failures.
- Changed reference lookup error handling so a Snipe-IT `status = error` response is surfaced instead of being treated as a missing reference.
- Kept the existing all-assets block when a required company/category/model create fails, but the copied failure now identifies the exact reference target and root API detail.
- Changed the manual TrayApp result log to group Snipe Import failures by their pre-orchestration code/root message, display occurrence count plus up to 3 affected target examples, and show up to 10 distinct root reasons.

Tests changed:

- Added `ImportAsync_ReportsFieldValidationDetails_WhenMessagesIsObject`.
- Added `ImportAsync_ClassifiesAuthenticationFailure_WithRequestContext`.
- Added `ImportAsync_ClassifiesServerFailure_WithResponseDetail`.
- Added `ImportAsync_ReportsReferenceTarget_WhenReferenceCreationFails` for the shared-reference failure path that blocks every dependent asset.
- Kept all Snipe-IT tests on mocked HTTP handlers only; no real Snipe-IT API call was added.

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `docs/module-plans/08-TrayApp-功能职责.md`
- `docs/technical-specs/08-TrayApp-技术规格.md`
- `docs/test-guides/08-TrayApp-单元测试指导手册.md`
- `progress.md`

Behavior notes:

- The official Snipe-IT API overview documents HTTP 200 error envelopes and object-shaped validation messages; that official contract was consulted before implementation.
- Preview does not expose write validation because it intentionally sends no `POST` or `PATCH`. The next real sync will now expose the exact failing reference or asset write and its field-level reason.
- A single failed reference create can still block all assets by design; TrayApp now shows this as one distinct reason with an occurrence count instead of filling the visible list with duplicate asset failures.

Latest verification commands:

```powershell
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-restore --filter FullyQualifiedName~SnipeImporterTests
dotnet build AteraSnipeSync.sln --no-restore
dotnet test AteraSnipeSync.sln --no-build --no-restore
git diff --check
```

Latest known result:

```text
SnipeImporterTests passed: 30 passed, 0 failed, 0 skipped.
Full solution build succeeded with 0 warnings and 0 errors.
Full solution tests passed: 146 passed, 0 failed, 0 skipped.
git diff --check succeeded; Git reported LF-to-CRLF conversion warnings only.
```

Remaining module gaps / next steps:

- Run one operator-controlled real sync to capture the now-detailed Snipe-IT response; do not print or share the API token.
- WorkerService/DI still needs to register and call the completed pipeline pieces, including Status Store and the Notification pipeline.

## Previous Update

SnipeImport model planning now uses one paginated Snipe-IT model snapshot per import run instead of one API search per unique model reference.

Code changed:

- Changed `SnipeImporter` to load `GET /models?limit=500&offset=...` pages once when at least one planned model belongs to an existing category.
- Added a run-local model index keyed by normalized model name + nested Snipe-IT `category.id`, preserving the previous behavior that existing-model matching does not require manufacturer id.
- Changed the model planning loop to perform local lookups after the snapshot is loaded, removing per-model `GET /models?search=...&category_id=...` requests.
- Preserved the existing new-category flow: when every model category will be created in the same run, the model snapshot is skipped and models are planned for creation after their categories.
- Added shared snapshot failure handling so model references under existing categories are blocked consistently and do not continue to hardware matching.

Tests changed:

- Updated Snipe-IT model fixtures to use the official list-response shape with nested `category.id`.
- Added `ImportAsync_LoadsModelSnapshotPagesBeforePlanning` for advancing model offsets and later-page matching.
- Added `ImportAsync_SharesModelSnapshotAcrossDifferentModelReferences` to prove different model names share one `/models` request page.
- Added `ImportAsync_BlocksExistingCategoryModels_WhenModelSnapshotFails` to verify shared failures stop hardware lookup.
- Kept all API-facing tests on mocked HTTP handlers only; no real Snipe-IT API call was added.

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `progress.md`

Behavior notes:

- Official Snipe-IT API documentation/OpenAPI and the official model transformer source were consulted before changing model pagination and response parsing.
- `limit=500` is a page size, not a total limit; pagination advances by the actual number of returned rows until `total` is reached or a page is empty.
- A run with 195 unique model references now pays for model-list pagination once, then resolves those references from memory.
- Company, category, and manufacturer reference lookup behavior is unchanged.

Latest verification commands:

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~SnipeImporterTests"
dotnet build .\AteraSnipeSync.sln --no-restore /nr:false
dotnet test .\AteraSnipeSync.sln --no-build /nr:false
git diff --check
```

Latest known result:

```text
SnipeImporterTests passed: 26 passed, 0 failed, 0 skipped.
Full solution build succeeded with 0 warnings and 0 errors.
Full solution tests passed: 142 passed, 0 failed, 0 skipped.
git diff --check succeeded; Git reported LF-to-CRLF conversion warnings only.
```

Remaining module gaps / next steps:

- Company, category, and manufacturer planning still performs one lookup per unique reference; these can use the same paginated snapshot pattern if they become the next measurable bottleneck.
- Model and hardware snapshots are run-local and are intentionally not persisted across import runs.
- WorkerService/DI still needs to register and call the completed pipeline pieces, including Status Store and the Notification pipeline.

## Previous Update

SnipeImport name fallback no longer updates assets unless the candidate also matches company, category, and model.

Code changed:

- Changed `SnipeAssetMatch` to retain parsed Snipe-IT hardware `company`, `category` / `asset_category`, and `model` names from the paginated hardware snapshot.
- Changed `SnipeImporter` so MAC matching remains first priority and serial matching remains second priority, but high-similarity name fallback now only considers candidates whose company/category/model all match the mapped import record.
- Kept non-matching name candidates as no-match instead of blocked, so same-name phone assets no longer cause computer imports to be planned as `Modify`.
- Added `MacAddressCustomFieldDbColumnName` and `NameMatchThreshold` to `SnipeItConfig`, and added example values to `samples/configs/appsettings.local.example.json`.

Tests changed:

- Added `ImportAsync_CreatesAsset_WhenNameMatchesButReferencesDiffer` to prove same-name Snipe-IT assets with different category/model are treated as no match and result in `POST /hardware`.
- Updated Snipe-IT asset fixtures to include company/category/model metadata for snapshot-based matching.
- Kept all Snipe-IT tests on mocked HTTP handlers only; no real Snipe-IT API call was added.

Documentation changed:

- `docs/technical-specs/03-SnipeImport-æŠ€æœ¯è§„æ ¼.md`
- `docs/test-guides/03-SnipeImport-å•å…ƒæµ‹è¯•æŒ‡å¯¼æ‰‹å†Œ.md`
- `samples/configs/appsettings.local.example.json`
- `progress.md`

Investigation notes:

- MAC matching is disabled when `SnipeImportOptions.MacAddressCustomFieldDbColumnName` is null or whitespace. In that case the importer records `SnipeImport.MacMatchingDisabled` and skips the MAC custom-field index.
- The MAC field value must be the Snipe-IT custom field DB column name, for example `_snipeit_mac_address_5`, not the display label.
- Existing `VUE-00xxx` false positives were allowed because the previous name fallback did not check company/category/model before returning an existing asset.

Latest verification commands:

```powershell
dotnet build .\AteraSnipeSync.sln --no-restore /nr:false
dotnet test .\AteraSnipeSync.sln --no-build /nr:false
git diff --check
```

Latest known result:

```text
Full solution build succeeded with 0 warnings and 0 errors.
Full solution tests passed: 139 passed, 0 failed, 0 skipped.
git diff --check succeeded; Git reported LF-to-CRLF conversion warnings only.
```

Remaining module gaps / next steps:

- Configure the runtime MAC custom-field DB column before real sync runs so MAC matching is enabled.
- Consider surfacing the parsed Snipe-IT candidate company/category/model in future diagnostic output when a name fallback is rejected.
- WorkerService/DI still needs to register and call the completed pipeline pieces, including Status Store and the Notification pipeline.

## Earlier Update

Company alias matching now tolerates visually equivalent whitespace and dash characters in Atera company names before preview/import planning.

Code changed:

- Changed `MappingValueResolver` so company alias source-key comparison still uses the configured Atera company name as the key, but compares through a normalized form that trims, collapses whitespace, treats non-breaking spaces as spaces, and treats common dash-like characters as `-`.
- Kept alias output one-way: matching `Atera company name` still returns the configured canonical Snipe-IT company name. No bidirectional company lookup was added to Snipe Import.
- Changed the TrayApp company alias editor syntax to display and validate aliases as `Atera company=Snipe-IT company` instead of `Atera company => Snipe-IT company`.

Tests changed:

- Added `Map_UsesCompanyAlias_WhenCustomerNameHasEquivalentWhitespaceAndDash` to prove alias keys match Atera customer names containing non-breaking spaces and an en dash.
- Added `ImportAsync_PreviewUsesCompanyAliasMappedFromAteraCompanyName` to run an `InventoryMapper` plus Snipe preview/preflight path using `Moore Equine Veterinary Centre - AR=Moore Equine Veterinary Centre`, verifying no company `Add` row is generated when Snipe-IT already has the alias target.
- Verified the TrayApp alias editor syntax change through a full solution build because the parser lives in the WinForms project rather than the unit-test project.
- Kept all API-facing tests on mocked HTTP handlers only; no real Atera or Snipe-IT API call was added.

Documentation changed:

- `docs/technical-specs/02-Reconstruction-技术规格.md`
- `docs/technical-specs/08-TrayApp-技术规格.md`
- `docs/test-guides/02-Reconstruction-单元测试指导手册.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `docs/test-guides/08-TrayApp-单元测试指导手册.md`
- `progress.md`

Investigation notes:

- The default local config at `C:\ProgramData\AteraSnipeSync\appsettings.local.json` does contain the expected company aliases, including `Moore Equine Veterinary Centre - AR => Moore Equine Veterinary Centre`.
- A focused preview-style test with exact `Atera Company Name => Company Alias SnipeIT Name` already passed before broadening the key comparison, so the issue was not missing bidirectional comparison.
- The likely miss condition was character-level mismatch between the Atera API customer name and the saved config key, despite the names looking identical in the UI.

Latest verification commands:

```powershell
dotnet test .\AteraSnipeSync.sln --filter "FullyQualifiedName~InventoryMapperTests.Map_UsesCompanyAlias_WhenCustomerNameHasEquivalentWhitespaceAndDash|FullyQualifiedName~SnipeImporterTests.ImportAsync_PreviewUsesCompanyAliasMappedFromAteraCompanyName"
dotnet build .\AteraSnipeSync.sln
dotnet test .\AteraSnipeSync.sln
dotnet test .\AteraSnipeSync.sln --no-build
git diff --check
```

Latest known result:

```text
Focused alias tests passed: 2 passed, 0 failed, 0 skipped.
Full solution build succeeded with 0 warnings and 0 errors.
Full solution tests passed: 138 passed, 0 failed, 0 skipped.
git diff --check succeeded; Git reported LF-to-CRLF conversion warnings only.
```

Remaining module gaps / next steps:

- If a future preview still shows a company Add row, compare that CSV company name against the Atera customer name in the asset notes to identify non-equivalent wording, not just punctuation/spacing differences.
- WorkerService/DI still needs to register and call the completed pipeline pieces, including Status Store and the Notification pipeline.
- Full TrayApp/WorkerService IPC remains future work; the current window is only a no-service manual validation entry.
- TrayApp status viewer still needs to read and display `SyncStatusSnapshot`.
- Real email, Teams, Slack, and webhook senders remain future extensions.
- Manufacturer creation policy remains future work.

## Earlier Update

SnipeImport asset matching now uses one paginated Snipe-IT hardware snapshot per import run instead of per-asset MAC/serial/name HTTP lookups.

Code changed:

- Changed `SnipeImporter` to load `GET /hardware?limit=500&offset=...` pages once after reference planning and before matching assets.
- Added an in-memory hardware lookup index for configured MAC custom field values and serial numbers, while preserving match priority: MAC, then serial, then high-confidence name.
- Changed name matching to score against the loaded hardware snapshot instead of per-asset `GET /hardware?search=...`.
- Added `SnipeAssetMatch.CustomFields` parsing for Snipe-IT `custom_fields` objects whose entries expose `field` and `value`.
- Added `SnipeImport.AmbiguousSerialMatch` failure handling when the loaded snapshot contains multiple assets with the same serial.
- Preserved the existing behavior that assets blocked during reference planning do not trigger hardware matching work.

Tests changed:

- Updated `SnipeImporterTests` to queue one shared hardware snapshot response instead of per-asset MAC/serial/name lookup responses.
- Added coverage for multi-page hardware snapshot loading with advancing `offset`.
- Added coverage for ambiguous serial matches.
- Kept automated tests on mocked HTTP handlers only; no real Snipe-IT API call was added.

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `progress.md`

Behavior notes:

- Official Snipe-IT API documentation/OpenAPI was consulted before changing hardware lookup behavior.
- The change targets preview and real sync planning speed: a 489-asset run should now pay for Snipe-IT hardware pagination once, then match locally in memory.
- Reference lookup behavior for companies, categories, manufacturers, and models is unchanged.
- Hardware writes still happen only after planning/preflight gates; dry-run still performs no `POST` or `PATCH`.

Latest verification commands:

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~SnipeImporterTests"
dotnet test .\AteraSnipeSync.sln
git diff --check
```

Latest known result:

```text
SnipeImporterTests passed: 21 passed, 0 failed, 0 skipped.
Full solution tests passed: 136 passed, 0 failed, 0 skipped.
git diff --check succeeded; Git reported LF-to-CRLF conversion warnings only.
```

Remaining module gaps / next steps:

- WorkerService/DI still needs to register and call the completed pipeline pieces, including Status Store and the Notification pipeline.
- Full TrayApp/WorkerService IPC remains future work; the current window is only a no-service manual validation entry.
- TrayApp status viewer still needs to read and display `SyncStatusSnapshot`.
- Real email, Teams, Slack, and webhook senders remain future extensions.
- Manufacturer creation policy remains future work.

## Previous Update

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
- category lookup/create for missing asset categories
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
- `POST /categories`
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
- creates missing asset categories when absent and not dry-run
- creates missing models only when enabled and not dry-run
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
- Snipe-IT missing asset category creation
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
- manufacturer creation policy
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

## 2026-07-15 Codebase Hardening Checkpoint (Paused)

The user requested a pause after adopting a test-stage secret boundary. Current credential behavior:

- `appsettings.local.json` no longer saves `Atera.ApiKey` or `SnipeIt.ApiToken`.
- runtime secrets are read from process/service environment variables `ATERA_API_KEY` and `SNIPEIT_API_TOKEN`.
- WorkerService depends on the read-only `ILocalAppSettingsReader`; it never writes configuration.
- WorkerService rejects legacy plaintext secret fields without modifying the file.
- TrayApp explicit config save removes legacy plaintext fields and preserves non-secret settings.

Implemented in this hardening pass:

- strict Snipe-IT `rows` envelope and row validation, safe pagination offsets, create-id validation, GET-only retry, mutation audit correctness, partial-cancellation result, batch/target identity conflict gates, indexed name fallback, custom-field validation, and CSV formula neutralization
- Atera HTTPS/official-host validation, configurable bounded probe page size/count, `Retry-After` plus exponential retry delay
- warning de-duplication, stable Atera failure codes, correct scheduled notification event/filter behavior, and scheduler exception survival
- atomic/cross-process serialized local settings writes, atomic status writes, history retention options, and asynchronous TrayApp daily logging
- WorkerService composition root and Windows Service hosting instead of the template one-second logger loop
- dependency patch updates, NuGet lock files, `.editorconfig`, `Directory.Build.props`, `global.json`, and Windows CI workflow

Verification completed at the pause point:

```powershell
dotnet build AteraSnipeSync.sln -c Release --no-restore
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj -c Release --no-build
```

Latest result:

```text
Build: succeeded, 0 warnings, 0 errors
Tests: 153 passed, 0 failed, 0 skipped
```

Remaining work is intentionally paused pending user prioritization. Use functional-test-stage standards rather than full production hardening for the next pass.

## 2026-07-15 Manual Sync Local Credential Persistence

At the user's request, the Manual Sync functional-test flow now persists credentials locally so they do not need to be re-entered on every TrayApp launch:

- `Save Config` writes trimmed `Atera.ApiKey` and `SnipeIt.ApiToken` to `C:\ProgramData\AteraSnipeSync\appsettings.local.json` through the existing atomic/cross-process serialized settings writer.
- TrayApp startup and `Load Config` read both credentials from that local file and repopulate the masked fields.
- Manual Sync no longer uses process environment variables as credential overrides.
- UI status/log text now states that credentials were saved to the sensitive local plaintext test file.
- The sample Snipe-IT base URL now includes the required `/api/v1` path.
- WorkerService behavior was intentionally not changed; it still rejects plaintext credentials and expects environment variables. Worker reconciliation remains future work after Manual Sync testing.

Documentation changed:

- `docs/module-plans/08-TrayApp-功能职责.md`
- `docs/technical-specs/08-TrayApp-技术规格.md`
- `docs/test-guides/08-TrayApp-单元测试指导手册.md`
- `samples/configs/appsettings.local.example.json`

Verification command:

```powershell
dotnet test AteraSnipeSync.sln --configuration Release --no-restore
dotnet build AteraSnipeSync.sln --configuration Release --no-restore
git diff --check
```

Latest result:

```text
Build: succeeded, 0 warnings, 0 errors
Tests: 156 passed, 0 failed, 0 skipped
Diff check: passed (line-ending conversion warnings only)
```

## 2026-07-15 Run-Local Snipe-IT Company Snapshot

Company reference validation now avoids one API request per unique company:

- consulted the official Snipe-IT `/companies` reference and OpenAPI response contract before changing integration behavior
- each non-empty import run calls `GET /companies` once without a company-name query
- all returned `{ id, name }` rows are loaded into a normalized in-memory company index shared by every source record in that run
- `total` must equal `rows.Count`; incomplete company snapshots fail with `SnipeImport.IncompleteCompanySnapshot` instead of being misclassified as missing companies
- duplicate normalized company names with different ids fail with `SnipeImport.AmbiguousCompanyMatch`
- missing-company planning and optional `POST /companies` creation behavior are unchanged
- company snapshot request/response failures are stored once and applied to all affected company references; there is no fallback to per-company GET requests

Production code and tests changed:

- `src/AteraSnipeSync.Core/SnipeIt/SnipeImporter.cs`
- `tests/AteraSnipeSync.Tests/SnipeIt/SnipeImporterTests.cs`

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`

Verification commands:

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~SnipeImporterTests"
dotnet test AteraSnipeSync.sln --configuration Release --no-restore
dotnet build AteraSnipeSync.sln --configuration Release --no-restore
```

Latest result:

```text
SnipeImporter tests: 41 passed, 0 failed, 0 skipped
All tests: 158 passed, 0 failed, 0 skipped
Build: succeeded, 0 warnings, 0 errors
```

## 2026-07-15 Configurable Ignored MAC Addresses

Manual, preview, real, and scheduled runs now share a persisted ignored-MAC configuration:

- `SnipeIt.IgnoredMacAddresses` is loaded and saved as a normalized, de-duplicated JSON string array.
- Manual Sync UI adds `Ignore MAC Addresses (; separated)` and reloads saved values.
- ignored MACs remain visible in mapped records and preflight CSV for audit, but no longer create mapping duplicate warnings or `SnipeImport.DuplicateBatchIdentity` blocks.
- source and target MAC matching excludes ignored values, and create/update payloads select the first valid non-ignored MAC for the configured custom field.
- invalid configured MACs fail before Snipe-IT lookup or mutation.
- request copies in Manual Sync, Sync Orchestrator, scheduler, and WorkerRuntimeFactory preserve the same ignored list.

Production code and tests changed across Configuration, Mapping, SnipeImport, TrayApp, WorkerService, manual/scheduled request factories, and their unit tests.

Documentation changed:

- `docs/module-plans/02-Reconstruction-功能职责.md`
- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/module-plans/04-SyncOrchestrator-功能职责.md`
- `docs/module-plans/07-WorkerScheduler-功能职责.md`
- `docs/module-plans/08-TrayApp-功能职责.md`
- corresponding technical specs and unit test guides
- `samples/configs/appsettings.local.example.json`

Verification commands:

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore
dotnet build .\AteraSnipeSync.sln --no-restore
git diff --check
```

Latest result:

```text
Tests: 167 passed, 0 failed, 0 skipped
Build: succeeded, 0 warnings, 0 errors
Diff check: passed (line-ending conversion warnings only)
```

Remaining manual verification: enter `00:09:0F:AA:00:01; 00:09:0F:FE:00:01` in the new field, save/reload config, then run Preview Changes against the operator's real local environment. Real API verification remains manual-only.

## 2026-07-15 Virtual Machine Shared-Serial Identity

Repeated Atera serials no longer block distinct records whose normalized model is exactly `Virtual Machine`:

- Reconstruction detects duplicate normalized serial groups and marks only their VM serials as audit-only; Asset Tags now follow the later global `ATERA-{SourceId}` rule for every mapped asset.
- the original serial remains on the import record and Preview CSV for audit, with `SerialIsReliableIdentity = false`.
- audit-only serials do not participate in mapping/import duplicate gates, target serial matching, target serial mismatch checks, or Snipe-IT create/update payloads.
- Snipe hardware snapshots now include an exact, case-insensitive asset-tag index; asset tag, MAC, and reliable serial matches must resolve to the same target id.
- ambiguous asset tags use `SnipeImport.AmbiguousAssetTagMatch`; conflicting strong identities remain blocked with `SnipeImport.ConflictingStrongIdentityMatch`.
- physical-machine duplicate serial behavior is unchanged.

Official Snipe-IT references consulted before changing integration behavior:

- hardware list (`asset_tag` and `serial` response fields)
- hardware create/update (`asset_tag` required and unique; `serial` optional)
- asset tag documentation (no two assets may share an asset tag)

Production code and tests changed:

- `src/AteraSnipeSync.Core/Mapping/InventoryMapper.cs`
- `src/AteraSnipeSync.Core/Mapping/MappingWarningFactory.cs`
- `src/AteraSnipeSync.Core/SnipeIt/SnipeAssetImportRecord.cs`
- `src/AteraSnipeSync.Core/SnipeIt/SnipeImporter.cs`
- `tests/AteraSnipeSync.Tests/Mapping/InventoryMapperTests.cs`
- `tests/AteraSnipeSync.Tests/SnipeIt/SnipeImporterTests.cs`

Documentation changed:

- `docs/module-plans/02-Reconstruction-功能职责.md`
- `docs/technical-specs/02-Reconstruction-技术规格.md`
- `docs/test-guides/02-Reconstruction-单元测试指导手册.md`
- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`

Verification commands:

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~InventoryMapperTests|FullyQualifiedName~SnipeImporterTests"
dotnet test AteraSnipeSync.sln --no-restore
dotnet build AteraSnipeSync.sln --no-restore
git diff --check
```

Latest result:

```text
Targeted tests: 66 passed, 0 failed, 0 skipped
All tests: 173 passed, 0 failed, 0 skipped
Build: succeeded, 0 warnings, 0 errors
Diff check: passed (line-ending conversion warnings only)
```

## 2026-07-15 Uniform Atera Source Asset Tags

All Reconstruction output now identifies Atera ownership directly in the Snipe-IT Asset Tag:

- every mapped record uses `AssetTag = ATERA-{SourceId}`, regardless of whether a serial is present
- `SourceId` remains the normalized Atera Agent ID when available; a valid serial is retained only as the compatibility fallback when Agent ID is missing
- serial remains an independent audit/matching field and no longer determines Asset Tag
- physical records with the same reliable serial still trigger `DuplicateSerial`, while different SourceIds no longer create an incidental duplicate Asset Tag
- the Virtual Machine shared-serial exception now only changes serial reliability; its Asset Tag already follows the global rule
- legacy Snipe-IT assets whose Asset Tag was the serial can be found through reliable serial matching and PATCHed to the new `ATERA-{SourceId}` value

The official Snipe-IT hardware list/update documentation was rechecked before adding the migration payload test: hardware snapshots return `asset_tag` and `serial`, and update requires a unique `asset_tag` while accepting `serial` separately.

Production code and tests changed:

- `src/AteraSnipeSync.Core/Mapping/AssetTagFactory.cs`
- `src/AteraSnipeSync.Core/Mapping/InventoryMapper.cs`
- `tests/AteraSnipeSync.Tests/Mapping/InventoryMapperTests.cs`
- `tests/AteraSnipeSync.Tests/Mapping/ReconstructionBoundaryTests.cs`
- `tests/AteraSnipeSync.Tests/SnipeIt/SnipeImporterTests.cs`

Documentation changed:

- `docs/AteraSnipeSync_AI_Agent_Master_Plan.md`
- `docs/module-plans/02-Reconstruction-功能职责.md`
- `docs/technical-specs/02-Reconstruction-技术规格.md`
- `docs/test-guides/02-Reconstruction-单元测试指导手册.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`

Verification commands:

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~InventoryMapperTests|FullyQualifiedName~ReconstructionBoundaryTests|FullyQualifiedName~SnipeImporterTests"
dotnet test AteraSnipeSync.sln --no-restore
dotnet build AteraSnipeSync.sln --no-restore
git diff --check
```

Latest result:

```text
Targeted tests: 69 passed, 0 failed, 0 skipped
All tests: 175 passed, 0 failed, 0 skipped
Build: succeeded, 0 warnings, 0 errors
Diff check: passed (line-ending conversion warnings only)
```

Remaining manual verification: run Preview Changes and confirm every planned Asset Tag begins with `ATERA-`; then review any legacy-tag rows planned as Modify before real Sync. Real API verification remains manual-only.
## 2026-07-15 Laptop/Desktop Model Category Normalization

Superseded on 2026-07-16 by the complete `/models` normalization described in Latest Update; the historical details below record the earlier limited design.

TrayApp now provides an operator-confirmed maintenance button for consolidating historical Snipe-IT Model categories:

- `Normalize Categories` reads the current Snipe-IT URL/token and program `Default Category` (default `Computer`).
- Core paginates the complete hardware snapshot, selects only actually referenced Models whose current category is exactly Laptop or Desktop (case-insensitive), deduplicates by Model ID, and counts affected assets.
- the existing target category is resolved from the complete category snapshot; missing, ambiguous, non-asset, inconsistent, or malformed snapshots stop before any write.
- after the UI confirmation, each distinct Model is updated once through documented `PUT /models/{id}` with the required `name` and `category_id`; no Asset/Model/Category is created or deleted.
- independent Model failures are classified and logged while later Models continue; cancellation is checked before the next PUT and a PUT already sent waits for a definite response.
- every click produces a dedicated `ModelCategoryNormalization_yyyyMMdd_HHmmss_fffffff.log` with scan/plan/outcome/summary details. If that audit log cannot be created during planning, mutation is blocked.

Official Snipe-IT documentation/source checked before implementation:

- hardware list `GET /hardware` and category list `GET /categories` pagination
- model update `PUT /models/{id}` requiring `name` and `category_id`
- official `AssetModelsController.update`, confirming `fill($request->all())` changes only submitted fields so optional Model fields are preserved when omitted

Production code changed:

- `src/AteraSnipeSync.Core/SnipeIt/SnipeModelCategoryNormalization.cs`
- `src/AteraSnipeSync.Core/SnipeIt/SnipeModelCategoryNormalizer.cs`
- `src/AteraSnipeSync.TrayApp/ModelCategoryNormalizationLog.cs`
- `src/AteraSnipeSync.TrayApp/ManualSyncForm.cs`

Tests changed:

- `tests/AteraSnipeSync.Tests/SnipeIt/SnipeModelCategoryNormalizerTests.cs`

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `docs/module-plans/08-TrayApp-功能职责.md`
- `docs/technical-specs/08-TrayApp-技术规格.md`
- `docs/test-guides/08-TrayApp-单元测试指导手册.md`

Verification commands:

```powershell
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~SnipeModelCategoryNormalizerTests"
dotnet test AteraSnipeSync.sln --no-restore
dotnet build AteraSnipeSync.sln --no-restore
git diff --check
```

Latest result:

```text
Targeted normalization tests: 11 passed, 0 failed, 0 skipped
All tests: 186 passed, 0 failed, 0 skipped
Build: succeeded, 0 warnings, 0 errors
Diff check: passed (line-ending conversion warnings only)
```

Remaining manual-only verification: back up Snipe-IT, run the new button, review the confirmation counts, then verify the dedicated log and a sample of updated Models. No automated test calls the real Snipe-IT API.
## 2026-07-15 Accurate Real-Sync Reference Progress

The real Snipe-IT reference creation phase no longer displays the mapped asset count as a misleading reference denominator:

- planned Company, Category, and Model creates are materialized and counted before mutations
- the initial progress line reports the real total plus per-type counts
- every reference POST reports its 1-based position, type, safe name, success duration, or failure code/duration
- zero missing references reports an explicit no-op
- Company -> Category -> Model ordering, endpoints, payloads, non-replay mutation behavior, and cancellation semantics are unchanged
- TrayApp bypasses normal heartbeat/milestone throttling for these controlled reference progress messages, so every creating/created/failed line is retained in the UI and daily Manual Sync log

Official Snipe-IT documentation rechecked before the change:

- API overview, including HTTP 200 bodies with `status=error` and structured `messages`
- Company create `POST /companies`
- Category create `POST /categories`
- Model create `POST /models`

Production code changed:

- `src/AteraSnipeSync.Core/SnipeIt/SnipeImporter.cs`
- `src/AteraSnipeSync.TrayApp/ManualSyncForm.cs`

Tests changed:

- `tests/AteraSnipeSync.Tests/SnipeIt/SnipeImporterTests.cs`
  - all-missing reference case asserts total=3 and Company/Category/Model positions 1/2/3 with durations
  - shared failing Company case asserts reference total=1 rather than two dependent assets and retains the validation code
  - no-missing reference case asserts an explicit total=0 no-op

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `docs/module-plans/08-TrayApp-功能职责.md`
- `docs/technical-specs/08-TrayApp-技术规格.md`
- `docs/test-guides/08-TrayApp-单元测试指导手册.md`

Verification commands:

```powershell
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~SnipeImporterTests"
dotnet test AteraSnipeSync.sln --no-restore
dotnet build AteraSnipeSync.sln --no-restore
git diff --check
```

Latest result:

```text
Targeted SnipeImporter tests: 52 passed, 0 failed, 0 skipped
All tests: 187 passed, 0 failed, 0 skipped
Build: succeeded, 0 warnings, 0 errors
Diff check: passed (line-ending conversion warnings only)
```

Remaining manual verification: run a real Sync Now containing missing references and confirm the UI/daily log shows the real reference total plus every per-reference duration. Automated tests still never call the real Snipe-IT API.

## 2026-07-16 Unified Default-Category Model MAC Fieldset Preparation

Preview Changes, Sync Now, and scheduled sync now share one upfront Model preparation phase:

- the GUI has an editable `MAC Fieldset Name`; the name is operator configuration rather than a hardcoded Snipe-IT default
- MAC custom-field DB column and Fieldset name are persisted and validated as a pair
- the complete `GET /models` snapshot is inspected once before per-asset matching, and `GET /fieldsets` is resolved once when MAC Fieldset reconciliation is enabled
- MAC Fieldset reconciliation applies only to all Models already in the configured `Default Category`, plus Models being normalized from the configured source category list into that target; other categories are untouched
- runs containing only other-category Models skip Fieldset resolution entirely
- new Models created in the default category include `fieldset_id`
- existing Models missing the target Fieldset are planned as Model `Modify`; category normalization and Fieldset assignment are combined into one `PATCH /models/{id}`
- Model creates/updates execute before any Asset create/update
- Preview writes the same Add/Modify Model plan without mutations and reports `UpdatedModels`
- the standalone Normalize Categories UI button was removed from the normal action panel so category behavior does not diverge from Preview/Sync

Official Snipe-IT documentation/source checked before implementation:

- `GET /fieldsets`
- `POST /models` with `fieldset_id`
- `PATCH /models/{id}` with partial `category_id` / `fieldset_id`
- Model response transformer output containing nested `fieldset.id/name`

Production code changed:

- `src/AteraSnipeSync.Core/Configuration/LocalAppSettingsStore.cs`
- `src/AteraSnipeSync.Core/Configuration/ManualSyncSettings.cs`
- `src/AteraSnipeSync.Core/Configuration/SnipeItConfig.cs`
- `src/AteraSnipeSync.Core/SnipeIt/SnipeImportOptions.cs`
- `src/AteraSnipeSync.Core/SnipeIt/SnipeImportPreflightCsvWriter.cs`
- `src/AteraSnipeSync.Core/SnipeIt/SnipeImportPreflightPlan.cs`
- `src/AteraSnipeSync.Core/SnipeIt/SnipeImportResult.cs`
- `src/AteraSnipeSync.Core/SnipeIt/SnipeImporter.cs`
- `src/AteraSnipeSync.Core/Status/JsonFileSyncStatusStore.cs`
- `src/AteraSnipeSync.Core/Sync/ManualSyncRequestFactory.cs`
- `src/AteraSnipeSync.Core/Sync/SyncOrchestrator.cs`
- `src/AteraSnipeSync.Core/Scheduling/ScheduledSyncRequestFactory.cs`
- `src/AteraSnipeSync.TrayApp/ManualSyncForm.cs`
- `src/AteraSnipeSync.WorkerService/Worker.cs`
- `src/AteraSnipeSync.WorkerService/WorkerRuntimeFactory.cs`

Tests changed:

- `tests/AteraSnipeSync.Tests/Configuration/LocalAppSettingsStoreTests.cs`
- `tests/AteraSnipeSync.Tests/ContractCompilationTests.cs`
- `tests/AteraSnipeSync.Tests/Scheduling/ScheduledSyncRequestFactoryTests.cs`
- `tests/AteraSnipeSync.Tests/SnipeIt/SnipeImporterTests.cs`
- `tests/AteraSnipeSync.Tests/Status/JsonFileSyncStatusStoreTests.cs`
- `tests/AteraSnipeSync.Tests/Sync/ManualSyncRequestFactoryTests.cs`
- `tests/AteraSnipeSync.Tests/Sync/SyncOrchestratorTests.cs`

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`
- `docs/module-plans/08-TrayApp-功能职责.md`
- `docs/technical-specs/08-TrayApp-技术规格.md`
- `docs/test-guides/08-TrayApp-单元测试指导手册.md`
- `docs/test-guides/04-SyncOrchestrator-单元测试指导手册.md`
- `docs/test-guides/07-WorkerScheduler-单元测试指导手册.md`

Verification commands:

```powershell
dotnet test .\AteraSnipeSync.sln --no-restore /nr:false
dotnet build .\AteraSnipeSync.sln --no-restore /nr:false
git diff --check
```

Latest known result:

```text
All tests: 204 passed, 0 failed, 0 skipped
Build: succeeded, 0 warnings, 0 errors
Diff check: passed (line-ending conversion warnings only)
```

Remaining manual-only verification: enter the actual Snipe-IT Fieldset name in the GUI, run Preview Changes, confirm only default-category/normalizing Models appear as Modify, then run Sync Now and verify Model mutations complete before Asset writes. Automated tests use mocked HTTP only and never call the real Snipe-IT API.

## 2026-07-16 Unique Model Number Reuse With Manufacturer/Category Guard

Model resolution now safely reuses an existing Snipe-IT Model when the Atera model name does not match an existing Model name:

- exact normalized Model name remains the first-priority lookup
- when name has no match, an exact `model_number` match is considered
- reuse is allowed only when exactly one Model id has that model number, its `manufacturer.id` equals the resolved source manufacturer id, and its current `category.id` equals the requested category id
- multiple Model ids with the same model number remain blocked even if one candidate has matching manufacturer/category
- missing or mismatched manufacturer/category remains `SnipeImport.ModelNameConflict`
- successful fallback reuse supplies the existing Model id to the normal Asset plan and does not create a Model
- the complete existing `GET /models` snapshot supplies model number, nested manufacturer, and nested category data; no per-asset or per-model API request was added

Official Snipe-IT documentation/source checked before implementation:

- current `/models` API documentation for the paginated Model list and `model_number`
- official `AssetModelsTransformer`, confirming nested `manufacturer { id, name }` and `category { id, name }` in Model rows

Production code changed:

- `src/AteraSnipeSync.Core/SnipeIt/SnipeImporter.cs`

Tests changed:

- `tests/AteraSnipeSync.Tests/SnipeIt/SnipeImporterTests.cs`
  - unique model number + manufacturer/category match reuses the existing Model id
  - manufacturer mismatch blocks
  - category mismatch blocks
  - multiple model-number matches block without selecting the otherwise matching candidate

Documentation changed:

- `docs/module-plans/03-SnipeImport-功能职责.md`
- `docs/technical-specs/03-SnipeImport-技术规格.md`
- `docs/test-guides/03-SnipeImport-单元测试指导手册.md`

Verification commands:

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~SnipeImporterTests" /nr:false
dotnet build .\AteraSnipeSync.sln --no-restore /nr:false
dotnet test .\AteraSnipeSync.sln --no-build /nr:false
git diff --check
```

Latest known result:

```text
Targeted SnipeImporter tests: 66 passed, 0 failed, 0 skipped
All tests: 207 passed, 0 failed, 0 skipped
Build: succeeded, 0 warnings, 0 errors
Diff check: passed (line-ending conversion warnings only)
```

Remaining manual-only verification: rerun Preview Changes against the same environment. The fifteen unique model-number conflicts should now plan normally; the duplicate `PowerEdge R640` model-number match must remain blocked. Automated tests use mocked HTTP only and never call the real Snipe-IT API.

## 2026-07-16 Manufacturer Alias Mapping for Shared Preview/Sync

Implemented deterministic manufacturer aliases by reusing the existing company-alias pattern instead of fuzzy auto-matching.

Production code changed:

- `MappingOptions` and `ManualSyncSettings` now expose `ManufacturerAliases` with an empty-dictionary default.
- `MappingValueResolver` now applies one-way manufacturer aliases after resolving the Atera manufacturer or configured fallback. Alias-key comparison trims, ignores case, collapses whitespace, treats NBSP as whitespace, and normalizes common dash-like characters; no similarity score can trigger automatic reuse.
- `LocalAppSettingsStore` loads/saves `Mapping.ManufacturerAliases` for manual and Worker runs.
- `ManualSyncForm` adds a `Manufacturer Aliases` multiline field using `Atera manufacturer=Snipe-IT manufacturer`, persists it, and passes it into the shared `MappingOptions` used by Preview Changes and Sync Now.
- `WorkerRuntimeFactory` passes the same aliases into unattended mapping.
- the sample local configuration includes `Dell Inc.=Dell`.
- unique model-number fallback now treats a matching planned category-normalization update as the Model's effective category. This includes a default category being created in the same run, provided the Model update and record share the same planned category object.

Tests changed:

- mapper tests cover `Dell Inc. -> Dell` and equivalent whitespace/dash alias keys.
- local settings tests cover manufacturer alias JSON save/load round-trip.
- a mocked Preview integration test maps `Dell Inc.` to `Dell` and verifies canonical manufacturer lookup plus unique model-number reuse without a Model Add.
- a mocked Preview test verifies unique model-number reuse when the existing Model is planned from `Laptop` to a same-run-created `Computer` category; unplanned category mismatch and multiple model-number tests remain fail-closed.

Documentation changed:

- module plans: Reconstruction, WorkerScheduler, TrayApp
- technical specs: Reconstruction, WorkerScheduler, TrayApp
- test guides: Reconstruction, SnipeImport, WorkerScheduler, TrayApp

Verification commands:

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~InventoryMapperTests.Map_UsesManufacturerAlias|FullyQualifiedName~LocalAppSettingsStoreTests.SaveManualSyncSettingsAsync_SavesManualPanelConfig|FullyQualifiedName~LocalAppSettingsStoreTests.LoadManualSyncSettingsAsync_ReturnsSavedManualPanelConfig|FullyQualifiedName~SnipeImporterTests.ImportAsync_PreviewUsesManufacturerAliasForUniqueModelNumberReuse"
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~SnipeImporterTests.ImportAsync_ReusesUniqueModelNumber_WhenCategoryWillBeNormalizedToPlannedCategory|FullyQualifiedName~SnipeImporterTests.ImportAsync_PreviewUsesManufacturerAliasForUniqueModelNumberReuse|FullyQualifiedName~SnipeImporterTests.ImportAsync_BlocksUniqueModelNumber_WhenCategoryDoesNotMatch|FullyQualifiedName~SnipeImporterTests.ImportAsync_BlocksModelNumber_WhenMultipleModelsMatch"
dotnet build .\AteraSnipeSync.sln
dotnet test .\AteraSnipeSync.sln --no-build
git diff --check
```

Latest known result:

```text
Focused manufacturer alias tests: 5 passed, 0 failed, 0 skipped
Focused effective-category/model-number tests: 4 passed, 0 failed, 0 skipped
All tests: 211 passed, 0 failed, 0 skipped
Build: succeeded, 0 warnings, 0 errors
```

Remaining manual-only verification: enter `Dell Inc.=Dell` in the new GUI field, save/reload, and rerun Preview against the rolled-back test database. Manufacturer aliasing removes the unresolved `Dell Inc.` guard, and a matching category-normalization plan now satisfies the category guard even when the default category is created in the same run. Multiple model-number matches remain intentionally blocked. Automated tests use mocked HTTP only and never call real APIs.

## 2026-07-17 Company and Manufacturer Direct-Match-First Alias Resolution

Corrected alias precedence so an operator alias is a fallback, not an unconditional replacement.

Production code changed:

- `SnipeAssetImportRecord` now carries `CompanyAliasName` and `ManufacturerAliasName` separately from the preserved source names.
- `MappingValueResolver` and `InventoryMapper` retain the Atera/default company and manufacturer names while producing optional deterministic alias candidates.
- company planning checks the complete in-memory Snipe company snapshot first; a direct source-name match is retained and the alias is considered only when the source is absent.
- manufacturer planning performs one exact source lookup per unique source manufacturer, then performs the alias lookup only after a not-found result. The selected effective name/id is applied once before Model planning, so Preview and Sync Now share the same decision and do not repeat it per asset.
- if both manufacturer lookups are missing, the existing lookup-only behavior remains: Model planning may continue without a manufacturer binding and exactly one `SnipeImport.ManufacturerMissing` warning names the final alias candidate.
- no API endpoint, request payload field, response DTO, authentication, or pagination shape changed in this correction.

Tests changed:

- mapper tests verify source names remain in `CompanyName` / `ManufacturerName` and aliases are carried separately.
- company Preview tests cover direct-source precedence, alias-target reuse, and alias-target create planning.
- manufacturer Preview tests cover direct-source precedence with one lookup, source-miss alias fallback, both-missing single-warning behavior, and existing unique model-number reuse.
- multiple model-number matches remain fail-closed and are not narrowed by aliases.

Documentation changed:

- module plans and technical specs for Reconstruction and Snipe Import.
- unit test guides for Reconstruction and Snipe Import.

Verification commands:

```powershell
dotnet build AteraSnipeSync.sln --no-restore
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --filter "FullyQualifiedName~InventoryMapperTests|FullyQualifiedName~ImportAsync_PreviewUsesCompanyAliasMappedFromAteraCompanyName|FullyQualifiedName~ImportAsync_PreviewPrefersDirectCompanyMatchOverConfiguredAlias|FullyQualifiedName~ImportAsync_PreviewCreatesAliasTarget_WhenSourceAndAliasAreMissing|FullyQualifiedName~ImportAsync_PreviewUsesManufacturerAliasForUniqueModelNumberReuse|FullyQualifiedName~ImportAsync_PreviewPrefersDirectManufacturerMatchOverConfiguredAlias"
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-build --filter "FullyQualifiedName~SnipeImporterTests"
dotnet build tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --no-incremental
dotnet test AteraSnipeSync.sln --no-build
git diff --check
```

Latest known result:

```text
Focused alias tests: 26 passed, 0 failed, 0 skipped
SnipeImporter tests before the final single-warning addition: 71 passed, 0 failed, 0 skipped
All tests including the final addition: 225 passed, 0 failed, 0 skipped
Build: succeeded, 0 warnings, 0 errors
Diff check: passed (line-ending conversion warnings only)
```

Remaining manual-only verification: rerun Preview Changes against the rolled-back test database with an alias whose source name also exists in Snipe-IT. The Preview CSV must retain the source company/manufacturer and must not plan the alias target. Then test a missing source name to confirm the alias fallback still resolves. Automated tests use mocked HTTP only and never call real APIs.
