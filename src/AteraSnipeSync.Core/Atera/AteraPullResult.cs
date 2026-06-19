using AteraSnipeSync.Core.Common;

namespace AteraSnipeSync.Core.Atera;

/// <summary>
/// Represents a completed Atera inventory pull, including every converted agent and non-fatal warning.
/// </summary>
public sealed class AteraPullResult
{
    public required IReadOnlyList<AgentInfo> Agents { get; init; }
    public required PullSummary Summary { get; init; }
    public required IReadOnlyList<ModuleWarning> Warnings { get; init; }
}
