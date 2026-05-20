namespace AteraSnipeSync.Core.Atera;

public sealed class PullSummary
{
    public required int AgentCount { get; init; }
    public required int CustomerCount { get; init; }
    public required DateTimeOffset PulledAt { get; init; }
}
