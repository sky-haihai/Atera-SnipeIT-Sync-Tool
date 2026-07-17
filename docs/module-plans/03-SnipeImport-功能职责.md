# Snipe Import - 功能职责

## 1. 模块目标

Snipe Import Module 负责把 Reconstruction Module 输出的 `SnipeImportBatch` 导入到 Snipe-IT。

本模块根据 import batch 中的 company、manufacturer、category、model、asset 字段查询 Snipe-IT，并决定每条 asset 应该被创建、更新、跳过或标记失败。

本模块的核心比较优先级为：

1. MAC address 精确匹配
2. Serial number 精确匹配
3. Asset name 高相似度匹配

如果以上三种比较都没有可靠命中，则创建新 hardware asset。

## 2. 官方 API 文档依据

实现 Snipe-IT API 集成前已核验官方 OpenAPI 文档：

- https://snipe-it.readme.io/reference/api-overview
- https://snipe-it.readme.io/openapi/snipe-it-rest-api.json

已确认的第一版使用 endpoint：

- `GET /companies`
- `POST /companies`
- `GET /manufacturers`
- `GET /categories`
- `GET /models` with `limit` / `offset` pagination
- `POST /models`
- `GET /hardware` with `limit` / `offset` pagination
- `PATCH /hardware/{id}`
- `POST /hardware`

官方 OpenAPI 显示 MAC Address 出现在 hardware `custom_fields` 示例中，而不是固定顶层字段。因此第一版不硬编码具体 MAC custom field 名称；必须通过 `SnipeImportOptions.MacAddressCustomFieldDbColumnName` 指定 Snipe-IT custom field db column，例如 `_snipeit_mac_address_5`。

## 3. 输入

本模块接收：

- `SnipeImportBatch`
- `SnipeImportOptions`
- `CancellationToken`

`SnipeImportBatch` 中每个 asset record 应至少包含：

- asset tag
- name
- optional serial
- zero or more MAC addresses
- company name
- manufacturer name
- model name
- category name
- status id
- notes
- source system/source id

`SnipeImportOptions` 应包含：

- Snipe-IT API base URL
- API token
- dry-run flag
- whether missing companies may be created
- whether missing models may be created
- optional MAC address custom field db column name
- zero or more ignored MAC addresses that must not participate in asset identity matching
- name match threshold
- manual sync preflight CSV output flag
- manual sync preflight CSV temporary output directory

## 4. 输出

本模块返回 `SnipeImportResult`，包含：

- created asset count
- updated asset count
- skipped asset count
- failed asset count
- created company count
- created category count
- created model count
- dry-run flag
- structured actions
- structured failures
- warnings
- optional manual sync preflight CSV files

Dry-run 下：

- 允许查询 Snipe-IT
- 不允许创建 company
- 不允许创建 model
- 不允许创建 asset
- 不允许更新 asset
- `ImportAction.WasExecuted` 必须为 `false`

Manual sync `Preview Changes` preflight CSV 下：

- 该流程只用于手动 `Preview Changes` 按钮，不是 `Sync Now` 的默认路径，也不是后台 scheduler 的常规运行模式
- `Preview Changes` 会先执行 lookup/mapping/planning，并把本次 run 计划修改的 Snipe-IT 对象写入临时 CSV
- `Preview Changes` 本身必须使用 dry-run，不执行真实 `POST` / `PATCH`
- 用户在预览后点击确认时，不按第一次 CSV 快照执行；系统应重新进入真实 sync pipeline 并重新计算当前状态
- `Sync Now` 是手动真实同步按钮，直接进入真实 sync pipeline，不生成 preflight CSV
- 临时 CSV 用于人工审核，必须和最终 sync result/report 分开保存
- CSV 必须按对象类型分表，不能把 asset、company、model 混在同一个表
- 每个 CSV 必须包含一列 `Operation`，用于说明本行是 `Add`、`Modify`、`Blocked` 还是未来保留的 `Delete`
- 第一版当前会产生的 CSV 表：
  - `snipeit-assets-plan.csv`
  - `snipeit-companies-plan.csv`
  - `snipeit-categories-plan.csv`
  - `snipeit-models-plan.csv`
- 第一版没有 delete 行为，因此 `Operation = Delete` 只是保留值，不应在当前实现中出现
- 第一版会自动创建缺失的 asset category，`category_type` 固定为 `asset`，并写入独立的 category CSV；manufacturer 仍只查询不创建
- 如果某个 asset 因 company/category/model/ambiguous match 等 planning failure 无法继续，`snipeit-assets-plan.csv` 仍必须写入该 asset 的 `Blocked` 行和 failure reason，避免人工预览时看到空表
- `snipeit-assets-plan.csv` 必须为每条 asset 输出全部有效 normalized MAC，并为 batch identity collision 额外输出冲突字段、具体冲突值和对方 asset；对方 asset 必须包含 Asset Tag、设备名和 source id，使 operator 不依赖原始 API payload 就能定位冲突记录
- 如果 `Preview Changes` CSV 无法写入，该预览流程必须失败，且不得执行任何真实 Snipe-IT 写入
- 后台 scheduler 自动 sync 不要求生成这些临时 CSV；它应依赖配置、日志、status/report 和 notification

## 5. 对外接口

```csharp
public interface ISnipeImporter
{
    Task<SnipeImportResult> ImportAsync(
        SnipeImportBatch batch,
        SnipeImportOptions options,
        CancellationToken cancellationToken);
}
```

## 6. 成功条件

一次 import run 成功处理每条 asset record 时，应满足：

- 能查询或创建 missing company，除非 options 禁止创建
- Snipe-IT API 返回的 escaped text 必须先 HTML-decode 一次再规范化和匹配；例如 snapshot 中的 `Child &amp; Family Services` 必须匹配 source 中的 `Child & Family Services`，不得误计划重复 company
- 能查询或创建 missing asset category，`category_type` 固定为 `asset`
- 能查询 manufacturer；查不到 manufacturer 时不阻塞导入，但 model 会不绑定 manufacturer
- 能查询或创建 missing model，除非 options 禁止创建
- 对已存在 category 下的 model，单次 import run 只分页读取一次 Snipe-IT model 列表，并在内存中按 model name + category id 匹配
- Snipe-IT model snapshot 中某个 name/category key 重复时，只阻塞实际引用该 key 的 asset；不得把局部 model ambiguity 扩散为整个 snapshot failure
- 能按 MAC address 优先比较 existing asset
- 配置在 `IgnoredMacAddresses` 中的 MAC 必须保留在原始审计/Preview CSV 中，但不得参与 batch duplicate detection、existing asset MAC matching 或 MAC custom field payload 选择
- MAC 未命中时能按 serial number 比较 existing asset
- MAC 和 serial 都未命中时能按 asset name 高相似度比较 existing asset
- 唯一可靠命中时更新 existing asset
- 没有可靠命中时创建 new hardware asset
- 程序首次创建 Company、Asset Category、Model 或 Hardware Asset 时，必须在该对象的 Snipe-IT `notes` 写入不可变的首次创建标记：`First added by Atera-SnipeIT Sync Tool at {UTC timestamp}`
- Hardware Asset 每次自动创建或更新时，继续在 `notes` 写入最新的 `Auto Synced from Atera at {UTC timestamp}`；后续更新必须保留已有首次创建标记，不得把更新时间误写成首次创建时间
- 对程序接管但并非由程序创建的既有 Hardware Asset，不得在更新时补造首次创建标记；Manufacturer 当前只查询不创建，因此不产生该标记
- `Preview Changes` preflight 开启时，能在不执行真实写入的情况下输出 company/category/model/asset 临时 CSV 修改计划
- 能识别 HTTP 200 但 JSON body `status = error` 的 Snipe-IT 业务失败
- 能区分 Snipe-IT 字段验证失败、认证失败、授权失败、限流、服务端失败、一般 HTTP 失败、网络失败与超时
- 对 `messages` 字符串以及 `messages` 对象/数组都保留可安全显示的错误细节；字段验证错误必须显示字段名，且不得输出 API token 或未经筛选的 raw response body
- 每个写入失败必须说明操作对象、HTTP method 和相对 endpoint，使 operator 能区分 company/category/model reference 创建失败与单笔 asset 创建/更新失败
- 能返回 structured actions/failures/warnings

## 7. 失败条件

以下情况应产生 `ImportFailure`，并继续处理 batch 中其它 asset：

- options 缺少 base URL 或 API token
- company 缺失且不允许创建
- company/category/model reference 创建失败，导致 asset 写入必须停止
- model 缺失且不允许创建
- asset 实际引用的 model name/category key 在 Snipe-IT 中对应多个不同 model id
- MAC address 命中多个不同 asset
- serial number response 表示 error 或 ambiguous state
- name similarity 命中多个高置信度候选
- Snipe-IT 返回 non-success HTTP status
- Snipe-IT 返回 `status = error`
- Snipe-IT `messages` 返回字段级 validation object/array
- Snipe-IT 请求发生网络连接失败或非用户取消导致的 timeout
- response JSON malformed 或缺少必须 id

## 8. 不负责事项

本模块不负责：

- 从 Atera 拉数据
- Atera raw DTO 解析
- Atera 到 Snipe-IT 的字段映射
- 生成 default company/manufacturer/model/category/status
- 决定 sync schedule
- 写 status file
- 发送 email 或 webhook
- 删除、归档、checkout、checkin Snipe-IT asset
- 创建 category
- 创建 manufacturer
- 自动发现 MAC custom field db column
- 为只读查询对象生成修改 CSV 表；例如第一版 manufacturer 只查询，不生成 manufacturer 修改表

## 9. 扩展点

后续可扩展：

- 自动发现或配置多个 MAC custom field
- 更复杂的名称相似度算法
- 人工确认队列
- manufacturer 自动创建选项
- manufacturer preflight CSV table support if it becomes writable
- checkout/checkin 行为
- disappeared devices archive policy
- 跨 import run 的 company/model/category/manufacturer cache；当前 company、model 与 hardware 快照只在单次 run 内存中存在

## 10. 2026-07 数据完整性与可靠性加固职责

- 所有 list lookup 必须要求合法 JSON object 且包含 `rows` array；`{}`、缺少 `rows`、`rows` 类型错误或 malformed row 都是 `MalformedResponse`，绝不能解释成“目标为空”。分页 offset 按服务器返回 row 数推进。
- create response 必须包含可读取的目标 id；HTTP success 但缺少 id 视为失败。update response 必须通过 Snipe-IT payload business status 校验。
- `MacAddressCustomFieldDbColumnName` 只允许 `_snipeit_...` db column；不得覆盖 `asset_tag`、`status_id`、`model_id` 等标准 payload 字段。
- `IgnoredMacAddresses` 中的值必须按通用 MAC 格式规范化并去重；无效配置值必须在 import 开始前被拒绝，避免 Preview 与真实 Sync 使用不同的身份集合。
- 只读 GET 对 `429`、临时网络错误、timeout 和 `5xx` 使用有上限的 `Retry-After`/指数退避+jitter。POST/PATCH 不自动重放；一次 mutation 发出后必须等待得到确定结果，再响应用户取消。
- dry-run action 标记为 planned/`WasExecuted=false`；真实 action 只能在对应 HTTP mutation 成功后加入且 `WasExecuted=true`。失败写入不得同时出现在 created/updated actions 中。
- 若取消发生在任何成功写入之后，必须返回带 `Cancelled=true`、已执行 actions 和结构化 failure 的部分结果供审计；首个写入前取消仍抛出 `OperationCanceledException`。
- batch 内重复强身份、MAC 与 serial 指向不同目标、或多个 source record 预订同一 Snipe asset id 时，所有相关记录都必须阻塞，不能按顺序覆盖。
- hardware 名称 fallback 必须使用按 company/category/model 建立的内存索引，避免逐 record 全表扫描。
- company 验证必须在每次 import run 中只调用一次 `GET /companies`，把响应中的全部 `rows` 加载到内存并按规范化名称建立索引；所有 source company 都与该索引对比，禁止恢复为 `GET /companies?name=...` 的逐公司请求。快照请求或响应失败时，受影响记录必须以同一个结构化 company lookup failure 阻塞，不能把失败误判成 company 缺失。
- preflight CSV 对以 `=`, `+`, `-`, `@` 开头（忽略前导空白）的外部文本前置单引号，防止 spreadsheet formula injection。
- import 自身只返回本阶段新增 warnings；上游 mapping warnings 由 Sync Orchestrator 聚合一次。

## 10.1 Virtual Machine 共享 serial 导入职责

- hardware snapshot 必须按 Snipe-IT 返回的唯一 `asset_tag` 建立精确索引；asset tag、MAC、可靠 serial 都属于 strong identity，命中不同 target id 时阻塞。
- `SerialIsReliableIdentity = false` 的 record 仍在 Preview 显示 source serial，但该值不参与 batch duplicate gate、Snipe-IT serial lookup、target serial conflict 检查或 create/update payload。
- 这类 record 依靠 `ATERA-{SourceId}` asset tag 在首次创建后稳定更新；MAC 可辅助匹配，但 MAC 改变不能导致重复创建已有相同 asset tag 的目标。
- asset tag 快照异常地包含多个相同值时，以 `SnipeImport.AmbiguousAssetTagMatch` 阻塞，不能任意选择。
- 普通 record 的可靠 serial 行为保持不变。

## 10.2 指定来源 Model Category 归一化职责（兼容维护入口）

SnipeImport 模块保留一个兼容维护入口，用于单独把 operator 指定来源 categories 下的 Asset Models 统一为程序默认 category。普通 Preview/Sync 的正式行为以 10.5 的统一 Model 准备流程为准，不得维护另一套 category 判断：

- 输入为 Snipe-IT base URL、API token、程序 `DefaultCategoryName`（UI 默认 `Computer`）和非空的来源 category 名称列表（UI 默认 `Server; Laptop; Desktop`）。
- 第一阶段必须分页读取完整 `GET /models` snapshot，统计扫描的 model 数量；只有当前 category 名称命中 operator 来源列表（trim、HTML decode、ordinal-ignore-case）且 category id 不等于目标 category id 的 models 才成为 candidate。未被 asset 引用但位于指定来源 category 的 Model 仍应包含。
- 未命中来源列表的其它 inventory categories 必须完全跳过，即使它们不是目标 category；已经属于目标 category 的 model 也必须跳过。
- 同一个 model id 在 snapshot 中重复出现时只生成一个 candidate；若名称或 category 元数据冲突，必须在任何写入前停止。
- 必须分页读取 `GET /categories`，按 HTML decode 后的精确名称（忽略大小写）解析唯一目标 category。目标不存在、同名多 id或不是 asset category 时必须在任何写入前停止；本操作不自动创建 category。
- planning 只返回结构化 plan，不产生任何写入。执行阶段按 candidate 逐个调用官方 `PUT /models/{id}`，payload 同时包含原 model `name` 和目标 `category_id`。
- 每个成功的 model update 影响所有引用该 model 的 asset；不得逐 asset 修改 `model_id`，不得创建或删除 Model/Category/Asset。
- 单个 model 更新失败时返回该 model 的安全错误 code/message 并继续处理其它 candidate。取消只在下一次 model mutation 发出前生效；已发出的 PUT 必须等待确定结果，返回部分结果供日志审计。
- API token、raw response body 和未筛选 payload 不得出现在结果或日志文本中。

成功条件：完整扫描已完成、目标 category 唯一有效，且所有 candidate model 均更新成功。没有 candidate 是成功的 no-op。部分失败或取消必须显式体现在结果中。

## 10.3 真实 Reference 创建进度职责

真实 import 在 asset writes 前创建 missing Company、Category、Model 时：

- progress 分母必须是本次实际待创建的 Company + Category + Model 去重后总数，不能使用 planned asset 数。
- 开始阶段必须同时给出三个 reference type 的数量；没有 missing reference 时明确报告 no-op。
- 每个 POST 前报告 1-based 序号、总数、reference type 与 name；成功后报告同一序号和该请求耗时。
- 单笔失败时，在返回原有结构化 import failure 前先报告失败的 reference type/name、错误 code 与耗时，使 operator 能确认卡点。
- reference 创建仍保持 Company -> Category -> Model 的顺序和单次 mutation 语义；进度增强不得改变 endpoint、payload、重试或取消规则。
- progress message 只能包含安全 reference metadata，不能包含 token、Authorization header、raw payload 或 raw response。

## 10.4 Model 全局唯一性 Preview Gate 与 Device Type 审计职责

- 每次存在待规划 Model reference 的 import run 都必须分页读取完整 `GET /models` snapshot，包括 source category 尚待创建的情况。
- Model 预检必须与 Snipe-IT 当前唯一性语义一致：待使用的 model name 既不能与其它未删除 Model 的 `name` 冲突，也不能与其 `model_number` 冲突；不得只按 name + category id 判断“缺失”后尝试 `POST /models`。
- 若唯一同名 Model 的 category id 与 record 要求的 category 不同，且该 Model 不在本次 `ModelCategoriesToNormalize` 到默认 target category 的统一更新计划内，Preview 与真实 Run 都必须在任何 reference/asset mutation 前以 `SnipeImport.ModelCategoryMismatch` 阻塞该 reference。failure 必须包含 model name/id、现有 category、期望 category 与安全的 source device type。
- 例如 `DeviceType=Server` 的 `PowerEdge R740` 仍要求默认 category；若 Snipe-IT 中该 Model 属于已配置的归一化来源 category，则 Preview 显示合并的 Model Modify，Sync 在 asset 前更新；若该来源 category 未获配置授权，则预检失败，不能复用或创建同名 Model。
- 若待用 model name 未命中任何现有 Model `name`，但精确命中唯一一个现有 Model 的 `model_number`，且该 Model 的 manufacturer id 与 alias 后已解析 source manufacturer id 相同，同时当前 category id 已相同或本次开头的统一 `PlannedModelUpdate` 明确把该 Model 改到同一个 `CategoryPlan`，则 Preview/Sync 必须直接复用该现有 Model id，不得创建同名 Model。该规则允许 target category 在同一 run 中计划创建，但必须由同一个 planned category object 同时驱动 Model update；manufacturer 未解析/不同、category 既不相同也无匹配的统一更新计划时仍以 `SnipeImport.ModelNameConflict` 阻塞。
- 同一 `model_number` 命中多个现有 Model id 时，即使 manufacturer/category 可缩小范围也不得自动选择，必须继续以 `SnipeImport.ModelNameConflict` 阻塞并列出歧义数量。
- 同一 batch 若把同一 model name 规划成多个 category/manufacturer create definitions，必须以 `SnipeImport.ModelBatchConflict` 阻塞所有相关 records，不能让真实 run 创建第一个后在第二个 POST 才失败。
- 同一 model name 对应多个现有 Model id 时只阻塞实际请求该 name 的 records；不得影响请求其它 model names 的 assets，也不得任意选择一个 id。
- 普通 Preview/Sync 必须按 10.5 自动规划 operator 已配置来源 categories 的 Model 重分类；兼容 maintenance 入口不得使用不同的筛选规则。
- `snipeit-assets-plan.csv` 最后一列必须为 `DeviceType`，对 planned 与 blocked rows 都输出 record 上保留的值；空值输出空单元格，并继续应用 CSV formula-injection 防护。
- 一个 reference planning failure 只应阻塞依赖该 reference 的 assets；在 mutation 尚未开始时不得把 category mismatch 扩散成全批次 reference-creation failure。

## 10.5 Preview/Sync 共用的 Model Category 与 MAC Fieldset 准备职责

普通 `Preview Changes`、`Sync Now` 与 scheduled sync 必须使用同一套 Model 准备逻辑，不再要求 operator 先运行独立维护按钮：

- `SnipeImportOptions.MacAddressFieldsetName` 由 operator 配置，不得硬编码为某个 Snipe-IT 默认名称；Fieldset 名称不是官方固定值。
- 当 `MacAddressCustomFieldDbColumnName` 与 `MacAddressFieldsetName` 都已配置，且完整 Model snapshot/本批次新 Model 中至少存在默认 target category 或将归一化进入该 category 的 Model 时，每次 run 必须调用一次 `GET /fieldsets`，按 HTML decode 后的名称忽略大小写解析唯一 Fieldset，并确认该 Fieldset 的 `fields.rows` 中包含配置的 MAC `db_column_name`。若只有其它 category 的 Model，则不解析 Fieldset。
- Fieldset 不存在、同名多 id、response 不完整，或 Fieldset 不包含配置的 MAC DB column 时，Preview 与真实 Sync 必须在任何 mutation 前失败，不能继续发送必然被 Snipe-IT 拒绝的 asset payload。
- 每次存在 Model planning 时必须复用完整 `GET /models` snapshot；MAC Fieldset 检查范围是当前 `ModelCategoryNormalizationTargetName`（即 GUI `Default Category`）下的全部 Model，以及本次将从来源列表归一化到该 target category 的 Model。其它 category 不检查、不修改 Fieldset。
- normalization target category 的额外解析必须延后到完整 Model snapshot 之后；只有 snapshot 中确实存在命中来源列表且尚未属于 target 的 Model 时才解析/规划 target category。只有其它 category 时不得产生无关 target-category lookup。
- 新建 Model 目标 category 等于 `ModelCategoryNormalizationTargetName` 时，`POST /models` 必须同时提交 `category_id`、可选 `manufacturer_id` 与已解析的 `fieldset_id`；其它 category 的新 Model 不自动绑定该 Fieldset。
- 上述目标范围内已有 Model 的 `fieldset.id` 与目标 Fieldset 不同或为空时，Preview 的 `snipeit-models-plan.csv` 必须输出 `Operation=Modify`；真实 Sync 在 asset writes 前通过 `PATCH /models/{id}` 写入目标 `fieldset_id`。
- `ModelCategoriesToNormalize` 中的现有 Model category 必须在同一个 Model `Modify` 计划中改为 `ModelCategoryNormalizationTargetName`。若同一 Model 同时缺少 MAC Fieldset和需要 category 归一化，只允许一条合并的 `Modify` 和一次 PATCH。
- 已有 Model 被当前 asset 引用且 category mismatch 可由本次配置的归一化计划修复时，该 asset 不再以 `ModelCategoryMismatch` 阻塞；不可由来源 category 列表修复的 mismatch 仍然阻塞。
- Model creates/updates 必须在 asset creates/updates 之前执行。任何必要 Model mutation 失败时，不得继续发送依赖准备状态的 asset writes。
- Dry-run 只生成 planned actions/counts/CSV，不发送 POST/PATCH；真实 Sync 使用相同解析、验证和 planning 结果重新计算当前状态后执行。
- 独立 `Normalize Categories` 按钮可移除或降级为兼容入口；不得继续维护与普通 Preview/Sync 不同的 category 判断实现。

`snipeit-models-plan.csv` 必须同时显示 Add 与 Modify，并包含 existing model id、当前/目标 category、当前/目标 Fieldset、manufacturer 与变更原因，使 operator 能在真实写入前确认 Model 级别影响。

## 10.6 Company 直接同名优先于 Alias 的职责

- Company planning 必须先加载一次完整 Snipe-IT company snapshot，不增加逐 asset company API 请求。
- 每条 mapped record 同时携带 Atera/customer 原始 company name 与 alias 候选。
- 若原始 company name 已在完整 snapshot 中直接命中，Preview 与 Sync 必须使用该现有 Company id 和原始名称；即使配置了指向其它名称的 alias，也不得创建或选择 alias target。
- 只有原始 company name 未命中时，才允许使用 alias 候选查找现有 Company，或按现有 `CreateMissingCompanies` policy 规划 alias target create。
- 最终选择必须在 company reference planning 开头完成，后续 category/model/asset matching、payload、preflight CSV 与 result history 全部使用同一个有效 company name。
- company snapshot 读取失败时保持现有 fail-closed 行为，不得靠 alias 猜测后继续写入。

## 10.7 Manufacturer 直接同名优先于 Alias 的职责

- 每条 mapped record 同时携带 Atera/default 原始 manufacturer name 与 alias 候选。
- Model/reference planning 对每个唯一 source manufacturer 先执行现有精确 lookup；source 命中时必须保留 source manufacturer id/name，忽略 alias。
- 只有 source lookup 返回未找到时，才查询 alias 候选。alias 命中时，后续 model-number guard、Model create 和 CSV 使用 alias manufacturer id/name。
- source 与 alias 都未命中时保持现有 lookup-only policy：以最终候选名称输出 `SnipeImport.ManufacturerMissing` warning，并允许 Model 不绑定 manufacturer；不得因 source miss 提前产生重复 warning。
- Preview 与 Sync 必须使用同一 resolution，不允许 fuzzy similarity 自动选择 manufacturer。
