using AteraSnipeSync.Core.Scheduling;

namespace AteraSnipeSync.Tests.WorkerService;

/// <summary>
/// Provides deterministic schedule-state persistence for Worker tests without touching machine-wide ProgramData.
/// </summary>
internal sealed class InMemoryScheduleRuntimeStateStore : IScheduleRuntimeStateStore
{
    public ScheduleRuntimeState? State { get; set; }
    public int SaveCount { get; private set; }

    public Task<ScheduleRuntimeState?> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(State);
    }

    public Task SaveAsync(ScheduleRuntimeState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State = state;
        SaveCount++;
        return Task.CompletedTask;
    }
}
