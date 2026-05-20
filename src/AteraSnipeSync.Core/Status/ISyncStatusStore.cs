using AteraSnipeSync.Core.Sync;

namespace AteraSnipeSync.Core.Status;

public interface ISyncStatusStore
{
    Task SaveAsync(
        SyncRunResult result,
        CancellationToken cancellationToken);

    Task<SyncStatusSnapshot?> ReadLatestAsync(
        CancellationToken cancellationToken);
}
