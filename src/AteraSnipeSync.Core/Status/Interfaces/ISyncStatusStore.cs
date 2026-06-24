using AteraSnipeSync.Core.Sync;

namespace AteraSnipeSync.Core.Status;

/// <summary>
/// Stores sync run history files and reads the latest local sync status snapshot.
/// </summary>
public interface ISyncStatusStore
{
    Task SaveAsync(
        SyncRunResult result,
        CancellationToken cancellationToken);

    Task<SyncStatusSnapshot?> ReadLatestAsync(
        CancellationToken cancellationToken);
}
