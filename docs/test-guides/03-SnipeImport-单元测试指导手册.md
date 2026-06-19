# Snipe Import - 单元测试指导手册

## 1. 测试目标

Snipe Import Module 的测试验证：

- `SnipeImporter` 按 MAC address、serial number、asset name 高相似度顺序匹配 asset
- missing company 可以按配置创建
- missing model 可以按配置创建
- category missing 会使单条 asset failed
- ambiguous MAC/name match 不会自动更新
- dry-run 不执行任何 POST/PATCH 写入
- HTTP 200 但 JSON body `status = error` 会被识别为 failure
- create/update asset payload 的 notes 会包含 `Auto Synced from Atera at {UTC timestamp}`
- `InventoryMapper` 会把 Atera `AgentInfo.MacAddresses` 传递到 `SnipeAssetImportRecord.MacAddresses`

所有自动化测试都使用 mocked `HttpMessageHandler`。不得在 `dotnet test` 中调用真实 Snipe-IT API。

## 2. 测试文件

主要测试：

```text
tests/AteraSnipeSync.Tests/SnipeIt/SnipeImporterTests.cs
```

相关 mapping 边界测试：

```text
tests/AteraSnipeSync.Tests/Mapping/InventoryMapperTests.cs
```

Production code：

```text
src/AteraSnipeSync.Core/SnipeIt/SnipeImporter.cs
src/AteraSnipeSync.Core/SnipeIt/MacAddressNormalizer.cs
src/AteraSnipeSync.Core/SnipeIt/AssetNameMatcher.cs
src/AteraSnipeSync.Core/SnipeIt/SnipeAssetImportRecord.cs
src/AteraSnipeSync.Core/SnipeIt/SnipeImportOptions.cs
```

## 3. 运行全部测试

```powershell
dotnet build AteraSnipeSync.sln --no-restore
dotnet test AteraSnipeSync.sln --no-build
```

## 4. 只运行 Snipe Import 测试

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~AteraSnipeSync.Tests.SnipeIt"
```

## 5. 重点测试说明

`ImportAsync_UpdatesAssetByMacBeforeSerial_WhenMacMatches`

- verifies MAC match is attempted before serial
- verifies serial lookup is skipped after a MAC match
- verifies update uses `PATCH /hardware/{id}`

`ImportAsync_UpdatesAssetBySerial_WhenMacDoesNotMatch`

- verifies serial lookup is used after MAC search returns no rows
- verifies update uses the serial matched asset id

`ImportAsync_UpdatesAssetByHighSimilarityName_WhenStrongKeysDoNotMatch`

- verifies name similarity fallback only runs after MAC and serial fail
- verifies a single high-confidence name candidate is updated

`ImportAsync_CreatesAsset_WhenNoMatchExists`

- verifies no match results in `POST /hardware`
- verifies asset payload includes `asset_tag`, `status_id`, `model_id`, serial, notes, company id, and configured MAC custom field

`ImportAsync_DoesNotWrite_WhenDryRun`

- verifies dry-run performs lookup GET requests
- verifies dry-run does not perform POST/PATCH
- verifies actions have `WasExecuted = false`

`ImportAsync_CreatesMissingCompany_WhenAllowed`

- verifies `POST /companies` is called when company lookup returns no rows and creation is enabled

`ImportAsync_CreatesMissingModel_WhenAllowed`

- verifies `POST /models` is called when model lookup returns no rows and creation is enabled

`ImportAsync_FailsRecord_WhenCategoryMissing`

- verifies missing category fails the asset because model creation requires a category id

`ImportAsync_FailsRecord_WhenMacMatchIsAmbiguous`

- verifies multiple MAC matches fail the record instead of updating an arbitrary asset

`ImportAsync_TreatsStatusErrorBodyAsFailure_WhenHttpStatusIsOk`

- verifies Snipe-IT business errors in a 200 response are treated as failures

`ImportAsync_AddsAutoSyncedNoteToCreateAndUpdatePayloads`

- verifies both create and update payloads preserve mapped notes
- verifies both create and update payloads append `Auto Synced from Atera at ...`
- uses a fixed `TimeProvider` so the expected timestamp is deterministic

`ImportAsync_WritesManualPreflightCsvBeforeAnyPostOrPatch_WhenEnabled`

- verifies manual sync preflight writes `snipeit-assets-plan.csv`, `snipeit-companies-plan.csv`, and `snipeit-models-plan.csv`
- verifies each writable object table includes an `Operation` column
- verifies the CSV files exist before the first real `POST` or `PATCH`

`ImportAsync_DoesNotWrite_WhenManualPreflightCsvWriteFails`

- verifies preflight CSV write failure returns `SnipeImport.PreflightCsvWriteFailed`
- verifies no real Snipe-IT `POST` or `PATCH` is sent after the CSV write failure

`ImportAsync_WritesManualPreflightCsvAndDoesNotMutate_WhenDryRun`

- verifies dry-run plus manual preflight still writes the review CSV files
- verifies dry-run plus manual preflight does not send Snipe-IT mutations

## 6. Mocking Strategy

`SnipeImporterTests` uses an in-test `StubHttpMessageHandler`.

The handler:

- queues deterministic JSON responses
- captures request method, path/query, and body
- never opens a network socket
- lets tests assert endpoint order and write suppression

When adding tests, queue responses in the same order `SnipeImporter` calls Snipe-IT:

1. `GET /companies`
2. `GET /categories`
3. `GET /manufacturers`
4. `GET /models`
5. `GET /hardware` for MAC
6. `GET /hardware/byserial/{serial}`
7. `GET /hardware` for name search
8. manual preflight CSV write, when enabled
9. optional `POST /companies`
10. optional `POST /models`
11. `PATCH /hardware/{id}` or `POST /hardware`

The exact sequence changes when an earlier match succeeds. For example, a MAC match skips serial and name search.

## 7. Manual Real-Key Verification Policy

Automated tests must never use a real Snipe-IT API token.

If the project owner later wants to validate against a real Snipe-IT instance, it must be a manual-only run outside build/test/CI. The API token must be provided through local-only environment variables or local config, must not be printed, logged, committed, or stored in tracked files, and must be cleared after the session.

Suggested manual verification shape for a future runner:

```powershell
$env:SNIPEIT_BASE_URL = "https://snipe.example.com/api/v1"
$env:SNIPEIT_API_TOKEN = "<set locally, never print>"
# Run a future manual-only command that supports dry-run first.
Remove-Item Env:SNIPEIT_API_TOKEN
Remove-Item Env:SNIPEIT_BASE_URL
```

Expected safe output should include only sanitized counts and target names, never the token.

## 8. 常见失败原因

- Stub response queue order does not match importer request order
- Test expects serial lookup even though MAC already matched
- Missing category fixture causes model ensure to fail early
- Dry-run test accidentally queues or expects POST/PATCH
- JSON response lacks `id` or `payload.id` after create
- Snipe-IT business error body uses `status = error`, so the importer correctly marks the record failed
