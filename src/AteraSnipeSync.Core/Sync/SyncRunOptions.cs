namespace AteraSnipeSync.Core.Sync;

/// <summary>
/// Carries run-wide switches used by orchestration, including dry-run intent and trigger source.
/// </summary>
public sealed class SyncRunOptions
{
    public required bool DryRun { get; init; }
    public required string TriggeredBy { get; init; }
}
