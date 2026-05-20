# Reconstruction - 单元测试指导手册

## 1. 测试范围

本测试手册覆盖 Module2 Reconstruction 的纯 mapping 逻辑。

测试目标：

- `InventoryMapper` 可以把 `AteraPullResult` 转换为 `SnipeImportBatch`
- serial number 优先作为 `AssetTag`
- serial 缺失时 fallback 到 `ATERA-{AgentID}`
- company / manufacturer / model 缺失时使用 `MappingOptions` 默认值
- 不可识别 agent 被跳过并输出 warning
- summary count 正确
- `source` / `options` 为 `null` 时抛出 `ArgumentNullException`

## 2. 测试文件

测试位于：

```text
tests/AteraSnipeSync.Tests/Mapping/InventoryMapperTests.cs
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

如果依赖尚未 restore，先运行：

```powershell
dotnet restore AteraSnipeSync.sln
```

## 4. Mocking 策略

Module2 是纯逻辑模块，不调用外部 API，不读写文件，不需要 mock HTTP client。

测试直接构造：

- `AteraPullResult`
- `AteraAgentDto`
- `MappingOptions`

不使用 fixture JSON，避免把 Atera API wire shape 固化在 Module2 测试中。

## 5. 常见失败原因

- `AteraAgentDto.AgentId` 或 `Name` 是 required property，测试 helper 必须赋值
- warning code 必须与技术规格一致
- missing serial 但 agent id 存在时应生成 asset，不应跳过
- serial 和 agent id 都缺失时应跳过 asset，并输出 `MissingAgentIdentity`
- `SourceAgentCount` 统计输入 agent 总数，`MappedAssetCount` 只统计成功生成的 asset 数量
