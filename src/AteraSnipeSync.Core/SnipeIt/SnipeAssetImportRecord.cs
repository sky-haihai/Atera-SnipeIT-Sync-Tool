namespace AteraSnipeSync.Core.SnipeIt;

/// <summary>
/// Carries one normalized asset candidate from mapping into the Snipe-IT import boundary.
/// </summary>
public sealed class SnipeAssetImportRecord
{
    public required string AssetTag { get; init; }
    public required string Name { get; init; }
    public string? Serial { get; init; }
    public IReadOnlyList<string> MacAddresses { get; init; } = [];
    public required string CompanyName { get; init; }
    public required string ManufacturerName { get; init; }
    public required string ModelName { get; init; }
    public required string CategoryName { get; init; }
    public required int StatusId { get; init; }
    public required string Notes { get; init; }
    public required string SourceSystem { get; init; }
    public required string SourceId { get; init; }
}
