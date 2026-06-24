# Notification - 单元测试指导手册

## 1. 当前状态

Module 6 Notification 已完成第一版 stub-safe 实现和自动化测试。

已实现 production code：

- `src/AteraSnipeSync.Core/Notifications/NotificationEventTypes.cs`
- `src/AteraSnipeSync.Core/Notifications/NotificationRequestFactory.cs`
- `src/AteraSnipeSync.Core/Notifications/NotificationEventFilter.cs`
- `src/AteraSnipeSync.Core/Notifications/NullNotificationPublisher.cs`

已补齐 comments 的现有 contract：

- `src/AteraSnipeSync.Core/Notifications/NotificationRequest.cs`
- `src/AteraSnipeSync.Core/Notifications/Interfaces/INotificationPublisher.cs`
- `src/AteraSnipeSync.Core/Configuration/NotificationConfig.cs`

已实现测试目录：

- `tests/AteraSnipeSync.Tests/Notifications/NotificationRequestFactoryTests.cs`
- `tests/AteraSnipeSync.Tests/Notifications/NotificationEventFilterTests.cs`
- `tests/AteraSnipeSync.Tests/Notifications/NullNotificationPublisherTests.cs`

## 2. 测试命令

从仓库根目录运行：

```powershell
dotnet build
dotnet test
```

最新验证结果：

```text
dotnet build: Build succeeded, 0 warnings, 0 errors.
dotnet test: Passed 119, Failed 0, Skipped 0.
```

## 3. Mocking / Fixture 策略

Notification 测试完全离线：

- 不使用真实 Atera API
- 不使用真实 Snipe-IT API
- 不使用真实 SMTP server
- 不调用 Teams、Slack 或 webhook endpoint
- 不读取真实 API key、token 或 local secret config
- 不依赖 WorkerService host 或 TrayApp UI

测试输入通过手写对象构造：

- `SyncRunResult`
- `ModuleWarning`
- `SyncFailure`
- `AteraPullResult`
- `SnipeImportBatch`
- `SnipeImportResult`

日志使用 `NullLogger<T>`。`NullNotificationPublisher` 测试不需要 fake HTTP handler，因为该 publisher 不得创建任何 HTTP、SMTP、Teams、Slack 或 webhook request。

## 4. 已实现测试用例

`NotificationRequestFactoryTests` 覆盖：

1. `CreateForSyncResult_ReturnsScheduledCompleted_WhenScheduledRunSucceeds`
2. `CreateForSyncResult_ReturnsScheduledFailed_WhenScheduledRunFails`
3. `CreateForSyncResult_ReturnsManualCompleted_WhenManualRunSucceeds`
4. `CreateForSyncResult_ReturnsManualPreviewCompleted_WhenManualPreviewSucceeds`
5. `CreateForSyncResult_ReturnsGenericEvent_WhenTriggeredByUnknown`
6. `CreateForSyncResult_UsesWarningSeverity_WhenSuccessfulRunHasWarnings`
7. `CreateForSyncResult_UsesCriticalSeverity_ForAuthenticationFailureCode`
8. `CreateForSyncResult_IncludesSummaryCountsAndFirstFailure`
9. `CreateForSyncResult_DoesNotIncludeSecretsOrRawPayloads`
10. `CreateForSyncResult_ThrowsArgumentNullException_WhenResultNull`
11. `CreateForSyncResult_ThrowsArgumentException_WhenTriggeredByBlank`

`NotificationEventFilterTests` 覆盖：

12. `ShouldPublish_ReturnsFalse_WhenNotificationsDisabled`
13. `ShouldPublish_ReturnsFalse_WhenOnEventsEmpty`
14. `ShouldPublish_ReturnsTrue_WhenEventConfiguredCaseInsensitive`
15. `ShouldPublish_ReturnsFalse_WhenEventNotConfigured`
16. `ShouldPublish_IgnoresBlankConfiguredEvents`
17. `ShouldPublish_ThrowsArgumentNullException_ForNullConfig`
18. `ShouldPublish_ThrowsArgumentNullException_ForNullRequest`

`NullNotificationPublisherTests` 覆盖：

19. `PublishAsync_CompletesWithoutExternalCalls`
20. `PublishAsync_ThrowsArgumentNullException_WhenRequestNull`
21. `PublishAsync_ThrowsArgumentException_WhenRequestFieldsBlank`
22. `PublishAsync_HonorsCancellation`

## 5. 重点断言

测试确认：

- scheduled 成功 run 生成 `ScheduledSyncCompleted`
- scheduled 失败 run 生成 `ScheduledSyncFailed`
- manual run 和 manual preview run 使用不同 event type
- 未知 `TriggeredBy` fallback 到 generic `SyncCompleted` / `SyncFailed`
- 成功但有 warnings 时 severity 是 `Warning`
- 明确认证、授权或 credential failure code 时 severity 是 `Critical`
- 普通失败 severity 是 `Error`
- message 包含 pulled / mapped / created / updated / skipped / failed counts
- message 包含第一条 failure 的 stage / code / sanitized message
- message 不包含 API key、token、authorization header 或 raw JSON
- disabled notification config 不发布
- `OnEvents` empty 时不发布任何事件
- `OnEvents` 匹配大小写不敏感，并 trim 空白
- `NullNotificationPublisher` 不创建 HTTP/SMTP/webhook request
- cancellation 通过 `OperationCanceledException` 传递

## 6. 常见失败原因

- 把 failed scheduled run 标记成 `ScheduledSyncCompleted`，导致配置无法只订阅失败事件。
- `OnEvents` empty 被当成“发布所有事件”，导致用户收到未明确订阅的消息。
- severity 只根据 event type 判断，忽略成功 run 中的 warnings。
- notification message 拼入 raw `SyncRunResult`、raw API payload 或配置 JSON，导致 secret 泄漏风险。
- `NullNotificationPublisher` 尝试读取 `EmailTo`、`WebhookUrl` 或创建外部 client。
- cancellation 被 catch 成普通失败，导致 runtime 无法快速停止。

## 7. 真实 API / 外部发送安全规则

Notification 自动化测试必须保持本地、离线、可重复。

不得在 Notification 测试中：

- 调用真实 Atera API
- 调用真实 Snipe-IT API
- 发送真实 email
- 调用真实 Teams、Slack 或 generic webhook URL
- 打印或保存真实 API key/token
- 依赖 `C:\ProgramData\AteraSnipeSync` 中的真实配置

如果未来需要人工验证真实 email/webhook 发送，必须另写 manual-only 指南，包含：

- exact commands or UI steps
- required local-only config or environment variables
- confirmation that secrets must not be printed, logged, committed, or stored in tracked files
- expected sanitized output
- cleanup steps for removing environment variables or temporary files
