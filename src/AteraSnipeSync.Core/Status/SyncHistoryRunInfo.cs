namespace AteraSnipeSync.Core.Status;

/// <summary>
/// Captures run-level timing and result metadata for one persisted sync history file.
/// </summary>
internal sealed class SyncHistoryRunInfo
{
    public required string RunId { get; init; }
    public required string Result { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset FinishedAtUtc { get; init; }
    public required long DurationMs { get; init; }
    public required bool DryRun { get; init; }
    public bool Cancelled { get; init; }
}
