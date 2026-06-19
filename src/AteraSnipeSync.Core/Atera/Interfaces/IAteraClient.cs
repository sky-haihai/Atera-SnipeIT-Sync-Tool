namespace AteraSnipeSync.Core.Atera;

/// <summary>
/// Defines the Atera pull boundary consumed by sync orchestration without exposing HTTP or pagination details.
/// </summary>
public interface IAteraClient
{
    /// <summary>
    /// Pulls a complete Atera inventory snapshot or fails without returning partial results.
    /// </summary>
    Task<AteraPullResult> PullInventoryAsync(
        AteraPullRequest request,
        CancellationToken cancellationToken);
}
