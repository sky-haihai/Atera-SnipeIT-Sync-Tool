using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.SnipeIt;

namespace AteraSnipeSync.Core.Sync;

/// <summary>
/// Reports the full outcome of one orchestrated sync run, including stage outputs and aggregated diagnostics.
/// </summary>
public sealed class SyncRunResult
{
    public required bool Success { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset FinishedAt { get; init; }
    public required AteraPullResult? PullResult { get; init; }
    public required SnipeImportBatch? ImportBatch { get; init; }
    public required SnipeImportResult? ImportResult { get; init; }
    public required IReadOnlyList<ModuleWarning> Warnings { get; init; }
    public required IReadOnlyList<SyncFailure> Failures { get; init; }
}
