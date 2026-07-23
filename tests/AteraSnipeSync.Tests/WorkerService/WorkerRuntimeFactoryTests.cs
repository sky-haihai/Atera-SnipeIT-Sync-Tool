using AteraSnipeSync.Core.Configuration;
using AteraSnipeSync.Core.Scheduling;
using AteraSnipeSync.WorkerService;
using Microsoft.Extensions.Logging.Abstractions;

namespace AteraSnipeSync.Tests.WorkerService;

/// <summary>
/// Verifies that each Worker operation receives a fresh immutable runtime built from the latest complete settings snapshot.
/// </summary>
public sealed class WorkerRuntimeFactoryTests
{
    [Fact]
    public async Task CreateSyncRuntimeAsync_ReloadsSettingsForEveryRun()
    {
        var settings = new MutableSettingsReader(CreateSettings("atera-key-1", "snipe-token-1"));
        var factory = new WorkerRuntimeFactory(
            settings,
            new NoNetworkHttpClientFactory(),
            NullLoggerFactory.Instance,
            TimeProvider.System);

        var first = await factory.CreateSyncRuntimeAsync(CancellationToken.None);
        settings.Current = CreateSettings("atera-key-2", "snipe-token-2");
        var second = await factory.CreateSyncRuntimeAsync(CancellationToken.None);

        Assert.Equal(2, settings.WorkerSettingsLoadCount);
        Assert.Equal(0, settings.NotificationLoadCount);
        Assert.Equal("atera-key-1", first.BaseRequest.Atera.ApiKey);
        Assert.Equal("snipe-token-1", first.BaseRequest.SnipeIt.ApiToken);
        Assert.Equal("atera-key-2", second.BaseRequest.Atera.ApiKey);
        Assert.Equal("snipe-token-2", second.BaseRequest.SnipeIt.ApiToken);
        Assert.Equal(SyncApplicationDefaults.CompanyName, first.BaseRequest.Mapping.DefaultCompanyName);
        Assert.Equal(SyncApplicationDefaults.ManufacturerName, first.BaseRequest.Mapping.DefaultManufacturerName);
        Assert.Equal(SyncApplicationDefaults.ModelName, first.BaseRequest.Mapping.DefaultModelName);
        Assert.False(first.BaseRequest.Sync.DryRun);
        Assert.False(first.BaseRequest.SnipeIt.DryRun);
        Assert.True(first.NotificationConfig.Enabled);
        Assert.NotSame(first.Orchestrator, second.Orchestrator);
    }

    private static SyncAppSettings CreateSettings(string ateraApiKey, string snipeApiToken)
    {
        return new SyncAppSettings
        {
            AteraBaseUrl = "https://app.atera.com/api/v3/",
            AteraApiKey = ateraApiKey,
            SnipeItBaseUrl = "https://snipe.example.test/api/v1/",
            SnipeItApiToken = snipeApiToken,
            DefaultCompanyName = "Ignored Custom Company",
            DefaultManufacturerName = "Ignored Custom Manufacturer",
            DefaultModelName = "Ignored Custom Model",
            DefaultCategoryName = "Computer",
            ModelCategoriesToNormalize = ["Computer"],
            DefaultStatusId = 2,
            NameMatchThreshold = 0.92,
            CreateMissingCompanies = false,
            CreateMissingModels = false,
            Notifications = new NotificationConfig
            {
                Enabled = true,
                OnEvents = ["SyncCompleted"]
            }
        };
    }

    /// <summary>
    /// Supplies mutable snapshots while counting Worker-level full-settings reads.
    /// </summary>
    private sealed class MutableSettingsReader(SyncAppSettings current) : ILocalAppSettingsReader
    {
        public SyncAppSettings Current { get; set; } = current;
        public int WorkerSettingsLoadCount { get; private set; }
        public int NotificationLoadCount { get; private set; }

        public Task<SyncAppSettings?> LoadWorkerSyncSettingsAsync(CancellationToken cancellationToken)
        {
            WorkerSettingsLoadCount++;
            return Task.FromResult<SyncAppSettings?>(Current);
        }

        public Task<SyncScheduleOptions?> LoadSyncScheduleOptionsAsync(CancellationToken cancellationToken)
            => Task.FromResult<SyncScheduleOptions?>(null);

        public Task<NotificationConfig> LoadNotificationConfigAsync(CancellationToken cancellationToken)
        {
            NotificationLoadCount++;
            return Task.FromResult(new NotificationConfig { Enabled = false, OnEvents = [] });
        }
    }

    /// <summary>
    /// Creates isolated clients whose handlers fail if runtime construction unexpectedly performs network I/O.
    /// </summary>
    private sealed class NoNetworkHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NoNetworkHandler());
    }

    /// <summary>
    /// Guards the runtime-factory test against accidental external HTTP requests.
    /// </summary>
    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Runtime construction must not perform network I/O.");
    }
}
