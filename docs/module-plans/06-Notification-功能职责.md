# Notification - 功能职责

## 2026-07 运行结果口径

- 通知必须直接读取 `SyncRunResult.DryRun`，不能通过是否存在 `ImportResult` 推断运行模式。
- `FailedAssets` 仅表示 `SnipeImportResult.FailedAssets`；Atera Pull、Mapping 等导入前失败显示为 0 个失败资产，同时保留结构化运行 failure。
- 本轮不改变通知 channel、SMTP/Webhook 配置和明文密钥保存方式。

## 1. 模块目标

Notification Module 负责把 sync run 或运行时事件转换成结构化通知请求，并通过可替换的 publisher 发布通知。

第一版曾建立安全、可测试、可替换的 notification 扩展点：

- 可以根据 `SyncRunResult` 构造失败、成功、预览完成等通知事件
- 可以根据 `NotificationConfig.Enabled` 和 `NotificationConfig.OnEvents` 判断事件是否应发布
- 可以注册 `NullNotificationPublisher`，保证当前系统拥有稳定依赖但不发送任何外部消息
- 可以在未来替换为 email、Teams、Slack 或 generic webhook publisher

自 2026-07 concrete delivery revision 起，模块必须真实发送标准 SMTP email 与 HTTPS webhook；Teams Workflow 使用 Adaptive Card，非 Teams endpoint 可选 generic JSON。后文“第一版/未来”描述仅保留历史背景，以第 14 节为当前职责。

Notification Module 不决定 sync 是否成功，不运行 Atera Pull，不运行 Snipe-IT Import，不保存 status history，也不渲染 UI。它只负责通知请求的构造、过滤和发布边界。

## 2. 输入

本模块接收以下输入：

- `NotificationRequest`
- `NotificationConfig`
- optional `SyncRunResult`
- event trigger context，例如 `scheduled`、`manual`、`manual-preview`
- `CancellationToken`

`SyncRunResult` 来自 Sync Orchestrator。Notification Module 只能读取其中的结构化结果、warnings 和 failures，用于构造通知摘要。

本模块不得读取真实 Atera API key、Snipe-IT token、raw API response payload 或完整资产 dump 来生成通知。

## 3. 输出

本模块输出：

- 被发布或被过滤的 notification decision
- `INotificationPublisher.PublishAsync` 调用
- notification 相关日志

第一版 `INotificationPublisher.PublishAsync` 不返回 notification result；失败通过异常传递给调用方。

通知发布失败不得修改原始 `SyncRunResult`，也不得把一次 sync 从成功改写为失败。调用方可以单独记录 notification failure。

## 4. 对外接口

本模块核心接口是：

```csharp
public interface INotificationPublisher
{
    Task PublishAsync(
        NotificationRequest request,
        NotificationConfig config,
        CancellationToken cancellationToken);
}
```

通知请求模型是：

```csharp
public sealed class NotificationRequest
{
    public required string EventType { get; init; }
    public required string Severity { get; init; }
    public required string Subject { get; init; }
    public required string Message { get; init; }
    public required int Deleted { get; init; }
    public required SyncRunResult? SyncResult { get; init; }
}
```

配置模型是：

```csharp
public sealed class NotificationConfig
{
    public required bool Enabled { get; init; }
    public required IReadOnlyList<string> OnEvents { get; init; }
    public string? SmtpHost { get; init; }
    public int SmtpPort { get; init; } = 587;
    public bool SmtpUseSsl { get; init; } = true;
    public string? SmtpUsername { get; init; }
    public string? SmtpPassword { get; init; }
    public string? EmailFrom { get; init; }
    public string? EmailTo { get; init; }
    public string? WebhookUrl { get; init; }
}
```

`NotificationConfig` 同时描述 event routing、SMTP transport 与 generic webhook endpoint。SMTP password 与现阶段其它 API credentials 一样只存本机未跟踪 JSON，不进入 IPC、UI result 或日志。

## 5. Event Type 规则

第一版必须支持以下事件名：

- `ScheduledSyncCompleted`
- `ScheduledSyncFailed`
- `ManualSyncCompleted`
- `ManualSyncFailed`
- `ManualPreviewCompleted`
- `ManualPreviewFailed`
- `SyncCompleted`
- `SyncFailed`

事件名必须作为稳定 string contract 处理，供 `NotificationConfig.OnEvents`、测试、日志和未来 UI 配置共同使用。

推荐规则：

- `TriggeredBy = "scheduled"` 且 `SyncRunResult.Success = true` -> `ScheduledSyncCompleted`
- `TriggeredBy = "scheduled"` 且 `SyncRunResult.Success = false` -> `ScheduledSyncFailed`
- `TriggeredBy = "manual"` 且成功 -> `ManualSyncCompleted`
- `TriggeredBy = "manual"` 且失败 -> `ManualSyncFailed`
- `TriggeredBy = "manual-preview"` 且成功 -> `ManualPreviewCompleted`
- `TriggeredBy = "manual-preview"` 且失败 -> `ManualPreviewFailed`
- 未知 trigger 成功 -> `SyncCompleted`
- 未知 trigger 失败 -> `SyncFailed`

## 6. Severity 规则

第一版 severity 使用以下稳定值：

- `Information`
- `Warning`
- `Error`
- `Critical`

推荐映射：

- sync 成功且没有 warning -> `Information`
- sync 成功但存在 warning -> `Warning`
- sync 失败 -> `Error`
- 明确认证、授权或配置 credential failure -> `Critical`

`Critical` 不得靠猜测产生。只有当 `SyncFailure.Code` 或其它结构化 failure code 明确表达 authentication / authorization / credential failure 时才使用。

## 7. 通知过滤规则

`NotificationConfig.Enabled = false` 时不得发布任何外部通知。

`NotificationConfig.Enabled = true` 时：

- `OnEvents` 中包含当前 `EventType` 才允许发布
- event name 比较应 trim 后按 ordinal ignore-case 处理
- `OnEvents` 为 empty list 时视为不发布任何事件

过滤逻辑必须独立于具体 publisher，避免每个 sender 重复实现配置判断。

## 8. 消息内容规则

通知 subject 应短且可扫描，例如：

- `Scheduled sync completed`
- `Scheduled sync failed`
- `Manual preview completed`

通知 message 应包含结构化摘要的用户可读版本：

- sync result
- start / finish UTC time
- pulled count
- mapped count
- created / updated / deleted / skipped / failed counts
- warning count
- first failure stage/code/message when failed

通知 message 不得包含：

- Atera API key
- Snipe-IT API token
- authorization header
- raw Atera JSON
- raw Snipe-IT JSON
- 完整 asset inventory dump
- full local configuration file content

## 9. Failure 行为

以下情况应阻止发布并抛出参数异常：

- `NotificationRequest` 为 `null`
- `EventType` 为空白
- `Severity` 为空白
- `Subject` 为空白
- `Message` 为空白

以下情况应让 `OperationCanceledException` 原样传递：

- cancellation token 在发布前已取消
- publisher 执行期间收到 cancellation

外部 publisher 发送失败时，应记录 error log 并把异常传回调用方。Notification Module 不负责自动重试，除非未来某个具体 sender 的技术规格明确加入 retry policy。

`NullNotificationPublisher` 不发送外部请求，也不应因为缺少 `EmailTo` 或 `WebhookUrl` 失败。

## 10. 不负责事项

Notification Module 不负责：

- 调用 Atera API
- 调用 Snipe-IT API
- 决定 asset create/update 逻辑
- 执行 sync pipeline
- 保存 status history
- 生成 manual preflight CSV
- 编辑 local config
- 渲染 TrayApp UI
- 安装 Windows Service
- 实现 OAuth/Graph、Teams authentication/run-history API、Slack-specific schema 或 custom webhook authentication/signing
- 自动 retry 外部通知
- 存储 notification audit database

这些职责分别属于 Atera Pull、Snipe Import、Sync Orchestrator、Status Store、TrayApp、WorkerService 或未来具体 sender 模块。

## 11. 成功条件

模块6文档完成后，应能指导后续 coding agent 直接实现：

- `NotificationRequest` 的 class comment 和 validation expectations
- `INotificationPublisher` 的 role comment
- `NotificationConfig` 的配置语义
- `NotificationEventTypes`
- `NotificationRequestFactory`
- `NotificationEventFilter`
- `NullNotificationPublisher`
- `tests/AteraSnipeSync.Tests/Notifications/*`

模块6生产实现完成后应满足：

- 失败 sync 可以生成 `*SyncFailed` notification request
- 成功 sync 可以生成 `*SyncCompleted` notification request
- warning 或记录级 failure 的 completed run 使用 `Warning` severity
- disabled config 不发布事件
- unmatched `OnEvents` 不发布事件
- `NullNotificationPublisher` 不发送任何外部请求
- notification message 不泄露 secret 或 raw payload
- cancellation 行为可测试
- 自动化测试不调用真实 Atera、Snipe-IT、SMTP、Teams、Slack 或 webhook endpoint

## 12. 扩展点

未来可以在不改变核心 sync pipeline 的前提下扩展：

- `EmailNotificationPublisher`
- `TeamsWebhookNotificationPublisher`
- `SlackNotificationPublisher`
- `GenericWebhookNotificationPublisher`
- notification retry policy
- notification template customization
- per-severity routing
- per-event recipient routing
- local notification audit history

这些扩展必须继续使用结构化 `NotificationRequest`，不得让 Sync Orchestrator 或 Worker Scheduler 了解具体 sender 的协议细节。

## 12. 2026-07 通知判定加固职责

- Scheduler 必须通过 `NotificationRequestFactory` 生成实际 `ScheduledSyncCompleted` 或 `ScheduledSyncFailed`，不得把失败写成 completed。
- Scheduler 必须先使用 `NotificationEventFilter` 和 `NotificationConfig` 判定是否发送；disabled 或 event 未订阅时不得调用 publisher。
- critical failure 匹配使用稳定的限定 failure code，并兼容 namespace 前缀（例如 `SnipeImport.AuthenticationFailed`）。
- notification publisher 异常不得终止后续 schedule loop；必须被记录并作为本次调度执行失败处理。

## 13. 2026-07 Email/Webhook concrete delivery revision

当前生产职责覆盖：

- `CompositeNotificationPublisher` 使用同一个 immutable `NotificationConfig` snapshot，把一个安全 `NotificationRequest` 分别交给所有已配置 channel。
- `EmailNotificationSender` 使用 SMTP host/port/TLS/optional username+password/from/to 发送纯文本邮件；username/password 必须同时提供或同时为空。
- `WebhookNotificationSender` 只允许 absolute HTTPS URL，并 POST `application/json`；generic payload 包含 event type、severity、subject、message、numeric deleted count 和 UTC timestamp，不含 config、credential 或 raw sync/API payload。
- normal scheduled notifications 仍先经过 `Enabled`/`OnEvents` filter；Test Notifications 明确绕过 event filter，但只测试已经完整配置的 channel。
- Test Notifications 必须独立尝试 Email 与 Webhook；一个 channel 失败不得阻止另一个 channel，并返回每个 channel 的 `Configured/Succeeded/Message` sanitized result。
- sender 不自动 retry，避免测试按钮或 scheduler 产生重复通知。HTTP non-success、SMTP/validation failure 必须作为 channel failure；不得把 endpoint、username、password、recipient 或 exception stack 送回 Tray。
- 当前 Email scope 是 standard SMTP with optional username/password and TLS；不包含 OAuth2/Graph。Webhook scope 包含 Teams Workflow Adaptive Card 与 generic HTTPS JSON POST；不包含 custom auth headers/signing、Teams authenticated trigger/run-history polling 或 Slack-specific schema。上述能力仍是明确 extension point，不得假装已实现。
- 生产发送发生在服务器 Worker Service（LocalSystem）中。空 SMTP username/password 明确表示 unauthenticated/IP relay，禁止隐式使用 LocalSystem Windows credentials；服务器必须具备 SMTP/Webhook endpoint 的 outbound DNS/firewall/proxy/TLS trust。
- 自动化测试使用 fake SMTP transport 与 fake HTTP handler，禁止连接真实 SMTP/webhook。

成功条件：正常 scheduled notification 和 Config 中的 Test Notifications 共用相同 sender；当两个 channel 都配置时确实各发送一次；缺失配置时显示 Not configured；取消、partial failure 与 sanitized failure 均有自动化覆盖。

## 14. 2026-07 Teams Workflow Adaptive Card 修订

Teams Workflow webhook 不再使用 generic 五字段 JSON。`NotificationConfig.WebhookPayloadFormat` 明确区分 `TeamsAdaptiveCard` 与 `GenericJson`；为修复既有 Teams 配置，未持久化该字段的旧配置按 `TeamsAdaptiveCard` 读取，操作员仍可在 UI 中显式选择 `GenericJson`。

- `TeamsAdaptiveCard` 使用 Microsoft Teams `When a Teams webhook request is received` 契约：外层 `type = message`，`attachments` 中使用 `application/vnd.microsoft.card.adaptive`，卡片 `content.type = AdaptiveCard`、schema/version/body 均完整提供。
- 卡片只显示 subject、severity、event type、UTC time 与安全 message，不包含配置、credential、webhook URL 或 raw API/sync payload。
- `GenericJson` 使用 event type、severity、subject、message、numeric deleted count、UTC timestamp 六字段 payload，供非 Teams 的 operator-owned HTTPS endpoint 使用。
- HTTP 2xx 只能证明 webhook trigger 接收了请求；SMTP send 完成只能证明 SMTP server 接受了请求。Test Notifications 的成功文案必须使用 `Accepted`，不得声称消息已经最终送达邮箱或 Teams channel。
- Teams Flow 在 HTTP 接收后异步执行，因此 sender 不轮询 Power Automate run history，也不需要或保存 Microsoft credentials。最终投递仍需操作员查看 Teams channel、Flow run history 或邮件系统 message trace。

成功条件：Teams payload 满足官方 Adaptive Card envelope；generic payload 保持兼容；两种格式均只允许 HTTPS；自动化测试验证完整 JSON shape 且不调用真实 Flow endpoint。

## 15. 2026-07 Completed event 与记录级失败边界

- `SyncRunResult.Success` 表示 pipeline 是否完整结束。完整 run 即使包含 asset/reference failure，也必须生成对应的 `ScheduledSyncCompleted`、`ManualSyncCompleted`、`ManualPreviewCompleted` 或 `SyncCompleted`，不得生成 `*SyncFailed`。
- completed run 有 warning 或任意 structured failure 时 severity 为 `Warning`；message 的结果文本使用 `Result: Completed`，同时继续包含 failed count 和第一条安全 failure 明细。
- 只有阶段异常、取消/中断或其它未完整结束的 structured run 才生成 `*SyncFailed`；硬进程崩溃可能无法执行通知发送，不能伪造 completed notification。

成功条件：部分记录失败的完整 run 发送 completed event 并保留失败摘要；fatal/incomplete run 发送 failed event；secret/raw payload 不进入通知。

## 16. 2026-07 删除数量通知职责

- `NotificationRequestFactory` 必须把 `SnipeImportResult.DeletedAssets` 投影为通知的非负 `Deleted` 计数；没有 `ImportResult` 的运行和 Test Notifications 使用 0。
- 人类可读的通知 message 必须包含 `DeletedAssets: <count>`，使 Email 与 Teams Adaptive Card 都能显示本次实际成功删除的资产数量。
- `GenericJson` webhook payload 必须新增顶层数值字段 `deleted`，值直接来自 `NotificationRequest.Deleted`，不得从 message 文本反向解析。
- Teams payload 保持 Microsoft 要求的 Adaptive Card envelope；删除数量通过安全 message 显示，不向 envelope 根节点添加非契约字段。
- `deleted` 只表示已经通过 Snipe-IT HTTP 与业务成功检查的删除数。失败或仅计划的删除不得计入，pipeline failure 也不得回填该字段。

成功条件：手动、排程和 fallback sync notification 都投影真实删除数；generic webhook JSON 包含数值 `deleted`；Test Notifications 使用 `deleted: 0`；所有自动化测试保持离线。
