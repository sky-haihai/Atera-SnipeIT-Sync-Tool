# Worker Scheduler - 单元测试指导手册

## 1. 测试目标

Worker Scheduler tests verify:

- daily / weekly / monthly next-run calculation
- monthly 29/30/31 behavior skips months where the selected date does not exist
- monthly last-day-of-month behavior
- invalid weekly/monthly schedules are rejected before save/use
- scheduler-triggered sync sets `TriggeredBy = "scheduled"`
- scheduler-triggered sync disables manual preflight CSV
- overlapping scheduled runs are skipped when `PreventOverlappingRuns = true`

Scheduled sync must not create manual preflight CSV files. The CSV preview flow belongs only to manual sync.

## 2. 测试文件

```text
tests/AteraSnipeSync.Tests/Scheduling/ScheduleCalculatorTests.cs
tests/AteraSnipeSync.Tests/Scheduling/ScheduledSyncRequestFactoryTests.cs
tests/AteraSnipeSync.Tests/Scheduling/SyncSchedulerTests.cs
```

Production code:

```text
src/AteraSnipeSync.Core/Scheduling/SyncScheduleOptions.cs
src/AteraSnipeSync.Core/Scheduling/ScheduleFrequency.cs
src/AteraSnipeSync.Core/Scheduling/ScheduleCalculator.cs
src/AteraSnipeSync.Core/Scheduling/ScheduledSyncRequestFactory.cs
src/AteraSnipeSync.Core/Scheduling/SyncScheduler.cs
```

## 3. 运行测试

```powershell
dotnet build AteraSnipeSync.sln --no-restore
dotnet test AteraSnipeSync.sln --no-build
```

Only scheduler tests:

```powershell
dotnet test .\tests\AteraSnipeSync.Tests\AteraSnipeSync.Tests.csproj --filter "FullyQualifiedName~AteraSnipeSync.Tests.Scheduling"
```

## 4. 重点测试说明

`GetNextRunUtc_ReturnsNextDailyTime`

- verifies multiple local run times are sorted and the next future time is selected

`GetNextRunUtc_ReturnsNextWeeklyDay`

- verifies selected weekdays are honored

`GetNextRunUtc_SkipsMonthlyDay_WhenMonthDoesNotContainThatDay`

- verifies a day like 31 is skipped in months without that date

`GetNextRunUtc_ReturnsMonthlyLastDay_WhenConfigured`

- verifies `RunOnLastDayOfMonth` schedules the actual final day of the month

`CreateScheduledRequest_ForcesScheduledTriggerAndDisablesManualPreflightCsv`

- verifies automatic scheduler sync cannot accidentally generate manual CSV preview files

`RunScheduledSyncAsync_SkipsSecondRun_WhenOverlapPreventionEnabled`

- verifies a second scheduled trigger is skipped while the previous run is still active

## 5. Mocking Strategy

Scheduler tests use fake in-memory implementations of:

- `ISyncOrchestrator`
- `ISyncStatusStore`
- `INotificationPublisher`

Tests must not call real Atera or Snipe-IT APIs. Scheduled request tests assert the request shape passed to the orchestrator instead of executing network work.
