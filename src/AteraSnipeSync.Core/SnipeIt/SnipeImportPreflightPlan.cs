namespace AteraSnipeSync.Core.SnipeIt;

/// <summary>
/// Carries the manual sync write plan that is exported before any Snipe-IT mutation is allowed.
/// </summary>
internal sealed class SnipeImportPreflightPlan
{
    public required IReadOnlyList<SnipeAssetPreflightRow> Assets { get; init; }
    public required IReadOnlyList<SnipeCompanyPreflightRow> Companies { get; init; }
    public required IReadOnlyList<SnipeModelPreflightRow> Models { get; init; }
}

/// <summary>
/// Describes one planned hardware create/update row for the asset preflight CSV.
/// </summary>
internal sealed record SnipeAssetPreflightRow(
    string Operation,
    string AssetTag,
    string Name,
    string? Serial,
    string CompanyName,
    string ModelName,
    string CategoryName,
    string ManufacturerName,
    int? ExistingAssetId,
    string? ExistingAssetTag);

/// <summary>
/// Describes one planned company create row for the company preflight CSV.
/// </summary>
internal sealed record SnipeCompanyPreflightRow(
    string Operation,
    string Name);

/// <summary>
/// Describes one planned model create row for the model preflight CSV.
/// </summary>
internal sealed record SnipeModelPreflightRow(
    string Operation,
    string Name,
    string CategoryName,
    int CategoryId,
    string ManufacturerName,
    int? ManufacturerId);
