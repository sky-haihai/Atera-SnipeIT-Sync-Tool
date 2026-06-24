namespace AteraSnipeSync.Core.Status;

/// <summary>
/// Describes one structured resource action from a sync run without requiring UI parsing of dense strings.
/// </summary>
internal sealed class SyncHistoryItem
{
    public required string Source { get; init; }
    public required string Action { get; init; }
    public required string TargetType { get; init; }
    public required string Name { get; init; }
    public string? Identifier { get; init; }
    public required bool WasExecuted { get; init; }
    public string? Message { get; init; }
}
