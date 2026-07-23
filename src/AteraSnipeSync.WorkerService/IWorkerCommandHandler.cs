using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Runtime.Ipc;

namespace AteraSnipeSync.WorkerService;

/// <summary>
/// Defines the validated Worker command boundary used by the local IPC server and exposes explicit request cancellation.
/// </summary>
public interface IWorkerCommandHandler
{
    Task<WorkerCommandResult> ExecuteAsync(
        WorkerIpcRequest request,
        IProgress<SyncProgressUpdate>? progress,
        CancellationToken cancellationToken);

    bool TryCancel(string targetRequestId);
}
