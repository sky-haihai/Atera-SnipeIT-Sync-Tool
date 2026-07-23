using System.Net;
using System.Text.Json;
using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Configuration;
using AteraSnipeSync.Core.Notifications;
using AteraSnipeSync.Core.Runtime.Ipc;
using AteraSnipeSync.Core.Scheduling;
using AteraSnipeSync.Core.SnipeIt;
using AteraSnipeSync.Core.Status;
using AteraSnipeSync.Core.Sync;
using AteraSnipeSync.WorkerService;
using Microsoft.Extensions.Logging.Abstractions;

namespace AteraSnipeSync.Tests.WorkerService;

/// <summary>
/// Verifies command dispatch, global busy behavior, cancellation, schedule reload, and IPC-safe result projection.
/// </summary>
public sealed class WorkerCommandHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_SecondRunReturnsBusy_AndCancelTargetsFirstRequest()
    {
        var blocking = new BlockingOrchestrator();
        var fixture = await CreateFixtureAsync(blocking);
        var firstRequest = Request(WorkerIpcCommands.PreviewChanges, "preview-1");
        var first = fixture.Handler.ExecuteAsync(firstRequest, progress: null, CancellationToken.None);
        await blocking.Started.WaitAsync(TimeSpan.FromSeconds(2));

        var second = await fixture.Handler.ExecuteAsync(
            Request(WorkerIpcCommands.SyncNow, "sync-2"),
            progress: null,
            CancellationToken.None);
        var cancelled = fixture.Handler.TryCancel("preview-1");
        var firstResult = await first;

        Assert.Equal(WorkerIpcEventTypes.Busy, second.EventType);
        Assert.Equal(WorkerOperationNames.Preview, second.ActiveOperation);
        Assert.True(cancelled);
        Assert.Equal(WorkerIpcEventTypes.Cancelled, firstResult.EventType);
        Assert.False(fixture.Coordinator.IsRunning);
    }

    [Fact]
    public async Task ExecuteAsync_ReloadSchedule_DoesNotRequireRunLease()
    {
        var fixture = await CreateFixtureAsync(new CompletedOrchestrator());
        Assert.True(fixture.Coordinator.TryAcquire(
            "scheduled-1",
            WorkerOperationNames.Scheduled,
            DateTimeOffset.UtcNow,
            out var lease));

        var result = await fixture.Handler.ExecuteAsync(
            Request(WorkerIpcCommands.ReloadSchedule, "reload-1"),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkerIpcEventTypes.Completed, result.EventType);
        Assert.True(result.ScheduleReload?.Applied);
        Assert.True(fixture.Coordinator.IsRunning);
        lease!.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_SyncNow_ReturnsSummaryWithoutRawAteraRecords()
    {
        var fixture = await CreateFixtureAsync(new ResultOrchestrator());

        var result = await fixture.Handler.ExecuteAsync(
            Request(WorkerIpcCommands.SyncNow, "sync-1"),
            progress: null,
            CancellationToken.None);
        var serialized = JsonSerializer.Serialize(result.SyncResult);

        Assert.Equal(WorkerIpcEventTypes.Completed, result.EventType);
        Assert.True(result.SyncResult?.Success);
        Assert.Equal(1, result.SyncResult?.Pulled);
        Assert.Equal(2, result.SyncResult?.Deleted);
        Assert.DoesNotContain("RawJson", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw-secret-record", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_PipelineFailureReportsRunFailureButZeroFailedAssets()
    {
        var fixture = await CreateFixtureAsync(new PipelineFailureOrchestrator());

        var result = await fixture.Handler.ExecuteAsync(
            Request(WorkerIpcCommands.PreviewChanges, "pipeline-failure"),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkerIpcEventTypes.Completed, result.EventType);
        Assert.False(result.SyncResult?.Success);
        Assert.True(result.SyncResult?.DryRun);
        Assert.Equal(0, result.SyncResult?.Failed);
        Assert.Single(result.SyncResult?.Failures ?? []);
    }

    [Fact]
    public async Task ExecuteAsync_SyncNow_PreservesOutcomeWhenLatestStatusSaveFails()
    {
        var fixture = await CreateFixtureAsync(
            new CompletedOrchestrator(),
            new ThrowingStatusStore());

        var result = await fixture.Handler.ExecuteAsync(
            Request(WorkerIpcCommands.SyncNow, "sync-status-failure"),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkerIpcEventTypes.Completed, result.EventType);
        Assert.Contains("Latest status could not be saved", result.Message, StringComparison.Ordinal);
        Assert.True(result.SyncResult?.Success);
    }

    [Fact]
    public async Task ExecuteAsync_SyncNow_PublishesCompletedNotification_WhenCompletedRunHasRecordFailures()
    {
        var publisher = new CapturingNotificationPublisher();
        var notificationConfig = new NotificationConfig
        {
            Enabled = true,
            OnEvents = [NotificationEventTypes.ManualSyncCompleted]
        };
        var fixture = await CreateFixtureAsync(
            new CompletedWithRecordFailuresOrchestrator(),
            notificationConfig: notificationConfig,
            notificationPublisher: publisher);

        var result = await fixture.Handler.ExecuteAsync(
            Request(WorkerIpcCommands.SyncNow, "sync-notification"),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkerIpcEventTypes.Completed, result.EventType);
        Assert.True(result.SyncResult?.Success);
        Assert.Equal(1, result.SyncResult?.Failed);
        Assert.Equal(NotificationEventTypes.ManualSyncCompleted, publisher.Request?.EventType);
        Assert.Equal("Warning", publisher.Request?.Severity);
        Assert.Contains("Result: Completed", publisher.Request?.Message);
        Assert.Same(notificationConfig, publisher.Config);
    }

    [Theory]
    [InlineData("TestAtera")]
    [InlineData("TestSnipeIt")]
    public async Task ExecuteAsync_RejectsLegacySplitConnectionCommands(string command)
    {
        var fixture = await CreateFixtureAsync(new CompletedOrchestrator());
        var result = await fixture.Handler.ExecuteAsync(
            Request(command, "legacy-1"),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkerIpcEventTypes.Error, result.EventType);
    }

    [Fact]
    public async Task ExecuteAsync_TestNotifications_ReturnsIndependentSanitizedResults()
    {
        var fixture = await CreateFixtureAsync(new CompletedOrchestrator());

        var result = await fixture.Handler.ExecuteAsync(
            Request(WorkerIpcCommands.TestNotifications, "notification-test-1"),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkerIpcEventTypes.Completed, result.EventType);
        Assert.NotNull(result.NotificationTest);
        Assert.False(result.NotificationTest.Email.Configured);
        Assert.False(result.NotificationTest.Webhook.Configured);
        Assert.False(fixture.Coordinator.IsRunning);
    }

    private static async Task<Fixture> CreateFixtureAsync(
        ISyncOrchestrator orchestrator,
        ISyncStatusStore? statusStore = null,
        NotificationConfig? notificationConfig = null,
        INotificationPublisher? notificationPublisher = null)
    {
        var settings = new ReloadableSettingsReader();
        var manager = new WorkerScheduleManager(
            settings,
            new InMemoryScheduleRuntimeStateStore(),
            new ScheduleCalculator(),
            TimeProvider.System,
            NullLogger<WorkerScheduleManager>.Instance);
        await manager.InitializeAsync(CancellationToken.None);
        var coordinator = new SyncRunCoordinator();
        var runtime = WorkerConnectionTesterTests.CreateRuntime(
            new SuccessfulAteraClient(),
            new HttpClient(new JsonHandler()),
            orchestrator,
            notificationConfig);
        var handler = new WorkerCommandHandler(
            coordinator,
            manager,
            new FixedRuntimeFactory(runtime),
            new WorkerConnectionTester(NullLogger<WorkerConnectionTester>.Instance),
            settings,
            new FakeNotificationTester(),
            notificationPublisher ?? new NullNotificationPublisher(NullLogger<NullNotificationPublisher>.Instance),
            new NotificationEventFilter(),
            statusStore ?? new CapturingStatusStore(),
            TimeProvider.System,
            NullLogger<WorkerCommandHandler>.Instance);
        return new Fixture(handler, coordinator);
    }

    private static WorkerIpcRequest Request(string command, string requestId)
    {
        return new WorkerIpcRequest
        {
            ProtocolVersion = WorkerIpcProtocol.Version,
            RequestId = requestId,
            Command = command
        };
    }

    /// <summary>
    /// Bundles the command handler and observable shared coordinator used by each test.
    /// </summary>
    private sealed record Fixture(
        WorkerCommandHandler Handler,
        SyncRunCoordinator Coordinator);

    /// <summary>
    /// Supplies a disabled schedule so reload commands can be tested without disk I/O.
    /// </summary>
    private sealed class ReloadableSettingsReader : ILocalAppSettingsReader
    {
        public Task<SyncAppSettings?> LoadWorkerSyncSettingsAsync(CancellationToken cancellationToken)
            => Task.FromResult<SyncAppSettings?>(null);
        public Task<SyncScheduleOptions?> LoadSyncScheduleOptionsAsync(CancellationToken cancellationToken)
            => Task.FromResult<SyncScheduleOptions?>(null);
        public Task<NotificationConfig> LoadNotificationConfigAsync(CancellationToken cancellationToken)
            => Task.FromResult(new NotificationConfig { Enabled = false, OnEvents = [] });
    }

    /// <summary>
    /// Returns the test's precomposed runtime for every command execution.
    /// </summary>
    private sealed class FixedRuntimeFactory(WorkerSyncRuntime runtime) : IWorkerRuntimeFactory
    {
        public Task<WorkerSyncRuntime> CreateSyncRuntimeAsync(CancellationToken cancellationToken)
            => Task.FromResult(runtime);
    }

    /// <summary>
    /// Returns deterministic channel results without contacting SMTP or webhook endpoints.
    /// </summary>
    private sealed class FakeNotificationTester : INotificationTester
    {
        public Task<NotificationTestResult> TestAsync(
            NotificationConfig config,
            CancellationToken cancellationToken)
            => Task.FromResult(new NotificationTestResult
            {
                Email = new NotificationChannelTestResult
                {
                    Configured = false,
                    Succeeded = false,
                    Message = "Email is not configured."
                },
                Webhook = new NotificationChannelTestResult
                {
                    Configured = false,
                    Succeeded = false,
                    Message = "Webhook is not configured."
                }
            });
    }

    /// <summary>
    /// Captures the manual notification request and exact configuration snapshot without external delivery.
    /// </summary>
    private sealed class CapturingNotificationPublisher : INotificationPublisher
    {
        public NotificationRequest? Request { get; private set; }
        public NotificationConfig? Config { get; private set; }

        public Task PublishAsync(
            NotificationRequest request,
            NotificationConfig config,
            CancellationToken cancellationToken)
        {
            Request = request;
            Config = config;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Holds one run open until Cancel propagates its request token.
    /// </summary>
    private sealed class BlockingOrchestrator : ISyncOrchestrator
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => _started.Task;

        public async Task<SyncRunResult> RunOnceAsync(
            SyncRunRequest request,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    /// <summary>
    /// Produces a minimal successful structured result without external work.
    /// </summary>
    private class CompletedOrchestrator : ISyncOrchestrator
    {
        public virtual Task<SyncRunResult> RunOnceAsync(
            SyncRunRequest request,
            CancellationToken cancellationToken)
        {
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
    /// Produces a completed pipeline with one record-level import failure for notification classification tests.
    /// </summary>
    private sealed class CompletedWithRecordFailuresOrchestrator : CompletedOrchestrator
    {
        public override Task<SyncRunResult> RunOnceAsync(
            SyncRunRequest request,
            CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new SyncRunResult
            {
                Success = true,
                DryRun = request.Sync.DryRun,
                StartedAt = now,
                FinishedAt = now,
                PullResult = null,
                ImportBatch = null,
                ImportResult = new SnipeImportResult
                {
                    CreatedAssets = 1,
                    UpdatedAssets = 0,
                    DeletedAssets = 0,
                    SkippedAssets = 0,
                    FailedAssets = 1,
                    CreatedCompanies = 0,
                    CreatedCategories = 0,
                    CreatedModels = 0,
                    UpdatedModels = 0,
                    DryRun = request.Sync.DryRun,
                    Actions = [],
                    Failures = [],
                    Warnings = []
                },
                Warnings = [],
                Failures =
                [
                    new SyncFailure
                    {
                        Stage = "SnipeImport",
                        Code = "SnipeImport.SerialConflict",
                        Message = "One asset could not be imported."
                    }
                ]
            });
        }
    }

    /// <summary>
    /// Produces a result containing a raw source marker to verify IPC projection removes it.
    /// </summary>
    private sealed class ResultOrchestrator : CompletedOrchestrator
    {
        public override Task<SyncRunResult> RunOnceAsync(
            SyncRunRequest request,
            CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new SyncRunResult
            {
                Success = true,
                DryRun = request.Sync.DryRun,
                StartedAt = now,
                FinishedAt = now,
                PullResult = new AteraPullResult
                {
                    Agents =
                    [
                        new AgentInfo
                        {
                            AgentId = "1",
                            Name = "device",
                            RawJson = "{\"secret\":\"raw-secret-record\"}"
                        }
                    ],
                    Summary = new PullSummary { AgentCount = 1, PulledAt = now },
                    Warnings = []
                },
                ImportBatch = null,
                ImportResult = new SnipeImportResult
                {
                    CreatedAssets = 0,
                    UpdatedAssets = 0,
                    DeletedAssets = 2,
                    SkippedAssets = 0,
                    FailedAssets = 0,
                    CreatedCompanies = 0,
                    CreatedCategories = 0,
                    CreatedModels = 0,
                    UpdatedModels = 0,
                    DryRun = request.Sync.DryRun,
                    Actions = [],
                    Failures = [],
                    Warnings = []
                },
                Warnings = [],
                Failures = []
            });
        }
    }

    /// <summary>
    /// Returns an Atera-stage failure before mapping/import so IPC asset counters must remain zero.
    /// </summary>
    private sealed class PipelineFailureOrchestrator : CompletedOrchestrator
    {
        public override Task<SyncRunResult> RunOnceAsync(
            SyncRunRequest request,
            CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new SyncRunResult
            {
                Success = false,
                DryRun = request.Sync.DryRun,
                StartedAt = now,
                FinishedAt = now,
                PullResult = null,
                ImportBatch = null,
                ImportResult = null,
                Warnings = [],
                Failures =
                [
                    new SyncFailure
                    {
                        Stage = "AteraPull",
                        Code = "Atera.AuthenticationFailed",
                        Message = "Authentication failed."
                    }
                ]
            });
        }
    }

    /// <summary>
    /// Satisfies connection-runtime composition without contacting Atera.
    /// </summary>
    private sealed class SuccessfulAteraClient : IAteraClient
    {
        public Task<AteraPullResult> PullInventoryAsync(
            AteraPullRequest request,
            CancellationToken cancellationToken,
            IProgress<SyncProgressUpdate>? progress = null)
            => Task.FromResult(new AteraPullResult
            {
                Agents = [],
                Summary = new PullSummary { AgentCount = 0, PulledAt = DateTimeOffset.UtcNow },
                Warnings = []
            });
    }

    /// <summary>
    /// Returns a minimal successful Snipe-IT list response entirely in memory.
    /// </summary>
    private sealed class JsonHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"total\":0,\"rows\":[]}")
            });
    }

    /// <summary>
    /// Accepts latest-status writes and returns no prior status.
    /// </summary>
    private sealed class CapturingStatusStore : ISyncStatusStore
    {
        public Task SaveAsync(SyncRunResult result, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task<SyncStatusSnapshot?> ReadLatestAsync(CancellationToken cancellationToken)
            => Task.FromResult<SyncStatusSnapshot?>(null);
    }

    /// <summary>
    /// Simulates a local audit-store outage after a structured sync result has already been produced.
    /// </summary>
    private sealed class ThrowingStatusStore : ISyncStatusStore
    {
        public Task SaveAsync(SyncRunResult result, CancellationToken cancellationToken)
            => throw new IOException("Simulated status write failure.");

        public Task<SyncStatusSnapshot?> ReadLatestAsync(CancellationToken cancellationToken)
            => Task.FromResult<SyncStatusSnapshot?>(null);
    }
}
