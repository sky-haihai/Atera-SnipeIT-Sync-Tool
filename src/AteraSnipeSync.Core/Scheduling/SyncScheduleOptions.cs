namespace AteraSnipeSync.Core.Scheduling;

/// <summary>
/// Describes when unattended scheduler-triggered sync jobs should run and how overlap is handled.
/// </summary>
public sealed class SyncScheduleOptions
{
    public required bool Enabled { get; init; }
    public required ScheduleFrequency Frequency { get; init; }
    public required string TimeZoneId { get; init; }
    public required IReadOnlyList<TimeOnly> RunTimes { get; init; }
    public IReadOnlyList<DayOfWeek> DaysOfWeek { get; init; } = [];
    public IReadOnlyList<int> DaysOfMonth { get; init; } = [];
    public bool RunOnLastDayOfMonth { get; init; }
    public bool PreventOverlappingRuns { get; init; } = true;
}
