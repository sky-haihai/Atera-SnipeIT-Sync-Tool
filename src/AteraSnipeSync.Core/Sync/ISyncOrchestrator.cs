namespace AteraSnipeSync.Core.Sync;

public interface ISyncOrchestrator
{
    Task<SyncRunResult> RunOnceAsync(
        SyncRunRequest request,
        CancellationToken cancellationToken);
}
