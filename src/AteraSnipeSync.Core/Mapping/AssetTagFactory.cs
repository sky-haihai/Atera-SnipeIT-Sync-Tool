namespace AteraSnipeSync.Core.Mapping;

/// <summary>
/// Produces a deterministic Snipe-IT asset tag that identifies the record as Atera-managed.
/// </summary>
internal static class AssetTagFactory
{
    /// <summary>
    /// Prefixes the validated source identity used by the import record.
    /// </summary>
    public static string Create(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        return $"ATERA-{sourceId.Trim()}";
    }
}
