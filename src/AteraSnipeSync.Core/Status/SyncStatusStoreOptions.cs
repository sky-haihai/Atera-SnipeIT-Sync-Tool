namespace AteraSnipeSync.Core.Status;

/// <summary>
/// Configures where local sync history JSON files are stored.
/// </summary>
public sealed class SyncStatusStoreOptions
{
    public string HistoryDirectoryPath { get; init; } =
        @"C:\ProgramData\AteraSnipeSync\History";
}
