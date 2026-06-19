namespace AteraSnipeSync.Core.SnipeIt;

/// <summary>
/// Records one planned or executed import operation for audit-friendly import results.
/// </summary>
public sealed class ImportAction
{
    public required string ActionType { get; init; }
    public required string TargetType { get; init; }
    public required string TargetName { get; init; }
    public required bool WasExecuted { get; init; }
    public string? Message { get; init; }
}
