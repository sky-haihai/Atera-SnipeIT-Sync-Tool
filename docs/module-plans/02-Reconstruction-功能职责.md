# Reconstruction - 功能职责

## 1. 模块目标

Reconstruction Module 负责把 Atera Pull Module 输出的原始 inventory 数据转换成 Snipe-IT Import Module 可消费的 import batch。

本模块是纯逻辑模块。它只做数据重建、字段 fallback、identity 选择、warning 收集和 summary 生成。

## 2. 输入

主要输入：

- `AteraPullResult`
- `MappingOptions`

`AteraPullResult` 应包含：

- Atera agents
- Atera customers
- pull summary
- pull warnings

`MappingOptions` 应包含：

- default company name
- default manufacturer name
- default model name
- default category name
- default status id
- company alias map, keyed by Atera/customer company name and valued by canonical Snipe-IT company name

## 3. 输出

主要输出：

- `SnipeImportBatch`

`SnipeImportBatch` 应包含：

- zero or more `SnipeAssetImportRecord`
- `MappingSummary`
- mapping warnings

## 4. 对外接口

本模块通过 `IInventoryMapper` 暴露能力：

```csharp
public interface IInventoryMapper
{
    SnipeImportBatch Map(
        AteraPullResult source,
        MappingOptions options);
}
```

调用方只需要传入 Atera pull result 和 mapping options，不需要知道内部字段重建规则。

## 5. 成功条件

一次 mapping 成功时应满足：

- 每个可识别的 Atera agent 被转换成一个 `SnipeAssetImportRecord`
- 每个输出 asset 的 `AssetTag` 都使用 `ATERA-{SourceId}`，明确标记其 Atera 来源
- `SourceId` 优先使用 Atera Agent ID；Agent ID 缺失但有有效 serial 时才以 serial 作为兼容 fallback
- 有可靠 serial number 时仍把 serial 作为独立 strong identity，用于目标匹配与冲突检测，但不再用于生成 Asset Tag
- 缺少 company 时使用 default company
- company alias 命中时，record 同时保留 Atera/customer 原始 company name 与 canonical Snipe-IT alias 候选；Reconstruction 不得在看不到 Snipe company snapshot 的情况下永久丢弃原名
- 缺少 manufacturer 时使用 default manufacturer
- 缺少 model 时使用 default model
- category 使用 default category
- status id 使用 default status id
- notes 中包含可追溯的 Atera 来源信息
- 输出 summary 能反映 source agent count 和 mapped asset count
- fallback 或跳过记录时输出 structured warning

## 6. 失败条件

本模块不应因为普通字段缺失而整体失败。以下情况应作为 warning 处理：

- missing company
- missing manufacturer
- missing model
- missing serial number but agent id exists

以下情况无法生成 import record：

- serial number 缺失，并且 agent id 也缺失

当 agent 无法识别时：

- 跳过该 agent
- 添加 warning
- 继续处理后续 agents

参数本身无效时：

- `source` 为 `null` 应视为调用错误
- `options` 为 `null` 应视为调用错误

## 7. 不负责的事情

Reconstruction Module 不负责：

- 调用 Atera API
- 调用 Snipe-IT API
- 查询 Snipe-IT 中 company 是否存在
- 查询 Snipe-IT 中 model 是否存在
- 查询 Snipe-IT 中 asset 是否存在
- 创建或更新 Snipe-IT company/model/asset
- 读取或写入配置文件
- 读取或写入 status file
- 写 log file
- 处理 retry
- 判断 sync run 最终是否成功
- 发送 notification
- 提供 UI
- 决定定时运行策略

## 8. 扩展点

后续可以在不改变模块边界的前提下扩展：

- company name override
- company alias table
- model name override
- manufacturer normalization
- category mapping table
- custom field mapping
- richer notes construction
- additional mapping warnings
- configurable identity priority

这些扩展仍应保持纯逻辑，不引入 API、file IO、UI 或 runtime scheduler 依赖。

## 8.1 Device Type Ignore Extension

The module must support an operator-configured `MappingOptions.IgnoredDeviceTypes` collection. Atera's official Swagger
definition for `/api/v3/agents` returns `APIResultWrapper[AgentQueryDTO]`, and `AgentQueryDTO` includes `DeviceType` as
a string property. The Swagger document does not define a closed enum for this field, so the mapper must not invent or
depend on a fixed list of possible values.

When an Atera agent has a non-blank `DeviceType` that matches a configured ignored device type after trimming and
case-insensitive comparison:

- the agent must not produce a `SnipeAssetImportRecord`
- the original `AteraPullResult` remains unchanged
- `MappingSummary.SourceAgentCount` still counts the source agent
- `MappingSummary.MappedAssetCount` excludes the skipped agent
- a structured Reconstruction warning records that the agent was skipped by device type

Blank or missing `DeviceType` values must not be skipped unless a future API contract provides a documented sentinel
value and the technical spec is updated.

## 9. 与后续文档的关系

本文件定义模块职责边界。

后续 `docs/technical-specs/02-Reconstruction-技术规格.md` 必须以本职责文档为依据，进一步明确：

- 具体 class/interface 设计
- method signatures
- public properties
- class calling relationships
- validation rules
- warning codes
- unit test cases

## 13. 2026-07 身份数据加固职责

- 常见固件占位 serial（例如 `To Be Filled By O.E.M.`、`Default string`、`Unknown`、`N/A`）必须按缺失值处理，不能作为 SourceId fallback，也不能参与强身份匹配。
- 同一 mapping batch 内重复的 source id、asset tag、有效 serial 或规范化 MAC 必须被识别并输出结构化 warning；Snipe Import 会把这类冲突升级为阻塞 failure，避免两条 source record 写入同一目标。
- `ATERA-{SourceId}` asset tag 必须保持确定性、可重复，并对所有由 Reconstruction 输出的资产统一应用。
- 身份规范化必须是纯逻辑并由单元测试覆盖，不访问任何外部 API。
- ignored MAC addresses that remain on mapped records for audit but do not produce duplicate-MAC mapping warnings

## 14. Virtual Machine 共享 serial 例外

部分 Atera 环境会让多个独立虚拟机使用同一个宿主机/固件 serial，但每个虚拟机仍有不同的 Atera agent id。Reconstruction 必须只在同一 batch 内 serial 确实重复、且 record 的规范化 `ModelName` 等于 `Virtual Machine` 时应用以下规则：

- `AssetTag` 保持全局统一规则生成的 `ATERA-{SourceId}`；VM 例外不再需要改变 tag，只改变 serial 的身份可靠性。
- 原始规范化 serial 保留在 `SnipeAssetImportRecord.Serial`，供 Preview 与审计显示。
- `SerialIsReliableIdentity` 设为 `false`，明确该 serial 不参与 batch duplicate gate、目标匹配或 Snipe-IT serial payload。
- 输出 `SharedVirtualMachineSerial` structured warning，说明受影响 source id 与 fallback 行为。
- 同组中非 `Virtual Machine` 的 record 不应用例外；物理设备之间的重复 serial 仍输出 `DuplicateSerial` 并由 import 阻塞。

该规则不依赖 MAC 是否存在或唯一。Atera agent id 必须已通过现有 source-id uniqueness gate；否则仍按重复 source id 阻塞。

## 15. Device Type 审计与单一默认 Category 职责

- `MappingOptions` 必须提供单一 `DefaultCategoryName`；所有未被忽略的 Atera agent（包括 `DeviceType=Server`）都映射到该 category，默认配置为 `Computer`。
- `SnipeAssetImportRecord` 必须保留 trim 后的原始 `DeviceType`；空或缺失值保留为 `null`，不得根据 model 名称猜测 device type。
- Category 赋值发生在纯 mapping 阶段，不调用 Snipe-IT API。SnipeImport 只消费 record 上已经确定的 `CategoryName` 与用于审计的 `DeviceType`。
- 被 `IgnoredDeviceTypes` 命中的 agent 仍在分类前跳过，不产生 import record。
- 成功条件：Server、PC、Workstation、Mac、Linux、SNMP、TCP、未知非空值与空值均输出同一个默认 Computer category，并保留可审计的 DeviceType。
- 失败边界：默认 category 配置缺失或空白时，由配置/UI/Worker composition 在运行前拒绝，不允许真实同步使用猜测值。

## 16. Manufacturer Alias 职责

- `MappingOptions` 必须接收 operator 配置的 `ManufacturerAliases`，key 为 Atera manufacturer，value 为 canonical Snipe-IT manufacturer。
- manufacturer alias 与 company alias 使用相同的确定性比较规则：trim、忽略大小写、折叠连续空白、把 non-breaking space 视为普通空格，并把常见 dash-like 字符规范为 `-`。
- mapped record 的 `ManufacturerName` 必须保留 Atera/default source 名称，`ManufacturerAliasName` 保存配置右侧的 canonical 候选；缺失 manufacturer 的默认值也允许生成 alias 候选。
- 不允许使用 fuzzy similarity 自动合并 manufacturer。未配置 alias 的名称保持原值，由 Snipe Import 按现有安全规则解析或阻塞。
- Preview、Sync Now 与 Worker 必须消费同一 mapped batch，因此不得分别实现 manufacturer alias 分支。
