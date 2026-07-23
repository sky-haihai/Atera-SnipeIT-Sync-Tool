namespace AteraSnipeSync.Core.SnipeIt;

/// <summary>
/// Describes a Snipe-IT hardware asset candidate found during identity comparison.
/// </summary>
internal sealed record SnipeAssetMatch(
    int Id,
    string Name,
    string? AssetTag,
    string? Serial,
    int? CompanyId,
    string? CompanyName,
    int? ModelId,
    string? CategoryName,
    string? ModelName,
    int? StatusId,
    string? Notes,
    IReadOnlyDictionary<string, string> CustomFields);
