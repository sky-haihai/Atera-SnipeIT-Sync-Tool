using AteraSnipeSync.Core.Common;

namespace AteraSnipeSync.Core.Atera;

public sealed class AteraPullResult
{
    public required IReadOnlyList<AteraAgentDto> Agents { get; init; }
    public required IReadOnlyList<AteraCustomerDto> Customers { get; init; }
    public required PullSummary Summary { get; init; }
    public required IReadOnlyList<ModuleWarning> Warnings { get; init; }
}
