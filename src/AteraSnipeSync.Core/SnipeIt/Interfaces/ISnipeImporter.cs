namespace AteraSnipeSync.Core.SnipeIt;

using AteraSnipeSync.Core.Common;

/// <summary>
/// Defines the Snipe-IT import boundary for planning and executing mapped asset changes.
/// </summary>
public interface ISnipeImporter
{
    /// <summary>
    /// Imports or dry-runs a mapped asset batch, optionally reporting safe per-asset progress.
    /// </summary>
    Task<SnipeImportResult> ImportAsync(
        SnipeImportBatch batch,
        SnipeImportOptions options,
        CancellationToken cancellationToken,
        IProgress<SyncProgressUpdate>? progress = null);
}
