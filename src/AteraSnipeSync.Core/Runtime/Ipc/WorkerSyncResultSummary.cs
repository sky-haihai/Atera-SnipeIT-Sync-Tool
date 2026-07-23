using AteraSnipeSync.Core.Common;

namespace AteraSnipeSync.Core.Runtime.Ipc;

/// <summary>
/// Carries the operator-facing outcome of one sync without serializing raw Atera records or API response content.
/// </summary>
public sealed class WorkerSyncResultSummary
{
    public required bool Success { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset FinishedAt { get; init; }
    public required bool DryRun { get; init; }
    public required bool Cancelled { get; init; }
    public required int Pulled { get; init; }
    public required int Mapped { get; init; }
    public required int Created { get; init; }
    public required int Updated { get; init; }
    public required int Deleted { get; init; }
    public required int Skipped { get; init; }
    public required int Failed { get; init; }
    public required int WarningCount { get; init; }
    public required IReadOnlyList<SyncFailure> Failures { get; init; }
}
