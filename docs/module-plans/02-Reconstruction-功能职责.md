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
- 有 serial number 时优先使用 serial number 作为 primary identity
- 缺少 serial number 时 fallback 到 `ATERA-{AgentID}`
- 缺少 company 时使用 default company
- company alias 命中时，把 Atera/customer company name 映射成 canonical Snipe-IT company name
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
