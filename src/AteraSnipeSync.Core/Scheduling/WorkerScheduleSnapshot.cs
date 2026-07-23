namespace AteraSnipeSync.Core.Scheduling;

/// <summary>
/// Captures one immutable, versioned view of the Worker's active schedule and its next calculated trigger.
/// </summary>
public sealed class WorkerScheduleSnapshot
{
    public required long Version { get; init; }
    public required bool ConfigurationValid { get; init; }
    public required bool Enabled { get; init; }
    public SyncScheduleOptions? Options { get; init; }
    public string? RuleFingerprint { get; init; }
    public DateTimeOffset? NextRunUtc { get; init; }
    public DateTimeOffset? LastTriggeredUtc { get; init; }
    public string? Error { get; init; }
}
