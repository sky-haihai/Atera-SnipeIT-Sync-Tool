using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Configuration;
using AteraSnipeSync.Core.Mapping;
using AteraSnipeSync.Core.Scheduling;
using AteraSnipeSync.Core.SnipeIt;
using AteraSnipeSync.Core.Sync;

namespace AteraSnipeSync.WorkerService;

/// <summary>
/// Rebuilds a validated, immutable Core runtime from the shared JSON file before every Worker run without issuing network requests.
/// </summary>
public sealed class WorkerRuntimeFactory(
    ILocalAppSettingsReader settingsStore,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory,
    TimeProvider timeProvider) : IWorkerRuntimeFactory
{
    private const string DefaultAteraBaseUrl = "https://app.atera.com/api/v3/";

    /// <summary>
    /// Reloads complete settings and plaintext credentials, validates them, and composes clients and orchestration for one run.
    /// </summary>
    public async Task<WorkerSyncRuntime> CreateSyncRuntimeAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadWorkerSyncSettingsAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Local settings file is missing or incomplete: {LocalAppSettingsStore.GetDefaultFilePath()}");
        var notificationConfig = settings.Notifications;

        var ateraApiKey = Require(settings.AteraApiKey, nameof(settings.AteraApiKey));
        var snipeApiToken = Require(settings.SnipeItApiToken, nameof(settings.SnipeItApiToken));
        var ateraBaseUri = ApiEndpointValidator.ValidateAteraBaseUri(
            string.IsNullOrWhiteSpace(settings.AteraBaseUrl)
                ? DefaultAteraBaseUrl
                : settings.AteraBaseUrl);
        var snipeBaseUri = ApiEndpointValidator.ValidateSnipeBaseUri(
            Require(settings.SnipeItBaseUrl, nameof(settings.SnipeItBaseUrl)));

        var baseRequest = new SyncRunRequest
        {
            Atera = new AteraPullRequest { ApiKey = ateraApiKey },
            Mapping = new MappingOptions
            {
                DefaultCompanyName = SyncApplicationDefaults.CompanyName,
                DefaultManufacturerName = SyncApplicationDefaults.ManufacturerName,
                DefaultModelName = SyncApplicationDefaults.ModelName,
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
                BaseUrl = snipeBaseUri.AbsoluteUri.TrimEnd('/'),
                ApiToken = snipeApiToken,
                DryRun = false,
                CreateMissingCompanies = settings.CreateMissingCompanies ?? false,
                CreateMissingModels = settings.CreateMissingModels ?? false,
                MacAddressCustomFieldDbColumnName = settings.MacAddressCustomFieldDbColumnName,
                MacAddressFieldsetName = settings.MacAddressFieldsetName,
                ModelCategoryNormalizationTargetName = settings.DefaultCategoryName,
                ModelCategoriesToNormalize = settings.ModelCategoriesToNormalize,
                IgnoredMacAddresses = settings.IgnoredMacAddresses,
                NameMatchThreshold = settings.NameMatchThreshold ?? 0.92
            },
            Sync = new SyncRunOptions
            {
                DryRun = false,
                TriggeredBy = WorkerOperationNames.Scheduled
            }
        };

        var ateraClient = CreateAteraClient(ateraBaseUri, itemsPerPage: 500, maxPages: null);
        var ateraConnectionClient = CreateAteraClient(ateraBaseUri, itemsPerPage: 1, maxPages: 1);
        var snipeHttpClient = httpClientFactory.CreateClient("SnipeIt");
        var importer = new SnipeImporter(
            snipeHttpClient,
            loggerFactory.CreateLogger<SnipeImporter>(),
            timeProvider);
        var orchestrator = new SyncOrchestrator(
            ateraClient,
            new InventoryMapper(),
            importer,
            loggerFactory.CreateLogger<SyncOrchestrator>(),
            timeProvider);

        return new WorkerSyncRuntime
        {
            Orchestrator = orchestrator,
            AteraConnectionClient = ateraConnectionClient,
            SnipeItHttpClient = snipeHttpClient,
            BaseRequest = baseRequest,
            NotificationConfig = notificationConfig
        };
    }

    private AteraClient CreateAteraClient(Uri baseUri, int itemsPerPage, int? maxPages)
    {
        return new AteraClient(
            httpClientFactory.CreateClient("Atera"),
            new AteraPullOptions
            {
                BaseUri = baseUri,
                ItemsPerPage = itemsPerPage,
                MaxPages = maxPages,
                MaxRetryAttempts = maxPages.HasValue ? 0 : 3,
                RetryDelay = TimeSpan.FromSeconds(1)
            },
            new SystemAteraClock(),
            loggerFactory.CreateLogger<AteraClient>());
    }

    private static string Require(string? value, string name)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Required setting '{name}' is missing.")
            : value.Trim();
    }
}
