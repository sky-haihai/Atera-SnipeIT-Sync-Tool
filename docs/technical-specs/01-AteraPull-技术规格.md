# Atera Pull - 技术规格

## 1. 目标

Atera Pull Module 负责通过 Atera API 拉取所有可同步设备，并输出系统内部 `AteraPullResult`。

本模块必须：

- 只拉取 Atera managed devices / agents
- 不单独拉取 Atera customers
- 不输出独立 customer DTO 集合
- 在内部遍历 Atera paged collection，直到得到完整设备集合
- 在最终 retry 仍失败时明确失败，不返回半成品 agents
- 让调用链可以停止本次 sync run，并暂停 service 自动循环

本技术规格基于：

- `docs/module-plans/01-AteraPull-功能职责.md`
- `docs/AteraSnipeSync_AI_Agent_Master_Plan.md` 的 `Step 4 - 实现 Atera Pull Module`

## 2. 官方 API 依据与阻塞项

已确认的官方/官方支持信息：

- Atera API 文档入口：`https://app.atera.com/apidocs`
- Atera 支持文档说明设备数据可通过 `GET /api/v3/agents` 或 `GET /api/v3/devices` 获取
- Atera 支持文档说明 device data 包含 customer assignment 和 status
- Atera 支持文档说明 large exports 需要使用 pagination

当前尚未从官方 API 文档页面可靠确认的实现细节：

- `GET /api/v3/agents` 与 `GET /api/v3/devices` 哪一个应作为第一版唯一 endpoint
- authentication header name and format
- query parameter names for paging
- default/max page size
- response envelope shape
- item collection JSON property name
- total count / total pages / next page indicator
- official status code and error payload semantics
- exact JSON property names for agent id, device name, serial number, manufacturer, model, customer id, customer name, and status

这些细节是 production implementation 的阻塞项。后续生成代码前，必须打开官方 Atera API docs 并把上述细节补入本技术规格或实现记录。若官方文档仍不可用或含义不明确，不允许猜测 wire shape。

项目当前没有独立的 Atera 测试环境。任何真实 Atera API call 都可能作用于真实租户数据，因此不得把真实 API 当作普通自动化测试目标。若官方文档无法确认某个 API 用法，后续 agent 必须停止并向项目 owner 列出不确定点；只有 owner 明确同意后，才可以在测试区使用最小范围 Python 探针脚本验证行为。

Python 探针脚本规则：

- 只允许执行只读 GET 请求
- 只请求确认 API 行为所需的最小数据量
- 不执行 create/update/delete
- 不在代码库中提交真实 API key
- 不打印 API key
- 不把完整真实响应写入 git tracked 文件
- 记录已验证的 endpoint、auth、pagination 和 JSON shape 结论

## 3. 命名空间与文件

Production code 位于 `src/AteraSnipeSync.Core/Atera/`。

需要创建或修改：

- `IAteraClient.cs`
- `AteraClient.cs`
- `AteraPullRequest.cs`
- `AteraPullResult.cs`
- `AteraAgentDto.cs`
- `PullSummary.cs`
- `AteraPullOptions.cs`
- `AteraPullException.cs`
- `AteraPullFailureKind.cs`
- `AteraWarningFactory.cs`
- `IAteraClock.cs`
- `SystemAteraClock.cs`

需要删除或停止使用：

- `AteraCustomerDto.cs`
- `AteraPullResult.Customers`
- `PullSummary.CustomerCount`

Tests 位于 `tests/AteraSnipeSync.Tests/Atera/`。

需要创建：

- `AteraClientTests.cs`
- `AteraPullContractTests.cs`

## 4. Public Contracts

### 4.1 IAteraClient

```csharp
namespace AteraSnipeSync.Core.Atera;

public interface IAteraClient
{
    Task<AteraPullResult> PullInventoryAsync(
        AteraPullRequest request,
        CancellationToken cancellationToken);
}
```

调用方只调用 `PullInventoryAsync`。分页、retry、wire DTO 解析都由 `AteraClient` 内部完成。

### 4.2 AteraPullRequest

```csharp
namespace AteraSnipeSync.Core.Atera;

public sealed class AteraPullRequest
{
    public required string ApiKey { get; init; }
}
```

Validation:

- `ApiKey` 为 `null`、空字符串或 whitespace 时抛出 `ArgumentException`
- 本 request 不包含 page/page size，因为本模块对外暴露的是“拉取完整设备集合”

### 4.3 AteraPullResult

```csharp
using AteraSnipeSync.Core.Common;

namespace AteraSnipeSync.Core.Atera;

public sealed class AteraPullResult
{
    public required IReadOnlyList<AteraAgentDto> Agents { get; init; }
    public required PullSummary Summary { get; init; }
    public required IReadOnlyList<ModuleWarning> Warnings { get; init; }
}
```

Rules:

- `Agents` 包含本次成功 pull 得到的完整可转换设备集合
- 不允许包含 `Customers`
- pull 失败时不返回 `AteraPullResult`

### 4.4 AteraAgentDto

```csharp
namespace AteraSnipeSync.Core.Atera;

public sealed class AteraAgentDto
{
    public required string AgentId { get; init; }
    public required string Name { get; init; }
    public string? SerialNumber { get; init; }
    public string? CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
}
```

Rules:

- `AgentId` 和 `Name` 是内部 required identity fields
- `CustomerId` / `CustomerName` 只来自 agent/device response 中可获得的归属属性
- 不通过独立 customer endpoint 补齐 customer fields
- JSON wire property names 必须在实现前由官方 Atera docs 验证

### 4.5 PullSummary

```csharp
namespace AteraSnipeSync.Core.Atera;

public sealed class PullSummary
{
    public required int AgentCount { get; init; }
    public required DateTimeOffset PulledAt { get; init; }
}
```

Rules:

- `AgentCount` 必须等于 `AteraPullResult.Agents.Count`
- 不包含 `CustomerCount`
- `PulledAt` 是完整 pull 成功后的 completion timestamp

### 4.6 AteraPullOptions

```csharp
namespace AteraSnipeSync.Core.Atera;

public sealed class AteraPullOptions
{
    public required Uri BaseUri { get; init; }
    public required int MaxRetryAttempts { get; init; }
    public required TimeSpan RetryDelay { get; init; }
}
```

Default values should be supplied by composition root, not by this DTO:

- `BaseUri`: official Atera API base URI from docs
- `MaxRetryAttempts`: `3`
- `RetryDelay`: `TimeSpan.FromSeconds(2)`

Validation:

- `BaseUri` must be absolute
- `MaxRetryAttempts` must be >= 0
- `RetryDelay` must be >= `TimeSpan.Zero`

## 5. Failure Contracts

### 5.1 AteraPullFailureKind

```csharp
namespace AteraSnipeSync.Core.Atera;

public enum AteraPullFailureKind
{
    InvalidRequest,
    AuthenticationFailed,
    RetryExhausted,
    NonRetryableHttpFailure,
    MalformedResponse,
    PaginationStateUnknown,
    Cancelled
}
```

### 5.2 AteraPullException

```csharp
namespace AteraSnipeSync.Core.Atera;

public sealed class AteraPullException : Exception
{
    public AteraPullException(
        AteraPullFailureKind failureKind,
        string message,
        Exception? innerException = null);

    public AteraPullFailureKind FailureKind { get; }
}
```

Rules:

- `AteraClient` throws `AteraPullException` for expected pull failures
- `OperationCanceledException` may be thrown directly when cancellation token is cancelled
- On failure, `AteraClient` must not return partial `AteraPullResult`

## 6. AteraClient

### 6.1 Class Definition

```csharp
using Microsoft.Extensions.Logging;

namespace AteraSnipeSync.Core.Atera;

public sealed class AteraClient : IAteraClient
{
    public AteraClient(
        HttpClient httpClient,
        AteraPullOptions options,
        IAteraClock clock,
        ILogger<AteraClient> logger);

    public Task<AteraPullResult> PullInventoryAsync(
        AteraPullRequest request,
        CancellationToken cancellationToken);
}
```

Constructor validation:

- `httpClient` cannot be `null`
- `options` cannot be `null`
- `clock` cannot be `null`
- `logger` cannot be `null`
- validate `AteraPullOptions`

### 6.2 Public Method Flow

`PullInventoryAsync` must:

1. Validate `request.ApiKey`
2. Initialize empty `List<AteraAgentDto>` and `List<ModuleWarning>`
3. Build the first agents/devices request URI using official docs
4. Loop through the Atera paged collection
5. For each page:
   - send authenticated HTTP request
   - retry retryable failures up to `MaxRetryAttempts`
   - stop immediately on authentication failure
   - parse the response envelope
   - convert valid records to `AteraAgentDto`
   - convert non-fatal malformed records to warnings
   - determine next page from official pagination fields
6. If any page ultimately fails, throw `AteraPullException`
7. Create `PullSummary`
8. Return `AteraPullResult`

### 6.3 No Partial Result Rule

If failure occurs after one or more pages were already collected:

- discard collected agents
- log the failure
- throw `AteraPullException`
- never return partial `AteraPullResult`

This rule applies to:

- retry exhausted
- non-retryable HTTP failure
- authentication failure
- malformed response envelope
- unknown pagination state
- cancellation

## 7. HTTP Behavior

### 7.1 Endpoint

Target endpoint must be selected from official Atera API docs before implementation.

Allowed candidates based on official support docs:

- `GET /api/v3/agents`
- `GET /api/v3/devices`

First implementation should prefer the endpoint that official docs identify as the canonical full device/agent list. If both are valid, choose `GET /api/v3/agents` because project contracts are named `AteraAgentDto` and master plan refers to agents.

### 7.2 Authentication

Authentication must use the official Atera API key mechanism.

Do not implement header name, scheme, or query auth until verified in `https://app.atera.com/apidocs`.

### 7.3 Pagination

Pagination must follow official docs.

The implementation must not assume:

- parameter names such as `page`, `itemsInPage`, `limit`, or `offset`
- response fields such as `totalPages`, `nextPage`, or `items`
- whether page numbers are zero-based or one-based
- maximum page size

Once verified, update this section with:

- request query parameters
- page size rule
- response item collection field
- response next-page/end condition

### 7.4 Retryable Failures

Retryable conditions must include .NET transient request failures:

- `HttpRequestException`
- request timeout if represented by .NET as a timeout exception

HTTP status retryability must be confirmed against official docs before implementation.

Expected default retry behavior:

- attempt original request once
- retry up to `AteraPullOptions.MaxRetryAttempts`
- wait `AteraPullOptions.RetryDelay` between attempts
- observe cancellation token during retry delay

Authentication failures must not be retried.

### 7.5 Logging

`AteraClient` must log:

- pull start at `Information`
- page request start at `Debug`
- retry attempt at `Warning`
- malformed non-fatal record at `Warning`
- pull success at `Information`
- final pull failure at `Error`

Logs must not include API key.

## 8. Wire Parsing

Wire parsing must be implemented only after official docs confirm response shape.

Recommended implementation shape after verification:

- internal sealed wire DTOs in `AteraClient.cs` or `AteraWireModels.cs`
- `[JsonPropertyName]` attributes matching official JSON properties
- `System.Text.Json`
- case-sensitive mapping unless official docs show otherwise

Malformed record handling:

- If a single record is missing optional fields, convert it and leave optional properties `null`
- If a single record is missing required internal identity fields, skip it and add warning
- If response envelope cannot be parsed, throw `AteraPullException(MalformedResponse)`
- If pagination state cannot be determined, throw `AteraPullException(PaginationStateUnknown)`

## 9. Warning Factory

### 9.1 AteraWarningFactory

```csharp
using AteraSnipeSync.Core.Common;

namespace AteraSnipeSync.Core.Atera;

public static class AteraWarningFactory
{
    public static ModuleWarning MalformedAgentRecord(string reason);
    public static ModuleWarning MissingAgentIdentity(string sourceDescription);
}
```

Warning rules:

- `ModuleWarning.Code` must start with `AteraPull.`
- Warning message must include enough source context to diagnose the record
- Warning must not include API key or full raw response body

## 10. Clock

### 10.1 IAteraClock

```csharp
namespace AteraSnipeSync.Core.Atera;

public interface IAteraClock
{
    DateTimeOffset UtcNow { get; }
}
```

### 10.2 SystemAteraClock

```csharp
namespace AteraSnipeSync.Core.Atera;

public sealed class SystemAteraClock : IAteraClock
{
    public DateTimeOffset UtcNow { get; }
}
```

`PullSummary.PulledAt` must use `IAteraClock.UtcNow`.

## 11. Calling Relationship

```text
Worker/Scheduler
  -> SyncOrchestrator
      -> IAteraClient.PullInventoryAsync
          -> AteraClient
              -> HttpClient
              -> Atera API
      -> Reconstruction only if pull succeeds
      -> SnipeImport only if reconstruction succeeds
```

If `IAteraClient.PullInventoryAsync` fails:

- Sync Orchestrator must not call Reconstruction
- Sync Orchestrator must not call Snipe-IT Import
- Worker/Scheduler must pause automatic loop
- later TrayApp/manual operation may resume automatic loop

The pause/resume implementation belongs to Worker Scheduler / TrayApp specs, not `AteraClient`.

## 12. Unit Tests

Create tests in `tests/AteraSnipeSync.Tests/Atera/AteraClientTests.cs`.

Use mocked `HttpMessageHandler`; do not call real Atera API.

Unit tests must never call the real Atera API. Tests that need HTTP behavior must use mocked handlers, local fixtures, or sanitized JSON captured from owner-approved probes.

Required tests:

1. `PullInventoryAsync_ThrowsArgumentException_WhenApiKeyMissing`
2. `PullInventoryAsync_ReturnsAllAgents_WhenSinglePageSucceeds`
3. `PullInventoryAsync_ReturnsAllAgents_WhenMultiplePagesSucceed`
4. `PullInventoryAsync_DoesNotCallCustomerEndpoint`
5. `PullInventoryAsync_ThrowsAuthenticationFailed_WhenAteraReturnsAuthFailure`
6. `PullInventoryAsync_DoesNotRetryAuthenticationFailure`
7. `PullInventoryAsync_RetriesRetryableFailure`
8. `PullInventoryAsync_ThrowsRetryExhaustedAndReturnsNoPartialResult_WhenFinalPageKeepsFailing`
9. `PullInventoryAsync_ThrowsMalformedResponse_WhenEnvelopeCannotBeParsed`
10. `PullInventoryAsync_AddsWarningAndSkipsRecord_WhenSingleAgentMissingRequiredIdentity`
11. `PullInventoryAsync_ThrowsPaginationStateUnknown_WhenNextPageCannotBeDetermined`
12. `PullInventoryAsync_UsesClockForPulledAt`
13. `PullInventoryAsync_DoesNotLogApiKey_WhenFailureOccurs`

Because official wire shape is not yet verified, tests that assert JSON property names, pagination field names, status codes, or auth headers must be written only after official docs are recorded in this spec.

## 13. Contract Tests

Update existing contract compilation tests so they compile against:

- `AteraPullResult.Agents`
- `AteraPullResult.Summary`
- `AteraPullResult.Warnings`
- `PullSummary.AgentCount`
- no `AteraPullResult.Customers`
- no `PullSummary.CustomerCount`

Add `tests/AteraSnipeSync.Tests/Atera/AteraPullContractTests.cs`:

```csharp
public sealed class AteraPullContractTests
{
    [Fact]
    public void AteraPullResult_DoesNotExposeCustomersCollection();

    [Fact]
    public void PullSummary_DoesNotExposeCustomerCount();
}
```

These tests may use reflection to verify removed public properties.

## 14. Acceptance Criteria

Implementation is complete when:

- `AteraClient` implements `IAteraClient`
- `PullInventoryAsync` returns complete agent/device collection on success
- no customer endpoint is called
- no `AteraCustomerDto` is required by public contracts
- retryable page failure either recovers or throws clear `AteraPullException`
- retry exhaustion never returns partial agents
- authentication failure is not retried
- malformed single record produces warning when safe to continue
- malformed response envelope fails the pull
- `PullSummary.AgentCount` matches output agents count
- logs do not expose API key
- tests pass

## 15. Explicit Non-Goals

Do not implement in this module:

- Snipe-IT company/model/asset create/update
- Atera to Snipe-IT mapping
- customer enrichment via separate customer endpoint
- exploratory real Atera API calls without owner approval
- automated tests against the real Atera tenant
- scheduler loop pause/resume mechanics
- TrayApp button
- status file persistence
- notification publishing

## 16. Implementation Blocker Before Code Generation

Before writing production code or tests that assert Atera wire behavior, complete this checklist:

- [ ] Confirm official canonical endpoint: `/api/v3/agents` or `/api/v3/devices`
- [ ] Confirm authentication header/scheme
- [ ] Confirm paging request parameters
- [ ] Confirm paging response envelope
- [ ] Confirm JSON property names for required and optional agent fields
- [ ] Confirm auth failure status/error body
- [ ] Confirm rate limit / retryable status semantics
- [ ] If official docs are unclear, ask owner before running any real API probe
- [ ] If owner approves probing, run only a minimal read-only Python probe in the agreed test area
- [ ] Record probe findings without committing secrets or full real tenant payloads

If any item cannot be verified, stop and record the uncertainty instead of guessing.
