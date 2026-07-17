# Reconstruction - 技术规格

## 1. 目标

Reconstruction Module 负责把 `AteraPullResult` 转换为 `SnipeImportBatch`。

本模块必须是纯逻辑模块：

- 不调用 Atera API
- 不调用 Snipe-IT API
- 不读写文件
- 不写 log file
- 不创建外部资源
- 不处理 retry

本技术规格用于后续 AI agent 直接生成 production code 和 unit tests。实现时应严格按本规格创建类、方法、调用关系和测试，不自行引入额外模块边界。

## 2. 现有 Contract

使用 `AteraSnipeSync.Core` 中已经存在的 contracts：

```csharp
namespace AteraSnipeSync.Core.Mapping;

public interface IInventoryMapper
{
    SnipeImportBatch Map(
        AteraPullResult source,
        MappingOptions options);
}
```

```csharp
public sealed class MappingOptions
{
    public required string DefaultCompanyName { get; init; }
    public required string DefaultManufacturerName { get; init; }
    public required string DefaultModelName { get; init; }
    public required string DefaultCategoryName { get; init; }
    public required int DefaultStatusId { get; init; }
    public IReadOnlyDictionary<string, string> CompanyAliases { get; init; }
    public IReadOnlyList<string> IgnoredDeviceTypes { get; init; }
    public IReadOnlyList<string> IgnoredMacAddresses { get; init; }
}
```

```csharp
public sealed class MappingSummary
{
    public required int SourceAgentCount { get; init; }
    public required int MappedAssetCount { get; init; }
}
```

```csharp
public sealed class SnipeImportBatch
{
    public required IReadOnlyList<SnipeAssetImportRecord> Assets { get; init; }
    public required MappingSummary Summary { get; init; }
    public required IReadOnlyList<ModuleWarning> Warnings { get; init; }
}
```

```csharp
public sealed class SnipeAssetImportRecord
{
    public required string AssetTag { get; init; }
    public required string Name { get; init; }
    public string? Serial { get; init; }
    public required string CompanyName { get; init; }
    public required string ManufacturerName { get; init; }
    public required string ModelName { get; init; }
    public required string CategoryName { get; init; }
    public required int StatusId { get; init; }
    public required string Notes { get; init; }
    public required string SourceSystem { get; init; }
    public required string SourceId { get; init; }
}
```

## 3. 新增 Production 类

在 `src/AteraSnipeSync.Core/Mapping/` 下新增以下类。

### 3.1 `InventoryMapper`

Namespace:

```csharp
namespace AteraSnipeSync.Core.Mapping;
```

Signature:

```csharp
public sealed class InventoryMapper : IInventoryMapper
{
    public SnipeImportBatch Map(
        AteraPullResult source,
        MappingOptions options);
}
```

职责：

- 遍历 `source.Agents`
- 为每个 Atera agent 生成一个 `SnipeAssetImportRecord`
- 聚合 mapping warnings
- 生成 `MappingSummary`
- 返回 `SnipeImportBatch`

公有成员：

- `Map(AteraPullResult source, MappingOptions options)`

参数验证：

- `source` 为 `null` 时抛出 `ArgumentNullException`
- `options` 为 `null` 时抛出 `ArgumentNullException`

调用关系：

```text
InventoryMapper.Map
  -> AssetTagFactory.Create
  -> MappingValueResolver.ResolveCompanyName
  -> MappingValueResolver.ResolveManufacturerName
  -> MappingValueResolver.ResolveModelName
  -> MappingValueResolver.ResolveCategoryName
  -> NotesBuilder.Build
  -> MappingWarningFactory helpers
```

### 3.2 `AssetTagFactory`

Namespace:

```csharp
namespace AteraSnipeSync.Core.Mapping;
```

Signature:

```csharp
internal static class AssetTagFactory
{
    public static string Create(string sourceId);
}
```

职责：

- 对每个可映射 record 返回 `ATERA-{sourceId.Trim()}`
- 不读取或判断 serial、model、MAC 等其它 identity 字段

规则：

- `InventoryMapper` 先计算 `SourceId = normalized AgentID ?? normalized valid serial`
- `sourceId` 传入 factory 前必须已验证为非空
- 当 Agent ID 与有效 serial 都为空时，`InventoryMapper` 不生成 asset，并产生 warning

### 3.3 `MappingValueResolver`

Namespace:

```csharp
namespace AteraSnipeSync.Core.Mapping;
```

Signature:

```csharp
internal static class MappingValueResolver
{
    public static string ResolveCompanyName(
        AgentInfo agent,
        MappingOptions options,
        ICollection<ModuleWarning> warnings);

    public static string ResolveManufacturerName(
        AgentInfo agent,
        MappingOptions options,
        ICollection<ModuleWarning> warnings);

    public static string ResolveModelName(
        AgentInfo agent,
        MappingOptions options,
        ICollection<ModuleWarning> warnings);

    public static string ResolveCategoryName(
        AgentInfo agent,
        MappingOptions options);
}
```

职责：

- 解析 company / manufacturer / model / category 字段
- 对缺失字段使用 `MappingOptions` 默认值
- 对 company 解析 Atera/customer 原名并应用 `MappingOptions.CompanyAliases` 生成 canonical Snipe-IT alias 候选；两个值都传给 Snipe Import，由完整 target snapshot 决定优先级
- 对 company / manufacturer / model fallback 产生 warning

字段映射：

- `CompanyName` 优先使用 `agent.CustomerName`，否则使用 `options.DefaultCompanyName`，并始终保留该 source 名称
- `CompanyAliasName` 保存 alias 命中后的 canonical 候选；无 alias 或 alias target 与 source 等价时为 `null`
- `CompanyAliases` lookup 应 trim source/target；source 比较应忽略大小写，折叠连续空白，把 non-breaking space 视为普通空格，并把常见 dash-like 字符规范成 `-` 后比较；alias target 为空白时忽略该 alias
- Reconstruction 不查询 Snipe-IT，因此不得决定 source direct match 与 alias target 的最终优先级
- `ManufacturerName` 优先使用 `agent.Manufacturer`，否则使用 `options.DefaultManufacturerName`
- `ModelName` 优先使用 `agent.Model`，否则使用 `options.DefaultModelName`
- `CategoryName` 对所有未忽略的 DeviceType 使用 `options.DefaultCategoryName`

### 3.4 `NotesBuilder`

Namespace:

```csharp
namespace AteraSnipeSync.Core.Mapping;
```

Signature:

```csharp
internal static class NotesBuilder
{
    public static string Build(AgentInfo agent);
}
```

职责：

- 构造可读的 Snipe-IT notes 字符串
- 不读取外部资源
- 不包含秘密值或 API token

输出格式：

```text
Imported from Atera.
Atera Agent ID: {AgentId}
Atera Device Name: {Name}
Atera Customer ID: {CustomerId}
Atera Customer Name: {CustomerName}
```

缺失字段行可以保留为空值文本，但不得抛异常。

### 3.5 `MappingWarningFactory`

Namespace:

```csharp
namespace AteraSnipeSync.Core.Mapping;
```

Signature:

```csharp
internal static class MappingWarningFactory
{
    public static ModuleWarning MissingAgentIdentity(AgentInfo agent);
    public static ModuleWarning MissingSerialNumber(AgentInfo agent);
    public static ModuleWarning MissingCompany(AgentInfo agent);
    public static ModuleWarning MissingManufacturer(AgentInfo agent);
    public static ModuleWarning MissingModel(AgentInfo agent);
    public static ModuleWarning IgnoredDeviceType(AgentInfo agent);
}
```

Warning contract：

- `Source` 固定为 `"Reconstruction"`
- `Code` 使用以下值：
  - `"MissingAgentIdentity"`
  - `"MissingSerialNumber"`
  - `"MissingCompany"`
  - `"MissingManufacturer"`
  - `"MissingModel"`
  - `"IgnoredDeviceType"`
- `Message` 必须包含 Atera agent id 或 device name，方便定位问题

### 3.6 Device Type Ignore Rules

Atera official Swagger `/api/v3/agents` returns `APIResultWrapper[AgentQueryDTO]`; `AgentQueryDTO` contains a
`DeviceType` string property. The spec does not define an enum. Implement filtering as plain string matching:

- build a normalized ignored-device-type set from `MappingOptions.IgnoredDeviceTypes`
- trim configured values and ignore blanks
- compare `agent.DeviceType` with `StringComparer.OrdinalIgnoreCase` after trimming
- skip only non-blank `agent.DeviceType` values that match the ignored set
- add `IgnoredDeviceType` warning when a record is skipped by this rule
- do not change Atera pull DTOs, Snipe-IT import DTOs, or API payload shapes for this feature

## 4. Mapping 行为

对每个 `AgentInfo agent`：

1. If `options.IgnoredDeviceTypes` contains the trimmed `agent.DeviceType` using case-insensitive comparison:
   - do not generate a `SnipeAssetImportRecord`
   - add `IgnoredDeviceType` warning
   - continue to the next agent
2. 如果 `agent.SerialNumber` 和 `agent.AgentId` 都为空白：
   - 不生成 `SnipeAssetImportRecord`
   - 添加 `MissingAgentIdentity` warning
3. 否则生成 asset：
   - `SourceId`: `agent.AgentId.Trim()`；如果 agent id 为空但 serial 存在，使用 serial
   - `AssetTag`: `ATERA-{SourceId}`
   - 当 serial 缺失但 agent id 存在时，仍生成 asset，并添加 `MissingSerialNumber` warning
   - `Name`: `agent.Name.Trim()`；如果为空白，使用 `AssetTag`
   - `Serial`: trimmed serial；如果为空白则为 `null`
   - `CompanyName`: agent customer name fallback 到 default company
   - `ManufacturerName`: agent manufacturer fallback 到 default manufacturer
   - `ModelName`: agent model fallback 到 default model
   - `CategoryName`: default category
   - `StatusId`: default status id
   - `Notes`: `NotesBuilder.Build(agent)`
   - `SourceSystem`: `"Atera"`

`MappingSummary`：

- `SourceAgentCount = source.Agents.Count`
- `MappedAssetCount = number of generated SnipeAssetImportRecord`, excluding agents skipped by ignored device type

## 5. 不做事项

实现时不得添加：

- Atera API client
- Snipe-IT API client
- HTTP request / response DTO
- 查询 Snipe-IT 中 company 是否存在
- 查询 Snipe-IT 中 model 是否存在
- 查询 Snipe-IT 中 asset 是否存在
- 创建或更新 Snipe-IT company/model/asset
- database/file persistence
- logging implementation
- dependency injection registration
- background worker changes
- WinForms UI changes

## 6. Unit Tests

在 `tests/AteraSnipeSync.Tests/Mapping/InventoryMapperTests.cs` 中新增测试。

必须覆盖：

1. `Map_UsesAteraSourceIdAssetTag_WhenSerialExists`
2. `Map_UsesAteraSourceIdAssetTagAndAddsWarning_WhenSerialMissing`
3. `Map_UsesDefaultCompanyAndAddsWarning_WhenCustomerNameMissing`
4. `Map_UsesDefaultManufacturerAndAddsWarning_WhenManufacturerMissing`
5. `Map_UsesDefaultModelAndAddsWarning_WhenModelMissing`
6. `Map_SkipsAgentAndAddsWarning_WhenSerialAndAgentIdMissing`
7. `Map_PopulatesSummaryCounts`
8. `Map_ThrowsArgumentNullException_WhenSourceIsNull`
9. `Map_ThrowsArgumentNullException_WhenOptionsIsNull`
10. `Map_SkipsAgentAndAddsWarning_WhenDeviceTypeIgnored`
11. `Map_UsesSerialBackedSourceIdInAteraAssetTag_WhenAgentIdMissing`

Test helpers:

- Use private helper method `CreateDefaultOptions()`
- Use private helper method `CreateAgent(...)`
- Do not use external files or fixture JSON for these unit tests

## 7. Acceptance Criteria

完成后必须满足：

- `InventoryMapper` implements `IInventoryMapper`
- 所有 production code 位于 `AteraSnipeSync.Core`
- 没有任何 HTTP、file IO、UI、worker scheduler 实现
- `dotnet build AteraSnipeSync.sln --no-restore` 通过
- `dotnet test AteraSnipeSync.sln --no-build` 通过

## 13. 2026-07 身份规范化设计

新增 `internal static class HardwareIdentityNormalizer`：

```csharp
internal static string? NormalizeSerial(string? value);
internal static bool IsPlaceholderSerial(string? value);
```

`NormalizeSerial` trim 后对常见 BIOS/OEM placeholder 返回 `null`，否则返回原有有效值。`InventoryMapper` 必须使用该 helper，确保 placeholder 既不作为 serial 输出、也不作为缺失 Agent ID 时的 `SourceId` fallback。`AssetTagFactory` 只消费已经验证的 `SourceId`。

`InventoryMapper` 在完成 records 后按 case-insensitive source id/asset tag/serial 与 `MacAddressNormalizer` 结果分组，为每个冲突 record 添加 warning code：`DuplicateSourceId`、`DuplicateAssetTag`、`DuplicateSerial` 或 `DuplicateMacAddress`。MAC 分组前必须排除 `MappingOptions.IgnoredMacAddresses` 的 normalized comparable values；ignored MAC 仍保留在输出 record 中。warning 只描述脱敏后的 identity 类型与受影响 source id，不包含 secret。

## 14. Virtual Machine 共享 serial 技术设计

`SnipeAssetImportRecord` 新增：

```csharp
public bool SerialIsReliableIdentity { get; init; } = true;
```

`InventoryMapper.Map` 完成初始 records 后调用纯逻辑 helper 处理 duplicate normalized serial group。对组内 `ModelName.Trim()` 等于 `Virtual Machine`（case-insensitive）的每条 record：

- 以完整 init-property copy 产生 replacement record。
- `AssetTag` 保持初始 mapping 已生成的 `ATERA-{SourceId}`。
- `Serial` 保持不变。
- `SerialIsReliableIdentity = false`。
- 添加 warning code `SharedVirtualMachineSerial`；同一 serial group 只添加一个 warning，并列出受影响 source ids。

后续 `DuplicateSerial` warning selector 只包含 `SerialIsReliableIdentity == true` 的 records。source id、asset tag 与非 ignored MAC 的重复检测保持不变。

必须新增单元测试：

- 两个 `Virtual Machine` records 共享 serial 时得到不同 `ATERA-{SourceId}` asset tag、保留 serial、flag 为 false、且不产生 `DuplicateSerial`/`DuplicateAssetTag`。
- 物理 model records 共享 serial 时仍保留 serial identity 并产生 `DuplicateSerial`；其不同 SourceId 生成的 Asset Tag 不冲突。
- mixed group 只对 `Virtual Machine` record 应用 fallback，非 VM record 仍保留可靠 serial identity。

## 15. Device Type 审计与单一 Category 映射技术规格

`MappingOptions` 提供一个必填 public property：

```csharp
public required string DefaultCategoryName { get; init; }
```

`SnipeAssetImportRecord` 新增：

```csharp
public string? DeviceType { get; init; }
```

`MappingValueResolver.ResolveCategoryName(AgentInfo agent, MappingOptions options)` 保持现有签名，并对所有未忽略 agent 返回 `options.DefaultCategoryName`。`DeviceType` 不参与 category 选择。

`InventoryMapper.Map` 创建 record 时同时设置 `DeviceType = InventoryMapper.Normalize(agent.DeviceType)`。`ApplySharedVirtualMachineSerialFallback` 做 replacement copy 时必须复制 `DeviceType`，不得丢失分类审计字段。

必需 tests：

1. `Map_UsesDefaultCategoryAndPreservesDeviceType_ForServerAndOtherTypes`：覆盖 Server、其它值、trim 与空值。
2. 所有上述 records 的 `CategoryName` 均为 `DefaultCategoryName`。
3. 现有 ignored-device-type test 继续证明 ignore gate 先于 record/category 生成。
4. Virtual Machine serial fallback test 断言 replacement record 保留 `DeviceType`。

## 16. Manufacturer Alias 技术规格

`MappingOptions` 新增：

```csharp
public IReadOnlyDictionary<string, string> ManufacturerAliases { get; init; } = new Dictionary<string, string>();
```

`MappingValueResolver.ResolveManufacturerName` 必须先取得 trim 后的 `agent.Manufacturer`；为空时产生现有 `MissingManufacturer` warning 并使用 `DefaultManufacturerName`。该 source 值写入 `ManufacturerName`。`ResolveManufacturerAliasName` 使用共享 alias resolver 生成可选 `ManufacturerAliasName`，但不得覆盖 source 值。

共享 alias resolver 接收 source value 与 alias dictionary，忽略空白 key/value。key 比较规则必须与 company alias 一致：case-insensitive、折叠 whitespace、NBSP 等价普通空格、常见 dash-like 字符等价 `-`。命中时返回 trim 后 target；未命中返回 trim 后 source。不得计算 Levenshtein/Jaro-Winkler 或其它 fuzzy similarity 作为自动匹配依据。

必需 tests：

1. `Map_UsesManufacturerAlias_WhenManufacturerMatchesAlias`：`ManufacturerName` 保留 `Dell Inc.`，`ManufacturerAliasName` 输出 `Dell`。
2. manufacturer alias key 的等价 whitespace/dash 形式能够命中。
3. 未配置 alias 时 manufacturer 原值保持不变。
4. Preview integration test 证明 mapped canonical manufacturer 能解析现有 Snipe manufacturer，并参与唯一 model-number reuse；测试只使用 mocked HTTP。
