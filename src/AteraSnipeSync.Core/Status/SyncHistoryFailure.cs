namespace AteraSnipeSync.Core.Status;

/// <summary>
/// Stores one run-level failure from a sync run in a structured history-friendly shape.
/// </summary>
internal sealed class SyncHistoryFailure
{
    public required string Stage { get; init; }
    public string? Code { get; init; }
    public required string Message { get; init; }
}
