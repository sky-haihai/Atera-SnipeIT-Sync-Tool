namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Carries count-only latest-real-run data read from local history when Worker status cannot provide it.
/// </summary>
internal sealed class LatestSyncHistorySummary
{
    public required DateTimeOffset? FinishedAtUtc { get; init; }
    public required int Created { get; init; }
    public required int Updated { get; init; }
    public required int NoChange { get; init; }
    public required int Deleted { get; init; }
}
