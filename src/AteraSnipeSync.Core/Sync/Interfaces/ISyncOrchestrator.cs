namespace AteraSnipeSync.Core.Sync;

/// <summary>
/// Defines the single entry point for running the full Atera-to-Snipe-IT sync pipeline.
/// </summary>
public interface ISyncOrchestrator
{
    /// <summary>
    /// Executes one sync run and returns structured stage output, warnings, and failures.
    /// </summary>
    Task<SyncRunResult> RunOnceAsync(
        SyncRunRequest request,
        CancellationToken cancellationToken);
}
