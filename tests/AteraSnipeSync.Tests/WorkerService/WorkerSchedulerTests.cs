using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Configuration;
using AteraSnipeSync.Core.Notifications;
using AteraSnipeSync.Core.Scheduling;
using AteraSnipeSync.Core.Status;
using AteraSnipeSync.Core.Sync;
using AteraSnipeSync.WorkerService;
using Microsoft.Extensions.Logging.Abstractions;

namespace AteraSnipeSync.Tests.WorkerService;

/// <summary>
/// Verifies scheduled execution uses the shared run gate and creates a fresh runtime for every accepted run.
/// </summary>
public sealed class WorkerSchedulerTests
{
    [Fact]
    public async Task RunScheduledSyncAsync_WhenBusy_SkipsBeforeRuntimeCreation()
    {
        var coordinator = new SyncRunCoordinator();
        Assert.True(coordinator.TryAcquire(
            "preview-1",
            WorkerOperationNames.Preview,
            DateTimeOffset.UtcNow,
            out var lease));
        var runtimeFactory = new CountingRuntimeFactory(CreateRuntime(new CompletedOrchestrator()));
        var scheduler = await CreateSchedulerAsync(coordinator, runtimeFactory);

        var ran = await scheduler.RunScheduledSyncAsync(CancellationToken.None);

        Assert.False(ran);
        Assert.Equal(0, runtimeFactory.CallCount);
        lease!.Dispose();
    }

    [Fact]
    public async Task RunScheduledSyncAsync_ReloadsRuntimeForEachRun_AndForcesScheduledRequest()
    {
        var orchestrator = new CompletedOrchestrator();
        var runtimeFactory = new CountingRuntimeFactory(CreateRuntime(orchestrator));
        var scheduler = await CreateSchedulerAsync(new SyncRunCoordinator(), runtimeFactory);

        Assert.True(await scheduler.RunScheduledSyncAsync(CancellationToken.None));
        Assert.True(await scheduler.RunScheduledSyncAsync(CancellationToken.None));

        Assert.Equal(2, runtimeFactory.CallCount);
        Assert.Equal(2, orchestrator.Requests.Count);
        Assert.All(orchestrator.Requests, request =>
        {
            Assert.Equal("scheduled", request.Sync.TriggeredBy);
            Assert.False(request.Sync.DryRun);
            Assert.False(request.SnipeIt.DryRun);
            Assert.False(request.SnipeIt.ManualPreflightCsvEnabled);
        });
    }

    [Fact]
    public async Task StartAsync_PersistsDueClaimBeforeExecuting_AndUsesFifteenSecondPolling()
    {
        var now = DateTimeOffset.Parse("2026-07-17T13:00:01Z");
        var schedule = new SyncScheduleOptions
        {
            Enabled = true,
            Frequency = ScheduleFrequency.Daily,
            TimeZoneId = "UTC",
            RunTimes = [new TimeOnly(13, 0)],
            PreventOverlappingRuns = true
        };
        var order = new List<string>();
        var stateStore = new OrderedStateStore(
            new ScheduleRuntimeState
            {
                RuleFingerprint = ScheduleRuleFingerprint.Create(schedule),
                NextRunUtc = DateTimeOffset.Parse("2026-07-17T13:00:00Z"),
                LastTriggeredUtc = null
            },
            order);
        var timeProvider = new FixedTimeProvider(now);
        var manager = new WorkerScheduleManager(
            new ScheduledSettingsReader(schedule),
            stateStore,
            new ScheduleCalculator(),
            timeProvider,
            NullLogger<WorkerScheduleManager>.Instance);
        await manager.InitializeAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var orchestrator = new OrderedOrchestrator(order, cancellation);
        var scheduler = new WorkerScheduler(
            manager,
            new SyncRunCoordinator(),
            new CountingRuntimeFactory(CreateRuntime(orchestrator)),
            new CapturingStatusStore(),
            new NullNotificationPublisher(NullLogger<NullNotificationPublisher>.Instance),
            new NotificationEventFilter(),
            timeProvider,
            NullLogger<WorkerScheduler>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.StartAsync(cancellation.Token));

        Assert.Equal(TimeSpan.FromSeconds(15), WorkerScheduler.PollInterval);
        Assert.Equal(["claim", "run"], order);
        Assert.Equal(DateTimeOffset.Parse("2026-07-17T13:00:00Z"), stateStore.State.LastTriggeredUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-07-18T13:00:00Z"), stateStore.State.NextRunUtc);
    }

    private static async Task<WorkerScheduler> CreateSchedulerAsync(
        ISyncRunCoordinator coordinator,
        IWorkerRuntimeFactory runtimeFactory)
    {
        var manager = new WorkerScheduleManager(
            new DisabledSettingsReader(),
            new InMemoryScheduleRuntimeStateStore(),
            new ScheduleCalculator(),
            TimeProvider.System,
            NullLogger<WorkerScheduleManager>.Instance);
        await manager.InitializeAsync(CancellationToken.None);
        return new WorkerScheduler(
            manager,
            coordinator,
            runtimeFactory,
            new CapturingStatusStore(),
            new NullNotificationPublisher(NullLogger<NullNotificationPublisher>.Instance),
            new NotificationEventFilter(),
            TimeProvider.System,
            NullLogger<WorkerScheduler>.Instance);
    }

    private static WorkerSyncRuntime CreateRuntime(ISyncOrchestrator orchestrator)
    {
        return WorkerConnectionTesterTests.CreateRuntime(
            new NeverCalledAteraClient(),
            new HttpClient(new NeverCalledHandler()),
            orchestrator);
    }

    /// <summary>
    /// Counts per-trigger runtime construction while returning a fixed in-memory runtime.
    /// </summary>
    private sealed class CountingRuntimeFactory(WorkerSyncRuntime runtime) : IWorkerRuntimeFactory
    {
        public int CallCount { get; private set; }

        public Task<WorkerSyncRuntime> CreateSyncRuntimeAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(runtime);
        }
    }

    /// <summary>
    /// Captures the scheduled request and returns a successful structured result.
    /// </summary>
    private sealed class CompletedOrchestrator : ISyncOrchestrator
    {
        public List<SyncRunRequest> Requests { get; } = [];

        public Task<SyncRunResult> RunOnceAsync(
            SyncRunRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new SyncRunResult
            {
                Success = true,
                DryRun = request.Sync.DryRun,
                StartedAt = now,
                FinishedAt = now,
                PullResult = null,
                ImportBatch = null,
                ImportResult = null,
                Warnings = [],
                Failures = []
            });
        }
    }

    /// <summary>
    /// Keeps the background schedule disabled while direct trigger behavior is tested.
    /// </summary>
    private sealed class DisabledSettingsReader : ILocalAppSettingsReader
    {
        public Task<SyncAppSettings?> LoadWorkerSyncSettingsAsync(CancellationToken cancellationToken)
            => Task.FromResult<SyncAppSettings?>(null);
        public Task<SyncScheduleOptions?> LoadSyncScheduleOptionsAsync(CancellationToken cancellationToken)
            => Task.FromResult<SyncScheduleOptions?>(null);
        public Task<NotificationConfig> LoadNotificationConfigAsync(CancellationToken cancellationToken)
            => Task.FromResult(new NotificationConfig { Enabled = false, OnEvents = [] });
    }

    /// <summary>
    /// Supplies one enabled schedule for immediate due-occurrence integration testing.
    /// </summary>
    private sealed class ScheduledSettingsReader(SyncScheduleOptions schedule) : ILocalAppSettingsReader
    {
        public Task<SyncAppSettings?> LoadWorkerSyncSettingsAsync(CancellationToken cancellationToken)
            => Task.FromResult<SyncAppSettings?>(null);
        public Task<SyncScheduleOptions?> LoadSyncScheduleOptionsAsync(CancellationToken cancellationToken)
            => Task.FromResult<SyncScheduleOptions?>(schedule);
        public Task<NotificationConfig> LoadNotificationConfigAsync(CancellationToken cancellationToken)
            => Task.FromResult(new NotificationConfig { Enabled = false, OnEvents = [] });
    }

    /// <summary>
    /// Records durable claim order while retaining the latest UTC schedule state.
    /// </summary>
    private sealed class OrderedStateStore(ScheduleRuntimeState state, List<string> order)
        : IScheduleRuntimeStateStore
    {
        public ScheduleRuntimeState State { get; private set; } = state;

        public Task<ScheduleRuntimeState?> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult<ScheduleRuntimeState?>(State);

        public Task SaveAsync(ScheduleRuntimeState state, CancellationToken cancellationToken)
        {
            order.Add("claim");
            State = state;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Records execution and stops the polling loop after the first scheduled run completes.
    /// </summary>
    private sealed class OrderedOrchestrator(List<string> order, CancellationTokenSource cancellation)
        : ISyncOrchestrator
    {
        public Task<SyncRunResult> RunOnceAsync(
            SyncRunRequest request,
            CancellationToken cancellationToken)
        {
            order.Add("run");
            var now = DateTimeOffset.Parse("2026-07-17T13:00:01Z");
            cancellation.Cancel();
            return Task.FromResult(new SyncRunResult
            {
                Success = true,
                DryRun = request.Sync.DryRun,
                StartedAt = now,
                FinishedAt = now,
                PullResult = null,
                ImportBatch = null,
                ImportResult = null,
                Warnings = [],
                Failures = []
            });
        }
    }

    /// <summary>
    /// Freezes scheduler UTC time for deterministic immediate-trigger behavior.
    /// </summary>
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    /// <summary>
    /// Counts successful latest-status persistence without writing files.
    /// </summary>
    private sealed class CapturingStatusStore : ISyncStatusStore
    {
        public Task SaveAsync(SyncRunResult result, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task<SyncStatusSnapshot?> ReadLatestAsync(CancellationToken cancellationToken)
            => Task.FromResult<SyncStatusSnapshot?>(null);
    }

    /// <summary>
    /// Guards scheduler tests against accidentally invoking the connection-test client.
    /// </summary>
    private sealed class NeverCalledAteraClient : IAteraClient
    {
        public Task<AteraPullResult> PullInventoryAsync(
            AteraPullRequest request,
            CancellationToken cancellationToken,
            IProgress<AteraSnipeSync.Core.Common.SyncProgressUpdate>? progress = null)
            => throw new InvalidOperationException("Not expected.");
    }

    /// <summary>
    /// Guards scheduler tests against accidental Snipe-IT HTTP calls.
    /// </summary>
    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Not expected.");
    }
}
