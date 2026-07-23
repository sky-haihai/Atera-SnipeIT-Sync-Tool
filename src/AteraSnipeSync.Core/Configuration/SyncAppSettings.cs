using AteraSnipeSync.Core.Scheduling;

namespace AteraSnipeSync.Core.Configuration;

/// <summary>
/// Carries the complete shared Tray/Worker configuration, including plaintext local credentials and the unattended schedule.
/// </summary>
public sealed class SyncAppSettings
{
    public string? AteraBaseUrl { get; init; }
    public string? AteraApiKey { get; init; }
    public string? SnipeItBaseUrl { get; init; }
    public string? SnipeItApiToken { get; init; }
    public string? DefaultCompanyName { get; init; } = SyncApplicationDefaults.CompanyName;
    public string? DefaultManufacturerName { get; init; } = SyncApplicationDefaults.ManufacturerName;
    public string? DefaultModelName { get; init; } = SyncApplicationDefaults.ModelName;
    public string? DefaultCategoryName { get; init; }
    public IReadOnlyList<string> ModelCategoriesToNormalize { get; init; } = [];
    public int? DefaultStatusId { get; init; }
    public IReadOnlyDictionary<string, string> CompanyAliases { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> ManufacturerAliases { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> IgnoredDeviceTypes { get; init; } = [];
    public string? MacAddressCustomFieldDbColumnName { get; init; }
    public string? MacAddressFieldsetName { get; init; }
    public IReadOnlyList<string> IgnoredMacAddresses { get; init; } = [];
    public double? NameMatchThreshold { get; init; }
    public bool? CreateMissingCompanies { get; init; }
    public bool? CreateMissingModels { get; init; }
    public SyncScheduleOptions? Schedule { get; init; }
    public NotificationConfig Notifications { get; init; } = new()
    {
        Enabled = false,
        OnEvents = []
    };
}
