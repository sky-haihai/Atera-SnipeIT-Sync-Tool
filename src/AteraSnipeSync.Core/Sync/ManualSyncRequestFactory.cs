using AteraSnipeSync.Core.SnipeIt;

namespace AteraSnipeSync.Core.Sync;

/// <summary>
/// Builds manual sync request shapes for TrayApp actions while keeping direct sync and CSV preview behavior separate.
/// </summary>
public static class ManualSyncRequestFactory
{
    /// <summary>
    /// Creates a direct manual sync request that performs real writes and does not generate preflight CSV files.
    /// </summary>
    public static SyncRunRequest CreateSyncNowRequest(SyncRunRequest baseRequest)
    {
        ArgumentNullException.ThrowIfNull(baseRequest);

        return new SyncRunRequest
        {
            Atera = baseRequest.Atera,
            Mapping = baseRequest.Mapping,
            SnipeIt = CopySnipeOptions(
                baseRequest.SnipeIt,
                dryRun: false,
                manualPreflightCsvEnabled: false,
                manualPreflightCsvDirectory: null),
            Sync = new SyncRunOptions
            {
                DryRun = false,
                TriggeredBy = "manual"
            }
        };
    }

    /// <summary>
    /// Creates a manual preview request that writes preflight CSV files and never mutates Snipe-IT.
    /// </summary>
    public static SyncRunRequest CreatePreviewChangesRequest(
        SyncRunRequest baseRequest,
        string preflightDirectory)
    {
        ArgumentNullException.ThrowIfNull(baseRequest);

        var trimmedPreflightDirectory = preflightDirectory?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedPreflightDirectory))
        {
            throw new ArgumentException("Manual preview preflight directory is required.", nameof(preflightDirectory));
        }

        return new SyncRunRequest
        {
            Atera = baseRequest.Atera,
            Mapping = baseRequest.Mapping,
            SnipeIt = CopySnipeOptions(
                baseRequest.SnipeIt,
                dryRun: true,
                manualPreflightCsvEnabled: true,
                manualPreflightCsvDirectory: trimmedPreflightDirectory),
            Sync = new SyncRunOptions
            {
                DryRun = true,
                TriggeredBy = "manual-preview"
            }
        };
    }

    private static SnipeImportOptions CopySnipeOptions(
        SnipeImportOptions source,
        bool dryRun,
        bool manualPreflightCsvEnabled,
        string? manualPreflightCsvDirectory)
    {
        return new SnipeImportOptions
        {
            BaseUrl = source.BaseUrl,
            ApiToken = source.ApiToken,
            DryRun = dryRun,
            CreateMissingCompanies = source.CreateMissingCompanies,
            CreateMissingModels = source.CreateMissingModels,
            MacAddressCustomFieldDbColumnName = source.MacAddressCustomFieldDbColumnName,
            MacAddressFieldsetName = source.MacAddressFieldsetName,
            ModelCategoryNormalizationTargetName = source.ModelCategoryNormalizationTargetName,
            ModelCategoriesToNormalize = source.ModelCategoriesToNormalize,
            IgnoredMacAddresses = source.IgnoredMacAddresses,
            NameMatchThreshold = source.NameMatchThreshold,
            ManualPreflightCsvEnabled = manualPreflightCsvEnabled,
            ManualPreflightCsvDirectory = manualPreflightCsvDirectory,
            MaxReadRetryAttempts = source.MaxReadRetryAttempts,
            RetryBaseDelay = source.RetryBaseDelay
        };
    }
}
