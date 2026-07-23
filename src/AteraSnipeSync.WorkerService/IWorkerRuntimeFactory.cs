namespace AteraSnipeSync.WorkerService;

/// <summary>
/// Defines the per-run runtime composition boundary so scheduling and command tests can use deterministic fakes.
/// </summary>
public interface IWorkerRuntimeFactory
{
    Task<WorkerSyncRuntime> CreateSyncRuntimeAsync(CancellationToken cancellationToken);
}
