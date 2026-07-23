using AteraSnipeSync.Core.Configuration;
using AteraSnipeSync.Core.Scheduling;
using AteraSnipeSync.WorkerService;
using Microsoft.Extensions.Logging.Abstractions;

namespace AteraSnipeSync.Tests.WorkerService;

/// <summary>
/// Verifies disk-backed schedule initialization, reload signaling, and fail-closed invalid configuration behavior.
/// </summary>
public sealed class WorkerScheduleManagerTests
{
    [Fact]
    public async Task ReloadAsync_AppliesNewSchedule_AndWakesExistingWaiter()
    {
        var reader = new FakeSettingsReader { Schedule = null };
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-07-17T12:00:00Z"));
        var manager = CreateManager(reader, time);
        var initialized = await manager.InitializeAsync(CancellationToken.None);
        var wait = manager.WaitForChangeAsync(initialized.Snapshot.Version, CancellationToken.None);
        reader.Schedule = DailyAt(new TimeOnly(13, 0));

        var reloaded = await manager.ReloadAsync(CancellationToken.None);
        await wait.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(reloaded.Applied);
        Assert.True(reloaded.Snapshot.ConfigurationValid);
        Assert.True(reloaded.Snapshot.Enabled);
        Assert.Equal(DateTimeOffset.Parse("2026-07-17T13:00:00Z"), reloaded.Snapshot.NextRunUtc);
        Assert.True(reloaded.Snapshot.Version > initialized.Snapshot.Version);
    }

    [Fact]
    public async Task ReloadAsync_InvalidSchedule_DisablesFutureTriggers()
    {
        var reader = new FakeSettingsReader { Schedule = DailyAt(new TimeOnly(13, 0)) };
        var manager = CreateManager(
            reader,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-07-17T12:00:00Z")));
        await manager.InitializeAsync(CancellationToken.None);
        reader.ScheduleException = new InvalidOperationException("malformed with secret-value");

        var result = await manager.ReloadAsync(CancellationToken.None);

        Assert.False(result.Applied);
        Assert.False(result.Snapshot.ConfigurationValid);
        Assert.False(result.Snapshot.Enabled);
        Assert.Null(result.Snapshot.NextRunUtc);
        Assert.DoesNotContain("secret-value", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClaimDueOccurrenceAsync_PersistsClaimAndComputesFollowingOccurrence()
    {
        var reader = new FakeSettingsReader { Schedule = DailyAt(new TimeOnly(13, 0)) };
        var manager = CreateManager(
            reader,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-07-17T12:00:00Z")));
        await manager.InitializeAsync(CancellationToken.None);

        var claimed = await manager.ClaimDueOccurrenceAsync(
            DateTimeOffset.Parse("2026-07-17T13:00:01Z"),
            CancellationToken.None);

        Assert.True(claimed);
        Assert.Equal(DateTimeOffset.Parse("2026-07-18T13:00:00Z"), manager.Current.NextRunUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-07-17T13:00:00Z"), manager.Current.LastTriggeredUtc);
    }

    [Fact]
    public async Task InitializeAsync_RetainsOverdueOccurrence_AndCoalescesBacklogIntoOneClaim()
    {
        var schedule = DailyAt(new TimeOnly(13, 0));
        var stateStore = new InMemoryScheduleRuntimeStateStore
        {
            State = new ScheduleRuntimeState
            {
                RuleFingerprint = ScheduleRuleFingerprint.Create(schedule),
                NextRunUtc = DateTimeOffset.Parse("2026-07-14T13:00:00Z"),
                LastTriggeredUtc = DateTimeOffset.Parse("2026-07-13T13:00:00Z")
            }
        };
        var now = DateTimeOffset.Parse("2026-07-17T14:00:00Z");
        var manager = CreateManager(
            new FakeSettingsReader { Schedule = schedule },
            new FixedTimeProvider(now),
            stateStore);

        await manager.InitializeAsync(CancellationToken.None);
        var firstClaim = await manager.ClaimDueOccurrenceAsync(now, CancellationToken.None);
        var secondClaim = await manager.ClaimDueOccurrenceAsync(now, CancellationToken.None);
        var restartedManager = CreateManager(
            new FakeSettingsReader { Schedule = schedule },
            new FixedTimeProvider(now),
            stateStore);
        await restartedManager.InitializeAsync(CancellationToken.None);
        var restartClaim = await restartedManager.ClaimDueOccurrenceAsync(now, CancellationToken.None);

        Assert.True(firstClaim);
        Assert.False(secondClaim);
        Assert.False(restartClaim);
        Assert.Equal(DateTimeOffset.Parse("2026-07-14T13:00:00Z"), stateStore.State?.LastTriggeredUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-07-18T13:00:00Z"), stateStore.State?.NextRunUtc);
        Assert.Equal(1, stateStore.SaveCount);
    }

    [Fact]
    public async Task InitializeAsync_DiscardsState_WhenRuleFingerprintChanges()
    {
        var oldSchedule = DailyAt(new TimeOnly(13, 0));
        var newSchedule = DailyAt(new TimeOnly(15, 0));
        var stateStore = new InMemoryScheduleRuntimeStateStore
        {
            State = new ScheduleRuntimeState
            {
                RuleFingerprint = ScheduleRuleFingerprint.Create(oldSchedule),
                NextRunUtc = DateTimeOffset.Parse("2026-07-17T13:00:00Z"),
                LastTriggeredUtc = null
            }
        };
        var manager = CreateManager(
            new FakeSettingsReader { Schedule = newSchedule },
            new FixedTimeProvider(DateTimeOffset.Parse("2026-07-17T12:00:00Z")),
            stateStore);

        var result = await manager.InitializeAsync(CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal(DateTimeOffset.Parse("2026-07-17T15:00:00Z"), result.Snapshot.NextRunUtc);
        Assert.Equal(ScheduleRuleFingerprint.Create(newSchedule), stateStore.State?.RuleFingerprint);
        Assert.Equal(1, stateStore.SaveCount);
    }

    [Fact]
    public async Task ClaimDueOccurrenceAsync_DoesNotPublishClaim_WhenPersistenceFails()
    {
        var schedule = DailyAt(new TimeOnly(13, 0));
        var stateStore = new SaveFailingStateStore
        {
            State = new ScheduleRuntimeState
            {
                RuleFingerprint = ScheduleRuleFingerprint.Create(schedule),
                NextRunUtc = DateTimeOffset.Parse("2026-07-17T13:00:00Z"),
                LastTriggeredUtc = null
            }
        };
        var manager = CreateManager(
            new FakeSettingsReader { Schedule = schedule },
            new FixedTimeProvider(DateTimeOffset.Parse("2026-07-17T12:00:00Z")),
            stateStore);
        await manager.InitializeAsync(CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => manager.ClaimDueOccurrenceAsync(
            DateTimeOffset.Parse("2026-07-17T13:00:01Z"),
            CancellationToken.None));

        Assert.Equal(DateTimeOffset.Parse("2026-07-17T13:00:00Z"), manager.Current.NextRunUtc);
        Assert.Null(manager.Current.LastTriggeredUtc);
    }

    [Fact]
    public async Task InitializeAsync_RebuildsState_WhenLoadingStateFails()
    {
        var stateStore = new LoadFailingStateStore();
        var manager = CreateManager(
            new FakeSettingsReader { Schedule = DailyAt(new TimeOnly(13, 0)) },
            new FixedTimeProvider(DateTimeOffset.Parse("2026-07-17T12:00:00Z")),
            stateStore);

        var result = await manager.InitializeAsync(CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal(DateTimeOffset.Parse("2026-07-17T13:00:00Z"), result.Snapshot.NextRunUtc);
        Assert.Equal(1, stateStore.SaveCount);
    }

    [Fact]
    public async Task ReloadAsync_ReenabledScheduleDoesNotReuseOccurrenceFromBeforeDisable()
    {
        var schedule = DailyAt(new TimeOnly(13, 0));
        var reader = new FakeSettingsReader { Schedule = null };
        var stateStore = new InMemoryScheduleRuntimeStateStore
        {
            State = new ScheduleRuntimeState
            {
                RuleFingerprint = ScheduleRuleFingerprint.Create(schedule),
                NextRunUtc = DateTimeOffset.Parse("2026-07-16T13:00:00Z"),
                LastTriggeredUtc = null
            }
        };
        var manager = CreateManager(
            reader,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-07-17T12:00:00Z")),
            stateStore);

        await manager.InitializeAsync(CancellationToken.None);
        reader.Schedule = schedule;
        var reenabled = await manager.ReloadAsync(CancellationToken.None);

        Assert.True(reenabled.Snapshot.Enabled);
        Assert.Equal(DateTimeOffset.Parse("2026-07-17T13:00:00Z"), reenabled.Snapshot.NextRunUtc);
        Assert.Null(reenabled.Snapshot.LastTriggeredUtc);
        Assert.Equal(2, stateStore.SaveCount);
    }

    private static WorkerScheduleManager CreateManager(
        ILocalAppSettingsReader reader,
        TimeProvider timeProvider,
        IScheduleRuntimeStateStore? stateStore = null)
    {
        return new WorkerScheduleManager(
            reader,
            stateStore ?? new InMemoryScheduleRuntimeStateStore(),
            new ScheduleCalculator(),
            timeProvider,
            NullLogger<WorkerScheduleManager>.Instance);
    }

    private static SyncScheduleOptions DailyAt(TimeOnly time)
    {
        return new SyncScheduleOptions
        {
            Enabled = true,
            Frequency = ScheduleFrequency.Daily,
            TimeZoneId = "UTC",
            RunTimes = [time],
            PreventOverlappingRuns = true
        };
    }

    /// <summary>
    /// Freezes UTC time so next-run calculations are deterministic.
    /// </summary>
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    /// <summary>
    /// Exposes a replaceable schedule snapshot and no sync credentials.
    /// </summary>
    private sealed class FakeSettingsReader : ILocalAppSettingsReader
    {
        public SyncScheduleOptions? Schedule { get; set; }
        public Exception? ScheduleException { get; set; }

        public Task<SyncAppSettings?> LoadWorkerSyncSettingsAsync(CancellationToken cancellationToken)
            => Task.FromResult<SyncAppSettings?>(null);

        public Task<SyncScheduleOptions?> LoadSyncScheduleOptionsAsync(CancellationToken cancellationToken)
            => ScheduleException is null
                ? Task.FromResult(Schedule)
                : Task.FromException<SyncScheduleOptions?>(ScheduleException);

        public Task<NotificationConfig> LoadNotificationConfigAsync(CancellationToken cancellationToken)
            => Task.FromResult(new NotificationConfig { Enabled = false, OnEvents = [] });
    }

    /// <summary>
    /// Simulates an atomic state-write failure to verify a trigger is not published as claimed in memory.
    /// </summary>
    private sealed class SaveFailingStateStore : IScheduleRuntimeStateStore
    {
        public ScheduleRuntimeState? State { get; init; }

        public Task<ScheduleRuntimeState?> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(State);

        public Task SaveAsync(ScheduleRuntimeState state, CancellationToken cancellationToken)
            => Task.FromException(new IOException("state write failed"));
    }

    /// <summary>
    /// Simulates a corrupt state document while accepting the manager's safe rebuilt replacement.
    /// </summary>
    private sealed class LoadFailingStateStore : IScheduleRuntimeStateStore
    {
        public int SaveCount { get; private set; }

        public Task<ScheduleRuntimeState?> LoadAsync(CancellationToken cancellationToken)
            => Task.FromException<ScheduleRuntimeState?>(new InvalidDataException("corrupt state"));

        public Task SaveAsync(ScheduleRuntimeState state, CancellationToken cancellationToken)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }
}
