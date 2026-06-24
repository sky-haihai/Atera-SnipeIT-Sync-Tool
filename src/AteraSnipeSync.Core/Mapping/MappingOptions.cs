namespace AteraSnipeSync.Core.Mapping;

/// <summary>
/// Defines fallback values and local normalization rules used when converting Atera agents into Snipe-IT import records.
/// </summary>
public sealed class MappingOptions
{
    public required string DefaultCompanyName { get; init; }
    public required string DefaultManufacturerName { get; init; }
    public required string DefaultModelName { get; init; }
    public required string DefaultCategoryName { get; init; }
    public required int DefaultStatusId { get; init; }
    public IReadOnlyDictionary<string, string> CompanyAliases { get; init; } = new Dictionary<string, string>();
}
