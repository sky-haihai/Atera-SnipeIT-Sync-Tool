using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Configuration;
using AteraSnipeSync.Core.Sync;

namespace AteraSnipeSync.WorkerService;

/// <summary>
/// Holds one immutable set of validated clients, options, and orchestration dependencies created for a single Worker run.
/// </summary>
public sealed class WorkerSyncRuntime
{
    public required ISyncOrchestrator Orchestrator { get; init; }
    public required IAteraClient AteraConnectionClient { get; init; }
    public required HttpClient SnipeItHttpClient { get; init; }
    public required SyncRunRequest BaseRequest { get; init; }
    public required NotificationConfig NotificationConfig { get; init; }
}
