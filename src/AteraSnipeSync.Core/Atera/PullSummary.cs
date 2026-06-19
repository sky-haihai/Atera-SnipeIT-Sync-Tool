namespace AteraSnipeSync.Core.Atera;

/// <summary>
/// Summarizes a successful Atera pull for downstream status and notification modules.
/// </summary>
public sealed class PullSummary
{
    public required int AgentCount { get; init; }
    public required DateTimeOffset PulledAt { get; init; }
}
