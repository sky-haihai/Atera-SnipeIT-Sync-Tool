using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.SnipeIt;

namespace AteraSnipeSync.Core.Sync;

/// <summary>
/// Reports whether one sync pipeline completed and preserves all stage outputs and record-level diagnostics.
/// </summary>
public sealed class SyncRunResult
{
    /// <summary>
    /// Gets whether pull, mapping, and import completed normally; record-level failures do not make this false.
    /// </summary>
    public required bool Success { get; init; }
    public required bool DryRun { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset FinishedAt { get; init; }
    public required AteraPullResult? PullResult { get; init; }
    public required SnipeImportBatch? ImportBatch { get; init; }
    public required SnipeImportResult? ImportResult { get; init; }
    public required IReadOnlyList<ModuleWarning> Warnings { get; init; }
    public required IReadOnlyList<SyncFailure> Failures { get; init; }
}
