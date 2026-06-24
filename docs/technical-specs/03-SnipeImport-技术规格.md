# Snipe Import - 技术规格

## 1. 目标

Snipe Import Module 实现 `ISnipeImporter`，把 `SnipeImportBatch` 导入 Snipe-IT。

本规格基于：

- `docs/module-plans/03-SnipeImport-功能职责.md`
- Snipe-IT 官方 OpenAPI `https://snipe-it.readme.io/openapi/snipe-it-rest-api.json`

本模块可以调用 Snipe-IT API，但 automated tests 必须使用 mocked HTTP handler，不能调用真实 Snipe-IT。

## 2. 需要更新的现有 Contracts

### 2.1 `SnipeAssetImportRecord`

文件：

```text
src/AteraSnipeSync.Core/SnipeIt/SnipeAssetImportRecord.cs
```

新增属性：

```csharp
public IReadOnlyList<string> MacAddresses { get; init; } = [];
```

职责：

- 保存 Reconstruction Module 从 Atera `AgentInfo.MacAddresses` 映射出的 MAC address list
- Snipe Import Module 使用该 list 作为第一优先级比较键

### 2.2 `SnipeImportOptions`

文件：

```text
src/AteraSnipeSync.Core/SnipeIt/SnipeImportOptions.cs
```

新增属性：

```csharp
public string? MacAddressCustomFieldDbColumnName { get; init; }
public double NameMatchThreshold { get; init; } = 0.92;
public bool ManualPreflightCsvEnabled { get; init; }
public string? ManualPreflightCsvDirectory { get; init; }
```

规则：

- `MacAddressCustomFieldDbColumnName` 为空时跳过 MAC comparison，并产生 warning
- `NameMatchThreshold` 有效范围为 `0.0 < value <= 1.0`
- `ManualPreflightCsvEnabled = true` 时，必须先输出手动 `Preview Changes` 临时 CSV write plan
- `ManualPreflightCsvDirectory` 在 manual preflight CSV 流程下必填
- `ManualPreflightCsvDirectory` 必须是本机可写目录；无法创建或写入时，本次 import 必须失败且不得执行任何 `POST` / `PATCH`
- `ManualPreflightCsvEnabled` 只用于手动 `Preview Changes`；`Sync Now` 和后台 scheduler 自动 sync 不应启用该选项

### 2.3 `InventoryMapper`

文件：

```text
src/AteraSnipeSync.Core/Mapping/InventoryMapper.cs
```

生成 `SnipeAssetImportRecord` 时必须把 `agent.MacAddresses` 转入 `MacAddresses`。

## 3. 新增 Production 类

Production code 位于：

```text
src/AteraSnipeSync.Core/SnipeIt/
```

### 3.1 `SnipeImporter`

Namespace:

```csharp
namespace AteraSnipeSync.Core.SnipeIt;
```

Signature:

```csharp
public sealed class SnipeImporter : ISnipeImporter
{
    public SnipeImporter(HttpClient httpClient, ILogger<SnipeImporter> logger);
    public SnipeImporter(HttpClient httpClient, ILogger<SnipeImporter> logger, TimeProvider timeProvider);

    public Task<SnipeImportResult> ImportAsync(
        SnipeImportBatch batch,
        SnipeImportOptions options,
        CancellationToken cancellationToken,
        IProgress<SyncProgressUpdate>? progress = null);
}
```

职责：

- validate inputs
- process each `SnipeAssetImportRecord`
- ensure company
- resolve category
- resolve manufacturer
- ensure model
- find matching asset by MAC, then serial, then high-similarity name
- plan unique company/category/manufacturer/model references before asset matching
- create or update asset
- append `Auto Synced from Atera at {UTC timestamp}` to asset notes when create/update payload is built
- when manual preflight CSV is enabled, write all planned company/category/model/asset changes to temporary CSV before any possible real write
- report optional safe progress during reference planning, asset matching, CSV writing, dry-run application, and execution
- aggregate actions/failures/warnings/result counters

### 3.2 `SnipeImportPreflightCsvWriter`

Namespace:

```csharp
namespace AteraSnipeSync.Core.SnipeIt;
```

Signature:

```csharp
internal sealed class SnipeImportPreflightCsvWriter
{
    public Task WriteAsync(
        SnipeImportPreflightPlan plan,
        string outputDirectory,
        CancellationToken cancellationToken);
}
```

职责：

- write one CSV file per writable Snipe-IT object type
- create output directory when missing
- write all files before manual `SnipeImporter` execution performs any real `POST` / `PATCH`
- write empty CSV files with headers when a table has no rows, so the operator can confirm the object type was reviewed
- never write API tokens
- keep preflight CSV output separate from final sync result/report files

CSV files:

```text
snipeit-assets-plan.csv
snipeit-companies-plan.csv
snipeit-categories-plan.csv
snipeit-models-plan.csv
```

Current writable object types:

- companies: add only
- models: add only
- assets: add or modify

Reserved operation value:

- `Delete` is reserved for future behavior, but first version must not emit delete rows because this project does not delete/archive Snipe-IT assets.

### 3.3 `SnipeImportPreflightPlan`

Namespace:

```csharp
namespace AteraSnipeSync.Core.SnipeIt;
```

Signature:

```csharp
internal sealed class SnipeImportPreflightPlan
{
    public required IReadOnlyList<SnipeCompanyPlanRow> Companies { get; init; }
    public required IReadOnlyList<SnipeCategoryPlanRow> Categories { get; init; }
    public required IReadOnlyList<SnipeModelPlanRow> Models { get; init; }
    public required IReadOnlyList<SnipeAssetPlanRow> Assets { get; init; }
}
```

职责：

- collect all planned writable changes after lookup/matching and before write execution
- preserve enough context for owner review
- avoid secrets and full raw JSON payload dumps

### 3.4 `SnipeCompanyPlanRow`

Signature:

```csharp
internal sealed class SnipeCompanyPlanRow
{
    public required string Operation { get; init; }
    public required string CompanyName { get; init; }
    public int? ExistingCompanyId { get; init; }
    public string? Reason { get; init; }
}
```

CSV columns:

```text
Operation,CompanyName,ExistingCompanyId,Reason
```

Allowed current operations:

- `Add`

### 3.5 `SnipeCategoryPlanRow`

Signature:

```csharp
internal sealed class SnipeCategoryPlanRow
{
    public required string Operation { get; init; }
    public required string CategoryName { get; init; }
    public required string CategoryType { get; init; }
}
```

CSV columns:

```text
Operation,CategoryName,CategoryType
```

Allowed current operations:

- `Add`

`CategoryType` must be `asset` for categories created to support hardware assets.

### 3.6 `SnipeModelPlanRow`

Signature:

```csharp
internal sealed class SnipeModelPlanRow
{
    public required string Operation { get; init; }
    public required string ModelName { get; init; }
    public int? ExistingModelId { get; init; }
    public required string CategoryName { get; init; }
    public int? CategoryId { get; init; }
    public required string ManufacturerName { get; init; }
    public int? ManufacturerId { get; init; }
    public string? Reason { get; init; }
}
```

CSV columns:

```text
Operation,ModelName,ExistingModelId,CategoryName,CategoryId,ManufacturerName,ManufacturerId,Reason
```

Allowed current operations:

- `Add`

### 3.7 `SnipeAssetPlanRow`

Signature:

```csharp
internal sealed class SnipeAssetPlanRow
{
    public required string Operation { get; init; }
    public required string AssetTag { get; init; }
    public required string Name { get; init; }
    public string? Serial { get; init; }
    public required string CompanyName { get; init; }
    public required string ModelName { get; init; }
    public required string CategoryName { get; init; }
    public required string ManufacturerName { get; init; }
    public int? ExistingAssetId { get; init; }
    public string? ExistingAssetTag { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureMessage { get; init; }
}
```

CSV columns:

```text
Operation,AssetTag,Name,Serial,CompanyName,ModelName,CategoryName,ManufacturerName,ExistingAssetId,ExistingAssetTag,FailureCode,FailureMessage
```

Allowed current operations:

- `Add`
- `Modify`
- `Blocked`

### 3.7 `MacAddressNormalizer`

Namespace:

```csharp
namespace AteraSnipeSync.Core.SnipeIt;
```

Signature:

```csharp
internal static class MacAddressNormalizer
{
    public static string? NormalizeComparable(string? value);
    public static string? NormalizeDisplay(string? value);
}
```

Rules:

- comparable value removes `:`, `-`, `.`, and whitespace, then uppercases
- comparable value must contain exactly 12 hex characters
- display value returns `AA:BB:CC:DD:EE:FF`
- invalid MAC returns `null`

### 3.8 `AssetNameMatcher`

Namespace:

```csharp
namespace AteraSnipeSync.Core.SnipeIt;
```

Signature:

```csharp
internal static class AssetNameMatcher
{
    public static SnipeAssetMatch? ChooseHighConfidenceMatch(
        string sourceName,
        IReadOnlyList<SnipeAssetMatch> candidates,
        double threshold);
}
```

Rules:

- normalize names by trimming, lowercasing, and collapsing whitespace
- exact normalized match returns score `1.0`
- otherwise use Levenshtein similarity: `1 - distance / maxLength`
- return a match only when exactly one candidate score is `>= threshold`
- if multiple candidates are `>= threshold`, return `null`; caller records ambiguous failure

### 3.9 `SnipeApiException`

Namespace:

```csharp
namespace AteraSnipeSync.Core.SnipeIt;
```

Signature:

```csharp
internal sealed class SnipeApiException : Exception
{
    public string Code { get; }
}
```

职责：

- represent Snipe-IT HTTP failure, business error payload, malformed JSON, or missing id
- message must not include API token

## 4. 官方 API 使用

All requests:

- use base URL from `SnipeImportOptions.BaseUrl`
- trim trailing slash from base URL
- add `Accept: application/json`
- add `Authorization: Bearer {ApiToken}`
- never log or expose API token

### 4.1 Companies

Find:

```text
GET /companies?name={companyName}
```

Create:

```text
POST /companies
{ "name": "..." }
```

Create only when `CreateMissingCompanies = true` and `DryRun = false`.

### 4.2 Manufacturers

Find:

```text
GET /manufacturers?name={manufacturerName}
```

First version does not create manufacturers.

### 4.3 Categories

Find:

```text
GET /categories?search={categoryName}&category_type=asset
```

Create when missing:

```text
POST /categories
{
  "name": "...",
  "category_type": "asset"
}
```

The category policy for this module is intentionally narrow: every Atera agent asset uses one operator-provided asset category name, defaulting to `Computer`, and missing categories are created with Snipe-IT `category_type = asset`.

### 4.4 Models

Find:

```text
GET /models?search={modelName}&category_id={categoryId}
```

Create:

```text
POST /models
{
  "name": "...",
  "category_id": 123,
  "manufacturer_id": 456
}
```

`manufacturer_id` is included only when manufacturer was found.

When a model depends on a category that will be created in the same run, the model plan leaves `CategoryId` empty during preview and the real run creates the category before posting the model.

### 4.5 Hardware list search

For MAC comparison:

```text
GET /hardware?limit=50&filter={"{MacAddressCustomFieldDbColumnName}":"AA:BB:CC:DD:EE:FF"}
```

For name comparison:

```text
GET /hardware?limit=50&search={assetName}
```

### 4.6 Hardware serial lookup

```text
GET /hardware/byserial/{serial}
```

### 4.7 Hardware create

Official required request fields are `asset_tag`, `status_id`, and `model_id`.

Payload:

```json
{
  "asset_tag": "...",
  "status_id": 2,
  "model_id": 123,
  "name": "...",
  "serial": "...",
  "notes": "...\nAuto Synced from Atera at 2026-06-17T21:00:00Z",
  "company_id": 456,
  "_snipeit_mac_address_5": "AA:BB:CC:DD:EE:FF"
}
```

Custom MAC field is included only when options provide a db column name and at least one valid MAC exists.

### 4.8 Hardware update

Use:

```text
PATCH /hardware/{id}
```

Payload uses the same fields as create where available.

## 5. Response Parsing Rules

List responses may contain:

- `rows`
- `total`

Create/update responses may contain:

- `status`
- `messages`
- `payload`

Entity id resolution:

- direct `id` property
- or `payload.id`

Failure rules:

- non-success HTTP status is failure
- JSON object with `status = "error"` is failure, even when HTTP status is 200
- malformed JSON is failure
- expected id missing after successful create/update is failure

## 6. Manual `Preview Changes` Preflight CSV

Manual sync preflight CSV is enabled by:

```csharp
SnipeImportOptions.ManualPreflightCsvEnabled = true
```

It is an optional manual `Preview Changes` path, not the default `Sync Now` path and not a scheduler mode:

- dry-run means no writes are executed
- manual preflight CSV means write temporary CSV review files for operator review
- `Preview Changes` must enable both dry-run and manual preflight CSV; CSV files are written and no POST/PATCH is executed
- `Sync Now` must disable manual preflight CSV and run the real sync pipeline directly
- scheduled automatic sync must disable manual preflight CSV and rely on normal result/status/report output
- when a user confirms after preview, the system must rerun the real sync pipeline and must not execute from the first CSV snapshot

Current writable Snipe-IT object types:

| Object type | Current operations | CSV file |
| --- | --- | --- |
| Company | Add | `snipeit-companies-plan.csv` |
| Category | Add | `snipeit-categories-plan.csv` |
| Model | Add | `snipeit-models-plan.csv` |
| Hardware asset | Add, Modify, Blocked | `snipeit-assets-plan.csv` |

Current read-only object types:

| Object type | Behavior | CSV write-plan table |
| --- | --- | --- |
| Manufacturer | lookup only | none |

No current object type emits `Delete`. Delete is reserved because future sync policy may support asset archive/delete, but first version must not delete Snipe-IT objects.

### 6.1 Required CSV write timing

When `ManualPreflightCsvEnabled = true`, `SnipeImporter` must:

1. perform all read-only lookups needed to decide planned writes
2. build a complete `SnipeImportPreflightPlan`
3. write all CSV files to `ManualPreflightCsvDirectory`
4. only then execute any real `POST` / `PATCH`, unless `DryRun = true`

If CSV writing fails, the import must return/throw a failure before any real `POST` / `PATCH` is sent.

The TrayApp `Preview Changes` path must call the importer through a request with `DryRun = true`, so preview generation itself never mutates Snipe-IT. The importer still enforces the CSV-before-write gate for any caller that enables manual preflight CSV incorrectly with `DryRun = false`.

### 6.2 Planned IDs before write

Some IDs are unknown before writes occur. For example:

- new company id is unknown before `POST /companies`
- new category id is unknown before `POST /categories`
- new model id is unknown before `POST /models`
- new asset id is unknown before `POST /hardware`

CSV rows should leave those id columns empty when the target does not exist yet.

When a record cannot be fully planned, for example because a required company
or model is missing and creation is disabled, `snipeit-assets-plan.csv` must
still include a row for that source asset. The row operation must be `Blocked`,
existing asset id/tag columns must be empty when no match was resolved, and
`FailureCode` / `FailureMessage` must explain why the asset cannot proceed.

### 6.3 CSV formatting rules

- Encoding: UTF-8
- Include a header row
- Quote fields containing commas, quotes, CR, or LF
- Escape quotes by doubling them
- Do not include API token
- Do not include full raw Atera JSON
- Write empty CSV files with headers even if there are no rows for that object type
- Use ISO-8601 UTC timestamps where timestamps are included in preview fields
- `snipeit-assets-plan.csv` must include `FailureCode` and `FailureMessage` columns so blocked records remain visible during manual review

## 7. Import Algorithm

For each import run:

1. Validate required fields for every `SnipeAssetImportRecord`.
2. Build shared reference plans before asset matching:
   - scan valid records for unique company names
   - find each company by name
   - plan company creation when missing and allowed
   - record one company failure when missing and creation is disabled
   - scan records with successful company plans for unique category names
   - find each category by name
   - plan category creation with `category_type = asset` when missing
   - scan records with successful company/category plans for unique manufacturer names
   - find each manufacturer by name
   - if missing, continue without manufacturer id and add one warning for that manufacturer
   - scan records with successful company/category/manufacturer plans for unique model keys
   - model key is model name + category id or planned category name + manufacturer id
   - find each model by model name/category id when the category already exists
   - plan model creation when missing and allowed
   - record one model failure when missing and creation is disabled
3. Scan valid records for asset plans:
   - first apply any stored company/category/manufacturer/model reference failure to the asset
   - blocked assets must be added to failed preflight rows and must not query `GET /hardware`
4. Find existing asset for records with successful reference plans:
   - try MAC comparison first when configured and valid MAC exists
   - try serial lookup second when serial exists
   - try name high-similarity comparison third
5. Decide write:
   - match found: update
   - no match: create
   - ambiguous match: fail record
6. Add planned write rows:
   - company add row when missing company will be created
   - category add row when missing asset category will be created
   - model add row when missing model will be created
   - asset add row when no existing asset matched
   - asset modify row when an existing asset matched
7. In manual preflight CSV flow:
   - write `snipeit-companies-plan.csv`
   - write `snipeit-categories-plan.csv`
   - write `snipeit-models-plan.csv`
   - write `snipeit-assets-plan.csv`
   - stop before real writes if CSV writing fails
8. In dry-run:
   - do not execute POST/PATCH
   - add planned action with `WasExecuted = false`
9. In real run:
   - create all missing companies
   - create all missing categories
   - create all missing models
   - only after all reference writes succeed, execute asset POST/PATCH
   - if any reference write fails, mark planned assets failed and stop before hardware writes
   - add executed action with `WasExecuted = true`
   - asset create/update payload notes must include the automatic sync timestamp line

## 8. Result Counters

Counters represent planned actions in dry-run and executed actions in real run.

- `CreatedAssets`: increment when an asset create action is planned or executed
- `UpdatedAssets`: increment when an asset update action is planned or executed
- `SkippedAssets`: increment only when record is intentionally skipped without failure
- `FailedAssets`: increment for each failed record
- `CreatedCompanies`: increment when company create action is planned or executed
- `CreatedCategories`: increment when category create action is planned or executed
- `CreatedModels`: increment when model create action is planned or executed

## 9. Failure Codes

Use these `ImportFailure.Code` values:

- `SnipeImport.InvalidOptions`
- `SnipeImport.InvalidRecord`
- `SnipeImport.CompanyMissing`
- `SnipeImport.CategoryMissing`
- `SnipeImport.ModelMissing`
- `SnipeImport.AmbiguousMacMatch`
- `SnipeImport.AmbiguousNameMatch`
- `SnipeImport.HttpFailure`
- `SnipeImport.BusinessError`
- `SnipeImport.MalformedResponse`
- `SnipeImport.MissingResponseId`
- `SnipeImport.PreflightCsvWriteFailed`

## 10. Warning Codes

Use `ModuleWarning.Source = "SnipeImport"` and these codes:

- `SnipeImport.MacMatchingDisabled`
- `SnipeImport.InvalidMacAddress`
- `SnipeImport.ManufacturerMissing`

## 11. Unit Tests

Add tests under:

```text
tests/AteraSnipeSync.Tests/SnipeIt/SnipeImporterTests.cs
```

Required tests:

1. `ImportAsync_UpdatesAssetByMacBeforeSerial_WhenMacMatches`
2. `ImportAsync_UpdatesAssetBySerial_WhenMacDoesNotMatch`
3. `ImportAsync_UpdatesAssetByHighSimilarityName_WhenStrongKeysDoNotMatch`
4. `ImportAsync_CreatesAsset_WhenNoMatchExists`
5. `ImportAsync_DoesNotWrite_WhenDryRun`
6. `ImportAsync_CreatesMissingCompany_WhenAllowed`
7. `ImportAsync_CreatesMissingModel_WhenAllowed`
8. `ImportAsync_CreatesMissingCategory_WhenCategoryMissing`
9. `ImportAsync_CreatesAllMissingReferencesBeforeHardwareWrites`
10. `ImportAsync_FailsRecord_WhenMacMatchIsAmbiguous`
11. `ImportAsync_TreatsStatusErrorBodyAsFailure_WhenHttpStatusIsOk`
12. `ImportAsync_AddsAutoSyncedNoteToCreateAndUpdatePayloads`
13. `ImportAsync_WritesManualPreflightCsvBeforePostOrPatch_WhenManualPreflightEnabled`
14. `ImportAsync_WritesMissingCategoryPreflightCsv_WhenCategoryWillBeCreated`
15. `ImportAsync_DoesNotPostOrPatch_WhenManualPreflightCsvWriteFails`
16. `ImportAsync_ReportsProgressDuringPlanning`
17. `ImportAsync_ReusesReferenceLookups_ForRepeatedReferenceNames`

Update mapping tests:

- verify `InventoryMapper` copies `AgentInfo.MacAddresses` into `SnipeAssetImportRecord.MacAddresses`

## 12. Acceptance Criteria

- `SnipeImporter` implements `ISnipeImporter`
- automated tests use mocked HTTP handlers only
- no real Snipe-IT API call in tests
- API token is not logged or written to result messages
- manual preflight CSV creates `snipeit-assets-plan.csv`, `snipeit-companies-plan.csv`, `snipeit-categories-plan.csv`, and `snipeit-models-plan.csv` before any real manual sync write
- missing categories are planned and created as Snipe-IT asset categories with `category_type = asset`
- manual preflight CSVs include `Operation` values using `Add`, `Modify`, or reserved `Delete`
- first version never emits `Delete`
- CSV write failure prevents real Snipe-IT writes
- `dotnet build AteraSnipeSync.sln --no-restore` passes
- `dotnet test AteraSnipeSync.sln --no-build` passes
