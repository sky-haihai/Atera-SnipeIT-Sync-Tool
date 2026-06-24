namespace AteraSnipeSync.Core.Status;

/// <summary>
/// Stores one non-fatal warning from a sync run in a structured history-friendly shape.
/// </summary>
internal sealed class SyncHistoryWarning
{
    public required string Source { get; init; }
    public string? Code { get; init; }
    public required string Message { get; init; }
}
