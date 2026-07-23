namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Carries the count-only latest-run data read from local history while Worker IPC is offline.
/// </summary>
internal sealed class LatestSyncHistorySummary
{
    public required DateTimeOffset? FinishedAtUtc { get; init; }
    public required int Created { get; init; }
    public required int Updated { get; init; }
    public required int NoChange { get; init; }
    public required int Deleted { get; init; }
}
