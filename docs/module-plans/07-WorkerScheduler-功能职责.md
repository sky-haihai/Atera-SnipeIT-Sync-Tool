# Worker Scheduler - 功能职责

## 1. 模块目标

Worker Scheduler Module 负责在后台按照用户配置的时间计划自动触发 sync run。

正常自动同步应像备份任务的 schedule 一样可预测、可审计、可暂停，并支持常见的 daily / weekly / monthly 时间规则。UI 表达可以参考 Veeam backup job schedule 的思路：用户选择“每天/每周/每月”，再选择具体时间、星期或日期。

## 2. 输入

本模块接收：

- `ISyncOrchestrator`
- `ISyncStatusStore`
- `INotificationPublisher`
- schedule configuration
- runtime cancellation token

Schedule configuration 应支持：

- schedule enabled/disabled
- daily schedule
- weekly schedule
- monthly schedule
- one or more local run times
- time zone id
- prevent overlapping runs
- missed-run handling policy

## 3. 输出

本模块输出：

- scheduled sync executions
- latest sync status updates
- optional notification requests on failure
- scheduler warnings/failures

本模块不直接返回 UI report；最终 run result 应由 Sync Orchestrator 和 Status Store 保存。

## 4. 正常 Scheduler 行为

Scheduler 自动 sync 是无人值守流程：

- 按配置时间自动运行
- 不弹 UI
- 不等待人工确认
- 不生成手动 sync preflight CSV
- 使用 normal sync result/status/report 作为运行记录
- 失败时写 status 并可触发 notification

## 5. Manual Sync 与 Scheduler 的区别

Manual sync 是人工触发流程，并且有两个不同按钮：

- `Sync Now`：直接进入真实 sync pipeline，不生成临时 CSV，不等待人工确认
- `Preview Changes`：先运行 preflight，生成临时 CSV 计划表，用户审核 CSV 后可以确认执行
- `Preview Changes` 确认执行时不按第一次 CSV 快照导入，而是重新进入真实 sync pipeline 并重新计算当前状态
- 临时 CSV 和最终 sync result/report 必须分开保存

Scheduler sync 是自动流程：

- 不生成临时 CSV 计划表
- 不等待人工确认
- 只按 schedule 执行
- 和 `Sync Now` / `Preview Changes` 确认后的执行共享同一套真实 sync pipeline

## 6. Schedule 类型

### 6.1 Daily

Daily schedule 表示每天在指定时间运行。

示例：

```text
Every day at 02:00
Every day at 02:00 and 14:00
```

### 6.2 Weekly

Weekly schedule 表示在指定星期几的指定时间运行。

示例：

```text
Every Monday at 02:00
Every Monday, Wednesday, Friday at 23:00
```

### 6.3 Monthly

Monthly schedule 表示在每月指定日期或月末运行。

示例：

```text
Day 1 of every month at 02:00
Last day of every month at 23:00
```

第一版 monthly 可以只支持：

- day number 1-31
- last day of month

如果某月没有指定 day number，例如 31 日，第一版应跳过该月该次运行，或明确按 `LastDay` 选项配置。

## 7. 成功条件

Scheduler 成功运行时应满足：

- 能从配置计算下一次运行时间
- 到达运行时间后调用 `ISyncOrchestrator.RunOnceAsync`
- 运行完成后调用 `ISyncStatusStore.SaveAsync`
- 同一时间不会重叠运行两个 sync
- cancellation 可以停止 scheduler loop
- disabled schedule 不触发自动 sync
- schedule 计算使用配置的 time zone

## 8. 失败条件

以下情况应记录 failure/status，而不是静默忽略：

- schedule config malformed
- time zone id invalid
- run time invalid
- orchestrator run failed
- status save failed
- notification publish failed

如果一次 sync 仍在运行，下一次 schedule 触发时应跳过并记录 warning，不能并发运行。

## 9. 不负责事项

本模块不负责：

- 调用 Atera API
- 调用 Snipe-IT API
- 执行 mapping
- 判断 asset create/update 细节
- 生成手动 sync preflight CSV
- 显示 TrayApp UI
- 编辑配置文件
- 实现 Windows Service 安装器

## 10. 扩展点

后续可扩展：

- active hours / backup window
- retry-after-failure schedule
- blackout dates
- manual run queue
- pause until date/time
- holiday calendar
- per-job schedule profiles
