namespace AteraSnipeSync.Core.Configuration;

/// <summary>
/// Carries the legacy interval-based synchronization setting retained by the scaffold configuration contract.
/// </summary>
public sealed class SyncConfig
{
    public required int IntervalMinutes { get; init; }
}
