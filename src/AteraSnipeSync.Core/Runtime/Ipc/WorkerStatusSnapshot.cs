using AteraSnipeSync.Core.Status;

namespace AteraSnipeSync.Core.Runtime.Ipc;

/// <summary>
/// Combines the active run, reloadable schedule, and latest persisted sync state returned to the Tray.
/// </summary>
public sealed class WorkerStatusSnapshot
{
    public required bool IsRunning { get; init; }
    public string? ActiveOperation { get; init; }
    public DateTimeOffset? ActiveStartedUtc { get; init; }
    public required bool ScheduleConfigurationValid { get; init; }
    public required bool ScheduleEnabled { get; init; }
    public DateTimeOffset? NextRunUtc { get; init; }
    public string? ScheduleError { get; init; }
    public SyncStatusSnapshot? LatestSync { get; init; }
}
