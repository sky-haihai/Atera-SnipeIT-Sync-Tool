namespace AteraSnipeSync.Core.Status;

/// <summary>
/// Represents one persisted sync run history record for TrayApp parsing and diagnostics.
/// </summary>
internal sealed class SyncHistoryDocument
{
    public required int SchemaVersion { get; init; }
    public required SyncHistoryRunInfo Run { get; init; }
    public required SyncHistorySummary Summary { get; init; }
    public required SyncHistoryChangeSet Assets { get; init; }
    public required SyncHistoryChangeSet Companies { get; init; }
    public required SyncHistoryChangeSet Models { get; init; }
    public required SyncHistoryChangeSet Manufacturers { get; init; }
    public required SyncHistoryChangeSet Categories { get; init; }
    public required IReadOnlyList<SyncHistoryWarning> Warnings { get; init; }
    public required IReadOnlyList<SyncHistoryFailure> Failures { get; init; }
}
