namespace AteraSnipeSync.Core.Configuration;

/// <summary>
/// Carries reusable settings entered in the temporary manual sync window without storing run-specific state.
/// </summary>
public sealed class ManualSyncSettings
{
    public string? AteraBaseUrl { get; init; }
    public string? AteraApiKey { get; init; }
    public string? SnipeItBaseUrl { get; init; }
    public string? SnipeItApiToken { get; init; }
    public string? DefaultCompanyName { get; init; }
    public string? DefaultManufacturerName { get; init; }
    public string? DefaultModelName { get; init; }
    public string? DefaultCategoryName { get; init; }
    public int? DefaultStatusId { get; init; }
    public IReadOnlyDictionary<string, string> CompanyAliases { get; init; } = new Dictionary<string, string>();
    public string? MacAddressCustomFieldDbColumnName { get; init; }
    public double? NameMatchThreshold { get; init; }
    public bool? CreateMissingCompanies { get; init; }
    public bool? CreateMissingModels { get; init; }
}
