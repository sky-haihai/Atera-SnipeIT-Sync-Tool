namespace AteraSnipeSync.Core.SnipeIt;

/// <summary>
/// Carries one normalized asset candidate from mapping into the Snipe-IT import boundary.
/// </summary>
public sealed class SnipeAssetImportRecord
{
    public required string AssetTag { get; init; }
    public required string Name { get; init; }
    public string? Serial { get; init; }
    /// <summary>
    /// Indicates whether Serial may be used as a strong identity and written to Snipe-IT.
    /// </summary>
    public bool SerialIsReliableIdentity { get; init; } = true;
    public IReadOnlyList<string> MacAddresses { get; init; } = [];
    public required string CompanyName { get; init; }
    /// <summary>
    /// Carries an optional canonical alias candidate; Snipe company planning uses it only when CompanyName has no direct target match.
    /// </summary>
    public string? CompanyAliasName { get; init; }
    public required string ManufacturerName { get; init; }
    /// <summary>
    /// Carries an optional canonical alias candidate; Snipe manufacturer planning uses it only when ManufacturerName has no direct target match.
    /// </summary>
    public string? ManufacturerAliasName { get; init; }
    public required string ModelName { get; init; }
    public required string CategoryName { get; init; }
    public string? DeviceType { get; init; }
    public required int StatusId { get; init; }
    public required string Notes { get; init; }
    public required string SourceSystem { get; init; }
    public required string SourceId { get; init; }
}
