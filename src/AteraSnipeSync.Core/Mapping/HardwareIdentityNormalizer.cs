namespace AteraSnipeSync.Core.Mapping;

/// <summary>
/// Normalizes hardware identity values and removes firmware placeholders that are unsafe for matching or asset tags.
/// </summary>
internal static class HardwareIdentityNormalizer
{
    private static readonly HashSet<string> PlaceholderSerials = new(StringComparer.OrdinalIgnoreCase)
    {
        "To Be Filled By O.E.M.",
        "To Be Filled By OEM",
        "Default string",
        "System Serial Number",
        "Unknown",
        "N/A",
        "NA",
        "None",
        "Not Applicable",
        "Not Specified",
        "123456789"
    };

    /// <summary>
    /// Returns a trimmed usable serial, or null when the value is blank or a known non-unique placeholder.
    /// </summary>
    public static string? NormalizeSerial(string? value)
    {
        var normalized = InventoryMapper.Normalize(value);
        return normalized is null || PlaceholderSerials.Contains(normalized) ? null : normalized;
    }

    public static bool IsPlaceholderSerial(string? value)
    {
        var normalized = InventoryMapper.Normalize(value);
        return normalized is not null && PlaceholderSerials.Contains(normalized);
    }
}
