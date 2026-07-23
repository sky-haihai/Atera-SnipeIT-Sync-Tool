namespace AteraSnipeSync.Core.Scheduling;

/// <summary>
/// Reports whether a disk-backed schedule reload was applied and returns the resulting scheduler state.
/// </summary>
public sealed class ScheduleReloadResult
{
    public required bool Applied { get; init; }
    public required WorkerScheduleSnapshot Snapshot { get; init; }
    public required string Message { get; init; }
}
