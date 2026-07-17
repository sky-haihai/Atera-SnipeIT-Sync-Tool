# Atera Pull - 功能职责

## 1. 模块目标

Atera Pull Module 负责从 Atera API 拉取同步所需的原始 inventory 数据，并把这些数据整理成系统内部后续模块可消费的 `AteraPullResult`。

本模块只负责 Atera 设备读取边界：

- 调用 Atera API 获取 managed devices / agents
- 内部遍历 Atera paged collection，直到收集完整设备集合
- 处理鉴权失败、可重试失败、不可重试失败和 malformed record
- 输出 pull summary、pull warnings 和 Atera agent DTO 集合

本模块不理解 Snipe-IT，也不负责 Atera 到 Snipe-IT 的字段映射、资产匹配、创建或更新。

## 2. 设计依据

本职责文档依据 `docs/AteraSnipeSync_AI_Agent_Master_Plan.md` 中：

- `Module 1 - Atera Pull Module`
- `Step 4 - 实现 Atera Pull Module`

Step 4 的目标是实现 Atera 设备数据读取，交付 `AteraClient`、`AgentInfo`、完整设备集合读取、auth failure handling、retryable failure handling 和 mocked API tests。

注意：Atera API 的 endpoint path、request/response wire shape、认证 header、分页参数和错误语义必须在后续技术规格或代码实现前再次查阅官方文档 `https://app.atera.com/apidocs`。如果官方文档不可用或含义不明确，不允许在技术规格或代码中猜测这些细节。

由于当前没有独立的 Atera 测试环境，任何真实 Atera API call 都必须非常谨慎。自动化测试必须只使用 mocked API、fake client、本地 fixture 或脱敏样例 payload。不得添加 Python 探针。若需要使用真实 API key 验证行为，只能由项目 owner/operator 手动运行，并且必须在测试指导或运行说明中附上命令、配置、安全注意事项、预期脱敏输出和清理步骤。

## 3. 输入

主要输入：

- `AteraPullRequest`
- `CancellationToken`

`AteraPullRequest` 应至少包含：

- Atera API key

调用方负责从配置系统读取 API key，并构造 request。本模块不直接读取配置文件、不读取 user secrets、不读取环境变量。

`CancellationToken` 用于：

- 取消尚未发出的 API 请求
- 取消正在等待的 API 请求
- 取消分页循环
- 取消 retry 等待

## 4. 输出

主要输出：

- `AteraPullResult`

`AteraPullResult` 应包含：

- zero or more `AgentInfo`
- `PullSummary`
- zero or more `ModuleWarning`

`PullSummary` 应反映本次 pull 的关键统计信息：

- pulled agent count
- pull completion timestamp

`ModuleWarning` 用于记录不应中断整次 pull 的问题，例如：

- 单条 Atera record 缺少非致命字段
- 单页中存在无法转换的 malformed record，但其余 records 可继续处理
- Atera 返回了可忽略或可降级处理的数据异常

## 5. 对外接口

本模块通过 `IAteraClient` 暴露能力：

```csharp
public interface IAteraClient
{
    Task<AteraPullResult> PullInventoryAsync(
        AteraPullRequest request,
        CancellationToken cancellationToken);
}
```

调用方只需要传入 Atera pull request 和 cancellation token，不需要知道：

- Atera devices/agents endpoint 如何分页
- 如何遍历 Atera paged collection 并组装完整设备集合
- HTTP client 如何注入 API key
- transient failures 如何 retry
- Atera response 如何转换为内部 agent DTO

## 6. 主要职责

### 6.1 拉取 agents

本模块必须从 Atera 拉取 managed devices / agents 数据。

成功拉取后，每条可用 agent record 应转换为内部 `AgentInfo`。`AgentInfo` 字段代表系统后续模块可能需要的 Atera 设备原始信息；第一版应尽可能保留官方 `AgentQueryDTO` 中的所有字段，暂时用不到的字段也先保留，后续 Reconstruction/Mapping/Snipe-IT Import 再决定消费哪些字段。

customer 不应作为本模块的独立主资源输出。如果 Atera agent/device response 中包含 customer id、customer name、site、company 或类似归属信息，本模块可以把这些信息作为 `AgentInfo` 的属性保留，供 Reconstruction Module 后续使用。

### 6.2 不单独拉取 customers

第一版 Atera Pull Module 不单独调用 customer list/detail endpoint，也不输出 `AteraCustomerDto` 集合。

如果后续发现 Atera agent/device response 不能提供足够的 customer 归属信息，必须先更新本功能职责文档和技术规格，再考虑引入 customer enrichment。不能在实现阶段隐式增加独立 customer pull 行为。

### 6.3 读取完整设备集合

本模块对外提供的是“拉取所有可同步设备”的能力，不把 page/page size 暴露为业务概念。

如果 Atera API 对 agents/devices list 使用 paged collection，本模块必须在内部遍历所有页面，直到 Atera API 明确表示没有更多数据，或请求被取消，或发生不可恢复错误。

完整设备集合读取实现必须避免：

- 只读取第一页
- 重复读取同一页导致无限循环
- 在某页失败后静默返回部分成功结果
- 将半吊子的部分设备集合交给 Reconstruction 或后续模块
- 将分页参数、页大小、结束条件写死为未经官方文档验证的假设

### 6.4 处理认证

本模块必须使用 request 中提供的 API key 调用 Atera API。

当 Atera 返回明确的鉴权失败时，本模块应产生明确失败，不应：

- retry 认证失败
- 返回空成功结果
- 把认证失败降级为 warning
- 继续调用后续 endpoint

### 6.5 处理可重试失败

本模块必须识别可重试失败，例如临时网络失败、超时、限流或 Atera 服务端临时错误。

可重试失败的具体判断条件必须以官方 API 文档和 .NET HTTP 行为为准。模块可以选择执行有限 retry，或在技术规格中定义明确的失败结果；无论采用哪种方式，都必须让调用方能区分这类失败和鉴权失败。

如果最终 retry 仍然失败，本模块必须把本次 pull 视为失败：

- 不返回部分成功的 `AteraPullResult`
- 不把已经拉到的一部分 agents 传给下一个步骤
- 让 Sync Orchestrator 能明确识别本次 pull failure
- 由调用链记录错误 log
- 整个 service 的自动同步循环应暂停，避免持续用同一个故障条件反复运行

后续 TrayApp 或运维入口实现后，可以提供手动按钮恢复/重启自动同步循环。本模块本身只负责暴露明确失败，不直接控制 TrayApp UI。

### 6.6 处理 malformed record

当单条 Atera record 无法转换为内部 DTO，但整个 response/page 仍可继续处理时，本模块应：

- 跳过该 malformed record
- 添加 structured warning
- 继续处理同一页和后续页中其他 records

当 response/page 整体无法解析，或无法判断分页状态时，本模块应视为 pull 失败，不应返回看似完整的成功结果。

### 6.7 生成 pull summary

本模块必须在成功完成 pull 后生成 summary。

summary 中的 agent count 必须与输出 `AgentInfo` 集合数量一致。completion timestamp 应由模块在 pull 成功完成时生成。

## 7. 成功条件

一次 pull 成功时应满足：

- agents 已按 Atera API 分页规则完整读取
- 如果 Atera API 使用 paged collection，所有页面已被内部遍历完成
- 每条可转换 agent 输出为 `AgentInfo`
- agent 上可获得的 customer/site/company 归属信息被保留为 agent 属性
- malformed but non-fatal records 被记录为 warnings
- `PullSummary.AgentCount` 等于输出 agents 数量
- `PullSummary.PulledAt` 表示本次成功 pull 的完成时间
- cancellation token 未被忽略

## 8. 失败条件

以下情况应导致本模块明确失败：

- API key 缺失或为空
- Atera 返回认证失败
- agents/devices endpoint 出现不可恢复错误
- paged collection 中任一页面在 retry 耗尽后仍读取失败
- response/page 整体无法解析
- 分页状态无法可靠判断
- retry 次数耗尽后仍无法完成请求
- 调用过程中收到 cancellation request

失败时不应伪造空的成功 `AteraPullResult`，也不应返回只包含部分 agents 的半成品结果。

当失败来自最终 retry 耗尽、不可恢复 API 错误、认证失败或无法可靠判断分页状态时，调用链应：

- 记录明确 error log
- 停止本次 sync run
- 不继续执行 Reconstruction 或 Snipe-IT Import
- 暂停 service 的自动循环，等待后续手动恢复机制重新启动

## 9. 不负责的事情

Atera Pull Module 不负责：

- 生成 Snipe-IT asset tag
- 单独拉取 Atera customers
- 输出独立 `AteraCustomerDto` 集合
- 判断 Snipe-IT company 是否存在
- 判断 Snipe-IT model 是否存在
- 判断 Snipe-IT asset 是否存在
- 创建或更新 Snipe-IT company/model/asset
- 执行 Atera to Snipe-IT 字段 mapping
- 对 manufacturer/model/company 做业务 fallback
- 决定 dry-run 行为
- 写入最终 status file
- 发送 notification
- 提供 tray UI
- 决定 scheduler 运行频率
- 直接实现 service loop pause/resume UI
- 读取或写入配置文件
- 在自动化测试、CI 或普通 `dotnet test` 中调用真实 Atera API
- 添加 Python 探针脚本验证 Atera API
- 在没有手动运行指示和安全说明时使用真实 Atera API key 做验证

这些职责分别属于 Reconstruction、Snipe Import、Sync Orchestrator、Status Store、Notification、Tray App 或 Worker Scheduler 模块。

## 10. 外部依赖边界

本模块允许依赖：

- .NET HTTP client abstraction
- Atera API configuration supplied by caller
- logging abstraction
- retry/backoff policy abstraction if defined in technical spec
- clock/time provider if defined in technical spec

本模块不应依赖：

- Snipe-IT client
- Reconstruction mapper
- Sync orchestrator
- Worker service hosting details
- Tray application UI
- local status file implementation
- notification publisher

## 11. 扩展点

后续可在不改变模块边界的前提下扩展：

- configurable page size
- configurable retry policy
- rate limit handling
- richer Atera DTO fields after official API verification
- optional customer enrichment in a later module/version if the agent response is insufficient
- source response metadata for diagnostics
- pull duration metrics
- selective customer/device filters if Atera API supports them
- correlation id / request id logging

这些扩展仍应保持本模块只负责 Atera data pull，不引入 Snipe-IT mapping 或 import 行为。

## 12. 与后续文档的关系

本文件只定义职责边界。

后续 `docs/technical-specs/01-AteraPull-技术规格.md` 必须基于本文件进一步明确：

- concrete classes and namespaces
- `AteraClient` constructor dependencies
- request/response models
- public properties
- official Atera endpoint paths
- official authentication mechanism
- official pagination behavior
- whether official agent/device response includes the required customer/site/company attributes
- retry policy
- error and warning types
- logging expectations
- mocked API test cases
- acceptance criteria

在技术规格进入 endpoint、wire DTO、pagination、authentication 和 error semantics 之前，必须再次查阅官方 Atera API 文档并记录依据。

## 13. 2026-07 稳定性加固职责

- Atera API base URL 必须使用 HTTPS，并且生产配置只允许官方 `app.atera.com` 主机，防止 `X-API-KEY` 被发送到任意地址。
- 读取请求对 `429`、临时网络错误和 `5xx` 执行有上限的指数退避，优先遵守 `Retry-After`，并加入小幅 jitter，认证/授权失败不得重试。
- `AteraPullOptions` 暴露经官方文档确认的 `itemsInPage`（1..500）和仅供连接探测使用的最大页数；正常同步仍必须遍历全部页面。
- TrayApp 的连接测试必须用单页、单条记录探测，不得为了验证凭据下载完整 inventory。
- 每个响应仍必须严格验证官方分页 envelope；无法判断分页状态时整次 pull 失败，不输出部分 inventory。
