namespace AteraSnipeSync.Core.Status;

/// <summary>
/// Configures where local sync history JSON files are stored.
/// </summary>
public sealed class SyncStatusStoreOptions
{
    public string HistoryDirectoryPath { get; init; } =
        @"C:\ProgramData\AteraSnipeSync\History";
    public TimeSpan HistoryRetentionAge { get; init; } = TimeSpan.FromDays(90);
    public int MaxHistoryFiles { get; init; } = 500;
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(15);
}
