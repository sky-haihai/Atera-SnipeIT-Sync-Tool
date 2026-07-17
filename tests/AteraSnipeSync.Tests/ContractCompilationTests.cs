using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Mapping;
using AteraSnipeSync.Core.Notifications;
using AteraSnipeSync.Core.SnipeIt;
using AteraSnipeSync.Core.Status;
using AteraSnipeSync.Core.Sync;

namespace AteraSnipeSync.Tests;

public sealed class ContractCompilationTests
{
    [Fact]
    public void SharedContractsCanBeConstructed()
    {
        var pullResult = new AteraPullResult
        {
            Agents = [],
            Summary = new PullSummary
            {
                AgentCount = 0,
                PulledAt = DateTimeOffset.UtcNow
            },
            Warnings = []
        };

        var importBatch = new SnipeImportBatch
        {
            Assets = [],
            Summary = new MappingSummary
            {
                SourceAgentCount = 0,
                MappedAssetCount = 0
            },
            Warnings = []
        };

        var runResult = new SyncRunResult
        {
            Success = true,
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
            PullResult = pullResult,
            ImportBatch = importBatch,
            ImportResult = null,
            Warnings = [],
            Failures = []
        };

        var notification = new NotificationRequest
        {
            EventType = "SyncCompleted",
            Severity = "Info",
            Subject = "Sync completed",
            Message = "The sync contract can represent a successful run.",
            SyncResult = runResult
        };

        Assert.True(runResult.Success);
        Assert.Equal("SyncCompleted", notification.EventType);
    }

    [Fact]
    public void CoreInterfacesCanBeImplementedByModules()
    {
        IAteraClient ateraClient = new FakeAteraClient();
        IInventoryMapper mapper = new FakeInventoryMapper();
        ISnipeImporter importer = new FakeSnipeImporter();
        ISyncOrchestrator orchestrator = new FakeSyncOrchestrator();
        ISyncStatusStore statusStore = new FakeSyncStatusStore();
        INotificationPublisher notificationPublisher = new FakeNotificationPublisher();

        Assert.NotNull(ateraClient);
        Assert.NotNull(mapper);
        Assert.NotNull(importer);
        Assert.NotNull(orchestrator);
        Assert.NotNull(statusStore);
        Assert.NotNull(notificationPublisher);
    }

    private sealed class FakeAteraClient : IAteraClient
    {
        public Task<AteraPullResult> PullInventoryAsync(
            AteraPullRequest request,
            CancellationToken cancellationToken,
            IProgress<SyncProgressUpdate>? progress = null)
        {
            return Task.FromResult(new AteraPullResult
            {
                Agents = [],
                Summary = new PullSummary
                {
                    AgentCount = 0,
                    PulledAt = DateTimeOffset.UtcNow
                },
                Warnings = []
            });
        }
    }

    private sealed class FakeInventoryMapper : IInventoryMapper
    {
        public SnipeImportBatch Map(
            AteraPullResult source,
            MappingOptions options)
        {
            return new SnipeImportBatch
            {
                Assets = [],
                Summary = new MappingSummary
                {
                    SourceAgentCount = source.Agents.Count,
                    MappedAssetCount = 0
                },
                Warnings = []
            };
        }
    }

    private sealed class FakeSnipeImporter : ISnipeImporter
    {
        public Task<SnipeImportResult> ImportAsync(
            SnipeImportBatch batch,
            SnipeImportOptions options,
            CancellationToken cancellationToken,
            IProgress<SyncProgressUpdate>? progress = null)
        {
            return Task.FromResult(new SnipeImportResult
            {
                CreatedAssets = 0,
                UpdatedAssets = 0,
                SkippedAssets = 0,
                FailedAssets = 0,
                CreatedCompanies = 0,
                CreatedCategories = 0,
                CreatedModels = 0,
                UpdatedModels = 0,
                DryRun = options.DryRun,
                Actions = [],
                Failures = [],
                Warnings = []
            });
        }
    }

    private sealed class FakeSyncOrchestrator : ISyncOrchestrator
    {
        public Task<SyncRunResult> RunOnceAsync(
            SyncRunRequest request,
            CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;

            return Task.FromResult(new SyncRunResult
            {
                Success = true,
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

    private sealed class FakeSyncStatusStore : ISyncStatusStore
    {
        public Task SaveAsync(
            SyncRunResult result,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<SyncStatusSnapshot?> ReadLatestAsync(
            CancellationToken cancellationToken)
        {
            return Task.FromResult<SyncStatusSnapshot?>(null);
        }
    }

    private sealed class FakeNotificationPublisher : INotificationPublisher
    {
        public Task PublishAsync(
            NotificationRequest request,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
