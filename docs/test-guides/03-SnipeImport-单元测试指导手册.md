# Snipe Import - 单元测试指导手册

## 1. 测试目标

Snipe Import Module 的测试验证：

- `SnipeImporter` 按 MAC address、serial number、asset name 高相似度顺序匹配 asset
- missing company 可以按配置创建
- missing model 可以按配置创建
- missing category 会创建 `category_type = asset` 的 Snipe-IT asset category
- ambiguous MAC/serial/name match 不会自动更新
- dry-run 不执行任何 POST/PATCH 写入
- HTTP 200 但 JSON body `status = error` 会被识别为 failure
- `messages` 对象/数组会保留字段名和每个 validation reason，不会退化成 generic Snipe-IT error
- 401、403、429、5xx、一般 HTTP、网络失败和 timeout 使用不同 failure code
- request-derived failure message 会显示操作对象、HTTP method 和相对 endpoint，但不显示 token、request payload 或 raw response JSON
- every Company、Asset Category、Model 和 Hardware Asset create payload 的 notes 都包含 `First added by Atera-SnipeIT Sync Tool at {UTC timestamp}`
- create/update asset payload 的 notes 会包含 `Auto Synced from Atera at {UTC timestamp}`；update 会保留既有合法 first-added marker，但不会给原本没有 marker 的既有资产补造首次创建时间
- `InventoryMapper` 会把 Atera `AgentInfo.MacAddresses` 传递到 `SnipeAssetImportRecord.MacAddresses`
- repeated company/category/manufacturer/model references are planned once before asset matching
- all unique company names share one complete `GET /companies` snapshot and are compared through an in-memory normalized-name index
- existing-category model references share one paginated Snipe-IT model snapshot instead of per-model search requests
- matchable assets share one paginated Snipe-IT hardware snapshot instead of per-asset lookup requests

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
- verifies serial matching is skipped after a MAC match
- verifies update uses `PATCH /hardware/{id}`

`ImportAsync_UpdatesAssetBySerial_WhenMacDoesNotMatch`

- verifies serial matching uses the in-memory hardware snapshot after MAC matching returns no asset
- verifies update uses the serial matched asset id

`ImportAsync_LoadsHardwareSnapshotPagesBeforeMatching`

- verifies `/hardware` pages are loaded with `limit` and advancing `offset`
- verifies a later snapshot page can provide the asset match

`ImportAsync_LoadsModelSnapshotPagesBeforePlanning`

- verifies `/models` pages are loaded with `limit=500` and advancing `offset`
- verifies a model found on a later page is reused without planning `POST /models`

`ImportAsync_SharesModelSnapshotAcrossDifferentModelReferences`

- verifies different model names under an existing category share one `/models` snapshot request
- verifies model planning performs local name/category id lookups after the snapshot is loaded

`ImportAsync_BlocksOnlyAssetsReferencingAmbiguousModelKey`

- verifies duplicate Snipe-IT model ids for one normalized model name/category key do not invalidate the whole model snapshot
- verifies only the asset that actually requests the ambiguous key receives `SnipeImport.AmbiguousModelMatch`
- verifies assets using unrelated unique model keys continue into hardware matching/planning

`ImportAsync_BlocksExistingCategoryModels_WhenModelSnapshotFails`

- verifies one model snapshot business failure is applied to all affected model references
- verifies blocked records do not continue to the hardware snapshot

`ImportAsync_UpdatesAssetByHighSimilarityName_WhenStrongKeysDoNotMatch`

- verifies name similarity fallback only runs after MAC and serial fail
- verifies a single high-confidence name candidate is updated only when company, category, and model also match

`ImportAsync_CreatesAsset_WhenNameMatchesButReferencesDiffer`

- verifies name-only matches are not enough for update fallback
- verifies a Snipe-IT asset with the same name but a different category/model is treated as no match
- verifies the source asset is planned and sent as `POST /hardware`

`ImportAsync_CreatesAsset_WhenNoMatchExists`

- verifies no match results in `POST /hardware`
- verifies asset payload includes `asset_tag`, `status_id`, `model_id`, serial, notes, company id, and configured MAC custom field

`ImportAsync_DoesNotWrite_WhenDryRun`

- verifies dry-run performs reference lookup and hardware snapshot GET requests
- verifies dry-run does not perform POST/PATCH
- verifies actions have `WasExecuted = false`

`ImportAsync_CreatesMissingCompany_WhenAllowed`

- verifies `POST /companies` is called when company lookup returns no rows and creation is enabled

`ImportAsync_CreatesMissingModel_WhenAllowed`

- verifies `POST /models` is called when the model snapshot contains no matching name/category row and creation is enabled

`ImportAsync_CreatesMissingCategory_WhenCategoryMissing`

- verifies `POST /categories` is called with `category_type = asset` when category lookup returns no rows
- verifies model snapshot loading is skipped when the category will be created in the same run
- verifies the model and asset can be created after the category is created

`ImportAsync_CreatesAllMissingReferencesBeforeHardwareWrites`

- verifies missing company, category, and model are created before the first `POST /hardware`
- verifies the real run stops interleaving reference creation with asset creation

`ImportAsync_FailsRecord_WhenMacMatchIsAmbiguous`

- verifies multiple MAC matches fail the record instead of updating an arbitrary asset

`ImportAsync_FailsRecord_WhenSerialMatchIsAmbiguous`

- verifies duplicate serial matches fail the record instead of updating an arbitrary asset

`ImportAsync_TreatsStatusErrorBodyAsFailure_WhenHttpStatusIsOk`

- verifies Snipe-IT business errors in a 200 response are treated as failures

`ImportAsync_ReportsFieldValidationDetails_WhenMessagesIsObject`

- verifies documented object-shaped validation messages use `SnipeImport.ValidationError`
- verifies `asset_tag` and `status_id` field names and reasons are flattened into the safe failure message
- verifies raw JSON is not copied into the failure message

`ImportAsync_ClassifiesAuthenticationFailure_WithRequestContext`

- verifies HTTP 401 uses `SnipeImport.AuthenticationFailed`
- verifies the safe company snapshot operation and `GET /companies` endpoint are retained

`ImportAsync_ClassifiesServerFailure_WithResponseDetail`

- verifies HTTP 503 uses `SnipeImport.ServerError`
- verifies safe `messages` text is retained with hardware snapshot request context

`ImportAsync_ReportsReferenceTarget_WhenReferenceCreationFails`

- verifies a shared company create failure identifies `Create Company '<name>' via POST /companies`
- verifies every dependent asset is counted failed while no hardware POST is attempted

`ImportAsync_AddsAutoSyncedNoteToCreateAndUpdatePayloads`

- verifies create payload preserves mapped notes and appends both first-added and automatic-sync lines
- verifies update payload preserves the original first-added line instead of replacing it with the current sync time
- verifies both create and update payloads append `Auto Synced from Atera at ...`
- uses a fixed `TimeProvider` so the expected timestamp is deterministic

`ImportAsync_AddsFirstAddedNoteToEveryCreatedObject`

- verifies `POST /companies`, `POST /categories`, `POST /models`, and `POST /hardware` all include the same second-precision UTC first-added note produced by the injected `TimeProvider`
- verifies the test uses mocked HTTP only and does not call a real Snipe-IT server

`ImportAsync_DoesNotBackfillFirstAddedNote_WhenUpdatingExistingAssetWithoutMarker`

- verifies an existing hardware row without the tool marker is updated without inventing a first-added timestamp
- verifies the normal latest automatic-sync line is still written

`ImportAsync_WritesManualPreflightCsvBeforeAnyPostOrPatch_WhenEnabled`

- verifies manual sync preflight writes `snipeit-assets-plan.csv`, `snipeit-companies-plan.csv`, `snipeit-categories-plan.csv`, and `snipeit-models-plan.csv`
- verifies each writable object table includes an `Operation` column
- verifies the CSV files exist before the first real `POST` or `PATCH`

`ImportAsync_DoesNotWrite_WhenManualPreflightCsvWriteFails`

- verifies preflight CSV write failure returns `SnipeImport.PreflightCsvWriteFailed`
- verifies no real Snipe-IT `POST` or `PATCH` is sent after the CSV write failure

`ImportAsync_WritesManualPreflightCsvAndDoesNotMutate_WhenDryRun`

- verifies dry-run plus manual preflight still writes the review CSV files
- verifies dry-run plus manual preflight does not send Snipe-IT mutations

`ImportAsync_PreviewUsesCompanyAliasMappedFromAteraCompanyName`

- verifies a mapped company alias from `InventoryMapper` is used for Snipe-IT company lookup during manual preview
- verifies visually equivalent Atera company names with non-breaking spaces or dash-like characters do not produce a company `Add` row when the alias target already exists

`ImportAsync_WritesMissingCategoryPreflightCsv_WhenCategoryWillBeCreated`

- verifies missing category preview writes `snipeit-categories-plan.csv`
- verifies the category row uses `Operation = Add` and `CategoryType = asset`
- verifies model preview leaves `CategoryId` empty when the category id is not known until real `POST /categories`

`ImportAsync_WritesBlockedAssetRowsToPreflightCsv_WhenPlanningFails`

- verifies manual preflight writes blocked asset rows when lookup/planning fails before a normal add/modify plan can be produced
- verifies the blocked asset row includes `FailureCode` and `FailureMessage`
- verifies reference-plan-blocked assets do not load the hardware snapshot when all valid records are blocked
- verifies no Snipe-IT `POST` or `PATCH` is sent for blocked preview rows

`ImportAsync_WritesMacAndConflictDetailsToPreflightCsv_WhenBatchIdentityCollides`

- verifies `MacAddresses` exports every valid source MAC in normalized colon-separated form
- verifies `ConflictingFields` and labelled `ConflictingValue` distinguish asset-tag, MAC, and serial collisions
- verifies `ConflictingAssets` lists every peer with Asset Tag, device name, and SourceId
- verifies multi-peer MAC conflicts remain fully visible while all conflicting records are blocked before HTTP writes

`ImportAsync_ReportsProgressDuringPlanning`

- verifies optional progress callbacks report reference planning, model/hardware snapshot loading, and asset matching details
- verifies progress messages include dependency planning stages without exposing secrets or raw payloads

`ImportAsync_ReusesReferenceLookups_ForRepeatedReferenceNames`

- verifies repeated reference names are planned once per unique company/category/manufacturer/model reference
- verifies repeated asset planning shares one hardware snapshot

`ImportAsync_LoadsCompaniesOnce_ForDifferentCompanyNames`

- verifies different source company names are both resolved from one `GET /companies` response
- verifies no company name query parameter or second company GET is sent

`ImportAsync_MatchesHtmlEscapedCompanyName_FromCompanySnapshot`

- verifies a company snapshot name containing `&amp;` matches the source company name containing `&`
- verifies real execution does not send a duplicate `POST /companies` and uses the existing company id in the hardware payload

`ImportAsync_BlocksCompanyPlanning_WhenCompanySnapshotCountIsIncomplete`

- verifies `total` greater than `rows.Count` produces `SnipeImport.IncompleteCompanySnapshot`
- verifies incomplete company data does not continue into category, model, hardware, or mutation requests

## 6. Mocking Strategy

`SnipeImporterTests` uses an in-test `StubHttpMessageHandler`.

The handler:

- queues deterministic JSON responses
- captures request method, path/query, and body
- never opens a network socket
- lets tests assert endpoint order and write suppression

When adding tests, queue responses in the same order `SnipeImporter` calls Snipe-IT:

1. `GET /companies` once, without a company-name query; queue all companies in one `{ total, rows }` response
2. `GET /categories`
3. `GET /manufacturers`
4. `GET /models?limit=500&offset=0`，只要存在可进入 Model planning 的 record 就读取完整 snapshot
5. additional `GET /models?limit=500&offset={next}` pages while `total` requires more rows
6. `GET /fieldsets` once when both MAC DB column and MAC Fieldset name are configured
7. `GET /hardware?limit=500&offset=0`
8. additional `GET /hardware?limit=500&offset={next}` pages while `total` requires more rows
9. manual preflight CSV write, when enabled
10. optional `POST /companies`
11. optional `POST /categories`
12. optional `POST /models`
13. optional combined `PATCH /models/{id}` for category normalization and/or MAC Fieldset assignment
14. `PATCH /hardware/{id}` or `POST /hardware`

The exact sequence changes when reference planning skips a lookup. Once records reach Model planning, the complete Model snapshot is still loaded even when a required category will be created in the same run.

Model snapshot planning now occurs before per-asset matching. Do not queue one model or Fieldset lookup per asset.

Reference planning runs before asset matching. If company, category, manufacturer, or model planning blocks every valid record, do not queue hardware snapshot responses.

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
- Company fixture `total` does not equal the number of queued `rows`, so the importer correctly blocks an incomplete snapshot
- Model fixture omits nested `category.id`, so the model cannot be indexed by name/category
- Test queues one `/models` response per model reference instead of one shared paginated snapshot
- Test expects per-asset serial endpoint calls instead of snapshot-based serial matching
- High-similarity name fixture omits matching company/category/model metadata and therefore no longer qualifies for update fallback
- Missing category fixture now requires category/model/asset create responses unless the test is dry-run
- Dry-run test accidentally queues or expects POST/PATCH
- Planning failure preview expects an empty assets CSV; blocked rows should be present instead
- JSON response lacks `id` or `payload.id` after create
- Snipe-IT business error body uses `status = error`, so the importer correctly marks the record failed
- Test expects object-shaped `messages` to use `SnipeImport.BusinessError`; field validation objects now use `SnipeImport.ValidationError`

## 9. Ignored MAC 测试

- `ImportAsync_IgnoresConfiguredMacForMatchingAndPayloadSelection`：ignored MAC 不匹配 target，也不会写入 custom field；后续真实 MAC 会被选择。
- `ImportAsync_DoesNotBlockRecordsThatOnlyShareIgnoredMac`：只共享 ignored MAC 的 source records 不会触发 `DuplicateBatchIdentity`，但 Preview CSV 仍保留该 MAC。
- `ImportAsync_RejectsInvalidIgnoredMacBeforeAnyRequest`：无效配置在任何 HTTP 请求前失败。

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~SnipeImporterTests&FullyQualifiedName~IgnoredMac"
```

## 10. Virtual Machine 共享 serial 测试

- `ImportAsync_CreatesSharedSerialVirtualMachines_WithoutWritingSerial`：audit-only serial 不触发 batch duplicate gate，两个 VM records 都可创建，且 hardware payload 不含 `serial`。
- `ImportAsync_UpdatesSharedSerialVirtualMachine_ByExactAssetTag`：`ATERA-{SourceId}` 精确命中 hardware snapshot 后执行 PATCH，即使 source/target serial 不同也不启用 serial conflict gate。
- `ImportAsync_BlocksSharedSerialVirtualMachine_WhenAssetTagAndMacMatchDifferentTargets`：asset tag 与 MAC 指向不同 target ids 时仍以 `SnipeImport.ConflictingStrongIdentityMatch` 阻塞。
- 既有 duplicate serial、ambiguous serial 与 MAC/serial conflict tests 继续覆盖普通可靠 serial。

所有 Snipe-IT responses 都由 `StubHttpMessageHandler` 提供，不使用真实 API token 或网络。

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~SnipeImporterTests"
```

## 11. Legacy Asset Tag 迁移测试

`ImportAsync_MigratesLegacySerialAssetTagToAteraSourceIdTag` 使用 mocked hardware snapshot 验证：目标资产仍使用旧 serial-based tag 时，importer 可通过可靠 serial 找到相同 target，并在 PATCH payload 中更新为 `ATERA-{SourceId}`。测试不调用真实 Snipe-IT API。
## 2026-07 Model Category 归一化维护测试

运行新增的 mocked-HTTP tests：

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~SnipeModelCategoryNormalizerTests"
```

`SnipeModelCategoryNormalizerTests` 不访问真实 Snipe-IT，覆盖：

- `GET /models` 多页完整扫描、offset 推进和 Model ID 一致性检查。
- 默认来源列表只让 Server、Laptop、Desktop category 进入 plan；未被 Asset 引用但属于这些来源的 Model 仍包含，Printer 等未列出 category 跳过。
- 自定义来源列表大小写不敏感且 trim 后生效；空来源列表在任何请求前失败。
- API escaped name 先 HTML decode，target category 名称忽略大小写精确匹配。
- target category 缺失、同名多 ID、非 asset 类型时在任何 PUT 前失败。
- model row malformed 或同一 Model ID 的 name/category 自相矛盾时在任何 PUT 前失败。
- `PUT /models/{id}` body 恰好包含保留的 `name` 和目标 `category_id`。
- 单个 Model 的 422 validation failure 产生带字段细节的 outcome，后续 Model 仍继续更新。
- empty plan 是无请求的成功 no-op。
- operator cancellation 若发生在 PUT 已发出后，当前 PUT 等待确定响应，然后返回 partial/cancelled result，不发送下一笔。

常见失败解释：

- 请求数量不符通常表示 Model 分页结束条件或完整快照读取退化。
- 出现任何意外 PUT 通常表示 planning gate 没有在 malformed/ambiguous target 时阻止 mutation。
- payload 多出字段表示维护操作开始覆盖与 category 无关的 Model 数据；应只发送官方更新接口要求的 `name`、`category_id`。
- cancellation test 出现 0 个成功 outcome 表示已发 mutation 被客户端取消，结果可能不确定；出现 2 个请求表示下一笔写入前没有检查 cancellation。

这些测试的 token 是固定假值 `test-token`，handler 只返回内存响应。不得把真实 Snipe-IT token 放入 test data、console output 或 tracked file。

## 2026-07 DeviceType Model Category Preview Gate 测试

运行：

```powershell
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~SnipeImporterTests"
```

新增重点用例：

- `ImportAsync_PreviewBlocksServerDeviceWhenModelUsesServerCategory_AndWritesDeviceType`
- `ImportAsync_PreviewBlocksComputerModelInServerCategory`
- `ImportAsync_PreviewReusesModel_WhenNameAndCategoryMatch`
- `ImportAsync_ReusesUniqueModelNumber_WhenManufacturerAndCategoryMatch`
- `ImportAsync_BlocksUniqueModelNumber_WhenManufacturerDoesNotMatch`
- `ImportAsync_BlocksUniqueModelNumber_WhenCategoryDoesNotMatch`
- `ImportAsync_BlocksModelNumber_WhenMultipleModelsMatch`
- `ImportAsync_BlocksBeforeCreatingMissingCategory_WhenSameNameExistsElsewhere`
- `ImportAsync_PreviewBlocksConflictingSameBatchModelCreates`

断言范围：

- Server DeviceType 仍要求单一默认 Computer category；existing Model 属于 Server 时在 dry-run/reference planning 阶段成为结构化 failure，不发送 POST。
- category 尚待创建时仍先读取完整 Model snapshot；同名冲突不得先创建 Category。
- 同名同 category Model 正常复用。
- name 未命中时，唯一 model_number 只有在 manufacturer id 与 category id 都相同时才复用；真实 hardware payload 必须使用该 existing model id，且不发送 `POST /models`。
- manufacturer/category 任一不相同或 model_number 命中多个 Model 时继续返回 `SnipeImport.ModelNameConflict`；多个命中时即使其中一个三字段完全相同也不得自动选择。
- `snipeit-assets-plan.csv` header 最后一列为 `DeviceType`，planned 与 Blocked rows 都保留值。
- 所有 HTTP 使用 `StubHttpMessageHandler`；自动测试不得调用真实 Snipe-IT。

## 2026-07 真实 Reference 创建进度测试

运行：

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~SnipeImporterTests"
```

重点用例：

- `ImportAsync_CreatesAllMissingReferencesBeforeHardwareWrites`：一次创建 Company、Category、Model；断言整体 total=3，逐笔 Current 为 1/2/3，creating/created 均包含安全 type/name，created 包含秒级耗时，并保持 Company -> Category -> Model -> Hardware 顺序。
- `ImportAsync_ReportsReferenceTarget_WhenReferenceCreationFails`：两条 asset 共用一个 missing Company 时 reference total 必须是 1 而不是 asset total 2；失败 progress 包含 Company name、`SnipeImport.ValidationError` 与耗时。
- `ImportAsync_ReportsNoOp_WhenNoReferenceCreatesAreNeeded`：所有 reference 已存在时报告 total=0 no-op，不出现 reference create start。

测试使用内存 `StubHttpMessageHandler`；不得调用真实 Snipe-IT。若分母退化成 asset count、逐笔 message 缺失、失败没有 code，或 reference 顺序变化，测试必须失败。

## 2026-07 Default Category Model MAC Fieldset 统一准备测试

运行：

```powershell
dotnet test tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~SnipeImporterTests"
```

重点用例：

- `ImportAsync_PreviewPlansFieldsetUpdatesForAllDefaultCategoryModelsOnly`：Preview 读取一次完整 Model snapshot 和一次 Fieldset snapshot；默认 category 下即使本批次未引用的 Model 也显示 `Modify`，其它 category（例如 Printer）不检查、不修改。
- `ImportAsync_CombinesCategoryNormalizationAndFieldsetUpdateBeforeAssetWrite`：来源 category 命中配置列表时，同一个 Model 只发送一次 `PATCH /models/{id}`，同时包含 `category_id` 与 `fieldset_id`，且发生在 hardware write 之前。
- `ImportAsync_AssignsFieldsetWhenCreatingModelInDefaultCategory`：新建到 GUI `Default Category` 的 Model，其 `POST /models` 包含 operator 配置的 `fieldset_id`。
- `ImportAsync_BlocksBeforeMutation_WhenConfiguredFieldsetDoesNotExist`：配置的 Fieldset 名称无法唯一解析时，在 hardware snapshot 和所有 POST/PATCH 之前阻塞。
- `ImportAsync_DoesNotResolveMacFieldset_WhenOnlyOtherCategoryModelsExist`：snapshot 和 batch 只有其它 category 时不调用 `GET /fieldsets`，也不因无关 Fieldset 配置阻塞。

`snipeit-models-plan.csv` 必须同时覆盖 `Add` 与 `Modify`，并显示 existing model id、当前/目标 category、当前/目标 Fieldset 和变更原因。Preview 与 Sync Now 使用同一个 importer planning 路径；区别仅在 dry-run 不执行 mutation。

所有响应仍由 `StubHttpMessageHandler` 提供。自动测试不得调用真实 Snipe-IT API。

## Company / Manufacturer Alias Direct-First 集成测试

- `ImportAsync_PreviewPrefersDirectCompanyMatchOverConfiguredAlias`：company snapshot 已有 source 同名项时忽略 alias，不创建 alias target，asset CSV 保留 source company。
- `ImportAsync_PreviewUsesCompanyAliasMappedFromAteraCompanyName` 与 `ImportAsync_PreviewCreatesAliasTarget_WhenSourceAndAliasAreMissing`：source company 不存在时才使用 alias target，并沿用现有 reuse/create policy。
- `ImportAsync_PreviewPrefersDirectManufacturerMatchOverConfiguredAlias`：source manufacturer 已存在时只发一次 source lookup，忽略 alias，后续 Model/asset CSV 使用 source name/id。
- `ImportAsync_PreviewUsesManufacturerAliasForUniqueModelNumberReuse`：source `Dell Inc.` lookup 未命中后才查询 alias `Dell`；mocked snapshot 提供 canonical manufacturer 与唯一 model-number match，断言没有 Model Add、没有 failure，asset CSV 使用 alias manufacturer。
- `ImportAsync_PreviewWarnsOnceForAliasTarget_WhenSourceAndAliasManufacturersAreMissing`：两个精确 lookup 均未命中时只输出一次 `SnipeImport.ManufacturerMissing`，warning 指向最终 alias target。
- `ImportAsync_ReusesUniqueModelNumber_WhenCategoryWillBeNormalizedToPlannedCategory` 覆盖 default category 尚待创建的情况：同一个 `PlannedCategoryCreate` 同时驱动 Model normalization 与 record category 后，复用现有 Model id，并保留一条 Model Modify。
- Preview 与 Sync Now 共用同一个 `PlanReferencesAsync`，因此 direct-first 决策不会在执行阶段重新逐 asset 计算。多个 model-number matches 的现有 fail-closed tests 保持不变；alias 不允许绕过歧义阻塞。
