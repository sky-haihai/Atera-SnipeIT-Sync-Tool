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
- Atera API 文档页面默认加载 Swagger JSON：`https://app.atera.com/swagger/docs/v3`
- 2026-06-17 已读取官方 Swagger JSON。Swagger 版本为 `2.0`，`host` 为 `app.atera.com`，`schemes` 为 `https`
- 官方 Swagger `securityDefinitions` 定义 API key authentication：header name 为 `X-API-KEY`
- 官方 Swagger 定义 `GET /api/v3/agents`，operationId 为 `Agent_Get`，summary 为 `Find agents`
- `GET /api/v3/agents` query parameters:
  - `page`: integer, optional, page index, default is `1`
  - `itemsInPage`: integer, optional, number of items per page, default is `20`, max is `500`
- `GET /api/v3/agents` success response schema 为 `APIResultWrapper[AgentQueryDTO]`
- `APIResultWrapper[AgentQueryDTO]` properties:
  - `items`: array of `AgentQueryDTO`
  - `totalItemCount`: integer
  - `page`: integer
  - `itemsInPage`: integer
  - `totalPages`: integer
  - `prevLink`: string
  - `nextLink`: string
- `AgentQueryDTO` 已确认包含以下与本模块相关的 properties:
  - identity/name: `AgentID`, `MachineID`, `DeviceGuid`, `AgentName`, `SystemName`, `MachineName`
  - customer ownership: `CustomerID`, `CustomerName`
  - hardware identity: `VendorSerialNumber`, `Vendor`, `VendorBrandModel`, `ProductName`
  - status/diagnostics available for later extension: `Online`, `LastSeen`, `ReportedFromIP`, `MacAddresses`, `IpAddresses`
- sibling 项目 `BD_Atera_AutoCompare` 曾使用同一 endpoint/auth/pagination shape 实际导出过 Atera agents，可作为历史交叉验证来源；后续不得新增 Python 探针

当前仍未从官方 Swagger 明确确认、不得写死为业务语义的细节：

- authentication failure 的具体 status/body shape
- rate limit response body shape
- retryable status semantics beyond documented `500`; implementation may classify standard transient HTTP statuses (`429`, `500`, `502`, `503`, `504`) as retryable and must keep this behavior covered by mocked tests
- whether `VendorBrandModel` or `ProductName` is the better long-term Snipe-IT model source; first implementation maps `VendorBrandModel` first and falls back to `ProductName`

已确认的 endpoint、auth、pagination envelope 和 core JSON property names 不再阻塞 first production implementation。未确认的错误 body 和 richer field semantics 必须在后续需要使用时再次查阅官方文档，或通过 owner/operator 手动真实 API 验证记录脱敏结论。

项目当前没有独立的 Atera 测试环境。任何真实 Atera API call 都可能作用于真实租户数据，因此不得把真实 API 当作普通自动化测试目标。

真实 API 验证规则：

- 自动化测试必须使用 mocked `HttpMessageHandler`、fake client、本地 fixture 或脱敏样例 payload
- 不允许添加 Python 探针脚本
- 不允许在 CI、普通 `dotnet test`、build 或默认开发命令中调用真实 Atera API
- 使用真实 API key 的验证必须由 owner/operator 手动运行
- 手动验证说明必须附 exact command/UI steps、所需 local-only config/env vars、预期脱敏输出和 cleanup steps
- 手动验证只允许执行只读 GET 请求
- 手动验证只请求确认 API 行为所需的最小数据量
- 不执行 create/update/delete
- 不在代码库中提交真实 API key
- 不打印 API key
- 不把完整真实响应写入 git tracked 文件
- 记录已验证的 endpoint、auth、pagination 和 JSON shape 结论

## 3. 命名空间与文件

Production code 位于 `src/AteraSnipeSync.Core/Atera/`。

Interface files must live under module-local `Interfaces/` folders while keeping the module namespace unchanged.

需要创建或修改：

- `Interfaces/IAteraClient.cs`
- `AteraClient.cs`
- `AteraPullRequest.cs`
- `AteraPullResult.cs`
- `AgentInfo.cs`
- `PullSummary.cs`
- `AteraPullOptions.cs`
- `AteraPullException.cs`
- `AteraPullFailureKind.cs`
- `AteraWarningFactory.cs`
- `Interfaces/IAteraClock.cs`
- `SystemAteraClock.cs`

需要删除或停止使用：

- `AteraCustomerDto.cs`
- `AteraAgentDto.cs`
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
        CancellationToken cancellationToken,
        IProgress<SyncProgressUpdate>? progress = null);
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
    public required IReadOnlyList<AgentInfo> Agents { get; init; }
    public required PullSummary Summary { get; init; }
    public required IReadOnlyList<ModuleWarning> Warnings { get; init; }
}
```

Rules:

- `Agents` 包含本次成功 pull 得到的完整可转换设备集合
- 不允许包含 `Customers`
- pull 失败时不返回 `AteraPullResult`

### 4.4 AgentInfo

```csharp
namespace AteraSnipeSync.Core.Atera;

public sealed class AgentInfo
{
    public required string AgentId { get; init; }
    public required string Name { get; init; }
    public required string RawJson { get; init; }
    public string? MachineId { get; init; }
    public string? DeviceGuid { get; init; }
    public string? CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public string? AgentName { get; init; }
    public string? SystemName { get; init; }
    public string? MachineName { get; init; }
    public string? Vendor { get; init; }
    public string? VendorSerialNumber { get; init; }
    public string? VendorBrandModel { get; init; }
    public string? ProductName { get; init; }
    public IReadOnlyList<string> MacAddresses { get; init; }
    public IReadOnlyList<string> IpAddresses { get; init; }
    public string? HardwareDisksJson { get; init; }
    public string? BatteryInfoJson { get; init; }
    // plus every currently documented AgentQueryDTO field needed to preserve Atera inventory shape.

    public string? SerialNumber { get; }
    public string? Manufacturer { get; }
    public string? Model { get; }
}
```

Rules:

- `AgentId` 和 `Name` 是内部 required identity fields
- `RawJson` 必须保留该 agent record 的脱敏前原始 JSON 文本，但不得包含 API key
- `AgentInfo` 必须尽可能覆盖官方 Swagger `AgentQueryDTO` 当前公开字段；字段暂时不用也要保留，后续模块再决定消费哪些字段
- 对复杂或不稳定的 nested object，例如 `HardwareDisks` 和 `BatteryInfo`，第一版用 raw JSON string 保留，避免提前发明内部结构
- `SerialNumber`、`Manufacturer`、`Model` 是给 Reconstruction/Mapping 使用的便利属性，分别来自 `VendorSerialNumber`、`Vendor`、`VendorBrandModel ?? ProductName`
- `CustomerId` / `CustomerName` 只来自 agent/device response 中可获得的归属属性
- 不通过独立 customer endpoint 补齐 customer fields
- JSON wire property names 以 2026-06-17 官方 Swagger `AgentQueryDTO` 为准

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
        CancellationToken cancellationToken,
        IProgress<SyncProgressUpdate>? progress = null);
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
2. Initialize empty `List<AgentInfo>` and `List<ModuleWarning>`
3. Build the first agents/devices request URI using official docs
4. Loop through the Atera paged collection
5. For each page:
   - send authenticated HTTP request
   - retry retryable failures up to `MaxRetryAttempts`
   - stop immediately on authentication failure
   - parse the response envelope
   - convert valid records to `AgentInfo`
   - convert non-fatal malformed records to warnings
   - determine next page from official pagination fields
   - report safe page-level progress without exposing API keys or raw payloads
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

Use official Swagger endpoint `GET /api/v3/agents`.

The first implementation must not call `GET /api/v3/devices` or any customer endpoint. If later modules need non-agent device types, update the module plan and this technical spec before expanding the endpoint set.

### 7.2 Authentication

Authentication uses the official Swagger API key mechanism:

- header name: `X-API-KEY`
- header value: raw Atera API key from `AteraPullRequest.ApiKey`
- no bearer prefix
- never put the API key in query string, logs, warnings, exceptions, or fixtures

### 7.3 Pagination

Pagination follows official Swagger:

- request query parameter `page` is one-based and defaults to `1`
- request query parameter `itemsInPage` defaults to `20` and maxes at `500`
- first implementation should request `itemsInPage=500`
- response item collection field is `items`
- response metadata fields are `totalItemCount`, `page`, `itemsInPage`, `totalPages`, `prevLink`, `nextLink`
- first implementation should use `totalPages` to stop; if `totalPages` is missing but `totalItemCount/itemsInPage` can compute the same value, that fallback is allowed
- if pagination state cannot be determined, throw `AteraPullException(PaginationStateUnknown)`

### 7.4 Retryable Failures

Retryable conditions must include .NET transient request failures:

- `HttpRequestException`
- request timeout if represented by .NET as a timeout exception

Expected default retry behavior:

- attempt original request once
- retry up to `AteraPullOptions.MaxRetryAttempts`
- wait `AteraPullOptions.RetryDelay` between attempts
- observe cancellation token during retry delay

Retryable HTTP statuses:

- `429`
- `500`
- `502`
- `503`
- `504`

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

Wire parsing is based on the 2026-06-17 official Swagger response shape.

Implementation shape:

- internal sealed response envelope in `AteraClient.cs`
- `items` records parsed as `JsonElement` so a single malformed record can be skipped without failing the entire page
- `[JsonPropertyName]` attributes matching official JSON properties
- `System.Text.Json`
- case-sensitive mapping unless official docs show otherwise
- `AgentInfo.RawJson` preserves the full agent item JSON
- `AgentInfo` maps all currently documented `AgentQueryDTO` simple fields where possible
- `HardwareDisks` and `BatteryInfo` are preserved as raw JSON strings because the current module does not need to model nested structure yet

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

Unit tests must never call the real Atera API. Tests that need HTTP behavior must use mocked handlers, fake clients, local fixtures, or sanitized hand-written/sample JSON.

Required tests:

1. `PullInventoryAsync_ThrowsArgumentException_WhenApiKeyMissing`
2. `PullInventoryAsync_ReturnsAllAgentInfo_WhenSinglePageSucceeds`
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
14. `PullInventoryAsync_ReportsProgress_WhenPagesComplete`

Tests that assert JSON property names, pagination field names, status codes, or auth headers must use mocked HTTP only and must be based on the official Swagger findings recorded in this spec.

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
- returned agents are `AgentInfo` records that preserve broad Atera `AgentQueryDTO` fields and `RawJson`
- no customer endpoint is called
- no `AteraCustomerDto` is required by public contracts
- no `AteraAgentDto` is required by public contracts
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
- Python probes
- manual real-key validation without documented run instructions
- scheduler loop pause/resume mechanics
- TrayApp button
- status file persistence
- notification publishing

## 16. Verification Checklist Before Future Wire Changes

Current implementation is allowed because the following items were verified from official Swagger on 2026-06-17:

- [x] Confirm official canonical endpoint: `GET /api/v3/agents`
- [x] Confirm authentication header/scheme: `X-API-KEY` header with raw API key
- [x] Confirm paging request parameters: `page`, `itemsInPage`
- [x] Confirm paging response envelope: `items`, `totalItemCount`, `page`, `itemsInPage`, `totalPages`, `prevLink`, `nextLink`
- [x] Confirm JSON property names for required and optional agent fields currently mapped into `AgentInfo`

Still uncertain and must be rechecked before depending on exact behavior:

- [ ] Confirm auth failure status/error body
- [ ] Confirm rate limit response body shape
- [ ] Confirm whether `VendorBrandModel` or `ProductName` should be the long-term Snipe-IT model source

If future changes need behavior not covered above, stop and record the uncertainty instead of guessing. Real Atera API validation must be manual-only, owner/operator-run, read-only, minimal, sanitized, and documented with run instructions.
