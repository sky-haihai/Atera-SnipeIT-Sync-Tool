using AteraSnipeSync.Core.SnipeIt;
using AteraSnipeSync.Core.Sync;

namespace AteraSnipeSync.Core.Scheduling;

/// <summary>
/// Produces scheduler-safe real-sync requests by marking the run as scheduled and disabling manual preflight CSV output.
/// </summary>
public static class ScheduledSyncRequestFactory
{
    /// <summary>
    /// Clones a base request for unattended real execution, disables dry-run, and guarantees no manual preflight CSV is generated.
    /// </summary>
    public static SyncRunRequest CreateScheduledRequest(SyncRunRequest baseRequest)
    {
        ArgumentNullException.ThrowIfNull(baseRequest);

        return new SyncRunRequest
        {
            Atera = baseRequest.Atera,
            Mapping = baseRequest.Mapping,
            SnipeIt = new SnipeImportOptions
            {
                BaseUrl = baseRequest.SnipeIt.BaseUrl,
                ApiToken = baseRequest.SnipeIt.ApiToken,
                DryRun = false,
                CreateMissingCompanies = baseRequest.SnipeIt.CreateMissingCompanies,
                CreateMissingModels = baseRequest.SnipeIt.CreateMissingModels,
                MacAddressCustomFieldDbColumnName = baseRequest.SnipeIt.MacAddressCustomFieldDbColumnName,
                MacAddressFieldsetName = baseRequest.SnipeIt.MacAddressFieldsetName,
                ModelCategoryNormalizationTargetName = baseRequest.SnipeIt.ModelCategoryNormalizationTargetName,
                ModelCategoriesToNormalize = baseRequest.SnipeIt.ModelCategoriesToNormalize,
                IgnoredMacAddresses = baseRequest.SnipeIt.IgnoredMacAddresses,
                NameMatchThreshold = baseRequest.SnipeIt.NameMatchThreshold,
                ManualPreflightCsvEnabled = false,
                ManualPreflightCsvDirectory = null,
                MaxReadRetryAttempts = baseRequest.SnipeIt.MaxReadRetryAttempts,
                RetryBaseDelay = baseRequest.SnipeIt.RetryBaseDelay
            },
            Sync = new SyncRunOptions
            {
                DryRun = false,
                TriggeredBy = "scheduled"
            }
        };
    }
}
