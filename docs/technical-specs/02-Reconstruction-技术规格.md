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
    public static string Create(AteraAgentDto agent);
}
```

职责：

- 如果 `agent.SerialNumber` 非空白，返回 trimmed serial number 作为 `AssetTag`
- 如果 serial 缺失，返回 `ATERA-{AgentID}`

规则：

- `AgentID` 使用 `agent.AgentId.Trim()`
- 当 `agent.AgentId` 为空白且 serial 也为空白时，`InventoryMapper` 不生成 asset，并产生 warning

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
        AteraAgentDto agent,
        MappingOptions options,
        ICollection<ModuleWarning> warnings);

    public static string ResolveManufacturerName(
        AteraAgentDto agent,
        MappingOptions options,
        ICollection<ModuleWarning> warnings);

    public static string ResolveModelName(
        AteraAgentDto agent,
        MappingOptions options,
        ICollection<ModuleWarning> warnings);

    public static string ResolveCategoryName(
        AteraAgentDto agent,
        MappingOptions options);
}
```

职责：

- 解析 company / manufacturer / model / category 字段
- 对缺失字段使用 `MappingOptions` 默认值
- 对 company / manufacturer / model fallback 产生 warning

字段映射：

- `CompanyName` 优先使用 `agent.CustomerName`，否则使用 `options.DefaultCompanyName`
- `ManufacturerName` 优先使用 `agent.Manufacturer`，否则使用 `options.DefaultManufacturerName`
- `ModelName` 优先使用 `agent.Model`，否则使用 `options.DefaultModelName`
- `CategoryName` 使用 `options.DefaultCategoryName`

### 3.4 `NotesBuilder`

Namespace:

```csharp
namespace AteraSnipeSync.Core.Mapping;
```

Signature:

```csharp
internal static class NotesBuilder
{
    public static string Build(AteraAgentDto agent);
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
    public static ModuleWarning MissingAgentIdentity(AteraAgentDto agent);
    public static ModuleWarning MissingSerialNumber(AteraAgentDto agent);
    public static ModuleWarning MissingCompany(AteraAgentDto agent);
    public static ModuleWarning MissingManufacturer(AteraAgentDto agent);
    public static ModuleWarning MissingModel(AteraAgentDto agent);
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
- `Message` 必须包含 Atera agent id 或 device name，方便定位问题

## 4. Mapping 行为

对每个 `AteraAgentDto agent`：

1. 如果 `agent.SerialNumber` 和 `agent.AgentId` 都为空白：
   - 不生成 `SnipeAssetImportRecord`
   - 添加 `MissingAgentIdentity` warning
2. 否则生成 asset：
   - `AssetTag`: serial 优先，否则 `ATERA-{AgentID}`
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
   - `SourceId`: `agent.AgentId.Trim()`；如果 agent id 为空但 serial 存在，使用 serial

`MappingSummary`：

- `SourceAgentCount = source.Agents.Count`
- `MappedAssetCount = number of generated SnipeAssetImportRecord`

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

1. `Map_UsesSerialNumberAsAssetTag_WhenSerialExists`
2. `Map_UsesAteraAgentIdFallbackAssetTagAndAddsWarning_WhenSerialMissing`
3. `Map_UsesDefaultCompanyAndAddsWarning_WhenCustomerNameMissing`
4. `Map_UsesDefaultManufacturerAndAddsWarning_WhenManufacturerMissing`
5. `Map_UsesDefaultModelAndAddsWarning_WhenModelMissing`
6. `Map_SkipsAgentAndAddsWarning_WhenSerialAndAgentIdMissing`
7. `Map_PopulatesSummaryCounts`
8. `Map_ThrowsArgumentNullException_WhenSourceIsNull`
9. `Map_ThrowsArgumentNullException_WhenOptionsIsNull`

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
