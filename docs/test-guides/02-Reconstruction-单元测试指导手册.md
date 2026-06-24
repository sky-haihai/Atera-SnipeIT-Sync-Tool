# Reconstruction - 单元测试指导手册

## 1. 测试范围

本测试手册覆盖 Module2 Reconstruction 的纯 mapping 逻辑。

测试目标：

- `InventoryMapper` 可以把 `AteraPullResult` 转换为 `SnipeImportBatch`
- representative `AteraPullResult` 可以适配成 Snipe-IT Import 模块消费的 batch
- serial number 优先作为 `AssetTag`
- serial 缺失时 fallback 到 `ATERA-{AgentID}`
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
- company alias matching should trim names and ignore source casing
- missing serial 但 agent id 存在时应生成 asset，不应跳过
- serial 和 agent id 都缺失时应跳过 asset，并输出 `MissingAgentIdentity`
- `SourceAgentCount` 统计输入 agent 总数，`MappedAssetCount` 只统计成功生成的 asset 数量
