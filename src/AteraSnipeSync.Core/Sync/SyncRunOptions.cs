namespace AteraSnipeSync.Core.Sync;

public sealed class SyncRunOptions
{
    public required bool DryRun { get; init; }
    public required string TriggeredBy { get; init; }
}
