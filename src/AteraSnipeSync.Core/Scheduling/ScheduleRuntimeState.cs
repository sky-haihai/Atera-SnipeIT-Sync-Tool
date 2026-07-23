namespace AteraSnipeSync.Core.Scheduling;

/// <summary>
/// Persists the Worker's claimed and upcoming UTC occurrences for one normalized local schedule rule.
/// </summary>
public sealed class ScheduleRuntimeState
{
    public required string RuleFingerprint { get; init; }
    public DateTimeOffset? NextRunUtc { get; init; }
    public DateTimeOffset? LastTriggeredUtc { get; init; }
}
