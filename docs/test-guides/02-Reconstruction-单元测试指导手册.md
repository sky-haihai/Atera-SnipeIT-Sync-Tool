# Reconstruction - 单元测试指导手册

## 1. 测试范围

本测试手册覆盖 Module2 Reconstruction 的纯 mapping 逻辑。

测试目标：

- `InventoryMapper` 可以把 `AteraPullResult` 转换为 `SnipeImportBatch`
- representative `AteraPullResult` 可以适配成 Snipe-IT Import 模块消费的 batch
- 所有 mapped assets 都使用 `ATERA-{SourceId}` 作为 `AssetTag`
- serial 作为独立 identity/audit 字段，不再决定 Asset Tag
- company / manufacturer / model 缺失时使用 `MappingOptions` 默认值
- company alias 命中时把 Atera/customer company name 映射到 canonical Snipe-IT company name
- 不可识别 agent 被跳过并输出 warning
- summary count 正确
- `source` / `options` 为 `null` 时抛出 `ArgumentNullException`

## 2. 测试文件

测试位于：

```text
tests/AteraSnipeSync.Tests/Mapping/InventoryMapperTests.cs
tests/AteraSnipeSync.Tests/Mapping/ReconstructionBoundaryTests.cs
```

生产代码位于：

```text
src/AteraSnipeSync.Core/Mapping/
```

## 3. 测试命令

推荐从仓库根目录运行：

```powershell
dotnet build AteraSnipeSync.sln --no-restore
dotnet test AteraSnipeSync.sln --no-build
```

只运行 Reconstruction 相关测试：

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~AteraSnipeSync.Tests.Mapping"
```

只运行 Reconstruction boundary test class：

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~ReconstructionBoundaryTests"
```

只运行 representative Atera pull result 到 Snipe import batch 的边界测试：

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~Map_ProducesSnipeImportBatchFromRepresentativeAteraPullResult"
```

如果依赖尚未 restore，先运行：

```powershell
dotnet restore AteraSnipeSync.sln
```

## 4. Mocking 策略

Module2 是纯逻辑模块，不调用外部 API，不读写文件，不需要 mock HTTP client。

unit tests 直接构造：

- `AteraPullResult`
- `AgentInfo`
- `MappingOptions`

boundary test 也直接构造 representative `AteraPullResult`，验证它能映射为 `SnipeImportBatch`。

不使用 fixture JSON，避免把 Atera API wire shape 固化在 Module2 测试中。真实 Atera API response 不得进入自动化测试；Module1 Atera Pull 的 wire behavior 测试应使用官方文档形状和 mocked HTTP。

## 5. 常见失败原因

- `AgentInfo.AgentId` 或 `Name` 是 required property，测试 helper 必须赋值
- warning code 必须与技术规格一致
- company alias matching should trim names, ignore source casing, collapse whitespace, and treat common dash-like characters as `-`
- missing serial 但 agent id 存在时应生成 asset，不应跳过
- serial 和 agent id 都缺失时应跳过 asset，并输出 `MissingAgentIdentity`
- `SourceAgentCount` 统计输入 agent 总数，`MappedAssetCount` 只统计成功生成的 asset 数量

## 6. Ignored MAC 测试

`Map_KeepsIgnoredMacsButDoesNotReportDuplicateMacWarning` 验证多个 record 共享配置忽略的 MAC 时，原始 `MacAddresses` 仍保留该值，但不会生成 `DuplicateMacAddress` warning。运行：

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~Map_KeepsIgnoredMacsButDoesNotReportDuplicateMacWarning"
```

## 7. Virtual Machine 共享 serial 测试

- `Map_UsesAteraIdentityAndKeepsSerialForAudit_WhenVirtualMachinesShareSerial`：重复 serial 的 `Virtual Machine` records 使用不同 `ATERA-{SourceId}` asset tags，保留 serial 但标记为 audit-only。
- `Map_KeepsDuplicateSerialProtection_WhenPhysicalMachinesShareSerial`：普通 model 的重复 serial 继续产生 `DuplicateSerial`，但不同 SourceId 的 Asset Tag 不冲突。
- `Map_OnlyAppliesSharedSerialFallbackToVirtualMachine_InMixedModelGroup`：mixed group 只转换 VM record。

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~InventoryMapperTests"
```

## 8. Device Type Category 路由测试

运行：

```powershell
dotnet test tests/AteraSnipeSync.Tests/AteraSnipeSync.Tests.csproj --no-restore --filter "FullyQualifiedName~InventoryMapperTests"
```

重点用例：

- `Map_UsesDefaultCategoryAndPreservesDeviceType_WhenDeviceTypeIsServer`：trim 后的 `Server` 保留在 `DeviceType`，但 category 仍使用单一 `DefaultCategoryName`。
- `Map_UsesDefaultCategory_ForNonServerAndMissingDeviceTypes`：Workstation 与空 DeviceType 同样使用 `DefaultCategoryName`。
- ignored-device-type 用例继续验证 ignore gate 先于 mapping。
- Virtual Machine shared-serial replacement 必须保留 `DeviceType`。

这些测试是纯 mapping tests，不访问 Atera 或 Snipe-IT。

## 9. 统一 Atera Asset Tag 测试

- `Map_UsesAteraSourceIdAssetTag_WhenSerialExists`：即使 serial 有效，Asset Tag 仍为 `ATERA-{AgentID}`。
- `Map_UsesAteraSourceIdAssetTagAndAddsWarning_WhenSerialMissing`：缺 serial 时沿用相同 Asset Tag 规则。
- `Map_UsesSerialBackedSourceIdInAteraAssetTag_WhenAgentIdMissing`：兼容缺 Agent ID 的旧记录，使用有效 serial 作为 SourceId，生成 `ATERA-{serial}`。
- `ReconstructionBoundaryTests` 验证整个 batch 中有/无 serial 的记录都使用统一前缀。

## 10. Manufacturer Alias 测试

- `Map_UsesManufacturerAlias_WhenManufacturerMatchesAlias`：配置 `Dell Inc.=Dell` 后，mapped `ManufacturerName` 保留 source `Dell Inc.`，`ManufacturerAliasName` 保存候选 `Dell`。
- `Map_UsesManufacturerAlias_WhenManufacturerHasEquivalentWhitespaceAndDash`：alias key 比较忽略 NBSP、连续空白与常见 dash 差异，同时 source manufacturer 保持不变。
- mapper 只携带 source 与 alias candidate；是否使用 alias 由 Snipe Import 根据目标库状态决定，source 同名存在时必须优先。
- alias tests 为纯 mapping tests，不调用外部 API，也不进行 fuzzy similarity 自动匹配。
