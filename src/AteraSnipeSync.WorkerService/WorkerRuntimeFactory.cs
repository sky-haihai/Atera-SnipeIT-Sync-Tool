using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Configuration;
using AteraSnipeSync.Core.Mapping;
using AteraSnipeSync.Core.Notifications;
using AteraSnipeSync.Core.Scheduling;
using AteraSnipeSync.Core.SnipeIt;
using AteraSnipeSync.Core.Status;
using AteraSnipeSync.Core.Sync;

namespace AteraSnipeSync.WorkerService;

/// <summary>
/// Composes the unattended Core pipeline from validated local settings without sending network requests during construction.
/// </summary>
public sealed class WorkerRuntimeFactory(
    ILocalAppSettingsReader settingsStore,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory,
    TimeProvider timeProvider)
{
    private const string DefaultAteraBaseUrl = "https://app.atera.com/api/v3/";

    /// <summary>
    /// Creates a scheduler or fails closed when required settings/secrets are missing or unsafe.
    /// </summary>
    public async Task<ISyncScheduler> CreateSchedulerAsync(CancellationToken cancellationToken)
    {
        var schedule = await settingsStore.LoadSyncScheduleOptionsAsync(cancellationToken).ConfigureAwait(false)
            ?? DisabledSchedule();
        if (!schedule.Enabled)
        {
            return new DisabledSyncScheduler(loggerFactory.CreateLogger<DisabledSyncScheduler>());
        }

        var settings = await settingsStore.LoadWorkerSyncSettingsAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Local settings file is missing or incomplete: {LocalAppSettingsStore.GetDefaultFilePath()}");
        var notificationConfig = await settingsStore.LoadNotificationConfigAsync(cancellationToken).ConfigureAwait(false);
        var dryRun = await settingsStore.LoadSyncDryRunAsync(cancellationToken).ConfigureAwait(false);

        var ateraApiKey = Require(settings.AteraApiKey, SecretEnvironmentVariables.AteraApiKey);
        var snipeApiToken = Require(settings.SnipeItApiToken, SecretEnvironmentVariables.SnipeItApiToken);
        var ateraBaseUri = new Uri(string.IsNullOrWhiteSpace(settings.AteraBaseUrl) ? DefaultAteraBaseUrl : settings.AteraBaseUrl);
        var baseRequest = new SyncRunRequest
        {
            Atera = new AteraPullRequest { ApiKey = ateraApiKey },
            Mapping = new MappingOptions
            {
                DefaultCompanyName = Require(settings.DefaultCompanyName, nameof(settings.DefaultCompanyName)),
                DefaultManufacturerName = Require(settings.DefaultManufacturerName, nameof(settings.DefaultManufacturerName)),
                DefaultModelName = Require(settings.DefaultModelName, nameof(settings.DefaultModelName)),
                DefaultCategoryName = Require(settings.DefaultCategoryName, nameof(settings.DefaultCategoryName)),
                DefaultStatusId = settings.DefaultStatusId is > 0
                    ? settings.DefaultStatusId.Value
                    : throw new InvalidOperationException("DefaultStatusId must be greater than zero."),
                CompanyAliases = settings.CompanyAliases,
                ManufacturerAliases = settings.ManufacturerAliases,
                IgnoredDeviceTypes = settings.IgnoredDeviceTypes,
                IgnoredMacAddresses = settings.IgnoredMacAddresses
            },
            SnipeIt = new SnipeImportOptions
            {
                BaseUrl = Require(settings.SnipeItBaseUrl, nameof(settings.SnipeItBaseUrl)),
                ApiToken = snipeApiToken,
                DryRun = dryRun,
                CreateMissingCompanies = settings.CreateMissingCompanies ?? false,
                CreateMissingModels = settings.CreateMissingModels ?? false,
                MacAddressCustomFieldDbColumnName = settings.MacAddressCustomFieldDbColumnName,
                MacAddressFieldsetName = settings.MacAddressFieldsetName,
                ModelCategoryNormalizationTargetName = settings.DefaultCategoryName,
                ModelCategoriesToNormalize = settings.ModelCategoriesToNormalize,
                IgnoredMacAddresses = settings.IgnoredMacAddresses,
                NameMatchThreshold = settings.NameMatchThreshold ?? 0.92
            },
            Sync = new SyncRunOptions { DryRun = dryRun, TriggeredBy = "scheduled" }
        };

        var ateraClient = new AteraClient(
            httpClientFactory.CreateClient("Atera"),
            new AteraPullOptions
            {
                BaseUri = ateraBaseUri,
                MaxRetryAttempts = 3,
                RetryDelay = TimeSpan.FromSeconds(1)
            },
            new SystemAteraClock(),
            loggerFactory.CreateLogger<AteraClient>());
        var importer = new SnipeImporter(
            httpClientFactory.CreateClient("SnipeIt"),
            loggerFactory.CreateLogger<SnipeImporter>(),
            timeProvider);
        var orchestrator = new SyncOrchestrator(
            ateraClient,
            new InventoryMapper(),
            importer,
            loggerFactory.CreateLogger<SyncOrchestrator>(),
            timeProvider);
        var statusStore = new JsonFileSyncStatusStore(
            new SyncStatusStoreOptions(),
            loggerFactory.CreateLogger<JsonFileSyncStatusStore>());

        return new SyncScheduler(
            orchestrator,
            statusStore,
            new NullNotificationPublisher(loggerFactory.CreateLogger<NullNotificationPublisher>()),
            notificationConfig,
            new NotificationEventFilter(),
            new ScheduleCalculator(),
            schedule,
            baseRequest,
            timeProvider,
            loggerFactory.CreateLogger<SyncScheduler>());
    }

    /// <summary>
    /// Represents an intentionally idle service when the unattended schedule is disabled.
    /// </summary>
    private sealed class DisabledSyncScheduler(ILogger<DisabledSyncScheduler> logger) : ISyncScheduler
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogInformation("Sync scheduler is disabled; WorkerService is idle and did not load API credentials.");
            return Task.CompletedTask;
        }
    }

    private static string Require(string? value, string name)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Required setting or environment variable '{name}' is missing.")
            : value.Trim();
    }

    private static SyncScheduleOptions DisabledSchedule()
    {
        return new SyncScheduleOptions
        {
            Enabled = false,
            Frequency = ScheduleFrequency.Daily,
            TimeZoneId = "UTC",
            RunTimes = [new TimeOnly(0, 0)],
            PreventOverlappingRuns = true
        };
    }
}
