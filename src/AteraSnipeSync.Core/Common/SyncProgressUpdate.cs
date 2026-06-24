namespace AteraSnipeSync.Core.Common;

/// <summary>
/// Describes safe, non-secret progress for a sync stage so UI callers can show what work is currently happening.
/// </summary>
public sealed class SyncProgressUpdate
{
    public required string Stage { get; init; }
    public required string Message { get; init; }
    public int? Current { get; init; }
    public int? Total { get; init; }
    public int? Percent { get; init; }
}
