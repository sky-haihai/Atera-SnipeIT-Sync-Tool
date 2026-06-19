# Worker Scheduler - 技术规格

## 1. 目标

Worker Scheduler Module 负责根据 schedule configuration 自动触发 sync run。

本技术规格基于：

- `docs/module-plans/07-WorkerScheduler-功能职责.md`
- 项目 master plan 中 Runtime Scheduler / Worker 模块边界

本模块只负责任务调度和调用 orchestrator，不直接调用 Atera 或 Snipe-IT API。

## 2. Schedule Configuration Contracts

新增 production models：

```text
src/AteraSnipeSync.Core/Scheduling/
```

### 2.1 `SyncScheduleOptions`

Namespace:

```csharp
namespace AteraSnipeSync.Core.Scheduling;
```

Signature:

```csharp
public sealed class SyncScheduleOptions
{
    public required bool Enabled { get; init; }
    public required ScheduleFrequency Frequency { get; init; }
    public required string TimeZoneId { get; init; }
    public required IReadOnlyList<TimeOnly> RunTimes { get; init; }
    public IReadOnlyList<DayOfWeek> DaysOfWeek { get; init; } = [];
    public IReadOnlyList<int> DaysOfMonth { get; init; } = [];
    public required bool RunOnLastDayOfMonth { get; init; }
    public required bool PreventOverlappingRuns { get; init; }
    public required MissedRunPolicy MissedRunPolicy { get; init; }
}
```

职责：

- describe one scheduled sync job
- support daily, weekly, and monthly schedules
- use local wall-clock times in the configured time zone

Validation:

- `TimeZoneId` must resolve through `TimeZoneInfo.FindSystemTimeZoneById`
- `RunTimes` must not be empty when `Enabled = true`
- `Daily` ignores `DaysOfWeek`, `DaysOfMonth`, and `RunOnLastDayOfMonth`
- `Weekly` requires at least one `DaysOfWeek`
- `Monthly` requires at least one `DaysOfMonth` or `RunOnLastDayOfMonth = true`
- `DaysOfMonth` values must be 1-31

### 2.2 `ScheduleFrequency`

```csharp
public enum ScheduleFrequency
{
    Daily,
    Weekly,
    Monthly
}
```

### 2.3 `MissedRunPolicy`

```csharp
public enum MissedRunPolicy
{
    Skip,
    RunOnceImmediately
}
```

First implementation may default to `Skip`.

## 3. Scheduler Service

### 3.1 `SyncScheduler`

Namespace:

```csharp
namespace AteraSnipeSync.Core.Scheduling;
```

Signature:

```csharp
public sealed class SyncScheduler : ISyncScheduler
{
    public SyncScheduler(
        ISyncOrchestrator orchestrator,
        ISyncStatusStore statusStore,
        INotificationPublisher notificationPublisher,
        SyncScheduleOptions scheduleOptions,
        SyncRunRequest syncRunRequest,
        TimeProvider timeProvider,
        ILogger<SyncScheduler> logger);

    public Task StartAsync(CancellationToken cancellationToken);
}
```

Responsibilities:

- validate schedule options
- calculate next due run
- wait until due time
- call `ISyncOrchestrator.RunOnceAsync`
- save result with `ISyncStatusStore.SaveAsync`
- publish notification on failure when notification module is configured
- prevent overlapping runs
- honor cancellation

Scheduler-triggered sync must set:

```csharp
SyncRunOptions.TriggeredBy = "scheduled"
```

Scheduler-triggered sync must not enable manual preflight CSV. Manual preflight CSV belongs to manual sync flow.

Manual sync has two UI-triggered request shapes outside this scheduler module:

- `Sync Now`: direct real sync, `TriggeredBy = "manual"`, `ManualPreflightCsvEnabled = false`
- `Preview Changes`: dry-run preview, `TriggeredBy = "manual-preview"`, `ManualPreflightCsvEnabled = true`

Scheduler shares the same real sync pipeline as `Sync Now`, but it must always use `TriggeredBy = "scheduled"` and must never generate preflight CSV.

### 3.2 `ScheduleCalculator`

Namespace:

```csharp
namespace AteraSnipeSync.Core.Scheduling;
```

Signature:

```csharp
internal sealed class ScheduleCalculator
{
    public DateTimeOffset? GetNextRun(
        SyncScheduleOptions options,
        DateTimeOffset nowUtc);
}
```

Responsibilities:

- convert `nowUtc` to configured local time zone
- find the next valid local scheduled time
- convert the selected local time back to UTC
- return `null` when schedule is disabled

## 4. Time Calculation Rules

### 4.1 Daily

Given:

```text
Frequency = Daily
RunTimes = [02:00, 14:00]
```

If current local time is before 02:00, next run is today 02:00.

If current local time is between 02:00 and 14:00, next run is today 14:00.

If current local time is after 14:00, next run is tomorrow 02:00.

### 4.2 Weekly

Given:

```text
Frequency = Weekly
DaysOfWeek = [Monday, Wednesday, Friday]
RunTimes = [23:00]
```

The calculator must scan forward from current local date/time until it finds the next selected weekday and run time.

### 4.3 Monthly

Given:

```text
Frequency = Monthly
DaysOfMonth = [1, 15]
RunTimes = [02:00]
```

The calculator must scan forward month by month until it finds a valid day/time.

If `DaysOfMonth` contains a day that does not exist in a month, skip that day for that month.

If `RunOnLastDayOfMonth = true`, include the actual last day for each month.

### 4.4 Time Zone and DST

Time calculations must use `TimeZoneInfo`.

For first implementation:

- invalid local times during DST transitions should be skipped
- ambiguous local times should use the earlier UTC occurrence
- all persisted status timestamps remain UTC

## 5. Runtime Loop

Pseudo-flow:

```text
StartAsync
  -> validate options
  -> while not cancelled
       -> calculate next run UTC
       -> delay until next run
       -> if already running and PreventOverlappingRuns:
            record warning and continue
       -> call orchestrator
       -> save status
       -> publish notification if failure
```

`Task.Delay` must use the supplied cancellation token.

## 6. No Overlap Rule

If a previous run is still active when the next run is due:

- do not start a second run
- record scheduler warning
- continue calculating the next scheduled time

## 7. Tests

Create tests under:

```text
tests/AteraSnipeSync.Tests/Scheduling/
```

Required tests:

1. `GetNextRun_ReturnsNull_WhenScheduleDisabled`
2. `GetNextRun_ReturnsTodayNextTime_ForDailySchedule`
3. `GetNextRun_ReturnsTomorrowFirstTime_WhenDailyTimesPassed`
4. `GetNextRun_ReturnsNextSelectedWeekday_ForWeeklySchedule`
5. `GetNextRun_ReturnsNextValidMonthDay_ForMonthlySchedule`
6. `GetNextRun_UsesLastDayOfMonth_WhenConfigured`
7. `Validate_Throws_WhenWeeklyHasNoDays`
8. `Validate_Throws_WhenMonthlyHasNoDays`
9. `StartAsync_DoesNotRunOverlappingSync_WhenPreviousRunActive`
10. `StartAsync_SavesStatus_AfterRunCompletes`

Tests must use fake orchestrator/status/notification dependencies and a fake or controlled `TimeProvider`. They must not call Atera or Snipe-IT.

## 8. Acceptance Criteria

- Scheduler supports daily, weekly, and monthly schedules
- Scheduler uses configured time zone
- Scheduler prevents overlapping runs
- Scheduler calls orchestrator with `TriggeredBy = "scheduled"`
- Scheduler does not enable manual preflight CSV
- Scheduler saves run status
- Scheduler can be cancelled cleanly
- `dotnet build AteraSnipeSync.sln --no-restore` passes
- `dotnet test AteraSnipeSync.sln --no-build` passes
