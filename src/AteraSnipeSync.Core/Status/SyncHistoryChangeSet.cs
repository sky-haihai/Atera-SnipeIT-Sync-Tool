namespace AteraSnipeSync.Core.Status;

/// <summary>
/// Groups structured per-resource changes by action outcome for one sync history record.
/// </summary>
internal sealed class SyncHistoryChangeSet
{
    public required IReadOnlyList<SyncHistoryItem> Created { get; init; }
    public required IReadOnlyList<SyncHistoryItem> Updated { get; init; }
    public required IReadOnlyList<SyncHistoryItem> Deleted { get; init; }
    public required IReadOnlyList<SyncHistoryItem> Skipped { get; init; }
    public required IReadOnlyList<SyncHistoryItem> Failed { get; init; }
}
