# Notification - 功能职责

## 1. 模块目标

Notification Module 负责把 sync run 或运行时事件转换成结构化通知请求，并通过可替换的 publisher 发布通知。

第一版目标是建立安全、可测试、可替换的 notification 扩展点：

- 可以根据 `SyncRunResult` 构造失败、成功、预览完成等通知事件
- 可以根据 `NotificationConfig.Enabled` 和 `NotificationConfig.OnEvents` 判断事件是否应发布
- 可以注册 `NullNotificationPublisher`，保证当前系统拥有稳定依赖但不发送任何外部消息
- 可以在未来替换为 email、Teams、Slack 或 generic webhook publisher

第一版不要求真实 email/webhook 发送能力。真实外部通知属于后续扩展，必须单独设计配置、认证、重试和测试策略。

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
    public required SyncRunResult? SyncResult { get; init; }
}
```

配置模型是：

```csharp
public sealed class NotificationConfig
{
    public required bool Enabled { get; init; }
    public required IReadOnlyList<string> OnEvents { get; init; }
    public string? EmailTo { get; init; }
    public string? WebhookUrl { get; init; }
}
```

`NotificationConfig` 只描述是否启用和哪些事件允许发布。第一版 `NullNotificationPublisher` 不使用 `EmailTo` 或 `WebhookUrl` 发送任何外部请求。

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
- created / updated / skipped / failed counts
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
- 实现 SMTP、OAuth、Teams、Slack 或 webhook 协议细节
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
- warning-only success 使用 `Warning` severity
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
