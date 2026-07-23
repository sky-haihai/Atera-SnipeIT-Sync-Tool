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
public IReadOnlyList<string> IgnoredMacAddresses { get; init; } = [];
public double NameMatchThreshold { get; init; } = 0.92;
public bool ManualPreflightCsvEnabled { get; init; }
public string? ManualPreflightCsvDirectory { get; init; }
```

规则：

- `MacAddressCustomFieldDbColumnName` 为空时跳过 MAC comparison，并产生 warning
- `IgnoredMacAddresses` 接受常见 `:`, `-`, `.`, whitespace MAC 格式；import 开始时使用 `MacAddressNormalizer.NormalizeComparable` 校验、规范化并按大小写不敏感去重
- `IgnoredMacAddresses` 中出现无效 MAC 时抛出 `ArgumentException`，不得开始 lookup 或 write
- ignored MAC 继续出现在 source record 与 preflight CSV 的 `MacAddresses` 审计列，但从 batch identity collision、source-to-Snipe MAC match、target hardware MAC index 和 create/update payload 的 MAC 候选中排除
- `NameMatchThreshold` 有效范围为 `0.0 < value <= 1.0`
- `ManualPreflightCsvEnabled = true` 时，必须先输出手动 `Preview Changes` 临时 CSV write plan
- `ManualPreflightCsvDirectory` 在 manual preflight CSV 流程下必填
- `ManualPreflightCsvDirectory` 必须是本机可写目录；无法创建或写入时，本次 import 必须失败且不得执行任何 `POST` / `PATCH` / `DELETE`
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
- find matching asset by MAC, then serial, then high-similarity name scoped by company/category/model
- build one normalized ignored-MAC set per import run and use it consistently for duplicate detection, source/target MAC matching, and payload selection
- plan unique company/category/manufacturer/model references before asset matching
- load one paginated Snipe-IT model snapshot for references whose categories already exist
- load one paginated Snipe-IT hardware snapshot before matching non-blocked assets
- create, update, skip, or delete asset
- compare the complete hardware snapshot against all current batch asset tags and plan deletion only for stale `ATERA-` tags
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
- write all files before manual `SnipeImporter` execution performs any real `POST` / `PATCH` / `DELETE`
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
- assets: add, modify, blocked, or delete

`Delete` rows represent Atera-managed Snipe-IT hardware whose normalized `ATERA-...` tag is absent from the current batch.

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

### 3.7 `SnipeAssetPreflightRow`

Signature:

```csharp
internal sealed record SnipeAssetPreflightRow(
    string Operation,
    string AssetTag,
    string Name,
    string? Serial,
    string MacAddresses,
    string CompanyName,
    string ModelName,
    string CategoryName,
    string ManufacturerName,
    int? ExistingAssetId,
    string? ExistingAssetTag,
    string? ConflictingFields,
    string? ConflictingValue,
    string? ConflictingAssets,
    string? FailureCode,
    string? FailureMessage);
```

CSV columns:

```text
Operation,AssetTag,Name,Serial,MacAddresses,CompanyName,ModelName,CategoryName,ManufacturerName,ExistingAssetId,ExistingAssetTag,ConflictingFields,ConflictingValue,ConflictingAssets,FailureCode,FailureMessage
```

Formatting rules:

- `MacAddresses` contains every valid MAC from the source record in normalized colon-separated form, de-duplicated and joined with `; `.
- `ConflictingFields` contains the deterministic conflicting identity labels (`source id`, `asset tag`, `MAC address`, `serial`) joined with `; `.
- `ConflictingValue` contains labelled normalized values such as `MAC address=00:11:22:33:44:55`; multiple field/value pairs are de-duplicated and joined with `; `.
- `ConflictingAssets` contains every other colliding source record as `AssetTag=<tag> | Name=<name> | SourceId=<id>`, de-duplicated and joined with `; `.
- The three conflict columns are populated for `SnipeImport.DuplicateBatchIdentity` rows and remain empty for ordinary Add/Modify or unrelated Blocked rows.
- All four columns use the existing CSV quoting and formula-neutralization behavior.

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
- ignored-MAC comparison always uses the comparable 12-hex form; display format is not used as the set key

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
- carry a granular `Code` that distinguishes validation, authentication, authorization, rate limiting, server, network, timeout, and generic HTTP failures
- message must not include API token
- message must include the safe operation label plus HTTP method and relative endpoint when the failure came from an HTTP request

## 4. 官方 API 使用

All requests:

- use base URL from `SnipeImportOptions.BaseUrl`
- trim trailing slash from base URL
- add `Accept: application/json`
- add `Authorization: Bearer {ApiToken}`
- never log or expose API token

### 4.1 Companies

Load the complete run-local company snapshot once:

```text
GET /companies
```

The response must be a JSON object containing `total` and `rows`. `SnipeImporter.LoadCompanyLookupAsync` parses every row as `{ id, name }`, verifies that `rows.Count == total`, and builds one `SnipeCompanyLookup` indexed by normalized company name. Snipe-IT documents that API output text is escaped; every JSON string read through the shared Snipe response helpers must therefore be HTML-decoded exactly once before trimming, indexing, or matching. This makes an API name such as `Ktunaxa Kinbasket Child &amp; Family Services` match the unescaped source name and prevents an invalid duplicate-company `POST`. `PlanReferencesAsync` uses this lookup for every unique source company and must not issue `GET /companies?name=...` requests. Duplicate normalized names with different ids fail as `SnipeImport.AmbiguousCompanyMatch`. Snapshot request, envelope, count, or row failures are stored once and applied to every affected company reference rather than being treated as a missing company.

Create:

```text
POST /companies
{
  "name": "...",
  "notes": "First added by Atera-SnipeIT Sync Tool at 2026-07-17T18:00:00Z"
}
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
  "category_type": "asset",
  "notes": "First added by Atera-SnipeIT Sync Tool at 2026-07-17T18:00:00Z"
}
```

The category policy for this module is intentionally narrow: every Atera agent asset uses one operator-provided asset category name, defaulting to `Computer`, and missing categories are created with Snipe-IT `category_type = asset`.

### 4.4 Model snapshot lookup and create

Official `/models` list supports `limit` and `offset`. After category and manufacturer planning identifies the unique model references, `SnipeImporter` must load the model list once per import run when at least one reference belongs to an existing category:

```text
GET /models?limit=500&offset={offset}
```

The importer must:

- continue requesting pages until `offset >= total` when `total` is present, or until a page returns no rows
- parse each model row's `id`, `name`, and nested `category.id`
- build an in-memory index keyed by normalized model name + category id
- preserve the existing lookup behavior that manufacturer id is not part of an existing-model match
- retain unique model name/category keys in the lookup and separately track keys that map to multiple distinct model ids
- throw `SnipeImport.AmbiguousModelMatch` only when `Find(modelName, categoryId)` requests an ambiguous key; unrelated model keys from the same snapshot must remain usable
- skip the model snapshot when every model depends on a category that will be created in the same run
- apply a shared snapshot failure to model references under existing categories while still allowing model references under newly planned categories to continue
- not issue per-model `GET /models?search=...&category_id=...` requests during model planning

Create:

```text
POST /models
{
  "name": "...",
  "category_id": 123,
  "manufacturer_id": 456,
  "notes": "First added by Atera-SnipeIT Sync Tool at 2026-07-17T18:00:00Z"
}
```

`manufacturer_id` is included only when manufacturer was found.

When a model depends on a category that will be created in the same run, the model plan does not require the model snapshot, leaves `CategoryId` empty during preview, and the real run creates the category before posting the model.

### 4.5 Hardware snapshot lookup

Official `/hardware` list supports `limit` and `offset`. After reference planning and before asset matching, `SnipeImporter` must load a paginated hardware snapshot once per import run:

```text
GET /hardware?limit=500&offset={offset}
```

The importer must:

- always load this snapshot for an empty Atera batch; for a non-empty batch load it once when at least one source record remains matchable. If every non-empty source record is already blocked, skip the target snapshot and all deletes as a fail-closed safety gate
- continue requesting pages until `offset >= total` when `total` is present, or until a page returns no rows
- parse hardware `rows` into `SnipeAssetMatch`
- parse `custom_fields` entries by their `field` db column and `value`
- build in-memory indexes for configured MAC custom field and serial number
- parse hardware `company`, `category` / `asset_category`, and `model` names when present
- run high-similarity name comparison only against in-memory assets whose company, category, and model names all match the mapped import record
- not call per-asset `GET /hardware?filter=...`, `GET /hardware?search=...`, or `GET /hardware/byserial/{serial}` during asset matching

If the snapshot request returns an HTTP failure, Snipe-IT business error, or malformed JSON, every otherwise-matchable asset must receive the same import failure and no real Snipe-IT write may be attempted for those assets.

### 4.6 Hardware create

Official required request fields are `asset_tag`, `status_id`, and `model_id`.

Payload:

```json
{
  "asset_tag": "...",
  "status_id": 2,
  "model_id": 123,
  "name": "...",
  "serial": "...",
  "notes": "...\nFirst added by Atera-SnipeIT Sync Tool at 2026-07-17T18:00:00Z\nAuto Synced from Atera at 2026-07-17T18:00:00Z",
  "company_id": 456,
  "_snipeit_mac_address_5": "AA:BB:CC:DD:EE:FF"
}
```

Custom MAC field is included only when options provide a db column name and at least one valid MAC exists.

### 4.7 Hardware update

Use:

```text
PATCH /hardware/{id}
```

Payload uses the same fields as create where available.

Creation-note rules:

- `SnipeImporter` must build all timestamps through its injected `TimeProvider` and format them as second-precision ISO-8601 UTC (`yyyy-MM-dd'T'HH:mm:ss'Z'`).
- Every successful create request issued by this module (`POST /companies`, `POST /categories`, `POST /models`, and `POST /hardware`) must include `notes = First added by Atera-SnipeIT Sync Tool at {UTC timestamp}`. Hardware combines this line with mapped notes and its latest automatic-sync line.
- `SnipeAssetMatch` must retain the hardware snapshot `notes` value. On `PATCH /hardware/{id}`, the importer preserves a syntactically valid existing first-added line, replaces the latest automatic-sync line, and must not invent a first-added line when the existing asset has none.
- Model category/Fieldset updates modify only their intended fields and must not replace Model notes.
- Manufacturer remains lookup-only; no manufacturer create-note payload exists in this version.

### 4.8 Hardware delete

Official endpoint:

```text
DELETE /hardware/{id}
```

- `{id}` is the integer Snipe-IT asset id from the complete hardware snapshot.
- The request has no JSON body.
- DELETE is a mutation and therefore uses one attempt only; it is never automatically replayed after timeout/network uncertainty.
- A non-success HTTP status is classified by `SnipeApiClient`; an HTTP-success response is then checked by `EnsureBusinessSuccess` because Snipe-IT may report transaction failure in a JSON `status = error` envelope.
- Official `POST /hardware/:id/restore` documentation identifies deleted hardware as soft-deleted. This module does not permanently purge assets.

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
- a string `messages` or `message` value is a business error and must be preserved as sanitized text
- an object or array `messages` value is a validation error; flatten nested values into `field: reason` entries while preserving field names
- error detail output must be de-duplicated, HTML-decoded, and limited to 2000 characters
- a 401 response uses `SnipeImport.AuthenticationFailed`
- a 403 response uses `SnipeImport.AuthorizationFailed`
- a 429 response uses `SnipeImport.RateLimited`
- a 5xx response uses `SnipeImport.ServerError`
- a non-success response with field validation details uses `SnipeImport.ValidationError`; other non-success responses use `SnipeImport.HttpFailure`
- `HttpRequestException` uses `SnipeImport.NetworkFailure`
- a request timeout that is not caller cancellation uses `SnipeImport.Timeout`
- a `status = error` response with field validation details uses `SnipeImport.ValidationError`; other `status = error` responses use `SnipeImport.BusinessError`
- request-derived messages use `<operation> via <METHOD> /<relative-path> failed: <reason>` and must not include authorization headers, API tokens, request payloads, or raw response JSON
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
- `Preview Changes` must enable both dry-run and manual preflight CSV; CSV files are written and no POST/PATCH/DELETE is executed
- `Sync Now` must disable manual preflight CSV and run the real sync pipeline directly
- scheduled automatic sync must disable manual preflight CSV and rely on normal result/status/report output
- when a user confirms after preview, the system must rerun the real sync pipeline and must not execute from the first CSV snapshot

Current writable Snipe-IT object types:

| Object type | Current operations | CSV file |
| --- | --- | --- |
| Company | Add | `snipeit-companies-plan.csv` |
| Category | Add | `snipeit-categories-plan.csv` |
| Model | Add | `snipeit-models-plan.csv` |
| Hardware asset | Add, Modify, Blocked, Delete | `snipeit-assets-plan.csv` |

Current read-only object types:

| Object type | Behavior | CSV write-plan table |
| --- | --- | --- |
| Manufacturer | lookup only | none |

Hardware assets emit `Delete` only under the `ATERA-` ownership and current-batch absence rules in section 24.

### 6.1 Required CSV write timing

When `ManualPreflightCsvEnabled = true`, `SnipeImporter` must:

1. perform all read-only lookups needed to decide planned writes
2. build a complete `SnipeImportPreflightPlan`
3. write all CSV files to `ManualPreflightCsvDirectory`
4. only then execute any real `POST` / `PATCH` / `DELETE`, unless `DryRun = true`

If CSV writing fails, the import must return/throw a failure before any real `POST` / `PATCH` / `DELETE` is sent.

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
   - call `GET /companies` exactly once and load all returned company rows into a normalized in-memory name index
   - require `total` to equal the number of returned rows so an incomplete response cannot be mistaken for the complete company set
   - find every unique source company in that in-memory index without further company GET requests
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
   - when any model category already exists, load `/models` pages once and build a model name/category id index
   - find each existing-category model from the in-memory model index
   - do not load the model snapshot when all model categories will be created in the same run
   - plan model creation when missing and allowed
   - record one model failure when missing and creation is disabled
3. Scan valid records for asset plans:
   - first apply any stored company/category/manufacturer/model reference failure to the asset
   - blocked assets must be added to failed preflight rows and must not trigger hardware snapshot loading by themselves
4. Load one complete Snipe-IT hardware snapshot when the batch is empty or at least one source record remains matchable; if a non-empty batch is entirely blocked, skip snapshot and delete planning:
   - request `/hardware` pages with `limit` and `offset`
   - build local MAC and serial indexes from parsed rows
5. Find existing asset for records with successful reference plans:
   - try MAC comparison first against the local custom-field index when configured and valid MAC exists
   - try serial comparison second against the local serial index when serial exists
   - try name high-similarity comparison third only against local assets with matching company, category, and model names
   - if the name matches but company/category/model does not all match, treat it as no match so the source asset is planned as `Add`
6. Decide write:
   - match found: update
   - no match: create
   - ambiguous match: fail record
7. Add planned write rows:
   - company add row when missing company will be created
   - category add row when missing asset category will be created
   - model add row when missing model will be created
   - asset add row when no existing asset matched
   - asset modify row when an existing asset matched
8. Build stale Atera-managed delete plans from the same hardware snapshot:
   - normalize every current batch asset tag and place it in a case-insensitive identity set
   - select only target assets whose normalized tag begins with `ATERA-`
   - exclude every target whose full normalized tag remains in the current identity set
   - order the remaining candidates by Snipe-IT asset id and add `Delete` rows with `ChangeReasons = MissingFromAtera`
9. In manual preflight CSV flow:
   - write `snipeit-companies-plan.csv`
   - write `snipeit-categories-plan.csv`
   - write `snipeit-models-plan.csv`
   - write `snipeit-assets-plan.csv`
   - stop before real writes if CSV writing fails
10. In dry-run:
   - do not execute POST/PATCH/DELETE
   - add planned action with `WasExecuted = false`
11. In real run:
   - create all missing companies
   - create all missing categories
   - create all missing models
   - only after all reference writes succeed, execute asset POST/PATCH/no-op and then stale DELETE plans
   - if any reference write fails, mark planned assets failed and stop before hardware writes
   - add executed action with `WasExecuted = true`
   - company/category/model/asset create payload notes must include the first-added timestamp line
   - asset create/update payload notes must include the automatic sync timestamp line; asset update must preserve an existing first-added line without backfilling one onto pre-existing assets

## 8. Result Counters

Counters represent planned actions in dry-run and executed actions in real run.

- `CreatedAssets`: increment when an asset create action is planned or executed
- `UpdatedAssets`: increment when an asset update action is planned or executed
- `DeletedAssets`: increment when an asset delete action is planned in dry-run or successfully executed in a real run
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
- `SnipeImport.AmbiguousSerialMatch`
- `SnipeImport.AmbiguousNameMatch`
- `SnipeImport.AuthenticationFailed`
- `SnipeImport.AuthorizationFailed`
- `SnipeImport.RateLimited`
- `SnipeImport.ServerError`
- `SnipeImport.NetworkFailure`
- `SnipeImport.Timeout`
- `SnipeImport.HttpFailure`
- `SnipeImport.ValidationError`
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
3. `ImportAsync_LoadsModelSnapshotPagesBeforePlanning`
4. `ImportAsync_SharesModelSnapshotAcrossDifferentModelReferences`
5. `ImportAsync_BlocksExistingCategoryModels_WhenModelSnapshotFails`
6. `ImportAsync_UpdatesAssetByHighSimilarityName_WhenStrongKeysDoNotMatch`
7. `ImportAsync_CreatesAsset_WhenNameMatchesButReferencesDiffer`
8. `ImportAsync_CreatesAsset_WhenNoMatchExists`
9. `ImportAsync_DoesNotWrite_WhenDryRun`
10. `ImportAsync_CreatesMissingCompany_WhenAllowed`
11. `ImportAsync_CreatesMissingModel_WhenAllowed`
12. `ImportAsync_CreatesMissingCategory_WhenCategoryMissing`
13. `ImportAsync_CreatesAllMissingReferencesBeforeHardwareWrites`
14. `ImportAsync_FailsRecord_WhenMacMatchIsAmbiguous`
15. `ImportAsync_TreatsStatusErrorBodyAsFailure_WhenHttpStatusIsOk`
16. `ImportAsync_ReportsFieldValidationDetails_WhenMessagesIsObject`
17. `ImportAsync_ClassifiesAuthenticationFailure_WithRequestContext`
18. `ImportAsync_ClassifiesServerFailure_WithResponseDetail`
19. `ImportAsync_ReportsReferenceTarget_WhenReferenceCreationFails`
20. `ImportAsync_AddsFirstAddedNoteToEveryCreatedObject`
21. `ImportAsync_AddsAutoSyncedNoteToCreateAndUpdatePayloads`
22. `ImportAsync_WritesManualPreflightCsvBeforePostOrPatch_WhenManualPreflightEnabled`
23. `ImportAsync_WritesMissingCategoryPreflightCsv_WhenCategoryWillBeCreated`
24. `ImportAsync_DoesNotPostOrPatch_WhenManualPreflightCsvWriteFails`
25. `ImportAsync_ReportsProgressDuringPlanning`
26. `ImportAsync_ReusesReferenceLookups_ForRepeatedReferenceNames`
27. `ImportAsync_LoadsCompaniesOnce_ForDifferentCompanyNames`
28. `ImportAsync_BlocksCompanyPlanning_WhenCompanySnapshotCountIsIncomplete`
28. `ImportAsync_BlocksOnlyAssetsReferencingAmbiguousModelKey`

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
- only stale Snipe-IT hardware with an `ATERA-` tag emits `Delete`; non-`ATERA-` assets never do
- CSV write failure prevents real Snipe-IT writes
- `dotnet build AteraSnipeSync.sln --no-restore` passes
- `dotnet test AteraSnipeSync.sln --no-build` passes

## 16. 2026-07 加固设计

### 16.1 响应解析

新增 `internal static class SnipeApiResponseParser`，从 `SnipeImporter` 拆出：

```csharp
internal static IReadOnlyList<JsonElement> ReadRequiredRows(JsonElement root, string operation);
internal static int? ReadTotal(JsonElement root);
internal static int ReadRequiredEntityId(JsonElement root, string operation);
internal static void EnsureBusinessSuccess(JsonElement root, string operation);
```

`ReadRequiredRows` 仅接受 object + `rows` array；缺失/错误类型立即抛 `SnipeApiException("MalformedResponse", ...)`。每个 hardware/model row 解析失败也终止 snapshot，不允许跳过。offset 使用原始 rows count 推进，并保留最大页数/no-progress guard。

### 16.2 options 与 endpoint 校验

`SnipeImportOptions` 新增 `int MaxReadRetryAttempts = 3`、`TimeSpan RetryBaseDelay`。`MacAddressCustomFieldDbColumnName` 必须匹配 `^_snipeit_[a-z0-9_]+$`。`ApiEndpointValidator.ValidateSnipeBaseUri` 要求 HTTPS 与 `/api/v1` path；HTTP 仅允许 loopback unit/integration host。

### 16.3 retry/cancellation/audit

`SendJsonAsync` 只为 GET 自动重试 `429`、网络/timeout、`500/502/503/504`，遵守 `Retry-After` 并使用指数退避+jitter；POST/PATCH 永不自动重放。mutation 开始前检查 caller token，发出后用内部 timeout token 等待确定响应。

`ImportRunContext` 提供：

```csharp
void AddPlannedAction(...);   // WasExecuted=false
void AddExecutedAction(...);  // WasExecuted=true，仅成功后调用
bool HasExecutedWrites { get; }
```

`SnipeImportResult` 新增 `bool Cancelled`。若 caller cancellation 在成功 mutation 后被观察到，importer 添加 `SnipeImport.CancelledAfterPartialExecution` failure 并返回部分结果；此前取消仍抛异常。

### 16.4 identity conflict gate

planning 前使用 `SnipeBatchIdentityValidator.Validate` 返回每条 record 的冲突 failure。强 identity 重复、MAC 与 serial 命中不同 asset、source serial 与 MAC 命中目标的非空不同 serial、以及多个 source record 命中同一 target id 均阻塞相关 records。所有 candidate 先完成匹配，再按 target id 分组预订，组大小大于 1 时整组失败且不写入。

### 16.5 lookup 性能与 CSV

`SnipeCompanyLookup` 从单次 `GET /companies` 响应构建规范化 company name 到 entity 的索引，整个 run 共享且不执行逐公司 GET。`SnipeHardwareLookup` 构建 `(company, category, model)` 规范化 key 到 assets 的索引；name fallback 只遍历该 bucket。`SnipeModelLookup` 构建 model name/category id 索引。`SnipeImportPreflightCsvWriter.Escape` 在 CSV quoting 前 neutralize formula-leading external text（前置 `'`）。

### 16.6 必测场景

- `{}`/缺少 rows/rows 非数组/malformed row 不得变成空 snapshot。
- malformed row 后 offset 不重叠；create success 缺 id 失败。
- failed mutation 无 executed action；dry-run 全部 action 未执行。
- GET retry、Retry-After、POST 不重放。
- mutation 后取消返回可审计部分结果；首写前取消抛异常。
- batch identity/target reservation/MAC-serial 冲突全部阻塞。
- custom db column 注入被拒；CSV formula 被 neutralize。
- 不同 source company 共用一次 `GET /companies`；company snapshot `total` 与 `rows.Count` 不一致时全部相关 company planning 被阻塞。

### 16.7 Virtual Machine 共享 serial 与 asset tag lookup

`SnipeHardwareLookup` 新增 case-insensitive asset-tag index 与：

```csharp
IReadOnlyList<SnipeAssetMatch> FindByAssetTag(string assetTag);
```

`FindExistingAsset` 同时解析 exact asset-tag match、非 ignored MAC match 与可靠 serial match。若非空 strong matches 指向多个 target ids，抛出 `SnipeImport.ConflictingStrongIdentityMatch`；否则按 asset tag、MAC、serial 的顺序返回唯一 target。asset tag 重复时抛 `SnipeImport.AmbiguousAssetTagMatch`。

当 `record.SerialIsReliableIdentity == false` 时：

- `FindBatchIdentityFailures` 不以该 record 的 serial 建立 duplicate conflict。
- `FindExistingAsset` 不执行 serial lookup 或 target serial mismatch gate。
- `BuildAssetPayload` 不写 `serial` 字段。
- preflight CSV 的 `Serial` 仍输出规范化 source serial，确保例外可审计。

必须新增 mocked-HTTP tests：共享 serial 的两个 VM-style records 不被 batch gate 阻塞且 payload 不包含 `serial`；相同 `ATERA-{SourceId}` asset tag 能更新既有 target；asset tag 与 MAC 命中不同 targets 时阻塞；普通重复 serial 仍阻塞。自动测试不得调用真实 Snipe-IT API。

## 18. Model 全局冲突预检与 DeviceType CSV 技术规格

官方依据：`GET /models` 支持 `limit`、`offset`、`name`、`model_number` 与 `category_id`；`POST /models` 要求 `name` 与 `category_id`。Snipe-IT `AssetModel` 当前 validation 使用 `two_column_unique_undeleted`，使 `name` 与所有未删除 rows 的 `name/model_number` 交叉唯一。实现不得把 category id 当成唯一键的一部分来决定是否可创建。

`SnipeImporter` 内部 model snapshot contract 改为：

```csharp
private sealed record SnipeModel(
    int Id,
    string Name,
    string? ModelNumber,
    int CategoryId,
    string? CategoryName,
    int? ManufacturerId,
    string? ManufacturerName);
```

`ParseModel` 必须读取 `id`、`name`、可选 `model_number`、nested `category.id/name` 与可选 nested `manufacturer.id/name`。缺少 id/name/category.id 仍是 malformed response；官方 transformer 允许 manufacturer 为 null，此时不得通过 manufacturer 相等检查。

`PlanReferencesAsync` 只要 `modelRecords.Count > 0` 就调用一次完整 paginated `LoadModelLookupAsync`，即使目标 category 将在同一 run 创建。snapshot failure 只阻塞需要 Model resolution 的 records，并禁止 reference mutation。

`SnipeModelLookup` 改为按 normalized model `name` 与非空 `model_number` 建立两个全局索引。lookup 输入必须包括 requested model name、resolved manufacturer id、`CategoryPlan`、record category name 与 `DeviceType`，行为如下：

1. name 未命中、model_number 未命中：返回 missing，交给现有 `CreateMissingModels` policy。
2. name 唯一命中且 category id 等于现有 target category id：返回 existing model id。
3. name 唯一命中但 category 不同，或 target category 尚待创建：若现有 model 已有统一 `PlannedModelUpdate.ChangeCategory` 且其 target 与当前 `CategoryPlan` 相同，则复用现有 model id；否则抛 `SnipeImport.ModelCategoryMismatch`。message 包含请求 name、现有 model id/category、期望 category、safe device type；不得计划 POST。
4. name 命中多个 model ids：抛 `SnipeImport.AmbiguousModelMatch`，只影响请求该 normalized name 的 model reference。
5. name 未命中且 model_number 恰好命中一个现有 model：仅当 resolved manufacturer id 非空并等于 `model.ManufacturerId`，且 category 当前相同，或 `ImportRunContext.TryGetPlannedModelUpdate(model.Id)` 返回 `ChangeCategory=true` 且 `TargetCategoryMatches(CategoryPlan)`，才返回该 existing model id；不得计划 POST。`TargetCategoryMatches` 必须同时支持 existing category id 和同一 `PlannedCategoryCreate` reference，因此 target category 可在本次 run 中创建。
6. 唯一 model_number 命中的 manufacturer 不同/无法解析，或 category 既不相同也没有匹配的统一 Model update：抛 `SnipeImport.ModelNameConflict`，message 必须列出 requested/existing manufacturer 与 category；不得复用或计划 POST。
7. model_number 命中多个不同 model ids：无论其中是否只有一个 manufacturer/category 相同，都抛 `SnipeImport.ModelNameConflict`；message 列出首个冲突与 additional count，不得自动选择。

完成逐 reference planning 后，按 normalized planned model name 检查 `ImportRunContext.PlannedModels`。同名存在多个 create definitions 时，把所有对应 `ReferencePlans.Models` entries 改为 `SnipeImport.ModelBatchConflict`。record-level failures 与 duplicate-target blocks 完成后，`ImportRunContext.RetainReferencesFor(plannedRecords)` 必须移除不再被任何可执行 record 引用的 Company/Category/Model creates，避免 blocked assets 留下孤立 reference mutation。

普通 Preview/Sync 按第 19 节在 asset planning 前统一产生允许的 Model category/Fieldset `PATCH /models/{id}` 计划；只有不在配置授权范围内的 category mismatch 才产生 `ModelCategoryMismatch`。`ModelCategoryMismatch` 与 `ModelNameConflict` 在 reference planning 阶段写入 affected asset failures，因此 `DryRun=true` 也返回 failed assets，并在 assets preflight CSV 产生 `Blocked` rows。

`SnipeAssetPreflightRow` 在现有参数列表末尾新增 `string? DeviceType`。`SnipeImportPreflightCsvWriter.BuildAssetsCsv` 保持所有现有列顺序，并把最后一列设为：

```text
Operation,AssetTag,Name,Serial,MacAddresses,CompanyName,ModelName,CategoryName,ManufacturerName,ExistingAssetId,ExistingAssetTag,ConflictingFields,ConflictingValue,ConflictingAssets,FailureCode,FailureMessage,DeviceType
```

planned 与 `ImportRunContext.AddFailure` 生成的 blocked rows 都必须传递 `record.DeviceType`。CSV escaping/formula neutralization 复用现有 `AppendRow`。

必需 mocked HTTP tests：

1. Preview 中 Server record 仍请求默认 Computer category；同名 existing Model 属于 Server 时返回 `ModelCategoryMismatch`、不 POST，并写出带 `DeviceType=Server` 的 Blocked CSV row。
2. 其它 DeviceType record 同名 Model 属于非默认 category 时同样 preview 阻塞。
3. 同名且 target category 相同时继续复用 existing model。
4. model name 未命中、但唯一 existing `model_number`、manufacturer id 相同且 category 当前相同或有匹配的统一 normalization update 时复用 existing model id；不创建 Model。
5. target category 尚待创建时，只要同一个 planned category 同时驱动该 existing Model 的 normalization update，仍复用 existing model id；manufacturer 不同/未解析，或 category 无匹配 update 时返回 `ModelNameConflict`、不 POST。
6. model_number 命中多个 Model 时继续返回 `ModelNameConflict`，即使只有一个 candidate 的 manufacturer/category 相同也不得选择。
7. target category 缺失时仍加载 model snapshot，并在同名 Model 属于其它 category 时于 category/model POST 前阻塞。
8. unrelated duplicated model name 不影响其它 requested model。
9. assets CSV 的 planned 与 blocked rows 都把 `DeviceType` 输出在最后一列。

## 17. Model Category 归一化维护规格

官方 API 依据：`GET /models` 和 `GET /categories` 使用 `limit`/`offset` 分页；Model 更新使用 `PUT /models/{id}`，并要求 body 同时提供 `name` 与 `category_id`。

在 `AteraSnipeSync.Core.SnipeIt` 新增以下 public 类型：

```csharp
public sealed class SnipeModelCategoryNormalizationOptions
{
    public string BaseUrl { get; init; }
    public string ApiToken { get; init; }
    public string TargetCategoryName { get; init; }
    public IReadOnlyList<string> SourceCategoryNames { get; init; }
    public int PageSize { get; init; }
}

public sealed record SnipeModelCategoryNormalizationCandidate(
    int ModelId,
    string ModelName,
    string SourceCategoryName);

public sealed class SnipeModelCategoryNormalizationPlan
{
    public int ScannedModelCount { get; init; }
    public int TargetCategoryId { get; init; }
    public string TargetCategoryName { get; init; }
    public IReadOnlyList<string> SourceCategoryNames { get; init; }
    public IReadOnlyList<SnipeModelCategoryNormalizationCandidate> Models { get; init; }
}

public sealed record SnipeModelCategoryNormalizationOutcome(
    int ModelId,
    string ModelName,
    string SourceCategoryName,
    string TargetCategoryName,
    bool Success,
    string? ErrorCode,
    string? ErrorMessage);

public sealed class SnipeModelCategoryNormalizationResult
{
    public SnipeModelCategoryNormalizationPlan Plan { get; init; }
    public IReadOnlyList<SnipeModelCategoryNormalizationOutcome> Outcomes { get; init; }
    public bool Cancelled { get; init; }
    public int UpdatedModelCount { get; }
    public int FailedModelCount { get; }
    public bool Success { get; }
}

public sealed class SnipeModelCategoryNormalizer
{
    public SnipeModelCategoryNormalizer(HttpClient httpClient, ILogger<SnipeModelCategoryNormalizer> logger);

    public Task<SnipeModelCategoryNormalizationPlan> PlanAsync(
        SnipeModelCategoryNormalizationOptions options,
        CancellationToken cancellationToken,
        IProgress<SyncProgressUpdate>? progress = null);

    public Task<SnipeModelCategoryNormalizationResult> ExecuteAsync(
        SnipeModelCategoryNormalizationPlan plan,
        SnipeModelCategoryNormalizationOptions options,
        CancellationToken cancellationToken,
        IProgress<SyncProgressUpdate>? progress = null);
}
```

`PlanAsync` 先验证并规范化非空 `SourceCategoryNames`（trim、HTML decode、ordinal-ignore-case 去重），解析唯一目标 Asset Category，再分页读取完整 Model snapshot。category/model snapshot 必须要求 object + `rows` array；offset 按实际 row count 推进，`total` 存在时最终 count 必须一致。Model row 必须包含正整数 id、非空 name，以及 nested category 的正整数 id 与非空 name。所有外部名称 HTML decode 一次。只有 category name 命中来源集合且 category id 不等于目标 id 的 Model 才成为 candidate；未命中来源集合和已属于目标 id 的 Model 跳过。candidate 按 model id 去重并按名称、id 稳定排序。

`ExecuteAsync` 在每次 PUT 前检查 cancellation。PUT 一旦开始则不使用 UI cancellation 中断，避免“服务端已改但客户端报告取消”的不确定状态。HTTP 非成功、malformed JSON 或 `status=error` 转为该 candidate 的 outcome；message 只提取长度受限的安全 `messages` 内容。其它 candidate 继续执行。

必须新增 mocked-handler tests：

1. 分页扫描全部 models；默认只计划 Server/Laptop/Desktop category 下的 models（包括未被 asset 引用者），跳过已属于 Computer 和未在 operator 来源列表中的其它 categories。
2. 自定义来源列表只计划命中的 category，空列表在任何 API 请求前失败。
3. category 名称大小写不敏感且 HTML entity 可匹配；同名多 id、缺失或非 asset target 在 PUT 前失败。
4. model snapshot row malformed 或相同 model id 元数据冲突时不发 PUT。
5. PUT payload 只包含所需的 `name` 与 `category_id`，endpoint 为 `/models/{id}`。
6. 一个 PUT 失败后继续其它 model，并返回 success/failure 细分 outcome。
7. no candidate 返回成功 no-op；取消返回可审计 partial result。
8. tests 只使用 fake `HttpMessageHandler`，不得访问真实 Snipe-IT。

## 18. Reference 创建逐笔进度规格

`SnipeImporter.TryExecuteReferencePlanAsync` 在 mutation 前把 `context.PlannedCompanies`、`PlannedCategories`、`PlannedModels` materialize 为稳定 list，并计算：

```csharp
referenceTotal = companies.Count + categories.Count + models.Count;
```

禁止继续使用 `plannedRecords.Count` 作为 reference progress total。`referenceTotal == 0` 时报告 `No missing Snipe-IT reference records need to be created.` 并直接成功返回。

新增 generic helper：

```csharp
private async Task ExecuteReferenceWithProgressAsync<T>(
    string referenceType,
    string referenceName,
    int current,
    int total,
    Func<Task<T>> executeAsync,
    IProgress<SyncProgressUpdate>? progress);
```

helper 使用 `Stopwatch.GetTimestamp()` / `Stopwatch.GetElapsedTime()`：

- 调用前：`Creating Snipe-IT {Type} reference '{Name}'.`，`Current=current`, `Total=total`。
- 成功后：`Created Snipe-IT {Type} reference '{Name}' in {seconds:F3}s.`，同一 Current/Total。
- `SnipeApiException`：先报告 `Failed ... after ... ({Code}).`，再原样 rethrow。
- `JsonException`：先报告 `Failed ... after ... (SnipeImport.MalformedResponse).`，再原样 rethrow。

整体开始消息包含 `companies=x; categories=y; models=z`，整体成功消息包含真实 total 与总耗时。所有消息仍使用 `Stage = SnipeImport`，不改变现有 overall progress range。

Mocked test 必须创建一个 Company、Category、Model 并断言所有 reference progress 的 `Total == 3`、Current 按 1/2/3 推进、三种 type 均有 creating/created message 且 created message 包含秒数；失败测试必须断言对应 reference 的 failed progress 包含原 failure code。

## 19. Model Category 与 MAC Fieldset 统一准备规格

官方 API 依据：

- `GET /fieldsets` 返回 Fieldset `id`、`name`、`fields.rows[].db_column_name`。
- `POST /models` 支持 `fieldset_id`。
- `PATCH /models/{id}` 支持部分更新 `category_id` 与 `fieldset_id`。
- `GET /models` 的 Model 表示包含 nested `fieldset.id/name`；无 Fieldset 时允许为 null。

`SnipeImportOptions` 新增：

```csharp
public string? MacAddressFieldsetName { get; init; }
public string? ModelCategoryNormalizationTargetName { get; init; }
public IReadOnlyList<string> ModelCategoriesToNormalize { get; init; } = [];
```

`MacAddressFieldsetName` 由 GUI/local config 提供。只有 MAC DB column 与 Fieldset name 都非空时启用 Fieldset reconciliation；若只配置其中一个，request construction/UI validation 必须拒绝。Core 为兼容历史调用可在两者都为空时禁用该能力。

新增内部类型：

```csharp
private sealed record SnipeFieldset(
    int Id,
    string Name,
    IReadOnlySet<string> FieldDbColumns);

private sealed record PlannedModelUpdate(
    int ModelId,
    string ModelName,
    int CurrentCategoryId,
    string? CurrentCategoryName,
    int? TargetCategoryId,
    PlannedCategoryCreate? TargetCategoryCreate,
    int? CurrentFieldsetId,
    string? CurrentFieldsetName,
    int? TargetFieldsetId,
    string? TargetFieldsetName,
    bool ChangeCategory,
    bool ChangeFieldset);
```

`RequiresMacFieldsetResolution` 必须先根据完整 Model snapshot 与本批次 Model records 判断 scope：只有已经属于 target category、命中 `ModelCategoriesToNormalize` 将进入 target category，或本批次将在 target category 新建的 Model 才需要解析 Fieldset；只有其它 category 时跳过 `GET /fieldsets`。

`LoadMacFieldsetAsync` 在上述 scope 非空时调用一次 `GET /fieldsets`，要求 object + `rows` array，且 `total` 存在时必须等于实际 rows 数。每行要求正整数 id、非空 name、`fields` object、`fields.rows` array；字段行要求非空 `db_column_name`。按 decode/trim/ordinal-ignore-case 名称匹配：

- 0 个：`SnipeImport.MacFieldsetMissing`
- 多个不同 id：`SnipeImport.AmbiguousMacFieldset`
- 唯一 Fieldset 不包含配置 DB column：`SnipeImport.MacFieldsetDoesNotContainField`

`SnipeModel` 扩展为：

```csharp
private sealed record SnipeModel(
    int Id,
    string Name,
    string? ModelNumber,
    int CategoryId,
    string? CategoryName,
    int? ManufacturerId,
    string? ManufacturerName,
    int? FieldsetId,
    string? FieldsetName);
```

完整 Model snapshot 加载后，`PlanModelMaintenance` 遍历所有 distinct model ids：

在调用 `PlanModelMaintenance` 前，`RequiresNormalizationTargetCategoryResolution` 先检查完整 snapshot。只有至少一个 Model 的 category 命中 normalized `ModelCategoriesToNormalize` 且尚未等于 target category 时，才从当前 reference plans 复用或通过 `PlanCategoryAsync` 解析 target category；其它 category-only run 不得额外查询 target category。

1. `ChangeCategory = model.CategoryName` 命中 normalized `ModelCategoriesToNormalize`，且 model category id 不等于 target category id；target category 可为 existing id 或本次 planned category create。
2. `ChangeFieldset = targetFieldset != null && (model 已属于 ModelCategoryNormalizationTargetName || ChangeCategory) && model.FieldsetId != targetFieldset.Id`。其它 category 不进行 MAC Fieldset 检查。
3. 两者均 false 时不生成 update。
4. 两者至少一个 true 时生成唯一 `PlannedModelUpdate`；同一个 model id 禁止生成多条。

当前 batch 的 model resolution 命中 existing Model 时：

- category 已相同：直接复用。
- category 不同但该 Model 的 `PlannedModelUpdate.ChangeCategory` 将其改为 record 要求的目标 category：复用 existing id。
- 其它 mismatch：保留 `SnipeImport.ModelCategoryMismatch`。

reference execution 顺序为 Company create、Category create、Model create、Model update、Asset create/update。Model create payload 在 target Fieldset 可用时包含 `fieldset_id`。Model update 使用 `PATCH /models/{id}`，payload 只包含实际变化的 `category_id` 和/或 `fieldset_id`。成功后添加 `Update/Model` executed action并增加 `UpdatedModels`；dry-run 添加 planned action且不发请求。

`SnipeImportResult` 新增：

```csharp
public required int UpdatedModels { get; init; }
```

`SnipeModelPreflightRow` 与 CSV columns 改为：

```text
Operation,Name,ExistingModelId,CurrentCategoryName,CurrentCategoryId,TargetCategoryName,TargetCategoryId,ManufacturerName,ManufacturerId,CurrentFieldsetName,CurrentFieldsetId,TargetFieldsetName,TargetFieldsetId,ChangeReasons
```

- create 为 `Add`，current columns 为空，target columns 显示将提交的 category/fieldset。
- existing update 为 `Modify`，`ChangeReasons` 使用稳定的 `Category; Fieldset` 子集。
- 未变化 Model 不输出。

必须新增 mocked HTTP tests：

1. 唯一名称 Fieldset 包含配置 MAC DB column时成功解析一次。
2. missing/ambiguous Fieldset 或字段不属于该 Fieldset时，Preview 与真实 Sync 均在 mutation 前失败。
3. Model create payload 包含目标 `fieldset_id`。
4. existing Model 缺少/使用其它 Fieldset 时，Preview 输出 Modify，真实 Sync 在 asset write 前 PATCH model。
5. source category 命中 normalization list 时，Preview 输出 category Modify，真实 Sync PATCH `category_id`。
6. 同一 Model 同时需要 category 与 Fieldset 时只发一次 PATCH，payload 包含两项。
7. 不在 normalization 来源列表中的 category mismatch 仍阻塞 asset。
8. snapshot 中未被当前 batch 引用、但属于 default target category且缺少目标 Fieldset的 Model仍进入 Modify；其它 category 的同类 Model不进入。
9. Preview 与 Sync Now 对同一 fixture 生成相同 Model Add/Modify 集合，区别仅为 mutation 是否执行。

## 20. Company Direct-Match-First 技术规格

`SnipeAssetImportRecord` 新增可选 `CompanyAliasName`，保存 trim 后的 alias target 候选；现有 `CompanyName` 始终保存 Atera/customer 原名。手工构造或旧调用方未设置 `CompanyAliasName` 时，importer 按 `CompanyName` 保持兼容。

`PlanReferencesAsync` 在分组 company references 前先调用现有 `LoadCompanyLookupAsync` 取得完整 snapshot。lookup 成功后，对 valid records 执行一次本地 effective-name 解析：

1. `SnipeCompanyLookup.Find(CompanyName)` 命中时保留原 record，直接 source match 优先。
2. source 未命中且 `CompanyAliasName` 非空时，复制 record 并令 `CompanyName=CompanyAliasName`；alias target 是否已存在继续由现有 `PlanCompany` lookup/create policy 处理。
3. effective records 替换 importer 当前 run 的 valid-record list，然后再执行 company grouping 和全部后续 planning。
4. lookup 失败时不改 record，并让现有 company reference failure 阻塞受影响 assets。

复制 record 时必须保留 asset tag、name、serial reliability、MACs、company alias candidate、manufacturer、model、category、device type、status、notes、source system 与 source id。不得新增 API endpoint、payload field 或逐 asset lookup。

必需 mocked HTTP tests：

- source company 与 alias target 不同，source 已存在时直接复用 source，不产生 alias target Add，asset CSV 显示 source。
- source 不存在但 alias target 已存在时继续复用 alias target。
- source 与 alias target 均不存在时只按 alias target 进入现有 create policy。

## 21. Manufacturer Direct-Match-First 技术规格

`SnipeAssetImportRecord` 新增可选 `ManufacturerAliasName`；`ManufacturerName` 始终保存 source 名称。`ManufacturerPlan` 同时保存最终 `ManufacturerName` 与可空 `ManufacturerId`。

manufacturer grouping 先按 source `ManufacturerName` 去重。每个 group 的 resolution 顺序为：

1. 精确查询 source；命中则返回 source name/id。
2. source 未命中且 alias candidate 非空时精确查询 alias；命中则返回 alias name/id。
3. 两者均未命中时返回 alias candidate（若有）否则 source，id 为 null，并只对这个最终名称产生一次 `SnipeImport.ManufacturerMissing` warning。
4. resolution 后按 source key 将 importer 当前 run 的 records 复制为最终 manufacturer name，再重建 manufacturer-ready records 与 model keys。
5. API/JSON failure 继续通过 `PlanReferenceAsync` fail closed，不得在 direct lookup 异常后尝试 alias。

复制 record 时必须保留 `ManufacturerAliasName`。必需 mocked tests 覆盖 source direct 优先、source miss 后 alias fallback、两者 miss 的单 warning，以及多个 model-number matches 继续阻塞。

## 22. Asset 真正变更判定与 no-op 技术规格

官方依据：Snipe-IT `GET /hardware` 提供 asset list；hardware 表示包含 `id`、`asset_tag`、`name`、`serial`、nested `model`、nested `company`、nested `status_label`、`notes` 与 custom fields。官方 hardware update contract管理 `asset_tag`、`status_id`、`model_id`、`company_id`、`name`、`serial`、`notes`，custom field update 使用配置的 DB Field Name：

- https://snipe-it.readme.io/reference/hardware-list
- https://snipe-it.readme.io/reference/hardware-update
- https://snipe-it.readme.io/reference/updating-custom-fields

### 22.1 Snapshot contract

`SnipeAssetMatch` 扩展为：

```csharp
internal sealed record SnipeAssetMatch(
    int Id,
    string Name,
    string? AssetTag,
    string? Serial,
    int? CompanyId,
    string? CompanyName,
    int? ModelId,
    string? CategoryName,
    string? ModelName,
    int? StatusId,
    string? Notes,
    IReadOnlyDictionary<string, string> CustomFields);
```

`ParseAsset(JsonElement element)` 必须通过现有 `ReadEntityId(element, propertyName)` 读取 `company.id`、`model.id` 与 `status_label.id`。关系为 null 或 id 缺失时保留 null；change detection 对受管目标 id 使用 fail-safe changed 语义。

### 22.2 Planned record 与 change reasons

`PlannedRecord` 新增：

```csharp
IReadOnlyList<string> ChangeReasons
```

并暴露 `RequiresAssetWrite => ExistingAsset is null || ChangeReasons.Count > 0`。`CreatePlannedRecord` 改为接收 `ImportRunContext context`，构造 reference 结果后调用：

```csharp
private static IReadOnlyList<string> BuildAssetChangeReasons(
    PlannedRecord plannedRecord,
    ImportRunContext context);
```

existing asset 的 stable reason 顺序固定为：

```text
AssetTag; Status; Model; Company; Name; Serial; MacAddress; Notes
```

判定规则：

1. `AssetTag`：source trim 后与已经由 `ParseAsset` 完成 HTML-decode/trim 的 snapshot 值做 ordinal 比较。
2. `Status`：`record.StatusId != existing.StatusId`；snapshot id 缺失仍为 changed。
3. `Model`：existing `model.id` 与已解析 `ModelId` 比较；存在 `ModelCreate` 时必为 changed。
4. `Company`：existing `company.id` 与已解析 `CompanyId` 比较；存在 `CompanyCreate` 时必为 changed。只有 target payload 不包含 company 时才不比较该字段。
5. `Name`：source trim 后与已经由 `ParseAsset` 完成 HTML-decode/trim 的 snapshot 值做 ordinal 比较。
6. `Serial`：仅当 `SerialIsReliableIdentity=true` 且 source serial 可规范化、因此 payload 会包含 `serial` 时比较；audit-only/空 serial 不触发清空或 change。
7. `MacAddress`：新增 `SelectPayloadMacAddress(record, ignoredMacAddresses)`，同时供 `BuildAssetPayload` 与 diff 使用；只比较实际会写入 custom field 的首个有效、非 ignored MAC，使用 `NormalizeComparable` 比较。
8. `Notes`：新增 `NormalizeBusinessNotes`，统一 CRLF/LF、保留业务行顺序与内容，但移除以 `FirstAddedNotePrefix` 或 `AutoSyncedNotePrefix` 开头的工具审计行并 trim end；与 `record.Notes` 的相同规范化结果 ordinal 比较。审计时间变化不得单独触发 PATCH。

### 22.3 Preflight CSV

`SnipeAssetPreflightRow` 在 `DeviceType` 前新增：

```csharp
string? ChangeReasons
```

asset CSV header 改为：

```text
Operation,AssetTag,Name,Serial,MacAddresses,CompanyName,ModelName,CategoryName,ManufacturerName,ExistingAssetId,ExistingAssetTag,ConflictingFields,ConflictingValue,ConflictingAssets,FailureCode,FailureMessage,ChangeReasons,DeviceType
```

`BuildPreflightPlan`：

- Add row：`ChangeReasons=Create`。
- Modify row：仅 `RequiresAssetWrite=true` 的 existing asset 输出，`ChangeReasons=string.Join("; ", ChangeReasons)`。
- unchanged existing asset：不输出 row。
- Blocked row：`ChangeReasons=null`，保留现有 conflict/failure evidence。
- 即使没有 Add/Modify/Blocked rows，也必须写出 header-only CSV。

### 22.4 Dry-run、真实执行与 counters

`ApplyDryRunPlan` 和 `ExecutePlanAsync` 在 existing asset 且 `RequiresAssetWrite=false` 时都调用：

```csharp
private void AddSkippedAsset(PlannedRecord plannedRecord)
```

该 helper 增加 `SkippedAssets`，添加 `ActionType="Skip"`、`TargetType="Asset"` 的 action；`WasExecuted` 在 dry-run 为 false，在真实 run 为 true，但不得增加 `ExecutedWriteCount`。真实 run 立即返回，不调用 `UpdateAssetAsync`。

Add/Modify 的 counters 与 action semantics 保持不变。reference creates/model updates 仍先执行；asset no-op 不取消当前 run 中独立必要的 reference/model writes。

每个成功的 asset Modify 必须复用 `PlannedRecord.ChangeReasons`，不得在执行阶段重新计算 diff：

- `UpdateAssetAsync` 接收 `IReadOnlyList<string> changeReasons`，只在 `EnsureBusinessSuccess` 通过后写一条 `LogInformation`，结构化字段至少包含 `AssetId`、`AssetTag` 与用 `", "` 连接的 `ChangedFields`。
- executed `ImportAction.Message` 必须追加 `Changed fields: {ChangedFields}.`，使 status/history 保存相同审计信息。
- `SnipeImportExecutor.ExecuteAssetsAsync` 的成功 progress 对 Modify 必须追加相同 changed-field labels；Tray 的 `DailyLogWriter` 会把该 progress 写入 `ManualSync_yyyyMMdd.log`。Create 与 Skip 的现有 progress 语义保持不变。
- changed-field labels 只能来自固定集合 `AssetTag; Status; Model; Company; Name; Serial; MacAddress; Notes`，日志不得包含 notes 正文、API token 或 secret。失败的 PATCH 不得写成功变更日志或 executed action。

### 22.5 必需 mocked tests

1. 完整相同 snapshot 在 Preview 中 `UpdatedAssets=0`、`SkippedAssets=1`，asset CSV 只有 header，不含 Modify。
2. 完整相同 snapshot 在真实 Sync 中无 hardware PATCH，产生一个真实 no-op Skip action。
3. 每个受管字段的差异都能产生 Modify；至少用一个组合 test 断言 stable `ChangeReasons` 与 CSV row。
4. 只有 `Auto Synced` timestamp/保留的 first-added timestamp 不同不产生 Modify。
5. snapshot 缺少 `status_label.id`、`model.id` 或应比较的 `company.id` 时 fail safe 为 Modify。
6. ignored/audit-only MAC 与 unreliable serial 不产生虚假 Modify；实际 payload MAC 不同会产生 Modify。
7. 现有 Add、Blocked、legacy asset-tag migration 与真正 update tests 继续通过；所有 HTTP 仍使用 `StubHttpMessageHandler`，不得调用真实 Snipe-IT API。
8. 成功 Modify 必须断言 `ILogger<SnipeImporter>`、per-record progress 与 executed action 都包含同一稳定顺序的 `Changed fields`；mocked PATCH 失败必须断言没有成功变更日志。

## 23. Company pagination and importer decomposition

### 23.1 Concrete types

- Keep `public sealed class SnipeImporter : ISnipeImporter` and both existing constructors as the public facade.
- Add internal `SnipeApiClient` to own request construction, Bearer authentication, GET retry/backoff, mutation no-retry semantics, HTTP-error classification and JSON lifetime. Operation-specific business-envelope validation remains shared with the facade.
- Add internal `SnipeSnapshotLoader` to own hardware/model/company/fieldset snapshot loading and progress. Its company page size is `500` and maximum page count remains `10000`.
- Add internal `SnipeImportPlanner` to coordinate reference resolution, model maintenance, asset matching/diff and immutable planned actions after facade-level record validation.
- Add internal `SnipeImportExecutor` to coordinate ordered execution and per-asset partial-result accounting; shared reference mutations remain the pre-asset execution gate.
- Shared run models used across these components remain internal and must each carry a class comment describing ownership and lifetime.

### 23.2 Company paging algorithm

For page 1 request `companies?limit=500&offset=0`, require `rows` and integer `total`, parse every row to a valid id/name entity, and save the first `total`. Advance `offset` by the returned row count. Continue while accumulated count is less than the first total. Every subsequent response must report the same total. A zero-row page before completion, changed total, invalid row, more than 10000 pages, or final count mismatch throws `SnipeApiException("SnipeImport.IncompleteCompanySnapshot", safeMessage)` before any mutation.

### 23.3 Compatibility tests

Mocked tests must cover 501+ companies over multiple pages, stable offsets, premature empty page, changed total, maximum pages and exact completion. Existing request paths/payloads, business-error parsing, cancellation and result counters must remain unchanged after decomposition. Delete the standalone normalizer tests only after equivalent importer planner/executor coverage exists.

## 24. Atera-managed Hardware stale comparison and soft-delete

### 24.1 Contract changes

Update `src/AteraSnipeSync.Core/SnipeIt/SnipeImportResult.cs`:

```csharp
public required int DeletedAssets { get; init; }
```

`ImportRunContext` adds `public int DeletedAssets { get; set; }`; `ToResult` copies it to `SnipeImportResult.DeletedAssets`.

Update `src/AteraSnipeSync.Core/SnipeIt/ImportAction.cs`:

```csharp
public string? Identifier { get; init; }
```

For delete actions `Identifier` is the invariant-culture decimal Snipe-IT asset id. Existing create/update/skip callers may leave it null. `JsonFileSyncStatusStore.AddImportAction` copies the optional value to `SyncHistoryItem.Identifier`.

Update count projections:

```csharp
// JsonFileSyncStatusStore.CreateHistoryDocument
AssetsDeleted = result.ImportResult?.DeletedAssets ?? 0;

// WorkerResultSanitizer.CreateSummary
Deleted = result.ImportResult?.DeletedAssets ?? 0;
```

Do not change the existing JSON/IPC property names `assetsDeleted` and `deleted`.

### 24.2 Internal planning models

Add nested records under the partial `SnipeImporter` implementation; each requires a concise role comment:

```csharp
private sealed record PlannedAssetDeletion(
    SnipeAssetMatch Asset,
    string AteraAgentId,
    string Reason);

private sealed record SnipeImportPlanningResult(
    IReadOnlyList<PlannedRecord> Assets,
    IReadOnlyList<PlannedAssetDeletion> Deletions);
```

`Reason` is the fixed safe value `MissingFromAtera`; it is not copied from either API response.

Change planner signature to:

```csharp
public Task<SnipeImportPlanningResult> PlanAssetsAsync(
    IReadOnlyList<SnipeAssetImportRecord> allRecords,
    IList<SnipeAssetImportRecord> validRecords,
    ImportRunContext context,
    IProgress<SyncProgressUpdate>? progress,
    CancellationToken cancellationToken);
```

`allRecords` is required for the active tag set so a source record that later fails validation/reference/matching still protects its target tag. `validRecords` continues to drive reference and create/update planning. Any source failure disables the complete deletion list for that run.

For an empty batch the planner calls `LoadHardwareLookupAsync` exactly once. For a non-empty batch it calls the loader once only when at least one record is matchable; an entirely blocked batch returns no delete plan without another API request. If snapshot load fails, it applies the existing target-specific failure to every matchable record; for an empty batch it adds one safe run-level asset snapshot failure. It returns no asset or delete plan, so `RetainReferencesFor([])` removes pending reference writes and no mutation begins.

### 24.3 Ownership and comparison helpers

Add these private static helpers to `SnipeImporter`:

```csharp
private static IReadOnlyList<PlannedAssetDeletion> PlanStaleAssetDeletions(
    IReadOnlyList<SnipeAssetImportRecord> allRecords,
    SnipeHardwareLookup hardwareLookup);

private static bool TryReadAteraManagedAssetTag(
    string? assetTag,
    out string normalizedAssetTag,
    out string ateraAgentId);
```

Rules:

1. Normalize source and target tags with the existing trim/HTML-decode response pipeline; identity comparison uses `StringComparer.OrdinalIgnoreCase`.
2. `TryReadAteraManagedAssetTag` succeeds only when the trimmed target tag begins with `ATERA-`, using `StringComparison.OrdinalIgnoreCase`.
3. `ateraAgentId` is the trimmed suffix after `ATERA-`; an empty suffix remains safe display text `<missing>` but the asset is still inside the explicit ownership prefix.
4. Build the active set from every nonblank normalized `allRecords[i].AssetTag`, before any per-record failure filtering.
5. A target is stale only when it is Atera-managed, its full normalized tag is absent from the active set, and the run has no source validation/reference/matching/duplicate-target failure.
6. Do not consult name, serial, MAC, notes, company, model, status, first-added marker, or match score when deciding ownership/deletion.
7. Return candidates ordered by `Asset.Id` ascending for deterministic CSV, progress, mutation, action, and log order.

This rule intentionally treats a manually created asset using an `ATERA-` tag as tool-managed. The asset-tag namespace is therefore an operator-visible ownership contract.

### 24.4 Dry-run, CSV, and execution

Change these signatures:

```csharp
private static void ApplyDryRunPlan(
    IReadOnlyList<PlannedRecord> plannedRecords,
    IReadOnlyList<PlannedAssetDeletion> plannedDeletions,
    ImportRunContext context);

private static SnipeImportPreflightPlan BuildPreflightPlan(
    IReadOnlyList<PlannedRecord> plannedRecords,
    IReadOnlyList<PlannedAssetDeletion> plannedDeletions,
    ImportRunContext context);

public Task ExecuteDeletionsAsync(
    IReadOnlyList<PlannedAssetDeletion> plannedDeletions,
    ImportRunContext context,
    IProgress<SyncProgressUpdate>? progress,
    CancellationToken cancellationToken);
```

`BuildPreflightPlan` appends one `SnipeAssetPreflightRow` per deletion with:

- `Operation = "Delete"`
- `AssetTag = deletion.Asset.AssetTag ?? "<missing>"`
- `Name = deletion.Asset.Name`
- snapshot serial/company/model/category where available
- `ExistingAssetId = deletion.Asset.Id`
- `ExistingAssetTag = deletion.Asset.AssetTag`
- `ChangeReasons = "MissingFromAtera"`
- conflict and failure columns null

Dry-run adds one planned delete action per candidate, increments `DeletedAssets`, and sends no DELETE. Real execution occurs only after the reference gate and `ExecuteAssetsAsync` complete. Cancellation is checked before each DELETE; once dispatched, existing mutation cancellation/no-retry semantics apply.

Add owner method:

```csharp
private Task DeleteAssetAsync(
    PlannedAssetDeletion deletion,
    ImportRunContext context,
    CancellationToken cancellationToken);
```

It sends `HttpMethod.Delete`, path `hardware/{deletion.Asset.Id}`, payload null, operation containing the safe asset tag, then calls `EnsureBusinessSuccess`. Only after success does it add an executed action and increment `DeletedAssets`.

### 24.5 Audit and failure behavior

Extend `ImportRunContext.AddPlannedAction` and `AddExecutedAction` with optional `string? identifier = null`, or add equivalent delete-specific helpers. Delete action message format must include these labels and safe values:

```text
AssetId={id}; AssetTag={tag}; Name={name}; AteraAgentId={agentId}; Reason=MissingFromAtera.
```

Add:

```csharp
public void AddDeletionFailure(
    PlannedAssetDeletion deletion,
    string code,
    string message);
```

It increments `FailedAssets` once, emits an `ImportFailure` with `TargetType = "Asset"` and target name equal to tag-or-name, and prefixes/appends the same safe audit identity fields. It does not increment `DeletedAssets` or add a successful Delete action.

`ExecuteDeletionsAsync` catches `SnipeApiException` and `JsonException` per candidate, calls `AddDeletionFailure`, reports progress, logs Warning, and continues. Success logging uses Information. Both templates expose structured fields:

```text
AssetId, AssetTag, AssetName, AteraAgentId, DeleteReason, DeleteResult
```

`DeleteResult` is `Succeeded` or the safe failure code. Do not log notes, serialized payload, Authorization headers, token, or raw response body.

The completed importer log adds `Deleted={DeletedAssets}` alongside Created/Updated/Failed. Progress includes `Deleting stale Snipe-IT asset {current}/{total}` and a terminal success/failure message without secrets.

### 24.6 Required mocked tests

Add tests to `SnipeImporterTests` using only `StubHttpMessageHandler`:

1. current `ATERA-1001` plus stale `ATERA-9999` and manual `MANUAL-1` → only id for `ATERA-9999` receives DELETE;
2. ownership and active-set comparisons ignore tag case and surrounding whitespace;
3. dry-run reports `DeletedAssets = 1`, planned Delete action and Delete CSV row, with zero DELETE requests;
4. empty Atera batch still loads hardware and deletes all `ATERA-` assets while preserving all non-prefixed assets;
5. source record blocked after validation/reference planning still protects the identical current tag;
6. malformed/incomplete hardware snapshot produces no POST/PATCH/DELETE;
7. first DELETE business/HTTP failure increments `FailedAssets`, keeps `DeletedAssets` unchanged for that record, logs key fields, and the next candidate still executes;
8. success log/action/history contain asset id, tag, name, parsed agent id and `MissingFromAtera`, while a sentinel token and notes string are absent;
9. `JsonFileSyncStatusStoreTests` assert `assetsDeleted` and `assets.deleted` use real importer data and preserve `Identifier`;
10. `WorkerCommandHandlerTests`/sanitizer assertions expect the real `DeletedAssets` count instead of fixed zero.

All existing create/update/no-op, preflight, cancellation, HTTP classification, pagination and partial-result tests must continue to pass. No automated test may call a real Atera or Snipe-IT API.
