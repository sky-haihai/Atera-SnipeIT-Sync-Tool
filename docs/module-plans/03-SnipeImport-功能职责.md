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
- `GET /models`
- `POST /models`
- `GET /hardware`
- `GET /hardware/byserial/{serial}`
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
- 能查询或创建 missing asset category，`category_type` 固定为 `asset`
- 能查询 manufacturer；查不到 manufacturer 时不阻塞导入，但 model 会不绑定 manufacturer
- 能查询或创建 missing model，除非 options 禁止创建
- 能按 MAC address 优先比较 existing asset
- MAC 未命中时能按 serial number 比较 existing asset
- MAC 和 serial 都未命中时能按 asset name 高相似度比较 existing asset
- 唯一可靠命中时更新 existing asset
- 没有可靠命中时创建 new hardware asset
- 每次自动创建或更新 asset 时，在 Snipe-IT notes 中写入 `Auto Synced from Atera at {UTC timestamp}`
- `Preview Changes` preflight 开启时，能在不执行真实写入的情况下输出 company/category/model/asset 临时 CSV 修改计划
- 能识别 HTTP 200 但 JSON body `status = error` 的 Snipe-IT 业务失败
- 能返回 structured actions/failures/warnings

## 7. 失败条件

以下情况应产生 `ImportFailure`，并继续处理 batch 中其它 asset：

- options 缺少 base URL 或 API token
- company 缺失且不允许创建
- company/category/model reference 创建失败，导致 asset 写入必须停止
- model 缺失且不允许创建
- MAC address 命中多个不同 asset
- serial number response 表示 error 或 ambiguous state
- name similarity 命中多个高置信度候选
- Snipe-IT 返回 non-success HTTP status
- Snipe-IT 返回 `status = error`
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
- company/model/category/manufacturer cache
