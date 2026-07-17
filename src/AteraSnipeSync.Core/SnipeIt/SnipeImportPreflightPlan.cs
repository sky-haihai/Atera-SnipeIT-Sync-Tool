namespace AteraSnipeSync.Core.SnipeIt;

/// <summary>
/// Carries the manual sync write plan that is exported before any Snipe-IT mutation is allowed.
/// </summary>
internal sealed class SnipeImportPreflightPlan
{
    public required IReadOnlyList<SnipeAssetPreflightRow> Assets { get; init; }
    public required IReadOnlyList<SnipeCompanyPreflightRow> Companies { get; init; }
    public required IReadOnlyList<SnipeCategoryPreflightRow> Categories { get; init; }
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
    string MacAddresses,
    string CompanyName,
    string ModelName,
    string CategoryName,
    string ManufacturerName,
    int? ExistingAssetId,
    string? ExistingAssetTag,
    string? ConflictingFields,
    string? ConflictingValue,
    string? ConflictingAssets,
    string? FailureCode,
    string? FailureMessage,
    string? DeviceType);

/// <summary>
/// Describes one planned company create row for the company preflight CSV.
/// </summary>
internal sealed record SnipeCompanyPreflightRow(
    string Operation,
    string Name);

/// <summary>
/// Describes one planned asset-category create row for the category preflight CSV.
/// </summary>
internal sealed record SnipeCategoryPreflightRow(
    string Operation,
    string Name,
    string CategoryType);

/// <summary>
/// Describes one planned model create or update row for the model preflight CSV.
/// </summary>
internal sealed record SnipeModelPreflightRow(
    string Operation,
    string Name,
    int? ExistingModelId,
    string? CurrentCategoryName,
    int? CurrentCategoryId,
    string TargetCategoryName,
    int? TargetCategoryId,
    string ManufacturerName,
    int? ManufacturerId,
    string? CurrentFieldsetName,
    int? CurrentFieldsetId,
    string? TargetFieldsetName,
    int? TargetFieldsetId,
    string ChangeReasons);
