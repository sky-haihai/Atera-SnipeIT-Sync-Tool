namespace AteraSnipeSync.Core.Configuration;

public sealed class SyncConfig
{
    public required int IntervalMinutes { get; init; }
    public required bool DryRun { get; init; }
}
