namespace AteraSnipeSync.Core.Scheduling;

/// <summary>
/// Defines atomic persistence for the Worker's UTC schedule state independently from editable local configuration.
/// </summary>
public interface IScheduleRuntimeStateStore
{
    Task<ScheduleRuntimeState?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(ScheduleRuntimeState state, CancellationToken cancellationToken);
}
