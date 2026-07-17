using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Mapping;
using AteraSnipeSync.Core.Notifications;
using AteraSnipeSync.Core.Scheduling;
using AteraSnipeSync.Core.SnipeIt;
using AteraSnipeSync.Core.Status;
using AteraSnipeSync.Core.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace AteraSnipeSync.Tests.Scheduling;

/// <summary>
/// Verifies scheduled run execution boundaries without calling real Atera or Snipe-IT APIs.
/// </summary>
public sealed class SyncSchedulerTests
{
    [Fact]
    public async Task RunScheduledSyncAsync_SkipsSecondRun_WhenOverlapPreventionEnabled()
    {
        var orchestrator = new BlockingOrchestrator();
        var scheduler = CreateScheduler(orchestrator);
        var firstRun = scheduler.RunScheduledSyncAsync(CancellationToken.None);
        await orchestrator.WaitForRunStartAsync();

        var secondRunResult = await scheduler.RunScheduledSyncAsync(CancellationToken.None);
        orchestrator.Release();
        var firstRunResult = await firstRun;

        Assert.True(firstRunResult);
        Assert.False(secondRunResult);
        Assert.Equal(1, orchestrator.RunCount);
    }

    [Fact]
    public async Task RunScheduledSyncAsync_UsesScheduledRequestWithoutPreflightCsv()
    {
        var orchestrator = new CompletedOrchestrator();
        var scheduler = CreateScheduler(orchestrator);

        var result = await scheduler.RunScheduledSyncAsync(CancellationToken.None);

        Assert.True(result);
        Assert.NotNull(orchestrator.LastRequest);
        Assert.Equal("scheduled", orchestrator.LastRequest.Sync.TriggeredBy);
        Assert.False(orchestrator.LastRequest.SnipeIt.ManualPreflightCsvEnabled);
    }

    private static SyncScheduler CreateScheduler(ISyncOrchestrator orchestrator)
    {
        return new SyncScheduler(
            orchestrator,
            new CapturingStatusStore(),
            new CapturingNotificationPublisher(),
            new ScheduleCalculator(),
            new SyncScheduleOptions
            {
                Enabled = true,
                Frequency = ScheduleFrequency.Daily,
                TimeZoneId = "UTC",
                RunTimes = [new TimeOnly(1, 0)],
                PreventOverlappingRuns = true
            },
            CreateBaseRequest(),
            TimeProvider.System,
            NullLogger<SyncScheduler>.Instance);
    }

    private static SyncRunRequest CreateBaseRequest()
    {
        return new SyncRunRequest
        {
            Atera = new AteraPullRequest { ApiKey = "atera-key" },
            Mapping = new MappingOptions
            {
                DefaultCompanyName = "Acme",
                DefaultManufacturerName = "Dell",
                DefaultModelName = "Latitude",
                DefaultCategoryName = "Computer",
                DefaultStatusId = 2
            },
            SnipeIt = new SnipeImportOptions
            {
                BaseUrl = "https://snipe.example.com/api/v1",
                ApiToken = "snipe-token",
                DryRun = false,
                CreateMissingCompanies = true,
                CreateMissingModels = true,
                ManualPreflightCsvEnabled = true,
                ManualPreflightCsvDirectory = @"C:\ProgramData\AteraSnipeSync\Preflight\run-1"
            },
            Sync = new SyncRunOptions
            {
                DryRun = false,
                TriggeredBy = "manual"
            }
        };
    }

    private static SyncRunResult CreateResult()
    {
        var now = DateTimeOffset.UtcNow;
        return new SyncRunResult
        {
            Success = true,
            StartedAt = now,
            FinishedAt = now,
            PullResult = null,
            ImportBatch = null,
            ImportResult = null,
            Warnings = Array.Empty<ModuleWarning>(),
            Failures = Array.Empty<SyncFailure>()
        };
    }

    private sealed class CompletedOrchestrator : ISyncOrchestrator
    {
        public SyncRunRequest? LastRequest { get; private set; }

        public Task<SyncRunResult> RunOnceAsync(SyncRunRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(CreateResult());
        }
    }

    private sealed class BlockingOrchestrator : ISyncOrchestrator
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RunCount { get; private set; }

        public async Task<SyncRunResult> RunOnceAsync(SyncRunRequest request, CancellationToken cancellationToken)
        {
            RunCount++;
            _started.SetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return CreateResult();
        }

        public Task WaitForRunStartAsync()
        {
            return _started.Task;
        }

        public void Release()
        {
            _release.SetResult();
        }
    }

    private sealed class CapturingStatusStore : ISyncStatusStore
    {
        public Task SaveAsync(SyncRunResult result, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<SyncStatusSnapshot?> ReadLatestAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<SyncStatusSnapshot?>(null);
        }
    }

    private sealed class CapturingNotificationPublisher : INotificationPublisher
    {
        public Task PublishAsync(NotificationRequest request, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
