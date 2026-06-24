namespace AteraSnipeSync.Core.Atera;

using AteraSnipeSync.Core.Common;

/// <summary>
/// Defines the Atera pull boundary consumed by sync orchestration without exposing HTTP or pagination details.
/// </summary>
public interface IAteraClient
{
    /// <summary>
    /// Pulls a complete Atera inventory snapshot or fails without returning partial results, optionally reporting safe page progress.
    /// </summary>
    Task<AteraPullResult> PullInventoryAsync(
        AteraPullRequest request,
        CancellationToken cancellationToken,
        IProgress<SyncProgressUpdate>? progress = null);
}
