# Atera Pull - 单元测试指导手册

## 1. 测试范围

本测试手册覆盖 Module1 Atera Pull 当前已经实现的 contract 层和 mocked HTTP client 层。

当前测试目标：

- `AteraPullResult` 只暴露 agents、summary、warnings
- `AteraPullResult` 不暴露独立 customers collection
- `PullSummary` 只统计 agent count 和 pulled timestamp
- `PullSummary` 不暴露 customer count
- `AteraPullException` 可以携带明确的 `AteraPullFailureKind`
- `AteraClient` 使用官方 `GET /api/v3/agents` endpoint
- `AteraClient` 使用 `X-API-KEY` header，不把 API key 放入日志
- `AteraClient` 使用 `page` / `itemsInPage` 分页参数和 `items` / `totalPages` response envelope
- `AgentInfo` 尽可能保留官方 `AgentQueryDTO` 字段，并保留 `RawJson`
- `AteraClient` 支持多页、retryable failure、authentication failure、malformed envelope 和 pagination unknown 行为
- 现有 Reconstruction tests 可以继续消费 agent-only `AteraPullResult`

仍不测试真实 Atera tenant。以下行为只通过 mocked HTTP 和官方 Swagger shape 测试：

- authentication header/scheme
- pagination query parameters
- response envelope
- JSON property names
- retryable status semantics

## 2. 测试文件

当前 Atera Pull 测试位于：

```text
tests/AteraSnipeSync.Tests/Atera/AteraPullContractTests.cs
tests/AteraSnipeSync.Tests/Atera/AteraClientTests.cs
tests/AteraSnipeSync.Tests/ContractCompilationTests.cs
```

相关 production contracts 位于：

```text
src/AteraSnipeSync.Core/Atera/
```

## 3. 测试命令

推荐从仓库根目录运行：

```powershell
dotnet build AteraSnipeSync.sln --no-restore
dotnet test AteraSnipeSync.sln --no-build
```

只运行 Atera Pull contract tests：

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~AteraPullContractTests"
```

只运行 shared contract compilation tests：

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~ContractCompilationTests"
```

如果依赖尚未 restore，先运行：

```powershell
dotnet restore AteraSnipeSync.sln
```

## 4. Mocking 策略

`AteraClientTests` 使用自定义 mocked `HttpMessageHandler`。

- 不允许调用真实 Atera API
- 不允许依赖真实 Atera 租户数据
- 不允许在 tests 中读取真实 API key
- fixture 必须是手写或脱敏 JSON
- mocked JSON 应保持官方 Swagger `APIResultWrapper[AgentQueryDTO]` shape

## 5. 真实 API 安全规则

项目当前没有独立的 Atera 测试环境。任何真实 Atera API call 都可能读取真实租户数据。

自动化测试规则：

- `dotnet test` 不得调用真实 Atera API
- CI 不得调用真实 Atera API
- tests 不得读取真实 API key
- tests 不得依赖真实租户数据
- tests 必须使用 mocked `HttpMessageHandler`、fake client、本地 fixture 或手写/脱敏 JSON
- 不得添加 Python 探针

真实 API key 验证规则：

- 只能手动运行
- 必须由 owner/operator 明确决定何时运行
- 必须附运行指示
- 必须只执行只读 GET 请求
- 必须只请求验证行为所需的最小数据量
- 不执行 create/update/delete
- 不打印 API key
- 不把 API key 写入仓库
- 不把完整真实响应提交到 git
- 只记录脱敏后的 endpoint、auth、pagination、JSON shape 结论

## 6. 手动真实 API 验证运行指示模板

当前仓库没有默认真实 API 自动化测试命令。未来如果需要验证真实 Atera tenant，必须创建或提供 manual-only 运行说明，且不得让该命令进入普通 `dotnet test`、build 或 CI。

手动说明必须包含：

```text
Purpose:
- One sentence describing the behavior being validated.

Prerequisites:
- Atera API key is available only to the operator.
- No key is stored in tracked files.
- Output path, if any, is outside git or ignored by git.

Run:
- Exact PowerShell command or UI steps.
- Example env var must be session-only, such as:
  $env:ATERA_API_KEY = "<paste key for this terminal session only>"

Expected sanitized output:
- Count/page metadata only.
- No API key.
- No full tenant payload.
- No customer/device secrets beyond the minimum explicitly needed.

Cleanup:
- Remove session env var:
  Remove-Item Env:\ATERA_API_KEY
- Delete any temporary local output that contains tenant data.
```

任何手动真实 API 验证完成后，只能把脱敏结论写入文档，例如 endpoint、auth、pagination、JSON shape。不要提交真实响应正文。

## 7. 已覆盖的 AteraClient 测试

- API key 缺失时失败
- 单页 agents 成功并填充广义 `AgentInfo`
- 多页 agents 成功
- 不调用 customer endpoint
- authentication failure 不 retry
- retryable failure 会 retry
- final page retry exhausted 时不返回 partial agents
- malformed response envelope 失败
- single malformed agent record 产生 warning 并跳过
- pagination state unknown 失败
- `PulledAt` 使用 clock provider
- log 不包含 API key

## 8. 常见失败原因

- 测试仍在构造 `AteraPullResult.Customers`
- 测试仍在设置 `PullSummary.CustomerCount`
- 新代码重新引入 `AteraCustomerDto` 或 `AteraAgentDto`
- 新测试直接调用真实 Atera API
- 新测试读取真实 API key 或环境变量
- 新增 Python 探针
- fixture 中包含未脱敏的真实租户 payload
- log assertion 发现 API key 泄露

## 9. 当前验证结果

最近一次验证命令：

```powershell
dotnet build AteraSnipeSync.sln
dotnet test AteraSnipeSync.sln --no-build
```

结果：

```text
Build succeeded.
Passed: 28
```
