namespace AteraSnipeSync.Core.SnipeIt;

/// <summary>
/// Defines Snipe-IT connection settings and import behavior switches for one import run.
/// </summary>
public sealed class SnipeImportOptions
{
    public required string BaseUrl { get; init; }
    public required string ApiToken { get; init; }
    public required bool DryRun { get; init; }
    public required bool CreateMissingCompanies { get; init; }
    public required bool CreateMissingModels { get; init; }
    public string? MacAddressCustomFieldDbColumnName { get; init; }
    public string? MacAddressFieldsetName { get; init; }
    public string? ModelCategoryNormalizationTargetName { get; init; }
    public IReadOnlyList<string> ModelCategoriesToNormalize { get; init; } = [];
    public IReadOnlyList<string> IgnoredMacAddresses { get; init; } = [];
    public double NameMatchThreshold { get; init; } = 0.92;
    public bool ManualPreflightCsvEnabled { get; init; }
    public string? ManualPreflightCsvDirectory { get; init; }
    public int MaxReadRetryAttempts { get; init; } = 3;
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(250);
}
