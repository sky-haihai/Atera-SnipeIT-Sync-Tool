namespace AteraSnipeSync.Core.SnipeIt;

/// <summary>
/// Describes a Snipe-IT hardware asset candidate found during identity comparison.
/// </summary>
internal sealed record SnipeAssetMatch(
    int Id,
    string Name,
    string? AssetTag,
    string? Serial,
    string? CompanyName,
    string? CategoryName,
    string? ModelName,
    string? Notes,
    IReadOnlyDictionary<string, string> CustomFields);
